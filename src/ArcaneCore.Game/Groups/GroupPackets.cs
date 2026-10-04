using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Groups;

/// <summary>Party and raid packets (vmangos Server/Packets/Group.cpp; gtker group/*.wowm).</summary>
public static class GroupPackets
{
    /// <summary>SMSG_PARTY_COMMAND_RESULT: u32 operation, CString member name, u32 result (vmangos PartyCommandResult).</summary>
    public static byte[] BuildPartyCommandResult(PartyOperation operation, string name, PartyResult result)
    {
        var writer = new PacketWriter(9 + name.Length);
        writer.WriteUInt32((uint)operation);
        writer.WriteCString(name);
        writer.WriteUInt32((uint)result);
        return writer.ToArray();
    }

    /// <summary>A packet that is a single CString (SMSG_GROUP_INVITE inviter, SMSG_GROUP_DECLINE / SMSG_GROUP_SET_LEADER name).</summary>
    public static byte[] BuildName(string name)
    {
        var writer = new PacketWriter(name.Length + 1);
        writer.WriteCString(name);
        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_GROUP_LIST for one member (vmangos Group::SendUpdate / GroupList::AppendBodyTo):
    /// u8 group type, u8 own subgroup | 0x80 if assistant, u32 other-member count, then per
    /// other member CString name, u64 guid, u8 status, u8 subgroup | 0x80; then u64 leader;
    /// with other members, also u8 loot method, u64 master looter (master loot only), u8 loot
    /// threshold and u8 0 (dungeon difficulty, builds after 1.10.2 — present in vmangos and
    /// cmangos-classic, absent from gtker smsg_group_list; the servers win).
    /// </summary>
    public static byte[] BuildGroupList(Group group, GroupMemberSlot self, Func<ObjectGuid, GroupMemberStatus> statusOf)
    {
        var writer = new PacketWriter(64 + (group.MemberCount * 24));
        writer.WriteByte((byte)group.Type);
        writer.WriteByte(Flags(self));
        writer.WriteUInt32((uint)(group.MemberCount - 1));
        foreach (GroupMemberSlot member in group.Members)
        {
            if (member.Guid == self.Guid)
            {
                continue;
            }

            writer.WriteCString(member.Name);
            writer.WriteUInt64(member.Guid.Value);
            writer.WriteByte((byte)statusOf(member.Guid));
            writer.WriteByte(Flags(member));
        }

        writer.WriteUInt64(group.LeaderGuid.Value);
        if (group.MemberCount > 1)
        {
            writer.WriteByte((byte)group.LootMethod);
            writer.WriteUInt64(group.LootMethod == LootMethod.MasterLoot ? group.LooterGuid.Value : 0);
            writer.WriteByte(group.LootThreshold);
            writer.WriteByte(0);
        }

        return writer.ToArray();
    }

    /// <summary>The "not in a group" SMSG_GROUP_LIST (vmangos default GroupList: type 0, flags 0, no members, leader 0).</summary>
    public static byte[] BuildEmptyGroupList()
    {
        var writer = new PacketWriter(14);
        writer.WriteByte(0);
        writer.WriteByte(0);
        writer.WriteUInt32(0);
        writer.WriteUInt64(0);
        return writer.ToArray();
    }

    /// <summary>vmangos Group::GetGroupMemberStatus.</summary>
    public static GroupMemberStatus StatusOf(Player? player)
    {
        if (player is null)
        {
            return GroupMemberStatus.Offline;
        }

        GroupMemberStatus status = GroupMemberStatus.Online;
        if ((player.UnitFlags & UnitFlags.Pvp) != 0)
        {
            status |= GroupMemberStatus.Pvp;
        }

        if (!player.IsAlive)
        {
            status |= GroupMemberStatus.Dead;
        }

        if ((player.Flags & PlayerFlags.Ghost) != 0)
        {
            status |= GroupMemberStatus.Ghost;
        }

        if ((player.Flags & PlayerFlags.FfaPvp) != 0)
        {
            status |= GroupMemberStatus.PvpFfa;
        }

        if (player.IsAfk)
        {
            status |= GroupMemberStatus.Afk;
        }

        if (player.IsDnd)
        {
            status |= GroupMemberStatus.Dnd;
        }

        return status;
    }

    /// <summary>
    /// SMSG_PARTY_MEMBER_STATS / _FULL body: packed guid, u32 mask, then masked fields in bit
    /// order. Aura masks and pet fields follow D:\refs\vmangos\src\game\Handlers\GroupHandler.cpp:599-755;
    /// D:\refs\wow_messages\wow_message_parser\wowm\world\social\smsg_party_member_stats.wowm:1-70
    /// has the 1.12 field order but omits the server's negative aura fields.
    /// </summary>
    public static byte[] BuildPartyMemberStats(Player player, GroupUpdateFlags mask)
        => BuildPartyMemberStats(GroupMemberStatsSnapshot.Capture(player), mask);

    internal static byte[] BuildPartyMemberStats(
        GroupMemberStatsSnapshot stats, GroupUpdateFlags mask, GroupMemberStatsSnapshot? previous = null)
    {
        var writer = new PacketWriter(64);
        writer.WritePackedGuid(stats.Guid.Value);
        writer.WriteUInt32((uint)mask);
        if ((mask & GroupUpdateFlags.Status) != 0)
        {
            writer.WriteByte((byte)stats.Status);
        }

        if ((mask & GroupUpdateFlags.CurrentHp) != 0)
        {
            writer.WriteUInt16(Clamp16(stats.Hp));
        }

        if ((mask & GroupUpdateFlags.MaxHp) != 0)
        {
            writer.WriteUInt16(Clamp16(stats.MaxHp));
        }

        if ((mask & GroupUpdateFlags.PowerType) != 0)
        {
            writer.WriteByte((byte)stats.Power);
        }

        if ((mask & GroupUpdateFlags.CurrentPower) != 0)
        {
            writer.WriteUInt16(Clamp16(stats.CurrentPower));
        }

        if ((mask & GroupUpdateFlags.MaxPower) != 0)
        {
            writer.WriteUInt16(Clamp16(stats.MaxPower));
        }

        if ((mask & GroupUpdateFlags.Level) != 0)
        {
            writer.WriteUInt16(stats.Level);
        }

        if ((mask & GroupUpdateFlags.Zone) != 0)
        {
            writer.WriteUInt16((ushort)stats.Zone);
        }

        if ((mask & GroupUpdateFlags.Position) != 0)
        {
            writer.WriteUInt16(unchecked((ushort)stats.X));
            writer.WriteUInt16(unchecked((ushort)stats.Y));
        }

        if ((mask & GroupUpdateFlags.Auras) != 0)
        {
            WriteAuras(writer, stats.Auras, previous?.Auras, 0, 32);
        }

        if ((mask & GroupUpdateFlags.AurasNegative) != 0)
        {
            WriteAuras(writer, stats.Auras, previous?.Auras, 32, 16);
        }

        if ((mask & GroupUpdateFlags.PetGuid) != 0)
        {
            writer.WriteUInt64(stats.PetGuid.Value);
        }

        if ((mask & GroupUpdateFlags.PetName) != 0)
        {
            writer.WriteCString(stats.PetName);
        }

        if ((mask & GroupUpdateFlags.PetModelId) != 0)
        {
            writer.WriteUInt16(Clamp16(stats.PetDisplayId));
        }

        if ((mask & GroupUpdateFlags.PetCurrentHp) != 0)
        {
            writer.WriteUInt16(Clamp16(stats.PetHp));
        }

        if ((mask & GroupUpdateFlags.PetMaxHp) != 0)
        {
            writer.WriteUInt16(Clamp16(stats.PetMaxHp));
        }

        if ((mask & GroupUpdateFlags.PetPowerType) != 0)
        {
            writer.WriteByte((byte)stats.PetPower);
        }

        if ((mask & GroupUpdateFlags.PetCurrentPower) != 0)
        {
            writer.WriteUInt16(Clamp16(stats.PetCurrentPower));
        }

        if ((mask & GroupUpdateFlags.PetMaxPower) != 0)
        {
            writer.WriteUInt16(Clamp16(stats.PetMaxPower));
        }

        if ((mask & GroupUpdateFlags.PetAuras) != 0)
        {
            WriteAuras(writer, stats.PetAuras,
                previous is not null && previous.PetGuid == stats.PetGuid ? previous.PetAuras : null, 0, 32);
        }

        if ((mask & GroupUpdateFlags.PetAurasNegative) != 0)
        {
            WriteAuras(writer, stats.PetAuras,
                previous is not null && previous.PetGuid == stats.PetGuid ? previous.PetAuras : null, 32, 16);
        }

        return writer.ToArray();
    }

    private static ushort Clamp16(uint value) => (ushort)Math.Min(value, ushort.MaxValue);

    /// <summary>
    /// D:\refs\vmangos\src\game\Handlers\GroupHandler.cpp:636-657,719-754:
    /// u32 positive mask or u16 negative mask,
    /// followed by one u16 spell id per set bit. A delta includes a cleared slot with id zero.
    /// </summary>
    private static void WriteAuras(PacketWriter writer, uint[] current, uint[]? previous, int start, int count)
    {
        uint bits = 0;
        for (int i = 0; i < count; i++)
        {
            int slot = start + i;
            if (previous is null ? current[slot] != 0 : current[slot] != previous[slot])
            {
                bits |= 1u << i;
            }
        }

        if (start == 0)
        {
            writer.WriteUInt32(bits);
        }
        else
        {
            writer.WriteUInt16((ushort)bits);
        }

        for (int i = 0; i < count; i++)
        {
            if ((bits & (1u << i)) != 0)
            {
                writer.WriteUInt16((ushort)current[start + i]);
            }
        }
    }

    /// <summary>SMSG_PARTY_MEMBER_STATS_FULL for a stranger or offline player: packed guid, mask STATUS, u8 offline (vmangos HandleRequestPartyMemberStatsOpcode).</summary>
    public static byte[] BuildPartyMemberStatsOffline(ObjectGuid guid)
    {
        var writer = new PacketWriter(14);
        writer.WritePackedGuid(guid.Value);
        writer.WriteUInt32((uint)GroupUpdateFlags.Status);
        writer.WriteByte((byte)GroupMemberStatus.Offline);
        return writer.ToArray();
    }

    /// <summary>MSG_RAID_READY_CHECK answer to the leader: u64 member, u8 state (vmangos RaidReadyCheckFromServer_Response).</summary>
    public static byte[] BuildReadyCheckResponse(ObjectGuid member, byte state)
    {
        var writer = new PacketWriter(9);
        writer.WriteUInt64(member.Value);
        writer.WriteByte(state);
        return writer.ToArray();
    }

    /// <summary>MSG_RAID_TARGET_UPDATE delta: u8 0, u8 icon, u64 target (vmangos RaidTargetUpdateDelta).</summary>
    public static byte[] BuildTargetIconDelta(byte icon, ObjectGuid target)
    {
        var writer = new PacketWriter(10);
        writer.WriteByte(0);
        writer.WriteByte(icon);
        writer.WriteUInt64(target.Value);
        return writer.ToArray();
    }

    /// <summary>MSG_RAID_TARGET_UPDATE full list: u8 1, then (u8 icon, u64 target) per set icon (vmangos RaidTargetUpdateAll).</summary>
    public static byte[] BuildTargetIconList(ObjectGuid[] icons)
    {
        var writer = new PacketWriter(1 + (icons.Length * 9));
        writer.WriteByte(1);
        for (int i = 0; i < icons.Length; i++)
        {
            if (!icons[i].IsEmpty)
            {
                writer.WriteByte((byte)i);
                writer.WriteUInt64(icons[i].Value);
            }
        }

        return writer.ToArray();
    }

    /// <summary>MSG_MINIMAP_PING to the group: u64 pinger, f32 x, f32 y (vmangos HandleMinimapPingOpcode).</summary>
    public static byte[] BuildMinimapPing(ObjectGuid pinger, float x, float y)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt64(pinger.Value);
        writer.WriteSingle(x);
        writer.WriteSingle(y);
        return writer.ToArray();
    }

    /// <summary>MSG_RANDOM_ROLL result: u32 min, u32 max, u32 roll, u64 roller (vmangos HandleRandomRollOpcode).</summary>
    public static byte[] BuildRandomRoll(uint min, uint max, uint roll, ObjectGuid roller)
    {
        var writer = new PacketWriter(20);
        writer.WriteUInt32(min);
        writer.WriteUInt32(max);
        writer.WriteUInt32(roll);
        writer.WriteUInt64(roller.Value);
        return writer.ToArray();
    }

    private static byte Flags(GroupMemberSlot slot) => (byte)(slot.SubGroup | (slot.Assistant ? 0x80 : 0));
}
