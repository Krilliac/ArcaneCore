using ArcaneCore.Protocol;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// The vmangos leash: the soft leash (Creature::IsOutOfThreatArea, Objects/Creature.cpp:2796-2815, selected through
/// ThreatContainer::selectNextVictim, Threat/ThreatManager.cpp:305-312) and the template's hard leash (Creature::Update,
/// Objects/Creature.cpp:976-993). Positions and the clock are set directly; every expectation is derived from those sources.
/// </summary>
public sealed class LeashTests
{
    private sealed record Fight(WorldRuntime World, Map Map, CreatureMapSystem System, Player Player, Creature Wolf) : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    /// <summary>A scripted caster: chases to <see cref="CreatureAI.CasterChaseDistance"/> (a ranged chase with an offset).</summary>
    private sealed class CasterLeashAI : AggressorAI
    {
        public CasterLeashAI(Creature creature)
            : base(creature)
        {
            CasterChaseDistance = 20f;
        }
    }

    private static Fight Start(CreatureTemplate? template = null, CreatureOptions? options = null, bool caster = false)
    {
        CreatureContent content = Content([caster ? (template ?? Template()) with { AIName = "CasterLeashAI" } : template ?? Template()], [Spawn(1, WolfEntry, 5, 0)]);
        var factory = new CreatureAiFactory();
        factory.Register("CasterLeashAI", c => new CasterLeashAI(c));
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content,
            caster ? new CreatureAiServices { Hostility = new AlwaysHostile(), Factory = factory } : new CreatureAiServices { Hostility = new AlwaysHostile() }, options);
        (Player player, _) = AddPlayer(runtime, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        wolf.AI!.CombatMovement = false; // stay where the test puts it
        map.Combat.DealDamage(player, wolf, 1, direct: false);
        Assert.Same(player, wolf.Combat.Victim);
        return new Fight(runtime, map, system, player, wolf);
    }

    private static void MoveBoth(Fight f, float wolfX, float playerX)
    {
        f.Wolf.Relocate(wolfX, 0, 83.5f, 0, 0);
        f.Player.Relocate(playerX, 0, 83.5f, 0, 0);
    }

    [Fact]
    public void TheThreatRadiusDefaultsTo50()
        => Assert.Equal(50f, new CreatureOptions().ThreatRadius);

    [Fact]
    public void ACreatureInsideTheArea_DoesNotLeash_EvenWithItsVictimFarAway()
    {
        using Fight f = Start();

        MoveBoth(f, wolfX: 5, playerX: 90); // the victim ran 85 yd away, the creature stayed
        Run(f.World, 30000);

        Assert.False(f.Wolf.IsInEvadeMode);
        Assert.Same(f.Player, f.Wolf.Combat.Victim);
        Assert.False(f.System.IsOutOfThreatArea(f.Wolf, f.Player));
    }

    [Fact]
    public void AVictimInsideTheArea_DoesNotLeash_EvenWithTheCreatureFarAway()
    {
        using Fight f = Start();

        MoveBoth(f, wolfX: 90, playerX: 6);
        Run(f.World, 30000);

        Assert.False(f.Wolf.IsInEvadeMode);
        Assert.False(f.System.IsOutOfThreatArea(f.Wolf, f.Player));
    }

    [Fact]
    public void BothOutsideTheArea_LeashAfterTheTwelveSecondExtension_NotBefore()
    {
        using Fight f = Start();

        MoveBoth(f, wolfX: 100, playerX: 101);
        Run(f.World, 11000);
        Assert.False(f.Wolf.IsInEvadeMode);        // inside the 12 s since the first look outside the area

        Run(f.World, 3000);
        Assert.True(f.Wolf.IsInEvadeMode);         // the leash extension ran out: out of area, evade
        Assert.Null(f.Wolf.Combat.Victim);
    }

    [Fact]
    public void TheThreatAreaRadius_IsTheLargerOfThreatRadiusAndOneAndAHalfTimesTheAggroRadius()
    {
        // Detection 60: radius max(1.5 x ~61, 50) = 91.5, so both at 70 yd are still inside it and the creature never leashes.
        using Fight wide = Start(Template() with { Detection = 60f });
        MoveBoth(wide, wolfX: 70, playerX: 71);
        Run(wide.World, 20000);
        Assert.False(wide.Wolf.IsInEvadeMode);

        // Detection 18: radius 50, so both at 70 yd are outside.
        using Fight narrow = Start();
        MoveBoth(narrow, wolfX: 70, playerX: 71);
        Run(narrow.World, 16000);
        Assert.True(narrow.Wolf.IsInEvadeMode);
    }

