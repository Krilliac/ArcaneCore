using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArcaneCore.Atlas.Live;

/// <summary>A world point with its map, as the action channel's <c>point</c> carries it.</summary>
/// <param name="Map">Map id.</param>
/// <param name="Instance">Instance id (0 for continents).</param>
/// <param name="X">World x.</param>
/// <param name="Y">World y.</param>
/// <param name="Z">World z.</param>
/// <param name="O">Orientation in radians.</param>
public sealed record ActionPoint(uint Map, uint Instance, double X, double Y, double Z, double O = 0)
{
    /// <summary>The wire form.</summary>
    public JsonObject ToWire() => new()
    {
        ["map"] = Map,
        ["instance"] = Instance,
        ["x"] = X,
        ["y"] = Y,
        ["z"] = Z,
        ["o"] = O,
    };
}

/// <summary>
/// One request to the daemon: either a wire template or a command line, with its targets and point. A logged action's inverse is a list of
/// these, run in order.
/// </summary>
/// <param name="Template">The daemon template id, or null for a command.</param>
/// <param name="Command">The command line (no leading dot), or null for a template.</param>
/// <param name="Targets">The guids the step acts on.</param>
/// <param name="Point">The point, or null.</param>
/// <param name="Args">Template arguments (numbers as printed text), or null.</param>
/// <param name="ActionId">The dev UI action this step belongs to, or null for a raw command.</param>
public sealed record ActionStep(string? Template, string? Command, IReadOnlyList<ulong> Targets, ActionPoint? Point, IReadOnlyDictionary<string, string>? Args, string? ActionId = null)
{
    /// <summary>A short description for the log: the command line, or the template and its targets.</summary>
    public string Describe()
    {
        if (Command is not null)
        {
            return Command;
        }

        string targets = Targets.Count == 0 ? string.Empty : " " + string.Join(',', Targets.Select(DevUiCatalog.GuidText));
        string where = Point is { } p
            ? string.Create(CultureInfo.InvariantCulture, $" at {p.Map}:{DevUiCatalog.Number(Math.Round(p.X, 2))},{DevUiCatalog.Number(Math.Round(p.Y, 2))},{DevUiCatalog.Number(Math.Round(p.Z, 2))}")
            : string.Empty;
        return $"template {Template}{targets}{where}";
    }
}

/// <summary>One finished action in the log.</summary>
public sealed class ActionEntry
{
    /// <summary>Log id, counting from 1 within this Atlas process.</summary>
    public long Id { get; init; }

    /// <summary>When the action finished.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>Who the daemon says ran it (<c>atlas-local:account#pid</c>), or <c>atlas-local</c> before the first caps answer.</summary>
    public string Actor { get; init; } = "atlas-local";

    /// <summary>The dev UI action id, <c>run</c> for a raw command, or <c>undo</c>.</summary>
    public string Action { get; init; } = "run";

    /// <summary>What was asked, one line per step.</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];

    /// <summary>The targets of the whole action.</summary>
    public IReadOnlyList<ulong> Targets { get; init; } = [];

    /// <summary>Whether every step succeeded.</summary>
    public bool Ok { get; init; }

    /// <summary>The first failure code, or null.</summary>
    public string? Code { get; init; }

    /// <summary>The daemon's refusal text, or empty.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>The reply lines of every step.</summary>
    public IReadOnlyList<string> Replies { get; init; } = [];

    /// <summary>The daemon's <c>writes</c> entries (old and new values), when it sent any.</summary>
    public JsonArray Writes { get; init; } = [];

    /// <summary>The steps that would undo this action; empty when there is no inverse.</summary>
    public IReadOnlyList<ActionStep> Inverse { get; init; } = [];

    /// <summary>Why there is no inverse (shown by the UI), or null when there is one or nothing was changed.</summary>
    public string? NoUndoReason { get; init; }

    /// <summary>The entry this one undoes, or null.</summary>
    public long? UndoOf { get; init; }

    /// <summary>The entry that undid this one, or null.</summary>
    public long? UndoneBy { get; set; }

    /// <summary>Whether Undo is offered.</summary>
    public bool Undoable => Inverse.Count > 0 && UndoneBy is null && Ok;

    /// <summary>The entry as the <c>action.log</c> command reports it.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["id"] = Id,
            ["at"] = At.ToString("O", CultureInfo.InvariantCulture),
            ["actor"] = Actor,
            ["action"] = Action,
            ["lines"] = new JsonArray([.. Lines.Select(l => (JsonNode)l)]),
            ["targets"] = new JsonArray([.. Targets.Select(t => (JsonNode)LiveGuid.Format(t))]),
            ["ok"] = Ok,
            ["replies"] = new JsonArray([.. Replies.Select(l => (JsonNode)l)]),
            ["writes"] = Writes.DeepClone(),
            ["undoable"] = Undoable,
        };
        if (Code is not null)
        {
            json["code"] = Code;
        }

        if (Message.Length > 0)
        {
            json["message"] = Message;
        }

        if (NoUndoReason is not null)
        {
            json["noUndoReason"] = NoUndoReason;
        }

        if (UndoOf is { } of)
        {
            json["undoOf"] = of;
        }

        if (UndoneBy is { } by)
        {
            json["undoneBy"] = by;
        }

        return json;
    }
}

