using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
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
    /// SMSG_PARTY_MEMBER_STATS / _FULL body (vmangos WorldSession::BuildPartyMemberStatsPacket):
    /// packed guid, u32 mask, then the masked fields in bit order. Auras and pets do not exist
    /// yet, so aura masks are empty and pet fields are the "no pet" values vmangos writes.
    /// </summary>
    public static byte[] BuildPartyMemberStats(Player player, GroupUpdateFlags mask, uint petPositiveAuraMask = 0, ushort petNegativeAuraMask = 0)
    {
        var writer = new PacketWriter(64);
        writer.WritePackedGuid(player.Guid.Value);
        writer.WriteUInt32((uint)mask);
        PowerType power = player.PowerType;
        int powerIndex = (int)power <= 4 ? (int)power : 0;
        if ((mask & GroupUpdateFlags.Status) != 0)
        {
            writer.WriteByte((byte)StatusOf(player));
        }

        if ((mask & GroupUpdateFlags.CurrentHp) != 0)
        {
            writer.WriteUInt16((ushort)Math.Min(player.Health, ushort.MaxValue));
        }

        if ((mask & GroupUpdateFlags.MaxHp) != 0)
        {
            writer.WriteUInt16((ushort)Math.Min(player.MaxHealth, ushort.MaxValue));
        }

        if ((mask & GroupUpdateFlags.PowerType) != 0)
        {
            writer.WriteByte((byte)power);
        }

        if ((mask & GroupUpdateFlags.CurrentPower) != 0)
        {
            writer.WriteUInt16((ushort)Math.Min(player.GetUInt32(UpdateFields.UnitFieldPower1 + powerIndex), ushort.MaxValue));
        }

        if ((mask & GroupUpdateFlags.MaxPower) != 0)
        {
            writer.WriteUInt16((ushort)Math.Min(player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + powerIndex), ushort.MaxValue));
        }

        if ((mask & GroupUpdateFlags.Level) != 0)
        {
            writer.WriteUInt16(player.Level);
        }

        if ((mask & GroupUpdateFlags.Zone) != 0)
        {
            writer.WriteUInt16((ushort)player.ZoneId);
        }

        if ((mask & GroupUpdateFlags.Position) != 0)
        {
            writer.WriteUInt16(unchecked((ushort)(short)player.X));
            writer.WriteUInt16(unchecked((ushort)(short)player.Y));
        }

        if ((mask & GroupUpdateFlags.Auras) != 0)
        {
            writer.WriteUInt32(0);
        }

        if ((mask & GroupUpdateFlags.AurasNegative) != 0)
        {
            writer.WriteUInt16(0);
        }

        if ((mask & GroupUpdateFlags.PetGuid) != 0)
        {
            writer.WriteUInt64(player.GetPet()?.Guid.Value ?? 0);
        }

        if ((mask & GroupUpdateFlags.PetName) != 0)
        {
            string name = player.GetPet()?.Summon?.Charm?.Name ?? string.Empty;
            writer.WriteCString(name);
        }

        if ((mask & GroupUpdateFlags.PetModelId) != 0)
        {
            writer.WriteUInt16(unchecked((ushort)(player.GetPet()?.GetUInt32(UpdateFields.UnitFieldDisplayid) ?? 0)));
        }

        if ((mask & GroupUpdateFlags.PetCurrentHp) != 0)
        {
            writer.WriteUInt16(unchecked((ushort)(player.GetPet()?.Health ?? 0)));
        }

        if ((mask & GroupUpdateFlags.PetMaxHp) != 0)
        {
            writer.WriteUInt16(unchecked((ushort)(player.GetPet()?.MaxHealth ?? 0)));
        }

        if ((mask & GroupUpdateFlags.PetPowerType) != 0)
        {
            writer.WriteByte(player.GetPet() is { } pet ? (byte)pet.PowerType : (byte)0);
        }

        if ((mask & GroupUpdateFlags.PetCurrentPower) != 0)
        {
            Creature? pet = player.GetPet();
            int index = pet is not null && (int)pet.PowerType <= 4 ? (int)pet.PowerType : 0;
            writer.WriteUInt16(unchecked((ushort)(pet?.GetUInt32(UpdateFields.UnitFieldPower1 + index) ?? 0)));
        }

        if ((mask & GroupUpdateFlags.PetMaxPower) != 0)
        {
            Creature? pet = player.GetPet();
            int index = pet is not null && (int)pet.PowerType <= 4 ? (int)pet.PowerType : 0;
            writer.WriteUInt16(unchecked((ushort)(pet?.GetUInt32(UpdateFields.UnitFieldMaxpower1 + index) ?? 0)));
        }

        if ((mask & GroupUpdateFlags.PetAuras) != 0)
        {
            uint auraMask = (mask & GroupUpdateFlags.Full) == GroupUpdateFlags.Full || petPositiveAuraMask == 0
                ? PetAuraMask(player, true) : petPositiveAuraMask;
            writer.WriteUInt32(auraMask);
            for (int slot = 0; slot < SpellSystem.MaxPositiveAuras; slot++)
                if ((auraMask & (1u << slot)) != 0)
                    writer.WriteUInt16(unchecked((ushort)(player.GetPet()?.GetUInt32(UpdateFields.UnitFieldAura + slot) ?? 0)));
        }

        if ((mask & GroupUpdateFlags.PetAurasNegative) != 0)
        {
            ushort auraMask = (mask & GroupUpdateFlags.Full) == GroupUpdateFlags.Full || petNegativeAuraMask == 0
                ? (ushort)PetAuraMask(player, false) : petNegativeAuraMask;
            writer.WriteUInt16(auraMask);
            for (int slot = SpellSystem.MaxPositiveAuras; slot < SpellSystem.MaxAuras; slot++)
                if ((auraMask & (1u << (slot - SpellSystem.MaxPositiveAuras))) != 0)
                    writer.WriteUInt16(unchecked((ushort)(player.GetPet()?.GetUInt32(UpdateFields.UnitFieldAura + slot) ?? 0)));
        }

        return writer.ToArray();
    }

    private static uint PetAuraMask(Player player, bool positive)
    {
        if (player.GetPet() is not { } pet) return 0;
        int start = positive ? 0 : SpellSystem.MaxPositiveAuras;
        int end = positive ? SpellSystem.MaxPositiveAuras : SpellSystem.MaxAuras;
        uint mask = 0;
        for (int slot = start; slot < end; slot++)
            if (pet.GetUInt32(UpdateFields.UnitFieldAura + slot) != 0) mask |= 1u << (slot - start);
        return mask;
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
