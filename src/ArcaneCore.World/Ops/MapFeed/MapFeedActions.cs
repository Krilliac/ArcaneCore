using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Atlas;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.ServerConsole;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>Who an action runs as.</summary>
/// <param name="Name">The invoker name: <c>atlas-local:user#pid</c> or <c>atlas-web:ACCOUNT</c>.</param>
/// <param name="AccountId">The account id, or null for a local actor without a configured account.</param>
/// <param name="Security">The security the command runs at (a web actor's is already capped).</param>
/// <param name="IsPublic">Whether the actor came in over the public listener.</param>
/// <param name="Ticket">The ticket a web actor presented (audit hashes it), or null.</param>
internal sealed record ActionActor(string Name, int? AccountId, AccountSecurity Security, bool IsPublic, string? Ticket);

/// <summary>One target's result in an action's answer.</summary>
/// <param name="Guid">The target.</param>
/// <param name="Ok">Whether the command run for it ended without a failure.</param>
/// <param name="Failure">The failure code, or null.</param>
/// <param name="Lines">The lines the run printed while it ran (per-target templates only), else null.</param>
internal sealed record TargetOutcome(ulong Guid, bool Ok, string? Failure, string[]? Lines);

/// <summary>The answer to one action, before it is written as a <c>result</c>.</summary>
internal sealed class ActionOutcome
{
    public bool Ok { get; init; }

    public string? Code { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<string> Lines { get; init; } = [];

    public bool Settled { get; init; } = true;

    public string? Failure { get; init; }

    public IReadOnlyList<TargetOutcome>? Targets { get; init; }

    public int? RetryMs { get; init; }

    public (string Code, string Summary)? Confirm { get; init; }

    /// <summary>The connection ended before the action was answered (the command may have run: see <see cref="Settled"/>).</summary>
    public bool Cancelled { get; init; }

    /// <summary>The <c>result</c> column of the log line and the audit records: never <c>ok</c> for an unsettled or cancelled action.</summary>
    public string AuditResult => Cancelled ? ActionCodes.Cancelled : !Settled ? ActionCodes.Unsettled : Ok ? "ok" : Code ?? ActionCodes.Failed;

    /// <summary>The <c>code</c> column of the durable record: the command's failure, else the refusal code, else null.</summary>
    public string? AuditCode => Failure ?? (Ok ? null : Code);

    /// <summary>The connection ended before anything ran.</summary>
    public static ActionOutcome CancelledBeforeRun()
        => new() { Ok = false, Code = ActionCodes.Cancelled, Message = "the connection ended before the action was answered", Cancelled = true };

    public static ActionOutcome Refused(string code, string message) => new() { Ok = false, Code = code, Message = message };

    public static ActionOutcome Refused(FeedError error) => Refused(error.Code, error.Message);
}

/// <summary>
/// The action channel (docs/areas/atlas-actions.md): the <c>action</c> and <c>caps</c> ops of the map feed. A tool asks the daemon to run a
/// command line or a catalog template as an actor, with a clicked point and a unit selection attached; the daemon answers with the command's
/// output. This class owns the whole pipeline, in order:
/// <list type="number">
/// <item>the channel is on (<see cref="ActionsGate"/>: the option, the live-server marker);</item>
/// <item>the actor's tier equals the connection's role (<see cref="FeedRole"/>), and the actor is resolved: the local actor from the pipe client's process
/// token (never from the request), a web actor from its ticket (<see cref="IFeedActorTickets"/>, cached 30 s, capped at <see cref="MapFeedActionsOptions.PublicMaxSecurity"/>);</item>
/// <item>the durable audit can be written (<see cref="MapFeedWebAudit.Preflight"/>), else <c>audit-unavailable</c>;</item>
/// <item>the actor's rate limit (<see cref="MapFeedActionRateLimit"/>);</item>
/// <item>the command text is built (a template expanded by the daemon, a raw line checked) and judged by <see cref="MapFeedActionPolicy"/> on its resolved path;</item>
/// <item>a bulk template needs its confirm code (<see cref="MapFeedActionConfirm"/>);</item>
/// <item>the world thread checks the targets exist and runs each line through <see cref="WorldConsoleExecutor"/> built with
/// <see cref="WorldConsoleExecutor.CreateRemote"/> (one executor and one DI scope per request, never <c>ForConsole</c>), with a <see cref="GmActionContext"/> attached;</item>
/// <item>the answer, the log line, the in-memory audit entry and the durable record (flushed before the answer is queued).</item>
/// </list>
/// Thread affinity: everything but step 7's delegate runs on the connection's reader task; the shared tables (tickets, buckets, codes) are thread-safe.
/// </summary>
internal sealed class MapFeedActions : IFeedExtension
{
    /// <summary>The logger category of the action log lines.</summary>
    public const string LogCategory = "ArcaneCore.Ops.MapFeed.Actions";

