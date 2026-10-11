using System.Numerics;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Elevator;

/// <summary>
/// The lifts as the data describes them (<see cref="ElevatorNetwork.Build"/>) and the choice of a lift for a trip
/// (<see cref="ElevatorNetwork.Choose"/>), on the classic-db and TransportAnimation.dbc rows (<see cref="ElevatorTestData"/>) with a fixture
/// of the zones the real terrain gives at the places involved (<c>PlayerbotElevatorRealTerrainTests</c> checks them on the terrain).
/// </summary>
public sealed class PlayerbotElevatorNetworkTests
{
    private static readonly Vector3 Bloodhoof = new(-2300f, -380f, -9f);
    private static readonly Vector3 ThunderBluffMesa = new(-1280f, 120f, 131f);
    private static readonly Vector3 Crossroads = new(-450f, -2650f, 96f);

    /// <summary>The zones (and areas) of the places, as the 5875 terrain and AreaTable.dbc give them.</summary>
    private static readonly ElevatorSurvey Zones = new(at => (MathF.Round(at.X), MathF.Round(at.Y)) switch
    {
        (-986f, -3797f) => (17u, 392u), // Ratchet
        (-450f, -2650f) => (17u, 0u), // the Crossroads
        (-5450f, -3750f) or (-4817f, -2079f) => (400u, 0u), // Thousand Needles
        (-5455f, -2443f) => (400u, 484u), // Freewind Post
        (-1221f, 1742f) => (405u, 0u), // Desolace
        (-2300f, -380f) => (215u, 0u), // Mulgore
        (-1280f, 120f) => (1638u, 0u), // Thunder Bluff
        _ => (0u, 0u),
    });

    private static ElevatorLift Lift(ElevatorNetwork network, float x, float y)
        => network.Lifts.Single(l => Vector2.Distance(l.Centre, new Vector2(x, y)) < 15f);

    [Fact]
    public void TheRows_GiveFourLiftsOfTwoCars_WithTheLevelsOfTheirPaths()
    {
        ElevatorNetwork network = ElevatorTestData.Network();
        Assert.Equal(4, network.Lifts.Count);
        Assert.All(network.Lifts, lift => Assert.Equal(2, lift.Cars.Count));
        ElevatorLift great = Lift(network, -4668f, -1839f);
        Assert.Equal([16874u, 16876u], great.Cars.Select(c => c.SpawnGuid));
        ElevatorCar top = great.Cars[0], bottom = great.Cars[1];
        Assert.Equal(85.4053f, top.High.Z, 3);
        Assert.Equal(85.4053f - 129.299881f, top.Low.Z, 3);
        Assert.Equal(-44.1442f, bottom.Low.Z, 3);
        Assert.Equal(-44.1442f + 129.55368f, bottom.High.Z, 3);
        Assert.Equal(30_000u, bottom.CycleMs);
        Assert.Equal("the Great Lift", ElevatorNetwork.RegionsOf(great)!.Name);
        Assert.Equal("the Freewind Post lift", ElevatorNetwork.RegionsOf(Lift(network, -5391f, -2497f))!.Name);
    }

    /// <summary>Tram cars (2,480 yards along a tunnel), plungers and doors (under 20 yards), and disabled spawns are no lifts.</summary>
    [Fact]
    public void OnlyCarsThatRiseStraightUp_AreLifts()
    {
        var templates = new Dictionary<uint, GameObjectTemplate>
        {
            [1] = ElevatorTestData.Template(1), [2] = ElevatorTestData.Template(2), [3] = ElevatorTestData.Template(3),
            [4] = new GameObjectTemplate { Entry = 4, Type = (uint)GameObjectType.Door, Data = new uint[GameObjectTemplate.DataCount] },
        };
        var animation = new TransportAnimationCatalog([
            (1u, new TransportAnimationNode(0, 0, 0, 0)), (1u, new TransportAnimationNode(10_000, 2482.3f, 0, 0)), // a tram car
            (2u, new TransportAnimationNode(0, 0, 0, 0)), (2u, new TransportAnimationNode(10_000, 0, 0, -12.1f)), // a plunger
            (3u, new TransportAnimationNode(0, 0, 0, 0)), (3u, new TransportAnimationNode(10_000, 0, 0, 60f)), // a lift
            (4u, new TransportAnimationNode(0, 0, 0, 0)), (4u, new TransportAnimationNode(10_000, 0, 0, 60f)), // not a transport
        ]);
        GameObjectSpawn Spawn(uint guid, uint entry, uint flags = 0) => new()
        {
            Guid = guid, Entry = entry, MapId = 1, X = guid * 100f, Y = 0, Z = 0, Rotation3 = 1, SpawnFlags = flags,
        };
        ElevatorNetwork network = ElevatorNetwork.Build([Spawn(1, 1), Spawn(2, 2), Spawn(3, 3), Spawn(4, 4), Spawn(5, 3, flags: 0x02)],
            entry => templates.GetValueOrDefault(entry), animation);
        ElevatorLift lift = Assert.Single(network.Lifts);
        Assert.Equal(3u, Assert.Single(lift.Cars).SpawnGuid);
    }

