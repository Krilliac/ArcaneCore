using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Live;

/// <summary>A scripted world daemon: records every line sent and answers from a handler.</summary>
internal sealed class FakeRequester : IFeedRequester
{
    public List<JsonObject> Sent { get; } = [];

    public bool Actions { get; set; } = true;

    public bool LiveServer { get; set; }

    public bool ServerCatalog { get; set; }

    public string[] Templates { get; set; } = ["place.creature", "place.gobject", "moveto", "tele", "kill", "debug", "spawns"];

    public string[] Commands { get; set; } = ["creature ai pause", "creature ai resume"];

    /// <summary>Answers an <c>action</c> line; the default is an ok answer with one reply line.</summary>
    public Func<JsonObject, JsonObject> OnAction { get; set; } = _ => new JsonObject { ["ok"] = true, ["lines"] = new JsonArray("done"), ["settled"] = true };

    public IEnumerable<JsonObject> ActionLines => Sent.Where(m => m["op"]!.GetValue<string>() == "action");

    public Task<JsonObject> RequestAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var copy = (JsonObject)message.DeepClone();
        Sent.Add(copy);
        switch (message["op"]!.GetValue<string>())
        {
            case "caps":
                var caps = new JsonObject
                {
                    ["ok"] = true,
                    ["actions"] = Actions,
                    ["login"] = false,
                    ["liveServer"] = LiveServer,
                    ["operator"] = "atlas-local:daemon#1",
                    ["role"] = "local",
                    ["templates"] = new JsonArray([.. (Actions ? Templates : []).Select(t => (JsonNode)t)]),
                };
                if (!Actions)
                {
                    caps["actionsWhyOff"] = "World:Control:MapFeed:Actions:Enabled is not set";
                }

                if (ServerCatalog)
                {
                    caps["catalog"] = true;
                }

                return Task.FromResult(caps);
            case "catalog":
                return Task.FromResult(new JsonObject
                {
                    ["ok"] = true,
                    ["commands"] = new JsonArray([.. Commands.Select(c => (JsonNode)new JsonObject { ["path"] = "." + c })]),
                });
            default:
                return Task.FromResult(OnAction(copy));
        }
    }
}

internal sealed class FakeDirectory : IEntityDirectory
{
    public Dictionary<ulong, LiveEntity> Entities { get; } = [];

    public LiveEntity? Get(ulong guid) => Entities.GetValueOrDefault(guid);

    public LiveEntity? FindPlayer(string name)
        => Entities.Values.FirstOrDefault(e => e.Kind is LiveKind.Player or LiveKind.Bot && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public static LiveEntity Entity(ulong id, LiveKind kind, string? name = null, double x = 10, double y = 20, double z = 30, uint map = 0)
        => new(id, kind, 1, name, map, 0, new WorldPoint(x, y, z), 1.5, 100, null, null, null, null, null);
}

internal sealed class DevUiRig
{
    public DevUiRig()
    {
        Requester = new FakeRequester();
        Directory = new FakeDirectory();
        Registry = new CommandRegistry();
        Events = new EventHub();
        Published = [];
        Events.Subscribe((topic, data) => Published.Add((topic, data)));
        Actions = new LiveActions(() => Requester, Directory, () => 0, Events);
        LiveActionCommands.Register(Registry);
        Services = new AtlasServices();
        Services.Add(Actions);
    }

    public FakeRequester Requester { get; }

    public FakeDirectory Directory { get; }

    public CommandRegistry Registry { get; }

    public EventHub Events { get; }

    public List<(string Topic, JsonNode? Data)> Published { get; }

    public LiveActions Actions { get; }

    public AtlasServices Services { get; }

    public Task<CommandResult> RunAsync(string command, string args)
        => Registry.InvokeAsync(command, JsonNode.Parse(args)!.AsObject(), Services, CommandOrigin.Cli, headless: true, CancellationToken.None);

