using System.Numerics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.GridTerrain;
using ArcaneCore.World.Tests.Playerbots.Ship;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// Teldrassil's upper level and the same-map teleport trigger (issue cluster #1456/#1485/#1569/#1576/#1578): the planner can route over a
/// <see cref="TriggerPortal"/> (a teleport trigger walked into), and the facts of a real bot list the triggers of its map that teleport
/// within the map. The component stub and coordinates are those of <see cref="PlayerbotTravelPlannerTests.TeldrassilFacts"/>.
/// </summary>
public sealed class TravelTriggerPortalPlanTests
{
    private const uint Darnassus = 527, Ruttheran = 542;

    private static TravelPlace Tp(float x, float y, float z) => PlayerbotTravelPlannerTests.Tp(x, y, z);

    /// <summary>Darnassus trigger 527 (volume and exit as the live content has them): tree top to the Rut'theran landing.</summary>
    private static readonly TriggerPortal DarnassusToRuttheran = new(Darnassus, PlayerbotTravelPlannerTests.DarnassusPortal, PlayerbotTravelPlannerTests.RuttheranLanding);

    /// <summary>Rut'theran trigger 542: the landing back to the tree's upper level (exit 9946.25, 2612.97, 1316.49).</summary>
    private static readonly TriggerPortal RuttheranToDarnassus = new(Ruttheran, Tp(8799.41f, 969.787f, 30.24f), Tp(9946.25f, 2612.97f, 1316.49f));

    private static TravelFacts WithPortals(params TriggerPortal[] portals) => PlayerbotTravelPlannerTests.TeldrassilFacts() with { TriggerPortals = portals };

    private static TravelMeans[] Means(TravelRoute? r) => r!.Legs.Select(l => l.Means).ToArray();

    [Fact]
    public void ABotOnTheTreesUpperLevel_WalksToTheDarnassusPortal_TakesIt_ThenTheBoat()
    {
        TravelRoute? r = PlayerbotTravelPlanner.Plan(WithPortals(DarnassusToRuttheran, RuttheranToDarnassus), PlayerbotTravelPlannerTests.TreeStart, PlayerbotTravelPlannerTests.AshenvaleGoal);
        Assert.NotNull(r);
        Assert.Equal([TravelMeans.Walk, TravelMeans.AreaTrigger, TravelMeans.Walk, TravelMeans.Boat, TravelMeans.Walk], Means(r));

        Assert.Equal(PlayerbotTravelPlannerTests.DarnassusPortal, r.Legs[0].To); // the walk ends at the trigger itself
        TravelLeg portal = r.Legs[1];
        Assert.Equal(Darnassus, portal.TriggerId);
        Assert.Equal(PlayerbotTravelPlannerTests.DarnassusPortal, portal.From);
        Assert.Equal(PlayerbotTravelPlannerTests.RuttheranLanding, portal.To);
        Assert.Equal(0u, portal.Copper);
        Assert.True(portal.Seconds > 0f, "a teleport takes a loading screen");
        Assert.Equal(PlayerbotTravelPlannerTests.RuttheranLanding, r.Legs[2].From);
        Assert.Equal(PlayerbotTravelPlannerTests.RuttheranPort, r.Legs[2].To);
        Assert.Equal(176244u, r.Legs[3].TransportEntry);
        Assert.Equal(PlayerbotTravelPlannerTests.AshenvaleGoal, r.Legs[4].To);

        // No walk joins two places of different components: the planner never plans the fictitious walk off the tree.
        TravelFacts f = WithPortals(DarnassusToRuttheran, RuttheranToDarnassus);
        foreach (TravelLeg walk in r.Legs.Where(l => l.Means == TravelMeans.Walk))
            Assert.True(f.Walkable!(walk.From, walk.To), $"walk leg {walk.From} to {walk.To} crosses components");
    }

    [Fact]
    public void ABotAlreadyBelowTheTree_DoesNotTakeThePortal()
    {
        TravelRoute? r = PlayerbotTravelPlanner.Plan(WithPortals(DarnassusToRuttheran, RuttheranToDarnassus), Tp(8700f, 900f, 10f), PlayerbotTravelPlannerTests.AshenvaleGoal);
        Assert.NotNull(r);
        Assert.Equal([TravelMeans.Walk, TravelMeans.Boat, TravelMeans.Walk], Means(r));
    }

    [Fact]
    public void AGoalOnTheSameWalkableComponent_IsOneWalk_NeverAPortal()
    {
        TravelRoute? r = PlayerbotTravelPlanner.Plan(WithPortals(DarnassusToRuttheran, RuttheranToDarnassus), PlayerbotTravelPlannerTests.TreeStart, Tp(9500f, 1500f, 1330f));
        Assert.NotNull(r);
        Assert.Equal([TravelMeans.Walk], Means(r));
    }

    /// <summary>A trigger is one way: from its entrance to its exit. With only 527 a bot at Rut'theran has no way up; with 542 it has.</summary>
    [Fact]
    public void ATriggerIsOneWay_TheReturnNeedsItsOwnTrigger()
    {
        TravelPlace landing = Tp(8700f, 900f, 10f), treeGoal = Tp(9800f, 2000f, 1320f);
        Assert.Null(PlayerbotTravelPlanner.Plan(WithPortals(DarnassusToRuttheran), landing, treeGoal));

        TravelRoute? back = PlayerbotTravelPlanner.Plan(WithPortals(DarnassusToRuttheran, RuttheranToDarnassus), landing, treeGoal);
        Assert.NotNull(back);
        Assert.Equal([TravelMeans.Walk, TravelMeans.AreaTrigger, TravelMeans.Walk], Means(back));
        Assert.Equal(Ruttheran, back.Legs[1].TriggerId);
    }

