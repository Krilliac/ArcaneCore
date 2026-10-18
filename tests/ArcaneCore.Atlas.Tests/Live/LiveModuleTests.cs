using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Live;

/// <summary>How the host sees the live module: discovery, registration, the layer source and the command schemas.</summary>
public sealed class LiveModuleTests
{
    private static readonly string[] CommandNames =
    [
        "live.connect",
        "live.disconnect",
        "live.entities",
        "live.follow",
        "live.query",
        "live.status",
        "live.subscribe",
    ];

    private static LiveOptions Options(string pipeName) => new()
    {
        PipeNameFor = _ => pipeName,
        Discovery = new FixedDiscovery(4242),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        HelloTimeout = TimeSpan.FromSeconds(10),
        QueryTimeout = TimeSpan.FromSeconds(10),
    };

    private static AtlasContext Compose(IAtlasModule module)
        => AtlasModules.Compose(new AtlasPaths(null, null), NullLoggerFactory.Instance, headless: true, [module]);

    private static Task<CommandResult> Invoke(AtlasContext context, string name, JsonObject? args = null)
        => context.Commands.InvokeAsync(name, args, context.Services, CommandOrigin.Cli, headless: true, CancellationToken.None);

    [Fact]
    public void TheHostFindsTheModuleByReflection_WithoutAnySharedList()
    {
        IReadOnlyList<IAtlasModule> found = AtlasModules.Discover(typeof(LiveModule).Assembly);

        LiveModule module = Assert.Single(found.OfType<LiveModule>());
        Assert.Equal("live", module.Name);
        Assert.Equal(ModuleOrder.Layers, module.Order);
    }

