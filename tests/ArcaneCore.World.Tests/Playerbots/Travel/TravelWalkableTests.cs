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
            var facts = new TravelFacts { Alliance = true, Level = 60, Money = 100_000, SpendCap = 10_000, Ships = ships, Walkable = PlayerbotTravel.WalkableFor(world.Player) };
            return PlayerbotTravelPlanner.Plan(facts, new TravelPlace(1, new Vector3(1177f, -2258f, 94f)), new TravelPlace(1, new Vector3(10261f, 728f, 1345f)));
        });
        Assert.NotNull(route);
        Assert.Contains(route.Legs, l => l.Means == TravelMeans.Boat && l.TransportEntry == 176244);
        Assert.DoesNotContain(route.Legs, l => l.Means == TravelMeans.Walk && l.From.Position.X < 7000f && l.To.Position.X > 9000f);
    }

    private static readonly TriggerPortal RuttheranToUpperLevel = new(542, new TravelPlace(1, new Vector3(8799.41f, 969.787f, 30.24f)), new TravelPlace(1, new Vector3(9946.25f, 2612.97f, 1316.49f)));

    /// <summary>
    /// Two places the region graph could not place count as joined, except at the two levels a tall same-map trigger joins (Rut'theran and
    /// Teldrassil's upper ground): a walk planned there fails only after thousands of yards, and the trigger is the way.
    /// </summary>
    [Fact]
    public void TwoUnplacedPlaces_AtTheTwoLevelsOfATallTrigger_AreNotJoined_ButOnOneLevelTheyAre()
    {
        var low = new TravelPlace(1, new Vector3(8800f, 970f, 40f));
        var high = new TravelPlace(1, new Vector3(9900f, 2600f, 1310f));
        var low2 = new TravelPlace(1, new Vector3(8900f, 1000f, 30f));
        Assert.True(PlayerbotTravel.TriggerSeparates(low, high, [RuttheranToUpperLevel]));
        Assert.True(PlayerbotTravel.TriggerSeparates(high, low, [RuttheranToUpperLevel]));
        Assert.False(PlayerbotTravel.Joined(low, high, 0, false, 0, false, [RuttheranToUpperLevel]));
        Assert.False(PlayerbotTravel.TriggerSeparates(low, low2, [RuttheranToUpperLevel]));
        Assert.True(PlayerbotTravel.Joined(low, low2, 0, false, 0, false, [RuttheranToUpperLevel]));
        Assert.True(PlayerbotTravel.Joined(low, high, 0, false, 0, false, []));
        Assert.True(PlayerbotTravel.Joined(low, high, 0, false, 0, false, null));
    }

    /// <summary>A small hop (a trigger moving the bot under 150 yards in height) joins no two levels, and placed places answer by component alone.</summary>
    [Fact]
    public void ALowTrigger_SeparatesNothing_AndPlacedPlacesAnswerByComponent()
    {
        var hop = new TriggerPortal(1, new TravelPlace(1, new Vector3(0f, 0f, 10f)), new TravelPlace(1, new Vector3(50f, 0f, 100f)));
        var a = new TravelPlace(1, new Vector3(0f, 5f, 10f));
        var b = new TravelPlace(1, new Vector3(50f, 5f, 100f));
        Assert.False(PlayerbotTravel.TriggerSeparates(a, b, [hop]));
        Assert.True(PlayerbotTravel.Joined(a, b, 3, true, 3, true, [RuttheranToUpperLevel]));
        Assert.False(PlayerbotTravel.Joined(a, b, 3, true, 4, true, []));
    }
}
