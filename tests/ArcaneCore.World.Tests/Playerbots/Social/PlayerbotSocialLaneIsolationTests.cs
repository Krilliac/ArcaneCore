using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>A social lane that throws is recorded and skipped; the lanes after it, and the pass itself, still run.</summary>
public sealed class PlayerbotSocialLaneIsolationTests
{
    [Fact]
    public async Task AThrowingLane_IsRecordedUnderItsName_AndTheLanesAfterItStillRun()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        PlayerbotSocialFeature feature = SocialTestKit.Feature(world.Host);
        int ran = 0;
        feature.InjectedLanes.Add(("boom", _ => throw new InvalidOperationException("lane boom")));
        feature.InjectedLanes.Add(("after", _ => ran++));

        long faultsBefore = FaultLedger.Shared.Total;
        int passesBefore = feature.Passes;
        await world.Host.OnWorldAsync(() => feature.RunPass(1000));

        Assert.Equal(1, ran);
        Assert.Equal(passesBefore + 1, feature.Passes);
        Assert.Equal(faultsBefore + 1, FaultLedger.Shared.Total);
    }
}
