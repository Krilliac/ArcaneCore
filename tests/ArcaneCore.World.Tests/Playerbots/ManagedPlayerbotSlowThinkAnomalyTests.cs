using System.Diagnostics;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// H1 hardening: <c>bot-brain/think-slow</c>, reported by <see cref="PlayerbotSlowThinkLog.Observe"/> for a think over
/// <see cref="PlayerbotOptions.SlowThinkLogMs"/>: through the unit (a probe of known length) and through the real feature with a controller
/// that burns its think budget. The ledger is process-wide, so captures filter on a bot name of their own.
/// </summary>
public sealed class ManagedPlayerbotSlowThinkAnomalyTests
{
    private static readonly string ThinkSlow = PlayerbotAnomalies.Key("brain", "think-slow");

    private static AnomalyCapture CaptureOf(string name)
        => AnomalyLedger.Shared.Capture(n => n.Key == ThinkSlow && n.Subject.Contains($"bot {name} ", StringComparison.Ordinal));

    private static PlayerbotThinkProbe ThinkOf(double ms)
    {
        var probe = new PlayerbotThinkProbe();
        probe.Begin();
        long until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);
        while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(50);
        probe.End();
        return probe;
    }

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
        Assert.True((await feature.StartScriptedAsync(slowId.ToString(), new Spinning(45))).Success);
        Assert.True((await feature.StartScriptedAsync(fastId.ToString(), new Spinning(0))).Success);

        await host.WaitForWorldAsync(() => slow.Any(ThinkSlow), "the slow think to be reported");

        AnomalyNotice notice = slow.Notices.First();
        Assert.Contains("(over 30.0 ms) driver=controller", notice.Detail, StringComparison.Ordinal);
        Assert.False(fast.Any(ThinkSlow), fast.Describe());
        await feature.ShutdownBeforeWorldStopAsync();
    }

    private sealed class Spinning(double ms) : IPlayerbotController
    {
        public void Tick(PlayerbotControllerContext context, uint elapsedMs)
        {
            long until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);
            while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(50);
        }

        public void Detached(Guid botId) { }
    }
}
