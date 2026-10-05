using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Pets;

public sealed class SummonDeadPetTests
{
    [Fact]
    public void Effect109_RevivesRetainedCorpseUsingPercentageAndSameWorldIdentity()
    {
        using PetTestKit kit = new();
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(player, PetTestKit.PetSpell));
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        ObjectGuid guid = pet.Guid;
        kit.Creatures.KillCreature(pet);
        Assert.False(pet.IsAlive);

        Assert.True(kit.Service.SummonDeadPet(player, 50, kit.Spells.System));
        Assert.Same(pet, kit.Creatures.FindCreature(guid));
        Assert.True(pet.IsAlive);
        Assert.Equal(pet.MaxHealth / 2, pet.Health);
    }

    [Fact]
    public void Effect109_RejectsAlivePetAndNonHunterOwner()
    {
        using PetTestKit kit = new();
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Cast(player, PetTestKit.PetSpell);
        Assert.False(kit.Service.SummonDeadPet(player, 50, kit.Spells.System));
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warlock);
        Assert.False(kit.Service.SummonDeadPet(player, 50, kit.Spells.System));
    }
}
