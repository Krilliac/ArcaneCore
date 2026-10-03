using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
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
/// A path without TaxiPathNode rows flies straight between the two TaxiNodes positions. Paths
/// that change map are refused (vmangos splits them at the teleport node; vanilla has none on
/// the continents). A player logging out mid-flight is put down at the final destination before
/// the save (vmangos instead stores the remaining route and resumes it at login). A teleport or
/// map change aborts the flight where the player is.
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
    private readonly NpcStore _npcs;
    private readonly TaxiPathNodeCatalog _pathNodes;
    private readonly Func<uint, uint> _mountDisplay;
    private readonly Func<uint> _nowMs;
    private readonly ILogger _logger;
    private uint _splineId;

    /// <param name="npcs">Taxi nodes.</param>
    /// <param name="pathNodes">TaxiPathNode waypoints.</param>
    /// <param name="mountDisplay">Mount creature entry → display id (vmangos ObjectMgr::GetTaxiMountDisplayId; 0 = unknown).</param>
    /// <param name="nowMs">World time in milliseconds (movement timestamps).</param>
    /// <param name="logger">Diagnostics.</param>
    public TaxiFlightSystem(NpcStore npcs, TaxiPathNodeCatalog pathNodes, Func<uint, uint> mountDisplay, Func<uint> nowMs, ILogger? logger = null)
    {
        _npcs = npcs;
        _pathNodes = pathNodes;
        _mountDisplay = mountDisplay;
        _nowMs = nowMs;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Raised after a flight ended at its destination (the player was put down).</summary>
    public event Action<Player, uint>? Landed;

    public int ActiveFlights => _flights.Count;

    public bool IsFlying(Player player) => _flights.TryGetValue(player.Guid, out Flight? f) && ReferenceEquals(f.Player, player);

    /// <summary>The remaining waypoints of the current hop (tests and diagnostics).</summary>
    public IReadOnlyList<Waypoint>? CurrentHop(Player player) => IsFlying(player) ? _flights[player.Guid].Hops[_flights[player.Guid].Hop] : null;

    public bool StartFlight(Player player, IReadOnlyList<uint> nodes, IReadOnlyList<uint> pathIds, uint mountCreatureEntry)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(pathIds);
        if (player.Map is not { } map || !player.IsInWorld || nodes.Count < 2 || pathIds.Count != nodes.Count - 1 || IsFlying(player))
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

        var flight = new Flight(player, map, hops, nodes[^1]);
        _flights[player.Guid] = flight;

        // Unit::Mount + FlightPathMovementGenerator::Initialize.
        player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, display);
        player.UnitFlags |= UnitFlags.RemoveClientControl | UnitFlags.TaxiFlight;
        player.Session.Send(WorldOpcode.SmsgActivatetaxireply, NpcPackets.ActivateTaxiReply(ActivateTaxiReply.Ok).AsSpan());
        LaunchHop(flight);
        return true;
    }

    public void Update(Map map, uint diffMs)
    {
        if (_flights.Count == 0)
        {
            return;
        }

        foreach (Flight flight in _flights.Values.Where(f => ReferenceEquals(f.Map, map)).ToList())
        {
            Advance(flight, diffMs);
        }
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
        if (_flights.TryGetValue(player.Guid, out Flight? flight) && ReferenceEquals(flight.Player, player))
        {
            Abort(player);
        }
    }

    /// <summary>Put a flying player down at the final destination now (logout before the save).</summary>
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
        IReadOnlyList<TaxiPathNodeRecord> rows = _pathNodes.Nodes(pathId);
        if (rows.Count > 0)
        {
            return rows.Any(r => r.MapId != mapId) ? null : rows.Select(r => new Waypoint(r.X, r.Y, r.Z)).ToArray();
        }

        if (_npcs.Node(from) is not { } a || _npcs.Node(to) is not { } b || a.MapId != mapId || b.MapId != mapId)
        {
            return null;
        }

        return [new Waypoint(a.X, a.Y, a.Z), new Waypoint(b.X, b.Y, b.Z)];
    }

    private void LaunchHop(Flight flight)
    {
        Player player = flight.Player;
        Waypoint[] hop = flight.Hops[flight.Hop];

        // MoveSplineInit::Launch replaces the first control point with the current position.
        var segments = new List<Waypoint>(hop.Length) { new(player.X, player.Y, player.Z) };
        segments.AddRange(hop.Skip(1));
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
                double t = length <= 0 ? 1 : remaining / length;
                Move(flight, new Waypoint(
                    (float)(a.X + ((b.X - a.X) * t)),
                    (float)(a.Y + ((b.Y - a.Y) * t)),
                    (float)(a.Z + ((b.Z - a.Z) * t))));
                return;
            }

            remaining -= length;
        }
    }

    private void Move(Flight flight, Waypoint to)
    {
        Player player = flight.Player;
        float orientation = MathF.Atan2(to.Y - player.Y, to.X - player.X);
        if (orientation < 0)
        {
            orientation += 2 * MathF.PI;
        }

        player.Relocate(to.X, to.Y, to.Z, float.IsFinite(orientation) ? orientation : player.Orientation, _nowMs());
        player.NeedsVisibilityUpdate = true;
        flight.Expected = to;
    }

    /// <summary>FlightPathMovementGenerator::Finalize: unmount, clear the flags, stop the spline.</summary>
    private void Finish(Flight flight, bool landed)
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
            Landed?.Invoke(player, flight.Destination);
        }
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

    private sealed class Flight(Player player, Map map, List<Waypoint[]> hops, uint destination)
    {
        public Player Player { get; } = player;

        public Map Map { get; } = map;

        public List<Waypoint[]> Hops { get; } = hops;

        public uint Destination { get; } = destination;

        public int Hop { get; set; }

        public List<Waypoint> Segments { get; set; } = [];

        public double Length { get; set; }

        public double Travelled { get; set; }

        public uint SplineId { get; set; }

        public Waypoint Expected { get; set; }
    }
}