    private static readonly TimeSpan TicketAge = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly MapFeedActionsOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly MapFeedWebAudit _audit;
    private readonly IFeedActorTickets? _tickets;
    private readonly MapFeedActionPolicy _policy;
    private readonly MapFeedActionConfirm _confirm;
    private readonly MapFeedActionRateLimit _limits;
    private readonly ConcurrentDictionary<string, CachedTicket> _ticketCache = new(StringComparer.Ordinal);
    private volatile ActionsGate _gate = ActionsGate.NotStarted;

    public MapFeedActions(IServiceProvider services, IOptions<MapFeedActionsOptions> options, ILoggerFactory loggers, MapFeedWebAudit audit)
    {
        _services = services;
        _options = options.Value;
        _loggers = loggers;
        _logger = loggers.CreateLogger(LogCategory);
        _time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _audit = audit;
        _tickets = services.GetService<IFeedActorTickets>();
        _policy = new MapFeedActionPolicy(_options);
        _confirm = new MapFeedActionConfirm(_time);
        _limits = new MapFeedActionRateLimit(_time);
    }

    /// <summary>Whether the channel accepts web actors (decided when the feed started).</summary>
    internal ActionsGate Gate => _gate;

    /// <summary>
    /// Forget the cached standing of web tickets so a logout, kick or revoke applies at once instead of after the 30 s cache: the ticket
    /// <paramref name="ticket"/> when given, else every cached ticket of <paramref name="account"/> (upper case).
    /// </summary>
    internal void ForgetTickets(string? ticket, string? account)
    {
        if (ticket is not null)
        {
            _ticketCache.TryRemove(ticket, out _);
            return;
        }

        foreach (KeyValuePair<string, CachedTicket> pair in _ticketCache)
        {
            if (string.Equals(pair.Value.Lookup.Account, account, StringComparison.Ordinal))
            {
                _ticketCache.TryRemove(pair.Key, out _);
            }
        }
    }

    /// <inheritdoc />
    public bool Handles(string op) => op is MapFeedExtensions.ActionOp or MapFeedExtensions.CapsOp or MapFeedExtensions.RoleOp;

    /// <inheritdoc />
    public void FeedStarted(WorldMapFeed feed)
        => _gate = ActionsGate.Decide(_options, feed.IsLiveServer, _tickets is not null,
            ActionsGate.PredicatePresent, _loggers.CreateLogger(WorldMapFeed.LogCategory));

    /// <inheritdoc />
    public Task<bool> HandleAsync(MapFeedConnection connection, string op, ReadOnlyMemory<byte> line)
        => op == MapFeedExtensions.CapsOp ? CapsAsync(connection, line) : ActionAsync(connection, line);

    // ---- caps ------------------------------------------------------------------------------------------------------------------

    private async Task<bool> CapsAsync(MapFeedConnection connection, ReadOnlyMemory<byte> line)
    {
        using JsonDocument document = FeedExtensionJson.Parse(line);
        if (!FeedExtensionJson.TryId(document.RootElement, out long id))
        {
            await connection.QueueAsync(MapFeedJson.ErrorLine(MapFeedProtocol.BadArgs, "caps needs an integer \"id\"")).ConfigureAwait(false);
            return false;
        }

        ActionsGate gate = _gate;
        FeedRole role = connection.Role;
        string? operatorName = role == FeedRole.Local ? connection.Identity.Operator : null;
        int settleSeconds = _options.SettleSeconds;
        await connection.QueueAsync(FeedExtensionJson.Result(id, ok: true, "caps", writer =>
        {
            writer.WriteBoolean("actions"u8, gate.On);
            // How long an action may take to answer: a client sizes its wait from it (the world waits this long up to twice per action).
            writer.WriteNumber("settleSeconds"u8, settleSeconds);
            if (!gate.On && gate.WhyOff is { } why)
            {
                writer.WriteString("actionsWhyOff"u8, why);
            }

            writer.WriteBoolean("login"u8, gate.Login);
            writer.WriteBoolean("liveServer"u8, gate.LiveServer);
            if (operatorName is not null)
            {
                writer.WriteString("operator"u8, operatorName);
            }

            writer.WriteString("role"u8, MapFeedRoles.Name(role));
            writer.WriteStartArray("templates"u8);
            if (gate.On)
            {
                foreach (ActionTemplate template in MapFeedActionCatalog.All)
                {
                    writer.WriteStringValue(template.Id);
                }
            }

            writer.WriteEndArray();
        })).ConfigureAwait(false);
        return true;
    }

