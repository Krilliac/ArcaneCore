using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Live;

/// <summary>
/// Sends one line of the world daemon's map-feed protocol and returns its answer. The dev UI talks to the daemon only through this seam, so its
/// code and tests run against a fake; the binding to the live connection (<c>LiveFeed</c>) is a separate service.
/// </summary>
public interface IFeedRequester
{
    /// <summary>
    /// Sends <paramref name="message"/> (an object with <c>op</c>; the transport adds the id) and returns the daemon's whole answer object,
    /// <c>ok:false</c> refusals included (a refusal is an answer, not an error). Throws <see cref="CommandException"/> with
    /// <see cref="CommandException.Unavailable"/> when there is no connection, and <see cref="OperationCanceledException"/> on cancellation.
    /// </summary>
    Task<JsonObject> RequestAsync(JsonObject message, CancellationToken cancellationToken);
}

/// <summary>The live entities the dev UI needs to look up: by guid, and a player or bot by name.</summary>
public interface IEntityDirectory
{
    /// <summary>The latest state of an entity, or null when it is not in the live data.</summary>
    LiveEntity? Get(ulong guid);

    /// <summary>The player or bot with this name (ignoring case), or null.</summary>
    LiveEntity? FindPlayer(string name);
}

/// <summary>The directory over the live store.</summary>
/// <param name="store">The feed's entity store.</param>
public sealed class StoreEntityDirectory(LiveStore store) : IEntityDirectory
{
    /// <inheritdoc />
    public LiveEntity? Get(ulong guid) => store.TryGetEntity(guid, out LiveEntity? entity) ? entity : null;

    /// <inheritdoc />
    public LiveEntity? FindPlayer(string name)
        => store.Snapshot(e => e.Kind is LiveKind.Player or LiveKind.Bot && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).FirstOrDefault()?.Entity;
}

/// <summary>
/// The selection every surface shares: the browser's selected entities, the CLI's and MCP's. Thread-safe; a change is published as
/// <c>devui.selection</c> so open UIs follow it.
/// </summary>
public sealed class DevUiSelection(EventHub? events = null)
{
    /// <summary>The most guids a selection holds (the daemon accepts 50 targets per action).</summary>
    public const int Max = LiveActions.MaxTargets;

    private readonly Lock _gate = new();
    private readonly List<ulong> _guids = [];
    private long _version;

    /// <summary>The selected guids in the order they were added.</summary>
    public IReadOnlyList<ulong> Snapshot()
    {
        lock (_gate)
        {
            return [.. _guids];
        }
    }

    /// <summary>Applies <c>set</c>, <c>add</c>, <c>remove</c> or <c>clear</c>; refuses a selection over <see cref="Max"/>.</summary>
    public void Apply(string mode, IEnumerable<ulong> guids)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(guids);
        JsonObject changed;
        lock (_gate)
        {
            List<ulong> next = [.. _guids];
            switch (mode)
            {
                case "clear":
                    next.Clear();
                    break;
                case "set":
                    next = [.. guids.Distinct()];
                    break;
                case "add":
                    next.AddRange(guids.Where(g => !next.Contains(g)).Distinct());
                    break;
                case "remove":
                    HashSet<ulong> drop = [.. guids];
                    next.RemoveAll(drop.Contains);
                    break;
                default:
                    throw new CommandException(CommandException.BadArguments, $"selection mode '{mode}' must be set, add, remove or clear.");
            }

            if (next.Count > Max)
            {
                throw new CommandException(CommandException.BadArguments, $"a selection holds at most {Max} entities (asked for {next.Count}).");
            }

            _guids.Clear();
            _guids.AddRange(next);
            changed = ToJson(++_version);
        }

