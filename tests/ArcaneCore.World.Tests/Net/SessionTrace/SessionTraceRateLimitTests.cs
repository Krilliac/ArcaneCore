using ArcaneCore.Kernel.Accounts;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Net.SessionTrace;
using Xunit;
using Trace = global::ArcaneCore.World.Net.SessionTrace.SessionTrace;

namespace ArcaneCore.World.Tests.Net.SessionTrace;

/// <summary>
/// The per-session token bucket (<see cref="TraceRateLimiter"/>) and its use by a trace: a flood is capped at the burst plus one second's
/// refill, every refused line is counted, and the next clock second reports the drops once, bypassing the bucket.
/// All times are fake; the limiter is fed timestamps, never the wall clock.
/// </summary>
public sealed class SessionTraceRateLimitTests
{
    private const long TicksPerSecond = 1000; // the fake clock's frequency: 1 tick = 1 ms
    private const long Start = 5_000_000;

    private static int Drain(TraceRateLimiter limiter, long now, int attempts = 10_000)
    {
        int admitted = 0;
        for (int i = 0; i < attempts; i++)
        {
            if (limiter.TryTake(now)) admitted++;
        }

        return admitted;
    }

    [Fact]
    public void Limiter_AdmitsNoMoreThanTheBurstPlusOneSecondOfRefill_WithinOneInstant()
    {
        var limiter = new TraceRateLimiter(50, 200, Start, TicksPerSecond);
        int admitted = Drain(limiter, Start);
        Assert.InRange(admitted, 200, 250); // the spec's bound: BurstLines + LinesPerSecond
        Assert.False(limiter.TryTake(Start));
    }

    [Fact]
    public void Limiter_RefillsAtLinesPerSecond_AfterItIsDrained()
    {
        var limiter = new TraceRateLimiter(50, 200, Start, TicksPerSecond);
        Drain(limiter, Start);

        Assert.InRange(Drain(limiter, Start + TicksPerSecond), 49, 51); // one second later: one second of lines
        Assert.InRange(Drain(limiter, Start + (TicksPerSecond * 11 / 10)), 4, 6); // 100 ms more: 5 lines
        Assert.False(limiter.TryTake(Start + (TicksPerSecond * 11 / 10)));
    }

    [Fact]
    public void Limiter_NeverBanksMoreThanTheBurst_AfterALongIdle()
    {
        var limiter = new TraceRateLimiter(50, 200, Start, TicksPerSecond);
        Drain(limiter, Start);

        Assert.Equal(200, Drain(limiter, Start + (3600 * TicksPerSecond)));
    }

    [Fact]
    public void Limiter_IgnoresATimestampThatGoesBackwards()
    {
        var limiter = new TraceRateLimiter(50, 200, Start, TicksPerSecond);
        Drain(limiter, Start + TicksPerSecond);

        Assert.False(limiter.TryTake(Start)); // earlier than the last decision: no refill, no exception
        Assert.False(limiter.TryTake(Start - TicksPerSecond));
    }

    [Fact]
    public void Limiter_HonoursTheTimerFrequencyItIsGiven()
    {
        // Stopwatch-sized ticks: 10 MHz. One second later is 10,000,000 ticks, not 1,000.
        const long Frequency = 10_000_000;
        var limiter = new TraceRateLimiter(50, 200, Start, Frequency);
        Drain(limiter, Start);

        Assert.False(limiter.TryTake(Start + 1000)); // 0.1 ms: not a token yet
        Assert.InRange(Drain(limiter, Start + Frequency), 49, 51);
    }