    // ---- action ----------------------------------------------------------------------------------------------------------------

    /// <summary>What the pipeline learns about one request on the way, for the log line, the audit entries and the answer.</summary>
    private sealed class Attempt(MapFeedConnection connection, ActionRequest request)
    {
        public MapFeedConnection Connection { get; } = connection;

        public ActionRequest Request { get; } = request;

        public ActionActor? Actor { get; set; }

        /// <summary>The command line as logged: redacted, no prefix. The first line for a per-target template.</summary>
        public string Line { get; set; } = ConsoleCommandRedactor.Redact(request.Command ?? ("template:" + request.Template));

        /// <summary>The table logs this command itself when it runs (so the channel adds no duplicate in-memory audit entry).</summary>
        public bool TableLogs { get; set; }

        /// <summary>What the world thread did, written there and read here whatever ended the wait for its answer.</summary>
        public RunBox Box { get; } = new();

        /// <summary>The world thread ran at least one line (or was running it when the wait ended).</summary>
        public bool Ran => Box.Ran;
    }

    private async Task<bool> ActionAsync(MapFeedConnection connection, ReadOnlyMemory<byte> line)
    {
        if (!MapFeedActionProtocol.TryParse(line, out ActionRequest request, out FeedError error))
        {
            if (!request.HasId)
            {
                connection.Logger.LogWarning("map feed: an action line without an integer id from {Client}", connection.Client);
                await connection.QueueAsync(MapFeedJson.ErrorLine(error.Code, error.Message)).ConfigureAwait(false);
                return false;
            }

            await connection.QueueAsync(FeedExtensionJson.Refused(request.Id, "action", error.Code, error.Message)).ConfigureAwait(false);
            return true;
        }

        ActionsGate gate = _gate;
        if (!gate.On)
        {
            await connection.QueueAsync(FeedExtensionJson.Refused(request.Id, "action", gate.OffCode ?? ActionCodes.ActionsOff, gate.WhyOff ?? "actions are off")).ConfigureAwait(false);
            return true;
        }

        var attempt = new Attempt(connection, request);
        ActionOutcome outcome;
        try
        {
            outcome = await ProcessAsync(attempt, gate).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The connection or the daemon ended before the world thread began (a started command is waited for in ExecuteAsync): nothing ran.
            outcome = ActionOutcome.CancelledBeforeRun();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("ops.map-feed-actions", ex);
            _logger.LogError(ex, "atlas action {Id} failed inside the server", request.Id);
            outcome = ActionOutcome.Refused(ActionCodes.Failed, "the action failed inside the server; see the server log");
        }

        await FinishAsync(attempt, outcome).ConfigureAwait(false);
        return true;
    }

