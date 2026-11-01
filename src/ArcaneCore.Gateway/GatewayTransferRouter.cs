using System.Buffers.Binary;
using System.Collections.Concurrent;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Transfer;

namespace ArcaneCore.Gateway;

/// <summary>What the router decided about one client packet (<see cref="GatewayTransferRouter.Intercept"/>).</summary>
public enum GatewayTransferVerdict
{
    /// <summary>No transfer concerns the packet: forward it as usual.</summary>
    Pass,

    /// <summary>A transfer is staged for the session: the router kept the packet and replays it after commit or cancel.</summary>
    Held,

    /// <summary>The packet is stamped with a generation the session left: discard it silently.</summary>
    Drop,

    /// <summary>The freeze buffer overflowed: the router released the staged state and kicked the client; the caller ends the connection.</summary>
    Overflow,
}

/// <summary>A worker link as the transfer router needs it: the target side of a staged transfer.</summary>
public interface IGatewayTransferLink
{
    /// <summary>Forwards one client packet on the session <paramref name="generation"/> of this link. False when the session is not open or the link is down.</summary>
    bool SendClientPacket(long generation, WorldOpcode opcode, ReadOnlySpan<byte> payload, uint receivedMs);

    /// <summary>Closes the session <paramref name="generation"/> on this link (the worker is told; no sink is called).</summary>
    void CloseSession(long generation, ClusterSessionDetachReason reason);
}

/// <summary>The session the router opened on the target worker while the transfer is staged.</summary>
/// <param name="Link">The target worker's link.</param>
/// <param name="Generation">The session generation on <paramref name="Link"/> (allocated by the gateway; the client's old generation stays valid on the source until commit).</param>
/// <param name="OwnerEpoch">The owner epoch the target worker stamped at open.</param>
public sealed record GatewayTransferTarget(IGatewayTransferLink Link, long Generation, long OwnerEpoch);

/// <summary>One client connection as the transfer router needs it.</summary>
public interface IGatewayTransferSession
{
    /// <summary>The generation of the session on its current link.</summary>
    long SessionGeneration { get; }

    /// <summary>False once the client is gone.</summary>
    bool IsOpen { get; }

    /// <summary>Encrypts and queues a gateway-synthesized server packet to the client.</summary>
    void SendToClient(WorldOpcode opcode, ReadOnlySpan<byte> payload);

    /// <summary>Forwards one client packet on the session's current link and generation. False when the link is gone.</summary>
    bool ForwardToWorker(WorldOpcode opcode, ReadOnlySpan<byte> payload, uint receivedMs);

    /// <summary>
    /// Moves the connection to <paramref name="target"/> (link and generation) and closes the session it had on the old link. Called under the router's
    /// gate, so no packet is forwarded between the replay and the switch.
    /// </summary>
    void Repoint(GatewayTransferTarget target);

    /// <summary>Disconnects the client.</summary>
    void Kick(string reason);
}

/// <summary>What the router needs from the gateway process: where sessions and the target worker are.</summary>
public interface IGatewayTransferEnvironment
{
    /// <summary>The open session whose current generation is <paramref name="sessionGeneration"/>, or null.</summary>
    IGatewayTransferSession? FindSession(long sessionGeneration);

    /// <summary>
    /// Opens a session for <paramref name="session"/>'s client on the worker <paramref name="targetNode"/> (a character-select session, as a fresh login opens).
    /// Server packets of the new session reach the client from the moment it opens. Null when the worker has no ready link or refused the open.
    /// </summary>
    Task<GatewayTransferTarget?> OpenTargetSessionAsync(IGatewayTransferSession session, string targetNode, CancellationToken cancellationToken);
}

