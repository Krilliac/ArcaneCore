using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Game.Maps.Terrain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Terrain;

/// <summary>
/// The client terrain readers against the real 1.12.1 client and the server's own extracted <c>.map</c> files. Every
/// assertion here holds only when the real files were read: counts of compared samples, exact heights equal to the
/// server's, file names and positions of real tiles.
/// </summary>
public sealed class ClientTerrainRealDataTests(ITestOutputHelper output)
{
    /// <summary>Goldshire: server tile X 49 (from world x -9464), Y 31 (from world y 62).</summary>
    private static readonly TileKey Goldshire = new(0, 49, 31);

    private const uint Deadmines = 36;
    private const uint ZulGurub = 309;

    private static ClientTerrainService Service()
        => new(() => ClientTerrainRealData.Files, () => ClientTerrainRealData.Maps, NullLogger.Instance, 8);

    private static IEnumerable<TileKey> Block(uint mapId, int centreX, int centreY)
    {
        for (int x = centreX - 1; x <= centreX + 1; x++)
        {
            for (int y = centreY - 1; y <= centreY + 1; y++)
            {
                yield return new TileKey(mapId, x, y);
            }
        }
    }

    private ClientTerrainOracle Run(string name, IEnumerable<TileKey> tiles, bool skipTilesWithoutServerMap = false)
    {
        var oracle = new ClientTerrainOracle();
        ClientTerrainService service = Service();
        int clientOnly = 0;
        foreach (TileKey tile in tiles)
        {
            ServerMapTile? server = ClientTerrainRealData.ServerTile(tile);
            AdtLoad load = service.LoadAdt(tile);
            if (server is null)
            {
                Assert.True(skipTilesWithoutServerMap, $"The server has no .map for {tile}.");
                clientOnly += load.State == TileState.Ok ? 1 : 0;
                continue;
            }

            if (load.State != TileState.Ok)
            {
                oracle.ServerOnly(tile, load);
                continue;
            }

            oracle.Compare(tile, load.File!, server);
        }

        output.WriteLine(oracle.Describe(name));
        if (clientOnly > 0)
        {
            output.WriteLine($"  {clientOnly} listed tiles have a client ADT but no server .map");
        }

        return oracle;
    }

    private static void AssertExplained(ClientTerrainOracle oracle, int tiles)
    {
        string because = string.Join(Environment.NewLine, oracle.Examples);
        Assert.Equal(tiles, oracle.Tiles);
        Assert.Equal((long)tiles * AdtFile.ChunkTotal * AdtChunk.HeightCount, oracle.Samples);
        Assert.True(oracle.Exact > 0, "No sample matched exactly.");
        Assert.True(oracle.Unexplained == 0, because);
        Assert.True(oracle.ServerOnlyTiles == 0, because);
        Assert.True(oracle.PositionMismatchTiles == 0, because);
        Assert.True(oracle.StorageMismatches == 0, because);
        Assert.True(oracle.HoleWordMismatches == 0, because);
        Assert.True(oracle.HoleQueryMismatches == 0, because);
        Assert.True(oracle.LiquidFlagMismatches == 0, because);
        Assert.True(oracle.LiquidLevelMismatches == 0, because);
        Assert.True(oracle.NotKeptOther == 0, because);
        Assert.Equal(oracle.LayerVerticesNotKept, oracle.NotKeptReset + oracle.NotKeptMaxOfLayers + oracle.NotKeptEdgeRewrite);
    }