    [Fact]
    public void NoLeashEvade_AndInstanceableMaps_NeverLeash()
    {
        using Fight flagged = Start(Template() with { ExtraFlags = 0x01, ExtraFlagsDialect = CreatureExtraFlagsDialect.VMangos });
        MoveBoth(flagged, wolfX: 100, playerX: 101);
        Run(flagged.World, 20000);
        Assert.False(flagged.Wolf.IsInEvadeMode);

        using Fight dungeon = Start();
        dungeon.Map.Combat.Hooks = new InstanceHooks();
        MoveBoth(dungeon, wolfX: 100, playerX: 101);
        Run(dungeon.World, 20000);
        Assert.False(dungeon.Wolf.IsInEvadeMode);
    }

    [Fact]
    public void ACrowdControlledCreature_KeepsRefreshingItsExtension_SoItCannotLeash()
    {
        using Fight f = Start();
        MoveBoth(f, wolfX: 100, playerX: 101);
        f.Wolf.UnitFlags |= UnitFlags.Stunned; // the 3 s check refreshes the extension clock (Creature.cpp:988-989)

        Run(f.World, 20000);
        Assert.False(f.Wolf.IsInEvadeMode);

        f.Wolf.UnitFlags &= ~UnitFlags.Stunned;
        Run(f.World, 9000);
        Assert.False(f.Wolf.IsInEvadeMode);
        Run(f.World, 7000);
        Assert.True(f.Wolf.IsInEvadeMode);
    }

    private static int LeashAge(Fight f) => (int)f.System.GetLeashFacts(f.Wolf, f.Player)!.Value.ClockAgeSeconds;

    [Fact]
    public void AChaserStandingInMeleeFarFromHome_RefreshesItsLeashEveryFiveSeconds_AndDoesNotEvade()
    {
        using Fight f = Start();
        MoveBoth(f, wolfX: 100, playerX: 101);
        f.Wolf.AI!.CombatMovement = true; // the chase generator runs: the wolf stands at its victim (vmangos m_leashExtensionTimer)
        f.System.ApplyCombatMovement(f.Wolf);
        Assert.True(MapCombat.CanReachWithMeleeAutoAttack(f.Wolf, f.Player));

        // Well past the 12 s extension, both beyond the 50 yd area (not 40 s: a fight with no damage exchanged is dropped at about 39 s).
        Run(f.World, 30000);

        Assert.False(f.Wolf.IsInEvadeMode);
        Assert.Same(f.Player, f.Wolf.Combat.Victim);
    }

    [Fact]
    public void AChaserThatIsKitedOutOfMelee_StillEvadesOnSchedule()
    {
        using Fight f = Start();
        MoveBoth(f, wolfX: 100, playerX: 101);
        f.Wolf.AI!.CombatMovement = true;
        f.System.ApplyCombatMovement(f.Wolf);

        Run(f.World, 3000); // standing in melee: the 5 s timer has not fired
        // The victim keeps stepping out of reach, so the chaser is always running after it and never stands long enough to refresh.
        for (int i = 0; i < 60 && !f.Wolf.IsInEvadeMode; i++)
        {
            f.Player.Relocate(f.Wolf.X + 8, 0, 83.5f, 0, 0);
            Run(f.World, 500);
        }

        Assert.True(f.Wolf.IsInEvadeMode);
    }

    [Fact]
    public void TheChaseLeashTimer_IsFiveSeconds_AndStartingANewPathRearmsIt()
    {
        // vmangos TargetedMovementGenerator.cpp:389-401, .h:107: the timer runs while the chase spline is finalized and is re-armed to 5000
        // when a new path starts. Driven in 100 ms world ticks so the 5 s boundary is exact; asserted on the leash clock's age.
        using Fight f = Start();
        MoveBoth(f, wolfX: 100, playerX: 101);
        f.Wolf.AI!.CombatMovement = true;
        f.System.ApplyCombatMovement(f.Wolf);
        Assert.Equal(MovementGeneratorType.Chase, f.Wolf.Motion.CurrentType);
        Assert.False(f.Wolf.IsMoving);

        Run(f.World, 4900); // 4.9 s standing: nothing yet (a refresh would drop the age to 0)
        Assert.True(LeashAge(f) >= 4);

        f.Player.Relocate(f.Wolf.X + 8, 0, 83.5f, 0, 0); // a new path starts: the timer is re-armed to 5000 (a short run: the 12 s leash must not expire first)
        Run(f.World, 100);
        Assert.True(f.Wolf.IsMoving);
        for (int i = 0; i < 100 && f.Wolf.IsMoving; i++)
        {
            Run(f.World, 100);
        }

        Assert.False(f.Wolf.IsMoving);
        Assert.True(MapCombat.CanReachWithMeleeAutoAttack(f.Wolf, f.Player));
        Run(f.World, 4800); // just under 5 s standing again since the move: still no refresh
        Assert.True(LeashAge(f) >= 9 && !f.Wolf.IsInEvadeMode, $"DBG age={LeashAge(f)} evade={f.Wolf.IsInEvadeMode}");

        Run(f.World, 300); // 5 s standing: the refresh
        Assert.Equal(0, LeashAge(f));
    }

