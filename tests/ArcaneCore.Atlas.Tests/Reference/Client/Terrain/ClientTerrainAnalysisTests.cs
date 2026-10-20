using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Terrain;

public sealed class ClientTerrainAnalysisTests
{
    private static (CommandRegistry Registry, AtlasServices Services) Setup(ClientTerrainService service)
    {
        var registry = new CommandRegistry();
        ClientTerrainCommands.Register(registry);
        var services = new AtlasServices();
        services.Add(service);
        return (registry, services);
    }

    private static async Task<CommandResult> Run((CommandRegistry Registry, AtlasServices Services) setup, string name, string args)
        => await setup.Registry.InvokeAsync(name, JsonNode.Parse(args)!.AsObject(), setup.Services, CommandOrigin.Cli, headless: true, CancellationToken.None);

    [Fact]
    public void SampleSurface_OnFlatTile_ReturnsUnitUpNormalZeroSlopeAndWalkable()
    {
        var fixture = new TerrainFixture();
        ClientTerrainService service = fixture.Service();
        (double x, double y) = TerrainFixture.At(TerrainFixture.Flat, 10, 10);

        TerrainSurfaceSample? sample = service.SampleSurface(0, x, y);

        Assert.NotNull(sample);
        TerrainSurfaceSample s = sample.Value;
        Assert.Equal(TerrainFixture.Flat, s.Tile);
        Assert.False(s.Terrain.IsHole);
        Assert.Equal(0.0, s.Normal.X, 1e-6);
        Assert.Equal(0.0, s.Normal.Y, 1e-6);
        Assert.Equal(1.0, s.Normal.Z, 1e-6);
        Assert.Equal(0.0, s.SlopeDegrees, 1e-6);
        Assert.False(s.IsSteep);
        Assert.True(s.WalkableSlope);
        Assert.Null(s.LiquidZ);
        Assert.Equal(0.0, s.LiquidDepth);
    }

    [Fact]
    public void SampleSurface_OnSlopedPlaneWithLiquid_ComputesExactNormalSlopeAndClampedLiquidDepth()
    {
        var fixture = new TerrainFixture();
        ClientTerrainService service = fixture.Service();

        // Square (2, 2) of chunk (6, 7) on Goldshire: plane z = 120 + 0.02*X - 0.01*Y, with river (152.5) and ocean (160).
        (double x, double y) = TerrainFixture.At(TerrainFixture.Goldshire, (6 * 8) + 2, (7 * 8) + 2, 0.25, 0.1);
        TerrainSurfaceSample? sample = service.SampleSurface(0, x, y);

        Assert.NotNull(sample);
        TerrainSurfaceSample s = sample.Value;

        double nxRaw = -TerrainFixture.PlaneSlopeX;
        double nyRaw = -TerrainFixture.PlaneSlopeY;
        double len = Math.Sqrt((nxRaw * nxRaw) + (nyRaw * nyRaw) + 1.0);
        double expectedNx = nxRaw / len;
        double expectedNy = nyRaw / len;
        double expectedNz = 1.0 / len;
        double expectedSlope = Math.Acos(expectedNz) * (180.0 / Math.PI);

        Assert.Equal(expectedNx, s.Normal.X, 1e-3);
        Assert.Equal(expectedNy, s.Normal.Y, 1e-3);
        Assert.Equal(expectedNz, s.Normal.Z, 1e-4);
        Assert.Equal(expectedSlope, s.SlopeDegrees, 1e-2);
        Assert.False(s.IsSteep);
        Assert.True(s.WalkableSlope);
        Assert.Equal(160.0, s.LiquidZ!.Value, 1e-5);
        Assert.Equal(Math.Max(0.0, 160.0 - s.Terrain.Z), s.LiquidDepth, 1e-4);
    }

    [Fact]
    public void SampleSurface_OnSteepCliffAndHole_ClassifiesWalkabilityAndClampsNegativeLiquidDepth()
    {
        var fixture = new TerrainFixture();
        var steepTile = new TileKey(0, 48, 33);

        // Slope of 2.0 along X (> sqrt(3) ~ 1.732, so > 60 degrees), with liquid below the terrain surface.
        AdtSpec spec = SyntheticTerrain.Plane(steepTile, 100f, 2.0f, 0.0f);
        spec.Liquid[0] =
        [
            new LiquidLayerSpec(AdtLiquidKind.River, -20000f, -20000f, (_, _) => -20000f, (_, _) => 0),
        ];
        fixture.Files.Add(ClientMapPaths.AdtPath("Azeroth", steepTile), SyntheticTerrain.Adt(spec).Bytes);
        ClientTerrainService service = fixture.Service();

        (double sx, double sy) = TerrainFixture.At(steepTile, 1, 1, 0.3, 0.1);
        TerrainSurfaceSample? steepSample = service.SampleSurface(0, sx, sy);
        Assert.NotNull(steepSample);
        TerrainSurfaceSample steep = steepSample.Value;

        Assert.True(steep.SlopeDegrees >= 60.0);
        Assert.True(steep.IsSteep);
        Assert.False(steep.WalkableSlope);
        Assert.Equal(0.0, steep.LiquidDepth);

        // Hole square on Goldshire: chunk (3, 4), square (1, 1).
        (double hx, double hy) = TerrainFixture.At(TerrainFixture.Goldshire, (3 * 8) + 1, (4 * 8) + 1);
        TerrainSurfaceSample? holeSample = service.SampleSurface(0, hx, hy);
        Assert.NotNull(holeSample);
        TerrainSurfaceSample hole = holeSample.Value;

        Assert.True(hole.Terrain.IsHole);
        Assert.True(double.IsNaN(hole.SlopeDegrees));
        Assert.False(hole.IsSteep);
        Assert.False(hole.WalkableSlope);
        Assert.Equal(0.0, hole.LiquidDepth);

        Assert.Null(service.SampleSurface(0, 999999, 0));
        Assert.Null(service.SampleSurface(0, double.NaN, 0));
    }

