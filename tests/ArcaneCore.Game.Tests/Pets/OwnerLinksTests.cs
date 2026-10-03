using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>vmangos Unit::GetAffectingPlayer, GetCharmerOrOwnerPlayerOrPlayerItself and friends (Unit.cpp:4795-4834, Unit.h:1228-1260).</summary>
public sealed class OwnerLinksTests
{
    [Fact]
    public void ThePlayerBehindAUnit_IsItsOwner_ForPetsGuardiansMiniPetsAndTotems()
    {
        using var kit = new PetTestKit();
        (Player owner, _) = kit.AddPlayer(1);
        kit.Cast(owner, PetSpell);
        kit.Cast(owner, GuardianSpell);
        kit.Cast(owner, CritterSpell);
        kit.Cast(owner, FireTotemSpell);

        foreach (Creature summon in kit.Creatures.Creatures)
        {
            Assert.Same(owner, summon.GetAffectingPlayer());
            Assert.Same(owner, summon.GetCharmerOrOwnerPlayerOrSelf());
            Assert.Same(owner, summon.GetCharmerOrOwnerPlayer());
            Assert.Same(owner, summon.GetCharmerOrOwner());
            Assert.True(summon.IsCharmerOrOwnerPlayerOrPlayerItself);
        }

        // a player is its own affecting player and has no owner
        Assert.Same(owner, owner.GetAffectingPlayer());
        Assert.Same(owner, owner.GetCharmerOrOwnerPlayerOrSelf());
        Assert.Null(owner.GetCharmerOrOwnerPlayer());
        Assert.Same(owner, owner.GetCharmerOrOwnerOrSelf());
        Assert.True(owner.IsCharmerOrOwnerPlayerOrPlayerItself);
    }

    [Fact]
    public void AnOwnerlessCreatureActsForNobody_AndAWildSummonStaysOwnerless()
    {
        using var kit = new PetTestKit();
        (Player owner, _) = kit.AddPlayer(1);
        kit.Cast(owner, WildSpell);
        Creature wild = Assert.Single(kit.Creatures.Creatures);
        Creature plain = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(NpcCasterEntry)!, 1, 1, 83.5f, 0);

        foreach (Creature creature in new[] { wild, plain })
        {
            Assert.Null(creature.GetAffectingPlayer());
            Assert.Null(creature.GetCharmerOrOwnerPlayerOrSelf());
            Assert.Null(creature.GetCharmerOrOwner());
            Assert.Same(creature, creature.GetCharmerOrOwnerOrSelf());
            Assert.False(creature.IsCharmerOrOwnerPlayerOrPlayerItself);
        }
    }

    [Fact]
    public void APetOfACreatureThatAPlayerOwns_StillActsForThePlayer()
    {
        using var kit = new PetTestKit();
        (Player owner, _) = kit.AddPlayer(1);
        kit.Cast(owner, GuardianSpell);
        Creature guardian = Assert.Single(kit.Creatures.Creatures);

        // a guardian that itself summons a guardian: its pet's master is a creature owned by the player (Unit.cpp:4831)
        kit.Cast(guardian, GuardianSpell);
        Creature grandchild = kit.Creatures.Creatures.Single(c => c.OwnerGuid == guardian.Guid);

        Assert.Same(guardian, grandchild.GetCharmerOrOwner());
        Assert.Same(owner, grandchild.GetAffectingPlayer());
        Assert.Null(grandchild.GetCharmerOrOwnerPlayerOrSelf()); // its direct owner is a creature, not a player
        Assert.False(grandchild.IsCharmerOrOwnerPlayerOrPlayerItself);
    }

    [Fact]
    public void TheCharmerComesBeforeTheOwner_AndGetPetIsOnlyTheSummonedPet()
    {
        using var kit = new PetTestKit();
        (Player owner, _) = kit.AddPlayer(1);
        (Player other, _) = kit.AddPlayer(2, 40, 40);
        kit.Cast(owner, PetSpell);
        kit.Cast(owner, GuardianSpell);
        Creature pet = owner.GetPet()!;
        Assert.Equal(PetEntry, pet.Entry);
        Assert.Equal(SummonKind.Pet, pet.Summon!.Kind);
        Assert.Null(other.GetPet());

        pet.SetUInt64(UpdateFields.UnitFieldCharmedby, other.Guid.Value);
        Assert.Equal(other.Guid, pet.CharmerOrOwnerGuid);
        Assert.Same(other, pet.GetCharmerOrOwner());
        Assert.Same(other, pet.GetAffectingPlayer());
        Assert.Same(other, pet.GetCharmerOrOwnerPlayerOrSelf());
    }
}
