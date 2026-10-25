using System.Numerics;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>The pure route planner (<see cref="PlayerbotTravelPlanner"/>): no world, no session.</summary>
public sealed class PlayerbotTravelPlannerTests
{
    private const uint EasternKingdoms = 0, Kalimdor = 1;

    private static TravelPlace P(uint map, float x, float y = 0f) => new(map, new Vector3(x, y, 0f));

    private static TravelFacts Facts(uint cap = 10_000, bool hearth = false, TravelPlace? bind = null) => new()
    {
        Alliance = true, Level = 40, Money = 100_000, SpendCap = cap, HearthReady = hearth, Bind = bind,
    };

    private static ShipRoute Ship(uint entry, TravelMeans means, TravelPlace a, TravelPlace b, float period) => new(entry, means, TravelAccess.Both, a, b, period);

    private static TravelMeans[] Means(TravelRoute? r) => r!.Legs.Select(l => l.Means).ToArray();

    [Fact]
    public void SameMap_IsOneWalkLeg()
    {
        TravelRoute? r = PlayerbotTravelPlanner.Plan(Facts(), P(EasternKingdoms, 0), P(EasternKingdoms, 700));
        Assert.NotNull(r);
        TravelLeg leg = Assert.Single(r.Legs);
        Assert.Equal(TravelMeans.Walk, leg.Means);
        Assert.Equal(700f * 1.3f / 7f, leg.Seconds, 0.01f);
        Assert.Equal(0u, leg.Copper);
    }

    [Fact]
    public void CrossContinent_TakesTheOnlyBoat()
    {
        TravelPlace menethil = P(EasternKingdoms, 100), auberdine = P(Kalimdor, 100);
        TravelFacts f = Facts() with { Ships = [Ship(176310, TravelMeans.Boat, menethil, auberdine, 300)] };
        TravelRoute? r = PlayerbotTravelPlanner.Plan(f, P(EasternKingdoms, 0), P(Kalimdor, 0));
        Assert.NotNull(r);
        Assert.Equal([TravelMeans.Walk, TravelMeans.Boat, TravelMeans.Walk], Means(r));
        Assert.Equal(176310u, r.Legs[1].TransportEntry);
        Assert.Equal(menethil, r.Legs[1].From);
        Assert.Equal(auberdine, r.Legs[1].To);
    }

    [Fact]
    public void BoatsRunBothWays()
    {
        TravelPlace a = P(EasternKingdoms, 100), b = P(Kalimdor, 100);
        TravelFacts f = Facts() with { Ships = [Ship(7, TravelMeans.Boat, a, b, 300)] };
        TravelRoute? r = PlayerbotTravelPlanner.Plan(f, P(Kalimdor, 0), P(EasternKingdoms, 0));
        Assert.NotNull(r);
        Assert.Equal(TravelMeans.Boat, r.Legs[1].Means);
        Assert.Equal(b, r.Legs[1].From);
        Assert.Equal(a, r.Legs[1].To);
    }

    [Fact]
    public void PicksTheZeppelinOverASlowerBoat()
    {
        TravelPlace a = P(EasternKingdoms, 100), b = P(Kalimdor, 100);
        TravelFacts f = Facts() with { Ships = [Ship(1, TravelMeans.Boat, a, b, 500), Ship(2, TravelMeans.Zeppelin, a, b, 300)] };
        TravelRoute? r = PlayerbotTravelPlanner.Plan(f, P(EasternKingdoms, 0), P(Kalimdor, 0));
        Assert.NotNull(r);
        Assert.Equal(TravelMeans.Zeppelin, r.Legs[1].Means);
        Assert.Equal(2u, r.Legs[1].TransportEntry);
    }

    [Fact]
    public void PicksTheHearthstoneWhenTheBindIsNearTheGoalAndBeatsTheBoat()
    {
        TravelPlace port = P(EasternKingdoms, 100), far = P(Kalimdor, 100), bind = P(Kalimdor, 990);
        TravelFacts f = Facts(hearth: true, bind: bind) with { Ships = [Ship(1, TravelMeans.Boat, port, far, 300)] };
        TravelRoute? r = PlayerbotTravelPlanner.Plan(f, P(EasternKingdoms, 0), P(Kalimdor, 1000));
        Assert.NotNull(r);
        Assert.Equal([TravelMeans.Hearthstone, TravelMeans.Walk], Means(r));
        Assert.Equal(bind, r.Legs[0].To);
    }

