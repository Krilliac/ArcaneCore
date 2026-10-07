using ArcaneCore.Game.Instances.Entry;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>The hourly per-account instance limit (vmangos AccountMgr::CheckInstanceCount / AddInstanceEnterTime, AccountMgr.cpp:441-472).</summary>
public sealed class InstanceEnterLimiterTests
{
    [Fact]
    public void InstanceZero_IsNeverRecorded_SoItCannotBecomeAFreeReEntry()
    {
        var limiter = new InstanceEnterLimiter();
        limiter.Record(7, 101, 1000);
        limiter.Record(7, 0, 1000); // 0 is "not created yet", never an entered instance

        Assert.Equal(1, limiter.Count(7));
        Assert.False(limiter.CanEnter(7, 0, maxCount: 1, now: 1000));
        Assert.True(limiter.CanEnter(7, 101, maxCount: 1, now: 1000)); // re-entering a recorded instance is free
    }
}
