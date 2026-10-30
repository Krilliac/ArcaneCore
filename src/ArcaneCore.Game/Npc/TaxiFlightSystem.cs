using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// Taxi flights (vmangos Player::ActivateTaxiPathTo tail, WorldSession::SendDoFlight,
/// FlightPathMovementGenerator Initialize/Update/Finalize, Unit::Mount/Unmount): the player is
/// mounted on the node's mount display, gets UNIT_FLAG_DISABLE_MOVE | UNIT_FLAG_TAXI_FLIGHT,
/// receives SMSG_ACTIVATETAXIREPLY OK and a flying SMSG_MONSTER_MOVE along the TaxiPathNode
/// waypoints of the first hop. The server position follows the path every map update; each
/// further hop of an express route starts a new spline without landing; at the end the player is
/// dismounted, the flags are cleared and a stop spline is sent. World thread.
/// </summary>
/// <remarks>
/// <para>
/// A path without TaxiPathNode rows is refused. Paths
/// that change map are refused (vmangos splits them at the teleport node). A player logging out
/// mid-flight saves the remaining route and resumes near the saved position on login. A teleport
/// or map change aborts the flight where the player is.
/// </para>
/// <para>
/// The client interpolates the flying spline as Catmull-Rom; the server follows the polyline
/// at the same speed, so positions match to within the curve's sag. Players who start seeing a
/// flyer mid-hop see it still until the next hop (create blocks carry no spline yet).
/// </para>
/// </remarks>
public sealed class TaxiFlightSystem : ITaxiFlights, IMapUpdater
{
    /// <summary>PLAYER_FLIGHT_SPEED (vmangos Player.h), yards per second.</summary>
    public const float FlightSpeed = 32.0f;

    /// <summary>A position drift beyond this (yards) between updates means someone else moved the player (teleport).</summary>
    private const float DriftTolerance = 0.5f;

    private readonly Dictionary<ObjectGuid, Flight> _flights = [];
    private readonly HashSet<ObjectGuid> _aborted = [];
    private readonly NpcStore _npcs;
    private readonly TaxiPathNodeCatalog _pathNodes;
    private readonly Func<uint, uint> _mountDisplay;
    private readonly Func<uint> _nowMs;
    private readonly Action<Player>? _beforeFlight;
    private readonly ILogger _logger;
    private uint _splineId;

    /// <param name="npcs">Taxi nodes.</param>
    /// <param name="pathNodes">TaxiPathNode waypoints.</param>
    /// <param name="mountDisplay">Mount creature entry → display id (vmangos ObjectMgr::GetTaxiMountDisplayId; 0 = unknown).</param>
    /// <param name="nowMs">World time in milliseconds (movement timestamps).</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="beforeFlight">Removes a scripted flight's old spell mount after the route is validated.</param>
    public TaxiFlightSystem(NpcStore npcs, TaxiPathNodeCatalog pathNodes, Func<uint, uint> mountDisplay, Func<uint> nowMs, ILogger? logger = null, Action<Player>? beforeFlight = null)
    {
        _npcs = npcs;
        _pathNodes = pathNodes;
        _mountDisplay = mountDisplay;
        _nowMs = nowMs;
        _logger = logger ?? NullLogger.Instance;
        _beforeFlight = beforeFlight;
    }

    /// <summary>Raised after a flight ended at its destination (the player was put down).</summary>
    public event Action<Player, uint>? Landed;

    /// <summary>Raised when an in-progress flight is aborted before reaching its destination (external teleport, drift, or explicit abort).</summary>
    public event Action<Player, uint>? Aborted;

    /// <summary>Raised when a route finishes or is interrupted, so its persisted resume state can be cleared.</summary>
    public event Action<Player>? FlightEnded;

    /// <summary>Whether the player's last flight was aborted before reaching its destination (consumed once read).</summary>
    public bool TryConsumeAborted(Player player) => player is not null && _aborted.Remove(player.Guid);

    public int ActiveFlights => _flights.Count;

    public bool IsFlying(Player player) => _flights.TryGetValue(player.Guid, out Flight? f) && ReferenceEquals(f.Player, player);

    /// <summary>The remaining waypoints of the current hop (tests and diagnostics).</summary>
    public IReadOnlyList<Waypoint>? CurrentHop(Player player) => IsFlying(player) ? _flights[player.Guid].Hops[_flights[player.Guid].Hop] : null;