    [Fact]
    public void IgnoresTheBindWhenTheHearthstoneIsNotReady()
    {
        TravelPlace port = P(EasternKingdoms, 100), far = P(Kalimdor, 100), bind = P(Kalimdor, 990);
        TravelFacts f = Facts(hearth: false, bind: bind) with { Ships = [Ship(1, TravelMeans.Boat, port, far, 300)] };
        TravelRoute? r = PlayerbotTravelPlanner.Plan(f, P(EasternKingdoms, 0), P(Kalimdor, 1000));
        Assert.NotNull(r);
        Assert.DoesNotContain(TravelMeans.Hearthstone, Means(r));
        Assert.Contains(TravelMeans.Boat, Means(r));
    }

    [Fact]
    public void ChoosesTheMagesOwnTeleport()
    {
        TravelFacts f = Facts() with { OwnTeleports = [new OwnTeleport(3567, P(Kalimdor, 950))] };
        TravelRoute? r = PlayerbotTravelPlanner.Plan(f, P(EasternKingdoms, 0), P(Kalimdor, 1000));
        Assert.NotNull(r);
        Assert.Equal([TravelMeans.MageTeleport, TravelMeans.Walk], Means(r));
        Assert.Equal(3567u, r.Legs[0].SpellId);
        Assert.Equal(0u, r.Legs[0].Copper);
    }

    [Fact]
    public void ChoosesAPortalWhenAMageIsNearAndTheFeeFitsTheCap()
    {
        TravelPlace mage = P(EasternKingdoms, 40), landing = P(Kalimdor, 950);
        TravelFacts f = Facts(cap: 1000) with { Portals = [new PortalOffer(0xABCD, 11417, mage, landing, 500)] };
        TravelRoute? r = PlayerbotTravelPlanner.Plan(f, P(EasternKingdoms, 0), P(Kalimdor, 1000));
        Assert.NotNull(r);
        Assert.Equal([TravelMeans.Walk, TravelMeans.MagePortal, TravelMeans.Walk], Means(r));
        TravelLeg portal = r.Legs[1];
        Assert.Equal(11417u, portal.SpellId);
        Assert.Equal(0xABCDul, portal.MageGuid);
        Assert.Equal(500u, portal.Copper);
        Assert.Equal(500u, r.Copper);
    }

    [Fact]
    public void RejectsAPortalWhoseFeeExceedsTheCap_AndFallsBackToTheBoat()
    {
        TravelPlace mage = P(EasternKingdoms, 40), landing = P(Kalimdor, 950);
        TravelPlace port = P(EasternKingdoms, 100), far = P(Kalimdor, 100);
        TravelFacts f = Facts(cap: 1000) with
        {
            Portals = [new PortalOffer(1, 11417, mage, landing, 2000)],
            Ships = [Ship(1, TravelMeans.Boat, port, far, 300)],
        };
        TravelRoute? r = PlayerbotTravelPlanner.Plan(f, P(EasternKingdoms, 0), P(Kalimdor, 1000));
        Assert.NotNull(r);
        Assert.DoesNotContain(TravelMeans.MagePortal, Means(r));
        Assert.Contains(TravelMeans.Boat, Means(r));
        Assert.True(r.Copper <= 1000);
    }

    [Fact]
    public void FlightReplacesALongWalkWhenFasterAndAffordable()
    {
        TravelPlace a = P(EasternKingdoms, 0), b = P(EasternKingdoms, 5000);
        TravelFacts f = Facts(cap: 1000) with { Flight = (_, _) => (200f, 100u) };
        TravelRoute? r = PlayerbotTravelPlanner.Plan(f, a, b);
        Assert.NotNull(r);
        TravelLeg leg = Assert.Single(r.Legs);
        Assert.Equal(TravelMeans.FlightPath, leg.Means);
        Assert.Equal(200f, leg.Seconds);
        Assert.Equal(100u, leg.Copper);
    }

