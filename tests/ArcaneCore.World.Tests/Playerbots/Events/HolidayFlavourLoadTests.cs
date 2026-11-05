using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Tests.Playerbots.Events;

/// <summary>The festival flavour load of <see cref="ManagedPlayerbotFeature"/>: the no-row-0 warning, the load catch and the log of a failed load. Unproven until the integration gate.</summary>
public sealed class HolidayFlavourLoadTests
{
    private sealed class StubStore(Func<Task<IReadOnlyList<HolidayFlavourRecord>>> load) : IHolidayFlavourStore
    {
        public Task<IReadOnlyList<HolidayFlavourRecord>> LoadAsync(CancellationToken cancellationToken = default) => load();
    }

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

        await Build(logger).LoadHolidayFlavoursAsync(new StubStore(() => Task.FromResult<IReadOnlyList<HolidayFlavourRecord>>(rows)), CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("no row for holiday id 0", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("could not be loaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Load_WithRowZero_LogsNothing()
    {
        var logger = new CapturingLogger();
        var rows = new List<HolidayFlavourRecord> { new(0, "k", "Default", 1, 0, 0, true) };

        await Build(logger).LoadHolidayFlavoursAsync(new StubStore(() => Task.FromResult<IReadOnlyList<HolidayFlavourRecord>>(rows)), CancellationToken.None);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Load_StoreFailure_IsCaughtAndLoggedAsNotLoaded()
    {
        var logger = new CapturingLogger();

        await Build(logger).LoadHolidayFlavoursAsync(new StubStore(() => throw new InvalidOperationException("boom")), CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("could not be loaded", StringComparison.Ordinal) && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Load_Cancelled_Propagates()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Build(new CapturingLogger()).LoadHolidayFlavoursAsync(
            new StubStore(() => throw new OperationCanceledException(cancel.Token)), cancel.Token));
    }
}
