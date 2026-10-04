using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Battlegrounds;

/// <summary>CMSG_BATTLEMASTER_JOIN (vmangos Server/Packets/Battleground.cpp:46-52): the battlemaster (or the player at a portal), the map, the instance (0 for first available) and the group flag.</summary>
public readonly record struct BattlemasterJoin(ObjectGuid Guid, uint MapId, uint InstanceId, bool JoinAsGroup);

/// <summary>CMSG_BATTLEFIELD_PORT, 1.12 layout (Battleground.cpp:28-34): the map and the action, 1 to enter the battle and 0 to leave the queue.</summary>
public readonly record struct BattlefieldPort(uint MapId, byte Action);

/// <summary>
/// The 1.12.1 battleground packet bodies. References: vmangos Server/Packets/Battleground.cpp, Handlers/BattleGroundHandler.cpp,
/// Battlegrounds/BattleGroundMgr.cpp (the builders) and the gtker/wow_messages 1.12 definitions. Client packets are parsed strictly:
/// a body of the wrong size is refused. GUIDs are the full 8 bytes (<c>ObjectGuid &lt;&lt;</c>), not packed.
/// <para>
/// One deliberate divergence from wow_messages: SMSG_BATTLEFIELD_STATUS carries the status as a u32 (vmangos Battleground.cpp:95 and
/// mangos-classic BattleGroundMgr.cpp:103 write <c>uint32(statusId)</c>), where <c>smsg_battlefield_status.wowm</c> types it u8.
/// </para>
/// </summary>
public static class BattlegroundPackets
{
    /// <summary>SMSG_GROUP_JOINED_BATTLEGROUND result: a group member is a deserter (vmangos <c>BG_GROUPJOIN_DESERTERS</c>, BattleGroundMgr.h:79).</summary>
    public const uint GroupJoinDeserters = 0xFFFFFFFE;

    /// <summary>SMSG_GROUP_JOINED_BATTLEGROUND result: the join failed for this member (vmangos <c>BG_GROUPJOIN_FAILED</c>, BattleGroundMgr.h:80).</summary>
    public const uint GroupJoinFailed = 0xFFFFFFFF;

    /// <summary>The empty form of SMSG_BATTLEFIELD_STATUS: the queue slot and a zero map (vmangos <c>BattlefieldStatusEmpty</c>, Battleground.cpp:113-119).</summary>
    public static byte[] BuildBattlefieldStatusEmpty(uint queueSlot)
    {
        var packet = new PacketWriter(8);
        packet.WriteUInt32(queueSlot);
        packet.WriteUInt32(0);
        return packet.ToArray();
    }

    /// <summary>
    /// SMSG_BATTLEFIELD_STATUS (vmangos <c>BattlefieldStatus</c>, Battleground.cpp:87-101): u32 slot, u32 map, u8 bracket, u32 client
    /// instance, u32 status, u32 time1 and, for a queued or running battleground only, u32 time2. Status <see cref="BattlegroundStatus.None"/>
    /// is the empty form (BattleGroundMgr.cpp:1036). time1/time2 are: WAIT_QUEUE average wait and time in queue; WAIT_JOIN the time left to
    /// accept; IN_PROGRESS the time until auto-leave and the time since the start.
    /// </summary>
    public static byte[] BuildBattlefieldStatus(uint queueSlot, uint mapId, byte bracket, uint clientInstanceId, BattlegroundStatus status, uint time1, uint time2)
    {
        if (status == BattlegroundStatus.None)
        {
            return BuildBattlefieldStatusEmpty(queueSlot);
        }

        var packet = new PacketWriter(25);
        packet.WriteUInt32(queueSlot);
        packet.WriteUInt32(mapId);
        packet.WriteByte(bracket);
        packet.WriteUInt32(clientInstanceId);
        packet.WriteUInt32((uint)status);
        packet.WriteUInt32(time1);
        if (status is BattlegroundStatus.WaitQueue or BattlegroundStatus.InProgress)
        {
            packet.WriteUInt32(time2);
        }

        return packet.ToArray();
    }

