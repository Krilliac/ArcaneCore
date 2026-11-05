using Xunit;

namespace ArcaneCore.World.Tests.Spells.ContractSweep;

/// <summary>
/// The wait that backs the j.batch-not-settled invariant can fail: it answers false when the clock never reaches the next batch boundary and
/// true when it does. Without a stuck-clock case the check would read as green whether or not it could ever fire.
/// </summary>
public sealed class SpellSweepBatchSettleTests
{
    [Fact]
    public void AClockThatAdvances_ReachesTheNextBoundary_WithinOneWindow()
    {
        uint now = 100_010; // inside a batch, not on its boundary
        int ticks = 0;

        bool settled = SpellSweepRunner.WaitForBatchBoundary(200, () => now, diff =>
        {
            now += diff;
            ticks++;
        });

        Assert.True(settled);
        Assert.Equal(4, ticks); // 100 010 -> 100 210 crosses the 100 200 boundary on the fourth 50 ms step
    }

    [Fact]
    public void AStuckClock_NeverReachesTheBoundary_AndIsReportedAsNotSettled()
    {
        uint now = 100_010;
        int ticks = 0;

        bool settled = SpellSweepRunner.WaitForBatchBoundary(200, () => now, _ => ticks++);

        Assert.False(settled);
        Assert.Equal(5, ticks); // the wait is bounded: one window plus a tick, in 50 ms steps
    }

    [Fact]
    public void AClockThatCreepsTooSlowly_IsReportedAsNotSettled()
    {
        uint now = 100_010;

        bool settled = SpellSweepRunner.WaitForBatchBoundary(200, () => now, _ => now += 10);

        Assert.False(settled); // five 10 ms steps reach 100 060, still the batch it started in
    }
}