    [Fact]
    public void FlightOverTheCapIsRejected_AndSoIsASlowerOne()
    {
        TravelPlace a = P(EasternKingdoms, 0), b = P(EasternKingdoms, 5000);
        TravelRoute? tooDear = PlayerbotTravelPlanner.Plan(Facts(cap: 50) with { Flight = (_, _) => (200f, 100u) }, a, b);
        Assert.NotNull(tooDear);
        Assert.Equal(TravelMeans.Walk, Assert.Single(tooDear.Legs).Means);

        TravelRoute? slower = PlayerbotTravelPlanner.Plan(Facts() with { Flight = (_, _) => (5000f, 10u) }, a, b);
        Assert.NotNull(slower);
        Assert.Equal(TravelMeans.Walk, Assert.Single(slower.Legs).Means);
    }

    [Fact]
    public void NotWalkableOnOneMap_ForcesTheBoat()
    {
        // A river at x = 500 that cannot be walked across; a ferry joins its banks.
        TravelPlace west = P(EasternKingdoms, 400), east = P(EasternKingdoms, 600);
        TravelFacts f = Facts() with
        {
            Walkable = (x, y) => (x.Position.X < 500f) == (y.Position.X < 500f),
            Ships = [Ship(9, TravelMeans.Boat, west, east, 120)],
        };
        TravelRoute? r = PlayerbotTravelPlanner.Plan(f, P(EasternKingdoms, 0), P(EasternKingdoms, 1000));
        Assert.NotNull(r);
        Assert.Equal([TravelMeans.Walk, TravelMeans.Boat, TravelMeans.Walk], Means(r));
    }

    [Fact]
    public void NotWalkableOnOneMap_ForcesThePortal()
    {
        TravelPlace mage = P(EasternKingdoms, 300), landing = P(EasternKingdoms, 700);
        TravelFacts f = Facts() with
        {
            Walkable = (x, y) => (x.Position.X < 500f) == (y.Position.X < 500f),
            Portals = [new PortalOffer(5, 1, mage, landing, 0)],
        };
        TravelRoute? r = PlayerbotTravelPlanner.Plan(f, P(EasternKingdoms, 0), P(EasternKingdoms, 1000));
        Assert.NotNull(r);
        Assert.Equal([TravelMeans.Walk, TravelMeans.MagePortal, TravelMeans.Walk], Means(r));
    }

    [Fact]
    public void Unreachable_IsNull()
    {
        TravelFacts f = Facts() with { Walkable = (_, _) => false };
        Assert.Null(PlayerbotTravelPlanner.Plan(f, P(EasternKingdoms, 0), P(EasternKingdoms, 1000)));
    }

    [Fact]
    public void NoShipsAndACrossMapGoal_IsNull()
    {
        Assert.Null(PlayerbotTravelPlanner.Plan(Facts(), P(EasternKingdoms, 0), P(Kalimdor, 0)));
    }

    [Fact]
    public void ABindOnTheStartMapDoesNotReachAnotherMap()
    {
        TravelFacts f = Facts(hearth: true, bind: P(EasternKingdoms, 300));
        Assert.Null(PlayerbotTravelPlanner.Plan(f, P(EasternKingdoms, 0), P(Kalimdor, 0)));
    }

    [Fact]
    public void StartAtTheGoal_IsAnEmptyRoute()
    {
        TravelRoute? same = PlayerbotTravelPlanner.Plan(Facts(), P(EasternKingdoms, 10), P(EasternKingdoms, 10));
        Assert.NotNull(same);
        Assert.Empty(same.Legs);
        Assert.Equal(0f, same.Seconds);
        TravelRoute? close = PlayerbotTravelPlanner.Plan(Facts(), P(EasternKingdoms, 10), P(EasternKingdoms, 13));
        Assert.NotNull(close);
        Assert.Empty(close.Legs);
        // Five yards or more is a walk.
        Assert.Single(PlayerbotTravelPlanner.Plan(Facts(), P(EasternKingdoms, 10), P(EasternKingdoms, 16))!.Legs);
    }

