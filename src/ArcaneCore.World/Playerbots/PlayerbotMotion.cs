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

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The movement "client" of a server-managed player: it moves the bot along its route the way a 1.12 client moves its
/// own character, and reports it with the packets a real client sends — MSG_MOVE_START_FORWARD, a heartbeat about
/// every 500 ms (and when the route turns), MSG_MOVE_STOP — through the ordinary movement handler, which relays them to
/// every observer.
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
internal static class PlayerbotMotion
{
    /// <summary>A real client heartbeats about every 500 ms while moving.</summary>
    internal const uint HeartbeatIntervalMs = 500;

    /// <summary>A route turn sharper than this (about 10 degrees) is reported at once with the new orientation.</summary>
    internal const float TurnRadians = 0.17f;

    /// <summary>How long a destination involved in a loop is refused.</summary>
    internal const uint LoopBlacklistMs = 60_000;

    /// <summary>Destinations closer than this are the same place for loop detection and the blacklist.</summary>
    internal const float SamePlaceYards = 5f;

    private static readonly ConditionalWeakTable<Player, MotionState> States = [];

    internal sealed class MotionState
    {
        internal WorldRuntime? World;
        internal PlayerbotRoute? Route;
        internal Map? Map;
        internal Vector3 Anchor;
        internal uint AnchorMs;
        internal float Heading;
        internal float Speed;
        internal bool Walk;
        internal float MoveSpeedCap;
        internal uint LeaseMs;
        internal uint RenewedMs;
        internal readonly PlayerbotLoopDetector Loops = new();
        internal readonly List<(Vector3 Destination, uint UntilMs)> Blacklist = [];
        internal bool LoopPending;
        internal int LoopCount;
        internal uint Packets;

        internal bool Active => Route is not null;

        internal void Clear()
        {
            Route = null;
            Map = null;
        }
    }

    internal static MotionState Of(Player player) => States.GetOrCreateValue(player);

    /// <summary>Whether the bot is moving along a route this class drives.</summary>
    internal static bool IsActive(Player player) => States.TryGetValue(player, out MotionState? state) && state.Active;

    /// <summary>Loops detected so far (inspection).</summary>
    internal static int LoopCount(Player player) => States.TryGetValue(player, out MotionState? state) ? state.LoopCount : 0;

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

    /// <summary>Whether <paramref name="destination"/> was given up after a loop and is still refused.</summary>
    internal static bool IsBlacklisted(Player player, Vector3 destination)
    {
        if (!States.TryGetValue(player, out MotionState? state) || state.World is not { } world || state.Blacklist.Count == 0)
            return false;
        uint now = world.NowMs;
        state.Blacklist.RemoveAll(entry => unchecked((int)(entry.UntilMs - now)) <= 0);
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
        return Finite(position) ? position : stored;
    }

