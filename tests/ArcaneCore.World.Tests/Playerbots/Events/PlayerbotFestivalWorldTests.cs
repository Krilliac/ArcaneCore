using ArcaneCore.Game;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Events;
using ArcaneCore.World.Playerbots.Population;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Events.EventsTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Events;

/// <summary>
/// Festival gathering end to end (P7a H2 and H3, design 4.2 and section 9): capital-homed idle bots on the scenario world with the real
/// game-event feature, spawn gate and town idle. A running festival moves a bounded number of them from the inn to a spot 8 to 12 yards
/// from a festival NPC, and its end brings them back; the NPCs a player talks to stay clear; a spot the bot cannot get to sends it back to
/// the inn instead of out hunting. Positions only: the tests read where the bots stand, not how they decided.
/// </summary>
public sealed class PlayerbotFestivalWorldTests
{
    private const uint RevelerEntry = 991_001, GiverEntry = 991_002, PlainEntry = 991_003, HiddenEntry = 991_004, FarEntry = 991_005, SecondRevelerEntry = 991_006;

    /// <summary>A bot at its spot stands within the town idle's linger distance (4 yards) of a point 8 to 12 yards from the NPC.</summary>
    private const float SpotReach = PlayerbotFestivalRules.RingMaxYards + PlayerbotTownIdle.LingerYards + 0.5f;

    private static FestivalNpc Reveler(float dx = 60f, float dy = 0f, float dz = 0f, uint entry = RevelerEntry)
        => new(entry, "Winter Reveler", NpcFlags.Gossip, dx, dy, dz);

    private static FestivalNpc Giver(float dx = -60f, float dy = 0f)
        => new(GiverEntry, "Greatfather Winter", NpcFlags.QuestGiver | NpcFlags.Vendor, dx, dy);

    private static void Quick(PlayerbotOptions options)
    {
        options.StallSeconds = 10;
        options.Social.Events.Holidays.CelebrateChance = 100; // every bot celebrates: these tests are about where it goes, not the roll
    }

    private static async Task<float> FromInnAsync(EventsTestWorld world, Guid bot) => Distance2D(await world.PositionAsync(bot), Inn);

    // --- the control -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithNoFestival_TheIdleBotIsHeldAtTheInn()
    {
        await using EventsTestWorld world = await EventsTestWorld.StartAsync([Reveler()], Quick);
        Guid bot = await world.AddHomedBotAsync("Innbot");

        await world.RunForAsync(bot, 60_000);

        Assert.True(await FromInnAsync(world, bot) <= PlayerbotTownIdle.LingerYards, "the idle bot left its inn with no festival running");
        Assert.False(await world.World.Host.OnWorldAsync(() => world.Events.IsActiveEvent(WinterVeil)));
    }

    // --- gathering and returning -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AFestival_SendsTheIdleBotToASpotAroundAnAnchor_HoldsItThere_AndTheEndBringsItBackToTheInn()
    {
        await using EventsTestWorld world = await EventsTestWorld.StartAsync([Reveler()], Quick);
        Guid bot = await world.AddHomedBotAsync("Festbot");
        var reveler = new Vector3(StartXOf(60f), StartYOf(0f), 0f);

        await world.StartEventAsync(WinterVeil);

        Assert.True(await world.RunUntilAsync(bot, 120_000, () => Near(world, bot, reveler, SpotReach)),
            $"the bot never reached a spot around the reveler (it is {Distance2D(await world.PositionAsync(bot), reveler):F1} yards from it)");
        float held = Distance2D(await world.PositionAsync(bot), reveler);
        Assert.True(held <= SpotReach, $"{held:F1} yards from the reveler");

        // Held there: it stays for a while, does not wander off, and the stall watch (10 s here) reports nothing.
        await world.RunForAsync(bot, 45_000);
        Assert.True(Distance2D(await world.PositionAsync(bot), reveler) <= SpotReach, "the bot left its spot while the festival ran");
        Assert.Equal(0, await world.World.Host.OnWorldAsync(() => world.World.Bots.FindBrain(bot)!.StallCount));
        Assert.Equal(0, await world.World.Host.OnWorldAsync(() => world.World.Bots.FindBrain(bot)!.ExploreWanderPlans));

        // The festival ends: back to the inn, and held there.
        await world.StopEventAsync(WinterVeil);
        Assert.True(await world.RunUntilAsync(bot, 120_000, () => InnDistance(world, bot) <= PlayerbotTownIdle.LingerYards),
            $"the bot did not return to the inn ({await FromInnAsync(world, bot):F1} yards from it)");
    }