        events?.Publish("devui.selection", changed);
    }

    /// <summary>The selection as <c>devui.selection</c> reports it.</summary>
    public JsonObject ToJson()
    {
        lock (_gate)
        {
            return ToJson(_version);
        }
    }

    private JsonObject ToJson(long version) => new()
    {
        ["guids"] = new JsonArray([.. _guids.Select(g => (JsonNode)LiveGuid.Format(g))]),
        ["count"] = _guids.Count,
        ["version"] = version,
    };
}

/// <summary>What the daemon said it can do (the <c>caps</c> answer).</summary>
/// <param name="Actions">Whether the action channel is on.</param>
/// <param name="WhyOff">Why not, when it is off.</param>
/// <param name="LiveServer">Whether the daemon carries the live-server marker (gated commands are refused).</param>
/// <param name="Templates">The template ids the daemon offers.</param>
/// <param name="Operator">The actor name the daemon gives this connection.</param>
/// <param name="Role">The connection role (<c>local</c> here).</param>
/// <param name="ServerCatalog">Whether the daemon answers a <c>catalog</c> op.</param>
/// <param name="Commands">The command paths the catalog lists; empty when there is no server catalog.</param>
/// <param name="CommandInfo">The catalog's command entries as the daemon sent them (path, security, help, write); empty when there is no server catalog.</param>
public sealed record ActionCaps(
    bool Actions, string? WhyOff, bool LiveServer, IReadOnlySet<string> Templates, string? Operator, string Role, bool ServerCatalog, IReadOnlySet<string> Commands, JsonArray CommandInfo);

/// <summary>
/// The dev UI's action layer: runs commands and catalog actions on the world daemon through <see cref="IFeedRequester"/>, keeps the action log
/// and the undo inverses, the pending confirmations and the shared selection. One instance per Atlas process; all methods are thread-safe
/// (the log and selection lock, the confirmation table locks, nothing is held across an await). The daemon does the authorisation, the gating
/// and the audit; this class never grants anything: it only builds the same requests a person could type.
/// </summary>
public sealed class LiveActions
{
    /// <summary>The most targets one action names (the daemon's limit).</summary>
    public const int MaxTargets = 50;

    /// <summary>The longest command text (characters), as the daemon accepts.</summary>
    public const int MaxCommandChars = 1024;

    /// <summary>How long a confirmation code stays valid (seconds).</summary>
    public const int ConfirmSeconds = 60;

    /// <summary>How long a <c>caps</c> answer is reused (seconds).</summary>
    public const int CapsSeconds = 5;

    private const int MaxPending = 16;

    private readonly Func<IFeedRequester?> _requester;
    private readonly IEntityDirectory _entities;
    private readonly Func<uint> _viewMap;
    private readonly EventHub? _events;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
    private ActionCaps? _caps;
    private DateTimeOffset _capsAt;
    private string? _character;

    /// <summary>Creates the action layer.</summary>
    /// <param name="requester">The current feed seam, or null while not connected (read on every call).</param>
    /// <param name="entities">The live entity lookup.</param>
    /// <param name="viewMap">The view's current map id (the default map of a point).</param>
    /// <param name="events">Where the log and selection changes are published, or null.</param>
    /// <param name="clock">The clock for expiry; the system clock when null.</param>
    public LiveActions(Func<IFeedRequester?> requester, IEntityDirectory entities, Func<uint> viewMap, EventHub? events = null, TimeProvider? clock = null)
    {
        _requester = requester ?? throw new ArgumentNullException(nameof(requester));
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _viewMap = viewMap ?? throw new ArgumentNullException(nameof(viewMap));
        _events = events;
        _clock = clock ?? TimeProvider.System;
        Selection = new DevUiSelection(events);
    }

    /// <summary>The actions run so far.</summary>
    public ActionLog Log { get; } = new();

    /// <summary>The shared selection.</summary>
    public DevUiSelection Selection { get; }

