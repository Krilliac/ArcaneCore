using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Transports;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Transports;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Ship;

/// <summary>
/// Bots ride ships through the real transport system (<see cref="PlayerbotShip"/>, <see cref="PlayerbotShipNetwork"/>) on the synthetic
/// crossing of <see cref="TransportWorldContent"/>: a real managed bot session walks to the dock, steps aboard through MSG_MOVE_HEARTBEAT,
/// is carried to map 1 by the ship's far teleport (acknowledged by its own motion pump) and steps off at the other port.
/// </summary>
public sealed class PlayerbotShipTests(ITestOutputHelper output)
{
    /// <summary>The synthetic crossing (990601) classified as a Horde zeppelin, and the ferry (990600) as an Alliance boat.</summary>
    private static readonly Dictionary<uint, ShipClass> s_synthetic = new()
    {
        [TransportWorldContent.Crossing] = new(TravelMeans.Zeppelin, TravelAccess.Horde, "synthetic zeppelin"),
        [TransportWorldContent.Ferry] = new(TravelMeans.Boat, TravelAccess.Alliance, "synthetic ferry"),
    };

    private static readonly string[] s_allShipCodes =
    [
        "ship-missing", "never-docked", "board-refused", "left-ship", "wrong-map", "ride-overdue", "disembark-refused", "dock-unreachable",
        "route-bad-data",
    ];

    private static string ShipKey(string code) => PlayerbotAnomalies.Key(PlayerbotShip.AnomalyArea, code);

    private async Task TraceAsync(ShipTestWorld world, TravelLegStep step, string? failure)
    {
        string trace = await world.OnWorldAsync(() =>
        {
            Player p = world.Player;
            ShipTrip? trip = PlayerbotShip.TripOf(p);
            return $"{step} ({failure}) at {world.Host.World.NowMs} ms: map {p.MapId} at ({p.X:F1},{p.Y:F1},{p.Z:F1}); trip stage {trip?.Stage}, "
                + $"started {trip?.StartedMs}, boarded {trip?.BoardedMs}, dock {trip?.DockSpot}, offset {trip?.Offset}, left at {trip?.LeftAt}";
        });
        output.WriteLine(trace);
    }

    private static void AssertNoShipAnomaly(AnomalyCapture capture)
    {
        foreach (string code in s_allShipCodes) Assert.False(capture.Any(ShipKey(code)), $"{code} reported: {capture.Describe()}");
    }

    [Fact]
    public async Task TheNetwork_BuildsBothPortsFromTheShipData_AndFiltersByFactionAndSwitch()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        (IReadOnlyList<ShipRoute> classic, IReadOnlyList<ShipRoute> synthetic, uint period, uint ferryPeriod) = await world.OnWorldAsync(() =>
            (PlayerbotShipNetwork.Routes(world.Host.World), PlayerbotShipNetwork.Routes(world.System, s_synthetic),
                world.System.FindByEntry(20808)!.Period, world.System.FindByEntry(TransportWorldContent.Ferry)!.Period));

        // Only the classic entry is a classic route: the Booty Bay boat on the crossing path, its ports the two stop rows.
        ShipRoute boat = Assert.Single(classic);
        Assert.Equal(20808u, boat.Entry);
        Assert.Equal((TravelMeans.Boat, TravelAccess.Both), (boat.Means, boat.Access));
        Assert.Equal(ShipTestWorld.PortOnMap0, boat.PortA);
        Assert.Equal(ShipTestWorld.PortOnMap1, boat.PortB);
        Assert.Equal(period / 1000f, boat.PeriodSeconds);
        Assert.Same(classic, await world.OnWorldAsync(() => PlayerbotShipNetwork.Routes(world.Host.World))); // cached per system