    /// <summary>SMSG_BATTLEFIELD_LIST (vmangos <c>BattlefieldList</c>, Battleground.cpp:203-214): u64 battlemaster, u32 map, u8 bracket, u32 count, u32 instance ids.</summary>
    public static byte[] BuildBattlefieldList(ObjectGuid battlemaster, uint mapId, byte bracket, IReadOnlyList<uint> instanceIds)
    {
        var packet = new PacketWriter(17 + (instanceIds.Count * 4));
        packet.WriteUInt64(battlemaster.Value);
        packet.WriteUInt32(mapId);
        packet.WriteByte(bracket);
        packet.WriteUInt32((uint)instanceIds.Count);
        foreach (uint id in instanceIds)
        {
            packet.WriteUInt32(id);
        }

        return packet.ToArray();
    }

    /// <summary>SMSG_BATTLEFIELD_WIN, an empty body (Battleground.cpp:216-223).</summary>
    public static byte[] BuildBattlefieldWin() => [];

    /// <summary>SMSG_BATTLEFIELD_LOSE, an empty body (Battleground.cpp:225-232).</summary>
    public static byte[] BuildBattlefieldLose() => [];

    /// <summary>
    /// MSG_PVP_LOG_DATA from the server (vmangos <c>PvpLogData</c>, Battleground.cpp:141-166): u8 ended, u8 winner if ended, u32 count (at most 80)
    /// and per player u64 guid, u32 rank, u32 killing blows, u32 honorable kills, u32 deaths, u32 bonus honor, u32 extra count and the extras.
    /// </summary>
    public static byte[] BuildPvpLogData(PvpLogSnapshot log)
    {
        int count = Math.Min(log.Rows.Count, BattlegroundConstants.PvpLogMaxPlayers);
        var packet = new PacketWriter(64 + (count * 40));
        if (log.Ended)
        {
            packet.WriteByte(1);
            packet.WriteByte((byte)log.Winner);
        }
        else
        {
            packet.WriteByte(0);
        }

        packet.WriteUInt32((uint)count);
        for (int i = 0; i < count; i++)
        {
            PvpLogRow row = log.Rows[i];
            packet.WriteUInt64(row.Player.Value);
            packet.WriteUInt32(row.Rank);
            packet.WriteUInt32(row.KillingBlows);
            packet.WriteUInt32(row.HonorableKills);
            packet.WriteUInt32(row.Deaths);
            packet.WriteUInt32(row.BonusHonor);
            packet.WriteUInt32((uint)row.ExtraFields.Count);
            foreach (uint extra in row.ExtraFields)
            {
                packet.WriteUInt32(extra);
            }
        }

        return packet.ToArray();
    }

    /// <summary>
    /// MSG_BATTLEGROUND_PLAYER_POSITIONS from the server (BattleGroundHandler.cpp:274-324): u32 teammate count and (u64 guid, f32 x, f32 y)
    /// each, then u8 carrier count and its entries (Warsong Gulch: the enemy flag carrier from the viewer's side).
    /// </summary>
    public static byte[] BuildPlayerPositions(IReadOnlyList<(ObjectGuid Guid, float X, float Y)> teammates, IReadOnlyList<(ObjectGuid Guid, float X, float Y)> carriers)
    {
        var packet = new PacketWriter(5 + ((teammates.Count + carriers.Count) * 16));
        packet.WriteUInt32((uint)teammates.Count);
        foreach ((ObjectGuid guid, float x, float y) in teammates)
        {
            WritePosition(packet, guid, x, y);
        }

        packet.WriteByte((byte)carriers.Count);
        foreach ((ObjectGuid guid, float x, float y) in carriers)
        {
            WritePosition(packet, guid, x, y);
        }

        return packet.ToArray();
    }

    private static void WritePosition(PacketWriter packet, ObjectGuid guid, float x, float y)
    {
        packet.WriteUInt64(guid.Value);
        packet.WriteSingle(x);
        packet.WriteSingle(y);
    }

    /// <summary>SMSG_BATTLEGROUND_PLAYER_JOINED: the joiner's full GUID (Battleground.cpp:175-178).</summary>
    public static byte[] BuildPlayerJoined(ObjectGuid player) => BuildGuid(player);

    /// <summary>SMSG_BATTLEGROUND_PLAYER_LEFT: the leaver's full GUID (Battleground.cpp:185-188).</summary>
    public static byte[] BuildPlayerLeft(ObjectGuid player) => BuildGuid(player);