/// <summary>
/// The gateway side of a cross-worker player transfer (S5c): <see cref="StageWorldPortAsync"/> buffers the session's client packets (bounded), opens the
/// session on the target and sends a synthesized SMSG_TRANSFER_PENDING; <see cref="CancelStagedWorldPortAsync"/> resumes the source and replays the buffer
/// to it; <see cref="CommitRouteSwitchAsync"/> replays the buffer to the target in order, re-points the connection and retires the old generation, after
/// which <see cref="Intercept"/> drops packets still stamped with it and <see cref="IsCurrent"/> refuses its server packets. Every step is idempotent per transfer id.
/// <para>
/// A buffer past <see cref="MaxBufferedPackets"/> packets or <see cref="MaxBufferedBytes"/> bytes aborts cleanly: the buffer is freed, the target session is
/// closed and the client is kicked (the same remedy as the login redirect's held-packet bound); a later cancel or commit of that transfer is then a no-op.
/// </para>
/// Thread safety: one gate guards all state; <see cref="Intercept"/> runs on client read loops and is lock-free while no transfer is staged; the control
/// methods run on link receive loops. Nothing under the gate blocks (link sends only enqueue). The ownership token is recorded per session for diagnostics
/// (<see cref="OwnershipOf"/>); packet validity is decided by generation and link owner epoch.
/// </summary>
public sealed class GatewayTransferRouter : ITransferGatewayRouter
{
    /// <summary>Default bound on client packets held during one freeze.</summary>
    public const int DefaultMaxBufferedPackets = 256;

    /// <summary>Default bound on payload bytes held during one freeze.</summary>
    public const int DefaultMaxBufferedBytes = 256 * 1024;

    private const int RetainedRetired = 4096;
    private const int RetainedFinished = 1024;

    private sealed record Held(WorldOpcode Opcode, byte[] Payload, uint ReceivedMs);

    private sealed class Staged(Guid transferId, IGatewayTransferSession session, string sourceNode, string targetNode)
    {
        public Guid TransferId { get; } = transferId;
        public IGatewayTransferSession Session { get; } = session;
        public string SourceNode { get; } = sourceNode;
        public string TargetNode { get; } = targetNode;
        public GatewayTransferTarget? Target { get; set; }
        public List<Held> Buffer { get; } = [];
        public int BufferedBytes { get; set; }
        public bool Overflowed { get; set; }
        public bool TargetLost { get; set; }
    }

    private sealed record Routed(long Epoch, CharacterOwnershipToken Token);

    private readonly IGatewayTransferEnvironment _environment;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, Staged> _staged = [];
    private readonly Dictionary<long, Routed> _routed = [];
    private readonly ConcurrentDictionary<long, byte> _retired = new();
    private readonly Queue<long> _retiredOrder = new();
    private readonly HashSet<Guid> _finished = [];
    private readonly Queue<Guid> _finishedOrder = new();
    private int _stagedCount;