    [ClientTerrainRealFact]
    public void MapDbc_NamesTheMapsTheServerExtracted()
    {
        DbcMapDirectory maps = ClientTerrainRealData.Maps;
        Assert.True(maps.TryGetMap(0, out ClientMap azeroth));
        Assert.Equal("Azeroth", azeroth.Directory);
        Assert.True(maps.TryGetMap(1, out ClientMap kalimdor));
        Assert.Equal("Kalimdor", kalimdor.Directory);
        Assert.True(maps.TryGetMap(Deadmines, out ClientMap deadmines));
        Assert.Equal("DeadminesInstance", deadmines.Directory);
        Assert.True(maps.TryGetMap(ZulGurub, out ClientMap zulGurub));
        Assert.Equal("Zul'gurub", zulGurub.Directory);
        Assert.Equal(0u, azeroth.InstanceType);
        Assert.NotEqual(0u, deadmines.InstanceType);
        Assert.NotEqual(0u, zulGurub.InstanceType);
        output.WriteLine($"Map.dbc: {maps.Maps.Count} maps; {deadmines.Name} instance type {deadmines.InstanceType}, {zulGurub.Name} instance type {zulGurub.InstanceType}");

        // Every map the server extracted is in Map.dbc and has its WDT where the naming says.
        IReadOnlyList<uint> serverMaps = ClientTerrainRealData.ServerMapIds();
        Assert.True(serverMaps.Count > 4);
        foreach (uint id in serverMaps)
        {
            Assert.True(maps.TryGetMap(id, out ClientMap map), $"Map {id} has server .map files but no Map.dbc row.");
            Assert.True(ClientTerrainRealData.Files.Exists(ClientMapPaths.WdtPath(map.Directory)), $"{ClientMapPaths.WdtPath(map.Directory)} is missing.");
        }
    }

    [ClientTerrainRealFact]
    public void AdtNaming_FirstNumberIsServerTileY_ProvenByPositionsAndHeights()
    {
        ClientTerrainService service = Service();
        AdtLoad load = service.LoadAdt(Goldshire);
        Assert.Equal(TileState.Ok, load.State);
        Assert.Equal("World\\Maps\\Azeroth\\Azeroth_31_49.adt", load.Path);
        AdtFile adt = load.File!;

        // The server's tile rule puts Goldshire in X 49, Y 31; every MCNK header position agrees with that tile and
        // none agrees with the transposed one.
        Assert.Equal<(int X, int Y)?>((49, 31), TerrainTile.TileOf(-9464f, 62f));
        Assert.Equal(0, AdtGeometry.CountPositionMismatches(adt, Goldshire));
        Assert.Equal(AdtFile.ChunkTotal, AdtGeometry.CountPositionMismatches(adt, new TileKey(0, Goldshire.Y, Goldshire.X)));

        // The heights of Azeroth_31_49.adt are those of the server's 0004931.map.
        Assert.Equal("0004931.map", TerrainTile.FileName(0, Goldshire.X, Goldshire.Y));
        var same = new ClientTerrainOracle();
        same.Compare(Goldshire, adt, ClientTerrainRealData.ServerTile(Goldshire)!);
        output.WriteLine(same.Describe("Azeroth_31_49.adt vs 0004931.map"));
        AssertExplained(same, 1);

        // Against a neighbouring tile's .map the same ADT disagrees almost everywhere.
        var other = new ClientTerrainOracle();
        other.Compare(Goldshire, adt, ClientTerrainRealData.ServerTile(new TileKey(0, 49, 32))!);
        output.WriteLine(other.Describe("Azeroth_31_49.adt vs 0004932.map"));
        Assert.True(other.Unexplained > other.Samples / 2, $"{other.Unexplained} of {other.Samples}");
    }

    [ClientTerrainRealFact]
    public void Oracle_GoldshireAndItsEightNeighbours()
        => AssertExplained(Run("Azeroth 48..50 x 30..32", Block(0, Goldshire.X, Goldshire.Y)), 9);

    [ClientTerrainRealFact]
    public void Oracle_KalimdorAroundOrgrimmar()
        => AssertExplained(Run("Kalimdor 28..30 x 39..41", Block(1, 29, 40)), 9);

    [ClientTerrainRealFact]
    public void Oracle_TwoInstanceMaps()
    {
        foreach (uint mapId in new[] { Deadmines, ZulGurub })
        {
            IReadOnlyList<TileKey> tiles = ClientTerrainRealData.ServerTiles(mapId);
            Assert.NotEmpty(tiles);
            AssertExplained(Run($"map {mapId}", tiles), tiles.Count);
        }
    }

