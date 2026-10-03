using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureMovement;

/// <summary>
/// vmangos RandomMovementGenerator beyond its timing (RandomMovementGenerator.cpp:26-67, 109-144): run/walk by the dialect-decoded
/// flags, the reset position evade consults, and the casting / cannot-move gates. The timing facts (1 s first move, 50 ms steps,
/// 4-10 s pauses) stay covered by CreatureTests.RandomMovement_* and CreatureMotionTests.
/// </summary>
public sealed class RandomWanderTests
{
    private static (WorldRuntime World, CreatureMapSystem System, Creature Wolf, FakeCaster Spells) Start(
        CreatureTemplate template, float wander = 5f, CreatureOptions? options = null)
    {
        CreatureContent content = Content([template], [Spawn(1, WolfEntry, 30, 0, movementType: 1, wander: wander)]);
        var spells = new FakeCaster();
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = spells }, options);
        AddPlayer(world, 1, 0, 0);
        return (world, system, Assert.Single(system.Creatures), spells);
    }

    /// <summary>Run until the wolf's first spline starts (vmangos: 1000 ms after spawn); fails when it never does.</summary>
    private static void RunUntilFirstLeg(WorldRuntime world, Creature wolf)
    {
        for (int i = 0; i < 100 && !wolf.IsMoving; i++)
        {
            world.RunTick(50);
        }

        Assert.True(wolf.IsMoving, "the wanderer never started a leg");
    }

    [Fact]
    public void AlwaysRun_InTheVmangosDialect_MakesTheLegsRun_OtherwiseTheyWalk()
    {
        (WorldRuntime w1, _, Creature walker, _) = Start(Template());
        using WorldRuntime walkWorld = w1;
        RunUntilFirstLeg(walkWorld, walker);
        Assert.False(walker.Spline!.Run);
        Assert.True(walker.Movement.HasFlag(MovementFlags.WalkMode));

        (WorldRuntime w2, _, Creature runner, _) = Start(Template() with { ExtraFlags = 0x40, ExtraFlagsDialect = CreatureExtraFlagsDialect.VMangos });
        using WorldRuntime runWorld = w2;
        RunUntilFirstLeg(runWorld, runner);
        Assert.True(runner.Spline!.Run);
        Assert.False(runner.Movement.HasFlag(MovementFlags.WalkMode));

        // 0x40 means nothing in the cmangos dialect (and is undecoded when the dialect is unknown): those creatures walk.
        (WorldRuntime w3, _, Creature cmangos, _) = Start(Template() with { ExtraFlags = 0x40, ExtraFlagsDialect = CreatureExtraFlagsDialect.CMangos });
        using WorldRuntime cmangosWorld = w3;
        RunUntilFirstLeg(cmangosWorld, cmangos);
        Assert.False(cmangos.Spline!.Run);
    }

    [Theory]
    [InlineData(100u, true)]
    [InlineData(0u, false)]
    public void RunDuringWander_IsAChanceDrawnPerLeg_OnlyInTheCmangosDialect(uint chancePercent, bool expectedRun)
    {
        var options = new CreatureOptions();
        options.Movement.RunDuringWanderChancePercent = chancePercent; // cmangos RandomMovementGenerator.cpp:136 draws urand(0,99) >= 15 for a walk
        (WorldRuntime w, _, Creature wolf, _) = Start(Template() with { ExtraFlags = 0x20, ExtraFlagsDialect = CreatureExtraFlagsDialect.CMangos }, options: options);
        using WorldRuntime world = w;

        RunUntilFirstLeg(world, wolf);

        Assert.Equal(expectedRun, wolf.Spline!.Run);

        // The same bit in the vmangos dialect is NO_MOVEMENT_PAUSE, not a run chance.
        (WorldRuntime w2, _, Creature other, _) = Start(Template() with { ExtraFlags = 0x20, ExtraFlagsDialect = CreatureExtraFlagsDialect.VMangos }, options: options);
        using WorldRuntime world2 = w2;
        RunUntilFirstLeg(world2, other);
        Assert.False(other.Spline!.Run);
    }

    [Fact]
    public void ResetPosition_IsTheCurrentPositionWithinTheWanderDistance_ElseTheSpawnPoint()
    {
        (WorldRuntime w, _, Creature wolf, _) = Start(Template(), wander: 5f);
        using WorldRuntime world = w;
        ICreatureMovementGenerator generator = new RandomMovementGenerator();

        wolf.Relocate(32f, 1f, 83.5f, 0f, 0); // 2.2 yd from the spawn (30,0): inside the 5 yd wander
        CreatureHome inside = Assert.IsType<CreatureHome>(generator.GetResetPosition(wolf));
        Assert.Equal((32f, 1f, 83.5f), (inside.X, inside.Y, inside.Z));

        wolf.Relocate(40f, 0f, 83.5f, 0f, 0); // 10 yd away
        CreatureHome outside = Assert.IsType<CreatureHome>(generator.GetResetPosition(wolf));
        Assert.Equal((30f, 0f, 83.5f), (outside.X, outside.Y, outside.Z));
    }

    [Fact]
    public void Evade_FromInsideTheWanderDisc_StaysWhereItIs_AndFromOutsideRunsToTheSpawn()
    {
        (WorldRuntime w, CreatureMapSystem system, Creature wolf, _) = Start(Template(), wander: 5f);
        using WorldRuntime world = w;

        wolf.Relocate(32f, 1f, 83.5f, 0f, 0);
        system.EnterEvadeMode(wolf);
        Run(world, 400, 50); // short: the wanderer resumes its first leg a second after the home move ends
        Assert.False(wolf.IsInEvadeMode);
        Assert.InRange(Distance2D(wolf, 32f, 1f), 0f, 0.5f); // no run back to the spawn point

        wolf.Relocate(45f, 0f, 83.5f, 0f, 0);
        system.EnterEvadeMode(wolf);
        Run(world, 8000);
        Assert.False(wolf.IsInEvadeMode);
        Assert.InRange(Distance2D(wolf, 30f, 0f), 0f, 5.5f); // home, then wandering again inside the disc
    }

    [Fact]
    public void ACastingWanderer_StandsStill_AndTheTimerDoesNotRun_UntilTheCastEnds()
    {
        (WorldRuntime w, _, Creature wolf, FakeCaster spells) = Start(Template());
        using WorldRuntime world = w;
        RunUntilFirstLeg(world, wolf);
        Assert.True(wolf.IsMoving);

        spells.Casting = true;
        world.RunTick(50);
        Assert.False(wolf.IsMoving); // UpdateAsync: IsNoMovementSpellCasted -> StopMoving (RandomMovementGenerator.cpp:118-121)

        // Far longer than any pause (max 10 s) plus the steps: no leg may start while it casts.
        Run(world, 15000, 250);
        Assert.False(wolf.IsMoving);

        spells.Casting = false;
        Run(world, 12000, 50);
        Assert.NotEqual(30f, wolf.X); // it did move after the cast
    }

    [Fact]
    public void AStunnedWanderer_DoesNotStartLegs_AndMovesAgainTheTickItRecovers()
    {
        (WorldRuntime w, _, Creature wolf, _) = Start(Template());
        using WorldRuntime world = w;
        wolf.UnitFlags |= UnitFlags.Stunned;

        Run(world, 15000, 250);
        Assert.False(wolf.IsMoving);
        Assert.Equal(30f, wolf.X);

        // vmangos zeroes the move timer while it cannot move (RandomMovementGenerator.cpp:113-117): the first free tick moves.
        wolf.UnitFlags &= ~UnitFlags.Stunned;
        world.RunTick(50);
        world.RunTick(50);
        Assert.True(wolf.IsMoving);
    }

    [Fact]
    public void TheGeneratorTypeNumbers_AreVmangos()
    {
        // vmangos Movement/MotionMaster.h:36-59.
        Assert.Equal(
            [0, 1, 2, 6, 7, 9, 10, 15],
            new[]
            {
                MovementGeneratorType.Idle, MovementGeneratorType.Random, MovementGeneratorType.Waypoint, MovementGeneratorType.Chase,
                MovementGeneratorType.Home, MovementGeneratorType.Point, MovementGeneratorType.Fleeing, MovementGeneratorType.Follow,
            }.Select(t => (int)t));
    }
}
