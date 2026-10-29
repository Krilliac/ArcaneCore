using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The movement "client" of a server-managed player: it moves the bot along its route the way a 1.12 client moves its
/// own character, and reports it with the packets a real client sends — MSG_MOVE_START_FORWARD, a heartbeat about
/// every 500 ms (and when the route turns), MSG_MOVE_STOP — through the ordinary movement handler, which relays them to
/// every observer. With <see cref="PlayerbotOptions.MovementPackets"/> off the server applies the same moves itself
/// (<see cref="WorldSession.TryManagedMovement"/>): observers receive the same MSG_MOVE_* stream, without the opcode dispatch.
/// <para>
/// Observers keep a remote player running along the last packet's orientation at its known speed until the next
/// packet. So every packet's position is the exact route position at that packet's server time (distance = speed ×
/// elapsed time since the previous packet, not since the bot last "thought"), its orientation is the direction of the
/// route segment the bot is on, and the cadence comes from the world tick (<see cref="Pump"/>), not from the brain's
/// think interval. Movement packets are never refused for lack of the shared per-tick action budget: a client always
/// reports its own motion.
/// </para>
/// <para>
/// It also watches for loops: a bot whose goals keep sending it back and forth between the same places, or that runs
/// a lot without getting anywhere, stops; the destinations involved are refused by
/// <see cref="PlayerbotNavigation.TryPlan"/> for <see cref="LoopBlacklistMs"/>, so its goals pick something else.
/// </para>
/// </summary>
internal static partial class PlayerbotMotion
{
    /// <summary>A real client heartbeats about every 500 ms while moving.</summary>
    internal const uint HeartbeatIntervalMs = 500;

    /// <summary>A route turn sharper than this (about 11.5 degrees) is reported at once with the new orientation.</summary>
    internal const float TurnRadians = 0.20f;

    /// <summary>How long a destination involved in a loop is refused.</summary>
    internal const uint LoopBlacklistMs = 60_000;

    /// <summary>Destinations closer than this are the same place for loop detection and the blacklist.</summary>
    internal const float SamePlaceYards = 5f;

    /// <summary>
    /// Consecutive step or packet rejections towards the same destination before the motion blacklists the stuck target
    /// and sets <c>LoopPending</c> so the caller drops its route and replans instead of spinning 50+ times.
    /// </summary>
    internal const int MaxRejectionsBeforeLoop = 3;

    /// <summary>
    /// Minimum height above the walkable floor at which an un-surfaced leg or direct-step start is draped onto the floor
    /// (up to <see cref="PlayerbotMoveCheck.DirectStepMaxDrapeYards"/>).
    /// </summary>
    internal const float DirectStepDrapeMinYards = 2.5f;

    private static readonly ConditionalWeakTable<Player, MotionState> States = [];

    internal sealed class MotionState
    {
        internal WorldRuntime? World;
        internal WorldSession? Session;
        internal ArcaneCore.Game.Teleport.TeleportService? Teleports;
        internal PlayerbotTriggerTracker? TriggerTracker;
        internal ElevatorTrip? ElevatorTrip;
        internal ShipTrip? ShipTrip;
        internal PlayerbotRoute? Route;
        internal Map? Map;
        internal Vector3 Anchor;
        internal uint AnchorMs;
        internal float Heading;
        internal float Speed;
        internal bool Walk;
        /// <summary>Whether the bot is at swim depth (<see cref="SwimAt"/>) as of its last packet's validated position (<see cref="SwimHeight"/> applied): <see cref="Speed"/> is its swim speed then.</summary>
        internal bool Swim;
        internal float MoveSpeedCap;
        /// <summary>The live options the bot follows its routes with (<c>.reload config</c> changes them in place).</summary>
        internal PlayerbotOptions? Options;
        internal uint LeaseMs;
        internal uint RenewedMs;
        /// <summary>Times a think started or kept this bot's route (<see cref="Follow"/>): a think that leaves it unchanged did not move the bot on.</summary>
        internal uint Renewals;
        /// <summary>The motion-clock time of the route's last <see cref="Follow"/> (adopted or renewed); <see cref="SettleAfterDriver"/> renews an unreleased route only for <see cref="SettleBoundMs"/> after it.</summary>
        internal uint FollowedMs;
        /// <summary>The bot's think gap in ms, smoothed (<see cref="NoteThinkGap"/>); 0 until the first measured gap.</summary>
        internal uint GapMs;
        /// <summary>The scheduler's measured main-lane full-pass time in ms (<see cref="NoteSchedulerPass"/>); 0 until the feature has one.</summary>
        internal uint PassMs;
        /// <summary>The server time of the bot's last think (<see cref="NoteThink"/>); meaningful only when <see cref="Thought"/>.</summary>
        internal uint LastThinkMs;
        /// <summary>Which driver made the last think (a static string: no allocation per think).</summary>
        internal string LastThinkDriver = "";
        /// <summary>Whether the bot has thought since its motion state was made: <see cref="LastThinkMs"/> and <see cref="LastThinkDriver"/> mean nothing until then.</summary>
        internal bool Thought;
        internal readonly PlayerbotLoopDetector Loops = new();
        internal readonly List<(Vector3 Destination, uint UntilMs)> Blacklist = [];
        internal bool LoopPending;
        internal int LoopCount;
        internal int ConsecutiveRejections;
        internal Vector3 LastRejectedDestination;
        internal uint Packets;
        /// <summary>The outcome checks' rate limits and samples (<see cref="PlayerbotMotionAnomalies"/>).</summary>
        internal readonly PlayerbotMotionAnomalies.MotionWatch Watch = new();

        internal bool Active => Route is not null;

        internal ArcaneCore.Game.Teleport.TeleportService? TeleportsOf(WorldSession session)
        {
            if (ReferenceEquals(Session, session)) return Teleports;
            Session = session;
            return Teleports = session.Services.GetService<TeleportFeature>()?.Teleports;
        }

        internal void Clear()
        {
            Route = null;
            Map = null;
        }
    }

    internal static MotionState Of(Player player) => States.GetOrCreateValue(player);

    internal static bool TryGetState(Player player, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MotionState? state)
        => States.TryGetValue(player, out state);

    /// <summary>The route the bot is moving along now, or null when its motion is idle.</summary>
    internal static PlayerbotRoute? RouteOf(Player player) => States.TryGetValue(player, out MotionState? state) ? state.Route : null;

    /// <summary>After a driver's think (battleground, bot-led group, party AI). The driver and its helpers hand their routes to Follow
    /// every decided think; a think that did not (a gate-only tick, an instant cast or buff on the way) kept the route unless its owner
    /// let go of it (PlayerbotRoute.Released, set by the owner's route slot): a released route is stopped now, a kept one is renewed.
    /// A kept route is renewed only while less than SettleBoundMs (the larger of its lease and three of the bot's smoothed think gaps) has passed since
    /// its last Follow (MotionState.FollowedMs): two keep-thinks in a row (two instant buffs) under a heavy rotation still renew, while a
    /// route nobody re-asserts, e.g. one of an owner without a releasing slot, lives a bounded time. renewalsBefore is
    /// RenewalsOf before the think. True when it stopped the bot.</summary>
    internal static bool SettleAfterDriver(WorldSession session, Player player, uint renewalsBefore, uint sinceLast)
    {
        if (sinceLast == 0 || !States.TryGetValue(player, out MotionState? state) || !state.Active || state.Route is not { } route) return false;
        if (state.Renewals != renewalsBefore) return false;
        if (route.Released) return SettleAfterThink(session, player, gaveUp: true, renewalsBefore, sinceLast);
        if (Age(session.World.NowMs, state.FollowedMs) >= SettleBoundMs(state)) return false;
        return SettleAfterThink(session, player, gaveUp: false, renewalsBefore, sinceLast);
    }

    /// <summary>How long after its last Follow an unreleased route may still be renewed by <see cref="SettleAfterDriver"/>:
    /// <c>max(lease, 2 gaps + 1 gap)</c> of the bot's smoothed think gap (the scheduler's pass while none is measured, as <see cref="Lease"/>
    /// takes it). The lease alone is not enough: <see cref="LeaseCapMs"/> can hold it under two gaps, and then a single keep-think would
    /// be the last. Two gaps are the two keep-thinks in a row (two instant buffs) that must both renew, and the margin of one more gap is
    /// the jitter of a rotation whose real gap runs over its smoothed average. Uncapped the lease is already 3 gaps, so the bound equals it
    /// and is not stretched. Worst-case lifetime of a route nobody follows: a settle just inside the bound renews it, and it then walks
    /// one lease from that renewal, so bound + lease after its last Follow. At a 30 s gap that is 105 s (cap at its 15 s floor: bound 90 s,
    /// lease 15 s) to 180 s (uncapped: bound 90 s, lease 90 s). At saturation (the gap and lease at <see cref="HardMaxGapLeaseMs"/>, 120 s)
    /// it is 480 s (bound 360 s, lease 120 s).</summary>
    internal static long SettleBoundMs(MotionState state)
    {
        long gap = state.GapMs != 0 ? state.GapMs : state.PassMs;
        long margin = gap;
        return Math.Max(state.LeaseMs, (2L * gap) + margin);
    }

    /// <summary>Whether the bot is moving along a route this class drives.</summary>
    internal static bool IsActive(Player player) => States.TryGetValue(player, out MotionState? state) && state.Active;

    /// <summary>Loops detected so far (inspection).</summary>
    internal static int LoopCount(Player player) => States.TryGetValue(player, out MotionState? state) ? state.LoopCount : 0;

    /// <summary>Consecutive rejected movement attempts towards the current destination (inspection).</summary>
    internal static int ConsecutiveRejections(Player player)
        => States.TryGetValue(player, out MotionState? state) ? state.ConsecutiveRejections : 0;

    /// <summary>True once after a loop was detected: the brain drops its current target and intent.</summary>
    internal static bool ConsumeLoop(Player player)
    {
        if (!States.TryGetValue(player, out MotionState? state) || !state.LoopPending) return false;
        state.LoopPending = false;
        return true;
    }

    /// <summary>
    /// Forget the motion without a packet (death, teleport: the server already placed the bot), and what area trigger volumes
    /// it was inside (<see cref="PlayerbotAreaTriggers"/>): where it lands only sets them again.
    /// </summary>
    internal static void Reset(Player player)
    {
        if (States.TryGetValue(player, out MotionState? state)) state.Clear();
        PlayerbotAreaTriggers.Reset(player);
    }

