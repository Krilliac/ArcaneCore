using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Net.SessionTrace;

/// <summary>
/// The outbound packets a trace records (a fixed set of the ones that change what the client may do or see of its own state) and the
/// decoding of a few of them. Every layout decoded here is the one the repo's own packet builder writes, cited at its case; a packet
/// without a builder cited here is logged by name and size only. Pure functions of the opcode and payload: any thread.
/// </summary>
internal static class SessionTraceOutbound
{
    private static readonly bool[] Curated = BuildCuratedTable();

    private static bool[] BuildCuratedTable()
    {
        // The status packet (SMSG_QUESTGIVER_STATUS) is left out on purpose: it goes out for every NPC in view and would drown the timeline.
        WorldOpcode[] set =
        [
            WorldOpcode.SmsgForceWalkSpeedChange, WorldOpcode.SmsgForceRunSpeedChange, WorldOpcode.SmsgForceRunBackSpeedChange,
            WorldOpcode.SmsgForceSwimSpeedChange, WorldOpcode.SmsgForceSwimBackSpeedChange,
            WorldOpcode.SmsgSplineSetWalkSpeed, WorldOpcode.SmsgSplineSetRunSpeed, WorldOpcode.SmsgSplineSetRunBackSpeed,
            WorldOpcode.SmsgSplineSetSwimSpeed, WorldOpcode.SmsgSplineSetSwimBackSpeed,
            WorldOpcode.SmsgForceMoveRoot, WorldOpcode.SmsgForceMoveUnroot, WorldOpcode.SmsgMoveKnockBack,
            WorldOpcode.SmsgMoveWaterWalk, WorldOpcode.SmsgMoveLandWalk, WorldOpcode.SmsgMoveFeatherFall, WorldOpcode.SmsgMoveNormalFall,
            WorldOpcode.SmsgMoveSetHover, WorldOpcode.SmsgMoveUnsetHover, WorldOpcode.MsgMoveTeleportAck,
            WorldOpcode.SmsgTransferPending, WorldOpcode.SmsgTransferAborted, WorldOpcode.SmsgNewWorld,
            WorldOpcode.SmsgLogoutResponse, WorldOpcode.SmsgLogoutComplete, WorldOpcode.SmsgLogoutCancelAck, WorldOpcode.SmsgPong,
            WorldOpcode.SmsgResurrectRequest, WorldOpcode.SmsgResurrectFailed, WorldOpcode.SmsgSpiritHealerConfirm,
            WorldOpcode.SmsgCorpseReclaimDelay, WorldOpcode.SmsgAreaSpiritHealerTime,
            WorldOpcode.SmsgGossipMessage, WorldOpcode.SmsgGossipComplete, WorldOpcode.SmsgLootResponse, WorldOpcode.SmsgLootReleaseResponse,
            WorldOpcode.SmsgQuestgiverQuestList, WorldOpcode.SmsgQuestgiverQuestDetails, WorldOpcode.SmsgQuestgiverRequestItems,
            WorldOpcode.SmsgQuestgiverOfferReward, WorldOpcode.SmsgQuestgiverQuestInvalid, WorldOpcode.SmsgQuestgiverQuestComplete,
            WorldOpcode.SmsgQuestgiverQuestFailed, WorldOpcode.SmsgLoginSettimespeed, WorldOpcode.SmsgCastResult,
        ];

        var table = new bool[set.Max(opcode => (int)opcode) + 1];
        foreach (WorldOpcode opcode in set)
        {
            table[(int)opcode] = true;
        }

        return table;
    }

    /// <summary>Whether <paramref name="opcode"/> is one a trace records: one array read, no allocation.</summary>
    internal static bool IsCurated(WorldOpcode opcode)
    {
        uint value = (ushort)opcode;
        return value < (uint)Curated.Length && Curated[value];
    }

