using ArcaneCore.Kernel.Cluster;

namespace ArcaneCore.World.Cluster;

/// <summary>
/// Versioned gateway–worker session envelope (<c>docs/CLUSTERING_DESIGN.md</c> §4.2, §10 M15.1).
/// Every proxied packet carries <see cref="SessionGeneration"/>, <see cref="OwnerEpoch"/>, and a monotonic
/// per-session <see cref="Sequence"/> so stale packets after reconnect, transfer, or lease loss are deterministically dropped.
/// </summary>
public readonly record struct ClusterSessionPacket(
    long SessionGeneration,
    int AccountId,
    ulong CharacterGuid,
    long OwnerEpoch,
    long Sequence,
    uint Opcode,
    ReadOnlyMemory<byte> Payload);

/// <summary>Result of attempting to attach a player session to an authoritative worker.</summary>
public readonly record struct ClusterSessionAttachResult(
    bool Accepted,
    long AssignedGeneration,
    long WorkerOwnerEpoch,
    string? RejectionReason);

/// <summary>Reason why a session was detached from a worker.</summary>
public enum ClusterSessionDetachReason : byte
{
    ClientDisconnect = 0,
    Logout = 1,
    SupersededByNewLogin = 2,
    Transferred = 3,
    WorkerDraining = 4,
    OwnerEpochMismatch = 5,

    /// <summary>The worker closed a gateway-backed session (kick, logout to nothing, shutdown): Tier 3a passthrough.</summary>
    WorkerClosed = 6,

    /// <summary>The link carrying the session dropped, or a peer broke the session protocol: Tier 3a passthrough.</summary>
    LinkLost = 7,
}

/// <summary>
/// Transport seam between a session-owning Gateway and an authoritative map Worker (<c>docs/CLUSTERING_DESIGN.md</c> §4).
/// In <see cref="ClusterRole.Combined"/> mode this is backed by <see cref="LocalGatewayWorkerTransport"/> with zero sockets;
/// in split <see cref="ClusterRole.Gateway"/> / <see cref="ClusterRole.Worker"/> mode it runs over framed RPC
/// (<see cref="LoopbackGatewayWorkerClient"/> / <see cref="LoopbackGatewayWorkerHost"/>).
/// </summary>
public interface IGatewayWorkerTransport : IAsyncDisposable
{
    event Action<ClusterSessionPacket>? OutboundPacketReceived;

    event Action<ulong, long, ClusterSessionDetachReason>? SessionDetached;

    ValueTask<ClusterSessionAttachResult> AttachSessionAsync(
        int accountId,
        ulong characterGuid,
        long requestedGeneration,
        long expectedOwnerEpoch,
        CancellationToken cancellationToken = default);

    ValueTask<bool> DetachSessionAsync(
        ulong characterGuid,
        long sessionGeneration,
        ClusterSessionDetachReason reason,
        CancellationToken cancellationToken = default);

    ValueTask<bool> SendToWorkerAsync(
        ClusterSessionPacket packet,
        CancellationToken cancellationToken = default);
}
