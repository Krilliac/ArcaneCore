using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Transports;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Tuning;

namespace ArcaneCore.World.Playerbots;

/// <summary>What a known ship entry is: a boat or a zeppelin, who may ride it, and the route's name (logs and anomalies only).</summary>
internal readonly record struct ShipClass(TravelMeans Means, TravelAccess Access, string Name);

/// <summary>
/// The boat and zeppelin routes bots may ride (docs/areas/playbots.md, "Travel"), built from the world's own ship data
/// (<see cref="TransportSystem.Templates"/>: <c>gameobject_template</c> type 15 and the client's TaxiPathNode.dbc). Nothing here is a
/// hand-typed coordinate: the two ports of a route are the positions of its two stop frames (TaxiPathNode action flag 2, where the ship
/// waits), and the round trip is the template's period. Only the entries of <see cref="Classic"/> (the <c>playerbot_ship_route</c> rows) are routes; the table says which kind each
/// is and who rides it (a classic-db entry the world does not sail is simply absent).
/// <para>
/// A route whose data does not have exactly two distinct ports (stop frames at different places) is skipped; a classic entry with such
/// data is reported once per transport system (<c>bot-ship/route-bad-data</c>), since a bot can never be offered it.
/// </para>
/// <para>
/// Thread affinity: the routes are built once per <see cref="TransportSystem"/> (on the first call, which reads the immutable templates:
/// any thread) and cached (<see cref="ConditionalWeakTable{TKey,TValue}"/>); the returned list and its records are immutable, so
/// reachability checks off the world thread may read them freely (<see cref="ConnectsMaps"/>).
/// </para>
/// </summary>
internal static class PlayerbotShipNetwork
{
    internal const string AnomalyArea = "ship";

    /// <summary>Two stop frames closer than this on one map are one port (a route that waits twice at one dock).</summary>
    internal const float SamePortYards = 1f;

    /// <summary>
    /// The classic routes by <c>gameobject_template</c> entry (classic-db z2815, vmangos the same; the entries
    /// <see cref="Scenarios.ShipCrossingScenario"/> and RealTransportContentTests name), read from the <c>playerbot_ship_route</c> rows of the
    /// active tuning catalog (<see cref="TuningCatalog.Active"/>; shipped: both factions ride the goblin boat of Booty Bay - Ratchet, the
    /// Alliance the other boats, the Horde the zeppelins). Cached per catalog; the dictionary is immutable.
    /// </summary>
    internal static IReadOnlyDictionary<uint, ShipClass> Classic
    {
        get
        {
            TuningCatalog active = TuningCatalog.Active;
            ClassicBox? box = Volatile.Read(ref s_classicBox);
            if (box is null || box.Generation != active.Generation)
            {
                box = new ClassicBox(active.Generation, ClassificationOf(active));
                Volatile.Write(ref s_classicBox, box);
            }

            return box.Map;
        }
    }

    /// <summary>The ship classification of <paramref name="catalog"/>'s <c>playerbot_ship_route</c> rows.</summary>
    internal static IReadOnlyDictionary<uint, ShipClass> ClassificationOf(TuningCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Ships.ToDictionary(r => r.Entry, r => new ShipClass((TravelMeans)r.Means, (TravelAccess)r.Access, r.Name));
    }

    private static ClassicBox? s_classicBox;

    private sealed record ClassicBox(long Generation, IReadOnlyDictionary<uint, ShipClass> Map);

    private static readonly ConditionalWeakTable<TransportSystem, Box> Cache = [];

    private sealed class Box(IReadOnlyList<ShipRoute> routes, long generation)
    {
        public IReadOnlyList<ShipRoute> Routes { get; } = routes;

        public long Generation { get; } = generation;
    }

    /// <summary>The classic routes of <paramref name="world"/>'s ships, or none while transports are off.</summary>
    internal static IReadOnlyList<ShipRoute> Routes(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return TransportSystem.Of(world) is { } system ? Routes(system) : [];
    }

    /// <summary>The classic routes of <paramref name="system"/> (built once, cached per system).</summary>
    internal static IReadOnlyList<ShipRoute> Routes(TransportSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        TuningCatalog active = TuningCatalog.Active;
        if (Cache.TryGetValue(system, out Box? cached) && cached.Generation == active.Generation) return cached.Routes;
        var built = new Box(Build(system.Templates, ClassificationOf(active), report: true), active.Generation);
        Cache.AddOrUpdate(system, built);
        return built.Routes;
    }

