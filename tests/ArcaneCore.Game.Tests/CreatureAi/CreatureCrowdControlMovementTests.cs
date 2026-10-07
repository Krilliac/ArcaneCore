using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules.CrowdControl;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// Crowd control against the movement generators, after vmangos: a confuse buried under a pushed generator, a timed flight that
/// ends under a fear aura, stun and root holding the plain flight (FleeingMovementGenerator.cpp:164-169, 220-225) and the chase
/// (TargetedMovementGenerator.cpp:266-270).
/// </summary>
public sealed class CreatureCrowdControlMovementTests
{
    private static void Tick(WorldRuntime world, int ticks, uint step = 50)
    {
        for (int i = 0; i < ticks; i++)
        {
            world.RunTick(step);
        }
    }

    // --- a confuse buried under a pushed point -----------------------------------------------

    [Fact]
    public void ConfuseEndingUnderAPushedPoint_RemovesTheBuriedStagger_WithoutStoppingThePoint()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        wolf.UnitFlags |= UnitFlags.Confused;
        Tick(world, 4);
        Assert.Equal(MovementGeneratorType.Confused, wolf.Motion.CurrentType);

        wolf.Motion.MovePoint(1, wolf.X + 40f, wolf.Y, wolf.Z, run: true);
        world.RunTick(50);
        Assert.Equal([MovementGeneratorType.Confused, MovementGeneratorType.Point], wolf.Motion.ActiveTypes);
        Assert.True(wolf.IsMoving);
        float endX = wolf.Spline!.EndX;

        wolf.UnitFlags &= ~UnitFlags.Confused;
        world.RunTick(50);

