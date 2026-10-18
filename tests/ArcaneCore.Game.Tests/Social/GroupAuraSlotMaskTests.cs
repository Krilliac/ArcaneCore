using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>vmangos Unit::UpdateAuraForGroup: a marked visible aura slot is re-sent with its current spell id even when the snapshot did not change.</summary>
public sealed class GroupAuraSlotMaskTests
{
    [Fact]
    public void ForcedOwnAuraSlot_IsWrittenEvenWithoutASnapshotChange()
    {
        using var kit = new PetTestKit();
        (var player, _) = kit.AddPlayer(1);
        player.SetUInt32(UpdateFields.UnitFieldAura + 3, 1126);
        player.SetUInt32(UpdateFields.UnitFieldAura + SpellSystem.MaxPositiveAuras + 1, 589);
        GroupMemberStatsSnapshot before = GroupMemberStatsSnapshot.Capture(player);
        GroupMemberStatsSnapshot now = GroupMemberStatsSnapshot.Capture(player);

        GroupUpdateFlags mask = GroupUpdateFlags.Auras | GroupUpdateFlags.AurasNegative;
        var reader = new PacketReader(GroupPackets.BuildPartyMemberStats(now, mask, before, new GroupAuraSlotMasks(Positive: 1u << 3, Negative: 1 << 1)));
        reader.ReadPackedGuid();
        Assert.Equal((uint)mask, reader.ReadUInt32());
        Assert.Equal(1u << 3, reader.ReadUInt32());
        Assert.Equal((ushort)1126, reader.ReadUInt16());
        Assert.Equal((ushort)(1 << 1), reader.ReadUInt16());
        Assert.Equal((ushort)589, reader.ReadUInt16());
    }

    [Fact]
    public void WithoutForcedSlots_AnUnchangedSnapshotWritesAnEmptyMask()
    {
        using var kit = new PetTestKit();
        (var player, _) = kit.AddPlayer(1);
        player.SetUInt32(UpdateFields.UnitFieldAura + 3, 1126);
        GroupMemberStatsSnapshot before = GroupMemberStatsSnapshot.Capture(player);

        var reader = new PacketReader(GroupPackets.BuildPartyMemberStats(GroupMemberStatsSnapshot.Capture(player), GroupUpdateFlags.Auras, before));
        reader.ReadPackedGuid();
        reader.ReadUInt32();
        Assert.Equal(0u, reader.ReadUInt32());
    }
}
