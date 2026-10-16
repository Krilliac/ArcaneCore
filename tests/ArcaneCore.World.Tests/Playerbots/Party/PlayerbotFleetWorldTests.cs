using System.Numerics;
using System.Text.RegularExpressions;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// A world with managed bots, optional real GM clients and a console rig. Everything is parked at an open spot far from the test map's
/// area triggers (MapTestData puts them on the human start's east-west line), so a summoned or walking bot never reports one.
/// </summary>
internal sealed class FleetWorld : IAsyncDisposable
{
    public const float BaseX = -8000f;
    public const float BaseY = 1000f;
    public const float BaseZ = 100f;

    private bool _started;

    private FleetWorld(WorldTestHost host)
    {
        Host = host;
        Console = new FleetConsoleRig(host);
    }

    public WorldTestHost Host { get; }

    public FleetConsoleRig Console { get; }

    public Dictionary<string, Guid> Ids { get; } = [];

    public ManagedPlayerbotFeature Bots => PartyTestHost.Feature(Host);

    public static Task<FleetWorld> StartAsync(Action<PlayerbotOptions>? configure = null, Action<IServiceCollection>? configureServices = null)
    {
        WorldTestHost host = PartyTestHost.Start(options =>
        {
            options.MaxBots = 8;
            configure?.Invoke(options);
        }, configureServices);
        return Task.FromResult(new FleetWorld(host));
    }

    private async Task EnsureStartedAsync()
    {
        if (_started) return;
        _started = true;
        await Bots.StartupAsync(default);
    }

    /// <summary>Create and start a bot and park it <paramref name="dx"/> yards east of the base spot.</summary>
    public async Task<Guid> AddBotAsync(string name, float dx)
    {
        await EnsureStartedAsync();
        PlayerbotOperationResult created = await Bots.CreateAsync(name, 1, 1);
        if (!created.Success || created.BotId is not { } id) throw new InvalidOperationException("create: " + created.Code);
        PlayerbotOperationResult started = await Bots.StartAsync(id.ToString());
        if (!started.Success) throw new InvalidOperationException("start: " + started.Code);
        await Host.WaitForWorldAsync(() => Host.World.FindOnlinePlayer(name) is { IsInWorld: true }, name + " logs in");
        await Host.PlaceAsync(name, BaseX + dx, BaseY, BaseZ);
        Ids[name] = id;
        return id;
    }

    /// <summary>Register a bot without starting it.</summary>
    public async Task<Guid> CreateStoppedBotAsync(string name)
    {
        await EnsureStartedAsync();
        PlayerbotOperationResult created = await Bots.CreateAsync(name, 1, 1);
        if (!created.Success || created.BotId is not { } id) throw new InvalidOperationException("create: " + created.Code);
        Ids[name] = id;
        return id;
    }

    /// <summary>An Administrator client in the world, standing on the base spot.</summary>
    public async Task<WorldTestClient> AddGmAsync(string account, string character)
    {
        WorldTestClient client = await Host.EnterWorldAsync(account, character, AccountSecurity.Administrator);
        await Host.PlaceAsync(character, BaseX, BaseY, BaseZ);
        await client.CollectAsync();
        return client;
    }

    public Task<(float X, float Y, float Z)> PositionAsync(string name) => Host.PlayerStateAsync(name, p => (p.X, p.Y, p.Z));

