using System.Diagnostics;
using System.Collections.Concurrent;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// A teleport starts reading its destination's terrain tiles on the thread pool (<see cref="WorldMaps.PrefetchArrival"/>), so the tick that
/// places the player installs them instead of reading the files: the live hang of 2026-10-25 03:20 was 11.4 s of a far teleport's
/// <c>Map.AddPlayer</c> in <c>File.ReadAllBytes</c>. The teleport itself is unchanged (<see cref="TeleportServiceTests"/>).
/// </summary>
public sealed class TeleportArrivalPrefetchTests : IDisposable
{
    private static readonly MapContent Content = new(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
        ],
        [], [], [], []);

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "arcanecore-arrival-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private WorldRuntime CreateWorld(float prefetchDistance = 200f)
    {
        string maps = Directory.CreateDirectory(Path.Combine(_dataDir, "maps")).FullName;
        foreach ((uint map, float x, float y) in new[] { (1u, 1000f, 2000f), (0u, 0f, 0f), (0u, -1500f, 10f) })
        {
            (int tx, int ty) = TerrainTile.TileOf(x, y)!.Value;
            File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(map, tx, ty)), new MapFileBuilder { GridHeight = 30f }.Build());
        }

        var options = new WorldRuntimeOptions { UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0, CommandTimeBudgetMs = 0 };
        options.Maps.DataDirectory = _dataDir;
        options.Maps.GridPrefetchDistance = prefetchDistance;
        var world = new WorldRuntime(options, new RecordingSaveQueue(), NullLogger<WorldRuntime>.Instance);
        WorldMaps.Of(world).Load(Content);
        return world;
    }

    private static void WaitForPrefetch(TerrainManager terrain, uint mapId, int tileX, int tileY)
    {
        var clock = Stopwatch.StartNew();
        bool finished = false;
        while (terrain.IsPrefetched(mapId, tileX, tileY, out finished) && !finished && clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            Thread.Sleep(5);
        }

        Assert.True(finished);
    }

    /// <summary>
    /// A far teleport asks for the destination tile when it starts; the arrival takes the prefetched tile (a prefetch hit, one tile load),
    /// and the player lands on the new map as before.
    /// </summary>
    [Fact]
    public void AFarTeleport_ReadsTheDestinationTileBeforeTheArrival_AndTheArrivalTakesIt()
    {
        using WorldRuntime world = CreateWorld();
        TerrainManager terrain = WorldMaps.Of(world).Terrain;
        var teleports = new TeleportService(world, _ => { }, _ => { });
        Player a = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
        world.AddPlayer(a);
        world.RunTick(50);
        int loadsBefore = terrain.FilesLoaded;
        (int tx, int ty) = TerrainTile.TileOf(1000f, 2000f)!.Value;

        Assert.True(teleports.TeleportTo(a, 1, 1000, 2000, 30, 2f));
        Assert.True(terrain.IsPrefetched(1, tx, ty, out _)); // asked at once, before the transfer is even sent
        world.RunTick(50);
        WaitForPrefetch(terrain, 1, tx, ty);
        Assert.True(teleports.HandleWorldportAck(a));
        world.RunTick(50);

        Assert.Same(world.GetMap(1), a.Map);
        Assert.True(world.GetMap(1).Terrain.IsTileLoaded(tx, ty));
        Assert.Equal(1, terrain.PrefetchHits);
        Assert.Equal(loadsBefore + 1, terrain.FilesLoaded);
        Assert.False(terrain.IsPrefetched(1, tx, ty, out _));
    }

    /// <summary>A near teleport far across the map asks for its destination too; with the approach prefetch off (0), nothing is asked.</summary>
    [Fact]
    public void ANearTeleport_AsksForItsDestination_AndAPrefetchDistanceOf0_TurnsItOff()
    {
        (int tx, int ty) = TerrainTile.TileOf(-1500f, 10f)!.Value;
        using (WorldRuntime world = CreateWorld())
        {
            var teleports = new TeleportService(world, _ => { }, _ => { });
            Player a = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
            world.AddPlayer(a);
            world.RunTick(50);
            Assert.False(WorldMaps.Of(world).Terrain.IsPrefetched(0, tx, ty, out _));
            Assert.True(teleports.TeleportTo(a, 0, -1500, 10, 30, 0f));
            Assert.True(teleports.IsBeingTeleportedNear(a));
            Assert.True(WorldMaps.Of(world).Terrain.IsPrefetched(0, tx, ty, out _));
        }

        using (WorldRuntime world = CreateWorld(prefetchDistance: 0f))
        {
            var teleports = new TeleportService(world, _ => { }, _ => { });
            Player a = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
            world.AddPlayer(a);
            world.RunTick(50);
            Assert.True(teleports.TeleportTo(a, 0, -1500, 10, 30, 0f));
            Assert.False(WorldMaps.Of(world).Terrain.IsPrefetched(0, tx, ty, out _));
        }
    }

    /// <summary>
    /// A teleport asks the model service and the navigation service for the destination's tiles as an arrival (their own prefetch budget, not
    /// the approach prefetch's), and a service whose prefetch fails neither stops the other nor the teleport.
    /// </summary>
    [Fact]
    public void ATeleport_AsksTheModelAndNavigationServicesForItsDestinationAsAnArrival_EvenWhenOneFails()
    {
        using WorldRuntime world = CreateWorld();
        var models = new RecordingService(fails: true);
        var mesh = new RecordingService(fails: false);
        WorldCollision.Of(world).Install(lineOfSight: models, pathfinder: mesh);
        var teleports = new TeleportService(world, _ => { }, _ => { });
        Player a = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
        world.AddPlayer(a);
        world.RunTick(50);
        (int tx, int ty) = TerrainTile.TileOf(1000f, 2000f)!.Value;

        Assert.True(teleports.TeleportTo(a, 1, 1000, 2000, 30, 2f));

        Assert.Contains((1u, tx, ty), mesh.Arrivals);
        Assert.Contains((1u, tx, ty), models.Arrivals); // asked, then threw
        Assert.Equal(models.Arrivals.OrderBy(t => t), mesh.Arrivals.OrderBy(t => t)); // the failing one did not skip tiles of the other
        Assert.Empty(mesh.Approaches);
        Assert.Empty(models.Approaches);
        Assert.True(teleports.IsBeingTeleportedFar(a) || teleports.IsBeingTeleportedNear(a));
    }

    private sealed class RecordingService(bool fails) : ILineOfSight, IPathfinder, ICollisionTilePrefetch
    {
        public ConcurrentQueue<(uint, int, int)> Arrivals { get; } = new();

        public ConcurrentQueue<(uint, int, int)> Approaches { get; } = new();

        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = default;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => null;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.None(start);

        public void Prefetch(uint mapId, int tileX, int tileY) => Approaches.Enqueue((mapId, tileX, tileY));

        public void PrefetchArrival(uint mapId, int tileX, int tileY)
        {
            Arrivals.Enqueue((mapId, tileX, tileY));
            if (fails)
            {
                throw new InvalidOperationException("injected");
            }
        }
    }
}
