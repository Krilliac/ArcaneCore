using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Gm.Atlas;
using ArcaneCore.World.Bans;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Ops.MapFeed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static ArcaneCore.World.Tests.Ops.Control.WorldControlPipeTests;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// The world action channel (design <c>_land/atlas-devui-design.md</c> 1.1-1.6; docs/areas/atlas-actions.md): the <c>action</c> and
/// <c>caps</c> ops over the real map feed pipe. A real world on the manual clock; spy commands (<see cref="ActionSpies"/>) count what
/// ran so "refused" always means "ran zero times". Web actors need <c>Actions:WebLogin</c>, which the channel refuses to start with until the
/// true-console fix (<c>claude/fix-remote-console-bypass</c>, design 1.7) is on the base: those tests are red until that PR is merged.
/// </summary>
public sealed class MapFeedActionsTests
{
    private const string Category = "ArcaneCore.Ops.MapFeed.Actions";

    private static FeedWorldSetup LocalWorld(
        Action<MapFeedActionsOptions>? options = null, TimeProvider? time = null, bool content = false, int extraCreatures = 0, bool live = false)
        => new()
        {
            Terrain = true,
            Content = content,
            ExtraCreatures = extraCreatures,
            LiveServer = live,
            Time = time,
            Actions = o =>
            {
                o.Enabled = true;
                options?.Invoke(o);
            },
        };

    private static FeedWorldSetup WebWorld(
        FakeFeedTickets tickets, Action<MapFeedActionsOptions>? options = null, TimeProvider? time = null, bool content = false, int extraCreatures = 0)
        => new()
        {
            Terrain = true,
            Content = content,
            ExtraCreatures = extraCreatures,
            Time = time,
            Tickets = tickets,
            Actions = o =>
            {
                o.Enabled = true;
                o.WebLogin = true;
                options?.Invoke(o);
            },
        };

    // ---- default configuration ----------------------------------------------------------------------------------------------