    [Fact]
    public void ARangedCasterStandingAtItsRangeBeyondTheThreatArea_DoesNotEvade()
    {
        // vmangos refreshes for every chase movement generator whose spline is finalized, a ranged chase with an offset included.
        using Fight f = Start(caster: true);
        MoveBoth(f, wolfX: 100, playerX: 120);
        f.Wolf.AI!.CombatMovement = true;
        f.System.ApplyCombatMovement(f.Wolf);
        Assert.IsType<RangedMovementGenerator>(f.Wolf.Motion.Top);

        Run(f.World, 40000);

        Assert.IsType<RangedMovementGenerator>(f.Wolf.Motion.Top);
        Assert.False(f.Wolf.IsInEvadeMode);
        Assert.Same(f.Player, f.Wolf.Combat.Victim);
    }

    [Theory]
    [InlineData(HitKind.Damage)]
    [InlineData(HitKind.Absorbed)]
    [InlineData(HitKind.HostileSpell)]
    public void ACreatureThatIsHit_RefreshesItsLeash(HitKind kind)
    {
        // vmangos Unit::SetInCombatWithAggressor (Unit.cpp:5985-5987), reached by DealDamage (Unit.cpp:781-785, the absorb path :747-750)
        // and by a hostile spell hit (Spell.cpp:1252, 1675).
        using Fight f = Start();
        MoveBoth(f, wolfX: 100, playerX: 101);
        Run(f.World, 6000);
        Assert.True(LeashAge(f) >= 5);

        switch (kind)
        {
            case HitKind.Damage:
                f.Map.Combat.DealDamage(f.Player, f.Wolf, 1);
                break;
            case HitKind.Absorbed:
                f.Map.Combat.DealDamage(f.Player, f.Wolf, 0); // a fully absorbed swing reaches DealDamage with no damage
                break;
            default:
                new ArcaneCore.Game.Spells.MapCombatDamageSink().DealSpellDamage(
                    f.Player, f.Wolf, global::ArcaneCore.Game.Tests.Spells.SpellTestKit.Spell(990_001), 0, periodic: false);
                break;
        }

        Assert.Equal(0, LeashAge(f));
    }

    [Fact]
    public void AHitThatDoesNotLinkCombat_ATouchOnlyHit_DoesNotRefreshTheLeash()
    {
        // vmangos refreshes inside SetInCombatWithAggressor's !touchOnly branch (Unit.cpp:5984-5987); a hit that only touches the PvP flag
        // (a hunter trap's hit, Spell.cpp:1650/1688) does not link combat.
        using Fight f = Start();
        MoveBoth(f, wolfX: 100, playerX: 101);
        Run(f.World, 6000);

        f.Map.Combat.DealDamage(f.Player, f.Wolf, 1, startsCombat: false);

        Assert.True(LeashAge(f) >= 5);
    }

    [Fact]
    public void TheProductionMover_ImplementsExtendLeash_NotTheInterfaceDefaultNoOp()
    {
        using Fight f = Start();
        MoveBoth(f, wolfX: 100, playerX: 101);
        Run(f.World, 6000);
        Assert.True(LeashAge(f) >= 5);

        ((ICreatureMover)f.System).ExtendLeash(f.Wolf);

        Assert.Equal(0, LeashAge(f));
    }

    [Fact]
    public void ARootedCreature_KeepsRefreshingItsExtension_AtTheThreeSecondCheck()
    {
        // vmangos Creature::Update (Creature.cpp:986-989): UNIT_STATE_NO_FREE_MOVE includes the root.
        using Fight f = Start();
        MoveBoth(f, wolfX: 100, playerX: 101);
        f.Wolf.AddMovementFlags(MovementFlags.Root);

        Run(f.World, 30000);

        Assert.False(f.Wolf.IsInEvadeMode);
    }

    [Fact]
    public void ACreatureWithCombatMovementOff_AttackingInMelee_StillEvadesByTheNormalLeashRule()
    {
        // No chase generator runs, so nothing refreshes the clock: the 12 s extension runs out and the creature evades.
        using Fight f = Start();
        MoveBoth(f, wolfX: 100, playerX: 101);
        Assert.False(f.Wolf.AI!.CombatMovement);
        Assert.True(MapCombat.CanReachWithMeleeAutoAttack(f.Wolf, f.Player));

        Run(f.World, 11000);
        Assert.False(f.Wolf.IsInEvadeMode);
        Run(f.World, 3000);

        Assert.True(f.Wolf.IsInEvadeMode);
    }

