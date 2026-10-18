using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Terrain;

public sealed class ClientTerrainLayerSourceTests
{
    private static ClientTerrainLayerSource Source(TerrainFixture fixture) => new(fixture.Service(), fixture.Log);

    [Fact]
    public void Layers_AreTheTwoReferenceTerrainLayers()
    {
        ClientTerrainLayerSource source = Source(new TerrainFixture());

        Assert.Equal(new[] { LayerIds.RefTerrain, LayerIds.RefLiquid }, source.Layers);
        Assert.All(source.Layers, id => Assert.True(LayerCatalog.TryGet(id, out LayerInfo info) && info.View == LayerView.Client));
    }

    [Fact]
    public void AvailableTiles_AreTheTilesWithFilesAndNeverTheGlobalTile()
    {
        var fixture = new TerrainFixture();
        ClientTerrainLayerSource source = Source(fixture);

        IReadOnlyCollection<TileKey> terrain = source.AvailableTiles(LayerIds.RefTerrain, 0);
        IReadOnlyCollection<TileKey> liquid = source.AvailableTiles(LayerIds.RefLiquid, 0);

        Assert.Equal(new[] { TerrainFixture.Goldshire, TerrainFixture.Flat }, terrain);
        Assert.Equal(terrain, liquid);
        Assert.Empty(source.AvailableTiles(LayerIds.RefTerrain, TerrainFixture.DungeonMap));
        Assert.Throws<ArgumentException>(() => source.AvailableTiles(LayerIds.Terrain, 0));
    }

    [Fact]
    public void Build_Terrain_ReturnsTheTilesGeometryAndWhereItCameFrom()
    {
        var fixture = new TerrainFixture();
        ClientTerrainLayerSource source = Source(fixture);

        LayerBuild build = source.Build(LayerIds.RefTerrain, TerrainFixture.Goldshire, CancellationToken.None);

        Assert.Equal(TileState.Ok, build.Status.State);
        Assert.Equal(LayerIds.RefTerrain, build.Status.LayerId);
        Assert.Equal(TerrainFixture.Goldshire, build.Status.Tile);
        GeometryChunk chunk = Assert.Single(build.Chunks);

        // 128 x 128 squares, four of them (one hole bit) cut out, four triangles each.
        Assert.Equal(((128 * 128) - 4) * 4, chunk.TriangleCount);
        Assert.Equal(build.Status.Primitives, chunk.TriangleCount + chunk.LineCount + chunk.PointCount);
        Assert.Equal("World\\Maps\\Azeroth\\Azeroth_32_48.adt", chunk.Stats["path"]!.GetValue<string>());
        Assert.Equal("memory", chunk.Stats["archive"]!.GetValue<string>());
        Assert.True(chunk.Stats["bytes"].AsInt() > 4000);
        Assert.Equal(4, chunk.Stats["holeSquares"].AsInt());
        Assert.Equal("Elwynn Forest", chunk.Attributes[(uint)TerrainFixture.HoleChunk]["area"]!.GetValue<string>());
        chunk.Validate();
    }

    [Fact]
    public void Build_Liquid_DrawsEachLayerAndLeavesTheTerrainOut()
    {
        ClientTerrainLayerSource source = Source(new TerrainFixture());

        LayerBuild build = source.Build(LayerIds.RefLiquid, TerrainFixture.Goldshire, CancellationToken.None);

        Assert.Equal(TileState.Ok, build.Status.State);
        GeometryChunk chunk = Assert.Single(build.Chunks);
        Assert.Equal(LayerIds.RefLiquid, chunk.LayerId);

        // River: rows 0..3 of the chunk (32 squares); ocean: its one square. Two triangles each.
        Assert.Equal((32 + 1) * 2, chunk.TriangleCount);
        Assert.Equal(1, chunk.Stats["stackedChunks"].AsInt());
        Assert.Equal(1, chunk.Stats["darkWaterSquares"].AsInt());
        Assert.Equal("memory", chunk.Stats["archive"]!.GetValue<string>());
    }

