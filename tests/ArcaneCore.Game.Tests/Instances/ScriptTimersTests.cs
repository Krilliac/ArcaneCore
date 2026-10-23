using ArcaneCore.Game.Instances.Scripts;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// Pins the countdown semantics of the three scripted-AI timer variants that <see cref="ScriptTimers"/> replaced (the per-class
/// <c>Due</c>/<c>Tick</c> copies). The variants differ at the boundaries and must not be merged.
/// </summary>
public sealed class ScriptTimersTests
{
    [Theory]
    [InlineData(1000u, 999u, false, 1u)]
    [InlineData(1000u, 1000u, true, 0u)]
    [InlineData(1000u, 1001u, true, 0u)]
    [InlineData(1u, 0u, false, 1u)]
    [InlineData(0u, 0u, true, 0u)]
    [InlineData(0u, 500u, true, 0u)]
    [InlineData(uint.MaxValue, uint.MaxValue, true, 0u)]
    [InlineData(uint.MaxValue, 1u, false, uint.MaxValue - 1)]
    public void Due_FiresWhenTimerIsAtOrBelowDiff_AndLeavesZero(uint timer, uint diff, bool fires, uint expected)
    {
        Assert.Equal(fires, ScriptTimers.Due(ref timer, diff));
        Assert.Equal(expected, timer);
    }

    [Fact]
    public void Due_TimerAtZeroKeepsFiringUntilTheCallerRearms()
    {
        uint timer = 0;
        Assert.True(ScriptTimers.Due(ref timer, 1));
        Assert.True(ScriptTimers.Due(ref timer, 1));
        timer = 3000;
        Assert.False(ScriptTimers.Due(ref timer, 1));
        Assert.Equal(2999u, timer);
    }

    [Theory]
    [InlineData(1000u, 999u, false, 1u)]
    [InlineData(1000u, 1000u, true, 0u)]
    [InlineData(1000u, 1001u, true, 0u)]
    [InlineData(0u, 1u, true, 0u)]
    public void DueRetry_HasTheSameCountdownAsDue(uint timer, uint diff, bool fires, uint expected)
    {
        Assert.Equal(fires, ScriptTimers.DueRetry(ref timer, diff));
        Assert.Equal(expected, timer);
    }

    [Fact]
    public void DueRetry_StaysFiringEveryUpdateAfterAFailedCast()
    {
        uint timer = 500;
        Assert.True(ScriptTimers.DueRetry(ref timer, 500));
        // the caller's cast failed, so it did not re-arm
        Assert.True(ScriptTimers.DueRetry(ref timer, 1));
        Assert.True(ScriptTimers.DueRetry(ref timer, 10_000));
        Assert.Equal(0u, timer);
    }

    [Theory]
    [InlineData(0u, 1u, false, 0u)]
    [InlineData(0u, uint.MaxValue, false, 0u)]
    [InlineData(1000u, 999u, false, 1u)]
    [InlineData(1000u, 1000u, true, 1u)]
    [InlineData(1000u, 1001u, true, 1u)]
    [InlineData(1u, 0u, false, 1u)]
    [InlineData(1u, 1u, true, 1u)]
    public void DueLatched_ZeroIsDisabled_AndFiringLatchesAtOne(uint timer, uint diff, bool fires, uint expected)
    {
        Assert.Equal(fires, ScriptTimers.DueLatched(ref timer, diff));
        Assert.Equal(expected, timer);
    }

    [Fact]
    public void DueLatched_RetriesEveryUpdateWhileLatchedAtOne()
    {
        uint timer = 100;
        Assert.True(ScriptTimers.DueLatched(ref timer, 100));
        Assert.True(ScriptTimers.DueLatched(ref timer, 1));
        Assert.True(ScriptTimers.DueLatched(ref timer, 60_000));
        Assert.Equal(1u, timer);
    }
}