        Assert.Equal([MovementGeneratorType.Point], wolf.Motion.ActiveTypes);
        Assert.True(wolf.IsMoving, "the buried stagger's Finish stopped the point's spline");
        Assert.Equal(endX, wolf.Spline!.EndX);
    }

    [Fact]
    public void ConfuseEndingUnderAFleeForAssistance_KeepsRunningToTheHelper_InsteadOfArrivingWhereItStands()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 5, 0), Spawn(2, WolfEntry, 28, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature caller = system.Creatures.Single(c => c.Spawn!.Guid == 1);
        Creature helper = system.Creatures.Single(c => c.Spawn!.Guid == 2);
        map.Combat.DealDamage(player, caller, 1, direct: false);
        caller.UnitFlags |= UnitFlags.Confused;
        world.RunTick(50);
        Assert.Equal(CrowdControlMovement.Confuse, caller.Motion.ActiveCrowdControl);

        system.FleeForAssistance(caller);
        Assert.Equal(MovementGeneratorType.Point, caller.Motion.CurrentType);
        world.RunTick(50);

        caller.UnitFlags &= ~UnitFlags.Confused; // the confuse ends while the creature is still ~20 yd from its helper
        world.RunTick(50);

        Assert.Null(helper.Combat.Victim);
        Assert.Equal(MovementGeneratorType.Point, caller.Motion.CurrentType); // a stopped spline reads as arrival: help called on the spot
        Assert.True(caller.IsMoving);
    }

    // --- a timed flight under a fear aura ----------------------------------------------------

    [Fact]
    public void TimedFlight_EndingWhileAFearAuraHoldsTheFlag_KeepsTheFlag_AndTheFearFlightTakesOver()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 5, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        wolf.Motion.MoveFleeing(player, 1000); // a critter or flee-for-assistance flight

        // A fear aura lands during the flight (CcState.RefreshFear): the flag is the aura's from now on.
        CcState.RememberFearSource(wolf, player);
        wolf.FearHeldByAura = true;
        wolf.UnitFlags |= UnitFlags.Fleeing;
        Tick(world, 30); // 1.5 s: the timed flight is over

        Assert.NotEqual(UnitFlags.None, wolf.UnitFlags & UnitFlags.Fleeing);
        Assert.Equal(CrowdControlMovement.Fear, wolf.Motion.ActiveCrowdControl);
        Assert.Equal(MovementGeneratorType.Fleeing, wolf.Motion.CurrentType);
    }

    [Fact]
    public void FearAura_OnACreature_RecordsThatTheAuraHoldsTheFleeingFlag()
    {
        const uint fearSpell = 930_101;
        using SpellTestKit kit = new(RuleTestSupport.Grant(fearSpell, AuraType.ModFear, 0));
        CreatureTemplate template = Template();
        var creature = new Creature(1, template, null, Content([template], []), new Random(1));

        RuleTestSupport.Apply(kit, creature, fearSpell);
        Assert.True(creature.FearHeldByAura);

        kit.System.RemoveAuras(creature, fearSpell);
        Assert.False(creature.FearHeldByAura);
    }

    // --- stun and root hold the plain flight and the chase -----------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlainFlight_StunnedOrRooted_StopsWhereItIs_AndRunsAgainWhenTheHoldLifts(bool stunned)
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 5, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        wolf.Motion.MoveFleeing(player, 0); // a critter's flight: until removed
        Tick(world, 2);
        Assert.True(wolf.IsMoving);

        if (stunned)
        {
            wolf.UnitFlags |= UnitFlags.Stunned;
        }
        else
        {
            wolf.AddMovementFlags(MovementFlags.Root);
        }

        world.RunTick(50);
        Assert.False(wolf.IsMoving);
        float x = wolf.X;
        float y = wolf.Y;
        Tick(world, 60); // 3 s: no new leg either
        Assert.False(wolf.IsMoving);
        Assert.Equal(x, wolf.X);
        Assert.Equal(y, wolf.Y);
        Assert.Equal(MovementGeneratorType.Fleeing, wolf.Motion.CurrentType);

        if (stunned)
        {
            wolf.UnitFlags &= ~UnitFlags.Stunned;
        }
        else
        {
            wolf.RemoveMovementFlags(MovementFlags.Root);
        }

        Tick(world, 30);
        Assert.True(wolf.X != x || wolf.Y != y, "the flight never ran again after the hold lifted");
    }

    [Fact]
    public void TimedFlight_DoesNotRunOutWhileStunned()
    {
        // vmangos TimedFleeingMovementGenerator::Update (FleeingMovementGenerator.cpp:215-232) returns before the flee timer
        // advances while the unit cannot move.
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 5, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        wolf.Motion.MoveFleeing(player, 1000);
        wolf.UnitFlags |= UnitFlags.Stunned;

        Tick(world, 40); // 2 s stunned

        Assert.Equal(MovementGeneratorType.Fleeing, wolf.Motion.CurrentType);
        wolf.UnitFlags &= ~UnitFlags.Stunned;
        Tick(world, 25);
        Assert.NotEqual(MovementGeneratorType.Fleeing, wolf.Motion.CurrentType);
    }

    [Fact]
    public void RootedCreature_DoesNotChase_UntilTheRootLifts()
    {
        // vmangos ChaseMovementGenerator::Update (TargetedMovementGenerator.cpp:266-270): UNIT_STATE_CAN_NOT_MOVE includes the root.
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 20, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        wolf.AddMovementFlags(MovementFlags.Root);

        wolf.Motion.MoveChase(player);
        Tick(world, 60);

        Assert.False(wolf.IsMoving);
        Assert.Equal(20f, wolf.X, 2);

        wolf.RemoveMovementFlags(MovementFlags.Root);
        Tick(world, 60);
        Assert.True(MapCombat.CanReachWithMeleeAutoAttack(wolf, player));
    }

    [Fact]
    public void Root_AppliedMidChase_StopsTheChaser()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        wolf.Motion.MoveChase(player);
        Tick(world, 2);
        Assert.True(wolf.IsMoving);

        wolf.AddMovementFlags(MovementFlags.Root);
        world.RunTick(50);
        Assert.False(wolf.IsMoving);
        float x = wolf.X;
        Tick(world, 20);
        Assert.Equal(x, wolf.X);
    }
}