    /// <summary>
    /// Forget the loop watch's history and the destinations it refuses (a death: <see cref="PlayerbotRecovery"/> at the first think of a
    /// new death). The routes of a ghost and those of the living bot before it are not one errand going back and forth: live
    /// 2026-10-11 (world.log 08:07-08:09, Wfbotfe), a bot revived by the Raven Hill spirit healer among the skeletons there retreated,
    /// died, walked to the same healer again, and faulted with <see cref="PlayerbotRecovery.SpiritHealerFailed"/> 21 s after its third
    /// death. In the world test that replays the sequence (PlayerbotUnsafeBodyRecoveryTests) the third walk to the healer completed an
    /// A, B, A pattern with the retreats, the healer's place was refused for <see cref="LoopBlacklistMs"/>, the ghost could plan no
    /// route to it, and the stuck bound faulted the recovery. A ghost's walk has its own bounds (the recovery's stuck and
    /// no-progress bounds); a revived bot that loops again is caught again.
    /// </summary>
    internal static void ForgetLoops(Player player)
    {
        if (!States.TryGetValue(player, out MotionState? state)) return;
        state.Loops.Reset();
        state.Blacklist.Clear();
        state.LoopPending = false;
        state.ConsecutiveRejections = 0;
        state.LastRejectedDestination = default;
    }

    /// <summary>
    /// Lifts the refusal of one place: removes the blacklist entries within <see cref="SamePlaceYards"/> of <paramref name="destination"/>
    /// and nothing else. The other refused places, the loop pattern and the rejection count stay (<see cref="ForgetLoops"/> clears them
    /// all, which a caller that may run again and again would turn into a guard that never trips). 2D like <see cref="IsBlacklisted"/>, whose refusal it lifts. Thread affinity: the world update, as
    /// the rest of the motion state.
    /// </summary>
    internal static void Unblacklist(Player player, Vector3 destination)
    {
        if (!States.TryGetValue(player, out MotionState? state) || state.Blacklist.Count == 0) return;
        state.Blacklist.RemoveAll(entry => Vector2.Distance(Flat(entry.Destination), Flat(destination)) <= SamePlaceYards);
    }

    /// <summary>
    /// Refuses <paramref name="places"/> for <see cref="LoopBlacklistMs"/> from <paramref name="now"/> and marks a loop pending so the
    /// brain drops its current target and intent on the next think.
    /// </summary>
    internal static void Blacklist(Player player, ReadOnlySpan<Vector3> places, uint now)
    {
        MotionState state = Of(player);
        foreach (Vector3 place in places)
        {
            if (!place.IsFinite()) continue;
            state.Blacklist.RemoveAll(entry => Vector2.Distance(Flat(entry.Destination), Flat(place)) <= SamePlaceYards);
            state.Blacklist.Add((place, unchecked(now + LoopBlacklistMs)));
        }

        state.LoopPending = true;
    }

    /// <summary>Whether <paramref name="destination"/> was given up after a loop and is still refused.</summary>
    internal static bool IsBlacklisted(Player player, Vector3 destination)
    {
        if (!States.TryGetValue(player, out MotionState? state) || state.Blacklist.Count == 0)
            return false;
        if (state.World is { } world)
        {
            uint now = world.NowMs;
            state.Blacklist.RemoveAll(entry => unchecked((int)(entry.UntilMs - now)) <= 0);
        }

        foreach ((Vector3 blocked, _) in state.Blacklist)
            if (Vector2.Distance(Flat(blocked), Flat(destination)) <= SamePlaceYards) return true;
        return false;
    }

    /// <summary>
    /// Where the bot is now: its route position at the current server time while moving (the stored position is that
    /// of the last packet, up to a heartbeat old), otherwise its stored position.
    /// </summary>
    internal static Vector3 CurrentPosition(Player player)
    {
        var stored = new Vector3(player.X, player.Y, player.Z);
        if (!States.TryGetValue(player, out MotionState? state) || state.Route is not { } route || state.World is not { } world
            || !ReferenceEquals(player.Map, state.Map))
            return stored;
        uint age = Age(world.NowMs, state.AnchorMs);
        Vector3 position = Walk(route, state.Anchor, route.NextPoint, state.Speed * age / 1000f, state.Heading,
            out _, out _, out _, out _);
        return position.IsFinite() ? position : stored;
    }

