using System.Diagnostics;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GcStallSerialCollection
{
    internal const string Name = "GcStallSerial";
}

/// <summary>
/// H1 hardening: <c>bot-brain/think-slow</c>, reported by <see cref="PlayerbotSlowThinkLog.Observe"/> for a think over
/// <see cref="PlayerbotOptions.SlowThinkLogMs"/>: through the unit (a probe of known length) and through the real feature with a controller
/// that burns its think budget. The ledger is process-wide, so captures filter on a bot name of their own.
/// </summary>
[Collection(GcStallSerialCollection.Name)] // its forced gen-2 collections would disturb the timing tests running beside it
public sealed class ManagedPlayerbotSlowThinkAnomalyTests
{
    private static readonly string ThinkSlow = PlayerbotAnomalies.Key("brain", "think-slow");

    private static readonly string ThinkGcStall = PlayerbotAnomalies.Key("brain", "think-gc-stall");

    private static AnomalyCapture CaptureOf(string name)
        => AnomalyLedger.Shared.Capture(n => n.Key == ThinkSlow && n.Subject.Contains($"bot {name} ", StringComparison.Ordinal));

    // A finished think of exactly ms with no GC pause. A wall-clock spin here also counted any process-wide GC pause during the spin
    // (GC.GetTotalPauseDuration), which turned a think-slow case into think-gc-stall (PlayerbotSlowThinkLog: elapsed - gc < threshold).
    // The real Begin/End accounting is exercised by GcStallThink below and by the feature test.
    private static PlayerbotThinkProbe ThinkOf(double ms) => PlayerbotThinkProbe.SpentForTest(ms);

    [Fact]
    public void ASlowThink_Fires_WithTheDriverGoalAndThreshold_AndAThinkUnderTheThresholdIsSilent()
    {
        var log = new PlayerbotSlowThinkLog(NullLogger.Instance);
        using AnomalyCapture capture = CaptureOf("Slowunit");
        PlayerbotThinkProbe probe = ThinkOf(5);

        Assert.False(log.Observe(probe, thresholdMs: 60_000, nowMs: 1, Guid.NewGuid(), "Slowunit", "its brain", "Explore"));
        Assert.False(log.Observe(probe, thresholdMs: 0, nowMs: 2, Guid.NewGuid(), "Slowunit", "its brain", "Explore")); // 0 = off
        Assert.False(capture.Any(ThinkSlow), capture.Describe());

        Guid id = Guid.NewGuid();
        Assert.True(log.Observe(probe, thresholdMs: 1, nowMs: 3, id, "Slowunit", "its brain", "Explore"));
        Assert.Equal(1, capture.Count(ThinkSlow));
        AnomalyNotice notice = capture.Notices.Single();
        Assert.Equal(AnomalySeverity.Warning, notice.Severity);
        Assert.Equal("bot-brain", notice.Subsystem);
        Assert.Contains("(over 1.0 ms) driver=its brain goal=Explore", notice.Detail, StringComparison.Ordinal);
        Assert.Contains($"bot Slowunit (id {id})", notice.Subject, StringComparison.Ordinal);
    }

