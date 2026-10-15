using System.Runtime.CompilerServices;
using ArcaneCore.Kernel.Diagnostics;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Diagnostics;

/// <summary>The fault and anomaly ledgers' timing detail: first/last seen, rates per hour, inter-arrival p50/p99, top subjects, recent distinct details, and the bounds on all of it.</summary>
public sealed class LedgerTimingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception Throw(string message)
    {
        try { throw new InvalidOperationException(message); } catch (Exception ex) { return ex; }
    }

    private static (AnomalyLedger Ledger, ManualTime Time) Anomalies()
    {
        var time = new ManualTime(T0);
        return (new AnomalyLedger(new AnomalyLedgerLimits(MaxKeys: 8, LogLimit: 2), time), time);
    }

    private static (FaultLedger Ledger, ManualTime Time) Faults()
    {
        var time = new ManualTime(T0);
        return (new FaultLedger(new FaultLedgerLimits(MaxSignatures: 8, LogLimit: 2), time), time);
    }

    // ---- anomalies ----

    [Fact]
    public void Anomaly_RatesCoverTheLast15MinutesAndTheLastHour_AndAgeOut()
    {
        (AnomalyLedger ledger, ManualTime time) = Anomalies();
        for (int i = 0; i < 6; i++)
        {
            ledger.Report("bot", "stuck", "bot A", "detail");
            if (i < 5) time.Advance(TimeSpan.FromMinutes(1));
        }

        AnomalySnapshot snap = Assert.Single(ledger.Snapshot());
        Assert.Equal(T0, snap.Timing.FirstSeen);
        Assert.Equal(T0.AddMinutes(5), snap.Timing.LastSeen);
        Assert.Equal(6, snap.Timing.Last15Min);
        Assert.Equal(6, snap.Timing.Last1Hour);
        Assert.Equal(24, snap.Timing.RatePerHour15Min);
        Assert.Equal(6, snap.Timing.RatePerHour1Hour);

        time.Advance(TimeSpan.FromMinutes(20)); // minute 25: the six reports are older than 15 minutes, not yet older than an hour
        snap = Assert.Single(ledger.Snapshot());
        Assert.Equal(0, snap.Timing.Last15Min);
        Assert.Equal(6, snap.Timing.Last1Hour);

        time.Advance(TimeSpan.FromMinutes(60));
        snap = Assert.Single(ledger.Snapshot());
        Assert.Equal(0, snap.Timing.Last1Hour);
        Assert.Equal(6, snap.Count); // the count is for life, the rate windows are not
    }

    [Fact]
    public void Anomaly_InterArrivalP50AndP99_AreNearestRankOverTheGaps()
    {
        (AnomalyLedger ledger, ManualTime time) = Anomalies();
        ledger.Report("bot", "stuck", "bot A", "d");
        Assert.Null(Assert.Single(ledger.Snapshot()).Timing.InterArrivalP50); // one report has no gap
        for (int i = 0; i < 100; i++)
        {
            time.Advance(i % 50 == 49 ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(1)); // 98 gaps of 1 s, 2 of 60 s
            ledger.Report("bot", "stuck", "bot A", "d");
        }

        LedgerTiming timing = Assert.Single(ledger.Snapshot()).Timing;
        Assert.Equal(TimeSpan.FromSeconds(1), timing.InterArrivalP50);
        Assert.Equal(TimeSpan.FromSeconds(60), timing.InterArrivalP99);
        Assert.Contains("gap p50 1 s p99 60 s", timing.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Anomaly_TopSubjects_CountEachSubjectAndRankThem()
    {
        (AnomalyLedger ledger, _) = Anomalies();
        for (int i = 0; i < 5; i++) ledger.Report("bot", "stuck", "bot A", "d");
        for (int i = 0; i < 3; i++) ledger.Report("bot", "stuck", "bot B", "d");
        ledger.Report("bot", "stuck", "bot C", "d");

        AnomalySnapshot snap = Assert.Single(ledger.Snapshot());
        Assert.Equal(3, snap.DistinctSubjects);
        Assert.Equal([new SubjectCount("bot A", 5), new SubjectCount("bot B", 3), new SubjectCount("bot C", 1)], snap.TopSubjects);
    }

    [Fact]
    public void Anomaly_RecentDetails_AreDistinctNewestFirstAndAtMostThree()
    {
        (AnomalyLedger ledger, _) = Anomalies();
        foreach (string detail in new[] { "d1", "d2", "d1", "d3", "d4" }) ledger.Report("bot", "stuck", "bot A", detail);

        Assert.Equal(["d4", "d3", "d1"], Assert.Single(ledger.Snapshot()).RecentDetails);
    }

    [Fact]
    public void Anomaly_MemoryStaysBounded_UnderManyDistinctSubjectsDetailsAndKeys()
    {
        (AnomalyLedger ledger, _) = Anomalies();
        for (int i = 0; i < 5_000; i++)
        {
            ledger.Report("bot", "stuck", "subject " + i, "detail " + i);
            if (i % 2 == 0) ledger.Report("bot", "stuck", "the heavy subject", "heavy");
        }

        AnomalySnapshot snap = Assert.Single(ledger.Snapshot());
        Assert.Equal(AnomalyLedger.MaxTrackedSubjects, snap.DistinctSubjects);
        Assert.True(snap.TopSubjects.Count <= AnomalyLedger.SnapshotTopSubjects);
        Assert.True(snap.RecentDetails.Count <= AnomalyLedger.RecentDetailCount);
        // Space-Saving never loses a heavy hitter: it stays first, with at least its real count.
        Assert.Equal("the heavy subject", snap.TopSubjects[0].Subject);
        Assert.True(snap.TopSubjects[0].Count >= 2_500);

        for (int i = 0; i < 40; i++) ledger.Report("sub" + i, "code", "s", "d"); // keys beyond the cap land in the one overflow bucket
        Assert.True(ledger.Snapshot().Count <= 8 + 1);
        Assert.Equal(AnomalyLedger.OverflowKey, ledger.Snapshot()[^1].Key);
    }

    [Fact]
    public void Anomaly_LedgerWideTiming_TracksFirstLastAndRates_AndResets()
    {
        (AnomalyLedger ledger, ManualTime time) = Anomalies();
        Assert.Null(ledger.FirstSeen);
        Assert.Equal(0, ledger.Timing().Last1Hour);
        ledger.Report("a", "x", "s", "d");
        time.Advance(TimeSpan.FromMinutes(3));
        ledger.Report("b", "y", "s", "d");

        LedgerTiming timing = ledger.Timing();
        Assert.Equal(T0, timing.FirstSeen);
        Assert.Equal(T0.AddMinutes(3), timing.LastSeen);
        Assert.Equal(2, timing.Last15Min);
        Assert.Equal(8, timing.RatePerHour15Min);

        ledger.Reset();
        Assert.Null(ledger.FirstSeen);
        Assert.Equal(0, ledger.Timing().Last1Hour);
    }

    [Fact]
    public void Anomaly_LogLine_KeepsItsOldPlaceholdersAndAppendsTheTiming()
    {
        (AnomalyLedger ledger, ManualTime time) = Anomalies();
        var logger = new CapturingLogger();
        ledger.Configure(logger, logLimit: 5);
        ledger.Report("bot", "stuck", "bot A", "first");
        time.Advance(TimeSpan.FromSeconds(30));
        ledger.Report("bot", "stuck", "bot A", "second");

        string line = logger.Entries[1];
        Assert.Contains("Anomaly bot/stuck bot A: second [count=2 suppressed=0]", line, StringComparison.Ordinal);
        Assert.Contains("rate 8.0/h last 15 min, 2.0/h last 1 h; gap p50 30 s p99 30 s", line, StringComparison.Ordinal);
    }

    // ---- faults ----

    [Fact]
    public void Fault_RatesGapsAndFirstLast_PerSignature()
    {
        (FaultLedger ledger, ManualTime time) = Faults();
        for (int i = 0; i < 4; i++)
        {
            ledger.Record("site", Throw("boom"));
            time.Advance(TimeSpan.FromMinutes(2));
        }

        FaultSnapshot snap = Assert.Single(ledger.Snapshot());
        Assert.Equal(T0, snap.Timing.FirstSeen);
        Assert.Equal(T0.AddMinutes(6), snap.Timing.LastSeen);
        Assert.Equal(4, snap.Timing.Last15Min);
        Assert.Equal(16, snap.Timing.RatePerHour15Min);
        Assert.Equal(TimeSpan.FromMinutes(2), snap.Timing.InterArrivalP50);
        Assert.Equal(TimeSpan.FromMinutes(2), snap.Timing.InterArrivalP99);

        time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, Assert.Single(ledger.Snapshot()).Timing.Last15Min);
    }

    [Fact]
    public void Fault_TopAndRecentMessages_AreDistinctRankedAndBounded()
    {
        (FaultLedger ledger, _) = Faults();
        for (int i = 0; i < 6; i++) ledger.Record("site", Throw("frequent message"));
        for (int i = 0; i < 2; i++) ledger.Record("site", Throw("rare message"));
        FaultSnapshot snap = Assert.Single(ledger.Snapshot());
        Assert.Equal([new SubjectCount("frequent message", 6), new SubjectCount("rare message", 2)], snap.TopMessages);
        Assert.Equal(["rare message", "frequent message"], snap.RecentMessages);

        for (int i = 0; i < 3_000; i++)
        {
            ledger.Record("site", Throw("unique " + i));
            if (i % 2 == 0) ledger.Record("site", Throw("frequent message"));
        }

        snap = Assert.Single(ledger.Snapshot());
        Assert.True(snap.TopMessages.Count <= FaultLedger.SnapshotTopMessages);
        Assert.True(snap.RecentMessages.Count <= FaultLedger.RecentMessageCount);
        Assert.Equal("frequent message", snap.TopMessages[0].Subject);
        Assert.True(snap.TopMessages[0].Count >= 1_500);
    }

    [Fact]
    public void Fault_AStormOfOneMessage_IsTalliedOncePerOccurrence_AndAnInterleavedMessageStillCounts()
    {
        (FaultLedger ledger, _) = Faults();
        for (int i = 0; i < 100; i++) ledger.Record("site", Throw("same message"));
        ledger.Record("site", Throw("other message"));
        for (int i = 0; i < 3; i++) ledger.Record("site", Throw("same message"));

        FaultSnapshot snap = Assert.Single(ledger.Snapshot());
        Assert.Equal([new SubjectCount("same message", 103), new SubjectCount("other message", 1)], snap.TopMessages);
        Assert.Equal(["same message", "other message"], snap.RecentMessages);
    }

    [Fact]
    public void Anomaly_ManyNewSubjectsAtCapacity_KeepTheHeavySubjectOnTop_AndTheTopListSorted()
    {
        (AnomalyLedger ledger, _) = Anomalies();
        for (int i = 0; i < 2_000; i++)
        {
            ledger.Report("bot", "stuck", "churn " + i, "d");
            if (i % 4 == 0) ledger.Report("bot", "stuck", "heavy subject", "d");
            if (i % 40 == 0) ledger.Report("bot", "stuck", "medium subject", "d");
        }

        AnomalySnapshot snap = Assert.Single(ledger.Snapshot());
        Assert.Equal("heavy subject", snap.TopSubjects[0].Subject);
        Assert.True(snap.TopSubjects[0].Count >= 500);
        Assert.Equal(AnomalyLedger.SnapshotTopSubjects, snap.TopSubjects.Count);
        for (int i = 1; i < snap.TopSubjects.Count; i++)
        {
            Assert.True(snap.TopSubjects[i - 1].Count >= snap.TopSubjects[i].Count);
        }
    }

    [Fact]
    public void Fault_LongAndMultiLineMessages_AreCutToOneLine()
    {
        (FaultLedger ledger, _) = Faults();
        ledger.Record("site", Throw("line one\nline two " + new string('x', 400)));

        FaultSnapshot snap = Assert.Single(ledger.Snapshot());
        string top = Assert.Single(snap.TopMessages).Subject;
        Assert.DoesNotContain('\n', top);
        Assert.True(top.Length <= 160);
    }

    [Fact]
    public void Fault_SignatureCap_StillBoundsTheLedger_WithTheNewState()
    {
        (FaultLedger ledger, _) = Faults();
        for (int i = 0; i < 40; i++) ledger.Record("site" + i, Throw("m"));

        Assert.True(ledger.Snapshot().Count <= 8 + 1);
        Assert.Equal(40, ledger.Total);
    }

    [Fact]
    public void Fault_LedgerWideTiming_AndTheLogLine()
    {
        (FaultLedger ledger, ManualTime time) = Faults();
        Assert.Null(ledger.FirstSeen);
        var logger = new CapturingLogger();
        ledger.Log(logger, Microsoft.Extensions.Logging.LogLevel.Error, "site", Throw("boom"), "it failed");
        time.Advance(TimeSpan.FromMinutes(1));
        ledger.Record("other", Throw("again"));

        Assert.Equal(T0, ledger.FirstSeen);
        Assert.Equal(T0.AddMinutes(1), ledger.Timing().LastSeen);
        Assert.Equal(2, ledger.Timing().Last1Hour);
        string entry = Assert.Single(logger.Entries);
        Assert.Contains("it failed [fault=", entry, StringComparison.Ordinal); // the old text first
        Assert.Contains(" count=1 stack=", entry, StringComparison.Ordinal);
        Assert.Contains("rate 4.0/h last 15 min, 1.0/h last 1 h", entry, StringComparison.Ordinal);

        ledger.Reset();
        Assert.Null(ledger.FirstSeen);
        Assert.Equal(0, ledger.Timing().Last1Hour);
    }

    [Fact]
    public void LedgerTiming_FormatsSpansInTheUnitThatReadsBest()
    {
        Assert.Equal("250 ms", LedgerTiming.FormatSpan(TimeSpan.FromMilliseconds(250)));
        Assert.Equal("12 s", LedgerTiming.FormatSpan(TimeSpan.FromSeconds(12)));
        Assert.Equal("5.0 min", LedgerTiming.FormatSpan(TimeSpan.FromMinutes(5)));
        Assert.Equal("3.0 h", LedgerTiming.FormatSpan(TimeSpan.FromHours(3)));
    }
}
