using ArcaneCore.Game.AntiCheat;
using Xunit;

namespace ArcaneCore.Game.Tests.AntiCheat;

/// <summary>
/// The long-term drift of <see cref="SpeedHackDetector"/> (<c>AntiCheat:SpeedClock:LongTermDriftMs</c>, off by default). Written without a build in
/// the lane that added them: unproven until the integration gate runs them.
/// </summary>
public sealed class SpeedHackDriftTests
{
    private static SpeedClockOptions Options(int driftMs) => new()
    {
        Enabled = true, Window = 20, MinSamples = 12, TolerancePercent = 30, SustainWindows = 3, MaxGapMs = 3000, CooldownMs = 10000, LongTermDriftMs = driftMs,
    };

    /// <summary>A client clock 10 percent fast, under the 30 percent tolerance: 500 ms of real time per packet, 550 ms of client time.</summary>
    private static (bool Fired, SpeedHackDecision Decision, int Packets) RunTenPercentFast(SpeedHackDetector detector, int packets)
    {
        uint client = 100000, real = 5000;
        for (int i = 1; i <= packets; i++)
        {
            real += 500;
            client += 550;
            SpeedHackDecision decision = detector.Feed(client, real);
            if (decision.Fire)
            {
                return (true, decision, i);
            }
        }

        return (false, default, packets);
    }

    [Fact]
    public void ASlowClock_TenPercentFast_NeverFiresWithTheDriftOff()
    {
        (bool fired, _, _) = RunTenPercentFast(new SpeedHackDetector(Options(0)), 2000);
        Assert.False(fired); // the default: only the sliding window, which tolerates 30 percent
    }

    [Fact]
    public void ASlowClock_TenPercentFast_FiresOnceTheExcessReachesTheLimit()
    {
        (bool fired, SpeedHackDecision decision, int packets) = RunTenPercentFast(new SpeedHackDetector(Options(5000)), 2000);
        Assert.True(fired);
        Assert.True(decision.LongTermDrift);
        Assert.Equal(101, packets); // the first packet has no predecessor; 50 ms of excess per pair reaches 5000 ms at the 100th pair
        Assert.InRange(decision.Severity, 9, 11);
        Assert.InRange(decision.Ratio, 1.09, 1.11);
    }

    [Fact]
    public void AfterFiring_TheCountRestarts_SoItFiresAgainOnlyAfterAnotherLimitsWorth()
    {
        var detector = new SpeedHackDetector(Options(5000));
        uint client = 100000, real = 5000;
        var fires = new List<int>();
        for (int i = 1; i <= 400; i++)
        {
            real += 500;
            client += 550;
            if (detector.Feed(client, real).Fire)
            {
                fires.Add(i);
            }
        }

        Assert.Equal([101, 201, 301], fires);
    }

    [Fact]
    public void HonestPlay_LagThenACatchUpBurst_StaysNearZero_AndNeverFires()
    {
        var detector = new SpeedHackDetector(Options(5000));
        uint client = 100000, real = 5000;
        for (int round = 0; round < 200; round++)
        {
            // Two seconds of lag (the real clock runs ahead of the client's), then the held packets arrive bunched.
            client += 500;
            real += 2500;
            Assert.False(detector.Feed(client, real).Fire);
            for (int i = 0; i < 4; i++)
            {
                client += 500;
                real += 1;
                Assert.False(detector.Feed(client, real).Fire);
            }
        }
    }

    [Fact]
    public void HonestPlay_AStallAboveMaxGap_ThenABurst_DoesNotAccumulateTowardsTheLimit()
    {
        // The stalled pair (5000 ms of real time, above MaxGapMs 3000) is discarded by the window's guards, but the drift still counts it, so the
        // burst that follows repays it. Only the drift is asserted: the window's own verdict on a burst is not what this pins.
        var detector = new SpeedHackDetector(Options(5000));
        uint client = 100000, real = 5000;
        detector.Feed(client, real);
        for (int round = 0; round < 300; round++)
        {
            client += 500;
            real += 5000;
            Assert.False(detector.Feed(client, real).LongTermDrift);
            for (int i = 0; i < 9; i++)
            {
                client += 500;
                real += 1;
                Assert.False(detector.Feed(client, real).LongTermDrift, $"round {round} packet {i}");
            }
        }
    }

    [Fact]
    public void HonestPlay_AStampSlightlyBehindThePreviousOne_DoesNotAccumulateTowardsTheLimit()
    {
        // The client's stamps jitter: every other packet carries a stamp 200 ms behind the true client time (still inside the regression
        // tolerance). The excess must stay at zero, not gain 200 ms per round.
        var detector = new SpeedHackDetector(Options(5000));
        uint real = 5000;
        const uint Offset = 100000;
        detector.Feed(real + Offset, real);
        for (int round = 0; round < 500; round++)
        {
            real += 500;
            Assert.False(detector.Feed(real + Offset - 200, real).LongTermDrift);
            real += 500;
            Assert.False(detector.Feed(real + Offset, real).LongTermDrift, $"round {round}");
        }
    }

    [Fact]
    public void AFireInsideTheCooldown_WaitsForItsEnd()
    {
        // A clock 3x fast with the limit at its minimum: the first fire starts the cooldown, so the next ones are held back until it ends.
        var detector = new SpeedHackDetector(Options(1000));
        uint client = 100000, real = 5000;
        detector.Feed(client, real);
        int fires = 0;
        for (int i = 0; i < 40; i++) // 40 pairs of 100 ms real: 4000 ms, cooldown 10000
        {
            real += 100;
            client += 300;
            if (detector.Feed(client, real).LongTermDrift)
            {
                fires++;
            }
        }

        Assert.Equal(1, fires);
    }

    [Fact]
    public void AReset_ForgetsTheExcess()
    {
        var detector = new SpeedHackDetector(Options(5000));
        uint client = 100000, real = 5000;
        for (int i = 0; i < 90; i++)
        {
            real += 500;
            client += 550;
            Assert.False(detector.Feed(client, real).Fire);
        }

        detector.Reset();
        for (int i = 0; i < 90; i++)
        {
            real += 500;
            client += 550;
            Assert.False(detector.Feed(client, real).Fire); // 89 pairs since the reset: still short of the limit
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1000, true)]
    [InlineData(3_600_000, true)]
    [InlineData(999, false)]
    [InlineData(-5, false)]
    [InlineData(3_600_001, false)]
    public void TheLimit_IsOffOrWithinRange(int value, bool valid)
    {
        Assert.Equal(valid, !Options(value).Validate().Any());
    }
}
