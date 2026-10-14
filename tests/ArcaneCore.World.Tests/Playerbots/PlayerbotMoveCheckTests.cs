using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Tests.Collision;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The legs a bot walks that do not come from a navigation-mesh corridor (stepped terrain routes, the retreat trail) are checked with
/// a line of sight that includes M2 doodads (<see cref="PlayerbotMoveCheck"/>) and, where a mesh is loaded, a mesh raycast. The
/// stand-in line of sight reports a hit on an "M2 wall" only when doodads are included, as a fence, tree or rock does: the default
/// (<c>ignoreM2: true</c>, spells) is blind to it.
/// </summary>
public sealed class PlayerbotMoveCheckTests : IDisposable
{
    private readonly NavMeshFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static readonly PlayerbotOptions Options = new() { Enabled = true, MaxRouteYards = 60, MaxPathPoints = 64 };

    /// <summary>A flat floor at <paramref name="height"/> and a doodad wall across the plane x = <paramref name="wallX"/> that only blocks when doodads count.</summary>
    private sealed class M2Wall(float height, float wallX) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true)
            => ignoreM2 || !(Math.Min(from.X, to.X) < wallX && wallX <= Math.Max(from.X, to.X));

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => height;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }

    /// <summary>A bot on a flat floor with the doodad wall six yards east of it and no navigation data (the stepped terrain route is all there is).</summary>
    private static async Task WithWallAsync(Func<WorldTestHost, Player, Vector3, float, Task> body)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Player player = session.Player!;
            (Vector3 here, float wallX) = await host.World.InvokeAsync(() =>
            {
                float x = player.X + 6f;
                WorldCollision.Of(host.World).Install(lineOfSight: new M2Wall(player.Z, x));
                return (new Vector3(player.X, player.Y, player.Z), x);
            });
            await body(host, player, here, wallX);
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>FAILS on code that checks the leg with the doodad-blind line of sight: the 12-yard route crossed the wall.</summary>
    [Fact]
    public Task AnM2WallAcrossTheSteppedLeg_RefusesAPlanForTheDestination()
        => WithWallAsync(async (host, player, here, _) =>
        {
            bool planned = await host.World.InvokeAsync(() => PlayerbotNavigation.TryPlan(player, here + new Vector3(12, 0, 0), Options, out PlayerbotRoute? _));
            Assert.False(planned, "a route was planned across the doodad wall");
        });

    /// <summary>A plan towards the far end is refused or plans a shorter chunk that ends before the wall, never one across it.</summary>
    [Fact]
    public Task AnM2WallAcrossTheSteppedLeg_NeverPlansAChunkAcrossIt()
        => WithWallAsync(async (host, player, here, wallX) =>
        {
            PlayerbotRoute? route = await host.World.InvokeAsync(() =>
                PlayerbotNavigation.TryPlanToward(player, here + new Vector3(12, 0, 0), Options, out PlayerbotRoute? r) ? r : null);
            Assert.True(route is null || route.Points[^1].X < wallX, $"the route ends at {route?.Points[^1]}, past the wall at x {wallX}");
        });

    /// <summary>A clear leg away from the wall is planned whole, as before.</summary>
    [Fact]
    public Task AClearSteppedLeg_IsPlannedWhole()
        => WithWallAsync(async (host, player, here, _) =>
        {
            Vector3 goal = here + new Vector3(-12, 0, 0);
            PlayerbotRoute? route = await host.World.InvokeAsync(() => PlayerbotNavigation.TryPlan(player, goal, Options, out PlayerbotRoute? r) ? r : null);
            Assert.NotNull(route);
            Assert.True(Vector2.Distance(new(route!.Points[^1].X, route.Points[^1].Y), new(goal.X, goal.Y)) < 0.5f, $"the route ends at {route.Points[^1]}");
        });

    /// <summary>
    /// The retreat trail: the bot walks back to its newest crumb (clear), but the chord on to the older one crosses the wall (it walked
    /// round it), so the way back is cut before the wall. FAILS on the old trail, which went through every crumb unchecked.
    /// </summary>
    [Fact]
    public Task TheRetreatTrail_IsCutAtAnM2Wall()
        => WithWallAsync(async (host, player, here, wallX) =>
        {
            Vector3[] crumbs = [here + new Vector3(12, 0, 0), here + new Vector3(-4, 0, 0)]; // oldest first
            PlayerbotRoute? route = await host.World.InvokeAsync(() => PlayerbotRetreat.TrailRoute(player, crumbs, 0));
            Assert.NotNull(route);
            Assert.Equal(3, route!.Points.Count);
            Assert.Equal(here, route.Points[0]);
            Assert.Equal(crumbs[1], route.Points[1]);
            Assert.True(route.Points[2].X < wallX && route.Points[2].X > route.Points[1].X, $"the trail ends at {route.Points[2]}, wall at x {wallX}");
        });

    /// <summary>A trail with no obstacle on it is walked crumb by crumb, unchanged.</summary>
    [Fact]
    public Task AClearRetreatTrail_IsUnchanged()
        => WithWallAsync(async (host, player, here, _) =>
        {
            Vector3[] crumbs = [here + new Vector3(-12, 0, 0), here + new Vector3(-8, 0, 0), here + new Vector3(-4, 0, 0)];
            PlayerbotRoute? route = await host.World.InvokeAsync(() => PlayerbotRetreat.TrailRoute(player, crumbs, 0));
            Assert.NotNull(route);
            Assert.Equal([here, crumbs[2], crumbs[1], crumbs[0]], route!.Points);
        });


    // One 40 x 40 yard tile (5-yard cells) at world X 0-40, Y 0-40; cells 3 and 4 along X (x 15-25) are a hole, as a building's footprint is.
    private static readonly (int X, int Y, CellTile Tile)[] HoleLayout =
    [
        (10, 10, new CellTile(0, 0, 0, 0, 8, 8, Size: 5) { Walkable = (i, _) => i is < 3 or > 4 }),
    ];

    private static readonly (int X, int Y, CellTile Tile)[] SolidLayout = [(10, 10, new CellTile(0, 0, 0, 0, 8, 8, Size: 5))];

    // The same tile placed against the east border of navigation tile (0, 0): world X 493.33-533.33, and no tile beyond it.
    private static readonly (int X, int Y, CellTile Tile)[] EdgeLayout = [(10, 10, new CellTile(0, 0, 493.3333f, 0, 8, 8, Size: 5))];

    private static readonly Vector3 West = new(5, 10, 0), East = new(35, 10, 0);

    /// <summary>A leg that walks off the mesh into a hole is cut at the hole's edge (x 15), half a yard short, by the mesh raycast alone.</summary>
    [Fact]
    public async Task ALegIntoAMeshHole_IsCutAtTheEdge()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, HoleLayout, new M2Wall(0, 1000f));
        await world.PlaceAsync(West);
        (bool Whole, bool Clip, Vector3 Reach, bool Terrain) result = await world.Host.OnWorldAsync(() =>
        {
            Map map = world.Player.Map!;
            bool whole = PlayerbotMoveCheck.CanStep(map, West, East);
            bool clip = PlayerbotMoveCheck.TryClip(map, West, East, out Vector3 reach);
            bool terrain = PlayerbotMoveCheck.TerrainRoute(map, West, East, Options, out _);
            return (whole, clip, reach, terrain);
        });
        Assert.False(result.Whole);
        Assert.True(result.Clip);
        Assert.InRange(result.Reach.X, 14f, 15f);
        Assert.False(result.Terrain, "a stepped route that leaves the mesh was accepted whole");
    }

    /// <summary>A leg that stays on the mesh is unchanged.</summary>
    [Fact]
    public async Task ALegOnTheMesh_IsUnchanged()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, SolidLayout, new M2Wall(0, 1000f));
        await world.PlaceAsync(West);
        (bool Whole, Vector3 Reach) result = await world.Host.OnWorldAsync(() =>
        {
            bool whole = PlayerbotMoveCheck.CanStep(world.Player.Map!, West, East);
            PlayerbotMoveCheck.TryClip(world.Player.Map!, West, East, out Vector3 reach);
            return (whole, reach);
        });
        Assert.True(result.Whole);
        Assert.Equal(East, result.Reach);
    }

    /// <summary>
    /// A leg that runs off the loaded mesh to ground with no tile (not loaded, or none) is not judged by the mesh, which only knows
    /// "nothing here": the line of sight alone applies, so the stepper still crosses to such ground.
    /// </summary>
    [Fact]
    public async Task ALegOntoGroundWithoutATile_FallsBackToLineOfSightOnly()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, EdgeLayout, new M2Wall(0, 1000f));
        Vector3 start = new(520, 10, 0), beyond = new(560, 10, 0); // world X 533.33 and on is another navigation tile, not loaded
        await world.PlaceAsync(start);
        (bool Whole, bool Terrain) result = await world.Host.OnWorldAsync(() =>
            (PlayerbotMoveCheck.CanStep(world.Player.Map!, start, beyond),
                PlayerbotMoveCheck.TerrainRoute(world.Player.Map!, start, beyond, Options, out _)));
        Assert.True(result.Whole);
        Assert.True(result.Terrain);
    }

    /// <summary>The line of sight still applies there: a doodad wall on ground without a tile blocks the leg.</summary>
    [Fact]
    public async Task ALegOntoGroundWithoutATile_IsStillBlockedByADoodad()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, EdgeLayout, new M2Wall(0, 545f));
        Vector3 start = new(520, 10, 0), beyond = new(560, 10, 0);
        await world.PlaceAsync(start);
        (bool Whole, Vector3 Reach) result = await world.Host.OnWorldAsync(() =>
        {
            bool whole = PlayerbotMoveCheck.CanStep(world.Player.Map!, start, beyond);
            PlayerbotMoveCheck.TryClip(world.Player.Map!, start, beyond, out Vector3 reach);
            return (whole, reach);
        });
        Assert.False(result.Whole);
        Assert.InRange(result.Reach.X, 543f, 545f);
    }

    /// <summary>A start more than three yards off the mesh cannot be judged by it: line of sight alone.</summary>
    [Fact]
    public async Task AStartOffTheMesh_IsJudgedByLineOfSightOnly()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, SolidLayout, new M2Wall(0, 1000f));
        await world.PlaceAsync(West);
        Vector3 off = new(80, 80, 0);
        bool whole = await world.Host.OnWorldAsync(() => PlayerbotMoveCheck.CanStep(world.Player.Map!, off, off + new Vector3(10, 0, 0)));
        Assert.True(whole);
    }
}
