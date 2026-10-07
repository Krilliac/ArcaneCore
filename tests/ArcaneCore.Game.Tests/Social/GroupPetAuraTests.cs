using ArcaneCore.Game;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

public sealed class GroupPetAuraTests
{
    [Fact]
    public void PartyMemberStats_SerializesPositiveAndNegativePetAuraMasksAndClears()
    {
        using var kit = new PetTestKit();
        (var owner, _) = kit.AddPlayer(1);
        kit.Cast(owner, PetTestKit.PetSpell);
        var pet = Assert.Single(kit.Creatures.Creatures);
        pet.SetUInt32(UpdateFields.UnitFieldAura, 49001);
        pet.SetUInt32(UpdateFields.UnitFieldAura + SpellSystem.MaxPositiveAuras, 49002);

        GroupUpdateFlags mask = GroupUpdateFlags.PetAuras | GroupUpdateFlags.PetAurasNegative;
        var reader = new PacketReader(GroupPackets.BuildPartyMemberStats(owner, mask));
        reader.ReadPackedGuid();
        Assert.Equal((uint)mask, reader.ReadUInt32());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal((ushort)49001, reader.ReadUInt16());
        Assert.Equal((ushort)1, reader.ReadUInt16());
        Assert.Equal((ushort)49002, reader.ReadUInt16());
    }
}