    [ClientTerrainRealFact]
    public void Oracle_EveryServerTileOfEveryMap()
    {
        ClientTerrainService service = Service();
        int total = 0;
        long notKept = 0;
        long notKeptReset = 0;
        long notKeptMax = 0;
        long notKeptEdge = 0;
        long notKeptOther = 0;
        foreach (uint mapId in ClientTerrainRealData.ServerMapIds())
        {
            IReadOnlyList<TileKey> server = ClientTerrainRealData.ServerTiles(mapId);
            ClientTerrainOracle oracle = Run($"map {mapId}", server);
            AssertExplained(oracle, server.Count);
            total += server.Count;
            notKept += oracle.LayerVerticesNotKept;
            notKeptReset += oracle.NotKeptReset;
            notKeptMax += oracle.NotKeptMaxOfLayers;
            notKeptEdge += oracle.NotKeptEdgeRewrite;
            notKeptOther += oracle.NotKeptOther;

            // Tiles the WDT lists with an ADT that the server never converted.
            int clientOnly = service.AvailableTiles(mapId).Count(t => !server.Contains(t));
            Assert.True(clientOnly == 0, $"map {mapId}: {clientOnly} WDT-listed client ADTs have no server .map");
        }

        output.WriteLine($"{total} tiles compared over {ClientTerrainRealData.ServerMapIds().Count} maps; liquid not-kept: {notKept} = {notKeptReset} reset + {notKeptMax} max-of-layers + {notKeptEdge} neighbour edge rewrite + {notKeptOther} other");
        Assert.True(total > 2000, $"{total} tiles");
    }

    [ClientTerrainRealFact]
    public void HeightQuery_InterpolatesLikeTheServerBetweenSamples()
    {
        ClientTerrainService service = Service();
        (double Row, double Column)[] fractions = [(0.25, 0.6), (0.7, 0.2), (0.5, 0.9), (0.1, 0.1)];
        long compared = 0;
        double worst = 0;
        foreach (TileKey tile in Block(0, Goldshire.X, Goldshire.Y))
        {
            AdtFile adt = service.LoadAdt(tile).File!;
            ServerMapTile server = ClientTerrainRealData.ServerTile(tile)!;
            Assert.Equal("Float", server.Storage);
            WorldPoint origin = WorldGrid.Origin(tile);
            for (int row = 0; row < ExtractorEmulation.Squares; row++)
            {
                for (int column = 0; column < ExtractorEmulation.Squares; column++)
                {
                    foreach ((double fr, double fc) in fractions)
                    {
                        // Both sides answer the same float32 position; the server then works in float32, so the
                        // tolerance allows the error of locating a point in the square (about 1e-3 of a square)
                        // times the square's height range.
                        float x = (float)(origin.X - ((row + fr) * WorldGrid.SquareSize));
                        float y = (float)(origin.Y - ((column + fc) * WorldGrid.SquareSize));
                        TerrainSample sample = AdtGeometry.SampleAt(adt, tile, x, y) ?? throw new InvalidOperationException(FormattableString.Invariant($"({x}, {y}) is not inside {tile}."));
                        float serverZ = server.Tile.GetHeight(x, y);
                        if (sample.IsHole)
                        {
                            Assert.Equal(TerrainTile.InvalidHeightValue, serverZ);
                            continue;
                        }

                        double range = SquareRange(adt, sample.SquareRow, sample.SquareColumn);
                        double difference = Math.Abs(sample.Z - serverZ);
                        worst = Math.Max(worst, difference);
                        Assert.True(difference <= ClientTerrainOracle.Tolerance + (2e-3 * range), FormattableString.Invariant($"{tile} ({x}, {y}): client {sample.Z}, server {serverZ}, square range {range}"));
                        compared++;
                    }
                }
            }
        }

        output.WriteLine(FormattableString.Invariant($"{compared} interpolated heights compared, worst difference {worst:G4} yd"));
        Assert.True(compared > 500_000, $"{compared}");
    }

