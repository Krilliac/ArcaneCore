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

    /// <summary>A pathfinder that answers no path (or a straight path once <see cref="Route"/> is set) and counts its searches.</summary>
    private sealed class SwitchablePathfinder : ArcaneCore.Game.Maps.Collision.IPathfinder
    {
        public bool Route { get; set; }

        public int Calls { get; private set; }

        public int? LastMaxSearchNodes { get; private set; }

        public bool Enabled => true;

        public ArcaneCore.Game.Maps.Collision.PathResult FindPath(
            uint mapId, System.Numerics.Vector3 start, System.Numerics.Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
        {
            Calls++;
            LastMaxSearchNodes = options?.MaxSearchNodes;
            ArcaneCore.Game.Maps.Collision.PathSearchCounters.RecordSearch(1, 1, capped: false);
            return Route
                ? new ArcaneCore.Game.Maps.Collision.PathResult(ArcaneCore.Game.Maps.Collision.PathType.Normal, [start, end])
                : new ArcaneCore.Game.Maps.Collision.PathResult(ArcaneCore.Game.Maps.Collision.PathType.NoPath, []);
        }
    }

    [Fact]
    public void AWandererWhosePointsHaveNoPath_BacksOff_AndResumesWhenAPathExists()
    {
        // Live 2026-10-16: a wanderer that never reaches its points searched every 1-1.5 s for good, each search a full A*. The wait
        // doubles per failed leg in a row (1-1.5 s, 2-3, 4-6, 8-12, then 16-24 s), so a minute costs about 7 searches, not about 48.
        (WorldRuntime w, _, Creature wolf, _) = Start(Template());
        using WorldRuntime world = w;
        var paths = new SwitchablePathfinder();
        ArcaneCore.Game.Maps.Collision.WorldCollision.Of(world).Install(pathfinder: paths);
        var generator = Assert.IsType<RandomMovementGenerator>(wolf.Motion.Default);

        Run(world, 60_000, 50);

        Assert.False(wolf.IsMoving);
        Assert.InRange(paths.Calls, 4, 10);
        Assert.Equal(CreatureMapSystem.WanderMaxSearchNodes, paths.LastMaxSearchNodes); // a wander leg runs a small search
        Assert.True(generator.ConsecutiveNoPath >= 4);

        paths.Route = true;
        Run(world, 25_000, 50); // longer than the longest wait (24 s)
        Assert.NotEqual(30f, wolf.X); // a leg ran
        Assert.Equal(0, generator.ConsecutiveNoPath);
    }

    /// <summary>A pathfinder whose every path is incomplete and ends <see cref="Drop"/> yd below the start (a floor under the terrain).</summary>
    private sealed class DivingPathfinder : ArcaneCore.Game.Maps.Collision.IPathfinder
    {
        public float Drop { get; set; } = 115f;

        public int Calls { get; private set; }

        public bool Enabled => true;

        public ArcaneCore.Game.Maps.Collision.PathResult FindPath(
            uint mapId, System.Numerics.Vector3 start, System.Numerics.Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
        {
            Calls++;
            ArcaneCore.Game.Maps.Collision.PathSearchCounters.RecordSearch(1, 1, capped: false);
            return new ArcaneCore.Game.Maps.Collision.PathResult(
                ArcaneCore.Game.Maps.Collision.PathType.Incomplete, [start, new System.Numerics.Vector3(end.X, end.Y, start.Z - Drop)]);
        }
    }

    [Fact]
    public void AWanderLegOffTheMesh_ThatEndsAFloorBelow_IsSkipped_ButAShallowOneRuns()
    {
        // Live 2026-10-18: Lordaeron Citizen 3617 (spawn off the mesh over a terrain hole) took incomplete legs that ended on the
        // Undercity floor 115 yd below and walked down through the ground.
        (WorldRuntime w, _, Creature wolf, _) = Start(Template());
        using WorldRuntime world = w;
        var paths = new DivingPathfinder();
        ArcaneCore.Game.Maps.Collision.WorldCollision.Of(world).Install(pathfinder: paths);
        var generator = Assert.IsType<RandomMovementGenerator>(wolf.Motion.Default);
        float spawnZ = wolf.Z;

        Run(world, 20_000, 50);

        Assert.True(paths.Calls >= 2);
        Assert.False(wolf.IsMoving);
        Assert.Equal(spawnZ, wolf.Z, 0.01f);
        Assert.True(generator.ConsecutiveNoPath >= 2);

        paths.Drop = -90f; // raw terrain 90 yd above a cave (Grizzled Den) is also skipped
        float beforeX = wolf.X;
        Run(world, 5_000, 50);
        Assert.Equal(beforeX, wolf.X, 0.01f);

        paths.Drop = 3f; // a step down off a doodad onto the mesh is a leg
        Run(world, 25_000, 50);
        Assert.NotEqual(beforeX, wolf.X);
        Assert.Equal(0, generator.ConsecutiveNoPath);
    }

    private sealed class ArchedCaveMeshPathfinder : ArcaneCore.Game.Maps.Collision.IPathfinder, ArcaneCore.Game.Maps.Collision.IMeshFloorQuery
    {
        public bool Enabled => true;

        public ArcaneCore.Game.Maps.Collision.PathResult FindPath(
            uint mapId, System.Numerics.Vector3 start, System.Numerics.Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
        {
            ArcaneCore.Game.Maps.Collision.PathSearchCounters.RecordSearch(1, 1, capped: false);
            return new ArcaneCore.Game.Maps.Collision.PathResult(
                ArcaneCore.Game.Maps.Collision.PathType.Normal,
                [start, new System.Numerics.Vector3(start.X + 8f, start.Y, start.Z - 3.0f)]);
        }

        public bool TryGetMeshFloor(uint mapId, float x, float y, float z, float below, float above, out float height)
        {
            height = 83.5f;
            return height >= z - below && height <= z + above;
        }
    }

    [Fact]
    public void AWanderLegAcrossAnArchedCaveMesh_DrapesCornersToTheMeshFloor()
    {
        (WorldRuntime w, _, Creature wendigo, _) = Start(Template());
        using WorldRuntime world = w;
        var caveMesh = new ArchedCaveMeshPathfinder();
        ArcaneCore.Game.Maps.Collision.WorldCollision.Of(world).Install(pathfinder: caveMesh);

        Run(world, 6_000, 50);

        Assert.Equal(83.5f, wendigo.Z, 0.05f);
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
