using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Transports;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>The stages of one bot's boat or zeppelin leg (<see cref="PlayerbotShip"/>).</summary>
internal enum ShipStage : byte { ToDock, WaitShip, Board, Ride, Disembark, Done, Failed }

/// <summary>
/// One bot's boat or zeppelin leg (<see cref="PlayerbotShip"/>): the route, the port it leaves from and the one it rides to, and the state
/// of the stage under way. Owned by <see cref="PlayerbotShip"/>'s per-bot table; world thread only. Read-only to callers (<see cref="PlayerbotShip.TripOf"/>).
/// </summary>
internal sealed class ShipTrip(ShipRoute route, bool fromA, uint nowMs, PlayerbotOptions options)
{
    public ShipRoute Route { get; } = route;
    public bool FromA { get; } = fromA;
    public TravelPlace From => FromA ? Route.PortA : Route.PortB;
    public TravelPlace To => FromA ? Route.PortB : Route.PortA;
    public ShipStage Stage { get; internal set; } = ShipStage.ToDock;
    public uint StartedMs { get; } = nowMs;
    public uint StageMs { get; internal set; } = nowMs;

    /// <summary>The reason the leg failed (null after an arrival; while <see cref="Settling"/>, the failure the leg will end with).</summary>
    public string? Failure { get; internal set; }

    /// <summary>The anomaly code of the failure (<see cref="PlayerbotShip.AnomalyArea"/>), null for a failure that is not one (a switch turned off).</summary>
    public string? FailureCode { get; internal set; }

    /// <summary><see cref="PlayerbotShip.Cancel"/> was asked while the bot was aboard: it rides to the next port, steps off and the leg ends there.</summary>
    public bool Cancelled { get; internal set; }

    /// <summary>The ride went past its bound (<see cref="PlayerbotShip.RideGraceMs"/>): reported once, and the bot steps off at the next port it reaches.</summary>
    public bool Overdue { get; internal set; }

    /// <summary>
    /// The leg failed with the bot still aboard (<c>ride-overdue</c>, <c>disembark-refused</c>, <c>wrong-map</c>, <c>left-ship</c> while the
    /// server still carries it): reported once, and the leg stays under way, riding to the next port the ship waits at once it sailed and
    /// stepping off there; only then does it end (<see cref="ShipStage.Failed"/>, or <see cref="ShipStage.Done"/> at the arrival port).
    /// </summary>
    public bool Settling { get; internal set; }

    /// <summary>When the leg began settling (bounded by <see cref="PlayerbotShip.SettleBoundMs"/>).</summary>
    public uint SettleMs { get; internal set; }

    /// <summary>The ship has left the departure port with the bot aboard (only then may a stop at that port end the ride).</summary>
    public bool Sailed { get; internal set; }

    /// <summary>Where the bot stood on the dock before stepping aboard (it steps back there when boarding fails).</summary>
    public Vector3 DockSpot { get; internal set; }

    /// <summary>The bot's place on the deck (offset from the ship's origin).</summary>
    public Vector3 Offset { get; internal set; }

    public uint BoardedMs { get; internal set; }
    public bool SteppedOff { get; internal set; }

    /// <summary>The port the bot stepped off at (the arrival port, or the departure port after a ride that missed it).</summary>
    public TravelPlace? LeftAt { get; internal set; }

    internal PlayerbotOptions Options = options;
    internal bool Returned;
    internal bool Reported;
    internal uint LastDriveMs = nowMs;
    internal uint LastPumpMs = nowMs;
    internal uint LastStepMs = nowMs;
    internal int Attempts;
    internal int Refusals;
    internal PlayerbotRoute? Walk;
    internal TileWait Tiles;
    internal uint NextPlanMs = nowMs;
    internal float Best = float.PositiveInfinity;
    internal uint BestMs = nowMs;
    internal Vector3? StepOff;
}

/// <summary>
/// The leg driver of a boat or zeppelin crossing (docs/areas/playbots.md, "Travel"): the bot rides a real ship through the transport system
/// as a 1.12 player does, a leg the journey planner (<see cref="PlayerbotTravelPlanner"/>) plans from <see cref="PlayerbotShipNetwork"/>.
/// <list type="number">
/// <item><see cref="ShipStage.ToDock"/>: walk (<see cref="PlayerbotNavigation.PlanToward"/> / <see cref="PlayerbotNavigation.TryAdvance"/>) to the
/// walkable point nearest the departure port, the place the ship waits at; fails (<c>dock-unreachable</c>) after
/// <see cref="MaxRefusedPlans"/> refused plans in a row or <see cref="NoProgressMs"/> without getting <see cref="ProgressYards"/> closer.</item>
/// <item><see cref="ShipStage.WaitShip"/>: stand until the route's ship waits at this port (<see cref="PlayerbotShipNetwork.DockedAt"/>) with at
/// least <see cref="BoardMarginMs"/> of its stop left; bounded by 1.5 round trips (<c>never-docked</c>).</item>
/// <item><see cref="ShipStage.Board"/>: step from the dock onto the deck through the movement handler, one MSG_MOVE_HEARTBEAT every
/// <see cref="StepIntervalMs"/> at most and never farther than the bot's own speed allows, then the heartbeat with MOVEFLAG_ONTRANSPORT, the
/// ship's GUID and the bot's place on the deck (<see cref="PlayerbotMotion.SendAboard"/>); the server boards it
/// (TransportMovementObserver.AfterApply → <c>TransportSystem.BoardFromClient</c>). Confirmed by <c>player.Transport</c>; after
/// <see cref="MaxAttempts"/> heartbeats the server did not take the bot steps back onto the dock (<c>board-refused</c>).</item>
/// <item><see cref="ShipStage.Ride"/>: aboard, the ship carries the bot (the server moves passengers with it, and a map change is a far
/// teleport that keeps it aboard, acknowledged by <see cref="PlayerbotMovementControl.AcknowledgeTransfer"/>). Watched every think: still
/// aboard (<c>left-ship</c>), on the ship's map (<c>wrong-map</c>), within the round trip plus <see cref="RideGraceMs"/> (<c>ride-overdue</c>,
/// then it steps off at the next port it reaches).</item>
/// <item><see cref="ShipStage.Disembark"/>: the ship waits at the arrival port: a heartbeat without MOVEFLAG_ONTRANSPORT at the floor beside
/// the deck (<see cref="PlayerbotMotion.SendStepOff"/>; the server takes the bot off), then a short walk to the walkable point nearest the
/// port. Confirmed by <c>player.Transport</c> null on the arrival map (<c>disembark-refused</c>, <c>wrong-map</c>).</item>
/// </list>
/// Every failure is reported once per leg (<see cref="PlayerbotAnomalies"/>, area <see cref="AnomalyArea"/>); a leg that arrives reports
/// nothing. A failure at the departure dock backs that dock off (<see cref="PlayerbotTravelOptions.BackoffMs"/>, doubling per failure).
/// A switch turned off (<see cref="PlayerbotTravelOptions.Boats"/>, <see cref="PlayerbotTravelOptions.Zeppelins"/>) ends a leg that has not
/// boarded; aboard, the bot rides to the next port and the leg ends there. Nobody is ever put off a ship at sea: a failure while the server
/// still carries the bot (<see cref="ShipTrip.Settling"/>) is reported at once, but the leg stays under way (<see cref="IsActive"/>) until the
/// bot stepped off at the next port the ship waits at, bounded by <see cref="SettleBoundMs"/>.
/// <para>
/// The call order: <see cref="PlayerbotBrain"/> calls <see cref="Drive"/> every think before any other mover while <see cref="IsActive"/> and
/// moves nothing else while it answers <see cref="TravelLegStep.Traveling"/>; a fight before the bot is aboard <see cref="Cancel"/>s the leg
/// so the brain fights (the journey begins it again after); <see cref="PlayerbotTravel.CancelLegs"/> cancels it on death or when a
/// controller, the party AI, a bot-led group or a battleground takes the bot over, and ManagedPlayerbotFeature drives a leg that still holds
/// the bot aboard (its HoldAboard) until it ends at a port. <see cref="Pump"/> may run every world tick (like PlayerbotTram.Pump) so a short
/// stop at a port is not missed between thinks.
/// </para>
/// <para>Thread affinity: world thread. Per-bot state lives in a <see cref="ConditionalWeakTable{TKey,TValue}"/> keyed by the player.</para>
/// </summary>
internal static partial class PlayerbotShip
{
    internal const string AnomalyArea = "ship";

