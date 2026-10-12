using System.Collections.Concurrent;
using System.Numerics;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// <c>.playerbot home</c> end to end: the command, <see cref="ManagedPlayerbotFeature"/>'s store and running brains, the status and
/// inspection lines. The tele values come from the test map data (<see cref="InMemoryMapDataStore"/>: Stormwind map 0 at
/// -8913.23, 554.633, 93.7944; Nowhere map 99); the reply texts are PlayerbotCommands' own. The test world has no terrain data, so
/// the zone clip reads a fixture (<see cref="ManagedPlayerbotFeature.ZoneLookups"/>): zone 12 everywhere unless a test says otherwise.
/// </summary>
public sealed class ManagedPlayerbotHomeTests
{
    private const string Accepted = "Playerbot home accepted.";
    private const uint FixtureZone = 12;

    [Fact]
    public async Task StartupAsync_DeletesAnOrphanRegistryRow_ForgetsItsHome_AndDoesNotRestoreIt()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var homes = new MemoryHomeStore();
        Account owner = await accounts.CreateAsync(new Account { Username = "PBORPHAN", Salt = new byte[32], Verifier = new byte[32], Security = AccountSecurity.Player });
        var bot = new ManagedPlayerbot(Guid.NewGuid(), owner.Id, 4242, owner.Username, true,
            ManagedPlayerbotState.Stopped, PlayerbotGoalKind.Explore, 0, 0, 0, 1, 1);
        await owners.CreateAsync(bot);
        await homes.SetAsync(new PlayerbotHome(bot.BotId, 4242, 0, -8913.23f, 554.633f, 93.7944f, 300f, "Stormwind", 1));

        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            var options = new PlayerbotOptions
            {
                Enabled = true, RestoreOnStartup = true, MaxBots = 4, ThinkIntervalMs = 100, MaxActionsPerTick = 2, MaxPathPoints = 32,
                MaxRouteYards = 100, AllowedMaps = [0, 1],
            };
            services.AddSingleton<IAccountStore>(accounts);
            services.AddSingleton<IAccountAdmin>(accounts);
            services.AddSingleton<ICharacterStore>(characters);
            services.AddSingleton<ICharacterLifeStore>(characters);
            services.AddSingleton<IManagedPlayerbotStore>(owners);
            services.AddSingleton<IManagedPlayerbotHomeStore>(homes);
            services.AddSingleton<IOptions<PlayerbotOptions>>(Options.Create(options));
            services.AddSingleton<IManagedPlayerbotProvisionStore>(new ManagedPlayerbotLifecycleTests.MemoryProvisionStore(accounts));
        });
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);

        Assert.Empty(await owners.LoadAllAsync());           // the orphan row is gone (it held a MaxRegisteredBots slot)
        Assert.Null(feature.HomeOf(bot.BotId));              // and so is its in-memory home
        Assert.DoesNotContain(feature.Snapshot(), b => b.BotId == bot.BotId);
        Assert.Null(await owners.FindAsync(bot.BotId));      // no restore attempt re-created or faulted it
        Assert.Equal("bot-not-found", (await feature.StartAsync(bot.BotId.ToString())).Code);
    }

    [Fact]
    public async Task SetHome_PersistsAndAppliesToTheRunningBrain_AndSurvivesARestart()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var homes = new MemoryHomeStore();
        Guid id;
        await using (WorldTestHost first = StartPersisted(accounts, characters, owners, homes))
        {
            ManagedPlayerbotFeature feature = first.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
            await feature.StartupAsync(default);
            id = Assert.IsType<Guid>((await feature.CreateAsync("Homeone", 1, 1)).BotId);
            Assert.True(feature.HomesAvailable);
            Assert.Null(Assert.Single(feature.Snapshot(), s => s.BotId == id).Home); // no home, no home line

            // A bot that is not running gets its home stored.
            ManagedPlayerbotFeature.HomeOutcome stored = await feature.SetHomesAsync(
                [new(id, 0, new Vector3(-8913.23f, 554.633f, 93.7944f))], 300f, "Stormwind");
            Assert.Equal(new ManagedPlayerbotFeature.HomeOutcome(1, 0), stored);
            PlayerbotHome row0 = feature.HomeOf(id)!;
            Assert.Equal(row0, feature.HomeOf(id));

            Assert.True((await feature.StartAsync(id.ToString())).Success);
            await first.WaitForWorldAsync(() => first.World.FindOnlinePlayer("Homeone") is { IsInWorld: true }, "bot in world");
            // The bot starts in Northshire, about 1,500 yards from Stormwind (map 0): outside, so it walks back.
            await first.WaitForWorldAsync(() => feature.Snapshot().Single(s => s.BotId == id).Home?.StartsWith("returning:", StringComparison.Ordinal) == true
                || feature.Snapshot().Single(s => s.BotId == id).Home?.StartsWith("blocked:", StringComparison.Ordinal) == true,
                "the running bot's home line");
            await feature.ShutdownBeforeWorldStopAsync();
        }

        PlayerbotHome row = Assert.Single(await homes.LoadAllAsync());
        Assert.Equal(id, row.BotId);
        Assert.Equal((0u, -8913.23f, 554.633f, 93.7944f, 300f, "Stormwind"), (row.MapId, row.X, row.Y, row.Z, row.Radius, row.Place));
        Assert.True(row.ZoneClipped); // the default
        Assert.Equal((await owners.FindAsync(id))!.CharacterId, row.CharacterId);

        // A new process: the home is loaded at startup and applied to the brain of the restarted bot.
        await using WorldTestHost second = StartPersisted(accounts, characters, owners, homes);
        ManagedPlayerbotFeature restarted = second.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await restarted.StartupAsync(default);
        Assert.Equal("stopped", Assert.Single(restarted.Snapshot(), s => s.BotId == id).Home);
        Assert.Equal(row, restarted.HomeOf(id));
        Assert.True((await restarted.StartAsync(id.ToString())).Success);
        await second.WaitForWorldAsync(() => restarted.Snapshot().Single(s => s.BotId == id) is { State: ManagedPlayerbotState.Running, Home: { } home }
            && (home.StartsWith("returning:", StringComparison.Ordinal) || home.StartsWith("blocked:", StringComparison.Ordinal)),
            "the restarted bot walks to its stored home");
        await restarted.ShutdownBeforeWorldStopAsync();
    }

    [Fact]
    public async Task SetHome_PrefixSelector_AnchorsEveryMatchingBot_AndReportsTheCount()
    {
        var homes = new MemoryHomeStore();
        await using WorldTestHost host = StartHost(homes);
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(host);
        await feature.StartupAsync(default);
        Guid a = await BotAsync(feature, host, "Hmprefixa"), b = await BotAsync(feature, host, "Hmprefixb"), other = await BotAsync(feature, host, "Otherbot");
        await using WorldTestClient admin = await host.EnterWorldAsync("HOMEADMIN1", "Homeadmin", AccountSecurity.Administrator);
        await admin.CollectAsync();

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot home Hmprefix* Stormwind 300");
        Assert.Equal(Accepted, (await admin.ReadChatAsync()).Text);
        Assert.Equal("Playerbot home: 2 set (Stormwind map 0 at -8913.2,554.6,93.8 radius 300, zone 12), 0 skipped (not running).",
            (await admin.ReadChatAsync()).Text);

        IReadOnlyList<PlayerbotHome> stored = await homes.LoadAllAsync();
        Assert.Equal(new[] { a, b }.Order(), stored.Select(h => h.BotId).Order());
        Assert.All(stored, h =>
        {
            Assert.Equal((0u, -8913.23f, 554.633f, 93.7944f, 300f, "Stormwind", true), (h.MapId, h.X, h.Y, h.Z, h.Radius, h.Place, h.ZoneClipped));
        });
        Assert.Null(feature.HomeOf(other));
        await host.WaitForWorldAsync(() => feature.Snapshot().Where(s => s.BotId != other).All(s => s.Home is not null)
            && feature.Snapshot().Single(s => s.BotId == other).Home is null, "home lines in the snapshot");

        // No radius given: the default (600).
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot home Otherbot Stormwind");
        Assert.Equal(Accepted, (await admin.ReadChatAsync()).Text);
        Assert.Equal("Playerbot home: 1 set (Stormwind map 0 at -8913.2,554.6,93.8 radius 600, zone 12), 0 skipped (not running).",
            (await admin.ReadChatAsync()).Text);
        Assert.Equal(PlayerbotHomeBounds.DefaultRadius, feature.HomeOf(other)!.Radius);
        await feature.ShutdownBeforeWorldStopAsync();
    }

    [Fact]
    public async Task SetHome_RefusesAShortPrefix_AnUnknownPlace_ADisallowedMap_AndARadiusOutOfBounds()
    {
        var homes = new MemoryHomeStore();
        await using WorldTestHost host = StartHost(homes);
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(host);
        await feature.StartupAsync(default);
        Guid bot = await BotAsync(feature, host, "Hmrefuse");
        await using WorldTestClient admin = await host.EnterWorldAsync("HOMEADMIN2", "Homeadmintwo", AccountSecurity.Administrator);
        await admin.CollectAsync();

        async Task<string> One(string command)
        {
            await admin.SendChatAsync(ChatType.Say, Language.Common, command);
            return (await admin.ReadChatAsync()).Text;
        }

        // One letter would anchor the whole fleet; so would a bare wildcard.
        const string selectorHelp = "Playerbot home: name a bot, give its id, or use a prefix of 2 to 11 letters with one trailing * (Wfbot*).";
        Assert.Equal(selectorHelp, await One(".playerbot home H* Stormwind"));
        Assert.Equal(selectorHelp, await One(".playerbot home * Stormwind"));
        Assert.Equal("Playerbot not found.", await One(".playerbot home Nosuchbot Stormwind"));
        Assert.Equal("Playerbot not found.", await One(".playerbot home Zz* Stormwind"));
        Assert.Equal(ArcaneCore.World.Gm.Core.GmStrings.TeleNotFound, await One(".playerbot home Hmrefuse Nosuchplace"));
        Assert.Equal("Playerbot home: map 99 is not a map playerbots may enter.", await One(".playerbot home Hmrefuse Nowhere"));
        const string radiusHelp = "Playerbot home: the radius must be between 50 and 5000 yards.";
        Assert.Equal(radiusHelp, await One(".playerbot home Hmrefuse Stormwind 49"));
        Assert.Equal(radiusHelp, await One(".playerbot home Hmrefuse Stormwind 5001"));
        Assert.Equal(radiusHelp, await One(".playerbot home Hmrefuse Stormwind 0"));
        Assert.Equal(radiusHelp, await One(".playerbot home Hmrefuse Stormwind -600"));
        Assert.Empty(await homes.LoadAllAsync()); // every refusal left nothing behind
        Assert.Null(feature.HomeOf(bot));

        // The bounds themselves are allowed.
        foreach (string radius in new[] { "50", "5000" })
        {
            await admin.SendChatAsync(ChatType.Say, Language.Common, $".playerbot home Hmrefuse Stormwind {radius}");
            Assert.Equal(Accepted, (await admin.ReadChatAsync()).Text);
            Assert.StartsWith("Playerbot home: 1 set (", (await admin.ReadChatAsync()).Text);
            Assert.Equal(float.Parse(radius), feature.HomeOf(bot)!.Radius);
        }

        // The feature itself refuses a radius out of bounds.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => feature.SetHomesAsync([new(bot, 0, Vector3.Zero)], float.NaN, null));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => feature.SetHomesAsync([new(bot, 0, Vector3.Zero)], 49.99f, null));
        await feature.ShutdownBeforeWorldStopAsync();
    }

    [Fact]
    public async Task SetHome_AStoreFailure_IsNotApplied_AndCountedAsFailed()
    {
        var homes = new MemoryHomeStore { FailSet = true };
        await using WorldTestHost host = StartHost(homes);
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(host);
        await feature.StartupAsync(default);
        Guid bot = await BotAsync(feature, host, "Hmfailing");

        ManagedPlayerbotFeature.HomeOutcome outcome = await feature.SetHomesAsync([new(bot, 0, Vector3.Zero)], 300f, "x");
        Assert.Equal(new ManagedPlayerbotFeature.HomeOutcome(0, 1), outcome);
        Assert.Null(feature.HomeOf(bot));
        Assert.Null(Assert.Single(feature.Snapshot(), s => s.BotId == bot).Home);
        Assert.Empty(await homes.LoadAllAsync());
        await feature.ShutdownBeforeWorldStopAsync();
    }

    [Fact]
    public async Task SetHome_Here_UsesEachBotsOwnPosition_AndSkipsStoppedBots()
    {
        var homes = new MemoryHomeStore();
        await using WorldTestHost host = StartHost(homes);
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(host);
        await feature.StartupAsync(default);
        Guid west = await BotAsync(feature, host, "Hmhereone"), east = await BotAsync(feature, host, "Hmheretwo");
        Guid stopped = await BotAsync(feature, host, "Hmherethree");
        Assert.True((await feature.StopAsync(stopped.ToString())).Success);
        await host.World.InvokeAsync(() =>
        {
            Game.Entities.Player moved = host.World.FindOnlinePlayer("Hmheretwo")!;
            moved.Relocate(moved.X + 100f, moved.Y, moved.Z, 0, host.World.NowMs);
            return true;
        });
        await using WorldTestClient admin = await host.EnterWorldAsync("HOMEADMIN3", "Hmadminthree", AccountSecurity.Administrator);
        await admin.CollectAsync();

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot home Hmhere* here 200");
        Assert.Equal(Accepted, (await admin.ReadChatAsync()).Text);
        Assert.Equal("Playerbot home: 2 set (here, radius 200, zone clipped), 1 skipped (not running).", (await admin.ReadChatAsync()).Text);

        PlayerbotHome homeWest = feature.HomeOf(west)!, homeEast = feature.HomeOf(east)!;
        Assert.Null(feature.HomeOf(stopped));
        Assert.Null(homeWest.Place);
        Assert.Equal(200f, homeWest.Radius);
        Assert.True(homeEast.X - homeWest.X is > 60f and < 140f, $"the two centres differ by {homeEast.X - homeWest.X} yards, not by the 100 the bots are apart");
        (float x, float y) = await host.World.InvokeAsync(() =>
        {
            Game.Entities.Player p = host.World.FindOnlinePlayer("Hmheretwo")!;
            return (p.X, p.Y);
        });
        Assert.InRange(MathF.Abs(homeEast.X - x), 0f, 20f);
        Assert.InRange(MathF.Abs(homeEast.Y - y), 0f, 20f);

        // Nobody running matches: nothing is stored and nothing is claimed.
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot home Hmherethree here");
        Assert.Equal("Playerbot home: 0 set, 1 skipped (not running).", (await admin.ReadChatAsync()).Text);
        Assert.Equal(2, (await homes.LoadAllAsync()).Count);
        await feature.ShutdownBeforeWorldStopAsync();
    }

    [Fact]
    public async Task ClearHome_RemovesTheRowAndTheBrainsHome()
    {
        var homes = new MemoryHomeStore();
        await using WorldTestHost host = StartHost(homes);
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(host);
        await feature.StartupAsync(default);
        Guid with = await BotAsync(feature, host, "Hmclearone"), without = await BotAsync(feature, host, "Hmcleartwo");
        await using WorldTestClient admin = await host.EnterWorldAsync("HOMEADMIN4", "Hmadminfour", AccountSecurity.Administrator);
        await admin.CollectAsync();
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot home Hmclearone Stormwind");
        Assert.Equal(Accepted, (await admin.ReadChatAsync()).Text);
        await admin.ReadChatAsync();
        await host.WaitForWorldAsync(() => feature.Snapshot().Single(s => s.BotId == with).Home is not null, "home line");

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot home Hmcleartwo clear"); // nothing to clear
        Assert.Equal(Accepted, (await admin.ReadChatAsync()).Text);
        Assert.Equal("Playerbot home: 0 cleared.", (await admin.ReadChatAsync()).Text);
        Assert.Single(await homes.LoadAllAsync());

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot home Hmclear* clear");
        Assert.Equal(Accepted, (await admin.ReadChatAsync()).Text);
        Assert.Equal("Playerbot home: 1 cleared.", (await admin.ReadChatAsync()).Text);
        Assert.Empty(await homes.LoadAllAsync());
        Assert.Null(feature.HomeOf(with));
        await host.WaitForWorldAsync(() => feature.Snapshot().Where(s => s.BotId == with || s.BotId == without).All(s => s.Home is null),
            "the home line is gone");
        await feature.ShutdownBeforeWorldStopAsync();
    }

    [Fact]
    public async Task ClearHomes_WhenTheStoreFailsPartway_StillClearsTheBrainsOfTheBotsAlreadyCleared()
    {
        var homes = new MemoryHomeStore();
        await using WorldTestHost host = StartHost(homes);
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(host);
        await feature.StartupAsync(default);
        Guid first = await BotAsync(feature, host, "Hmfailone"), second = await BotAsync(feature, host, "Hmfailtwo"), third = await BotAsync(feature, host, "Hmfailthree");
        Assert.Equal(3, (await feature.SetHomesAsync(
            [new(first, 0, new Vector3(-8913.23f, 554.633f, 93.7944f)), new(second, 0, new Vector3(-8913.23f, 554.633f, 93.7944f)), new(third, 0, new Vector3(-8913.23f, 554.633f, 93.7944f))],
            300f, "Stormwind")).Set);
        await host.WaitForWorldAsync(() => feature.Snapshot().Where(s => s.BotId == first || s.BotId == second || s.BotId == third).All(s => s.Home is not null), "home lines");

        homes.FailClearOnCall = 2; // the first clear succeeds, the second throws, the third is never reached
        await Assert.ThrowsAsync<InvalidOperationException>(() => feature.ClearHomesAsync([first, second, third]));

        await host.WaitForWorldAsync(() => feature.Snapshot().Single(s => s.BotId == first).Home is null, "the cleared bot's brain lost its home");
        Assert.Null(feature.HomeOf(first));
        Assert.NotNull(feature.HomeOf(second));
        Assert.NotNull(feature.Snapshot().Single(s => s.BotId == second).Home);
        Assert.NotNull(feature.Snapshot().Single(s => s.BotId == third).Home);
        Assert.Equal(new[] { second, third }.Order(), (await homes.LoadAllAsync()).Select(h => h.BotId).Order());
        await feature.ShutdownBeforeWorldStopAsync();
    }

    [Fact]
    public async Task ReplacedBrain_KeepsTheHome()
    {
        // A party AI drives a grouped bot and its brain is replaced when the group ends (ManagedPlayerbotFeature.ReplaceBrain): the
        // new brain must come back with the stored home. While the party AI drives, the bot ignores its home ("suspended").
        var homes = new MemoryHomeStore();
        await using WorldTestHost host = PartyTestHost.Start(o =>
        {
            o.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone;
            o.Party.MasterTimeoutSeconds = 1;
        }, services => services.AddSingleton<IManagedPlayerbotHomeStore>(homes));
        Guid id = await PartyTestHost.StartBotAsync(host, "Hmreplaced");
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(host);
        Assert.Equal(1, (await feature.SetHomesAsync([new(id, 0, new Vector3(-8913.23f, 554.633f, 93.7944f))], 300f, "Stormwind")).Set);
        await host.WaitForWorldAsync(() => feature.Snapshot().Single(s => s.BotId == id).Home is { } h && h != "stopped" && h != "suspended",
            "the brain's home line");

        WorldTestClient master = await host.EnterWorldAsync("HOMEMASTER", "Homemaster");
        await master.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString("Hmreplaced"));
        await host.WaitForWorldAsync(() => feature.IsPartyDriven(id), "the party AI drives the grouped bot");
        await host.WaitForWorldAsync(() => feature.Snapshot().Single(s => s.BotId == id).Home == "suspended", "the home is suspended");

        await master.DisposeAsync();
        await host.WaitForWorldAsync(() => !feature.IsPartyDriven(id), "the brain drives the bot again");
        await host.WaitForWorldAsync(() => feature.Snapshot().Single(s => s.BotId == id).Home is { } h && h != "stopped" && h != "suspended",
            "the new brain reports the home");
        await feature.ShutdownBeforeWorldStopAsync();
    }

    [Fact]
    public async Task HomeCommand_IsAdministrator_AndStatusAndInspectShowTheHome()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.NotNull(table.Resolve("playerbot home", AccountSecurity.Administrator));
        Assert.Null(table.Resolve("playerbot home", AccountSecurity.GameMaster));
        Assert.Null(table.Resolve("playerbot home", AccountSecurity.Player));

        var homes = new MemoryHomeStore();
        await using WorldTestHost host = StartHost(homes);
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(host);
        await feature.StartupAsync(default);
        Guid withHome = await BotAsync(feature, host, "Hmshowone");
        await BotAsync(feature, host, "Hmshowtwo");
        await using WorldTestClient admin = await host.EnterWorldAsync("HOMEADMIN5", "Hmadminfive", AccountSecurity.Administrator);
        await admin.CollectAsync();
        Assert.Equal(1, (await feature.SetHomesAsync([new(withHome, 0, new Vector3(-8949.95f, -132.49f, 83.53f))], 250f, "Northshire")).Set);
        await host.WaitForWorldAsync(() => feature.Snapshot().Single(s => s.BotId == withHome).Home?.StartsWith("inside", StringComparison.Ordinal) == true,
            "the bot is inside its home");

        // status: the home is a trailing home=<line>; a bot without a home has none.
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot status Hmshowone");
        string status = (await admin.ReadChatAsync()).Text;
        Assert.Contains(" home=inside", status);
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot status Hmshowtwo");
        Assert.DoesNotContain(" home=", (await admin.ReadChatAsync()).Text);

        // the full status ends with the terminator line the ops runner waits for (a missing bot cannot hide it).
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot status");
        string last;
        do last = (await admin.ReadChatAsync()).Text;
        while (!last.StartsWith("Playerbot status end: ", StringComparison.Ordinal));
        Assert.Equal("Playerbot status end: 2", last);

        // inspect: the long form, place, map, centre, radius, distance and state.
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot inspect Hmshowone");
        string line;
        do line = (await admin.ReadChatAsync()).Text;
        while (!line.StartsWith("BOTINSPECT home=", StringComparison.Ordinal));
        Assert.StartsWith("BOTINSPECT home=place=Northshire map=0 centre=-8950.0,-132.5,83.5 radius=250 zone=12 distance=", line);
        Assert.EndsWith(" state=inside mode=auto teleports=0", line); // the return mode (World:Playerbots:Home:ReturnMode) and the return teleports so far
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot inspect Hmshowtwo");
        do line = (await admin.ReadChatAsync()).Text;
        while (!line.StartsWith("BOTINSPECT home=", StringComparison.Ordinal));
        Assert.Equal("BOTINSPECT home=none", line);
        await feature.ShutdownBeforeWorldStopAsync();
    }

    /// <summary>
    /// The zone clip option: <c>nozone</c> (before or after the radius) stores an unclipped home and the brain shows <c>zone=off</c>;
    /// <c>zone</c> (the default) stores a clipped one; a clipped home whose centre has no zone the terrain knows is refused before
    /// anything is stored (it would contain nothing), for a tele place and for <c>here</c>.
    /// </summary>
    [Fact]
    public async Task SetHome_ZoneOption_StoresTheClip_AndRefusesACentreWithNoKnownZone()
    {
        var homes = new MemoryHomeStore();
        uint zoneEverywhere = FixtureZone;
        await using WorldTestHost host = StartHost(homes, zones: (x, y, z) => zoneEverywhere);
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(host);
        await feature.StartupAsync(default);
        Guid bot = await BotAsync(feature, host, "Hmzoneone");
        await using WorldTestClient admin = await host.EnterWorldAsync("HOMEADMIN6", "Hmadminsix", AccountSecurity.Administrator);
        await admin.CollectAsync();

        async Task<string> Set(string command)
        {
            await admin.SendChatAsync(ChatType.Say, Language.Common, command);
            Assert.Equal(Accepted, (await admin.ReadChatAsync()).Text);
            return (await admin.ReadChatAsync()).Text;
        }

        Assert.Equal("Playerbot home: 1 set (Stormwind map 0 at -8913.2,554.6,93.8 radius 300, no zone clip), 0 skipped (not running).",
            await Set(".playerbot home Hmzoneone Stormwind 300 nozone"));
        Assert.False(feature.HomeOf(bot)!.ZoneClipped);
        Assert.False(Assert.Single(await homes.LoadAllAsync()).ZoneClipped);
        // The reply comes after the running brain got the home (SetHomesAsync applies it on the world thread first).
        Assert.Contains(" zone=off ", (await feature.InspectAsync(bot.ToString()))!.Home, StringComparison.Ordinal);

        Assert.Equal("Playerbot home: 1 set (Stormwind map 0 at -8913.2,554.6,93.8 radius 400, zone 12), 0 skipped (not running).",
            await Set(".playerbot home Hmzoneone Stormwind zone 400"));
        Assert.True(feature.HomeOf(bot)!.ZoneClipped);
        Assert.Equal(400f, feature.HomeOf(bot)!.Radius);
        Assert.Contains(" zone=12 ", (await feature.InspectAsync(bot.ToString()))!.Home, StringComparison.Ordinal);

        Assert.Equal("Playerbot home: 1 set (Stormwind map 0 at -8913.2,554.6,93.8 radius 500, no zone clip), 0 skipped (not running).",
            await Set(".playerbot home Hmzoneone Stormwind nozone 500"));
        Assert.False(feature.HomeOf(bot)!.ZoneClipped);

        // The terrain knows no zone: a clipped home is refused and the stored one is left as it was.
        zoneEverywhere = 0;
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot home Hmzoneone Stormwind 300");
        Assert.Equal("Playerbot home: the terrain knows no zone at Stormwind; a zone-clipped home there would contain nothing (add nozone).",
            (await admin.ReadChatAsync()).Text);
        // Nothing left to store: refused at once, without the accepted line.
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot home Hmzoneone here 300");
        Assert.Equal("Playerbot home: 0 set, 0 skipped (not running), 1 refused (no zone known at the bot's position; add nozone).",
            (await admin.ReadChatAsync()).Text);
        PlayerbotHome kept = Assert.Single(await homes.LoadAllAsync());
        Assert.Equal((500f, false), (kept.Radius, kept.ZoneClipped));
        // Without the clip it is accepted.
        Assert.Equal("Playerbot home: 1 set (here, radius 300, no zone clip), 0 skipped (not running).",
            await Set(".playerbot home Hmzoneone here 300 nozone"));
        await feature.ShutdownBeforeWorldStopAsync();
    }

    private static async Task<Guid> BotAsync(ManagedPlayerbotFeature feature, WorldTestHost host, string name)
    {
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync(name, 1, 1)).BotId);
        Assert.True((await feature.StartAsync(id.ToString())).Success);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name) is { IsInWorld: true }, name + " logs in");
        await host.WaitForWorldAsync(() => feature.Snapshot().Any(s => s.BotId == id && s.State == ManagedPlayerbotState.Running), name + " runs");
        return id;
    }

    private static WorldTestHost StartHost(MemoryHomeStore homes, Action<PlayerbotOptions>? configure = null,
        Func<float, float, float, uint>? zones = null)
    {
        WorldTestHost host = PartyTestHost.Start(configure, services =>
        {
            services.AddSingleton<IManagedPlayerbotHomeStore>(homes);
            // The daemon registers the command's service (AddWorldDaemon); the test host registers only the feature.
            services.AddSingleton<IPlayerbotService>(sp => sp.GetRequiredService<ManagedPlayerbotFeature>());
        });
        PlayerbotZoneLookup lookup = new(zones ?? ((x, y, z) => FixtureZone));
        PartyTestHost.Feature(host).ZoneLookups = _ => lookup;
        return host;
    }

    private static WorldTestHost StartPersisted(InMemoryAccountStore accounts, InMemoryCharacterStore characters,
        ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore owners, MemoryHomeStore homes)
    {
        WorldTestHost host = StartPersistedHost(accounts, characters, owners, homes);
        PlayerbotZoneLookup lookup = new((x, y, z) => FixtureZone);
        host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>().ZoneLookups = _ => lookup;
        return host;
    }

    private static WorldTestHost StartPersistedHost(InMemoryAccountStore accounts, InMemoryCharacterStore characters,
        ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore owners, MemoryHomeStore homes)
        => WorldTestHost.Start(configureServices: services =>
        {
            var options = new PlayerbotOptions
            {
                Enabled = true, MaxBots = 4, ThinkIntervalMs = 100, MaxActionsPerTick = 2, MaxPathPoints = 32, MaxRouteYards = 100,
                AllowedMaps = [0, 1],
            };
            services.AddSingleton<IAccountStore>(accounts);
            services.AddSingleton<IAccountAdmin>(accounts);
            services.AddSingleton<ICharacterStore>(characters);
            services.AddSingleton<ICharacterLifeStore>(characters);
            services.AddSingleton<IManagedPlayerbotStore>(owners);
            services.AddSingleton<IManagedPlayerbotHomeStore>(homes);
            services.AddSingleton<IOptions<PlayerbotOptions>>(Options.Create(options));
            services.AddSingleton<IManagedPlayerbotProvisionStore>(new ManagedPlayerbotLifecycleTests.MemoryProvisionStore(accounts));
        });

    private sealed class MemoryHomeStore : IManagedPlayerbotHomeStore
    {
        private readonly ConcurrentDictionary<Guid, PlayerbotHome> _rows = new();

        private int _clearCalls;

        public bool FailSet { get; init; }

        /// <summary>1-based: the Nth <see cref="ClearAsync"/> call throws (0: never).</summary>
        public int FailClearOnCall { get; set; }

        public Task<IReadOnlyList<PlayerbotHome>> LoadAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PlayerbotHome>>([.. _rows.Values.OrderBy(h => h.BotId)]);

        public Task SetAsync(PlayerbotHome home, CancellationToken cancellationToken = default)
        {
            if (FailSet) throw new InvalidOperationException("the characters database is unavailable");
            _rows[home.BotId] = home;
            return Task.CompletedTask;
        }

        public Task<bool> ClearAsync(Guid botId, CancellationToken cancellationToken = default)
        {
            if (FailClearOnCall != 0 && Interlocked.Increment(ref _clearCalls) == FailClearOnCall)
                throw new InvalidOperationException("the characters database is unavailable");
            return Task.FromResult(_rows.TryRemove(botId, out _));
        }
    }
}