    private async Task<ActionOutcome> ProcessAsync(Attempt attempt, ActionsGate gate)
    {
        MapFeedConnection connection = attempt.Connection;
        ActionRequest request = attempt.Request;

        // 1. the connection's own bucket, before anything else: a refusal costs a log line and (for the web) a durable write, so even a client
        // that never gets as far as an actor is bounded. A web connection gets twice the actor's burst, because it may carry several actors.
        (int connPerSecond, int connBurst) = connection.Role == FeedRole.Web ? (_options.WebPerSecond, _options.WebBurst * 2) : (_options.LocalPerSecond, _options.LocalBurst);
        if (!_limits.TryTake("conn:" + connection.ConnectionId.ToString(CultureInfo.InvariantCulture), connPerSecond, connBurst, out int connRetryMs))
        {
            return new ActionOutcome { Ok = false, Code = ActionCodes.RateLimited, Message = "too many requests on this connection; slow down", RetryMs = connRetryMs };
        }

        // 2. tier and actor
        if (!string.Equals(request.Tier, MapFeedRoles.Name(connection.Role), StringComparison.Ordinal))
        {
            return ActionOutcome.Refused(ActionCodes.RoleMismatch, $"this connection is {MapFeedRoles.Name(connection.Role)}; a {request.Tier} actor cannot act on it");
        }

        if (connection.Role == FeedRole.Web)
        {
            if (!gate.Login)
            {
                return ActionOutcome.Refused(ActionCodes.WebLoginOff, "web actors are not accepted on this daemon (World:Control:MapFeed:Actions:WebLogin)");
            }

            (ActionActor? web, ActionOutcome? refusal) = await ResolveWebAsync(connection, request).ConfigureAwait(false);
            if (web is null)
            {
                return refusal!;
            }

            attempt.Actor = web;
        }
        else
        {
            attempt.Actor = new ActionActor(connection.Identity.ActorName, _options.LocalAccountId, _options.LocalSecurity, IsPublic: false, Ticket: null);
        }

        ActionActor actor = attempt.Actor;

        // 3. the durable audit must be writable
        if ((actor.IsPublic || _options.AuditLocalDurable) && !_audit.Preflight())
        {
            return ActionOutcome.Refused(ActionCodes.AuditUnavailable, "the audit file cannot be written, so nothing was run");
        }

        // 4. rate limit
        int perSecond = actor.IsPublic ? _options.WebPerSecond : _options.LocalPerSecond;
        int burst = actor.IsPublic ? _options.WebBurst : _options.LocalBurst;
        if (!_limits.TryTake(actor.Name, perSecond, burst, out int retryMs))
        {
            return new ActionOutcome { Ok = false, Code = ActionCodes.RateLimited, Message = "too many actions; slow down", RetryMs = retryMs };
        }

        // 5. the command text
        ActionTemplate? template = null;
        IReadOnlyList<ExpandedStep> steps;
        if (request.Template is { } templateId)
        {
            template = MapFeedActionCatalog.Find(templateId);
            if (template is null)
            {
                return ActionOutcome.Refused(ActionCodes.UnknownCommand, $"there is no template '{templateId}'");
            }

            if (MapFeedActionCatalog.Expand(template, request, out FeedError expandError) is not { } expanded)
            {
                return ActionOutcome.Refused(expandError);
            }

            steps = expanded;
        }
        else
        {
            steps = [new ExpandedStep(null, MapFeedActionPolicy.Normalize(request.Command!))];
        }

        attempt.Line = ConsoleCommandRedactor.Redact(steps[0].Line);

        // 6. policy, on the path the table resolves the line to
        CommandTable table = _services.GetRequiredService<CommandTableSource>().Current;
        if (_policy.Check(table, steps[0].Line, actor.Security, actor.IsPublic, out CommandLookup found) is { } denied)
        {
            return ActionOutcome.Refused(denied);
        }

        attempt.TableLogs = found.Command is { } command && GmCommandLog.ShouldLog(command, table.Gm);

        // 7. bulk confirm
        if (template is { PerTarget: true } && request.Targets.Length > _options.BulkConfirmAbove)
        {
            string binding = BindingOf(template, request);
            if (request.Confirm is null)
            {
                string code = _confirm.Issue(actor.Name, binding);
                string summary = string.Create(CultureInfo.InvariantCulture, $"{template.Summary} ({template.Id}) on {request.Targets.Length} targets");
                return new ActionOutcome
                {
                    Ok = false,
                    Code = ActionCodes.ConfirmRequired,
                    Message = $"{request.Targets.Length} targets need a confirm code; send the same request with \"confirm\":\"{code}\" within {MapFeedActionConfirm.ExpiresInSeconds} seconds",
                    Confirm = (code, summary),
                };
            }

            if (_confirm.Check(actor.Name, binding, request.Confirm) != ConfirmVerdict.Confirmed)
            {
                return ActionOutcome.Refused(ActionCodes.ConfirmWrong, "the confirm code is wrong, expired or for another request");
            }
        }

        // 8. run
        return await ExecuteAsync(attempt, steps, template is { PerTarget: true }).ConfigureAwait(false);
    }

    private static string BindingOf(ActionTemplate template, ActionRequest request)
    {
        var text = new StringBuilder(template.Id).Append('|');
        foreach ((string key, JsonElement value) in request.Args.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            text.Append(key).Append('=').Append(value.GetRawText()).Append(',');
        }

        text.Append('|');
        foreach (ulong guid in request.Targets)
        {
            text.Append(MapFeedActionCatalog.Guid(guid)).Append(',');
        }

        if (request.Point is { } p)
        {
            text.Append(string.Create(CultureInfo.InvariantCulture, $"|{p.Map}:{p.Instance}:{p.X:R}:{p.Y:R}:{p.Z:R}:{p.O:R}"));
        }

        return text.ToString();
    }

    // ---- web actors --------------------------------------------------------------------------------------------------------------

    private sealed record CachedTicket(FeedTicketLookup Lookup, DateTimeOffset Expires);