    /// <summary>A bot this close (flat yards) to a port, within <see cref="DockHeightYards"/> of its height, stands at the dock; and this close to the docked ship it may step aboard.</summary>
    internal const float BoardReachYards = 40f;

    /// <summary>
    /// The farthest (flat yards) from the departure port a leg begins (<see cref="TryBegin"/>): a journey's walk hands off within
    /// <see cref="PlayerbotTravel.HandOffYards"/> of the port, and the dock may lie <see cref="BoardReachYards"/> beyond it. The leg's own walk
    /// covers only those last yards; a bot farther out is still on its walk leg, whose failure is the walk's (issue #1456).
    /// </summary>
    internal const float BeginReachYards = PlayerbotTravel.HandOffYards + BoardReachYards;

    /// <summary>The height a dock may lie above or below the port's node (a pier above a boat at sea level; not the ground under a zeppelin tower).</summary>
    internal const float DockHeightYards = 25f;

    /// <summary>
    /// The deck: a bot stands within this rectangle of the ship's origin, level with the dock it boarded from (<see cref="DeckOffset"/>).
    /// UNVERIFIED against the hull models (the server has no transport collision): a place inside the
    /// <see cref="TransportSystem.MaxPassengerOffset"/> bound the server checks, on the dock's side of the ship.
    /// </summary>
    internal const float DeckHalfLength = 8f, DeckHalfWidth = 3f;

    /// <summary>A stepping heartbeat at most this often (a moving client reports about every 500 ms; the step is the bot's speed times the time since the last one).</summary>
    internal const uint StepIntervalMs = 250;

    /// <summary>Within this of the deck point the bot is on the deck and sends the heartbeat aboard.</summary>
    internal const float OnDeckYards = 0.5f;

    /// <summary>Heartbeats aboard (or off) the server may not take before the leg fails; refused plans to the dock in a row likewise.</summary>
    internal const int MaxAttempts = 3, MaxRefusedPlans = 3;

    /// <summary>The ship's stop must leave this long to step aboard.</summary>
    internal const uint BoardMarginMs = 2_000;

    /// <summary>Walking that gains less than this on its target within <see cref="NoProgressMs"/> is stuck.</summary>
    internal const float ProgressYards = 5f;

    internal const uint NoProgressMs = 60_000;

    /// <summary>After a path query found nothing, the next one waits this long.</summary>
    internal const uint PlanRetryMs = 2_000;

    /// <summary>A ride may last the round trip plus this before it is overdue.</summary>
    internal const uint RideGraceMs = 60_000;

    /// <summary>
    /// A leg settling aboard (<see cref="ShipTrip.Settling"/>) may ride this much longer than two round trips (two more ports at least) before
    /// it ends where it is; past it the server has refused every way off, and the leg ends <c>Failed</c> aboard (the one failure that leaves
    /// a bot on a ship, already reported).
    /// </summary>
    internal static uint SettleBoundMs(ShipRoute route) => (2 * PeriodMs(route)) + RideGraceMs;

    /// <summary>Where the bot steps off: the floor beside the deck, at most this much below or above it, within <see cref="StepOffRadiiYards"/>.</summary>
    internal const float StepOffHeightYards = 8f;

    private static readonly float[] StepOffRadiiYards = [4f, 8f, 12f, 16f, 20f, 25f, 30f];
    private const int StepOffDirections = 16;

    private static readonly ConditionalWeakTable<Player, ShipTrip> Trips = [];
    private static readonly ConditionalWeakTable<Player, Dictionary<(uint Entry, bool FromA), DockMemo>> Docks = [];

    private sealed class DockMemo
    {
        public int Failures;
        public uint UntilMs;
        public bool ShipMissingReported;
    }

    /// <summary>The bot's leg, under way or ended (inspection and tests).</summary>
    internal static ShipTrip? TripOf(Player player) => Trips.TryGetValue(player, out ShipTrip? trip) ? trip : null;

    /// <summary>Whether a leg is under way (not done, not failed).</summary>
    internal static bool IsActive(Player player)
        => Trips.TryGetValue(player, out ShipTrip? trip) && trip.Stage is not (ShipStage.Done or ShipStage.Failed);

    /// <summary>
    /// Whether the departure dock of <paramref name="entry"/> (port A with <paramref name="fromA"/>) is backed off for the bot at
    /// <paramref name="nowMs"/>: a leg from it failed lately (<see cref="TryBegin"/> refuses it; the journey planner leaves the route out).
    /// </summary>
    internal static bool IsBackedOff(Player player, uint entry, bool fromA, uint nowMs)
        => Docks.TryGetValue(player, out Dictionary<(uint Entry, bool FromA), DockMemo>? docks) && docks.TryGetValue((entry, fromA), out DockMemo? memo)
            && memo.Failures > 0 && unchecked((int)(memo.UntilMs - nowMs)) > 0;

    /// <summary>Whether the bot rides the ship of its leg now (the server's passenger link).</summary>
    internal static bool IsAboard(Player player)
        => Trips.TryGetValue(player, out ShipTrip? trip) && trip.Stage is not (ShipStage.Done or ShipStage.Failed)
            && player.Transport is { } ship && ship.Entry == trip.Route.Entry;

    /// <summary>
    /// Begin the leg on <paramref name="route"/> from port A (<paramref name="fromA"/>) or B. Refused (false, with why) when the means is
    /// switched off, the bot's faction may not ride it, the bot is dead, aboard something or not on the departure map, the arrival map is
    /// not one bots may walk (<see cref="PlayerbotOptions.AllowedMaps"/>), the ship does not sail (<c>ship-missing</c>, reported once until a
    /// leg of that dock succeeds), the departure dock is backed off, or the bot stands more than <see cref="BeginReachYards"/> from the departure
/// port (not reported: the walk there is someone else's). A leg of the same route and direction under way answers true.
    /// </summary>
    internal static bool TryBegin(WorldSession session, Player player, ShipRoute route, bool fromA, PlayerbotOptions options, out string? why)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(options);
        why = null;
        if (Trips.TryGetValue(player, out ShipTrip? current) && current.Stage is not (ShipStage.Done or ShipStage.Failed))
        {
            if (current.Route.Entry == route.Entry && current.FromA == fromA) return true;
            why = $"ship: a leg on ship {current.Route.Entry} is under way";
            return false;
        }

        if (route.Means is not (TravelMeans.Boat or TravelMeans.Zeppelin)) return Refuse(out why, $"ship: {route.Means} is not a ship");
        if (!PlayerbotShipNetwork.SwitchedOn(route.Means, options.Travel))
            return Refuse(out why, $"ship: {(route.Means == TravelMeans.Boat ? "boats" : "zeppelins")} are switched off");
        bool alliance = PlayerbotShipNetwork.IsAlliance(player);
        if (!PlayerbotShipNetwork.Admits(route.Access, alliance))
            return Refuse(out why, $"ship: the {route.Access} ship {route.Entry} does not take a {(alliance ? "Alliance" : "Horde")} bot");
        if (!player.IsAlive || !player.IsInWorld || player.Map is null) return Refuse(out why, "ship: the bot is not alive in the world");
        TravelPlace from = fromA ? route.PortA : route.PortB, to = fromA ? route.PortB : route.PortA;
        if (player.Transport is { } activeTransport && activeTransport.Entry != route.Entry)
        {
            if (player.MapId == from.MapId && PlayerbotNavigation.OnWalkableMesh(player.Map, Position(player)))
            {
                Vector3 dockFloor = StepOffPoint(player.Map, Position(player));
                PlayerbotMotion.SendStepOff(session, player, dockFloor, player.Orientation);
            }

            if (player.Transport is not null)
                return Refuse(out why, "ship: the bot is aboard another transport");
        }