    public bool StartFlight(Player player, IReadOnlyList<uint> nodes, IReadOnlyList<uint> pathIds, uint mountCreatureEntry)
        => StartFlight(player, nodes, pathIds, mountCreatureEntry,
            new uint[(pathIds ?? throw new ArgumentNullException(nameof(pathIds))).Count], static (_, _) => true);

    public bool StartFlight(Player player, IReadOnlyList<uint> nodes, IReadOnlyList<uint> pathIds, uint mountCreatureEntry,
        IReadOnlyList<uint> legCosts, Func<Player, uint, bool> chargeLeg)
        => StartFlightCore(player, nodes, pathIds, mountCreatureEntry, legCosts, chargeLeg, resuming: false);

    /// <summary>Resume the saved route from the character's stored position without another OK reply or first-leg fare.</summary>
    public bool ResumeFlight(Player player, TaxiFlightRoute route, uint mountCreatureEntry, Func<Player, uint, bool> chargeLeg)
        => route.IsValid && StartFlightCore(player, route.Nodes, route.PathIds, mountCreatureEntry,
            route.LegCosts, chargeLeg, resuming: true);

    private bool StartFlightCore(Player player, IReadOnlyList<uint> nodes, IReadOnlyList<uint> pathIds, uint mountCreatureEntry,
        IReadOnlyList<uint> legCosts, Func<Player, uint, bool> chargeLeg, bool resuming)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(pathIds);
        ArgumentNullException.ThrowIfNull(legCosts);
        ArgumentNullException.ThrowIfNull(chargeLeg);
        if (player.Map is not { } map || !player.IsInWorld || nodes.Count < 2 || pathIds.Count != nodes.Count - 1
            || legCosts.Count != pathIds.Count || IsFlying(player))
        {
            return false;
        }

        uint display = _mountDisplay(mountCreatureEntry);
        if (display == 0)
        {
            _logger.LogDebug("Taxi mount creature {Entry} has no display", mountCreatureEntry);
            return false;
        }

        var hops = new List<Waypoint[]>(pathIds.Count);
        for (int i = 0; i < pathIds.Count; i++)
        {
            if (BuildHop(pathIds[i], nodes[i], nodes[i + 1], player.MapId) is not { } hop)
            {
                return false;
            }

            hops.Add(hop);
        }

        if (!resuming && !chargeLeg(player, legCosts[0]))
        {
            return false;
        }

        // vmangos Player::ActivateTaxiPathTo removes a spell mount before a scripted flight
        // (Player.cpp:17889-17893). The flight-master path already rejects a mounted player.
        _beforeFlight?.Invoke(player);

        var flight = new Flight(player, map, hops, nodes.ToArray(), pathIds.ToArray(), legCosts.ToArray(), chargeLeg)
        {
            Resuming = resuming,
        };
        // vmangos Player::ActivateTaxiPathTo (Player.cpp:18081): a validated taxi start
        // consumes pending extra attacks before flight state and client-control fields publish.
        player.Combat.ResetExtraAttacks();
        _aborted.Remove(player.Guid);
        _flights[player.Guid] = flight;