    [Fact]
    public async Task HeightCommand_IncludesNormalSlopeWalkableSlopeAndLiquidDepth()
    {
        var fixture = new TerrainFixture();
        var setup = Setup(fixture.Service());
        (double x, double y) = TerrainFixture.At(TerrainFixture.Goldshire, (6 * 8) + 2, (7 * 8) + 2);
        string args = FormattableString.Invariant($"{{\"map\":0,\"at\":[{x}, {y}, 200]}}");

        CommandResult result = await Run(setup, ClientTerrainCommands.HeightName, args);

        Assert.True(result.Ok, result.ErrorMessage);
        JsonNode terrain = result.Data!["terrain"]!;
        JsonNode normal = terrain["normal"]!;
        double nx = normal["x"].AsDouble();
        double ny = normal["y"].AsDouble();
        double nz = normal["z"].AsDouble();
        Assert.Equal(1.0, Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz)), 1e-6);
        Assert.True(nz > 0.99);
        Assert.InRange(terrain["slopeDegrees"].AsDouble(), 1.0, 2.0);
        Assert.True(terrain["walkableSlope"]!.GetValue<bool>());
        double z = terrain["z"].AsDouble();
        Assert.Equal(160.0 - z, terrain["liquidDepth"].AsDouble(), 0.02);
    }

    [Fact]
    public async Task AnalyzeCommand_SummarizesElevationSteepTrianglesHolesLiquidAndAreasWithSteepestOverlay()
    {
        var fixture = new TerrainFixture();
        TileKey tile = TerrainFixture.Flat;
        var spec = new AdtSpec
        {
            Tile = tile,
            BaseHeight = (row, col) => row == 2 && col == 3 ? 50f : 10f,
            Outer = (row, col, y, x) => row == 2 && col == 3 ? y * 10f : 0f,
            Inner = (row, col, y, x) => row == 2 && col == 3 ? (y + 0.5f) * 10f : 0f,
        };
        spec.Areas[0] = 12;
        spec.Areas[(2 * 16) + 3] = 40;
        spec.Holes[5] = 0x0003; // 2 bits = 8 hole squares
        spec.Liquid[0] =
        [
            new LiquidLayerSpec(AdtLiquidKind.River, 15f, 15f, (_, _) => 15f, (y, x) => y < 2 && x < 4 ? (byte)0 : AdtLiquidLayer.HiddenSquare),
        ];
        fixture.Files.Add(ClientMapPaths.AdtPath("Azeroth", tile), SyntheticTerrain.Adt(spec).Bytes);
        var setup = Setup(fixture.Service());

        CommandResult result = await Run(setup, ClientTerrainCommands.AnalyzeName, """{"map":0,"tileX":48,"tileY":33}""");

        Assert.True(result.Ok, result.ErrorMessage);
        JsonNode data = result.Data!;
        Assert.Equal(0u, data["map"].AsUInt());
        Assert.Equal(10.0, data["elevation"]!["min"].AsDouble(), 1e-4);
        Assert.Equal(130.0, data["elevation"]!["max"].AsDouble(), 1e-4);
        Assert.True(data["elevation"]!["mean"].AsDouble() > 10.0);

        // Chunk (2, 3) has 64 squares = 256 triangles, each with dZ/dy = 10 over SquareSize (4.1667 yd) -> slope ~67.38 deg (>= 60 deg).
        Assert.Equal(256, data["steepTriangles"].AsInt());
        Assert.Equal(((128 * 128) - 8) * 4, data["totalTriangles"].AsInt());
        Assert.Equal(256.0 / (((128 * 128) - 8) * 4), data["steepTriangleRatio"].AsDouble(), 1e-6);
        Assert.Equal(8, data["holeCount"].AsInt());
        Assert.Equal(8, data["liquidSquares"].AsInt());
        Assert.Equal(8.0 / (128 * 128), data["liquidCoverageRatio"].AsDouble(), 1e-6);

        uint[] areas = [.. data["areaIds"]!.AsArray().Select(a => a.AsUInt())];
        Assert.Equal(new uint[] { 0u, 12u, 40u }, areas);

        JsonArray steepest = data["steepestChunks"]!.AsArray();
        Assert.Single(steepest);
        Assert.Equal((2 * 16) + 3, steepest[0]!["chunkIndex"].AsInt());
        Assert.Equal(256, steepest[0]!["steepTriangles"].AsInt());

        JsonArray boxes = data["overlay"]!["boxes"]!.AsArray();
        Assert.Single(boxes);
        Assert.Contains("(2, 3)", boxes[0]!["label"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"map":0,"tileX":64,"tileY":0}""")]
    [InlineData("""{"map":0,"tileX":0,"tileY":-1}""")]
    [InlineData("""{"map":999,"tileX":48,"tileY":32}""")]
    public async Task AnalyzeCommand_RefusesOutOfRangeTilesAndUnknownMaps(string args)
    {
        var setup = Setup(new TerrainFixture().Service());

        CommandResult result = await Run(setup, ClientTerrainCommands.AnalyzeName, args);

        Assert.False(result.Ok);
        Assert.Equal(CommandException.BadArguments, result.ErrorCode);
    }
}
