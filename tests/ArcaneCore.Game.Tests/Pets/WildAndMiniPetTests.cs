using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>SPELL_EFFECT_SUMMON_WILD (SpellEffects.cpp:2685-2773) and SPELL_EFFECT_SUMMON_CRITTER (SpellEffects.cpp:5400-5472).</summary>
public sealed class WildAndMiniPetTests
{
    private static Creature Only(PetTestKit kit, uint entry) => Assert.Single(kit.Creatures.Creatures, c => c.Entry == entry);

    [Fact]
    public void WildSummon_IsAUnitWithoutOwnerLinks_KeepsItsTemplateFaction_AndCarriesTheSpell()
    {
        using var kit = new PetTestKit();
        (Player caster, _) = kit.AddPlayer(1, 7, 8);
        caster.Orientation = 2.0f;

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(caster, WildSpell));

        Creature wild = Only(kit, WildEntry);
        Assert.Equal(HighGuid.Unit, wild.Guid.High);
        Assert.Equal(SummonKind.Wild, wild.Summon!.Kind);
        Assert.False(wild.IsPet);
        Assert.False(wild.IsTotem);

        // "UNIT_FIELD_CREATEDBY are not set for these kind of spells" (SpellEffects.cpp:2761)
        Assert.True(wild.OwnerGuid.IsEmpty);
        Assert.True(wild.CreatorGuid.IsEmpty);
        Assert.Equal(14u, wild.FactionTemplate);
        Assert.Equal(5, wild.Level);
        Assert.Equal(WildSpell, wild.GetUInt32(UpdateFields.UnitCreatedBySpell));

        // no radius, no destination: at the caster, facing the caster's way
        Assert.Equal((7f, 8f), (wild.X, wild.Y));
        Assert.Equal(2.0f, wild.Orientation, 3);