    [Fact]
    public async Task ASecondaryTownBot_InItsExploreWindow_ClaimsNoPlace_SoNoPlaceIsTakenAndReleasedEveryThink()
    {
        await using EventsTestWorld world = await EventsTestWorld.StartAsync([Reveler()], Quick);
        Guid bot = await world.AddHomedBotAsync("Secbot", TownStay.Secondary);
        await world.StartEventAsync(WinterVeil);

        // The control: in its first (linger) window the bot takes a place.
        Assert.True(await world.RunUntilAsync(bot, 60_000, () => world.World.Bots.FindBrain(bot)!.HoldsEventSpot), "the bot never took a place in its linger window");

        // The linger window (120 s from the first think) ends and the explore window (240 s) begins: from then on no place is held at any think.
        Assert.True(await world.RunUntilAsync(bot, 150_000, () => !world.World.Bots.FindBrain(bot)!.HoldsEventSpot), "the bot kept its place into the explore window");
        // A place taken and released inside one think is never seen holding between thinks: count the claims taken instead.
        long taken = await world.World.Host.OnWorldAsync(() => world.World.Bots.FindBrain(bot)!.EventBoard!.Claims.TakenCount);
        Assert.False(await world.RunUntilAsync(bot, 100_000, () => world.World.Bots.FindBrain(bot)!.EventBoard!.Claims.TakenCount != taken), "the bot took a place again inside its explore window");
    }

    public static TheoryData<string> SwitchesOff() => new() { "Events:Enabled", "Holidays:Enabled", "CelebrateChance=0" };

    [Theory]
    [MemberData(nameof(SwitchesOff))]
    public async Task WithTheFeatureSwitchedOff_NoBotLeavesTheInn_AndSwitchingItOnLiveSendsIt(string off)
    {
        await using EventsTestWorld world = await EventsTestWorld.StartAsync([Reveler()], options =>
        {
            Quick(options);
            Apply(options, off, on: false);
        });
        Guid bot = await world.AddHomedBotAsync("Offbot");
        await world.StartEventAsync(WinterVeil);

        await world.RunForAsync(bot, 90_000);
        Assert.True(await FromInnAsync(world, bot) <= PlayerbotTownIdle.LingerYards, $"the bot left the inn with {off}");

        // The control: the same festival, the switch on (live, no restart), and the bot goes.
        await world.World.Host.OnWorldAsync(() => Apply(world.Options, off, on: true));
        Assert.True(await world.RunUntilAsync(bot, 120_000, () => InnDistance(world, bot) > 30f), "switching the feature on live did not send the bot");
    }

    private static bool Apply(PlayerbotOptions options, string key, bool on)
    {
        switch (key)
        {
            case "Events:Enabled": options.Social.Events.Enabled = on; break;
            case "Holidays:Enabled": options.Social.Events.Holidays.Enabled = on; break;
            default: options.Social.Events.Holidays.CelebrateChance = on ? 100 : 0; break;
        }

        return true;
    }

    // --- which NPCs count ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ANpcWithNoFlag_ANotSelectableOne_AndOneOutsideTheHome_AreNotAnchors()
    {
        // The real anchor stands north of the inn; the three others east, west and 450 yards east (the home is 300 yards across).
        FestivalNpc real = Reveler(dx: 0f, dy: 80f);
        FestivalNpc[] others =
        [
            new(PlainEntry, "Wickerman Guardian", NpcFlags.None, 70f, 0f),
            new(HiddenEntry, "Spotlight", NpcFlags.Gossip, -70f, 0f, UnitFlags: (uint)UnitFlags.NotSelectable),
            new(FarEntry, "Distant Reveler", NpcFlags.Gossip, 450f, 0f),
        ];
        await using EventsTestWorld world = await EventsTestWorld.StartAsync([real, .. others], Quick);
        Guid bot = await world.AddHomedBotAsync("Pickybot");
        var anchor = new Vector3(StartXOf(0f), StartYOf(80f), 0f);

        await world.StartEventAsync(WinterVeil);
        Assert.True(await world.RunUntilAsync(bot, 150_000, () => Near(world, bot, anchor, SpotReach)), "the bot did not go to the one real anchor");

        // Held there, nowhere near the others (the nearest is 70 yards from the inn, 100 from the real anchor).
        await world.RunForAsync(bot, 30_000);
        Vector3 at = await world.PositionAsync(bot);
        Assert.True(Distance2D(at, anchor) <= SpotReach);
        foreach (FestivalNpc other in others)
            Assert.True(Distance2D(at, new Vector3(StartXOf(other.Dx), StartYOf(other.Dy), 0f)) > 40f, $"the bot went to {other.Name}");
    }

    // --- the quest giver stays clear -------------------------------------------------------------------------------------