/// <summary>
/// The actions this Atlas process has run, newest last, bounded. Thread-safe. The log is a memory of the session, not an audit: the daemon
/// writes the audit (actor, command, result) for every action whatever Atlas keeps.
/// </summary>
public sealed class ActionLog
{
    /// <summary>The most entries kept; the oldest are dropped first.</summary>
    public const int Capacity = 500;

    private readonly Lock _gate = new();
    private readonly List<ActionEntry> _entries = [];
    private long _next;

    /// <summary>Takes the next id for an entry about to be added.</summary>
    public long NextId()
    {
        lock (_gate)
        {
            return ++_next;
        }
    }

    /// <summary>Adds an entry whose <see cref="ActionEntry.Id"/> came from <see cref="NextId"/>.</summary>
    public void Add(ActionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > Capacity)
            {
                _entries.RemoveRange(0, _entries.Count - Capacity);
            }
        }
    }

    /// <summary>The last <paramref name="limit"/> entries, oldest first.</summary>
    public IReadOnlyList<ActionEntry> Recent(int limit)
    {
        lock (_gate)
        {
            return [.. _entries.Skip(Math.Max(0, _entries.Count - Math.Max(0, limit)))];
        }
    }

    /// <summary>The entry with this id, or null (dropped or never existed).</summary>
    public ActionEntry? Find(long id)
    {
        lock (_gate)
        {
            return _entries.Find(e => e.Id == id);
        }
    }

    /// <summary>Marks <paramref name="id"/> as undone by <paramref name="byId"/>; false when it is gone or already undone.</summary>
    public bool MarkUndone(long id, long byId)
    {
        lock (_gate)
        {
            ActionEntry? entry = _entries.Find(e => e.Id == id);
            if (entry is null || entry.UndoneBy is not null)
            {
                return false;
            }

            entry.UndoneBy = byId;
            return true;
        }
    }

    /// <summary>The newest entry that can still be undone, or null.</summary>
    public ActionEntry? LastUndoable()
    {
        lock (_gate)
        {
            return _entries.FindLast(e => e.Undoable);
        }
    }
}

/// <summary>
/// The inverses of the actions that have one. Undo only exists where the opposite is a command the daemon already has and the information
/// to run it is known: a temporary creature is deleted by the guid in the daemon's reply; a teleport or a walk goes back to the position the
/// live data showed just before; kill is answered by respawn; the paired bot and AI commands undo each other. Anything else, including every
/// "set ..." edit (the daemon sends no old value), has no inverse and says so.
/// </summary>
public static partial class ActionInverse
{
    /// <summary>Where one target was before a move.</summary>
    /// <param name="Guid">The target.</param>
    /// <param name="Where">Its position and map.</param>
    public sealed record Previous(ulong Guid, ActionPoint Where);

    [GeneratedRegex(@"\(\w+:([0-9A-Fa-f]{16})\)")]
    private static partial Regex PlacedGuid();

    /// <summary>
    /// The steps that undo <paramref name="action"/> run as <paramref name="step"/>, for the targets that succeeded. Empty with a reason in
    /// <paramref name="reason"/> when there is no inverse.
    /// </summary>
    /// <param name="action">The dev UI action, or null for a raw command (which never has an inverse).</param>
    /// <param name="step">The step that ran.</param>
    /// <param name="okTargets">The targets that succeeded.</param>
    /// <param name="replies">The reply lines of the step.</param>
    /// <param name="previous">The positions the live data showed before the action.</param>
    /// <param name="reason">Why there is none, or null.</param>
    public static IReadOnlyList<ActionStep> Compute(
        UiAction? action, ActionStep step, IReadOnlyList<ulong> okTargets, IReadOnlyList<string> replies, IReadOnlyList<Previous> previous, out string? reason)
    {
        reason = null;
        if (action is null)
        {
            reason = "a raw command has no known inverse";
            return [];
        }

        if (action.Undo is null)
        {
            reason = action.Write ? $"{action.Label} has no inverse (the daemon sends no old value)" : null;
            return [];
        }

        switch (action.Undo)
        {
            case "place":
                foreach (string line in replies)
                {
                    Match m = PlacedGuid().Match(line);
                    if (m.Success && ulong.TryParse(m.Groups[1].ValueSpan, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong guid))
                    {
                        return [new ActionStep(null, "creature delete", [guid], null, null, "undo.delete")];
                    }
                }

                reason = "the reply named no placed object";
                return [];
            case "tele":
            case "moveto":
                var steps = new List<ActionStep>();
                foreach (ulong target in okTargets)
                {
                    Previous? before = previous.FirstOrDefault(p => p.Guid == target);
                    if (before is null)
                    {
                        reason = "a target was not in the live data, so its previous position is unknown";
                        continue;
                    }

                    if (action.Undo == "moveto" && step.Point is { } to && to.Map != before.Where.Map)
                    {
                        reason = "a walk cannot be undone across maps";
                        continue;
                    }

                    steps.Add(new ActionStep(action.Undo, null, [target], before.Where, null, action.Id));
                }

                return steps;
            case "kill":
                return [.. okTargets.Select(t => new ActionStep(null, "creature respawn", [t], null, null, "undo.respawn"))];
            default:
                UiAction? opposite = DevUiCatalog.Find(action.Undo);
                if (opposite?.Line is null || okTargets.Count == 0)
                {
                    reason = "no inverse is known";
                    return [];
                }

                // One undo step per target: the line text is expanded when the undo runs, from the same action.
                return [.. okTargets.Select(t => new ActionStep(null, null, [t], step.Point, null, opposite.Id))];
        }
    }
}
