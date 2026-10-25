using System.Numerics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Ship;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>The walkable test of the planner's facts (<see cref="PlayerbotTravel.WalkableFor"/>) where the mesh decides nothing.</summary>
public sealed class TravelWalkableTests
{
    /// <summary>
    /// #1466: level-60 Night Elves on the Kalimdor mainland planned one Walk leg to Teldrassil (9 644 yd, three refused plans): with no mesh
    /// (or its tiles unread) nothing decided, and undecided counts as joined. The water between the mainland and Teldrassil is never walked.
    /// </summary>
    [Fact]
    public async Task WithNoMeshDeciding_TheMainlandIsNotWalkedToTeldrassil_TheAuberdineBoatIsTaken()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        var auberdine = new TravelPlace(1, new Vector3(6406.2f, 823.1f, 0f));
        var rutheran = new TravelPlace(1, new Vector3(8790f, 967f, 0f));
        ShipRoute[] ships = [new(176244, TravelMeans.Boat, TravelAccess.Both, rutheran, auberdine, 300f)];
        TravelRoute? route = await world.OnWorldAsync(() =>
        {
            var facts = new TravelFacts { Alliance = true, Level = 60, Money = 100_000, SpendCap = 10_000, Ships = ships, Walkable = PlayerbotTravel.WalkableFor(world.Player, ships) };
            return PlayerbotTravelPlanner.Plan(facts, new TravelPlace(1, new Vector3(1177f, -2258f, 94f)), new TravelPlace(1, new Vector3(10261f, 728f, 1345f)));
        });
        Assert.NotNull(route);
        Assert.Contains(route.Legs, l => l.Means == TravelMeans.Boat && l.TransportEntry == 176244);
        Assert.DoesNotContain(route.Legs, l => l.Means == TravelMeans.Walk && l.From.Position.X < 7000f && l.To.Position.X > 9000f);
    }
}
