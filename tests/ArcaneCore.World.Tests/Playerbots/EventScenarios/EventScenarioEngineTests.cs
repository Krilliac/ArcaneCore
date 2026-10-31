using ArcaneCore.World.Bots;
using System.Numerics;
using ArcaneCore.Data.World.WorldState;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.EventScenarios;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.EventScenarios;

/// <summary>
/// The scenario engine against a fake server: a game event that starts claims, creates, brings online, boosts, places and gives the bots
/// their role; one that stops takes the posts and the siege down. Time moves only when a test moves it.
/// </summary>
public sealed class EventScenarioEngineTests
{
    private const ushort Event = 900;
    private static readonly Vector3 Start = new(1600f, 277f, 55.44f);

    private sealed class Rig
    {
        public FakeScenarioHost Host { get; } = new();

        public EventScenarioEngine Engine { get; }

        public Rig(params EventScenarioGroup[] groups)
        {
            Engine = new EventScenarioEngine(Host, NullLogger.Instance);
            Engine.SetCatalog(EventScenarioCatalogFixtures.Catalog(groups));
        }

        /// <summary>Advance the world clock, then one engine pass.</summary>
        public void Tick(uint ms = 2_000)
        {
            Host.Now += ms;
            Engine.Tick();
            Host.Wander();
        }

        public void Ticks(int count, uint ms = 2_000)
        {
            for (int i = 0; i < count; i++) Tick(ms);
        }

        public string Status() => string.Join('\n', Engine.StatusLines(Event, _ => "Test scenario"));
    }

    private static EventScenarioPost Post(uint id, float x) => new(id, 0, new Vector3(x, 277f, 55.44f), 40f);

    private static EventScenarioGroup Defenders(string prefix = "Ucltop", int count = 2, uint group = 2, bool provision = false, int level = 60, int posts = 1)
        => new(Event, group, prefix, count, 1, [new EventScenarioClassSlot(2, 1), new EventScenarioClassSlot(5, 5)], level, provision, 0, Start, 0f, EventScenarioRole.Defend,
            [.. Enumerable.Range(1, posts).Select(i => Post((uint)i, 1600f + (i * 10)))], null, null);

    private static EventScenarioGroup Raiders(string prefix = "Siegebot", int count = 8, int minReady = 100, int level = 60, bool provision = false, uint group = 1)
        => new(Event, group, prefix, count, 0, [new EventScenarioClassSlot(1, 1), new EventScenarioClassSlot(1, 5)], level, provision, 0, Start, 2.94f, EventScenarioRole.Siege, [],
            new EventScenarioSiege(10181, 2, minReady, 0, [new Vector3(1925f, 225f, 49.41f), new Vector3(1850f, 240f, 62.75f)]), null);

    private static FakeBot FarBot(FakeScenarioHost host, string name, int level = 60, byte team = 1) => host.Add(name, team).OnlineAt(1, new Vector3(-3000f, 100f, 40f), level);

    // --- a defend group ----------------------------------------------------------------------------------------------------

    [Fact]
    public void EventStart_ClaimsTheBotsByPrefix_PlacesThemOnce_AndSetsRoundRobinPosts()
    {
        var rig = new Rig(Defenders(count: 2, posts: 2));
        FakeBot a = FarBot(rig.Host, "Ucltopaa");
        FakeBot b = FarBot(rig.Host, "Ucltopbb");
        FakeBot other = FarBot(rig.Host, "Randomone");

        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(3);

        Assert.Equal((Event, 2u), rig.Engine.ClaimOf(a.Id));
        Assert.Equal((Event, 2u), rig.Engine.ClaimOf(b.Id));
        Assert.Null(rig.Engine.ClaimOf(other.Id));
        Assert.Equal(["Ucltopaa", "Ucltopbb"], rig.Host.Teleports.Select(t => t.Bot));
        Assert.All(rig.Host.Teleports, t => Assert.Equal(0u, t.Map));
        PlayerbotDefendPost first = rig.Host.Posts[a.Id]!;
        PlayerbotDefendPost second = rig.Host.Posts[b.Id]!;
        Assert.NotEqual(first.PostId, second.PostId);
        Assert.Equal(EventScenarioPostIds.For(Event, 2, 1), first.PostId);
        Assert.Equal(EventScenarioPostIds.For(Event, 2, 2), second.PostId);
        Assert.True(first.PostId >= EventScenarioPostIds.Base);
        Assert.Equal(40f, first.Radius);

        // Placed and posted: more passes move nobody and set nothing again.
        int teleports = rig.Host.Teleports.Count;
        rig.Ticks(5);
        Assert.Equal(teleports, rig.Host.Teleports.Count);
        Assert.Contains("claimed 2/2, online 2, at level 2, placed 2, posts set 2", rig.Status());
    }

    [Fact]
    public void ABotAlreadyAtTheStart_IsNotTeleported()
    {
        var rig = new Rig(Defenders(count: 1));
        FakeBot bot = rig.Host.Add("Ucltopaa").OnlineAt(0, Start + new Vector3(10f, 0f, 0f), 60);

        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(2);

        Assert.Empty(rig.Host.Teleports);
        Assert.NotNull(rig.Host.Posts[bot.Id]);
    }

    [Fact]
    public void EventStop_ClearsThePosts_ForgetsTheClaims_AndLeavesTheBotsWhereTheyAre()
    {
        var rig = new Rig(Defenders(count: 2));
        FakeBot a = FarBot(rig.Host, "Ucltopaa");
        FakeBot b = FarBot(rig.Host, "Ucltopbb");
        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(3);
        Vector3 placedAt = a.Position;
        Assert.NotNull(rig.Host.Posts[a.Id]);

        rig.Engine.EventChanged(Event, active: false);

        Assert.Null(rig.Host.Posts[a.Id]);
        Assert.Null(rig.Host.Posts[b.Id]);
        Assert.Null(rig.Engine.ClaimOf(a.Id));
        Assert.Null(rig.Engine.RunOf(Event, 2));
        Assert.Equal(placedAt, a.Position);
        Assert.Equal(2, rig.Host.Bots.Count);
        int calls = rig.Host.Teleports.Count;
        rig.Ticks(3);
        Assert.Equal(calls, rig.Host.Teleports.Count);
    }

    [Fact]
    public void ARestartedEvent_ClaimsAgainAndDoesNotMoveBotsThatStandAtTheirStart()
    {
        var rig = new Rig(Defenders(count: 1));
        FakeBot bot = FarBot(rig.Host, "Ucltopaa");
        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(3);
        rig.Engine.EventChanged(Event, active: false);
        int teleports = rig.Host.Teleports.Count;

        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(3);

        Assert.Equal(teleports, rig.Host.Teleports.Count);
        Assert.NotNull(rig.Host.Posts[bot.Id]);
        Assert.Equal((Event, 2u), rig.Engine.ClaimOf(bot.Id));
    }

    // --- provisioning, levels ----------------------------------------------------------------------------------------------