    [Fact]
    public async Task ATrace_AdmitsNoMoreThanTheBucketAllows_CountsTheRest_AndTheNextSecondReportsTheDropsOnce()
    {
        var logs = new CapturingLoggerProvider();
        var clock = new FakeTimeProvider();
        await using WorldTestHost host = SessionTraceTestKit.Start(clock, logs);
        await using WorldTestClient gm = await host.EnterWorldAsync("STRL0", "Ratelimitgm", AccountSecurity.GameMaster);
        SessionTraceFeature feature = SessionTraceTestKit.FeatureOf(host);
        uint low = await SessionTraceTestKit.LowGuidAsync(host, "Ratelimitgm");
        await gm.CollectAsync();
        Assert.StartsWith("Session trace on for Ratelimitgm", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session on"), StringComparison.Ordinal);
        Trace trace = await host.OnWorldAsync(() => Assert.Single(feature.Active));

        // 10,000 lines in one fake second (the clock does not move).
        const int Total = 10_000;
        long linesBefore = trace.Lines, droppedBefore = trace.Dropped;
        int admitted = 0;
        for (int i = 0; i < Total; i++)
        {
            if (trace.Write("probe", "n=" + i)) admitted++;
        }

        SessionTraceOptions options = feature.Options;
        Assert.InRange(admitted, 1, options.BurstLines + options.LinesPerSecond);
        Assert.Equal(admitted, SessionTraceTestKit.Lines(logs).Count(l => l.Event == "probe"));
        Assert.Equal(admitted, trace.Lines - linesBefore);
        long dropped = trace.Dropped - droppedBefore;
        Assert.Equal(Total - admitted, dropped);
        Assert.True(dropped > 0);
        Assert.DoesNotContain(SessionTraceTestKit.Lines(logs), l => l.Event == "rate-limit"); // reported on the tick, not inline

        // The next clock second: the world tick writes the report, which is not itself refused by the empty bucket.
        SessionTraceTestKit.Tick(clock);
        TraceLine report = await SessionTraceTestKit.WaitForLineAsync(logs, l => l.Guid == low && l.Event == "rate-limit", "the rate-limit report");
        Assert.Equal($"dropped={dropped} total-dropped={trace.Dropped}", report.Detail);
        Assert.Equal(trace.Dropped, droppedBefore + dropped); // the report is not a drop

        // Nothing dropped since: another second writes no second report.
        SessionTraceTestKit.Tick(clock);
        await Task.Delay(100);
        Assert.Single(SessionTraceTestKit.Lines(logs), l => l.Event == "rate-limit");
    }

    [Fact]
    public async Task ATrace_WrittenFromManyThreads_KeepsEveryLineAccountedFor()
    {
        var logs = new CapturingLoggerProvider();
        await using WorldTestHost host = SessionTraceTestKit.Start(new FakeTimeProvider(), logs);
        await using WorldTestClient gm = await host.EnterWorldAsync("STRL1", "Threadsgm", AccountSecurity.GameMaster);
        SessionTraceFeature feature = SessionTraceTestKit.FeatureOf(host);
        await gm.CollectAsync();
        Assert.StartsWith("Session trace on for Threadsgm", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session on"), StringComparison.Ordinal);
        Trace trace = await host.OnWorldAsync(() => Assert.Single(feature.Active));
        long linesBefore = trace.Lines, droppedBefore = trace.Dropped;

        int admitted = 0;
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            for (int i = 0; i < 2_500; i++)
            {
                if (trace.Write("probe", "n=" + i)) Interlocked.Increment(ref admitted);
            }
        });

        SessionTraceOptions options = feature.Options;
        Assert.InRange(admitted, 1, options.BurstLines + options.LinesPerSecond);
        Assert.Equal(admitted, trace.Lines - linesBefore);
        Assert.Equal(20_000 - admitted, trace.Dropped - droppedBefore);
        Assert.Equal(admitted, SessionTraceTestKit.Lines(logs).Count(l => l.Event == "probe"));
    }

    [Fact]
    public async Task Configuration_BindsTheBucket_SoASmallerBurstAdmitsFewerLines()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Diagnostics:SessionTrace:BurstLines"] = "10",
            ["Diagnostics:SessionTrace:LinesPerSecond"] = "5",
        };
        var logs = new CapturingLoggerProvider();
        await using WorldTestHost host = SessionTraceTestKit.Start(new FakeTimeProvider(), logs, settings);
        await using WorldTestClient gm = await host.EnterWorldAsync("STRL2", "Smallburstgm", AccountSecurity.GameMaster);
        SessionTraceFeature feature = SessionTraceTestKit.FeatureOf(host);
        Assert.Equal(10, feature.Options.BurstLines);
        Assert.Equal(5, feature.Options.LinesPerSecond);
        await gm.CollectAsync();
        Assert.StartsWith("Session trace on for Smallburstgm", await SessionTraceTestKit.CommandAsync(gm, ".debug trace session on"), StringComparison.Ordinal);
        Trace trace = await host.OnWorldAsync(() => Assert.Single(feature.Active));

        int admitted = 0;
        for (int i = 0; i < 1_000; i++)
        {
            if (trace.Write("probe", "n=" + i)) admitted++;
        }

        Assert.InRange(admitted, 1, 15);
        Assert.Equal(1_000 - admitted, trace.Dropped);
    }
}