    [WindowsFact]
    public async Task ByDefault_TheChannelIsOff_CapsSaysWhy_AndAnActionLineIsRefusedActionsOff()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Actions = _ => { } });
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync();

        JsonElement caps = await ActionWire.RequestAsync(client, """{"op":"caps","id":1}""", 1);
        Assert.True(caps.Ok(), caps.ToString());
        Assert.False(caps.GetProperty("actions").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(caps.GetProperty("actionsWhyOff").GetString()), "the reason the channel is off is part of the answer");
        Assert.False(caps.GetProperty("liveServer").GetBoolean());

        JsonElement refused = await ActionWire.ActAsync(client, 2, ActionWire.Local(), command: ".zplayer");
        refused.AssertRefused("actions-off");
        Assert.Equal(0, spies.Total);
    }

    [WindowsFact]
    public async Task TheHello_IsTheSameWithTheChannelRegistered_AsWithout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string[] Names(JsonElement hello) => [.. hello.EnumerateObject().Select(p => p.Name)];

        string[] without;
        await using (FeedWorld plain = await FeedWorld.StartAsync(new FeedWorldSetup()))
        {
            FeedClient client = await plain.ConnectAsync(hello: false);
            without = Names(await client.HelloAsync());
        }

        await using FeedWorld on = await FeedWorld.StartAsync(LocalWorld());
        FeedClient withChannel = await on.ConnectAsync(hello: false);
        JsonElement hello = await withChannel.HelloAsync();
        Assert.Equal(without, Names(hello));
        Assert.False(hello.TryGetProperty("actions", out _), "capability discovery is the caps op, not the hello (design 1.1, r2)");
    }

    [WindowsFact]
    public async Task Enabled_CapsSaysActionsOn_AndNamesTheOperatorByTheClientProcessToken()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld());
        FeedClient client = await world.ConnectAsync();

        JsonElement caps = await ActionWire.RequestAsync(client, """{"op":"caps","id":1}""", 1);

        Assert.True(caps.GetProperty("actions").GetBoolean(), caps.ToString());
        Assert.False(caps.TryGetProperty("actionsWhyOff", out JsonElement why) && why.ValueKind == JsonValueKind.String, "nothing to explain when on");
        string expectedActor = ActionWire.ExpectedLocalActor();
        Assert.Equal(expectedActor[..expectedActor.LastIndexOf('#')], caps.GetProperty("operator").GetString());
        Assert.Equal("local", caps.GetProperty("role").GetString());
    }

    // ---- security and the invoker -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AccountSecurity.Player, "zplayer", null)]
    [InlineData(AccountSecurity.Player, "zmod", "security")]
    [InlineData(AccountSecurity.Moderator, "zmod", null)]
    [InlineData(AccountSecurity.Moderator, "zspy", "security")]
    [InlineData(AccountSecurity.GameMaster, "zspy", null)]
    [InlineData(AccountSecurity.GameMaster, "zadmin", "security")]
    [InlineData(AccountSecurity.Administrator, "zadmin", null)]
    public async Task TheLocalActor_RunsAtItsConfiguredSecurity_AndOnlyThere(AccountSecurity local, string command, string? refusedWith)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld(o => o.LocalSecurity = local));
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync();

        JsonElement result = await ActionWire.ActAsync(client, 1, ActionWire.Local(), command: command);

        if (refusedWith is null)
        {
            Assert.True(result.Ok(), result.ToString());
            Assert.Contains($"{command} ran", result.Lines());
            Assert.Equal(1, spies.Count(command));
        }
        else
        {
            result.AssertRefused(refusedWith);
            Assert.Equal(0, spies.Total);
        }
    }

    [WindowsFact]
    public async Task TheLocalActor_RunsThroughForRemote_NotAsTheConsole_AndTheNameInTheRequestIsIgnored()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld(o => o.LocalSecurity = AccountSecurity.GameMaster));
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync();

        JsonElement result = await ActionWire.ActAsync(client, 1, ActionWire.Local(ignoredName: "Mallory"), command: ".zwho");

        string expectedActor = ActionWire.ExpectedLocalActor();
        Assert.True(result.Ok(), result.ToString());
        Assert.Equal($"who:{expectedActor}:GameMaster", Assert.Single(result.Lines()));
        ActionSpies.Call call = Assert.Single(spies.Calls);
        Assert.True(call.IsRemote, "the channel builds ForRemote contexts, never ForConsole");
        Assert.Equal(expectedActor, call.Invoker);
        Assert.Equal(AccountSecurity.GameMaster, call.Security);
        Assert.DoesNotContain("Mallory", world.ActionLog.Select(e => e.Message).Concat(world.Host.WorldServices.GetRequiredService<GmAuditFeature>().Tail(50).Select(e => e.Player)));
    }

    [WindowsFact]
    public async Task ADotIsOptional_AndAnUnknownOrEmptyCommandIsAnAnswer_NotAStrike()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld());
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync();

        Assert.True((await ActionWire.ActAsync(client, 1, ActionWire.Local(), command: "zplayer")).Ok());
        Assert.True((await ActionWire.ActAsync(client, 2, ActionWire.Local(), command: ".zplayer")).Ok());
        Assert.Equal(2, spies.Count("zplayer"));

        (await ActionWire.ActAsync(client, 3, ActionWire.Local(), command: ".znosuchcommand")).AssertRefused("unknown-command");
        (await ActionWire.ActAsync(client, 4, ActionWire.Local(), command: "  ")).AssertRefused(MapFeedProtocol.BadArgs);
        (await ActionWire.ActAsync(client, 5, ActionWire.Local())).AssertRefused(MapFeedProtocol.BadArgs);
        (await ActionWire.ActAsync(client, 6, ActionWire.Local(), command: ".zplayer", template: "place.creature")).AssertRefused(MapFeedProtocol.BadArgs);
        (await ActionWire.ActAsync(client, 7, ActionWire.Local(), template: "no.such.template")).AssertRefused("unknown-command");

        // Six refusals are one strike short of nothing: the connection is still served (MaxStrikes counts uncorrelated errors only).
        Assert.True((await ActionWire.ActAsync(client, 8, ActionWire.Local(), command: "zplayer")).Ok());
        Assert.Equal(3, spies.Count("zplayer"));
    }

    // ---- audit ------------------------------------------------------------------------------------------------------------

    [WindowsFact]
    public async Task EveryAction_IsAudited_WithTheActorsInvokerName_EvenALevelZeroCommandTheTableDoesNotLog_AndARefusedOne()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld(o => o.LocalSecurity = AccountSecurity.GameMaster));
        ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync();
        string actor = ActionWire.ExpectedLocalActor();

        Assert.True((await ActionWire.ActAsync(client, 1, ActionWire.Local(), command: ".zplayer")).Ok());
        (await ActionWire.ActAsync(client, 2, ActionWire.Local(), command: ".zadmin")).AssertRefused("security");

        CapturedCategoryLog[] lines = world.ActionLog;
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.Equal(LogLevel.Information, l.Level));
        Assert.StartsWith($"atlas action actor={actor} tier=local cmd=zplayer targets=0 ", lines[0].Message, StringComparison.Ordinal);
        Assert.EndsWith("-> ok", lines[0].Message, StringComparison.Ordinal);
        Assert.StartsWith($"atlas action actor={actor} tier=local cmd=zadmin targets=0 ", lines[1].Message, StringComparison.Ordinal);
        Assert.EndsWith("-> security", lines[1].Message, StringComparison.Ordinal);

        // The table never logs a level-0 command (GmCommandLog.ShouldLog needs a level above 0): the channel records it itself.
        IReadOnlyList<GmAuditEntry> tail = world.Host.WorldServices.GetRequiredService<GmAuditFeature>().Tail(50);
        Assert.Contains(tail, e => e.Player == actor && e.Command.StartsWith("zplayer", StringComparison.Ordinal));
        Assert.Contains(tail, e => e.Player == actor && e.Command.StartsWith("zadmin", StringComparison.Ordinal));
    }

    // ---- the live marker ----------------------------------------------------------------------------------------------------

    [WindowsFact]
    public async Task OnTheLiveServer_ActionsAreRefused_CapsSaysSo_AndTheDecisionIsAWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld(live: true));
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync();

        JsonElement caps = await ActionWire.RequestAsync(client, """{"op":"caps","id":1}""", 1);
        Assert.False(caps.GetProperty("actions").GetBoolean(), caps.ToString());
        Assert.True(caps.GetProperty("liveServer").GetBoolean());
        Assert.Contains("live", caps.GetProperty("actionsWhyOff").GetString(), StringComparison.OrdinalIgnoreCase);

        (await ActionWire.ActAsync(client, 2, ActionWire.Local(), command: ".zplayer")).AssertRefused("live-server");
        Assert.Equal(0, spies.Total);
        Assert.Contains(world.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("live server", StringComparison.OrdinalIgnoreCase)
            && e.Message.Contains("actions", StringComparison.OrdinalIgnoreCase));
    }

    [WindowsFact]
    public async Task OnTheLiveServer_AllowOnLiveServer_TurnsThemOn_WithAWarningNamingTheLevel()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld(o =>
        {
            o.AllowOnLiveServer = true;
            o.LocalSecurity = AccountSecurity.GameMaster;
        }, live: true));
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync();

        Assert.True((await ActionWire.ActAsync(client, 1, ActionWire.Local(), command: ".zspy")).Ok());
        Assert.Equal(1, spies.Count("zspy"));
        CapturedCategoryLog warning = Assert.Single(
            world.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith("map feed actions ON on the live server", StringComparison.Ordinal));
        Assert.Contains("GameMaster", warning.Message, StringComparison.Ordinal);
    }

    // ---- click context reaching a command -----------------------------------------------------------------------------------

    [WindowsFact]
    public async Task PlaceCreatureTemplate_PutsThePointIntoTheCommand_AndTheCreatureAppearsInTheNextFrame()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld(content: true));
        await world.AddPlayerAsync("Feedalice");
        FeedClient client = await world.ConnectAsync();
        float x = FeedWorld.StartX + 3f;
        float y = FeedWorld.StartY + 2.5f;
        float z = FeedWorld.StartZ;

        JsonElement result = await ActionWire.ActAsync(
            client, 1, ActionWire.Local(), template: "place.creature",
            args: new JsonObject { ["entry"] = FeedWorld.WolfEntry }, point: ActionWire.Point(0, x, y, z, 1.5f));

        Assert.True(result.Ok(), result.ToString());
        string expanded = Assert.Single(world.ActionLog).Message;
        Assert.Contains("cmd=atlas place creature 299 0 ", expanded, StringComparison.Ordinal);

        await client.SendAsync(FeedClient.Subscribe());
        JsonElement frame = await client.ReadOpAsync("frame");
        JsonElement placed = Assert.Single(frame.OfKind("creature"), e =>
            e.GetProperty("g").GetString() != FeedWorld.Hex(FeedWorld.WolfGuid) && e.GetProperty("e").GetUInt32() == FeedWorld.WolfEntry);
        Assert.InRange(placed.GetProperty("x").GetSingle(), x - 0.05f, x + 0.05f);
        Assert.InRange(placed.GetProperty("y").GetSingle(), y - 0.05f, y + 0.05f);
    }

    [WindowsFact]
    public async Task PlaceCreatureTemplate_OnAnUnloadedMap_IsMapNotLoaded_AndPlacesNothing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld(content: true));
        await world.AddPlayerAsync("Feedalice");
        FeedClient client = await world.ConnectAsync();

        JsonElement result = await ActionWire.ActAsync(
            client, 1, ActionWire.Local(), template: "place.creature",
            args: new JsonObject { ["entry"] = FeedWorld.WolfEntry }, point: ActionWire.Point(1, -2000f, -2000f, 50f));

        result.AssertRefused("map-not-loaded");
        await client.SendAsync(FeedClient.Subscribe());
        JsonElement frame = await client.ReadOpAsync("frame");
        Assert.Single(frame.OfKind("creature"));
    }

    [WindowsFact]
    public async Task MoveToTemplate_ExpandsTheTargetGuidAsAnExplicitSelector_AndThePoint()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld(content: true));
        await world.AddPlayerAsync("Feedalice");
        FeedClient client = await world.ConnectAsync();
        string wolf = FeedWorld.Hex(FeedWorld.WolfGuid);

        JsonElement result = await ActionWire.ActAsync(
            client, 1, ActionWire.Local(), template: "moveto", targets: [wolf], point: ActionWire.Point(0, -8941f, -131f, 83.5f));

        Assert.True(result.Ok(), result.ToString());
        Assert.Contains($"cmd=atlas moveto 0x{wolf} ", Assert.Single(world.ActionLog).Message, StringComparison.Ordinal);
        JsonElement target = Assert.Single(result.GetProperty("targets").EnumerateArray());
        Assert.Equal(wolf, target.GetProperty("guid").GetString());
        Assert.True(target.GetProperty("ok").GetBoolean());
    }

    [WindowsFact]
    public async Task AGuidThatIsNotInTheWorld_IsTargetGone_AndTheCommandDoesNotRun()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld(content: true));
        await world.AddPlayerAsync("Feedalice");
        FeedClient client = await world.ConnectAsync();

        JsonElement result = await ActionWire.ActAsync(
            client, 1, ActionWire.Local(), template: "moveto", targets: ["F130000123000000"], point: ActionWire.Point(0, -8941f, -131f, 83.5f));

        result.AssertRefused("target-gone");
        Assert.EndsWith("-> target-gone", Assert.Single(world.ActionLog).Message, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task TheClickContext_IsStillAttached_WhenTheCommandsWorkRunsInsideAWorldPostDeferral()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld());
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync();

        Task<JsonElement> pending = ActionWire.ActAsync(client, 1, ActionWire.Local(), command: ".zdefer", point: ActionWire.Point(0, 12.5f, -34.25f, 56f, 0.75f));
        await WorldTestHost.WaitForAsync(() => spies.Count("zdefer") == 1, "the command ran");
        await world.StepAsync(2); // the posted work runs at tick start, long after the setter's flow ended (an AsyncLocal would be gone)
        JsonElement result = await pending;

        Assert.True(result.Ok(), result.ToString());
        Assert.True(result.GetProperty("settled").GetBoolean());
        Assert.True(spies.DeferredSawContext, "no action context inside the deferral");
        Assert.NotNull(spies.DeferredPoint);
        GmClickPoint point = spies.DeferredPoint!.Value;
        Assert.Equal(0u, point.Map);
        Assert.Equal(12.5f, point.X);
        Assert.Equal(-34.25f, point.Y);
        Assert.Equal(56f, point.Z);
        Assert.Equal(0.75f, point.O);
    }

    // ---- bulk confirm ---------------------------------------------------------------------------------------------------------

    private static async Task<(JsonElement First, string Code)> BulkMoveAsync(FeedClient client, long id, JsonObject actor, string[] targets, float x = -8941f)
    {
        JsonElement first = await ActionWire.ActAsync(client, id, actor, template: "moveto", targets: targets, point: ActionWire.Point(0, x, -131f, 83.5f));
        first.AssertRefused("confirm-required");
        JsonElement confirm = first.GetProperty("confirm");
        string code = confirm.GetProperty("code").GetString()!;
        Assert.Matches("^[A-Z0-9]{6}$", code);
        Assert.Equal(60, confirm.GetProperty("expiresIn").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(confirm.GetProperty("summary").GetString()));
        return (first, code);
    }

    private static string[] TwoWolves() => [FeedWorld.Hex(FeedWorld.WolfGuid), FeedWorld.Hex(FeedWorld.ExtraWolfGuid(0))];

    private static Task<int> MovingWolvesAsync(FeedWorld world) => world.Host.OnWorldAsync(() =>
        world.Host.World.FindMap(0, 0)!.FindUpdater<CreatureMapSystem>()!.Creatures.Count(c => c.Spline is not null));

    private static async Task<FeedWorld> BulkWorldAsync(FeedTestClock time, FakeFeedTickets? tickets = null)
    {
        FeedWorldSetup setup = tickets is null ? LocalWorld(time: time, content: true, extraCreatures: 1) : WebWorld(tickets, time: time, content: true, extraCreatures: 1);
        FeedWorld world = await FeedWorld.StartAsync(setup);
        await world.AddPlayerAsync("Feedalice");
        return world;
    }

    [WindowsFact]
    public async Task ABulkTemplate_WithoutACode_IsConfirmRequired_AndMovesNothing_ThenTheCodeRunsItOnce()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var time = new FeedTestClock();
        await using FeedWorld world = await BulkWorldAsync(time);
        FeedClient client = await world.ConnectAsync();
        string[] targets = TwoWolves();

        (_, string code) = await BulkMoveAsync(client, 1, ActionWire.Local(), targets);
        Assert.Equal(0, await MovingWolvesAsync(world));
        Assert.EndsWith("-> confirm-required", Assert.Single(world.ActionLog).Message, StringComparison.Ordinal);

        JsonElement confirmed = await ActionWire.ActAsync(
            client, 2, ActionWire.Local(), template: "moveto", targets: targets, point: ActionWire.Point(0, -8941f, -131f, 83.5f), confirm: code);
        Assert.True(confirmed.Ok(), confirmed.ToString());
        Assert.Equal(2, confirmed.GetProperty("targets").GetArrayLength());
        Assert.All(confirmed.GetProperty("targets").EnumerateArray(), t => Assert.True(t.GetProperty("ok").GetBoolean()));
        Assert.Equal(2, await MovingWolvesAsync(world));

        // A code is spent by the run it confirmed.
        JsonElement replay = await ActionWire.ActAsync(
            client, 3, ActionWire.Local(), template: "moveto", targets: targets, point: ActionWire.Point(0, -8941f, -131f, 83.5f), confirm: code);
        replay.AssertRefused("confirm-wrong");
    }

    [WindowsFact]
    public async Task ASingleTarget_NeedsNoConfirmCode_BecauseBulkConfirmAboveIsOne()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var time = new FeedTestClock();
        await using FeedWorld world = await BulkWorldAsync(time);
        FeedClient client = await world.ConnectAsync();

        JsonElement result = await ActionWire.ActAsync(
            client, 1, ActionWire.Local(), template: "moveto", targets: [TwoWolves()[0]], point: ActionWire.Point(0, -8941f, -131f, 83.5f));

        Assert.True(result.Ok(), result.ToString());
        Assert.Equal(1, await MovingWolvesAsync(world));
    }

    [WindowsFact]
    public async Task ThreeWrongCodes_KillTheToken_SoEvenTheRightCodeIsThenRefused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var time = new FeedTestClock();
        await using FeedWorld world = await BulkWorldAsync(time);
        FeedClient client = await world.ConnectAsync();
        string[] targets = TwoWolves();
        (_, string code) = await BulkMoveAsync(client, 1, ActionWire.Local(), targets);
        string wrong = code == "AAAAAA" ? "BBBBBB" : "AAAAAA";

        for (int attempt = 0; attempt < 3; attempt++)
        {
            (await ActionWire.ActAsync(client, 10 + attempt, ActionWire.Local(), template: "moveto", targets: targets,
                point: ActionWire.Point(0, -8941f, -131f, 83.5f), confirm: wrong)).AssertRefused("confirm-wrong");
        }

        (await ActionWire.ActAsync(client, 20, ActionWire.Local(), template: "moveto", targets: targets,
            point: ActionWire.Point(0, -8941f, -131f, 83.5f), confirm: code)).AssertRefused("confirm-wrong");
        Assert.Equal(0, await MovingWolvesAsync(world));
    }

    [WindowsFact]
    public async Task ACode_ExpiresAfterSixtySeconds_AndIsBoundToTheTargetsAndThePoint()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var time = new FeedTestClock();
        await using FeedWorld world = await BulkWorldAsync(time);
        FeedClient client = await world.ConnectAsync();
        string[] targets = TwoWolves();

        // Another point: the code was issued for the first one.
        (_, string moved) = await BulkMoveAsync(client, 1, ActionWire.Local(), targets);
        (await ActionWire.ActAsync(client, 2, ActionWire.Local(), template: "moveto", targets: targets,
            point: ActionWire.Point(0, -8930f, -131f, 83.5f), confirm: moved)).AssertRefused("confirm-wrong");

        // Another target list.
        (_, string reordered) = await BulkMoveAsync(client, 3, ActionWire.Local(), targets);
        (await ActionWire.ActAsync(client, 4, ActionWire.Local(), template: "moveto", targets: [targets[0], FeedWorld.Hex(FeedWorld.ExtraWolfGuid(0) + 1)],
            point: ActionWire.Point(0, -8941f, -131f, 83.5f), confirm: reordered)).AssertRefused("confirm-wrong");

        // The same request, after the minute is up.
        (_, string expiring) = await BulkMoveAsync(client, 5, ActionWire.Local(), targets);
        time.Advance(TimeSpan.FromSeconds(61));
        (await ActionWire.ActAsync(client, 6, ActionWire.Local(), template: "moveto", targets: targets,
            point: ActionWire.Point(0, -8941f, -131f, 83.5f), confirm: expiring)).AssertRefused("confirm-wrong");
        Assert.Equal(0, await MovingWolvesAsync(world));
    }

    [WindowsFact]
    public async Task ACode_IssuedToOneWebActor_IsRefusedForAnother_AndTheOwnerCanStillUseIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var time = new FeedTestClock();
        FakeFeedTickets tickets = new FakeFeedTickets()
            .Set("T-A", 71, "WEBA", AccountSecurity.GameMaster)
            .Set("T-B", 72, "WEBB", AccountSecurity.GameMaster);
        await using FeedWorld world = await BulkWorldAsync(time, tickets);
        FeedClient client = await world.ConnectAsync("web");
        string[] targets = TwoWolves();

        (_, string code) = await BulkMoveAsync(client, 1, ActionWire.Web("T-A"), targets);

        (await ActionWire.ActAsync(client, 2, ActionWire.Web("T-B"), template: "moveto", targets: targets,
            point: ActionWire.Point(0, -8941f, -131f, 83.5f), confirm: code)).AssertRefused("confirm-wrong");
        Assert.Equal(0, await MovingWolvesAsync(world));

        JsonElement owner = await ActionWire.ActAsync(client, 3, ActionWire.Web("T-A"), template: "moveto", targets: targets,
            point: ActionWire.Point(0, -8941f, -131f, 83.5f), confirm: code);
        Assert.True(owner.Ok(), owner.ToString());
        Assert.Equal(2, await MovingWolvesAsync(world));
    }

    // ---- rate limit -----------------------------------------------------------------------------------------------------------

    [WindowsFact]
    public async Task TheWebRateLimit_IsPerActor_RefillsWithTime_AndARefusedActionDoesNotRun()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var time = new FeedTestClock();
        FakeFeedTickets tickets = new FakeFeedTickets()
            .Set("T-A", 71, "WEBA", AccountSecurity.GameMaster)
            .Set("T-B", 72, "WEBB", AccountSecurity.GameMaster);
        await using FeedWorld world = await FeedWorld.StartAsync(WebWorld(tickets, o =>
        {
            o.WebPerSecond = 2;
            o.WebBurst = 5;
        }, time));
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync("web");

        for (int i = 0; i < 5; i++)
        {
            Assert.True((await ActionWire.ActAsync(client, 1 + i, ActionWire.Web("T-A"), command: ".zplayer")).Ok(), $"burst action {i}");
        }

        JsonElement limited = await ActionWire.ActAsync(client, 6, ActionWire.Web("T-A"), command: ".zplayer");
        limited.AssertRefused("rate-limited");
        Assert.InRange(limited.GetProperty("retryMs").GetInt32(), 1, 1000);
        Assert.Equal(5, spies.Count("zplayer"));
        Assert.EndsWith("-> rate-limited", world.ActionLog[^1].Message, StringComparison.Ordinal);

        Assert.True((await ActionWire.ActAsync(client, 7, ActionWire.Web("T-B"), command: ".zplayer")).Ok(), "another actor has its own bucket");

        time.Advance(TimeSpan.FromSeconds(1)); // two tokens at 2 per second
        Assert.True((await ActionWire.ActAsync(client, 8, ActionWire.Web("T-A"), command: ".zplayer")).Ok());
        Assert.Equal(7, spies.Count("zplayer"));
    }

    [WindowsFact]
    public async Task TheLocalRateLimit_UsesItsOwnBurst()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var time = new FeedTestClock();
        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld(o =>
        {
            o.LocalPerSecond = 1;
            o.LocalBurst = 2;
        }, time));
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync();

        Assert.True((await ActionWire.ActAsync(client, 1, ActionWire.Local(), command: ".zplayer")).Ok());
        Assert.True((await ActionWire.ActAsync(client, 2, ActionWire.Local(), command: ".zplayer")).Ok());
        (await ActionWire.ActAsync(client, 3, ActionWire.Local(), command: ".zplayer")).AssertRefused("rate-limited");
        Assert.Equal(2, spies.Count("zplayer"));
    }

    // ---- web tickets ----------------------------------------------------------------------------------------------------------

    [WindowsFact]
    public async Task AWebActor_NeedsAValidTicket_EachFailureHasItsOwnCode_AndNothingRuns()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        FakeFeedTickets tickets = new FakeFeedTickets()
            .Set("T-EXPIRED", 71, "WEBA", AccountSecurity.GameMaster, FeedTicketStatus.Expired)
            .Set("T-BANNED", 72, "WEBB", AccountSecurity.GameMaster, FeedTicketStatus.Banned);
        await using FeedWorld world = await FeedWorld.StartAsync(WebWorld(tickets));
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync("web");

        (await ActionWire.RequestAsync(client, ActionWire.Action(1, new JsonObject { ["tier"] = "web" }, command: ".zplayer"), 1)).AssertRefused("no-ticket");
        (await ActionWire.ActAsync(client, 2, ActionWire.Web("T-NOSUCH"), command: ".zplayer")).AssertRefused("no-ticket");
        (await ActionWire.ActAsync(client, 3, ActionWire.Web("T-EXPIRED"), command: ".zplayer")).AssertRefused("expired");
        (await ActionWire.ActAsync(client, 4, ActionWire.Web("T-BANNED"), command: ".zplayer")).AssertRefused("banned");
        Assert.Equal(0, spies.Total);
    }

    [WindowsFact]
    public async Task ABanOrADemotion_TakesEffectWithin30Seconds_OnTheFakeClock()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var time = new FeedTestClock();
        FakeFeedTickets tickets = new FakeFeedTickets().Set("T-GM", 77, "WEBGM", AccountSecurity.GameMaster);
        await using FeedWorld world = await FeedWorld.StartAsync(WebWorld(tickets, time: time));
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync("web");

        Assert.True((await ActionWire.ActAsync(client, 1, ActionWire.Web("T-GM"), command: ".zspy")).Ok());

        // Demoted: within 30 s (the cache's age) at the latest, the next action runs at the new level.
        tickets.Set("T-GM", 77, "WEBGM", AccountSecurity.Moderator);
        time.Advance(TimeSpan.FromSeconds(31));
        (await ActionWire.ActAsync(client, 2, ActionWire.Web("T-GM"), command: ".zspy")).AssertRefused("security");
        Assert.True((await ActionWire.ActAsync(client, 3, ActionWire.Web("T-GM"), command: ".zmod")).Ok());

        // Banned.
        tickets.Set("T-GM", 77, "WEBGM", AccountSecurity.Moderator, FeedTicketStatus.Banned);
        time.Advance(TimeSpan.FromSeconds(31));
        (await ActionWire.ActAsync(client, 4, ActionWire.Web("T-GM"), command: ".zmod")).AssertRefused("banned");

        Assert.Equal(1, spies.Count("zspy"));
        Assert.Equal(1, spies.Count("zmod"));
    }

    [WindowsFact]
    public async Task AnAdministratorAccount_IsClampedToPublicMaxSecurity_AndPublicDenyMatchesTheResolvedPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        FakeFeedTickets tickets = new FakeFeedTickets().Set("T-ADMIN", 78, "WEBADMIN", AccountSecurity.Administrator);
        await using FeedWorld world = await FeedWorld.StartAsync(WebWorld(tickets));
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync("web");

        // PublicMaxSecurity defaults to GameMaster: an Administrator logged in over the Internet acts as a GameMaster.
        (await ActionWire.ActAsync(client, 1, ActionWire.Web("T-ADMIN"), command: ".zadmin")).AssertRefused("security");
        Assert.True((await ActionWire.ActAsync(client, 2, ActionWire.Web("T-ADMIN"), command: ".zwho")).Lines().SequenceEqual(["who:atlas-web:WEBADMIN:GameMaster"]));
        Assert.Equal(0, spies.Count("zadmin"));

        await using FeedWorld raised = await FeedWorld.StartAsync(WebWorld(tickets, o =>
        {
            o.PublicMaxSecurity = AccountSecurity.Administrator;
            o.PublicDeny = ["zadmin"];
        }));
        ActionSpies raisedSpies = ActionSpies.Install(raised.Host);
        FeedClient other = await raised.ConnectAsync("web");

        // Denied by the path the lookup resolved to, so an abbreviation cannot dodge it (CommandTable.Lookup resolves the first prefix match).
        (await ActionWire.ActAsync(other, 1, ActionWire.Web("T-ADMIN"), command: ".zadm")).AssertRefused("security");
        (await ActionWire.ActAsync(other, 2, ActionWire.Web("T-ADMIN"), command: ".ZADMIN")).AssertRefused("security");
        Assert.Equal(0, raisedSpies.Count("zadmin"));
    }

    [WindowsFact]
    public async Task ActionsDeny_AppliesToEveryActor_IncludingTheLocalOne()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld(o => o.Deny = ["zadmin"]));
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync();

        (await ActionWire.ActAsync(client, 1, ActionWire.Local(), command: ".zadm")).AssertRefused("security");
        Assert.True((await ActionWire.ActAsync(client, 2, ActionWire.Local(), command: ".zspy")).Ok());
        Assert.Equal(0, spies.Count("zadmin"));
    }

    // ---- executor isolation ---------------------------------------------------------------------------------------------------

    [WindowsFact]
    public async Task TwoActorsInterleavedOnOneWebConnection_NeverShareALevel_AndEachAuditLineNamesItsSender()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        FakeFeedTickets tickets = new FakeFeedTickets()
            .Set("T-A", 71, "ALPHA", AccountSecurity.GameMaster)
            .Set("T-B", 72, "BRAVO", AccountSecurity.Moderator);
        await using FeedWorld world = await FeedWorld.StartAsync(WebWorld(tickets, o =>
        {
            o.WebPerSecond = 100;
            o.WebBurst = 100;
        }));
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync("web");
        long id = 0;

        for (int round = 0; round < 4; round++)
        {
            JsonElement a = await ActionWire.ActAsync(client, ++id, ActionWire.Web("T-A"), command: ".zspy");
            JsonElement b = await ActionWire.ActAsync(client, ++id, ActionWire.Web("T-B"), command: ".zspy");
            JsonElement aWho = await ActionWire.ActAsync(client, ++id, ActionWire.Web("T-A"), command: ".zwho");
            JsonElement bWho = await ActionWire.ActAsync(client, ++id, ActionWire.Web("T-B"), command: ".zwho");

            Assert.True(a.Ok(), a.ToString());
            b.AssertRefused("security"); // B is a Moderator: never at A's level, however the requests interleave
            Assert.Equal("who:atlas-web:ALPHA:GameMaster", Assert.Single(aWho.Lines()));
            Assert.Equal("who:atlas-web:BRAVO:Moderator", Assert.Single(bWho.Lines()));
        }

        Assert.Equal(4, spies.Count("zspy"));
        Assert.All(spies.Calls.Where(c => c.Name == "zspy"), c => Assert.Equal("atlas-web:ALPHA", c.Invoker));
        Assert.All(spies.Calls.Where(c => c.Name == "zwho"), c => Assert.Equal(c.Invoker == "atlas-web:ALPHA" ? AccountSecurity.GameMaster : AccountSecurity.Moderator, c.Security));

        CapturedCategoryLog[] lines = world.ActionLog;
        Assert.Equal(16, lines.Length);
        for (int i = 0; i < lines.Length; i += 4)
        {
            Assert.Contains("actor=atlas-web:ALPHA tier=web cmd=zspy", lines[i].Message, StringComparison.Ordinal);
            Assert.Contains("actor=atlas-web:BRAVO tier=web cmd=zspy", lines[i + 1].Message, StringComparison.Ordinal);
            Assert.EndsWith("-> security", lines[i + 1].Message, StringComparison.Ordinal);
            Assert.Contains("actor=atlas-web:ALPHA tier=web cmd=zwho", lines[i + 2].Message, StringComparison.Ordinal);
            Assert.Contains("actor=atlas-web:BRAVO tier=web cmd=zwho", lines[i + 3].Message, StringComparison.Ordinal);
        }
    }

    // ---- injection --------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("299; .zadmin")]
    [InlineData("299\n.zadmin")]
    [InlineData("299\r\n.zadmin")]
    [InlineData("0x12G")]
    [InlineData("-1")]
    [InlineData("4294967296")]
    [InlineData("1e3")]
    [InlineData("")]
    public async Task ATemplateArgument_ThatIsNotANumber_IsBadArgs_AndNothingIsExpandedOrRun(string entry)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld(content: true));
        await world.AddPlayerAsync("Feedalice");
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync();

        JsonElement result = await ActionWire.ActAsync(
            client, 1, ActionWire.Local(), template: "place.creature",
            args: new JsonObject { ["entry"] = entry }, point: ActionWire.Point(0, FeedWorld.StartX, FeedWorld.StartY, FeedWorld.StartZ));

        result.AssertRefused(MapFeedProtocol.BadArgs);
        Assert.Equal(0, spies.Total);
        await client.SendAsync(FeedClient.Subscribe());
        Assert.Single((await client.ReadOpAsync("frame")).OfKind("creature"));
    }

    [Theory]
    [InlineData("zspy\n.zadmin")]
    [InlineData("zspy\r.zadmin")]
    [InlineData("zspy\0.zadmin")]
    public async Task ARawCommandWithALineBreak_IsBadArgs_AndTheSecondCommandNeverRuns(string command)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld());
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync();

        (await ActionWire.ActAsync(client, 1, ActionWire.Local(), command: command)).AssertRefused(MapFeedProtocol.BadArgs);
        Assert.Equal(0, spies.Total);
    }

    [WindowsFact]
    public async Task ASemicolonInACommand_IsPlainText_NotASecondCommand()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld());
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync();

        JsonElement result = await ActionWire.ActAsync(client, 1, ActionWire.Local(), command: ".zspy ; .zadmin ; .server shutdown 0");

        Assert.True(result.Ok(), result.ToString());
        Assert.Equal(0, spies.Count("zadmin"));
        Assert.Equal("; .zadmin ; .server shutdown 0", Assert.Single(spies.Calls).Args);
        await world.StepAsync(2);
        Assert.True((await ActionWire.ActAsync(client, 2, ActionWire.Local(), command: ".zplayer")).Ok(), "the world is still running and serving");
    }

    [Theory]
    [InlineData("0x12; .zadmin")]
    [InlineData("not-hex")]
    [InlineData("F13000012300000000")]
    [InlineData("")]
    public async Task ATargetThatIsNotAGuid_IsBadArgs(string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld());
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync();

        (await ActionWire.ActAsync(client, 1, ActionWire.Local(), command: ".zspy", targets: [target])).AssertRefused(MapFeedProtocol.BadArgs);
        Assert.Equal(0, spies.Total);
    }

    [WindowsFact]
    public async Task FiftyTargetsAreTheLimit_FiftyOneIsTooManyTargets()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld());
        ActionSpies spies = ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync();
        string[] Guids(int count) => [.. Enumerable.Range(1, count).Select(i => FeedWorld.Hex(0xF130000000000000UL + (ulong)i))];

        (await ActionWire.ActAsync(client, 1, ActionWire.Local(), command: ".zspy", targets: Guids(51))).AssertRefused("too-many-targets");
        Assert.Equal(0, spies.Total);

        JsonElement atLimit = await ActionWire.ActAsync(client, 2, ActionWire.Local(), command: ".zspy", targets: Guids(50));
        Assert.NotEqual("too-many-targets", atLimit.CodeOrNull());
        Assert.NotEqual(MapFeedProtocol.BadArgs, atLimit.CodeOrNull());
    }

    [WindowsFact]
    public async Task APointWithANonFiniteOrOutOfRangeNumber_IsBadArgs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld(content: true));
        await world.AddPlayerAsync("Feedalice");
        ActionSpies.Install(world.Host);
        FeedClient client = await world.ConnectAsync();
        var args = new JsonObject { ["entry"] = FeedWorld.WolfEntry };

        JsonObject huge = ActionWire.Point(0, 1, 2, 3);
        huge["x"] = 1e40; // does not fit a float: a non-finite coordinate after the parse
        (await ActionWire.ActAsync(client, 1, ActionWire.Local(), template: "place.creature", args: args, point: huge)).AssertRefused(MapFeedProtocol.BadArgs);
        JsonObject missing = ActionWire.Point(0, 1, 2, 3);
        missing.Remove("y");
        (await ActionWire.ActAsync(client, 2, ActionWire.Local(), template: "place.creature", args: args, point: missing)).AssertRefused(MapFeedProtocol.BadArgs);
        (await ActionWire.ActAsync(client, 3, ActionWire.Local(), template: "place.creature", args: args)).AssertRefused(MapFeedProtocol.BadArgs);
    }

    // ---- remote bypasses (design 1.7), through the channel ---------------------------------------------------------------

    [Theory]
    [InlineData("web")]
    [InlineData("local")]
    public async Task AGameMasterActor_CannotActOnAnOfflineAdministratorsCharacter(string tier)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        FakeFeedTickets tickets = new FakeFeedTickets().Set("T-GM", 77, "WEBGM", AccountSecurity.GameMaster);
        await using FeedWorld world = await FeedWorld.StartAsync(tier == "web"
            ? WebWorld(tickets)
            : LocalWorld(o => o.LocalSecurity = AccountSecurity.GameMaster));
        ActionSpies spies = ActionSpies.Install(world.Host);
        await world.Host.AddAccountAsync("OFFADMIN", AccountSecurity.Administrator);
        await world.Host.AddAccountAsync("OFFPLAYER", AccountSecurity.Player);
        int adminId = (await world.Host.Accounts.FindByUsernameAsync("OFFADMIN"))!.Id;
        int playerId = (await world.Host.Accounts.FindByUsernameAsync("OFFPLAYER"))!.Id;
        FeedClient client = await world.ConnectAsync(tier);
        JsonObject actor = tier == "web" ? ActionWire.Web("T-GM") : ActionWire.Local();

        // Control: a character of an account the GameMaster outranks goes through the shared offline gate and runs.
        JsonElement allowed = await ActionWire.ActAsync(client, 1, actor, command: $".zoffline {playerId}");
        Assert.True(allowed.Ok(), allowed.ToString());
        await WorldTestHost.WaitForAsync(() => spies.OfflineRan == 1, "the offline action of a lower account runs");

        JsonElement refused = await ActionWire.ActAsync(client, 2, actor, command: $".zoffline {adminId}");
        Assert.Contains("You have low security level for this.", refused.Lines());
        Assert.Equal(1, spies.OfflineRan);
    }

    [Theory]
    [InlineData("web")]
    [InlineData("local")]
    public async Task AGameMasterActor_CannotBanAnAdministratorAccount_ButCanBanAPlayerAccount(string tier)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        FakeFeedTickets tickets = new FakeFeedTickets().Set("T-GM", 77, "WEBGM", AccountSecurity.GameMaster);
        await using FeedWorld world = await FeedWorld.StartAsync(tier == "web"
            ? WebWorld(tickets, o => o.PublicAllowModeration = true) // the public deny-list would refuse .ban before the command ran
            : LocalWorld(o => o.LocalSecurity = AccountSecurity.GameMaster));
        await world.Host.AddAccountAsync("BANADMIN", AccountSecurity.Administrator);
        await world.Host.AddAccountAsync("BANVICTIM", AccountSecurity.Player);
        int adminId = (await world.Host.Accounts.FindByUsernameAsync("BANADMIN"))!.Id;
        int victimId = (await world.Host.Accounts.FindByUsernameAsync("BANVICTIM"))!.Id;
        FeedClient client = await world.ConnectAsync(tier);
        JsonObject actor = tier == "web" ? ActionWire.Web("T-GM") : ActionWire.Local();

        // The ban command answers from a background task after it returned, so its reply is a late actionLine, not part of the result.
        var others = new List<JsonElement>();
        JsonElement refused = await ActionWire.RequestAsync(client, ActionWire.Action(1, actor, command: ".ban account banadmin 1h testing"), 1, others);
        Assert.True(refused.Ok(), refused.ToString());
        Assert.Contains(BanCommandText.TargetSecurityTooHigh, await RepliesUntilAsync(client, refused, others, BanCommandText.TargetSecurityTooHigh));
        Assert.Null(await world.Host.Bans.GetActiveAccountBanAsync(adminId));

        others.Clear();
        JsonElement allowed = await ActionWire.RequestAsync(client, ActionWire.Action(2, actor, command: ".ban account banvictim 1h testing"), 2, others);
        Assert.True(allowed.Ok(), allowed.ToString());
        Assert.Contains("BANVICTIM is banned for 1h. Reason: testing.", await RepliesUntilAsync(client, allowed, others, "BANVICTIM is banned for 1h. Reason: testing."));
        Assert.NotNull(await world.Host.Bans.GetActiveAccountBanAsync(victimId));
    }

    /// <summary>The lines a command printed: those in the result, those that came before it, and (reading on) the late <c>actionLine</c>s until <paramref name="expected"/> arrives.</summary>
    private static async Task<List<string>> RepliesUntilAsync(FeedClient client, JsonElement result, List<JsonElement> before, string expected)
    {
        List<string> lines = [.. result.Lines(), .. before.Where(l => l.Op() == "actionLine").Select(l => l.GetProperty("line").GetString()!)];
        while (!lines.Contains(expected))
        {
            JsonElement next = await client.ReadAsync(); // bounded: a reply that never comes fails the test
            if (next.Op() == "actionLine")
            {
                lines.Add(next.GetProperty("line").GetString()!);
            }
        }

        return lines;
    }

    [Theory]
    [InlineData("web")]
    [InlineData("local")]
    public async Task AConsoleOnlyLeaf_AnswersConsoleOnly_ForTheLocalActorAndForAWebAdministrator(string tier)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The registry the true-console fix adds (design 1.7, "Console-only"): resolved command paths the channel refuses before it executes.
        ArcaneCore.World.Commands.ConsoleOnlyCommands.Register("zconsole");
        try
        {
            FakeFeedTickets tickets = new FakeFeedTickets().Set("T-ADMIN", 78, "WEBADMIN", AccountSecurity.Administrator);
            await using FeedWorld world = await FeedWorld.StartAsync(tier == "web"
                ? WebWorld(tickets, o => o.PublicMaxSecurity = AccountSecurity.Administrator)
                : LocalWorld(o => o.LocalSecurity = AccountSecurity.Administrator));
            ActionSpies spies = ActionSpies.Install(world.Host);
            FeedClient client = await world.ConnectAsync(tier);

            JsonElement result = await ActionWire.ActAsync(client, 1, tier == "web" ? ActionWire.Web("T-ADMIN") : ActionWire.Local(), command: ".zconsole");

            result.AssertRefused("console-only");
            Assert.Equal(0, spies.Count("zconsole"));
        }
        finally
        {
            ArcaneCore.World.Commands.ConsoleOnlyCommands.Unregister("zconsole"); // the registry is process-wide
        }
    }

    // ---- web login needs the true-console fix -----------------------------------------------------------------------------

    [Fact]
    public void WebLogin_IsOnlyOffered_WhenTheTrueConsolePredicateIsOnTheBase()
    {
        // This build carries the predicate the web gate probes for, so web login can be offered ...
        Assert.True(ActionsGate.PredicatePresent);
        var options = new MapFeedActionsOptions { Enabled = true, WebLogin = true };
        var logs = new CapturingLoggerProvider();
        using ILoggerFactory loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        ILogger logger = loggers.CreateLogger("gate");
        Assert.True(ActionsGate.Decide(options, liveServer: false, hasTickets: true, predicatePresent: true, logger).Login);

        // ... and a build without it refuses web actors, saying why.
        ActionsGate without = ActionsGate.Decide(options, liveServer: false, hasTickets: true, predicatePresent: false, logger);
        Assert.False(without.Login);
        Assert.True(without.On, "the local channel is unaffected");
        Assert.Contains(logs.Entries, e => e.Level >= LogLevel.Warning && e.Message.Contains("WebLogin", StringComparison.Ordinal));
    }
}
