using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// At shutdown the managed feature stops every bot and refuses every start and creation (<c>playerbots-disabled</c>) while the population
/// feature is still attached. The population must not read those stops as <c>bot-population/parked-unexpected</c>, must not run passes that
/// try to start or create bots, and must not run start-home sweeps. A stop outside a shutdown still reports.
/// </summary>
public sealed class PopulationShutdownTests
{
    private const string Key = "bot-population/parked-unexpected";

    private static async Task TwoBotsAsync(PopulationTestHost world)
    {
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        await world.WaitForSettledAsync(2);
    }

    [Fact]
    public async Task BotsStoppedByTheShutdown_AreNotParkedUnexpected_AndNoPassTriesToStartOrCreate()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync();
        await TwoBotsAsync(world);
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureIn(world.Host.World, n => n.Key == Key);
        int membersBefore = world.Members.Snapshot.Count;
        world.Population.TargetOnline = 5; // more wanted than running: an unguarded pass would start or create bots

        await world.Bots.ShutdownBeforeWorldStopAsync();
        Assert.Equal(0, world.RunningCount);
        for (int i = 0; i < 3; i++)
        {
            world.Ticks.Set(200, 10);
            await world.PassAsync();
        }

        Assert.False(capture.Any(Key), capture.Describe());
        Assert.Null(world.Feature.Status.LastError);
        Assert.Equal(0, world.RunningCount);
        Assert.Equal(membersBefore, world.Members.Snapshot.Count);
    }

    [Fact]
    public async Task AStopOutsideTheShutdown_IsStillParkedUnexpected()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync();
        await TwoBotsAsync(world);
        Guid bot = world.Members.Snapshot.Select(m => m.BotId).OrderBy(id => id).First();
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == Key && n.Subject.Contains(bot.ToString("D"), StringComparison.Ordinal));

        Assert.True((await world.Bots.StopAsync(bot.ToString())).Success);
        await world.Host.WaitForWorldAsync(() => world.RunningCount == 1, "the operator stop landed");
        world.Population.TargetOnline = 1;
        world.Ticks.Set(200, 10);
        await world.PassAsync();

        Assert.Equal(1, capture.Count(Key));
    }

    [Fact]
    public async Task APassInFlightWhenTheShutdownBegins_StaysSilent_AndStartsNothing()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync();
        await TwoBotsAsync(world);
        Guid parked = world.Members.Snapshot.Select(m => m.BotId).OrderBy(id => id).First();
        // One bot is stopped by the operator before the pass captures its states, so the in-flight pass sees a stopped, started-by-us member:
        // exactly what the parked-unexpected check reports outside a shutdown.
        Assert.True((await world.Bots.StopAsync(parked.ToString())).Success);
        await world.Host.WaitForWorldAsync(() => world.RunningCount == 1, "the operator stop landed");
        world.Population.TargetOnline = 5; // the pass would restart the stopped bot and create more
        using AnomalyCapture capture = PlayerbotAnomalies.CaptureIn(world.Host.World, n => n.Key == Key);

        TaskCompletionSource held = world.Members.HoldNextLoad();
        world.Ticks.Set(200, 10);
        Task pass = world.Feature.RunPassNowAsync();
        await held.Task.WaitAsync(TimeSpan.FromSeconds(30)); // the pass has captured the world and is blocked reading the membership

        await world.Bots.ShutdownBeforeWorldStopAsync();
        world.Members.ReleaseHeldLoad();
        await pass.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(capture.Any(Key), capture.Describe());
        Assert.Null(world.Feature.Status.LastError);
        Assert.Equal(0, world.RunningCount);
    }
}
