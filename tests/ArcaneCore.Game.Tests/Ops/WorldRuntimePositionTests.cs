using ArcaneCore.Game.Maps;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Ops;

/// <summary>The world thread's published position (phase, work, since) must be read as one consistent triple by the watchdog thread.</summary>
public sealed class WorldRuntimePositionTests
{
    private const long FeaturesSince = 1_000_000;

    // Wall-clock dependent: reads give up after a time budget, so a starved writer on a loaded runner can fail the unverified-ratio assert.
    // It runs in the serial Timing stage with its isolated rerun.
    [Fact]
    [Trait("Category", "Timing")]
    public void CurrentPosition_UnderConcurrentPublishing_NeverPairsOnePositionsPhaseWithAnothersWork()
    {
        var options = new WorldRuntimeOptions { TickIntervalMs = 5, UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0 };
        options.Perf.SummaryIntervalSeconds = 0;
        using var world = new WorldRuntime(options, new RecordingSaveQueue(), new CapturingLogger<WorldRuntime>());

        // The world is never started, so this test's writer thread is the only publisher.

        var marker = new object();
        using var stop = new CancellationTokenSource();
        using var writerStarted = new ManualResetEventSlim();
        var writer = new Thread(() =>
        {
            writerStarted.Set();
            while (!stop.IsCancellationRequested)
            {
                // A short spin after each publish stands in for the work the world thread does between publishes. Publishing
                // back to back left a reader on a two-core CI runner almost no window between writes, so reads ran out their time
                // budget there (6 of 398 on 2026-10-25) although no read was torn. Writes still overlap reads all the time.
                world.SetPosition(TickPhase.Features, marker, FeaturesSince);
                Thread.SpinWait(32);
                world.SetPosition(TickPhase.Idle, null, 0);
                Thread.SpinWait(32);
            }
        }) { IsBackground = true, Name = "position-writer" };
        writer.Start();
        Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(10)));

        // A read that exhausts PositionReadBudgetMs deliberately returns an unverified, possibly mixed position (and counts it), so
        // only verified reads are held to consistency. This thread is the only reader, so the counter moving across a read
        // identifies exactly that read.
        long reads = 0, sawIdle = 0, sawFeatures = 0, torn = 0, unverifiedReads = 0;
        string? firstTorn = null;
        // At least 400 ms of reads, and on until both phases were read often: on a loaded runner the writer thread can get no core for the
        // whole 400 ms (Node1, five gates at once: 679,437 reads, every one Idle), which is a starved writer, not a verdict on the reads.
        // The 60 s cap only ends a run whose writer never runs; the vacuous-run assertion below then fails it.
        const long MinimumMs = 400, CapMs = 60_000, EnoughOfEach = 1_000;
        long started = Environment.TickCount64;
        try
        {
            while (Environment.TickCount64 - started is var ms && ms < CapMs
                && (ms < MinimumMs || sawIdle < EnoughOfEach || sawFeatures < EnoughOfEach))
            {
                long unverifiedBefore = world.PositionReadsUnverified;
                TickPosition p = world.CurrentPosition;
                reads++;
                if (world.PositionReadsUnverified != unverifiedBefore) { unverifiedReads++; continue; }
                bool consistent;
                if (p.Phase == TickPhase.Features) { sawFeatures++; consistent = ReferenceEquals(p.Work, marker) && p.SinceTimestamp == FeaturesSince; }
                else if (p.Phase == TickPhase.Idle) { sawIdle++; consistent = p.Work is null && p.SinceTimestamp == 0; }
                else consistent = false;
                if (!consistent) { torn++; firstTorn ??= $"{p.Phase} work={(p.Work is null ? "null" : "marker")} since={p.SinceTimestamp}"; }
            }
        }
        finally
        {
            stop.Cancel();
            Assert.True(writer.Join(TimeSpan.FromSeconds(10)), "writer thread did not stop");
        }

        Assert.True(sawIdle >= EnoughOfEach && sawFeatures >= EnoughOfEach, $"vacuous run: {reads} reads, {sawIdle} Idle, {sawFeatures} Features");
        Assert.True(unverifiedReads * 100 < reads, $"{unverifiedReads} of {reads} reads ran out the read budget");
        Assert.True(torn == 0, $"{torn} of {reads - unverifiedReads} verified reads were torn ({unverifiedReads} unverified reads excluded); first: {firstTorn}");
    }
}