    /// <summary>
    /// The routes of <paramref name="system"/> under another classification (tests: a synthetic entry classified as a zeppelin). Not
    /// cached and never reported.
    /// </summary>
    internal static IReadOnlyList<ShipRoute> Routes(TransportSystem system, IReadOnlyDictionary<uint, ShipClass> classification)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(classification);
        return Build(system.Templates, classification, report: false);
    }

    /// <summary>
    /// The routes a bot of the given faction may ride now: <see cref="Routes(WorldRuntime)"/> filtered by
    /// <see cref="Filter"/> (the faction, <see cref="PlayerbotTravelOptions.Boats"/> and <see cref="PlayerbotTravelOptions.Zeppelins"/>).
    /// </summary>
    internal static IReadOnlyList<ShipRoute> RoutesFor(WorldRuntime world, bool alliance, PlayerbotOptions options)
        => Filter(Routes(world), alliance, options);

    /// <summary>The routes of <paramref name="routes"/> a bot of the given faction may ride with <paramref name="options"/> (pure).</summary>
    internal static IReadOnlyList<ShipRoute> Filter(IReadOnlyList<ShipRoute> routes, bool alliance, PlayerbotOptions options)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(options);
        var allowed = new List<ShipRoute>(routes.Count);
        foreach (ShipRoute route in routes)
            if (MayRide(route, alliance, options.Travel)) allowed.Add(route);
        return allowed;
    }

    /// <summary>Whether a bot of the given faction may ride <paramref name="route"/> with <paramref name="travel"/> (its switch on, its faction let on).</summary>
    internal static bool MayRide(ShipRoute route, bool alliance, PlayerbotTravelOptions travel)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(travel);
        return SwitchedOn(route.Means, travel) && Admits(route.Access, alliance);
    }

    /// <summary>Whether the switch of <paramref name="means"/> is on (a means that is neither a boat nor a zeppelin is never a ship).</summary>
    internal static bool SwitchedOn(TravelMeans means, PlayerbotTravelOptions travel)
        => means switch
        {
            TravelMeans.Boat => travel.Boats,
            TravelMeans.Zeppelin => travel.Zeppelins,
            _ => false,
        };

    internal static bool Admits(TravelAccess access, bool alliance)
        => access == TravelAccess.Both || (access == TravelAccess.Alliance) == alliance;

    /// <summary>
    /// Whether the ship graph of <paramref name="routes"/> joins <paramref name="fromMap"/> to <paramref name="toMap"/> (true for one map):
    /// a breadth-first walk over the maps the routes' ports lie on. Pure and allocation-light; safe off the world thread.
    /// </summary>
    internal static bool ConnectsMaps(IReadOnlyList<ShipRoute> routes, uint fromMap, uint toMap)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (fromMap == toMap) return true;
        var reached = new HashSet<uint> { fromMap };
        var frontier = new Queue<uint>();
        frontier.Enqueue(fromMap);
        while (frontier.TryDequeue(out uint map))
        {
            foreach (ShipRoute route in routes)
            {
                uint other;
                if (route.PortA.MapId == map) other = route.PortB.MapId;
                else if (route.PortB.MapId == map) other = route.PortA.MapId;
                else continue;
                if (other == toMap) return true;
                if (reached.Add(other)) frontier.Enqueue(other);
            }
        }

        return false;
    }

    /// <summary>
    /// The routes of <paramref name="templates"/> that <paramref name="classification"/> names, in entry order. Ports: the distinct stop
    /// frames of the template in key frame order (port A the first). Skipped: an entry without exactly two distinct ports, or with a zero
    /// period; reported (<paramref name="report"/>) once per build.
    /// </summary>
    internal static IReadOnlyList<ShipRoute> Build(IEnumerable<TransportTemplate> templates, IReadOnlyDictionary<uint, ShipClass> classification,
        bool report)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(classification);
        var routes = new List<ShipRoute>();
        foreach (TransportTemplate template in templates.OrderBy(t => t.Entry))
        {
            if (!classification.TryGetValue(template.Entry, out ShipClass kind)) continue;
            List<TravelPlace> ports = Ports(template);
            if (ports.Count != 2 || template.PathTime == 0)
            {
                if (report)
                {
                    AnomalyLedger.Shared.Report(PlayerbotAnomalies.Prefix + AnomalyArea, "route-bad-data",
                        string.Create(CultureInfo.InvariantCulture, $"transport entry {template.Entry}"),
                        string.Create(CultureInfo.InvariantCulture,
                            $"{kind.Name}: {ports.Count} distinct port(s) among {template.KeyFrames.Count(f => f.IsStopFrame)} stop frame(s), period {template.PathTime} ms; bots never ride it"));
                }

                continue;
            }

            routes.Add(new ShipRoute(template.Entry, kind.Means, kind.Access, ports[0], ports[1], template.PathTime / 1000f));
        }

        return routes;
    }

    /// <summary>The distinct places the ship of <paramref name="template"/> waits at, in key frame order.</summary>
    internal static List<TravelPlace> Ports(TransportTemplate template)
    {
        var ports = new List<TravelPlace>(2);
        foreach (TransportKeyFrame frame in template.KeyFrames)
        {
            if (!frame.IsStopFrame) continue;
            var place = new TravelPlace(frame.Node.MapId, frame.Position);
            if (!float.IsFinite(place.Position.X) || !float.IsFinite(place.Position.Y) || !float.IsFinite(place.Position.Z)) continue;
            if (ports.Any(p => p.DistanceTo(place) <= SamePortYards)) continue;
            ports.Add(place);
        }

        return ports;
    }

    /// <summary>Whether <paramref name="frame"/> is the stop frame of <paramref name="port"/> (its map, within <see cref="SamePortYards"/>).</summary>
    internal static bool IsPort(TransportKeyFrame frame, TravelPlace port)
        => frame.IsStopFrame && frame.Node.MapId == port.MapId && Vector3.Distance(frame.Position, port.Position) <= SamePortYards;

    /// <summary>Whether <paramref name="ship"/> waits at <paramref name="port"/> now (world thread): on its map, not moving, at that stop frame.</summary>
    internal static bool DockedAt(ShipTransport ship, TravelPlace port)
        => ship.CurrentMap is { } map && map.MapId == port.MapId && !ship.IsMoving && IsPort(ship.CurrentFrame, port);

    /// <summary>The faction test of a bot (<see cref="Player.Team"/>).</summary>
    internal static bool IsAlliance(Player player) => player.Team == Team.Alliance;
}