    /// <summary>
    /// The detail text of an <c>out</c> line (<c>NAME size=N [decoded]</c>), or null when the packet is not about the traced character and
    /// should not be written (a spline speed change of another unit). <paramref name="self"/> is the traced character's GUID.
    /// </summary>
    internal static string? Describe(WorldOpcode opcode, ReadOnlySpan<byte> payload, ObjectGuid self)
    {
        if (opcode is WorldOpcode.SmsgSplineSetWalkSpeed or WorldOpcode.SmsgSplineSetRunSpeed or WorldOpcode.SmsgSplineSetRunBackSpeed
            or WorldOpcode.SmsgSplineSetSwimSpeed or WorldOpcode.SmsgSplineSetSwimBackSpeed)
        {
            // Sent for every unit in view: another unit's change is dropped before anything is built.
            var probe = new PacketReader(payload);
            if (probe.TryReadPackedGuid(out ulong other) && other != self.Value)
            {
                return null;
            }
        }

        string head = string.Create(CultureInfo.InvariantCulture, $"{WorldOpcodeNames.GetName(opcode)} size={payload.Length}");
        var reader = new PacketReader(payload);
        switch (opcode)
        {
            case WorldOpcode.SmsgForceWalkSpeedChange:
            case WorldOpcode.SmsgForceRunSpeedChange:
            case WorldOpcode.SmsgForceRunBackSpeedChange:
            case WorldOpcode.SmsgForceSwimSpeedChange:
            case WorldOpcode.SmsgForceSwimBackSpeedChange:
                // SpeedPackets.BuildForceChange (ArcaneCore.Game/Locomotion/Speed/SpeedPackets.cs): packed GUID, u32 counter, f32 speed.
                return reader.TryReadPackedGuid(out _) && reader.TryReadUInt32(out uint speedCounter) && reader.TryReadSingle(out float speed)
                    ? string.Create(CultureInfo.InvariantCulture, $"{head} counter={speedCounter} speed={speed:0.###}")
                    : head;

            case WorldOpcode.SmsgSplineSetWalkSpeed:
            case WorldOpcode.SmsgSplineSetRunSpeed:
            case WorldOpcode.SmsgSplineSetRunBackSpeed:
            case WorldOpcode.SmsgSplineSetSwimSpeed:
            case WorldOpcode.SmsgSplineSetSwimBackSpeed:
                // SpeedPackets.BuildSpline: packed GUID, f32 speed. Sent to everyone who sees the unit, so only the traced character's own are written.
                if (!reader.TryReadPackedGuid(out ulong unit) || !reader.TryReadSingle(out float splineSpeed))
                {
                    return head;
                }

                return unit == self.Value ? string.Create(CultureInfo.InvariantCulture, $"{head} speed={splineSpeed:0.###}") : null;

            case WorldOpcode.SmsgForceMoveRoot:
            case WorldOpcode.SmsgForceMoveUnroot:
            case WorldOpcode.SmsgMoveWaterWalk:
            case WorldOpcode.SmsgMoveLandWalk:
            case WorldOpcode.SmsgMoveFeatherFall:
            case WorldOpcode.SmsgMoveNormalFall:
            case WorldOpcode.SmsgMoveSetHover:
            case WorldOpcode.SmsgMoveUnsetHover:
                // MovementChangePackets.BuildFlagChange (ArcaneCore.Game/Locomotion/MovementChangePackets.cs): packed GUID, u32 counter.
                return reader.TryReadPackedGuid(out _) && reader.TryReadUInt32(out uint flagCounter)
                    ? string.Create(CultureInfo.InvariantCulture, $"{head} counter={flagCounter}")
                    : head;

            case WorldOpcode.SmsgNewWorld:
                // TeleportPackets.BuildNewWorld (ArcaneCore.Game/Teleport/TeleportPackets.cs): u32 map, f32 x, y, z, orientation.
                return reader.TryReadUInt32(out uint newMap) && reader.TryReadSingle(out float x) && reader.TryReadSingle(out float y) && reader.TryReadSingle(out float z)
                    ? string.Create(CultureInfo.InvariantCulture, $"{head} map={newMap} x={x:0.00} y={y:0.00} z={z:0.00}")
                    : head;

            case WorldOpcode.SmsgTransferPending:
                // TeleportPackets.BuildTransferPending: u32 target map first (both overloads).
                return reader.TryReadUInt32(out uint pendingMap) ? string.Create(CultureInfo.InvariantCulture, $"{head} map={pendingMap}") : head;

            case WorldOpcode.SmsgTransferAborted:
                // TeleportPackets.BuildTransferAborted: u8 reason.
                return reader.TryReadByte(out byte abortReason) ? string.Create(CultureInfo.InvariantCulture, $"{head} reason={abortReason}") : head;

            case WorldOpcode.SmsgLogoutResponse:
                // MiscPackets.BuildLogoutResponse (ArcaneCore.World/Packets/MiscPackets.cs): u32 result, u8 instant.
                return reader.TryReadUInt32(out uint result) && reader.TryReadByte(out byte instant)
                    ? string.Create(CultureInfo.InvariantCulture, $"{head} result={result} instant={instant}")
                    : head;

            case WorldOpcode.SmsgPong:
                // WorldSession.TryHandlePing writes the pong: u32 sequence.
                return reader.TryReadUInt32(out uint sequence) ? string.Create(CultureInfo.InvariantCulture, $"{head} seq={sequence}") : head;

            default:
                return head;
        }
    }
}
