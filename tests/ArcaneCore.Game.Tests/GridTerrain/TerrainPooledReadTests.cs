using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// The detached terrain reads the bot population calls in bulk (<see cref="TerrainManager.ReadTileDetached"/>,
/// <see cref="TerrainManager.ReadZoneAndAreaIdDetached"/>) read the <c>.map</c> file into a pooled buffer: a float-height tile is over
/// 85 KB, so a fresh array per read was a large object heap allocation (issue #1491). Synthetic files (<see cref="MapFileBuilder"/>).
/// </summary>
public sealed class TerrainPooledReadTests : IDisposable
{
    private const int Reads = 20;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "arcanecore-terrain-pooled-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    /// <summary>Tile (32, 32) of map 0 with float heights and per-cell areas; returns the file's bytes.</summary>
    private byte[] WriteFloatTile()
    {
        string maps = Directory.CreateDirectory(Path.Combine(_dataDir, "maps")).FullName;
        byte[] file = new MapFileBuilder
        {
            AreaFlags = Enumerable.Range(0, 256).Select(i => (ushort)(1000 + i)).ToArray(),
            V9 = MapFileBuilder.Fill(129 * 129, (i, r, c) => (r * 0.25f) + (c * 0.5f), 129),
            V8 = MapFileBuilder.Fill(128 * 128, (i, r, c) => (r * 0.25f) + (c * 0.5f) + 0.125f, 128),
        }.Build();
        File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(0, 32, 32)), file);
        return file;
    }

    private static AreaTable Areas()
    {
        var areas = new List<AreaTemplate>();
        for (int i = 0; i < 256; i++)
            areas.Add(new AreaTemplate((uint)(5000 + i), 0, 100, (uint)(1000 + i), 0, 1, "Cell" + i, 0, 0));
        areas.Add(new AreaTemplate(100, 0, 0, 0, 0, 1, "Zone", 0, 0));
        var registry = new MapRegistry([new MapTemplate(0, 0, MapType.Common, 12, 0, 0, -1, 0, 0, "Eastern Kingdoms", "")]);
        return new AreaTable(areas, registry);
    }

    [Fact]
    public void ReadTileDetached_DoesNotAllocateTheFileOnEveryRead()
    {
        byte[] file = WriteFloatTile();
        Assert.True(file.Length >= 85_000, $"the test tile must be a large object heap size file ({file.Length} bytes)");
        var manager = new TerrainManager(_dataDir);
        Assert.True(manager.ReadTileDetached(0, 32, 32).HasData); // warm the pool and the JIT

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Reads; i++)
            Assert.True(manager.ReadTileDetached(0, 32, 32).HasData);
        long perRead = (GC.GetAllocatedBytesForCurrentThread() - before) / Reads;

        // The parsed tile keeps its own copies (about the file's size); a fresh file array per read doubles it.
        Assert.True(perRead < file.Length * 3L / 2, $"{perRead} bytes per read for a {file.Length}-byte file");
    }

    [Fact]
    public void ReadZoneAndAreaIdDetached_DoesNotAllocateTheFileOnEveryRead()
    {
        byte[] file = WriteFloatTile();
        AreaTable areas = Areas();
        var manager = new TerrainManager(_dataDir);
        Assert.Equal(100u, manager.ReadZoneAndAreaIdDetached(0, -100f, -100f, areas).ZoneId); // warm the pool and the JIT

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Reads; i++)
            Assert.Equal(100u, manager.ReadZoneAndAreaIdDetached(0, -100f, -100f, areas).ZoneId);
        long perRead = (GC.GetAllocatedBytesForCurrentThread() - before) / Reads;

        Assert.True(perRead < file.Length * 3L / 2, $"{perRead} bytes per read for a {file.Length}-byte file");
    }

    [Fact]
    public void PooledReads_AnswerExactlyWhatTheWholeFileParseAnswers()
    {
        byte[] file = WriteFloatTile();
        AreaTable areas = Areas();
        var manager = new TerrainManager(_dataDir);
        TerrainTile reference = TerrainTile.Parse(file);
        TerrainTile read = manager.ReadTileDetached(0, 32, 32);
        for (int k = 0; k < 64; k++)
        {
            // Positions inside tile (32, 32): world x and y in (-533.33, 0].
            float x = -1f - (k * 8.1f);
            float y = -1f - (((k * 37) % 64) * 8.1f);
            Assert.Equal(reference.GetHeight(x, y), read.GetHeight(x, y));
            Assert.Equal(reference.GetAreaFlag(x, y), read.GetAreaFlag(x, y));
            Assert.Equal(areas.GetZoneAndAreaId(reference.GetAreaFlag(x, y), 0), manager.ReadZoneAndAreaIdDetached(0, x, y, areas));
        }

        Assert.False(manager.ReadTileDetached(0, 40, 40).HasData);
        Assert.Equal((0u, 0u), manager.ReadZoneAndAreaIdDetached(0, -4500f, -4500f, areas));
    }
}
