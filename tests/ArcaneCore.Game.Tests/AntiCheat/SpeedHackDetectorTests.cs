using ArcaneCore.Game.AntiCheat;
using Xunit;

namespace ArcaneCore.Game.Tests.AntiCheat;

/// <summary>
/// The independent-clock detector over synthetic delta sequences: the six cases of the fork's
/// src/gateway/test/test_gateway_speedhack.cpp, ported to xunit, plus the 32-bit wrap and the disabled switch.
/// </summary>
public sealed class SpeedHackDetectorTests
{
    private static SpeedClockOptions Defaults() => new()
    {
        Enabled = true, Window = 20, MinSamples = 12, TolerancePercent = 30, SustainWindows = 3, MaxGapMs = 3000, CooldownMs = 10000,
    };

    [Fact]
    public void NormalPlay_HeartbeatCadenceWithRealSideJitter_DoesNotFire()
    {
        var detector = new SpeedHackDetector(Defaults());
        uint client = 100000, real = 5000;
        int[] jitter = [+60, -40, +80, -20, +30, -70, +50, -10];
        for (int i = 0; i < 40; i++)
        {
            client += 500;
            real = (uint)(real + 500 + jitter[i % 8]);
            Assert.False(detector.Feed(client, real).Fire);
        }
    }

    [Fact]
    public void AnAcceleratedClock_OneAndAHalfTimesReal_Fires_WithSeverityAboutFifty()
    {
        SpeedClockOptions options = Defaults();
        var detector = new SpeedHackDetector(options);
        uint client = 200000, real = 7000;
        SpeedHackDecision fired = default;
        int steps = options.MinSamples + (options.SustainWindows * options.Window) + 5;
        for (int i = 0; i < steps; i++)
        {
            real += 500;
            client += 750;
            SpeedHackDecision decision = detector.Feed(client, real);
            if (decision.Fire)
            {
                fired = decision;
            }
        }

        Assert.True(fired.Fire);
        Assert.InRange(fired.Severity, 45, 55);
        Assert.InRange(fired.Ratio, 1.40, 1.60);
    }

    [Fact]
    public void HeavyLag_RealDeltasTwiceTheClients_DoesNotFire()
    {
        var detector = new SpeedHackDetector(Defaults());
        uint client = 300000, real = 9000;
        for (int i = 0; i < 60; i++)
        {
            client += 500;
            real += 1000;
            Assert.False(detector.Feed(client, real).Fire);
        }
    }

    [Fact]
    public void AnIdleGap_IsFiltered_AndNormalPlayResumesClean()
    {
        var detector = new SpeedHackDetector(Defaults());
        uint client = 400000, real = 11000;
        for (int i = 0; i < 6; i++)
        {
            client += 500;
            real += 500;
            Assert.False(detector.Feed(client, real).Fire);
        }

        // One minute AFK: the client barely advanced its movement clock; the pair is discarded.
        client += 200;
        real += 60000;
        Assert.False(detector.Feed(client, real).Fire);

        for (int i = 0; i < 30; i++)
        {
            client += 500;
            real += 500;
            Assert.False(detector.Feed(client, real).Fire);
        }
    }

    [Fact]
    public void TheHysteresisBand_DoesNotFire_ButASustainedHotRunDoes()
    {
        SpeedClockOptions options = Defaults();
        var detector = new SpeedHackDetector(options);
        uint client = 500000, real = 13000;
        for (int i = 0; i < options.Window; i++)
        {
            real += 500;
            client += 600; // ratio 1.2: between 1.15 and 1.30
            Assert.False(detector.Feed(client, real).Fire);
        }

        bool firedLate = false;
        for (int i = 0; i < (options.Window * options.SustainWindows) + options.Window; i++)
        {
            real += 500;
            client += 800; // ratio 1.6
            firedLate |= detector.Feed(client, real).Fire;
        }

        Assert.True(firedLate);
    }

    [Fact]
    public void AfterAFire_ItStaysSilentForTheCooldown_ThenCanFireAgain()
    {
        SpeedClockOptions options = Defaults();
        var detector = new SpeedHackDetector(options);
        uint client = 600000, real = 15000;
        bool first = false;
        while (!first && real < 15000 + 200000)
        {
            real += 500;
            client += 750;
            first = detector.Feed(client, real).Fire;
        }

        Assert.True(first);
        uint cooldownStart = real;
        while (real - cooldownStart < options.CooldownMs - 1000)
        {
            real += 500;
            client += 750;
            Assert.False(detector.Feed(client, real).Fire);
        }

        bool again = false;
        for (int i = 0; i < (options.Window * (options.SustainWindows + 1)) + 4; i++)
        {
            real += 500;
            client += 750;
            again |= detector.Feed(client, real).Fire;
        }

        Assert.True(again);
    }

    [Fact]
    public void TheClientClockWrapping_AtTwoToTheThirtyTwo_IsNormalPlay()
    {
        var detector = new SpeedHackDetector(Defaults());
        uint client = uint.MaxValue - 5000, real = uint.MaxValue - 9000;
        for (int i = 0; i < 60; i++)
        {
            client = unchecked(client + 500);
            real = unchecked(real + 500);
            Assert.False(detector.Feed(client, real).Fire);
        }
    }

    [Fact]
    public void Disabled_NeverFires_AndResetForgetsTheWindow()
    {
        SpeedClockOptions options = Defaults();
        options.Enabled = false;
        var off = new SpeedHackDetector(options);
        uint client = 0, real = 0;
        for (int i = 0; i < 200; i++)
        {
            Assert.False(off.Feed(client += 1000, real += 500).Fire);
        }

        var on = new SpeedHackDetector(Defaults());
        client = 0;
        real = 0;
        for (int i = 0; i < 70; i++)
        {
            on.Feed(client += 750, real += 500);
        }

        on.Reset();
        for (int i = 0; i < 11; i++)
        {
            Assert.False(on.Feed(client += 750, real += 500).Fire); // fewer pairs than MinSamples after the reset
        }
    }
}
