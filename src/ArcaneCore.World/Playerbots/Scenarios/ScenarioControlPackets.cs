using ArcaneCore.Protocol;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>SMSG_CLIENT_CONTROL_UPDATE as a scenario sees it: the unit and whether this client may move it.</summary>
public sealed record ClientControlView(ulong Unit, bool AllowMove);

/// <summary>
/// SMSG_PET_SPELLS as a scenario sees it: the pet or charm (0 when the bar is removed), the control duration, the react and command bytes
/// (the possess form carries a u32 0 there) and the ten action words.
/// </summary>
public sealed record PetBarView(ulong Unit, int DurationMs, byte React, byte Command, IReadOnlyList<uint> Bar);

/// <summary>A relayed movement block: the mover's packed GUID and its position (MSG_MOVE_* from the server).</summary>
public sealed record MoveView(ulong Mover, float X, float Y, float Z);

/// <summary>
/// The unit-control packets of the scenario harness (docs/areas/unit-control.md): decoders for SMSG_CLIENT_CONTROL_UPDATE, SMSG_PET_SPELLS and
/// relayed movement, and the client packets a controlling bot sends (CMSG_SET_ACTIVE_MOVER, a movement block, CMSG_PET_ACTION). Layouts
/// follow <c>CharmPackets</c>, <c>PetPackets</c> and <c>MovementInfo</c> (wow_messages smsg_client_control_update, smsg_pet_spells, msg_move_*).
/// </summary>
public static class ScenarioControlPackets
{
    public static ClientControlView ClientControl(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var r = new PacketReader(payload);
        ulong unit = r.ReadPackedGuid();
        bool allow = r.ReadByte() != 0;
        if (r.Remaining != 0)
        {
            throw new FormatException($"ClientControl: {r.Remaining} trailing bytes");
        }

        return new ClientControlView(unit, allow);
    }

    public static PetBarView PetBar(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var r = new PacketReader(payload);
        ulong unit = r.ReadUInt64();
        if (unit == 0)
        {
            return new PetBarView(0, 0, 0, 0, []);
        }

        int duration = r.ReadInt32();
        byte react = r.ReadByte();
        byte command = r.ReadByte();
        r.ReadByte();
        r.ReadByte();
        uint[] bar = new uint[10];
        for (int i = 0; i < bar.Length; i++)
        {
            bar[i] = r.ReadUInt32();
        }

        return new PetBarView(unit, duration, react, command, bar);
    }

    public static MoveView Move(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var r = new PacketReader(payload);
        ulong mover = r.ReadPackedGuid();
        MovementInfo movement = MovementInfo.Read(ref r);
        return new MoveView(mover, movement.X, movement.Y, movement.Z);
    }

    /// <summary>CMSG_SET_ACTIVE_MOVER: u64 GUID.</summary>
    public static byte[] SetActiveMover(ulong unit) => ScenarioPackets.Guid(unit);

    /// <summary>A client movement block (MSG_MOVE_* client to server is the MovementInfo alone, gtker MSG_MOVE_*_Client 1.12).</summary>
    public static byte[] Movement(float x, float y, float z, float orientation, uint time, MovementFlags flags = MovementFlags.None)
    {
        var movement = new MovementInfo { Flags = flags, Time = time, X = x, Y = y, Z = z, Orientation = orientation };
        var w = new PacketWriter(32);
        movement.Write(w);
        return w.ToArray();
    }

    /// <summary>CMSG_PET_ACTION: u64 pet, u32 action | type &lt;&lt; 24, u64 target.</summary>
    public static byte[] PetAction(ulong pet, uint action, byte type, ulong target = 0)
    {
        var w = new PacketWriter(20);
        w.WriteUInt64(pet);
        w.WriteUInt32((action & 0x00FFFFFF) | ((uint)type << 24));
        w.WriteUInt64(target);
        return w.ToArray();
    }
}
