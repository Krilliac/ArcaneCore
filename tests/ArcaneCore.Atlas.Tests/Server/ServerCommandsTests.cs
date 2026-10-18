using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Server;
using ArcaneCore.Game.Maps.Terrain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Server;

/// <summary>The server module, its query commands and the engine's owner thread, over synthetic terrain written at runtime.</summary>
public sealed class ServerCommandsTests : IDisposable
{
    private const uint Map = 1;
    private static readonly TileKey Tile = new(Map, 30, 40);
    private readonly TempTerrainRoot _root = new();

    public void Dispose() => _root.Dispose();

    private static float Corner(int r, int c) => (r * 0.5f) + (c * 0.25f) + 10f;

    private static float Centre(int r, int c) => ((r + 0.5f) * 0.5f) + ((c + 0.5f) * 0.25f) + 10f + ((r + c) % 3 * 0.75f);

    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private AtlasContext Compose(AtlasPaths? paths = null)
        => AtlasModules.Compose(paths ?? _root.Paths, NullLoggerFactory.Instance, headless: true, [new ServerModule()]);

    private static Task<CommandResult> Invoke(AtlasContext context, string name, string args)
        => context.Commands.InvokeAsync(name, JsonNode.Parse(args)!.AsObject(), context.Services, CommandOrigin.Cli, headless: true, CancellationToken.None);

    private void WriteFloatTile()
        => _root.WriteMap(Map, Tile.X, Tile.Y, new SyntheticMapFile
        {
            V9 = SyntheticMapFile.Grid(129, Corner),
            V8 = SyntheticMapFile.Grid(128, Centre),
        }.Build());

    // A point inside the synthetic tile, away from square edges.
    private static (double X, double Y) Inside()
    {
        WorldPoint o = WorldGrid.Origin(Tile);
        return (o.X - (40.3 * WorldGrid.SquareSize), o.Y - (77.6 * WorldGrid.SquareSize));
    }

    [Fact]
    public void Module_IsDiscovered_AndRegistersEveryServerLayerAndCommand()
    {
        Assert.Contains(AtlasModules.Discover(typeof(ServerModule).Assembly), m => m is ServerModule);
        using AtlasContext context = Compose();

        string[] layers =
        [
            LayerIds.Tiles, LayerIds.Terrain, LayerIds.TerrainHoles, LayerIds.TerrainNoHeight, LayerIds.Area, LayerIds.Liquid,
            LayerIds.VMapWmo, LayerIds.VMapM2, LayerIds.VMapBounds, LayerIds.VMapLiquid,
            LayerIds.NavPoly, LayerIds.NavDetail, LayerIds.NavPortals, LayerIds.NavBvRejected,
        ];
        foreach (string id in layers)
        {
            Assert.True(context.Layers.TryGet(id, out _), id);
        }

        string[] commands = [ServerCommands.HeightName, ServerCommands.LosName, ServerCommands.PathName, ServerCommands.FloorStackName, ServerCommands.StatusName];
        Assert.All(commands, c => Assert.Contains(context.Commands.All, d => d.Name == c));
        Assert.NotNull(context.Services.Get<ServerEngine>());
        Assert.NotNull(context.Services.Get<ServerData>());
        Assert.Empty(context.Problems);
    }

    [Theory]
    [InlineData("query.height", """{"map":1,"at":[1e9,0,0]}""")]
    [InlineData("query.height", """{"map":1,"at":[0,0,1e7]}""")]
    [InlineData("query.height", """{"map":1,"at":[0,0,0],"x":1,"y":2,"z":3}""")]
    [InlineData("query.height", """{"map":1,"x":1,"y":2}""")]
    [InlineData("query.height", """{"map":1,"x":"NaN","y":2,"z":3}""")]
    [InlineData("query.height", """{"map":1,"at":[0,0,0],"search":-1}""")]
    [InlineData("query.los", """{"map":1,"a":[0,0,0],"b":[0,"Infinity",0]}""")]
    [InlineData("query.path", """{"map":1,"a":[0,0,0],"b":[17100,0,0]}""")]
    [InlineData("query.path", """{"map":1,"a":[0,0,0],"b":[1,1,1],"mover":{"swims":true}}""")]
    [InlineData("query.path", """{"map":1,"a":[0,0,0],"b":[1,1,1],"options":{"maxPoints":1}}""")]
    [InlineData("query.floorstack", """{"map":1,"x":0,"y":-40000}""")]
    public async Task BadCoordinatesAndArguments_AreRefusedBeforeTheEngineRuns(string command, string args)
    {
        WriteFloatTile();
        using AtlasContext context = Compose();

        CommandResult result = await Invoke(context, command, args);

        Assert.False(result.Ok);
        Assert.Equal(CommandException.BadArguments, result.ErrorCode);
        ServerStatus status = context.Services.Require<ServerEngine>().Status();
        Assert.False(status.EngineStarted);
        Assert.Equal(0, status.QueriesRun + status.QueriesRefused + status.QueriesFailed);
    }