    [Fact]
    public void Build_Liquid_OfATileWithoutLiquidIsEmptyNotMissing()
    {
        ClientTerrainLayerSource source = Source(new TerrainFixture());

        LayerBuild build = source.Build(LayerIds.RefLiquid, TerrainFixture.Flat, CancellationToken.None);

        Assert.Equal(TileState.Empty, build.Status.State);
        Assert.Empty(build.Chunks);
        Assert.Contains("no liquid", build.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ReportsMissingTilesWithTheirReason()
    {
        var fixture = new TerrainFixture();
        ClientTerrainLayerSource source = Source(fixture);

        LayerBuild absent = source.Build(LayerIds.RefTerrain, TerrainFixture.ListedButAbsent, CancellationToken.None);
        LayerBuild notListed = source.Build(LayerIds.RefLiquid, TerrainFixture.NotListed, CancellationToken.None);
        LayerBuild unknownMap = source.Build(LayerIds.RefTerrain, new TileKey(55, 1, 1), CancellationToken.None);
        LayerBuild noWdt = source.Build(LayerIds.RefTerrain, new TileKey(1, 1, 1), CancellationToken.None);

        Assert.Equal(TileState.Missing, absent.Status.State);
        Assert.Contains("Azeroth_32_49.adt", absent.Status.Detail, StringComparison.Ordinal);
        Assert.Equal(TileState.Missing, notListed.Status.State);
        Assert.Contains("does not list", notListed.Status.Detail, StringComparison.Ordinal);
        Assert.Equal(TileState.Missing, unknownMap.Status.State);
        Assert.Contains("55", unknownMap.Status.Detail, StringComparison.Ordinal);
        Assert.Equal(TileState.Missing, noWdt.Status.State);
        Assert.Empty(absent.Chunks);
    }

    [Fact]
    public void Build_WithoutAnyClientFilesIsMissingWithTheReason()
    {
        var source = new ClientTerrainLayerSource(new ClientTerrainService(() => null, () => null, NullLogger.Instance), NullLogger.Instance);

        LayerBuild build = source.Build(LayerIds.RefTerrain, TerrainFixture.Goldshire, CancellationToken.None);
        TileStatus peek = source.PeekStatus(LayerIds.RefTerrain, TerrainFixture.Goldshire);

        Assert.Equal(TileState.Missing, build.Status.State);
        Assert.Contains("No client files", build.Status.Detail, StringComparison.Ordinal);
        Assert.Equal(TileState.Missing, peek.State);
    }

    [Fact]
    public void Build_RejectsAFileTheReaderRefusesWithTheReadersMessage()
    {
        var fixture = new TerrainFixture();
        fixture.Files.Add(ClientMapPaths.AdtPath("Azeroth", TerrainFixture.Flat), [0x52, 0x45, 0x56, 0x4D, 4, 0, 0, 0, 99, 0, 0, 0]);
        ClientTerrainLayerSource source = Source(fixture);

        LayerBuild build = source.Build(LayerIds.RefTerrain, TerrainFixture.Flat, CancellationToken.None);

        Assert.Equal(TileState.Rejected, build.Status.State);
        Assert.Contains("MVER is 99", build.Status.Detail, StringComparison.Ordinal);
        Assert.Contains("Azeroth_33_48.adt", build.Status.Detail, StringComparison.Ordinal);
        Assert.Empty(build.Chunks);
        Assert.DoesNotContain(fixture.Log.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public void Build_RejectsAnUnauthorizedFileWithoutThrowing()
    {
        var fixture = new TerrainFixture();
        var counting = new CountingFiles(fixture.Files);
        counting.Unauthorized.Add(ClientMapPaths.AdtPath("Azeroth", TerrainFixture.Flat));
        var source = new ClientTerrainLayerSource(new ClientTerrainService(() => counting, () => fixture.Maps, fixture.Log), fixture.Log);

        LayerBuild build = source.Build(LayerIds.RefTerrain, TerrainFixture.Flat, CancellationToken.None);

        Assert.Equal(TileState.Rejected, build.Status.State);
        Assert.Contains("access-denied", build.Status.Detail, StringComparison.Ordinal);
        Assert.Empty(build.Chunks);
        Assert.DoesNotContain(fixture.Log.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public void Build_RejectsAFileWhoseHeightsCannotBeDrawn()
    {
        var fixture = new TerrainFixture();
        var spec = new AdtSpec
        {
            Tile = TerrainFixture.Flat,
            BaseHeight = (row, col) => row == 1 && col == 1 ? float.MaxValue : 0f,
            Outer = (row, col, y, x) => row == 1 && col == 1 && y == 4 && x == 4 ? float.MaxValue : 0f,
        };
        fixture.Files.Add(ClientMapPaths.AdtPath("Azeroth", TerrainFixture.Flat), SyntheticTerrain.Adt(spec).Bytes);
        ClientTerrainLayerSource source = Source(fixture);

        LayerBuild build = source.Build(LayerIds.RefTerrain, TerrainFixture.Flat, CancellationToken.None);

        Assert.Equal(TileState.Rejected, build.Status.State);
        Assert.Contains("MCNK 17", build.Status.Detail, StringComparison.Ordinal);
        Assert.Contains(fixture.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("refused", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Log.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public void Build_OfTheGlobalTileIsNotApplicable()
    {
        ClientTerrainLayerSource source = Source(new TerrainFixture());

        foreach (string layer in source.Layers)
        {
            LayerBuild build = source.Build(layer, TileKey.Global(TerrainFixture.DungeonMap), CancellationToken.None);
            TileStatus peek = source.PeekStatus(layer, TileKey.Global(0));

            Assert.Equal(TileState.NotApplicable, build.Status.State);
            Assert.Empty(build.Chunks);
            Assert.Equal(TileState.NotApplicable, peek.State);
        }
    }

    [Fact]
    public void Build_RefusesAnUnknownLayerAnInvalidTileAndHonoursCancellation()
    {
        ClientTerrainLayerSource source = Source(new TerrainFixture());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<ArgumentException>(() => source.Build(LayerIds.Terrain, TerrainFixture.Goldshire, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => source.Build(LayerIds.RefTerrain, new TileKey(0, 64, 0), CancellationToken.None));
        Assert.Throws<ArgumentException>(() => source.Build(LayerIds.RefTerrain, new TileKey(0, -1, 3), CancellationToken.None));
        Assert.Throws<ArgumentException>(() => source.PeekStatus("nope", TerrainFixture.Goldshire));
        Assert.Throws<ArgumentException>(() => source.PeekStatus(LayerIds.RefTerrain, new TileKey(0, 70, 70)));
        Assert.Throws<OperationCanceledException>(() => source.Build(LayerIds.RefTerrain, TerrainFixture.Goldshire, cts.Token));
    }

    [Fact]
    public void PeekStatus_NeverReadsAnAdt()
    {
        var fixture = new TerrainFixture();
        var counting = new CountingFiles(fixture.Files);
        var source = new ClientTerrainLayerSource(new ClientTerrainService(() => counting, () => fixture.Maps, fixture.Log), fixture.Log);

        TileStatus listed = source.PeekStatus(LayerIds.RefTerrain, TerrainFixture.Goldshire);
        TileStatus notListed = source.PeekStatus(LayerIds.RefLiquid, TerrainFixture.NotListed);
        source.Build(LayerIds.RefTerrain, TerrainFixture.Goldshire, CancellationToken.None);
        int readsAfterBuild = counting.Reads;
        TileStatus afterBuild = source.PeekStatus(LayerIds.RefTerrain, TerrainFixture.Goldshire);

        Assert.Equal(TileState.Unknown, listed.State);
        Assert.Equal(TileState.Missing, notListed.State);
        Assert.Contains("does not list", notListed.Detail, StringComparison.Ordinal);
        Assert.Equal(TileState.Unknown, afterBuild.State);
        Assert.Equal(readsAfterBuild, counting.Reads);
    }

    [Fact]
    public void PeekStatus_ReportsARefusedWdtAsRejected()
    {
        var fixture = new TerrainFixture();
        fixture.Files.Add(ClientMapPaths.WdtPath("Azeroth"), [1, 2, 3]);
        ClientTerrainLayerSource source = Source(fixture);

        TileStatus status = source.PeekStatus(LayerIds.RefTerrain, TerrainFixture.Goldshire);

        Assert.Equal(TileState.Rejected, status.State);
        Assert.Contains("Azeroth.wdt", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void WorksBehindTheSharedLayerCache()
    {
        var fixture = new TerrainFixture();
        var registry = new LayerRegistry();
        registry.Add(Source(fixture));
        var cache = new LayerCache(registry);

        LayerBuild build = cache.Get(LayerIds.RefTerrain, TerrainFixture.Goldshire, CancellationToken.None);
        IReadOnlyList<byte[]> wire = cache.GetWire(LayerIds.RefTerrain, TerrainFixture.Goldshire, CancellationToken.None);
        TileStatus peek = cache.Peek(LayerIds.RefTerrain, TerrainFixture.Goldshire);
        TileStatus missing = cache.Peek(LayerIds.RefLiquid, TerrainFixture.NotListed);

        Assert.Equal(TileState.Ok, build.Status.State);
        byte[] single = Assert.Single(wire);
        GeometryChunk back = GeometryWire.Read(single);
        Assert.Equal(build.Chunks[0].TriangleCount, back.TriangleCount);
        Assert.Equal(TileState.Ok, peek.State);
        Assert.Equal(TileState.Missing, missing.State);
        Assert.Equal(new[] { LayerIds.RefTerrain, LayerIds.RefLiquid }, registry.Available);
    }

    [Fact]
    public void ManyThreadsBuildingTheSameTilesGetEqualResults()
    {
        var fixture = new TerrainFixture();
        var counting = new CountingFiles(fixture.Files);
        var source = new ClientTerrainLayerSource(new ClientTerrainService(() => counting, () => fixture.Maps, fixture.Log), fixture.Log);
        var results = new LayerBuild[48];

        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 12 }, i =>
        {
            string layer = i % 2 == 0 ? LayerIds.RefTerrain : LayerIds.RefLiquid;
            TileKey tile = (i / 2) % 2 == 0 ? TerrainFixture.Goldshire : TerrainFixture.Flat;
            results[i] = source.Build(layer, tile, CancellationToken.None);
        });

        for (int i = 0; i < results.Length; i++)
        {
            LayerBuild reference = results[i % 4];
            Assert.Equal(reference.Status, results[i].Status);
            Assert.Equal(reference.Chunks.Sum(c => c.VertexCount), results[i].Chunks.Sum(c => c.VertexCount));
        }

        // One WDT read, then one read per tile however many layers and threads asked.
        Assert.Equal(3, counting.Reads);
    }
}
