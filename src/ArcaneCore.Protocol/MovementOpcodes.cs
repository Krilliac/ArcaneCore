namespace ArcaneCore.Protocol;

/// <summary>
/// The client movement opcodes the server applies and relays to nearby players — exactly the
/// set vmangos Opcodes.cpp routes to WorldSession::HandleMovementOpcodes, minus
/// CMSG_MOVE_FALL_RESET, which is applied but never relayed (the client has no handler for it).
/// </summary>
public static class MovementOpcodes
{
    private static readonly HashSet<WorldOpcode> RelayableSet =
    [
        WorldOpcode.MsgMoveStartForward, WorldOpcode.MsgMoveStartBackward, WorldOpcode.MsgMoveStop,
        WorldOpcode.MsgMoveStartStrafeLeft, WorldOpcode.MsgMoveStartStrafeRight, WorldOpcode.MsgMoveStopStrafe,
        WorldOpcode.MsgMoveJump, WorldOpcode.MsgMoveStartTurnLeft, WorldOpcode.MsgMoveStartTurnRight,
        WorldOpcode.MsgMoveStopTurn, WorldOpcode.MsgMoveStartPitchUp, WorldOpcode.MsgMoveStartPitchDown,
        WorldOpcode.MsgMoveStopPitch, WorldOpcode.MsgMoveSetRunMode, WorldOpcode.MsgMoveSetWalkMode,
        WorldOpcode.MsgMoveFallLand, WorldOpcode.MsgMoveStartSwim, WorldOpcode.MsgMoveStopSwim,
        WorldOpcode.MsgMoveSetFacing, WorldOpcode.MsgMoveSetPitch, WorldOpcode.MsgMoveHeartbeat,
    ];

    /// <summary>Opcodes whose movement is relayed to observers.</summary>
    public static IReadOnlyCollection<WorldOpcode> Relayable => RelayableSet;

    public static bool IsRelayable(WorldOpcode opcode) => RelayableSet.Contains(opcode);
}
