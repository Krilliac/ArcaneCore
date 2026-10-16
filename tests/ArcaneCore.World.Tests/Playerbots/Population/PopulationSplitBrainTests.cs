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
    /// <summary>A parked bot the population owns and would restart on its first pass.</summary>
    private static async Task<Guid> OwnedParkedBotAsync(PopulationTestHost world, string name)
    {
        PlayerbotOperationResult made = await world.Bots.CreateAsync(name, 1, 1);
        Guid id = Assert.IsType<Guid>(made.BotId);
        ManagedPlayerbot record = Assert.IsType<ManagedPlayerbot>(world.Owners.Peek(id));
        world.Members.Seed(new PlayerbotPopulationMember(id, record.CharacterId, PopulationTestHost.Elwynn, 0, 100, 5000));
        return id;
    }

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
        Guid id = await OwnedParkedBotAsync(world, "Parkedone");
        world.Ticks.Set(200, 10);
        await world.Bots.ForceSplitBrainForTests();
        // Only this test's bot: the ledger is shared with tests running in parallel.
        using AnomalyCapture anomalies = AnomalyLedger.Shared.Capture(n => n.Subject.Contains(id.ToString("D"), StringComparison.Ordinal));
        PlayerbotPopulationMember[] members = [.. world.Members.Snapshot];
        int rows = world.Owners.RowsWritten;
        int warned = warnings.Count + managedWarnings.Count;

        for (int second = 0; second < 8; second++) await world.Host.World.AdvanceClockAsync(1_000); // four intervals of 2 s

        Assert.Equal(0, world.Feature.PassesStarted); // the timer started no pass: a positive signal, not a wait
        Assert.Equal(0, world.Feature.Status.Passes);
        Assert.Equal(0, world.RunningCount);
        Assert.Empty(anomalies.Notices);
        Assert.Equal(warned, warnings.Count + managedWarnings.Count);
        Assert.Equal(members, world.Members.Snapshot);
        Assert.Equal(rows, world.Owners.RowsWritten);
        Assert.NotNull(world.Feature.StartRush(1, 1)); // a rush is refused too
    }

    [Fact]
    public async Task AfterAClear_ThePopulationRestartsItsMembers_AndNoOtherBotStarts()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1);
        Guid member = await OwnedParkedBotAsync(world, "Memberone");
        PlayerbotOperationResult other = await world.Bots.CreateAsync("Otherone", 1, 1); // registered, not a population member
        Guid operatorBot = Assert.IsType<Guid>(other.BotId);
        world.Ticks.Set(200, 10);
        await world.Bots.ForceSplitBrainForTests();
        Assert.Equal("playerbot-split-brain", (await world.Bots.StartAsync(member.ToString())).Code);

        Assert.True((await world.Bots.ClearSplitBrainAsync()).WasSet);
        PopulationStatus status = await world.PassAsync();

        Assert.Contains("started 1", status.Reason);
        await world.WaitForSettledAsync(1);
        Assert.Equal(ManagedPlayerbotState.Running, world.Owners.Peek(member)!.State);
        Assert.NotEqual(ManagedPlayerbotState.Running, world.Owners.Peek(operatorBot)!.State);
    }

    [Fact]
    public async Task APassInFlightWhenTheLatchIsSet_WritesNoMemberRow_AndEndsQuietly()
    {
        var warnings = new WarningCounter<PlayerbotPopulationFeature>();
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 0,
            configureServices: s => s.AddSingleton<ILogger<PlayerbotPopulationFeature>>(warnings));
        // A member row whose bot is gone: the pass deletes it, which is a member-row write.
        var orphan = new PlayerbotPopulationMember(Guid.NewGuid(), 4242, PopulationTestHost.Elwynn, 0, 100, 100);
        world.Members.Seed(orphan);
        world.Ticks.Set(200, 10);
        TaskCompletionSource loadHeld = world.Members.HoldNextLoad();
        Task pass = world.Feature.RunPassNowAsync();
        await loadHeld.Task.WaitAsync(TimeSpan.FromSeconds(30)); // the pass is in flight, before its first write
        int warned = warnings.Count;

        await world.Bots.ForceSplitBrainForTests();
        world.Members.ReleaseHeldLoad();
        await pass.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains(orphan, world.Members.Snapshot); // the stale delete never reached the store
        Assert.Equal(0, world.Feature.Status.Passes); // the pass ended without publishing
        Assert.Equal(warned, warnings.Count);
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