        // The injected classification: the crossing as a Horde zeppelin, the ferry's two ports on map 0.
        Assert.Equal([TransportWorldContent.Ferry, TransportWorldContent.Crossing], synthetic.Select(r => r.Entry));
        ShipRoute ferry = synthetic[0];
        Assert.Equal(new TravelPlace(0, new Vector3(TransportWorldContent.StartX, TransportWorldContent.StartY, TransportWorldContent.StartZ)), ferry.PortA);
        Assert.Equal(new TravelPlace(0, new Vector3(TransportWorldContent.StartX + 300, TransportWorldContent.StartY, TransportWorldContent.StartZ)), ferry.PortB);
        Assert.Equal(ferryPeriod / 1000f, ferry.PeriodSeconds);
        ShipRoute zeppelin = synthetic[1];
        Assert.Equal((ShipTestWorld.PortOnMap0, ShipTestWorld.PortOnMap1), (zeppelin.PortA, zeppelin.PortB));

        var all = new List<ShipRoute>(synthetic) { boat };
        var options = new PlayerbotOptions();
        Assert.Equal([TransportWorldContent.Ferry, 20808u], PlayerbotShipNetwork.Filter(all, alliance: true, options).Select(r => r.Entry));
        Assert.Equal([TransportWorldContent.Crossing, 20808u], PlayerbotShipNetwork.Filter(all, alliance: false, options).Select(r => r.Entry));
        options.Travel.Boats = false;
        Assert.Empty(PlayerbotShipNetwork.Filter(all, alliance: true, options));
        Assert.Equal([TransportWorldContent.Crossing], PlayerbotShipNetwork.Filter(all, alliance: false, options).Select(r => r.Entry));
        options.Travel.Boats = true;
        options.Travel.Zeppelins = false;
        Assert.Equal([20808u], PlayerbotShipNetwork.Filter(all, alliance: false, options).Select(r => r.Entry));
        Assert.Equal([TransportWorldContent.Ferry, 20808u], PlayerbotShipNetwork.Filter(all, alliance: true, options).Select(r => r.Entry));