    [Fact]
    public void AGroupThatProvisions_CreatesTheMissingBots_StartsBoostsPlacesAndPostsThem()
    {
        var rig = new Rig(Defenders(count: 5, provision: true));
        FarBot(rig.Host, "Ucltopaa", level: 60);
        FarBot(rig.Host, "Ucltopbb", level: 60);

        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(12);

        Assert.Equal(3, rig.Host.Created.Count);
        Assert.All(rig.Host.Created, name => Assert.StartsWith("Ucltop", name));
        Assert.Equal(3, rig.Host.Created.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(rig.Host.Created, name => Assert.True(name.Length <= 12 && name.All(char.IsAsciiLetter), name));
        Assert.Equal(3, rig.Host.Starts.Count);
        Assert.Equal(3, rig.Host.Boosts.Count);
        Assert.All(rig.Host.Boosts, b => Assert.Equal(60, b.Level));
        Assert.Equal(5, rig.Host.Posts.Count(p => p.Value is not null));
        Assert.Contains("claimed 5/5, online 5, at level 5, placed 5, posts set 5", rig.Status());
        Assert.DoesNotContain("short", rig.Status());
    }

    [Fact]
    public void AtMostTwoBotsAreCreatedPerGroupPerPass_AndOnlyWhatIsMissing()
    {
        var rig = new Rig(Defenders(count: 6, provision: true));
        rig.Host.HoldCreates = true;
        rig.Engine.EventChanged(Event, active: true);

        rig.Tick();
        Assert.Equal(2, rig.Host.Created.Count);
        rig.Tick();
        Assert.Equal(4, rig.Host.Created.Count); // four in flight is the global ceiling
        rig.Tick();
        Assert.Equal(4, rig.Host.Created.Count);

        rig.Host.CompleteCreates();
        rig.Ticks(3);
        Assert.Equal(6, rig.Host.Created.Count);
    }

    [Fact]
    public void WithNoRoom_NoBotIsCreated_AndTheStatusSaysShortAndNoRoom()
    {
        var rig = new Rig(Defenders(count: 4, provision: true));
        FarBot(rig.Host, "Ucltopaa");
        rig.Host.Room = 0;
        rig.Engine.EventChanged(Event, active: true);

        rig.Ticks(3);

        Assert.Empty(rig.Host.Created);
        Assert.Contains("claimed 1/4", rig.Status());
        Assert.Contains("short 3, no room", rig.Status());
    }

    [Fact]
    public void AGroupThatDoesNotProvision_UsesOnlyTheBotsThatExist()
    {
        var rig = new Rig(Defenders(count: 3, provision: false));
        FarBot(rig.Host, "Ucltopaa");
        rig.Engine.EventChanged(Event, active: true);

        rig.Ticks(3);

        Assert.Empty(rig.Host.Created);
        Assert.Contains("short 2, not provisioned", rig.Status());
    }

    [Fact]
    public void ARefusedCreation_IsShown_AndANameInUseIsRetriedAtOnceWithAnotherName()
    {
        var rig = new Rig(Defenders(count: 1, provision: true));
        int refusals = 0;
        rig.Host.CreateAnswer = name => refusals++ < 1 ? new PlayerbotOperationResult(false, "name-in-use") : null;
        rig.Engine.EventChanged(Event, active: true);

        rig.Tick();
        Assert.Single(rig.Host.Created);
        rig.Tick();
        Assert.Equal(2, rig.Host.Created.Count);
        Assert.NotEqual(rig.Host.Created[0], rig.Host.Created[1]);

        var other = new Rig(Defenders(count: 1, provision: true));
        other.Host.CreateAnswer = _ => new PlayerbotOperationResult(false, "playerbot-registry-full");
        other.Engine.EventChanged(Event, active: true);
        other.Tick();
        other.Tick();
        Assert.Single(other.Host.Created); // a real refusal waits before the next try
        Assert.Contains("create refused: playerbot-registry-full", other.Status());
        other.Ticks(16);
        Assert.Equal(2, other.Host.Created.Count);
    }

    [Fact]
    public void ABotBelowTheLevel_IsBoostedWhenItCanBe_AndNotPlacedBefore()
    {
        var rig = new Rig(Defenders(count: 1));
        FakeBot bot = FarBot(rig.Host, "Ucltopaa", level: 10);
        bot.InCombat = true;
        rig.Engine.EventChanged(Event, active: true);

        rig.Ticks(3);
        Assert.Empty(rig.Host.Boosts);
        Assert.Empty(rig.Host.Teleports);
        Assert.Contains("boost: bot-in-combat", rig.Status());

        bot.InCombat = false;
        rig.Host.BoostRefusal = "bot-settling-quest";
        rig.Tick();
        Assert.Contains("boost: bot-settling-quest", rig.Status());

        rig.Host.BoostRefusal = null;
        rig.Ticks(3);
        Assert.Equal([("Ucltopaa", 60)], rig.Host.Boosts);
        Assert.Single(rig.Host.Teleports);
        Assert.NotNull(rig.Host.Posts[bot.Id]);
    }

    [Fact]
    public void ARefusedTeleport_IsRetriedAfterAWhile_AndShown()
    {
        var rig = new Rig(Defenders(count: 1));
        FakeBot bot = FarBot(rig.Host, "Ucltopaa");
        rig.Host.TeleportRefusal = "teleport-refused";
        rig.Engine.EventChanged(Event, active: true);

        rig.Tick();
        Assert.Contains("place: teleport-refused", rig.Status());
        Assert.Null(rig.Host.Posts.GetValueOrDefault(bot.Id));
        rig.Host.TeleportRefusal = null;
        rig.Ticks(10); // 20 s: past the 15 s retry
        Assert.NotNull(rig.Host.Posts[bot.Id]);
    }

    // --- bringing bots online, resume --------------------------------------------------------------------------------------

    [Fact]
    public void AnEventRunningBeforeTheBotsAreOnline_WaitsForTheRestore_ThenReconcilesOnceTheyAre()
    {
        var rig = new Rig(Defenders(count: 2));
        FakeBot a = rig.Host.Add("Ucltopaa");
        FakeBot b = rig.Host.Add("Ucltopbb");
        a.Desired = b.Desired = true; // registered, wanted running, the startup restore has not logged them in yet

        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(5); // 10 s

        Assert.Equal((Event, 2u), rig.Engine.ClaimOf(a.Id));
        Assert.Empty(rig.Host.Starts);
        Assert.Empty(rig.Host.Teleports);
        Assert.Contains("claimed 2/2, online 0", rig.Status());

        a.OnlineAt(1, new Vector3(-3000f, 0f, 0f), 60);
        b.OnlineAt(1, new Vector3(-3000f, 0f, 0f), 60);
        rig.Ticks(3);

        Assert.Empty(rig.Host.Starts);
        Assert.NotNull(rig.Host.Posts[a.Id]);
        Assert.NotNull(rig.Host.Posts[b.Id]);
    }

    [Fact]
    public void ABotTheRestoreNeverBringsUp_IsStartedAfterAMinute_AndAStoppedBotAtOnce()
    {
        var rig = new Rig(Defenders(count: 2));
        FakeBot restored = rig.Host.Add("Ucltopaa");
        restored.Desired = true;
        rig.Host.Add("Ucltopbb"); // stopped, not wanted running

        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(2);
        Assert.Equal(["Ucltopbb"], rig.Host.Starts);

        rig.Ticks(32); // past the 60 s the restore is given
        Assert.Contains("Ucltopaa", rig.Host.Starts);
    }

    [Fact]
    public void ACatalogThatArrivesAfterTheEventStarted_StillBuildsTheRun()
    {
        var rig = new Rig();
        FarBot(rig.Host, "Ucltopaa");
        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(2);
        Assert.Empty(rig.Host.Teleports);

        rig.Engine.SetCatalog(EventScenarioCatalogFixtures.Catalog(Defenders(count: 1)));
        rig.Ticks(3);

        Assert.Single(rig.Host.Posts, p => p.Value is not null);
    }

    [Fact]
    public void ADeletedBot_IsNoLongerClaimed_AndAProvisioningGroupReplacesIt()
    {
        var rig = new Rig(Defenders(count: 2, provision: true));
        FarBot(rig.Host, "Ucltopaa");
        FakeBot gone = FarBot(rig.Host, "Ucltopbb");
        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(3);
        Assert.Empty(rig.Host.Created);

        gone.Online = false;
        rig.Host.Bots.Remove(gone);
        rig.Ticks(6);

        Assert.Null(rig.Engine.ClaimOf(gone.Id));
        Assert.Single(rig.Host.Created);
    }

    // --- bots that are not the scenario's to take --------------------------------------------------------------------------

    [Fact]
    public void ABotInAGroupOrAScriptOrOnTheOtherTeam_IsNotTaken_AndCountsAsUnavailable()
    {
        var rig = new Rig(Defenders(count: 3));
        FakeBot grouped = FarBot(rig.Host, "Ucltopaa");
        grouped.HeldBy = "in a group";
        FakeBot scripted = FarBot(rig.Host, "Ucltopbb");
        scripted.HeldBy = "a controller holds the bot";
        FakeBot alliance = FarBot(rig.Host, "Ucltopcc", team: 0);
        FakeBot free = FarBot(rig.Host, "Ucltopdd");

        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(3);

        Assert.Null(rig.Engine.ClaimOf(grouped.Id));
        Assert.Null(rig.Engine.ClaimOf(scripted.Id));
        Assert.Null(rig.Engine.ClaimOf(alliance.Id));
        Assert.Equal((Event, 2u), rig.Engine.ClaimOf(free.Id));
        Assert.DoesNotContain(rig.Host.Teleports, t => t.Bot is "Ucltopaa" or "Ucltopbb" or "Ucltopcc");
        Assert.Contains("claimed 1/3", rig.Status());
        Assert.Contains("unavailable 3", rig.Status());
    }

    [Fact]
    public void ABotClaimedByAnotherScenarioGroup_IsNotTakenTwice()
    {
        var rig = new Rig(Defenders(prefix: "Uc", count: 1, group: 1), Defenders(prefix: "Ucltop", count: 1, group: 2));
        FakeBot only = FarBot(rig.Host, "Ucltopaa");

        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(3);

        Assert.Equal((Event, 1u), rig.Engine.ClaimOf(only.Id));
        Assert.Empty(rig.Engine.RunOf(Event, 2)!.Claims);
        Assert.Equal(1, rig.Engine.RunOf(Event, 2)!.Unavailable);
        Assert.Single(rig.Host.Posts, p => p.Value is not null);
    }

    [Fact]
    public void ABotAnotherSystemTookAfterTheClaim_IsNotMovedFromUnderIt()
    {
        var rig = new Rig(Defenders(count: 1));
        FakeBot bot = FarBot(rig.Host, "Ucltopaa");
        bot.HeldBy = null;
        rig.Engine.EventChanged(Event, active: true);
        rig.Tick();
        Assert.Single(rig.Host.Teleports);
        bot.Position = new Vector3(-3000f, 0f, 0f);
        bot.Map = 1;
        bot.HeldBy = "the battleground coordinator drives the bot";
        rig.Ticks(10);
        Assert.Single(rig.Host.Teleports);
    }

    // --- a siege group -----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSiegeStartsOnlyOnceTheReadyPercentageIsPlaced_AndOnlyOnce()
    {
        var rig = new Rig(Raiders(count: 10, minReady: 80));
        for (int i = 0; i < 7; i++) FarBot(rig.Host, "Siegebot" + (char)('a' + i), team: 0);
        foreach (string late in new[] { "Siegebothh", "Siegebotii", "Siegebotjj" })
            rig.Host.Add(late, team: 0).Desired = true; // registered and wanted running: the restore has not logged them in yet

        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(4);

        Assert.Empty(rig.Host.SiegeStarts);
        Assert.Contains("siege waiting: 7/10 ready, starts at 80%", rig.Status());

        rig.Host.Find("Siegebothh").OnlineAt(1, new Vector3(-3000f, 0f, 0f), 60);
        rig.Ticks(3);

        Assert.Single(rig.Host.SiegeStarts);
        (IReadOnlyList<Guid> bots, EventScenarioSiege siege) = rig.Host.SiegeStarts[0];
        Assert.Equal(8, bots.Count);
        Assert.Equal((10181u, 2, 2), (siege.ObjectiveEntry, siege.Raids, siege.Waypoints.Count));
        rig.Ticks(5);
        Assert.Single(rig.Host.SiegeStarts);
        Assert.Contains("siege 1 running (8 bots)", rig.Status());
    }

    /// <summary>Five wanted, four online and placed, the fifth not online yet, so a 100 percent siege keeps waiting.</summary>
    private static (Rig Rig, FakeBot Drifter, FakeBot Late) WaitingRaiders(bool provision = false)
    {
        var rig = new Rig(Raiders(count: 5, minReady: 100, provision: provision));
        for (int i = 0; i < 4; i++) FarBot(rig.Host, "Siegebot" + (char)('a' + i), team: 0);
        FakeBot late = rig.Host.Add("Siegebote", team: 0); // not online yet
        late.Desired = true;
        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(3);
        Assert.Equal(4, rig.Host.Teleports.Count);
        Assert.Contains("placed 4", rig.Status());
        return (rig, rig.Host.Find("Siegebota"), late);
    }

    private static void AssertDriftedBotIsTeleportedBackAfterTheGrace_AndEnlistedOnlyWhenBack(Rig rig, FakeBot drifter, FakeBot late)
    {
        rig.Host.TeleportRefusal = "teleport-refused";
        rig.Ticks(2);
        rig.Tick(30_000); // inside the grace: the bot's own walk-back has its chance, nothing is teleported
        Assert.Equal(4, rig.Host.Teleports.Count);
        Assert.Contains("placed 4", rig.Status()); // still counted placed: its own walk-back may bring it home
        Assert.Empty(rig.Host.SiegeStarts);
        rig.Tick(20_000); // past the 45 seconds

        Assert.True(rig.Host.Teleports.Count > 4, "the drifted bot is teleported back after the grace");
        Assert.Contains("placed 3", rig.Status());
        Assert.Empty(rig.Host.SiegeStarts);

        rig.Host.TeleportRefusal = null;
        late.OnlineAt(1, new Vector3(-3000f, 100f, 40f), 60);
        rig.Ticks(10);

        Assert.Single(rig.Host.SiegeStarts);
        Assert.Equal(5, rig.Host.SiegeStarts[0].Bots.Count);
        Assert.Contains(drifter.Id, rig.Host.SiegeStarts[0].Bots);
        Assert.True(Vector3.Distance(drifter.Position, Start) <= EventScenarioEngine.PlacedYards);
    }

    [Fact]
    public void APlacedSiegeBotCarriedOffByDistance_IsTeleportedBackAfterTheGrace_AndNotEnlistedUntilBack()
    {
        (Rig rig, FakeBot drifter, FakeBot late) = WaitingRaiders();
        drifter.Position = Start + new Vector3(1000f, 0f, 0f); // same map, 1,000 yards off: a retreat or a fight carried it away
        AssertDriftedBotIsTeleportedBackAfterTheGrace_AndEnlistedOnlyWhenBack(rig, drifter, late);
    }

    [Fact]
    public void APlacedSiegeBotOnAnotherMap_IsTeleportedBackAfterTheGrace_AndNotEnlistedUntilBack()
    {
        (Rig rig, FakeBot drifter, FakeBot late) = WaitingRaiders();
        drifter.Map = 1; // same coordinates, another map (a dungeon portal)
        AssertDriftedBotIsTeleportedBackAfterTheGrace_AndEnlistedOnlyWhenBack(rig, drifter, late);
    }

    [Fact]
    public void APlacedBotAway_IsNotTeleportedBeforeTheGrace_AndIsAfterIt()
    {
        (Rig rig, FakeBot drifter, _) = WaitingRaiders();
        drifter.Position = Start + new Vector3(500f, 0f, 0f);
        rig.Tick();
        rig.Tick((uint)EventScenarioEngine.DriftTeleportMs - 4_000);
        Assert.Equal(4, rig.Host.Teleports.Count);

        rig.Tick(6_000);
        Assert.Equal(5, rig.Host.Teleports.Count);
        Assert.Equal("Siegebota", rig.Host.Teleports[^1].Bot);
    }

    [Fact]
    public void WaitingSiegeBots_HoldAStay_NotADefendPost()
    {
        (Rig rig, _, FakeBot late) = WaitingRaiders();
        Assert.All(rig.Host.Posts.Values, post => Assert.Null(post));
        FakeBot[] online = [.. rig.Host.Bots.Where(b => b.Online)];
        Assert.Equal(4, online.Length);
        foreach (FakeBot bot in online)
        {
            PlayerbotStay stay = Assert.IsType<PlayerbotStay>(rig.Host.Stays[bot.Id]);
            Assert.True(EventScenarioPostIds.IsScenario(stay.StayId));
            Assert.Equal(Start, stay.Centre);
            Assert.Equal(0u, stay.MapId);
            Assert.True(stay.Radius < EventScenarioEngine.PlacedYards);
        }

        late.OnlineAt(1, new Vector3(-3000f, 100f, 40f), 60);
        rig.Ticks(6);

        Assert.Single(rig.Host.SiegeStarts);
        Assert.Equal(5, rig.Host.SiegeStarts[0].Bots.Count);
    }

    [Fact]
    public void TheStay_IsMusteredBeforeThePlacement_AndRenewedEveryPass()
    {
        var rig = new Rig(Raiders(count: 5, minReady: 100));
        FakeBot far = FarBot(rig.Host, "Siegebota", team: 0);
        rig.Host.Add("Siegebotb", team: 0).Desired = true;
        rig.Engine.EventChanged(Event, active: true);
        rig.Tick();

        Assert.NotNull(rig.Host.Stays[far.Id]);
        uint first = rig.Host.Stays[far.Id]!.UntilMs;
        int calls = rig.Host.StayCalls;
        rig.Tick();
        rig.Tick();

        Assert.Equal(calls + 2, rig.Host.StayCalls);
        Assert.True(unchecked((int)(rig.Host.Stays[far.Id]!.UntilMs - first)) > 0);
    }

    [Fact]
    public void TheStay_IsClearedWhenTheSiegeStarts_WhenTheEventStops_AndWhenAClaimIsGivenUp()
    {
        (Rig rig, _, FakeBot late) = WaitingRaiders();
        FakeBot held = rig.Host.Find("Siegebotb");
        held.HeldBy = "is in a group";
        rig.Tick();
        rig.Tick((uint)EventScenarioEngine.BusyReleaseMs + 1_000);
        Assert.Null(rig.Host.Stays[held.Id]); // given up
        Assert.Null(rig.Engine.ClaimOf(held.Id));

        // a siege that starts clears every stay
        late.OnlineAt(1, new Vector3(-3000f, 100f, 40f), 60);
        rig.Host.Add("Siegebotf", team: 0).OnlineAt(1, new Vector3(-3000f, 100f, 40f), 60);
        rig.Ticks(8);
        Assert.Single(rig.Host.SiegeStarts);
        Assert.All(rig.Host.Stays.Values, stay => Assert.Null(stay));

        // an event that stops while the siege waits clears them too
        (Rig waiting, _, _) = WaitingRaiders();
        Assert.All(waiting.Host.Bots.Where(b => b.Online), b => Assert.NotNull(waiting.Host.Stays[b.Id]));
        waiting.Engine.EventChanged(Event, active: false);
        Assert.All(waiting.Host.Stays.Values, stay => Assert.Null(stay));
    }

    [Fact]
    public void ARefusedStay_IsShown_NotCountedReady_AndRetriedAtTheUsualCadence()
    {
        var rig = new Rig(Raiders(count: 4, minReady: 100));
        for (int i = 0; i < 4; i++) FarBot(rig.Host, "Siegebot" + (char)('a' + i), team: 0);
        rig.Host.StayRefusal = "bot-busy";
        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(4);

        Assert.Empty(rig.Host.SiegeStarts);
        Assert.Contains("stay: bot-busy x4", rig.Status());
        Assert.Contains("siege waiting: 0/4 ready", rig.Status());

        rig.Host.StayRefusal = null;
        rig.Tick((uint)EventScenarioEngine.RetryMs);
        rig.Ticks(3);
        Assert.Single(rig.Host.SiegeStarts);
    }

    [Fact]
    public void ABotHeldElsewhere_GetsNoStay_AndIsNotReady()
    {
        var rig = new Rig(Raiders(count: 4, minReady: 100));
        for (int i = 0; i < 4; i++) FarBot(rig.Host, "Siegebot" + (char)('a' + i), team: 0);
        FakeBot held = rig.Host.Find("Siegebotd");
        rig.Engine.EventChanged(Event, active: true);
        rig.Tick();
        held.HeldBy = "is queued for a battleground";
        rig.Ticks(3);

        Assert.Null(rig.Host.Stays[held.Id]); // no stay for a bot held elsewhere (cleared, not left to the lease)
        Assert.Contains("siege waiting: 3/4 ready", rig.Status());
        Assert.Contains("held 1", rig.Status());
    }

    [Fact]
    public void AWaitingClaimThatTurnsBusy_HasItsStayClearedAtOnce_NotAtTheEndOfTheLease()
    {
        var rig = new Rig(Raiders(count: 4, minReady: 100));
        for (int i = 0; i < 4; i++) FarBot(rig.Host, "Siegebot" + (char)('a' + i), team: 0);
        FakeBot held = rig.Host.Find("Siegebotd");
        rig.Engine.EventChanged(Event, active: true);
        rig.Tick();
        Assert.NotNull(rig.Host.Stays[held.Id]);
        held.HeldBy = "is queued for a battleground";
        rig.Tick();

        Assert.Null(rig.Host.Stays[held.Id]); // cleared on the next pass, long before the 30 s lease ends
    }

    [Fact]
    public void TheBusyClock_DoesNotRunWhileTheBotIsOffline()
    {
        (Rig rig, _, FakeBot late) = WaitingRaiders(provision: true);
        FakeBot held = rig.Host.Find("Siegebotb");
        held.HeldBy = "is in a group";
        rig.Tick((uint)EventScenarioEngine.BusyReleaseMs - 10_000); // busy for most of the window
        held.Online = false;
        held.State = ManagedPlayerbotState.Running; // logged out for a long while
        rig.Tick((uint)EventScenarioEngine.BusyReleaseMs);
        held.Online = true;
        late.OnlineAt(1, new Vector3(-3000f, 100f, 40f), 60);
        rig.Tick();

        Assert.NotNull(rig.Engine.ClaimOf(held.Id)); // the clock restarted when it came back
    }

    [Fact]
    public void ReplacementCreation_IsCappedToATenthOfTheGroup_AndAReleasedBotIsClaimedAgainWhenFree()
    {
        var rig = new Rig(Raiders(count: 10, minReady: 100, provision: true));
        rig.Host.SiegeRefusal = "held-for-the-test"; // the siege keeps waiting however ready the group is
        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(12);
        Assert.Equal(10, rig.Host.Created.Count);
        foreach (FakeBot created in rig.Host.Bots) created.Team = 0; // the fake registers new characters as Horde; the group is Alliance
        FakeBot[] busy = [.. rig.Host.Bots.Take(3)];
        foreach (FakeBot bot in busy) bot.HeldBy = "is in a group";
        rig.Tick();
        rig.Tick((uint)EventScenarioEngine.BusyReleaseMs + 1_000);
        rig.Ticks(20);

        Assert.Equal(11, rig.Host.Created.Count); // a tenth of 10 beyond the 10 the group needed
        Assert.Contains("replacements 1/1", rig.Status());

        foreach (FakeBot bot in busy) bot.HeldBy = null;
        rig.Ticks(12);
        Assert.Equal(11, rig.Host.Created.Count);
        Assert.Equal(10, rig.Host.Bots.Count(b => rig.Engine.ClaimOf(b.Id) is not null));
    }

    [Fact]
    public void AClaimGivenUpWhileTheGroupIsStillFilling_DoesNotStopTheFill()
    {
        var rig = new Rig(Raiders(count: 20, minReady: 100, provision: true));
        rig.Host.SiegeRefusal = "held-for-the-test";
        for (int i = 0; i < 5; i++) FarBot(rig.Host, "Siegebot" + (char)('a' + i), team: 0);
        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(2);
        Assert.True(rig.Host.Created.Count is > 0 and < 10, "the fill is under way: " + rig.Host.Created.Count);

        FakeBot held = rig.Host.Find("Siegebota");
        held.HeldBy = "is in a group";
        rig.Tick();
        rig.Tick((uint)EventScenarioEngine.BusyReleaseMs + 1_000); // given up while many are still missing
        Assert.Null(rig.Engine.ClaimOf(held.Id));
        rig.Ticks(30);

        Assert.Equal(20, rig.Host.Bots.Count(b => rig.Engine.ClaimOf(b.Id) is not null)); // still filled to the count
        Assert.Equal(16, rig.Host.Created.Count); // 4 existing bots remain, 16 created: within the count plus a tenth
    }

    [Fact]
    public void ARefusedCreation_GivesItsAllowanceBack()
    {
        var rig = new Rig(Raiders(count: 10, minReady: 100, provision: true));
        rig.Host.SiegeRefusal = "held-for-the-test";
        int refusals = 0;
        rig.Host.CreateAnswer = name => refusals++ < 30 ? new PlayerbotOperationResult(false, "name-in-use") : null;
        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(40);

        Assert.Equal(10, rig.Host.Bots.Count(b => rig.Engine.ClaimOf(b.Id) is not null)); // 30 refusals did not use up the cap of 11
    }

    [Fact]
    public void EightyBotSiegeAt100Pct_UnderThrottledFill_StartsWithinBoundedPasses_AlthoughUnpinnedBotsWander()
    {
        var rig = RunEighty(honourStays: true, out FakeBot[] all);
        Assert.Single(rig.Host.SiegeStarts);
        Assert.Equal(80, rig.Host.SiegeStarts[0].Bots.Count);
        Assert.All(all, bot => Assert.True(Vector3.Distance(bot.Position, Start) <= EventScenarioEngine.PlacedYards));
        Assert.True(rig.Host.Teleports.Count <= 80 + 20, "teleports " + rig.Host.Teleports.Count);
    }

    [Fact]
    public void EightyBotSiegeAt100Pct_WhenBotsIgnoreTheirStay_NeverConverges()
    {
        var rig = RunEighty(honourStays: false, out _);
        Assert.Empty(rig.Host.SiegeStarts);
        Assert.True(rig.Host.Teleports.Count > 80, "teleports " + rig.Host.Teleports.Count);
    }

    private static Rig RunEighty(bool honourStays, out FakeBot[] all)
    {
        var rig = new Rig(Raiders(count: 80, minReady: 100));
        rig.Host.WanderAfterPasses = 2;
        rig.Host.HonourStays = honourStays;
        all = new FakeBot[80];
        for (int i = 0; i < 80; i++)
        {
            all[i] = rig.Host.Add("Siegebot" + (char)('a' + (i / 26)) + (char)('a' + (i % 26)), team: 0);
            all[i].State = ManagedPlayerbotState.Running; // registered and running, but not logged in yet: the fill is throttled
            all[i].Desired = true;
        }

        rig.Engine.EventChanged(Event, active: true);
        for (int pass = 0; pass < 40 && (!honourStays || rig.Host.SiegeStarts.Count == 0); pass++)
        {
            if (pass % 3 == 0 && pass / 3 < 8)
                for (int k = 0; k < 10; k++) all[((pass / 3) * 10) + k].OnlineAt(1, new Vector3(-3000f, 100f, 40f), 60);
            rig.Tick();
        }

        return rig;
    }

    [Fact]
    public void AWaitingClaimHeldElsewhereForTooLong_IsGivenUp_AndAReplacementIsCreated()
    {
        (Rig rig, _, FakeBot late) = WaitingRaiders(provision: true);
        late.OnlineAt(1, new Vector3(-3000f, 100f, 40f), 60);
        FakeBot held = rig.Host.Find("Siegebotb");
        held.HeldBy = "is in a group";
        rig.Tick();
        Assert.Empty(rig.Host.SiegeStarts);
        Assert.NotNull(rig.Engine.ClaimOf(held.Id));

        rig.Tick((uint)EventScenarioEngine.BusyReleaseMs + 1_000);
        Assert.Null(rig.Engine.ClaimOf(held.Id));
        rig.Ticks(12);

        Assert.Single(rig.Host.Created);
        Assert.Single(rig.Host.SiegeStarts);
        Assert.Equal(5, rig.Host.SiegeStarts[0].Bots.Count);
        Assert.DoesNotContain(held.Id, rig.Host.SiegeStarts[0].Bots);
    }

    [Fact]
    public void AWaitingClaimThatBecomesBusyBriefly_IsKept()
    {
        (Rig rig, _, FakeBot late) = WaitingRaiders(provision: true);
        FakeBot held = rig.Host.Find("Siegebotb");
        held.HeldBy = "is in a group";
        rig.Ticks(3);
        held.HeldBy = null;
        rig.Tick((uint)EventScenarioEngine.BusyReleaseMs);
        late.OnlineAt(1, new Vector3(-3000f, 100f, 40f), 60);
        rig.Ticks(6);

        Assert.NotNull(rig.Engine.ClaimOf(held.Id));
        Assert.Empty(rig.Host.Created);
        Assert.Single(rig.Host.SiegeStarts);
    }

    [Fact]
    public void ASiegeBotOnAGmsDefendPostElsewhere_IsNotMovedFromUnderIt_NotReady_AndGivenUpAfterTheTimeout()
    {
        (Rig rig, _, _) = WaitingRaiders();
        FakeBot gm = rig.Host.Find("Siegebotb");
        gm.Position = Start + new Vector3(800f, 0f, 0f);
        rig.Host.Posts[gm.Id] = new PlayerbotDefendPost(0, gm.Position, 40f, 7); // a GM's post (not a scenario id)
        rig.Ticks(4);

        Assert.Equal(4, rig.Host.Teleports.Count); // not teleported away from its post
        Assert.Contains("siege waiting: 3/5 ready", rig.Status());
        Assert.Empty(rig.Host.SiegeStarts);

        rig.Tick((uint)EventScenarioEngine.BusyReleaseMs + 1_000);
        Assert.Null(rig.Engine.ClaimOf(gm.Id));
        Assert.Equal(7, rig.Host.Posts[gm.Id]!.PostId); // the GM's post is untouched
    }

    [Fact]
    public void ARefusedSiege_IsShownWithItsReason_AndRetriedUntilItStarts()
    {
        var rig = new Rig(Raiders(count: 4));
        for (int i = 0; i < 4; i++) FarBot(rig.Host, "Siegebot" + (char)('a' + i), team: 0);
        rig.Host.SiegeRefusal = "too-few-bots";
        rig.Engine.EventChanged(Event, active: true);

        rig.Ticks(4);
        Assert.Empty(rig.Host.SiegeStarts);
        Assert.Contains("siege refused: too-few-bots (retrying)", rig.Status());

        rig.Host.SiegeRefusal = null;
        rig.Ticks(3);
        Assert.Single(rig.Host.SiegeStarts);
        Assert.DoesNotContain("refused", rig.Status());
    }

    [Fact]
    public void ASiegeThatEnded_IsNotStartedAgain_AndTheStatusShowsHowItEnded()
    {
        var rig = new Rig(Raiders(count: 4));
        for (int i = 0; i < 4; i++) FarBot(rig.Host, "Siegebot" + (char)('a' + i), team: 0);
        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(6);
        Assert.Single(rig.Host.SiegeStarts);

        rig.Host.RunningSiegeId = null;
        rig.Host.SiegeOutcome = "won";
        rig.Ticks(6);

        Assert.Single(rig.Host.SiegeStarts);
        Assert.Contains("siege ended: won", rig.Status());
    }

    [Fact]
    public void EventStop_StopsTheSiegeThisScenarioStarted_ButNotAnotherOne()
    {
        var rig = new Rig(Raiders(count: 4));
        for (int i = 0; i < 4; i++) FarBot(rig.Host, "Siegebot" + (char)('a' + i), team: 0);
        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(6);
        Assert.NotNull(rig.Host.RunningSiegeId);

        rig.Engine.EventChanged(Event, active: false);
        Assert.Equal(["event stopped"], rig.Host.SiegeStops);
        Assert.Null(rig.Host.RunningSiegeId);

        var other = new Rig(Raiders(count: 4));
        for (int i = 0; i < 4; i++) FarBot(other.Host, "Siegebot" + (char)('a' + i), team: 0);
        other.Host.RunningSiegeId = 77; // a GM's siege
        other.Engine.EventChanged(Event, active: true);
        other.Ticks(6);
        Assert.Empty(other.Host.SiegeStarts);
        Assert.Contains("siege refused: siege-running", other.Status(), StringComparison.Ordinal);
    }

    // --- cost --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WithNoWantedEvent_AnEngineTickReadsNothingAndAllocatesNothing()
    {
        var rig = new Rig(Defenders());
        FarBot(rig.Host, "Ucltopaa");
        rig.Engine.EventChanged(77, active: true); // a running event that has no scenario
        rig.Engine.Tick();
        void Window() { for (int i = 0; i < 10_000; i++) rig.Engine.Tick(); }
        ArcaneCore.TestSupport.AllocationProbe.AssertEveryCleanWindowAtMost(Window, 0, "10000 idle engine ticks");

        Assert.Equal(0, rig.Host.RosterCalls);
        Assert.Empty(rig.Host.Teleports);
    }

    [Fact]
    public void ADisposedEngine_IgnoresLateAnswers_AndTicksNothing()
    {
        var rig = new Rig(Defenders(count: 2, provision: true));
        rig.Host.HoldCreates = true;
        rig.Engine.EventChanged(Event, active: true);
        rig.Tick();
        Assert.Equal(2, rig.Host.Created.Count);

        rig.Engine.Dispose();
        rig.Host.CompleteCreates();
        rig.Ticks(3);

        Assert.Equal(2, rig.Host.Created.Count);
        Assert.Empty(rig.Host.Starts);
    }

    [Fact]
    public void List_NamesTheScenarioEvents_AndWhetherTheyRun()
    {
        var rig = new Rig(Defenders(count: 3));
        IReadOnlyList<string> idle = rig.Engine.ListLines(_ => "Test scenario");
        Assert.Contains("Event 900 \"Test scenario\": 1 group, 3 bots, not running.", idle);

        rig.Engine.EventChanged(Event, active: true);
        Assert.Contains("Event 900 \"Test scenario\": 1 group, 3 bots, running.", rig.Engine.ListLines(_ => "Test scenario"));
        Assert.Contains("Event 5 has no bot scenario.", rig.Engine.StatusLines(5, _ => null));
    }

    // --- review fixes -------------------------------------------------------------------------------------------------------

    [Fact]
    public void ADefenderThatRestarts_GetsItsPostAgain_AndTheStatusSaysWhatIsTrue()
    {
        var rig = new Rig(Defenders(count: 1));
        FakeBot bot = rig.Host.Add("Ucltopaa").OnlineAt(0, Start, 60);
        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(2);
        Assert.NotNull(rig.Host.Posts[bot.Id]);
        Assert.Contains("posts set 1", rig.Status());

        // A restart: the bot goes offline and its new brain has no post (the post lived in the old brain only).
        bot.Online = false;
        bot.State = ManagedPlayerbotState.Stopped;
        rig.Host.Posts.Remove(bot.Id);
        rig.Ticks(1);
        Assert.Contains("posts set 0", rig.Status());

        bot.Online = true;
        bot.State = ManagedPlayerbotState.Running;
        rig.Ticks(2);

        Assert.Equal(EventScenarioPostIds.For(Event, 2, 1), rig.Host.Posts[bot.Id]!.PostId);
        Assert.Contains("posts set 1", rig.Status());
    }

    [Fact]
    public void APostLostWhileTheBotStaysOnline_IsSetAgain_AndAPostThatIsStillThereIsNotSetTwice()
    {
        var rig = new Rig(Defenders(count: 1));
        FakeBot bot = rig.Host.Add("Ucltopaa").OnlineAt(0, Start, 60);
        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(2);
        int sets = rig.Host.DefendCalls;
        rig.Ticks(4);
        Assert.Equal(sets, rig.Host.DefendCalls);

        rig.Host.Posts.Remove(bot.Id);
        rig.Ticks(1);

        Assert.Equal(sets + 1, rig.Host.DefendCalls);
        Assert.NotNull(rig.Host.Posts[bot.Id]);
    }

    [Fact]
    public void AGmPost_IsNeverOverwrittenOrCleared_AndTheStatusSaysSo()
    {
        var rig = new Rig(Defenders(count: 2));
        FakeBot gm = rig.Host.Add("Ucltopaa").OnlineAt(0, Start, 60);
        FakeBot plain = rig.Host.Add("Ucltopbb").OnlineAt(0, Start, 60);
        var gmPost = new PlayerbotDefendPost(0, Start, 15f, 3);
        rig.Host.Posts[gm.Id] = gmPost;
        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(3);

        Assert.Same(gmPost, rig.Host.Posts[gm.Id]);
        Assert.True(EventScenarioPostIds.IsScenario(rig.Host.Posts[plain.Id]!.PostId));
        Assert.Contains("posts set 1", rig.Status());
        Assert.Contains("defend: post-set-by-gm", rig.Status());

        // A GM replaces the scenario's post of the other bot: from then on it is the GM's.
        var later = new PlayerbotDefendPost(0, Start, 20f, 4);
        rig.Host.Posts[plain.Id] = later;
        rig.Ticks(2);
        Assert.Same(later, rig.Host.Posts[plain.Id]);

        rig.Engine.EventChanged(Event, active: false);
        Assert.Same(gmPost, rig.Host.Posts[gm.Id]);
        Assert.Same(later, rig.Host.Posts[plain.Id]);
    }

    [Fact]
    public void ABotTakenByAnotherSystemAfterItWasClaimed_IsNotBoosted()
    {
        var rig = new Rig(Defenders(count: 1));
        FakeBot bot = rig.Host.Add("Ucltopaa").OnlineAt(0, Start, 10);
        bot.Alive = false;
        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(2);
        Assert.Empty(rig.Host.Boosts);

        bot.Alive = true;
        bot.HeldBy = "in a battleground";
        rig.Ticks(3);
        Assert.Empty(rig.Host.Boosts);
        Assert.Contains("boost: in a battleground", rig.Status());

        bot.HeldBy = null;
        rig.Ticks(2);
        Assert.Single(rig.Host.Boosts);
    }

    [Fact]
    public void AReloadedFeature_AdoptsTheSiegeUnderWay_AndNeverStartsASecondOneWhenItEnds()
    {
        var first = new Rig(Raiders(count: 4));
        for (int i = 0; i < 4; i++) FarBot(first.Host, "Siegebot" + (char)('a' + i), team: 0);
        first.Engine.EventChanged(Event, active: true);
        first.Ticks(6);
        Assert.Single(first.Host.SiegeStarts);
        foreach (FakeBot bot in first.Host.Bots) bot.HeldBy = "in a group"; // the raids hold them

        // The feature is detached and attached again: a new engine over the same server, the event still running.
        first.Engine.Dispose();
        var engine = new EventScenarioEngine(first.Host, NullLogger.Instance);
        engine.SetCatalog(EventScenarioCatalogFixtures.Catalog(Raiders(count: 4)));
        engine.EventChanged(Event, active: true);
        first.Host.Now += 2_000;
        engine.Tick();
        Assert.Contains("siege 1 running", string.Join('\n', engine.StatusLines(Event, _ => "t")));

        // The siege ends, the bots are free again: it is over for this event run, not started again.
        first.Host.RunningSiegeId = null;
        first.Host.SiegeOutcome = "won";
        foreach (FakeBot bot in first.Host.Bots) bot.HeldBy = null;
        for (int i = 0; i < 8; i++)
        {
            first.Host.Now += 2_000;
            engine.Tick();
        }

        Assert.Single(first.Host.SiegeStarts);
        Assert.Contains("siege ended: won", string.Join('\n', engine.StatusLines(Event, _ => "t")));
        engine.EventChanged(Event, active: false);
        Assert.Empty(first.Host.SiegeStops); // it ended on its own
    }

    [Fact]
    public void ASiegeOfOtherBots_IsNotAdopted_AndTheScenarioSiegeStartsWhenItEnds()
    {
        var rig = new Rig(Raiders(count: 4));
        for (int i = 0; i < 4; i++) FarBot(rig.Host, "Siegebot" + (char)('a' + i), team: 0);
        rig.Host.RunningSiegeId = 77; // a GM's siege with other bots
        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(3);
        Assert.Empty(rig.Host.SiegeStarts);

        rig.Host.RunningSiegeId = null;
        rig.Ticks(3);
        Assert.Single(rig.Host.SiegeStarts);
    }

    [Fact]
    public void AReloadedFeature_WithProvisioning_CreatesNoBotDuringOrAfterTheAdoptedSiege_AndReclaimsTheOriginals()
    {
        var first = new Rig(Raiders(count: 4, provision: true));
        for (int i = 0; i < 4; i++) FarBot(first.Host, "Siegebot" + (char)('a' + i), team: 0);
        first.Engine.EventChanged(Event, active: true);
        first.Ticks(6);
        Assert.Single(first.Host.SiegeStarts);
        Assert.Empty(first.Host.Created);
        foreach (FakeBot bot in first.Host.Bots) bot.HeldBy = "in a group";

        first.Engine.Dispose();
        var engine = new EventScenarioEngine(first.Host, NullLogger.Instance);
        engine.SetCatalog(EventScenarioCatalogFixtures.Catalog(Raiders(count: 4, provision: true)));
        engine.EventChanged(Event, active: true);
        for (int i = 0; i < 6; i++)
        {
            first.Host.Now += 2_000;
            engine.Tick();
        }

        Assert.Empty(first.Host.Created);
        Assert.Equal(4, first.Host.Bots.Count);
        Assert.All(first.Host.Bots, b => Assert.Equal((Event, 1u), engine.ClaimOf(b.Id)));
        Assert.Contains("claimed 4/4", string.Join('\n', engine.StatusLines(Event, _ => "t")));

        first.Host.RunningSiegeId = null;
        first.Host.SiegeOutcome = "won";
        foreach (FakeBot bot in first.Host.Bots) bot.HeldBy = null;
        for (int i = 0; i < 10; i++)
        {
            first.Host.Now += 2_000;
            engine.Tick();
        }

        Assert.Empty(first.Host.Created);
        Assert.Equal(4, first.Host.Bots.Count);
        Assert.Single(first.Host.SiegeStarts);
    }

    [Fact]
    public void EventStop_StopsAnAdoptedSiege()
    {
        var first = new Rig(Raiders(count: 4));
        for (int i = 0; i < 4; i++) FarBot(first.Host, "Siegebot" + (char)('a' + i), team: 0);
        first.Engine.EventChanged(Event, active: true);
        first.Ticks(6);
        foreach (FakeBot bot in first.Host.Bots) bot.HeldBy = "in a group";
        first.Engine.Dispose();
        var engine = new EventScenarioEngine(first.Host, NullLogger.Instance);
        engine.SetCatalog(EventScenarioCatalogFixtures.Catalog(Raiders(count: 4)));
        engine.EventChanged(Event, active: true);
        first.Host.Now += 2_000;
        engine.Tick();

        engine.EventChanged(Event, active: false);

        Assert.Equal(["event stopped"], first.Host.SiegeStops);
        Assert.Null(first.Host.RunningSiegeId);
    }

    [Fact]
    public void AGmSiege_WithAFewBotsOfThePrefix_IsNotAdopted_AndNotStoppedWithTheEvent()
    {
        var rig = new Rig(Raiders(count: 4));
        for (int i = 0; i < 4; i++) FarBot(rig.Host, "Siegebot" + (char)('a' + i), team: 0);
        rig.Host.RunningSiegeId = 77;
        for (int i = 0; i < 36; i++) rig.Host.SiegeBots.Add(Guid.NewGuid());
        foreach (FakeBot bot in rig.Host.Bots) rig.Host.SiegeBots.Add(bot.Id);
        rig.Engine.EventChanged(Event, active: true);
        rig.Ticks(3);

        rig.Engine.EventChanged(Event, active: false);

        Assert.Empty(rig.Host.SiegeStops);
        Assert.Equal(77, rig.Host.RunningSiegeId);
    }

    [Fact]
    public void ARefusedDefendPost_IsRetriedAtTheUsualCadence_NotEveryPass()
    {
        var rig = new Rig(Defenders(count: 1));
        rig.Host.Add("Ucltopaa").OnlineAt(0, Start, 60);
        rig.Host.DefendRefusal = "bot-busy";
        rig.Engine.EventChanged(Event, active: true);

        rig.Ticks(14); // 28 seconds
        Assert.Equal(1, rig.Host.DefendAttempts);
        rig.Ticks(2);
        Assert.Equal(2, rig.Host.DefendAttempts);
        Assert.Contains("defend: bot-busy", rig.Status());
    }

    /// <summary>A server where a siege with the given bots in its raids is under way, and a fresh engine (a reloaded feature) over it.</summary>
    private static (FakeScenarioHost Host, EventScenarioEngine Engine) Reloaded(EventScenarioGroup group, int prefixBots, int otherBots = 0)
    {
        var host = new FakeScenarioHost();
        for (int i = 0; i < prefixBots; i++)
        {
            FakeBot bot = host.Add("Siegebot" + (char)('a' + i), 0).OnlineAt(0, new Vector3(1900f, 225f, 49f), 60);
            bot.HeldBy = "in a group";
            host.SiegeBots.Add(bot.Id);
        }

        for (int i = 0; i < otherBots; i++)
        {
            FakeBot bot = host.Add("Other" + (char)('a' + i), 0).OnlineAt(0, new Vector3(1900f, 225f, 49f), 60);
            bot.HeldBy = "in a group";
            host.SiegeBots.Add(bot.Id);
        }

        host.RunningSiegeId = 5;
        var engine = new EventScenarioEngine(host, NullLogger.Instance);
        engine.SetCatalog(EventScenarioCatalogFixtures.Catalog(group));
        engine.EventChanged(Event, active: true);
        return (host, engine);
    }

    private static void TickAll(FakeScenarioHost host, EventScenarioEngine engine, int count)
    {
        for (int i = 0; i < count; i++)
        {
            host.Now += 2_000;
            engine.Tick();
        }
    }

    [Fact]
    public void AdoptedBots_AreNotWalkedBackToTheStart_WhenTheSiegeReleasesThem()
    {
        (FakeScenarioHost host, EventScenarioEngine engine) = Reloaded(Raiders(count: 4), 4);
        TickAll(host, engine, 3);
        Assert.Contains("siege 5 running", string.Join('\n', engine.StatusLines(Event, _ => "t")));

        host.RunningSiegeId = null;
        host.SiegeOutcome = "won";
        foreach (FakeBot bot in host.Bots) { bot.HeldBy = null; bot.Position = new Vector3(1300f, 300f, 50f); }
        TickAll(host, engine, 8);

        Assert.Empty(host.Teleports);
        Assert.Empty(host.SiegeStarts);
    }

    [Theory]
    [InlineData(9, 1, true)]  // exactly 90 percent
    [InlineData(8, 2, false)] // 80 percent
    public void AdoptionNeedsNinetyPercentOfTheRaidToBeThePrefix(int prefixBots, int others, bool adopted)
    {
        (FakeScenarioHost host, EventScenarioEngine engine) = Reloaded(Raiders(count: 9, provision: true), prefixBots, others);
        TickAll(host, engine, 4);

        string status = string.Join('\n', engine.StatusLines(Event, _ => "t"));
        Assert.Equal(adopted, status.Contains("siege 5 running", StringComparison.Ordinal));
        Assert.Empty(host.Created); // the prefix bots are in the running siege either way: nothing is made
        if (adopted) Assert.Equal(prefixBots, host.Bots.Count(b => engine.ClaimOf(b.Id) is not null));
        engine.EventChanged(Event, active: false);
        Assert.Equal(adopted ? 1 : 0, host.SiegeStops.Count(s => s == "event stopped"));
    }

    [Fact]
    public void MoreRaidBotsThanTheGroupCount_AreAdoptedUpToTheCount_AndNothingIsCreated()
    {
        (FakeScenarioHost host, EventScenarioEngine engine) = Reloaded(Raiders(count: 4, provision: true), 6);
        TickAll(host, engine, 4);

        Assert.Equal(4, host.Bots.Count(b => engine.ClaimOf(b.Id) is not null));
        Assert.Empty(host.Created);
        Assert.Contains("claimed 4/4", string.Join('\n', engine.StatusLines(Event, _ => "t")));
    }

    [Fact]
    public void AnAdoptedSiege_WithOfflineMembers_KeepsThemClaimed_AndCreatesNothing()
    {
        (FakeScenarioHost host, EventScenarioEngine engine) = Reloaded(Raiders(count: 4, provision: true), 4);
        FakeBot offline = host.Find("Siegebotd");
        offline.Online = false;
        offline.State = ManagedPlayerbotState.Stopped;
        offline.Desired = true; // the restore is on its way
        TickAll(host, engine, 4);

        Assert.Equal((Event, 1u), engine.ClaimOf(offline.Id));
        Assert.Empty(host.Created);
        Assert.Contains("claimed 4/4, online 3", string.Join('\n', engine.StatusLines(Event, _ => "t")));
        Assert.Contains("siege 5 running", string.Join('\n', engine.StatusLines(Event, _ => "t")));
    }

    [Fact]
    public void WhenTheSiegeMembersAreNotKnownYet_AdoptionWaits_NothingIsCreated_AndTheNextPassAdopts()
    {
        (FakeScenarioHost host, EventScenarioEngine engine) = Reloaded(Raiders(count: 4, provision: true), 4);
        List<Guid> members = [.. host.SiegeBots];
        host.SiegeBots.Clear(); // the coordinator's raids are not formed yet
        TickAll(host, engine, 3);
        Assert.Empty(host.Created);
        Assert.DoesNotContain("siege 5 running", string.Join('\n', engine.StatusLines(Event, _ => "t")));

        host.SiegeBots.AddRange(members);
        TickAll(host, engine, 2);

        Assert.Empty(host.Created);
        Assert.Contains("siege 5 running", string.Join('\n', engine.StatusLines(Event, _ => "t")));
        Assert.Equal(4, host.Bots.Count(b => engine.ClaimOf(b.Id) is not null));
    }
}

/// <summary>Catalogs built from groups directly.</summary>
internal static class EventScenarioCatalogFixtures
{
    /// <summary>A catalog holding exactly <paramref name="groups"/> (built through the row validation, so the fixtures stay valid).</summary>
    public static EventScenarioCatalog Catalog(params EventScenarioGroup[] groups)
    {
        var groupRows = new List<GameEventPlayerbotGroupRow>();
        var postRows = new List<GameEventPlayerbotPostRow>();
        var siegeRows = new List<GameEventPlayerbotSiegeRow>();
        var wayRows = new List<GameEventPlayerbotWaypointRow>();
        foreach (EventScenarioGroup g in groups)
        {
            groupRows.Add(new GameEventPlayerbotGroupRow
            {
                Event = g.EventId, GroupId = g.GroupId, NamePrefix = g.NamePrefix, Count = g.Count, Team = g.Team,
                ClassSpec = string.Join(',', g.ClassSpec.Select(s => $"{s.Race}:{s.Class}")), Level = (byte)g.Level, Provision = g.Provision,
                StartMap = g.StartMap, StartX = g.Start.X, StartY = g.Start.Y, StartZ = g.Start.Z, StartO = g.StartOrientation, Role = (byte)g.Role,
            });
            postRows.AddRange(g.Posts.Select(p => new GameEventPlayerbotPostRow
            {
                Event = g.EventId, GroupId = g.GroupId, PostId = p.PostId, Map = p.Map, X = p.Centre.X, Y = p.Centre.Y, Z = p.Centre.Z, Radius = p.Radius,
            }));
            if (g.Siege is { } siege)
            {
                siegeRows.Add(new GameEventPlayerbotSiegeRow
                {
                    Event = g.EventId, GroupId = g.GroupId, ObjectiveEntry = siege.ObjectiveEntry, Raids = (byte)siege.Raids, MinReadyPct = (byte)siege.MinReadyPct,
                });
                uint seq = 1;
                wayRows.AddRange(siege.Waypoints.Select(w => new GameEventPlayerbotWaypointRow
                {
                    Event = g.EventId, GroupId = g.GroupId, Seq = seq++, Map = siege.Map, X = w.X, Y = w.Y, Z = w.Z,
                }));
            }
        }

        EventScenarioCatalog catalog = EventScenarioCatalog.Build(new GameEventPlayerbotContent(groupRows, postRows, siegeRows, wayRows), _ => true);
        Assert.Empty(catalog.Issues);
        return catalog;
    }
}
