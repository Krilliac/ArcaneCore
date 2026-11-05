using System.Threading.Channels;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Battlegrounds;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>
/// A node's connection to the battleground queue (docs/integration/cluster-realm-state.md, S3e lane 8): implements both links of <see cref="BattlegroundClusterQueue"/>
/// over the <see cref="BattlegroundHub"/> that the Social lease holder runs, reached through <see cref="IBattlegroundHubTransport"/> (in process or the peer link).
/// <list type="bullet">
/// <item>Worker side (every node): <see cref="Submit"/> queues the players' commands for one sender task that submits them to the hub in order; the pump drains the notices
/// the hub holds for this node and hands each to the feature, which applies it on the world thread.</item>
/// <item>Holder side (only a node that can run a match, <c>canHold</c> of <see cref="Bind"/>): the pump takes the queued commands from the hub, which makes this node the
/// holder while it keeps asking, and gives them to the feature's queue (<c>BattlegroundFeature.SubmitClusterCommand</c>), which runs them on the next world tick.
/// <see cref="Deliver"/> queues the holder's notices for a second sender task that posts them to the hub. A node that is not (or no longer) the holder drops notices.</item>
/// </list>
/// Failure handling: a command or notice whose send fails is dropped with a log line (counted in <see cref="SendFailed"/>); players retry, as they do after a lost packet.
/// The exception is Login and Logout: those are kept (bounded by <see cref="MaxRetained"/>) and resent first when the hub is reachable again, and a holder runs its own players' commands
/// without the hub at all, so a lost Logout does not leave a queue slot behind. A worker the hub has not heard from for <see cref="BattlegroundHub.DefaultWorkerTtl"/> is reported to the holder
/// (<see cref="BattlegroundWorkerGoneCommand"/>), which logs out every player it served for that worker.
/// A holder that cannot reach the hub for longer than <see cref="BattlegroundHub.DefaultHolderTtl"/> stops acting as holder (another node can take over). Queues are in memory:
/// a hub move (the Social lease changing owner) or a holder change loses the waiting commands and the queue state of the old holder's manager.
/// <para>Thread-safety: <see cref="Submit"/> and <see cref="Deliver"/> are safe from any thread and never block (bounded channels, a full channel drops and counts). The sender and pump tasks run on the
/// thread pool and block on the transport, never on the world thread. Ownership: owns the tasks; <see cref="DisposeAsync"/> stops them.</para>
/// </summary>
internal sealed class ClusterBattlegroundLink : IBattlegroundHolderLink, IBattlegroundWorkerLink, IAsyncDisposable
{
    /// <summary>Most commands or notices waiting for their sender task.</summary>
    public const int MaxQueued = 4096;

    /// <summary>Most items per send.</summary>
    public const int MaxBatchItems = 128;

    /// <summary>Most encoded bytes per send (the peer link's payload limit is 64 KiB).</summary>
    public const int MaxBatchBytes = 48 * 1024;

    /// <summary>Most Login and Logout commands kept for a resend after a failed send (under the retain gate; the sender task and the pump flush them).</summary>
    public const int MaxRetained = 1024;

    /// <summary>Most batches one <see cref="PumpOnce"/> takes or drains per direction.</summary>
    public const int MaxPumpBatches = 16;

