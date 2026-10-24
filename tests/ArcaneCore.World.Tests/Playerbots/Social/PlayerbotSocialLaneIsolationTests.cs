using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>A social lane that throws is recorded and skipped; the lanes after it, and the pass itself, still run.</summary>
public sealed class PlayerbotSocialLaneIsolationTests
{
    [Fact]
    public async Task AThrowingLane_RunningBeforeTheBuiltInOnes_IsRecordedUnderItsName_AndTheRestStillRun()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        PlayerbotSocialFeature feature = SocialTestKit.Feature(world.Host);
        string unique = "isolation-" + Guid.NewGuid().ToString("N");
        int ran = 0;
        feature.InjectedLanes.Add((unique, _ => throw new InvalidOperationException("lane boom " + unique)));
        feature.InjectedLanes.Add(("after", _ => ran++));
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Detail.Contains(unique, StringComparison.Ordinal));
        long faultsBefore = feature.LaneFaultCount;
        int passesBefore = feature.Passes;

        await world.Host.OnWorldAsync(() => feature.RunPass(1000));

        Assert.Equal(1, ran);
        Assert.Equal(passesBefore + 1, feature.Passes);
        Assert.Equal(faultsBefore + 1, feature.LaneFaultCount);
        // Recorded under the lane's name (by this fault's own detail, not the process-wide ledger total, which other tests move).
        Assert.Contains(capture.Notices, n => n.Key == PlayerbotAnomalies.Key("social", "feature-fault") && n.Detail.Contains("lane " + unique, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALaneThatThrowsEveryPass_IsReportedOncePerMinute_AndCountedEveryTime()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        PlayerbotSocialFeature feature = SocialTestKit.Feature(world.Host);
        string unique = "flood-" + Guid.NewGuid().ToString("N");
        feature.InjectedLanes.Add((unique, _ => throw new InvalidOperationException("lane boom " + unique)));
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Detail.Contains(unique, StringComparison.Ordinal));
        long faultsBefore = feature.LaneFaultCount;

        for (int i = 0; i < 5; i++) await world.Host.OnWorldAsync(() => feature.RunPass(1000));

        Assert.Equal(faultsBefore + 5, feature.LaneFaultCount);
        Assert.Single(capture.Notices);
    }
}
