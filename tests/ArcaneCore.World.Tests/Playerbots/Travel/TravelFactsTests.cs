using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Ship;
using ArcaneCore.World.Tests.Playerbots.Taxi;
using ArcaneCore.World.Tests.Transports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// The planner's choice from a bot's real facts (<see cref="PlayerbotTravel.BuildFacts(WorldSession, Player, PlayerbotOptions)"/>): every means
/// is planned while its switch is on, and never once it is off; with Travel off no journey begins.
/// </summary>
public sealed class TravelFactsTests
{
    private static readonly Dictionary<uint, ShipClass> s_zeppelin = new()
    {
        [TransportWorldContent.Crossing] = new(TravelMeans.Zeppelin, TravelAccess.Horde, "synthetic zeppelin"),
    };

    private static TravelMeans[]? Means(TravelRoute? route) => route?.Legs.Select(l => l.Means).ToArray();

    private static TravelRoute? Plan(WorldSession session, Player player, PlayerbotOptions options, TravelPlace goal, IReadOnlyList<ShipRoute>? ships = null)
        => PlayerbotTravelPlanner.Plan(
            PlayerbotTravel.BuildFacts(session, player, options, ships ?? PlayerbotShipNetwork.Routes(session.World), null), TravelPlace.Of(player), goal);

