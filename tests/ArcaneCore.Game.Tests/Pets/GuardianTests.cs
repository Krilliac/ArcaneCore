using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>SPELL_EFFECT_SUMMON_GUARDIAN: vmangos Spell::EffectSummonGuardian (SpellEffects.cpp:2775-2914).</summary>
public sealed class GuardianTests
{
    private static List<Creature> GuardiansOf(PetTestKit kit, Unit owner) => [.. kit.Map.Pets!.GuardiansOf(owner)];

    [Fact]
    public void Guardians_AreHighGuidPetsLinkedToTheirOwner_ButNeverItsPet()
    {
        using var kit = new PetTestKit();
        (Player caster, _) = kit.AddPlayer(1, 5, 6);
        caster.Orientation = 1.0f;

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(caster, TwoGuardiansSpell));

        Assert.Equal(2, kit.Creatures.Creatures.Count);
        foreach (Creature guardian in kit.Creatures.Creatures)
        {
            Assert.Equal(HighGuid.Pet, guardian.Guid.High);
            Assert.Equal(SummonKind.Guardian, guardian.Summon!.Kind);
            Assert.True(guardian.IsPet);
            Assert.Equal(caster.Guid, guardian.OwnerGuid);
            Assert.Equal(caster.Guid, guardian.CreatorGuid);
            Assert.Equal(caster.FactionTemplate, guardian.FactionTemplate);
            Assert.Equal(TwoGuardiansSpell, guardian.GetUInt32(UpdateFields.UnitCreatedBySpell));

            // no destination: next to the caster, facing +orientation (SpellEffects.cpp:2865)
            Assert.Equal((5f, 6f), (guardian.X, guardian.Y));
            Assert.Equal(1.0f, guardian.Orientation, 3);

            // a player's guardian keeps the template level (SpellEffects.cpp:2810)
            Assert.Equal(5, guardian.Level);
        }

        // AddGuardian, not SetPet: UNIT_FIELD_SUMMON stays empty
        Assert.True(caster.PetGuid.IsEmpty);
        Assert.Equal(2, kit.Map.Pets!.GuardiansOf(caster, GuardianEntry).Count());

