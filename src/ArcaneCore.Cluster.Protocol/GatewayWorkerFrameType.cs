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

    // Warden on the gateway: a verdict the gateway reached about one of its sessions (gateway to worker only).
    WardenVerdict = 18,

    // The gateway's Warden posture, sent once after admission and before any SessionOpen (gateway to worker only).
    WardenPosture = 19,

    // The gateway's monotonic clock reading, sent once right after admission (gateway to worker only): the worker derives the clock offset
    // it uses to translate the receive time stamped on every SessionClientPacket (docs/areas/anticheat.md).
    LinkClock = 20,

    // Login redirect (worker to gateway): the login worker found the character's saved map on another worker and asks the gateway to
    // move the session there. Sent before any world state exists; the gateway answers with SessionAdopt on the owning worker's link.
    SessionRedirect = 21,

    // Login redirect (gateway to worker): opens a session on the map owner with the generation, the authenticated account and the
    // buffered CMSG_PLAYER_LOGIN. Answered with SessionOpenReply, like SessionOpen.
    SessionAdopt = 22,

    // Realm liveness (worker to gateway): the worker's tick counter, session count and lifecycle state, sent on admission and then every few seconds,
    // so the gateway can write the realm-list row of a clustered realm (docs/ops/realm-liveness.md section 7).
    WorkerStatus = 23,

    // Cross-worker player transfer (S5c), control frames on the existing link (GatewayTransferWire). The source worker's coordinator sends
    // TransferStage / TransferCancel / TransferCommit to the gateway over its own link; the gateway answers each with a TransferAck.
    TransferStage = 24,
    TransferCancel = 25,
    TransferCommit = 26,
    TransferAck = 27,

    // Unified control channel (contract 5, design _wf/handoff-control-DESIGN.md section 2, ClusterControlWire): the gateway coordinates both the
    // whole-World handoff and per-player transfers by sending ControlRequest to a worker, which answers ControlReply on the same link. A worker
    // asks the gateway to start an operation with ControlSubmit (answered by ControlSubmitReply); the gateway tells a worker about an outcome with
    // ControlNotice. Frames 24-27 stay as they are for the worker-hosted mode.
    ControlRequest = 28,
    ControlReply = 29,
    ControlSubmit = 30,
    ControlSubmitReply = 31,
    ControlNotice = 32,
}
