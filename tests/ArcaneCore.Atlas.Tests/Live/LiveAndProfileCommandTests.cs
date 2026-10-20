using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;
using ArcaneCore.Atlas.Server;
using ArcaneCore.Atlas.Tests.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Live;

/// <summary>Unit tests for <c>query.profile</c>, <c>live.find</c> and <c>live.hotspots</c>.</summary>
public sealed class LiveAndProfileCommandTests : IDisposable
{
    private const uint Map = 1;
    private static readonly TileKey Tile = new(Map, 30, 40);
    private readonly TempTerrainRoot _root = new();

    public void Dispose() => _root.Dispose();

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private AtlasContext ComposeServer(AtlasPaths? paths = null)
        => AtlasModules.Compose(paths ?? _root.Paths, NullLoggerFactory.Instance, headless: true, [new ServerModule()]);

    private static Task<CommandResult> InvokeServer(AtlasContext context, string name, string args)
        => context.Commands.InvokeAsync(name, Obj(args), context.Services, CommandOrigin.Cli, headless: true, CancellationToken.None);

    private void WriteSlopedLiquidTile(float liquidLevel = 25f)
    {
        var liquidTypes = new byte[256];
        Array.Fill(liquidTypes, (byte)0x01);
        _root.WriteMap(Map, Tile.X, Tile.Y, new SyntheticMapFile
        {
            V9 = SyntheticMapFile.Grid(129, (r, c) => 10f + (r * 0.5f) + (c * 0.25f)),
            V8 = SyntheticMapFile.Grid(128, (r, c) => 10f + ((r + 0.5f) * 0.5f) + ((c + 0.5f) * 0.25f)),
            LiquidTypes = liquidTypes,
            LiquidLevel = liquidLevel,
        }.Build());
    }

    private static (double X, double Y) InsideTile()
    {
        WorldPoint o = WorldGrid.Origin(Tile);
        return (o.X - (40.0 * WorldGrid.SquareSize), o.Y - (60.0 * WorldGrid.SquareSize));
    }

    private static LiveEntity MakeEntity(
        ulong id,
        LiveKind kind,
        uint entry,
        string name,
        uint map,
        double x,
        double y,
        double z,
        string motion = "idle",
        ulong? target = null)
        => new(id, kind, entry, name, map, 0, new WorldPoint(x, y, z), 0, 100, motion, target, null, null, null);

    private static LiveFrame MakeFrame(long seq, uint map, params LiveEntity[] entities)
        => new(seq, seq * 100, map, 0, [], null, entities, Stopwatch.GetTimestamp());

    [Theory]
    [InlineData("""{"map":1,"from":{"x":0,"y":0},"to":{"x":10,"y":10},"steps":1}""")]
    [InlineData("""{"map":1,"from":{"x":0,"y":0},"to":{"x":10,"y":10},"steps":257}""")]
    [InlineData("""{"map":1,"from":{"x":1e9,"y":0},"to":{"x":10,"y":10}}""")]
    [InlineData("""{"map":1,"from":{"x":0,"y":0,"w":1},"to":{"x":10,"y":10}}""")]
    [InlineData("""{"map":1,"from":{"x":0},"to":{"x":10,"y":10}}""")]
    [InlineData("""{"map":1,"from":{"x":0,"y":0}}""")]
    public async Task Profile_RefusesBadArguments_BeforeTheEngineStarts(string args)
    {
        WriteSlopedLiquidTile();
        using AtlasContext context = ComposeServer();

        CommandResult result = await InvokeServer(context, ServerCommands.ProfileName, args);

        Assert.False(result.Ok);
        Assert.Equal(CommandException.BadArguments, result.ErrorCode);
        ServerStatus status = context.Services.Require<ServerEngine>().Status();
        Assert.False(status.EngineStarted);
    }

