using System.Collections.Concurrent;
using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Tests.Collision;
using ArcaneCore.Game.Tests.GridTerrain;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// Creature heights stand on static models. vmangos asks <c>Map::GetHeight</c> with vmaps on (Map.cpp:3407-3411:
/// <c>TerrainInfo::GetHeightStatic(x, y, z, vmap = true)</c>, GridMap.cpp:815-871) for every creature point: the random point off the
/// navmesh (Map::GetWalkRandomPosition, Map.cpp:3381-3401, reached from RandomMovementGenerator.cpp:47) and a summon's ground
/// (WorldObject::UpdateGroundPositionZ, Object.cpp:2079-2084). A point over a WMO floor is on the floor, not on the terrain under the
/// building. Synthetic data: flat terrain at 0 in tile (31, 31) of map 0 and three 10 x 10 yd slabs (repository-authored vmaps).
/// </summary>
public sealed class CreatureModelFloorTests : IDisposable
{
    /// <summary>The top of a slab 30 yd above the terrain at (100, 100): an upper storey.</summary>
    private const float HighFloor = 30.5f;

    /// <summary>The top of a slab 4.5 yd above the terrain at (140, 100): within a wander leg's 7 yd drop of the terrain under it.</summary>
    private const float LowFloor = 4.5f;

    private const float LowX = 140f;
    private const float LowY = 100f;

    /// <summary>The top of a slab 20 yd under the terrain at (60, 100): a cellar floor.</summary>
    private const float CellarFloor = -20f;

    private const float CellarX = 60f;
    private const float CellarY = 100f;

    private readonly VMapFixture _models = new();
    private readonly string _terrain;

    public CreatureModelFloorTests()
    {
        WorldModel slab = VMapFixture.Box(new Vector3(-5, -5, -0.5f), new Vector3(5, 5, 0.5f));
        _models.Place("high", slab, new Vector3(100, 100, HighFloor - 0.5f));
        _models.Place("low", slab, new Vector3(LowX, LowY, LowFloor - 0.5f));
        _models.Place("cellar", slab, new Vector3(CellarX, CellarY, CellarFloor - 0.5f));
        _models.Write(0);
        _terrain = Path.Combine(_models.Directory, "terrain");
        Directory.CreateDirectory(Path.Combine(_terrain, "maps"));
        File.WriteAllBytes(Path.Combine(_terrain, "maps", TerrainTile.FileName(0, 31, 31)), new MapFileBuilder { GridHeight = 0f }.Build());
    }

    public void Dispose() => _models.Dispose();

    /// <summary>
    /// The model service with every read watched: a read of the model trees made without the serial gate while maps update in parallel is
    /// recorded (<see cref="WorldRuntime.HoldsSerialGate"/>). Tile loading and coverage questions pass through to the real service.
    /// </summary>
    private sealed class GateWatcher(ILineOfSight inner, WorldRuntime world) : ILineOfSight, ICollisionTileLifecycle, ICollisionTilePrefetch, ICollisionTileDemand, IModelCoverage
    {
        private int _reads;

        public ConcurrentQueue<string> OutsideGate { get; } = new();

        public int Reads => Volatile.Read(ref _reads);

        public bool Enabled => inner.Enabled;

