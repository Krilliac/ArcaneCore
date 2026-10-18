using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Game.Maps.Templates;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>.map parsing and lookups (vmangos GridMap.cpp) on synthetic files.</summary>
public sealed class TerrainTests : IDisposable
{
    private const float X = -110f;
    private const float Y = -130f;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "arcanecore-terrain-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    /// <summary>The fractional V9 row a world X falls on inside its tile (GridMap: 128 * (32 - x / SIZE_OF_GRIDS)).</summary>
    private static float RowOf(float x)
    {
        float row = 128 * (32 - (x / GridDefines.SizeOfGrids));
        return row - ((int)row & ~127);
    }

    [Fact]
    public void NoHeightSection_GivesTheGridHeightEverywhere()
    {
        TerrainTile tile = TerrainTile.Parse(new MapFileBuilder { GridHeight = 42f }.Build());
        Assert.Equal(42f, tile.GetHeight(X, Y));
        Assert.Equal(42f, tile.GetHeight(-500f, -20f));
    }

    [Fact]
    public void FloatHeights_InterpolateOverTheTriangles()
    {
        // A plane rising one unit per V9 row: every triangle gives row + fraction.
        var builder = new MapFileBuilder
        {
            V9 = MapFileBuilder.Fill(129 * 129, (_, row, _) => row, 129),
            V8 = MapFileBuilder.Fill(128 * 128, (_, row, _) => row + 0.5f, 128),
        };
        TerrainTile tile = TerrainTile.Parse(builder.Build());

        Assert.Equal(RowOf(X), tile.GetHeight(X, Y), 3);
        Assert.Equal(RowOf(-7.3f), tile.GetHeight(-7.3f, -400.9f), 3);
    }

    [Fact]
    public void Uint16Heights_AreScaledBetweenGridHeightAndMax()
    {
        var builder = new MapFileBuilder
        {
            GridHeight = 0f,
            GridMaxHeight = 655.35f, // multiplier 0.01
            V9 = Enumerable.Range(0, 129 * 129).Select(i => (ushort)(i / 129 * 100)).ToArray(),
            V8 = Enumerable.Range(0, 128 * 128).Select(i => (ushort)((i / 128 * 100) + 50)).ToArray(),
        };
        TerrainTile tile = TerrainTile.Parse(builder.Build());

        Assert.Equal(RowOf(X), tile.GetHeight(X, Y), 2);
    }

    [Fact]
    public void Uint8Heights_AreScaledBetweenGridHeightAndMax()
    {
        var builder = new MapFileBuilder
        {
            GridHeight = 100f,
            GridMaxHeight = 355f, // multiplier 1
            V9 = Enumerable.Repeat((byte)7, 129 * 129).ToArray(),
            V8 = Enumerable.Repeat((byte)7, 128 * 128).ToArray(),
        };
        TerrainTile tile = TerrainTile.Parse(builder.Build());

        Assert.Equal(107f, tile.GetHeight(X, Y), 3);
    }

    [Fact]
    public void Holes_InAFloatMap_HaveNoHeight()
    {
        var builder = new MapFileBuilder
        {
            V9 = new float[129 * 129],
            V8 = new float[128 * 128],
            Holes = Enumerable.Repeat((ushort)0xFFFF, 256).ToArray(),
        };
        TerrainTile tile = TerrainTile.Parse(builder.Build());

        Assert.Equal(TerrainTile.InvalidHeightValue, tile.GetHeight(X, Y));
    }

    [Fact]
    public void AreaFlags_ArePerCell_OrTheGridAreaWithoutThem()
    {
        TerrainTile perCell = TerrainTile.Parse(new MapFileBuilder
        {
            AreaFlags = Enumerable.Range(0, 256).Select(i => (ushort)(1000 + i)).ToArray(),
        }.Build());
        int lx = (int)(16 * (32 - (X / GridDefines.SizeOfGrids))) & 15;
        int ly = (int)(16 * (32 - (Y / GridDefines.SizeOfGrids))) & 15;
        Assert.Equal((ushort)(1000 + (lx * 16) + ly), perCell.GetAreaFlag(X, Y));

        TerrainTile whole = TerrainTile.Parse(new MapFileBuilder { GridArea = 41 }.Build());
        Assert.Equal((ushort)41, whole.GetAreaFlag(X, Y));
    }