    [Fact]
    public async Task Boats_ArePlannedFromTheWorldsShips_AndNeverWhenSwitchedOff()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        await world.PlaceAsync(0, ShipTestWorld.Start);
        var goal = new TravelPlace(1, TravelJourneyShipTests.Goal);
        PlayerbotOptions options = TravelJourneyShipTests.Options();
        await world.OnWorldAsync(() =>
        {
            TravelFacts facts = PlayerbotTravel.BuildFacts(world.Session, world.Player, options);
            Assert.Equal([20808u], facts.Ships.Select(s => s.Entry));
            Assert.Equal([TravelMeans.Walk, TravelMeans.Boat, TravelMeans.Walk], Means(Plan(world.Session, world.Player, options, goal)));

            options.Travel.Boats = false;
            Assert.Empty(PlayerbotTravel.BuildFacts(world.Session, world.Player, options).Ships);
            Assert.Null(Plan(world.Session, world.Player, options, goal)); // nothing else crosses: unreachable
            var travel = new PlayerbotTravel(options);
            Assert.False(travel.CanReach(world.Session, world.Player, 1, goal.Position));
            Assert.False(travel.TryBegin(world.Session, world.Player, 1, goal.Position, out string? why));
            Assert.Contains("no route", why);
            Assert.False(travel.IsTraveling(world.Player));
        });
    }

    [Fact]
    public async Task Zeppelins_ArePlannedForAHordeBot_AndNeverWhenSwitchedOff()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync(race: 2);
        await world.PlaceAsync(0, ShipTestWorld.Start);
        var goal = new TravelPlace(1, TravelJourneyShipTests.Goal);
        PlayerbotOptions options = TravelJourneyShipTests.Options();
        await world.OnWorldAsync(() =>
        {
            IReadOnlyList<ShipRoute> ships = PlayerbotShipNetwork.Routes(world.System, s_zeppelin);
            Assert.Equal([TravelMeans.Walk, TravelMeans.Zeppelin, TravelMeans.Walk], Means(Plan(world.Session, world.Player, options, goal, ships)));
            options.Travel.Zeppelins = false;
            Assert.Empty(PlayerbotTravel.BuildFacts(world.Session, world.Player, options, ships, null).Ships);
            Assert.Null(Plan(world.Session, world.Player, options, goal, ships));
        });
    }

    [Fact]
    public async Task TheHearthstone_IsPlannedWhenReady_AndNeverWhenSwitchedOff()
    {
        await using MageTravelRig rig = await MageTravelRig.StartAsync();
        WorldSession session = await rig.EnterAsync("Factshearth");
        Player player = session.Player!;
        PlayerbotOptions options = MageTravelRig.Options();
        var goal = new TravelPlace(0, MageTravelFixture.Bind + new Vector3(3f, 0f, 0f));
        await rig.OnWorld(() =>
        {
            MageTravelRig.Give(player, MageTravelFixture.Hearthstone);
            player.Home = new HomeBind(0, 12, MageTravelFixture.Bind.X, MageTravelFixture.Bind.Y, MageTravelFixture.Bind.Z);
            TravelFacts facts = PlayerbotTravel.BuildFacts(session, player, options);
            Assert.True(facts.HearthReady);
            Assert.Equal(new TravelPlace(0, MageTravelFixture.Bind), facts.Bind);
            Assert.Equal(TravelMeans.Hearthstone, Means(Plan(session, player, options, goal))![0]);

            options.Travel.Hearthstone = false;
            Assert.False(PlayerbotTravel.BuildFacts(session, player, options).HearthReady);
            Assert.Equal([TravelMeans.Walk], Means(Plan(session, player, options, goal)));
            return 0;
        });
    }

    [Fact]
    public async Task PortalsAndOwnTeleports_ArePlannedWithTheMageService_AndNeverWhenItIsOff()
    {
        await using MageTravelRig rig = await MageTravelRig.StartAsync();
        WorldSession mageSession = await rig.EnterAsync("Factsmage");
        WorldSession requesterSession = await rig.EnterAsync("Factsrider");
        Player mage = mageSession.Player!, requester = requesterSession.Player!;
        PlayerbotOptions options = MageTravelRig.Options();
        var stormwind = new TravelPlace(0, MageTravelFixture.Stormwind);
        await rig.OnWorld(() =>
        {
            rig.Teach(mage, MageTravelFixture.PortalSpell, MageTravelFixture.TeleportSpell);
            MageTravelRig.Give(mage, MageTravelFixture.RuneOfPortals);
            MageTravelRig.Give(mage, MageTravelFixture.RuneOfTeleportation);
            PlayerbotMageService.DriveMage(mageSession, mage, options, 500); // the idle mage lists itself

            Assert.Contains(TravelMeans.MagePortal, Means(Plan(requesterSession, requester, options, stormwind))!);
            Assert.Equal([TravelMeans.MageTeleport], Means(Plan(mageSession, mage, options, stormwind)));

            options.Travel.MageService = false;
            Assert.Empty(PlayerbotTravel.BuildFacts(requesterSession, requester, options).Portals);
            Assert.Empty(PlayerbotTravel.BuildFacts(mageSession, mage, options).OwnTeleports);
            Assert.Equal([TravelMeans.Walk], Means(Plan(requesterSession, requester, options, stormwind)));
            Assert.Equal([TravelMeans.Walk], Means(Plan(mageSession, mage, options, stormwind)));
            return 0;
        });
    }

    [Fact]
    public async Task AKnownFlightPath_IsPlannedWithTaxiOn_AndNeverWhenTaxiIsOff()
    {
        TaxiServices.Current.Value = new TaxiFixture();
        WorldTestHost host = WorldTestHost.Start();
        try
        {
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            QuestNpcServices services = host.WorldServices.GetRequiredService<QuestNpcFeature>().Services;
            await host.WaitForWorldAsync(() => services.StateOf(session.Player!) is { Loaded: true }, "npc state loaded");
            PlayerbotOptions options = TravelJourneyShipTests.Options();
            options.Taxi.MinRouteYards = 100f;
            var goal = new TravelPlace(0, new Vector3(TaxiFixture.StartX + 250f, TaxiFixture.StartY + 262f, TaxiFixture.Z));
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                WorldCollision.Of(host.World).Install(lineOfSight: new MageTravelFlatFloor(), pathfinder: new MageTravelOpenPathfinder());
                player.Money = 1_000;
                uint[] mask = services.StateOf(player)!.TaxiMask;
                foreach (uint node in new uint[] { 1, 2, 3 }) mask[(node - 1) / 32] |= 1u << (int)((node - 1) % 32);

                TravelRoute? flying = Plan(session, player, options, goal);
                Assert.Equal(TravelMeans.FlightPath, Means(flying)![0]);
                Assert.Equal(150u, flying!.Copper); // the fares of paths 1-2 and 2-3

                options.Taxi.Enabled = false;
                Assert.Null(PlayerbotTravel.BuildFacts(session, player, options).Flight);
                Assert.Equal([TravelMeans.Walk], Means(Plan(session, player, options, goal)));
            });
            session.Kick();
            await session.ManagedClosed;
        }
        finally
        {
            await host.DisposeAsync();
            TaxiServices.Current.Value = null;
        }
    }

    [Fact]
    public async Task WithTravelOff_NoJourneyBegins_AndAJourneyUnderWayEndsWithoutAReport()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        await world.PlaceAsync(0, ShipTestWorld.Start);
        PlayerbotOptions options = TravelJourneyShipTests.Options();
        var travel = new PlayerbotTravel(options);
        Vector3 near = ShipTestWorld.Start + new Vector3(40f, 0f, 0f);
        using ArcaneCore.Kernel.Diagnostics.AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        await world.OnWorldAsync(() =>
        {
            Assert.True(travel.TryBegin(world.Session, world.Player, 0, near, out string? why), why);
            options.Travel.Enabled = false;
            Assert.Equal(TravelLegStep.Idle, PlayerbotTravel.Drive(world.Session, world.Player, options, 100, out string? failure));
            Assert.Null(failure);
            Assert.Equal(JourneyState.SwitchedOff, PlayerbotTravel.JourneyOf(world.Player)!.State);
            Assert.False(travel.IsTraveling(world.Player));

            Assert.False(travel.CanReach(world.Session, world.Player, 1, TravelJourneyShipTests.Goal));
            Assert.False(travel.TryBegin(world.Session, world.Player, 1, TravelJourneyShipTests.Goal, out why));
            Assert.Contains("switched off", why);
            Assert.False(travel.IsTraveling(world.Player));
        });
        Assert.False(capture.OfSubsystem(PlayerbotAnomalies.Subsystem(PlayerbotTravel.AnomalyArea)).Any());
    }
}