    public GatewayTransferRouter(
        IGatewayTransferEnvironment environment,
        int maxBufferedPackets = DefaultMaxBufferedPackets,
        int maxBufferedBytes = DefaultMaxBufferedBytes)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBufferedPackets, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBufferedBytes, 1);
        _environment = environment;
        MaxBufferedPackets = maxBufferedPackets;
        MaxBufferedBytes = maxBufferedBytes;
    }

    /// <summary>Most client packets held during one freeze.</summary>
    public int MaxBufferedPackets { get; }

    /// <summary>Most payload bytes held during one freeze.</summary>
    public int MaxBufferedBytes { get; }

    /// <summary>Sessions with a staged transfer right now.</summary>
    public int StagedCount => Volatile.Read(ref _stagedCount);

    /// <summary>The ownership token of the last committed transfer of the session whose current generation is <paramref name="generation"/>, or null.</summary>
    public CharacterOwnershipToken? OwnershipOf(long generation)
    {
        lock (_gate)
        {
            return _routed.TryGetValue(generation, out Routed? routed) ? routed.Token : null;
        }
    }

    /// <summary>
    /// Decides what happens to a client packet read for the session stamped <paramref name="stampedGeneration"/>: held while a transfer is staged for it,
    /// dropped when the stamp is a generation the session left, otherwise passed. <paramref name="payload"/> is kept by reference when held.
    /// </summary>
    public GatewayTransferVerdict Intercept(long stampedGeneration, WorldOpcode opcode, byte[] payload, uint receivedMs)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (Volatile.Read(ref _stagedCount) == 0)
        {
            return !_retired.IsEmpty && _retired.ContainsKey(stampedGeneration) ? GatewayTransferVerdict.Drop : GatewayTransferVerdict.Pass;
        }

        IGatewayTransferSession? toKick = null;
        lock (_gate)
        {
            if (!_staged.TryGetValue(stampedGeneration, out Staged? staged))
            {
                return _retired.ContainsKey(stampedGeneration) ? GatewayTransferVerdict.Drop : GatewayTransferVerdict.Pass;
            }

            if (staged.Overflowed)
            {
                return GatewayTransferVerdict.Drop;
            }

            if (staged.Buffer.Count >= MaxBufferedPackets || staged.BufferedBytes + payload.Length > MaxBufferedBytes)
            {
                Overflow(staged);
                toKick = staged.Session;
            }
            else
            {
                staged.Buffer.Add(new Held(opcode, payload, receivedMs));
                staged.BufferedBytes += payload.Length;
                return GatewayTransferVerdict.Held;
            }
        }

        toKick?.Kick("transfer input buffer overflow");
        return GatewayTransferVerdict.Overflow;
    }

    /// <summary>
    /// Whether a server packet of the session <paramref name="generation"/> stamped with the link owner epoch <paramref name="ownerEpoch"/> is current:
    /// false for a generation retired by a commit, and for an epoch that differs from the one the session was committed under.
    /// </summary>
    public bool IsCurrent(long generation, long ownerEpoch)
    {
        if (!_retired.IsEmpty && _retired.ContainsKey(generation))
        {
            return false;
        }

        lock (_gate)
        {
            return !_routed.TryGetValue(generation, out Routed? routed) || routed.Epoch == ownerEpoch;
        }
    }

    public async Task StageWorldPortAsync(
        Guid transferId,
        ulong characterGuid,
        long sessionGeneration,
        WorkerNodeId sourceWorkerId,
        WorkerNodeId targetWorkerId,
        uint targetMapId,
        uint targetInstanceId,
        TransferPosition targetPosition,
        CancellationToken cancellationToken = default)
    {
        IGatewayTransferSession session = _environment.FindSession(sessionGeneration)
            ?? throw new InvalidOperationException($"no open gateway session with generation {sessionGeneration} (character {characterGuid})");
        var staged = new Staged(transferId, session, sourceWorkerId.Value, targetWorkerId.Value);
        lock (_gate)
        {
            if (_staged.TryGetValue(sessionGeneration, out Staged? existing))
            {
                if (existing.TransferId == transferId)
                {
                    return; // a repeat of the stage that is in place
                }

                throw new InvalidOperationException($"session {sessionGeneration} already has transfer {existing.TransferId} staged");
            }

            if (_finished.Contains(transferId))
            {
                throw new InvalidOperationException($"transfer {transferId} already ended");
            }

            _staged[sessionGeneration] = staged;
            Interlocked.Increment(ref _stagedCount); // from here the client's packets are buffered
        }

        GatewayTransferTarget? target;
        try
        {
            target = await _environment.OpenTargetSessionAsync(session, targetWorkerId.Value, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Resume(sessionGeneration, staged);
            throw;
        }

        if (target is null)
        {
            Resume(sessionGeneration, staged);
            throw new InvalidOperationException($"worker '{targetWorkerId}' has no ready link or refused the session");
        }

        bool overflowed;
        bool lost;
        lock (_gate)
        {
            if (!_staged.TryGetValue(sessionGeneration, out Staged? current) || !ReferenceEquals(current, staged))
            {
                // A cancel ended this stage while the target open was pending: close the session that just opened and report the stage as gone.
                target.Link.CloseSession(target.Generation, ClusterSessionDetachReason.Transferred);
                throw new InvalidOperationException($"transfer {transferId} was cancelled while the target session was opening");
            }

            lost = staged.TargetLost;
            overflowed = staged.Overflowed;
            staged.Target = lost ? null : target;
            if (overflowed)
            {
                target.Link.CloseSession(target.Generation, ClusterSessionDetachReason.Transferred);
            }
        }

        if (lost)
        {
            Resume(sessionGeneration, staged);
            throw new InvalidOperationException("the target worker closed the session while the transfer was staging");
        }

        if (overflowed)
        {
            throw new InvalidOperationException("the client sent too much input while the transfer was staging; it was disconnected");
        }

        // SMSG_TRANSFER_PENDING (u32 map) as vmangos Player::ExecuteTeleportFar sends it before SMSG_NEW_WORLD (Player.cpp:2068-2118): the client shows its loading screen.
        byte[] pending = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(pending, targetMapId);
        session.SendToClient(WorldOpcode.SmsgTransferPending, pending);
    }

    public Task CancelStagedWorldPortAsync(
        Guid transferId,
        ulong characterGuid,
        long sessionGeneration,
        WorkerNodeId restoredWorkerId,
        string abortReason,
        CancellationToken cancellationToken = default)
    {
        Staged? staged;
        lock (_gate)
        {
            if (!_staged.TryGetValue(sessionGeneration, out staged) || staged.TransferId != transferId)
            {
                Finish(transferId);
                return Task.CompletedTask; // never staged here, or already ended
            }
        }

        Resume(sessionGeneration, staged);
        return Task.CompletedTask;
    }

    public Task CommitRouteSwitchAsync(
        Guid transferId,
        ulong characterGuid,
        long sessionGeneration,
        WorkerNodeId targetWorkerId,
        CharacterOwnershipToken targetOwnershipToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetOwnershipToken);
        IGatewayTransferSession? toKick = null;
        bool lostTarget = false;
        lock (_gate)
        {
            if (_finished.Contains(transferId))
            {
                return Task.CompletedTask; // a repeated commit (roll-forward retry)
            }

            if (!_staged.TryGetValue(sessionGeneration, out Staged? staged) || staged.TransferId != transferId)
            {
                throw new InvalidOperationException($"transfer {transferId} is not staged for session {sessionGeneration}");
            }

            if (!string.Equals(staged.TargetNode, targetWorkerId.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"transfer {transferId} was staged for worker '{staged.TargetNode}', not '{targetWorkerId}'");
            }

            RemoveStaged(sessionGeneration);
            Finish(transferId);
            GatewayTransferTarget? target = staged.Target;
            if (staged.Overflowed)
            {
                return Task.CompletedTask; // the client was kicked and the target session closed already
            }

            if (staged.TargetLost || target is null)
            {
                // The target session is gone and the commit cannot be honoured.
                toKick = staged.Session;
                lostTarget = true;
                staged.Buffer.Clear();
                staged.BufferedBytes = 0;
            }
            else
            {
                if (!staged.Session.IsOpen)
                {
                    target.Link.CloseSession(target.Generation, ClusterSessionDetachReason.ClientDisconnect);
                    return Task.CompletedTask;
                }

                bool delivered = true;
                foreach (Held held in staged.Buffer)
                {
                    if (!target.Link.SendClientPacket(target.Generation, held.Opcode, held.Payload, held.ReceivedMs))
                    {
                        delivered = false;
                        break;
                    }
                }

                staged.Buffer.Clear();
                staged.BufferedBytes = 0;
                staged.Session.Repoint(target);
                Retire(sessionGeneration);
                _routed[target.Generation] = new Routed(target.OwnerEpoch, targetOwnershipToken);
                if (!delivered)
                {
                    toKick = staged.Session;
                }
            }
        }

        if (lostTarget)
        {
            toKick!.Kick("the target worker closed the session before the commit");
            return Task.CompletedTask;
        }

        toKick?.Kick("the worker link is gone");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies one control frame and reports the result through <paramref name="sendAck"/> (the link's ack writer).
    /// A failure is reported as a refusal with the exception message; the coordinator on the source worker decides what to do with it.
    /// </summary>
    public async Task HandleControlAsync(GatewayTransferControl control, Func<GatewayTransferAck, bool> sendAck, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(sendAck);
        GatewayTransferAck ack;
        try
        {
            switch (control)
            {
                case GatewayTransferStage stage:
                    await StageWorldPortAsync(stage.TransferId, stage.CharacterGuid, stage.SessionGeneration, new WorkerNodeId(stage.SourceNode),
                        new WorkerNodeId(stage.TargetNode), stage.TargetMapId, stage.TargetInstanceId, stage.TargetPosition, cancellationToken).ConfigureAwait(false);
                    break;
                case GatewayTransferCancel cancel:
                    await CancelStagedWorldPortAsync(cancel.TransferId, cancel.CharacterGuid, cancel.SessionGeneration, new WorkerNodeId(cancel.RestoredNode),
                        cancel.Reason, cancellationToken).ConfigureAwait(false);
                    break;
                case GatewayTransferCommit commit:
                    await CommitRouteSwitchAsync(commit.TransferId, commit.CharacterGuid, commit.SessionGeneration, new WorkerNodeId(commit.TargetNode),
                        new CharacterOwnershipToken(commit.TokenScope, commit.TokenEpoch, commit.TokenIncarnation), cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException("unknown transfer control frame");
            }

            ack = new GatewayTransferAck(control.TransferId, control.Phase, true, string.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ack = new GatewayTransferAck(control.TransferId, control.Phase, false, ex.Message);
        }

        sendAck(ack);
    }

    /// <summary>
    /// The target worker closed (or lost) the session <paramref name="targetGeneration"/> it opened for <paramref name="session"/>. Before the commit the source
    /// still owns the character: the staged target is marked gone and the coordinator's cancel resumes the source, so the client stays connected. After the
    /// commit, when that generation is the connection's current one, the client is kicked. Any other close (the router's own, a retired generation) is ignored.
    /// </summary>
    public void OnTargetSessionClosed(IGatewayTransferSession session, long targetGeneration, ClusterSessionDetachReason reason)
    {
        ArgumentNullException.ThrowIfNull(session);
        bool kick = false;
        lock (_gate)
        {
            foreach (Staged staged in _staged.Values)
            {
                if (ReferenceEquals(staged.Session, session) && (staged.Target is null || staged.Target.Generation == targetGeneration))
                {
                    staged.TargetLost = true;
                    staged.Target = null;
                    return;
                }
            }

            kick = _routed.ContainsKey(targetGeneration) && session.SessionGeneration == targetGeneration;
        }

        if (kick)
        {
            session.Kick("the target worker closed the session (" + reason + ")");
        }
    }

    /// <summary>Ends a staged transfer without a commit: closes the target session, replays the buffer to the source in order and resumes plain forwarding.</summary>
    private void Resume(long sessionGeneration, Staged staged)
    {
        bool delivered = true;
        lock (_gate)
        {
            if (!_staged.TryGetValue(sessionGeneration, out Staged? current) || !ReferenceEquals(current, staged))
            {
                return;
            }

            RemoveStaged(sessionGeneration);
            Finish(staged.TransferId);
            if (staged.Target is { } target)
            {
                target.Link.CloseSession(target.Generation, ClusterSessionDetachReason.Transferred);
            }

            if (!staged.Overflowed && staged.Session.IsOpen)
            {
                foreach (Held held in staged.Buffer)
                {
                    if (!staged.Session.ForwardToWorker(held.Opcode, held.Payload, held.ReceivedMs))
                    {
                        delivered = false;
                        break;
                    }
                }
            }

            staged.Buffer.Clear();
            staged.BufferedBytes = 0;
        }

        if (!delivered)
        {
            staged.Session.Kick("the worker link is gone");
        }
    }

    /// <summary>Frees the buffer and the target session of a staged transfer that overflowed. Caller holds the gate.</summary>
    private static void Overflow(Staged staged)
    {
        staged.Overflowed = true;
        staged.Buffer.Clear();
        staged.BufferedBytes = 0;
        if (staged.Target is { } target)
        {
            target.Link.CloseSession(target.Generation, ClusterSessionDetachReason.Transferred);
        }
    }

    private void RemoveStaged(long sessionGeneration)
    {
        if (_staged.Remove(sessionGeneration))
        {
            Interlocked.Decrement(ref _stagedCount);
        }
    }

    private void Retire(long generation)
    {
        _routed.Remove(generation);
        _retired[generation] = 0;
        _retiredOrder.Enqueue(generation);
        while (_retiredOrder.Count > RetainedRetired)
        {
            _retired.TryRemove(_retiredOrder.Dequeue(), out _);
        }
    }

    private void Finish(Guid transferId)
    {
        if (_finished.Add(transferId))
        {
            _finishedOrder.Enqueue(transferId);
            while (_finishedOrder.Count > RetainedFinished)
            {
                _finished.Remove(_finishedOrder.Dequeue());
            }
        }
    }
}

/// <summary>A <see cref="GatewaySessionLinkClient"/> as an <see cref="IGatewayTransferLink"/>.</summary>
internal sealed class GatewayLinkClientAdapter(GatewaySessionLinkClient client) : IGatewayTransferLink
{
    public GatewaySessionLinkClient Client { get; } = client;

    public bool SendClientPacket(long generation, WorldOpcode opcode, ReadOnlySpan<byte> payload, uint receivedMs) =>
        Client.SendClientPacket(generation, opcode, payload, receivedMs);

    public void CloseSession(long generation, ClusterSessionDetachReason reason) => Client.CloseSession(generation, reason);
}

/// <summary>
/// The production <see cref="IGatewayTransferEnvironment"/>: sessions are the server's client connections, the target is the registered link of the worker
/// with the node id the coordinator names (the node ids of <c>Gateway:Workers</c>). The target session's packets go to the client as they arrive, as long
/// as the router still considers the session's generation and the link's owner epoch current.
/// </summary>
internal sealed class GatewayServerTransferEnvironment(GatewayServer server) : IGatewayTransferEnvironment
{
    public IGatewayTransferSession? FindSession(long sessionGeneration) => server.FindConnection(sessionGeneration);

    public async Task<GatewayTransferTarget?> OpenTargetSessionAsync(IGatewayTransferSession session, string targetNode, CancellationToken cancellationToken)
    {
        if (session is not GatewayClientConnection connection || server.FindLinkByNode(targetNode) is not { } link)
        {
            return null;
        }

        var sink = new TargetSink(session, server.Transfers);
        GatewaySessionOpenReply reply = await link.OpenAsync(server.NextGeneration, generation =>
        {
            sink.Generation = generation;
            return connection.CreateOpenRequest(generation);
        }, sink, cancellationToken).ConfigureAwait(false);
        return reply.Accepted ? new GatewayTransferTarget(new GatewayLinkClientAdapter(link), sink.Generation, reply.OwnerEpoch) : null;
    }

    private sealed class TargetSink(IGatewayTransferSession session, GatewayTransferRouter router) : IGatewaySessionSink
    {
        private long _epoch;

        public long Generation { get; set; }

        public void OnOpened(GatewaySessionOpenReply reply) => Volatile.Write(ref _epoch, reply.OwnerEpoch);

        public void OnServerPacket(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
            if (router.IsCurrent(Generation, Volatile.Read(ref _epoch)))
            {
                session.SendToClient(opcode, payload);
            }
        }

        public void OnClosed(ClusterSessionDetachReason reason) => router.OnTargetSessionClosed(session, Generation, reason);
    }
}
