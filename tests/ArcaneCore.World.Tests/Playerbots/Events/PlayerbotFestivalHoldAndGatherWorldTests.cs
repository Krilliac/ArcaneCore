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
/// Idle bots held at the inn, sent to a festival anchor, and rehomed. Split from the former <c>PlayerbotFestivalWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotFestivalWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotFestivalHoldAndGatherWorldTests : PlayerbotFestivalWorldTestBase
{
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

    [Fact]
    public async Task ABotRehomedFromASecondaryTownInItsExploreWindow_ToACapital_GathersThere()
    {
        await using EventsTestWorld world = await EventsTestWorld.StartAsync([Reveler()], Quick);
        Guid bot = await world.AddHomedBotAsync("Rehomebot", TownStay.Secondary);
        await world.StartEventAsync(WinterVeil);
        Assert.True(await world.RunUntilAsync(bot, 60_000, () => world.World.Bots.FindBrain(bot)!.HoldsEventSpot), "no place in the linger window");
        Assert.True(await world.RunUntilAsync(bot, 150_000, () => !world.World.Bots.FindBrain(bot)!.HoldsEventSpot), "the explore window never began");
        await world.RunForAsync(bot, 5_000);

        await world.RehomeAsync(bot, TownStay.Capital);

        Assert.True(await world.RunUntilAsync(bot, 120_000, () => world.World.Bots.FindBrain(bot)!.HoldsEventSpot), "the re-homed bot took no place in its capital");
    }

    [Fact]
    public async Task ARehomedBot_ReleasesTheFestivalPlaceOfItsOldHome()
    {
        await using EventsTestWorld world = await EventsTestWorld.StartAsync([Reveler()], Quick);
        Guid bot = await world.AddHomedBotAsync("Movebot");
        await world.StartEventAsync(WinterVeil);
        Assert.True(await world.RunUntilAsync(bot, 120_000, () => world.World.Bots.FindBrain(bot)!.HoldsEventSpot), "no place taken");

        await world.RehomeAsync(bot, TownStay.Secondary);

        Assert.False(await world.World.Host.OnWorldAsync(() => world.World.Bots.FindBrain(bot)!.HoldsEventSpot), "the old home's place outlived the Home change");
    }

    [Fact]
    public async Task ASpotOnTheFarSideOfTheNpc_IsReachedWithin3Yards_AndTheHeldBotIsAtLeast5YardsFromTheNpc()
    {
        // The NPC 10 yards from the inn: a bot whose spot lies beyond the NPC walks past it and arrives from the NPC's side.
        await using EventsTestWorld world = await EventsTestWorld.StartAsync([Reveler(dx: 10f)], Quick);
        var anchor = new Vector3(StartXOf(10f), StartYOf(0f), 0f);
        var bots = new List<Guid>();
        for (int i = 0; i < 6; i++) bots.Add(await world.AddHomedBotAsync("Farbot" + (char)('a' + i)));
        await world.StartEventAsync(WinterVeil);

        await world.RunForAsync(bots[0], 150_000);

        int held = 0, farSide = 0;
        foreach (Guid bot in bots)
        {
            (bool holds, Vector3 spot) = await world.World.Host.OnWorldAsync(() =>
            {
                PlayerbotFestivalClaim? claim = world.World.Bots.FindBrain(bot)!.EventClaim;
                return (claim is not null, claim?.Spot ?? default);
            });
            if (!holds) continue;
            Vector3 at = await world.PositionAsync(bot);
            if (Distance2D(at, spot) > PlayerbotFestivalRules.SpotArriveYards + 0.1f) continue; // still on its way, or a bot whose spot it gave up
            held++;
            if (Distance2D(Inn, spot) > Distance2D(Inn, anchor)) farSide++;
            Assert.True(Distance2D(at, anchor) >= PlayerbotFestivalRules.AnchorClearYards - 0.05f, $"{Distance2D(at, anchor):F2} yards from the NPC");
        }

        Assert.True(held > 0, "no bot arrived at a spot");
        Assert.True(farSide > 0, "no bot had a spot beyond the NPC, so the approach from the NPC's side was not exercised");
    }
}