        Assert.True(PlayerbotShipNetwork.ConnectsMaps(classic, 0, 1));
        Assert.True(PlayerbotShipNetwork.ConnectsMaps(classic, 1, 0));
        Assert.True(PlayerbotShipNetwork.ConnectsMaps([], 0, 0));
        Assert.False(PlayerbotShipNetwork.ConnectsMaps([ferry], 0, 1));
        Assert.False(PlayerbotShipNetwork.ConnectsMaps(classic, 0, 530));
    }

    [Fact]
    public async Task ABot_BoardsTheBoatAtTheDock_CrossesToMap1Aboard_AndStepsOffAtTheOtherPort()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        ShipRoute boat = Assert.Single(await world.OnWorldAsync(() => PlayerbotShipNetwork.Routes(world.Host.World)));
        await world.PlaceAsync(0, ShipTestWorld.Start);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        string name = await world.OnWorldAsync(() => world.Player.Name);
        using AnomalyCapture everything = AnomalyLedger.Shared.Capture(n => n.Subject.Contains(name, StringComparison.Ordinal));
        var options = new PlayerbotOptions();
        Assert.True(await world.OnWorldAsync(() => (PlayerbotShip.TryBegin(world.Session, world.Player, boat, fromA: true, options, out string? why), why))
            is (true, null));

        bool aboardOnMap0 = false, aboardOnMap1 = false;
        (TravelLegStep step, string? failure) = await world.DriveAsync(options, 300_000, observe: p =>
        {
            aboardOnMap0 |= p.IsInWorld && p.MapId == 0 && p.Transport?.Entry == 20808 && (p.Movement.Flags & MovementFlags.OnTransport) != 0;
            aboardOnMap1 |= p.IsInWorld && p.MapId == 1 && p.Transport?.Entry == 20808 && (p.Movement.Flags & MovementFlags.OnTransport) != 0;
        });

        await TraceAsync(world, step, failure);
        Assert.True(step == TravelLegStep.Arrived, $"{step}: {failure}");
        Assert.True(aboardOnMap0, "the bot was never aboard at the map-0 port");
        Assert.True(aboardOnMap1, "the bot was never aboard on map 1 (carried through the map change)");
        (uint map, ShipTransport? transport, bool onTransport, float fromPort, ShipStage stage) = await world.OnWorldAsync(() =>
            (world.Player.MapId, world.Player.Transport, (world.Player.Movement.Flags & MovementFlags.OnTransport) != 0,
                Vector2.Distance(new(world.Player.X, world.Player.Y), new(ShipTestWorld.PortOnMap1.Position.X, ShipTestWorld.PortOnMap1.Position.Y)),
                PlayerbotShip.TripOf(world.Player)!.Stage));
        Assert.Equal(1u, map);
        Assert.Null(transport);
        Assert.False(onTransport);
        Assert.True(fromPort <= PlayerbotShip.BoardReachYards, $"{fromPort:0.0} yd from the port");
        Assert.Equal(ShipStage.Done, stage);
        Assert.Equal(TravelLegStep.Idle, await world.OnWorldAsync(() => PlayerbotShip.Drive(world.Session, world.Player, options, 100, out _)));
        output.WriteLine("every anomaly naming the bot: " + everything.Describe());
        AssertNoShipAnomaly(capture);
    }

    [Fact]
    public async Task AHordeBot_RidesTheZeppelin_ToMap1_WithThePerTickPump()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync(race: 2);
        ShipRoute zeppelin = (await world.OnWorldAsync(() => PlayerbotShipNetwork.Routes(world.System, s_synthetic)))
            .Single(r => r.Entry == TransportWorldContent.Crossing);
        await world.PlaceAsync(0, ShipTestWorld.Start);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        var options = new PlayerbotOptions();
        Assert.True(await world.OnWorldAsync(() => (PlayerbotShip.TryBegin(world.Session, world.Player, zeppelin, fromA: true, options, out string? why), why))
            is (true, null));

        bool aboardOnMap1 = false;
        (TravelLegStep step, string? failure) = await world.DriveAsync(options, 300_000, thinkMs: 1000, pumpShip: true, observe: p =>
            aboardOnMap1 |= p.IsInWorld && p.MapId == 1 && p.Transport?.Entry == TransportWorldContent.Crossing);

        await TraceAsync(world, step, failure);
        Assert.True(step == TravelLegStep.Arrived, $"{step}: {failure}");
        Assert.True(aboardOnMap1, "the bot was never aboard on map 1");
        (uint map, bool off, float fromPort) = await world.OnWorldAsync(() => (world.Player.MapId, world.Player.Transport is null,
            Vector2.Distance(new(world.Player.X, world.Player.Y), new(ShipTestWorld.PortOnMap1.Position.X, ShipTestWorld.PortOnMap1.Position.Y))));
        Assert.Equal((1u, true), (map, off));
        Assert.True(fromPort <= PlayerbotShip.BoardReachYards, $"{fromPort:0.0} yd from the port");
        AssertNoShipAnomaly(capture);
    }

    [Fact]
    public async Task BoatsOrZeppelinsSwitchedOff_RefuseTheLeg_AndALegNotYetAboardEndsWithoutAnAnomaly()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        ShipRoute boat = Assert.Single(await world.OnWorldAsync(() => PlayerbotShipNetwork.Routes(world.Host.World)));
        ShipRoute zeppelin = (await world.OnWorldAsync(() => PlayerbotShipNetwork.Routes(world.System, s_synthetic)))
            .Single(r => r.Entry == TransportWorldContent.Crossing) with { Access = TravelAccess.Both };
        await world.PlaceAsync(0, ShipTestWorld.Start);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);

        var options = new PlayerbotOptions();
        options.Travel.Boats = false;
        (bool begun, string? why) = await world.OnWorldAsync(() => (PlayerbotShip.TryBegin(world.Session, world.Player, boat, true, options, out string? w), w));
        Assert.False(begun);
        Assert.Contains("boats are switched off", why);
        Assert.True(await world.OnWorldAsync(() => PlayerbotShip.TryBegin(world.Session, world.Player, zeppelin, true, options, out _)));
        await world.OnWorldAsync(() => PlayerbotShip.Cancel(world.Player));
        Assert.False(await world.OnWorldAsync(() => PlayerbotShip.IsActive(world.Player)));

        options.Travel.Boats = true;
        options.Travel.Zeppelins = false;
        (begun, why) = await world.OnWorldAsync(() => (PlayerbotShip.TryBegin(world.Session, world.Player, zeppelin, true, options, out string? w), w));
        Assert.False(begun);
        Assert.Contains("zeppelins are switched off", why);

        // Begun with boats on, switched off on the way to the dock: the leg ends at the next think, and that is no anomaly.
        Assert.True(await world.OnWorldAsync(() => PlayerbotShip.TryBegin(world.Session, world.Player, boat, true, options, out _)));
        Assert.Equal(TravelLegStep.Traveling, await world.OnWorldAsync(() => PlayerbotShip.Drive(world.Session, world.Player, options, 100, out _)));
        options.Travel.Boats = false;
        (TravelLegStep step, string? failure) = await world.DriveAsync(options, 1_000);
        Assert.Equal(TravelLegStep.Failed, step);
        Assert.Contains("switched off", failure);
        AssertNoShipAnomaly(capture);
    }

    [Fact]
    public async Task FactionAccess_AnAllianceBotMayNotTakeTheHordeZeppelin_NorAHordeBotTheAllianceBoat()
    {
        await using (ShipTestWorld alliance = await ShipTestWorld.StartAsync(race: 1))
        {
            ShipRoute zeppelin = (await alliance.OnWorldAsync(() => PlayerbotShipNetwork.Routes(alliance.System, s_synthetic)))
                .Single(r => r.Entry == TransportWorldContent.Crossing);
            await alliance.PlaceAsync(0, ShipTestWorld.Start);
            (bool begun, string? why) = await alliance.OnWorldAsync(() =>
                (PlayerbotShip.TryBegin(alliance.Session, alliance.Player, zeppelin, true, new PlayerbotOptions(), out string? w), w));
            Assert.False(begun);
            Assert.Contains("Alliance bot", why);
            Assert.False(await alliance.OnWorldAsync(() => PlayerbotShip.IsActive(alliance.Player)));
        }

        await using ShipTestWorld horde = await ShipTestWorld.StartAsync(race: 2);
        ShipRoute boat = Assert.Single(await horde.OnWorldAsync(() => PlayerbotShipNetwork.Routes(horde.Host.World))) with { Access = TravelAccess.Alliance };
        await horde.PlaceAsync(0, ShipTestWorld.Start);
        (bool hordeBegun, string? hordeWhy) = await horde.OnWorldAsync(() =>
            (PlayerbotShip.TryBegin(horde.Session, horde.Player, boat, true, new PlayerbotOptions(), out string? w), w));
        Assert.False(hordeBegun);
        Assert.Contains("Horde bot", hordeWhy);

        // The Booty Bay boat itself takes both.
        Assert.True(await horde.OnWorldAsync(() =>
            PlayerbotShip.TryBegin(horde.Session, horde.Player, boat with { Access = TravelAccess.Both }, true, new PlayerbotOptions(), out _)));
    }

    [Fact]
    public async Task AShipThatNeverWaitsAtThePort_FailsTheLegNeverDocked_ReportedOnce_AndBacksTheDockOff()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        ShipRoute boat = Assert.Single(await world.OnWorldAsync(() => PlayerbotShipNetwork.Routes(world.Host.World)));
        // A port 15 yards off the stop row: the ship waits at its own row, never there.
        ShipRoute elsewhere = boat with { PortA = boat.PortA with { Position = boat.PortA.Position + new Vector3(15f, 0f, 0f) } };
        await world.PlaceAsync(0, ShipTestWorld.Start);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        var options = new PlayerbotOptions();
        Assert.True(await world.OnWorldAsync(() => PlayerbotShip.TryBegin(world.Session, world.Player, elsewhere, true, options, out _)));

        (TravelLegStep step, string? failure) = await world.DriveAsync(options, PlayerbotShip.WaitBoundMs(boat) + 60_000, thinkMs: 500);

        Assert.Equal(TravelLegStep.Failed, step);
        Assert.Contains("did not wait at the port", failure);
        Assert.Equal(1, capture.Count(ShipKey("never-docked")));
        Assert.Equal(TravelLegStep.Idle, await world.OnWorldAsync(() => PlayerbotShip.Drive(world.Session, world.Player, options, 100, out _)));
        Assert.Equal(1, capture.Count(ShipKey("never-docked")));
        Assert.Null(await world.OnWorldAsync(() => world.Player.Transport));
        (bool again, string? why) = await world.OnWorldAsync(() => (PlayerbotShip.TryBegin(world.Session, world.Player, elsewhere, true, options, out string? w), w));
        Assert.False(again);
        Assert.Contains("backed off", why);
    }

    [Fact]
    public async Task AServerThatDoesNotTakeTheBotAboard_FailsTheLegBoardRefused_ReportedOnce_AndTheBotStandsOnTheDock()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        ShipRoute boat = Assert.Single(await world.OnWorldAsync(() => PlayerbotShipNetwork.Routes(world.Host.World)));
        await world.PlaceAsync(0, ShipTestWorld.Start);
        Player bot = world.Player;
        // Whatever the bot's movement says, the ship puts it straight off again (the server refusing the passenger).
        await world.OnWorldAsync(() => world.System.PassengerBoarded += (ship, unit) =>
        {
            if (ReferenceEquals(unit, bot)) ship.RemovePassenger(unit);
        });
        using AnomalyCapture capture = BotAnomalyCapture.For(bot);
        var options = new PlayerbotOptions();
        Assert.True(await world.OnWorldAsync(() => PlayerbotShip.TryBegin(world.Session, bot, boat, true, options, out _)));

        (TravelLegStep step, string? failure) = await world.DriveAsync(options, 120_000);

        Assert.Equal(TravelLegStep.Failed, step);
        Assert.Contains("did not board it", failure);
        Assert.Equal(1, capture.Count(ShipKey("board-refused")));
        Assert.Equal(TravelLegStep.Idle, await world.OnWorldAsync(() => PlayerbotShip.Drive(world.Session, bot, options, 100, out _)));
        Assert.Equal(1, capture.Count(ShipKey("board-refused")));
        (bool aboard, bool onTransport, float z, Vector3 dock) = await world.OnWorldAsync(() =>
            (bot.Transport is not null, (bot.Movement.Flags & MovementFlags.OnTransport) != 0, bot.Z, PlayerbotShip.TripOf(bot)!.DockSpot));
        Assert.False(aboard);
        Assert.False(onTransport);
        Assert.Equal(dock.Z, z, 0.1f); // back on the dock, not hanging between it and the deck
    }

    /// <summary>
    /// Bot QA #820, #824, #792 (live 2026-10-17): a zeppelin's origin hangs about 18 yards above the tower platform it docks at (bots aboard
    /// at 77.9 over the Orgrimmar platform the mesh puts at 53.8; at 121.0 beside the Tirisfal tower over the floor at 97.5), and a deck
    /// a fixed 6 yards above the origin put the bot 24 yards above the platform: its step off found no floor within reach and it stood in
    /// the air (no route anywhere: leg-failed), dropped to the ground under the tower (dock-unreachable 0.4 yards from the port), or sent
    /// heartbeats 10 yards down that the server did not take (move-rejected). The deck is level with the dock the bot boarded from.
    /// </summary>
    [Fact]
    public async Task TheDeck_IsLevelWithTheDockTheBotBoardedFrom()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        (Vector3 offset, Vector3 seat, Vector3 dock) = await world.Host.OnWorldAsync(() =>
        {
            ShipTransport ship = world.System.Ships.First(s => s.Entry == TransportWorldContent.Crossing);
            Vector3 dockSpot = new(ship.X + 6f, ship.Y + 2f, ship.Z - 18f);
            Vector3 deck = PlayerbotShip.DeckOffset(ship, dockSpot);
            return (deck, PlayerbotShip.Seat(ship, deck), dockSpot);
        });
        Assert.Equal(-18f, offset.Z, 0.1f);
        Assert.Equal(dock.Z, seat.Z, 0.1f);
    }

    [Fact]
    public async Task Ride_WhenPlayerRepopsOrTeleportsFarFromShip_CancelsTripWithoutLeftShipAnomaly()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        ShipRoute boat = Assert.Single(await world.OnWorldAsync(() => PlayerbotShipNetwork.Routes(world.Host.World)));
        await world.PlaceAsync(0, ShipTestWorld.Start);
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        var options = new PlayerbotOptions();
        Assert.True(await world.OnWorldAsync(() => PlayerbotShip.TryBegin(world.Session, world.Player, boat, fromA: true, options, out _)));

        for (int i = 0; i < 600; i++)
        {
            bool aboard = await world.OnWorldAsync(() => PlayerbotShip.TripOf(world.Player)?.Stage == ShipStage.Ride && world.Player.Transport is not null);
            if (aboard) break;
            await world.Host.World.AdvanceClockAsync(100);
            await world.OnWorldAsync(() => PlayerbotShip.Drive(world.Session, world.Player, options, 100, out _));
        }

        Assert.Equal(ShipStage.Ride, await world.OnWorldAsync(() => PlayerbotShip.TripOf(world.Player)?.Stage));

        TravelLegStep step = await world.OnWorldAsync(() =>
        {
            Player p = world.Player;
            p.Transport?.RemovePassenger(p);
            var mov = p.Movement;
            mov.Flags &= ~MovementFlags.OnTransport;
            mov.TransportGuid = 0;
            p.ApplyMovement(mov, world.Host.World.NowMs);
            p.Relocate(ShipTestWorld.PortOnMap0.Position.X - 500f, ShipTestWorld.PortOnMap0.Position.Y - 500f, ShipTestWorld.PortOnMap0.Position.Z, 0, world.Host.World.NowMs);
            return PlayerbotShip.Drive(world.Session, p, options, 100, out _);
        });

        Assert.Equal(TravelLegStep.Idle, step);
        Assert.False(await world.OnWorldAsync(() => PlayerbotShip.IsActive(world.Player)));
        Assert.Equal(0, capture.Count(ShipKey("left-ship")));
    }

    [Fact]
    public async Task ToDock_WhenPortKeyframeIsInWaterOffMesh_ResolvesWalkablePierAndDoesNotFailDockUnreachable()
    {
        var pierMesh = new PierMeshOnly(ShipTestWorld.PortOnMap0.Position, radiusFromPortMin: 10f);
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        await world.OnWorldAsync(() => ArcaneCore.Game.Maps.Collision.WorldCollision.Of(world.Host.World).Install(
            lineOfSight: new PierFloor(ShipTestWorld.PortOnMap0.Position.Z),
            pathfinder: pierMesh));
        ShipRoute boat = Assert.Single(await world.OnWorldAsync(() => PlayerbotShipNetwork.Routes(world.Host.World)));
        await world.PlaceAsync(0, ShipTestWorld.PortOnMap0.Position + new Vector3(-55f, 0f, 0f));
        using AnomalyCapture capture = BotAnomalyCapture.For(world.Player);
        var options = new PlayerbotOptions { MaxPathPoints = 128 };
        Assert.True(await world.OnWorldAsync(() => PlayerbotShip.TryBegin(world.Session, world.Player, boat, fromA: true, options, out _)));

        (TravelLegStep step, string? failure) = await world.OnWorldAsync(() =>
            (PlayerbotShip.Drive(world.Session, world.Player, options, 100, out string? why), why));

        Assert.Equal(TravelLegStep.Traveling, step);
        Assert.Null(failure);
        Assert.NotNull(await world.OnWorldAsync(() => PlayerbotShip.TripOf(world.Player)?.Walk));
        Assert.Equal(0, capture.Count(ShipKey("dock-unreachable")));
    }

    [Fact]
    public async Task ResolveDockTarget_WhenPortKeyframeIsOnWaterPolygon_ResolvesDryPierFloorInsteadOfWater()
    {
        var waterAtPortMesh = new WaterAtPortMesh(ShipTestWorld.PortOnMap0.Position, radiusFromPortMin: 10f);
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        await world.OnWorldAsync(() => ArcaneCore.Game.Maps.Collision.WorldCollision.Of(world.Host.World).Install(
            lineOfSight: new PierFloor(ShipTestWorld.PortOnMap0.Position.Z),
            pathfinder: waterAtPortMesh));
        Vector3 toward = ShipTestWorld.PortOnMap0.Position + new Vector3(-40f, 0f, 0f);
        Vector3 resolved = await world.OnWorldAsync(() =>
            PlayerbotShip.ResolveDockTarget(world.Player.Map!, ShipTestWorld.PortOnMap0.Position, toward));

        Assert.True(Vector2.Distance(new Vector2(resolved.X, resolved.Y), new Vector2(ShipTestWorld.PortOnMap0.Position.X, ShipTestWorld.PortOnMap0.Position.Y)) >= 10f,
            $"expected dry pier >= 10 yd from water keyframe, got {resolved}");
    }

    /// <summary>The dock probes read navigation tiles without blocking the world thread: they ask non-blocking and take "pending" as no dry mesh.</summary>
    [Fact]
    public async Task ResolveDockTarget_ReadsTilesWithoutBlocking_AndAnswersPendingNotDry()
    {
        var mesh = new TileReadingMesh(ShipTestWorld.PortOnMap0.Position.Z);
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        await world.OnWorldAsync(() => ArcaneCore.Game.Maps.Collision.WorldCollision.Of(world.Host.World).Install(
            lineOfSight: new PierFloor(ShipTestWorld.PortOnMap0.Position.Z), pathfinder: mesh));
        Vector3 toward = ShipTestWorld.PortOnMap0.Position + new Vector3(-40f, 0f, 0f);
        Vector3 resolved = await world.OnWorldAsync(() =>
            PlayerbotShip.ResolveDockTarget(world.Player.Map!, ShipTestWorld.PortOnMap0.Position, toward));

        Assert.Equal(0, mesh.BlockingReads); // a blocking query would have read its tile on the world thread
        Assert.True(mesh.PendingAnswers > 0);
        Assert.Equal(ShipTestWorld.PortOnMap0.Position, resolved); // nothing is known dry yet: the port itself, the next think asks again
    }

    [Fact]
    public async Task TryBegin_WhenBotIsAlreadyAboardTheSameShip_StartsInRideStageInsteadOfRefusing()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        ShipRoute boat = Assert.Single(await world.OnWorldAsync(() => PlayerbotShipNetwork.Routes(world.Host.World)));
        await world.PlaceAsync(0, ShipTestWorld.Start);
        var options = new PlayerbotOptions();

        (bool begun, string? why, ShipStage? stage) = await world.OnWorldAsync(() =>
        {
            ShipTransport ship = world.System.FindByEntry(boat.Entry)!;
            ship.AddPassenger(world.Player);
            bool ok = PlayerbotShip.TryBegin(world.Session, world.Player, boat, fromA: true, options, out string? w);
            return (ok, w, PlayerbotShip.TripOf(world.Player)?.Stage);
        });

        Assert.True(begun, why);
        Assert.Null(why);
        Assert.Equal(ShipStage.Ride, stage);
    }

    [Fact]
    public async Task TryBegin_WhenBotHasStaleOtherTransportAttachmentOnWalkableDock_StepsOffAndBeginsLeg()
    {
        await using ShipTestWorld world = await ShipTestWorld.StartAsync();
        ShipRoute boat = Assert.Single(await world.OnWorldAsync(() => PlayerbotShipNetwork.Routes(world.Host.World)));
        await world.PlaceAsync(0, ShipTestWorld.Start);
        var options = new PlayerbotOptions();

        (bool begun, string? why, bool stillAboardOther) = await world.OnWorldAsync(() =>
        {
            ShipTransport ferry = world.System.FindByEntry(TransportWorldContent.Ferry)!;
            ferry.AddPassenger(world.Player);
            bool ok = PlayerbotShip.TryBegin(world.Session, world.Player, boat, fromA: true, options, out string? w);
            return (ok, w, world.Player.Transport is not null);
        });

        Assert.True(begun, why);
        Assert.Null(why);
        Assert.False(stillAboardOther);
    }

    private sealed class WaterAtPortMesh(Vector3 port, float radiusFromPortMin) : ArcaneCore.Game.Maps.Collision.IPathfinder, ArcaneCore.Game.Maps.Collision.IMeshPointQuery
    {
        public bool Enabled => true;

        public ArcaneCore.Game.Maps.Collision.PathResult FindPath(uint mapId, Vector3 start, Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
        {
            bool nearPort = Vector2.Distance(new Vector2(end.X, end.Y), new Vector2(port.X, port.Y)) < radiusFromPortMin;
            bool allowsDeepWater = options is null || (options.EffectiveIncludeFlags & ArcaneCore.Game.Maps.Collision.NavTerrain.Water) != 0;
            if (nearPort && !allowsDeepWater)
                return ArcaneCore.Game.Maps.Collision.PathResult.None(start);
            return ArcaneCore.Game.Maps.Collision.PathResult.StraightLine(start, end with { Z = port.Z }, ArcaneCore.Game.Maps.Collision.PathType.Normal);
        }

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, ArcaneCore.Game.Maps.Collision.PathOptions? options, out Vector3 point)
        {
            point = center;
            return false;
        }

        public ArcaneCore.Game.Maps.Collision.MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
            => new(true, false, 1f, end with { Z = port.Z });
    }

    /// <summary>A navigation mesh whose tiles are never in: a query that may not block answers pending, one that may counts as a synchronous tile read.</summary>
    private sealed class TileReadingMesh(float z) : ArcaneCore.Game.Maps.Collision.IPathfinder, ArcaneCore.Game.Maps.Collision.IMeshPointQuery
    {
        public int BlockingReads { get; private set; }
        public int PendingAnswers { get; private set; }
        public bool Enabled => true;

        public ArcaneCore.Game.Maps.Collision.PathResult FindPath(uint mapId, Vector3 start, Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
        {
            if (options is { LoadTiles: true, NonBlockingTiles: false })
            {
                BlockingReads++;
                return ArcaneCore.Game.Maps.Collision.PathResult.StraightLine(start, end with { Z = z }, ArcaneCore.Game.Maps.Collision.PathType.Normal);
            }

            PendingAnswers++;
            return new ArcaneCore.Game.Maps.Collision.PathResult(ArcaneCore.Game.Maps.Collision.PathType.TilesPending, [start]);
        }

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, ArcaneCore.Game.Maps.Collision.PathOptions? options, out Vector3 point)
        {
            point = center;
            return false;
        }

        public ArcaneCore.Game.Maps.Collision.MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
            => new(true, false, 1f, end with { Z = z });
    }

    private sealed class PierFloor(float z) : ArcaneCore.Game.Maps.Collision.ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float zSearch, float maxSearchDistance) => z;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float zSearch, out ArcaneCore.Game.Maps.Collision.ModelAreaInfo info) { info = default; return false; }
    }

    private sealed class PierMeshOnly(Vector3 port, float radiusFromPortMin) : ArcaneCore.Game.Maps.Collision.IPathfinder, ArcaneCore.Game.Maps.Collision.IMeshPointQuery
    {
        public bool Enabled => true;

        public ArcaneCore.Game.Maps.Collision.PathResult FindPath(uint mapId, Vector3 start, Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
        {
            if (Vector2.Distance(new Vector2(end.X, end.Y), new Vector2(port.X, port.Y)) < radiusFromPortMin)
                return ArcaneCore.Game.Maps.Collision.PathResult.None(start);
            return ArcaneCore.Game.Maps.Collision.PathResult.StraightLine(start, end with { Z = port.Z }, ArcaneCore.Game.Maps.Collision.PathType.Normal);
        }

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, ArcaneCore.Game.Maps.Collision.PathOptions? options, out Vector3 point)
        {
            point = center;
            return false;
        }

        public ArcaneCore.Game.Maps.Collision.MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
        {
            if (Vector2.Distance(new Vector2(start.X, start.Y), new Vector2(port.X, port.Y)) < radiusFromPortMin)
                return ArcaneCore.Game.Maps.Collision.MeshRaycastResult.Unavailable(end);
            return new ArcaneCore.Game.Maps.Collision.MeshRaycastResult(true, false, 1f, end with { Z = port.Z });
        }
    }
}
