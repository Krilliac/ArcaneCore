using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Ship;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>One journey is planned per bot per <see cref="PlayerbotTravel.JourneyPlanCooldownMs"/>, whatever the goal (#1453).</summary>
public sealed class TravelPlanCooldownTests
{
    [Fact]
    public async Task AfterAPlan_NoGoalIsPlannedAgainUntilTheCooldownPassed()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        await world.PlaceAsync(0, ShipTestWorld.Start);
        PlayerbotOptions options = TravelJourneyShipTests.Options();
        options.Travel.Boats = false; // no way over the sea: every plan to map 1 finds no route
        int plans = 0;
        var travel = new PlayerbotTravel(options)
        {
            ShipRoutes = world =>
            {
                plans++;
                return PlayerbotShipNetwork.Routes(world);
            },
        };

        (bool first, bool second, bool otherGoal, string? why) = await world.OnWorldAsync(() =>
        {
            bool a = travel.TryBegin(world.Session, world.Player, 1, TravelJourneyShipTests.Goal, out _);
            bool b = travel.TryBegin(world.Session, world.Player, 1, TravelJourneyShipTests.Goal, out _);
            bool c = travel.TryBegin(world.Session, world.Player, 1, TravelJourneyShipTests.Goal with { X = TravelJourneyShipTests.Goal.X + 300f }, out string? w);
            return (a, b, c, w);
        });
        Assert.False(first || second || otherGoal);
        Assert.Contains("cooling down", why);
        Assert.Equal(1, plans);

        await world.AdvanceAsync(PlayerbotTravel.JourneyPlanCooldownMs + 100);
        await world.OnWorldAsync(() => travel.TryBegin(world.Session, world.Player, 1, TravelJourneyShipTests.Goal, out _));
        Assert.Equal(2, plans);
    }
}
