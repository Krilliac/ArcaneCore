using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Transports;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Transports.TransportTestKit;

namespace ArcaneCore.Game.Tests.Transports;

/// <summary>
/// The H1 transport anomaly keys, each proven both ways: <c>transport/passenger-left-behind</c>, <c>transport/passenger-desync</c>,
/// <c>transport/stalled</c>, <c>transport/missing-on-map</c> and <c>move/client-transport-unknown</c>. Player-subject reports are
/// captured by the player's unique guid; ship-subject reports use private template entries so no other test's ship can be mistaken for
/// the one under test (the ledger is shared static state and test classes run in parallel).
/// </summary>
public sealed class TransportAnomalyTests
{
    private const string LeftBehind = "transport/passenger-left-behind";
    private const string Desync = "transport/passenger-desync";
    private const string Stalled = "transport/stalled";
    private const string Missing = "transport/missing-on-map";
    private const string Unknown = "move/client-transport-unknown";

    private static AnomalyCapture CaptureFor(uint guid)
        => AnomalyLedger.Shared.Capture(n => n.Subject.Contains($"(guid {guid})", StringComparison.Ordinal));

    private static AnomalyCapture CaptureEntry(uint entry)
        => AnomalyLedger.Shared.Capture(n => n.Key.StartsWith("transport/", StringComparison.Ordinal) && n.Subject.Contains($"transport entry {entry}", StringComparison.Ordinal));

    private static void ClientMoves(WorldRuntime world, Player player, MovementInfo movement)
    {
        MovementInfo previous = player.Movement;
        var context = new MovementObserverContext(player, world, WorldOpcode.MsgMoveHeartbeat);
        MovementObservers.Before(context, in previous, ref movement);
        player.ApplyClientMovement(movement, world.NowMs);
        MovementObservers.After(context, in previous);
    }

    /// <summary>A ferry on map 0 (waiting at its first stop) and a player standing aboard it at a small offset.</summary>
    private static (WorldRuntime World, TransportSystem System, ShipTransport Ferry, Player Player) Aboard4(uint guid)
    {
        WorldRuntime world = ManualWorld();
        TransportSystem system = Install(world, Ferry);
        ShipTransport ferry = system.FindByEntry(Ferry)!;
        Player player = TestWorld.CreatePlayer(guid, 100, 0, new FakeSession((int)guid));
        world.AddPlayer(player);
        ClientMoves(world, player, Aboard(ferry, 4f, 0f, 6f));
        Assert.Same(ferry, player.Transport);
        return (world, system, ferry, player);
    }

    private static TransportTemplate Private(uint entry, uint path = FerryPath)
        => TransportTemplateBuilder.Build(Ship(entry, path), Paths(), _ => false, null, out TransportTemplateError error)
            ?? throw new InvalidOperationException("synthetic route refused: " + error);

    private static TransportSystem InstallPrivate(WorldRuntime world, params TransportTemplate[] templates)
    {
        var system = new TransportSystem(world, templates);
        TransportSystem.Register(world, system);
        system.Install();
        return system;
    }

    // --- passenger-left-behind: the movement observer ----------------------------------------------------------------------

    [Fact]
    public void LeftBehind_AnOffsetBeyondTheBound_WhileAboard_IsReportedOncePerEpisode_AndAgainAfterRecovering()
    {
        (WorldRuntime world, _, ShipTransport ferry, Player player) = Aboard4(4_201_001);
        using AnomalyCapture capture = CaptureFor(4_201_001);

        ClientMoves(world, player, Aboard(ferry, 300f, 0f, 6f));
        ClientMoves(world, player, Aboard(ferry, 310f, 0f, 6f));
        Assert.Equal(1, capture.Count(LeftBehind));
        Assert.False(capture.Any(Desync), "an out-of-bounds offset is judged by the bound, not by the desync test: " + capture.Describe());

        ClientMoves(world, player, Aboard(ferry, 4f, 0f, 6f));
        Assert.Equal(1, capture.Count(LeftBehind));
        ClientMoves(world, player, Aboard(ferry, 300f, 0f, 6f));
        Assert.Equal(2, capture.Count(LeftBehind));
    }