        bool alreadyAboardTargetShip = player.Transport is { } aboardTarget && aboardTarget.Entry == route.Entry;
        if (!alreadyAboardTargetShip && player.MapId != from.MapId)
            return Refuse(out why, $"ship: the bot is on map {player.MapId}, the port on map {from.MapId}");
        float portYards = Flat(Position(player), from.Position);
        if (!alreadyAboardTargetShip && portYards > BeginReachYards)
            return Refuse(out why, string.Create(CultureInfo.InvariantCulture,
                $"ship: the bot stands {portYards:F0} yd from the port of ship {route.Entry} (at most {BeginReachYards:F0}); it walks there first"));
        if (!PlayerbotMapPolicy.Allows(options.AllowedMaps, to.MapId, null, current: false))
            return Refuse(out why, $"ship: bots may not walk map {to.MapId}");
        uint now = session.World.NowMs;
        DockMemo memo = MemoOf(player, route.Entry, fromA);
        if (memo.Failures > 0 && unchecked((int)(memo.UntilMs - now)) > 0)
            return Refuse(out why, string.Create(CultureInfo.InvariantCulture,
                $"ship: the dock of ship {route.Entry} failed {memo.Failures} time(s); backed off for {unchecked(memo.UntilMs - now) / 1000} s"));
        if (TransportSystem.Of(session.World)?.FindByEntry(route.Entry) is not { } liveShip)
        {
            if (!memo.ShipMissingReported && CanReport(session, player))
            {
                memo.ShipMissingReported = true;
                PlayerbotAnomalies.Report(player, AnomalyArea, "ship-missing", string.Create(CultureInfo.InvariantCulture,
                    $"ship {route.Entry} ({route.Means}) does not sail (transports off, or the route was not built)"));
            }

            return Refuse(out why, $"ship: ship {route.Entry} does not sail");
        }

        var trip = new ShipTrip(route, fromA, now, options);
        if (ReferenceEquals(player.Transport, liveShip))
        {
            trip.DockSpot = Position(player);
            trip.Offset = DeckOffset(liveShip, trip.DockSpot);
            Boarded(trip, now);
        }

