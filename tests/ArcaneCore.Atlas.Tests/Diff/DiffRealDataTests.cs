using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Diff;
using ArcaneCore.Atlas.Reference.Client.Models;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Atlas.Reference.Navmesh;
using ArcaneCore.Atlas.Server;
using ArcaneCore.Atlas.Tests.Reference.Client.Terrain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Atlas.Tests.Diff;

/// <summary>
/// Real-data verification for Lane D (<c>diff.terrain</c>, <c>diff.models</c>, <c>diff.nav</c>, and
/// <c>ref.query.floorstack</c> vs <c>query.floorstack</c>) on Goldshire tile (0, 49, 31).
/// </summary>
public sealed class DiffRealDataTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TileKey Goldshire = new(0, 49, 31);
    private const double InnX = -9462.66;
    private const double InnY = 16.1915;
    private const double InnFloorZ = 56.963;

    private readonly AtlasContext _context = CreateContext();

    public void Dispose() => _context.Dispose();

    [ClientTerrainRealFact]
    public async Task DiffTerrain_Goldshire_ReportsZeroUnexplainedMismatch()
    {
        JsonObject data = await InvokeOkAsync("diff.terrain", """{"map":0,"x":49,"y":31,"radius":0}""");
        output.WriteLine(data.ToJsonString());

        Assert.Equal(1, data["tilesCompared"]!.GetValue<int>());
        Assert.Equal(0L, data["unexplainedCount"]!.GetValue<long>());
        Assert.True(data["expectedCount"]!.GetValue<long>() > 0);

        Assert.True(_context.Layers.TryGet(LayerIds.DiffTerrain, out ILayerSource source));
        LayerBuild build = source.Build(LayerIds.DiffTerrain, Goldshire, CancellationToken.None);
        Assert.True(build.Status.State is TileState.Ok or TileState.Empty);
        if (build.Chunks.Count > 0)
        {
            Assert.Equal(0L, build.Chunks[0].Stats["unexplainedCount"]!.GetValue<long>());
        }
    }

    [ClientTerrainRealFact]
    public async Task DiffModels_Goldshire_MatchesEveryServerSpawnToClientPlacement()
    {
        JsonObject data = await InvokeOkAsync("diff.models", """{"map":0,"x":49,"y":31,"radius":0}""");
        output.WriteLine(data.ToJsonString());

        Assert.Equal(1, data["tilesCompared"]!.GetValue<int>());
        Assert.Equal(0L, data["unexplainedCount"]!.GetValue<long>());
        Assert.True(data["totals"]!["matchedSpawns"]!.GetValue<long>() > 0);

        Assert.True(_context.Layers.TryGet(LayerIds.DiffModels, out ILayerSource source));
        LayerBuild build = source.Build(LayerIds.DiffModels, Goldshire, CancellationToken.None);
        Assert.True(build.Status.State is TileState.Ok or TileState.Empty);
        if (build.Chunks.Count > 0)
        {
            Assert.Equal(0L, build.Chunks[0].Stats["unexplainedCount"]!.GetValue<long>());
        }
    }

    [ClientTerrainRealFact]
    public async Task DiffModels_BootyBay_GiantGoblinStatueAndAllModelsMatchWithZeroGeometryMismatch()
    {
        // Booty Bay tile (0, 58, 30) contains Goblinstatuebooty.m2 (spawnId 120780) at 23.62x scale (ScaleRaw = 24186).
        JsonObject data = await InvokeOkAsync("diff.models", """{"map":0,"x":58,"y":30,"radius":0}""");
        output.WriteLine(data.ToJsonString());

        Assert.Equal(1, data["tilesCompared"]!.GetValue<int>());
        Assert.Equal(0L, data["unexplainedCount"]!.GetValue<long>());
        Assert.Equal(0L, data["totals"]!["geometryMismatches"]!.GetValue<long>());
        Assert.True(data["totals"]!["matchedSpawns"]!.GetValue<long>() > 0);
    }

    [ClientTerrainRealFact]
    public async Task DiffNav_Goldshire_MatchesEveryPolygonAndCrossTilePortalBetweenServerAndDotRecast()
    {
        JsonObject data = await InvokeOkAsync("diff.nav", """{"map":0,"x":49,"y":31,"radius":0}""");
        output.WriteLine(data.ToJsonString());

        Assert.Equal(1, data["tilesCompared"]!.GetValue<int>());
        Assert.Equal(0L, data["unexplainedCount"]!.GetValue<long>());
        Assert.Equal(0L, data["totals"]!["portalSlabClimbRule"]!.GetValue<long>());
        Assert.True(data["totals"]!["groundPolygonsMatched"]!.GetValue<long>() > 0);
        Assert.True(data["totals"]!["portalsMatched"]!.GetValue<long>() > 0);

        Assert.True(_context.Layers.TryGet(LayerIds.DiffNav, out ILayerSource source));
        LayerBuild build = source.Build(LayerIds.DiffNav, Goldshire, CancellationToken.None);
        Assert.True(build.Status.State is TileState.Ok or TileState.Empty);
        if (build.Chunks.Count > 0)
        {
            Assert.Equal(0L, build.Chunks[0].Stats["unexplainedCount"]!.GetValue<long>());
        }
    }

    [ClientTerrainRealFact]
    public async Task FloorStack_ServerAndReferenceClient_AgreeAtGoldshireInn()
    {
        string args = string.Create(
            CultureInfo.InvariantCulture,
            $$$"""{"map":0,"x":{{{InnX}}},"y":{{{InnY}}},"zMin":0,"zMax":200}""");

        JsonObject serverStack = await InvokeOkAsync(ServerCommands.FloorStackName, args);
        JsonObject refStack = await InvokeOkAsync("ref.query.floorstack", args);
        output.WriteLine($"server: {serverStack.ToJsonString()}");
        output.WriteLine($"ref:    {refStack.ToJsonString()}");

        JsonArray serverSurfaces = serverStack["surfaces"]!.AsArray();
        JsonArray refSurfaces = refStack["surfaces"]!.AsArray();

        Assert.Contains(
            serverSurfaces,
            s => s!["kind"]!.GetValue<string>() == "model"
                && Math.Abs(s["z"]!.GetValue<double>() - InnFloorZ) < 0.05);
        Assert.Contains(
            refSurfaces,
            s => s!["kind"]!.GetValue<string>() == "model"
                && s["modelKind"]!.GetValue<string>() == "wmo"
                && Math.Abs(s["z"]!.GetValue<double>() - InnFloorZ) < 0.05);

        // Goldshire Inn sits over a terrain hole (for its cellar/basement); both server and reference agree on no terrain surface here.
        Assert.DoesNotContain(serverSurfaces, s => s!["kind"]!.GetValue<string>() == "terrain");
        Assert.DoesNotContain(refSurfaces, s => s!["kind"]!.GetValue<string>() == "terrain");

        foreach (JsonNode? refSurface in refSurfaces)
        {
            double rz = refSurface!["z"]!.GetValue<double>();
            Assert.Contains(
                serverSurfaces,
                s => s!["kind"]!.GetValue<string>() == "model"
                    && Math.Abs(s["z"]!.GetValue<double>() - rz) < 0.05);
        }

        // Outside the inn on the same Goldshire tile, both return the terrain surface and agree on its height.
        const string OutsideArgs = """{"map":0,"x":-9462.0,"y":62.0,"zMin":0,"zMax":200}""";
        JsonObject serverOutside = await InvokeOkAsync(ServerCommands.FloorStackName, OutsideArgs);
        JsonObject refOutside = await InvokeOkAsync("ref.query.floorstack", OutsideArgs);
        double serverTerrainZ = serverOutside["surfaces"]!.AsArray()
            .First(s => s!["kind"]!.GetValue<string>() == "terrain")!["z"]!
            .GetValue<double>();
        double refTerrainZ = refOutside["surfaces"]!.AsArray()
            .First(s => s!["kind"]!.GetValue<string>() == "terrain")!["z"]!
            .GetValue<double>();
        Assert.Equal(serverTerrainZ, refTerrainZ, 2);
    }

    private static AtlasContext CreateContext()
    {
        if (ClientTerrainRealData.SkipReason() is not null)
        {
            return new AtlasContext(new AtlasPaths(null, null), NullLoggerFactory.Instance, headless: true);
        }

        string? terrainRoot = Environment.GetEnvironmentVariable(ClientTerrainRealData.TerrainVariable);
        string? clientRoot = Environment.GetEnvironmentVariable(DirectoryClientFiles.TestEnvironmentVariable);
        var paths = new AtlasPaths(terrainRoot, clientRoot);
        var context = new AtlasContext(paths, NullLoggerFactory.Instance, headless: true);

        context.Services.Add<IClientFileSource>(ClientTerrainRealData.Files);
        context.Services.Add<IMapDirectory>(ClientTerrainRealData.Maps);
        new ServerModule().Register(context);
        new ClientTerrainModule().Register(context);
        new ClientModelsModule().Register(context);
        new ReferenceNavmeshModule().Register(context);
        new DiffModule().Register(context);
        return context;
    }

    private async Task<JsonObject> InvokeOkAsync(string command, string jsonArgs)
    {
        CommandResult result = await _context.Commands.InvokeAsync(
            command,
            JsonNode.Parse(jsonArgs)!.AsObject(),
            _context.Services,
            CommandOrigin.Cli,
            headless: true,
            CancellationToken.None);
        Assert.True(result.Ok, $"{command} {jsonArgs}: {result.ErrorCode} {result.ErrorMessage}");
        return result.Data!.AsObject();
    }
}