    [Fact]
    public async Task AroundAFestivalQuestGiver_AtMostThreeBotsGather_AndNoneStandsWithinFiveYardsOfIt()
    {
        await using EventsTestWorld world = await EventsTestWorld.StartAsync([Giver()], Quick);
        var bots = new List<Guid>();
        for (int i = 0; i < 8; i++) bots.Add(await world.AddHomedBotAsync("Crowdbot" + (char)('a' + i)));
        var giver = new Vector3(StartXOf(-60f), StartYOf(0f), 0f);

        await world.StartEventAsync(WinterVeil);
        await world.RunForAsync(bots[0], 150_000);

        List<Vector3> positions = [];
        foreach (Guid bot in bots) positions.Add(await world.PositionAsync(bot));
        int gathered = positions.Count(p => Distance2D(p, giver) <= SpotReach);
        int atInn = positions.Count(p => Distance2D(p, Inn) <= PlayerbotTownIdle.LingerYards);

        Assert.Equal(3, gathered); // MaxPerServiceAnchor: exactly the first three claims
        Assert.Equal(5, atInn); // the rest keep the inn
        foreach (Vector3 p in positions.Where(p => Distance2D(p, giver) <= SpotReach))
        {
            Assert.True(Math.Abs(p.Z - StartZ) <= 5f);
            Assert.True(Distance2D(p, giver) > 5f, $"a bot stands {Distance2D(p, giver):F1} yards from the quest giver: inside its interaction range, over the NPC");
        }
    }

    // --- a spot the bot cannot reach -------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAnchorOnAnotherFloor_SendsNoBotHuntingAndLeavesItHeldAtTheInn()
    {
        // 20 yards above the floor the bots can reach: they can walk under it but never stand where the spot is.
        await using EventsTestWorld world = await EventsTestWorld.StartAsync([Reveler(dz: 20f)], Quick);
        Guid bot = await world.AddHomedBotAsync("Floorbot");

        await world.StartEventAsync(WinterVeil);
        await world.RunForAsync(bot, 150_000);

        Assert.True(await FromInnAsync(world, bot) <= PlayerbotTownIdle.LingerYards, $"the bot is {await FromInnAsync(world, bot):F1} yards from the inn");
        Assert.Equal(0, await world.World.Host.OnWorldAsync(() => world.World.Bots.FindBrain(bot)!.ExploreWanderPlans)); // the walk back is the inn's, not a hunt
    }

    // --- the centre changes under a walk ---------------------------------------------------------------------------------

    [Fact]
    public async Task AFestivalStartingWhileTheBotWalksToTheInn_DoesNotMakeThatWalkLookStalled()
    {
        await using EventsTestWorld world = await EventsTestWorld.StartAsync([Reveler(dx: 150f)], Quick);
        Guid bot = await world.AddHomedBotAsync("Walkbot");
        var reveler = new Vector3(StartXOf(150f), StartYOf(0f), 0f);

        // 150 yards west of the inn, walking home; the festival starts five seconds into the walk, with its anchor east of the inn.
        await world.World.Host.OnWorldAsync(() =>
        {
            Player player = world.World.Bots.FindSession(bot)!.Player!;
            player.Relocate(Inn.X - 150f, Inn.Y, Inn.Z, 0f, world.World.Host.World.NowMs);
            return true;
        });
        await world.RunForAsync(bot, 5_000);
        Assert.True(await FromInnAsync(world, bot) is > 40f and < 150f, "the bot was not on its way to the inn");

        await world.StartEventAsync(WinterVeil);

        // 265 yards at the bots speed is under 40 s; a walk judged by the distances of the other centre would have been given up at 20 s.
        Assert.True(await world.RunUntilAsync(bot, 120_000, () => Near(world, bot, reveler, SpotReach)),
            $"the bot never reached its spot ({Distance2D(await world.PositionAsync(bot), reveler):F1} yards from the reveler, {await FromInnAsync(world, bot):F1} from the inn)");
    }

    // --- helpers ---------------------------------------------------------------------------------------------------------

    private static float StartXOf(float dx) => StartX + dx;

    private static float StartYOf(float dy) => StartY + dy;

    private static bool Near(EventsTestWorld world, Guid bot, Vector3 point, float yards)
    {
        Player player = world.World.Bots.FindSession(bot)!.Player!;
        return Distance2D(new Vector3(player.X, player.Y, player.Z), point) <= yards;
    }

    private static float InnDistance(EventsTestWorld world, Guid bot)
    {
        Player player = world.World.Bots.FindSession(bot)!.Player!;
        return Distance2D(new Vector3(player.X, player.Y, player.Z), Inn);
    }
}