        // leaving the map does not take a wild summon along
        kit.Map.RemovePlayer(caster);
        Assert.Same(wild, kit.Creatures.FindCreature(wild.Guid));
    }

    [Fact]
    public void WildSummon_WithARadiusAndNoDestination_StandsThatFarInFrontOfTheCaster()
    {
        using var kit = new PetTestKit();
        (Player caster, _) = kit.AddPlayer(1, 10, 10);
        caster.Orientation = 0f;

        kit.Cast(caster, WildRadiusSpell);

        Creature wild = Only(kit, WildEntry);
        Assert.Equal(10 + caster.BoundingRadius + 4f, wild.X, 3);
        Assert.Equal(10f, wild.Y, 3);
    }

    [Fact]
    public void WildSummons_WithADestination_FirstOneThere_TheRestWithinTheRadius()
    {
        using var kit = new PetTestKit();
        (Player caster, _) = kit.AddPlayer(1);

        kit.Cast(caster, WildThreeSpell, new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = (30, 40, 83.5f) });

        List<Creature> wilds = [.. kit.Creatures.Creatures.OrderBy(c => c.Guid.Counter)];
        Assert.Equal(3, wilds.Count);
        Assert.Equal((30f, 40f), (wilds[0].X, wilds[0].Y));
        foreach (Creature other in wilds.Skip(1))
        {
            Assert.InRange(MathF.Sqrt(((other.X - 30) * (other.X - 30)) + ((other.Y - 40) * (other.Y - 40))), 0f, 5.001f);
        }
    }

    [Fact]
    public void WildSummon_IsKilledWhenItsDurationEnds_AndOnlyDiesWithoutOne()
    {
        using var kit = new PetTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        kit.Cast(caster, TimedWildSpell);
        Creature timed = Only(kit, WildEntry);
        kit.Run(800);
        Assert.True(timed.IsAlive);
        kit.Run(300);

        // TEMPSUMMON_TIMED_DEATH_AND_DEAD_DESPAWN: killed at the timer, its corpse then decays like any temporary summon
        Assert.False(timed.IsAlive);
        Assert.Equal(CreatureDeathState.Corpse, timed.DeathState);

        kit.Cast(caster, WildSpell);
        Creature forever = Assert.Single(kit.Creatures.Creatures, c => c.IsAlive);
        kit.Run(60_000);
        Assert.True(forever.IsAlive);
    }

    [Fact]
    public void MiniPet_IsOwnedByThePlayerButNotItsPet_KeepsTheTemplateLevelAndNpcFlags()
    {
        using var kit = new PetTestKit();
        (Player caster, _) = kit.AddPlayer(1, 3, 4);

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(caster, CritterSpell));

        Creature critter = Only(kit, MiniPetEntry);
        Assert.Equal(HighGuid.Pet, critter.Guid.High);
        Assert.Equal(SummonKind.MiniPet, critter.Summon!.Kind);
        Assert.True(critter.IsPet);
        Assert.Equal(caster.Guid, critter.OwnerGuid);
        Assert.Equal(caster.Guid, critter.CreatorGuid);
        Assert.Equal(caster.FactionTemplate, critter.FactionTemplate);
        Assert.Equal(CritterSpell, critter.GetUInt32(UpdateFields.UnitCreatedBySpell));
        Assert.Equal(5, critter.Level);
        Assert.Equal(2u, critter.NpcFlags);
        Assert.True(caster.PetGuid.IsEmpty);
        Assert.Same(critter, kit.Map.Pets!.MiniPetOf(caster));
        Assert.Equal((3f, 4f), (critter.X, critter.Y));

        // a pet can be summoned beside it
        kit.Cast(caster, PetSpell);
        Assert.False(caster.PetGuid.IsEmpty);
    }

    [Fact]
    public void MiniPet_SameEntryDismisses_AnotherEntryReplaces_AndNonPlayersSummonNothing()
    {
        using var kit = new PetTestKit();
        (Player caster, _) = kit.AddPlayer(1);

        kit.Cast(caster, CritterSpell);
        Creature first = Only(kit, MiniPetEntry);

        kit.Cast(caster, CritterSpell);
        Assert.Empty(kit.Creatures.Creatures);
        Assert.Null(kit.Map.Pets!.MiniPetOf(caster));

        kit.Cast(caster, CritterSpell);
        kit.Cast(caster, Critter2Spell);
        Creature second = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(MiniPetEntry2, second.Entry);
        Assert.NotSame(first, second);

        Creature npc = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(NpcCasterEntry)!, 0, 0, 83.5f, 0);
        kit.Cast(npc, CritterSpell);
        Assert.Equal(2, kit.Creatures.Creatures.Count); // the replaced mini pet and the npc
    }

    [Fact]
    public void MiniPet_WithADestinationStandsBesideThePlayerFacingIt()
    {
        using var kit = new PetTestKit();
        (Player caster, _) = kit.AddPlayer(1, 10, 10);
        caster.Orientation = 0.5f;

        kit.Cast(caster, CritterSpell, new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = (99, 99, 83.5f) });

        // SpellEffects.cpp:5433-5434: the destination is ignored, PET_FOLLOW_DIST at MINI_PET_SUMMON_ANGLE is used
        Creature critter = Only(kit, MiniPetEntry);
        float range = caster.BoundingRadius + PetConstants.FollowDistance + critter.BoundingRadius;
        float angle = 0.5f + PetConstants.MiniPetSummonAngle;
        Assert.Equal(10 + (range * MathF.Cos(angle)), critter.X, 3);
        Assert.Equal(10 + (range * MathF.Sin(angle)), critter.Y, 3);
        Assert.Equal(Creature.NormalizeOrientation(MathF.Atan2(10 - critter.Y, 10 - critter.X)), critter.Orientation, 3);
    }

    [Fact]
    public void MiniPet_IsDismissedWithItsOwner()
    {
        using var kit = new PetTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        kit.Cast(caster, CritterSpell);
        Assert.Single(kit.Creatures.Creatures);

        kit.Map.RemovePlayer(caster);

        Assert.Empty(kit.Creatures.Creatures);
    }
}