        private void Watch(string read)
        {
            Interlocked.Increment(ref _reads);
            if (!world.HoldsSerialGate)
            {
                OutsideGate.Enqueue($"{read} on thread {Environment.CurrentManagedThreadId}");
            }
        }

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true)
        {
            Watch(nameof(IsInLineOfSight));
            return inner.IsInLineOfSight(mapId, from, to, ignoreM2);
        }

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            Watch(nameof(TryGetObjectHit));
            return inner.TryGetObjectHit(mapId, from, to, modifyDistance, out hit);
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
        {
            Watch(nameof(GetModelHeight));
            return inner.GetModelHeight(mapId, x, y, z, maxSearchDistance);
        }

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            Watch(nameof(TryGetAreaInfo));
            return inner.TryGetAreaInfo(mapId, x, y, z, out info);
        }

        public void OnTileLoaded(uint mapId, int tileX, int tileY) => (inner as ICollisionTileLifecycle)?.OnTileLoaded(mapId, tileX, tileY);

        public void OnTileUnloaded(uint mapId, int tileX, int tileY) => (inner as ICollisionTileLifecycle)?.OnTileUnloaded(mapId, tileX, tileY);

        public void Prefetch(uint mapId, int tileX, int tileY) => (inner as ICollisionTilePrefetch)?.Prefetch(mapId, tileX, tileY);

        public bool LoadTileOnDemand(uint mapId, float x, float y) => (inner as ICollisionTileDemand)?.LoadTileOnDemand(mapId, x, y) ?? false;

        public ModelCoverage CoverageAt(uint mapId, int tileX, int tileY) => (inner as IModelCoverage)?.CoverageAt(mapId, tileX, tileY) ?? ModelCoverage.None;
    }

    private WorldRuntime CreateWorld(ParallelMapOptions? parallel = null)
    {
        var world = new WorldRuntime(
            new WorldRuntimeOptions
            {
                UpdateCompressionThreshold = 0,
                AutosaveIntervalMs = 0,
                CommandTimeBudgetMs = 0,
                Maps = new MapOptions { DataDirectory = _terrain },
                Parallel = parallel ?? new ParallelMapOptions(),
            },
            new RecordingSaveQueue(),
            NullLogger<WorldRuntime>.Instance);
        WorldCollision.Of(world).Install(lineOfSight: new VMapManager(_models.Directory));
        return world;
    }

    [Fact]
    public void TheCreatureHeight_OverAModelFloor_IsTheFloor_NotTheTerrainFarBelow()
    {
        using WorldRuntime world = CreateWorld();
        Map map = world.GetMap(0);
        Assert.True(((VMapManager)map.Collision.LineOfSight).LoadTile(0, 31, 31));
        var provider = new MapCreatureHeightProvider(map);

        Assert.Equal(HighFloor, provider.GetHeight(0, 100, 100, HighFloor + 0.1f)!.Value, 3); // on the upper storey
        Assert.Equal(0f, provider.GetHeight(0, 100, 100, 3f)!.Value, 3);                     // under it: the ground
        Assert.Equal(0f, provider.GetHeight(0, 120, 100, HighFloor + 0.1f)!.Value, 3);       // beside it: the ground

        // A summon's ground (WorldObject::UpdateGroundPositionZ, +0.05) is the same floor.
        var system = new CreatureMapSystem(map, Content([Template()], []), random: new Random(1));
        map.AddUpdater(system);
        Assert.Equal(HighFloor + 0.05f, system.GroundZ(100, 100, HighFloor + 0.1f)!.Value, 3);
    }

    [Fact]
    public void TheCreatureHeight_UnderTheTerrain_WithNoFloorBelow_IsTheTerrainAbove()
    {
        // GetHeightStatic finds no model under the point and returns the terrain height (GridMap.cpp:871); GetWalkRandomPosition then
        // raises its point to it (Map.cpp:3399-3401): a point drawn up a slope is put on the slope, as on a map without models.
        using WorldRuntime world = CreateWorld();
        Map map = world.GetMap(0);
        Assert.True(((VMapManager)map.Collision.LineOfSight).LoadTile(0, 31, 31));
        var provider = new MapCreatureHeightProvider(map);

        Assert.Equal(TerrainTile.InvalidHeightValue, map.Collision.GetHeight(120, 100, -5f)); // nothing under the point
        Assert.Equal(0f, provider.GetHeight(0, 120, 100, -5f)!.Value, 3);
    }

    [Fact]
    public void ANonFinitePoint_AsksNoModel_AndBreaksNoInvariant()
    {
        // A creature with a broken position is reported by CheckNonFinitePosition; its under-map sample must not also trip
        // MapCollision.GetHeight's finite-coordinates invariant. The terrain answers, as the terrain-only provider did.
        using WorldRuntime world = CreateWorld();
        Map map = world.GetMap(0);
        Assert.True(((VMapManager)map.Collision.LineOfSight).LoadTile(0, 31, 31));
        var provider = new MapCreatureHeightProvider(map);
        using InvariantCapture capture = Invariant.Capture();

        Assert.Null(provider.GetHeight(0, float.NaN, 100, HighFloor + 0.1f));
        Assert.Equal(0f, provider.GetHeight(0, 100, 100, float.NaN)!.Value, 3);
        Assert.Empty(capture.Failures);
    }

    [Fact]
    public void AWandererOffTheNavmesh_OnALowModelFloor_WalksOnTheFloor_NotDownToTheTerrainUnderIt()
    {
        using WorldRuntime world = CreateWorld();
        CreatureMapSystem system = AddWanderer(world.GetMap(0));
        Creature wolf = Assert.Single(system.Creatures);

        for (int i = 0; i < 100 && !wolf.IsMoving; i++)
        {
            world.RunTick(50);
        }

        Assert.True(wolf.IsMoving, "the wanderer never started a leg");
        Assert.Equal(LowFloor + GroundHeight.StandOffset, wolf.Spline!.EndZ, 3); // the floor plus the stand offset (GroundHeight)
    }

    [Fact]
    public void AWandererOutsideTheSerialGate_GetsTheSameModelFloor()
    {
        // World:Parallel with CreatureMapSystem ungated: the plain wanderers' legs are drawn on the map thread (CreatureMapSystem
        // UpdateRandomMotion outside the gate); the model floor is read in the gate, never a terrain-only shortcut.
        using WorldRuntime world = CreateWorld(new ParallelMapOptions
        {
            MapUpdateThreads = 2, OwnerGuard = OwnerGuardMode.Throw, Ungated = "CreatureMapSystem", CreatureSplinePass = true,
        });
        world.UseManualClockFrom(0);
        var watcher = new GateWatcher(world.GetMap(0).Collision.LineOfSight, world);
        WorldCollision.Of(world).Install(lineOfSight: watcher);
        CreatureMapSystem system = AddWanderer(world.GetMap(0));
        Map other = world.GetMap(1); // a second map, so the maps phase runs in parallel
        other.AddPlayer(TestWorld.CreatePlayer(2, 0, 0, new FakeSession(2), mapId: 1));
        Creature wolf = Assert.Single(system.Creatures);

        string breakdown = "";
        for (int i = 0; i < 100 && !wolf.IsMoving; i++)
        {
            world.RunTick(50);
            breakdown = ((IMapUpdateBreakdown)system).DescribeLastUpdate();
        }

        Assert.True(wolf.IsMoving, "the wanderer never started a leg");
        Assert.Contains("1 wanderers outside the gate", breakdown, StringComparison.Ordinal);
        Assert.Equal(LowFloor + GroundHeight.StandOffset, wolf.Spline!.EndZ, 3);
        Assert.Equal(0, world.ParallelCounters.OwnerViolations); // the owner guard sees map ownership only, not model reads: the watcher below does
        Assert.True(watcher.Reads > 0, "the legs read no model floor at all");
        Assert.True(watcher.OutsideGate.IsEmpty, "model reads outside the serial gate: " + string.Join("; ", watcher.OutsideGate.Take(5)));
        // Terrain tile reads are not instrumented (TerrainInfo has no seam); the drape reads only tiles already in memory.
    }

    [Fact]
    public void ACreatureFallenJustThroughAModelFloorUnderTheTerrain_IsStillReportedUnderTheMap()
    {
        // The under-map sample compares with the terrain, and the models only clear a conviction (ConfirmUnderMap). With the model floor as
        // its surface it would compare with the cellar floor 8 yd over the creature's head and never ask: the fall would go unreported.
        // (8 yd: farther than the classifier's 5 yd nearby-floor allowance, so the models do not excuse it.)
        const uint entry = 949110;
        using WorldRuntime world = CreateWorld();
        Map map = world.GetMap(0);
        Assert.True(((VMapManager)map.Collision.LineOfSight).LoadTile(0, 31, 31));
        CreatureContent content = Content([Template(entry)], [Spawn(1, entry, CellarX, CellarY, CellarFloor - 8f)]);
        var system = new CreatureMapSystem(map, content, random: new Random(1));
        map.AddUpdater(system);
        map.AddPlayer(TestWorld.CreatePlayer(1, CellarX + 10f, CellarY, new FakeSession(1)));
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(
            n => n.Key == "creature/under-map" && n.Subject.StartsWith($"creature entry {entry} ", StringComparison.Ordinal));

        for (int i = 0; i < 60; i++)
        {
            world.RunTick(50);
        }

        Assert.Equal(CellarFloor - 8f, Assert.Single(system.Creatures).Z, 3);
        Assert.Contains("below terrain/mesh floor", Assert.Single(capture.Notices).Detail, StringComparison.Ordinal);
    }

    /// <summary>A wolf wandering 3 yd around the middle of the low slab (no navmesh: the point is drawn off the mesh) and a player by it.</summary>
    private static CreatureMapSystem AddWanderer(Map map)
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, LowX, LowY, LowFloor, movementType: 1, wander: 3f)]);
        var system = new CreatureMapSystem(map, content, random: new Random(1));
        map.AddUpdater(system);
        map.AddPlayer(TestWorld.CreatePlayer(1, LowX + 10f, LowY, new FakeSession(1)));
        return system;
    }
}
