using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The ages <c>.playerbot lift</c> reports (<see cref="ElevatorTripInfo.AgeMs"/>): both sides are wrapped World readings, so the modular difference is the true age.
/// </summary>
public sealed class PlayerbotElevatorSnapshotTests
{
    [Fact]
    public void AStampTakenThisTick_HasAgeZero()
    {
        Assert.Equal(0u, ElevatorTripInfo.AgeMs(5_000, 5_000));
    }

    [Fact]
    public void AnEarlierStamp_HasTheElapsedTimeAsItsAge()
    {
        Assert.Equal(1_250u, ElevatorTripInfo.AgeMs(6_250, 5_000));
    }

    [Fact]
    public void AStampBeforeTheWrapOfTheWorldClock_StillGivesTheTrueAge()
    {
        // The clock wrapped at 2^32 ms (49.7 days) between the stamp and now: 300 ms before the wrap plus 700 ms after it.
        Assert.Equal(1_000u, ElevatorTripInfo.AgeMs(700, uint.MaxValue - 299));
    }
}
