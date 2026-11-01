using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Cluster.Supervision;
using ArcaneCore.World.Ops.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Gateway.Handoff;

/// <summary>
/// <see cref="IWorkerHandoffDriver"/> of the gateway: drains the slot a worker belongs to with <see cref="GatewayHandoffService.HandoffAsync"/>. The operator may name the slot (its
/// <c>Gateway:Workers:N:NodeId</c>) or the process that is the slot's active worker now.
/// </summary>
internal sealed class GatewaySlotHandoffDriver(GatewayHandoffService service) : IWorkerHandoffDriver
{
    /// <summary>Test seam: the coordinator's per-stage hook of every handoff this driver starts.</summary>
    public Func<WorldHandoffStage, CancellationToken, Task>? AfterStage { get; set; }

    /// <summary>The slot <paramref name="worker"/> names (the slot's node id, or its active process), or null when it names none.</summary>
    public string? SlotOf(string worker)
    {
        foreach (string slot in service.Slots)
        {
            if (string.Equals(slot, worker, StringComparison.Ordinal)
                || (service.SlotOf(slot) is { } s && s.IsActive(worker)))
            {
                return slot;
            }
        }

        return null;
    }

    public Task<WorldHandoffRecord> HandoffAsync(WorkerNodeId worker, CancellationToken cancellationToken)
    {
        string slot = SlotOf(worker.Value)
            ?? throw new WorldHandoffUnavailableException($"gateway worker '{worker}' has no standby configured (Gateway:Workers:N:StandbyNodeId)");
        return service.HandoffAsync(slot, AfterStage, cancellationToken);
    }
}

/// <summary>
/// <see cref="IWorkerRestartRequester"/> of the gateway: after a completed handoff the retired process (the slot's standby now) is relaunched by the cluster host supervisor as the
/// slot's next standby (<c>--Cluster:Worker:Standby=true</c>, the blue/green swap), addressed by child name (<see cref="SupervisorChildRestartRequest"/>).
/// "Accepted" means the line was queued toward a supervisor; the supervisor's verdict is not awaited. Thread-safe: <see cref="SupervisorProtocolChannel.Send"/> only queues.
/// </summary>
internal sealed class SupervisorStandbyRestartRequester(
    SupervisorProtocolChannel channel, GatewayHandoffService service, GatewaySlotHandoffDriver driver, ILogger? logger = null) : IWorkerRestartRequester
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public Task<bool> RequestRestartAsync(WorkerNodeId worker, string requestedBy, CancellationToken cancellationToken)
    {
        if (!channel.IsSupervised)
        {
            _logger.LogWarning("Restart of the retired {Worker} not requested: this gateway is not running under a cluster host supervisor", worker);
            return Task.FromResult(false);
        }

        // After the swap the slot's standby is the process that was just retired: that is the child to relaunch.
        string child = driver.SlotOf(worker.Value) is { } slot && service.SlotOf(slot) is { } s ? s.StandbyNode : worker.Value;
        channel.Send(SupervisorProtocol.FormatChildRestartRequest(new SupervisorChildRestartRequest(channel.Token!, child, Standby: true, requestedBy)));
        return Task.FromResult(true);
    }
}

/// <summary>
/// The gateway end of the operator <c>.cluster restart</c> (design _wf/handoff-control-DESIGN.md section 3.4, commit C14). A worker sends the operator's request as a
/// <see cref="ControlSubmit"/> of kind <see cref="ControlSubmitKind.OperatorRestart"/> on the link that carries the operator's session; the gateway
/// <list type="number">
/// <item><description>checks that the account's connection is the one that sent it (same session generation, forwarded on that very link) and re-reads the account's security in the
/// authentication database: a claim the database contradicts, or an account below Administrator, is refused (never believe the worker's word);</description></item>
/// <item><description>answers <see cref="ControlSubmitReply"/> at once (the worker prints "started" while its world thread still runs);</description></item>
/// <item><description>runs the unchanged <see cref="ClusterRestartOrchestration"/> with the gateway's handoff service as driver and the supervisor (by child name) as requester;</description></item>
/// <item><description>sends the verdict as a <see cref="ControlNotice"/> of kind <see cref="ControlNoticeKind.OperatorText"/> to the operator's connection found with
/// <see cref="GatewayAccountClaims.Find"/>, on its current link: the promoted world after a completed handoff, the old one after an abort.</description></item>
/// </list>
/// Opt-in with <c>Gateway:Handoff:Enabled</c> (registered with the handoff hosting). Transfer submits are ignored here (the transfer service answers those). Thread-safe; runs are tracked and
/// awaited (bounded) by <see cref="StopAsync"/>. A handoff past its epoch takeover finishes even if the host stops (the coordinator ignores the token afterwards).
/// </summary>
public sealed class GatewayRestartOrchestration : IHostedService
{
    private readonly GatewayServer _server;
    private readonly GatewaySlotHandoffDriver _driver;
    private readonly ClusterRestartOrchestration _orchestration;
    private readonly ILogger _logger;
    private readonly ConditionalWeakTable<GatewaySessionLinkClient, object> _attached = new();
    private readonly ConcurrentDictionary<Task, byte> _runs = new();
    private readonly CancellationTokenSource _stopping = new();
    private int _started;