        // and a pet can be summoned beside them
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(caster, PetSpell));
        Assert.False(caster.PetGuid.IsEmpty);
    }

    [Fact]
    public void FollowAngle_GrowsWithTheNumberOfGuardiansAndPet()
    {
        using var kit = new PetTestKit();
        (Player caster, _) = kit.AddPlayer(1);

        kit.Cast(caster, GuardianSpell);
        kit.Cast(caster, GuardianSpell);
        kit.Cast(caster, PetSpell);
        kit.Cast(caster, GuardianSpell);

        // SpellEffects.cpp:2889-2897: PET_FOLLOW_ANGLE + pi/6 * (guardians + pet), only when there is one
        float[] angles = [.. GuardiansOf(kit, caster).Select(g => g.Summon!.FollowAngle)];
        Assert.Equal(3, angles.Length);
        Assert.Equal(MathF.PI / 2, angles[0], 4);
        Assert.Equal((MathF.PI / 2) + (MathF.PI / 6), angles[1], 4);
        Assert.Equal((MathF.PI / 2) + (MathF.PI / 6 * 3), angles[2], 4);
    }

    [Fact]
    public void SecondDirectCast_DismissesThePlayersGuardians_UnlessTheSpellHasADurationAndACategory()
    {
        using var kit = new PetTestKit();
        (Player caster, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.CastSpell(caster, GuardianSpell, SpellCastTargets.ForSelf(), triggered: false));
        Assert.Single(GuardiansOf(kit, caster));

        kit.Spells.Advance(2_000);
        Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.CastSpell(caster, GuardianSpell, SpellCastTargets.ForSelf(), triggered: false));
        Assert.Empty(GuardiansOf(kit, caster)); // found one, no cooldown: it only dismissed

        kit.Spells.Advance(2_000);
        kit.Spells.System.CastSpell(caster, GuardianSpell, SpellCastTargets.ForSelf(), triggered: false);
        Assert.Single(GuardiansOf(kit, caster));

        // a spell with a duration and a category (a cooldown spell) dismisses and summons anew
        kit.Spells.Advance(2_000);
        kit.Spells.System.CastSpell(caster, CooldownGuardianSpell, SpellCastTargets.ForSelf(), triggered: false);
        Assert.Single(GuardiansOf(kit, caster));
        Creature first = GuardiansOf(kit, caster)[0];
        kit.Spells.Advance(2_000);
        kit.Spells.System.CastSpell(caster, CooldownGuardianSpell, SpellCastTargets.ForSelf(), triggered: false);
        Creature second = Assert.Single(GuardiansOf(kit, caster));
        Assert.NotSame(first, second);
    }

    [Fact]
    public void TriggeredRecast_DoesNotDismiss()
    {
        using var kit = new PetTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        kit.Cast(caster, GuardianSpell);
        kit.Cast(caster, GuardianSpell);
        Assert.Equal(2, GuardiansOf(kit, caster).Count);
    }

    [Fact]
    public void NpcCaster_IsCappedAtFifteenPlusOneGuardiansOfTheEntry()
    {
        using var kit = new PetTestKit();
        Creature npc = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(NpcCasterEntry)!, 0, 0, 83.5f, 0);

        for (int i = 0; i < 20; i++)
        {
            kit.Cast(npc, GuardianSpell);
        }

        // SpellEffects.cpp:2806-2808: refused once the caster already has more than 15 of the entry
        Assert.Equal(16, kit.Map.Pets!.GuardiansOf(npc, GuardianEntry).Count());
    }

    [Fact]
    public void NpcCaster_UsesItsOwnLevelPlusANonPositiveEffectMultipleValue_OtherwiseTheTemplateLevel()
    {
        using var kit = new PetTestKit();
        Creature npc = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(NpcCasterEntry)!, 0, 0, 83.5f, 0);
        Assert.Equal(10, npc.Level);

        kit.Cast(npc, NpcRelativeLevelSpell); // MultipleValue -3: level 7
        Assert.Equal(7, Assert.Single(kit.Map.Pets!.GuardiansOf(npc)).Level);

        kit.Cast(npc, NpcFixedLevelSpell); // MultipleValue 2 > 0: the template's 5
        Assert.Equal([5, 7], kit.Map.Pets!.GuardiansOf(npc).Select(g => (int)g.Level).Order());

        // a player caster ignores EffectMultipleValue (SpellEffects.cpp:2812-2822)
        (Player player, _) = kit.AddPlayer(1);
        kit.Cast(player, NpcRelativeLevelSpell);
        Assert.Equal(5, Assert.Single(kit.Map.Pets!.GuardiansOf(player)).Level);
    }

    [Fact]
    public void WithADestination_TheFirstGuardianStandsThereFacingMinusOrientation_TheOthersWithinTheRadius()
    {
        using var kit = new PetTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        caster.Orientation = 1.0f;

        kit.Cast(caster, ThreeGuardiansSpell, new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = (50, 60, 83.5f) });

        List<Creature> guardians = GuardiansOf(kit, caster);
        Assert.Equal(3, guardians.Count);
        Creature first = guardians.OrderBy(g => g.Guid.Counter).First();
        Assert.Equal((50f, 60f), (first.X, first.Y));
        Assert.Equal(Creature.NormalizeOrientation(-1.0f), first.Orientation, 3);
        foreach (Creature other in guardians.Where(g => !ReferenceEquals(g, first)))
        {
            Assert.InRange(MathF.Sqrt(((other.X - 50) * (other.X - 50)) + ((other.Y - 60) * (other.Y - 60))), 0f, 5.001f);
            Assert.Equal(1.0f, other.Orientation, 3);
        }
    }

    [Fact]
    public void Guardian_EndsWithItsDuration_AndWhenItsOwnerLeavesTheMap()
    {
        using var kit = new PetTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        kit.Cast(caster, TimedGuardianSpell);
        Creature timed = Assert.Single(GuardiansOf(kit, caster));
        kit.Run(800);
        Assert.Same(timed, kit.Creatures.FindCreature(timed.Guid));
        kit.Run(300);
        Assert.Null(kit.Creatures.FindCreature(timed.Guid));

        kit.Cast(caster, GuardianSpell);
        Assert.Single(GuardiansOf(kit, caster));
        kit.Map.RemovePlayer(caster);
        Assert.Empty(kit.Creatures.Creatures);
    }
}