    /// <summary>The operator's own character name (for "teleport me" and "follow me"), or null.</summary>
    public string? Character
    {
        get
        {
            lock (_gate)
            {
                return _character;
            }
        }

        set
        {
            lock (_gate)
            {
                _character = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }
    }

    /// <summary>The view's current map id.</summary>
    public uint ViewMap => _viewMap();

    // ---- catalog ---------------------------------------------------------------------------------------------------------------

    /// <summary>The daemon's capabilities, reused for <see cref="CapsSeconds"/> seconds unless <paramref name="force"/>.</summary>
    public async Task<ActionCaps> CapsAsync(bool force, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (!force && _caps is { } cached && (now - _capsAt).TotalSeconds < CapsSeconds)
            {
                return cached;
            }
        }

        JsonObject reply = await SendAsync(new JsonObject { ["op"] = "caps" }, cancellationToken).ConfigureAwait(false);
        if (!IsOk(reply))
        {
            throw new CommandException(CommandException.Failed, $"the world daemon refused caps: {Text(reply, "code") ?? "refused"}: {Text(reply, "message") ?? "it gave no reason"}");
        }

        bool serverCatalog = reply["catalog"] is JsonValue c && c.TryGetValue(out bool hasCatalog) && hasCatalog;
        HashSet<string> commands = [];
        var commandInfo = new JsonArray();
        if (serverCatalog)
        {
            JsonObject catalog = await SendAsync(new JsonObject { ["op"] = "catalog", ["actor"] = new JsonObject { ["tier"] = "local" } }, cancellationToken).ConfigureAwait(false);
            if (IsOk(catalog) && catalog["commands"] is JsonArray list)
            {
                foreach (JsonNode? item in list)
                {
                    if ((item is JsonObject o ? o["path"] : item) is JsonValue v && v.TryGetValue(out string? path) && path is not null)
                    {
                        commands.Add(path.TrimStart('.'));
                        commandInfo.Add(item?.DeepClone());
                    }
                }
            }
            else
            {
                serverCatalog = false;
            }
        }

        var caps = new ActionCaps(
            reply["actions"] is JsonValue a && a.TryGetValue(out bool on) && on,
            Text(reply, "actionsWhyOff"),
            reply["liveServer"] is JsonValue l && l.TryGetValue(out bool live) && live,
            reply["templates"] is JsonArray templates ? new HashSet<string>(templates.Select(t => t?.GetValue<string>()).Where(t => t is not null).Select(t => t!), StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal),
            Text(reply, "operator"),
            Text(reply, "role") ?? "local",
            serverCatalog,
            commands,
            commandInfo);
        lock (_gate)
        {
            _caps = caps;
            _capsAt = now;
        }

        return caps;
    }

    /// <summary>Whether the daemon offers this action to this connection: a template it lists, or a command (a gated one only if its catalog lists it and the live marker is off).</summary>
    public static bool Available(UiAction action, ActionCaps caps)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(caps);
        if (!caps.Actions)
        {
            return false;
        }

        if (action.Template is not null)
        {
            return caps.Templates.Contains(action.Template);
        }

        if (caps.ServerCatalog && action.Path is { } path && !caps.Commands.Contains(path))
        {
            return false;
        }

