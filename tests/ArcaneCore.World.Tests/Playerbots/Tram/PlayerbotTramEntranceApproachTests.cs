using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Tests.Collision;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Tram;

/// <summary>
/// Live 2026-10-12: trips gave up with "tram: no way to the Deeprun Tram - Ironforge Instance entrance" from Dun Morogh
/// (-6598, 314 and -6494, 324), Loch Modan (-5568, -453), Wetlands (-4716, -888) and, less often, Stormwind. The walk aimed at the
/// entrance trigger's centre. That point is the middle of the instance volume, and Ironforge's floats 6.3 yards above the continent
/// floor the exit puts a player on. On a mesh of that floor the pathfinder does not accept it as a place to stand
/// (<see cref="NavMeshPathfinder.NearExtents"/> is 5 yards up, <see cref="PlayerbotNavigation.ProjectedEndYards"/> is 6), so a way
/// longer than <see cref="PlayerbotOptions.MaxRouteYards"/> to it is refused whole. <see cref="TramPlatform.Approach"/> is the floor
/// point inside the volume, on the landing's side.
/// </summary>
public sealed class PlayerbotTramEntranceApproachTests
{
    /// <summary>The live sample whose straight distance to Ironforge's entrance is inside one route and past the first step (Loch Modan).</summary>
    private static readonly Vector3 LochModan = new(-5568.0f, -452.8f, 413.9f);

    private static TramPlatform Platform(uint entranceId)
    {
        AreaTriggerTemplate entrance = Assert.Single(TramTestData.Triggers, t => t.Id == entranceId);
        AreaTriggerTeleport into = Assert.Single(TramTestData.Teleports, t => t.Id == entranceId);
        AreaTriggerTeleport back = Assert.Single(TramTestData.Teleports, t => t.TargetMap == entrance.MapId && t.Id != entranceId
            && Vector2.Distance(new(t.TargetX, t.TargetY), new(entrance.X, entrance.Y)) <= TramNetwork.PairYards);
        AreaTriggerTemplate exit = Assert.Single(TramTestData.Triggers, t => t.Id == back.Id);
        return new TramPlatform(entrance, into, exit, back);
    }

    /// <summary>
    /// The continent floor beside the entrance is the exit's landing (the only height the world data stands a player at there; the
    /// Stormwind mesh projects the trigger onto that floor). Ironforge's centre is not a walkable point on it, and not on the landing's
    /// side of the volume. The approach point is.
    /// </summary>
    [Fact]
    public void IronforgeEntranceCentre_IsNotAWalkablePointOnTheContinentSide()
    {
        TramPlatform ironforge = Platform(2175);
        TramPlatform stormwind = Platform(2173);
        float ironforgeRise = ironforge.EntranceCentre.Z - ironforge.Arrival.Z;
        float stormwindRise = stormwind.EntranceCentre.Z - stormwind.Arrival.Z;
        Assert.True(ironforgeRise > NavMeshPathfinder.NearExtents.Y, $"Ironforge centre is only {ironforgeRise:F2} yards above its floor");
        Assert.True(ironforgeRise > PlayerbotNavigation.ProjectedEndYards);
        Assert.True(stormwindRise < NavMeshPathfinder.NearExtents.Y); // the w22 floor, still inside the near box

        using var fixture = new NavMeshFixture();
        NavMeshPathfinder nav = FloorUnder(fixture, ironforge);
        var options = new PathOptions { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, AllowPartial = true };
        PathResult toCentre = nav.FindPath(0, ironforge.Arrival, ironforge.EntranceCentre, options);
        Assert.Equal(PathType.Incomplete, toCentre.Type);
        Assert.True(MathF.Abs(toCentre.End.Z - ironforge.Arrival.Z) < 0.25f, $"mesh end {toCentre.End} is not the continent floor");
        Assert.True(MathF.Abs(ironforge.EntranceCentre.Z - toCentre.End.Z) > PlayerbotNavigation.ProjectedEndYards);
        Assert.False(PlayerbotNavigation.EndsAt(toCentre, ironforge.EntranceCentre));
        Assert.True(Vector2.Distance(Flat(toCentre.End), Flat(ironforge.EntranceCentre)) <= 1f);
        // The only point offered for the centre is under it, inside the instance volume, not toward the landing.
        Assert.True(AreaTriggerZone.Contains(ironforge.Entrance, 0, toCentre.End.X, toCentre.End.Y, toCentre.End.Z));
        Assert.True(Vector2.Distance(Flat(toCentre.End), Flat(ironforge.Arrival))
            > Vector2.Distance(Flat(ironforge.Approach), Flat(ironforge.Arrival)));

        Vector3 approach = ironforge.Approach;
        Assert.True(AreaTriggerZone.Contains(ironforge.Entrance, 0, approach.X, approach.Y, approach.Z));
        Assert.Equal(ironforge.Arrival.Z, approach.Z);
        Assert.True(Vector2.Distance(Flat(approach), Flat(ironforge.Arrival)) + 1f
            < Vector2.Distance(Flat(ironforge.EntranceCentre), Flat(ironforge.Arrival)));
        PathResult toApproach = nav.FindPath(0, ironforge.Arrival, approach, options);
        Assert.Equal(PathType.Normal, toApproach.Type);
        Assert.True(Vector3.Distance(toApproach.End, approach) < 1f, $"approach landed at {toApproach.End}, not {approach}");

        // Stormwind's centre still fits the old height bound; its approach is the same kind of point, so both entrances agree.
        Vector3 stormwindApproach = stormwind.Approach;
        Assert.True(AreaTriggerZone.Contains(stormwind.Entrance, 0, stormwindApproach.X, stormwindApproach.Y, stormwindApproach.Z));
        Assert.Equal(stormwind.Arrival.Z, stormwindApproach.Z);
        Assert.True(Vector2.Distance(Flat(stormwindApproach), Flat(stormwind.Arrival))
            < Vector2.Distance(Flat(stormwind.EntranceCentre), Flat(stormwind.Arrival)));
    }