    public async Task<JsonObject> DataAsync(string command, string args)
    {
        CommandResult result = await RunAsync(command, args);
        Assert.True(result.Ok, $"{command} {args}: {result.ErrorCode}: {result.ErrorMessage}");
        return result.Data!.AsObject();
    }
}

/// <summary>The dev UI's <c>action.*</c> and <c>devui.*</c> commands against a scripted daemon.</summary>
public sealed class LiveActionCommandsTests
{
    private const ulong Wolf = 0xF130_0000_0000_0001;
    private const ulong Bot1 = 0x1000_0000_0000_0002;
    private const ulong Bot2 = 0x1000_0000_0000_0003;
    private const ulong Me = 0x1000_0000_0000_0009;

    private static string Hex(ulong guid) => guid.ToString("X", System.Globalization.CultureInfo.InvariantCulture);

    private static DevUiRig Rig()
    {
        var rig = new DevUiRig();
        rig.Directory.Entities[Wolf] = FakeDirectory.Entity(Wolf, LiveKind.Creature, "Wolf", 100, 200, 30);
        rig.Directory.Entities[Bot1] = FakeDirectory.Entity(Bot1, LiveKind.Bot, "Alphabot", 1, 2, 3);
        rig.Directory.Entities[Bot2] = FakeDirectory.Entity(Bot2, LiveKind.Bot, "Betabot", 4, 5, 6);
        rig.Directory.Entities[Me] = FakeDirectory.Entity(Me, LiveKind.Player, "Operator", 7, 8, 9);
        return rig;
    }