        Trips.AddOrUpdate(player, trip);
        PlayerbotMotion.Of(player).ShipTrip = trip;
        return true;
    }

    /// <summary>
    /// One think of the leg. <see cref="TravelLegStep.Idle"/>: no leg (or its end was already answered). <see cref="TravelLegStep.Traveling"/>:
    /// under way, nothing else moves the bot. <see cref="TravelLegStep.Arrived"/> / <see cref="TravelLegStep.Failed"/> once, when the leg ended
    /// (here or in <see cref="Pump"/>); <paramref name="failure"/> then says why. A dead bot not aboard drops its leg (Idle); aboard, the ship
    /// carries it on (the server revives a passenger at a map change).
    /// </summary>
    internal static TravelLegStep Drive(WorldSession session, Player player, PlayerbotOptions options, uint elapsedMs, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        failure = null;
        if (!Trips.TryGetValue(player, out ShipTrip? trip)) return TravelLegStep.Idle;
        if (trip.Stage is ShipStage.Done or ShipStage.Failed) return Ended(trip, out failure);
        uint now = session.World.NowMs;
        trip.Options = options;
        trip.LastDriveMs = now;
        if (!player.IsAlive && !IsAboard(player))
        {
            Trips.Remove(player);
            if (PlayerbotMotion.TryGetState(player, out PlayerbotMotion.MotionState? deadState)) deadState.ShipTrip = null;
            return TravelLegStep.Idle;
        }

        PlayerbotThinkProbe.Phase("ship");
        Step(session, player, trip, options, Math.Max(1u, elapsedMs), now, pump: false);
        if (!Trips.TryGetValue(player, out _)) return TravelLegStep.Idle;
        return trip.Stage is ShipStage.Done or ShipStage.Failed ? Ended(trip, out failure) : TravelLegStep.Traveling;
    }

    /// <summary>
    /// Per-tick sampling for the brain's integration (to be called from <see cref="PlayerbotMotion.Pump"/>, like PlayerbotTram.Pump): every
    /// <see cref="StepIntervalMs"/> after the last <see cref="Drive"/> it notices the ship at the port, steps aboard and off, and watches the ride,
    /// so a short stop is not missed between thinks. Walking (to the dock, off the pier) stays with <see cref="Drive"/>. An end reached here is
    /// answered by the next <see cref="Drive"/>.
    /// </summary>
    internal static void Pump(WorldSession session, Player player, uint now)
    {
        if (!Trips.TryGetValue(player, out ShipTrip? trip) || trip.Stage is ShipStage.Done or ShipStage.Failed)
        {
            if (PlayerbotMotion.TryGetState(player, out PlayerbotMotion.MotionState? endState)) endState.ShipTrip = null;
            return;
        }
        if (trip.Stage == ShipStage.ToDock) return;
        if (trip.Stage == ShipStage.Disembark && trip.SteppedOff) return;
        if (!player.IsAlive || !player.IsInWorld) return;
        if (unchecked(now - trip.LastDriveMs) < StepIntervalMs || unchecked(now - trip.LastPumpMs) < StepIntervalMs) return;
        trip.LastPumpMs = now;
        Step(session, player, trip, trip.Options, StepIntervalMs, now, pump: true);
    }

    /// <summary>
    /// Drop the leg (death, a controller taking the bot over). Not boarded yet: dropped (a bot half way up the gangway steps back onto the
    /// dock first). Aboard: never put off at sea; the leg is marked cancelled and rides on to the next port, where <see cref="Drive"/> steps the
    /// bot off and ends it. Walking off the pier: dropped.
    /// </summary>
    internal static void Cancel(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!Trips.TryGetValue(player, out ShipTrip? trip) || trip.Stage is ShipStage.Done or ShipStage.Failed) return;
        switch (trip.Stage)
        {
            case ShipStage.Ride:
            case ShipStage.Disembark when !trip.SteppedOff:
                trip.Cancelled = true;
                return;
            case ShipStage.Board when player.Transport is null && PlayerbotMotion.CanMove(player) && player.Session is WorldSession session:
                PlayerbotMotion.SendStepOff(session, player, trip.DockSpot, player.Orientation);
                break;
        }

        Trips.Remove(player);
        if (PlayerbotMotion.TryGetState(player, out PlayerbotMotion.MotionState? cancelState)) cancelState.ShipTrip = null;
    }

    private static TravelLegStep Ended(ShipTrip trip, out string? failure)
    {
        failure = trip.Stage == ShipStage.Failed ? trip.Failure : null;
        if (trip.Returned) return TravelLegStep.Idle;
        trip.Returned = true;
        return trip.Stage == ShipStage.Done ? TravelLegStep.Arrived : TravelLegStep.Failed;
    }

    private static void Step(WorldSession session, Player player, ShipTrip trip, PlayerbotOptions options, uint elapsedMs, uint now, bool pump)
    {
        // Between maps (the ship's map change, or any teleport): the client half acknowledges it (PlayerbotMovementControl); nothing to do.
        if (!player.IsInWorld || player.Map is not { } map
            || session.Services.GetService<TeleportFeature>()?.Teleports.IsBeingTeleported(player) == true)
            return;
        ShipTransport? ship = TransportSystem.Of(session.World)?.FindByEntry(trip.Route.Entry);
        if (ship is null)
        {
            // A settling leg whose ship is gone has nothing left to ride to a port: it ends with the failure it settled on.
            if (trip.Settling) End(trip, now, StripPrefix(trip.Failure) + $"; ship {trip.Route.Entry} no longer sails", trip.FailureCode);
            else Fail(session, player, trip, now, "ship-missing", $"ship {trip.Route.Entry} no longer sails (stage {trip.Stage})", backoff: true);
            return;
        }

        switch (trip.Stage)
        {
            case ShipStage.ToDock:
                if (!pump) ToDock(session, player, map, trip, options, elapsedMs, now);
                return;
            case ShipStage.WaitShip:
                WaitShip(session, player, map, ship, trip, now);
                return;
            case ShipStage.Board:
                Board(session, player, map, ship, trip, now);
                return;
            case ShipStage.Ride:
                Ride(session, player, ship, trip, now);
                return;
            case ShipStage.Disembark:
                Disembark(session, player, map, ship, trip, options, elapsedMs, now, pump);
                return;
        }
    }

    private static void ToDock(WorldSession session, Player player, Map map, ShipTrip trip, PlayerbotOptions options, uint elapsedMs, uint now)
    {
        if (!PlayerbotShipNetwork.SwitchedOn(trip.Route.Means, options.Travel))
        {
            PlayerbotMovementControl.Stop(session, player);
            End(trip, now, $"{trip.Route.Means} switched off before boarding", code: null);
            return;
        }

        if (map.MapId != trip.From.MapId)
        {
            Fail(session, player, trip, now, "wrong-map", $"on map {map.MapId} on the way to the port on map {trip.From.MapId}", backoff: false);
            return;
        }

        if (AtPort(player, trip.From))
        {
            PlayerbotMovementControl.Stop(session, player);
            trip.DockSpot = Position(player);
            Enter(trip, ShipStage.WaitShip, now);
            return;
        }

        long pendingMark = PathSearchCounters.TilesPending;
        Vector3 dockTarget = ResolveDockTarget(map, trip.From.Position, Position(player));
        if (!Walk(session, player, trip, dockTarget, options, elapsedMs, now, out string? why, pendingMark))
            Fail(session, player, trip, now, "dock-unreachable", why!, backoff: true);
    }

    private static void WaitShip(WorldSession session, Player player, Map map, ShipTransport ship, ShipTrip trip, uint now)
    {
        if (!PlayerbotShipNetwork.SwitchedOn(trip.Route.Means, trip.Options.Travel))
        {
            End(trip, now, $"{trip.Route.Means} switched off while waiting for the ship", code: null);
            return;
        }

        if (map.MapId != trip.From.MapId || !AtPort(player, trip.From))
        {
            // Pushed off the dock (a knock-back, a fear): walk back to it.
            Enter(trip, ShipStage.ToDock, now);
            return;
        }

        uint bound = WaitBoundMs(trip.Route);
        if (unchecked(now - trip.StageMs) > bound)
        {
            Fail(session, player, trip, now, "never-docked", string.Create(CultureInfo.InvariantCulture,
                $"ship {trip.Route.Entry} did not wait at the port ({trip.From.Position.X:F1},{trip.From.Position.Y:F1},{trip.From.Position.Z:F1}) on map {trip.From.MapId} within {bound / 1000} s"),
                backoff: true);
            return;
        }

        if (!PlayerbotShipNetwork.DockedAt(ship, trip.From) || StopLeftMs(ship) < BoardMarginMs) return;
        if (Flat(Position(player), new Vector3(ship.X, ship.Y, ship.Z)) > BoardReachYards) return;
        if (!PlayerbotMovementControl.Stop(session, player)) return;
        trip.DockSpot = Position(player);
        trip.Offset = DeckOffset(ship, trip.DockSpot);
        trip.LastStepMs = now;
        Enter(trip, ShipStage.Board, now);
    }

    private static void Board(WorldSession session, Player player, Map map, ShipTransport ship, ShipTrip trip, uint now)
    {
        if (ReferenceEquals(player.Transport, ship))
        {
            Boarded(trip, now);
            return;
        }

        bool switchedOff = !PlayerbotShipNetwork.SwitchedOn(trip.Route.Means, trip.Options.Travel);
        if (switchedOff || !PlayerbotShipNetwork.DockedAt(ship, trip.From) || map.MapId != trip.From.MapId)
        {
            // The ship sails before the bot is aboard (or the switch went off): back onto the dock.
            PlayerbotMotion.SendStepOff(session, player, trip.DockSpot, player.Orientation);
            if (switchedOff)
            {
                End(trip, now, $"{trip.Route.Means} switched off while boarding", code: null);
                return;
            }

            if (++trip.Attempts >= MaxAttempts)
            {
                Fail(session, player, trip, now, "board-refused", $"the ship left {trip.Attempts} times before the bot was aboard", backoff: true);
                return;
            }

            Enter(trip, ShipStage.WaitShip, now);
            return;
        }

        Vector3 deck = Seat(ship, trip.Offset);
        Vector3 at = Position(player);
        float left = Vector3.Distance(at, deck);
        if (left > OnDeckYards)
        {
            uint since = unchecked(now - trip.LastStepMs);
            if (since < StepIntervalMs) return;
            if (!PlayerbotMotion.Speed(player, trip.Options.MoveSpeed, out float speed, out _)) speed = Unit.BaseRunSpeed;
            float stride = MathF.Min(left, MathF.Max(1f, speed * Math.Min(since, 2_000u) / 1000f));
            Vector3 next = left - stride <= OnDeckYards ? deck : at + ((deck - at) * (stride / left));
            if (PlayerbotMotion.SendStepOff(session, player, next, Heading(at, deck)))
            {
                trip.LastStepMs = now;
            }
            else if (++trip.Attempts >= MaxAttempts)
            {
                FailBoard(session, player, trip, now, "the server did not take the steps onto the deck");
                return;
            }

            if (Vector3.Distance(Position(player), deck) > OnDeckYards) return;
        }

        // On the deck: the heartbeat that stands on the ship.
        PlayerbotMotion.SendAboard(session, player, ship.Guid.Value, deck, player.Orientation, trip.Offset, 0f);
        if (ReferenceEquals(player.Transport, ship))
        {
            Boarded(trip, now);
            return;
        }

        if (++trip.Attempts >= MaxAttempts)
            FailBoard(session, player, trip, now, string.Create(CultureInfo.InvariantCulture,
                $"{trip.Attempts} heartbeats aboard ship {trip.Route.Entry} at offset ({trip.Offset.X:F1},{trip.Offset.Y:F1},{trip.Offset.Z:F1}) did not board it"));
    }

    private static void Boarded(ShipTrip trip, uint now)
    {
        trip.Attempts = 0;
        trip.BoardedMs = now;
        trip.Sailed = false;
        Enter(trip, ShipStage.Ride, now);
    }

    private static void FailBoard(WorldSession session, Player player, ShipTrip trip, uint now, string why)
    {
        // Back onto the dock: a bot never stays half way up the gangway (the heartbeat without the ship also leaves it, if it boarded late).
        PlayerbotMotion.SendStepOff(session, player, trip.DockSpot, player.Orientation);
        Fail(session, player, trip, now, "board-refused", why, backoff: true);
    }

    private static void Ride(WorldSession session, Player player, ShipTransport ship, ShipTrip trip, uint now)
    {
        bool onShip = ReferenceEquals(player.Transport, ship);
        if (!onShip)
        {
            // A bot that died or repopped/teleported away from the ship and port left the ride through death/teleport, not a ship drop.
            if (!player.IsAlive || (player.Transport is null
                && Flat(Position(player), new Vector3(ship.X, ship.Y, ship.Z)) > 100f
                && Flat(Position(player), trip.From.Position) > 100f))
            {
                Trips.Remove(player);
                if (PlayerbotMotion.TryGetState(player, out PlayerbotMotion.MotionState? cancelState)) cancelState.ShipTrip = null;
                return;
            }

            // Off the ship (the server let it go): nothing carries the bot any more, and the leg ends here.
            Fail(session, player, trip, now, "left-ship", string.Create(CultureInfo.InvariantCulture,
                $"no longer aboard ship {trip.Route.Entry} (transport {(player.Transport is { } other ? other.Entry.ToString(CultureInfo.InvariantCulture) : "none")}) {unchecked(now - trip.BoardedMs) / 1000} s after boarding"),
                backoff: false);
            return;
        }

        if ((player.Movement.Flags & MovementFlags.OnTransport) == 0 && !trip.Settling)
        {
            // The server still carries the bot but its movement no longer says so: the leg settles (rides on, steps off at the next port).
            Fail(session, player, trip, now, "left-ship", string.Create(CultureInfo.InvariantCulture,
                $"aboard ship {trip.Route.Entry} without MOVEFLAG_ONTRANSPORT {unchecked(now - trip.BoardedMs) / 1000} s after boarding"), backoff: false);
        }

        if (trip.Settling && unchecked(now - trip.SettleMs) > SettleBoundMs(trip.Route))
        {
            End(trip, now, string.Create(CultureInfo.InvariantCulture,
                $"{StripPrefix(trip.Failure)}; still aboard ship {trip.Route.Entry} {unchecked(now - trip.SettleMs) / 1000} s later, no port took the bot off"), trip.FailureCode);
            return;
        }

        if (ship.CurrentMap is not { } shipMap || shipMap.MapId != player.MapId)
        {
            if (!trip.Settling)
                Fail(session, player, trip, now, "wrong-map", string.Create(CultureInfo.InvariantCulture,
                    $"aboard ship {trip.Route.Entry} on map {player.MapId} while the ship sails on map {ship.CurrentMap?.MapId.ToString(CultureInfo.InvariantCulture) ?? "none"}"),
                    backoff: false);
            return; // settling: the ship's map change catches up, or the settle bound ends the leg
        }

        if (ship.IsMoving) trip.Sailed = true;
        uint riding = unchecked(now - trip.BoardedMs);
        uint period = PeriodMs(trip.Route);
        if (!trip.Settling && !trip.Overdue && riding > period + RideGraceMs)
        {
            trip.Overdue = true;
            if (CanReport(session, player))
            {
                PlayerbotAnomalies.Report(player, AnomalyArea, "ride-overdue", string.Create(CultureInfo.InvariantCulture,
                    $"aboard ship {trip.Route.Entry} for {riding / 1000} s (round trip {period / 1000} s) without reaching the port on map {trip.To.MapId}"));
                trip.Reported = true;
            }
        }

        if (!trip.Settling && riding > (2 * period) + RideGraceMs)
        {
            // Still aboard: never put off at sea. The leg settles: it rides on to the next port and steps off there, then fails.
            Fail(session, player, trip, now, "ride-overdue", string.Create(CultureInfo.InvariantCulture,
                $"aboard ship {trip.Route.Entry} for {riding / 1000} s, two round trips, without stepping off"), backoff: false);
            return;
        }

        bool atTo = PlayerbotShipNetwork.DockedAt(ship, trip.To), atFrom = PlayerbotShipNetwork.DockedAt(ship, trip.From);
        bool stepOff = trip.Settling ? trip.Sailed && (atTo || atFrom)
            : atTo || ((trip.Overdue || trip.Cancelled) && trip.Sailed && atFrom);
        if (stepOff)
        {
            trip.Attempts = 0;
            trip.StepOff = null;
            Enter(trip, ShipStage.Disembark, now);
            Disembark(session, player, player.Map!, ship, trip, trip.Options, StepIntervalMs, now, pump: true);
        }
    }

    private static void Disembark(WorldSession session, Player player, Map map, ShipTransport ship, ShipTrip trip, PlayerbotOptions options,
        uint elapsedMs, uint now, bool pump)
    {
        if (!trip.SteppedOff)
        {
            TravelPlace port = PlayerbotShipNetwork.DockedAt(ship, trip.To) ? trip.To : trip.From;
            if (!ReferenceEquals(player.Transport, ship))
            {
                Fail(session, player, trip, now, "left-ship", $"off ship {trip.Route.Entry} before stepping off at the port", backoff: false);
                return;
            }

            if (!PlayerbotShipNetwork.DockedAt(ship, port))
            {
                // The ship sails on before the bot is off: it rides to the next port.
                Enter(trip, ShipStage.Ride, now);
                return;
            }

            if (!PlayerbotMotion.CanMove(player)) return;
            trip.StepOff ??= StepOffPoint(map, Position(player));
            PlayerbotMotion.SendStepOff(session, player, trip.StepOff.Value, Heading(Position(player), trip.StepOff.Value));
            if (player.Transport is not null)
            {
                if (++trip.Attempts >= MaxAttempts)
                    Fail(session, player, trip, now, "disembark-refused", string.Create(CultureInfo.InvariantCulture,
                        $"{trip.Attempts} heartbeats off ship {trip.Route.Entry} at ({trip.StepOff.Value.X:F1},{trip.StepOff.Value.Y:F1},{trip.StepOff.Value.Z:F1}) did not take the bot off"),
                        backoff: false);
                return;
            }

            trip.SteppedOff = true;
            trip.LeftAt = port;
            trip.Refusals = 0;
            trip.Walk = null;
            trip.Best = float.PositiveInfinity;
            trip.BestMs = now;
            trip.NextPlanMs = now;
            if (player.MapId != port.MapId)
            {
                Fail(session, player, trip, now, "wrong-map", $"stepped off on map {player.MapId}, the port lies on map {port.MapId}", backoff: false);
                return;
            }
        }

        if (pump) return; // the walk off the pier is the brain's (Drive)
        TravelPlace at = trip.LeftAt ?? trip.To;
        if (player.MapId != at.MapId)
        {
            Fail(session, player, trip, now, "wrong-map", $"on map {player.MapId} after stepping off at the port on map {at.MapId}", backoff: false);
            return;
        }

        // A short walk off the pier, to the walkable point nearest the port: one route; the leg ends when it is walked (or none is needed).
        if (trip.Walk is { Complete: false } walk && PlayerbotNavigation.TryAdvance(session, walk, options, elapsedMs, now)) return;
        if (trip.Walk is null && unchecked(now - trip.StageMs) <= NoProgressMs)
        {
            if (unchecked((int)(trip.NextPlanMs - now)) > 0) return;
            long dockMark = PathSearchCounters.TilesPending;
            Vector3 dockTarget = ResolveDockTarget(map, at.Position, Position(player));
            if (dockTarget == at.Position && PathSearchCounters.TilesPending != dockMark && trip.Tiles.Hold(now))
            {
                // The dock resolved to the port itself (no dry point beside it, or the port read as dry) while a tile of a probe was still being
                // read: no verdict on the pier yet, and no refused plan to count. (A dry point found stands even when a probe beside it met an
                // unread tile.) Ask again shortly.
                trip.NextPlanMs = unchecked(now + PlanRetryMs);
                return;
            }

            PlanOutcome outcome = PlayerbotNavigation.PlanToward(player, dockTarget, options, ref trip.Tiles, now, out PlayerbotRoute? route);
            if (outcome == PlanOutcome.Route && route is not null && route.Distance > ProgressYards)
            {
                trip.Walk = route;
                PlayerbotNavigation.TryAdvance(session, route, options, elapsedMs, now);
                return;
            }

            if (outcome != PlanOutcome.Route && ++trip.Refusals < MaxRefusedPlans)
            {
                trip.NextPlanMs = unchecked(now + PlanRetryMs);
                return;
            }
        }

        PlayerbotMovementControl.Stop(session, player);
        if (!AtPort(player, at))
        {
            Fail(session, player, trip, now, "dock-unreachable", string.Create(CultureInfo.InvariantCulture,
                $"stepped off ship {trip.Route.Entry} but stands {Flat(Position(player), at.Position):F1} yd from the port on map {at.MapId}"), backoff: false);
            return;
        }

        if (at != trip.To)
        {
            // A settling leg ends with the failure it settled on (reported then); a cancelled or missed ride ends without one.
            if (trip.Settling) End(trip, now, StripPrefix(trip.Failure) + "; stepped off at the departure port", trip.FailureCode);
            else End(trip, now, trip.Cancelled ? "the leg was cancelled; stepped off at the departure port" : "the ride missed the arrival port; stepped off at the departure port",
                code: null);
            return;
        }

        trip.Stage = ShipStage.Done;
        trip.StageMs = now;
        trip.Failure = null;
        trip.FailureCode = null; // a settling leg that still stepped off at the arrival port arrived (its failure was reported when it began)
        if (Docks.TryGetValue(player, out Dictionary<(uint, bool), DockMemo>? docks)) docks.Remove((trip.Route.Entry, trip.FromA));
    }

    /// <summary>
    /// Follow (planning when needed, one query per <see cref="PlanRetryMs"/>) a route towards <paramref name="target"/>. False (with why) after
    /// <see cref="MaxRefusedPlans"/> refused plans in a row, or no <see cref="ProgressYards"/> gained within <see cref="NoProgressMs"/>.
    /// </summary>
    private static bool Walk(WorldSession session, Player player, ShipTrip trip, Vector3 target, PlayerbotOptions options, uint elapsedMs, uint now,
        out string? why, long pendingMark = -1)
    {
        why = null;
        float left = Vector3.Distance(Position(player), target);
        if (left + ProgressYards <= trip.Best)
        {
            trip.Best = left;
            trip.BestMs = now;
        }
        else if (unchecked(now - trip.BestMs) > NoProgressMs)
        {
            why = string.Create(CultureInfo.InvariantCulture, $"no progress towards the port for {NoProgressMs / 1000} s ({left:F1} yd left)");
            return false;
        }

        if (trip.Walk is { Complete: false } route && PlayerbotNavigation.TryAdvance(session, route, options, elapsedMs, now)) return true;
        trip.Walk = null;
        if (unchecked((int)(trip.NextPlanMs - now)) > 0) return true;
        PlanOutcome outcome = PlayerbotNavigation.PlanToward(player, target, options, ref trip.Tiles, now, out PlayerbotRoute? next);
        if (outcome == PlanOutcome.Refused && left > BoardReachYards && player.Map is { } map && map.Collision.MeshPoints is not null)
        {
            outcome = TryPlanApproachStep(player, map, target, options, ref trip.Tiles, now, out next);
        }

        if (outcome == PlanOutcome.Refused && pendingMark >= 0 && PathSearchCounters.TilesPending != pendingMark && trip.Tiles.Hold(now))
        {
            outcome = PlanOutcome.Pending;
        }

        if (outcome == PlanOutcome.Pending)
        {
            trip.NextPlanMs = unchecked(now + PlanRetryMs);
            return true;
        }

        if (outcome != PlanOutcome.Route || next is null)
        {
            trip.NextPlanMs = unchecked(now + PlanRetryMs);
            if (++trip.Refusals < MaxRefusedPlans) return true;
            why = string.Create(CultureInfo.InvariantCulture, $"{trip.Refusals} plans towards the port were refused ({left:F1} yd left)");
            return false;
        }

        trip.Refusals = 0;
        trip.Walk = next;
        PlayerbotNavigation.TryAdvance(session, next, options, elapsedMs, now);
        return true;
    }

    private static PlanOutcome TryPlanApproachStep(Player player, Map map, Vector3 target, PlayerbotOptions options, ref TileWait tiles,
        uint now, out PlayerbotRoute? route)
    {
        route = null;
        Vector3 bot = Position(player);
        Vector2 delta = new(target.X - bot.X, target.Y - bot.Y);
        float flatDist = delta.Length();
        if (flatDist < 1f) return PlanOutcome.Refused;
        Vector2 dir = delta / flatDist;
        float chunk = MathF.Min(options.MaxRouteYards * 0.9f, Math.Max(24f, options.MaxPathPoints - 4));
        ReadOnlySpan<float> stepRadii = [MathF.Min(chunk, flatDist * 0.5f), MathF.Min(chunk * 0.5f, flatDist * 0.25f)];
        ReadOnlySpan<float> angles = [0f, 0.45f, -0.45f];
        bool anyPending = false;
        foreach (float radius in stepRadii)
        {
            if (radius < ProgressYards * 2f) continue;
            foreach (float angle in angles)
            {
                float c = MathF.Cos(angle), s = MathF.Sin(angle);
                Vector2 rot = new((dir.X * c) - (dir.Y * s), (dir.X * s) + (dir.Y * c));
                float x = bot.X + (rot.X * radius);
                float y = bot.Y + (rot.Y * radius);
                float z = map.Collision.GetHeight(x, y, bot.Z + 20f);
                if (!ValidHeight(z)) z = map.Terrain.GetHeight(x, y, bot.Z);
                if (!ValidHeight(z)) z = bot.Z;
                var stepTarget = new Vector3(x, y, z);
                PlanOutcome outcome = PlayerbotNavigation.PlanToward(player, stepTarget, options, ref tiles, now, out PlayerbotRoute? candidate);
                if (outcome == PlanOutcome.Pending)
                {
                    anyPending = true;
                    continue;
                }

                if (outcome == PlanOutcome.Route && candidate is { Points.Count: >= 2 }
                    && Flat(candidate.Points[^1], target) + ProgressYards <= flatDist)
                {
                    route = candidate;
                    return PlanOutcome.Route;
                }
            }
        }

        return anyPending ? PlanOutcome.Pending : PlanOutcome.Refused;
    }

    private static void Fail(WorldSession session, Player player, ShipTrip trip, uint now, string code, string why, bool backoff)
    {
        if (trip.Stage is (ShipStage.Ride or ShipStage.Disembark) && !trip.SteppedOff && player.Transport is { } aboard && aboard.Entry == trip.Route.Entry)
        {
            Settle(session, player, trip, now, code, why);
            return;
        }

        if (trip.Stage is ShipStage.ToDock) PlayerbotMovementControl.Stop(session, player);
        End(trip, now, why, code);
        if (!trip.Reported && CanReport(session, player))
        {
            trip.Reported = true;
            PlayerbotAnomalies.Report(player, AnomalyArea, code, string.Create(CultureInfo.InvariantCulture,
                $"ship {trip.Route.Entry} ({trip.Route.Means}) from map {trip.From.MapId} to map {trip.To.MapId}: {why}"));
        }

        if (!backoff) return;
        DockMemo memo = MemoOf(player, trip.Route.Entry, trip.FromA);
        memo.Failures++;
        memo.UntilMs = unchecked(now + trip.Options.Travel.BackoffMs(memo.Failures));
    }

    /// <summary>
    /// A failure with the server still carrying the bot (<see cref="ShipTrip.Settling"/>): reported once now, and the leg rides on until the
    /// ship has sailed and waits at a port, where the bot steps off; the leg ends after that. A step off a port refused starts the wait over.
    /// </summary>
    private static void Settle(WorldSession session, Player player, ShipTrip trip, uint now, string code, string why)
    {
        if (!trip.Settling)
        {
            trip.Settling = true;
            trip.SettleMs = now;
            trip.Failure = "ship: " + why;
            trip.FailureCode = code;
            if (!trip.Reported && CanReport(session, player))
            {
                trip.Reported = true;
                PlayerbotAnomalies.Report(player, AnomalyArea, code, string.Create(CultureInfo.InvariantCulture,
                    $"ship {trip.Route.Entry} ({trip.Route.Means}) from map {trip.From.MapId} to map {trip.To.MapId}: {why} (still aboard: it steps off at the next port)"));
            }
        }

        trip.Sailed = false;
        trip.Attempts = 0;
        trip.StepOff = null;
        Enter(trip, ShipStage.Ride, now);
    }

    private static string StripPrefix(string? failure) => failure is null ? "the leg failed aboard" : failure.StartsWith("ship: ", StringComparison.Ordinal) ? failure[6..] : failure;

    private static void End(ShipTrip trip, uint now, string why, string? code)
    {
        trip.Stage = ShipStage.Failed;
        trip.StageMs = now;
        trip.Failure = "ship: " + why;
        trip.FailureCode = code;
        trip.Walk = null;
    }

    private static void Enter(ShipTrip trip, ShipStage stage, uint now)
    {
        if (trip.Stage == stage) return;
        trip.Stage = stage;
        trip.StageMs = now;
        trip.NextPlanMs = now;
        trip.Best = float.PositiveInfinity;
        trip.BestMs = now;
        trip.Refusals = 0;
        trip.Walk = null;
    }

    private static bool Refuse(out string? why, string reason)
    {
        why = reason;
        return false;
    }

    private static DockMemo MemoOf(Player player, uint entry, bool fromA)
    {
        Dictionary<(uint Entry, bool FromA), DockMemo> docks = Docks.GetOrCreateValue(player);
        if (!docks.TryGetValue((entry, fromA), out DockMemo? memo)) docks[(entry, fromA)] = memo = new DockMemo();
        return memo;
    }

    private static bool CanReport(WorldSession session, Player player)
        => !session.IsInChaosProbe && player.IsInWorld && !player.IsLoggingOut;

    /// <summary>Whether the bot stands at <paramref name="port"/>'s dock: its map, within <see cref="BoardReachYards"/> flat and <see cref="DockHeightYards"/> of height.</summary>
    internal static bool AtPort(Player player, TravelPlace port)
        => player.MapId == port.MapId && Flat(Position(player), port.Position) <= BoardReachYards
            && MathF.Abs(player.Z - port.Position.Z) <= DockHeightYards;

    /// <summary>How long a ship waiting at a stop stays there (ms; 0 while it moves).</summary>
    internal static uint StopLeftMs(ShipTransport ship)
    {
        if (ship.IsMoving || ship.Period == 0) return 0;
        uint progress = ship.PathProgress % ship.Period;
        uint departure = ship.CurrentFrame.DepartureTime;
        return departure > progress ? departure - progress : 0;
    }

    internal static uint PeriodMs(ShipRoute route) => (uint)Math.Clamp(route.PeriodSeconds * 1000f, 1f, uint.MaxValue / 4f);

    /// <summary>How long a bot waits at a port for its ship: one and a half round trips.</summary>
    internal static uint WaitBoundMs(ShipRoute route) => PeriodMs(route) * 3 / 2;

    /// <summary>
    /// The bot's place on the deck: its own position from the dock taken into the ship's frame, held inside the deck rectangle, at the
    /// dock's height (within <see cref="DockHeightYards"/> of the origin): a ship docks with its deck level with the pier or the tower
    /// platform, whatever the height of its origin above the deck. A zeppelin's origin hangs about 18 yards above the platform (bot QA #820,
    /// #824, #792, live 2026-10-17: bots aboard at 77.9 over the Orgrimmar platform the mesh puts at 53.8, and at 121.0 beside the Tirisfal
    /// tower over the floor at 97.5); a fixed deck 6 yards above the origin put them 24 yards over the platform, where the step off found no
    /// floor within <see cref="StepOffHeightYards"/>.
    /// </summary>
    internal static Vector3 DeckOffset(ShipTransport ship, Vector3 from)
    {
        float x = from.X, y = from.Y, z = from.Z, o = 0f;
        ShipTransport.CalculatePassengerOffset(ref x, ref y, ref z, ref o, ship.X, ship.Y, ship.Z, ship.Orientation);
        return new Vector3(Math.Clamp(x, -DeckHalfLength, DeckHalfLength), Math.Clamp(y, -DeckHalfWidth, DeckHalfWidth),
            Math.Clamp(z, -DockHeightYards, DockHeightYards));
    }

    /// <summary>The world position of <paramref name="offset"/> on <paramref name="ship"/> now (vmangos CalculatePassengerPosition).</summary>
    internal static Vector3 Seat(ShipTransport ship, Vector3 offset)
    {
        float x = offset.X, y = offset.Y, z = offset.Z, o = 0f;
        ship.CalculatePassengerPosition(ref x, ref y, ref z, ref o);
        return new Vector3(x, y, z);
    }

    /// <summary>
    /// Where a bot on the deck at <paramref name="deck"/> steps off: the nearest floor around it (rings of <see cref="StepOffRadiiYards"/>)
    /// within <see cref="StepOffHeightYards"/> of the deck that the navigation mesh stands on; the floor under the bot when none is (the
    /// walk off the pier then finds its way).
    /// </summary>
    internal static Vector3 StepOffPoint(Map map, Vector3 deck)
    {
        foreach (float radius in StepOffRadiiYards)
        {
            Vector3? best = null;
            float bestDz = float.PositiveInfinity;
            for (int i = 0; i < StepOffDirections; i++)
            {
                float angle = i * MathF.Tau / StepOffDirections;
                float x = deck.X + (MathF.Cos(angle) * radius), y = deck.Y + (MathF.Sin(angle) * radius);
                float floor = map.Collision.GetHeight(x, y, deck.Z + 2f);
                if (!ValidHeight(floor) || MathF.Abs(floor - deck.Z) > StepOffHeightYards) continue;
                var point = new Vector3(x, y, floor + 0.05f);
                if (MathF.Abs(floor - deck.Z) >= bestDz || !PlayerbotNavigation.OnWalkableMesh(map, point)) continue;
                best = point;
                bestDz = MathF.Abs(floor - deck.Z);
            }

            if (best is { } found) return found;
        }

        float under = map.Collision.GetHeight(deck.X, deck.Y, deck.Z + 2f);
        return ValidHeight(under) && under <= deck.Z ? new Vector3(deck.X, deck.Y, under + 0.05f) : deck;
    }

    private static readonly float[] DockSearchRadiiYards = [4f, 8f, 12f, 16f, 20f, 25f, 30f, 35f, 38f];
    private static readonly float[] DockSearchZOffsets = [0f, -8f, -16f, -22f, 6f];

    /// <summary>
    /// Resolve a walkable dock floor near <paramref name="portPos"/> when the ship's port keyframe is over water or in mid-air
    /// (boat hull origin at z=0 in the harbour water or zeppelin hull origin ~18-24 yards above the tower platform).
    /// </summary>
    internal static Vector3 ResolveDockTarget(Map map, Vector3 portPos, Vector3 toward)
    {
        map.Collision.LoadTilesOnDemand(portPos.X, portPos.Y);
        if (map.Collision.MeshPoints is not { } mesh)
            return portPos;

        DockCache cache = DockCaches.GetValue(mesh, static _ => new DockCache());
        var key = new DockKey(map.MapId, BitConverter.SingleToInt32Bits(portPos.X), BitConverter.SingleToInt32Bits(portPos.Y),
            BitConverter.SingleToInt32Bits(portPos.Z));
        if (!cache.TryGet(key, Environment.TickCount64, out DockEntry? entry))
        {
            t_dockPending = false;
            Interlocked.Increment(ref DockComputeCount);
            int cutsBefore = PlayerbotThinkProbe.BudgetCuts;
            entry = ComputeDock(map, portPos);
            // Not stored when any probe was cut short: pending tiles, a fallback query refused or clamped by the think's node budget, or a
            // plan query answered capped/without a search. Such an answer says "unknown", not "no dock".
            if (!t_dockPending && PlayerbotThinkProbe.BudgetCuts == cutsBefore)
                cache.Store(key, entry, Environment.TickCount64 + DockCacheTtlMs);
        }

        if (entry.PortIsDock)
            return portPos;

        // The first ring with any candidate answers; among its candidates (in direction order) the one nearest `toward`, the first on a tie.
        Vector3? best = null;
        float bestDist = float.PositiveInfinity;
        foreach (Vector3 candidate in entry.Candidates)
        {
            float dist = Flat(candidate, toward);
            if (dist < bestDist)
            {
                best = candidate;
                bestDist = dist;
            }
        }

        return best ?? portPos;
    }

    /// <summary>
    /// How long a cached dock answer is kept: the ring reads terrain, models and liquid of tiles beside the port's, which may load later.
    /// The cache is keyed by the navigation mesh object only: a models or liquid reload that does not replace the pathfinder stays stale for
    /// up to this long (or until <see cref="InvalidateDockCache"/>).
    /// </summary>
    internal const long DockCacheTtlMs = 60_000;

    /// <summary>The most ports one cache keeps (a world has a dozen).</summary>
    private const int DockCacheMaxEntries = 256;

    [ThreadStatic] private static bool t_dockPending;
    private static long DockComputeCount; // Interlocked only

    /// <summary>The dock searches run in full since the process started (test seam: a cache hit adds none).</summary>
    internal static long DockComputes => Interlocked.Read(ref DockComputeCount);

    private readonly record struct DockKey(uint MapId, int X, int Y, int Z);

    /// <summary>
    /// A dock answer independent of the asker: the port is itself a dock, or the candidates of the first ring that has any, in direction order.
    /// Immutable once built.
    /// </summary>
    private sealed class DockEntry(bool portIsDock, Vector3[] candidates, long expiresMs)
    {
        public bool PortIsDock { get; } = portIsDock;
        public Vector3[] Candidates { get; } = candidates;
        public long ExpiresMs { get; } = expiresMs;
    }

    /// <summary>
    /// The dock answers of one navigation mesh, keyed by (map, port position). It hangs off the mesh object (<see cref="ConditionalWeakTable{TKey,TValue}"/>),
    /// so installing another mesh starts empty and the old cache goes with its mesh. Read and written from any thread under its lock.
    /// </summary>
    private sealed class DockCache
    {
        private readonly object _gate = new();
        private readonly Dictionary<DockKey, DockEntry> _entries = [];

        public bool TryGet(DockKey key, long nowMs, out DockEntry entry)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out DockEntry? found) && nowMs < found.ExpiresMs)
                {
                    entry = found;
                    return true;
                }
            }

            entry = null!;
            return false;
        }

        public void Store(DockKey key, DockEntry entry, long expiresMs)
        {
            lock (_gate)
            {
                if (_entries.Count >= DockCacheMaxEntries && !_entries.ContainsKey(key))
                    _entries.Clear();
                _entries[key] = new DockEntry(entry.PortIsDock, entry.Candidates, expiresMs);
            }
        }
    }

    private static readonly ConditionalWeakTable<object, DockCache> DockCaches = [];

    /// <summary>Forget every cached dock answer of every mesh (a transport schedule or route data reload; tests).</summary>
    internal static void InvalidateDockCache()
    {
        DockCaches.Clear();
    }

    private static DockEntry ComputeDock(Map map, Vector3 portPos)
    {
        if (OnDryDockMesh(map, portPos))
            return new DockEntry(true, [], 0);

        var found = new List<Vector3>(StepOffDirections);
        foreach (float radius in DockSearchRadiiYards)
        {
            for (int i = 0; i < StepOffDirections; i++)
            {
                float angle = i * MathF.Tau / StepOffDirections;
                float x = portPos.X + (MathF.Cos(angle) * radius);
                float y = portPos.Y + (MathF.Sin(angle) * radius);
                if (TryResolveDockPoint(map, x, y, portPos, out Vector3 candidate))
                    found.Add(candidate);
            }

            if (found.Count > 0)
                break;
        }

        return new DockEntry(false, found.ToArray(), 0);
    }

    private static bool TryResolveDockPoint(Map map, float x, float y, Vector3 portPos, out Vector3 point)
    {
        float portZ = portPos.Z;
        float floor = map.Collision.GetHeight(x, y, portZ + DockHeightYards);
        if (ValidHeight(floor) && MathF.Abs(floor - portZ) <= DockHeightYards)
        {
            var candidate = new Vector3(x, y, floor + 0.05f);
            if (OnDryDockMesh(map, candidate))
            {
                point = candidate;
                return true;
            }
        }

        foreach (float zOff in DockSearchZOffsets)
        {
            var probe = new Vector3(x, y, portZ + zOff);
            PathResult res = PlayerbotThinkProbe.Find(map.Collision, probe, probe, new PathOptions
            {
                MaxPoints = 4,
                IncludeFlags = NavTerrain.Ground,
                ExcludeFlags = NavTerrain.SteepSlopes | NavTerrain.Water,
                AllowPartial = true,
                MaxSearchNodes = 64,
                LoadTiles = true,
                NonBlockingTiles = true, // a tile not in yet is read on the pool; the probe is then no point (TilesPending) and the next think asks again
            });
            if ((res.Type & PathType.TilesPending) != 0 || PlayerbotThinkProbe.LastQueryCapped) t_dockPending = true;
            if ((res.Type & (PathType.NoPath | PathType.NotUsingPath | PathType.TilesPending)) == 0
                && MathF.Abs(res.End.Z - portZ) <= DockHeightYards
                && (res.Points.Count == 0 || MathF.Abs(res.End.Z - probe.Z) <= 5f))
            {
                Vector3 end = res.Points.Count > 0 ? res.End : probe;
                var candidate = new Vector3(end.X, end.Y, end.Z + 0.05f);
                if (Flat(candidate, portPos) <= BoardReachYards - 0.5f
                    && !InDockWater(map, candidate)
                    && !PlayerbotNavigation.IsPointOnMeshFragment(map, candidate)
                    && OnDryDockMesh(map, candidate))
                {
                    point = candidate;
                    return true;
                }
            }
        }

        point = default;
        return false;
    }

    private static bool InDockWater(Map map, Vector3 point)
        => (map.GetLiquidStatus(point.X, point.Y, point.Z + 0.01f, LiquidTypeFlags.AllLiquids, out _)
            & (LiquidStatus.InWater | LiquidStatus.UnderWater)) != 0;

    private static bool OnDryDockMesh(Map map, Vector3 point)
    {
        if (InDockWater(map, point))
            return false;
        PathResult res = PlayerbotThinkProbe.Find(map.Collision, point, point, new PathOptions
        {
            MaxPoints = 4,
            IncludeFlags = NavTerrain.Ground,
            ExcludeFlags = NavTerrain.SteepSlopes | NavTerrain.Water,
            AllowPartial = true,
            MaxSearchNodes = 64,
            LoadTiles = true,
            NonBlockingTiles = true, // pending is not dry mesh: the dock resolves on a later think, once the tile is in
        });
        if ((res.Type & PathType.TilesPending) != 0 || PlayerbotThinkProbe.LastQueryCapped) t_dockPending = true;
        return (res.Type & (PathType.NoPath | PathType.NotUsingPath | PathType.TilesPending)) == 0
            && (res.Points.Count == 0 || MathF.Abs(res.End.Z - point.Z) <= 5f)
            && !PlayerbotNavigation.IsPointOnMeshFragment(map, res.Points.Count > 0 ? res.End : point);
    }

    private static bool ValidHeight(float height)
        => float.IsFinite(height) && height > TerrainTile.InvalidHeight && height != TerrainTile.InvalidHeightValue;

    private static Vector3 Position(Player player) => new(player.X, player.Y, player.Z);

    private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y));

    private static float Heading(Vector3 from, Vector3 to)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y;
        return dx * dx + dy * dy > 1e-6f ? PlayerbotMotion.Normalize(MathF.Atan2(dy, dx)) : 0f;
    }
}
