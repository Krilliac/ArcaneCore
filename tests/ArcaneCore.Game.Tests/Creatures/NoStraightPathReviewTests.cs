using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Tests.SpawnGroups;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>Review cycle 1 of lane w44-path-no-straight: home on partial and deferred paths, the 7 yd end tolerance, waypoint and formation retries, stale scripted moves, fliers.</summary>
public sealed partial class NoStraightPathTests
{
    private sealed class PartialPath : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(PathType.Incomplete, [start, Vector3.Lerp(start, end, 0.5f)]);
    }

    [Fact]
    public void Home_OnAPartialPath_Teleports()
    {
        using var scene = new Scene(x: 30, y: 20, custom: new PartialPath());
        scene.Wolf.Motion.MoveTargetedHome(new CreatureHome(12, 20, 0, 1f));

        Assert.Equal(12f, scene.Wolf.X, 2);
        Assert.False(scene.Wolf.IsMoving);
        Assert.Equal(1, scene.Creatures.HomePathTeleports);
    }

    [Fact]
    public void Home_DeferredByTheBudget_WaitsThenTeleportsAfterThreeSeconds()
    {
        using var scene = new Scene(x: 30, y: 20, custom: StraightLinePathfinder.Instance);
        scene.Creatures.PathBudget = new CreaturePathBudget(0, long.MaxValue);
        scene.Wolf.Motion.MoveTargetedHome(new CreatureHome(12, 20, 0, 1f));

        scene.Ticks(50); // 2.5 s: still waiting for budget, not walking
        Assert.Equal(30f, scene.Wolf.X, 2);
        Assert.False(scene.Wolf.IsMoving);
        Assert.Equal(0, scene.Creatures.HomePathTeleports);

        scene.Ticks(20); // 3.5 s: HomeMovementGenerator.DeferredTeleportMs has passed
        Assert.Equal(12f, scene.Wolf.X, 2);
        Assert.Equal(1, scene.Creatures.HomePathTeleports);
        Assert.Equal(0, scene.Creatures.DirectFallbackPaths);
    }

    [Fact]
    public void Home_DeferredByTheBudget_WalksWhenBudgetReturnsInTime()
    {
        using var scene = new Scene(x: 30, y: 20, custom: StraightLinePathfinder.Instance);
        scene.Creatures.PathBudget = new CreaturePathBudget(0, long.MaxValue);
        scene.Wolf.Motion.MoveTargetedHome(new CreatureHome(12, 20, 0, 1f));
        scene.Ticks(10);
        scene.Creatures.PathBudget = CreaturePathBudget.Default;
        scene.Ticks(1);

        Assert.True(scene.Wolf.IsMoving);
        Assert.Equal(0, scene.Creatures.HomePathTeleports);
    }

    [Theory]
    [InlineData(41f, 0f)]
    [InlineData(43f, 0f)]
    [InlineData(44.5f, 0f)]   // 4.5 yd beside the mesh
    [InlineData(44.5f, 4f)]   // 4.5 beside and 4 above: 3D distance 6.0, inside vmangos' 7 yd
    [InlineData(42f, 3f)]     // a vertical offset alone
    public void Home_APointOffTheMeshWithinSevenYards_WalksToTheNearestMeshPoint(float homeX, float homeZ)
    {
        // The walkable tile ends at x = 40; vmangos FindWalkPoly looks 5 yd around (10 up and down) and only calls the path partial past 7 yd
        // (3D), so the creature runs to the nearest mesh point instead of teleporting.
        using var scene = new Scene(x: 30, y: 30);
        scene.Wolf.Motion.MoveTargetedHome(new CreatureHome(homeX, 30, homeZ, 1f));

        Assert.Equal(0, scene.Creatures.HomePathTeleports);
        Assert.True(scene.Wolf.IsMoving);
        Assert.Equal(30f, scene.Wolf.X, 2);
        Vector3 end = scene.Wolf.Spline!.Points[^1];
        Assert.InRange(end.X, 39.5f, 40.05f); // the edge of the walkable tile, short of the spawn point
        Assert.InRange(end.Y, 29.5f, 30.5f);
        Assert.False(InHouse(end.X, end.Y));
    }

    [Theory]
    [InlineData(44.5f, 6.5f)] // 3D distance 7.9: past farFromPoly
    [InlineData(46f, 0f)]     // beyond the 5 yd search box
    [InlineData(38f, -4f)]    // the mesh is 4 above the spawn point: refused ("do not select points over player pos")
    public void Home_APointTooFarFromTheMesh_Teleports(float homeX, float homeZ)
    {
        using var scene = new Scene(x: 30, y: 30);
        scene.Wolf.Motion.MoveTargetedHome(new CreatureHome(homeX, 30, homeZ, 1f));

        Assert.Equal(1, scene.Creatures.HomePathTeleports);
        Assert.False(scene.Wolf.IsMoving);
        Assert.Equal(homeX, scene.Wolf.X, 2);
    }

    [Theory]
    [InlineData(44.5f, 0f)]   // 4.5 yd beside the mesh
    [InlineData(44f, 4.5f)]   // 4 beside and 4.5 above: 3D distance 6.0
    public void Home_FromAStartOffTheMeshWithinSevenYards_RunsHome(float x, float z)
    {
        // vmangos locates the start of the way home as it locates the home point (getPolyByLocation, PathFinder.cpp:188-189): 5 yd around,
        // whole within 7 yd (3D), so the creature runs home instead of teleporting.
        using var scene = new Scene(x: x, y: 30, z: z);
        scene.Wolf.Motion.MoveTargetedHome(new CreatureHome(30, 30, 0, 1f));

        Assert.Equal(0, scene.Creatures.HomePathTeleports);
        Assert.True(scene.Wolf.IsMoving);
        Vector3 end = scene.Wolf.Spline!.Points[^1];
        Assert.Equal(30f, end.X, 2);
        Assert.Equal(30f, end.Y, 2);
    }

    [Theory]
    [InlineData(30f, 8f)]     // 8 above the mesh: past farFromPoly (an incomplete path, PathFinder.cpp:209-235)
    [InlineData(30f, 15f)]    // above FindWalkPoly's 10 yd: no start polygon
    [InlineData(30f, -4f)]    // the mesh 4 above: refused ("do not select points over player pos")
    [InlineData(46f, 0f)]     // beyond the 5 yd box
    public void Home_FromAStartTooFarFromTheMesh_Teleports(float x, float z)
    {
        // vmangos HomeMovementGenerator.cpp:58-71: anything but a NORMAL path home is a teleport, never a walk through the floor or the air.
        using var scene = new Scene(x: x, y: 30, z: z);
        scene.Wolf.Motion.MoveTargetedHome(new CreatureHome(30, 32, 0, 1f));

        Assert.Equal(1, scene.Creatures.HomePathTeleports);
        Assert.False(scene.Wolf.IsMoving);
        Assert.Equal(32f, scene.Wolf.Y, 2);
        Assert.Equal(0f, scene.Wolf.Z, 2);
    }

    [Fact]
    public void Home_APointInAnUnreachableArea_Teleports()
    {
        // A wall of unwalkable cells (x in [20, 22)) splits the tile: the home point is on the other side, the corridor cannot reach it.
        using var scene = new Scene(x: 30, y: 30, walkable: (i, j) => i != 10);
        scene.Wolf.Motion.MoveTargetedHome(new CreatureHome(10, 30, 0, 1f));

        Assert.Equal(1, scene.Creatures.HomePathTeleports);
        Assert.Equal(10f, scene.Wolf.X, 2);
        Assert.False(scene.Wolf.IsMoving);
    }

    [Fact]
    public void Waypoint_WhenTheBudgetIsSpent_WaitsAndStartsTheSameLegLater()
    {
        using var scene = new Scene(x: 30, y: 20, custom: StraightLinePathfinder.Instance);
        scene.Creatures.PathBudget = new CreaturePathBudget(0, long.MaxValue);
        scene.Wolf.Motion.Initialize(new WaypointMovementGenerator([new(1, 12, 20, 0, 100f, 0), new(2, 30, 30, 0, 100f, 0)]), scene.Creatures, start: true);

        scene.Ticks(10);
        Assert.False(scene.Wolf.IsMoving);
        Assert.Equal(30f, scene.Wolf.X, 2);
        Assert.Equal(0, scene.Creatures.DirectFallbackPaths);
        Assert.True(scene.Creatures.DeferredPathSearches >= 2, "the leg was not retried on later updates");

        scene.Creatures.PathBudget = CreaturePathBudget.Default;
        scene.Ticks(1);
        Assert.True(scene.Wolf.IsMoving);
        Assert.Equal(12f, scene.Wolf.Spline!.Points[^1].X, 2); // the first node, not a skipped one
    }

    [Fact]
    public void ADeferredScriptedMove_IsDropped_WhenTheCreatureDies()
    {
        using var scene = new Scene(x: 30, y: 20, custom: StraightLinePathfinder.Instance);
        scene.Creatures.PathBudget = new CreaturePathBudget(0, long.MaxValue);
        scene.Creatures.MoveTo(scene.Wolf, 12, 20, 0, run: true, finalOrientation: null);
        Assert.Equal(1, scene.Creatures.DeferredMoveCount);

        scene.World.GetMap(MapId, 0).Combat.DealDamage(scene.Player, scene.Wolf, 100_000, direct: false);
        Assert.False(scene.Wolf.IsAlive);
        scene.Creatures.PathBudget = CreaturePathBudget.Default;
        scene.Ticks(2);

        Assert.Equal(0, scene.Creatures.DeferredMoveCount);
        Assert.False(scene.Wolf.IsMoving);
    }

    [Fact]
    public void ADeferredScriptedMove_IsDropped_WhenTheCreatureLeavesTheMap()
    {
        using var scene = new Scene(x: 30, y: 20, custom: StraightLinePathfinder.Instance);
        scene.Creatures.PathBudget = new CreaturePathBudget(0, long.MaxValue);
        scene.Creatures.MoveTo(scene.Wolf, 12, 20, 0, run: true, finalOrientation: null);
        Assert.Equal(1, scene.Creatures.DeferredMoveCount);

        scene.Creatures.Despawn(scene.Wolf);
        scene.Creatures.PathBudget = CreaturePathBudget.Default;
        scene.Ticks(2);

        Assert.Equal(0, scene.Creatures.DeferredMoveCount);
        Assert.False(scene.Wolf.IsMoving);
    }

    [Fact]
    public void AStaleScriptedMove_IsDropped_ByStopMoving_AndByATeleport()
    {
        using var scene = new Scene(x: 30, y: 20, custom: StraightLinePathfinder.Instance);
        scene.Creatures.PathBudget = new CreaturePathBudget(0, long.MaxValue);

        scene.Creatures.MoveTo(scene.Wolf, 12, 20, 0, run: true, finalOrientation: null);
        scene.Creatures.StopMoving(scene.Wolf);
        Assert.Equal(0, scene.Creatures.DeferredMoveCount);

        scene.Creatures.MoveTo(scene.Wolf, 12, 20, 0, run: true, finalOrientation: null);
        scene.Creatures.NearTeleport(scene.Wolf, 35, 35, 0, 0);
        Assert.Equal(0, scene.Creatures.DeferredMoveCount);

        scene.Creatures.PathBudget = CreaturePathBudget.Default;
        scene.Ticks(2);
        Assert.False(scene.Wolf.IsMoving);
        Assert.Equal(35f, scene.Wolf.X, 2);
    }

    [Fact]
    public void AFlyingCreature_CrossesAHoleInTheMesh_InsteadOfStandingOnNoPath()
    {
        // The player stands in the middle of the house: a walker has no whole path (it does not enter), a flier gets the flier shortcut.
        using var flier = new Scene(x: 30, y: 20, playerX: 20, playerY: 20, template: Template() with { InhabitType = 4 });
        flier.Pull();
        flier.Ticks(4);
        Assert.True(flier.Wolf.IsMoving, "the flier stands still");
        Assert.True(flier.Wolf.Motion.IsReachable);

        using var walker = new Scene(x: 30, y: 20, playerX: 20, playerY: 20);
        walker.Pull();
        walker.Ticks(4);
        Assert.False(InHouse(walker.Wolf.X, walker.Wolf.Y));
        Assert.False(walker.Wolf.Motion.IsReachable);
    }

    [Fact]
    public void Formation_FollowerWithNoBudget_WaitsThenFollowsWhenBudgetReturns_NeverStraight()
    {
        const float shiftX = 6852.72f;
        const float shiftY = 2249.63f;
        (uint Guid, uint Entry, float X, float Y, float O)[] spawns =
        [
            (6877, 9085, -6687.94f, -2159.12f, 3.9968f), (6880, 9083, -6690.19f, -2156.94f, 4.01426f), (6883, 9086, -6692.72f, -2159.63f, 4.03171f),
            (6885, 9082, -6686.64f, -2162.67f, 3.80482f), (6886, 9084, -6689.95f, -2161.96f, 3.9968f),
        ];
        var content = new CreatureContent(
            [.. spawns.Select(s => Template(s.Entry))],
            spawns.Select(s => Spawn(s.Guid, s.Entry, s.X + shiftX, s.Y + shiftY, 83.5f, respawnSeconds: 300) with { Orientation = s.O }),
            [], [], [],
            entryWaypoints: [(CreatureContent.WaypointPathEntry, CreatureContent.WaypointPathBit | 6883u,
                new CreatureWaypoint(1, -6692.72f + shiftX, -2159.63f + shiftY, 83.5f, 4.03171f, 225_000))])
        {
            SpawnGroups = new SpawnGroupCatalog([ClassicDbSpawnGroupRows.KargathExpeditionaryForce with { Flags = SpawnGroupFlags.None }]),
        };
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime runtime = world;
        AddPlayer(runtime, 1, 300, 300);
        Run(runtime, 200);
        Creature follower = system.Creatures.Single(c => c.Spawn?.Guid == 6877);

        system.PathBudget = new CreaturePathBudget(0, long.MaxValue);
        long fallbacks = system.DirectFallbackPaths;
        system.NearTeleport(follower, follower.X + 8, follower.Y, follower.Z, follower.Orientation);
        float x = follower.X;
        for (int i = 0; i < 40; i++)
        {
            runtime.RunTick(50);
        }

        Assert.False(follower.IsMoving);
        Assert.Equal(x, follower.X, 2);
        Assert.Equal(fallbacks, system.DirectFallbackPaths);
        Assert.True(system.DeferredPathSearches > 0);

        system.PathBudget = CreaturePathBudget.Default;
        for (int i = 0; i < 20 && !follower.IsMoving; i++)
        {
            runtime.RunTick(50);
        }

        Assert.True(follower.IsMoving, "the follower never set off once budget returned");
    }

    private static void MakePet(Scene scene) => scene.Wolf.Summon = new SummonLinks(SummonKind.Pet, scene.Player.Guid, 1, TotemSlots.None, 0);

    [Fact]
    public void APet_WithAGroundOnlyTemplate_SwimsOverWaterPolygons_ALikeNonPetCannot()
    {
        // The whole tile is water: vmangos Creature::CanSwim is IsPet || the template's water bit (Creature.h:166).
        using var scene = new Scene(x: 30, y: 30, template: Template() with { InhabitType = 1 }, terrain: NavTerrain.Water);
        var destination = new Vector3(10, 30, 0);

        Assert.Equal(PathOutcome.NoPath, scene.Creatures.PlanPath(scene.Wolf, destination).Outcome); // a ground creature: no water polygons

        MakePet(scene);
        PlannedPath asPet = scene.Creatures.PlanPath(scene.Wolf, destination);
        Assert.Equal(PathOutcome.Mesh, asPet.Outcome);
        Assert.True(asPet.CanMove);
    }

    [Fact]
    public void APet_WithTheAirBit_GetsNoFlierShortcut_ANonPetFlierDoes()
    {
        var inHole = new Vector3(20, 20, 0);
        using var scene = new Scene(x: 30, y: 20, template: Template() with { InhabitType = 5 });

        Assert.Equal(PathOutcome.Direct, scene.Creatures.PlanPath(scene.Wolf, inHole).Outcome); // a flier crosses the hole

        MakePet(scene);
        Assert.NotEqual(PathOutcome.Direct, scene.Creatures.PlanPath(scene.Wolf, inHole).Outcome); // CanFly is !IsPet && air (Creature.h:167)
    }
}
