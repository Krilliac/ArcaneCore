namespace ArcaneCore.World.Cluster.Peer;

/// <summary>
/// Frame types of the worker-to-worker peer link (the link a node opens to the holder of the Social/Economy realm authority lease). The type is the first byte of
/// every frame. The numeric values are wire format, pinned by <c>RealmServiceFrameTypeTests</c>: never renumber or reuse one, only append, and raise
/// <see cref="RealmServiceProtocol.Version"/> in the same change. A frame type this build does not know closes the link.
/// </summary>
internal enum RealmServiceFrameType : byte
{
    /// <summary>Holder to client, first frame of a link: a random nonce the client must answer with <see cref="Proof"/>.</summary>
    Challenge = 1,

    /// <summary>
    /// Client to holder: HMAC-SHA256 of the nonce keyed by the shared peer secret, then the client's own 32-byte nonce (64 bytes). Holder to client (once,
    /// right after it accepted the client's proof): the holder's own proof, 32 bytes, bound to both nonces. Every frame after that proof carries a MAC tag.
    /// </summary>
    Proof = 2,

    /// <summary>Client to holder: protocol version, the client's node manifest, and the lease tokens the client holds.</summary>
    Hello = 3,

    /// <summary>Holder to client: admitted or refused (with a reason), and when admitted the holder's manifest and held lease tokens.</summary>
    HelloAck = 4,

    /// <summary>Client to holder: a correlated echo request (request id, idempotency id, payload).</summary>
    Ping = 5,

    /// <summary>Holder to client: the answer to one <see cref="Ping"/>, carrying its request id and echoing its payload.</summary>
    Pong = 6,
}

/// <summary>The version of the peer link frame set; raised whenever <see cref="RealmServiceFrameType"/> or a frame layout changes.</summary>
internal static class RealmServiceProtocol
{
    /// <summary>
    /// Current peer link protocol version, carried in Hello and HelloAck; a peer with another version is refused.
    /// 2: mutual proof and a MAC tag on every frame after the handshake proofs; a version 1 peer is refused. 1 was the one-way proof without frame MACs.
    /// </summary>
    public const ushort Version = 2;
}