    /// <summary>
    /// SMSG_GROUP_JOINED_BATTLEGROUND: the map id on a successful join, <see cref="GroupJoinDeserters"/> or <see cref="GroupJoinFailed"/>
    /// (vmangos BattleGroundHandler.cpp:176,218,244,256). wow_messages types this as a BgTypeId enum; vmangos sends the map id.
    /// </summary>
    public static byte[] BuildGroupJoined(uint result)
    {
        var packet = new PacketWriter(4);
        packet.WriteUInt32(result);
        return packet.ToArray();
    }

    /// <summary>SMSG_PLAY_SOUND: the sound id (wow_messages smsg_play_sound.wowm).</summary>
    public static byte[] BuildPlaySound(uint soundId) => BuildUInt32(soundId);

    /// <summary>SMSG_UPDATE_WORLD_STATE: the field and the value (vmangos BattleGroundMgr.cpp:1144-1150); a negative state is sent as its 32-bit pattern.</summary>
    public static byte[] BuildUpdateWorldState(uint field, uint value)
    {
        var packet = new PacketWriter(8);
        packet.WriteUInt32(field);
        packet.WriteUInt32(value);
        return packet.ToArray();
    }

    /// <summary>SMSG_AREA_SPIRIT_HEALER_TIME: the spirit healer and the milliseconds to the next resurrection wave (wow_messages smsg_area_spirit_healer_time.wowm).</summary>
    public static byte[] BuildAreaSpiritHealerTime(ObjectGuid spiritHealer, uint nextResurrectMs)
    {
        var packet = new PacketWriter(12);
        packet.WriteUInt64(spiritHealer.Value);
        packet.WriteUInt32(nextResurrectMs);
        return packet.ToArray();
    }

    private static byte[] BuildGuid(ObjectGuid guid)
    {
        var packet = new PacketWriter(8);
        packet.WriteUInt64(guid.Value);
        return packet.ToArray();
    }

    private static byte[] BuildUInt32(uint value)
    {
        var packet = new PacketWriter(4);
        packet.WriteUInt32(value);
        return packet.ToArray();
    }

    // ------------------------------------------------------------------ client packets

    /// <summary>CMSG_BATTLEMASTER_JOIN: u64 guid, u32 map, u32 instance, u8 join as group (exactly 17 bytes).</summary>
    public static bool TryParseBattlemasterJoin(ReadOnlySpan<byte> body, out BattlemasterJoin join)
    {
        join = default;
        if (body.Length != 17)
        {
            return false;
        }

        var reader = new PacketReader(body);
        join = new BattlemasterJoin(new ObjectGuid(reader.ReadUInt64()), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadByte() != 0);
        return true;
    }

    /// <summary>CMSG_BATTLEFIELD_PORT: u32 map, u8 action (exactly 5 bytes).</summary>
    public static bool TryParseBattlefieldPort(ReadOnlySpan<byte> body, out BattlefieldPort port)
    {
        port = default;
        if (body.Length != 5)
        {
            return false;
        }

        var reader = new PacketReader(body);
        port = new BattlefieldPort(reader.ReadUInt32(), reader.ReadByte());
        return true;
    }

    /// <summary>CMSG_BATTLEFIELD_LIST, CMSG_BATTLEFIELD_JOIN and CMSG_LEAVE_BATTLEFIELD (1.12): a u32 map (exactly 4 bytes).</summary>
    public static bool TryParseMap(ReadOnlySpan<byte> body, out uint mapId)
    {
        mapId = 0;
        if (body.Length != 4)
        {
            return false;
        }

        mapId = new PacketReader(body).ReadUInt32();
        return true;
    }

    /// <summary>CMSG_BATTLEMASTER_HELLO, CMSG_AREA_SPIRIT_HEALER_QUERY and CMSG_AREA_SPIRIT_HEALER_QUEUE: a u64 guid (exactly 8 bytes).</summary>
    public static bool TryParseGuid(ReadOnlySpan<byte> body, out ObjectGuid guid)
    {
        guid = default;
        if (body.Length != 8)
        {
            return false;
        }

        guid = new ObjectGuid(new PacketReader(body).ReadUInt64());
        return true;
    }
}
