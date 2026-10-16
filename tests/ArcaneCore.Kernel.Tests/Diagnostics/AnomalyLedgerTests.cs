using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Diagnostics;

/// <summary>The anomaly ledger: keys, throttled logging, bounds, lazy checks, captures, and failure isolation.</summary>
public sealed class AnomalyLedgerTests
{
    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static (AnomalyLedger Ledger, ManualTime Time) Create(int maxKeys = 8, int logLimit = 2)
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        return (new AnomalyLedger(new AnomalyLedgerLimits(maxKeys, logLimit), time), time);
    }

    [Fact]
    public void Report_CountsUnderTheNormalisedKey_AndKeepsTheLastSubjectAndDetail()
    {
        (AnomalyLedger ledger, _) = Create();
        ledger.Report(" Bot Elevator ", "Board Missed", "bot A", "first");
        AnomalyRecording second = ledger.Report("bot-elevator", "board-missed", "bot B", "second");

        Assert.Equal("bot-elevator/board-missed", second.Key);
        Assert.Equal(2, second.Count);
        AnomalySnapshot snap = Assert.Single(ledger.Snapshot());
        Assert.Equal("bot-elevator", snap.Subsystem);
        Assert.Equal("board-missed", snap.Code);
        Assert.Equal("bot B", snap.LastSubject);
        Assert.Equal("second", snap.LastDetail);
        Assert.Equal(2, snap.DistinctSubjects);
        Assert.Equal(2, ledger.Total);
        Assert.Equal(2, ledger.CountOf("Bot Elevator/Board Missed"));
    }

    [Fact]
    public void Logging_IsThrottled_AfterTheLimit_ThenOncePerMinuteWithTheHeldBackCount()
    {
        (AnomalyLedger ledger, ManualTime time) = Create(logLimit: 2);
        Assert.True(ledger.Report("s", "c", "x", "").ShouldLog);
        Assert.True(ledger.Report("s", "c", "x", "").ShouldLog);
        Assert.False(ledger.Report("s", "c", "x", "").ShouldLog);
        Assert.False(ledger.Report("s", "c", "x", "").ShouldLog);
        time.Advance(TimeSpan.FromMinutes(1));
        AnomalyRecording after = ledger.Report("s", "c", "x", "");
        Assert.True(after.ShouldLog);
        Assert.Equal(2, after.Suppressed);
        Assert.Equal(5, after.Count);
    }

    [Fact]
    public void KeysBeyondTheCap_AreCountedInTheOverflowBucket_NotDropped()
    {
        (AnomalyLedger ledger, _) = Create(maxKeys: 2);
        ledger.Report("a", "1", "", "");
        ledger.Report("a", "2", "", "");
        AnomalyRecording third = ledger.Report("a", "3", "", "");

        Assert.Equal(AnomalyLedger.OverflowKey, third.Key);
        Assert.Equal(2, ledger.Distinct);
        Assert.Equal(3, ledger.Total);
        Assert.Equal(1, ledger.CountOf(AnomalyLedger.OverflowKey));
    }

    [Fact]
    public void Expect_ReportsOnlyOnFailure_AndRunsTheDetailFactoryOnlyThen()
    {
        (AnomalyLedger ledger, _) = Create();
        int built = 0;
        Assert.True(ledger.Expect(true, "s", "c", "x", () => { built++; return "never"; }));
        Assert.Equal(0, built);
        Assert.Equal(0, ledger.Total);

        Assert.False(ledger.Expect(false, "s", "c", "x", () => { built++; return "built"; }));
        Assert.Equal(1, built);
        Assert.Equal("built", ledger.Find("s/c")!.LastDetail);
    }

    [Fact]
    public void Capture_SeesEveryReport_EvenThrottledOnes_AndOnlyThoseMatchingItsFilter()
    {
        (AnomalyLedger ledger, _) = Create(logLimit: 0);
        using AnomalyCapture capture = ledger.Capture(n => n.Subject == "mine");
        ledger.Report("s", "c", "mine", "1");
        ledger.Report("s", "c", "other", "2");
        ledger.Report("s", "c", "mine", "3");

        Assert.Equal(2, capture.Count("s/c"));
        Assert.True(capture.Any("S/C"));
        Assert.False(capture.Any("s/other"));
        capture.Dispose();
        ledger.Report("s", "c", "mine", "4");
        Assert.Equal(2, capture.Notices.Count);
    }

    [Fact]
    public void HandlersAndLoggersThatThrow_NeverEscapeAReport()
    {
        (AnomalyLedger ledger, _) = Create();
        ledger.Configure(new ThrowingLogger(), logLimit: 10);
        ledger.AnomalyNoticed += _ => throw new InvalidOperationException("handler");
        ledger.Reported += _ => throw new InvalidOperationException("handler");

        AnomalyRecording recording = ledger.Report("s", "c", "x", "y", AnomalySeverity.Error);
        Assert.Equal(1, recording.Count);
        Assert.Equal(AnomalySeverity.Error, ledger.Find("s/c")!.Severity);
    }

    [Fact]
    public void LongText_IsTruncated_AndEmptyNamesBecomeUnknown()
    {
        (AnomalyLedger ledger, _) = Create();
        ledger.Report("", "  ", new string('s', 1000), new string('d', 1000));
        AnomalySnapshot snap = ledger.Find("unknown/unknown")!;
        Assert.Equal(AnomalyLedger.MaxTextLength, snap.LastSubject.Length);
        Assert.Equal(AnomalyLedger.MaxTextLength, snap.LastDetail.Length);
    }

    [Fact]
    public void Reset_ForgetsEveryKey()
    {
        (AnomalyLedger ledger, _) = Create();
        ledger.Report("s", "c", "x", "");
        ledger.Reset();
        Assert.Empty(ledger.Snapshot());
        Assert.Equal(0, ledger.Total);
        Assert.Null(ledger.Find("s/c"));
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("logger");
    }
}
