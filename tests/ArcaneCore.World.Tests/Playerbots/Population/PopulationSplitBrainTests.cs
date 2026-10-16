using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>While this world is in a split brain (another world owns the playerbot rows) the population manager stands still: no start attempt, warning, anomaly or member-row write.</summary>
public sealed class PopulationSplitBrainTests
{
    [Fact]
    public async Task APopulationThatWouldRestartABot_StandsStill_WhileSplitBrainIsLatched()
    {
        var warnings = new WarningCounter<PlayerbotPopulationFeature>();
        var managedWarnings = new WarningCounter<ManagedPlayerbotFeature>();
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.Population.TargetOnline = 1;
            o.Population.IntervalSeconds = 2;
        }, manualClock: true, configureServices: s =>
        {
            s.AddSingleton<ILogger<PlayerbotPopulationFeature>>(warnings);
            s.AddSingleton<ILogger<ManagedPlayerbotFeature>>(managedWarnings);
        });
        // A parked bot the population owns and would restart on its first pass.
        PlayerbotOperationResult made = await world.Bots.CreateAsync("Parkedone", 1, 1);
        Guid id = Assert.IsType<Guid>(made.BotId);
        ManagedPlayerbot record = Assert.IsType<ManagedPlayerbot>(world.Owners.Peek(id));
        world.Members.Seed(new PlayerbotPopulationMember(id, record.CharacterId, PopulationTestHost.Elwynn, 0, 100, 5000));
        world.Ticks.Set(200, 10);
        await world.Bots.ForceSplitBrainForTests();
        using AnomalyCapture anomalies = AnomalyLedger.Shared.Capture(n => n.Key.StartsWith("bot-population/", StringComparison.Ordinal));
        PlayerbotPopulationMember[] members = [.. world.Members.Snapshot];
        int rows = world.Owners.RowsWritten;
        int warned = warnings.Count + managedWarnings.Count;

        for (int second = 0; second < 8; second++) await world.Host.World.AdvanceClockAsync(1_000); // four intervals of 2 s
        await Task.Delay(500); // a pass that was wrongly started would have settled by now

        Assert.Equal(0, world.Feature.Status.Passes);
        Assert.Equal(0, world.RunningCount);
        Assert.Empty(anomalies.Notices);
        Assert.Equal(warned, warnings.Count + managedWarnings.Count);
        Assert.Equal(members, world.Members.Snapshot);
        Assert.Equal(rows, world.Owners.RowsWritten);
        Assert.NotNull(world.Feature.StartRush(1, 1)); // a rush is refused too
    }

    private sealed class WarningCounter<T> : ILogger<T>
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Interlocked.Increment(ref _count);
        }
    }
}
