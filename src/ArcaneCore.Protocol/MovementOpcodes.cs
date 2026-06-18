namespace ArcaneCore.Protocol;

/// <summary>Classifies the client→server movement opcodes that the server relays to nearby players.</summary>
public static class MovementOpcodes
{
    private static readonly HashSet<WorldOpcode> Relayable =
    [
        WorldOpcode.MsgMoveStartForward, WorldOpcode.MsgMoveStartBackward, WorldOpcode.MsgMoveStop,
        WorldOpcode.MsgMoveStartStrafeLeft, WorldOpcode.MsgMoveStartStrafeRight, WorldOpcode.MsgMoveStopStrafe,
        WorldOpcode.MsgMoveJump, WorldOpcode.MsgMoveStartTurnLeft, WorldOpcode.MsgMoveStartTurnRight,
        WorldOpcode.MsgMoveStopTurn, WorldOpcode.MsgMoveStartPitchUp, WorldOpcode.MsgMoveStartPitchDown,
        WorldOpcode.MsgMoveStopPitch, WorldOpcode.MsgMoveSetRunMode, WorldOpcode.MsgMoveSetWalkMode,
        WorldOpcode.MsgMoveFallLand, WorldOpcode.MsgMoveStartSwim, WorldOpcode.MsgMoveStopSwim,
        WorldOpcode.MsgMoveSetFacing, WorldOpcode.MsgMoveSetPitch, WorldOpcode.MsgMoveHeartbeat,
    ];

    public static bool IsRelayable(WorldOpcode opcode) => Relayable.Contains(opcode);
}