    [Fact]
    public async Task MapWithoutData_IsUnavailable_WithTheReason()
    {
        WriteFloatTile();
        using AtlasContext context = Compose();

        CommandResult result = await Invoke(context, ServerCommands.HeightName, """{"map":7,"at":[0,0,0]}""");

        Assert.False(result.Ok);
        Assert.Equal(CommandException.Unavailable, result.ErrorCode);
        Assert.Contains("Map 7 has no maps, vmaps or mmaps data", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoDataDirectory_IsUnavailable()
    {
        using AtlasContext context = Compose(new AtlasPaths(null, null));

        CommandResult result = await Invoke(context, ServerCommands.HeightName, """{"map":1,"at":[0,0,0]}""");

        Assert.Equal(CommandException.Unavailable, result.ErrorCode);
    }

    [Fact]
    public async Task Height_AnswersTheServerTerrainHeight_AndCarriesAnOverlay()
    {
        WriteFloatTile();
        using AtlasContext context = Compose();
        (double x, double y) = Inside();
        float expected = TerrainTile.Parse(File.ReadAllBytes(Path.Combine(_root.Root, "maps", TerrainTile.FileName(Map, Tile.X, Tile.Y)))).GetHeight((float)x, (float)y);

        CommandResult result = await Invoke(context, ServerCommands.HeightName, $$"""{"map":1,"at":[{{N(x)}},{{N(y)}},200]}""");

        Assert.True(result.Ok, result.ErrorMessage);
        JsonObject data = result.Data!.AsObject();
        Assert.Equal(expected, (double)data["terrain"]!["z"]!, 3);
        Assert.Equal(expected, (double)data["collision"]!["z"]!, 3);
        Assert.Equal("Ok", (string)data["files"]!["map"]!["state"]!);
        Assert.Equal("Missing", (string)data["files"]!["vmtile"]!["state"]!);
        Assert.NotEmpty(data["overlay"]!["points"]!.AsArray());
        ServerStatus status = context.Services.Require<ServerEngine>().Status();
        Assert.True(status.EngineStarted);
        Assert.Equal([Map], status.Maps);
        Assert.Equal(1, status.QueriesRun);
    }

    [Fact]
    public async Task FloorStack_ListsTheTerrainSurface()
    {
        WriteFloatTile();
        using AtlasContext context = Compose();
        (double x, double y) = Inside();

        CommandResult result = await Invoke(context, ServerCommands.FloorStackName, $$"""{"map":1,"x":{{N(x)}},"y":{{N(y)}}}""");

        Assert.True(result.Ok, result.ErrorMessage);
        JsonArray surfaces = result.Data!["surfaces"]!.AsArray();
        JsonNode terrain = Assert.Single(surfaces, s => (string)s!["kind"]! == "terrain")!;
        Assert.InRange((double)terrain["z"]!, 10, 110);
    }

    [Fact]
    public async Task Los_TerrainSample_SeesTheGroundBetweenTwoPoints()
    {
        WriteFloatTile();
        using AtlasContext context = Compose();
        (double x, double y) = Inside();

        CommandResult above = await Invoke(context, ServerCommands.LosName, $$"""{"map":1,"a":[{{N(x)}},{{N(y)}},300],"b":[{{N(x - 20)}},{{N(y - 20)}},300]}""");
        CommandResult through = await Invoke(context, ServerCommands.LosName, $$"""{"map":1,"a":[{{N(x)}},{{N(y)}},300],"b":[{{N(x - 20)}},{{N(y - 20)}},-50]}""");

        Assert.True(above.Ok, above.ErrorMessage);
        Assert.True((bool)above.Data!["terrain"]!["clear"]!);
        Assert.True(through.Ok, through.ErrorMessage);
        Assert.False((bool)through.Data!["terrain"]!["clear"]!);
    }

    [Fact]
    public async Task Path_WithoutNavmeshData_IsUnavailable()
    {
        WriteFloatTile();
        using AtlasContext context = Compose();
        (double x, double y) = Inside();

        CommandResult result = await Invoke(context, ServerCommands.PathName, $$"""{"map":1,"a":[{{N(x)}},{{N(y)}},50],"b":[{{N(x - 5)}},{{N(y)}},50]}""");

        Assert.Equal(CommandException.Unavailable, result.ErrorCode);
        Assert.Contains("navmesh", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_ReportsDirectoriesAndReaderVersions()
    {
        using AtlasContext context = Compose();

        CommandResult result = await Invoke(context, ServerCommands.StatusName, "{}");

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal("z1.4", (string)result.Data!["readers"]!["map"]!["version"]!);
        Assert.Equal("MAPS", (string)result.Data!["readers"]!["map"]!["magic"]!);
        Assert.Equal("VMAP_7.0", (string)result.Data!["readers"]!["vmap"]!["magic"]!);
        Assert.Equal(Path.Combine(_root.Root, "maps"), (string)result.Data!["directories"]!["maps"]!);
        Assert.False((bool)result.Data!["engineStarted"]!);
    }

    [Fact]
    public async Task EveryEngineCall_RunsOnOneOwnerThread()
    {
        WriteFloatTile();
        using AtlasContext context = Compose();
        ServerEngine engine = context.Services.Require<ServerEngine>();
        Assert.False(engine.OnOwnerThread);

        Task<(int Thread, bool Owner)>[] calls =
            [.. Enumerable.Range(0, 16).Select(_ => Task.Run(() => engine.RunOnOwnerAsync(_ => (Environment.CurrentManagedThreadId, engine.OnOwnerThread))))];
        (int Thread, bool Owner)[] seen = await Task.WhenAll(calls);

        Assert.All(seen, s => Assert.True(s.Owner));
        Assert.Single(seen.Select(s => s.Thread).Distinct());
        Assert.NotEqual(Environment.CurrentManagedThreadId, seen[0].Thread);
    }

    [Fact]
    public async Task OwnerThread_PropagatesFailures_CancelsPromptlyWhileQueued_AndRefusesWorkAfterDispose()
    {
        var owner = new OwnerThread("atlas-server-test");
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.RunAsync<int>(() => throw new InvalidOperationException("boom")));
        Assert.Equal(5, await owner.RunAsync(() => 5));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.RunAsync(() => 1, cancelled.Token));

        using var blockGate = new ManualResetEventSlim(false);
        using var enteredGate = new ManualResetEventSlim(false);
        Task blocker = owner.RunAsync(() =>
        {
            enteredGate.Set();
            blockGate.Wait(TimeSpan.FromSeconds(5));
        });
        Assert.True(enteredGate.Wait(TimeSpan.FromSeconds(5)));

        using var waitingCts = new CancellationTokenSource();
        int queuedRan = 0;
        Task<int> waiting = owner.RunAsync(() => Interlocked.Increment(ref queuedRan), waitingCts.Token);
        await waitingCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(1)));

        blockGate.Set();
        await blocker;
        Assert.Equal(0, Volatile.Read(ref queuedRan));

        owner.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = owner.RunAsync(() => 1); });
    }

    [Fact]
    public async Task ParseCache_EvictsLeastRecentlyUsed_AndClearDoesNotDriftCostWhenLoadInFlight()
    {
        var cache = new ParseCache<string, string>(maxCost: 20);
        cache.GetOrLoad("a", () => (ParsedFile<string>.Ok("A", "a"), 10));
        cache.GetOrLoad("b", () => (ParsedFile<string>.Ok("B", "b"), 10));
        Assert.Equal(20, cache.Stats.Cost);

        // Touch "a" so "b" becomes least recently used, then insert "c" (cost 10) to evict "b".
        cache.GetOrLoad("a", () => (ParsedFile<string>.Ok("A2", "a"), 10));
        cache.GetOrLoad("c", () => (ParsedFile<string>.Ok("C", "c"), 10));
        Assert.True(cache.TryPeek("a", out _));
        Assert.False(cache.TryPeek("b", out _));
        Assert.True(cache.TryPeek("c", out _));
        Assert.Equal(20, cache.Stats.Cost);

        using var inFlightStarted = new ManualResetEventSlim(false);
        using var releaseInFlight = new ManualResetEventSlim(false);
        Task<ParsedFile<string>> inFlight = Task.Run(() => cache.GetOrLoad("d", () =>
        {
            inFlightStarted.Set();
            releaseInFlight.Wait(TimeSpan.FromSeconds(5));
            return (ParsedFile<string>.Ok("D", "d"), 15);
        }));
        Assert.True(inFlightStarted.Wait(TimeSpan.FromSeconds(5)));
        cache.Clear();
        Assert.Equal(0, cache.Stats.Cost);
        releaseInFlight.Set();
        ParsedFile<string> dResult = await inFlight;
        Assert.True(dResult.IsOk);
        Assert.Equal(0, cache.Stats.Cost);
        Assert.False(cache.TryPeek("d", out _));
    }

    [Fact]
    public async Task Los_VerticalSegment_SamplesPlanarStepsOnly_AndNonFiniteIndoorGroundZIsNull()
    {
        WriteFloatTile();
        using AtlasContext context = Compose();
        (double x, double y) = Inside();

        CommandResult vertical = await Invoke(context, ServerCommands.LosName, $$"""{"map":1,"a":[{{N(x)}},{{N(y)}},300000],"b":[{{N(x)}},{{N(y)}},-300000]}""");
        Assert.True(vertical.Ok, vertical.ErrorMessage);
        Assert.Equal(2, (int)vertical.Data!["terrain"]!["samples"]!);

        var sample = new IndoorSample(InsideWmo: true, MogpFlags: 0x2000, AdtId: 1, RootId: 2, GroupId: 3, GroundZ: float.NaN, Outdoors: false);
        JsonObject json = sample.ToJson();
        Assert.True((bool)json["insideWmo"]!);
        Assert.Null(json["groundZ"]);
    }
}
