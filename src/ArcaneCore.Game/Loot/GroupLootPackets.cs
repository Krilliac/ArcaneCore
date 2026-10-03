using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Loot;

/// <summary>
/// The group loot packets of 1.12.1: need/greed rolls, master loot and the loot error reply.
/// Layouts follow vmangos Server/Packets/Loot.cpp (20-175, 190-215) and Packets/Group.cpp (264-275),
/// cross-checked with wow_messages world/loot (smsg_loot_start_roll, smsg_loot_roll, smsg_loot_roll_won,
/// smsg_loot_all_passed, smsg_loot_master_list, cmsg_loot_roll, cmsg_loot_master_give). The item random
/// suffix and property are always 0 in 1.12 (wow_messages: "not used").
/// </summary>
public static class GroupLootPackets
{
    /// <summary>SMSG_LOOT_START_ROLL: u64 corpse, u32 slot, u32 item, u32 suffix 0, u32 property 0, u32 countdown ms (28 bytes).</summary>
    public static byte[] StartRoll(ObjectGuid corpse, uint slot, uint itemId, uint countdownMs)
    {
        var writer = new PacketWriter(28);
        writer.WriteUInt64(corpse.Value);
        writer.WriteUInt32(slot);
        writer.WriteUInt32(itemId);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(countdownMs);
        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_LOOT_ROLL: u64 corpse, u32 slot, u64 roller, u32 item, u32 0, u32 0, u8 roll number, u8 type (34 bytes).
    /// Raw form: <see cref="VoteAnnouncement"/> and <see cref="ResolvedRoll"/> are the two uses.
    /// </summary>
    public static byte[] Roll(ObjectGuid corpse, uint slot, ObjectGuid roller, uint itemId, byte rollNumber, byte rollType)
    {
        var writer = new PacketWriter(34);
        writer.WriteUInt64(corpse.Value);
        writer.WriteUInt32(slot);
        writer.WriteUInt64(roller.Value);
        writer.WriteUInt32(itemId);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteByte(rollNumber);
        writer.WriteByte(rollType);
        return writer.ToArray();
    }

    /// <summary>
    /// The SMSG_LOOT_ROLL sent when a player votes (vmangos Group::CountRollVote, Group.cpp:957-972):
    /// pass is (128, 128), need (0, 0), greed (128, 2).
    /// </summary>
    public static byte[] VoteAnnouncement(ObjectGuid corpse, uint slot, ObjectGuid roller, uint itemId, RollVote vote)
        => vote switch
        {
            RollVote.Need => Roll(corpse, slot, roller, itemId, 0, 0),
            RollVote.Greed => Roll(corpse, slot, roller, itemId, 128, (byte)RollVote.Greed),
            _ => Roll(corpse, slot, roller, itemId, 128, 128),
        };

    /// <summary>The SMSG_LOOT_ROLL of a resolved roll (Group.cpp:1149 and 1202): the 1..100 number and the vote (need = 1, greed = 2).</summary>
    public static byte[] ResolvedRoll(ObjectGuid corpse, uint slot, ObjectGuid roller, uint itemId, byte number, RollVote vote)
        => Roll(corpse, slot, roller, itemId, number, (byte)vote);

    /// <summary>SMSG_LOOT_ROLL_WON: u64 corpse, u32 slot, u32 item, u32 0, u32 0, u64 winner, u8 roll, u8 vote (34 bytes).</summary>
    public static byte[] RollWon(ObjectGuid corpse, uint slot, uint itemId, ObjectGuid winner, byte roll, RollVote vote)
    {
        var writer = new PacketWriter(34);
        writer.WriteUInt64(corpse.Value);
        writer.WriteUInt32(slot);
        writer.WriteUInt32(itemId);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt64(winner.Value);
        writer.WriteByte(roll);
        writer.WriteByte((byte)vote);
        return writer.ToArray();
    }

    /// <summary>SMSG_LOOT_ALL_PASSED: u64 corpse, u32 slot, u32 item, u32 random property 0, u32 random suffix 0 (24 bytes; the field order differs from START_ROLL).</summary>
    public static byte[] AllPassed(ObjectGuid corpse, uint slot, uint itemId)
    {
        var writer = new PacketWriter(24);
        writer.WriteUInt64(corpse.Value);
        writer.WriteUInt32(slot);
        writer.WriteUInt32(itemId);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        return writer.ToArray();
    }

    /// <summary>SMSG_LOOT_MASTER_LIST: u8 count, then that many player GUIDs (vmangos Packets/Group.cpp:264-275).</summary>
    public static byte[] MasterList(IReadOnlyList<ObjectGuid> eligible)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        int count = Math.Min(eligible.Count, byte.MaxValue);
        var writer = new PacketWriter(1 + (count * 8));
        writer.WriteByte((byte)count);
        for (int i = 0; i < count; i++)
        {
            writer.WriteUInt64(eligible[i].Value);
        }

        return writer.ToArray();
    }

    /// <summary>
    /// The error form of SMSG_LOOT_RESPONSE: u64 guid, u8 loot type 0, u8 error code (10 bytes; vmangos
    /// Loot.cpp LootResponse::AppendBodyTo and Player::SendLootError, mangos-classic Player::SendLootError).
    /// wow_messages lists gold and items after the error code; both servers stop at the code.
    /// </summary>
    public static byte[] LootErrorResponse(ObjectGuid target, LootError error)
    {
        var writer = new PacketWriter(10);
        writer.WriteUInt64(target.Value);
        writer.WriteByte(0);
        writer.WriteByte((byte)error);
        return writer.ToArray();
    }

    /// <summary>
    /// CMSG_LOOT_ROLL: u64 corpse, u32 slot, u8 vote. False for a short payload or a vote of 3 or more
    /// (vmangos GroupHandler.cpp:370-379 drops MAX_ROLL_FROM_CLIENT and above).
    /// </summary>
    public static bool TryParseLootRoll(ReadOnlySpan<byte> payload, out ObjectGuid corpse, out uint slot, out RollVote vote)
    {
        corpse = default;
        slot = 0;
        vote = RollVote.Pass;
        if (payload.Length < 13)
        {
            return false;
        }

        var reader = new PacketReader(payload);
        ulong guid = reader.ReadUInt64();
        uint itemSlot = reader.ReadUInt32();
        byte raw = reader.ReadByte();
        if (raw > (byte)RollVote.Greed)
        {
            return false;
        }

        corpse = new ObjectGuid(guid);
        slot = itemSlot;
        vote = (RollVote)raw;
        return true;
    }

    /// <summary>CMSG_LOOT_MASTER_GIVE: u64 loot guid, u8 slot, u64 target player. False for a short payload.</summary>
    public static bool TryParseMasterGive(ReadOnlySpan<byte> payload, out ObjectGuid loot, out byte slot, out ObjectGuid target)
    {
        loot = default;
        slot = 0;
        target = default;
        if (payload.Length < 17)
        {
            return false;
        }

        var reader = new PacketReader(payload);
        loot = new ObjectGuid(reader.ReadUInt64());
        slot = reader.ReadByte();
        target = new ObjectGuid(reader.ReadUInt64());
        return true;
    }
}