    [Theory]
    [InlineData(4_201_002u, 250.0f, false)] // |offset| <= MaxPassengerOffset (250) is on the ship
    [InlineData(4_201_003u, 250.5f, true)]
    public void LeftBehind_TheOffsetBound_IsTwoHundredFiftyYards(uint guid, float offset, bool reported)
    {
        (WorldRuntime world, _, ShipTransport ferry, Player player) = Aboard4(guid);
        using AnomalyCapture capture = CaptureFor(guid);

        ClientMoves(world, player, Aboard(ferry, offset, 0f, 6f));

        Assert.Equal(reported, capture.Any(LeftBehind));
    }

    [Fact]
    public void LeftBehind_ABoardingClaimWithAnOutOfBoundsOffset_IsReported_AndBoardsNothing()
    {
        WorldRuntime world = ManualWorld();
        ShipTransport ferry = Install(world, Ferry).FindByEntry(Ferry)!;
        Player player = TestWorld.CreatePlayer(4_201_004, 100, 0, new FakeSession(4_201_004));
        world.AddPlayer(player);
        using AnomalyCapture capture = CaptureFor(4_201_004);

        ClientMoves(world, player, Aboard(ferry, 400f, 0f, 6f));

        Assert.Equal(1, capture.Count(LeftBehind));
        Assert.Contains("attempted to board", Assert.Single(capture.Notices, n => n.Key == LeftBehind).Detail);
        Assert.Null(player.Transport);
    }

    [Fact]
    public void LeftBehind_OrdinaryBoardingAndMovingAboard_AndAChaosProbe_ReportNothing()
    {
        WorldRuntime world = ManualWorld();
        ShipTransport ferry = Install(world, Ferry).FindByEntry(Ferry)!;
        Player player = TestWorld.CreatePlayer(4_201_005, 100, 0, new FakeSession(4_201_005));
        world.AddPlayer(player);
        using AnomalyCapture capture = CaptureFor(4_201_005);

        ClientMoves(world, player, Aboard(ferry, 4f, 0f, 6f));
        ClientMoves(world, player, Aboard(ferry, 5f, 1f, 6f));
        Assert.Same(ferry, player.Transport);

        player.Locomotion.SuppressAnomalies = true;
        ClientMoves(world, player, Aboard(ferry, 900f, 0f, 6f));

        Assert.False(capture.Any(LeftBehind), capture.Describe());
    }

    // --- passenger-desync: the movement observer ----------------------------------------------------------------------------

    [Fact]
    public void Desync_AWorldPositionFarFromTheShipOffset_IsReportedOncePerEpisode_AndAgainAfterRecovering()
    {
        (WorldRuntime world, _, ShipTransport ferry, Player player) = Aboard4(4_202_001);
        using AnomalyCapture capture = CaptureFor(4_202_001);

        MovementInfo lying = Aboard(ferry, 4f, 0f, 6f);
        lying.X += 100f;
        ClientMoves(world, player, lying);
        ClientMoves(world, player, lying);
        Assert.Equal(1, capture.Count(Desync));
        Assert.False(capture.Any(LeftBehind), capture.Describe());

        ClientMoves(world, player, Aboard(ferry, 4f, 0f, 6f)); // consistent again: the episode ends
        ClientMoves(world, player, lying);
        Assert.Equal(2, capture.Count(Desync));
    }

    [Theory]
    [InlineData(4_202_002u, 24.0f, false)] // PassengerDesyncThresholdYards = 25
    [InlineData(4_202_003u, 26.0f, true)]
    public void Desync_TheThreshold_IsTwentyFiveYards(uint guid, float error, bool reported)
    {
        (WorldRuntime world, _, ShipTransport ferry, Player player) = Aboard4(guid);
        using AnomalyCapture capture = CaptureFor(guid);

        MovementInfo block = Aboard(ferry, 4f, 0f, 6f);
        block.X += error;
        ClientMoves(world, player, block);

        Assert.Equal(reported, capture.Any(Desync));
    }

    // --- move/client-transport-unknown ---------------------------------------------------------------------------------------

    private static MovementInfo ClaimsTransport(ulong guid) => new()
    {
        Flags = MovementFlags.OnTransport, X = 100, Y = 0, Z = 5, TransportGuid = guid, TransportX = 1, TransportY = 0, TransportZ = 1,
    };

