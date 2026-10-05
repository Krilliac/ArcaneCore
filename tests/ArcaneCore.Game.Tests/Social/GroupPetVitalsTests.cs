using ArcaneCore.Game.Groups;
using ArcaneCore.Game;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

public sealed class GroupPetVitalsTests
{
    [Fact]
    public void PartyMemberStats_StaleCurrentPetGuidSerializesEmptyPet()
    {
        using var kit = new PetTestKit();
        (var owner, _) = kit.AddPlayer(1);
        owner.SetPetGuid(new ObjectGuid(0x1234));

        var reader = new PacketReader(GroupPackets.BuildPartyMemberStats(owner, GroupUpdateFlags.PetGuid | GroupUpdateFlags.PetName));
        reader.ReadPackedGuid();
        Assert.Equal((uint)(GroupUpdateFlags.PetGuid | GroupUpdateFlags.PetName), reader.ReadUInt32());
        Assert.Equal(0ul, reader.ReadUInt64());
        Assert.Equal(string.Empty, reader.ReadCString());
    }

    [Fact]
    public void PartyMemberStats_UsesLivePetBodyFieldsAndUncheckedU16Widths()
    {
        using var kit = new PetTestKit();
        (var owner, _) = kit.AddPlayer(1);
        kit.Cast(owner, PetTestKit.PetSpell);
        var pet = Assert.Single(kit.Creatures.Creatures);
        pet.Health = 70_000;
        pet.MaxHealth = 70_000;
        pet.SetUInt32(UpdateFields.UnitFieldDisplayid, 321);
        pet.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        pet.SetUInt32(UpdateFields.UnitFieldPower1, 30);
        pet.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);

        GroupUpdateFlags mask = GroupUpdateFlags.PetGuid | GroupUpdateFlags.PetModelId
            | GroupUpdateFlags.PetCurrentHp | GroupUpdateFlags.PetMaxHp | GroupUpdateFlags.PetPowerType
            | GroupUpdateFlags.PetCurrentPower | GroupUpdateFlags.PetMaxPower;
        var reader = new PacketReader(GroupPackets.BuildPartyMemberStats(owner, mask));
        reader.ReadPackedGuid();
        Assert.Equal((uint)mask, reader.ReadUInt32());
        Assert.Equal(pet.Guid.Value, reader.ReadUInt64());
        Assert.Equal((ushort)321, reader.ReadUInt16());
        Assert.Equal(unchecked((ushort)70_000), reader.ReadUInt16());
        Assert.Equal(unchecked((ushort)70_000), reader.ReadUInt16());
        Assert.Equal((byte)pet.PowerType, reader.ReadByte());
        Assert.Equal((ushort)30, reader.ReadUInt16());
        Assert.Equal((ushort)100, reader.ReadUInt16());
    }
}