    private static double SquareRange(AdtFile adt, int row, int column)
    {
        AdtChunk chunk = adt.Chunk(row / 8, column / 8);
        int y = row % 8;
        int x = column % 8;
        float[] h = [chunk.OuterHeight(y, x), chunk.OuterHeight(y + 1, x), chunk.OuterHeight(y, x + 1), chunk.OuterHeight(y + 1, x + 1), chunk.InnerHeight(y, x)];
        return h.Max() - h.Min();
    }

    [ClientTerrainRealFact]
    public void Layers_BuildRealGoldshireTerrainAndLiquid()
    {
        ClientTerrainService service = Service();
        AdtFile adt = service.LoadAdt(Goldshire).File!;
        var layers = new ClientTerrainLayerSource(service, NullLogger.Instance);

        LayerBuild terrain = layers.Build(LayerIds.RefTerrain, Goldshire, CancellationToken.None);
        Assert.Equal(TileState.Ok, terrain.Status.State);
        int holeSquares = adt.Chunks.Sum(c => c.HoleSquares);
        Assert.Equal(4 * ((ExtractorEmulation.Squares * ExtractorEmulation.Squares) - holeSquares), terrain.Chunks.Sum(c => c.TriangleCount));

        LayerBuild liquid = layers.Build(LayerIds.RefLiquid, Goldshire, CancellationToken.None);
        int visible = adt.Chunks.Sum(c => c.LiquidLayers.Sum(l => l.VisibleSquares));
        Assert.Equal(visible > 0 ? TileState.Ok : TileState.Empty, liquid.Status.State);
        Assert.Equal(2 * visible, liquid.Chunks.Sum(c => c.TriangleCount));
        output.WriteLine($"Goldshire ref.terrain {terrain.Chunks.Sum(c => c.TriangleCount)} triangles ({holeSquares} hole squares), ref.liquid {visible} visible squares");
    }

    [ClientTerrainRealFact]
    public void Placements_ResolveRealModelPaths()
    {
        ClientTerrainService service = Service();
        var placements = new ClientPlacementSource(service);
        IReadOnlyList<ClientPlacement>? goldshire = placements.Placements(Goldshire);
        Assert.NotNull(goldshire);
        AdtFile adt = service.LoadAdt(Goldshire).File!;
        Assert.Equal(adt.Doodads.Count, goldshire.Count(p => p.Kind == PlacementKind.M2));
        Assert.Equal(adt.WmoObjects.Count, goldshire.Count(p => p.Kind == PlacementKind.Wmo));
        Assert.True(adt.Doodads.Count > 0 && adt.WmoObjects.Count > 0);
        Assert.All(goldshire, p => Assert.Equal(Goldshire, p.Tile));

        // MMDX names say .mdx (or .mdl); the archives hold the model as .m2.
        int asNamed = 0;
        foreach (ClientPlacement p in goldshire)
        {
            bool exists = ClientTerrainRealData.Files.Exists(p.ModelPath);
            asNamed += exists ? 1 : 0;
            Assert.True(exists || ClientTerrainRealData.Files.Exists(Path.ChangeExtension(p.ModelPath, ".m2")), $"{p.ModelPath} is not in the client files.");
        }

        Assert.All(goldshire.Where(p => p.Kind == PlacementKind.Wmo), p => Assert.True(ClientTerrainRealData.Files.Exists(p.ModelPath), p.ModelPath));

        // A WMO-only map: its WDT names the one global WMO.
        ClientMap global = ClientTerrainRealData.Maps.Maps.First(m => service.LoadWdt(m.Id).File is { IsGlobalWmo: true });
        IReadOnlyList<ClientPlacement>? wmo = placements.Placements(TileKey.Global(global.Id));
        Assert.NotNull(wmo);
        Assert.NotEmpty(wmo);
        Assert.All(wmo, p => Assert.True(p.Kind == PlacementKind.Wmo && ClientTerrainRealData.Files.Exists(p.ModelPath), p.ModelPath));

        // Every MODF entry in every real 1.12.1 WDT and ADT has BoundsMin <= BoundsMax componentwise.
        int wdtModf = 0;
        int adtModf = 0;
        int invertedBounds = 0;
        int zeroAdts = 0;
        foreach (string path in ClientTerrainRealData.Files.List("World\\Maps\\"))
        {
            if (path.EndsWith(".wdt", StringComparison.OrdinalIgnoreCase))
            {
                WdtFile wdtFile = WdtFile.Parse(ClientTerrainRealData.Files.Read(path)!);
                foreach (AdtWmoObject o in wdtFile.WmoObjects)
                {
                    wdtModf++;
                    invertedBounds += IsOrdered(o) ? 0 : 1;
                }
            }
            else if (path.EndsWith(".adt", StringComparison.OrdinalIgnoreCase))
            {
                byte[] bytes = ClientTerrainRealData.Files.Read(path)!;
                if (bytes.Length == 0)
                {
                    zeroAdts++;
                    continue;
                }

                AdtFile adtFile = AdtFile.Parse(bytes);
                foreach (AdtWmoObject o in adtFile.WmoObjects)
                {
                    adtModf++;
                    invertedBounds += IsOrdered(o) ? 0 : 1;
                }
            }
        }

        Assert.Equal(20, wdtModf);
        Assert.Equal(4851, adtModf);
        Assert.Equal(49, zeroAdts);
        Assert.Equal(0, invertedBounds);
        output.WriteLine($"Goldshire: {adt.Doodads.Count} doodads, {adt.WmoObjects.Count} WMOs, {asNamed} paths as named; {global.Directory} global WMO {wmo[0].ModelPath}; {wdtModf} WDT + {adtModf} ADT MODFs ({zeroAdts} zero-byte ADTs), {invertedBounds} inverted bounds");
    }

