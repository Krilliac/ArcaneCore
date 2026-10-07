using ArcaneCore.Game;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

public sealed class StartingZoneFoodTests
{
    [Fact]
    public void UseItemPayload_UsesExactThreeBytePrefixAndSelfTargets()
    {
        byte[] payload = StartingZoneFood.BuildUseItemPayload(23);
        Assert.Equal(5, payload.Length);
        Assert.Equal((byte)InventorySlots.Bag0, payload[0]);
        Assert.Equal(23, payload[1]);
        Assert.Equal(0, payload[2]);
        var reader = new PacketReader(payload.AsSpan(3));
        SpellCastTargets targets = SpellCastTargets.Read(ref reader);
        Assert.Equal(SpellCastTargetFlags.Self, targets.Mask);
        Assert.Equal(0, reader.Remaining);
    }

    [Theory]
    [InlineData(22)]
    [InlineData(39)]
    public void UseItemPayload_RefusesSlotsOutsideTheObservedBackpack(byte slot)
    {
        Assert.Throws<MockProtocolException>(() => StartingZoneFood.BuildUseItemPayload(slot));
    }

    [Fact]
    public void FindFood_UsesObservedSlotAndRejectsAnotherPlayersOwner()
    {
        ulong guid = ObjectGuid.Item(22).Value;
        var fields = new Dictionary<int, uint>
        {
            [UpdateFields.PlayerFieldPackSlot1 + 10] = (uint)guid,
            [UpdateFields.PlayerFieldPackSlot1 + 11] = (uint)(guid >> 32),
        };
        var item = new Dictionary<int, uint>
        {
            [UpdateFields.ObjectFieldEntry] = 117,
            [UpdateFields.ItemFieldOwner] = 14,
            [UpdateFields.ItemFieldStackCount] = 5,
        };
        Assert.Equal((guid, (byte)(InventorySlots.ItemStart + 5), 5u),
            StartingZoneFood.FindFood(fields, 14, _ => item));
        item[UpdateFields.ItemFieldOwner] = 15;
        Assert.Throws<MockProtocolException>(() => StartingZoneFood.FindFood(fields, 14, _ => item));
    }

    [Fact]
    public void FoodStart_RequiresOwnCaster_AndAuraProofIsIndependent()
    {
        var writer = new PacketWriter(16);
        writer.WritePackedGuid(ObjectGuid.Item(22).Value);
        writer.WritePackedGuid(14);
        writer.WriteUInt32(433);
        Assert.True(StartingZoneFood.IsOwnFoodStart(writer.ToArray(), 14));
        Assert.False(StartingZoneFood.IsOwnFoodStart(writer.ToArray(), 15));
        Assert.False(StartingZoneFood.IsOwnFoodStart([0], 14));
        var fields = new Dictionary<int, uint>();
        Assert.False(StartingZoneFood.HasFoodAura(fields));
        fields[UpdateFields.UnitFieldAura + 4] = 433;
        Assert.True(StartingZoneFood.HasFoodAura(fields));
        fields[UpdateFields.UnitFieldAura + 4] = 0;
        Assert.False(StartingZoneFood.HasFoodAura(fields));
    }

    [Fact]
    public void CandidateIdentityRejectsChangedItemSlotOrStackBeforeUse()
    {
        StartingZoneFood.RequireFoodIdentity((22, 23, 3), (22, 23, 3));
        Assert.Throws<MockProtocolException>(() => StartingZoneFood.RequireFoodIdentity((24, 23, 3), (22, 23, 3)));
        Assert.Throws<MockProtocolException>(() => StartingZoneFood.RequireFoodIdentity((22, 24, 3), (22, 23, 3)));
        Assert.Throws<MockProtocolException>(() => StartingZoneFood.RequireFoodIdentity((22, 23, 2), (22, 23, 3)));
    }
}