    public static float Xy(Player p, Player q) => Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(q.X, q.Y));

    /// <summary>A GM client types a command; returns the first <paramref name="lines"/> system lines of the reply.</summary>
    public static async Task<List<string>> SayAsync(WorldTestClient gm, string line, int lines = 1)
    {
        await gm.SendChatAsync(ChatType.Say, Language.Common, line);
        var reply = new List<string>();
        while (reply.Count < lines)
        {
            ChatMessage message = await gm.ReadChatAsync();
            if (message.Type == ChatType.System) reply.Add(message.Text);
        }

        return reply;
    }

    private bool Teleporting(Player player) => Host.WorldServices.GetRequiredService<TeleportFeature>().Teleports.IsBeingTeleported(player);

    /// <summary>
    /// Wait until every bot has arrived on <paramref name="anchor"/>'s map in a ring around it (3 to 19 yards), then check that no two
    /// stand within 2 yards of each other.
    /// </summary>
    public async Task WaitAroundAsync(IReadOnlyList<string> bots, string anchor)
    {
        await Host.WaitForWorldAsync(() =>
        {
            Player? a = Host.World.FindOnlinePlayer(anchor);
            if (a is not { IsInWorld: true }) return false;
            foreach (string name in bots)
            {
                if (Host.World.FindOnlinePlayer(name) is not { IsInWorld: true } p || p.MapId != a.MapId || Teleporting(p)) return false;
                float d = Xy(p, a);
                if (d is < 2.9f or > 19f) return false;
            }

            return true;
        }, string.Join(", ", bots) + " stand around " + anchor);
        await Host.OnWorldAsync(() =>
        {
            Player[] players = [.. bots.Select(b => Host.World.FindOnlinePlayer(b)!)];
            for (int i = 0; i < players.Length; i++)
                for (int j = i + 1; j < players.Length; j++)
                    Assert.True(Xy(players[i], players[j]) >= 2f, $"{players[i].Name} and {players[j].Name} stand {Xy(players[i], players[j])} yards apart");
        });
    }

    public Task WaitNearAsync(string name, uint mapId, float x, float y, float z, float maxXyDistance)
        => Host.WaitForWorldAsync(() => Host.World.FindOnlinePlayer(name) is { IsInWorld: true } p && p.MapId == mapId && !Teleporting(p)
            && Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(x, y)) is var d && d >= 2.9f && d <= maxXyDistance && MathF.Abs(p.Z - z) < 0.5f,
            $"{name} stands near {x},{y} on map {mapId}");

    /// <summary>Send a bot to Kalimdor (a far teleport the bot's own session acknowledges) and wait until it is there.</summary>
    public async Task SendToKalimdorAsync(string name)
    {
        Assert.True(await Host.OnWorldAsync(() => Host.WorldServices.GetRequiredService<TeleportFeature>().Teleports
            .TeleportTo(Host.World.FindOnlinePlayer(name)!, 1, -441.8f, -2596.08f, 96.2f, 0f)));
        await Host.WaitForWorldAsync(() => Host.World.FindOnlinePlayer(name) is { IsInWorld: true, MapId: 1 }, name + " reaches Kalimdor");
    }

    public async ValueTask DisposeAsync()
    {
        await Console.DisposeAsync();
        await Host.DisposeAsync();
    }
}

/// <summary>A scripted controller that does nothing (a bot some other system drives).</summary>
internal sealed class IdleController : IPlayerbotController
{
    public void Tick(PlayerbotControllerContext context, uint elapsedMs)
    {
    }

    public void Detached(Guid botId)
    {
    }
}

/// <summary>
/// The fleet commands against a real world (real handlers and clock): a GM socket client or the console types the command, managed
/// bots move. Every wait names its condition.
/// </summary>
public sealed class PlayerbotFleetWorldTests
{
    [Fact]
    public async Task Summon_Prefix_MovesMatchingRunningBotsAroundTheGm_NoTwoOnOneSpot()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await using WorldTestClient gm = await world.AddGmAsync("FLEETAGM", "Fleetagm");
        string[] mine = ["Fleetaaa", "Fleetaab", "Fleetaac"];
        for (int i = 0; i < mine.Length; i++) await world.AddBotAsync(mine[i], 100 + (10 * i));
        await world.AddBotAsync("Otherbot", 150);
        (float X, float Y, float Z) other = await world.PositionAsync("Otherbot");

        List<string> reply = await FleetWorld.SayAsync(gm, ".playerbot summon fleetaa*");

