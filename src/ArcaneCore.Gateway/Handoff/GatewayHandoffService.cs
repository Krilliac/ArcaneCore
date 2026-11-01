using System.Collections.Concurrent;
using System.Net;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Cluster.Handoff.Remote;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Gateway.Handoff;

/// <summary>
/// The gateway's session-less control link to the standby process of one slot (<c>Gateway:Workers:N:StandbyNodeId</c>, design _wf/handoff-control-DESIGN.md section 3.3). It connects
/// the same way a worker link does (same manifest and incarnation, so the worker counts both links as one gateway) but opens no session on it, keeps
/// one <see cref="LinkControlChannel"/> per connection, and reconnects with the gateway's link backoff. <see cref="Retarget"/> points it at another process (the role swap after a
/// handoff). Thread safety: <see cref="RunAsync"/> runs on one task and writes the state; readers use volatile fields.
/// </summary>
internal sealed class GatewayStandbyLink
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly GatewayServer _server;
    private readonly string _slotNode;
    private readonly ILogger _logger;
    private volatile IPEndPoint _target;
    private volatile GatewaySessionLinkClient? _client;
    private volatile LinkControlChannel? _channel;
    private readonly Lock _publish = new();
    private TaskCompletionSource _up = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _retarget;

    public GatewayStandbyLink(GatewayServer server, string slotNode, IPEndPoint endpoint, ILogger logger)
    {
        _server = server;
        _slotNode = slotNode;
        _target = endpoint;
        _logger = logger;
    }

    /// <summary>The endpoint the link connects to.</summary>
    public IPEndPoint EndPoint => _target;

    /// <summary>Raised each time the link is connected and published (not for a connection that was retargeted while it was being established). Runs on the link's loop task: handlers must only signal.</summary>
    public event Action? Up;

    /// <summary>The control channel of the connected link, or null while it is down.</summary>
    public LinkControlChannel? Channel => _channel is { IsConnected: true } channel ? channel : null;

    /// <summary>Completes with the channel as soon as the link is connected.</summary>
    public async Task<LinkControlChannel> WaitForChannelAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (Channel is { } channel)
            {
                return channel;
            }

            try
            {
                await Volatile.Read(ref _up).Task.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Poll again.
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Point the link at <paramref name="endpoint"/>; a connected link to another endpoint is closed and the loop reconnects.</summary>
    public void Retarget(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        // Drop the channel synchronously: the run loop clears it only after several thread-pool hops, and until then a verb meant for the
        // process that just became the other role would be sent over the old connection to the wrong process. The run loop publishes a new
        // channel under the same lock only while the target is unchanged, so a connection to the old endpoint cannot be published after this.
        lock (_publish)
        {
            _target = endpoint;
            _channel = null;
        }

        Volatile.Read(ref _retarget)?.TrySetResult();
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        GatewayOptions config = _server.Options;
        LinkControlTimeouts timeouts = _server.ControlTimeouts ?? new LinkControlTimeouts();
        TimeSpan delay = config.LinkRetryDelay > TimeSpan.Zero ? config.LinkRetryDelay : TimeSpan.FromSeconds(1);
        TimeSpan nextDelay = delay;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                IPEndPoint endpoint = _target;
                var client = new GatewaySessionLinkClient(
                    config.ToManifest(_server.Incarnation), endpoint, config.LinkSecret, _server.Loggers.CreateLogger<GatewaySessionLinkClient>(), _server.Warden?.Posture);
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    attempt.CancelAfter(TimeSpan.FromSeconds(10));
                    await client.ConnectAsync(attempt.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    if (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }

                    _logger.LogDebug("Standby link of slot {Slot} to {Endpoint} not established ({Message}); retrying in {Delay}", _slotNode, endpoint, ex.Message, nextDelay);
                    try
                    {
                        await Task.Delay(nextDelay, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    nextDelay = TimeSpan.FromTicks(Math.Min(nextDelay.Ticks * 2, MaxRetryDelay.Ticks));
                    continue;
                }

                nextDelay = delay;
                var retarget = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _retarget, retarget);
                var channel = new LinkControlChannel(client, timeouts, _server.Loggers.CreateLogger<LinkControlChannel>());
                bool published;
                lock (_publish)
                {
                    // Publish only while the target is still the endpoint this connection was made to; Retarget changes the target and clears the channel under the same lock.
                    published = Equals(_target, endpoint);
                    if (published)
                    {
                        _client = client;
                        _channel = channel;
                    }
                }

                if (published)
                {
                    Volatile.Read(ref _up).TrySetResult();
                    _logger.LogInformation("Standby link of slot {Slot} registered with {Endpoint}", _slotNode, endpoint);
                    Up?.Invoke();
                }
                else
                {
                    retarget.TrySetResult(); // retargeted while this attempt was connecting: never published
                }

                try
                {
                    await Task.WhenAny(client.Ended, retarget.Task).WaitAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                finally
                {
                    _channel = null;
                    _client = null;
                    Volatile.Write(ref _up, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    await client.DisposeAsync().ConfigureAwait(false);
                }

                _logger.LogInformation("Standby link of slot {Slot} to {Endpoint} ended", _slotNode, endpoint);
            }
        }
        finally
        {
            _channel = null;
            if (Interlocked.Exchange(ref _client, null) is { } leftover)
            {
                await leftover.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// One handoff slot: a <c>Gateway:Workers:N</c> entry with a standby, the standby's control link, and which process (by its Cluster:Worker:NodeId) is the active one and which the
/// standby. After a handoff the roles swap (<see cref="PromoteStandby"/>): the promoted standby is the slot's link target and the old active is the next standby. Thread-safe.
/// </summary>
internal sealed class GatewayHandoffSlot : IDisposable
{
    private readonly Lock _gate = new();
    private string _activeNode;
    private string _standbyNode;

    public GatewayHandoffSlot(string slotNode, string activeNode, string standbyNode, GatewayStandbyLink standby)
    {
        SlotNode = slotNode;
        _activeNode = activeNode;
        _standbyNode = standbyNode;
        Standby = standby;
    }

    /// <summary>The slot's key: its <c>Gateway:Workers:N:NodeId</c>, the name the gateway's node-keyed hold and retarget methods use.</summary>
    public string SlotNode { get; }

    public GatewayStandbyLink Standby { get; }

    /// <summary>One handoff or recovery at a time per slot. Use <see cref="TryEnter"/> and <see cref="Exit"/>; the semaphore is disposed with the slot.</summary>
    public SemaphoreSlim Busy { get; } = new(1, 1);

    /// <summary>Takes the slot without waiting; false when a handoff or recovery holds it or the slot is disposed.</summary>
    public bool TryEnter()
    {
        try
        {
            return Busy.Wait(0);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Gives the slot back; a no-op once the slot is disposed (a run that outlived the service's stop).</summary>
    public void Exit()
    {
        try
        {
            Busy.Release();
        }
        catch (ObjectDisposedException)
        {
            // The service was disposed while this run finished.
        }
    }

    public void Dispose() => Busy.Dispose();

    public string ActiveNode
    {
        get { lock (_gate) return _activeNode; }
    }

    public string StandbyNode
    {
        get { lock (_gate) return _standbyNode; }
    }

    public bool IsActive(string workerName) => string.Equals(ActiveNode, workerName, StringComparison.Ordinal);

    public bool IsStandby(string workerName) => string.Equals(StandbyNode, workerName, StringComparison.Ordinal);

    /// <summary>Sets the process names from what the workers reported (<c>Describe</c>).</summary>
    public void SetNames(string activeNode, string standbyNode)
    {
        lock (_gate)
        {
            _activeNode = activeNode;
            _standbyNode = standbyNode;
        }
    }

    /// <summary>
    /// The standby at <paramref name="newActive"/> became the active process: swaps the names and points the standby link at <paramref name="oldActive"/> (where the retired process, or its
    /// relaunch as the next standby, listens). Idempotent: false, changing nothing, when the standby link no longer points at <paramref name="newActive"/>.
    /// </summary>
    public bool PromoteStandby(IPEndPoint newActive, IPEndPoint oldActive)
    {
        lock (_gate)
        {
            if (!Equals(Standby.EndPoint, newActive))
            {
                return false;
            }

            (_activeNode, _standbyNode) = (_standbyNode, _activeNode);
            Standby.Retarget(oldActive);
            return true;
        }
    }
}

/// <summary>
/// The gateway's whole-World handoff coordinator (design _wf/handoff-control-DESIGN.md sections 1, 3 and 6, commit C7): it runs the unchanged <see cref="WorldHandoffCoordinator"/> over
/// the control channels of the slot's active worker (the gateway's worker link) and of its standby (a session-less <see cref="GatewayStandbyLink"/>), with the local
/// <see cref="GatewayWorldHandoffRouter"/>, and finishes interrupted handoffs from the journal (<see cref="RecoverRecordAsync"/>, driven by <see cref="GatewayHandoffRecoveryService"/>).
/// <list type="bullet">
/// <item><description>Opt-in: it exists only with <c>Gateway:Handoff:Enabled</c>; the journal is the characters database, read validate-only.</description></item>
/// <item><description>One handoff or recovery per slot at a time; a handoff past its epoch takeover is not cancellable and a host stop waits for it (bounded by the stop token).</description></item>
/// <item><description><c>Resume</c>, <c>ReleaseWriterLock</c> and <c>Retire</c> are best effort when the worker's link is gone (a dead process holds no lock and cannot resume); the journal, not the verb, decides.</description></item>
/// </list>
/// Thread-safe. Disposing stops it like <see cref="StopAsync"/>.
/// </summary>
public sealed class GatewayHandoffService : IHostedService, IAsyncDisposable
{
    private readonly GatewayServer _server;
    private readonly IWorldHandoffStore _store;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly Dictionary<string, GatewayHandoffSlot> _slots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, byte> _running = new();
    private readonly WorldHandoffInFlight _inFlight = new();
    private readonly CancellationTokenSource _runs = new();
    private readonly CancellationTokenSource _links = new();
    private readonly List<Task> _linkLoops = [];
    private int _stopped;
    private int _linkUpWired;

    /// <summary>
    /// Builds the slots from <c>Gateway:Workers:N:StandbyNodeId</c>. Throws <see cref="InvalidOperationException"/> when <c>Gateway:Handoff:Enabled</c> is off for <paramref name="server"/>
    /// (its worker links then carry no control channel).
    /// </summary>
    public GatewayHandoffService(GatewayServer server, IWorldHandoffStore store, ILoggerFactory? loggers = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(store);
        if (server.ControlTimeouts is null)
        {
            throw new InvalidOperationException("the gateway handoff service needs Gateway:Handoff:Enabled");
        }

        _server = server;
        _store = store;
        _loggers = loggers ?? NullLoggerFactory.Instance;
        _logger = _loggers.CreateLogger<GatewayHandoffService>();
        foreach (GatewayWorkerEntry entry in server.Options.EffectiveWorkers())
        {
            if (string.IsNullOrWhiteSpace(entry.StandbyNodeId))
            {
                continue;
            }

            var standby = new GatewayStandbyLink(
                server, entry.NodeId, new IPEndPoint(IPAddress.Parse(entry.StandbyAddress), entry.StandbyPort), _loggers.CreateLogger<GatewayStandbyLink>());
            _slots[entry.NodeId] = new GatewayHandoffSlot(entry.NodeId, entry.NodeId, entry.StandbyNodeId, standby);
        }
    }

    /// <summary>How long a best-effort verb (and a promotion) waits for the link of its worker to come up before it gives up (default 5 s).</summary>
    internal TimeSpan LinkGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The slots that can be handed off (those with a standby configured), by <c>Gateway:Workers:N:NodeId</c>.</summary>
    public IReadOnlyCollection<string> Slots => _slots.Keys;

    /// <summary>
    /// Set by the gateway's transfer service (<c>Gateway:Transfers:Enabled</c>, design section 5): <see cref="HandoffAsync"/> enters it before its pre-flight, so a slot with player transfers running or unresolved is refused, and
    /// from then on its transfers are refused until the handoff ends. Null (the default): no coordination, the handoff behaves as before.
    /// </summary>
    internal ArcaneCore.Gateway.Transfers.IGatewayHandoffGuard? Guard { get; set; }

    /// <summary>The process names (<c>Cluster:Worker:NodeId</c>) of the slot's active and standby processes, or just <paramref name="slotNode"/> for a slot without a standby.</summary>
    internal IReadOnlyList<string> NodeNames(string slotNode)
    {
        if (_slots.TryGetValue(slotNode, out GatewayHandoffSlot? slot))
        {
            return [slotNode, slot.ActiveNode, slot.StandbyNode];
        }

        return [slotNode];
    }

    /// <summary>The handoffs running on this service (the host's stop waits for them).</summary>
    public WorldHandoffInFlight InFlight => _inFlight;

    /// <summary>
    /// Raised, with the slot's key, when the worker link or the standby link of a slot comes up (design section 6: recovery also runs then). Raised on a link's supervisor task between
    /// <see cref="StartAsync"/> and <see cref="StopAsync"/>; handlers must only signal.
    /// </summary>
    internal event Action<string>? LinkUp;

    /// <summary>The slot object of <paramref name="slotNode"/> (its names, standby link and busy gate), or null for an unknown slot.</summary>
    internal GatewayHandoffSlot? SlotOf(string slotNode) => _slots.GetValueOrDefault(slotNode);

    /// <summary>Whether this service is running handoff <paramref name="handoffId"/> right now.</summary>
    internal bool IsRunning(Guid handoffId) => _running.ContainsKey(handoffId);

    /// <summary>Whether <paramref name="record"/> names two processes of one of this gateway's slots (a record that is not ours is left to its owner).</summary>
    internal bool Owns(WorldHandoffRecord record) => FindSlot(record) is not null;

    /// <summary>The control channel of the slot's active worker (its gateway link), or null while that link is down or the slot is unknown.</summary>
    public LinkControlChannel? ActiveChannel(string slotNode) =>
        _slots.ContainsKey(slotNode) ? _server.FindWorker(slotNode)?.ControlChannel : null;

    /// <summary>The control channel of the slot's standby, or null while that link is down or the slot is unknown.</summary>
    public LinkControlChannel? StandbyChannel(string slotNode) => _slots.TryGetValue(slotNode, out GatewayHandoffSlot? slot) ? slot.Standby.Channel : null;

    /// <summary>The slot's standby link object, or null for an unknown slot.</summary>
    internal GatewayStandbyLink? StandbyLink(string slotNode) => _slots.TryGetValue(slotNode, out GatewayHandoffSlot? slot) ? slot.Standby : null;

    /// <summary>The endpoint the slot's standby link points at (the standby process; after a handoff, the old active's), or null for an unknown slot.</summary>
    public IPEndPoint? StandbyEndPoint(string slotNode) => _slots.TryGetValue(slotNode, out GatewayHandoffSlot? slot) ? slot.Standby.EndPoint : null;

    /// <summary>Starts the standby links. The gateway server must have started (its worker links exist). Idempotent per instance only once.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _linkUpWired, 1) == 0)
        {
            _server.WorkerLinkUp += OnWorkerLinkUp;
            foreach (GatewayHandoffSlot slot in _slots.Values)
            {
                string slotNode = slot.SlotNode;
                slot.Standby.Up += () => LinkUp?.Invoke(slotNode);
            }
        }

        foreach (GatewayHandoffSlot slot in _slots.Values)
        {
            GatewayStandbyLink link = slot.Standby;
            _linkLoops.Add(Task.Run(() => link.RunAsync(_links.Token), CancellationToken.None));
        }

        _logger.LogInformation("Gateway handoff service started: {Count} slot(s) with a standby", _slots.Count);
        return Task.CompletedTask;
    }

    private void OnWorkerLinkUp(string nodeId)
    {
        if (_slots.ContainsKey(nodeId))
        {
            LinkUp?.Invoke(nodeId);
        }
    }

    /// <summary>
    /// Cancels the handoffs that have not taken the epoch over (they abort and resume their worker), waits for every running handoff (bounded by <paramref name="cancellationToken"/>;
    /// one past its takeover is finished by <see cref="RecoverRecordAsync"/> on the next start if the wait ends first), then closes the standby links.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        await _runs.CancelAsync().ConfigureAwait(false);
        try
        {
            await _inFlight.WhenIdleAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("The gateway stopped while a world handoff was still finishing; it will be recovered from its journal on the next start");
        }

        await _links.CancelAsync().ConfigureAwait(false);
        _server.WorkerLinkUp -= OnWorkerLinkUp;
        try
        {
            await Task.WhenAll(_linkLoops).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "A standby link loop ended with an error while the gateway handoff service stopped");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _runs.Dispose();
        _links.Dispose();
        foreach (GatewayHandoffSlot slot in _slots.Values)
        {
            slot.Dispose();
        }
    }

    /// <summary>
    /// Runs one whole-World handoff of slot <paramref name="slotNode"/>: the active worker drains into the slot's standby, the gateway's sessions follow, the roles swap.
    /// Pre-flight (<c>Describe</c> of both, which refuses a worker with control off, a standby that is not one, no characters ownership, or more than one admitted gateway)
    /// journals nothing. Throws <see cref="WorldHandoffAbortedException"/> when the handoff aborted (the active worker resumed), <see cref="WorldHandoffUnavailableException"/> when the slot has no
    /// standby or a link is down; any other exception after the takeover leaves it committed for recovery. <paramref name="afterStage"/> is the coordinator's per-stage hook (tests). <paramref name="handoffId"/> is the journal key the caller already told someone (null: a fresh one).
    /// </summary>
    public async Task<WorldHandoffRecord> HandoffAsync(
        string slotNode, Func<WorldHandoffStage, CancellationToken, Task>? afterStage = null, CancellationToken cancellationToken = default, Guid? handoffId = null)
    {
        GatewayHandoffSlot slot = RequireSlot(slotNode);
        if (!slot.TryEnter())
        {
            throw new InvalidOperationException($"slot '{slotNode}' already has a handoff or a recovery running (or the service is disposed)");
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _runs.Token);
            CancellationToken token = linked.Token;
            using IDisposable? guard = Guard is null ? null : await Guard.EnterHandoffAsync(slotNode, token).ConfigureAwait(false);
            await ReconcileSlotAsync(slot, token).ConfigureAwait(false);
            LinkControlChannel active = ActiveChannel(slotNode)
                ?? throw new WorldHandoffUnavailableException($"the worker link of slot '{slotNode}' is down");
            LinkControlChannel standby = slot.Standby.Channel
                ?? throw new WorldHandoffUnavailableException($"the standby link of slot '{slotNode}' is down");
            Guid id = handoffId is { } chosen && chosen != Guid.Empty ? chosen : Guid.NewGuid();
            RemoteWorldHandoffSource described = await RemoteWorldHandoffSource.ConnectAsync(active, id, token).ConfigureAwait(false);
            RemoteWorldHandoffTarget target = await RemoteWorldHandoffTarget.ConnectAsync(standby, id, token).ConfigureAwait(false);
            if (!slot.IsActive(described.WorkerId.Value) || !slot.IsStandby(target.WorkerId.Value))
            {
                throw new InvalidOperationException(
                    $"slot '{slotNode}' expects active '{slot.ActiveNode}' and standby '{slot.StandbyNode}', but the workers are '{described.WorkerId.Value}' and '{target.WorkerId.Value}' " +
                    "(Gateway:Workers:N:NodeId and StandbyNodeId must equal the workers' Cluster:Worker:NodeId)");
            }

            var source = new SlotHandoffSource(this, slot, id, described.WorkerId, described.Ownership);
            var coordinator = new WorldHandoffCoordinator(_store, source, target, new SlotRouter(this, slot, target.WorkerId.Value), _loggers.CreateLogger<WorldHandoffCoordinator>());
            _running[id] = 0;
            try
            {
                return await _inFlight.Track(coordinator.RunAsync(id, afterStage, token)).ConfigureAwait(false);
            }
            finally
            {
                _running.TryRemove(id, out _);
            }
        }
        finally
        {
            slot.Exit();
        }
    }

    /// <summary>
    /// Finishes one unresolved handoff from its journal row (design section 6): before the epoch takeover it aborts and resumes the source and releases the held clients; after it, it
    /// rolls forward (promote the standby, reroute the slot, retire the source). Returns null, doing nothing, when the record belongs to no slot of this gateway, is being run now, or its slot is
    /// busy. Throws what the roll-forward threw (a standby link that is not up yet, for one); the caller retries.
    /// </summary>
    public async Task<WorldHandoffRecord?> RecoverRecordAsync(WorldHandoffRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        GatewayHandoffSlot? slot = FindSlot(record);
        if (slot is null || _running.ContainsKey(record.HandoffId))
        {
            return null;
        }

        if (!slot.TryEnter())
        {
            return null;
        }

        try
        {
            if (_running.ContainsKey(record.HandoffId))
            {
                return null;
            }

            var source = new SlotHandoffSource(this, slot, record.HandoffId, record.SourceWorkerId, record.SourceOwnershipToken);
            var target = new SlotHandoffTarget(this, slot, record);
            var coordinator = new WorldHandoffCoordinator(
                _store, source, target, new SlotRouter(this, slot, record.TargetWorkerId.Value), _loggers.CreateLogger<WorldHandoffCoordinator>());
            _running[record.HandoffId] = 0;
            try
            {
                return await _inFlight.Track(coordinator.RecoverAsync(record.HandoffId, cancellationToken)).ConfigureAwait(false);
            }
            finally
            {
                _running.TryRemove(record.HandoffId, out _);
            }
        }
        finally
        {
            slot.Exit();
        }
    }

    /// <summary>
    /// Learns which process of each slot is the active one and which the standby from <c>Describe</c>, and when the roles are the other way round from the configuration (an earlier handoff swapped
    /// them) points the slot's worker link at the active process. A slot whose links are not both up, or whose workers do not answer, is left as it is.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        foreach (GatewayHandoffSlot slot in _slots.Values)
        {
            if (!slot.TryEnter())
            {
                continue;
            }

            try
            {
                await ReconcileSlotAsync(slot, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                slot.Exit();
            }
        }
    }

    internal GatewayServer Server => _server;

    internal ILogger Logger => _logger;

    private GatewayHandoffSlot RequireSlot(string slotNode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotNode);
        return _slots.TryGetValue(slotNode, out GatewayHandoffSlot? slot)
            ? slot
            : throw new WorldHandoffUnavailableException($"gateway worker '{slotNode}' has no standby configured (Gateway:Workers:N:StandbyNodeId)");
    }

    private GatewayHandoffSlot? FindSlot(WorldHandoffRecord record)
    {
        foreach (GatewayHandoffSlot slot in _slots.Values)
        {
            string a = slot.ActiveNode;
            string b = slot.StandbyNode;
            bool source = string.Equals(record.SourceWorkerId.Value, a, StringComparison.Ordinal) || string.Equals(record.SourceWorkerId.Value, b, StringComparison.Ordinal);
            bool target = string.Equals(record.TargetWorkerId.Value, a, StringComparison.Ordinal) || string.Equals(record.TargetWorkerId.Value, b, StringComparison.Ordinal);
            if (source && target)
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>The channel of the process named <paramref name="workerName"/> in <paramref name="slot"/> right now, or null.</summary>
    internal LinkControlChannel? ChannelOf(GatewayHandoffSlot slot, string workerName)
    {
        if (slot.IsActive(workerName))
        {
            return _server.FindWorker(slot.SlotNode)?.ControlChannel;
        }

        return slot.IsStandby(workerName) ? slot.Standby.Channel : null;
    }

    /// <summary>Waits up to <see cref="LinkGrace"/> for the channel of <paramref name="workerName"/>; null when it did not come up.</summary>
    internal async Task<LinkControlChannel?> WaitForChannelAsync(GatewayHandoffSlot slot, string workerName, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + LinkGrace;
        while (true)
        {
            if (ChannelOf(slot, workerName) is { } channel)
            {
                return channel;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReconcileSlotAsync(GatewayHandoffSlot slot, CancellationToken cancellationToken)
    {
        LinkControlChannel? active = ActiveChannel(slot.SlotNode);
        LinkControlChannel? standby = slot.Standby.Channel;
        if (active is null || standby is null)
        {
            return;
        }

        ControlDescribePayload a;
        ControlDescribePayload b;
        try
        {
            a = await DescribeAsync(active, cancellationToken).ConfigureAwait(false);
            b = await DescribeAsync(standby, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ControlLinkLostException or ControlTimeoutException or ControlRefusedException or InvalidDataException)
        {
            _logger.LogDebug("Slot {Slot}: roles not reconciled ({Message})", slot.SlotNode, ex.Message);
            return;
        }

        // The active process is the one that does not report Standby (design 3.3; Describe's flag is the one WorkerStatus carries). A retired source is not a Standby either, so after a gateway restart
        // that followed a completed handoff, or a crash between MarkRerouted and Retire, both processes can report "not standby": the promoted standby is then the one that never held a source world
        // (a standby has no handoff source, so its HeldSourceToken is always null) while the retired source still names the ownership it held.
        ActiveSide side = ChooseActive(a, b);
        if (side == ActiveSide.Worker)
        {
            slot.SetNames(a.WorkerId, b.WorkerId);
        }
        else if (side == ActiveSide.Standby)
        {
            // The configured active endpoint is no longer the active process: an earlier handoff swapped the roles. Follow the active process.
            GatewayWorkerLink? worker = _server.FindWorker(slot.SlotNode);
            IPEndPoint newActive = slot.Standby.EndPoint;
            IPEndPoint oldActive = worker?.ConnectedEndPoint ?? worker?.EndPoint ?? newActive;
            _logger.LogInformation("Slot {Slot}: {Active} is the active process; moving the worker link from {Old} to {New}", slot.SlotNode, b.WorkerId, oldActive, newActive);
            slot.SetNames(b.WorkerId, a.WorkerId);
            await _server.RetargetWorkerSeamlessAsync(slot.SlotNode, newActive, cancellationToken).ConfigureAwait(false);
            slot.Standby.Retarget(oldActive);
        }
        else
        {
            _logger.LogDebug("Slot {Slot}: {A} and {B} cannot be told apart by their standby flags; the roles are left as they are (the journal decides)", slot.SlotNode, a.WorkerId, b.WorkerId);
        }
    }

    private enum ActiveSide
    {
        Undecided,
        Worker,
        Standby,
    }

    /// <summary>Which side of a slot is the active process: <paramref name="worker"/> answered on the slot's worker link, <paramref name="standby"/> on its standby link.</summary>
    private static ActiveSide ChooseActive(ControlDescribePayload worker, ControlDescribePayload standby)
    {
        if (!worker.IsStandby && standby.IsStandby)
        {
            return ActiveSide.Worker;
        }

        if (worker.IsStandby && !standby.IsStandby)
        {
            return ActiveSide.Standby;
        }

        if (!worker.IsStandby && !standby.IsStandby)
        {
            if (worker.HeldSourceToken is not null && standby.HeldSourceToken is null)
            {
                return ActiveSide.Standby;
            }

            if (worker.HeldSourceToken is null && standby.HeldSourceToken is not null)
            {
                return ActiveSide.Worker;
            }
        }

        return ActiveSide.Undecided;
    }

    private static async Task<ControlDescribePayload> DescribeAsync(LinkControlChannel channel, CancellationToken cancellationToken)
    {
        ControlReply reply = await channel.CallAsync(ControlKind.WorldHandoff, Guid.NewGuid(), ControlVerb.Describe, ControlNoArguments.Instance, cancellationToken).ConfigureAwait(false);
        return reply.Payload as ControlDescribePayload ?? throw new InvalidDataException("the Describe reply carries no describe payload");
    }

    /// <summary>
    /// The draining world as the coordinator sees it: identity from <c>Describe</c> (a live run) or from the journal row (recovery), every verb sent on the channel of that process
    /// <em>at call time</em>, because the slot's worker link moves to the standby at the reroute and the old active process is then reached through the standby link.
    /// </summary>
    private sealed class SlotHandoffSource(
        GatewayHandoffService owner, GatewayHandoffSlot slot, Guid handoffId, WorkerNodeId workerId, CharacterOwnershipToken ownership) : IWorldHandoffSource
    {
        public WorkerNodeId WorkerId => workerId;

        public CharacterOwnershipToken Ownership => ownership;

        public Task StopAdmissionsAsync(CancellationToken cancellationToken) => RequiredAsync(ControlVerb.StopAdmissions, cancellationToken);

        public Task FreezeAsync(CancellationToken cancellationToken) => RequiredAsync(ControlVerb.Freeze, cancellationToken);

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            await RequiredAsync(ControlVerb.Flush, cancellationToken).ConfigureAwait(false);

            // The source's saves are durable: the held clients now survive the loss of its link (a crashed source) and move to the promoted world.
            owner.Server.MarkHandoffFlushed(slot.SlotNode);
        }

        public Task ResumeAsync(CancellationToken cancellationToken) => BestEffortAsync(ControlVerb.Resume, cancellationToken);

        public Task RetireAsync(CancellationToken cancellationToken) => BestEffortAsync(ControlVerb.Retire, cancellationToken);

        public Task ReleaseWriterLockAsync(CancellationToken cancellationToken) => BestEffortAsync(ControlVerb.ReleaseWriterLock, cancellationToken);

        private async Task RequiredAsync(ControlVerb verb, CancellationToken cancellationToken)
        {
            LinkControlChannel channel = owner.ChannelOf(slot, workerId.Value)
                ?? throw new ControlLinkLostException($"the control link to worker {workerId.Value} is down");
            await channel.CallAsync(ControlKind.WorldHandoff, handoffId, verb, ControlNoArguments.Instance, cancellationToken).ConfigureAwait(false);
        }

        // A dead process holds no writer lock, cannot be resumed and need not be retired: the journal, not this verb, is the truth. A worker that answers refusing still throws.
        private async Task BestEffortAsync(ControlVerb verb, CancellationToken cancellationToken)
        {
            LinkControlChannel? channel = await owner.WaitForChannelAsync(slot, workerId.Value, CancellationToken.None).ConfigureAwait(false);
            if (channel is null)
            {
                owner.Logger.LogWarning("Handoff {Handoff}: no control link to worker {Worker}; {Verb} skipped (best effort)", handoffId, workerId.Value, verb);
                return;
            }

            try
            {
                await channel.CallAsync(ControlKind.WorldHandoff, handoffId, verb, ControlNoArguments.Instance, cancellationToken).ConfigureAwait(false);
            }
            catch (ControlLinkLostException ex)
            {
                owner.Logger.LogWarning("Handoff {Handoff}: the link to worker {Worker} ended during {Verb} ({Message}); best effort, continuing", handoffId, workerId.Value, verb, ex.Message);
            }
        }
    }

    /// <summary>The standby of a recovered handoff: identity from the journal row, <c>Promote</c> on the standby's channel (waiting for its link to come up).</summary>
    private sealed class SlotHandoffTarget(GatewayHandoffService owner, GatewayHandoffSlot slot, WorldHandoffRecord record) : IWorldHandoffTarget
    {
        public WorkerNodeId WorkerId => record.TargetWorkerId;

        public string OwnerNode => record.TargetOwnerNode ?? string.Empty;

        public Guid Incarnation => record.TargetOwnershipToken?.Incarnation ?? Guid.Empty;

        public async Task WarmAsync(CancellationToken cancellationToken)
        {
            LinkControlChannel channel = await RequireAsync(cancellationToken).ConfigureAwait(false);
            await channel.CallAsync(ControlKind.WorldHandoff, record.HandoffId, ControlVerb.Warm, new ControlWarmArguments(Incarnation), cancellationToken).ConfigureAwait(false);
        }

        public async Task PromoteAsync(CharacterOwnershipToken takenOver, CancellationToken cancellationToken)
        {
            LinkControlChannel channel = await RequireAsync(cancellationToken).ConfigureAwait(false);
            await channel.CallAsync(ControlKind.WorldHandoff, record.HandoffId, ControlVerb.Promote, new ControlTokenArguments(takenOver), cancellationToken).ConfigureAwait(false);
        }

        private async Task<LinkControlChannel> RequireAsync(CancellationToken cancellationToken) =>
            await owner.WaitForChannelAsync(slot, record.TargetWorkerId.Value, cancellationToken).ConfigureAwait(false)
                ?? throw new ControlLinkLostException($"the control link to worker {record.TargetWorkerId.Value} is not up");
    }

    /// <summary>
    /// <see cref="GatewayWorldHandoffRouter"/> for the slot plus the role swap after the reroute: the slot's worker link moves to the standby's endpoint and the standby link to the old active's.
    /// Idempotent (a roll-forward repeats the reroute): a target that already is the slot's active process needs no move.
    /// </summary>
    private sealed class SlotRouter(GatewayHandoffService owner, GatewayHandoffSlot slot, string targetName) : IWorldHandoffRouter
    {
        public Task HoldAsync(CancellationToken cancellationToken)
        {
            owner.Server.HoldSessionsForHandoff(slot.SlotNode);
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(CancellationToken cancellationToken)
        {
            owner.Server.ReleaseHandoffHold(slot.SlotNode);
            return Task.CompletedTask;
        }

        public async Task RerouteAsync(CancellationToken cancellationToken)
        {
            if (!slot.IsStandby(targetName))
            {
                return; // the target already is the slot's active process: rerouted before
            }

            IPEndPoint newActive = slot.Standby.EndPoint;
            GatewayWorkerLink worker = owner.Server.FindWorker(slot.SlotNode)
                ?? throw new InvalidOperationException($"gateway worker '{slot.SlotNode}' is not configured");
            IPEndPoint oldActive = worker.ConnectedEndPoint ?? worker.EndPoint;
            await new GatewayWorldHandoffRouter(owner.Server, slot.SlotNode, newActive).RerouteAsync(cancellationToken).ConfigureAwait(false);
            slot.PromoteStandby(newActive, oldActive);
        }
    }
}