    private async Task<(ActionActor? Actor, ActionOutcome? Refusal)> ResolveWebAsync(MapFeedConnection connection, ActionRequest request)
    {
        if (request.Ticket is not { Length: > 0 and <= 512 } ticket)
        {
            return (null, ActionOutcome.Refused(ActionCodes.NoTicket, "a web actor needs a ticket"));
        }

        DateTimeOffset now = _time.GetUtcNow();
        FeedTicketLookup lookup;
        if (_ticketCache.TryGetValue(ticket, out CachedTicket? cached) && cached.Expires > now)
        {
            lookup = cached.Lookup;
        }
        else
        {
            // Unknown tickets are not cached, so a stream of guesses would reach the ticket service each time: bound them per connection.
            if (!_limits.TryTake("ticket:" + connection.ConnectionId.ToString(CultureInfo.InvariantCulture), _options.WebPerSecond, _options.WebBurst * 4, out int retryMs))
            {
                return (null, new ActionOutcome { Ok = false, Code = ActionCodes.RateLimited, Message = "too many tickets tried; slow down", RetryMs = retryMs });
            }

            try
            {
                lookup = await _tickets!.ResolveAsync(ticket, connection.Lifetime).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                FaultLedger.Shared.Record("ops.map-feed-actions", ex);
                _logger.LogError(ex, "atlas action: the ticket service failed");
                return (null, ActionOutcome.Refused(ActionCodes.TicketUnavailable, "the ticket service failed; try again"));
            }

            if (lookup.Status == FeedTicketStatus.Valid)
            {
                if (_ticketCache.Count > 512)
                {
                    foreach (string stale in _ticketCache.Where(t => t.Value.Expires <= now).Select(t => t.Key).ToList())
                    {
                        _ticketCache.TryRemove(stale, out _);
                    }
                }

                _ticketCache[ticket] = new CachedTicket(lookup, now + TicketAge);
            }
            else
            {
                _ticketCache.TryRemove(ticket, out _);
            }
        }

        switch (lookup.Status)
        {
            case FeedTicketStatus.Expired:
                return (null, ActionOutcome.Refused(ActionCodes.Expired, "the ticket's session has ended; log in again"));
            case FeedTicketStatus.Banned:
                return (null, ActionOutcome.Refused(ActionCodes.Banned, "the account may not act"));
            case FeedTicketStatus.Valid:
                break;
            default:
                return (null, ActionOutcome.Refused(ActionCodes.NoTicket, "the ticket is not known; log in again"));
        }

        AccountSecurity security = (AccountSecurity)Math.Min((int)lookup.Security, (int)_options.PublicMaxSecurity);
        if (security < _options.WebLoginMinSecurity)
        {
            return (null, ActionOutcome.Refused(ActionCodes.Security, "this account's level may not act through the web"));
        }

        return (new ActionActor("atlas-web:" + lookup.Account, lookup.AccountId, security, IsPublic: true, ticket), null);
    }

    // ---- execution ---------------------------------------------------------------------------------------------------------------

    /// <summary>The result of the world-thread part: the runs that happened, or the first target that is not in the world.</summary>
    private sealed class ActionRun
    {
        private readonly List<StepRun> _steps = [];

        public ulong? Gone { get; set; }

        /// <summary>The steps run so far (a copy: the world thread may still be adding).</summary>
        public StepRun[] Steps
        {
            get
            {
                lock (_steps)
                {
                    return [.. _steps];
                }
            }
        }

        public void Add(StepRun step)
        {
            lock (_steps)
            {
                _steps.Add(step);
            }
        }
    }

    private sealed record StepRun(ulong? Target, CommandContext? Context, string[] Lines);

    /// <summary>
    /// What the world-thread delegate did, owned by the delegate and read by the reader task after the wait for it ended for any reason (a value,
    /// the query timeout, the connection ending). The delegate, not the return value, is the truth about whether anything ran: the world runtime
    /// still runs a delegate that began before its caller was cancelled, and drops its result.
    /// </summary>
    private sealed class RunBox
    {
        private const int Queued = 0;
        private const int Started = 1;
        private const int Abandoned = 2;

        private int _state;
        private Task? _settle;

        /// <summary>The first line was handed to the executor: a command ran or is running.</summary>
        public volatile bool Ran;

        /// <summary>Filled progressively on the world thread (partial steps stay visible when the delegate faults).</summary>
        public ActionRun Run { get; } = new();

        /// <summary>Completes when the delegate returns (result) or throws (exception).</summary>
        public TaskCompletionSource<ActionRun> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The delegate was entered on the world thread.</summary>
        public bool IsStarted => Volatile.Read(ref _state) == Started;

        /// <summary>World thread: claim the work. False when the reader already gave up on it (it must then do nothing).</summary>
        public bool TryStart() => Interlocked.CompareExchange(ref _state, Started, Queued) == Queued;

        /// <summary>Reader: give up on work that has not begun. True means nothing ran and nothing will; false means the delegate has begun.</summary>
        public bool TryAbandon() => Interlocked.CompareExchange(ref _state, Abandoned, Queued) is Queued or Abandoned;