        Assert.Equal(["Playerbot summon: matched=3 moved=3 skipped=0"], reply);
        await world.WaitAroundAsync(mine, "Fleetagm");
        Assert.Equal(other, await world.PositionAsync("Otherbot"));
        // They stand level with the GM (no terrain data: the anchor's z is kept) and run on, not stalled or faulted.
        foreach (string name in mine)
        {
            Assert.Equal(FleetWorld.BaseZ, (await world.PositionAsync(name)).Z, 1);
            PlayerbotStatus status = world.Bots.Snapshot().Single(s => s.Name == name);
            Assert.Equal(ManagedPlayerbotState.Running, status.State);
        }
    }

    [Fact]
    public async Task Summon_Count_TakesNearestOnTheSameMapFirst()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await using WorldTestClient gm = await world.AddGmAsync("FLEETBGM", "Fleetbgm");
        await world.AddBotAsync("Fleetbaa", 40);
        await world.AddBotAsync("Fleetbab", 20);
        await world.AddBotAsync("Fleetbac", 60);
        await world.AddBotAsync("Fleetbad", 10);
        await world.SendToKalimdorAsync("Fleetbad"); // nearest by name order, but on another map
        (float X, float Y, float Z) farthest = await world.PositionAsync("Fleetbac");

        List<string> reply = await FleetWorld.SayAsync(gm, ".playerbot summon Fleetba* 2");

        Assert.Equal(["Playerbot summon: matched=4 moved=2 skipped=0"], reply);
        await world.WaitAroundAsync(["Fleetbaa", "Fleetbab"], "Fleetbgm");
        Assert.Equal(farthest, await world.PositionAsync("Fleetbac"));
        Assert.Equal(1u, await world.Host.PlayerStateAsync("Fleetbad", p => p.MapId));
    }

    [Fact]
    public async Task Summon_ReportsSkipReasons_DeadNotRunningScriptedTeleporting()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddGmAsync("FLEETCGM", "Fleetcgm");
        await world.AddBotAsync("Fleetcaa", 30);
        await world.AddBotAsync("Fleetcab", 31);
        await world.CreateStoppedBotAsync("Fleetcac");
        Guid scripted = await world.AddBotAsync("Fleetcad", 33);
        await world.AddBotAsync("Fleetcae", 34);
        Assert.True(await world.Bots.SetControllerAsync(scripted, new IdleController()));

        string[] lines = await world.Console.RunAsync(".playerbot summon Fleetca* to Fleetcgm", () =>
        {
            Player master = world.Host.World.FindOnlinePlayer("Fleetcgm")!;
            Player dead = world.Host.World.FindOnlinePlayer("Fleetcab")!;
            dead.Map!.Combat.DealDamage(master, dead, dead.Health);
            Assert.False(dead.IsAlive);
            Assert.True(world.Host.WorldServices.GetRequiredService<TeleportFeature>().Teleports
                .TeleportTo(world.Host.World.FindOnlinePlayer("Fleetcae")!, 1, -441.8f, -2596.08f, 96.2f, 0f));
        });

        Assert.Equal("Playerbot summon: matched=5 moved=1 skipped=4 (not-running=1 dead=1 scripted=1 teleporting=1)", lines[0]);
        Assert.Contains("Fleetcab: dead", lines);
        Assert.Contains("Fleetcac: not-running", lines);
        Assert.Contains("Fleetcad: scripted", lines);
        Assert.Contains("Fleetcae: teleporting", lines);
        Assert.Equal(5, lines.Length);
        await world.WaitAroundAsync(["Fleetcaa"], "Fleetcgm");
    }

    [Fact]
    public async Task Summon_IntoAnInstance_SkipsBotsOutsideTheGmsGroup()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Fleetdaa", 30);
        await world.AddBotAsync("Fleetdab", 31);
        Assert.True(await world.Host.OnWorldAsync(() => world.Host.WorldServices.GetRequiredService<TeleportFeature>().Teleports
            .TeleportTo(world.Host.World.FindOnlinePlayer("Fleetdab")!, 36, -16.4f, -383.07f, 61.78f, 0f)));
        await world.Host.WaitForWorldAsync(() => world.Host.World.FindOnlinePlayer("Fleetdab") is { IsInWorld: true, MapId: 36 }, "Fleetdab is in the Deadmines");
        (float X, float Y, float Z) before = await world.PositionAsync("Fleetdaa");

        // The anchor is in an instance: a bot of no group with it cannot be summoned there (vmangos LANG_CANNOT_SUMMON_TO_INST).
        string[] lines = await world.Console.RunAsync(".playerbot summon Fleetdaa to Fleetdab");

        Assert.Equal(["Playerbot summon: matched=1 moved=0 skipped=1 (instance=1)", "Fleetdaa: instance"], lines);
        Assert.Equal(before, await world.PositionAsync("Fleetdaa"));
        Assert.Equal(0u, await world.Host.PlayerStateAsync("Fleetdaa", p => p.MapId));
    }

    [Fact]
    public async Task Summon_PrefixNeverMatchesARealPlayerWithThatPrefix()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await using WorldTestClient gm = await world.AddGmAsync("FLEETEGM", "Fleetegm");
        await using WorldTestClient player = await world.Host.EnterWorldAsync("FLEETEPL", "Fleetepl");
        await world.Host.PlaceAsync("Fleetepl", FleetWorld.BaseX + 300, FleetWorld.BaseY, FleetWorld.BaseZ);
        await world.AddBotAsync("Fleetebot", 100);
        (float X, float Y, float Z) real = await world.PositionAsync("Fleetepl");

        List<string> reply = await FleetWorld.SayAsync(gm, ".playerbot summon Fleete*");
        List<string> exact = await FleetWorld.SayAsync(gm, ".playerbot summon Fleetepl");

        Assert.Equal(["Playerbot summon: matched=1 moved=1 skipped=0"], reply);
        Assert.Equal(["No playerbot matches 'Fleetepl'."], exact);
        Assert.Equal(real, await world.PositionAsync("Fleetepl"));
        await world.WaitAroundAsync(["Fleetebot"], "Fleetegm");

        // 'all' is every registered bot: the real players are still not in it.
        List<string> all = await FleetWorld.SayAsync(gm, ".playerbot summon all");
        Assert.Equal(["Playerbot summon: matched=1 moved=1 skipped=0"], all);
        Assert.Equal(real, await world.PositionAsync("Fleetepl"));
    }

    [Fact]
    public async Task Summon_AutonomousBotFromKalimdor_CompletesTheWorldportAndKeepsThinkingOnMapZero()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await using WorldTestClient gm = await world.AddGmAsync("FLEETFGM", "Fleetfgm");
        Guid id = await world.AddBotAsync("Fleetfaa", 100);
        await world.SendToKalimdorAsync("Fleetfaa");
        Assert.False(world.Bots.IsPartyDriven(id));

        List<string> reply = await FleetWorld.SayAsync(gm, ".playerbot summon Fleetfaa");

        Assert.Equal(["Playerbot summon: matched=1 moved=1 skipped=0"], reply);
        // A cross-map summon is a far teleport: SMSG_NEW_WORLD, then the bot's own MSG_MOVE_WORLDPORT_ACK
        // (PlayerbotMovementControl.AcknowledgeTransfer, pumped every tick), and only then is it in the map again.
        await world.WaitAroundAsync(["Fleetfaa"], "Fleetfgm");
        Assert.Equal(0u, await world.Host.PlayerStateAsync("Fleetfaa", p => p.MapId));
        PlayerbotStatus status = world.Bots.Snapshot().Single(s => s.BotId == id);
        Assert.Equal(ManagedPlayerbotState.Running, status.State);
        Assert.Null(status.ErrorCode);
        Assert.NotNull(world.Bots.FindBrain(id));
        // Its brain keeps thinking: the status line reports map 0 and the bot answers a further command.
        await world.Host.WaitForWorldAsync(() => world.Bots.Snapshot().Single(s => s.BotId == id).MapId == 0, "the status line reports map 0");
        Assert.Equal(["Playerbot summon: matched=1 moved=1 skipped=0"], await FleetWorld.SayAsync(gm, ".playerbot summon Fleetfaa"));
    }

    [Fact]
    public async Task Send_TeleName_PlacesBotsInARingAtTheGameTele_FarAndNear()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Fleetgaa", 30);
        await world.AddBotAsync("Fleetgab", 31);
        await world.AddBotAsync("Fleetgac", 32);
        await world.AddBotAsync("Fleetgba", 33);

        string[] near = await world.Console.RunAsync(".playerbot send Fleetga* Stormwind");
        string[] far = await world.Console.RunAsync(".playerbot send Fleetgba Crossroads");

        Assert.Equal(["Playerbot send: matched=3 moved=3 skipped=0"], near);
        Assert.Equal(["Playerbot send: matched=1 moved=1 skipped=0"], far);
        foreach (string name in new[] { "Fleetgaa", "Fleetgab", "Fleetgac" })
            await world.WaitNearAsync(name, 0, -8913.23f, 554.633f, 93.7944f, 19f);
        await world.WaitNearAsync("Fleetgba", 1, -441.8f, -2596.08f, 96.2155f, 19f);
        await world.Host.OnWorldAsync(() =>
        {
            Player[] ring = [.. new[] { "Fleetgaa", "Fleetgab", "Fleetgac" }.Select(n => world.Host.World.FindOnlinePlayer(n)!)];
            for (int a = 0; a < ring.Length; a++)
                for (int b = a + 1; b < ring.Length; b++) Assert.True(FleetWorld.Xy(ring[a], ring[b]) >= 2f);
        });
    }

    [Fact]
    public async Task Send_Here_UsesTheInvokersPosition()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await using WorldTestClient gm = await world.AddGmAsync("FLEETHGM", "Fleethgm");
        await world.AddBotAsync("Fleethaa", 100);
        await world.AddBotAsync("Fleethab", 110);

        List<string> reply = await FleetWorld.SayAsync(gm, ".playerbot send Fleetha* here");

        Assert.Equal(["Playerbot send: matched=2 moved=2 skipped=0"], reply);
        await world.WaitAroundAsync(["Fleethaa", "Fleethab"], "Fleethgm");
    }

    [Fact]
    public async Task Follow_WithoutAParty_FollowsTheGm_AndInspectShowsMaster()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await PartyTestHost.InstallFlatGroundAsync(world.Host, FleetWorld.BaseZ);
        await using WorldTestClient gm = await world.AddGmAsync("FLEETIGM", "Fleetigm");
        Guid id = await world.AddBotAsync("Fleetiaa", 30);

        List<string> reply = await FleetWorld.SayAsync(gm, ".playerbot follow Fleetiaa");

        Assert.Equal(["Playerbot follow: matched=1 following=1 skipped=0"], reply);
        Assert.True(world.Bots.IsPartyDriven(id));
        Assert.False(await world.Host.OnWorldAsync(() => world.Host.WorldServices.GetRequiredService<ArcaneCore.World.Social.SocialFeature>()
            .Context.Groups.GetGroup(world.Host.World.FindOnlinePlayer("Fleetiaa")!.Guid) is not null));
        PlayerbotInspection inspection = (await world.Bots.InspectAsync("Fleetiaa"))!;
        Assert.Equal("Fleetigm", inspection.Master);
        Assert.Equal(PlayerbotPartyMode.Follow, inspection.PartyMode);
        await world.Host.WaitForWorldAsync(() => world.Host.World.FindOnlinePlayer("Fleetiaa") is { } b
            && world.Host.World.FindOnlinePlayer("Fleetigm") is { } m && FleetWorld.Xy(b, m) <= 6f, "the bot walks up to the GM");
        Assert.Equal(PlayerbotGoalKind.Follow, world.Bots.Snapshot().Single(s => s.BotId == id).Goal);
    }

    [Fact]
    public async Task Stay_HoldsPosition_AndFightsBackOnlyItsAttackers()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await PartyTestHost.InstallFlatGroundAsync(world.Host, FleetWorld.BaseZ);
        await using WorldTestClient gm = await world.AddGmAsync("FLEETJGM", "Fleetjgm");
        Guid id = await world.AddBotAsync("Fleetjaa", 3);

        List<string> reply = await FleetWorld.SayAsync(gm, ".playerbot stay Fleetjaa");

        Assert.Equal(["Playerbot stay: matched=1 staying=1 skipped=0"], reply);
        Assert.Equal(PlayerbotPartyMode.Stay, await world.Host.OnWorldAsync(() => world.Bots.FindParty(id)!.Mode));
        await world.Host.WaitForWorldAsync(() => world.Host.World.FindOnlinePlayer("Fleetjaa") is { } p && (p.Movement.Flags & MovementFlags.MaskMoving) == 0, "the bot stands");
        await world.Host.PlaceAsync("Fleetjgm", FleetWorld.BaseX, FleetWorld.BaseY + 80, FleetWorld.BaseZ);
        (float X, float Y, float Z) place = await world.PositionAsync("Fleetjaa");

        // Its master walked 80 yards away; a staying bot holds its place.
        await Task.Delay(800);
        Assert.Equal(place, await world.PositionAsync("Fleetjaa"));

        // It fights back what attacks it, standing where it is: a creature hits it from beside it and the bot targets it.
        await world.Host.OnWorldAsync(() =>
        {
            Player bot = world.Host.World.FindOnlinePlayer("Fleetjaa")!;
            var attacker = new Creature(992_950, new CreatureTemplate
            {
                Entry = 992_950, Name = "attacker", CreatureType = 1, MinLevelHealth = 20_000, MaxLevelHealth = 20_000,
            }, null, CreatureContent.Empty, new Random(7));
            attacker.Relocate(bot.X + 2f, bot.Y, bot.Z, 0, world.Host.World.NowMs);
            bot.Map!.AddObject(attacker);
            if (!bot.Map.Combat.Attack(attacker, bot)) throw new InvalidOperationException("the attacker could not engage the bot");
            bot.Map.Combat.DealDamage(attacker, bot, 1);
        });
        await world.Host.WaitForWorldAsync(() => world.Host.World.FindOnlinePlayer("Fleetjaa") is { } p && p.Combat.Victim is not null, "the staying bot fights back");
        (float X, float Y, float Z) fighting = await world.PositionAsync("Fleetjaa");
        Assert.True(Vector3.Distance(new Vector3(fighting.X, fighting.Y, fighting.Z), new Vector3(place.X, place.Y, place.Z)) <= 1.5f);
    }

    [Fact]
    public async Task Dismiss_ReturnsTheBotToItsBrain()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await PartyTestHost.InstallFlatGroundAsync(world.Host, FleetWorld.BaseZ);
        await using WorldTestClient gm = await world.AddGmAsync("FLEETKGM", "Fleetkgm");
        Guid id = await world.AddBotAsync("Fleetkaa", 30);
        PlayerbotBrain before = world.Bots.FindBrain(id)!;
        await FleetWorld.SayAsync(gm, ".playerbot follow Fleetkaa");
        Assert.True(world.Bots.IsPartyDriven(id));

        List<string> reply = await FleetWorld.SayAsync(gm, ".playerbot dismiss Fleetkaa");

        Assert.Equal(["Playerbot dismiss: matched=1 dismissed=1 skipped=0"], reply);
        await world.Host.WaitForWorldAsync(() => !world.Bots.IsPartyDriven(id), "the bot is autonomous again");
        Assert.NotSame(before, world.Bots.FindBrain(id));
        Assert.Null(world.Bots.FindParty(id));
        Assert.NotEqual(PlayerbotGoalKind.Follow, world.Bots.Snapshot().Single(s => s.BotId == id).Goal);
        PlayerbotInspection inspection = (await world.Bots.InspectAsync("Fleetkaa"))!;
        Assert.Null(inspection.Master);
        // A second dismiss has nothing to end.
        Assert.Equal(["Playerbot dismiss: matched=1 dismissed=0 skipped=1 (not-leashed=1)", "Fleetkaa: not-leashed"], await FleetWorld.SayAsync(gm, ".playerbot dismiss Fleetkaa", 2));
    }

    [Fact]
    public async Task Follow_LeashMasterLogsOut_BotReturnsToAutonomy()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await PartyTestHost.InstallFlatGroundAsync(world.Host, FleetWorld.BaseZ);
        WorldTestClient gm = await world.AddGmAsync("FLEETLGM", "Fleetlgm");
        Guid id = await world.AddBotAsync("Fleetlaa", 30);
        await FleetWorld.SayAsync(gm, ".playerbot follow Fleetlaa");
        Assert.True(world.Bots.IsPartyDriven(id));
        PlayerbotBrain leashedBrain = world.Bots.FindBrain(id)!;

        await world.Host.OnWorldAsync(() => ((WorldSession)world.Host.World.FindOnlinePlayer("Fleetlgm")!.Session).Kick());
        await gm.DisposeAsync();

        await world.Host.WaitForWorldAsync(() => world.Host.World.FindOnlinePlayer("Fleetlgm") is null && !world.Bots.IsPartyDriven(id),
            "the master is gone and the bot is autonomous again");
        Assert.NotSame(leashedBrain, world.Bots.FindBrain(id));
        Assert.Equal(ManagedPlayerbotState.Running, world.Bots.Snapshot().Single(s => s.BotId == id).State);
    }

    [Fact]
    public async Task Follow_RefusesBotsInARealPlayersGroup_AndABotAsMaster()
    {
        await using FleetWorld world = await FleetWorld.StartAsync(o => o.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);
        await using WorldTestClient gm = await world.AddGmAsync("FLEETMGM", "Fleetmgm");
        await using WorldTestClient leader = await world.Host.EnterWorldAsync("FLEETMLD", "Fleetmld");
        await world.AddBotAsync("Fleetmaa", 30);
        await world.AddBotAsync("Fleetmab", 31);
        await leader.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString("Fleetmaa"));
        await world.Host.WaitForWorldAsync(() => world.Bots.IsPartyDriven(world.Ids["Fleetmaa"]), "Fleetmaa joins the leader's group");

        List<string> reply = await FleetWorld.SayAsync(gm, ".playerbot follow Fleetma*", 2);
        string[] asBot = await world.Console.RunAsync(".playerbot follow Fleetmab to Fleetmaa");

        Assert.Equal("Playerbot follow: matched=2 following=1 skipped=1 (in-player-group=1)", reply[0]);
        Assert.Contains("Fleetmaa: in-player-group", reply);
        Assert.Equal(["Playerbot follow: Fleetmaa is a bot; name a real player."], asBot);
        Assert.Equal("Fleetmld", (await world.Bots.InspectAsync("Fleetmaa"))!.Master);
    }

    [Fact]
    public async Task Near_ListsRunningBotsInRadius_WithLevelClassHealthStateGoalDistance_Capped()
    {
        await using FleetWorld world = await FleetWorld.StartAsync(o => o.MaxBots = 30);
        await using WorldTestClient gm = await world.AddGmAsync("FLEETNGM", "Fleetngm");
        await world.AddBotAsync("Fleetnaa", 10);
        await world.AddBotAsync("Fleetnab", 20);
        await world.AddBotAsync("Fleetnac", 100);

        List<string> few = await FleetWorld.SayAsync(gm, ".playerbot near 50", 3);

        Assert.Equal("Playerbots within 50 yards: 2.", few[0]);
        Assert.Matches(new Regex(@"^Fleetnaa L1 Warrior hp=100% alive goal=\w+ d=10\.0$"), few[1]);
        Assert.Matches(new Regex(@"^Fleetnab L1 Warrior hp=100% alive goal=\w+ d=20\.0$"), few[2]);
        List<string> wide = await FleetWorld.SayAsync(gm, ".playerbot near 500", 4);
        Assert.Equal("Playerbots within 500 yards: 3.", wide[0]);
        Assert.StartsWith("Fleetnac ", wide[3], StringComparison.Ordinal);

        // A dead bot is listed as dead (the line is read in the same turn as the kill, before the bot's own recovery moves it).
        string deadLine = await world.Host.OnWorldAsync(() =>
        {
            Player master = world.Host.World.FindOnlinePlayer("Fleetngm")!;
            Player victim = world.Host.World.FindOnlinePlayer("Fleetnab")!;
            victim.Map!.Combat.DealDamage(master, victim, victim.Health);
            return PlayerbotFleetCommands.NearLine(world.Bots.Snapshot().Single(s => s.Name == "Fleetnab"), victim, 20f);
        });
        Assert.Matches(new Regex(@"^Fleetnab L1 Warrior hp=0% dead goal=\w+ d=20\.0$"), deadLine);

        // Beyond the cap the rest is only counted.
        for (int i = 0; i < 22; i++) await world.AddBotAsync("Fleetnb" + (char)('a' + i) + "x", 5 + i);
        List<string> capped = await FleetWorld.SayAsync(gm, ".playerbot near 200", PlayerbotFleetCommands.MaxNearLines + 2);
        Assert.Equal("Playerbots within 200 yards: 25.", capped[0]);
        Assert.Equal("... and 5 more.", capped[^1]);
    }

    [Fact]
    public async Task StartStop_PrefixAndAll_ReportPerBotOutcomesAndCapacity()
    {
        await using FleetWorld world = await FleetWorld.StartAsync(o =>
        {
            o.MaxBots = 2;
            o.MaxBulkCommand = 2;
        });
        await world.CreateStoppedBotAsync("Fleetoaa");
        await world.CreateStoppedBotAsync("Fleetoab");
        await world.CreateStoppedBotAsync("Fleetoac");

        // More matches than the bulk cap: refused without force.
        string[] refused = await world.Console.RunAsync(".playerbot start all");
        Assert.Equal(["Playerbot start: 3 bots match; give a count <= 2 or add 'force'."], refused);
        Assert.DoesNotContain(world.Bots.Snapshot(), s => s.State == ManagedPlayerbotState.Running);

        Assert.Equal("Playerbot start accepted (3).", FleetConsoleRig.FirstLine(await world.Console.RunAsync(".playerbot start all force")));
        string[] started = await world.Console.WaitForLineAsync(l => l.StartsWith("Playerbot start: matched=3", StringComparison.Ordinal), "the start summary");
        Assert.Contains("Playerbot start: matched=3 started=2 failed=1 (playerbot-capacity=1)", started);
        Assert.Contains("Fleetoac: playerbot-capacity", started);
        // The status snapshot is republished by the world tick, so it may trail the operation results by a tick.
        await world.Host.WaitForWorldAsync(() => world.Bots.Snapshot().Count(s => s.State == ManagedPlayerbotState.Running) == 2,
            "two bots report Running");
        Assert.Equal(2, world.Bots.Snapshot().Count(s => s.State == ManagedPlayerbotState.Running));

        Assert.Equal(["Playerbot stop: 3 bots match; give a count <= 2 or add 'force'."], await world.Console.RunAsync(".playerbot stop Fleetoa*"));
        Assert.Equal("Playerbot stop accepted (3).", FleetConsoleRig.FirstLine(await world.Console.RunAsync(".playerbot stop all force")));
        await world.Console.WaitForLineAsync(l => l.StartsWith("Playerbot stop: matched=3 stopped=3 failed=0", StringComparison.Ordinal), "the stop summary");
        await world.Host.WaitForWorldAsync(() => !world.Bots.Snapshot().Any(s => s.State == ManagedPlayerbotState.Running),
            "no bot reports Running");
        Assert.DoesNotContain(world.Bots.Snapshot(), s => s.State == ManagedPlayerbotState.Running);
    }

    [Fact]
    public async Task Revive_RevivesOnlyDeadMatchingBots()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await using WorldTestClient gm = await world.AddGmAsync("FLEETPGM", "Fleetpgm");
        await world.AddBotAsync("Fleetpaa", 30);
        await world.AddBotAsync("Fleetpab", 31);
        await world.AddBotAsync("Fleetqaa", 32);
        await world.Host.OnWorldAsync(() =>
        {
            Player master = world.Host.World.FindOnlinePlayer("Fleetpgm")!;
            foreach (string name in new[] { "Fleetpaa", "Fleetqaa" })
            {
                Player victim = world.Host.World.FindOnlinePlayer(name)!;
                victim.Map!.Combat.DealDamage(master, victim, victim.Health);
                Assert.False(victim.IsAlive);
            }
        });

        List<string> reply = await FleetWorld.SayAsync(gm, ".playerbot revive Fleetpa*", 2);

        Assert.Equal("Playerbot revive: matched=2 revived=1 skipped=1 (alive=1)", reply[0]);
        Assert.Equal("Fleetpab: alive", reply[1]);
        Assert.True(await world.Host.PlayerStateAsync("Fleetpaa", p => p.IsAlive && p.Health > 0));
        Assert.False(await world.Host.PlayerStateAsync("Fleetqaa", p => p.IsAlive));
    }
}