    private readonly IBattlegroundHubTransport _hub;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _holderLoss;
    private readonly Channel<(string WorkerId, BattlegroundCommand Command)> _commands = Channel.CreateBounded<(string, BattlegroundCommand)>(
        new BoundedChannelOptions(MaxQueued) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    private readonly Channel<(string WorkerId, BattlegroundNotice Notice)> _notices = Channel.CreateBounded<(string, BattlegroundNotice)>(
        new BoundedChannelOptions(MaxQueued) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    private readonly Lock _retainGate = new();
    private readonly List<(string WorkerId, BattlegroundCommand Command)> _retained = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _commandSender;
    private readonly Task _noticeSender;
    private Task _pump = Task.CompletedTask;
    private Action<string, BattlegroundCommand>? _submitToQueue;
    private Action<BattlegroundNotice>? _receive;
    private bool _canHold;
    private bool _preferred;
    private int _isHolder;
    private long _lastTakeOk;
    private long _commandsDropped;
    private long _noticesDropped;
    private long _sendFailed;
    private int _outageLogged;
    private int _noHolderLogged;
    private int _started;
    private int _disposed;

    public ClusterBattlegroundLink(IBattlegroundHubTransport hub, string nodeId, ILogger? logger = null, TimeProvider? time = null, TimeSpan? holderLoss = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        _hub = hub;
        NodeId = nodeId;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _holderLoss = holderLoss ?? BattlegroundHub.DefaultHolderTtl;
        _commandSender = Task.Run(RunCommandSenderAsync, CancellationToken.None);
        _noticeSender = Task.Run(RunNoticeSenderAsync, CancellationToken.None);
    }

    /// <summary>This node's worker id as the hub and the holder know it.</summary>
    public string NodeId { get; }

    /// <summary>True while this node is the battleground holder (its last take was granted and the hub has not been out of reach for the holder time to live).</summary>
    public bool IsHolder => Volatile.Read(ref _isHolder) != 0;

    /// <summary>True when this node can run matches, so the pump asks the hub for the holder role.</summary>
    public bool CanHold => _canHold;

    /// <summary>True when this node's map list names the battleground maps, so the gateway sends the transfers here: it wins the holder election over a node that merely owns everything.</summary>
    public bool Preferred => _preferred;

    /// <summary>Commands dropped because the send queue was full or the command has no wire form.</summary>
    public long CommandsDropped => Interlocked.Read(ref _commandsDropped);

    /// <summary>Notices dropped: queue full, this node is not the holder, or the notice has no wire form.</summary>
    public long NoticesDropped => Interlocked.Read(ref _noticesDropped);

    /// <summary>Sends that failed (no link, timeout, refusal); each lost its items.</summary>
    public long SendFailed => Interlocked.Read(ref _sendFailed);

    /// <summary>
    /// Connect the feature: <paramref name="submitToQueue"/> hands a taken command to the holder's queue (any thread), <paramref name="receive"/> hands a drained notice to the
    /// worker side (any thread; it posts to the world thread), <paramref name="canHold"/> is whether this node owns the battleground maps, <paramref name="preferred"/> whether its
    /// map list names them (the gateway's first choice). Call once, before <see cref="Start"/>.
    /// </summary>
    public void Bind(Action<string, BattlegroundCommand> submitToQueue, Action<BattlegroundNotice> receive, bool canHold, bool preferred = false)
    {
        ArgumentNullException.ThrowIfNull(submitToQueue);
        ArgumentNullException.ThrowIfNull(receive);
        if (Volatile.Read(ref _started) != 0)
        {
            throw new InvalidOperationException("the battleground link is already started");
        }

        _submitToQueue = submitToQueue;
        _receive = receive;
        _canHold = canHold;
        _preferred = canHold && preferred;
    }

    public void Submit(string workerId, BattlegroundCommand command)
    {
        ArgumentNullException.ThrowIfNull(workerId);
        ArgumentNullException.ThrowIfNull(command);

        // This node is the holder and the command is its own player's: it goes straight to the holder's queue. A round trip through the hub could drop it while the hub is
        // unreachable (a Logout lost that way would leave the player's slot in the queue for good), and the hub adds nothing for a command that ends up here anyway.
        // Only a window around becoming the holder can reorder: this node's older commands still waiting at the hub arrive with the first take.
        if (IsHolder && Volatile.Read(ref _disposed) == 0 && string.Equals(workerId, NodeId, StringComparison.Ordinal) && _submitToQueue is { } submit)
        {
            submit(workerId, command);
            return;
        }

        if (Volatile.Read(ref _disposed) != 0 || !_commands.Writer.TryWrite((workerId, command)))
        {
            Interlocked.Increment(ref _commandsDropped);
        }
    }

    public void Deliver(string workerId, BattlegroundNotice notice)
    {
        ArgumentNullException.ThrowIfNull(workerId);
        ArgumentNullException.ThrowIfNull(notice);
        if (!IsHolder || Volatile.Read(ref _disposed) != 0 || !_notices.Writer.TryWrite((workerId, notice)))
        {
            Interlocked.Increment(ref _noticesDropped);
        }
    }

    /// <summary>
    /// One pump pass (pump task or a test): when this node can hold, take the queued commands and give them to the feature's queue; then drain this node's notices and hand
    /// them to the feature. Returns how many items were handed over. Blocking: never call on the world thread.
    /// </summary>
    public int PumpOnce()
    {
        Action<string, BattlegroundCommand>? submit = _submitToQueue;
        Action<BattlegroundNotice>? receive = _receive;
        if (submit is null || receive is null || !_hub.IsAuthorityReachable)
        {
            DemoteIfSilent();
            return 0;
        }

        FlushRetained();

        int handed = 0;
        if (_canHold)
        {
            for (int batch = 0; batch < MaxPumpBatches; batch++)
            {
                BattlegroundTakeResult taken;
                try
                {
                    taken = _hub.TakeBattlegroundCommands(NodeId, _preferred);
                }
                catch (Exception ex) when (IsTransportFailure(ex))
                {
                    _logger.LogDebug("Battleground take deferred ({Message})", ex.Message);
                    DemoteIfSilent();
                    break;
                }

                if (!taken.IsHolder)
                {
                    SetHolder(false);
                    break;
                }

                Volatile.Write(ref _lastTakeOk, _time.GetTimestamp());
                SetHolder(true);
                foreach (BattlegroundQueuedCommand item in taken.Commands)
                {
                    submit(item.WorkerId, item.Command);
                    handed++;
                }

                if (taken.Commands.Count < BattlegroundHub.MaxBatchItems)
                {
                    break;
                }
            }
        }

        for (int batch = 0; batch < MaxPumpBatches; batch++)
        {
            IReadOnlyList<BattlegroundNotice> notices;
            try
            {
                notices = _hub.DrainBattlegroundNotices(NodeId);
            }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                _logger.LogDebug("Battleground notice drain deferred ({Message})", ex.Message);
                break;
            }

            foreach (BattlegroundNotice notice in notices)
            {
                receive(notice);
                handed++;
            }

            if (notices.Count < BattlegroundHub.MaxBatchItems)
            {
                break;
            }
        }

        return handed;
    }

    private void SetHolder(bool holder)
    {
        if (Interlocked.Exchange(ref _isHolder, holder ? 1 : 0) == (holder ? 1 : 0))
        {
            return;
        }

        if (holder)
        {
            _logger.LogInformation("Battleground queue: this node ({Node}) is the holder", NodeId);
        }
        else
        {
            _logger.LogWarning("Battleground queue: this node ({Node}) is no longer the holder; queue state here is stale and its notices are dropped", NodeId);
        }
    }

    private void DemoteIfSilent()
    {
        if (IsHolder && _time.GetElapsedTime(Volatile.Read(ref _lastTakeOk), _time.GetTimestamp()) > _holderLoss)
        {
            SetHolder(false);
        }
    }

    private async Task RunCommandSenderAsync()
    {
        ChannelReader<(string WorkerId, BattlegroundCommand Command)> reader = _commands.Reader;
        var batch = new List<BattlegroundCommand>();
        string? batchWorker = null;
        int bytes = 0;
        try
        {
            while (await reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out (string WorkerId, BattlegroundCommand Command) item))
                {
                    int size;
                    try
                    {
                        size = BattlegroundWireCodec.EncodedSize(item.WorkerId, item.Command);
                    }
                    catch (ArgumentException ex)
                    {
                        Interlocked.Increment(ref _commandsDropped);
                        _logger.LogWarning("Battleground command {Command} dropped: {Message}", item.Command.GetType().Name, ex.Message);
                        continue;
                    }

                    if (batchWorker is not null && (batchWorker != item.WorkerId || batch.Count >= MaxBatchItems || bytes + size > MaxBatchBytes))
                    {
                        SendCommands(batchWorker, batch);
                        batch.Clear();
                        bytes = 0;
                        batchWorker = null;
                    }

                    batchWorker = item.WorkerId;
                    batch.Add(item.Command);
                    bytes += size;
                }

                if (batchWorker is not null)
                {
                    SendCommands(batchWorker, batch);
                    batch.Clear();
                    bytes = 0;
                    batchWorker = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A command that must not be lost: the holder learns who is online and who left from these, and a lost Logout leaves a queue slot behind.</summary>
    private static bool IsPresenceCommand(BattlegroundCommand command) => command is BattlegroundLoginCommand or BattlegroundLogoutCommand;

    /// <summary>Keep the presence commands of a batch that could not be sent, in order, for the next send (bounded; the oldest go first when full). Takes the retain gate.</summary>
    private void Retain(string workerId, List<BattlegroundCommand> commands)
    {
        lock (_retainGate)
        {
            foreach (BattlegroundCommand command in commands)
            {
                if (!IsPresenceCommand(command))
                {
                    continue;
                }

                if (_retained.Count >= MaxRetained)
                {
                    _retained.RemoveAt(0);
                }

                _retained.Add((workerId, command));
            }
        }
    }

    /// <summary>Resend the retained presence commands ahead of new ones; false when they could not be sent (they stay retained). Blocking; takes the retain gate; never call on the world thread.</summary>
    private bool FlushRetained()
    {
        lock (_retainGate)
        {
            while (_retained.Count > 0)
            {
                string worker = _retained[0].WorkerId;
                var batch = new List<BattlegroundCommand>();
                foreach ((string WorkerId, BattlegroundCommand Command) item in _retained)
                {
                    if (!string.Equals(item.WorkerId, worker, StringComparison.Ordinal) || batch.Count >= MaxBatchItems)
                    {
                        break;
                    }

                    batch.Add(item.Command);
                }

                try
                {
                    _hub.SubmitBattlegroundCommands(worker, batch);
                }
                catch (Exception ex) when (IsTransportFailure(ex))
                {
                    return false;
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    // The hub refused the batch itself: resending it would fail the same way, so it is dropped.
                    Interlocked.Increment(ref _sendFailed);
                    _logger.LogFault("cluster.battleground-send", ex, "A retained battleground command batch failed at the hub; it is dropped");
                }

                _retained.RemoveRange(0, batch.Count);
            }

            return true;
        }
    }

    /// <summary>Commands waiting for a resend after a failed send (diagnostics, tests).</summary>
    public int RetainedCommands
    {
        get
        {
            lock (_retainGate)
            {
                return _retained.Count;
            }
        }
    }

    private void SendCommands(string workerId, List<BattlegroundCommand> commands)
    {
        if (!_hub.IsAuthorityReachable || !FlushRetained())
        {
            Retain(workerId, commands);
            Lost(commands.Count, "no link to the Social holder's battleground hub");
            return;
        }

        try
        {
            bool holderLive = _hub.SubmitBattlegroundCommands(workerId, commands);
            Volatile.Write(ref _outageLogged, 0);
            if (!holderLive)
            {
                if (Interlocked.Exchange(ref _noHolderLogged, 1) == 0)
                {
                    _logger.LogWarning("Battleground queue commands are waiting: no node that owns the battleground maps is acting as holder (they stay queued at the hub, bounded)");
                }
            }
            else
            {
                Volatile.Write(ref _noHolderLogged, 0);
            }
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            Retain(workerId, commands);
            Lost(commands.Count, ex.Message);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            Interlocked.Increment(ref _sendFailed);
            _logger.LogFault("cluster.battleground-send", ex, "A battleground command batch failed at the hub; it is dropped");
        }
    }

    private async Task RunNoticeSenderAsync()
    {
        ChannelReader<(string WorkerId, BattlegroundNotice Notice)> reader = _notices.Reader;
        var batch = new List<BattlegroundQueuedNotice>();
        int bytes = 0;
        try
        {
            while (await reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out (string WorkerId, BattlegroundNotice Notice) item))
                {
                    int size;
                    try
                    {
                        size = BattlegroundWireCodec.EncodedSize(item.WorkerId, item.Notice);
                    }
                    catch (ArgumentException ex)
                    {
                        Interlocked.Increment(ref _noticesDropped);
                        _logger.LogWarning("Battleground notice {Notice} dropped: {Message}", item.Notice.GetType().Name, ex.Message);
                        continue;
                    }

                    if (batch.Count > 0 && (batch.Count >= MaxBatchItems || bytes + size > MaxBatchBytes))
                    {
                        SendNotices(batch);
                        batch.Clear();
                        bytes = 0;
                    }

                    batch.Add(new BattlegroundQueuedNotice(item.WorkerId, item.Notice));
                    bytes += size;
                }

                if (batch.Count > 0)
                {
                    SendNotices(batch);
                    batch.Clear();
                    bytes = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SendNotices(List<BattlegroundQueuedNotice> notices)
    {
        if (!_hub.IsAuthorityReachable)
        {
            Lost(notices.Count, "no link to the Social holder's battleground hub", notice: true);
            return;
        }

        try
        {
            if (!_hub.PostBattlegroundNotices(NodeId, notices))
            {
                // The hub names another node as holder: this node's manager is stale.
                SetHolder(false);
                Interlocked.Add(ref _noticesDropped, notices.Count);
            }
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            Lost(notices.Count, ex.Message, notice: true);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            Interlocked.Increment(ref _sendFailed);
            _logger.LogFault("cluster.battleground-post", ex, "A battleground notice batch failed at the hub; it is dropped");
        }
    }

    private void Lost(int count, string why, bool notice = false)
    {
        Interlocked.Increment(ref _sendFailed);
        if (notice)
        {
            Interlocked.Add(ref _noticesDropped, count);
        }
        else
        {
            Interlocked.Add(ref _commandsDropped, count);
        }

        if (Interlocked.Exchange(ref _outageLogged, 1) == 0)
        {
            _logger.LogWarning("Battleground queue traffic is being dropped ({Why}); first loss was {Count} {Kind}", why, count, notice ? "notice(s)" : "command(s)");
        }
    }

    /// <summary>Starts the pump: <see cref="PumpOnce"/> every <paramref name="interval"/>. Idempotent; call after <see cref="Bind"/>.</summary>
    public void Start(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        if (_submitToQueue is null)
        {
            throw new InvalidOperationException("the battleground link is not bound to a feature");
        }

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _pump = Task.Run(() => RunPumpAsync(interval, _stop.Token), CancellationToken.None);
    }

    private async Task RunPumpAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    PumpOnce();
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    _logger.LogFault("cluster.battleground-pump", ex, "A battleground pump pass failed; the loop continues");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool IsTransportFailure(Exception ex) => ex is IOException or TimeoutException or InvalidOperationException or ObjectDisposedException;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _commands.Writer.TryComplete();
        _notices.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_commandSender, _noticeSender, _pump).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.battleground-link", ex);
        }

        _stop.Dispose();
    }
}