    [Theory]
    [InlineData(30f, LiquidStatus.UnderWater)]
    [InlineData(49f, LiquidStatus.InWater)]
    [InlineData(50f, LiquidStatus.WaterWalk)]
    [InlineData(60f, LiquidStatus.AboveWater)]
    [InlineData(17f, LiquidStatus.NoWater)] // more than 2 below the ground
    public void LiquidStatus_FollowsTheDepthBands(float z, LiquidStatus expected)
    {
        TerrainTile tile = TerrainTile.Parse(new MapFileBuilder
        {
            GridHeight = 20f,
            HasLiquid = true,
            LiquidGlobalFlags = (byte)LiquidTypeFlags.Water,
            LiquidGlobalEntry = 1,
            LiquidLevel = 50f,
        }.Build());

        Assert.Equal(expected, tile.GetLiquidStatus(X, Y, z, LiquidTypeFlags.None, out LiquidData data));
        if (expected != LiquidStatus.NoWater)
        {
            Assert.Equal(new LiquidData(1, LiquidTypeFlags.Water, 50f, 20f), data);
        }

        Assert.Equal(50f, tile.GetLiquidLevel(X, Y));
        Assert.Equal(LiquidTypeFlags.Water, tile.GetTerrainType(X, Y));
    }

    [Fact]
    public void LiquidStatus_HonoursTheRequiredType_AndPerCellHeights()
    {
        var builder = new MapFileBuilder
        {
            GridHeight = 0f,
            HasLiquid = true,
            LiquidFlags = Enumerable.Repeat((byte)LiquidTypeFlags.Magma, 256).ToArray(),
            LiquidEntries = Enumerable.Repeat((ushort)19, 256).ToArray(),
            LiquidHeights = Enumerable.Repeat(12.5f, 128 * 128).ToArray(),
        };
        TerrainTile tile = TerrainTile.Parse(builder.Build());

        Assert.Equal(LiquidStatus.NoWater, tile.GetLiquidStatus(X, Y, 5f, LiquidTypeFlags.Water, out _));
        Assert.Equal(LiquidStatus.UnderWater, tile.GetLiquidStatus(X, Y, 5f, LiquidTypeFlags.Magma, out LiquidData data));
        Assert.Equal(19u, data.Entry);
        Assert.Equal(12.5f, tile.GetLiquidLevel(X, Y));
    }

    [Fact]
    public void BadOrTruncatedFiles_AreRejected()
    {
        byte[] good = new MapFileBuilder { V9 = new float[129 * 129], V8 = new float[128 * 128] }.Build();
        byte[] badMagic = (byte[])good.Clone();
        badMagic[4] = (byte)'x';

        Assert.Throws<InvalidDataException>(() => TerrainTile.Parse(badMagic));
        Assert.Throws<InvalidDataException>(() => TerrainTile.Parse(good.AsSpan(0, 2000)));
        Assert.Throws<InvalidDataException>(() => TerrainTile.Parse(good.AsSpan(0, 20)));
    }

    [Fact]
    public void Names_AndTileIndices_FollowTheExtractor()
    {
        Assert.Equal("0013231.map", TerrainTile.FileName(1, 32, 31));
        Assert.Equal((32, 32), TerrainTile.TileOf(X, Y));
        Assert.Equal((31, 32), TerrainTile.TileOf(100f, -100f));
        Assert.Null(TerrainTile.TileOf(float.NaN, 0));
        Assert.Equal((31, 31), TerrainTile.TileOf(GridDefines.ComputeGridCoord(100f, 100f)));
    }

    [Fact]
    public void Manager_WithoutData_FailsSoft()
    {
        var missing = new TerrainManager(Path.Combine(_dataDir, "nowhere"));
        Assert.False(missing.Enabled);
        Assert.Equal(TerrainTile.InvalidHeightValue, missing.For(0).GetHeight(X, Y, 0));
        Assert.Equal((0u, 0u), missing.For(0).GetZoneAndAreaId(X, Y, 0));

        var none = new TerrainManager(null);
        Assert.False(none.Enabled);
        Assert.False(none.For(1).GetTile(X, Y).HasData);
    }

