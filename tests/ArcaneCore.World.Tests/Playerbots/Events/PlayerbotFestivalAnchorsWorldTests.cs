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
/// Which NPCs anchor, the gather cap, other floors, and a walk to the inn. Split from the former <c>PlayerbotFestivalWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotFestivalWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotFestivalAnchorsWorldTests : PlayerbotFestivalWorldTestBase
{
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
}