    private static bool IsOrdered(AdtWmoObject o)
        => o.BoundsMin.X <= o.BoundsMax.X && o.BoundsMin.Y <= o.BoundsMax.Y && o.BoundsMin.Z <= o.BoundsMax.Z;

    [ClientTerrainRealFact]
    public async Task Commands_AnswerFromTheRealGoldshireTile()
    {
        ClientTerrainService service = Service();
        var registry = new CommandRegistry();
        ClientTerrainCommands.Register(registry);
        var services = new AtlasServices();
        services.Add(service);

        CommandResult height = await registry.InvokeAsync("ref.query.height", JsonNode.Parse("{\"map\":0,\"at\":[-9464,62,60]}")!.AsObject(), services, CommandOrigin.Cli, headless: true, CancellationToken.None);
        Assert.True(height.Ok, height.ErrorMessage);
        JsonNode terrain = height.Data!["terrain"]!;
        float serverZ = ClientTerrainRealData.ServerTile(Goldshire)!.Tile.GetHeight(-9464f, 62f);
        Assert.Equal(serverZ, terrain["z"].AsDouble(), 0.01);

        CommandResult info = await registry.InvokeAsync("ref.adt.info", JsonNode.Parse("{\"map\":0,\"x\":49,\"y\":31}")!.AsObject(), services, CommandOrigin.Cli, headless: true, CancellationToken.None);
        Assert.True(info.Ok, info.ErrorMessage);
        AdtFile adt = service.LoadAdt(Goldshire).File!;
        JsonNode data = info.Data!;
        Assert.Equal("0004931.map", data["serverMapFile"]!.GetValue<string>());
        Assert.Equal(0, data["positionMismatches"].AsInt());
        Assert.Equal(adt.Textures.Count, data["textures"].AsInt());
        Assert.Equal(adt.Doodads.Count, data["placements"]!["doodads"].AsInt());
        output.WriteLine(FormattableString.Invariant($"Goldshire ref.query.height z {terrain["z"].AsDouble():F3} (server {serverZ:F3}); {adt.Textures.Count} textures"));
    }
}