        /// <summary>Completes when the delegate returned and every context it opened has settled. Reader task only.</summary>
        public Task GetSettleTask()
            => _settle ??= Done.Task.IsCompleted
                ? WhenContextsSettled()
                : Done.Task.ContinueWith(_ => WhenContextsSettled(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();

        private Task WhenContextsSettled() => Task.WhenAll(Run.Steps.Select(s => s.Context?.WhenSettled ?? Task.CompletedTask));
    }

    private async Task<ActionOutcome> ExecuteAsync(Attempt attempt, IReadOnlyList<ExpandedStep> steps, bool perTarget)
    {
        MapFeedConnection connection = attempt.Connection;
        ActionRequest request = attempt.Request;
        ActionActor actor = attempt.Actor!;
        WorldRuntime world = connection.Feed.World;
        RunBox box = attempt.Box;
        TimeSpan settleWait = TimeSpan.FromSeconds(_options.SettleSeconds);

        // The scope is released by "settled" or by the five-minute cap (DisposeWhenSettledAsync), never by control flow: see the finally block.
        AsyncServiceScope scope = _services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        try
        {
            long id = request.Id;
            var sink = new ActionSink(late => connection.QueueLate(FeedExtensionJson.ActionLine(id, late)));
            WorldConsoleExecutor executor = WorldConsoleExecutor.CreateRemote(
                scope.ServiceProvider,
                _services.GetRequiredService<OpcodeTable>(),
                world,
                _services.GetRequiredService<SessionRegistry>(),
                _services.GetService<IOptions<WorldSessionOptions>>()?.Value ?? new WorldSessionOptions(),
                _loggers.CreateLogger<WorldSession>(),
                sink,
                actor.Security,
                actor.Name,
                connection.Lifetime);

            ActionRun? run = null;
            FeedError? failure = null;
            try
            {
                (run, failure) = await connection.InvokeWorldAsync(() => RunOnWorld(box, executor, world, _services, actor, request, steps, sink)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (box.TryAbandon())
                {
                    throw; // the world thread never began: nothing ran (ActionAsync records "cancelled")
                }

                // The connection ended after the world thread began: the command runs on, so what it does is recorded below.
            }

            if (run is null)
            {
                if (box.TryAbandon())
                {
                    return ActionOutcome.Refused(failure!.Value); // timeout or world-stopping before the world thread began: nothing ran
                }

                // The delegate began, then the timeout, the connection or a fault took the answer away. Refusing now would deny a command that ran
                // (or is running), so wait for the delegate itself, bounded like the settle wait, and no longer cancellable: it is committed.
                try
                {
                    run = await box.Done.Task.WaitAsync(settleWait, CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return Outcome(request, box.Run, sink.Finish(), settled: false, cancelled: connection.Lifetime.IsCancellationRequested, perTarget);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogError(ex, "atlas action {Id} failed inside the server", request.Id);
                    return ActionOutcome.Refused(ActionCodes.Failed, "the action failed inside the server; see the server log");
                }
            }

            if (run.Gone is { } gone)
            {
                return ActionOutcome.Refused(ActionCodes.TargetGone, $"target {MapFeedActionCatalog.Guid(gone)} is not in the world");
            }

            Task all = box.GetSettleTask();
            bool settled = true;
            bool cancelled = false;
            if (!all.IsCompleted)
            {
                try
                {
                    await all.WaitAsync(settleWait, connection.Lifetime).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    settled = false;
                }
                catch (OperationCanceledException)
                {
                    settled = false;
                    cancelled = true;
                }
            }

            return Outcome(request, run, sink.Finish(), settled, cancelled, perTarget);
        }
        finally
        {
            if (!box.IsStarted)
            {
                await scope.DisposeAsync().ConfigureAwait(false); // the delegate never began (and now never will): nothing uses the scope
            }
            else
            {
                Task settle = box.GetSettleTask();
                if (settle.IsCompletedSuccessfully)
                {
                    await scope.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    _ = DisposeWhenSettledAsync(scope, settle); // a command is still open (or still running on the world thread)
                }
            }
        }
    }

    /// <summary>The world-thread delegate: claims the work (a reader that gave up first wins and nothing runs), then runs it and completes <see cref="RunBox.Done"/> either way.</summary>
    private static ActionRun RunOnWorld(
        RunBox box, WorldConsoleExecutor executor, WorldRuntime world, IServiceProvider services, ActionActor actor, ActionRequest request,
        IReadOnlyList<ExpandedStep> steps, ActionSink sink)
    {
        if (!box.TryStart())
        {
            return box.Run; // abandoned while queued; the caller has already answered and drops this
        }

        try
        {
            RunSteps(box, executor, world, services, actor, request, steps, sink);
            box.Done.TrySetResult(box.Run);
            return box.Run;
        }
        catch (Exception ex)
        {
            box.Done.TrySetException(ex);
            throw;
        }
    }

    /// <summary>On the world thread: every target must exist, then each line runs with its action context attached.</summary>
    private static void RunSteps(
        RunBox box, WorldConsoleExecutor executor, WorldRuntime world, IServiceProvider services, ActionActor actor, ActionRequest request,
        IReadOnlyList<ExpandedStep> steps, ActionSink sink)
    {
        foreach (ulong guid in request.Targets)
        {
            if (GmActionContext.FindUnit(world, services, guid) is null)
            {
                box.Run.Gone = guid;
                return;
            }
        }

        foreach (ExpandedStep step in steps)
        {
            ulong[] selection = step.Target is { } target ? [target] : request.Targets;
            var action = new GmActionContext
            {
                ActorName = actor.Name,
                ActorAccountId = actor.AccountId,
                IsPublic = actor.IsPublic,
                Selection = selection,
                Primary = selection.Length > 0 ? selection[0] : 0,
                Point = request.Point,
            };
            int from = sink.Count;
            box.Ran = true; // before the executor: from here a command may be running, whatever the reader sees
            CommandContext? context = executor.Run(step.Line, c => GmActionContext.Attach(c, action));
            box.Run.Add(new StepRun(step.Target, context, sink.Slice(from, sink.Count)));
        }
    }

    private static ActionOutcome Outcome(ActionRequest request, ActionRun run, string[] lines, bool settled, bool cancelled, bool perTarget)
    {
        StepRun[] ran = run.Steps;
        string? failure = ran.Select(s => s.Context?.Failure).FirstOrDefault(f => f is not null);
        bool ok = failure is null;
        IReadOnlyList<TargetOutcome>? targets = null;
        if (perTarget)
        {
            targets = [.. ran.Select(s => new TargetOutcome(s.Target!.Value, s.Context?.Failure is null, s.Context?.Failure, s.Lines))];
        }
        else if (request.Targets.Length > 0)
        {
            targets = [.. request.Targets.Select(t => new TargetOutcome(t, ok, failure, null))];
        }

        return new ActionOutcome
        {
            Ok = ok,
            Code = failure,
            Message = ok ? null : $"the command failed ({failure})",
            Lines = lines,
            Settled = settled,
            Cancelled = cancelled,
            Failure = failure,
            Targets = targets,
        };
    }

    private async Task DisposeWhenSettledAsync(AsyncServiceScope scope, Task settled)
    {
        try
        {
            await settled.WaitAsync(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning("atlas action: a command was still open after 5 minutes; its service scope is released now");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("ops.map-feed-actions", ex);
        }
        finally
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---- the record --------------------------------------------------------------------------------------------------------------

    private async Task FinishAsync(Attempt attempt, ActionOutcome outcome)
    {
        MapFeedConnection connection = attempt.Connection;
        ActionRequest request = attempt.Request;
        ActionActor? actor = attempt.Actor;
        string result = outcome.AuditResult;
        string who = actor?.Name ?? "-";
        string point = request.Point is { } p
            ? string.Create(CultureInfo.InvariantCulture, $"{p.Map}:{p.X:F1},{p.Y:F1},{p.Z:F1}")
            : "none";

        bool durable = connection.Role == FeedRole.Web || (actor is not null && _options.AuditLocalDurable);

        // A web request refused before an actor was resolved is merged with the identical refusals before it: one durable record per run of the
        // same code (plus a count when the run ends), and its repeats are logged at Debug. Nothing ran and no account is named, so nothing is lost.
        bool repeat = false;
        WebAuditRecord? summary = null;
        if (durable && actor is null && result != ActionCodes.AuditUnavailable)
        {
            (repeat, summary) = NoteRefusal(connection.ConnectionId, result, request.Ip);
        }

        _logger.Log(
            repeat ? LogLevel.Debug : LogLevel.Information,
            "{Line}",
            string.Create(CultureInfo.InvariantCulture, $"atlas action actor={who} tier={request.Tier} cmd={attempt.Line} targets={request.Targets.Length} point={point} -> {result}"));

        if (actor is not null && !(attempt.TableLogs && attempt.Ran) && _services.GetService<GmAuditFeature>() is { } audit)
        {
            audit.RecordCommand(
                actor.AccountId ?? 0,
                actor.Name,
                attempt.Line,
                GmCommandLog.DescribeConsole(attempt.Line) + " [Atlas " + actor.Name + "] -> " + result);
        }

        if (summary is not null)
        {
            await _audit.AppendAsync(summary).ConfigureAwait(false);
        }

        if (durable && result != ActionCodes.AuditUnavailable && !repeat)
        {
            bool written = await _audit.AppendAsync(new WebAuditRecord(
                "action", actor?.Name, actor?.AccountId, request.Ticket, request.Ip, attempt.Line, request.Targets.Length, result, outcome.AuditCode)).ConfigureAwait(false);
            if (!written && actor is not null)
            {
                _logger.LogError("atlas action: the durable audit record of an action by {Actor} was lost ({Result})", actor.Name, result);
            }
        }

        try
        {
            await connection.QueueAsync(AnswerLine(request.Id, outcome)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
        {
            // The client is gone or the daemon is stopping: the records above are the point of this method, and there is nobody to answer.
        }
    }

    // ---- repeated refusals -------------------------------------------------------------------------------------------------------

    /// <summary>The most repeats one summary record stands for.</summary>
    private const int MaxRepeats = 100;

    /// <summary>The run of identical pre-actor refusals on one connection. Touched by its reader task, and once more when the connection ends.</summary>
    private sealed class RefusalRun
    {
        public string? Code;
        public int Repeats;
        public string? Ip;
    }

    private readonly ConcurrentDictionary<long, RefusalRun> _refusals = new();

    /// <summary>
    /// Count a pre-actor refusal. <c>Repeat</c> is true when the previous refusal on this connection had the same code (nothing new to write);
    /// <c>Summary</c> is the record for a finished run of repeats (the code changed, or the run reached <see cref="MaxRepeats"/>), else null.
    /// </summary>
    private (bool Repeat, WebAuditRecord? Summary) NoteRefusal(long connectionId, string code, string? ip)
    {
        RefusalRun run = _refusals.GetOrAdd(connectionId, static _ => new RefusalRun());
        lock (run)
        {
            if (string.Equals(run.Code, code, StringComparison.Ordinal))
            {
                run.Repeats++;
                if (run.Repeats >= MaxRepeats)
                {
                    return (true, TakeSummary(run));
                }

                return (true, null);
            }

            WebAuditRecord? finished = TakeSummary(run);
            run.Code = code;
            run.Ip = ip;
            return (false, finished);
        }
    }

    private static WebAuditRecord? TakeSummary(RefusalRun run)
    {
        if (run.Repeats == 0 || run.Code is null)
        {
            return null;
        }

        var summary = new WebAuditRecord("refusals", null, null, null, run.Ip, null, run.Repeats, run.Code, run.Code);
        run.Repeats = 0;
        return summary;
    }

    /// <inheritdoc />
    public async ValueTask ConnectionEndedAsync(MapFeedConnection connection)
    {
        if (_refusals.TryRemove(connection.ConnectionId, out RefusalRun? run))
        {
            WebAuditRecord? summary;
            lock (run)
            {
                summary = TakeSummary(run);
            }

            if (summary is not null)
            {
                await _audit.AppendAsync(summary).ConfigureAwait(false);
            }
        }
    }

    private static byte[] AnswerLine(long id, ActionOutcome outcome)
        => FeedExtensionJson.Result(id, outcome.Ok, "action", writer =>
        {
            if (!outcome.Ok)
            {
                writer.WriteString("code"u8, outcome.Code ?? ActionCodes.Failed);
                writer.WriteString("message"u8, outcome.Message ?? string.Empty);
            }

            writer.WriteStartArray("lines"u8);
            foreach (string line in outcome.Lines)
            {
                writer.WriteStringValue(line);
            }

            writer.WriteEndArray();
            writer.WriteBoolean("settled"u8, outcome.Settled);
            if (outcome.Failure is { } failure)
            {
                writer.WriteString("failure"u8, failure);
            }

            if (outcome.Targets is { } targets)
            {
                writer.WriteStartArray("targets"u8);
                foreach (TargetOutcome target in targets)
                {
                    writer.WriteStartObject();
                    writer.WriteString("guid"u8, target.Guid.ToString("X16", CultureInfo.InvariantCulture));
                    writer.WriteBoolean("ok"u8, target.Ok);
                    if (target.Failure is { } targetFailure)
                    {
                        writer.WriteString("failure"u8, targetFailure);
                    }

                    if (target.Lines is { } targetLines)
                    {
                        writer.WriteStartArray("lines"u8);
                        foreach (string line in targetLines)
                        {
                            writer.WriteStringValue(line);
                        }

                        writer.WriteEndArray();
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            if (outcome.RetryMs is { } retry)
            {
                writer.WriteNumber("retryMs"u8, retry);
            }

            if (outcome.Confirm is { } confirm)
            {
                writer.WriteStartObject("confirm"u8);
                writer.WriteString("code"u8, confirm.Code);
                writer.WriteNumber("expiresIn"u8, MapFeedActionConfirm.ExpiresInSeconds);
                writer.WriteString("summary"u8, confirm.Summary);
                writer.WriteEndObject();
            }
        });
}
