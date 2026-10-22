using System.Numerics;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Elevator;

/// <summary>
/// LS-009 (live 2026-10-17): on Undercity's enclosed vault lifts the shaft reaches the doors (9.06..10.60 yards from its centre), so
/// a floor inside them under the lower stop is the shaft's bottom. The bots stranded there (1599.0,181.5,-62.0 by the lift at 1595,189)
/// failed "no way to the lower landing" 356 times and were never rescued.
/// </summary>
public sealed class PlayerbotElevatorVaultPitTests
{
    private static ElevatorLift UndercityLift()
        => new(0, [new ElevatorCar(0, 20650, 20658, new Vector3(1595.0f, 189.0f, -40.76f), new Vector3(1595.0f, 189.0f, 55.65f), 16_667)]);

    [Fact]
    public void TheBottomOfAVaultShaft_InsideTheDoors_IsAPit()
    {
        ElevatorLift lift = UndercityLift();
        Assert.True(PlayerbotElevator.InPit(lift, new Vector3(1599.0f, 181.5f, -62.0f), -62.0f)); // the live spot, 8.5 yd out
        Assert.True(PlayerbotElevator.InPit(lift, new Vector3(1595.0f, 189.0f, -62.0f), -62.0f)); // under the car
    }

    [Fact]
    public void TheLandingOutsideTheDoors_AndTheLandingFloor_AreNotAPit()
    {
        ElevatorLift lift = UndercityLift();
        Assert.False(PlayerbotElevator.InPit(lift, new Vector3(1595.0f + 12.5f, 189.0f, -40.8f), -40.8f)); // the landing ring
        Assert.False(PlayerbotElevator.InPit(lift, new Vector3(1595.0f + 12.5f, 189.0f, -62.0f), -62.0f)); // below, but outside the doors
        Assert.False(PlayerbotElevator.InPit(lift, new Vector3(1599.0f, 181.5f, -40.5f), -40.5f)); // at the stop's level
    }

    [Fact]
    public void AnOpenLift_KeepsTheCarFootprint()
    {
        // Thunder Bluff's lifts are open cars on a cliff: 8.5 yd from the shaft is not the shaft.
        var lift = new ElevatorLift(1, [new ElevatorCar(1, 1, 2, new Vector3(-1033f, -39f, 69f), new Vector3(-1033f, -39f, 140f), 40_000)]);
        Assert.False(PlayerbotElevator.InPit(lift, new Vector3(-1033f + 6f, -39f + 6f, 60f), 60f));
        Assert.True(PlayerbotElevator.InPit(lift, new Vector3(-1033f + 2f, -39f, 60f), 60f));
        // Issue #811: 5.82 yards horizontally from the Thunder Bluff lower stop at z=60.6 (below Low.Z=68.6) is inside the shaft pit.
        Assert.True(PlayerbotElevator.InPit(lift, new Vector3(-1033f + 5.82f, -39f, 60.6f), 60.6f));
    }

    [Fact]
    public async Task LandingBesideReachable_WhenBotIsMoreThan250YardsAway_StillSelectsReachableLanding()
    {
        var stop = new Vector3(-4669.2f, -1832.8f, ElevatorTestWorld.BottomFloor);
        var northOnlyMesh = new NorthSideOnlyMesh(stop);
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.OnWorldAsync(() => ArcaneCore.Game.Maps.Collision.WorldCollision.Of(world.Host.World).Install(
            lineOfSight: northOnlyMesh,
            pathfinder: northOnlyMesh));
        var start = new Vector3(stop.X - 280.4f, stop.Y, stop.Z);
        await world.PlaceAsync(start);
        var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100 };

        Vector3? chosen = await world.OnWorldAsync(() =>
            PlayerbotElevator.LandingBesideReachable(world.Player, world.Player.Map!, stop, start, options));