    [Fact]
    public async Task Profile_SamplesElevationAndLiquidAlongSegment_ComputesGrades_AndBuildsPolylineOverlay()
    {
        WriteSlopedLiquidTile(liquidLevel: 60f);
        using AtlasContext context = ComposeServer();
        (double x0, double y0) = InsideTile();
        double x1 = x0 - 40.0;
        double y1 = y0 - 30.0;

        CommandResult result = await InvokeServer(
            context,
            ServerCommands.ProfileName,
            $$"""{"map":1,"from":{"x":{{N(x0)}},"y":{{N(y0)}},"z":200},"to":{"x":{{N(x1)}},"y":{{N(y1)}}},"steps":4}""");

        Assert.True(result.Ok, result.ErrorMessage);
        JsonObject data = result.Data!.AsObject();
        Assert.Equal(1u, (uint)data["map"]!);
        Assert.Equal(4, (int)data["steps"]!);
        Assert.Equal(50.0, (double)data["totalHorizontalDistance"]!, 2);

        JsonArray samples = data["samples"]!.AsArray();
        Assert.Equal(5, samples.Count);

        JsonObject first = samples[0]!.AsObject();
        Assert.Equal(0.0, (double)first["t"]!, 3);
        Assert.Equal(0.0, (double)first["distance"]!, 3);
        Assert.Equal(0.0, (double)first["deltaFloorZ"]!, 3);
        Assert.Equal(0.0, (double)first["gradeDegrees"]!, 3);
        Assert.True((bool)first["hasLiquid"]!);
        Assert.Equal(60.0, (double)first["liquidZ"]!, 2);

        JsonObject last = samples[4]!.AsObject();
        Assert.Equal(1.0, (double)last["t"]!, 3);
        Assert.Equal(50.0, (double)last["distance"]!, 2);
        Assert.NotNull(last["terrainZ"]);
        Assert.NotNull(last["floorZ"]);
        Assert.True((double)last["gradeDegrees"]! > 0.0);

        double minFloor = (double)data["minFloorZ"]!;
        double maxFloor = (double)data["maxFloorZ"]!;
        double maxGrade = (double)data["maxGradeDegrees"]!;
        Assert.True(maxFloor > minFloor);
        Assert.True(maxGrade > 0.0);

        JsonArray polylines = data["overlay"]!["polylines"]!.AsArray();
        Assert.NotEmpty(polylines);
        Assert.Equal(5, polylines[0]!["pts"]!.AsArray().Count);

        // Array endpoints [x, y, z] are also accepted.
        CommandResult arrayCall = await InvokeServer(
            context,
            ServerCommands.ProfileName,
            $$"""{"map":1,"from":[{{N(x0)}},{{N(y0)}},200],"to":[{{N(x1)}},{{N(y1)}},200],"steps":2}""");
        Assert.True(arrayCall.Ok, arrayCall.ErrorMessage);
        Assert.Equal(3, arrayCall.Data!["samples"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("live.find", """{"limit":0}""")]
    [InlineData("live.find", """{"limit":501}""")]
    [InlineData("live.find", """{"kind":"dragon"}""")]
    [InlineData("live.find", """{"map":-1}""")]
    [InlineData("live.find", """{"inCombat":"yes"}""")]
    [InlineData("live.hotspots", """{"limit":0}""")]
    [InlineData("live.hotspots", """{"limit":101}""")]
    [InlineData("live.hotspots", """{"cellSize":0}""")]
    [InlineData("live.hotspots", """{"cellSize":-10}""")]
    [InlineData("live.hotspots", """{"map":-2}""")]
    public async Task LiveFindAndHotspots_RefuseBadArguments(string command, string args)
    {
        await using var rig = new LiveRig();

        CommandResult result = await rig.RunAsync(command, Obj(args));

        Assert.False(result.Ok);
        Assert.Equal(CommandException.BadArguments, result.ErrorCode);
    }

    [Fact]
    public async Task LiveFind_MatchesByNameSubstring_ExactGuid_ExactEntry_Kind_Map_AndCombatState()
    {
        await using var rig = new LiveRig();
        rig.Feed.Store.Apply(MakeFrame(
            1,
            0,
            MakeEntity(0xA1, LiveKind.Player, 1, "Thrall", 0, 10, 20, 5, motion: "idle", target: 0xB2),
            MakeEntity(0xB2, LiveKind.Creature, 4040, "Defias Pillager", 0, 15, 22, 5, motion: "chase"),
            MakeEntity(0xC3, LiveKind.Creature, 5050, "Kobold Vermin", 0, -100, -200, 12, motion: "idle"),
            MakeEntity(0xD4, LiveKind.Bot, 2, "HealBot", 0, 12, 18, 5, motion: "flee")));
        rig.Feed.Store.Apply(MakeFrame(
            1,
            1,
            MakeEntity(0xE5, LiveKind.Creature, 4040, "Defias Scout", 1, 50, 50, 0, motion: "idle")));

        // Substring name match (case-insensitive) across all maps.
        CommandResult byName = await rig.RunAsync("live.find", Obj("""{"query":"DEFIAS"}"""));
        Assert.True(byName.Ok, byName.ErrorMessage);
        Assert.Equal(2, (int)byName.Data!["matched"]!);
        Assert.Equal(new[] { "B2", "E5" }, byName.Data["entities"]!.AsArray().Select(e => (string)e!["guid"]!));

        // Exact hex guid match.
        CommandResult byHexGuid = await rig.RunAsync("live.find", Obj("""{"query":"0xC3"}"""));
        Assert.True(byHexGuid.Ok, byHexGuid.ErrorMessage);
        Assert.Equal(new[] { "C3" }, byHexGuid.Data!["entities"]!.AsArray().Select(e => (string)e!["guid"]!));

        // Exact entry match (4040 matches B2 and E5) filtered to map 0.
        CommandResult byEntryMap0 = await rig.RunAsync("live.find", Obj("""{"map":0,"query":"4040"}"""));
        Assert.True(byEntryMap0.Ok, byEntryMap0.ErrorMessage);
        Assert.Equal(new[] { "B2" }, byEntryMap0.Data!["entities"]!.AsArray().Select(e => (string)e!["guid"]!));

        // Combat filter: A1 (has target), B2 (chase), D4 (flee) are in combat; C3 and E5 are not.
        CommandResult inCombat = await rig.RunAsync("live.find", Obj("""{"inCombat":true}"""));
        Assert.True(inCombat.Ok, inCombat.ErrorMessage);
        Assert.Equal(new[] { "A1", "B2", "D4" }, inCombat.Data!["entities"]!.AsArray().Select(e => (string)e!["guid"]!));
        Assert.All(inCombat.Data["entities"]!.AsArray(), e => Assert.True((bool)e!["inCombat"]!));

        CommandResult outOfCombat = await rig.RunAsync("live.find", Obj("""{"inCombat":false,"kind":"creature"}"""));
        Assert.True(outOfCombat.Ok, outOfCombat.ErrorMessage);
        Assert.Equal(new[] { "C3", "E5" }, outOfCombat.Data!["entities"]!.AsArray().Select(e => (string)e!["guid"]!));

        // Limit caps returned list while reporting total matched.
        CommandResult limited = await rig.RunAsync("live.find", Obj("""{"inCombat":true,"limit":2}"""));
        Assert.True(limited.Ok, limited.ErrorMessage);
        Assert.Equal(3, (int)limited.Data!["matched"]!);
        Assert.Equal(2, (int)limited.Data["returned"]!);
        Assert.True((bool)limited.Data["truncated"]!);
    }

    [Fact]
    public async Task LiveHotspots_BucketsBySpatialCell_RanksByCombatThenPlayersThenTotal_AndClampsCellSize()
    {
        await using var rig = new LiveRig();
        // Cell (0, 0) for cellSize=100: [0..100, 0..100) -> 2 entities in combat (1 player + 1 creature).
        // Cell (2, 0) for cellSize=100: [200..300, 0..100) -> 3 entities, 0 in combat (2 players + 1 creature).
        // Cell (-1, -1) for cellSize=100: [-100..0, -100..0) -> 1 idle creature.
        rig.Feed.Store.Apply(MakeFrame(
            1,
            0,
            MakeEntity(1, LiveKind.Player, 1, "Alice", 0, 10, 20, 6, target: 2),
            MakeEntity(2, LiveKind.Creature, 100, "Wolf", 0, 30, 40, 8, motion: "chase"),
            MakeEntity(3, LiveKind.Player, 1, "Bob", 0, 210, 10, 0),
            MakeEntity(4, LiveKind.Bot, 2, "Botty", 0, 220, 20, 0),
            MakeEntity(5, LiveKind.Creature, 101, "Boar", 0, 230, 30, 0),
            MakeEntity(6, LiveKind.Creature, 102, "Deer", 0, -50, -50, 0)));

        CommandResult res = await rig.RunAsync("live.hotspots", Obj("""{"map":0,"cellSize":100,"limit":2}"""));
        Assert.True(res.Ok, res.ErrorMessage);
        Assert.Equal(100.0, (double)res.Data!["cellSize"]!);
        Assert.Equal(2, (int)res.Data["count"]!);

        JsonArray hotspots = res.Data["hotspots"]!.AsArray();
        Assert.Equal(2, hotspots.Count);

        // First hotspot: cell (0, 0) because inCombatCount == 2 beats cell (2, 0)'s inCombatCount == 0.
        JsonObject first = hotspots[0]!.AsObject();
        Assert.Equal(0, (int)first["cellX"]!);
        Assert.Equal(0, (int)first["cellY"]!);
        Assert.Equal(2, (int)first["inCombatCount"]!);
        Assert.Equal(1, (int)first["playerCount"]!);
        Assert.Equal(1, (int)first["creatureCount"]!);
        Assert.Equal(2, (int)first["totalCount"]!);
        Assert.Equal(20.0, (double)first["centroid"]!["x"]!, 2);
        Assert.Equal(30.0, (double)first["centroid"]!["y"]!, 2);
        Assert.Equal(7.0, (double)first["centroid"]!["z"]!, 2);

        // Second hotspot: cell (2, 0) because playerCount == 2 beats cell (-1, -1)'s playerCount == 0.
        JsonObject second = hotspots[1]!.AsObject();
        Assert.Equal(2, (int)second["cellX"]!);
        Assert.Equal(0, (int)second["cellY"]!);
        Assert.Equal(0, (int)second["inCombatCount"]!);
        Assert.Equal(2, (int)second["playerCount"]!);
        Assert.Equal(1, (int)second["creatureCount"]!);
        Assert.Equal(3, (int)second["totalCount"]!);

        // CellSize clamping to 16..2000.
        CommandResult clampedSmall = await rig.RunAsync("live.hotspots", Obj("""{"cellSize":4}"""));
        Assert.True(clampedSmall.Ok, clampedSmall.ErrorMessage);
        Assert.Equal(16.0, (double)clampedSmall.Data!["cellSize"]!);

        CommandResult clampedLarge = await rig.RunAsync("live.hotspots", Obj("""{"cellSize":9999}"""));
        Assert.True(clampedLarge.Ok, clampedLarge.ErrorMessage);
        Assert.Equal(2000.0, (double)clampedLarge.Data!["cellSize"]!);
    }
}
