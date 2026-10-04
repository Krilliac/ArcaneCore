using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules.CrowdControl;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// Fear and confuse movement of creatures: the map tick reads the unit flags the crowd-control auras own and runs the
/// crowd-control generator on the creature's MotionMaster (synthetic map, no navmesh: paths are straight lines). Fleeing
/// follows vmangos FleeingMovementGenerator; the stagger follows mangosserver ConfusedMovementGenerator.cpp.
/// </summary>
public sealed class FearConfuseMovementTests
{
    private const float CreatureX = 30f;

    private static (WorldRuntime World, CreatureMapSystem System, Creature Wolf, Player Caster) Start(byte movementType = 0, float wander = 0)
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, CreatureX, 0, movementType: movementType, wander: wander)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        (Player caster, _) = AddPlayer(world, 1, 20, 0); // 10 yd from the wolf: well inside the 28 yd quiet distance
        world.RunTick(50);
        return (world, system, Assert.Single(system.Creatures), caster);
    }

    private static void Tick(WorldRuntime world, int ticks, uint step = 50)
    {
        for (int i = 0; i < ticks; i++)
        {
            world.RunTick(step);
        }
    }

    private static float Distance(Unit a, Unit b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    [Fact]
    public void FearFlag_StartsAFlightThatRunsAwayFromTheCaster_EveryLeg()
    {
        (WorldRuntime w, _, Creature wolf, Player caster) = Start();
        using WorldRuntime world = w;
        CcState.RememberFearSource(wolf, caster);
        wolf.UnitFlags |= UnitFlags.Fleeing;
        float before = Distance(wolf, caster);

        world.RunTick(50);

        Assert.Equal(MovementGeneratorType.Fleeing, wolf.Motion.CurrentType);
        Assert.True(wolf.IsMoving);
        Assert.True(wolf.Spline!.Run);

        // A leg that starts nearer than the quiet distance (28 yd) is aimed away from the caster (within 45 degrees), so the gap
        // never closes over it; past it the creature mills about in any direction (vmangos rule). Sample the gap whenever a
        // leg has ended, for 10 s.
        float last = before;
        float farthest = before;
        for (int i = 0; i < 200; i++)
        {
            world.RunTick(50);
            if (!wolf.IsMoving)
            {
                float gap = Distance(wolf, caster);
                Assert.True(last >= FleeingMovementGenerator.MinQuietDistance || gap >= last - 0.01f, $"tick {i}: the gap closed from {last} to {gap}");
                last = gap;
                farthest = Math.Max(farthest, gap);
            }
        }

        Assert.True(farthest > before + 5f, $"the creature only got from {before} to {farthest} yd away");
    }

    [Fact]
    public void FearWithoutAKnownSource_StillFlees_InSomeDirection()
    {
        (WorldRuntime w, _, Creature wolf, _) = Start();
        using WorldRuntime world = w;
        wolf.UnitFlags |= UnitFlags.Fleeing;

        Tick(world, 40);

        Assert.Equal(MovementGeneratorType.Fleeing, wolf.Motion.CurrentType);
        Assert.True(Math.Abs(wolf.X - CreatureX) + Math.Abs(wolf.Y) > 1f);
    }

    [Fact]
    public void ClearingTheFearFlag_StopsTheFlight_AndTheDefaultResumes()
    {
        (WorldRuntime w, _, Creature wolf, Player caster) = Start(movementType: 1, wander: 5);
        using WorldRuntime world = w;
        Assert.Equal(MovementGeneratorType.Random, wolf.Motion.CurrentType);
        CcState.RememberFearSource(wolf, caster);
        wolf.UnitFlags |= UnitFlags.Fleeing;
        Tick(world, 5);
        Assert.Equal(MovementGeneratorType.Fleeing, wolf.Motion.CurrentType);
        Assert.Equal([MovementGeneratorType.Fleeing], wolf.Motion.ActiveTypes);

        wolf.UnitFlags &= ~UnitFlags.Fleeing;
        world.RunTick(50);

        Assert.Equal(MovementGeneratorType.Random, wolf.Motion.CurrentType);
        Assert.Empty(wolf.Motion.ActiveTypes);
        Assert.Equal(UnitFlags.None, wolf.UnitFlags & UnitFlags.Fleeing);
        Assert.False(wolf.IsMoving); // the flight's spline was stopped, and the random generator waits its first second
    }

    [Fact]
    public void ConfusedCreature_StaggersToRandomPointsAroundWhereItLostItsWits_AndNeverRuns()
    {
        (WorldRuntime w, _, Creature wolf, _) = Start();
        using WorldRuntime world = w;
        float anchorX = wolf.X;
        float anchorY = wolf.Y;
        wolf.UnitFlags |= UnitFlags.Confused;

        var legEnds = new HashSet<(int, int)>();
        for (int i = 0; i < 400; i++) // 20 s
        {
            world.RunTick(50);
            Assert.Equal(MovementGeneratorType.Confused, wolf.Motion.CurrentType);
            Assert.True(MathF.Sqrt(((wolf.X - anchorX) * (wolf.X - anchorX)) + ((wolf.Y - anchorY) * (wolf.Y - anchorY))) <= ConfusedMovementGenerator.StaggerRadius + 0.01f);
            if (wolf.Spline is { } spline)
            {
                Assert.False(spline.Run);
                legEnds.Add(((int)MathF.Round(spline.EndX), (int)MathF.Round(spline.EndY)));
            }
        }

        // A new point every 0.8-1.5 s: some fifteen destinations in 20 s, in different places.
        Assert.True(legEnds.Count >= 8, $"only {legEnds.Count} distinct stagger points");
        Assert.True(legEnds.Select(p => p.Item1).Distinct().Count() > 3);
    }

    [Fact]
    public void ClearingTheConfuseFlag_StopsTheStagger_AndThePreviousGeneratorResumes()
    {
        (WorldRuntime w, _, Creature wolf, Player caster) = Start();
        using WorldRuntime world = w;
        wolf.Motion.MoveChase(caster);
        Assert.Equal(MovementGeneratorType.Chase, wolf.Motion.CurrentType);

        wolf.UnitFlags |= UnitFlags.Confused;
        world.RunTick(50);
        Assert.Equal(MovementGeneratorType.Confused, wolf.Motion.CurrentType);
        Assert.Equal([MovementGeneratorType.Chase, MovementGeneratorType.Confused], wolf.Motion.ActiveTypes);
        Tick(world, 30);

        wolf.UnitFlags &= ~UnitFlags.Confused;
        world.RunTick(50);

        Assert.Equal(MovementGeneratorType.Chase, wolf.Motion.CurrentType);
        Assert.Equal([MovementGeneratorType.Chase], wolf.Motion.ActiveTypes);
        Assert.Same(caster, wolf.Motion.TargetedUnit);
    }

    [Fact]
    public void FearOverAChase_GivesTheChaseBack_WhenTheFlagClears()
    {
        (WorldRuntime w, _, Creature wolf, Player caster) = Start();
        using WorldRuntime world = w;
        wolf.Motion.MoveChase(caster);
        CcState.RememberFearSource(wolf, caster);

        wolf.UnitFlags |= UnitFlags.Fleeing;
        world.RunTick(50);
        Assert.Equal([MovementGeneratorType.Chase, MovementGeneratorType.Fleeing], wolf.Motion.ActiveTypes);

        wolf.UnitFlags &= ~UnitFlags.Fleeing;
        world.RunTick(50);
        Assert.Equal([MovementGeneratorType.Chase], wolf.Motion.ActiveTypes);
    }

    [Theory]
    [InlineData(UnitFlags.Fleeing, false)]
    [InlineData(UnitFlags.Fleeing, true)]
    [InlineData(UnitFlags.Confused, false)]
    [InlineData(UnitFlags.Confused, true)]
    public void StunnedOrRootedCreature_DoesNotMove_UntilTheHoldLifts(UnitFlags state, bool stunned)
    {
        (WorldRuntime w, _, Creature wolf, Player caster) = Start();
        using WorldRuntime world = w;
        CcState.RememberFearSource(wolf, caster);
        float x = wolf.X;
        float y = wolf.Y;
        if (stunned)
        {
            wolf.UnitFlags |= UnitFlags.Stunned;
        }
        else
        {
            wolf.AddMovementFlags(MovementFlags.Root);
        }

        wolf.UnitFlags |= state;
        Tick(world, 100); // 5 s

        Assert.False(wolf.IsMoving);
        Assert.Equal(x, wolf.X);
        Assert.Equal(y, wolf.Y);
        Assert.Equal(state == UnitFlags.Fleeing ? MovementGeneratorType.Fleeing : MovementGeneratorType.Confused, wolf.Motion.CurrentType);

        if (stunned)
        {
            wolf.UnitFlags &= ~UnitFlags.Stunned;
        }
        else
        {
            wolf.RemoveMovementFlags(MovementFlags.Root);
        }

        Tick(world, 60);
        Assert.True(wolf.X != x || wolf.Y != y, "the creature never moved after the hold lifted");
    }

    [Fact]
    public void RootAppliedMidLeg_StopsTheCreatureWhereItIs()
    {
        (WorldRuntime w, _, Creature wolf, Player caster) = Start();
        using WorldRuntime world = w;
        CcState.RememberFearSource(wolf, caster);
        wolf.UnitFlags |= UnitFlags.Fleeing;
        Tick(world, 4);
        Assert.True(wolf.IsMoving);

        wolf.AddMovementFlags(MovementFlags.Root);
        world.RunTick(50);

        Assert.False(wolf.IsMoving);
        float x = wolf.X;
        Tick(world, 40);
        Assert.Equal(x, wolf.X);
    }

    [Fact]
    public void BothFlags_FearWins_AndConfuseTakesOverWhenTheFearEnds()
    {
        (WorldRuntime w, _, Creature wolf, _) = Start();
        using WorldRuntime world = w;
        wolf.UnitFlags |= UnitFlags.Fleeing | UnitFlags.Confused;
        world.RunTick(50);
        Assert.Equal([MovementGeneratorType.Fleeing], wolf.Motion.ActiveTypes);

        wolf.UnitFlags &= ~UnitFlags.Fleeing;
        world.RunTick(50);

        Assert.Equal([MovementGeneratorType.Confused], wolf.Motion.ActiveTypes);
        Assert.Equal(UnitFlags.Confused, wolf.UnitFlags & (UnitFlags.Confused | UnitFlags.Fleeing));
    }

    [Fact]
    public void CombatMovement_DoesNotPushAChaseOverAConfusedCreature()
    {
        (WorldRuntime w, CreatureMapSystem system, Creature wolf, Player caster) = Start();
        using WorldRuntime world = w;
        wolf.UnitFlags |= UnitFlags.Confused;
        world.RunTick(50);
        system.Map.Combat.Attack(wolf, caster, melee: true);

        system.ApplyCombatMovement(wolf);

        Assert.Equal(MovementGeneratorType.Confused, wolf.Motion.CurrentType);
    }

    [Fact]
    public void ClearingTheStack_WhileTheAurasHoldTheFlag_StartsTheGeneratorAgain_AndKeepsTheFlag()
    {
        (WorldRuntime w, _, Creature wolf, Player caster) = Start();
        using WorldRuntime world = w;
        CcState.RememberFearSource(wolf, caster);
        wolf.UnitFlags |= UnitFlags.Fleeing;
        world.RunTick(50);

        wolf.Motion.Clear(); // an evade or a script drops the stack; Finish must not clear the aura-owned flag
        Assert.NotEqual(0u, (uint)(wolf.UnitFlags & UnitFlags.Fleeing));
        world.RunTick(50);

        Assert.Equal([MovementGeneratorType.Fleeing], wolf.Motion.ActiveTypes);
    }

    [Fact]
    public void ResettingTheMotionMaster_ForgetsTheCrowdControlGenerator()
    {
        (WorldRuntime w, _, Creature wolf, _) = Start();
        using WorldRuntime world = w;
        wolf.UnitFlags |= UnitFlags.Confused;
        world.RunTick(50);
        Assert.Equal(CrowdControlMovement.Confuse, wolf.Motion.ActiveCrowdControl);

        wolf.Motion.Reset();

        Assert.Equal(CrowdControlMovement.None, wolf.Motion.ActiveCrowdControl);
        Assert.Empty(wolf.Motion.ActiveTypes);
    }

    // --- the aura side: CcState ----------------------------------------------------------------

    private const uint FearSpell = 930_001;
    private const uint RootSpell = 930_002;
    private const uint StunSpell = 930_003;

    private static SpellTestKit AuraKit() => new(
        RuleTestSupport.Grant(FearSpell, AuraType.ModFear, 0),
        RuleTestSupport.Grant(RootSpell, AuraType.ModRoot, 0),
        RuleTestSupport.Grant(StunSpell, AuraType.ModStun, 0));

    private static Creature LooseWolf()
    {
        CreatureTemplate template = Template();
        return new Creature(1, template, null, Content([template], []), new Random(1));
    }

    [Fact]
    public void RootAndStunAuras_SetAndClearTheCreatureRootFlag_UntilTheLastOneIsGone()
    {
        using SpellTestKit kit = AuraKit();
        Creature creature = LooseWolf();

        RuleTestSupport.Apply(kit, creature, RootSpell);
        Assert.True(creature.Movement.HasFlag(MovementFlags.Root));
        RuleTestSupport.Apply(kit, creature, StunSpell);
        kit.System.RemoveAuras(creature, RootSpell);
        Assert.True(creature.Movement.HasFlag(MovementFlags.Root)); // the stun still roots it
        kit.System.RemoveAuras(creature, StunSpell);
        Assert.False(creature.Movement.HasFlag(MovementFlags.Root));
    }

    [Fact]
    public void FearAura_OnACreature_SetsAndClearsTheFlagTheMovementHookReads()
    {
        using SpellTestKit kit = AuraKit();
        Creature creature = LooseWolf();

        RuleTestSupport.Apply(kit, creature, FearSpell);
        Assert.NotEqual(0u, (uint)(creature.UnitFlags & UnitFlags.Fleeing));
        Assert.Null(CcState.FearSource(creature)); // it feared itself: nothing to run from

        kit.System.RemoveAuras(creature, FearSpell);
        Assert.Equal(UnitFlags.None, creature.UnitFlags & UnitFlags.Fleeing);
    }

    [Fact]
    public void FearSource_IsTheLatestCaster_NeverTheCreatureItself()
    {
        using SpellTestKit kit = AuraKit();
        Creature creature = LooseWolf();
        (Player caster, _) = kit.AddPlayer(1);

        CcState.RememberFearSource(creature, caster);
        Assert.Same(caster, CcState.FearSource(creature));

        CcState.RememberFearSource(creature, creature);
        Assert.Null(CcState.FearSource(creature));
        CcState.RememberFearSource(creature, caster);
        CcState.RememberFearSource(creature, null);
        Assert.Null(CcState.FearSource(creature));
    }
}