    /// <summary>A think over <paramref name="threshold"/> whose time minus its GC pause is under it (null if the machine never gave one).</summary>
    private static PlayerbotThinkProbe? GcStallThink(double threshold)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var candidate = new PlayerbotThinkProbe();
            candidate.Begin();
            // Blocking collections until the think is over the threshold: nearly all of its time is the collector's pause.
            do GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            while (candidate.RunningMs < threshold + 1);
            candidate.End();
            if (candidate.ElapsedMs - candidate.GcPauseMs < threshold) return candidate;
        }

        return null;
    }

    [Fact]
    public void AGcStallThink_DoesNotOpenOrCloseTheThinkSlowEpisode_AndIsInfoSoBotQaDoesNotQualifyIt()
    {
        var log = new PlayerbotSlowThinkLog(NullLogger.Instance);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => (n.Key == ThinkSlow || n.Key == ThinkGcStall) && n.Subject.Contains("bot Slowmix ", StringComparison.Ordinal));
        Guid id = Guid.NewGuid();
        PlayerbotThinkProbe? stall = GcStallThink(2.0);
        Assert.NotNull(stall);

        // The first slow think of the episode is a GC stall; the real slow think right after it is still a think-slow report.
        Assert.True(log.Observe(stall, 2.0, 1, id, "Slowmix", "its brain", "Grind"));
        Assert.True(log.Observe(ThinkOf(30), 2.0, 2, id, "Slowmix", "its brain", "Grind"));
        Assert.Equal(1, capture.Count(ThinkGcStall));
        Assert.Equal(1, capture.Count(ThinkSlow));
        Assert.Equal(AnomalySeverity.Info, capture.Notices.Single(n => n.Key == ThinkGcStall).Severity);
        Assert.Equal(AnomalySeverity.Warning, capture.Notices.Single(n => n.Key == ThinkSlow).Severity);

        // The QA feed takes Warning and above: the stall is not a finding, the real slow think (given the thresholds) is.
        var feed = new ArcaneCore.World.Playerbots.Qa.PlayerbotQaAnomalyFeed(AnomalyLedger.Shared, minCount: 1, minDistinctSubjects: 1);
        Assert.False(feed.Qualifies(AnomalyLedger.Shared.Find(ThinkGcStall)!));
        Assert.True(feed.Qualifies(AnomalyLedger.Shared.Find(ThinkSlow)!));
    }

    [Fact]
    public void AThinkSlowOnlyForTheCollectorsPause_IsCountedAsAGcStall_NotAsThinkSlow()
    {
        var log = new PlayerbotSlowThinkLog(NullLogger.Instance);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => (n.Key == ThinkSlow || n.Key == ThinkGcStall) && n.Subject.Contains("bot Slowgc ", StringComparison.Ordinal));
        const double threshold = 2.0;
        PlayerbotThinkProbe? probe = GcStallThink(threshold);

        Assert.NotNull(probe);
        Assert.True(probe.GcPauseMs > 0, $"pause {probe.GcPauseMs} ms");
        Assert.True(probe.GcCollections.Gen2 > 0);
        Assert.True(probe.ElapsedMs >= threshold);

        Assert.True(log.Observe(probe, threshold, nowMs: 1, Guid.NewGuid(), "Slowgc", "its brain", "Grind"));
        Assert.False(capture.Any(ThinkSlow), capture.Describe());
        Assert.Equal(1, capture.Count(ThinkGcStall));
        Assert.Contains("gc=", capture.Notices.Single().Detail, StringComparison.Ordinal);
        Assert.Matches(@"gc=\d+\.\d ms \(\d+/\d+/\d+\)", probe.Describe());
    }

    [Fact]
    public void AThinkSlowBeyondItsGcPause_StillFiresThinkSlow_WithTheGcInItsDetail()
    {
        var log = new PlayerbotSlowThinkLog(NullLogger.Instance);
        using AnomalyCapture capture = CaptureOf("Slowreal");
        PlayerbotThinkProbe probe = ThinkOf(30);

        Assert.True(log.Observe(probe, thresholdMs: 5, nowMs: 1, Guid.NewGuid(), "Slowreal", "its brain", "Grind"));
        Assert.Equal(1, capture.Count(ThinkSlow));
        Assert.Contains(" gc=", capture.Notices.Single().Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsecutiveSlowThinks_AreOneEpisode_AndAFastThinkStartsANewOne()
    {
        var log = new PlayerbotSlowThinkLog(NullLogger.Instance);
        using AnomalyCapture capture = CaptureOf("Slowepisode");
        Guid id = Guid.NewGuid();
        Guid other = Guid.NewGuid();

        // Two slow thinks in a row (a fresh probe each, as the feature has): one report.
        Assert.True(log.Observe(ThinkOf(3), 1, 1, id, "Slowepisode", "its brain", "Explore"));
        Assert.True(log.Observe(ThinkOf(3), 1, 2, id, "Slowepisode", "its brain", "Explore"));
        Assert.Equal(1, capture.Count(ThinkSlow));

        // Another bot's fast think does not end this bot's episode.
        log.EndEpisode(other);
        Assert.True(log.Observe(ThinkOf(3), 1, 3, id, "Slowepisode", "its brain", "Explore"));
        Assert.Equal(1, capture.Count(ThinkSlow));

        // This bot's fast think ends it: the next slow think is a new episode and reports again.
        log.EndEpisode(id);
        Assert.True(log.Observe(ThinkOf(3), 1, 4, id, "Slowepisode", "its brain", "Explore"));
        Assert.Equal(2, capture.Count(ThinkSlow));
    }

    [Fact]
    public void ThePerProbeFlag_IsSetWhenReported_SoTheSameThinkIsNeverReportedTwice()
    {
        var log = new PlayerbotSlowThinkLog(NullLogger.Instance);
        using AnomalyCapture capture = CaptureOf("Slowflag");
        PlayerbotThinkProbe probe = ThinkOf(3);
        Assert.False(probe.SlowAnomalyReported);

        Assert.True(log.Observe(probe, 1, 1, Guid.NewGuid(), "Slowflag", "its brain", "Explore"));
        Assert.True(probe.SlowAnomalyReported);
        Assert.True(log.Observe(probe, 1, 2, Guid.NewGuid(), "Slowflag", "its brain", "Explore")); // the same think seen again, even as another bot
        Assert.Equal(1, capture.Count(ThinkSlow));
    }

    [Fact]
    public async Task ASlowControllerThink_FiresOnTheLedger_AndAFastOneDoesNot()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners, configure: o => o.SlowThinkLogMs = 30);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        using AnomalyCapture slow = CaptureOf("Slowfire");
        using AnomalyCapture fast = CaptureOf("Slowquiet");

        Guid slowId = Assert.IsType<Guid>((await feature.CreateAsync("Slowfire", 1, 1)).BotId);
        Guid fastId = Assert.IsType<Guid>((await feature.CreateAsync("Slowquiet", 1, 1)).BotId);
        Assert.True((await feature.StartScriptedAsync(slowId.ToString(), new Spinning(300, once: true))).Success);
        Assert.True((await feature.StartScriptedAsync(fastId.ToString(), new Spinning(0))).Success);

        // 300 ms against a 30 ms threshold: the collector pauses of a busy test run (GcPauseMs is process-wide) cannot account for all of it.
        await host.WaitForWorldAsync(() => slow.Any(ThinkSlow), "the slow think to be reported");

        AnomalyNotice notice = slow.Notices.First();
        Assert.Matches(@"^bot Slowfire \(guid [0-9A-F]+\)$", notice.Subject); // the standard bot subject, as every other bot anomaly
        Assert.Contains("(over 30.0 ms) driver=controller", notice.Detail, StringComparison.Ordinal);
        Assert.False(fast.Any(ThinkSlow), fast.Describe());
        await feature.ShutdownBeforeWorldStopAsync();
    }

    private sealed class Spinning(double ms, bool once = false) : IPlayerbotController
    {
        private bool _spun;

        public void Tick(PlayerbotControllerContext context, uint elapsedMs)
        {
            if (once && _spun) return;
            _spun = true;
            long until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);
            while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(50);
        }

        public void Detached(Guid botId) { }
    }
}