    /// <summary>
    /// Issue #186: bots in Thousand Needles (where three stalled on quest 1111) heading for Wharfmaster Dizzywig in Ratchet take the Great
    /// Lift from its foot, and back from Ratchet from its top.
    /// </summary>
    [Fact]
    public void FromThousandNeedles_ToRatchet_TheGreatLiftFromItsFoot()
    {
        ElevatorNetwork network = ElevatorTestData.Network();
        ElevatorLift great = Lift(network, -4668f, -1839f);
        Assert.Equal((great, ElevatorLevel.Low), network.Choose(1, ElevatorTestData.ShimmeringFlatsStall, ElevatorTestData.Dizzywig, Zones));
        Assert.Equal((great, ElevatorLevel.Low), network.Choose(1, ElevatorTestData.UnderTheLiftStall, ElevatorTestData.Dizzywig, Zones));
        Assert.Equal((great, ElevatorLevel.High), network.Choose(1, ElevatorTestData.Dizzywig, ElevatorTestData.ShimmeringFlatsStall, Zones));
        Assert.Null(network.Choose(0, ElevatorTestData.ShimmeringFlatsStall, ElevatorTestData.Dizzywig, Zones)); // another map
    }

    /// <summary>
    /// Beside a lift the terrain cannot tell the top from the foot (the Great Lift's top landing lies in Thousand Needles' "The Great Lift"
    /// area), so there the height decides: a bot just off the car at the top heading for Ratchet walks on; one at the foot rides.
    /// </summary>
    [Fact]
    public void BesideTheLift_TheHeightDecidesTheLevel()
    {
        ElevatorNetwork network = ElevatorTestData.Network();
        var nearZones = new ElevatorSurvey(at => at.Y < -3000f ? (17u, 392u) : (400u, 485u));
        Assert.Null(network.Choose(1, new Vector3(-4640f, -1830f, 85.4f), ElevatorTestData.Dizzywig, nearZones));
        Assert.NotNull(network.Choose(1, new Vector3(-4690f, -1860f, -44f), ElevatorTestData.Dizzywig, nearZones));
    }

    /// <summary>
    /// Quest 5361: taken on Freewind Post's mesa, ended in Desolace (in neither region of the Freewind lift): the lift down, for a mesa is
    /// left only by its lift. The way up from the canyon floor to Freewind Post is the same lift; from Ratchet, the Great Lift down first.
    /// </summary>
    [Fact]
    public void OnAMesa_AGoalOffIt_IsTheLiftDown()
    {
        ElevatorNetwork network = ElevatorTestData.Network();
        ElevatorLift freewind = Lift(network, -5391f, -2497f);
        Assert.Equal((freewind, ElevatorLevel.High), network.Choose(1, ElevatorTestData.CliffwatcherLonghorn, ElevatorTestData.NatakaLonghorn, Zones));
        Assert.Equal((freewind, ElevatorLevel.Low), network.Choose(1, ElevatorTestData.ShimmeringFlatsStall, ElevatorTestData.CliffwatcherLonghorn, Zones));
        Assert.Equal((Lift(network, -4668f, -1839f), ElevatorLevel.High), network.Choose(1, ElevatorTestData.Dizzywig,
            ElevatorTestData.CliffwatcherLonghorn, Zones));
    }

    /// <summary>Mulgore to Thunder Bluff: a Thunder Bluff lift up (the nearer way); The Barrens to Mulgore is walkable: no lift.</summary>
    [Fact]
    public void ThunderBluff_IsReachedByItsLifts_AndMulgoreWithout()
    {
        ElevatorNetwork network = ElevatorTestData.Network();
        (ElevatorLift Lift, ElevatorLevel From)? up = network.Choose(1, Bloodhoof, ThunderBluffMesa, Zones);
        Assert.NotNull(up);
        Assert.Equal(ElevatorLevel.Low, up.Value.From);
        Assert.Equal("the Thunder Bluff lift", ElevatorNetwork.RegionsOf(up.Value.Lift)!.Name);
        Assert.Equal(ElevatorLevel.High, network.Choose(1, ThunderBluffMesa, ElevatorTestData.Dizzywig, Zones)?.From);
        Assert.Null(network.Choose(1, Crossroads, Bloodhoof, Zones));
    }
}