    [Fact]
    public void RegisteringAddsExactlyTheSevenCommands_TheFeedService_AndNoProblems()
    {
        using AtlasContext context = Compose(new LiveModule(Options("unused")));

        Assert.Equal(CommandNames, context.Commands.All.Select(c => c.Name).Where(n => n.StartsWith("live.", StringComparison.Ordinal)));
        LiveFeed feed = context.Services.Require<LiveFeed>();
        Assert.False(feed.IsConnected);
        Assert.DoesNotContain(context.Problems, p => p.Contains("live", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheDefaultConstructorWorksToo_AsDiscoveryUsesIt()
    {
        using AtlasContext context = Compose(new LiveModule());

        Assert.NotNull(context.Services.Get<LiveFeed>());
        Assert.True(context.Commands.TryGet("live.status", out _));
    }

    [Fact]
    public void EveryLiveCommandRunsHeadless_BecauseRunScriptAndHeadlessMcpMayUseThem()
    {
        using AtlasContext context = Compose(new LiveModule(Options("unused")));

        foreach (CommandDefinition command in context.Commands.All.Where(c => c.Name.StartsWith("live.", StringComparison.Ordinal)))
        {
            Assert.True(command.Surfaces.HasFlag(CommandSurfaces.Headless), command.Name);
            Assert.False(string.IsNullOrWhiteSpace(command.Summary), command.Name);
            Assert.All(command.Args, a => Assert.False(string.IsNullOrWhiteSpace(a.Description), command.Name + "." + a.Name));
        }
    }

    [Fact]
    public void TheSchemasAreClosed_RequireWhatIsRequired_AndListTheChoices()
    {
        using AtlasContext context = Compose(new LiveModule(Options("unused")));
        JsonObject Schema(string name)
        {
            Assert.True(context.Commands.TryGet(name, out CommandDefinition? definition));
            return CommandSchema.InputSchema(definition);
        }

        foreach (string name in CommandNames)
        {
            Assert.False(Schema(name)["additionalProperties"]!.GetValue<bool>(), name);
        }

        Assert.Equal("""["map"]""", Schema("live.subscribe")["required"]!.ToJsonString());
        Assert.Equal("""["kind","a"]""", Schema("live.query")["required"]!.ToJsonString());
        Assert.Null(Schema("live.status")["required"]);
        Assert.Equal("""["player","bot","creature","gameobject"]""", Schema("live.entities")["properties"]!["kind"]!["enum"]!.ToJsonString());
        Assert.Equal("""["los","height","path","undermap"]""", Schema("live.query")["properties"]!["kind"]!["enum"]!.ToJsonString());
        Assert.Equal("array", Schema("live.query")["properties"]!["a"]!["type"]!.GetValue<string>());
        Assert.Equal(4, Schema("live.subscribe")["properties"]!["hz"]!["default"]!.GetValue<double>());
        Assert.Equal(100, Schema("live.entities")["properties"]!["limit"]!["default"]!.GetValue<int>());
    }

    [Fact]
    public void TheLiveLayersHaveAnOwner_ThatNeverPretendsToHaveTiles()
    {
        using AtlasContext context = Compose(new LiveModule(Options("unused")));
        string[] liveIds = [.. LayerCatalog.All.Where(l => l.View == LayerView.Live).Select(l => l.Id)];
        var tile = new TileKey(0, 32, 48);

        Assert.NotEmpty(liveIds);
        Assert.Equal(liveIds, context.Layers.Available);
        foreach (string id in liveIds)
        {
            Assert.True(context.Layers.TryGet(id, out ILayerSource? source), id);
            Assert.Empty(source.AvailableTiles(id, 0));
            TileStatus status = source.PeekStatus(id, tile);
            Assert.Equal(TileState.NotApplicable, status.State);
            Assert.Contains("live", status.Detail, StringComparison.Ordinal);
            LayerBuild build = source.Build(id, tile, CancellationToken.None);
            Assert.Equal(TileState.NotApplicable, build.Status.State);
            Assert.Empty(build.Chunks);
        }
    }

    [Fact]
    public void TheLayerSourceRefusesALayerThatIsNotLive()
    {
        var source = new LiveLayerSource();
        string terrain = LayerCatalog.All.First(l => l.View != LayerView.Live).Id;

        Assert.DoesNotContain(terrain, source.Layers);
        Assert.Throws<ArgumentException>(() => source.Build(terrain, new TileKey(0, 1, 1), CancellationToken.None));
    }

    [Fact]
    public async Task ComposedLikeTheHost_ConnectsSubscribesAndShowsFramesToTheUi()
    {
        string pipe = FakeFeedServer.NewName();
        using AtlasContext context = Compose(new LiveModule(Options(pipe)));
        using var log = new EventLog(context.Events);
        await using var server = new FakeFeedServer(pipe);

        Task accept = server.AcceptAsync();
        Task<CommandResult> connecting = Invoke(context, "live.connect");
        await accept;
        await server.AnswerHelloAsync(Wire.Hello(1, 4242, "feedface", (0u, 0u, 1, 1)));
        CommandResult connected = await connecting.WaitAsync(FakeFeedServer.Patience);

        Assert.True(connected.Ok, connected.ErrorMessage);
        JsonNode? status = await log.NextAsync(EventHub.Topics.LiveStatus);
        Assert.True(status!["connected"]!.GetValue<bool>());
        Assert.Equal(4242, status["pid"]!.GetValue<int>());

        CommandResult subscribed = await Invoke(context, "live.subscribe", new JsonObject { ["map"] = 0, ["hz"] = 10 });
        JsonObject sent = await server.ReadAsync();
        Assert.True(subscribed.Ok, subscribed.ErrorMessage);
        Assert.Equal("subscribe", sent["op"]!.GetValue<string>());

        await server.WriteAsync(Wire.Frame(0, 0, 1, Wire.Entity("A1", "player", -9462, 16, 57, "Alice")));
        JsonNode? pushed = await log.NextAsync(EventHub.Topics.LiveFrame);
        Assert.Single(pushed!["entities"]!.AsArray());
        Assert.Equal("A1", pushed["entities"]![0]!["g"]!.GetValue<string>());

        CommandResult listed = await Invoke(context, "live.entities", new JsonObject { ["name"] = "ali" });
        CommandResult followed = await Invoke(context, "live.follow", new JsonObject { ["guid"] = "a1" });
        CommandResult disconnected = await Invoke(context, "live.disconnect");

        Assert.Equal("A1", listed.Data!["entities"]![0]!["guid"]!.GetValue<string>());
        Assert.True(followed.Ok, followed.ErrorMessage);
        Assert.Equal("A1", context.View.Current.FollowGuid);
        Assert.Equal(new WorldPoint(-9462, 16, 57), context.View.Current.Camera.Target);
        Assert.True(disconnected.Ok, disconnected.ErrorMessage);
        Assert.True(disconnected.Data!["wasConnected"]!.GetValue<bool>());
        Assert.Equal("unsubscribe", (await server.ReadAsync())["op"]!.GetValue<string>());
    }

    [Fact]
    public async Task DisposingTheContextClosesTheFeed()
    {
        string pipe = FakeFeedServer.NewName();
        AtlasContext context = Compose(new LiveModule(Options(pipe)));
        LiveFeed feed = context.Services.Require<LiveFeed>();
        await using var server = new FakeFeedServer(pipe);
        Task accept = server.AcceptAsync();
        Task<CommandResult> connecting = Invoke(context, "live.connect");
        await accept;
        await server.AnswerHelloAsync();
        Assert.True((await connecting.WaitAsync(FakeFeedServer.Patience)).Ok);
        Assert.True(feed.IsConnected);

        context.Dispose();

        Assert.False(feed.IsConnected);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => feed.ConnectAsync(4242, CancellationToken.None));
        Assert.Null(await server.ReadLineAsync());
    }
}
