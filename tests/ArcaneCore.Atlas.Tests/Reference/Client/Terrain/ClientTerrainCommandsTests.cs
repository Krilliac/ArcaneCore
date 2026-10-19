using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Terrain;

public sealed class ClientTerrainCommandsTests
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

    private static string Point(double x, double y, double z)
        => FormattableString.Invariant($"[{x}, {y}, {z}]");

    private static string Args(string map, string at, string extra = "")
        => "{\"map\":" + map + ",\"at\":" + at + extra + "}";

    [Fact]
    public void Registration_DeclaresTypedArgumentsAndSchemas()
    {
        var registry = new CommandRegistry();
        ClientTerrainCommands.Register(registry);

        Assert.True(registry.TryGet("ref.query.height", out CommandDefinition height));
        Assert.True(registry.TryGet("ref.adt.info", out CommandDefinition info));
        Assert.Equal(CommandSurfaces.All, height.Surfaces);
        Assert.Equal(new[] { "map", "at" }, height.Args.Select(a => a.Name));
        Assert.Equal(new[] { "map", "x", "y" }, info.Args.Select(a => a.Name));
        Assert.All(height.Args.Concat(info.Args), a => Assert.True(a.Required));
        JsonObject schema = CommandSchema.InputSchema(height);
        Assert.Equal("array", schema["properties"]!["at"]!["type"]!.GetValue<string>());
        Assert.Throws<ArgumentException>(() => ClientTerrainCommands.Register(registry));
    }

    [Fact]
    public async Task Height_ReportsTerrainAndEveryLiquidLayerAtAPoint()
    {
        var fixture = new TerrainFixture();
        var setup = Setup(fixture.Service());

        // Square (2, 2) of chunk (6, 7): river (dark) and ocean are both shown there.
        (double x, double y) = TerrainFixture.At(TerrainFixture.Goldshire, (6 * 8) + 2, (7 * 8) + 2);
        CommandResult result = await Run(setup, "ref.query.height", Args("0", Point(x, y, 200)));

        Assert.True(result.Ok, result.ErrorMessage);
        JsonNode data = result.Data!;
        Assert.Equal(0u, data["map"].AsUInt());
        Assert.Equal("Eastern Kingdoms", data["mapName"]!.GetValue<string>());
        Assert.Equal(48, data["tile"]![1].AsInt());
        Assert.Equal(32, data["tile"]![2].AsInt());
        Assert.Equal("Ok", data["adt"]!["state"]!.GetValue<string>());
        Assert.Equal("World\\Maps\\Azeroth\\Azeroth_32_48.adt", data["adt"]!["path"]!.GetValue<string>());

        JsonNode terrain = data["terrain"]!;
        double z = terrain["z"].AsDouble();
        Assert.Equal(TerrainFixture.PlaneAt(x, y), z, 0.02);
        Assert.Equal(200 - z, terrain["deltaZ"].AsDouble(), 1e-9);
        Assert.False(terrain["hole"]!.GetValue<bool>());
        Assert.Equal(6, terrain["chunk"]![0].AsInt());
        Assert.Equal(7, terrain["chunk"]![1].AsInt());
        Assert.Equal((6 * 8) + 2, terrain["square"]![0].AsInt());
        Assert.Equal(12u, terrain["areaId"].AsUInt());
        Assert.Equal("Elwynn Forest", terrain["area"]!.GetValue<string>());

        JsonArray liquid = data["liquid"]!.AsArray();
        Assert.Equal(2, liquid.Count);
        Assert.Equal("river", liquid[0]!["liquid"]!.GetValue<string>());
        Assert.True(liquid[0]!["darkWater"]!.GetValue<bool>());

        // River heights at the square: 152 at the first row, 153 at the next; the centre sits on the diagonal: 152.5.
        Assert.Equal(152.5, liquid[0]!["level"].AsDouble(), 1e-3);
        Assert.Equal(152.5 - z, liquid[0]!["depth"].AsDouble(), 0.02);
        Assert.Equal("ocean", liquid[1]!["liquid"]!.GetValue<string>());
        Assert.Equal(160.0, liquid[1]!["level"].AsDouble(), 1e-6);
        Assert.Equal(4, liquid[0]!["corners"]!.AsArray().Count);

        JsonArray points = data["overlay"]!["points"]!.AsArray();
        Assert.Equal(3, points.Count);
        Assert.Equal(ColorKeys.Ground, points[0]!["colorKey"]!.GetValue<string>());
        Assert.Equal(ColorKeys.DeepWater, points[1]!["colorKey"]!.GetValue<string>());
        Assert.Equal(ColorKeys.Ocean, points[2]!["colorKey"]!.GetValue<string>());
        Assert.Equal(z, points[0]!["at"]![2].AsDouble(), 1e-9);
    }

    [Fact]
    public async Task Height_AtAHoleHasNoZ()
    {
        var setup = Setup(new TerrainFixture().Service());

        // Hole bit 0 of chunk (3, 4): its first two squares in both directions.
        (double x, double y) = TerrainFixture.At(TerrainFixture.Goldshire, (3 * 8) + 1, (4 * 8) + 1);
        CommandResult result = await Run(setup, "ref.query.height", Args("0", Point(x, y, 50)));

        Assert.True(result.Ok, result.ErrorMessage);
        JsonNode terrain = result.Data!["terrain"]!;
        Assert.True(terrain["hole"]!.GetValue<bool>());
        Assert.Null(terrain["z"]);
        Assert.Null(terrain["deltaZ"]);
        Assert.Null(terrain["triangle"]);
        Assert.Empty(result.Data["liquid"]!.AsArray());
        Assert.Empty(result.Data["overlay"]!["points"]!.AsArray());
    }

    [Fact]
    public async Task Height_OnATileWithoutAFileSaysSoAndOffersNoTerrain()
    {
        var setup = Setup(new TerrainFixture().Service());
        (double x, double y) = TerrainFixture.At(TerrainFixture.ListedButAbsent, 10, 10);

        CommandResult result = await Run(setup, "ref.query.height", Args("0", Point(x, y, 0)));

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal("Missing", result.Data!["adt"]!["state"]!.GetValue<string>());
        Assert.Contains("Azeroth_32_49.adt", result.Data["adt"]!["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(result.Data["terrain"]);
        Assert.Null(result.Data["liquid"]);
    }

    [Fact]
    public async Task Height_RefusesWhatItCannotAnswer()
    {
        var fixture = new TerrainFixture();
        var setup = Setup(fixture.Service());
        (double x, double y) = TerrainFixture.At(TerrainFixture.Goldshire, 5, 5);
        string at = Point(x, y, 10);

        CommandResult unknownMap = await Run(setup, "ref.query.height", Args("999", at));
        CommandResult offGrid = await Run(setup, "ref.query.height", """{"map":0,"at":[50000, 0, 0]}""");
        CommandResult twoValues = await Run(setup, "ref.query.height", """{"map":0,"at":[1, 2]}""");
        CommandResult notANumber = await Run(setup, "ref.query.height", """{"map":0,"at":"nan,1,2"}""");
        CommandResult negativeMap = await Run(setup, "ref.query.height", Args("-1", at));
        CommandResult unknownArg = await Run(setup, "ref.query.height", Args("0", at, ",\"search\":5"));
        CommandResult missingArg = await Run(setup, "ref.query.height", """{"map":0}""");
        var noFiles = Setup(new ClientTerrainService(() => null, () => fixture.Maps, NullLogger.Instance));
        CommandResult unavailable = await Run(noFiles, "ref.query.height", Args("0", at));
        var noMaps = Setup(new ClientTerrainService(() => fixture.Files, () => null, NullLogger.Instance));
        CommandResult unavailableMaps = await Run(noMaps, "ref.query.height", Args("0", at));

        Assert.Equal(CommandException.BadArguments, unknownMap.ErrorCode);
        Assert.Contains("999", unknownMap.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(CommandException.BadArguments, offGrid.ErrorCode);
        Assert.Equal(CommandException.BadArguments, twoValues.ErrorCode);
        Assert.Equal(CommandException.BadArguments, notANumber.ErrorCode);
        Assert.Equal(CommandException.BadArguments, negativeMap.ErrorCode);
        Assert.Equal(CommandException.BadArguments, unknownArg.ErrorCode);
        Assert.Equal(CommandException.BadArguments, missingArg.ErrorCode);
        Assert.Equal(CommandException.Unavailable, unavailable.ErrorCode);
        Assert.Equal(CommandException.Unavailable, unavailableMaps.ErrorCode);
    }

    [Fact]
    public async Task Commands_NeedTheServiceTheModuleRegisters()
    {
        var registry = new CommandRegistry();
        ClientTerrainCommands.Register(registry);

        CommandResult result = await registry.InvokeAsync("ref.adt.info", JsonNode.Parse("""{"map":0,"x":48,"y":32}""")!.AsObject(), new AtlasServices(), CommandOrigin.Cli, true, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(CommandException.Unavailable, result.ErrorCode);
        Assert.Contains(nameof(ClientTerrainService), result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdtInfo_SummarisesTheTile()
    {
        var setup = Setup(new TerrainFixture().Service());

        CommandResult result = await Run(setup, "ref.adt.info", """{"map":0,"x":48,"y":32}""");

        Assert.True(result.Ok, result.ErrorMessage);
        JsonNode data = result.Data!;
        Assert.Equal("0004832.map", data["serverMapFile"]!.GetValue<string>());
        Assert.Equal("Ok", data["adt"]!["state"]!.GetValue<string>());
        Assert.Equal("Ok", data["wdt"]!["state"]!.GetValue<string>());
        Assert.True(data["wdt"]!["listsTile"]!.GetValue<bool>());
        Assert.False(data["wdt"]!["globalWmo"]!.GetValue<bool>());
        Assert.Equal(3, data["wdt"]!["tiles"].AsInt());

        JsonNode chunks = data["chunks"]!;
        Assert.Equal(256, chunks["count"].AsInt());
        Assert.Equal(4, chunks["holeSquares"].AsInt());
        Assert.Equal(0, chunks["withoutHeights"].AsInt());
        Assert.Equal(1, chunks["liquidChunks"].AsInt());
        Assert.Equal(1, chunks["stackedLiquidChunks"].AsInt());
        Assert.Equal(1, chunks["liquidLayers"]!["river"].AsInt());
        Assert.Equal(1, chunks["liquidLayers"]!["ocean"].AsInt());
        Assert.Equal(0, chunks["liquidLayers"]!["magma"].AsInt());
        Assert.Equal(1, chunks["distinctAreas"].AsInt());
        Assert.Equal("Elwynn Forest", chunks["areas"]![0]!["area"]!.GetValue<string>());
        Assert.Equal(256, chunks["areas"]![0]!["chunks"].AsInt());

        JsonNode placements = data["placements"]!;
        Assert.Equal(2, placements["doodads"].AsInt());
        Assert.Equal(1, placements["wmos"].AsInt());
        Assert.Equal("World\\wmo\\Inn.wmo", placements["wmoList"]![0]!["path"]!.GetValue<string>());
        Assert.False(placements["wmoListTruncated"]!.GetValue<bool>());
        Assert.Equal(1, data["textures"].AsInt());
        Assert.Equal(0, data["positionMismatches"].AsInt());

        JsonNode box = data["overlay"]!["boxes"]![0]!;
        (double minX, double maxX, double minY, double maxY) = WorldGrid.Bounds(TerrainFixture.Goldshire);
        Assert.Equal(minX, box["min"]![0].AsDouble(), 1e-6);
        Assert.Equal(maxY, box["max"]![1].AsDouble(), 1e-6);
        Assert.True(box["min"]![2].AsDouble() < box["max"]![2].AsDouble());
        Assert.NotEqual(maxX, minX);
    }

    [Fact]
    public async Task AdtInfo_ForATileTheWdtDoesNotListSaysSo()
    {
        var setup = Setup(new TerrainFixture().Service());

        CommandResult result = await Run(setup, "ref.adt.info", """{"map":0,"x":10,"y":10}""");

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal("Missing", result.Data!["adt"]!["state"]!.GetValue<string>());
        Assert.False(result.Data["wdt"]!["listsTile"]!.GetValue<bool>());
        Assert.Null(result.Data["chunks"]);
    }

    [Theory]
    [InlineData("""{"map":0,"x":64,"y":0}""")]
    [InlineData("""{"map":0,"x":0,"y":-1}""")]
    [InlineData("""{"map":0,"x":"a","y":0}""")]
    [InlineData("""{"map":0,"x":1}""")]
    [InlineData("""{"map":4294967296,"x":1,"y":1}""")]
    public async Task AdtInfo_RefusesBadTileIndices(string args)
    {
        var setup = Setup(new TerrainFixture().Service());

        CommandResult result = await Run(setup, "ref.adt.info", args);

        Assert.False(result.Ok);
        Assert.Equal(CommandException.BadArguments, result.ErrorCode);
    }
}