    [Fact]
    public void Manager_LoadsTilesFromTheDataDirectory_AndSkipsBadOnes()
    {
        string maps = Directory.CreateDirectory(Path.Combine(_dataDir, "maps")).FullName;
        File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(0, 32, 32)), new MapFileBuilder
        {
            GridHeight = 55f,
            GridArea = 41,
            HasLiquid = true,
            LiquidGlobalFlags = (byte)LiquidTypeFlags.Water,
            LiquidLevel = 60f,
        }.Build());
        File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(0, 31, 32)), [1, 2, 3]);

        var manager = new TerrainManager(_dataDir);
        var registry = new MapRegistry([new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", "")]);
        manager.Areas = new AreaTable([new AreaTemplate(12, 0, 0, 41, 0, 1, "Elwynn Forest", 0, 0), new AreaTemplate(87, 0, 12, 42, 0, 1, "Goldshire", 0, 0)], registry);
        TerrainInfo terrain = manager.For(0);

        Assert.True(manager.Enabled);
        Assert.Equal(55f, terrain.GetHeight(X, Y, 0));
        Assert.Equal((12u, 12u), terrain.GetZoneAndAreaId(X, Y, 0));
        Assert.Equal(LiquidStatus.InWater, terrain.GetLiquidStatus(X, Y, 59f, LiquidTypeFlags.AllLiquids, out _));
        Assert.Equal(60f, terrain.GetWaterOrGroundLevel(X, Y, GridDefines.MaxHeight));
        Assert.Equal(1, manager.FilesLoaded);

        // The corrupt neighbour is logged and left empty; the server keeps going.
        Assert.Equal(TerrainTile.InvalidHeightValue, terrain.GetHeight(100f, Y, 0));
        Assert.Equal(1, manager.FilesLoaded);
    }

    /// <summary>
    /// <see cref="TerrainManager.ReadZoneAndAreaIdDetached"/> reads the file itself and must equal the cached lookup. Every one of the
    /// 256 cells of the tile has its own area, so a wrong cell (swapped axes, wrong index) or wrong tile gives a different answer.
    /// </summary>
    [Fact]
    public void DetachedZoneAndAreaRead_EqualsTheCachedLookup_ForEveryCell_AndFailsSoftWhereThereIsNoTile()
    {
        string maps = Directory.CreateDirectory(Path.Combine(_dataDir, "maps")).FullName;
        File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(0, 32, 32)), new MapFileBuilder
        {
            AreaFlags = Enumerable.Range(0, 256).Select(i => (ushort)(1000 + i)).ToArray(),
        }.Build());
        File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(0, 31, 32)), [1, 2, 3]); // corrupt
        File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(1, 32, 32)), new MapFileBuilder { GridArea = 41 }.Build());

        // Area 5000 + i is the cell with flag 1000 + i; the zone of cell i is 100 + i / 16 (a parent area, itself a zone).
        var areas = new List<AreaTemplate> { new(41, 1, 41, 41, 0, 1, "Whole", 0, 0) };
        for (int i = 0; i < 256; i++)
            areas.Add(new AreaTemplate((uint)(5000 + i), 0, (uint)(100 + (i / 16)), (uint)(1000 + i), 0, 1, "Cell" + i, 0, 0));
        for (uint z = 100; z < 116; z++)
            areas.Add(new AreaTemplate(z, 0, 0, 0, 0, 1, "Zone" + z, 0, 0));
        var registry = new MapRegistry([new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", "")]);
        var manager = new TerrainManager(_dataDir) { Areas = new AreaTable(areas, registry) };
        TerrainInfo cached = manager.For(0);

        // The centre of every cell of tile (32, 32): x in (-533.3, 0], y in (-533.3, 0].
        float cell = GridDefines.SizeOfGrids / 16f;
        var seen = new HashSet<uint>();
        for (int cx = 0; cx < 16; cx++)
        {
            for (int cy = 0; cy < 16; cy++)
            {
                float x = -((cx + 0.5f) * cell), y = -((cy + 0.5f) * cell);
                (uint, uint) expected = cached.GetZoneAndAreaId(x, y, 0f);
                (uint zone, uint area) = manager.ReadZoneAndAreaIdDetached(0, x, y, manager.Areas);
                Assert.Equal(expected, (zone, area));
                Assert.InRange(area, 5000u, 5255u);
                Assert.Equal(100u + ((area - 5000) / 16), zone);
                seen.Add(area);
            }
        }

        Assert.Equal(256, seen.Count); // every cell answered with its own area

        // The detached read does not touch the tile cache.
        var fresh = new TerrainManager(_dataDir) { Areas = manager.Areas };
        Assert.Equal(cached.GetZoneAndAreaId(-300f, -20f, 0f), fresh.ReadZoneAndAreaIdDetached(0, -300f, -20f, fresh.Areas));
        Assert.Equal(0, fresh.FilesLoaded);

        // A tile-wide area, another map's file, a corrupt file, a missing file, outside the grid and not-a-number: the cached lookup's answer.
        Assert.Equal((41u, 41u), manager.ReadZoneAndAreaIdDetached(1, X, Y, manager.Areas));
        Assert.Equal(manager.For(1).GetZoneAndAreaId(X, Y, 0f), manager.ReadZoneAndAreaIdDetached(1, X, Y, manager.Areas));
        Assert.Equal((0u, 0u), manager.ReadZoneAndAreaIdDetached(0, 100f, Y, manager.Areas)); // tile 31,32 is corrupt
        Assert.Equal(cached.GetZoneAndAreaId(100f, Y, 0f), manager.ReadZoneAndAreaIdDetached(0, 100f, Y, manager.Areas));
        Assert.Equal((0u, 0u), manager.ReadZoneAndAreaIdDetached(0, 3000f, 3000f, manager.Areas)); // no file
        Assert.Equal((0u, 0u), manager.ReadZoneAndAreaIdDetached(0, 40000f, 0f, manager.Areas));
        Assert.Equal((0u, 0u), manager.ReadZoneAndAreaIdDetached(0, float.NaN, 0f, manager.Areas));
        Assert.Equal((0u, 0u), new TerrainManager(null).ReadZoneAndAreaIdDetached(0, X, Y, manager.Areas));
        Assert.Throws<ArgumentNullException>(() => manager.ReadZoneAndAreaIdDetached(0, X, Y, null!));
    }

    [Fact]
    public void Tiles_AreRefCounted_AndFreedByTheCleanUp()
    {
        TerrainInfo terrain = new TerrainManager(null).For(0);
        terrain.Load(10, 20);
        terrain.Load(10, 20);
        terrain.GetTile(11, 20); // on-demand, unreferenced
        Assert.Equal(2, terrain.RefCount(10, 20));

        terrain.Unload(10, 20);
        terrain.CleanUp(TerrainInfo.CleanUpIntervalMs);
        Assert.True(terrain.IsTileLoaded(10, 20));
        Assert.False(terrain.IsTileLoaded(11, 20));

        terrain.Unload(10, 20);
        terrain.CleanUp(TerrainInfo.CleanUpIntervalMs - 1);
        Assert.True(terrain.IsTileLoaded(10, 20)); // not a full minute yet
        terrain.CleanUp(1);
        Assert.False(terrain.IsTileLoaded(10, 20));
        Assert.Equal(0, terrain.RefCount(10, 20));
    }

    /// <summary>
    /// One <see cref="TerrainInfo"/> serves every instance of a map id (vmangos <c>Map::Map</c> takes it from
    /// <c>sTerrainMgr.LoadTerrain(id)</c>, Map.cpp:127), and vmangos cleans every map id's terrain once per world update
    /// (<c>TerrainManager::Update</c>, GridMap.cpp:1275-1280, at the end of <c>World::Update</c>, World.cpp:2147-2149): the minute
    /// does not shrink with the number of instances.
    /// </summary>
    [Fact]
    public void CleanUp_RunsOncePerWorldTick_HoweverManyInstancesShareTheTerrain()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map first = world.GetMap(0, 1);
        Map second = world.GetMap(0, 2);
        TerrainInfo terrain = first.Terrain;
        Assert.Same(terrain, second.Terrain);
        terrain.GetTile(11, 20); // on-demand, unreferenced

        for (int tick = 1; tick < TerrainInfo.CleanUpIntervalMs / 1000; tick++)
        {
            world.RunTick(1000);
        }

        Assert.True(terrain.IsTileLoaded(11, 20), "59 s of world time: not a full minute yet");
        world.RunTick(1000);
        Assert.False(terrain.IsTileLoaded(11, 20));
    }

    /// <summary>
    /// The clean-up belongs to the world, not to a map: once the last instance of a map id is gone its terrain's tiles are still freed
    /// (and the <c>TerrainInfo</c> goes with them, <see cref="Terrain_GoesWithTheLastInstanceOfItsMapId_AndTheNextInstanceGetsAWorkingOne"/>).
    /// </summary>
    [Fact]
    public void CleanUp_FreesTheTilesOfAMapId_WhoseInstancesAreAllGone()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map instance = world.GetMap(0, 1);
        TerrainInfo terrain = instance.Terrain;
        instance.AddObject(new TestUnit(1, X, Y)); // its grid references the tile
        (int tileX, int tileY) = TerrainTile.TileOf(X, Y)!.Value;
        Assert.Equal(1, terrain.RefCount(tileX, tileY));

        world.UnloadMap(instance);
        world.RunTick(0);
        Assert.Null(world.FindMap(0, 1));
        Assert.Equal(0, terrain.RefCount(tileX, tileY));
        Assert.True(terrain.IsTileLoaded(tileX, tileY));

        for (int tick = 0; tick < TerrainInfo.CleanUpIntervalMs / 1000; tick++)
        {
            world.RunTick(1000);
        }

        Assert.False(terrain.IsTileLoaded(tileX, tileY));
    }

    /// <summary>
    /// vmangos deletes a map id's terrain with its last map: every <c>Map</c> holds its <c>TerrainInfo</c> (taken in <c>Map::Map</c>,
    /// Map.cpp:127 and :153, <c>Referencable</c>, GridMap.h:123-130), <c>Map::~Map</c> lets go of it (Map.cpp:76-78) and
    /// <c>TerrainManager::UnloadTerrain</c> erases the one no map holds any more (GridMap.cpp:1255-1273). Here it goes with the clean-up
    /// that frees its last tile, and the next instance of the map id gets a new terrain that reads the tile files again.
    /// </summary>
    [Fact]
    public void Terrain_GoesWithTheLastInstanceOfItsMapId_AndTheNextInstanceGetsAWorkingOne()
    {
        string maps = Directory.CreateDirectory(Path.Combine(_dataDir, "maps")).FullName;
        (int tileX, int tileY) = TerrainTile.TileOf(X, Y)!.Value;
        File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(0, tileX, tileY)), new MapFileBuilder { GridHeight = 55f }.Build());
        using WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.Maps.DataDirectory = _dataDir;
        TerrainManager manager = WorldMaps.Of(world).Terrain;
        Map first = world.GetMap(0, 1);
        Map second = world.GetMap(0, 2);
        TerrainInfo terrain = first.Terrain;
        first.AddObject(new TestUnit(1, X, Y));
        second.AddObject(new TestUnit(2, X, Y));
        Assert.Equal(2, terrain.RefCount(tileX, tileY));

        // One instance goes: the other still holds the terrain, so it stays through the clean-up.
        world.UnloadMap(first);
        for (int tick = 0; tick < TerrainInfo.CleanUpIntervalMs / 1000; tick++)
        {
            world.RunTick(1000);
        }

        Assert.Same(terrain, manager.For(0));
        Assert.True(terrain.IsTileLoaded(tileX, tileY));

        // The last one goes: its tile waits for the clean-up's minute, and so does the terrain.
        world.UnloadMap(second);
        world.RunTick(0);
        Assert.Null(world.FindMap(0, 2));
        Assert.Same(terrain, manager.For(0));
        Assert.True(terrain.IsTileLoaded(tileX, tileY));
        for (int tick = 0; tick < TerrainInfo.CleanUpIntervalMs / 1000; tick++)
        {
            world.RunTick(1000);
        }

        Assert.False(terrain.IsTileLoaded(tileX, tileY));

        // The next instance of the map id gets a new terrain: the one the manager hands out, reading the tile file again.
        Map third = world.GetMap(0, 3);
        Assert.NotSame(terrain, third.Terrain);
        Assert.Same(third.Terrain, manager.For(0));
        int filesBefore = manager.FilesLoaded;
        third.AddObject(new TestUnit(3, X, Y));
        Assert.Equal(1, third.Terrain.RefCount(tileX, tileY));
        Assert.Equal(55f, third.GetHeight(X, Y, 0f));
        Assert.Equal(filesBefore + 1, manager.FilesLoaded);
        Assert.Equal(0, terrain.RefCount(tileX, tileY));
    }

    /// <summary>
    /// <c>TerrainManager::UnloadTerrain</c> erases only a terrain no map holds (GridMap.cpp:1264-1270): an instance made again before
    /// the clean-up has freed the released terrain's tiles takes that same terrain, tiles and all, and the terrain stays with it.
    /// </summary>
    [Fact]
    public void ReleasedTerrain_TakenAgainBeforeItsTilesAreFreed_Stays()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        TerrainManager manager = WorldMaps.Of(world).Terrain;
        Map first = world.GetMap(0, 1);
        TerrainInfo terrain = first.Terrain;
        first.AddObject(new TestUnit(1, X, Y));
        (int tileX, int tileY) = TerrainTile.TileOf(X, Y)!.Value;
        world.UnloadMap(first);
        world.RunTick(0);
        Assert.Equal(0, terrain.MapRefCount);

        Map second = world.GetMap(0, 2);
        Assert.Same(terrain, second.Terrain);
        Assert.Equal(1, terrain.MapRefCount);
        Assert.True(terrain.IsTileLoaded(tileX, tileY));
        for (int tick = 0; tick < 2 * TerrainInfo.CleanUpIntervalMs / 1000; tick++)
        {
            world.RunTick(1000);
        }

        Assert.False(terrain.IsTileLoaded(tileX, tileY)); // nothing references it: the clean-up still frees the tile
        Assert.Equal(1, manager.TerrainCount);
        Assert.Same(terrain, manager.For(0));
        Assert.Same(terrain, second.Terrain);
    }

    /// <summary>With <c>GridUnload</c> off vmangos never unloads a terrain (GridMap.cpp:1257-1258); its clean-up still frees the tiles.</summary>
    [Fact]
    public void Terrain_StaysAfterItsLastInstance_WithGridUnloadOff()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.Maps.GridUnload = false;
        TerrainManager manager = WorldMaps.Of(world).Terrain;
        Map instance = world.GetMap(0, 1);
        TerrainInfo terrain = instance.Terrain;
        instance.AddObject(new TestUnit(1, X, Y));
        (int tileX, int tileY) = TerrainTile.TileOf(X, Y)!.Value;

        world.UnloadMap(instance);
        for (int tick = 0; tick <= TerrainInfo.CleanUpIntervalMs / 1000; tick++)
        {
            world.RunTick(1000);
        }

        Assert.Null(world.FindMap(0, 1));
        Assert.Equal(0, terrain.MapRefCount);
        Assert.False(terrain.IsTileLoaded(tileX, tileY));
        Assert.Equal(1, manager.TerrainCount);
        Assert.Same(terrain, world.GetMap(0, 2).Terrain);
    }

    /// <summary>
    /// The manager lets a terrain go only once its last map has released it and no tile of it is held: a grid reference left over (an
    /// unload that failed half way) keeps it. A terrain no map ever held (a <c>.go</c> or a bot lookup asking <c>For</c>) is kept, as
    /// vmangos keeps a <c>LoadTerrain</c> without a map (only <c>Map::~Map</c> calls <c>UnloadTerrain</c>, Map.cpp:76-78).
    /// </summary>
    [Fact]
    public void Manager_LetsATerrainGo_AfterItsLastMap_OnceNoTileIsHeld()
    {
        var manager = new TerrainManager(null);
        TerrainInfo lookedUp = manager.For(1);
        lookedUp.GetTile(10, 20);
        TerrainInfo held = manager.Acquire(0);
        TerrainInfo alsoHeld = manager.Acquire(0);
        Assert.Same(held, alsoHeld);
        Assert.Equal(2, held.MapRefCount);
        held.Load(10, 20);

        manager.Release(held, gridUnload: true);
        manager.Update(TerrainInfo.CleanUpIntervalMs);
        Assert.Same(held, manager.For(0)); // another map still holds it

        manager.Release(held, gridUnload: true);
        manager.Update(TerrainInfo.CleanUpIntervalMs);
        Assert.Equal(0, held.MapRefCount);
        Assert.True(held.IsTileLoaded(10, 20)); // its grid still references the tile
        Assert.Equal(2, manager.TerrainCount);

        held.Unload(10, 20);
        manager.Update(TerrainInfo.CleanUpIntervalMs - 1);
        Assert.Equal(2, manager.TerrainCount); // the tile waits for the clean-up's minute, and so does the terrain
        manager.Update(1);
        Assert.False(held.IsTileLoaded(10, 20));
        Assert.Equal(1, manager.TerrainCount);
        Assert.Same(lookedUp, manager.For(1));
        Assert.False(lookedUp.IsTileLoaded(10, 20));
        Assert.NotSame(held, manager.For(0));
    }
}
