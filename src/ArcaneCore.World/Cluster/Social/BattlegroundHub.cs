using ArcaneCore.World.Battlegrounds;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>A queue command waiting at the hub, with the worker whose player issued it.</summary>
public sealed record BattlegroundQueuedCommand(string WorkerId, BattlegroundCommand Command);

/// <summary>A notice waiting at the hub for <paramref name="WorkerId"/>'s player.</summary>
public sealed record BattlegroundQueuedNotice(string WorkerId, BattlegroundNotice Notice);

/// <summary>The answer to a take: whether the caller is (now) the battleground holder, and, if it is, the commands that were waiting.</summary>
public sealed record BattlegroundTakeResult(bool IsHolder, IReadOnlyList<BattlegroundQueuedCommand> Commands);

/// <summary>
/// The battleground queue mailbox as a node reaches it: the hub in process (<see cref="PresenceHolderService"/>) or over the peer link (<see cref="PeerPresenceClient"/>).
/// Members block (a remote call is a round trip): never call on the world thread.
/// </summary>
public interface IBattlegroundHubTransport
{
    /// <summary>True when a call can be made now (the hub is in process or the peer link to it is open). Never blocks; false is the normal state before the link connects.</summary>
    bool IsAuthorityReachable { get; }

    /// <summary>Queue <paramref name="commands"/> of players served by <paramref name="workerId"/> for the battleground holder. Returns false when no holder is live (the commands stay queued, bounded).</summary>
    bool SubmitBattlegroundCommands(string workerId, IReadOnlyList<BattlegroundCommand> commands);

    /// <summary>
    /// A node able to run matches asks for the queued commands. The first asker becomes the holder and stays it while it keeps asking (the hub's holder time to live);
    /// every other asker gets <see cref="BattlegroundTakeResult.IsHolder"/> false until the holder has been silent that long.
    /// </summary>
    BattlegroundTakeResult TakeBattlegroundCommands(string nodeId);

    /// <summary>The holder posts notices for workers' players. Returns false (nothing queued) when <paramref name="nodeId"/> is not the holder.</summary>
    bool PostBattlegroundNotices(string nodeId, IReadOnlyList<BattlegroundQueuedNotice> notices);

    /// <summary>One bounded batch of the notices queued for <paramref name="workerId"/>, oldest first.</summary>
    IReadOnlyList<BattlegroundNotice> DrainBattlegroundNotices(string workerId);
}

/// <summary>
/// The mailbox the node holding the Social lease runs for battleground queues (docs/integration/cluster-realm-state.md, S3e lane 8). The battleground holder is not
/// this node: it is the node that owns the battleground maps (the one that can run a match), found here by election. Workers submit their players' queue commands,
/// the holder takes them and posts its notices, workers drain theirs. Holder election: a node able to run matches calls <see cref="Take"/>; the first one becomes
/// holder and keeps the role while it calls at least once per <see cref="HolderTtl"/>; after that silence the next caller takes over. State is memory only: when the
/// Social lease moves, the new hub starts empty and the holder claims it again with its next take (queued commands and undelivered notices are lost).
/// <para>Thread-safety: one lock guards everything; the lock is never held across a callback. Memory: at most <see cref="MaxQueuedCommands"/> commands and
/// <see cref="MaxQueuedNoticesPerWorker"/> notices per worker (oldest dropped, counted).</para>
/// </summary>
public sealed class BattlegroundHub(TimeProvider? time = null, TimeSpan? holderTtl = null)
{
    /// <summary>Most commands waiting for the holder.</summary>
    public const int MaxQueuedCommands = 8192;

    /// <summary>Most notices waiting for one worker.</summary>
    public const int MaxQueuedNoticesPerWorker = 2048;

    /// <summary>Most items in one reply batch.</summary>
    public const int MaxBatchItems = 256;

    /// <summary>Most encoded bytes in one reply batch (the peer link's payload limit is 64 KiB).</summary>
    public const int MaxBatchBytes = 48 * 1024;

    /// <summary>How long the holder may stay silent before another node can take the role.</summary>
    public static readonly TimeSpan DefaultHolderTtl = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Queue<BattlegroundQueuedCommand> _commands = new();
    private readonly Dictionary<string, Queue<BattlegroundNotice>> _outboxes = new(StringComparer.Ordinal);
    private string? _holder;
    private long _holderSeen;
    private long _droppedCommands;
    private long _droppedNotices;
    private long _oversized;

    /// <summary>The holder time to live in effect.</summary>
    public TimeSpan HolderTtl { get; } = holderTtl ?? DefaultHolderTtl;

