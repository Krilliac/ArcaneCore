using ArcaneCore.Game.Locomotion;
using Xunit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>The mirror timer (vmangos MirrorTimer.cpp:19-150 and ShortIntervalTimer, shared/Timer.h:101-126).</summary>
public sealed class MirrorTimerTests
{
    private static MirrorTimer Running(uint interval = 60000, int scale = -1)
    {
        var timer = new MirrorTimer(MirrorTimerType.Breath);
        timer.SetScale(scale);
        timer.Start(interval);
        return timer;
    }

    [Fact]
    public void Start_NeedsANegativeScale_AndAnnouncesAFullUpdateOnce()
    {
        MirrorTimer timer = Running();

        Assert.True(timer.IsActive);
        Assert.Equal((60000u, 60000u), (timer.Duration, timer.Remaining));
        Assert.Equal(MirrorTimerStatus.FullUpdate, timer.FetchStatus());
        Assert.Equal(MirrorTimerStatus.Unchanged, timer.FetchStatus()); // fetching resets it

        var regenerating = new MirrorTimer(MirrorTimerType.Breath);
        regenerating.SetScale(10);
        regenerating.Start(60000);
        Assert.False(regenerating.IsActive); // "if (m_scale < 0) ... else Stop()"
    }

    [Fact]
    public void RunningOut_PulsesOnExpiry_ThenEveryTwoSeconds()
    {
        MirrorTimer timer = Running();

        Assert.True(timer.Update(59999));
        Assert.Equal(1u, timer.Remaining);
        Assert.False(timer.Update(1));          // expired: the instant tick

        // Subsequent ticks: a pulse every 2000 ms of expired time.
        for (int i = 0; i < 19; i++)
        {
            Assert.True(timer.Update(100), $"tick {i}");
        }

        Assert.False(timer.Update(100));        // the 2000th millisecond
        for (int i = 0; i < 19; i++)
        {
            Assert.True(timer.Update(100));
        }

        Assert.False(timer.Update(100));
    }

    [Fact]
    public void AScaleOfMinusTwo_RunsOutTwiceAsFast()
    {
        MirrorTimer timer = Running(interval: 10000, scale: -2);
        Assert.True(timer.Update(4999));
        Assert.False(timer.Update(1));
    }

    [Fact]
    public void Regenerating_DrainsTenTimesFaster_AndStopsAtZero()
    {
        MirrorTimer timer = Running();
        timer.Update(30000);                      // half the breath gone
        Assert.Equal(30000u, timer.Remaining);
        timer.FetchStatus();

        timer.SetScale(10);                       // surfaced
        Assert.Equal(MirrorTimerStatus.FullUpdate, timer.FetchStatus());
        Assert.True(timer.Update(2000));          // 2000 x 10 = 20000 regained
        Assert.Equal(50000u, timer.Remaining);
        Assert.True(timer.IsActive);

        Assert.True(timer.Update(2000));          // 20000 more is more than what was missing
        Assert.False(timer.IsActive);
        Assert.Equal(MirrorTimerStatus.StatusUpdate, timer.FetchStatus());
    }

    [Fact]
    public void ARegeneratingTimer_CannotBeFrozen_ARunningOneCan()
    {
        MirrorTimer timer = Running();
        timer.SetFrozen(true);
        Assert.True(timer.IsFrozen);
        Assert.True(timer.Update(120000));        // frozen: nothing advances, no pulse
        Assert.Equal(60000u, timer.Remaining);

        timer.SetScale(10);
        Assert.False(timer.IsFrozen);
    }

    [Fact]
    public void SetFrozen_OnAnActiveTimer_IsAStatusUpdate()
    {
        MirrorTimer timer = Running();
        timer.FetchStatus();

        timer.SetFrozen(true);

        Assert.Equal(MirrorTimerStatus.StatusUpdate, timer.FetchStatus());
    }

    [Fact]
    public void SetDurationAndRemaining_ChangeTheBar()
    {
        MirrorTimer timer = Running();
        timer.FetchStatus();

        timer.SetDuration(240000);
        Assert.Equal(240000u, timer.Duration);
        Assert.Equal(MirrorTimerStatus.FullUpdate, timer.FetchStatus());

        timer.SetRemaining(1000);
        Assert.Equal(1000u, timer.Remaining);

        timer.SetDuration(0);                     // zero stops it
        Assert.False(timer.IsActive);
    }

    [Fact]
    public void AnInactiveTimer_DoesNotAdvance()
    {
        var timer = new MirrorTimer(MirrorTimerType.Fatigue);
        Assert.True(timer.Update(1_000_000));
    }

    [Fact]
    public void Stop_ResetsTheTimer_AndAnnouncesAStatusUpdate()
    {
        MirrorTimer timer = Running();
        timer.Update(5000);
        timer.FetchStatus();

        timer.Stop();

        Assert.False(timer.IsActive);
        Assert.Equal(MirrorTimerStatus.StatusUpdate, timer.FetchStatus());
        timer.Stop();                             // already stopped: nothing to announce
        Assert.Equal(MirrorTimerStatus.Unchanged, timer.FetchStatus());
    }

    [Fact]
    public void EnvironmentFlags_HaveTheVmangosValues()
    {
        Assert.Equal((byte)0x01, (byte)EnvironmentFlags.InWater);
        Assert.Equal((byte)0x02, (byte)EnvironmentFlags.InMagma);
        Assert.Equal((byte)0x04, (byte)EnvironmentFlags.InSlime);
        Assert.Equal((byte)0x08, (byte)EnvironmentFlags.HighSea);
        Assert.Equal((byte)0x10, (byte)EnvironmentFlags.Underwater);
        Assert.Equal((byte)0x20, (byte)EnvironmentFlags.HighLiquid);
        Assert.Equal((byte)0x40, (byte)EnvironmentFlags.Liquid);
        Assert.Equal(EnvironmentFlags.InMagma | EnvironmentFlags.InSlime, EnvironmentFlags.MaskLiquidHazard);
    }

    [Fact]
    public void Options_DefaultToTheVmangosValues_AndTheMaximumNeverUndercutsTheMinimum()
    {
        var options = new LocomotionOptions();
        Assert.Equal((60u, 60u, 1u, 605u, 610u, false), (options.MirrorTimerFatigueMaxSec, options.MirrorTimerBreathMaxSec, options.MirrorTimerEnvironmentalMaxSec, options.EnvironmentalDamageMin, options.EnvironmentalDamageMax, options.SlimeDamage));

        var inverted = new LocomotionOptions { EnvironmentalDamageMin = 700, EnvironmentalDamageMax = 650 };
        Assert.Equal([nameof(LocomotionOptions.EnvironmentalDamageMax)], inverted.Normalize());
        Assert.Equal(700u, inverted.EnvironmentalDamageMax);
    }
}