    [Fact]
    public void ClientTransportUnknown_AClaimOnAShipThatDoesNotExist_IsReportedOncePerGuid()
    {
        WorldRuntime world = ManualWorld();
        Install(world, Ferry);
        Player player = TestWorld.CreatePlayer(4_203_001, 100, 0, new FakeSession(4_203_001));
        world.AddPlayer(player);
        using AnomalyCapture capture = CaptureFor(4_203_001);
        ulong ghostShip = 0x1FC0_0000_0000_0000UL | 123_456;

        ClientMoves(world, player, ClaimsTransport(ghostShip));
        ClientMoves(world, player, ClaimsTransport(ghostShip));
        ClientMoves(world, player, ClaimsTransport(ghostShip));
        Assert.Equal(1, capture.Count(Unknown));

        ClientMoves(world, player, ClaimsTransport(ghostShip + 1)); // another unknown ship is a new claim
        Assert.Equal(2, capture.Count(Unknown));

        ClientMoves(world, player, new MovementInfo { X = 100, Y = 0, Z = 5 }); // leaving the claim resets the memory
        ClientMoves(world, player, ClaimsTransport(ghostShip));
        Assert.Equal(3, capture.Count(Unknown));
    }

    [Fact]
    public void ClientTransportUnknown_AKnownShip_AnEmptyGuidIsStillReported_AndAChaosProbeIsSilent()
    {
        WorldRuntime world = ManualWorld();
        ShipTransport ferry = Install(world, Ferry).FindByEntry(Ferry)!;
        Player known = TestWorld.CreatePlayer(4_203_002, 100, 0, new FakeSession(4_203_002));
        Player empty = TestWorld.CreatePlayer(4_203_003, 100, 0, new FakeSession(4_203_003));
        Player probe = TestWorld.CreatePlayer(4_203_004, 100, 0, new FakeSession(4_203_004));
        world.AddPlayer(known);
        world.AddPlayer(empty);
        world.AddPlayer(probe);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == Unknown && n.Subject.Contains("(guid 420300", StringComparison.Ordinal));

        ClientMoves(world, known, Aboard(ferry, 4f, 0f, 6f));
        probe.Locomotion.SuppressAnomalies = true;
        ClientMoves(world, probe, ClaimsTransport(0x1FC0_0000_0000_0000UL | 123_457));
        Assert.False(capture.Any(Unknown), capture.Describe());