    /// <summary>
    /// A Loch Modan start (live, 12 of the refusals) is inside one route of the entrance, so the walk plans to the point itself. The
    /// mesh's answer to the centre is a way longer than the route bound that stops on the floor under it: not an arrival, refused whole.
    /// The same way to the approach, which is that floor, is walked.
    /// </summary>
    [Fact]
    public async Task ALochModanWayToTheIronforgeCentre_IsRefused_TheContinentSideApproachIsWalked()
    {
        TramPlatform ironforge = Platform(2175);
        await using TramTestWorld world = await TramTestWorld.StartAsync(pathfinder: new FloorProjection(ironforge.Arrival.Z));
        await world.PlaceAsync(0, LochModan);
        PlayerbotOptions options = new()
        {
            Enabled = true, ThinkIntervalMs = 100, MaxActionsPerTick = 12, MaxPathPoints = 128, MaxRouteYards = 2000, MoveSpeed = 7,
        };

        float toCentre = await world.OnWorldAsync(() =>
            PlayerbotNavigation.TryPlanToward(world.Player, ironforge.EntranceCentre, options, out PlayerbotRoute? route) ? route!.Distance : -1f);
        Assert.Equal(-1f, toCentre);

        float toApproach = await world.OnWorldAsync(() =>
            PlayerbotNavigation.TryPlanToward(world.Player, ironforge.Approach, options, out PlayerbotRoute? route) ? route!.Distance : -1f);
        Assert.InRange(toApproach, 1f, options.MaxRouteYards);
    }

    /// <summary>The trip walks to the approach, not the trigger centre (the open mesh of the fixture answers the point it was given).</summary>
    [Fact]
    public async Task TheTrip_WalksToTheApproach_NotTheTriggerCentre()
    {
        await using TramTestWorld world = await TramTestWorld.StartAsync();
        await world.PlaceAsync(0, TramTestData.StormwindExitLanding);
        var brain = new PlayerbotBrain(world.Session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100 });
        Assert.True(await world.OnWorldAsync(() => PlayerbotTram.TryBegin(world.Session, world.Player,
            new Vector3(-4918.9f, -982.5f, 501.5f), TramTestData.ZoneAt)));
        Vector3? end = await world.TickAsync(() =>
        {
            brain.Update(100);
            return PlayerbotTram.TripOf(world.Player)?.Route is { } route ? route.Points[^1] : (Vector3?)null;
        });
        TramPlatform stormwind = Platform(2173);
        Assert.NotNull(end);
        Assert.True(Vector3.Distance(end.Value, stormwind.Approach) < 1f, $"walked toward {end}, not {stormwind.Approach}");
        Assert.True(Vector3.Distance(end.Value, stormwind.EntranceCentre) > 2f);
        Assert.True(AreaTriggerZone.Contains(stormwind.Entrance, 0, end.Value.X, end.Value.Y, end.Value.Z));
    }

    /// <summary>A flat continent floor at the exit landing's height, covering the entrance and the landing (one Detour tile).</summary>
    private static NavMeshPathfinder FloorUnder(NavMeshFixture fixture, TramPlatform platform)
    {
        const float size = 5f;
        float x0 = -4900f, y0 = -1360f;
        (int X, int Y)? tile = TerrainTile.TileOf(platform.EntranceCentre.X, platform.EntranceCentre.Y);
        Assert.NotNull(tile);
        // Detour x is world Y, Detour y is world X (NavMeshFormat.ToRecast), on the mmap origin of (0, 0, 0).
        int detourX = (int)MathF.Floor(platform.EntranceCentre.Y / 533.3333f);
        int detourY = (int)MathF.Floor(platform.EntranceCentre.X / 533.3333f);
        var floor = new CellTile(detourX, detourY, x0, y0, 18, 16, Size: size) { Height = (_, _) => platform.Arrival.Z };
        fixture.WriteParams(0);
        fixture.WriteTile(0, tile.Value.X, tile.Value.Y, floor);
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(0, tile.Value.X, tile.Value.Y));
        return nav;
    }

    /// <summary>
    /// The mesh's answer: a road bending south, longer than the route bound, ending on the floor under the point asked for. A point
    /// already on that floor is a whole way; one above it is the same road, incomplete.
    /// </summary>
    private sealed class FloorProjection(float floor) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            bool onFloor = MathF.Abs(end.Z - floor) <= 0.5f;
            Vector3 at = onFloor ? end : new Vector3(end.X, end.Y, floor);
            Vector3 bend = ((start + at) * 0.5f) + new Vector3(0f, -1000f, 0f);
            var points = new List<Vector3> { start };
            for (int i = 1; i <= 12; i++) points.Add(Vector3.Lerp(start, bend, i / 12f));
            for (int i = 1; i <= 12; i++) points.Add(Vector3.Lerp(bend, at, i / 12f));
            return new PathResult(onFloor ? PathType.Normal : PathType.Incomplete, points);
        }
    }

    private static Vector2 Flat(Vector3 value) => new(value.X, value.Y);
}