    /// <summary>
    /// Follow <paramref name="route"/> (the brain and its goals call this every think). Starts moving with
    /// MSG_MOVE_START_FORWARD, switches to a different route with a heartbeat carrying the new orientation, and renews
    /// the lease on the current route. False when the bot cannot move along it (the caller drops the route).
    /// </summary>
    internal static bool Follow(WorldSession session, Player player, PlayerbotRoute route, PlayerbotOptions options, uint now)
    {
        MotionState state = Of(player);
        state.World = session.World;
        if (player.Map is not { } map) return false;
        if (route.Complete || route.Points.Count < 2) return false;
        if (IsBlacklisted(player, route.Points[^1]))
        {
            Stop(session, player);
            return false;
        }

        if (state.Active && ReferenceEquals(state.Route, route))
        {
            state.RenewedMs = now;
            state.LeaseMs = Lease(options);
            Pump(session, player, now);
            return state.Active || route.Complete;
        }

        if ((player.Movement.Flags & (MovementFlags.Root | MovementFlags.Jumping | MovementFlags.FallingFar)) != 0)
        {
            Stop(session, player);
            return false;
        }

        if (!Speed(player, options.MoveSpeed, out float speed, out bool walk))
        {
            Stop(session, player);
            return false;
        }

        // Switching routes: catch the old motion up to now first, so the new one starts where observers see the bot.
        bool moving = (player.Movement.Flags & MovementFlags.MaskMoving) != 0;
        Vector3 current = new(player.X, player.Y, player.Z);
        if (state.Active && ReferenceEquals(state.Map, map) && (player.Movement.Flags & MovementFlags.Forward) != 0)
        {
            // One heartbeat at the current position with the new orientation replaces the old route's next packet.
            Vector3 position = Walk(state.Route!, state.Anchor, state.Route!.NextPoint,
                state.Speed * Age(now, state.AnchorMs) / 1000f, state.Heading, out _, out _, out _, out _);
            current = Validate(map, state.Anchor, position) ?? state.Anchor;
        }

        while (route.NextPoint < route.Points.Count - 1 && Vector2.Distance(Flat(current), Flat(route.Points[route.NextPoint])) <= 0.05f)
            route.NextPoint++;
        // The first stretch must be walkable before anything is announced.
        Vector3 probe = Walk(route, current, route.NextPoint, MathF.Max(0.1f, MathF.Min(speed * 0.5f, 2f)),
            HeadingTo(current, route.Points[route.NextPoint]), out _, out _, out _, out _);
        if (Vector2.Distance(Flat(current), Flat(probe)) <= 0.05f || Validate(map, current, probe) is null)
        {
            Stop(session, player);
            return false;
        }

        float heading = HeadingTo(current, route.Points[route.NextPoint]);
        // A deliberate start spends the caller's action budget; a running bot changing its route does not.
        WorldOpcode opcode = moving ? WorldOpcode.MsgMoveHeartbeat : WorldOpcode.MsgMoveStartForward;
        if (!Send(session, player, opcode, current, heading, walk, moving: true, now, budgeted: !moving))
        {
            if (!moving) state.Clear();
            return false;
        }

        state.Route = route;
        state.Map = map;
        state.Anchor = current;
        state.AnchorMs = now;
        state.Heading = heading;
        state.Speed = speed;
        state.Walk = walk;
        state.MoveSpeedCap = options.MoveSpeed;
        state.RenewedMs = now;
        state.LeaseMs = Lease(options);
        PlayerbotAreaTriggers.Begin(session, player, map, current);
        if (state.Loops.OnRoute(route.Points[^1], now) is { } loop)
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
        // Between maps the bot's brain does not run; the client half still ends its loading screen (PlayerbotMovementControl).
        if (PlayerbotMovementControl.AcknowledgeTransfer(session, player)) return;
        if (!States.TryGetValue(player, out MotionState? state) || state.Route is not { } route) return;
        state.World = session.World;
        if (!CanMove(player) || !player.IsInWorld || player.Map is not { } map || !ReferenceEquals(map, state.Map)
            || session.Services.GetService<TeleportFeature>()?.Teleports.IsBeingTeleported(player) == true
            || (player.Movement.Flags & MovementFlags.Forward) == 0)
        {
            // Something else already moved or stopped the bot (death, teleport, a root acknowledgement).
            state.Clear();
            return;
        }

        uint age = Age(now, state.AnchorMs);
        if (age == 0) return;
        if ((player.Movement.Flags & (MovementFlags.Root | MovementFlags.Jumping | MovementFlags.FallingFar)) != 0
            || player.StandState != StandState.Stand || Age(now, state.RenewedMs) > state.LeaseMs)
        {
            StopMotion(session, player, state, now);
            return;
        }

        Vector3 position = Walk(route, state.Anchor, route.NextPoint, state.Speed * age / 1000f, state.Heading,
            out int nextPoint, out float heading, out bool arrived, out bool turned);
        // A client checks its area triggers every frame; this checks every world tick. Stepping into a volume is reported at
        // once: a heartbeat where the bot is now, then CMSG_AREATRIGGER (PlayerbotAreaTriggers).
        bool entering = PlayerbotAreaTriggers.WouldEnter(session, player, map, position);
        if (arrived)
        {
            StopAt(session, player, state, position, heading, nextPoint, now);
            if (ReferenceEquals(player.Map, map)) PlayerbotAreaTriggers.Update(session, player, map, position);
            return;
        }

        if (!turned && !entering && age < HeartbeatIntervalMs) return;
        if (Validate(map, state.Anchor, position) is not { } valid)
        {
            // The route is no longer walkable here: stop where the last packet put the bot.
            StopAt(session, player, state, state.Anchor, state.Heading, route.NextPoint, now);
            return;
        }

        if (!Send(session, player, WorldOpcode.MsgMoveHeartbeat, valid, heading, state.Walk, moving: true, now, budgeted: false))
        {
            state.Clear();
            return;
        }

        route.NextPoint = nextPoint;
        float travelled = Vector2.Distance(Flat(state.Anchor), Flat(valid));
        state.Anchor = valid;
        state.AnchorMs = now;
        state.Heading = heading;
        if (state.Loops.OnPosition(valid, travelled, now) is { } loop) GiveUp(session, player, state, loop, now);
        // Last: a trigger may teleport the bot (the next pump then finds it between maps and forgets the route).
        PlayerbotAreaTriggers.Update(session, player, map, position);
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
        if (Validate(map, state.Anchor, position) is not { } valid
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
    }

    /// <summary>After an acknowledged speed change the motion continues at the new speed.</summary>
    internal static void RefreshSpeed(Player player)
    {
        if (States.TryGetValue(player, out MotionState? state) && state.Active
            && Speed(player, state.MoveSpeedCap, out float speed, out bool walk) && walk == state.Walk)
            state.Speed = speed;
    }

    /// <summary>
    /// The speed observers use for this bot: its run speed, or — when the configured cap is below it — walk mode at
    /// its walk speed. A client cannot be told any other speed, so moving at another one makes observers drift.
    /// </summary>
    internal static bool Speed(Player player, float cap, out float speed, out bool walk)
    {
        float run = UnitSpeed.Get(player, MoveType.Run);
        walk = float.IsFinite(cap) && cap < run - 0.001f;
        speed = walk ? UnitSpeed.Get(player, MoveType.Walk) : run;
        return float.IsFinite(speed) && speed > 0;
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

            if (remaining <= 1e-4f) break;
            float segmentHeading = Normalize(MathF.Atan2(segment.Y, segment.X));
            if (AngleBetween(segmentHeading, newHeading) > TurnRadians) turned = true;
            newHeading = segmentHeading;
            if (remaining < length)
            {
                position += (target - position) * (remaining / length);
                remaining = 0;
                break;
            }

            position = target;
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
        if (Validate(map, state.Anchor, position) is { } valid) StopAt(session, player, state, valid, heading, nextPoint, now);
        else StopAt(session, player, state, state.Anchor, state.Heading, route.NextPoint, now);
    }

    private static void StopAt(WorldSession session, Player player, MotionState state, Vector3 position, float heading,
        int nextPoint, uint now)
    {
        PlayerbotRoute? route = state.Route;
        state.Clear();
        if (Send(session, player, WorldOpcode.MsgMoveStop, position, heading, state.Walk, moving: false, now, budgeted: false)
            && route is not null)
            route.NextPoint = nextPoint;
    }

    /// <summary>One movement packet through the ordinary handler; true when the server stored it.</summary>
    private static bool Send(WorldSession session, Player player, WorldOpcode opcode, Vector3 position, float heading,
        bool walk, bool moving, uint now, bool budgeted)
    {
        MovementInfo movement = player.Movement;
        movement.Flags &= ~(MovementFlags.MaskMoving | MovementFlags.SplineEnabled | MovementFlags.WalkMode
            | MovementFlags.TurnLeft | MovementFlags.TurnRight);
        if (moving) movement.Flags |= MovementFlags.Forward;
        if (walk) movement.Flags |= MovementFlags.WalkMode;
        movement.X = position.X;
        movement.Y = position.Y;
        movement.Z = position.Z;
        movement.Orientation = Normalize(heading);
        movement.Time = now;
        movement.FallTime = 0;
        movement.CorrectData();
        var writer = new PacketWriter(64);
        movement.Write(writer);
        ManagedActionBudget? budget = session.ManagedBudget;
        if (!budgeted) session.ManagedBudget = null;
        bool sent;
        try { sent = session.TryManagedAction(opcode, writer.ToArray()); }
        finally { if (!budgeted) session.ManagedBudget = budget; }
        if (!sent) return false;
        Of(player).Packets++;
        return Vector3.Distance(new(player.X, player.Y, player.Z), position) <= 0.5f
            && ((player.Movement.Flags & MovementFlags.Forward) != 0) == moving;
    }

    /// <summary>The floor under <paramref name="candidate"/> and a clear line from <paramref name="from"/>, or null.</summary>
    private static Vector3? Validate(Map map, Vector3 from, Vector3 candidate)
    {
        if (!Finite(candidate)) return null;
        float floor = map.Collision.GetHeight(candidate.X, candidate.Y, candidate.Z);
        if (!float.IsFinite(floor) || floor == TerrainTile.InvalidHeight || floor == TerrainTile.InvalidHeightValue
            || MathF.Abs(floor - candidate.Z) > 2f)
            return null;
        if (!map.Collision.IsInLineOfSight(from.X, from.Y, from.Z + 2, candidate.X, candidate.Y, floor + 2)) return null;
        return candidate with { Z = floor + 0.05f };
    }

    /// <summary>Alive, or a ghost running back to its corpse; a corpse lying on the ground does not move.</summary>
    internal static bool CanMove(Player player) => player.IsAlive || (player.Flags & PlayerFlags.Ghost) != 0;

    private static uint Lease(PlayerbotOptions options) => (uint)Math.Max(2000, options.ThinkIntervalMs * 4);

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

    private static Vector2 Flat(Vector3 value) => new(value.X, value.Y);

    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
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
    internal const float AreaRadiusYards = 10f;

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
