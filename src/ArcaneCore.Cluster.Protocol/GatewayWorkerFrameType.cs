namespace ArcaneCore.World.Cluster;

internal enum GatewayWorkerFrameType : byte
{
    HelloRegister = 1,
    HelloAck = 2,
    AttachSessionRequest = 3,
    AttachSessionReply = 4,
    DetachSessionRequest = 5,
    DetachSessionReply = 6,
    ClientSessionPacket = 7,
    ClientSessionPacketAck = 8,
    ServerSessionPacket = 9,
    ServerSessionDetached = 10,

    // Tier 3a passthrough (GatewaySessionLink*.cs): sessions keyed by gateway generation, opened before a character exists.
    SessionOpen = 11,
    SessionOpenReply = 12,
    SessionClientPacket = 13,
    SessionServerPacket = 14,
    SessionClose = 15,
    LinkChallenge = 16,
    LinkProof = 17,
}
