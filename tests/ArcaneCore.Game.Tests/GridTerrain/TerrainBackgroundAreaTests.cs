using System.Diagnostics;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// The zone lookup that never reads a terrain file on the calling thread (<see cref="TerrainInfo.TryGetZoneAndAreaIdWithoutLoading"/>), for bot
/// planning over far positions: the live hang stacks of 2026-10-25/26 had the world thread in <c>File.ReadAllBytes</c> under
/// <c>ElevatorSurvey.RegionAt</c> for 3-8 s at a time. Synthetic <c>.map</c> files (<see cref="MapFileBuilder"/>).
/// </summary>
public sealed class TerrainBackgroundAreaTests : IDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "arcanecore-terrain-areas-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    /// <summary>Tile (32, 32) with its own area in every cell, (31, 32) corrupt, (33, 32) a tile-wide area; none for (40, 40).</summary>
    private (string Maps, AreaTable Areas) WriteTiles()
    {
        string maps = Directory.CreateDirectory(Path.Combine(_dataDir, "maps")).FullName;
        File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(0, 32, 32)), new MapFileBuilder
        {
            AreaFlags = Enumerable.Range(0, 256).Select(i => (ushort)(1000 + i)).ToArray(),
        }.Build());
        File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(0, 31, 32)), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(0, 33, 32)), new MapFileBuilder { GridArea = 41 }.Build());

        var areas = new List<AreaTemplate> { new(41, 0, 0, 41, 0, 1, "Whole", 0, 0), new(12, 0, 0, 0, 0, 1, "Linked", 0, 0) };
        for (int i = 0; i < 256; i++)
            areas.Add(new AreaTemplate((uint)(5000 + i), 0, (uint)(100 + (i / 16)), (uint)(1000 + i), 0, 1, "Cell" + i, 0, 0));
        for (uint z = 100; z < 116; z++)
            areas.Add(new AreaTemplate(z, 0, 0, 0, 0, 1, "Zone" + z, 0, 0));
        var registry = new MapRegistry([new MapTemplate(0, 0, MapType.Common, 12, 0, 0, -1, 0, 0, "Eastern Kingdoms", "")]);
        return (maps, new AreaTable(areas, registry));
    }

    private static void WaitForBackgroundReads(TerrainManager manager)
    {
        var clock = Stopwatch.StartNew();
        while (manager.BackgroundAreaReadsInFlight > 0 && clock.Elapsed < ReadTimeout)
        {
            Thread.Sleep(5);
        }

        Assert.Equal(0, manager.BackgroundAreaReadsInFlight);
    }

    /// <summary>The centre of every cell of a tile (32 - x / 533.33 = tile, per axis).</summary>
    private static IEnumerable<(float X, float Y)> CellCentres(int tileX, int tileY)
    {
        float cell = GridDefines.SizeOfGrids / 16f;
        for (int cx = 0; cx < 16; cx++)
            for (int cy = 0; cy < 16; cy++)
                yield return (((32 - tileX) * GridDefines.SizeOfGrids) - ((cx + 0.5f) * cell), ((32 - tileY) * GridDefines.SizeOfGrids) - ((cy + 0.5f) * cell));
    }

    /// <summary>
    /// A position on a tile not in memory: false at first, no file read on the calling thread and no tile cached or counted; once the pool
    /// has read the area section, the answer of the full lookup (which reads the whole tile) for every cell of a per-cell tile, a tile-wide
    /// area, a corrupt file and a missing one. The full lookup runs on a second manager so it cannot fill the first one's cache.
    /// </summary>
    [Fact]
    public void ALookupOnATileNotInMemory_ReadsNothingOnTheCallingThread_ThenAnswersLikeTheFullLookup()
    {
        (_, AreaTable areas) = WriteTiles();
        var manager = new TerrainManager(_dataDir) { Areas = areas };
        var reference = new TerrainManager(_dataDir) { Areas = areas };
        TerrainInfo terrain = manager.For(0);
        int readsHere = TerrainManager.AreaReadsOnThisThread;
        (int X, int Y)[] tiles = [(32, 32), (31, 32), (33, 32), (40, 40)];

        foreach ((int tx, int ty) in tiles)
        {
            (float x, float y) = CellCentres(tx, ty).First();
            Assert.False(terrain.TryGetZoneAndAreaIdWithoutLoading(x, y, 0f, out (uint, uint) unknown));
            Assert.Equal((0u, 0u), unknown);
        }

        Assert.Equal(0, manager.FilesLoaded);
        Assert.All(tiles, t => Assert.False(terrain.IsTileLoaded(t.X, t.Y)));
        WaitForBackgroundReads(manager);
        Assert.Equal(readsHere, TerrainManager.AreaReadsOnThisThread); // every area section was read on the pool

        var seen = new HashSet<uint>();
        foreach ((int tx, int ty) in tiles)
        {
            foreach ((float x, float y) in CellCentres(tx, ty))
            {
                Assert.True(terrain.TryGetZoneAndAreaIdWithoutLoading(x, y, 0f, out (uint ZoneId, uint AreaId) found));
                Assert.Equal(reference.For(0).GetZoneAndAreaId(x, y, 0f), found);
                seen.Add(found.AreaId);
            }
        }

        Assert.Equal(256 + 2, seen.Count); // 256 cells, the tile-wide area, the linked zone (corrupt and missing files)
        Assert.Equal(0, manager.FilesLoaded);
        Assert.All(tiles, t => Assert.False(terrain.IsTileLoaded(t.X, t.Y)));
        Assert.Equal(readsHere, TerrainManager.AreaReadsOnThisThread);
    }

    /// <summary>A tile in memory, a position outside the map and a manager without data answer at once, as the full lookup does.</summary>
    [Fact]
    public void ATileInMemory_OutsideTheMap_OrNoData_AnswersAtOnce()
    {
        (_, AreaTable areas) = WriteTiles();
        var manager = new TerrainManager(_dataDir) { Areas = areas };
        TerrainInfo terrain = manager.For(0);
        (float x, float y) = CellCentres(32, 32).Skip(17).First();
        terrain.Load(32, 32);
        Assert.True(terrain.TryGetZoneAndAreaIdWithoutLoading(x, y, 0f, out (uint, uint) loaded));
        Assert.Equal(terrain.GetZoneAndAreaId(x, y, 0f), loaded);
        Assert.Equal(1, manager.FilesLoaded);

        Assert.True(terrain.TryGetZoneAndAreaIdWithoutLoading(40000f, 0f, 0f, out (uint, uint) outside));
        Assert.Equal(terrain.GetZoneAndAreaId(40000f, 0f, 0f), outside);

        var empty = new TerrainManager(null) { Areas = areas };
        Assert.True(empty.For(0).TryGetZoneAndAreaIdWithoutLoading(x, y, 0f, out (uint, uint) none));
        Assert.Equal((12u, 12u), none); // no terrain: flag 0, the map's linked zone
        Assert.Equal(0, empty.BackgroundAreaReadsInFlight);
        Assert.Equal(1, manager.FilesLoaded);
    }

    /// <summary>The warm-up reads every tile file of the map on the pool, so the first lookup on any of them answers.</summary>
    [Fact]
    public void TheWarmUp_ReadsEveryTileOfTheMapInTheBackground()
    {
        (_, AreaTable areas) = WriteTiles();
        var manager = new TerrainManager(_dataDir) { Areas = areas };
        var reference = new TerrainManager(_dataDir) { Areas = areas };
        TerrainInfo terrain = manager.For(0);
        int readsHere = TerrainManager.AreaReadsOnThisThread;
        terrain.WarmAreasInBackground();
        terrain.WarmAreasInBackground(); // once per map
        WaitForBackgroundReads(manager);

        foreach ((int tx, int ty) in new[] { (32, 32), (31, 32), (33, 32) })
        {
            (float x, float y) = CellCentres(tx, ty).Last();
            Assert.True(terrain.TryGetZoneAndAreaIdWithoutLoading(x, y, 0f, out (uint, uint) found));
            Assert.Equal(reference.For(0).GetZoneAndAreaId(x, y, 0f), found);
        }

        Assert.Equal(readsHere, TerrainManager.AreaReadsOnThisThread);
        Assert.Equal(0, manager.FilesLoaded);
        Assert.Equal(0, manager.BackgroundAreaReadsInFlight);
    }

    /// <summary>The area-only read gives what the parsed tile does for every cell, and rejects what the parser rejects in the header.</summary>
    [Fact]
    public void TheAreaOnlyRead_EqualsTheParsedTile()
    {
        (string maps, _) = WriteTiles();
        foreach ((int tx, int ty) in new[] { (32, 32), (33, 32) })
        {
            string path = Path.Combine(maps, TerrainTile.FileName(0, tx, ty));
            TerrainTile parsed = TerrainTile.Parse(File.ReadAllBytes(path));
            ushort[] flags = TerrainTile.ReadAreaFlags(path);
            foreach ((float x, float y) in CellCentres(tx, ty))
                Assert.Equal(parsed.GetAreaFlag(x, y), flags[TerrainTile.AreaCellIndex(x, y)]);
        }

        Assert.Throws<InvalidDataException>(() => TerrainTile.ReadAreaFlags(Path.Combine(maps, TerrainTile.FileName(0, 31, 32))));
    }
}
