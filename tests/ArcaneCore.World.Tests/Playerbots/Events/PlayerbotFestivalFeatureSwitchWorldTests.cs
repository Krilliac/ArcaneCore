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
/// The festival feature switched off, and on again live. Split from the former <c>PlayerbotFestivalWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotFestivalWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotFestivalFeatureSwitchWorldTests : PlayerbotFestivalWorldTestBase
{
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
}