    [Fact]
    public void WithNoTriggerPortals_TheTreeStillHasNoRoute()
        => Assert.Null(PlayerbotTravelPlanner.Plan(WithPortals(), PlayerbotTravelPlannerTests.TreeStart, PlayerbotTravelPlannerTests.AshenvaleGoal));

    // ---- Facts from a real bot's world ----

    private static AreaTriggerTemplate Sphere(uint id, uint map, Vector3 at) => new(id, map, at.X, at.Y, at.Z, 3f, 0, 0, 0, 0, "trigger " + id);

    private static AreaTriggerTeleport Teleport(uint id, uint targetMap, Vector3 to) => new(id, "teleport " + id, "", 0, targetMap, to.X, to.Y, to.Z, 0f);

    /// <summary>
    /// The facts list exactly the area triggers of the bot's map whose teleport leads within that map: 527 and 542 are in; a trigger to
    /// another map, a trigger with no teleport row (a tavern) and a same-map trigger on another map are out. A trigger the journey
    /// excluded drops out of the next facts.
    /// </summary>
    [Fact]
    public async Task TheFacts_HoldTheSameMapTeleportTriggersOfTheBotsMap_LessTheExcludedOnes()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        await world.PlaceAsync(0, ShipTestWorld.Start);
        var darnassusAt = new Vector3(9947.48f, 2630.04f, 1318.6f);
        var ruttheranExit = new Vector3(8785.79f, 966.983f, 30.1999f);
        var ruttheranAt = new Vector3(8799.41f, 969.787f, 30.24f);
        var darnassusExit = new Vector3(9946.25f, 2612.97f, 1316.49f);
        const uint toOtherMap = 700, tavern = 701, otherMapsOwn = 702;
        PlayerbotOptions options = TravelJourneyShipTests.Options();
        await world.OnWorldAsync(() =>
        {
            WorldMaps maps = WorldMaps.Of(world.Host.World);
            maps.ReplaceAreaTriggerTables(maps.BuildAreaTriggerTables(InMemoryMapDataStore.Content with
            {
                AreaTriggers =
                [
                    Sphere(Darnassus, 0, darnassusAt), Sphere(Ruttheran, 0, ruttheranAt),
                    Sphere(toOtherMap, 0, new Vector3(100f, 100f, 0f)), Sphere(tavern, 0, new Vector3(200f, 200f, 0f)),
                    Sphere(otherMapsOwn, 1, new Vector3(300f, 300f, 0f)),
                ],
                AreaTriggerTeleports =
                [
                    Teleport(Darnassus, 0, ruttheranExit), Teleport(Ruttheran, 0, darnassusExit),
                    Teleport(toOtherMap, 1, new Vector3(-400f, -2600f, 96f)), Teleport(otherMapsOwn, 1, new Vector3(310f, 310f, 0f)),
                ],
            }));

            TravelFacts facts = PlayerbotTravel.BuildFacts(world.Session, world.Player, options);
            Assert.Equal([Darnassus, Ruttheran], facts.TriggerPortals.Select(p => p.TriggerId).Order());
            TriggerPortal darnassus = facts.TriggerPortals.Single(p => p.TriggerId == Darnassus);
            Assert.Equal(new TravelPlace(0, darnassusAt), darnassus.Entrance);
            Assert.Equal(new TravelPlace(0, ruttheranExit), darnassus.Exit);
            TriggerPortal ruttheran = facts.TriggerPortals.Single(p => p.TriggerId == Ruttheran);
            Assert.Equal(new TravelPlace(0, ruttheranAt), ruttheran.Entrance);
            Assert.Equal(new TravelPlace(0, darnassusExit), ruttheran.Exit);

            var excluded = new TravelExclusions();
            excluded.Triggers.Add(Darnassus);
            TravelFacts less = PlayerbotTravel.BuildFacts(world.Session, world.Player, options, PlayerbotShipNetwork.Routes(world.Host.World), excluded);
            Assert.Equal([Ruttheran], less.TriggerPortals.Select(p => p.TriggerId));

            excluded.Triggers.Add(Ruttheran);
            Assert.Empty(PlayerbotTravel.BuildFacts(world.Session, world.Player, options, PlayerbotShipNetwork.Routes(world.Host.World), excluded).TriggerPortals);
        });
    }

    // --- requirements prefilter (BuildFacts leaves out a trigger the bot cannot pass) ------------------------------------

    private static AreaTriggerTeleport Row(byte level = 0, uint item = 0, uint item2 = 0, uint quest = 0, uint condition = 0)
        => new(527, "Test", "", level, 0, 0f, 0f, 0f, 0f, item, item2, quest, condition);

    [Fact]
    public void MeetsRequirements_PassesALevelAtOrBelowTheBots_AndNoOtherRequirement()
    {
        Assert.True(PlayerbotTravel.MeetsRequirements(Row(), 1));
        Assert.True(PlayerbotTravel.MeetsRequirements(Row(level: 10), 10));
        Assert.False(PlayerbotTravel.MeetsRequirements(Row(level: 11), 10));
    }

    [Theory]
    [InlineData(1u, 0u, 0u, 0u)]
    [InlineData(0u, 1u, 0u, 0u)]
    [InlineData(0u, 0u, 1u, 0u)]
    [InlineData(0u, 0u, 0u, 1u)]
    public void MeetsRequirements_RefusesAnItemQuestOrConditionRequirement(uint item, uint item2, uint quest, uint condition)
        => Assert.False(PlayerbotTravel.MeetsRequirements(Row(item: item, item2: item2, quest: quest, condition: condition), 60));
}
