using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Events;

/// <summary>A flavour store whose load is a delegate.</summary>
internal sealed class StubHolidayFlavourStore(Func<Task<IReadOnlyList<HolidayFlavourRecord>>> load) : IHolidayFlavourStore
{
    public Task<IReadOnlyList<HolidayFlavourRecord>> LoadAsync(CancellationToken cancellationToken = default) => load();
}

/// <summary>The festival flavour load of <see cref="ManagedPlayerbotFeature"/>: the no-row-0 warning, the load catch and the log of a failed load.</summary>
public sealed class HolidayFlavourLoadTests
{
    private sealed class CapturingLogger : ILogger<ManagedPlayerbotFeature>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private static ManagedPlayerbotFeature Build(CapturingLogger logger)
        => new(new ServiceCollection().BuildServiceProvider(), logger);

    [Fact]
    public async Task Load_WithoutRowZero_WarnsThatFestivalsRunAsPlainGathering()
    {
        var logger = new CapturingLogger();
        var rows = new List<HolidayFlavourRecord> { new(7, "k", "Seven", 1, 0, 0, true) };

        await Build(logger).LoadHolidayFlavoursAsync(new StubHolidayFlavourStore(() => Task.FromResult<IReadOnlyList<HolidayFlavourRecord>>(rows)), CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("no row for holiday id 0", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("could not be loaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Load_WithRowZero_LogsNothing()
    {
        var logger = new CapturingLogger();
        var rows = new List<HolidayFlavourRecord> { new(0, "k", "Default", 1, 0, 0, true) };

        await Build(logger).LoadHolidayFlavoursAsync(new StubHolidayFlavourStore(() => Task.FromResult<IReadOnlyList<HolidayFlavourRecord>>(rows)), CancellationToken.None);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Load_StoreFailure_IsCaughtAndLoggedAsNotLoaded()
    {
        var logger = new CapturingLogger();

        await Build(logger).LoadHolidayFlavoursAsync(new StubHolidayFlavourStore(() => throw new InvalidOperationException("boom")), CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("could not be loaded", StringComparison.Ordinal) && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Load_Cancelled_Propagates()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build(new CapturingLogger()).LoadHolidayFlavoursAsync(
            new StubHolidayFlavourStore(() => throw new OperationCanceledException(cancel.Token)), cancel.Token));
    }

    [Fact]
    public async Task Load_BoardRefreshFailure_LogsRefreshNotNotLoaded()
    {
        var logger = new CapturingLogger();
        var rows = new List<HolidayFlavourRecord> { new(0, "k", "Default", 1, 0, 0, true) };

        await Build(logger).LoadHolidayFlavoursAsync(new StubHolidayFlavourStore(() => Task.FromResult<IReadOnlyList<HolidayFlavourRecord>>(rows)), CancellationToken.None,
            (_, _) => throw new InvalidOperationException("world host failed"));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("could not be refreshed", StringComparison.Ordinal) && e.Exception is InvalidOperationException);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("could not be loaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Load_BoardRefreshCancelled_Propagates()
    {
        var rows = new List<HolidayFlavourRecord> { new(0, "k", "Default", 1, 0, 0, true) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build(new CapturingLogger()).LoadHolidayFlavoursAsync(
            new StubHolidayFlavourStore(() => Task.FromResult<IReadOnlyList<HolidayFlavourRecord>>(rows)), CancellationToken.None, (_, _) => throw new OperationCanceledException()));
    }
}

/// <summary>A flavour load on a running world: the event board is invalidated, so a festival that started before the load picks up the loaded flavour.</summary>
public sealed class HolidayFlavourReloadWorldTests : PlayerbotFestivalWorldTestBase
{
    [Fact]
    public async Task Load_OnAWorldHost_InvalidatesTheBoard_SoARunningFestivalTakesTheLoadedFlavour()
    {
        await using EventsTestWorld world = await EventsTestWorld.StartAsync([Reveler()], Quick);
        await world.StartEventAsync(EventsTestWorld.WinterVeil);
        var rows = new List<HolidayFlavourRecord> { new(EventsTestWorld.WinterVeilHoliday, "reloaded-key", "Reloaded Veil", 1, 0, 0, true) };

        (string Key, string Name, int Version) before = await world.World.Host.OnWorldAsync(() =>
        {
            PlayerbotWorldEventBoard board = world.World.Bots.EventBoard;
            PlayerbotFestival festival = board.FestivalOf(EventsTestWorld.WinterVeil) ?? throw new InvalidOperationException("the festival does not run");
            return (festival.Flavour.Key, festival.Flavour.Name, board.Version);
        });
        Assert.NotEqual("reloaded-key", before.Key);

        await world.World.Bots.LoadHolidayFlavoursAsync(new StubHolidayFlavourStore(() => Task.FromResult<IReadOnlyList<HolidayFlavourRecord>>(rows)), CancellationToken.None);

        (string Key, string Name, int Version) after = await world.World.Host.OnWorldAsync(() =>
        {
            PlayerbotWorldEventBoard board = world.World.Bots.EventBoard;
            PlayerbotFestival festival = board.FestivalOf(EventsTestWorld.WinterVeil) ?? throw new InvalidOperationException("the festival does not run");
            return (festival.Flavour.Key, festival.Flavour.Name, board.Version);
        });
        Assert.Equal("reloaded-key", after.Key);
        Assert.Equal("Reloaded Veil", after.Name);
        Assert.NotEqual(before.Version, after.Version);
    }
}