/// <summary>The walkable answer for a place the walking region graph could not place (<see cref="PlayerbotTravel.Joined"/>).</summary>
public sealed class TravelWalkableUndecidedTests
{
    private static TravelPlace Kalimdor(float x, float y) => new(1, new Vector3(x, y, 0f));

    [Fact]
    public void UndecidedPlace_AcrossTheTeldrassilWater_IsNotWalkable()
    {
        TravelPlace upper = Kalimdor(9054f, 1599f), mainlandDock = Kalimdor(6873f, -2039f);
        Assert.False(PlayerbotTravel.Joined(upper, mainlandDock, 284, true, -1, false, []));
        Assert.False(PlayerbotTravel.Joined(mainlandDock, upper, -1, false, 284, true, []));
        Assert.False(PlayerbotTravel.Joined(upper, mainlandDock, -1, false, -1, false, []));
    }

    [Fact]
    public void UndecidedPlace_OnTheSameSide_IsWalkable()
    {
        Assert.True(PlayerbotTravel.Joined(Kalimdor(9054f, 1599f), Kalimdor(8800f, 900f), 284, true, -1, false, []));
        Assert.True(PlayerbotTravel.Joined(Kalimdor(6873f, -2039f), Kalimdor(6000f, -3000f), -1, false, 12764, true, []));
    }

    [Fact]
    public void DecidedPlaces_AreWalkableOnlyInOneComponent()
    {
        Assert.True(PlayerbotTravel.Joined(Kalimdor(0f, 0f), Kalimdor(10f, 10f), 5, true, 5, true, []));
        Assert.False(PlayerbotTravel.Joined(Kalimdor(0f, 0f), Kalimdor(10f, 10f), 5, true, 6, true, []));
    }

    // Rut'theran's portal (trigger 542): the entrance on the island at sea level, the exit on Teldrassil's upper ground 1,300 yards up.
    private static readonly TriggerTeleport s_portalUp = new(542, Kalimdor(8799f, 970f, 30f), Kalimdor(9946f, 2613f, 1316f));
    private static TravelPlace Kalimdor(float x, float y, float z) => new(1, new Vector3(x, y, z));

    [Fact]
    public void UndecidedPort_AndAnyUpperTeldrassilPlace_AreNotWalkable_WhereTheTriggerJoinsThem()
    {
        TravelPlace port = Kalimdor(8706f, 965f, 13f), upperGoal = Kalimdor(9054f, 1599f, 1335f), dolanaar = Kalimdor(9701f, 945f, 1291f);
        TriggerTeleport[] triggers = [s_portalUp];
        Assert.False(PlayerbotTravel.Joined(port, upperGoal, -1, false, 284, true, triggers));
        Assert.False(PlayerbotTravel.Joined(upperGoal, port, -1, false, -1, false, triggers));
        Assert.False(PlayerbotTravel.Joined(port, dolanaar, -1, false, -1, false, triggers));
        Assert.False(PlayerbotTravel.Joined(dolanaar, port, -1, false, -1, false, triggers));
    }

    [Fact]
    public void UndecidedPlace_OnOneLevelOfTheTrigger_IsStillWalkable()
    {
        TriggerTeleport[] triggers = [s_portalUp];
        Assert.True(PlayerbotTravel.Joined(Kalimdor(8706f, 965f, 13f), Kalimdor(8800f, 900f, 20f), -1, false, -1, false, triggers));
        Assert.True(PlayerbotTravel.Joined(Kalimdor(9054f, 1599f, 1335f), Kalimdor(9701f, 945f, 1291f), -1, false, 284, true, triggers));
        // Without the trigger the same two levels stay "undecided counts as joined" (the strait rule does not apply on this side).
        Assert.True(PlayerbotTravel.Joined(Kalimdor(8706f, 965f, 13f), Kalimdor(9054f, 1599f, 1335f), -1, false, 284, true, []));
    }
}