    /// <summary>The node id of the current holder (even a silent one until another takes over); null before the first take.</summary>
    public string? Holder
    {
        get
        {
            lock (_gate)
            {
                return _holder;
            }
        }
    }

    /// <summary>Commands dropped because the queue was full.</summary>
    public long DroppedCommands => Interlocked.Read(ref _droppedCommands);

    /// <summary>Notices dropped because a worker's outbox was full.</summary>
    public long DroppedNotices => Interlocked.Read(ref _droppedNotices);

    /// <summary>Items dropped because one of them alone did not fit a batch.</summary>
    public long Oversized => Interlocked.Read(ref _oversized);

    /// <summary>Commands waiting (diagnostics, tests).</summary>
    public int QueuedCommands
    {
        get
        {
            lock (_gate)
            {
                return _commands.Count;
            }
        }
    }

    /// <summary>Queue commands for the holder; true when a holder has asked within the time to live.</summary>
    public bool Submit(string workerId, IReadOnlyList<BattlegroundCommand> commands)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentNullException.ThrowIfNull(commands);
        lock (_gate)
        {
            foreach (BattlegroundCommand command in commands)
            {
                _commands.Enqueue(new BattlegroundQueuedCommand(workerId, command));
                if (_commands.Count > MaxQueuedCommands)
                {
                    _commands.Dequeue();
                    Interlocked.Increment(ref _droppedCommands);
                }
            }

            return HolderIsLive();
        }
    }

    /// <summary>The election and the hand-over of the queued commands (one bounded batch; call again while it is full).</summary>
    public BattlegroundTakeResult Take(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        lock (_gate)
        {
            long now = _time.GetTimestamp();
            bool free = _holder is null || string.Equals(_holder, nodeId, StringComparison.Ordinal) || _time.GetElapsedTime(_holderSeen, now) > HolderTtl;
            if (!free)
            {
                return new BattlegroundTakeResult(false, []);
            }

            _holder = nodeId;
            _holderSeen = now;
            var batch = new List<BattlegroundQueuedCommand>();
            int bytes = 0;
            while (_commands.Count > 0 && batch.Count < MaxBatchItems)
            {
                BattlegroundQueuedCommand next = _commands.Peek();
                int size = BattlegroundWireCodec.EncodedSize(next.WorkerId, next.Command);
                if (size > MaxBatchBytes)
                {
                    _commands.Dequeue();
                    Interlocked.Increment(ref _oversized);
                    continue;
                }

                if (bytes + size > MaxBatchBytes)
                {
                    break;
                }

                bytes += size;
                batch.Add(_commands.Dequeue());
            }

            return new BattlegroundTakeResult(true, batch);
        }
    }

    /// <summary>The holder's notices; false when <paramref name="nodeId"/> is not the holder.</summary>
    public bool Post(string nodeId, IReadOnlyList<BattlegroundQueuedNotice> notices)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(notices);
        lock (_gate)
        {
            if (!string.Equals(_holder, nodeId, StringComparison.Ordinal))
            {
                return false;
            }

            foreach (BattlegroundQueuedNotice item in notices)
            {
                if (!_outboxes.TryGetValue(item.WorkerId, out Queue<BattlegroundNotice>? box))
                {
                    box = new Queue<BattlegroundNotice>();
                    _outboxes[item.WorkerId] = box;
                }

                box.Enqueue(item.Notice);
                if (box.Count > MaxQueuedNoticesPerWorker)
                {
                    box.Dequeue();
                    Interlocked.Increment(ref _droppedNotices);
                }
            }

            return true;
        }
    }

    /// <summary>One bounded batch of the notices queued for <paramref name="workerId"/>.</summary>
    public IReadOnlyList<BattlegroundNotice> Drain(string workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return [];
        }

        lock (_gate)
        {
            if (!_outboxes.TryGetValue(workerId, out Queue<BattlegroundNotice>? box) || box.Count == 0)
            {
                return [];
            }

            var batch = new List<BattlegroundNotice>();
            int bytes = 0;
            while (box.Count > 0 && batch.Count < MaxBatchItems)
            {
                BattlegroundNotice next = box.Peek();
                int size = BattlegroundWireCodec.EncodedSize(workerId, next);
                if (size > MaxBatchBytes)
                {
                    box.Dequeue();
                    Interlocked.Increment(ref _oversized);
                    continue;
                }

                if (bytes + size > MaxBatchBytes)
                {
                    break;
                }

                bytes += size;
                batch.Add(box.Dequeue());
            }

            return batch;
        }
    }

    /// <summary>Under the gate.</summary>
    private bool HolderIsLive() => _holder is not null && _time.GetElapsedTime(_holderSeen, _time.GetTimestamp()) <= HolderTtl;
}
