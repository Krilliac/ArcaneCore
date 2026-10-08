using ArcaneCore.Kernel.Net;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Net;

/// <summary>
/// The per-connection packet budgets (Net:Protection:World*), ported from the fork's gateway test_gateway_rate.cpp and
/// extended with the refill, window and disabled cases.
/// </summary>
public sealed class OpcodeRateLimiterTests
{
    private const ushort Move = 0x00B5;
    private const ushort Other = 0x0001;

    [Fact]
    public void Bucket_AllowsItsCapacity_ThenDrops_ThenRefillsOneTokenPerInterval()
    {
        var limiter = new OpcodeRateLimiter(burst: 3, refillPerSecond: 1.0, packetsPerSecond: 0, floodPacketsPerSecond: 0);

        Assert.Equal(OpcodeRateVerdict.Allowed, limiter.OnPacket(Move, 0));
        Assert.Equal(OpcodeRateVerdict.Allowed, limiter.OnPacket(Move, 0));
        Assert.Equal(OpcodeRateVerdict.Allowed, limiter.OnPacket(Move, 0));
        Assert.Equal(OpcodeRateVerdict.Dropped, limiter.OnPacket(Move, 0));

        // One second later exactly one token is back.
        Assert.Equal(OpcodeRateVerdict.Allowed, limiter.OnPacket(Move, 1000));
        Assert.Equal(OpcodeRateVerdict.Dropped, limiter.OnPacket(Move, 1000));
    }

    [Fact]
    public void Buckets_ArePerOpcode()
    {
        var limiter = new OpcodeRateLimiter(burst: 5, refillPerSecond: 0, packetsPerSecond: 0, floodPacketsPerSecond: 0);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(OpcodeRateVerdict.Allowed, limiter.OnPacket(Move, 0));
        }

        Assert.Equal(OpcodeRateVerdict.Dropped, limiter.OnPacket(Move, 0)); // the 6th over the burst
        Assert.Equal(OpcodeRateVerdict.Allowed, limiter.OnPacket(Other, 0)); // another opcode has its own bucket
        Assert.Equal(2, limiter.TrackedOpcodes);
    }

    [Fact]
    public void GlobalCap_DropsThenFloodCapReportsAFlood_AndANewWindowStartsOver()
    {
        var limiter = new OpcodeRateLimiter(burst: 0, refillPerSecond: 0, packetsPerSecond: 50, floodPacketsPerSecond: 100);
        OpcodeRateVerdict verdict = OpcodeRateVerdict.Allowed;
        for (int i = 0; i < 50; i++)
        {
            verdict = limiter.OnPacket(Other, 0);
        }

        Assert.Equal(OpcodeRateVerdict.Allowed, verdict);
        Assert.Equal(OpcodeRateVerdict.Dropped, limiter.OnPacket(Other, 10)); // the 51st in the window

        for (int i = 52; i <= 100; i++)
        {
            verdict = limiter.OnPacket(Other, 20);
        }

        Assert.Equal(OpcodeRateVerdict.Dropped, verdict);
        Assert.Equal(OpcodeRateVerdict.Flood, limiter.OnPacket(Other, 30)); // the 101st
        Assert.Equal(OpcodeRateVerdict.Allowed, limiter.OnPacket(Other, 1000)); // the next one-second window
    }

    [Fact]
    public void ARetailClientCadence_IsNeverLimitedByTheDefaults()
    {
        var defaults = new Configuration.NetProtectionOptions();
        var limiter = new OpcodeRateLimiter(defaults.WorldOpcodeBurst, defaults.WorldOpcodeRefillPerSecond, defaults.WorldPacketsPerSecond, defaults.WorldFloodPacketsPerSecond);

        // A login into a crowded city with an empty client cache: hundreds of creature queries in a few milliseconds,
        // then ten minutes of heartbeats, turns and casts.
        for (int i = 0; i < 600; i++)
        {
            Assert.Equal(OpcodeRateVerdict.Allowed, limiter.OnPacket(0x0060, i / 100));
        }

        for (long t = 1000; t < 600_000; t += 100)
        {
            Assert.Equal(OpcodeRateVerdict.Allowed, limiter.OnPacket(Move, t));
            Assert.Equal(OpcodeRateVerdict.Allowed, limiter.OnPacket(0x00DA, t + 5));
        }
    }

    [Fact]
    public void EveryBudgetAtZero_AllowsEverything()
    {
        var limiter = new OpcodeRateLimiter(0, 0, 0, 0);
        Assert.False(limiter.IsEnabled);
        for (int i = 0; i < 100_000; i++)
        {
            Assert.Equal(OpcodeRateVerdict.Allowed, limiter.OnPacket(Move, 0));
        }
    }

    [Fact]
    public void AClockThatGoesBackwards_StartsANewWindowInsteadOfCountingForever()
    {
        var limiter = new OpcodeRateLimiter(burst: 0, refillPerSecond: 0, packetsPerSecond: 0, floodPacketsPerSecond: 3);
        Assert.Equal(OpcodeRateVerdict.Allowed, limiter.OnPacket(Move, 5000));
        Assert.Equal(OpcodeRateVerdict.Allowed, limiter.OnPacket(Move, 5000));
        Assert.Equal(OpcodeRateVerdict.Allowed, limiter.OnPacket(Move, 100)); // earlier stamp: a new window
        Assert.Equal(OpcodeRateVerdict.Allowed, limiter.OnPacket(Move, 100));
    }
}
