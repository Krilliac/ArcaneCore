using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Threat;

/// <summary>vmangos CritterAI (AI/CritterAI.cpp:16-60): critters never attack, flee for 30 s when hurt, and go home after 30 s of combat.</summary>
public sealed class CritterAiTests
{
    private const uint Debuff = 960001;
    private const uint Buff = 960002;
    private const uint Bolt = 960003;

    private static ThreatArena Arena() => new(
        Spell(Debuff, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)) with { Duration = new SpellDuration(60000, 0, 60000), RangeIndex = 4, Range = new SpellRange(0, 30), SpellVisual = 1 },
        Spell(Buff, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, AuraType.Dummy)) with { Duration = new SpellDuration(60000, 0, 60000), RangeIndex = 4, Range = new SpellRange(0, 30), SpellVisual = 1 },
        Spell(Bolt, Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.UnitEnemy)) with { School = SpellSchool.Fire, RangeIndex = 4, Range = new SpellRange(0, 30) });

    private static Creature Critter(ThreatArena a, uint guid = 91, uint? health = null)
    {
        Creature critter = a.SpawnCreature(guid, 3, t => t with { CreatureType = 8 });
        if (health is { } hp)
        {
            critter.Health = hp;
        }

        return critter;
    }

    [Fact]
    public void ACritterTemplateGetsCritterAi_OthersDoNot_AndAnExplicitNameWins()
    {
        using ThreatArena a = Arena();
        Assert.IsType<CritterAI>(Critter(a).AI);
        Assert.IsType<AggressorAI>(a.Wolf.AI);

        Creature named = a.SpawnCreature(92, 5, t => t with { AIName = "AggressorAI", CreatureType = 8 });
        Assert.IsType<AggressorAI>(named.AI);
        Creature explicitCritter = a.SpawnCreature(93, 6, t => t with { AIName = "CritterAI" });
        Assert.IsType<CritterAI>(explicitCritter.AI);
    }

    [Fact]
    public void ACritterNeverAttacksOnSight_NorFightsBack()
    {
        using ThreatArena a = Arena();
        Creature critter = Critter(a);

        a.Systems[^1].CallAiMoveInLineOfSight(critter, a.Tank);
        Assert.Null(critter.Combat.Victim);
        Assert.False(critter.AI!.AggroesOnSight);

        Assert.False(critter.AI.AttackStart(a.Tank));
        a.Map.Combat.DealDamage(a.Tank, critter, 1, direct: false);
        Assert.Null(critter.Combat.Victim);
    }

    [Fact]
    public void ANonLethalHit_MakesItFleeForThirtySeconds_AndAfterThirtySecondsOfCombatItGoesHome()
    {
        using ThreatArena a = Arena();
        Creature critter = Critter(a);

        a.Map.Combat.DealDamage(a.Tank, critter, 1, direct: false);

        Assert.Equal(MovementGeneratorType.Fleeing, critter.Motion.CurrentType);
        Assert.True(critter.Combat.IsInCombat);

        Run(a.Kit.World, 29000);
        Assert.True(critter.Combat.IsInCombat);

        Run(a.Kit.World, 3000);
        Assert.False(critter.Combat.IsInCombat); // evaded after the 30 s combat timer
        Assert.NotEqual(MovementGeneratorType.Fleeing, critter.Motion.CurrentType);
    }

    [Fact]
    public void AnotherHit_RestartsTheCombatTimer()
    {
        using ThreatArena a = Arena();
        Creature critter = Critter(a);
        a.Map.Combat.DealDamage(a.Tank, critter, 1, direct: false);
        Run(a.Kit.World, 20000);

        a.Map.Combat.DealDamage(a.Tank, critter, 1, direct: false);
        Run(a.Kit.World, 20000); // 40 s since the first hit, 20 s since the second

        Assert.True(critter.Combat.IsInCombat);
    }

    [Fact]
    public void ALethalHit_DoesNotMakeItFlee()
    {
        using ThreatArena a = Arena();
        Creature critter = Critter(a, health: 5);

        a.Map.Combat.DealDamage(a.Tank, critter, 5, direct: false);

        Assert.False(critter.IsAlive);
        Assert.NotEqual(MovementGeneratorType.Fleeing, critter.Motion.CurrentType);
    }

    [Fact]
    public void AHostileSpellThatDealsNoDirectDamage_MakesItFlee_APositiveOneDoesNot()
    {
        using ThreatArena a = Arena();
        Creature buffed = Critter(a);
        Assert.Equal(SpellCastResult.CastOk, a.Cast(a.Healer, buffed, Buff));
        Assert.NotEqual(MovementGeneratorType.Fleeing, buffed.Motion.CurrentType);

        Creature debuffed = Critter(a, 94);
        Assert.Equal(SpellCastResult.CastOk, a.Cast(a.Tank, debuffed, Debuff));

        Assert.Equal(MovementGeneratorType.Fleeing, debuffed.Motion.CurrentType);
    }
}
