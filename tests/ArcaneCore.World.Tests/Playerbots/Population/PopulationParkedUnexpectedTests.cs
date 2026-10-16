using System.Reflection;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// <c>bot-population/parked-unexpected</c> is report-only (hardening wave H1): one report per episode of a population bot that stopped
/// outside the park pass, cleared when the bot runs again, and it never edits <c>_startedAt</c> / <c>_parkedAt</c>, which
/// <see cref="PopulationParking.RestartOrder"/> reads.
/// </summary>
public sealed class PopulationParkedUnexpectedTests
{
    private const string Key = "bot-population/parked-unexpected";

    private static Dictionary<Guid, long> Field(PlayerbotPopulationFeature feature, string name)
        => (Dictionary<Guid, long>)typeof(PlayerbotPopulationFeature).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(feature)!;

    private static async Task<Guid> TwoBotsAsync(PopulationTestHost world)
    {
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        await world.WaitForSettledAsync(2);
        return world.Members.Snapshot.Select(m => m.BotId).OrderBy(id => id).First();
    }

    private static async Task StopAsOperatorAsync(PopulationTestHost world, Guid bot)
    {
        Assert.True((await world.Bots.StopAsync(bot.ToString())).Success);
        await world.Host.WaitForWorldAsync(() => world.RunningCount == 1, "the operator stop landed");
    }

    [Fact]
    public async Task APopulationBot_StoppedOutsideThePark_IsReportedOncePerEpisode_AndTheMapsAreUntouched()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync();
        Guid bot = await TwoBotsAsync(world);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == Key && n.Subject.Contains(bot.ToString("D"), StringComparison.Ordinal));
        Assert.True(Field(world.Feature, "_startedAt").ContainsKey(bot));
        long startedAt = Field(world.Feature, "_startedAt")[bot];

        await StopAsOperatorAsync(world, bot);
        world.Population.TargetOnline = 1; // the stopped bot is not wanted back: it stays stopped across passes
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        world.Ticks.Set(200, 10);
        await world.PassAsync();

        Assert.Equal(1, capture.Count(Key));
        Assert.False(Field(world.Feature, "_parkedAt").ContainsKey(bot), "the report wrote _parkedAt: the restart order changed");
        Assert.True(Field(world.Feature, "_startedAt").TryGetValue(bot, out long still) && still == startedAt, "the report edited _startedAt");

        // It runs again (the target is back): the episode ends, and a second stop is a second episode.
        world.Population.TargetOnline = 2;
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        await world.WaitForSettledAsync(2);
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        Assert.Equal(1, capture.Count(Key));

        await StopAsOperatorAsync(world, bot);
        world.Population.TargetOnline = 1;
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        Assert.Equal(2, capture.Count(Key));
    }

    [Fact]
    public async Task AStoppedBot_WithoutAParkRecord_RestartsBeforeABotTheParkPassParkedEarlier()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync();
        Guid bot = await TwoBotsAsync(world);
        await StopAsOperatorAsync(world, bot);
        world.Population.TargetOnline = 1;
        world.Ticks.Set(200, 10);
        await world.PassAsync();

        // The bot has no _parkedAt entry, so RestartOrder ranks it as parked longest (before any bot with a recorded park time).
        Dictionary<Guid, long> parkedAt = Field(world.Feature, "_parkedAt");
        Guid other = Guid.NewGuid();
        parkedAt[other] = 1;
        PlayerbotPopulationMember member = world.Members.Snapshot.Single(m => m.BotId == bot);
        PlayerbotPopulationMember pretend = member with { BotId = other };
        Assert.Equal([bot, other], PopulationParking.RestartOrder([pretend, member], parkedAt).Select(m => m.BotId));
        parkedAt.Remove(other);
    }

    [Fact]
    public async Task ABotTheParkPassParked_IsSilent()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync();
        await TwoBotsAsync(world);
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureIn(world.Host.World, n => n.Key == Key);

        world.Ticks.Set(200, 80); // over the budget: the population parks one itself
        PopulationStatus status = await world.PassAsync();
        Assert.Contains("parked 1", status.Reason);
        await world.Host.WaitForWorldAsync(() => world.RunningCount == 1, "one bot parked");
        world.Ticks.Set(200, 80);
        await world.PassAsync();
        world.Ticks.Set(200, 80);
        await world.PassAsync();

        Assert.False(capture.Any(Key), capture.Describe());
    }

    [Fact]
    public async Task ARunningPopulation_IsSilent()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync();
        await TwoBotsAsync(world);
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureIn(world.Host.World, n => n.Key == Key);

        for (int i = 0; i < 3; i++)
        {
            world.Ticks.Set(200, 10);
            await world.PassAsync();
        }

        Assert.False(capture.Any(Key), capture.Describe());
    }
}