    public enum HitKind
    {
        Damage,
        Absorbed,
        HostileSpell,
    }

    [Fact]
    public void TheTemplateLeash_EvadesAtTheThreeSecondCheck_WhenTheCreatureIsFarFromWhereTheFightBegan()
    {
        // creature_template.Leash is a hard leash polled every 3000 ms (tickTime % 3000 <= diff): 30 yd from the combat start.
        using Fight f = Start(Template() with { Leash = 30f });

        f.Wolf.Relocate(5 + 20, 0, 83.5f, 0, 0); // 20 yd from the fight's start: inside the leash
        Run(f.World, 7000);
        Assert.False(f.Wolf.IsInEvadeMode);

        f.Wolf.Relocate(5 + 40, 0, 83.5f, 0, 0);
        Run(f.World, 3100); // the next 3 s boundary falls inside this window
        Assert.True(f.Wolf.IsInEvadeMode);
    }

    [Fact]
    public void TheHardLeash_AddsTheCreaturesBoundingRadius()
    {
        // vmangos IsWithinDist3d (Object.cpp:1712-1721) defaults to SizeFactor::BoundingRadius: a 3 yd creature leashes past 33 yd.
        using Fight f = Start(Template() with { Leash = 30f });
        f.Wolf.SetFloat(UpdateFields.UnitFieldBoundingradius, 3f);

        f.Wolf.Relocate(5 + 32, 0, 83.5f, 0, 0);
        Run(f.World, 3100);
        Assert.False(f.Wolf.IsInEvadeMode);

        f.Wolf.Relocate(5 + 34, 0, 83.5f, 0, 0);
        Run(f.World, 3100);
        Assert.True(f.Wolf.IsInEvadeMode);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(7u)]
    [InlineData(333u)]
    [InlineData(1499u)]
    [InlineData(2999u)]
    [InlineData(3001u)]
    [InlineData(4500u)]
    [InlineData(9100u)]
    public void TheHardLeashCheck_IsNeverSkipped_WhateverTheTickLength(uint step)
    {
        // The clock advances by exactly the tick's diff before the check, so clock % 3000 <= diff holds on every tick that
        // reaches a multiple of 3000 (vmangos Creature.cpp:979 polls tickTime() % 3000 <= update_diff the same way): one window of
        // 3000 ms plus one tick always contains a check.
        using Fight f = Start(Template() with { Leash = 30f });
        f.Wolf.Relocate(5 + 40, 0, 83.5f, 0, 0);

        Run(f.World, 3000 + step, step);

        Assert.True(f.Wolf.IsInEvadeMode);
    }

    [Fact]
    public void TheLeashCheckInterval_IsConfigurable_AndZeroTurnsTheHardLeashOff()
    {
        using Fight off = Start(Template() with { Leash = 30f }, new CreatureOptions { LeashCheckIntervalMs = 0 });
        off.Wolf.Relocate(5 + 40, 0, 83.5f, 0, 0);
        Run(off.World, 10000);
        Assert.False(off.Wolf.IsInEvadeMode);
    }

    [Fact]
    public void CreaturesThatJoinThroughAnAssistanceCall_ShareTheCallersLeashClock()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 5, 0), Spawn(2, WolfEntry, 6, 1)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() },
            new CreatureOptions { AggroScanMode = AggroScanMode.Poll, AggroRate = 0 });
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature caller = system.Creatures.Single(c => c.Spawn!.Guid == 1);
        Creature helper = system.Creatures.Single(c => c.Spawn!.Guid == 2);

        map.Combat.DealDamage(player, caller, 1, direct: false);
        Run(world, 2500); // the assistance call executes after 1500 ms

        Assert.Same(player, helper.Combat.Victim);
        Assert.NotNull(caller.LeashClock);
        Assert.Same(caller.LeashClock, helper.LeashClock);
    }

    [Fact]
    public void TheClockIsClearedWhenCombatStops_SoTheNextFightGetsAFreshTwelveSeconds()
    {
        using Fight f = Start();
        MoveBoth(f, wolfX: 100, playerX: 101);
        Run(f.World, 14000);
        Assert.True(f.Wolf.IsInEvadeMode);

        Run(f.World, 14000); // 95 yd home at run speed
        Assert.False(f.Wolf.IsInEvadeMode);
        f.Map.Combat.DealDamage(f.Player, f.Wolf, 1, direct: false);
        f.Wolf.AI!.CombatMovement = false;
        MoveBoth(f, wolfX: 100, playerX: 101);

        Run(f.World, 11000);

        Assert.False(f.Wolf.IsInEvadeMode);
    }

    private sealed class InstanceHooks : CombatHooks
    {
        public override bool IsInstanceable(uint mapId) => true;
    }
}
