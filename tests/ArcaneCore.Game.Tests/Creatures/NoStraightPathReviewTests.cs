using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
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
    [InlineData(41f)]
    [InlineData(43f)]
    [InlineData(44.5f)]
    public void Home_APointOneToFiveYardsOffTheMesh_WalksHome(float homeX)
    {
        // The walkable tile ends at x = 40; the spawn point lies 1-4.5 yd beyond it. vmangos FindWalkPoly looks 5 yd around and only
        // calls the path partial past 7 yd, so the creature runs to the nearest mesh point instead of teleporting.
        using var scene = new Scene(x: 30, y: 30);
        scene.Wolf.Motion.MoveTargetedHome(new CreatureHome(homeX, 30, 0, 1f));

        Assert.Equal(0, scene.Creatures.HomePathTeleports);
        Assert.True(scene.Wolf.IsMoving);
        Assert.Equal(30f, scene.Wolf.X, 2);
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
}
