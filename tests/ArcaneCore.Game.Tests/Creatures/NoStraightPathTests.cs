using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Tests.Collision;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// No creature motion walks a straight line through geometry for want of a path (vmangos: TargetedMovementGenerator.cpp:188-189,
/// FleeingMovementGenerator.cpp:54-63, HomeMovementGenerator.cpp:58-71). Over a mesh tile with a house-shaped hole, a chase toward a target
/// inside the house stands and evades, flee and wander wait, home teleports, a spent search budget defers, and a scripted move paths around
/// the hole. A map with no mesh data still moves straight, and an authored route (point, waypoint) with no path still goes straight.
/// </summary>
public sealed partial class NoStraightPathTests
{
    private const uint MapId = 0;

    /// <summary>20×20 cells of 2 yd over world [0, 40)² with a hole of 4×4 cells at world [16, 24)² (a house).</summary>
    private static CellTile House() => new(0, 0, 0, 0, 20, 20, Size: 2) { Walkable = (i, j) => !(i is >= 8 and < 12 && j is >= 8 and < 12) };

    private static bool InHouse(float x, float y) => x > 16.05f && x < 23.95f && y > 16.05f && y < 23.95f;

    private static bool CrossesHouse(Vector3 a, Vector3 b)
    {
        for (int i = 0; i <= 64; i++)
        {
            Vector3 p = Vector3.Lerp(a, b, i / 64f);
            if (InHouse(p.X, p.Y))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class CountingNoPath : IPathfinder
    {
        public int Calls { get; private set; }

        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Calls++;
            return PathResult.None(start);
        }
    }

    private sealed class Scene : IDisposable
    {
        private readonly NavMeshFixture? _fixture;

        public Scene(float x, float y, IPathfinder? custom = null, byte movementType = 0, float wander = 0, float playerX = 0, float playerY = 0,
            CreatureTemplate? template = null, Func<int, int, bool>? walkable = null, NavTerrain? terrain = null)
        {
            CreatureContent content = Content([template ?? Template()], [Spawn(1, WolfEntry, x, y, z: 0, movementType: movementType, wander: wander)]);
            (World, _, Creatures) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() });
            if (custom is null)
            {
                _fixture = new NavMeshFixture();
                _fixture.WriteParams(MapId);
                _fixture.WriteTile(MapId, 31, 31, (walkable is null ? House() : House() with { Walkable = walkable }) with { Flags = (_, _) => terrain ?? NavTerrain.Ground });
                var nav = new NavMeshPathfinder(_fixture.Directory);
                Assert.True(nav.LoadTile(MapId, 31, 31));
                WorldCollision.Of(World).Install(pathfinder: nav);
            }
            else
            {
                WorldCollision.Of(World).Install(pathfinder: custom);
            }

            (Player, _) = AddPlayer(World, 1, playerX, playerY);
            Wolf = Assert.Single(Creatures.Creatures);
        }

        public WorldRuntime World { get; }

        public CreatureMapSystem Creatures { get; }

        public Player Player { get; }

        public Creature Wolf { get; }

        public void Pull() => World.GetMap(MapId, 0).Combat.DealDamage(Player, Wolf, 1, direct: false);

        public void Ticks(int count)
        {
            for (int i = 0; i < count; i++)
            {
                World.RunTick(50);
            }
        }