    /// <param name="requester">Asks the supervisor to relaunch the retired process; null uses this process's supervisor channel (not supervised: the restart is reported as not queued).</param>
    public GatewayRestartOrchestration(GatewayServer server, GatewayHandoffService handoff, IWorkerRestartRequester? requester = null, ILoggerFactory? loggers = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(handoff);
        loggers ??= NullLoggerFactory.Instance;
        _server = server;
        _logger = loggers.CreateLogger<GatewayRestartOrchestration>();
        _driver = new GatewaySlotHandoffDriver(handoff);
        requester ??= new SupervisorStandbyRestartRequester(SupervisorProtocolChannel.Process, handoff, _driver, loggers.CreateLogger<SupervisorStandbyRestartRequester>());
        _orchestration = new ClusterRestartOrchestration(_driver, requester, placement: null, loggers.CreateLogger<ClusterRestartOrchestration>());
    }

    /// <summary>Test seam: the coordinator's per-stage hook of every handoff an operator restart starts.</summary>
    internal Func<WorldHandoffStage, CancellationToken, Task>? AfterStageForTests
    {
        get => _driver.AfterStage;
        set => _driver.AfterStage = value;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _server.WorkerLinkUp += OnWorkerLinkUp;
        }

        // Links that came up before this service started.
        foreach (GatewayWorkerLink worker in _server.Workers)
        {
            Attach(worker.Link);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _server.WorkerLinkUp -= OnWorkerLinkUp;
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_runs.Keys).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            _logger.LogWarning("Gateway operator restart: {Count} run(s) still going when the stop wait ended", _runs.Count);
        }
    }

    /// <summary>
    /// The listener is set when a worker link registers (the supervisor task raises <see cref="GatewayServer.WorkerLinkUp"/> right after the link is published), so a submit that arrives in
    /// the first instants of a link, before this runs, is not seen. An operator types commands long after that.
    /// </summary>
    private void OnWorkerLinkUp(string nodeId) => Attach(_server.FindLinkByNode(nodeId));

    private void Attach(GatewaySessionLinkClient? client)
    {
        if (client is null || !_attached.TryAdd(client, new object()))
        {
            return;
        }

        client.ControlSubmitReceived += submit => OnSubmit(client, submit);
    }

    private void OnSubmit(GatewaySessionLinkClient link, ControlSubmit submit)
    {
        if (submit.Body is not ControlRestartSubmit restart)
        {
            return; // transfer requests are not ours
        }

        Task run = Task.Run(() => HandleAsync(link, submit.RequestId, restart), CancellationToken.None);
        _runs[run] = 0;
        _ = run.ContinueWith(static (done, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(done, out _), _runs, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task HandleAsync(GatewaySessionLinkClient link, ulong requestId, ControlRestartSubmit request)
    {
        GatewayClientConnection? connection = null;
        Guid operation = Guid.Empty;
        try
        {
            (string? refusal, AccountSecurity security, connection) = await CheckAsync(link, request, _stopping.Token).ConfigureAwait(false);
            if (refusal is not null)
            {
                _logger.LogWarning("Operator restart of account {Account} refused: {Reason}", request.AccountId, refusal);
                link.SendControlSubmitReply(new ControlSubmitReply(requestId, false, Guid.Empty, refusal));
                return;
            }

            operation = Guid.NewGuid();
            if (!link.SendControlSubmitReply(new ControlSubmitReply(requestId, true, operation, string.Empty)))
            {
                _logger.LogWarning("Operator restart of account {Account} not run: the worker link is gone", request.AccountId);
                return;
            }

            ClusterRestartResult result = await _orchestration.RunAsync(security, connection!.AccountName, request.Arguments.Trim(), _stopping.Token).ConfigureAwait(false);
            Notify(connection, request.AccountId, operation, ToOutcome(result.Outcome), result.Message);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            _logger.LogWarning("Operator restart of account {Account} was cancelled by the gateway stopping", request.AccountId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("cluster.operator-restart", ex, "Operator restart of account {Account} failed", request.AccountId);
            if (operation == Guid.Empty)
            {
                link.SendControlSubmitReply(new ControlSubmitReply(requestId, false, Guid.Empty, "the gateway could not check the request; see its log"));
            }
            else if (connection is not null)
            {
                Notify(connection, request.AccountId, operation, ControlNoticeOutcome.Failed, "Cluster restart failed unexpectedly; see the gateway log and '.cluster handoff status'.");
            }
        }
    }

    /// <summary>Everything the gateway believes about a restart request before it runs one; a non-null first element is the reason to refuse.</summary>
    private async Task<(string? Refusal, AccountSecurity Security, GatewayClientConnection? Connection)> CheckAsync(
        GatewaySessionLinkClient link, ControlRestartSubmit request, CancellationToken cancellationToken)
    {
        string worker = request.Arguments.Trim();
        if (worker.Length == 0 || worker.Any(char.IsWhiteSpace))
        {
            return ("Syntax: .cluster restart $worker", AccountSecurity.Player, null);
        }

        GatewayClientConnection? connection = _server.Claims.Find(request.AccountId);
        if (connection is null || connection.SessionGeneration != request.SessionGeneration || !ReferenceEquals(connection.CurrentLink, link))
        {
            return ("the request does not come from the link that carries your session", AccountSecurity.Player, null);
        }

        Account? stored;
        await using (AsyncServiceScope scope = _server.Scopes.CreateAsyncScope())
        {
            stored = await scope.ServiceProvider.GetRequiredService<IAccountStore>().FindByUsernameAsync(connection.AccountName, cancellationToken).ConfigureAwait(false);
        }

        if (stored is null || stored.Id != request.AccountId || stored.Status != AccountStatus.Active)
        {
            return ("the account is not known or not active", AccountSecurity.Player, null);
        }

        if (request.ClaimedSecurityLevel > (byte)stored.Security)
        {
            return ("the security level the worker claimed contradicts the account database", AccountSecurity.Player, null);
        }

        if (stored.Security < AccountSecurity.Administrator)
        {
            return ("This command is not available to you.", AccountSecurity.Player, null);
        }

        if (_driver.SlotOf(worker) is null)
        {
            return ($"Unknown worker '{worker}'; nothing was done.", AccountSecurity.Player, null);
        }

        return (null, stored.Security, connection);
    }

    /// <summary>Sends the verdict to the operator's connection, but only if it is still the connection the request came from (a newer login of the account is not the operator).</summary>
    private void Notify(GatewayClientConnection operatorConnection, int accountId, Guid operation, ControlNoticeOutcome outcome, string text)
    {
        GatewayClientConnection? current = _server.Claims.Find(accountId);
        if (current is null || !ReferenceEquals(current, operatorConnection) || current.CurrentLink is not { } link)
        {
            _logger.LogInformation("Operator restart {Operation}: the operator left; verdict not delivered: {Text}", operation, text);
            return;
        }

        if (!link.SendControlNotice(new ControlNotice(ControlNoticeKind.OperatorText, current.SessionGeneration, accountId, 0, operation, outcome, text)))
        {
            _logger.LogWarning("Operator restart {Operation}: the operator's link is down; verdict not delivered: {Text}", operation, text);
        }
    }

    private static ControlNoticeOutcome ToOutcome(ClusterRestartOutcome outcome) => outcome switch
    {
        ClusterRestartOutcome.RestartQueued => ControlNoticeOutcome.Completed,
        ClusterRestartOutcome.HandoffAborted => ControlNoticeOutcome.Aborted,
        ClusterRestartOutcome.Refused or ClusterRestartOutcome.UnknownWorker or ClusterRestartOutcome.HandoffUnavailable => ControlNoticeOutcome.Refused,
        _ => ControlNoticeOutcome.Failed,
    };
}
