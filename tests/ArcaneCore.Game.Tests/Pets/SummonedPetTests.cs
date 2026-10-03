using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>SPELL_EFFECT_SUMMON through the production sink: vmangos Spell::EffectSummon and Pet::Update.</summary>
public sealed class SummonedPetTests
{
    private const uint TimedPetSpell = 910200;

    private static PetTestKit Kit() => new([Spell(TimedPetSpell, Effect(SpellEffectName.Summon, 0, misc: (int)PetEntry)) with
    {
        Duration = new SpellDuration(1_000, 0, 1_000),
    }]);

    [Fact]
    public void SummonEffect_CreatesAHighGuidPetLinkedToItsOwner()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 5, 6);
        caster.Orientation = 1.0f;
        Assert.True(kit.Service.CanSummon(caster, PetEntry));

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(caster, PetSpell));

        Creature pet = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(PetEntry, pet.Entry);
        Assert.Equal(HighGuid.Pet, pet.Guid.High);
        Assert.True(pet.IsPet);
        Assert.False(pet.IsTotem);
        Assert.Equal(SummonKind.Pet, pet.Summon!.Kind);

        // SpellEffects.cpp:2391-2397
        Assert.Equal(caster.Guid, pet.OwnerGuid);
        Assert.Equal(caster.Guid, pet.CreatorGuid);
        Assert.Equal(caster.FactionTemplate, pet.FactionTemplate);
        Assert.Equal(caster.Level, pet.Level);
        Assert.Equal(PetSpell, pet.GetUInt32(UpdateFields.UnitCreatedBySpell));
        Assert.Equal(0u, pet.NpcFlags);
        Assert.Equal(pet.Guid, caster.PetGuid);
        Assert.Equal(pet.Guid, caster.GetUInt64(UpdateFields.UnitFieldSummon) is var raw ? new ObjectGuid(raw) : default);

        // The pet appears at the caster's position facing -orientation (SpellEffects.cpp:2372).
        Assert.Equal((5f, 6f), (pet.X, pet.Y));
        Assert.Equal(Creature.NormalizeOrientation(-1.0f), pet.Orientation, 3);

        // Quest reward preflight still models the built-in effect, and a second pet is refused.
        Assert.True(kit.Spells.System.HasBuiltInEffectHandler(SpellEffectName.Summon));
        Assert.False(kit.Service.CanSummon(caster, PetEntry));
        kit.Cast(caster, PetSpell);
        Assert.Single(kit.Creatures.Creatures);
    }

    [Fact]
    public void Pet_IsUnsummonedWhenItsOwnerNoLongerHasIt_OrWhenFarAway_OrWhenTheOwnerDiesOutOfCombat()
    {
        using var kit = Kit();
        (Player a, _) = kit.AddPlayer(1);
        kit.Cast(a, PetSpell);
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        kit.Run(100);
        Assert.Same(pet, kit.Creatures.FindCreature(pet.Guid));

        // IsControlled() && !owner->GetPetGuid() (Pet.cpp:662-690)
        a.SetPetGuid(ObjectGuid.Empty);
        kit.Run(100);
        Assert.Null(kit.Creatures.FindCreature(pet.Guid));

        // beyond 120 yards
        a.SetPetGuid(ObjectGuid.Empty);
        kit.Cast(a, PetSpell);
        Creature second = Assert.Single(kit.Creatures.Creatures);
        a.SetPosition(119, 0, a.Z, 0);
        kit.Run(100);
        Assert.Same(second, kit.Creatures.FindCreature(second.Guid));
        a.SetPosition(125, 0, a.Z, 0);
        kit.Run(100);
        Assert.Null(kit.Creatures.FindCreature(second.Guid));
        Assert.True(a.PetGuid.IsEmpty);

        // dead owner, pet out of combat (Pet.cpp:696-701)
        a.SetPosition(0, 0, a.Z, 0);
        kit.Cast(a, PetSpell);
        Creature third = Assert.Single(kit.Creatures.Creatures);
        a.Health = 0;
        kit.Run(100);
        Assert.Null(kit.Creatures.FindCreature(third.Guid));
    }

    [Fact]
    public void Pet_IsUnsummonedWhenItsDurationEnds_AndAPetWithoutOneStays()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        kit.Cast(caster, TimedPetSpell);
        Creature timed = Assert.Single(kit.Creatures.Creatures);
        kit.Run(800);
        Assert.Same(timed, kit.Creatures.FindCreature(timed.Guid));
        kit.Run(300);
        Assert.Null(kit.Creatures.FindCreature(timed.Guid));
        Assert.True(caster.PetGuid.IsEmpty);

        kit.Cast(caster, PetSpell);
        Creature permanent = Assert.Single(kit.Creatures.Creatures);
        kit.Run(5_000);
        Assert.Same(permanent, kit.Creatures.FindCreature(permanent.Guid));
    }

    [Fact]
    public void PetThatDiesIsUnsummonedWhenItsCorpseDecays_AndTheOwnerCanSummonAgain()
    {
        // vmangos Pet::Update CORPSE: m_corpseDecayTimer <= diff -> Unsummon (Pet.cpp:677-686).
        using var kit = Kit();
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        kit.Cast(caster, PetSpell);
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(pet.Guid, caster.PetGuid);

        kit.Creatures.KillCreature(pet);
        Assert.Equal(CreatureDeathState.Corpse, pet.DeathState);
        kit.Run(600_000);

        Assert.Null(kit.Creatures.FindCreature(pet.Guid));
        Assert.True(caster.PetGuid.IsEmpty);
        Assert.True(caster.GetUInt64(UpdateFields.UnitFieldSummon) == 0);
        Assert.Empty(kit.Map.Pets!.Summons);
        Assert.True(kit.Service.CanSummon(caster, PetEntry));

        kit.Cast(caster, PetSpell);
        Creature second = Assert.Single(kit.Creatures.Creatures);
        Assert.NotEqual(pet.Guid, second.Guid);
        Assert.Equal(second.Guid, caster.PetGuid);
    }

    [Fact]
    public void OwnerLeavingTheMap_TakesItsPetWithIt()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        kit.Cast(caster, PetSpell);
        Assert.Single(kit.Creatures.Creatures);

        kit.Map.RemovePlayer(caster);

        Assert.Empty(kit.Creatures.Creatures);
        Assert.Empty(kit.Map.Pets!.Summons);
    }
}