        return !action.Gated || (!caps.LiveServer && caps.ServerCatalog);
    }

    /// <summary>The <c>action.catalog</c> answer: the actions this connection may offer, the daemon's state and the shared selection.</summary>
    public async Task<JsonObject> CatalogAsync(string? filter, CancellationToken cancellationToken)
    {
        ActionCaps caps = await CapsAsync(force: false, cancellationToken).ConfigureAwait(false);
        var items = new JsonArray();
        if (caps.Actions)
        {
            foreach (UiAction action in DevUiCatalog.All)
            {
                if (Available(action, caps) && Matches(action, filter))
                {
                    items.Add(action.ToJson());
                }
            }
        }

        var json = new JsonObject
        {
            ["actions"] = caps.Actions,
            ["role"] = caps.Role,
            ["liveServer"] = caps.LiveServer,
            ["serverCatalog"] = caps.ServerCatalog,
            ["templates"] = new JsonArray([.. caps.Templates.Order(StringComparer.Ordinal).Select(t => (JsonNode)t)]),
            ["items"] = items,
            ["commands"] = caps.CommandInfo.DeepClone(),
            ["selection"] = Selection.ToJson(),
            ["map"] = ViewMap,
        };
        if (caps.Operator is not null)
        {
            json["operator"] = caps.Operator;
        }

        if (caps.WhyOff is not null)
        {
            json["whyOff"] = caps.WhyOff;
        }

        if (Character is { } character)
        {
            json["character"] = character;
        }

        return json;
    }

    private static bool Matches(UiAction action, string? filter)
        => string.IsNullOrWhiteSpace(filter)
            || action.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || action.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || action.Group.Contains(filter, StringComparison.OrdinalIgnoreCase);

    // ---- running ---------------------------------------------------------------------------------------------------------------

    /// <summary>Runs one raw command line (the palette), optionally with a selection and a point.</summary>
    public async Task<JsonObject> RunCommandAsync(string command, IReadOnlyList<ulong> targets, ActionPoint? point, string? confirm, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(targets);
        string line = command.Trim();
        if (line.Length > 0 && line[0] is '.' or '!')
        {
            line = line[1..].TrimStart();
        }

        if (line.Length == 0 || line.Length > MaxCommandChars || line.Any(char.IsControl))
        {
            throw Bad($"a command is one non-empty line of at most {MaxCommandChars} characters without control characters.");
        }

        CheckTargets(targets);
        var plan = new Plan("run", null, [new ActionStep(null, line, targets, point, null)], []);
        return await ExecuteAsync(plan, confirm, atlasConfirmed: false, undoOf: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a catalog action by id on explicit targets (or the selection) and/or a point.</summary>
    public async Task<JsonObject> RunActionAsync(
        string actionId, IReadOnlyList<ulong> targets, ActionPoint? point, JsonObject? args, string? confirm, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actionId);
        ArgumentNullException.ThrowIfNull(targets);
        UiAction action = DevUiCatalog.Find(actionId)
            ?? throw Bad($"no action '{actionId}' (action.catalog lists them: {string.Join(", ", DevUiCatalog.All.Select(a => a.Id))}).");
        ActionCaps caps = await CapsAsync(force: false, cancellationToken).ConfigureAwait(false);
        if (!caps.Actions)
        {
            throw new CommandException(CommandException.Unavailable, $"the world daemon's action channel is off: {caps.WhyOff ?? "it gave no reason"}.");
        }

        if (!Available(action, caps))
        {
            throw new CommandException(CommandException.Unavailable, $"the world daemon does not offer '{action.Id}' to this connection (it is not in its catalog{(action.Gated && caps.LiveServer ? ", and it is refused on the live server" : string.Empty)}).");
        }

        Plan plan = BuildPlan(action, targets, point, args);
        return await ExecuteAsync(plan, confirm, atlasConfirmed: false, undoOf: null, cancellationToken).ConfigureAwait(false);
    }

    private Plan BuildPlan(UiAction action, IReadOnlyList<ulong> given, ActionPoint? point, JsonObject? args)
    {
        if (action.NeedsPoint && point is null)
        {
            throw Bad($"action '{action.Id}' needs a point (at=[x,y,z] and map).");
        }

        Dictionary<string, string> printed = DevUiCatalog.PrintArgs(action, args);
        IReadOnlyList<ulong> targets;
        switch (action.Subject)
        {
            case "none":
                if (given.Count > 0)
                {
                    throw Bad($"action '{action.Id}' takes no targets.");
                }

                targets = [];
                break;
            case "me":
                targets = [ResolveMe().Id];
                break;
            case "selection-bots":
                targets = (given.Count > 0 ? given : Selection.Snapshot()).Where(g => _entities.Get(g)?.Kind == LiveKind.Bot).ToArray();
                if (targets.Count == 0)
                {
                    throw Bad($"action '{action.Id}' needs at least one selected bot.");
                }

                break;
            default:
                targets = given.Count > 0 ? given : Selection.Snapshot();
                if (targets.Count == 0)
                {
                    throw Bad($"action '{action.Id}' needs at least one target (targets=[...] or select entities first).");
                }

                break;
        }

        CheckTargets(targets);
        foreach (ulong guid in targets)
        {
            if (_entities.Get(guid) is { } entity && action.Kinds.Count > 0 && !action.Kinds.Contains(LiveKinds.Name(entity.Kind)))
            {
                throw Bad($"'{action.Label}' does not apply to a {LiveKinds.Name(entity.Kind)} ({LiveGuid.Format(guid)}); it applies to {string.Join(", ", action.Kinds)}.");
            }
        }

        string? self = action.Line is not null && action.Line.Contains("{self}", StringComparison.Ordinal) ? DevUiCatalog.GuidText(ResolveMe().Id) : null;
        var steps = new List<ActionStep>();
        if (action.Template is not null)
        {
            steps.Add(new ActionStep(action.Template, null, targets, point, printed, action.Id));
        }
        else if (targets.Count == 0)
        {
            steps.Add(new ActionStep(null, DevUiCatalog.ExpandLine(action, null, null, point, self, printed), [], point, null, action.Id));
        }
        else
        {
            foreach (ulong guid in targets)
            {
                steps.Add(new ActionStep(null, DevUiCatalog.ExpandLine(action, guid, _entities.Get(guid)?.Name, point, self, printed), [guid], point, null, action.Id));
            }
        }

        var previous = new List<ActionInverse.Previous>();
        if (action.Undo is "tele" or "moveto")
        {
            foreach (ulong guid in targets)
            {
                if (_entities.Get(guid) is { } before)
                {
                    previous.Add(new ActionInverse.Previous(guid, new ActionPoint(before.Map, before.Instance, before.Position.X, before.Position.Y, before.Position.Z, before.Orientation)));
                }
            }
        }

        return new Plan(action.Id, action, steps, previous);
    }

    private LiveEntity ResolveMe()
    {
        string name = Character ?? throw Bad("this action needs your character: run devui.character name=<your character name> first.");
        return _entities.FindPlayer(name)
            ?? throw Bad($"your character '{name}' is not in the live data (is it online on the subscribed map?).");
    }

    private static void CheckTargets(IReadOnlyList<ulong> targets)
    {
        if (targets.Count > MaxTargets)
        {
            throw new CommandException(CommandException.BadArguments, $"an action names at most {MaxTargets} targets (got {targets.Count}).");
        }

        if (targets.Any(t => t == 0))
        {
            throw Bad("a target guid is not zero.");
        }
    }

    // ---- confirm ---------------------------------------------------------------------------------------------------------------

    /// <summary>Resubmits the pending request that was issued this confirmation code.</summary>
    public async Task<JsonObject> ConfirmAsync(string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        Pending? pending;
        lock (_gate)
        {
            PurgeExpired();
            _pending.Remove(code.Trim(), out pending);
        }

        if (pending is null)
        {
            throw Bad("no pending request has that confirmation code (it may have expired after 60 seconds, or been used).");
        }

        return await ExecuteAsync(pending.Plan, pending.FromDaemon ? pending.Code : null, atlasConfirmed: true, pending.UndoOf, cancellationToken).ConfigureAwait(false);
    }

    private JsonObject IssueConfirm(Plan plan, string code, bool fromDaemon, string summary, long? undoOf)
    {
        lock (_gate)
        {
            PurgeExpired();
            while (_pending.Count >= MaxPending)
            {
                string oldest = _pending.OrderBy(p => p.Value.Expires).First().Key;
                _pending.Remove(oldest);
            }

            _pending[code] = new Pending(plan, code, fromDaemon, _clock.GetUtcNow().AddSeconds(ConfirmSeconds), undoOf);
        }

        return new JsonObject
        {
            ["id"] = 0,
            ["ok"] = false,
            ["code"] = "confirm-required",
            ["message"] = $"{summary}: confirm with the code {code} (action.confirm code={code}) within {ConfirmSeconds} seconds.",
            ["action"] = plan.Name,
            ["lines"] = new JsonArray(),
            ["confirm"] = new JsonObject { ["code"] = code, ["expiresIn"] = ConfirmSeconds, ["summary"] = summary, ["source"] = fromDaemon ? "daemon" : "atlas" },
        };
    }

    private void PurgeExpired()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        foreach (string key in _pending.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList())
        {
            _pending.Remove(key);
        }
    }

    // ---- undo ------------------------------------------------------------------------------------------------------------------

    /// <summary>Runs the inverse of a logged action. Undo is an action like any other: it is logged, audited by the daemon, and itself has no further undo.</summary>
    public async Task<JsonObject> UndoAsync(long id, CancellationToken cancellationToken)
    {
        ActionEntry entry = Log.Find(id) ?? throw Bad($"no logged action {id} (the log keeps the last {ActionLog.Capacity}).");
        if (entry.UndoneBy is { } by)
        {
            throw Bad($"action {id} was already undone by action {by}.");
        }

        if (!entry.Ok)
        {
            throw Bad($"action {id} failed, so there is nothing to undo.");
        }

        if (entry.Inverse.Count == 0)
        {
            throw Bad($"action {id} cannot be undone: {entry.NoUndoReason ?? "it changed nothing"}.");
        }

        var steps = new List<ActionStep>();
        foreach (ActionStep step in entry.Inverse)
        {
            steps.Add(Materialize(step));
        }

        JsonObject result = await ExecuteAsync(new Plan("undo", null, steps, []), null, atlasConfirmed: true, id, cancellationToken).ConfigureAwait(false);
        if (result["ok"]?.GetValue<bool>() == true && result["logId"] is JsonValue lid && lid.TryGetValue(out long logId))
        {
            Log.MarkUndone(id, logId);
        }

        return result;
    }

    /// <summary>An inverse step that names a catalog action instead of a ready line is expanded now, against the live data of now.</summary>
    private ActionStep Materialize(ActionStep step)
    {
        if (step.Template is not null || step.Command is not null || step.ActionId is null || DevUiCatalog.Find(step.ActionId) is not { Line: not null } action)
        {
            return step;
        }

        ulong guid = step.Targets[0];
        string? self = action.Line.Contains("{self}", StringComparison.Ordinal) ? DevUiCatalog.GuidText(ResolveMe().Id) : null;
        string line = DevUiCatalog.ExpandLine(action, guid, _entities.Get(guid)?.Name, step.Point, self, DevUiCatalog.PrintArgs(action, null));
        return new ActionStep(null, line, step.Targets, step.Point, null, action.Id);
    }

    // ---- executing -------------------------------------------------------------------------------------------------------------

    private sealed record Plan(string Name, UiAction? Action, IReadOnlyList<ActionStep> Steps, IReadOnlyList<ActionInverse.Previous> Previous);

    private sealed record Pending(Plan Plan, string Code, bool FromDaemon, DateTimeOffset Expires, long? UndoOf);

    private async Task<JsonObject> ExecuteAsync(Plan plan, string? daemonConfirm, bool atlasConfirmed, long? undoOf, CancellationToken cancellationToken)
    {
        if (!atlasConfirmed && daemonConfirm is null && plan.Action?.Template is null && plan.Steps.Count > 1)
        {
            string atlasCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(3));
            string summary = $"{plan.Action?.Label ?? plan.Name} on {plan.Steps.Count} targets";
            return IssueConfirm(plan, atlasCode, fromDaemon: false, summary, undoOf);
        }

        ActionCaps caps = await CapsAsync(force: false, cancellationToken).ConfigureAwait(false);
        if (!caps.Actions)
        {
            throw new CommandException(CommandException.Unavailable, $"the world daemon's action channel is off: {caps.WhyOff ?? "it gave no reason"}.");
        }

        var lines = new List<string>();
        var replies = new List<string>();
        var writes = new JsonArray();
        var targets = new JsonArray();
        var inverse = new List<ActionStep>();
        string? noUndo = null;
        string? code = null;
        string message = string.Empty;
        bool ok = true;
        bool settled = true;
        JsonObject? overlay = null;
        var allTargets = new List<ulong>();

        foreach (ActionStep step in plan.Steps)
        {
            lines.Add(step.Describe());
            allTargets.AddRange(step.Targets.Where(t => !allTargets.Contains(t)));
            JsonObject reply = await SendAsync(StepMessage(step, daemonConfirm), cancellationToken).ConfigureAwait(false);
            if (reply["confirm"] is JsonObject confirm && Text(confirm, "code") is { } daemonCode)
            {
                return IssueConfirm(plan, daemonCode, fromDaemon: true, Text(confirm, "summary") ?? plan.Name, undoOf);
            }

            bool stepOk = IsOk(reply);
            JsonArray replyLines = reply["lines"] as JsonArray ?? [];
            string[] stepReplies = [.. replyLines.Select(l => l?.GetValue<string>() ?? string.Empty)];
            replies.AddRange(stepReplies);
            if (reply["writes"] is JsonArray stepWrites)
            {
                foreach (JsonNode? w in stepWrites)
                {
                    writes.Add(w?.DeepClone());
                }
            }

            if (reply["settled"] is JsonValue sv && sv.TryGetValue(out bool stepSettled) && !stepSettled)
            {
                settled = false;
            }

            var okTargets = new List<ulong>();
            if (reply["targets"] is JsonArray perTarget)
            {
                foreach (JsonNode? t in perTarget)
                {
                    if (t is JsonObject o && Text(o, "guid") is { } guidText && LiveGuid.TryParse(guidText, out ulong guid))
                    {
                        bool targetOk = o["ok"] is JsonValue tv && tv.TryGetValue(out bool b) && b;
                        targets.Add(new JsonObject { ["guid"] = LiveGuid.Format(guid), ["ok"] = targetOk, ["failure"] = Text(o, "failure") });
                        if (targetOk)
                        {
                            okTargets.Add(guid);
                        }
                    }
                }
            }
            else if (step.Targets.Count > 0)
            {
                foreach (ulong guid in step.Targets)
                {
                    targets.Add(new JsonObject { ["guid"] = LiveGuid.Format(guid), ["ok"] = stepOk, ["failure"] = stepOk ? null : Text(reply, "code") });
                }

                if (stepOk)
                {
                    okTargets.AddRange(step.Targets);
                }
            }

            if (!stepOk)
            {
                ok = false;
                code ??= Text(reply, "code") ?? "failed";
                if (message.Length == 0)
                {
                    message = Text(reply, "message") ?? string.Empty;
                }

                if (reply["retryMs"] is JsonValue)
                {
                    message = $"{message} (retry in {reply["retryMs"]} ms)".Trim();
                }
            }

            if (plan.Action is { } action)
            {
                inverse.AddRange(ActionInverse.Compute(action, step, okTargets, stepReplies, plan.Previous, out string? why));
                noUndo ??= why;
                if (stepOk && JsonOverlay(action, stepReplies) is { } built)
                {
                    overlay = built;
                }
            }
            else if (plan.Name == "undo")
            {
                noUndo = "an undo is not undone again";
            }
            else
            {
                noUndo = "a raw command has no known inverse";
            }
        }

        // A successful read-only catalog action (the AI overlay's 2 Hz "debug" refresh, "spawns") changes nothing and has nothing to
        // undo; logging each one would evict the 500-entry log's real, undoable actions and flood devui.action. It still returns its
        // overlay; only the log entry and the broadcast are skipped.
        bool recorded = !(ok && plan.Action is { Write: false } && writes.Count == 0);
        string actor = caps.Operator ?? "atlas-local";
        var entry = new ActionEntry
        {
            Id = recorded ? Log.NextId() : 0,
            At = _clock.GetUtcNow(),
            Actor = actor,
            Action = plan.Name,
            Lines = lines,
            Targets = allTargets,
            Ok = ok,
            Code = code,
            Message = message,
            Replies = replies,
            Writes = writes,
            Inverse = ok ? inverse : [],
            NoUndoReason = inverse.Count == 0 ? noUndo : null,
            UndoOf = undoOf,
        };
        if (recorded)
        {
            Log.Add(entry);
            _events?.Publish("devui.action", entry.ToJson());
        }

        var result = new JsonObject
        {
            ["id"] = entry.Id,
            ["logId"] = recorded ? entry.Id : null,
            ["logged"] = recorded,
            ["ok"] = ok,
            ["action"] = plan.Name,
            ["lines"] = new JsonArray([.. lines.Select(l => (JsonNode)l)]),
            ["replies"] = new JsonArray([.. replies.Select(l => (JsonNode)l)]),
            ["settled"] = settled,
            ["targets"] = targets,
            ["writes"] = writes.DeepClone(),
            ["undoable"] = entry.Undoable,
        };
        if (code is not null)
        {
            result["code"] = code;
            result["message"] = message;
        }

        if (entry.NoUndoReason is not null)
        {
            result["noUndoReason"] = entry.NoUndoReason;
        }

        if (overlay is not null)
        {
            result["overlay"] = overlay;
        }

        return result;
    }

    private JsonObject? JsonOverlay(UiAction action, IReadOnlyList<string> replies)
    {
        if (action.Id is not ("debug" or "spawns") || ActionOverlays.FirstJson(replies) is not { } json)
        {
            return null;
        }

        if (action.Id == "spawns")
        {
            return ActionOverlays.FromSpawns(json);
        }

        WorldPoint? at = null;
        if (json["guid"]?.GetValue<string>() is { } guidText && LiveGuid.TryParse(guidText, out ulong guid) && _entities.Get(guid) is { } entity)
        {
            at = entity.Position;
        }

        return ActionOverlays.FromDebug(json, at, g => _entities.Get(g)?.Position);
    }

    private static JsonObject StepMessage(ActionStep step, string? confirm)
    {
        var message = new JsonObject { ["op"] = "action", ["actor"] = new JsonObject { ["tier"] = "local" } };
        if (step.Template is not null)
        {
            message["template"] = step.Template;
            var args = new JsonObject();
            if (step.Args is not null)
            {
                foreach ((string key, string value) in step.Args)
                {
                    args[key] = uint.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
                }
            }

            message["args"] = args;
        }
        else
        {
            message["command"] = step.Command;
        }

        if (step.Targets.Count > 0)
        {
            message["targets"] = new JsonArray([.. step.Targets.Select(t => (JsonNode)LiveGuid.Format(t))]);
        }

        if (step.Point is { } point)
        {
            message["point"] = point.ToWire();
        }

        if (confirm is not null)
        {
            message["confirm"] = confirm;
        }

        return message;
    }

    private async Task<JsonObject> SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        IFeedRequester requester = _requester()
            ?? throw new CommandException(CommandException.Unavailable, "not connected to a world daemon's map feed; run live.connect first.");
        return await requester.RequestAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsOk(JsonObject reply) => reply["ok"] is JsonValue v && v.TryGetValue(out bool ok) && ok;

    private static string? Text(JsonObject json, string key) => json[key] is JsonValue v && v.TryGetValue(out string? text) ? text : null;

    private static CommandException Bad(string message) => new(CommandException.BadArguments, message);
}
