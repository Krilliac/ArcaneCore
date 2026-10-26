using System.Numerics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Ship;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>After a plan that found no route, that destination is not planned again for <see cref="PlayerbotTravel.JourneyPlanCooldownMs"/> (#1453); others are.</summary>
public sealed class TravelPlanCooldownTests
{
    [Fact]
    public async Task AfterANoRoutePlan_ThatDestinationWaits_OtherDestinationsAreNotRefused_AndTheGmCommandPlansAtOnce()
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
        Vector3 goal = TravelJourneyShipTests.Goal, elsewhere = goal + new Vector3(500f, 0f, 0f);

        (bool first, bool again, string? waiting, string? other) = await world.OnWorldAsync(() =>
        {
            bool a = travel.TryBegin(world.Session, world.Player, 1, goal, out _);
            bool b = travel.TryBegin(world.Session, world.Player, 1, goal, out string? w);
            travel.TryBegin(world.Session, world.Player, 1, elsewhere, out string? o);
            return (a, b, w, o);
        });
        Assert.False(first || again);
        Assert.Contains("cooling down", waiting);
        Assert.DoesNotContain("cooling down", other); // the caller of another goal is not refused for this one: it plans (and finds no route) itself
        Assert.Equal(2, plans);

        // The GM command asks to plan now.
        await world.OnWorldAsync(() =>
        {
            PlayerbotTravel.ClearPlanCooldown(world.Player);
            travel.TryBegin(world.Session, world.Player, 1, goal, out _);
        });
        Assert.Equal(3, plans);

        await world.AdvanceAsync(PlayerbotTravel.JourneyPlanCooldownMs + 100);
        await world.OnWorldAsync(() => travel.TryBegin(world.Session, world.Player, 1, goal, out _));
        Assert.Equal(4, plans);
    }
}