        Assert.NotNull(chosen);
        Assert.True(chosen.Value.Y > stop.Y + 4f, $"expected reachable North-side candidate, got {chosen.Value}");
    }

    [Fact]
    public async Task LandingBeside_OnVaultLiftWithSlopingCorridorAndIsolatedFragment_SelectsSlopingCorridorInsteadOfFragment()
    {
        var stop = new Vector3(1595.0f, 189.0f, -40.76f);
        var toward = new Vector3(1580.0f, 180.0f, -43.9f);
        var mesh = new SlopingCorridorAndSouthFragmentMesh(stop);
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.OnWorldAsync(() => ArcaneCore.Game.Maps.Collision.WorldCollision.Of(world.Host.World).Install(
            lineOfSight: mesh,
            pathfinder: mesh));

        Vector3? chosen = await world.OnWorldAsync(() =>
        {
            var ek = world.Host.World.GetMap(0);
            return PlayerbotElevator.LandingBeside(ek, stop, toward);
        });

        Assert.NotNull(chosen);
        // The south candidate (Y < stop.Y - 8f) is an isolated mesh fragment; the northwest sloping corridor (X < stop.X - 6f) is the real corridor.
        Assert.True(chosen.Value.X < stop.X - 6f, $"expected non-fragment sloping corridor landing west of shaft, got {chosen.Value}");
    }

    private sealed class SlopingCorridorAndSouthFragmentMesh(Vector3 stop) : ArcaneCore.Game.Maps.Collision.ILineOfSight, ArcaneCore.Game.Maps.Collision.IPathfinder, ArcaneCore.Game.Maps.Collision.IMeshPointQuery
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }

        public float? GetModelHeight(uint mapId, float x, float y, float zSearch, float maxSearchDistance)
        {
            // South wall fragment is at -43.1f (2.34 yd below stop.Z); NW sloping corridor is at -43.9f (3.14 yd below stop.Z).
            if (x < stop.X - 6f) return stop.Z - 3.14f;
            if (y < stop.Y - 8f) return stop.Z - 2.34f;
            return null;
        }

        public bool TryGetAreaInfo(uint mapId, float x, float y, float zSearch, out ArcaneCore.Game.Maps.Collision.ModelAreaInfo info) { info = default; return false; }

        public ArcaneCore.Game.Maps.Collision.PathResult FindPath(uint mapId, Vector3 start, Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
        {
            // South candidate (Y < stop.Y - 8f) is an isolated 1-yard fragment that cannot reach 12-yard probe targets.
            if (start.Y < stop.Y - 8f)
                return new ArcaneCore.Game.Maps.Collision.PathResult(ArcaneCore.Game.Maps.Collision.PathType.Incomplete, [start, start + new Vector3(0.5f, 0f, 0f)]);
            return ArcaneCore.Game.Maps.Collision.PathResult.StraightLine(start, end, ArcaneCore.Game.Maps.Collision.PathType.Normal);
        }

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, ArcaneCore.Game.Maps.Collision.PathOptions? options, out Vector3 point)
        {
            point = center;
            return false;
        }

        public ArcaneCore.Game.Maps.Collision.MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
        {
            float z = start.X < stop.X - 6f ? stop.Z - 3.14f : stop.Z - 2.34f;
            return new ArcaneCore.Game.Maps.Collision.MeshRaycastResult(true, false, 1f, end with { Z = z });
        }
    }

    private sealed class NorthSideOnlyMesh(Vector3 stop) : ArcaneCore.Game.Maps.Collision.ILineOfSight, ArcaneCore.Game.Maps.Collision.IPathfinder, ArcaneCore.Game.Maps.Collision.IMeshPointQuery
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float zSearch, float maxSearchDistance) => stop.Z;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float zSearch, out ArcaneCore.Game.Maps.Collision.ModelAreaInfo info) { info = default; return false; }

        public ArcaneCore.Game.Maps.Collision.PathResult FindPath(uint mapId, Vector3 start, Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
        {
            if (Vector2.Distance(new Vector2(end.X, end.Y), new Vector2(stop.X, stop.Y)) < 50f && end.Y <= stop.Y + 4f)
                return ArcaneCore.Game.Maps.Collision.PathResult.None(start);
            return ArcaneCore.Game.Maps.Collision.PathResult.StraightLine(start, end with { Z = stop.Z }, ArcaneCore.Game.Maps.Collision.PathType.Normal);
        }

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, ArcaneCore.Game.Maps.Collision.PathOptions? options, out Vector3 point)
        {
            point = center;
            return false;
        }

        public ArcaneCore.Game.Maps.Collision.MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
        {
            if (Vector2.Distance(new Vector2(start.X, start.Y), new Vector2(stop.X, stop.Y)) < 50f && start.Y <= stop.Y + 4f)
                return ArcaneCore.Game.Maps.Collision.MeshRaycastResult.Unavailable(end);
            return new ArcaneCore.Game.Maps.Collision.MeshRaycastResult(true, false, 1f, end with { Z = stop.Z });
        }
    }

    [Fact]
    public async Task LandingBeside_WhenAllRingsAreInSteepVestibuleFragment_FallsBackToWalkableMeshInsteadOfReturningNull()
    {
        using var fixture = new ArcaneCore.Game.Tests.Collision.NavMeshFixture();
        (int X, int Y, ArcaneCore.Game.Tests.Collision.CellTile Tile)[] vestibule =
        [
            (10, 10, new ArcaneCore.Game.Tests.Collision.CellTile(0, 0, 0, 0, 8, 8, Size: 5)),
        ];
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(fixture, vestibule, new FloorLineOfSight());
        var stop = new Vector3(20f, 20f, 0f);
        var toward = new Vector3(5f, 20f, 0f);
        await world.PlaceAsync(stop);
        _ = await world.Nav.RegionGraphTask(0, ArcaneCore.Game.Maps.Collision.PathMover.Player.IncludeFlags, ArcaneCore.Game.Maps.Collision.NavTerrain.SteepSlopes)!;

        Vector3? chosen = await world.Host.OnWorldAsync(() =>
        {
            Assert.True(PlayerbotNavigation.IsPointOnMeshFragment(world.Player.Map!, new Vector3(14f, 20f, 0f)));
            return PlayerbotElevator.LandingBeside(world.Player.Map!, stop, toward);
        });

        Assert.NotNull(chosen);
        Assert.Equal(0f, chosen.Value.Z, 0.2f);
    }

    [Fact]
    public async Task JourneyWalk_WhenDetourRouteMovesAwayFromTargetForOver60Seconds_StillCountsPolylineProgressAndDoesNotFail()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        var detourPathfinder = new DetourPathfinder();
        await world.OnWorldAsync(() => ArcaneCore.Game.Maps.Collision.WorldCollision.Of(world.Host.World).Install(
            lineOfSight: detourPathfinder,
            pathfinder: detourPathfinder));
        await world.PlaceAsync(new Vector3(0f, 0f, ElevatorTestWorld.BottomFloor));
        var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100 };
        var walk = new JourneyWalk();
        var target = new Vector3(100f, 100f, ElevatorTestWorld.BottomFloor);

        JourneyWalk.Outcome lastOutcome = JourneyWalk.Outcome.Walking;
        string? lastWhy = null;
        for (int i = 0; i < 650; i++)
        {
            await world.Host.World.AdvanceClockAsync(100);
            (lastOutcome, lastWhy) = await world.OnWorldAsync(() =>
            {
                world.Session.ManagedBudget = new ArcaneCore.World.Net.ManagedActionBudget(8);
                uint now = world.Host.World.NowMs;
                PlayerbotMotion.Pump(world.Session, world.Player, now);
                JourneyWalk.Outcome o = walk.Step(world.Session, world.Player, options, target, 100, now, maxFare: 0, out string? w);
                return (o, w);
            });
            if (lastOutcome == JourneyWalk.Outcome.Failed) break;
        }

        Assert.True(lastOutcome != JourneyWalk.Outcome.Failed, $"expected detour walk to stay active past 60s while advancing along route, got {lastOutcome}: {lastWhy}");
    }

    private sealed class AllFragmentVestibuleMesh(Vector3 stop) : ArcaneCore.Game.Maps.Collision.ILineOfSight, ArcaneCore.Game.Maps.Collision.IPathfinder, ArcaneCore.Game.Maps.Collision.IMeshPointQuery
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float zSearch, float maxSearchDistance) => stop.Z;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float zSearch, out ArcaneCore.Game.Maps.Collision.ModelAreaInfo info) { info = default; return false; }

        public ArcaneCore.Game.Maps.Collision.PathResult FindPath(uint mapId, Vector3 start, Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
            => new(ArcaneCore.Game.Maps.Collision.PathType.Incomplete, [start, start + new Vector3(0.5f, 0f, 0f)]);

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, ArcaneCore.Game.Maps.Collision.PathOptions? options, out Vector3 point)
        {
            point = center;
            return false;
        }

        public ArcaneCore.Game.Maps.Collision.MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
            => new(true, false, 1f, end with { Z = stop.Z });
    }

    private sealed class DetourPathfinder : ArcaneCore.Game.Maps.Collision.ILineOfSight, ArcaneCore.Game.Maps.Collision.IPathfinder
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float zSearch, float maxSearchDistance) => ElevatorTestWorld.BottomFloor;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float zSearch, out ArcaneCore.Game.Maps.Collision.ModelAreaInfo info) { info = default; return false; }

        public ArcaneCore.Game.Maps.Collision.PathResult FindPath(uint mapId, Vector3 start, Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
        {
            float z = ElevatorTestWorld.BottomFloor;
            return new ArcaneCore.Game.Maps.Collision.PathResult(
                ArcaneCore.Game.Maps.Collision.PathType.Normal,
                [start with { Z = z }, new Vector3(-600f, 0f, z), new Vector3(-600f, 100f, z), end with { Z = z }]);
        }
    }
}