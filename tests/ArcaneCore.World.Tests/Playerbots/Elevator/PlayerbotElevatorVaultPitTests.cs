using System.Numerics;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Elevator;

/// <summary>
/// LS-009 (live 2026-10-17): on Undercity's enclosed vault lifts the shaft reaches the doors (9.06..10.60 yards from its centre), so
/// a floor inside them under the lower stop is the shaft's bottom. The bots stranded there (1599.0,181.5,-62.0 by the lift at 1595,189)
/// failed "no way to the lower landing" 356 times and were never rescued.
/// </summary>
public sealed class PlayerbotElevatorVaultPitTests
{
    private static ElevatorLift UndercityLift()
        => new(0, [new ElevatorCar(0, 20650, 20658, new Vector3(1595.0f, 189.0f, -40.76f), new Vector3(1595.0f, 189.0f, 55.65f), 16_667)]);

    [Fact]
    public void TheBottomOfAVaultShaft_InsideTheDoors_IsAPit()
    {
        ElevatorLift lift = UndercityLift();
        Assert.True(PlayerbotElevator.InPit(lift, new Vector3(1599.0f, 181.5f, -62.0f), -62.0f)); // the live spot, 8.5 yd out
        Assert.True(PlayerbotElevator.InPit(lift, new Vector3(1595.0f, 189.0f, -62.0f), -62.0f)); // under the car
    }

    [Fact]
    public void TheLandingOutsideTheDoors_AndTheLandingFloor_AreNotAPit()
    {
        ElevatorLift lift = UndercityLift();
        Assert.False(PlayerbotElevator.InPit(lift, new Vector3(1595.0f + 12.5f, 189.0f, -40.8f), -40.8f)); // the landing ring
        Assert.False(PlayerbotElevator.InPit(lift, new Vector3(1595.0f + 12.5f, 189.0f, -62.0f), -62.0f)); // below, but outside the doors
        Assert.False(PlayerbotElevator.InPit(lift, new Vector3(1599.0f, 181.5f, -40.5f), -40.5f)); // at the stop's level
    }

    [Fact]
    public void AnOpenLift_KeepsTheCarFootprint()
    {
        // Thunder Bluff's lifts are open cars on a cliff: 8.5 yd from the shaft is not the shaft.
        var lift = new ElevatorLift(1, [new ElevatorCar(1, 1, 2, new Vector3(-1033f, -39f, 69f), new Vector3(-1033f, -39f, 140f), 40_000)]);
        Assert.False(PlayerbotElevator.InPit(lift, new Vector3(-1033f + 6f, -39f + 6f, 60f), 60f));
        Assert.True(PlayerbotElevator.InPit(lift, new Vector3(-1033f + 2f, -39f, 60f), 60f));
    }
}