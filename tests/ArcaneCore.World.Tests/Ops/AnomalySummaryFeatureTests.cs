using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Ops.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

public sealed class AnomalySummaryFeatureTests
{
    [Fact]
    public void RunSummaryPass_SkipsLoggingWhenTotalDeltaIsZero()
    {
        var ledger = new AnomalyLedger();
        var logger = new CapturingLogger<AnomalySummaryFeature>();
        var feature = new AnomalySummaryFeature(logger, ledger);

        // Empty ledger: delta is 0, nothing logged.
        Assert.Equal(0, feature.RunSummaryPass());
        Assert.Empty(logger.Entries);

        ledger.Report("bot-elevator", "board-missed", "bot Thrall (guid 1)", "missed car", AnomalySeverity.Warning);
        Assert.Equal(1, feature.RunSummaryPass());
        Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, logger.Entries[0].Level);

        // Second pass with no new reports: delta is 0, no new log entry.
        logger.Entries.Clear();
        Assert.Equal(0, feature.RunSummaryPass());
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void RunSummaryPass_LogsTopTenKeysByCountDelta()
    {
        var ledger = new AnomalyLedger();
        var logger = new CapturingLogger<AnomalySummaryFeature>();
        var feature = new AnomalySummaryFeature(logger, ledger);

        for (int i = 1; i <= 12; i++)
        {
            string code = $"code-{i:D2}";
            for (int c = 0; c < i; c++)
            {
                ledger.Report("bot-nav", code, $"bot {i}", "stuck", AnomalySeverity.Warning);
            }
        }

        long delta = feature.RunSummaryPass();
        Assert.Equal(78, delta);

        LogEntry info = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, info.Level);
        Assert.Contains("bot-nav/code-12=+12", info.Message, StringComparison.Ordinal);
        Assert.Contains("bot-nav/code-03=+3", info.Message, StringComparison.Ordinal);
        // Only top 10 keys are included in the line; code-01 and code-02 are omitted.
        Assert.DoesNotContain("bot-nav/code-02=", info.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("bot-nav/code-01=", info.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSummaryPass_LogsWarningWhenKeyFirstAppearsWithErrorSeverity_OncePerKey()
    {
        var ledger = new AnomalyLedger();
        var logger = new CapturingLogger<AnomalySummaryFeature>();
        var feature = new AnomalySummaryFeature(logger, ledger);

        ledger.Report("movement", "below-world", "bot Sylvanas (guid 7)", "z=-5000", AnomalySeverity.Error);

        feature.RunSummaryPass();
        Assert.Equal(2, logger.Entries.Count);
        Assert.Equal(LogLevel.Warning, logger.Entries[0].Level);
        Assert.Contains("movement/below-world", logger.Entries[0].Message, StringComparison.Ordinal);
        Assert.Equal(LogLevel.Information, logger.Entries[1].Level);

        // Subsequent reports on the same Error key do not re-trigger the first-seen Warning.
        logger.Entries.Clear();
        ledger.Report("movement", "below-world", "bot Sylvanas (guid 7)", "z=-5100", AnomalySeverity.Error);
        feature.RunSummaryPass();
        LogEntry entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
    }

    [Fact]
    public void OnTick_AccumulatesFiveMinuteWindowBeforeRunningSummaryPass()
    {
        var ledger = new AnomalyLedger();
        var logger = new CapturingLogger<AnomalySummaryFeature>();
        var feature = new AnomalySummaryFeature(logger, ledger);

        ledger.Report("bot-combat", "leash-loop", "bot Garrosh (guid 2)", "evaded 3x", AnomalySeverity.Warning);

        feature.OnTick(AnomalySummaryFeature.DefaultSummaryIntervalMs - 1);
        Assert.Empty(logger.Entries);

        feature.OnTick(1);
        LogEntry info = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, info.Level);
        Assert.Contains("bot-combat/leash-loop=+1", info.Message, StringComparison.Ordinal);
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