        public void Dispose()
        {
            World.Dispose();
            _fixture?.Dispose();
        }
    }

    /// <summary>Whether the spline (from where the wolf was) goes through the house.</summary>
    private static bool SplineCrossesHouse(Vector3 from, CreatureSpline spline)
    {
        Vector3 previous = from;
        foreach (Vector3 point in spline.Points)
        {
            if (CrossesHouse(previous, point))
            {
                return true;
            }

            previous = point;
        }

        return false;
    }

    // --- chase ---------------------------------------------------------------------------------------------

    [Fact]
    public void Chase_TowardATargetInsideTheHouse_NeverSplinesThroughTheHole_AndEvadesAsUnreachable()
    {
        using var scene = new Scene(x: 30, y: 20, playerX: 20, playerY: 20);
        var start = new Vector3(30, 20, 0);
        scene.Pull();
        Assert.Equal(MovementGeneratorType.Chase, scene.Wolf.Motion.CurrentType);

        for (int i = 0; i < 100; i++) // 5 s
        {
            scene.Ticks(1);
            Assert.False(InHouse(scene.Wolf.X, scene.Wolf.Y), $"the chaser walked into the house at ({scene.Wolf.X}, {scene.Wolf.Y})");
            if (scene.Wolf.Spline is { } spline)
            {
                Assert.False(SplineCrossesHouse(start, spline), "a chase spline goes through the house");
            }
        }

        Assert.False(scene.Wolf.Motion.IsReachable);
        Assert.True(scene.Wolf.IsInEvadeMode, "the unreachable-target evade did not trigger");
    }

    [Fact]
    public void Chase_WithNoPath_DoesNotMove_AndTheUnreachableEvadeStillTriggers()
    {
        var paths = new CountingNoPath();
        using var scene = new Scene(x: 30, y: 20, custom: paths);
        scene.Pull();
        Run(scene.World, 1000, step: 50);

        Assert.False(scene.Wolf.IsMoving);
        Assert.Equal(30f, scene.Wolf.X, 2);
        Assert.False(scene.Wolf.Motion.IsReachable);
        Assert.InRange(paths.Calls, 8, 14); // re-asked on the 100 ms re-check, not every 50 ms tick

        Run(scene.World, 3000, step: 50);
        Assert.Equal(30f, scene.Wolf.X, 2);
        Assert.True(scene.Wolf.IsInEvadeMode);
    }

    // --- flee and confused ---------------------------------------------------------------------------------

    [Fact]
    public void Flee_WithNoPath_WaitsAboutASecondAndAHalf_ThenRetriesWithANewPoint()
    {
        var paths = new CountingNoPath();
        using var scene = new Scene(x: 30, y: 20, custom: paths, playerX: 36, playerY: 20);
        scene.Wolf.Motion.MoveFleeing(scene.Player, 0);
        Assert.Equal(1, paths.Calls);
        Assert.False(scene.Wolf.IsMoving);

        scene.Ticks(18); // 900 ms: still waiting
        Assert.Equal(1, paths.Calls);

        scene.Ticks(14); // 1.6 s in: the 1-1.5 s wait is over and it asked again
        Assert.Equal(2, paths.Calls);
        Assert.False(scene.Wolf.IsMoving);
        Assert.Equal(30f, scene.Wolf.X, 2);

        WorldCollision.Of(scene.World).Install(pathfinder: StraightLinePathfinder.Instance);
        scene.Ticks(40);
        Assert.True(scene.Wolf.X != 30f || scene.Wolf.Y != 20f, "the flight never started once a path existed");
    }

    [Fact]
    public void Confused_WithNoPath_DoesNotStagger()
    {
        var paths = new CountingNoPath();
        using var scene = new Scene(x: 30, y: 20, custom: paths);
        scene.Wolf.UnitFlags |= UnitFlags.Confused; // the map tick's movement hook starts the stagger from the flag
        scene.Ticks(100);

        Assert.True(paths.Calls >= 2);
        Assert.Equal(30f, scene.Wolf.X, 2);
        Assert.Equal(20f, scene.Wolf.Y, 2);
    }

    // --- wander ----------------------------------------------------------------------------------------------

    [Fact]
    public void Wander_WithNoPath_SkipsTheLeg()
    {
        var paths = new CountingNoPath();
        using var scene = new Scene(x: 30, y: 20, custom: paths, movementType: 1, wander: 8f);
        scene.Ticks(400); // 20 s

        Assert.True(paths.Calls >= 3, $"only {paths.Calls} legs were tried");
        Assert.False(scene.Wolf.IsMoving);
        Assert.Equal(30f, scene.Wolf.X, 2);
    }

    // --- home ------------------------------------------------------------------------------------------------

    [Fact]
    public void Home_WithNoPath_TeleportsHome()
    {
        using var scene = new Scene(x: 30, y: 20, custom: new CountingNoPath());
        scene.Wolf.Motion.MoveTargetedHome(new CreatureHome(12, 20, 0, 1f));

        Assert.Equal(12f, scene.Wolf.X, 2);
        Assert.Equal(20f, scene.Wolf.Y, 2);
        Assert.Equal(1, scene.Creatures.HomePathTeleports);
        Assert.False(scene.Wolf.IsMoving);
        scene.Ticks(2);
        Assert.NotEqual(MovementGeneratorType.Home, scene.Wolf.Motion.CurrentType); // arrived: the generator ended as a run does
    }

    [Fact]
    public void Home_WithAMeshPath_Walks_AroundTheHouse()
    {
        using var scene = new Scene(x: 30, y: 20);
        scene.Wolf.Motion.MoveTargetedHome(new CreatureHome(12, 20, 0, 1f));

        Assert.Equal(0, scene.Creatures.HomePathTeleports);
        Assert.True(scene.Wolf.IsMoving);
        Assert.False(SplineCrossesHouse(new Vector3(30, 20, 0), scene.Wolf.Spline!));
        Assert.True(scene.Wolf.Spline!.Points.Count > 1);
    }

    // --- budget ------------------------------------------------------------------------------------------------

    [Fact]
    public void ASpentBudget_Defers_NeverWalksStraight_AndTheRouteIsServedWhenBudgetReturns()
    {
        using var scene = new Scene(x: 30, y: 20);
        scene.Creatures.PathBudget = new CreaturePathBudget(0, long.MaxValue);
        scene.Wolf.Motion.MovePoint(5, 12, 20, 0, run: true);

        Assert.False(scene.Wolf.IsMoving);
        scene.Ticks(5);
        Assert.False(scene.Wolf.IsMoving);
        Assert.Equal(30f, scene.Wolf.X, 2);
        Assert.Equal(0, scene.Creatures.DirectFallbackPaths);
        Assert.True(scene.Creatures.DeferredPathSearches > 0);

        scene.Creatures.PathBudget = CreaturePathBudget.Default;
        scene.Ticks(1);
        Assert.True(scene.Wolf.IsMoving);
        Assert.False(SplineCrossesHouse(new Vector3(30, 20, 0), scene.Wolf.Spline!));
    }

    [Fact]
    public void AScriptedMove_WaitsForBudget_ThenPathsAroundTheHole()
    {
        using var scene = new Scene(x: 30, y: 20);
        scene.Creatures.PathBudget = new CreaturePathBudget(0, long.MaxValue);
        scene.Creatures.MoveTo(scene.Wolf, 12, 20, 0, run: true, finalOrientation: null);
        Assert.False(scene.Wolf.IsMoving);
        Assert.Equal(1, scene.Creatures.DeferredMoveCount);

        scene.Creatures.PathBudget = CreaturePathBudget.Default;
        scene.Ticks(1);

        Assert.Equal(0, scene.Creatures.DeferredMoveCount);
        Assert.True(scene.Wolf.IsMoving);
        Assert.False(SplineCrossesHouse(new Vector3(30, 20, 0), scene.Wolf.Spline!));
    }

    // --- scripted moves ----------------------------------------------------------------------------------------

    [Fact]
    public void AScriptedMoveTo_PathsAroundTheHole()
    {
        using var scene = new Scene(x: 30, y: 20);
        scene.Creatures.MoveTo(scene.Wolf, 12, 20, 0, run: false, finalOrientation: null);

        Assert.True(scene.Wolf.IsMoving);
        Assert.True(scene.Wolf.Spline!.Points.Count > 1, "one straight leg through the house");
        Assert.False(SplineCrossesHouse(new Vector3(30, 20, 0), scene.Wolf.Spline));
    }

    // --- straight lines that remain -----------------------------------------------------------------------------

    [Fact]
    public void AMapWithoutMeshData_StillMovesStraight()
    {
        using var scene = new Scene(x: 30, y: 20, custom: StraightLinePathfinder.Instance);
        scene.Creatures.MoveTo(scene.Wolf, 12, 20, 0, run: false, finalOrientation: null);

        Assert.True(scene.Wolf.IsMoving);
        Assert.Single(scene.Wolf.Spline!.Points);
        Assert.Equal(0, scene.Creatures.DirectFallbackPaths);
    }

    [Fact]
    public void AnAuthoredPoint_WithNoPath_StillGoesStraight_AndIsCounted()
    {
        using var scene = new Scene(x: 30, y: 20, custom: new CountingNoPath());
        long before = CreaturePathStats.DirectFallbacks;
        scene.Wolf.Motion.MovePoint(5, 12, 20, 0, run: true);

        Assert.True(scene.Wolf.IsMoving);
        Assert.Single(scene.Wolf.Spline!.Points);
        Assert.Equal(1, scene.Creatures.DirectFallbackPaths);
        Assert.True(CreaturePathStats.DirectFallbacks > before);
    }
}