        // Unit::Mount + FlightPathMovementGenerator::Initialize.
        player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, display);
        player.UnitFlags |= UnitFlags.RemoveClientControl | UnitFlags.TaxiFlight;
        if (!resuming)
        {
            player.Session.Send(WorldOpcode.SmsgActivatetaxireply, NpcPackets.ActivateTaxiReply(ActivateTaxiReply.Ok).AsSpan());
        }

        LaunchHop(flight);
        return true;
    }

    /// <summary>Save the unpaid tail for logout; the ordinary character snapshot saves this position.</summary>
    public TaxiFlightRoute? SuspendForLogout(Player player)
    {
        // A player leaving for good has no later reader of the aborted flag; pruning it here bounds the set by online players.
        _aborted.Remove(player.Guid);
        if (!_flights.TryGetValue(player.Guid, out Flight? flight) || !ReferenceEquals(flight.Player, player))
        {
            return null;
        }

        var costs = flight.LegCosts[flight.Hop..];
        costs[0] = 0; // the active leg was already paid for.
        var route = new TaxiFlightRoute(flight.Nodes[flight.Hop..], flight.PathIds[flight.Hop..], costs);
        Finish(flight, landed: false, retainRoute: true);
        return route;
    }

    public void Update(Map map, uint diffMs)
    {
        if (_flights.Count == 0)
        {
            return;
        }

        foreach (Flight flight in _flights.Values.Where(f => ReferenceEquals(f.Map, map)).ToList())
        {
            try
            {
                Advance(flight, diffMs);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("map.taxi", ex);
            }
        }
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
        if (_flights.TryGetValue(player.Guid, out Flight? flight) && ReferenceEquals(flight.Player, player))
        {
            Abort(player);
        }
    }

    /// <summary>Force a flying player down at the final destination now (administrative recovery).</summary>
    public void LandNow(Player player)
    {
        if (!_flights.TryGetValue(player.Guid, out Flight? flight) || !ReferenceEquals(flight.Player, player))
        {
            return;
        }

        Waypoint last = flight.Hops[^1][^1];
        player.Relocate(last.X, last.Y, last.Z, player.Orientation, _nowMs());
        player.NeedsVisibilityUpdate = true;
        Finish(flight, landed: true);
    }

    /// <summary>End a flight where the player is (teleport, map change).</summary>
    public void Abort(Player player)
    {
        if (_flights.TryGetValue(player.Guid, out Flight? flight) && ReferenceEquals(flight.Player, player))
        {
            Finish(flight, landed: false);
        }
    }

    /// <summary>
    /// SMSG_MONSTER_MOVE, flying form (vmangos PacketBuilder::WriteMonsterMove with
    /// Mask_CatmullRom: WriteCatmullRomPath sends every control point after the start as a full
    /// Vector3): packed GUID, start Vector3, u32 spline id, u8 type 0 (normal), u32 flags
    /// RUNMODE|FLYING, u32 duration ms, u32 point count, points.
    /// </summary>
    /// <remarks>gtker/wow_messages models only the linear (packed-offset) point form; servers win.</remarks>
    public static byte[] BuildFlightMove(ObjectGuid guid, float startX, float startY, float startZ, uint splineId, uint durationMs, IReadOnlyList<Waypoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var w = new PacketWriter(40 + (points.Count * 12));
        w.WritePackedGuid(guid.Value);
        w.WriteSingle(startX);
        w.WriteSingle(startY);
        w.WriteSingle(startZ);
        w.WriteUInt32(splineId);
        w.WriteByte((byte)MonsterMoveType.Normal);
        w.WriteUInt32((uint)(SplineFlags.Runmode | SplineFlags.Flying));
        w.WriteUInt32(durationMs);
        w.WriteUInt32((uint)points.Count);
        foreach (Waypoint p in points)
        {
            w.WriteSingle(p.X);
            w.WriteSingle(p.Y);
            w.WriteSingle(p.Z);
        }

        return w.ToArray();
    }

    /// <summary>The waypoints of one hop, or null when the path is unusable from this map.</summary>
    private Waypoint[]? BuildHop(uint pathId, uint from, uint to, uint mapId)
    {
        if (_npcs.Path(from, to)?.Id != pathId || _npcs.Node(from) is not { } fromNode || fromNode.MapId != mapId
            || _npcs.Node(to) is not { } toNode || toNode.MapId != mapId)
        {
            return null;
        }

        Invariant.Check(
            float.IsFinite(fromNode.X) && float.IsFinite(fromNode.Y) && float.IsFinite(fromNode.Z)
            && float.IsFinite(toNode.X) && float.IsFinite(toNode.Y) && float.IsFinite(toNode.Z),
            "taxi-node-finite-coords",
            $"Taxi nodes ({from} -> {to}) on path {pathId} have non-finite coordinates");

        IReadOnlyList<TaxiPathNodeRecord> rows = _pathNodes.Nodes(pathId);
        if (rows.Count < 2 || rows.Any(r => r.MapId != mapId))
        {
            return null;
        }

        var hop = new Waypoint[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            TaxiPathNodeRecord r = rows[i];
            Invariant.Check(
                float.IsFinite(r.X) && float.IsFinite(r.Y) && float.IsFinite(r.Z),
                "taxi-waypoint-finite-coords",
                $"TaxiPathNode on path {pathId} index {i} has non-finite coordinates ({r.X}, {r.Y}, {r.Z})");
            hop[i] = new Waypoint(r.X, r.Y, r.Z);
        }

        return hop;
    }

    private void LaunchHop(Flight flight)
    {
        Player player = flight.Player;
        Waypoint[] hop = flight.Hops[flight.Hop];

        // MoveSplineInit::Launch replaces the first control point with the current position.
        var segments = new List<Waypoint>(hop.Length) { new(player.X, player.Y, player.Z) };
        // vmangos Player::ContinueTaxiFlight finds the nearest path segment to the saved
        // position (Player.cpp:18128-18167); do not fly back to the first waypoint on relog.
        // A later fold can pass closer at a vertex than the chord still being flown.
        int next = flight.Resuming ? NearestSegment(hop, player.X, player.Y, player.Z) + 1 : 1;
        segments.AddRange(hop.Skip(next));
        flight.Resuming = false;
        if (segments.Count == 1)
        {
            segments.Add(hop[0]);
        }

        flight.Segments = segments;
        flight.Travelled = 0;
        flight.Length = 0;
        for (int i = 1; i < segments.Count; i++)
        {
            flight.Length += Distance(segments[i - 1], segments[i]);
        }

        uint duration = (uint)Math.Max(1, Math.Ceiling(flight.Length / FlightSpeed * 1000));
        flight.SplineId = ++_splineId;
        byte[] packet = BuildFlightMove(player.Guid, player.X, player.Y, player.Z, flight.SplineId, duration, segments.Skip(1).ToList());
        player.Session.Send(WorldOpcode.SmsgMonsterMove, packet);
        flight.Map.BroadcastToObservers(player, WorldOpcode.SmsgMonsterMove, packet);
        flight.Expected = segments[0];
    }

    private void Advance(Flight flight, uint diffMs)
    {
        Player player = flight.Player;
        if (!player.IsAlive)
        {
            // Death in flight is exceptional; put the body at the current hop's departure node rather than in the air
            // (not the route's origin, which an express flight may have left several hops ago).
            PutDownAtNode(flight, flight.Nodes[flight.Hop]);
            Finish(flight, landed: false);
            return;
        }

        if (!player.IsInWorld || !ReferenceEquals(player.Map, flight.Map)
            || Math.Abs(player.X - flight.Expected.X) > DriftTolerance || Math.Abs(player.Y - flight.Expected.Y) > DriftTolerance
            || Math.Abs(player.Z - flight.Expected.Z) > DriftTolerance)
        {
            Finish(flight, landed: false);
            return;
        }

        flight.Travelled += FlightSpeed * diffMs / 1000.0;
        if (flight.Travelled >= flight.Length)
        {
            Waypoint end = flight.Segments[^1];
            Move(flight, end);
            if (flight.Hop + 1 < flight.Hops.Count)
            {
                flight.Hop++;
                // vmangos FlightPathMovementGenerator::Update charges a subsequent leg at its path boundary.
                if (!flight.ChargeLeg(player, flight.LegCosts[flight.Hop]))
                {
                    // An express transition does not land: put the player down at the node just reached, not in the air.
                    PutDownAtNode(flight, flight.Nodes[flight.Hop]);
                    Finish(flight, landed: false);
                    return;
                }

                LaunchHop(flight);
            }
            else
            {
                Finish(flight, landed: true);
            }

            return;
        }

        double remaining = flight.Travelled;
        for (int i = 1; i < flight.Segments.Count; i++)
        {
            Waypoint a = flight.Segments[i - 1];
            Waypoint b = flight.Segments[i];
            double length = Distance(a, b);
            if (remaining <= length)
            {
                float t = length <= 0 ? 1f : (float)(remaining / length);
                Waypoint interpolated = EvaluateCatmullRom(flight.Segments, i - 1, t, flight.Player.Orientation, out float heading);
                Move(flight, interpolated, heading);
                return;
            }

            remaining -= length;
        }
    }

    /// <summary>
    /// Evaluate the uniform Catmull-Rom spline (with clamped endpoints) on segment <paramref name="segmentIndex"/> (<c>P1 -&gt; P2</c>)
    /// at parameter <paramref name="t"/> in <c>[0, 1]</c>, matching the 1.12.1 client's <c>SplineFlags.Flying</c> Catmull-Rom interpolation.
    /// For a 2-point hop (<c>segments.Count &lt; 3</c>), returns exact linear interpolation.
    /// </summary>
    public static Waypoint EvaluateCatmullRom(
        IReadOnlyList<Waypoint> segments,
        int segmentIndex,
        float t,
        float fallbackOrientation,
        out float orientation)
    {
        ArgumentNullException.ThrowIfNull(segments);
        t = Math.Clamp(t, 0f, 1f);
        Waypoint p1 = segments[segmentIndex];
        Waypoint p2 = segments[segmentIndex + 1];

        if (segments.Count < 3)
        {
            float dx = p2.X - p1.X;
            float dy = p2.Y - p1.Y;
            orientation = (dx * dx) + (dy * dy) > 0.0001f
                ? Creature.NormalizeOrientation(MathF.Atan2(dy, dx))
                : fallbackOrientation;
            return new Waypoint(
                p1.X + ((p2.X - p1.X) * t),
                p1.Y + ((p2.Y - p1.Y) * t),
                p1.Z + ((p2.Z - p1.Z) * t));
        }

        Waypoint p0 = segmentIndex > 0 ? segments[segmentIndex - 1] : p1;
        Waypoint p3 = segmentIndex + 2 < segments.Count ? segments[segmentIndex + 2] : p2;

        float t2 = t * t;
        float t3 = t2 * t;

        // Standard Catmull-Rom basis (tau = 0.5):
        // P(t) = 0.5 * (2*P1 + (-P0 + P2)*t + (2*P0 - 5*P1 + 4*P2 - P3)*t^2 + (-P0 + 3*P1 - 3*P2 + P3)*t^3)
        float x = 0.5f * ((2f * p1.X)
            + ((-p0.X + p2.X) * t)
            + (((2f * p0.X) - (5f * p1.X) + (4f * p2.X) - p3.X) * t2)
            + ((-p0.X + (3f * p1.X) - (3f * p2.X) + p3.X) * t3));
        float y = 0.5f * ((2f * p1.Y)
            + ((-p0.Y + p2.Y) * t)
            + (((2f * p0.Y) - (5f * p1.Y) + (4f * p2.Y) - p3.Y) * t2)
            + ((-p0.Y + (3f * p1.Y) - (3f * p2.Y) + p3.Y) * t3));
        float z = 0.5f * ((2f * p1.Z)
            + ((-p0.Z + p2.Z) * t)
            + (((2f * p0.Z) - (5f * p1.Z) + (4f * p2.Z) - p3.Z) * t2)
            + ((-p0.Z + (3f * p1.Z) - (3f * p2.Z) + p3.Z) * t3));

        // Analytical tangent derivative P'(t):
        float tx = 0.5f * ((-p0.X + p2.X)
            + (2f * ((2f * p0.X) - (5f * p1.X) + (4f * p2.X) - p3.X) * t)
            + (3f * (-p0.X + (3f * p1.X) - (3f * p2.X) + p3.X) * t2));
        float ty = 0.5f * ((-p0.Y + p2.Y)
            + (2f * ((2f * p0.Y) - (5f * p1.Y) + (4f * p2.Y) - p3.Y) * t)
            + (3f * (-p0.Y + (3f * p1.Y) - (3f * p2.Y) + p3.Y) * t2));

        orientation = (tx * tx) + (ty * ty) > 0.0001f
            ? Creature.NormalizeOrientation(MathF.Atan2(ty, tx))
            : fallbackOrientation;
        return new Waypoint(x, y, z);
    }

    /// <summary>Relocate the player onto a taxi node of the flight's map; a node missing or on another map leaves it in place.</summary>
    private void PutDownAtNode(Flight flight, uint nodeId)
    {
        if (_npcs.Node(nodeId) is { } node && node.MapId == flight.Map.MapId)
        {
            Invariant.Check(
                float.IsFinite(node.X) && float.IsFinite(node.Y) && float.IsFinite(node.Z),
                "taxi-node-finite-coords",
                $"Taxi node {nodeId} has non-finite coordinates ({node.X}, {node.Y}, {node.Z})");
            flight.Player.Relocate(node.X, node.Y, node.Z, flight.Player.Orientation, _nowMs());
            flight.Player.NeedsVisibilityUpdate = true;
        }
    }

    private void Move(Flight flight, Waypoint to, float? tangentOrientation = null)
    {
        Invariant.Check(
            float.IsFinite(to.X) && float.IsFinite(to.Y) && float.IsFinite(to.Z),
            "taxi-waypoint-finite-coords",
            $"Taxi flight waypoint for player {flight.Player.Guid} has non-finite coordinates ({to.X}, {to.Y}, {to.Z})");
        Player player = flight.Player;
        float orientation;
        if (tangentOrientation is { } tangent && float.IsFinite(tangent))
        {
            orientation = tangent;
        }
        else
        {
            float dx = to.X - player.X;
            float dy = to.Y - player.Y;
            orientation = (dx * dx) + (dy * dy) > 0.0001f
                ? Creature.NormalizeOrientation(MathF.Atan2(dy, dx))
                : player.Orientation;
        }

        // vmangos Unit::SetPosition for a player (Unit.cpp:10672) -> Player::SetPosition: a spline step moves the player, so a state emote ends.
        player.ClearEmoteState();
        player.Relocate(to.X, to.Y, to.Z, orientation, _nowMs());
        player.NeedsVisibilityUpdate = true;
        flight.Expected = to;
    }

    /// <summary>FlightPathMovementGenerator::Finalize: unmount, clear the flags, stop the spline.</summary>
    private void Finish(Flight flight, bool landed, bool retainRoute = false)
    {
        Player player = flight.Player;
        _flights.Remove(player.Guid);
        player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 0);
        player.UnitFlags &= ~(UnitFlags.RemoveClientControl | UnitFlags.TaxiFlight);
        if (player.IsInWorld && ReferenceEquals(player.Map, flight.Map))
        {
            byte[] stop = CreatureMovePackets.BuildStop(player.Guid, player.X, player.Y, player.Z, ++_splineId);
            player.Session.Send(WorldOpcode.SmsgMonsterMove, stop);
            flight.Map.BroadcastToObservers(player, WorldOpcode.SmsgMonsterMove, stop);
        }

        if (landed)
        {
            _aborted.Remove(player.Guid);
            Landed?.Invoke(player, flight.Destination);
        }
        else if (!retainRoute)
        {
            _aborted.Add(player.Guid);
            Aborted?.Invoke(player, flight.Destination);
        }

        if (!retainRoute)
        {
            FlightEnded?.Invoke(player);
        }
    }

    private static int NearestSegment(Waypoint[] path, float x, float y, float z)
    {
        int nearest = 0;
        double best = double.PositiveInfinity;
        int along = -1;
        double bestAlong = double.PositiveInfinity;
        for (int i = 0; i < path.Length - 1; i++)
        {
            Waypoint a = path[i];
            Waypoint b = path[i + 1];
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            double length2 = dx * dx + dy * dy + dz * dz;
            double rawT = length2 == 0 ? 0 : ((x - a.X) * dx + (y - a.Y) * dy + (z - a.Z) * dz) / length2;
            double t = Math.Clamp(rawT, 0, 1);
            double ex = x - (a.X + t * dx), ey = y - (a.Y + t * dy), ez = z - (a.Z + t * dz);
            double distance2 = ex * ex + ey * ey + ez * ez;
            if (distance2 < best)
            {
                best = distance2;
                nearest = i;
            }

            // Between the endpoints, not clamped to a vertex the next fold happens to swing past.
            if (rawT > 0 && rawT < 1 && distance2 < bestAlong)
            {
                bestAlong = distance2;
                along = i;
            }
        }

        // Sitting on a vertex continues from that vertex. Otherwise the chord the player is
        // actually between wins, even when a fold's vertex is nearer. 16 yards still counts as
        // that chord (Catmull-Rom sag); a point merely inside a distant segment's slab does not.
        const double onVertex2 = 0.05 * 0.05;
        const double onSegment2 = 16.0 * 16.0;
        if (along >= 0 && bestAlong <= onSegment2 && best > onVertex2)
        {
            return along;
        }

        return nearest;
    }

    private static double Distance(Waypoint a, Waypoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double dz = b.Z - a.Z;
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>A flight waypoint.</summary>
    public readonly record struct Waypoint(float X, float Y, float Z);

    private sealed class Flight(Player player, Map map, List<Waypoint[]> hops, uint[] nodes, uint[] pathIds,
        uint[] legCosts, Func<Player, uint, bool> chargeLeg)
    {
        public Player Player { get; } = player;

        public Map Map { get; } = map;

        public List<Waypoint[]> Hops { get; } = hops;

        public uint Destination => Nodes[^1];

        public uint[] Nodes { get; } = nodes;

        public uint[] PathIds { get; } = pathIds;

        public uint[] LegCosts { get; } = legCosts;

        public Func<Player, uint, bool> ChargeLeg { get; } = chargeLeg;

        public int Hop { get; set; }

        public bool Resuming { get; set; }

        public List<Waypoint> Segments { get; set; } = [];

        public double Length { get; set; }

        public double Travelled { get; set; }

        public uint SplineId { get; set; }

        public Waypoint Expected { get; set; }
    }
}
