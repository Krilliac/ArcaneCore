using ArcaneCore.Game.Maps;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Ops;

/// <summary>The world thread's published position (phase, work, since) must be read as one consistent triple by the watchdog thread.</summary>
public sealed class WorldRuntimePositionTests
{
    private const long FeaturesSince = 1_000_000;

    [Fact]
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
                world.SetPosition(TickPhase.Features, marker, FeaturesSince);
                world.SetPosition(TickPhase.Idle, null, 0);
            }
        }) { IsBackground = true, Name = "position-writer" };
        writer.Start();
        Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(10)));

        long reads = 0, sawIdle = 0, sawFeatures = 0, torn = 0;
        string? firstTorn = null;
        long deadline = Environment.TickCount64 + 400;
        try
        {
            while (Environment.TickCount64 < deadline)
            {
                TickPosition p = world.CurrentPosition;
                reads++;
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

        Assert.True(sawIdle > 0 && sawFeatures > 0, $"vacuous run: {reads} reads, {sawIdle} Idle, {sawFeatures} Features");
        Assert.True(torn == 0, $"{torn} of {reads} reads were torn ({world.PositionReadsUnverified} unverified after the read budget); first: {firstTorn}");
    }
}