    /// <summary>
    /// Follow <paramref name="route"/> (the brain and its goals call this every think). Starts moving with
    /// MSG_MOVE_START_FORWARD, switches to a different route with a heartbeat carrying the new orientation, and renews
    /// the lease on the current route. False when the bot cannot move along it (the caller drops the route).
    /// </summary>
    internal static bool Follow(WorldSession session, Player player, PlayerbotRoute route, PlayerbotOptions options, uint now,
        bool keepMotion = false)
    {
        MotionState state = Of(player);
        state.World = session.World;
        state.Options = options;
        if (player.Map is not { } map) return false;
        if (route.Complete || route.Points.Count < 2 || route.IsStaleFor(map))
        {
            if (!keepMotion) Stop(session, player);
            return false;
        }
        if (IsBlacklisted(player, route.Points[^1]))
        {
            if (!keepMotion) Stop(session, player);
            return false;
        }

        if (state.Active && ReferenceEquals(state.Route, route))
        {
            state.RenewedMs = now;
            state.LeaseMs = Lease(options, state);
            state.Renewals++;
            route.Released = false;
            state.FollowedMs = now;
            Pump(session, player, now);
            return state.Active || route.Complete;
        }

        if ((player.Movement.Flags & (MovementFlags.Root | MovementFlags.Jumping | MovementFlags.FallingFar)) != 0)
        {
            if (!keepMotion) Stop(session, player);
            return false;
        }

        // A stun, fear or confusion pauses the walk: nothing starts (no packet), and the caller keeps its route (TryAdvance answers true).
        if (ControlLost(player)) return false;

        // A teleport under way (a stalled bot's teleport home, an elevator rescue), a pending quest settlement, or standing aboard a
        // transport takes no ground movement (MovementHandlers; vmangos MovementHandler.cpp:311-313), so a start sent now would be
        // rejected (bot QA #792). Nothing is sent or reported; the caller replans once the transition settles.
        if (state.TeleportsOf(session)?.IsBeingTeleported(player) == true
            || player.IsQuestSettlementPending
            || player.Transport is not null
            || (player.Movement.Flags & MovementFlags.OnTransport) != 0)
        {
            state.Clear();
            return false;
        }

        // Only the can-move check; the speed that counts is the one at the place the walk starts (below).
        if (!Speed(player, options.MoveSpeed, swimming: false, out float speed, out bool walk))
        {
            if (!keepMotion) Stop(session, player);
            return false;
        }

        // Switching routes: catch the old motion up to now first, so the new one starts where observers see the bot.
        bool moving = (player.Movement.Flags & MovementFlags.MaskMoving) != 0;
        Vector3 current = new(player.X, player.Y, player.Z);
        if (state.Active && ReferenceEquals(state.Map, map) && (player.Movement.Flags & MovementFlags.Forward) != 0)
        {
            // One heartbeat at the current position with the new orientation replaces the old route's next packet.
            Vector3 position = Walk(state.Route!, state.Anchor, state.Route!.NextPoint,
                state.Speed * Age(now, state.AnchorMs) / 1000f, state.Heading, out int reached, out _, out _, out _);
            current = Validate(map, state.Route!, state.Route!.NextPoint, reached, state.Anchor, position) ?? state.Anchor;
        }
        else if (!moving && !route.LegsNavigated(route.NextPoint, route.NextPoint))
        {
            TrySnapDirectStepStart(map, player, ref current);
        }

        float skipDistance = moving ? 0.30f : 0.05f;
        while (route.NextPoint < route.Points.Count - 1 && Vector2.Distance(Flat(current), Flat(route.Points[route.NextPoint])) <= skipDistance)
            route.NextPoint++;
        if (route.LegsNavigated(route.NextPoint, route.NextPoint) && !OnLeg(route, route.NextPoint, current))
        {
            for (int idx = route.NextPoint + 1; idx < route.Points.Count; idx++)
            {
                if (route.LegsNavigated(idx, idx)
                    && OnLeg(route, idx, current)
                    && PlayerbotMoveCheck.LineClear(map.Collision, current, route.Points[idx]))
                {
                    route.NextPoint = idx;
                    break;
                }
            }

            if (route.LegsNavigated(route.NextPoint, route.NextPoint) && !OnLeg(route, route.NextPoint, current))
            {
                route.NextPoint = route.Points.Count;
                if (!keepMotion) Stop(session, player);
                return false;
            }
        }
        // The speed is the one at the place the walk starts: swim speed in swimmable water. A mesh start on the surface of deep water
        // is put at swim depth first (SwimHeight), so the start packet, the speed and the environment flags agree.
        current = SwimHeight(map, player, current);
        bool swim = SwimAt(map, player, current);
        if (!Speed(player, options.MoveSpeed, swim, out speed, out walk))
        {
            if (!keepMotion) Stop(session, player);
            return false;
        }

        // The first stretch must be walkable before anything is announced.
        float probeStep = MathF.Max(speed * (HeartbeatIntervalMs / 1000f), 2f);
        Vector3 probe = Walk(route, current, route.NextPoint, probeStep,
            HeadingTo(current, route.Points[route.NextPoint]), out int probed, out _, out _, out _);
        bool noStretch = Vector2.Distance(Flat(current), Flat(probe)) <= 0.05f;
        string? rejectReason = null;
        Vector3? validStart = noStretch ? null : Validate(map, route, route.NextPoint, probed, current, probe, out rejectReason);
        if (!noStretch && validStart is null && TrySkipBlockedMicroCorner(map, player, route, ref current, probeStep, out probe, out probed, out validStart, out rejectReason))
        {
            noStretch = false;
        }

        bool hadBudget = session.ManagedBudget is not { Remaining: <= 0 };
        if (!moving && !hadBudget) return false;

        if (noStretch || validStart is null)
        {
            bool driftedOffNavigatedLeg = route.LegsNavigated(route.NextPoint, route.NextPoint) && !OnLeg(route, route.NextPoint, current);
            if (!noStretch && !driftedOffNavigatedLeg)
            {
                string mode = MoveModeOf(route, route.NextPoint, probed);
                TrySnapPlayerToFloor(map, player, ref current);
                AdvancePastRejectedWaypoint(route, current);
                int consecutive = NoteStepRejected(session, player, state, route, now);
                PlayerbotMotionAnomalies.ReportStartBlocked(session, player, route, current, now, consecutive, rejectReason ?? "NoGroundSupport", mode);
            }

            route.NextPoint = route.Points.Count;
            if (!keepMotion) Stop(session, player);
            return false;
        }

        float heading = HeadingTo(current, route.Points[route.NextPoint]);
        // A deliberate start spends the caller's action budget; a running bot changing its route does not.
        WorldOpcode opcode = moving ? WorldOpcode.MsgMoveHeartbeat : WorldOpcode.MsgMoveStartForward;
        if (!Send(session, player, opcode, current, heading, walk, moving: true, now, budgeted: !moving, swim: swim))
        {
            // A start the caller's action budget refused is its own back-off, not the server refusing the move.
            if (hadBudget || moving)
            {
                string mode = MoveModeOf(route, route.NextPoint, probed);
                TrySnapPlayerToFloor(map, player, ref current);
                AdvancePastRejectedWaypoint(route, current);
                int consecutive = NoteStepRejected(session, player, state, route, now);
                PlayerbotMotionAnomalies.ReportMoveRejected(session, player, opcode.ToString(), current, now, consecutive, "NoGroundSupport", mode);
                route.NextPoint = route.Points.Count;
            }

            if (!moving) state.Clear();
            return false;
        }

        if (state.ConsecutiveRejections == 0 || Vector2.DistanceSquared(Flat(state.Anchor), Flat(current)) >= 1f)
        {
            state.ConsecutiveRejections = 0;
            state.LastRejectedDestination = default;
        }
        state.Route = route;
        route.Released = false;
        state.FollowedMs = now;
        state.Map = map;
        state.Anchor = current;
        state.AnchorMs = now;
        state.Heading = heading;
        state.Speed = speed;
        state.Walk = walk;
        state.Swim = swim;
        state.MoveSpeedCap = options.MoveSpeed;
        state.RenewedMs = now;
        state.LeaseMs = Lease(options, state);
        state.Renewals++;
        PlayerbotAreaTriggers.Begin(session, player, map, current, options, state.TriggerTracker ??= PlayerbotAreaTriggers.TrackerOf(player));
        // A chase follows a moving target: its destinations are not a back-and-forth (kiting a mob must not blacklist the spot).
        if (!route.Chase && state.Loops.OnRoute(route.Points[^1], now) is { } loop)
        {
            GiveUp(session, player, state, loop, now);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Advance the motion to <paramref name="now"/> (world thread, every world tick for every bot, whatever its brain
    /// does): a heartbeat every <see cref="HeartbeatIntervalMs"/> and at route turns, STOP on arrival, when the route
    /// is abandoned (not followed for a few thinks) or when the bot can no longer move.
    /// </summary>
    internal static void Pump(WorldSession session, Player player, uint now)
    {
        PlayerbotMotionAnomalies.CheckStoredPosition(session, player, now);
        // Between maps the bot's brain does not run; the client half still ends its loading screen (PlayerbotMovementControl).
        if (PlayerbotMovementControl.AcknowledgeTransfer(session, player)) return;
        PlayerbotTram.Pump(session, player, now);
        if (!States.TryGetValue(player, out MotionState? state)) return;
        if (state.ElevatorTrip is not null) PlayerbotElevator.Pump(session, player, now);
        if (state.ShipTrip is not null) PlayerbotShip.Pump(session, player, now); // a short stop at a port is not missed between thinks (boarding, stepping off)
        if (state.Route is not { } route) return;
        state.World = session.World;
        if (!CanMove(player) || !player.IsInWorld || player.Map is not { } map || !ReferenceEquals(map, state.Map)
            || state.TeleportsOf(session)?.IsBeingTeleported(player) == true
            || player.IsQuestSettlementPending
            || player.Transport is not null
            || (player.Movement.Flags & MovementFlags.OnTransport) != 0
            || player.Locomotion.ActiveSpline is not null
            || (player.Movement.Flags & MovementFlags.Forward) == 0)
        {
            // Something else already moved or stopped the bot (death, teleport, quest settlement, transport, charge spline, a root acknowledgement).
            state.Clear();
            return;
        }

        if (route.IsStaleFor(map))
        {
            StopMotion(session, player, state, now);
            return;
        }

        PlayerbotMotionAnomalies.CheckJump(session, player, state, now);
        uint age = Age(now, state.AnchorMs);
        if (age == 0) return;
        if ((player.Movement.Flags & (MovementFlags.Root | MovementFlags.Jumping | MovementFlags.FallingFar)) != 0
            || ControlLost(player)
            || player.StandState != StandState.Stand || Age(now, state.RenewedMs) > state.LeaseMs)
        {
            PlayerbotMotionAnomalies.CheckLeaseExpired(session, player, route, state, now);
            StopMotion(session, player, state, now);
            return;
        }

        Vector3 position = Walk(route, state.Anchor, route.NextPoint, state.Speed * age / 1000f, state.Heading,
            out int nextPoint, out float heading, out bool arrived, out bool turned);
        if (!position.IsFinite()) PlayerbotMotionAnomalies.ReportNonFinite(session, player, "the position the route walk computed", now);
        PlayerbotTriggerTracker tracker = state.TriggerTracker ??= PlayerbotAreaTriggers.TrackerOf(player);
        // A client checks its area triggers every frame; this checks every world tick. Stepping into a volume is reported at
        // once: a heartbeat where the bot is now, then CMSG_AREATRIGGER (PlayerbotAreaTriggers).
        bool entering = PlayerbotAreaTriggers.WouldEnter(session, player, map, position, tracker);
        if (arrived)
        {
            if (Validate(map, route, route.NextPoint, nextPoint, state.Anchor, position, out string? arrivalReject) is not { } arrivedAt)
            {
                // The last step is refused: the bot stays where the last packet put it, and the route is not complete (it is
                // reported and damped like a refused heartbeat, so a brain that re-plans to the same target does not loop silently).
                string arrivalMode = MoveModeOf(route, route.NextPoint, nextPoint);
                AdvancePastRejectedWaypoint(route, state.Anchor);
                int arrivalRejections = NoteStepRejected(session, player, state, route, now);
                PlayerbotMotionAnomalies.ReportRouteBlocked(session, player, route, state.Anchor, now, arrivalRejections, arrivalReject ?? "NoGroundSupport", arrivalMode);
                StopAt(session, player, state, state.Anchor, state.Heading, route.NextPoint, now);
                return;
            }

            StopAt(session, player, state, arrivedAt, heading, nextPoint, now);
            if (ReferenceEquals(player.Map, map)) PlayerbotAreaTriggers.Update(session, player, map, arrivedAt, tracker);
            return;
        }

        // Entering or leaving swimmable water changes the bot's speed: re-anchor there at once (a client sends MSG_MOVE_START_SWIM /
        // MSG_MOVE_STOP_SWIM as it crosses), so the next step is walked from here at the new speed and the position does not jump.
        // The gate below only decides whether to look closer: per bot per tick it costs the liquid query of SwimHeight and the one of
        // SwimAt (and a floor query only when the position is at the liquid surface). The packet's own swim value is decided once, on
        // the validated position, below; both use SwimHeight, so a gate that opens is never contradicted into a repeat.
        if (!turned && !entering && SwimAt(map, player, SwimHeight(map, player, position)) == state.Swim && age < HeartbeatIntervalMs) return;
        if (Validate(map, route, route.NextPoint, nextPoint, state.Anchor, position, out string? rejectReason) is not { } valid)
        {
            // The route is no longer walkable here: stop where the last packet put the bot.
            string mode = MoveModeOf(route, route.NextPoint, nextPoint);
            AdvancePastRejectedWaypoint(route, state.Anchor);
            int consecutive = NoteStepRejected(session, player, state, route, now);
            PlayerbotMotionAnomalies.ReportRouteBlocked(session, player, route, state.Anchor, now, consecutive, rejectReason ?? "NoGroundSupport", mode);
            route.NextPoint = route.Points.Count;
            StopAt(session, player, state, state.Anchor, state.Heading, route.Points.Count, now);
            return;
        }

        valid = SwimHeight(map, player, valid);
        bool swimNow = SwimAt(map, player, valid);
        bool swimFlip = swimNow != state.Swim;
        WorldOpcode stepOpcode = !swimFlip ? WorldOpcode.MsgMoveHeartbeat : swimNow ? WorldOpcode.MsgMoveStartSwim : WorldOpcode.MsgMoveStopSwim;
        if (!Send(session, player, stepOpcode, valid, heading, state.Walk, moving: true, now, budgeted: false, swim: swimNow))
        {
            string mode = MoveModeOf(route, route.NextPoint, nextPoint);
            AdvancePastRejectedWaypoint(route, state.Anchor);
            int consecutive = NoteStepRejected(session, player, state, route, now);
            PlayerbotMotionAnomalies.ReportMoveRejected(session, player, "a heartbeat", valid, now, consecutive, "NoGroundSupport", mode);
            route.NextPoint = route.Points.Count;
            state.Clear();
            return;
        }

        state.ConsecutiveRejections = 0;
        state.LastRejectedDestination = default;
        int walkedFrom = route.NextPoint;
        route.NextPoint = nextPoint;
        float travelled = Vector2.Distance(Flat(state.Anchor), Flat(valid));
        Vector3 walkedFromAt = state.Anchor;
        state.Anchor = valid;
        state.AnchorMs = now;
        state.Heading = heading;
        bool walkedInWalkMode = state.Walk;
        PlayerbotMotionAnomalies.AfterHeartbeat(session, player, map, state, route, walkedFromAt, walkedFrom, nextPoint, valid, now, age, state.Swim, walkedInWalkMode);
        // A client always moves at its current speed (a mount gone at swim depth or after a dismount, a swim speed left behind on the
        // shore): the next step is walked at it. After the overspeed check, which judged the step at the speed it was walked at.
        // The walk mode is not changed here by a changed cap alone (Follow decides it, as before); it is adopted when the swim state
        // flipped, so that leaving the water always drops the swim speed even if the cap now asks for walking.
        state.Swim = swimNow;
        if (Speed(player, state.MoveSpeedCap, state.Swim, out float currentSpeed, out bool currentWalk) && (currentWalk == walkedInWalkMode || swimFlip))
        {
            state.Speed = currentSpeed;
            state.Walk = currentWalk;
        }
        if (state.Loops.OnPosition(valid, travelled, now) is { } loop) GiveUp(session, player, state, loop, now);
        // Last: a trigger may teleport the bot (the next pump then finds it between maps and forgets the route).
        PlayerbotAreaTriggers.Update(session, player, map, position, tracker);
    }

    /// <summary>
    /// Stop now (unbudgeted): MSG_MOVE_STOP at the bot's current route position, where observers already see it. True
    /// when the bot is stopped afterwards.
    /// </summary>
    internal static bool Stop(WorldSession session, Player player)
    {
        uint now = session.World.NowMs;
        MotionState state = Of(player);
        state.World = session.World;
        if (state.Active) StopMotion(session, player, state, now);

        if ((player.Movement.Flags & MovementFlags.MaskMoving) == 0) return true;
        Send(session, player, WorldOpcode.MsgMoveStop, new(player.X, player.Y, player.Z), player.Orientation,
            (player.Movement.Flags & MovementFlags.WalkMode) != 0, moving: false, now, budgeted: false);
        return (player.Movement.Flags & MovementFlags.MaskMoving) == 0;
    }

    /// <summary>
    /// Turn on the spot to <paramref name="heading"/> (MSG_MOVE_SET_FACING), as a client does when the player attacks a target
    /// beside or behind it. Unbudgeted, like a stop: turning is not a managed action. The bot must stand still (the caller stops it
    /// first); true when the server stored the new orientation.
    /// </summary>
    internal static bool Face(WorldSession session, Player player, float heading)
    {
        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0 || Of(player).Active) return false;
        return Send(session, player, WorldOpcode.MsgMoveSetFacing, new(player.X, player.Y, player.Z), heading,
            (player.Movement.Flags & MovementFlags.WalkMode) != 0, moving: false, session.World.NowMs, budgeted: false);
    }

    /// <summary>
    /// Step off an edge (a follower's drop, <see cref="Party.PlayerbotFollowDrop"/>): the heartbeat a client sends as it walks over the
    /// edge and starts to fall, at <paramref name="stepOff"/> (the edge's height, one step out) with MOVEFLAG_FALLINGFAR. The server
    /// records where the fall began (vmangos <c>Player::UpdateFallInformationIfNeed</c>, <see cref="FallObserver"/>). Unbudgeted, like
    /// every movement report; the bot must not be moving along a route (the caller stops it first). True when the server stored it.
    /// </summary>
    internal static bool StartFall(WorldSession session, Player player, Vector3 stepOff, float heading)
    {
        MotionState state = Of(player);
        state.World = session.World;
        if (state.Active || !CanMove(player)) return false;
        return Send(session, player, WorldOpcode.MsgMoveHeartbeat, stepOff, heading, walk: false, moving: true, session.World.NowMs,
            budgeted: false, MovementFlags.FallingFar);
    }

    /// <summary>
    /// Land after <see cref="StartFall"/>: MSG_MOVE_FALL_LAND at <paramref name="landing"/> with the time the fall took, as a client
    /// sends it; the server applies vmangos' fall damage (<c>Player::HandleFall</c>). True when the bot stands there afterwards.
    /// </summary>
    internal static bool Land(WorldSession session, Player player, Vector3 landing, float heading, uint fallTimeMs)
    {
        Of(player).World = session.World;
        return Send(session, player, WorldOpcode.MsgMoveFallLand, landing, heading, walk: false, moving: false, session.World.NowMs,
            budgeted: false, fallTime: fallTimeMs);
    }

    /// <summary>
    /// Stand on an elevator or tram car (<see cref="PlayerbotTram"/>): MSG_MOVE_HEARTBEAT with MOVEFLAG_ONTRANSPORT, the car's GUID and the
    /// bot's offset on it, at the world position the car and the offset give, as a 1.12 client reports itself standing on a transport
    /// (vmangos MovementInfo transport block; the first such packet is the step aboard). Unbudgeted like every movement report; the bot
    /// must not be following a route (the caller stops it first). True when the server stored it.
    /// </summary>
    internal static bool SendAboard(WorldSession session, Player player, ulong transportGuid, Vector3 world, float heading,
        Vector3 offset, float offsetOrientation)
    {
        MotionState state = Of(player);
        state.World = session.World;
        if (state.Active || !CanMove(player)) return false;
        return Send(session, player, WorldOpcode.MsgMoveHeartbeat, world, heading, walk: false, moving: false, session.World.NowMs,
            budgeted: false, seat: new Seat(true, transportGuid, offset, offsetOrientation));
    }

    /// <summary>
    /// Step off a transport onto <paramref name="world"/>: MSG_MOVE_HEARTBEAT without MOVEFLAG_ONTRANSPORT and without the transport block,
    /// as a client sends once its feet leave the car. True when the server stored it.
    /// </summary>
    internal static bool SendStepOff(WorldSession session, Player player, Vector3 world, float heading)
    {
        MotionState state = Of(player);
        state.World = session.World;
        if (state.Active || !CanMove(player)) return false;
        return Send(session, player, WorldOpcode.MsgMoveHeartbeat, world, heading, walk: false, moving: false, session.World.NowMs,
            budgeted: false, seat: new Seat(false, 0, default, 0));
    }

    private readonly record struct Seat(bool Aboard, ulong Guid, Vector3 Offset, float Orientation);

    /// <summary>
    /// Stop refusing destinations near <paramref name="place"/> that a loop put on the blacklist: a follower's destination is its
    /// leader, and refusing the leader for a minute only strands it (the follower has its own loop detection,
    /// <see cref="Party.PlayerbotFollowProgress"/>).
    /// </summary>
    internal static void Forgive(Player player, Vector3 place)
    {
        if (!States.TryGetValue(player, out MotionState? state)) return;
        state.Blacklist.RemoveAll(entry => Vector2.Distance(Flat(entry.Destination), Flat(place)) <= SamePlaceYards * 2);
        state.LoopPending = false;
        state.ConsecutiveRejections = 0;
        state.LastRejectedDestination = default;
    }

    /// <summary>
    /// Report the current route position with a heartbeat before something else is acknowledged with the stored
    /// movement block (speed/flag orders), so observers do not see the bot jump back to its last packet.
    /// </summary>
    internal static void Flush(WorldSession session, Player player, uint now)
    {
        if (!States.TryGetValue(player, out MotionState? state) || state.Route is not { } route || player.Map is not { } map) return;
        Pump(session, player, now);
        if (!state.Active || Age(now, state.AnchorMs) == 0) return;
        Vector3 position = Walk(route, state.Anchor, route.NextPoint, state.Speed * Age(now, state.AnchorMs) / 1000f,
            state.Heading, out int nextPoint, out float heading, out _, out _);
        if (Validate(map, route, route.NextPoint, nextPoint, state.Anchor, position) is not { } valid
            || !Send(session, player, WorldOpcode.MsgMoveHeartbeat, valid, heading, state.Walk, moving: true, now, budgeted: false))
        {
            state.Clear();
            return;
        }

        route.NextPoint = nextPoint;
        state.Anchor = valid;
        state.AnchorMs = now;
        state.Heading = heading;
    }

    /// <summary>
    /// Test seam: pretend <paramref name="milliseconds"/> of server time passed since the bot's last movement packet.
    /// Unit tests that run several thinks inside one world-thread call (where the server clock does not move) use it
    /// to let the motion progress between them.
    /// </summary>
    internal static void ElapseForTests(Player player, uint milliseconds)
    {
        if (!States.TryGetValue(player, out MotionState? state) || !state.Active) return;
        state.AnchorMs = unchecked(state.AnchorMs - milliseconds);
        state.RenewedMs = unchecked(state.RenewedMs - milliseconds);
        state.FollowedMs = unchecked(state.FollowedMs - milliseconds);
    }

    /// <summary>After an acknowledged speed change the motion continues at the new speed.</summary>
    internal static void RefreshSpeed(Player player)
    {
        if (States.TryGetValue(player, out MotionState? state) && state.Active
            && Speed(player, state.MoveSpeedCap, state.Swim, out float speed, out bool walk) && walk == state.Walk)
            state.Speed = speed;
    }

    /// <summary>
    /// The speed observers use for this bot: its run speed, or — when the configured cap is below it — walk mode at
    /// its walk speed. A client cannot be told any other speed, so moving at another one makes observers drift.
    /// <para>
    /// The cap is compared with the run speed of an unhastened player (<see cref="Unit.BaseRunSpeed"/>) at most: a bot whose run
    /// speed is raised (a ghost, a speed buff) runs at it. Compared with the raised speed, the default cap (7, the base run speed)
    /// put every ghost into walk mode, and a corpse run of 359 yards took two minutes instead of half of one.
    /// </para>
    /// </summary>
    internal static bool Speed(Player player, float cap, out float speed, out bool walk)
        => Speed(player, cap, swimming: false, out speed, out walk);

    /// <summary><see cref="Speed(Player, float, out float, out bool)"/> for a bot that is (<paramref name="swimming"/>) or is not at swim depth.</summary>
    internal static bool Speed(Player player, float cap, bool swimming, out float speed, out bool walk)
    {
        // The MoveSpeed cap is not applied in water: the bot swims at its swim speed.
        if (swimming)
        {
            // In swimmable water a player swims whatever it rides or runs at: the mounted and run speed auras only change the run speed
            // (UnitSpeed), and a mount does not survive the water (AURA_INTERRUPT_UNDER_WATER_CANCELS, LiquidEnvironment.SetFlags).
            walk = false;
            speed = UnitSpeed.Get(player, MoveType.Swim);
            return float.IsFinite(speed) && speed > 0;
        }

        float run = UnitSpeed.Get(player, MoveType.Run);
        walk = float.IsFinite(cap) && cap < MathF.Min(run, Unit.BaseRunSpeed) - 0.001f;
        speed = walk ? UnitSpeed.Get(player, MoveType.Walk) : run;
        return float.IsFinite(speed) && speed > 0;
    }

    /// <summary>
    /// Whether a bot at <paramref name="at"/> on <paramref name="map"/> is at swim depth, by the rule the server applies to its
    /// environment flags (<see cref="LiquidEnvironment.IsSwimDepth(Map, float, float, float, float)"/>, vmangos
    /// Player::UpdateTerainEnvironmentFlags). Never aboard a transport.
    /// </summary>
    internal static bool SwimAt(Map map, Player player, Vector3 at)
        => player.Transport is null && (player.Movement.Flags & MovementFlags.OnTransport) == 0
            && LiquidEnvironment.IsSwimDepth(map, at.X, at.Y, at.Z, player.Locomotion.CollisionHeight);

    /// <summary>
    /// Our <see cref="LiquidEnvironment.IsSwimDepth(Map, float, float, float, float)"/> (vmangos' HighLiquid test) is a strict
    /// <c>level &gt; z + minSwimDepth</c>, so a unit at exactly <c>level - minSwimDepth</c> would not count as swimming; <see cref="SwimHeight"/> goes
    /// this far below that height. A judgement call, not a reference value.
    /// </summary>
    internal const float SwimDepthEpsilonYards = 0.05f;

    /// <summary>
    /// How far above the liquid level a position still counts as "at the surface" for <see cref="SwimHeight"/>. A judgement call that keeps
    /// pier and bridge decks well above the water out of it.
    /// </summary>
    internal const float SurfaceBandYards = 0.25f;

    /// <summary>
    /// A mesh route over deep water runs on the mesh's surface, at the liquid level, where the swim depth rule is false for a bot standing
    /// there. vmangos places a swimming unit at <c>GetWaterLevel(x, y, z, &amp;ground) - GetMinSwimDepth()</c> and accepts that while
    /// <c>ground &lt; z</c> (Unit.cpp:10336; GetMinSwimDepth = collision height * 0.75, Unit.h:543, "client switches to swim animation at this
    /// depth"). So a position at or near the liquid surface (z in [level - minSwimDepth, level + <see cref="SurfaceBandYards"/>]) whose floor lies
    /// below <c>level - minSwimDepth - </c><see cref="SwimDepthEpsilonYards"/> (no floor found counts as deep) is lowered to that height. Shallow water,
    /// a bridge or pier deck (a floor under the point), positions already at swim depth and positions away from the water come back
    /// unchanged. Aboard a transport the position is never changed.
    /// </summary>
    internal static Vector3 SwimHeight(Map map, Player player, Vector3 at)
    {
        if (player.Transport is not null || (player.Movement.Flags & MovementFlags.OnTransport) != 0) return at;
        if (map.GetLiquidStatus(at.X, at.Y, at.Z + 0.01f, LiquidTypeFlags.AllLiquids, out LiquidData liquid) == LiquidStatus.NoWater) return at;
        float minSwimDepth = player.Locomotion.CollisionHeight * 0.75f;
        if (at.Z < liquid.Level - minSwimDepth || at.Z > liquid.Level + SurfaceBandYards) return at;
        float swimZ = liquid.Level - minSwimDepth - SwimDepthEpsilonYards;
        float floor = map.Collision.GetHeight(at.X, at.Y, at.Z);
        if (float.IsFinite(floor) && floor > TerrainTile.InvalidHeight && floor >= swimZ) return at;
        return new Vector3(at.X, at.Y, swimZ);
    }

    /// <summary>
    /// Walk <paramref name="distance"/> horizontal yards along <paramref name="route"/> from <paramref name="from"/>,
    /// heading for point <paramref name="nextPoint"/>. <paramref name="turned"/>: a segment was entered whose direction
    /// differs from <paramref name="heading"/> by more than <see cref="TurnRadians"/>.
    /// </summary>
    internal static Vector3 Walk(PlayerbotRoute route, Vector3 from, int nextPoint, float distance, float heading,
        out int newNextPoint, out float newHeading, out bool arrived, out bool turned)
    {
        Vector3 position = from;
        newHeading = heading;
        turned = false;
        newNextPoint = Math.Max(1, nextPoint);
        float remaining = float.IsFinite(distance) ? MathF.Max(0, distance) : 0;
        float walked = 0f;
        while (newNextPoint < route.Points.Count)
        {
            Vector3 target = route.Points[newNextPoint];
            Vector2 segment = Flat(target) - Flat(position);
            float length = segment.Length();
            if (length <= 0.05f)
            {
                position = position with { Z = target.Z };
                newNextPoint++;
                continue;
            }

            float segmentHeading = Normalize(MathF.Atan2(segment.Y, segment.X));
            newHeading = segmentHeading;
            if (AngleBetween(segmentHeading, heading) > TurnRadians)
                turned = true;

            if (remaining <= 1e-4f) break;
            if (remaining < length)
            {
                position += (target - position) * (remaining / length);
                walked += remaining;
                remaining = 0;
                break;
            }

            position = target;
            walked += length;
            remaining -= length;
            newNextPoint++;
        }

        arrived = newNextPoint >= route.Points.Count;
        return position;
    }

    private static void GiveUp(WorldSession session, Player player, MotionState state, IReadOnlyList<Vector3> places, uint now)
    {
        foreach (Vector3 place in places) state.Blacklist.Add((place, unchecked(now + LoopBlacklistMs)));
        state.LoopPending = true;
        state.LoopCount++;
        state.Loops.Reset();
        if (state.Active) StopMotion(session, player, state, now);
    }

    private static void StopMotion(WorldSession session, Player player, MotionState state, uint now)
    {
        if (state.Route is not { } route || player.Map is not { } map)
        {
            state.Clear();
            return;
        }

        Vector3 position = Walk(route, state.Anchor, route.NextPoint, state.Speed * Age(now, state.AnchorMs) / 1000f,
            state.Heading, out int nextPoint, out float heading, out _, out _);
        if (Validate(map, route, route.NextPoint, nextPoint, state.Anchor, position) is { } valid) StopAt(session, player, state, valid, heading, nextPoint, now);
        else StopAt(session, player, state, state.Anchor, state.Heading, route.NextPoint, now);
    }

    private static void StopAt(WorldSession session, Player player, MotionState state, Vector3 position, float heading,
        int nextPoint, uint now)
    {
        PlayerbotRoute? route = state.Route;
        if (player.Map is { } stopMap) position = SwimHeight(stopMap, player, position); // the stop agrees with the heartbeats before it
        state.Clear();
        if (Send(session, player, WorldOpcode.MsgMoveStop, position, heading, state.Walk, moving: false, now, budgeted: false)
            && route is not null)
            route.NextPoint = nextPoint;
    }

    /// <summary>
    /// One movement packet through the ordinary handler, or applied by the server directly when
    /// <see cref="PlayerbotOptions.MovementPackets"/> is off (read at every packet, so a reload takes effect at once); true when the
    /// server stored it.
    /// </summary>
    private static bool Send(WorldSession session, Player player, WorldOpcode opcode, Vector3 position, float heading,
        bool walk, bool moving, uint now, bool budgeted, MovementFlags extra = MovementFlags.None, uint fallTime = 0, Seat? seat = null,
        bool? swim = null)
    {
        MovementInfo movement = player.Movement;
        movement.Flags &= ~(MovementFlags.MaskMoving | MovementFlags.SplineEnabled | MovementFlags.WalkMode
            | MovementFlags.TurnLeft | MovementFlags.TurnRight);
        if (moving) movement.Flags |= MovementFlags.Forward;
        if (walk) movement.Flags |= MovementFlags.WalkMode;
        movement.Flags |= extra;
        // MOVEFLAG_SWIMMING follows the position of every packet (MaskMoving does not include it, so a stale one would survive onto land).
        // A caller that already decided the swim state of the packet passes it (swim); the others get the rule at the packet's position.
        // A falling, jumping or aboard packet never carries it.
        movement.Flags &= ~MovementFlags.Swimming;
        if (seat is not { Aboard: true } && (movement.Flags & (MovementFlags.FallingFar | MovementFlags.Jumping)) == 0
            && player.Map is { } swimMap && (swim ?? SwimAt(swimMap, player, position)))
            movement.Flags |= MovementFlags.Swimming;
        if (seat is { } place)
        {
            if (place.Aboard) movement.Flags |= MovementFlags.OnTransport;
            else movement.Flags &= ~MovementFlags.OnTransport;
            movement.TransportGuid = place.Guid;
            movement.TransportX = place.Offset.X;
            movement.TransportY = place.Offset.Y;
            movement.TransportZ = place.Offset.Z;
            movement.TransportOrientation = place.Orientation;
        }
        else
        {
            movement.Flags &= ~MovementFlags.OnTransport;
            movement.TransportGuid = 0;
        }
        movement.X = position.X;
        movement.Y = position.Y;
        movement.Z = position.Z;
        movement.Orientation = Normalize(heading);
        movement.Time = now;
        movement.FallTime = fallTime;
        movement.CorrectData();
        MotionState state = Of(player);
        bool packets = (state.Options ??= session.Services.GetService<IOptions<PlayerbotOptions>>()?.Value)?.MovementPackets ?? true;
        ManagedActionBudget? budget = session.ManagedBudget;
        if (!budgeted) session.ManagedBudget = null;
        bool sent;
        try
        {
            if (packets)
            {
                var writer = new PacketWriter(64);
                movement.Write(writer);
                sent = session.TryManagedAction(opcode, writer.ToArray());
            }
            else
            {
                sent = session.TryManagedMovement(opcode, movement);
            }
        }
        finally { if (!budgeted) session.ManagedBudget = budget; }
        if (!sent) return false;
        state.Packets++;
        return Vector3.Distance(new(player.X, player.Y, player.Z), position) <= 0.5f
            && ((player.Movement.Flags & MovementFlags.Forward) != 0) == moving;
    }

    /// <summary>How far a mesh route's straight leg may run from the floor under it before the motion keeps the mesh's own height.</summary>
    internal const float MeshFloorToleranceYards = 4f;

    /// <summary>Extra raise above a mesh corner's Z used to probe stair lips and steep ramps.</summary>
    internal const float MeshFloorProbeExtraYards = 4f;

    /// <summary>
    /// How far (flat) the start of a walk may lie from the route's leg before the walk is no longer that leg (<see cref="OnLeg"/>). A
    /// walk's positions lie on its legs; a route planned from the bot starts where the mesh query placed it, within a few yards.
    /// </summary>
    internal const float OffLegYards = 5f;

    /// <summary>
    /// Whether <paramref name="from"/> lies within <see cref="OffLegYards"/> (flat) of the route's leg ending at point
    /// <paramref name="toPoint"/> (point 1 for 0, as <see cref="Walk"/> walks it), so that a walk from it toward that point walks the
    /// leg itself and its proof (<see cref="PlayerbotRoute.LegsNavigated"/>) holds.
    /// </summary>
    private static bool OnLeg(PlayerbotRoute route, int toPoint, Vector3 from)
    {
        if (route.Points.Count < 2) return true;
        int end = Math.Clamp(toPoint, 1, route.Points.Count - 1);
        Vector2 start = Flat(route.Points[end - 1]);
        Vector2 leg = Flat(route.Points[end]) - start;
        Vector2 here = Flat(from);
        float lengthSquared = leg.LengthSquared();
        float along = lengthSquared <= 1e-6f ? 0f : Math.Clamp(Vector2.Dot(here - start, leg) / lengthSquared, 0f, 1f);
        return Vector2.Distance(here, start + (leg * along)) <= OffLegYards;
    }

    /// <summary>
    /// Where the bot stands at <paramref name="candidate"/>, or null when it cannot stand there.
    /// <list type="bullet">
    /// <item><description>A route without a navigation-mesh proof (<see cref="PlayerbotRoute.Navigated"/>, the terrain stepper): a floor
    /// within 2 yards, probed from 2 yards above, and a clear line of sight from <paramref name="from"/>.</description></item>
    /// <item><description>A mesh route: refused for its height only when the step changes storey (more than
    /// <see cref="PlayerbotMoveCheck.DirectStepMaxStepUpYards"/> up or down) and neither the mesh's proof (<see cref="PlayerbotMotionAnomalies.StaysOnMesh"/>)
    /// nor any chest-height sight line (to the draped, the probed or the raw height) clears it; keeping the old height needs a
    /// floor within one step height under it. Never refused for a level step's line of sight. Its legs are straight lines between the
    /// mesh's corners; the floor under a leg (stairs, the lip of a ramp) can lie a few yards off, and a probe from 2 yards above
    /// then finds the storey below or nothing. Two positions on either side of a corner see each other through the obstacle the
    /// corner goes around. Both refused every start out of the Deathknell crypt and, in the Goldshire inn, every start towards
    /// William Pestle (a pillar on the line), and those bots stood still for good. The leg's height is first moved onto the mesh's
    /// detail surface under the walk, followed along the mesh from the last position or the leg's corner (<see cref="MeshSurface"/>),
    /// when the mesh has one there. The bot then stands on the floor nearest to the
    /// leg's height among the probes from 2 and 6 yards above within <see cref="MeshFloorToleranceYards"/>, else at the leg's
    /// own height.</description></item>
    /// <item><description>The mesh's proof covers its own legs only: a walk from a <paramref name="from"/> more than
    /// <see cref="OffLegYards"/> off the leg ending at <paramref name="fromPoint"/> (<see cref="OnLeg"/>) is judged as a route without
    /// one.</description></item>
    /// </list>
    /// </summary>
    /// <param name="fromPoint">The point the walk was heading for from <paramref name="from"/> (its leg ends there).</param>
    /// <param name="toPoint">The point the walk heads for at <paramref name="candidate"/>: every leg in between is checked by its own proof.</param>
    private static Vector3? Validate(Map map, PlayerbotRoute route, int fromPoint, int toPoint, Vector3 from, Vector3 candidate)
        => Validate(map, route, fromPoint, toPoint, from, candidate, out _);

    private static Vector3? Validate(Map map, PlayerbotRoute route, int fromPoint, int toPoint, Vector3 from, Vector3 candidate,
        out string? rejectReason)
    {
        rejectReason = null;
        if (!candidate.IsFinite())
        {
            rejectReason = "NonFiniteCoordinate";
            return null;
        }

        // A bot taken off its route (a chase, an escape, a fear, a same-map teleport) and handed the route again walks a straight line
        // from where it stands to the next point: no leg of the mesh, and with the mesh's proof it went through hills, rocks and city
        // walls at its own height (live: bot-motion/under-map 10-36 yd under the Valley of Trials mesa, 92-94% of the under-map
        // samples on routed bots more than 10 yd off their leg).
        bool navigated = route.LegsNavigated(fromPoint, toPoint) && OnLeg(route, fromPoint, from);
        Vector3 rawCandidate = candidate;
        bool hasMeshSurface = false;
        if (navigated && MeshSurface(map.Collision, route, fromPoint, toPoint, from, candidate) is { } surface)
        {
            // The leg is a straight line between string-pulled corners, so its own height is a chord: through the hill, over the hollow,
            // under the ramp. The mesh's surface under the walk is where the floor is (live: bots 8-30 yd under the terrain and 6-10 yd
            // above it in the open, under the Ironforge ramps, 2-5 yd under the Loch Modan tunnel floor).
            candidate = candidate with { Z = surface };
            hasMeshSurface = true;
        }

        float maxCeilingZ = MathF.Max(from.Z, candidate.Z) + 1.25f;
        if (navigated)
        {
            float best = float.NaN;
            foreach (float raise in MeshFloorProbes)
            {
                float floor = ResolveWalkFloor(map, candidate.X, candidate.Y, candidate.Z, raise, maxCeilingZ);
                if (!ValidHeight(floor) || MathF.Abs(floor - candidate.Z) > MeshFloorToleranceYards) continue;
                if (float.IsNaN(best) || MathF.Abs(floor - candidate.Z) < MathF.Abs(best - candidate.Z)) best = floor;
                // Only fall back to the +4 yd stair-lip probe when the standard probe found no floor near the leg.
                if (MathF.Abs(best - candidate.Z) <= 1.25f) break;
            }

            if (float.IsNaN(best) && !hasMeshSurface)
            {
                float directFloor = ResolveWalkFloor(map, candidate.X, candidate.Y, candidate.Z, 0f, maxCeilingZ);
                if (ValidHeight(directFloor))
                {
                    if (candidate.Z - directFloor > DirectStepDrapeMinYards
                        && candidate.Z - directFloor <= PlayerbotMoveCheck.DirectStepMaxDrapeYards)
                    {
                        best = directFloor;
                    }
                    else if (map.Collision.MeshPoints is not null
                        && route.MeshPoints is not null
                        && candidate.Z - directFloor > PlayerbotMotionAnomalies.AirwalkYards)
                    {
                        rejectReason = "NoGroundSupport";
                        return null;
                    }
                }
            }

            Vector3 resolved = float.IsNaN(best) ? candidate : candidate with { Z = best + 0.05f };
            if (map.Collision.LineOfSight is { Enabled: true } los
                && (resolved.Z != rawCandidate.Z || MathF.Abs(resolved.Z - from.Z) > PlayerbotMoveCheck.DirectStepMaxStepUpYards))
            {
                Vector3 fromChest = new(from.X, from.Y, from.Z + PlayerbotMoveCheck.ChestYards);
                Vector3 resolvedChest = resolved with { Z = resolved.Z + PlayerbotMoveCheck.ChestYards };
                if (!los.IsInLineOfSight(map.MapId, fromChest, resolvedChest, ignoreM2: true)
                    && !PlayerbotMotionAnomalies.StaysOnMesh(map, from, resolved))
                {
                    if (candidate.Z != resolved.Z
                        && los.IsInLineOfSight(map.MapId, fromChest, candidate with { Z = candidate.Z + PlayerbotMoveCheck.ChestYards }, ignoreM2: true))
                    {
                        resolved = candidate;
                    }
                    else if (rawCandidate.Z != resolved.Z
                        && los.IsInLineOfSight(map.MapId, fromChest, rawCandidate with { Z = rawCandidate.Z + PlayerbotMoveCheck.ChestYards }, ignoreM2: true))
                    {
                        resolved = rawCandidate;
                    }
                    else if (MathF.Abs(resolved.Z - from.Z) > PlayerbotMoveCheck.DirectStepMaxStepUpYards
                        && los.IsInLineOfSight(map.MapId, fromChest, rawCandidate with { Z = from.Z + PlayerbotMoveCheck.ChestYards }, ignoreM2: true)
                        && HasFloorBelow(map, rawCandidate.X, rawCandidate.Y, from.Z))
                    {
                        // Keeping the old height is only safe with a floor within reach under it (live: a dock edge over a seabed 20 yd down).
                        resolved = rawCandidate with { Z = from.Z };
                    }
                    else if (MathF.Abs(resolved.Z - from.Z) > PlayerbotMoveCheck.DirectStepMaxStepUpYards)
                    {
                        // No sight line clears the storey change: returning `resolved` would put the bot through a floor or slab.
                        rejectReason = "NoGroundSupport";
                        return null;
                    }
                }
            }

            if (map.Collision.ClassifyUnderMap(resolved.X, resolved.Y, resolved.Z, PlayerbotMotionAnomalies.UnderMapYards).Kind == UnderMapKind.UnderMap
                && map.Collision.ClassifyUnderMap(from.X, from.Y, from.Z, PlayerbotMotionAnomalies.UnderMapYards).Kind != UnderMapKind.UnderMap)
            {
                rejectReason = "NoGroundSupport";
                return null;
            }

            return resolved;
        }

        // Non-navigated (DirectStep / stepped / trail) leg: validate and drape onto the walkable floor via PlayerbotMoveCheck.
        if (!PlayerbotMoveCheck.TryValidateStep(map, from, candidate, out Vector3 draped, out rejectReason))
            return null;
        return draped;
    }

    /// <summary>True when walkable floor lies at or just under <paramref name="z"/> at x/y, within one step height (a client falls, it does not hover).</summary>
    private static bool HasFloorBelow(Map map, float x, float y, float z)
    {
        float floor = ResolveWalkFloor(map, x, y, z, 0f, z + 1.25f);
        return ValidHeight(floor) && z - floor <= PlayerbotMoveCheck.DirectStepMaxStepUpYards;
    }

    private static string MoveModeOf(PlayerbotRoute route, int fromPoint, int toPoint)
        => route.LegsNavigated(fromPoint, toPoint) ? "NavMesh" : "DirectStep";

    private static void TrySnapDirectStepStart(Map map, Player player, ref Vector3 current)
    {
        float floor = ResolveWalkFloor(map, current.X, current.Y, current.Z, 0f, current.Z + 2.25f);
        if (!ValidHeight(floor))
            floor = ResolveWalkFloor(map, current.X, current.Y, current.Z, 2f, current.Z + 2.25f);
        if (!ValidHeight(floor)) return;
        float delta = current.Z - floor;
        if ((delta > DirectStepDrapeMinYards && delta <= PlayerbotMoveCheck.DirectStepMaxDrapeYards)
            || (delta < -0.10f && delta >= -PlayerbotMoveCheck.DirectStepMaxStepUpYards))
        {
            current = current with { Z = floor + 0.05f };
            player.SetPosition(current.X, current.Y, current.Z, player.Orientation);
        }
    }

    private static void TrySnapPlayerToFloor(Map map, Player player, ref Vector3 current)
    {
        float floor = ResolveWalkFloor(map, current.X, current.Y, current.Z, 0f, current.Z + 2.25f);
        if (!ValidHeight(floor) || MathF.Abs(floor - current.Z) > PlayerbotMoveCheck.DirectStepMaxStepUpYards)
        {
            float raised = ResolveWalkFloor(map, current.X, current.Y, current.Z, 2f, current.Z + 2.25f);
            if (ValidHeight(raised)) floor = raised;
        }

        if (!ValidHeight(floor)) return;
        float delta = current.Z - floor;
        if (delta < -PlayerbotMoveCheck.DirectStepMaxStepUpYards || delta > PlayerbotMoveCheck.DirectStepMaxDrapeYards) return;
        current = current with { Z = floor + 0.05f };
        player.SetPosition(current.X, current.Y, current.Z, player.Orientation);
    }

    private static bool TrySkipBlockedMicroCorner(Map map, Player player, PlayerbotRoute route, ref Vector3 current, float probeStep,
        out Vector3 probe, out int probed, out Vector3? validStart, out string? rejectReason)
    {
        probe = default;
        probed = route.NextPoint;
        validStart = null;
        rejectReason = null;
        TrySnapPlayerToFloor(map, player, ref current);
        if (route.NextPoint < route.Points.Count - 1
            && Vector2.Distance(Flat(current), Flat(route.Points[route.NextPoint])) <= 2.5f)
        {
            route.NextPoint++;
        }

        probe = Walk(route, current, route.NextPoint, probeStep,
            HeadingTo(current, route.Points[route.NextPoint]), out probed, out _, out _, out _);
        if (Vector2.Distance(Flat(current), Flat(probe)) <= 0.05f) return false;
        validStart = Validate(map, route, route.NextPoint, probed, current, probe, out rejectReason);
        return validStart is not null;
    }

    private static void AdvancePastRejectedWaypoint(PlayerbotRoute route, Vector3 from)
    {
        if (route.NextPoint < route.Points.Count - 1
            && Vector2.Distance(Flat(from), Flat(route.Points[route.NextPoint])) <= SamePlaceYards)
        {
            route.NextPoint++;
        }
    }

    private static int NoteStepRejected(WorldSession session, Player player, MotionState state, PlayerbotRoute route, uint now)
    {
        Vector3 destination = route.Points[^1];
        if (state.ConsecutiveRejections > 0 && Vector2.Distance(Flat(state.LastRejectedDestination), Flat(destination)) <= SamePlaceYards)
        {
            state.ConsecutiveRejections++;
        }
        else
        {
            state.ConsecutiveRejections = 1;
            state.LastRejectedDestination = destination;
        }

        int consecutive = state.ConsecutiveRejections;
        if (consecutive >= MaxRejectionsBeforeLoop)
        {
            Vector3 waypoint = route.Points[Math.Clamp(route.NextPoint, 0, route.Points.Count - 1)];
            GiveUp(session, player, state, [destination, waypoint], now);
        }

        return consecutive;
    }

    /// <summary>
    /// The mesh surface under a navigated walk at <paramref name="candidate"/>: each leg is walked on the mesh from where the
    /// previous one ended (<see cref="MapCollision.TryWalkMeshSurface"/>), so the storey is the one the walk is on and a step of any
    /// length follows the ground's rise and fall. A corner's stored height is the coarse polygon's, a chord that can sit far off the
    /// detail surface, outside the mesh query's vertical window: walking from that height is "no mesh", and the step would keep the
    /// chord (live: 8-30 yd under the terrain and 6-10 yd above it the moment a heartbeat passes such a corner). The straight line
    /// from the last position to <paramref name="candidate"/> is not used once a corner was passed — it cuts through the obstacle the
    /// corner goes around. When no leg can be walked (a route started off the mesh), the surface straight under or over the leg's
    /// height within <see cref="MeshFloorToleranceYards"/>. Null: none.
    /// </summary>
    private static float? MeshSurface(MapCollision collision, PlayerbotRoute route, int fromPoint, int toPoint, Vector3 from, Vector3 candidate)
    {
        PathOptions options = PlayerbotMoveCheck.MeshOptions;
        if (toPoint <= fromPoint)
        {
            if (collision.TryWalkMeshSurface(from, candidate, out float walked, options)) return walked;
            return WalkFromCorner(collision, route, toPoint, candidate, options) ?? NearFloor(collision, candidate);
        }

        Vector3 at = from;
        int first = Math.Max(1, fromPoint);
        int lastCorner = Math.Min(toPoint - 1, route.Points.Count - 1);
        for (int point = first; point <= lastCorner; point++)
        {
            if (!collision.TryWalkMeshSurface(at, route.Points[point], out float surface, options))
                return WalkFromCorner(collision, route, toPoint, candidate, options) ?? NearFloor(collision, candidate);
            at = route.Points[point] with { Z = surface };
        }

        if (collision.TryWalkMeshSurface(at, candidate, out float end, options)) return end;
        return WalkFromCorner(collision, route, toPoint, candidate, options) ?? NearFloor(collision, candidate);
    }

    /// <summary>The surface walked from the corner the leg ending at <paramref name="toPoint"/> starts at, or null when that corner is not on the mesh.</summary>
    private static float? WalkFromCorner(MapCollision collision, PlayerbotRoute route, int toPoint, Vector3 candidate, PathOptions options)
    {
        if (route.Points.Count == 0) return null;
        int corner = Math.Clamp(toPoint - 1, 0, route.Points.Count - 1);
        return collision.TryWalkMeshSurface(route.Points[corner], candidate, out float along, options) ? along : null;
    }

    /// <summary>The mesh surface within <see cref="MeshFloorToleranceYards"/> of the leg's own height, or null.</summary>
    private static float? NearFloor(MapCollision collision, Vector3 candidate)
        => collision.TryGetMeshFloor(candidate.X, candidate.Y, candidate.Z, MeshFloorToleranceYards, MeshFloorToleranceYards, out float near)
            ? near : null;

    /// <summary>
    /// Resolve the walkable floor near <paramref name="z"/> without snapping onto overhead M2/WMO props (tree branches, bushes, fences,
    /// roof eaves) that <see cref="MapCollision.GetHeight"/> prefers whenever <c>modelHeight &gt; mapHeight</c>, or snapping downward
    /// onto an underground cave/mine WMO floor while walking on surface terrain.
    /// </summary>
    internal static float ResolveWalkFloor(Map map, float x, float y, float z, float raise, float maxCeilingZ)
    {
        float terrain = map.Terrain.GetHeight(x, y, z);
        float floor = map.Collision.GetHeight(x, y, z + raise);
        if (ValidHeight(terrain) && ValidHeight(floor))
        {
            if (floor > terrain + 0.4f && MathF.Abs(terrain - z) + 0.25f < MathF.Abs(floor - z))
            {
                floor = terrain;
            }
            else if (floor < terrain - 2.0f && z >= terrain - 1.5f)
            {
                floor = terrain;
            }
        }

        if (ValidHeight(floor) && floor > maxCeilingZ)
        {
            return ValidHeight(terrain) && terrain <= maxCeilingZ ? terrain : TerrainTile.InvalidHeightValue;
        }

        return floor;
    }

    private static readonly float[] MeshFloorProbes = [0f, 4f];

    internal static bool ValidHeight(float height)
        => float.IsFinite(height) && height != TerrainTile.InvalidHeight && height != TerrainTile.InvalidHeightValue;

    /// <summary>A stun, fear or confusion: the bot does not walk its own routes (<see cref="Pump"/> stops it, nothing starts it) until it ends.</summary>
    internal static bool ControlLost(Player player)
        => (player.UnitFlags & (UnitFlags.Stunned | UnitFlags.Confused | UnitFlags.Fleeing)) != 0;

    /// <summary>Alive, or a ghost running back to its corpse; a corpse lying on the ground does not move.</summary>
    internal static bool CanMove(Player player) => player.IsAlive || (player.Flags & PlayerFlags.Ghost) != 0;

    /// <summary>The longest lease a measured think gap can ask for (<see cref="Lease"/>).</summary>
    internal const uint MaxGapLeaseMs = 15_000;

    /// <summary>The longest lease the scheduler's pass can raise the cap to (<see cref="LeaseCapMs"/>).</summary>
    internal const uint HardMaxGapLeaseMs = 120_000;

    /// <summary>
    /// The cap on a lease for a scheduler whose main lane needs <paramref name="passMs"/> for a full pass (0 = not measured):
    /// 1.5 x the pass, at least <see cref="MaxGapLeaseMs"/> and at most <see cref="HardMaxGapLeaseMs"/>. With a thousand bots the pass is
    /// 8 to 30 s, so a fixed 15 s cap sat under the real think gap and stopped bots mid-road (lease-expired-midroute).
    /// </summary>
    internal static uint LeaseCapMs(uint passMs)
        => (uint)Math.Clamp(passMs * 3L / 2, MaxGapLeaseMs, HardMaxGapLeaseMs);

    /// <summary>
    /// How long a route stays walked after the think that last followed it: <c>max(4000, 8 × ThinkIntervalMs, 3 × smoothed think gap)</c>,
    /// the gap term held to <see cref="LeaseCapMs"/> (<see cref="MaxGapLeaseMs"/> or 1.5 x the scheduler's measured full pass, at most
    /// <see cref="HardMaxGapLeaseMs"/>; a gap not yet measured counts as one full pass). Only a think that follows the route again renews it, and with a thousand bots the
    /// rotation (<see cref="PlayerbotThinkRotation"/>) thinks for a bot every few seconds, not every <c>ThinkIntervalMs</c>: a fixed
    /// lease of 2 s stopped every bot well before its next think and it stood until then (live 2026-10-12: walk 5 s, stop, wait, walk),
    /// and <c>max(2000, 4 × ThinkIntervalMs, 2 × gap)</c> still ran out mid-route (lease-expired-midroute, raised 2026-10-17).
    /// A route the brain gives up is stopped by the brain (see <see cref="PlayerbotBrain.TakeRouteAbandoned"/>), not left to the lease.
    /// </summary>
    private static uint Lease(PlayerbotOptions options, MotionState state)
    {
        uint floor = (uint)Math.Max(4000, options.ThinkIntervalMs * 8);
        // A bot with no measured gap yet (a fresh login or re-add: its first think skips NoteThinkGap) is taken to wait a full pass.
        long gapMs = state.GapMs != 0 ? state.GapMs : state.PassMs;
        uint gap = (uint)Math.Min(gapMs * 3, LeaseCapMs(state.PassMs));
        return Math.Max(floor, gap);
    }

    /// <summary>
    /// A think of this bot ran <paramref name="sinceLast"/> ms after its previous one: fold it into the smoothed gap (an exponentially
    /// weighted average, weight 1/4). A gap of 0 (two thinks in one clock reading) is no measure; the first measured gap is taken as is.
    /// A think of the combat lane (<paramref name="combatLane"/>: fighting, dead or a ghost, thought for every CombatThinkIntervalMs) only
    /// raises the average: its short gaps said nothing of the rotation the bot returns to after the fight, and the lease they left (2 s)
    /// stopped the bot's next walk long before its next think (live 2026-10-17: about a third of bot-motion/lease-expired-midroute lines
    /// had a think gap under 1 s while the main lane's p95 gap was 4 to 8 s).
    /// </summary>
    internal static void NoteThinkGap(Player player, uint sinceLast, bool combatLane = false)
    {
        if (sinceLast == 0 || sinceLast > int.MaxValue) return;
        MotionState state = Of(player);
        uint sample = Math.Min(sinceLast, HardMaxGapLeaseMs);
        if (combatLane && state.GapMs != 0 && sample <= state.GapMs) return;
        state.GapMs = state.GapMs == 0 ? sample : (uint)((state.GapMs * 3L + sample + 2) / 4);
        if (state.Active && state.Options is { } opts)
        {
            state.LeaseMs = Math.Max(state.LeaseMs, Math.Max(Lease(opts, state), Math.Min(sample * 3u, LeaseCapMs(state.PassMs))));
        }
    }

    /// <summary>
    /// After a think: when it gave up its route or changed its goal (<paramref name="gaveUp"/>) and neither kept nor started a route
    /// (<paramref name="renewalsBefore"/> is <see cref="RenewalsOf"/> before the think), the bot is stopped: the lease would let it walk
    /// the stale route for seconds. A tile wait is judged per route, not here: a think that keeps the route it has while a plan waits
    /// renews it (the chase), and an unrelated wait in the same think does not keep a route the brain gave up. True when it stopped the bot.
    /// A think with <paramref name="sinceLast"/> 0 (a second think in one clock reading, which the rotation never makes in play) decided
    /// nothing a gap could stale, and its caller is a test that steps by hand: it is skipped.
    /// </summary>
    internal static bool SettleAfterThink(WorldSession session, Player player, bool gaveUp, uint renewalsBefore, uint sinceLast)
    {
        if (sinceLast == 0 || !States.TryGetValue(player, out MotionState? state) || !state.Active) return false;
        if (state.Renewals != renewalsBefore) return false;
        if (!gaveUp)
        {
            state.RenewedMs = session.World.NowMs;
            if (state.Options is { } opts)
            {
                state.LeaseMs = Math.Max(state.LeaseMs, Lease(opts, state));
            }

            return false;
        }

        Stop(session, player);
        return true;
    }

    /// <summary>Records that the bot thought now under <paramref name="driver"/> (a static string), for the lease-expiry anomaly's detail.</summary>
    internal static void NoteThink(Player player, string driver, uint now)
    {
        MotionState state = Of(player);
        state.LastThinkMs = now;
        state.LastThinkDriver = driver;
        state.Thought = true;
    }

    /// <summary>The scheduler's measured main-lane full-pass time (ms; 0 = none yet) as this bot's motion should see it.</summary>
    internal static void NoteSchedulerPass(Player player, uint passMs)
    {
        // 0 is "not measured" (a fresh feature after a reload): it never wipes a pass the bot's motion already knows.
        if (passMs != 0) Of(player).PassMs = passMs;
    }

    /// <summary>The scheduler pass the bot's motion last saw (<see cref="NoteSchedulerPass"/>), or 0 when none.</summary>
    internal static uint SchedulerPassMsOf(Player player) => States.TryGetValue(player, out MotionState? state) ? state.PassMs : 0;

    /// <summary>The bot's smoothed think gap in ms (<see cref="NoteThinkGap"/>), or 0 when none was measured.</summary>
    internal static uint ThinkGapMs(Player player) => States.TryGetValue(player, out MotionState? state) ? state.GapMs : 0;

    /// <summary>The lease the bot's route has now, in ms (0 when it follows none).</summary>
    internal static uint LeaseMsOf(Player player) => States.TryGetValue(player, out MotionState? state) && state.Active ? state.LeaseMs : 0;

    /// <summary>How many times a think started or kept the bot's route; compare before and after a think to learn whether it did.</summary>
    internal static uint RenewalsOf(Player player) => States.TryGetValue(player, out MotionState? state) ? state.Renewals : 0;

    /// <summary>
    /// How long the bot needs to walk the rest of <paramref name="route"/> (the one it is moving along now), from its position now.
    /// False when it does not move along that route.
    /// </summary>
    internal static bool TryTimeToEnd(Player player, PlayerbotRoute route, out uint remainingMs)
    {
        remainingMs = 0;
        if (!TryYardsToEnd(player, route, out float yards) || !States.TryGetValue(player, out MotionState? state)) return false;
        remainingMs = (uint)Math.Min(uint.MaxValue / 2f, yards / state.Speed * 1000f);
        return true;
    }

    /// <summary>How many flat yards of <paramref name="route"/> the bot has left to walk, from its position now. False when it does not move along that route.</summary>
    internal static bool TryYardsToEnd(Player player, PlayerbotRoute route, out float yards)
    {
        yards = 0;
        if (!States.TryGetValue(player, out MotionState? state) || !ReferenceEquals(state.Route, route) || state.World is not { } world
            || !ReferenceEquals(player.Map, state.Map) || !(state.Speed > 0)) return false;
        Vector3 position = Walk(route, state.Anchor, route.NextPoint, state.Speed * Age(world.NowMs, state.AnchorMs) / 1000f, state.Heading,
            out int nextPoint, out _, out bool arrived, out _);
        if (!arrived)
        {
            Vector3 at = position;
            for (int point = Math.Max(1, nextPoint); point < route.Points.Count; point++)
            {
                yards += Vector2.Distance(Flat(at), Flat(route.Points[point]));
                at = route.Points[point];
            }
        }

        return float.IsFinite(yards);
    }

    private static uint Age(uint now, uint then)
    {
        uint age = unchecked(now - then);
        return age <= int.MaxValue ? age : 0;
    }

    private static float HeadingTo(Vector3 from, Vector3 to)
    {
        Vector2 direction = Flat(to) - Flat(from);
        return direction.LengthSquared() > 1e-6f ? Normalize(MathF.Atan2(direction.Y, direction.X)) : 0f;
    }

    /// <summary>An orientation in [0, 2π), as a client sends it.</summary>
    internal static float Normalize(float radians)
    {
        if (!float.IsFinite(radians)) return 0f;
        float value = radians % MathF.Tau;
        if (value < 0) value += MathF.Tau;
        return value >= MathF.Tau ? 0f : value;
    }

    private static float AngleBetween(float a, float b)
    {
        float difference = MathF.Abs(Normalize(a) - Normalize(b));
        return difference > MathF.PI ? MathF.Tau - difference : difference;
    }

}

/// <summary>
/// Notices a bot that is not getting anywhere: its goals keep sending it back to places it just left (A, B, A, B...),
/// or it keeps running inside a small area. Either returns the places to give up.
/// </summary>
internal sealed class PlayerbotLoopDetector
{
    /// <summary>Route destinations remembered for the back-and-forth check.</summary>
    internal const uint DestinationWindowMs = 30_000;

    /// <summary>Returns to a recently left destination that count as a loop.</summary>
    internal const int ReturnsForLoop = 3;

    /// <summary>The running-in-place check: this window, this much running, and never farther than the radius.</summary>
    internal const uint AreaWindowMs = 15_000;
    internal const float AreaMinTravelYards = 50f;
    internal const float AreaRadiusYards = 14f;

    private readonly List<(uint Ms, Vector3 Destination)> _destinations = [];
    private readonly List<(uint Ms, Vector3 Position, float Travelled)> _samples = [];
    private float _travelled;

    internal void Reset()
    {
        _destinations.Clear();
        _samples.Clear();
        _travelled = 0;
    }

    /// <summary>A route to <paramref name="destination"/> started; the loop's places when this completes a loop.</summary>
    internal IReadOnlyList<Vector3>? OnRoute(Vector3 destination, uint now)
    {
        _destinations.RemoveAll(entry => unchecked(now - entry.Ms) > DestinationWindowMs);
        if (_destinations.Count > 0 && Same(_destinations[^1].Destination, destination))
        {
            _destinations[^1] = (now, destination); // the same goal re-planned (a moving target, a retried path)
            return null;
        }

        _destinations.Add((now, destination));
        // A return: a destination the bot already left for somewhere else within the window (A, B, A).
        int returns = 0;
        for (int i = 2; i < _destinations.Count; i++)
            for (int j = 0; j < i - 1; j++)
                if (Same(_destinations[j].Destination, _destinations[i].Destination)) { returns++; break; }
        if (returns < ReturnsForLoop) return null;
        var places = new List<Vector3>();
        foreach ((_, Vector3 place) in _destinations)
            if (!places.Any(known => Same(known, place))) places.Add(place);
        return places;
    }

    /// <summary>The bot reported <paramref name="position"/> after running <paramref name="travelled"/> yards.</summary>
    internal IReadOnlyList<Vector3>? OnPosition(Vector3 position, float travelled, uint now)
    {
        _travelled += float.IsFinite(travelled) ? travelled : 0;
        _samples.Add((now, position, _travelled));
        _samples.RemoveAll(sample => unchecked(now - sample.Ms) > AreaWindowMs);
        if (_samples.Count < 2) return null;
        (uint firstMs, Vector3 first, float firstTravelled) = _samples[0];
        if (unchecked(now - firstMs) < AreaWindowMs * 4 / 5 || _travelled - firstTravelled < AreaMinTravelYards) return null;
        foreach ((_, Vector3 sample, _) in _samples)
            if (Vector2.Distance(new(sample.X, sample.Y), new(first.X, first.Y)) > AreaRadiusYards) return null;
        return [_destinations.Count > 0 ? _destinations[^1].Destination : position];
    }

    private static bool Same(Vector3 a, Vector3 b)
        => Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y)) <= PlayerbotMotion.SamePlaceYards;
}
