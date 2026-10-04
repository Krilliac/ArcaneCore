using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

public sealed class GroupMemberStatsPacketTests
{
    [Fact]
    public void FullStats_IncludeTheCurrentPetAndItsAuras()
    {
        using var kit = new PetTestKit();
        (Player owner, _) = kit.AddPlayer(1);
        kit.Cast(owner, PetTestKit.PetSpell);
        var pet = owner.GetPet()!;
        pet.SetUInt32(UpdateFields.UnitFieldAura, 1234);
        pet.SetUInt32(UpdateFields.UnitFieldAura + 32, 4321);

        var reader = new PacketReader(GroupPackets.BuildPartyMemberStats(owner, GroupUpdateFlags.Full));
        Assert.Equal(owner.Guid.Value, reader.ReadPackedGuid());
        Assert.Equal((uint)GroupUpdateFlags.Full, reader.ReadUInt32());
        reader.Skip(1 + 2 + 2 + 1 + 2 + 2 + 2 + 2 + 2 + 2);
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal((ushort)0, reader.ReadUInt16());
        Assert.Equal(pet.Guid.Value, reader.ReadUInt64());
        Assert.Equal(pet.Template.Name, reader.ReadCString());
        Assert.Equal((ushort)pet.DisplayId, reader.ReadUInt16());
        Assert.Equal((ushort)pet.Health, reader.ReadUInt16());
        Assert.Equal((ushort)pet.MaxHealth, reader.ReadUInt16());
        Assert.Equal((byte)pet.PowerType, reader.ReadByte());
        reader.Skip(2 + 2);
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal((ushort)1234, reader.ReadUInt16());
        Assert.Equal((ushort)1, reader.ReadUInt16());
        Assert.Equal((ushort)4321, reader.ReadUInt16());
        Assert.Equal(0, reader.Remaining);
    }
}