    [Fact]
    public async Task Catalog_ListsOnlyWhatTheDaemonOffers()
    {
        DevUiRig rig = Rig();
        rig.Requester.Templates = ["kill", "tele"];

        JsonObject catalog = await rig.DataAsync("action.catalog", "{}");
        string[] ids = [.. catalog["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>())];

        Assert.True(catalog["actions"]!.GetValue<bool>());
        Assert.Contains("kill", ids);
        Assert.Contains("tele.me", ids);
        Assert.DoesNotContain("place.creature", ids);
        Assert.DoesNotContain("moveto.selected", ids);
        Assert.Contains("heal", ids);
        Assert.Equal("atlas-local:daemon#1", catalog["operator"]!.GetValue<string>());
    }

    [Fact]
    public async Task Catalog_WhenTheChannelIsOff_IsEmptyWithTheReason()
    {
        DevUiRig rig = Rig();
        rig.Requester.Actions = false;

        JsonObject catalog = await rig.DataAsync("action.catalog", "{}");

        Assert.False(catalog["actions"]!.GetValue<bool>());
        Assert.Empty(catalog["items"]!.AsArray());
        Assert.Contains("Actions:Enabled", catalog["whyOff"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Catalog_HidesGatedItems_UnlessTheDaemonCatalogListsThemAndTheLiveMarkerIsOff(bool serverCatalog, bool liveServer, bool shown)
    {
        DevUiRig rig = Rig();
        rig.Requester.ServerCatalog = serverCatalog;
        rig.Requester.LiveServer = liveServer;

        JsonObject catalog = await rig.DataAsync("action.catalog", "{}");
        string[] ids = [.. catalog["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>())];

        Assert.Equal(shown, ids.Contains("ai.pause", StringComparer.Ordinal));
        Assert.Equal(shown, ids.Contains("ai.resume", StringComparer.Ordinal));
        Assert.Contains("kill", ids);
    }

    [Fact]
    public async Task Catalog_WithAServerCatalog_HidesCommandsItDoesNotList()
    {
        DevUiRig rig = Rig();
        rig.Requester.ServerCatalog = true;
        rig.Requester.Commands = ["creature ai pause", "creature ai resume", "alter health", "creature respawn"];

        JsonObject catalog = await rig.DataAsync("action.catalog", "{}");
        string[] ids = [.. catalog["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>())];

        Assert.Contains("heal", ids);
        Assert.Contains("respawn", ids);
        Assert.DoesNotContain("despawn", ids);
        Assert.DoesNotContain("revive", ids);
        Assert.Contains(rig.Requester.Sent, m => m["op"]!.GetValue<string>() == "catalog");
    }

    /// <summary>Set to 1 to rewrite the fixture the JavaScript dev UI tests read.</summary>
    private const string UpdateVariable = "ARCANECORE_UPDATE_DEVUI_FIXTURE";

    private static string FixturePath([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "Web", "support", "devui-catalog.json"));

    [Fact]
    public async Task CatalogFixture_IsTheCatalogTheCommandReturns()
    {
        // The browser tests build their menus from this file, so it must be what action.catalog really says.
        DevUiRig rig = Rig();
        rig.Requester.ServerCatalog = true;
        rig.Requester.Commands = [.. DevUiCatalog.All.Where(a => a.Path is not null).Select(a => a.Path!)];
        JsonObject catalog = await rig.DataAsync("action.catalog", "{}");
        var fixture = new JsonObject { ["items"] = catalog["items"]!.DeepClone() };
        string path = FixturePath();
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(path, fixture.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        }

        Assert.True(File.Exists(path), $"{path} is missing; run this test with {UpdateVariable}=1");
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), fixture),
            $"{path} is out of date with DevUiCatalog; run this test with {UpdateVariable}=1 and commit the file");
        Assert.Equal(DevUiCatalog.All.Count, fixture["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task EveryCatalogAction_IsRunnableThroughItsActionCommand()
    {
        // The menu is built from the catalog; this proves each entry has a working server-side command, with the request the daemon should see.
        var seen = new List<string>();
        foreach (UiAction action in DevUiCatalog.All)
        {
            DevUiRig rig = Rig();
            rig.Requester.ServerCatalog = true;
            rig.Requester.Commands = [.. DevUiCatalog.All.Where(a => a.Path is not null).Select(a => a.Path!)];
            rig.Requester.OnAction = _ => new JsonObject { ["ok"] = true, ["lines"] = new JsonArray("ok"), ["settled"] = true };
            await rig.DataAsync("devui.character", """{"name":"Operator"}""");
            await rig.DataAsync("devui.select", $$"""{"guids":["{{Hex(Bot1)}}"]}""");

            string args = action.Args.Count == 0 ? "{}" : "{" + string.Join(',', action.Args.Select(a => $"\"{a.Name}\":{ArgSample(a)}")) + "}";
            ulong target = action.Kinds.Contains("bot") ? Bot1 : action.Kinds.Contains("creature") ? Wolf : Me;
            CommandResult result = action.Scope == "point"
                ? await rig.RunAsync("action.at", $$"""{"template":"{{action.Id}}","at":[11,22,33],"map":1,"args":{{args}}}""")
                : await rig.RunAsync("action.on", $$"""{"template":"{{action.Id}}","guid":"{{Hex(target)}}","at":[11,22,33],"map":1,"args":{{args}}}""");

            Assert.True(result.Ok, $"{action.Id}: {result.ErrorCode}: {result.ErrorMessage}");
            Assert.True(result.Data!["ok"]!.GetValue<bool>(), action.Id);
            JsonObject sent = rig.Requester.ActionLines.Single();
            Assert.Equal("local", sent["actor"]!["tier"]!.GetValue<string>());
            if (action.Template is not null)
            {
                Assert.Equal(action.Template, sent["template"]!.GetValue<string>());
            }
            else
            {
                Assert.StartsWith(action.Path!, sent["command"]!.GetValue<string>(), StringComparison.Ordinal);
                Assert.DoesNotContain('{', sent["command"]!.GetValue<string>());
            }

            seen.Add(action.Id);
        }

        Assert.Equal(DevUiCatalog.All.Count, seen.Count);
        Assert.True(seen.Count >= 20, $"only {seen.Count} catalog actions were exercised");
    }

    private static string ArgSample(UiActionArg arg) => arg.Type switch
    {
        "guid" => $"\"{Hex(Wolf)}\"",
        "number" => "1.5",
        _ => ((long)(arg.Default ?? arg.Min ?? 5)).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    [Fact]
    public async Task TemplateAction_SendsTheTypedRequest_WithPointAndTargets()
    {
        DevUiRig rig = Rig();

        JsonObject result = await rig.DataAsync("action.at", """{"template":"place.creature","at":[1.5,2.5,3.5],"map":530,"o":0.25,"args":{"entry":1234}}""");

        JsonObject sent = rig.Requester.ActionLines.Single();
        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Equal("place.creature", sent["template"]!.GetValue<string>());
        Assert.Equal(1234u, sent["args"]!["entry"]!.GetValue<uint>());
        Assert.Equal(530u, sent["point"]!["map"]!.GetValue<uint>());
        Assert.Equal(1.5, sent["point"]!["x"]!.GetValue<double>());
        Assert.Equal(0.25, sent["point"]!["o"]!.GetValue<double>());
        Assert.Null(sent["command"]);
    }

    [Theory]
    [InlineData("""{"template":"place.creature","at":[1,2,3],"args":{"entry":"1; .server shutdown"}}""")]
    [InlineData("""{"template":"place.creature","at":[1,2,3],"args":{"entry":"-1"}}""")]
    [InlineData("""{"template":"place.creature","at":[1,2,3],"args":{"entry":1,"extra":2}}""")]
    [InlineData("""{"template":"place.creature","at":[1,2,3]}""")]
    [InlineData("""{"template":"spawns","at":[1,2,3],"args":{"radius":99999}}""")]
    [InlineData("""{"template":"nope","at":[1,2,3]}""")]
    [InlineData("""{"template":"kill","at":[1,2,3]}""")]
    public async Task ActionAt_RefusesBadArguments_BeforeAnythingIsSentToTheDaemon(string args)
    {
        DevUiRig rig = Rig();

        CommandResult result = await rig.RunAsync("action.at", args);

        Assert.False(result.Ok, args);
        Assert.Empty(rig.Requester.ActionLines);
    }

    [Fact]
    public async Task ActionOn_RefusesAnEntityOfTheWrongKind_AndANameThatCouldInjectACommand()
    {
        DevUiRig rig = Rig();
        rig.Directory.Entities[Bot2] = FakeDirectory.Entity(Bot2, LiveKind.Player, "Bad; .server shutdown");

        CommandResult wrongKind = await rig.RunAsync("action.on", $$"""{"template":"kill","guid":"{{Hex(Bot1)}}"}""");
        CommandResult revive = await rig.RunAsync("action.on", $$"""{"template":"revive","guid":"{{Hex(Bot2)}}"}""");

        Assert.False(wrongKind.Ok);
        Assert.Contains("does not apply to a bot", wrongKind.ErrorMessage, StringComparison.Ordinal);
        Assert.False(revive.Ok);
        Assert.Equal(CommandException.BadArguments, revive.ErrorCode);
        Assert.Empty(rig.Requester.ActionLines);
    }

    [Fact]
    public async Task LineAction_PrintsOnlyValidatedText_AndTargetsItsSelection()
    {
        DevUiRig rig = Rig();

        await rig.DataAsync("action.on", $$$"""{"template":"setlevel","guid":"{{{Hex(Wolf)}}}","args":{"level":"12"}}""");
        await rig.DataAsync("action.on", $$"""{"template":"revive","guid":"{{Hex(Me)}}"}""");

        JsonObject[] sent = [.. rig.Requester.ActionLines];
        Assert.Equal("alter level 12", sent[0]["command"]!.GetValue<string>());
        Assert.Equal(Hex(Wolf), sent[0]["targets"]![0]!.GetValue<string>());
        Assert.Equal("revive Operator", sent[1]["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task BotAttack_UsesTheSelectedBots_AndTheClickedUnit()
    {
        DevUiRig rig = Rig();
        await rig.DataAsync("devui.select", $$"""{"guids":["{{Hex(Bot1)}}","{{Hex(Wolf)}}"]}""");

        JsonObject result = await rig.DataAsync("action.on", $$$"""{"template":"bot.attack","args":{"unit":"{{{Hex(Wolf)}}}"}}""");

        // The wolf is not a bot, so only the selected bot is told; one bot needs no bulk confirmation.
        Assert.True(result["ok"]!.GetValue<bool>());
        JsonObject sent = rig.Requester.ActionLines.Single();
        Assert.Equal($"botctl attack 0x{Bot1:X16} 0x{Wolf:X16}", sent["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task TeleportMe_NeedsTheOperatorCharacter_ThenSendsItsGuid()
    {
        DevUiRig rig = Rig();

        CommandResult without = await rig.RunAsync("action.at", """{"template":"tele.me","at":[1,2,3]}""");
        await rig.DataAsync("devui.character", """{"name":"operator"}""");
        JsonObject with = await rig.DataAsync("action.at", """{"template":"tele.me","at":[1,2,3],"map":1}""");

        Assert.False(without.Ok);
        Assert.Contains("devui.character", without.ErrorMessage, StringComparison.Ordinal);
        Assert.True(with["ok"]!.GetValue<bool>());
        JsonObject sent = rig.Requester.ActionLines.Single();
        Assert.Equal("tele", sent["template"]!.GetValue<string>());
        Assert.Equal(Hex(Me), sent["targets"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task SeveralTargetsOfALineAction_NeedAConfirmationCode_ThenRunEachOnce()
    {
        DevUiRig rig = Rig();
        rig.Directory.Entities[0x1000_0000_0000_0010] = FakeDirectory.Entity(0x1000_0000_0000_0010, LiveKind.Bot, "Gammabot");

        JsonObject first = await rig.DataAsync("action.on", $$"""{"template":"bot.take","targets":["{{Hex(Bot1)}}","{{Hex(Bot2)}}"]}""");

        Assert.False(first["ok"]!.GetValue<bool>());
        Assert.Equal("confirm-required", first["code"]!.GetValue<string>());
        Assert.Equal("atlas", first["confirm"]!["source"]!.GetValue<string>());
        Assert.Empty(rig.Requester.ActionLines);
        string code = first["confirm"]!["code"]!.GetValue<string>();

        CommandResult wrong = await rig.RunAsync("action.confirm", """{"code":"ZZZZZZ"}""");
        JsonObject done = await rig.DataAsync("action.confirm", $$"""{"code":"{{code.ToLowerInvariant()}}"}""");
        CommandResult again = await rig.RunAsync("action.confirm", $$"""{"code":"{{code}}"}""");

        Assert.False(wrong.Ok);
        Assert.True(done["ok"]!.GetValue<bool>());
        Assert.Equal(2, rig.Requester.ActionLines.Count());
        Assert.Equal([$"botctl take 0x{Bot1:X16}", $"botctl take 0x{Bot2:X16}"], rig.Requester.ActionLines.Select(m => m["command"]!.GetValue<string>()).ToArray());
        Assert.False(again.Ok);
    }

    [Fact]
    public async Task ADaemonConfirmation_IsPassedBackAndResubmittedWithItsCode()
    {
        DevUiRig rig = Rig();
        int calls = 0;
        rig.Requester.OnAction = m =>
        {
            calls++;
            return m["confirm"] is null
                ? new JsonObject
                {
                    ["ok"] = false,
                    ["code"] = "confirm-required",
                    ["message"] = "needs a code",
                    ["confirm"] = new JsonObject { ["code"] = "AB12CD", ["expiresIn"] = 60, ["summary"] = "kill on 2 targets" },
                }
                : new JsonObject { ["ok"] = true, ["lines"] = new JsonArray("killed"), ["settled"] = true };
        };

        JsonObject first = await rig.DataAsync("action.on", $$"""{"template":"kill","targets":["{{Hex(Wolf)}}"]}""");
        JsonObject second = await rig.DataAsync("action.confirm", """{"code":"AB12CD"}""");

        Assert.Equal("daemon", first["confirm"]!["source"]!.GetValue<string>());
        Assert.True(second["ok"]!.GetValue<bool>());
        Assert.Equal(2, calls);
        Assert.Equal("AB12CD", rig.Requester.ActionLines.Last()["confirm"]!.GetValue<string>());
        Assert.Single(rig.Actions.Log.Recent(10));
    }

    [Fact]
    public async Task ADaemonRefusal_IsTheActionsOutcome_NotACommandFailure()
    {
        DevUiRig rig = Rig();
        rig.Requester.OnAction = _ => new JsonObject { ["ok"] = false, ["code"] = "security", ["message"] = "not allowed" };

        JsonObject result = await rig.DataAsync("action.on", $$"""{"template":"kill","guid":"{{Hex(Wolf)}}"}""");

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal("security", result["code"]!.GetValue<string>());
        Assert.False(result["undoable"]!.GetValue<bool>());
        ActionEntry entry = Assert.Single(rig.Actions.Log.Recent(10));
        Assert.False(entry.Ok);
    }

    [Fact]
    public async Task WithoutAConnection_AnActionIsUnavailable_NotASilentSuccess()
    {
        var actions = new LiveActions(() => null, new FakeDirectory(), () => 0);
        var registry = new CommandRegistry();
        LiveActionCommands.Register(registry);
        var services = new AtlasServices();
        services.Add(actions);

        CommandResult result = await registry.InvokeAsync("action.run", new JsonObject { ["command"] = "creature kill" }, services, CommandOrigin.Cli, true, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(CommandException.Unavailable, result.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("creature kill\n.server shutdown")]
    [InlineData(".")]
    public async Task ActionRun_RefusesAnEmptyOrMultiLineCommand(string command)
    {
        DevUiRig rig = Rig();

        CommandResult result = await rig.RunAsync("action.run", new JsonObject { ["command"] = command }.ToJsonString());

        Assert.False(result.Ok);
        Assert.Empty(rig.Requester.ActionLines);
    }

    [Fact]
    public async Task ActionRun_SendsTheLineWithoutItsDot_AndTheSelectionAsTargets()
    {
        DevUiRig rig = Rig();

        await rig.DataAsync("action.run", $$"""{"command":".creature kill","targets":["{{Hex(Wolf)}}","{{Hex(Wolf)}}"],"at":[1,2,3],"map":0}""");

        JsonObject sent = rig.Requester.ActionLines.Single();
        Assert.Equal("creature kill", sent["command"]!.GetValue<string>());
        Assert.Single(sent["targets"]!.AsArray());
        Assert.Equal(3, sent["point"]!["z"]!.GetValue<double>());
    }

    [Fact]
    public async Task ActionRun_RefusesMoreThanFiftyTargets()
    {
        DevUiRig rig = Rig();
        string guids = string.Join(',', Enumerable.Range(1, 51).Select(i => $"\"{i:X}\""));

        CommandResult result = await rig.RunAsync("action.run", $$"""{"command":"creature kill","targets":[{{guids}}]}""");

        Assert.False(result.Ok);
        Assert.Contains("at most 50", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selection_SetAddRemoveClear_PublishesAndRefusesOverFifty()
    {
        DevUiRig rig = Rig();

        JsonObject set = await rig.DataAsync("devui.select", """{"guids":["A","B"]}""");
        JsonObject add = await rig.DataAsync("devui.select", """{"guids":["B","C"],"mode":"add"}""");
        JsonObject remove = await rig.DataAsync("devui.select", """{"guids":["A"],"mode":"remove"}""");
        JsonObject read = await rig.DataAsync("devui.selection", "{}");
        string many = string.Join(',', Enumerable.Range(1, 51).Select(i => $"\"{i:X}\""));
        CommandResult tooMany = await rig.RunAsync("devui.select", $$"""{"guids":[{{many}}]}""");
        JsonObject clear = await rig.DataAsync("devui.select", """{"mode":"clear"}""");

        Assert.Equal(2, set["count"]!.GetValue<int>());
        Assert.Equal(3, add["count"]!.GetValue<int>());
        Assert.Equal("B", remove["guids"]![0]!.GetValue<string>());
        Assert.Equal(2, read["count"]!.GetValue<int>());
        Assert.False(tooMany.Ok);
        Assert.Equal(0, clear["count"]!.GetValue<int>());
        Assert.Equal(4, rig.Published.Count(p => p.Topic == "devui.selection"));
        Assert.True(read["version"]!.GetValue<long>() >= 3);
    }

    [Fact]
    public async Task ActionOn_WithNoTargets_UsesTheSharedSelection()
    {
        DevUiRig rig = Rig();
        await rig.DataAsync("devui.select", $$"""{"guids":["{{Hex(Wolf)}}"]}""");

        await rig.DataAsync("action.on", """{"template":"kill"}""");
        CommandResult empty = await rig.RunAsync("devui.select", """{"mode":"clear"}""");
        CommandResult none = await rig.RunAsync("action.on", """{"template":"kill"}""");

        Assert.True(empty.Ok);
        Assert.False(none.Ok);
        Assert.Equal(Hex(Wolf), rig.Requester.ActionLines.Single()["targets"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task DebugAction_ReturnsAnOverlayTheUiAlreadyDraws()
    {
        DevUiRig rig = Rig();
        rig.Directory.Entities[Bot2] = FakeDirectory.Entity(Bot2, LiveKind.Player, "Pulled", 110, 200, 30);
        string json = $$"""{"guid":"{{Wolf:X16}}","entry":299,"aggroRadius":20,"leash":30,"home":{"x":90,"y":190,"z":30},"spawn":{"guid":5,"x":91,"y":191,"z":30},"waypoints":[{"x":1,"y":2,"z":3},{"x":4,"y":5,"z":6}],"threat":[{"guid":"{{Bot2:X16}}","threat":12.5}],"ai":"AggressorAI"}""";
        rig.Requester.OnAction = _ => new JsonObject { ["ok"] = true, ["lines"] = new JsonArray(json), ["settled"] = true };

        JsonObject result = await rig.DataAsync("action.on", $$"""{"template":"debug","guid":"{{Hex(Wolf)}}"}""");

        JsonObject overlay = result["overlay"]!.AsObject();
        Assert.True(overlay["polylines"]!.AsArray().Count >= 3, "aggro ring, leash ring and the route");
        Assert.Single(overlay["segments"]!.AsArray());
        Assert.Contains(overlay["points"]!.AsArray(), p => p!["label"]!.GetValue<string>() == "AI AggressorAI");
        Assert.Equal(new JsonArray(110, 200, 30).ToJsonString(), overlay["segments"]![0]!["to"]!.ToJsonString());
        Assert.Contains(overlay["polylines"]!.AsArray(), p => p!["label"]!.GetValue<string>() == "aggro 20");
    }

    [Fact]
    public async Task SpawnsAction_ReturnsOnePointPerRow()
    {
        DevUiRig rig = Rig();
        rig.Requester.OnAction = _ => new JsonObject
        {
            ["ok"] = true,
            ["lines"] = new JsonArray("""{"rows":[{"kind":"creature","guid":1,"entry":299,"x":1,"y":2,"z":3},{"kind":"gameobject","guid":2,"entry":5,"x":4,"y":5,"z":6}],"truncated":false}"""),
            ["settled"] = true,
        };

        JsonObject result = await rig.DataAsync("action.at", """{"template":"spawns","at":[0,0,0],"args":{"radius":50}}""");

        Assert.Equal(2, result["overlay"]!["points"]!.AsArray().Count);
    }

    [Fact]
    public async Task ActionLog_ListsNewestLast_AndPublishesEachAction()
    {
        DevUiRig rig = Rig();

        await rig.DataAsync("action.run", """{"command":"creature kill"}""");
        await rig.DataAsync("action.run", """{"command":"creature respawn"}""");
        JsonObject log = await rig.DataAsync("action.log", """{"limit":1}""");

        Assert.Equal(1, log["count"]!.GetValue<int>());
        Assert.Equal("creature respawn", log["entries"]![0]!["lines"]![0]!.GetValue<string>());
        Assert.Equal("atlas-local:daemon#1", log["entries"]![0]!["actor"]!.GetValue<string>());
        Assert.Equal(2, rig.Published.Count(p => p.Topic == "devui.action"));
        CommandResult bad = await rig.RunAsync("action.log", """{"limit":0}""");
        Assert.False(bad.Ok);
    }

    [Fact]
    public async Task ReadOnlyOverlayRefreshes_NeitherEvictTheUndoableLog_NorBroadcast()
    {
        DevUiRig rig = Rig();
        await rig.DataAsync("action.on", $$"""{"template":"kill","guid":"{{Hex(Wolf)}}"}""");
        long killId = Assert.Single(rig.Actions.Log.Recent(10)).Id;
        int broadcastBefore = rig.Published.Count(p => p.Topic == "devui.action");

        for (int i = 0; i < 1000; i++)
        {
            JsonObject refresh = await rig.DataAsync("action.on", $$"""{"template":"debug","guid":"{{Hex(Wolf)}}"}""");
            Assert.False(refresh["logged"]!.GetValue<bool>());
        }

        ActionEntry entry = Assert.Single(rig.Actions.Log.Recent(ActionLog.Capacity));
        Assert.Equal(killId, entry.Id);
        Assert.Equal(broadcastBefore, rig.Published.Count(p => p.Topic == "devui.action"));
        Assert.NotNull(rig.Actions.Log.Find(killId));
    }
}