    [Fact]
    public void NoTwoWalkLegsFollowEachOther()
    {
        TravelPlace a = P(EasternKingdoms, 100), b = P(Kalimdor, 100);
        TravelFacts f = Facts() with
        {
            Ships = [Ship(1, TravelMeans.Boat, a, b, 300)],
            OwnTeleports = [new OwnTeleport(1, P(EasternKingdoms, 200))],
        };
        TravelRoute? r = PlayerbotTravelPlanner.Plan(f, P(EasternKingdoms, 0), P(Kalimdor, 400));
        Assert.NotNull(r);
        for (int i = 1; i < r.Legs.Count; i++)
            Assert.False(r.Legs[i].Means == TravelMeans.Walk && r.Legs[i - 1].Means == TravelMeans.Walk);
    }

    [Fact]
    public void TwoWalksNeverChain()
    {
        // from -> mid and mid -> to are walkable but from -> to is not: the legs would be two walks in a row, which the planner never emits.
        TravelPlace from = P(EasternKingdoms, 0), mid = P(EasternKingdoms, 20), to = P(EasternKingdoms, 600);
        TravelFacts f = Facts() with
        {
            OwnTeleports = [new OwnTeleport(1, mid)],
            Walkable = (x, y) => !(x.Position.X == 0f && y.Position.X == 600f),
        };
        TravelRoute? r = PlayerbotTravelPlanner.Plan(f, from, to);
        Assert.NotNull(r);
        for (int i = 1; i < r.Legs.Count; i++)
            Assert.False(r.Legs[i].Means == TravelMeans.Walk && r.Legs[i - 1].Means == TravelMeans.Walk);
    }

    [Fact]
    public void LegsChainFromTheStartToTheGoal()
    {
        TravelPlace a = P(EasternKingdoms, 100), b = P(Kalimdor, 100);
        TravelFacts f = Facts() with { Ships = [Ship(1, TravelMeans.Boat, a, b, 300)] };
        TravelPlace from = P(EasternKingdoms, 0), to = P(Kalimdor, 0);
        TravelRoute r = PlayerbotTravelPlanner.Plan(f, from, to)!;
        Assert.Equal(from, r.Legs[0].From);
        for (int i = 1; i < r.Legs.Count; i++) Assert.Equal(r.Legs[i - 1].To, r.Legs[i].From);
        Assert.Equal(to, r.Legs[^1].To);
    }

    [Fact]
    public void Ties_AreDeterministic()
    {
        TravelPlace a = P(EasternKingdoms, 100), b = P(Kalimdor, 100);
        ShipRoute first = Ship(1, TravelMeans.Boat, a, b, 300), second = Ship(2, TravelMeans.Boat, a, b, 300);
        TravelFacts f = Facts() with { Ships = [first, second] };
        TravelRoute r1 = PlayerbotTravelPlanner.Plan(f, P(EasternKingdoms, 0), P(Kalimdor, 0))!;
        TravelRoute r2 = PlayerbotTravelPlanner.Plan(f, P(EasternKingdoms, 0), P(Kalimdor, 0))!;
        Assert.Equal(r1.Legs, r2.Legs);
        Assert.Equal(1u, r1.Legs[1].TransportEntry);
        TravelRoute swapped = PlayerbotTravelPlanner.Plan(f with { Ships = [second, first] }, P(EasternKingdoms, 0), P(Kalimdor, 0))!;
        Assert.Equal(2u, swapped.Legs[1].TransportEntry);
    }

    [Fact]
    public void EqualTime_PrefersLessCopper()
    {
        TravelPlace mage = P(EasternKingdoms, 0), landing = P(Kalimdor, 0);
        TravelFacts f = Facts() with { Portals = [new PortalOffer(1, 1, mage, landing, 300), new PortalOffer(2, 1, mage, landing, 100)] };
        TravelRoute? r = PlayerbotTravelPlanner.Plan(f, mage, P(Kalimdor, 100));
        Assert.NotNull(r);
        Assert.Equal(2ul, r.Legs[0].MageGuid);
    }

