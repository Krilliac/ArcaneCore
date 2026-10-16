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

    /// <summary>
    /// The live log shows many unrelated keys at <c>[count=52 suppressed=0] rate 208.0/h last 15 min, 52.0/h last 1 h</c> soon after a start:
    /// each key logs its own first <c>FaultLogLimit</c> reports, so every busy key prints a count=52 line, and while all of a key's reports
    /// fall in the last 15 minutes its 15-minute rate is its count times four. Counts and rates are per key, not shared.
    /// </summary>
    [Fact]
    public void CountsAndRates_ArePerKey_AndTheFifteenMinuteRateIsFourTimesTheReportsInIt()
    {
        (AnomalyLedger ledger, ManualTime time) = Create(logLimit: 1000);
        AnomalyRecording busy = default;
        for (int i = 0; i < 52; i++)
        {
            busy = ledger.Report("creature", "under-map", "creature entry 40 spawn 1", "");
        }

        AnomalyRecording quiet = default;
        for (int i = 0; i < 3; i++)
        {
            quiet = ledger.Report("bot-motion", "airwalk", "bot A", "");
        }

        Assert.Equal(52, busy.Count);
        Assert.Equal(208d, busy.Timing!.Value.RatePerHour15Min);
        Assert.Equal(52d, busy.Timing!.Value.RatePerHour1Hour);
        Assert.Equal(3, quiet.Count);
        Assert.Equal(12d, quiet.Timing!.Value.RatePerHour15Min);
        Assert.Equal(3d, quiet.Timing!.Value.RatePerHour1Hour);

        time.Advance(TimeSpan.FromMinutes(30)); // the first 52 leave the 15-minute window, not the hour
        AnomalyRecording later = ledger.Report("creature", "under-map", "creature entry 40 spawn 1", "");
        Assert.Equal(53, later.Count);
        Assert.Equal(4d, later.Timing!.Value.RatePerHour15Min);
        Assert.Equal(53d, later.Timing!.Value.RatePerHour1Hour);
        Assert.Equal(56, ledger.Total);
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