        ClientMoves(world, empty, ClaimsTransport(0));
        Assert.Equal(1, capture.Count(Unknown));
        Assert.Contains("(guid 4203003)", Assert.Single(capture.Notices).Subject);
    }

    [Fact]
    public void ClientTransportUnknown_AnEmptyGuidRepeated_IsOneReportPerEpisode()
    {
        WorldRuntime world = ManualWorld();
        Install(world, Ferry);
        Player player = TestWorld.CreatePlayer(4_203_005, 100, 0, new FakeSession(4_203_005));
        world.AddPlayer(player);
        using AnomalyCapture capture = CaptureFor(4_203_005);

        for (int i = 0; i < 5; i++)
        {
            ClientMoves(world, player, ClaimsTransport(0));
        }

        Assert.Equal(1, capture.Count(Unknown));

        ClientMoves(world, player, ClaimsTransport(0x1FC0_0000_0000_0000UL | 123_458)); // the guid changed: a new claim
        Assert.Equal(2, capture.Count(Unknown));
        ClientMoves(world, player, ClaimsTransport(0));
        Assert.Equal(3, capture.Count(Unknown));

        ClientMoves(world, player, new MovementInfo { X = 100, Y = 0, Z = 5 }); // off the transport: the episode ends
        ClientMoves(world, player, ClaimsTransport(0));
        Assert.Equal(4, capture.Count(Unknown));
    }

    // --- ShipTransport.UpdatePassengerPosition ---------------------------------------------------------------------------------

    [Fact]
    public void LeftBehind_APassengerWhoseStoredOffsetLeftTheShip_IsReportedWhenTheShipMovesIt()
    {
        (_, _, ShipTransport ferry, Player player) = Aboard4(4_204_001);
        using AnomalyCapture capture = CaptureFor(4_204_001);

        player.SetTransportData(ferry.Guid, 900f, 0f, 6f, 0f);
        ferry.UpdatePassengerPosition(player);
        ferry.UpdatePassengerPosition(player);

        Assert.Equal(1, capture.Count(LeftBehind));
        Assert.Contains("exceeds max offset", Assert.Single(capture.Notices, n => n.Key == LeftBehind).Detail);
    }

    [Fact]
    public void Desync_AShipNearTheEdgeOfTheMap_WhosePassengerWouldLandOutsideIt_IsReportedOnce_AndAnInBoundsOneIsNot()
    {
        (_, _, ShipTransport ferry, Player player) = Aboard4(4_204_002);
        using AnomalyCapture capture = CaptureFor(4_204_002);
        player.SetTransportData(ferry.Guid, 200f, 0f, 6f, 0f);

        ferry.UpdatePosition(17_000f, 0f, 0f, 0f); // 17000 + 200 is beyond the 17066 half map
        ferry.UpdatePosition(17_000f, 0f, 0f, 0f);
        Assert.Equal(1, capture.Count(Desync));
        Assert.Contains("outside valid map coordinates", Assert.Single(capture.Notices, n => n.Key == Desync).Detail);

        player.SetTransportData(ferry.Guid, 5f, 0f, 6f, 0f);
        ferry.UpdatePosition(17_000f, 0f, 0f, 0f); // valid again: the episode ends and the passenger is moved
        Assert.Equal(17_005f, player.X, 1);
        player.SetTransportData(ferry.Guid, 200f, 0f, 6f, 0f);
        ferry.UpdatePosition(17_000f, 0f, 0f, 0f);
        Assert.Equal(2, capture.Count(Desync));
    }

    [Fact]
    public void LeftBehind_APassengerOnAnotherMapThanItsShip_IsReported_UnlessItIsBeingTeleportedOrAProbe()
    {
        WorldRuntime world = ManualWorld();
        bool teleporting = false;
        LocomotionEnvironment.Register(world, new LocomotionEnvironment(new LocomotionOptions(), _ => teleporting));
        ShipTransport ferry = Install(world, Ferry).FindByEntry(Ferry)!;
        Player player = TestWorld.CreatePlayer(4_204_003, 100, 0, new FakeSession(4_204_003), mapId: 1);
        world.AddPlayer(player);
        Assert.NotSame(ferry.CurrentMap, player.Map);
        ferry.AddPassenger(player);
        using AnomalyCapture capture = CaptureFor(4_204_003);

        teleporting = true; // a far teleport in flight: the player is legitimately between maps
        ferry.UpdatePassengerPosition(player);
        Assert.False(capture.Any(LeftBehind), "while teleporting: " + capture.Describe());

        teleporting = false;
        player.Locomotion.SuppressAnomalies = true;
        ferry.UpdatePassengerPosition(player);
        Assert.False(capture.Any(LeftBehind), "chaos probe: " + capture.Describe());

        player.Locomotion.SuppressAnomalies = false;
        ferry.UpdatePassengerPosition(player);
        ferry.UpdatePassengerPosition(player);
        Assert.Equal(1, capture.Count(LeftBehind));
        Assert.Contains("without a pending teleport", Assert.Single(capture.Notices, n => n.Key == LeftBehind).Detail);
    }

    // --- TransportSystem.TeleportTransport / CarryPlayer ---------------------------------------------------------------------

    [Fact]
    public void LeftBehind_APassengerThatCannotFollowTheShipToTheNewMap_IsReported()
    {
        (_, TransportSystem system, ShipTransport ferry, Player player) = Aboard4(4_205_001);
        using AnomalyCapture capture = CaptureFor(4_205_001);
        system.TeleportPassenger = (_, _, _, _, _, _) => false;

        system.TeleportTransport(ferry, 1, 1100f, 0f, 0f, 0f);

        Assert.Equal(1, capture.Count(LeftBehind));
        Assert.Contains("could not be carried", Assert.Single(capture.Notices, n => n.Key == LeftBehind).Detail);
        Assert.Null(player.Transport);
    }

    [Fact]
    public void LeftBehind_APassengerCarriedToTheNewMap_OrToTheSameMap_ReportsNothing()
    {
        (_, TransportSystem system, ShipTransport ferry, Player player) = Aboard4(4_205_002);
        using AnomalyCapture capture = CaptureFor(4_205_002);
        int carried = 0;
        system.TeleportPassenger = (_, _, _, _, _, _) => { carried++; return true; };

        system.TeleportTransport(ferry, 1, 1100f, 0f, 0f, 0f);
        system.TeleportTransport(ferry, 0, 150f, 0f, 0f, 0f); // back to the passenger's own map: relocated, nobody to carry

        Assert.Equal(1, carried);
        Assert.Same(ferry, player.Transport);
        Assert.False(capture.Any(LeftBehind), capture.Describe());
    }

    [Fact]
    public void LeftBehind_APassengerWithNoMapWhenTheShipChangesMaps_IsReported_UnlessItIsAProbe()
    {
        WorldRuntime world = ManualWorld();
        TransportSystem system = Install(world, Ferry);
        ShipTransport ferry = system.FindByEntry(Ferry)!;
        Player stray = TestWorld.CreatePlayer(4_205_003, 100, 0, new FakeSession(4_205_003)); // never added to a map
        Player probe = TestWorld.CreatePlayer(4_205_004, 100, 0, new FakeSession(4_205_004));
        probe.Locomotion.SuppressAnomalies = true;
        ferry.AddPassenger(stray);
        ferry.AddPassenger(probe);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == LeftBehind && n.Subject.Contains("(guid 420500", StringComparison.Ordinal));

        system.TeleportTransport(ferry, 0, 100f, 0f, 0f, 0f);

        Assert.Equal(1, capture.Count(LeftBehind));
        Assert.Contains("(guid 4205003)", Assert.Single(capture.Notices).Subject);
        Assert.Empty(ferry.Passengers);
    }

    // --- login seat restore ------------------------------------------------------------------------------------------------------

    private static Player Load(uint id, TransportSeat seat, uint mapId = 0, float x = 100f)
    {
        Player player = TestWorld.CreatePlayer(id, x, 0, new FakeSession((int)id), mapId);
        player.LoginTransportSeat = seat;
        return player;
    }

    [Fact]
    public void LeftBehind_ALoginSeatWithAnOffsetOffTheShip_IsReported_AndASaneSeatIsNot()
    {
        WorldRuntime world = ManualWorld();
        TransportSystem system = Install(world, Ferry);
        int homebinds = 0;
        system.TeleportToHomebind = _ => { homebinds++; return true; };
        Player bad = Load(4_206_001, new TransportSeat(Ferry, 900f, 0f, 5f, 0f));
        Player good = Load(4_206_002, new TransportSeat(Ferry, 3f, 2f, 5f, 0f));
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Subject.Contains("(guid 420600", StringComparison.Ordinal));

        world.AddPlayer(good);
        Assert.False(capture.Any(LeftBehind), "a sane seat: " + capture.Describe());
        world.AddPlayer(bad);
        world.RunTick(50);

        Assert.Equal(1, capture.Count(LeftBehind));
        Assert.Contains("(guid 4206001)", Assert.Single(capture.Notices, n => n.Key == LeftBehind).Subject);
        Assert.Equal(1, homebinds);
    }

    [Fact]
    public void MissingOnMap_ALoginSeatOnAShipThatDoesNotSail_IsReportedOncePerShip()
    {
        WorldRuntime world = ManualWorld();
        TransportSystem system = Install(world, Ferry);
        system.TeleportToHomebind = _ => true;
        const uint Phantom = 4_207_001;
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == Missing && n.Subject == $"transport entry {Phantom}");

        world.AddPlayer(Load(4_207_002, new TransportSeat(Phantom, 1f, 0f, 1f, 0f)));
        world.AddPlayer(Load(4_207_003, new TransportSeat(Phantom, 1f, 0f, 1f, 0f)));

        Assert.Equal(1, capture.Count(Missing)); // a second character of the same missing ship is the same episode
    }

    [Fact]
    public void LeftBehind_ADeferredSeatRestoreThatCannotFollowTheShip_IsReported()
    {
        WorldRuntime world = ManualWorld();
        TransportSystem system = Install(world, Crossing);
        system.TeleportToHomebind = _ => true;
        Advance(world, 36000); // the ship is on map 1; the character saved aboard logs in on map 0
        Player stuck = Load(4_208_001, new TransportSeat(Crossing, 1f, 0f, 2f, 0f));
        using AnomalyCapture capture = CaptureFor(4_208_001);

        world.AddPlayer(stuck);
        Assert.False(capture.Any(LeftBehind), "the follow is deferred to after the map update: " + capture.Describe());
        world.RunTick(50); // FollowShip runs with no TeleportPassenger wired: nothing can carry the player to map 1

        Assert.Equal(1, capture.Count(LeftBehind));
        Assert.Contains("Deferred login seat restore", Assert.Single(capture.Notices, n => n.Key == LeftBehind).Detail);
    }

    [Fact]
    public void LeftBehind_ADeferredSeatRestoreThatFollowsTheShip_ReportsNothing()
    {
        WorldRuntime world = ManualWorld();
        TransportSystem system = Install(world, Crossing);
        var teleports = new ArcaneCore.Game.Teleport.TeleportService(world, _ => { }, _ => { });
        system.TeleportPassenger = (p, map, x, y, z, o) => teleports.TeleportTo(p, map, x, y, z, o, ArcaneCore.Game.Teleport.TeleportOptions.NotLeaveTransport);
        system.TeleportToHomebind = _ => true;
        Advance(world, 36000); // the ship is on map 1; the character saved aboard logs in on map 0
        Player follower = Load(4_208_002, new TransportSeat(Crossing, 1f, 0f, 2f, 0f));
        using AnomalyCapture capture = CaptureFor(4_208_002);

        world.AddPlayer(follower);
        world.RunTick(50);
        world.RunTick(50);

        Assert.Same(system.FindByEntry(Crossing), follower.Transport); // it really did board and follow
        Assert.False(capture.Any(LeftBehind), capture.Describe());
    }

    // --- transport/stalled -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Stalled_AShipWithNoPeriod_IsReportedOnce()
    {
        const uint Entry = 4_209_001;
        TransportTemplate template = Private(Entry);
        template.PathTime = 0;
        WorldRuntime world = ManualWorld();
        using AnomalyCapture capture = CaptureEntry(Entry);
        InstallPrivate(world, template);

        Advance(world, 2000);

        Assert.Equal(1, capture.Count(Stalled));
        Assert.Contains("Period=0", Assert.Single(capture.Notices, n => n.Key == Stalled).Detail);
    }

    [Fact]
    public void Stalled_AShipWithASingleKeyFrame_IsReportedOnce()
    {
        const uint Entry = 4_209_002;
        TransportTemplate template = Private(Entry);
        template.Frames.RemoveRange(1, template.Frames.Count - 1);
        WorldRuntime world = ManualWorld();
        using AnomalyCapture capture = CaptureEntry(Entry);
        InstallPrivate(world, template);

        Advance(world, 2000);

        Assert.Equal(1, capture.Count(Stalled));
        Assert.Contains("KeyFrames.Count=1", Assert.Single(capture.Notices, n => n.Key == Stalled).Detail);
    }

    [Fact]
    public void Stalled_TwoFullRoundTripsOfAHealthyRoute_ReportNothing()
    {
        const uint Entry = 4_209_003;
        const uint CrossingEntry = 4_209_004;
        WorldRuntime world = ManualWorld();
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key.StartsWith("transport/", StringComparison.Ordinal)
            && (n.Subject.Contains($"transport entry {Entry}", StringComparison.Ordinal) || n.Subject.Contains($"transport entry {CrossingEntry}", StringComparison.Ordinal)));
        TransportSystem system = InstallPrivate(world, Private(Entry), Private(CrossingEntry, CrossingPath));

        Advance(world, 2 * system.FindByEntry(Entry)!.Period + 1000);
        Advance(world, 2 * system.FindByEntry(CrossingEntry)!.Period);

        Assert.False(capture.Any(Stalled), capture.Describe());
        Assert.False(capture.Any(Missing), capture.Describe());
        Assert.True(system.FindByEntry(Entry)!.PathProgress > system.FindByEntry(Entry)!.Period, "the ship really sailed");
    }

    // --- transport/missing-on-map ----------------------------------------------------------------------------------------------

    [Fact]
    public void MissingOnMap_ARouteThatNeverSpawned_IsReportedOnce_AndAHealthyRouteIsNot()
    {
        const uint Orphan = 4_210_001;
        const uint Healthy = 4_210_002;
        TransportTemplate orphan = Private(Orphan);
        orphan.Maps.Clear();
        orphan.Maps.Add(7); // the route claims only a map none of its frames is on, so no map ever spawns it
        WorldRuntime world = ManualWorld();
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == Missing
            && (n.Subject == $"transport entry {Orphan}" || n.Subject == $"transport entry {Healthy}"));

        InstallPrivate(world, orphan, Private(Healthy));
        Advance(world, 1000); // the per-map update re-checks every tick: still one report

        Assert.Equal(1, capture.Count(Missing));
        Assert.Equal($"transport entry {Orphan}", Assert.Single(capture.Notices).Subject);
    }
}