    /// <summary>
    /// #1456: a walk wrongly taken as joined to the dock left the planner only one way to cross a gap, the ship there and straight back
    /// (Teldrassil top "walked" to Auberdine, the boat ridden to Rut'theran and back). A ship is never taken straight back the way it came.
    /// </summary>
    [Fact]
    public void AShipIsNeverTakenStraightBack_EvenWhenThatIsTheOnlyWayToSplitTwoWalks()
    {
        TravelPlace a = P(Kalimdor, 100), b = P(Kalimdor, 4000), start = P(Kalimdor, 3990), goal = P(Kalimdor, 3900);
        TravelFacts f = Facts() with
        {
            Ships = [Ship(176244, TravelMeans.Boat, a, b, 300)],
            // From the start only the near port is walkable; the goal cannot be walked to from the start or from the far port.
            Walkable = (x, y) => x == start ? y == b : !(y == goal && x == a),
        };
        TravelRoute? r = PlayerbotTravelPlanner.Plan(f, start, goal);
        Assert.Null(r); // the only route would be walk, boat there, boat back, walk
    }
}

public sealed class PlayerbotTravelOptionsTests
{
    [Fact]
    public void Defaults_Validate()
    {
        new PlayerbotTravelOptions().Validate();
    }

    [Theory]
    [InlineData(9.9f, false)]
    [InlineData(10f, true)]
    [InlineData(1000f, true)]
    [InlineData(1000.1f, false)]
    [InlineData(float.NaN, false)]
    public void Validate_MageServiceRadiusRange(float yards, bool ok) => Assert.Equal(ok, Throws(new PlayerbotTravelOptions { MageServiceRadiusYards = yards }) is false);

    [Theory]
    [InlineData(100_000u, true)]
    [InlineData(100_001u, false)]
    public void Validate_FeeRange(uint fee, bool ok) => Assert.Equal(ok, Throws(new PlayerbotTravelOptions { MageServiceFeeCopper = fee }) is false);

    [Theory]
    [InlineData(0u, true)]
    [InlineData(10_000_000u, true)]
    [InlineData(10_000_001u, false)]
    public void Validate_SpendRange(uint copper, bool ok) => Assert.Equal(ok, Throws(new PlayerbotTravelOptions { MaxTravelSpendCopper = copper }) is false);

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(300, true)]
    [InlineData(3601, false)]
    public void Validate_BackoffRange(int seconds, bool ok) => Assert.Equal(ok, Throws(new PlayerbotTravelOptions { TransitBackoffSeconds = seconds, TransitBackoffMaxSeconds = 3600 }) is false);

    [Theory]
    [InlineData(9, false)]
    [InlineData(86_400, true)]
    [InlineData(86_401, false)]
    public void Validate_BackoffMaxRange(int seconds, bool ok) => Assert.Equal(ok, Throws(new PlayerbotTravelOptions { TransitBackoffSeconds = 10, TransitBackoffMaxSeconds = seconds }) is false);

    [Fact]
    public void Validate_MaxBelowFirstThrows()
    {
        Assert.True(Throws(new PlayerbotTravelOptions { TransitBackoffSeconds = 120, TransitBackoffMaxSeconds = 60 }));
    }

    [Fact]
    public void BackoffMs_DoublesFromTheFirstWaitToTheCap()
    {
        var o = new PlayerbotTravelOptions();
        Assert.Equal([60_000u, 120_000u, 240_000u, 300_000u, 300_000u], Enumerable.Range(1, 5).Select(o.BackoffMs).ToArray());
        Assert.Equal(60_000u, o.BackoffMs(0));
    }

    [Fact]
    public void BackoffMs_FirstEqualToMaxNeverGrows()
    {
        var o = new PlayerbotTravelOptions { TransitBackoffSeconds = 300, TransitBackoffMaxSeconds = 300 };
        Assert.All(Enumerable.Range(1, 6), n => Assert.Equal(300_000u, o.BackoffMs(n)));
    }

    private static bool Throws(PlayerbotTravelOptions o)
    {
        try { o.Validate(); return false; }
        catch (InvalidOperationException) { return true; }
    }
}
