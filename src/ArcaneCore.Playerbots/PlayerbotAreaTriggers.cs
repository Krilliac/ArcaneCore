using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Area triggers the way a 1.12 client reports them: while the bot moves along a route (<see cref="PlayerbotMotion"/>), entering
/// an <c>areatrigger_template</c> volume on its map — a sphere, or a box turned by its orientation (<see cref="AreaTriggerZone"/>,
/// vmangos <c>IsPointInAreaTriggerZone</c>) — sends CMSG_AREATRIGGER once; the trigger fires again only after the bot has left the
/// volume and come back. The server half is the ordinary handler (<c>TeleportHandlers.HandleAreaTrigger</c>, vmangos
/// MiscHandler.cpp:653-800 with the ghost rules of MiscHandler.cpp:712-756), which checks the position again with its 5-yard lag
/// tolerance, so the bot reports where it is (a heartbeat) right before the trigger.
/// <para>
/// Unlike a player, a bot does not take every teleport it walks into (<see cref="PlayerbotMapPolicy.AllowsTrigger"/>): a trigger
/// whose <c>areatrigger_teleport</c> leads off <see cref="PlayerbotOptions.AllowedMaps"/> (a dungeon entrance, the Deeprun Tram) is
/// skipped, because nothing would bring the bot back and the login gate refuses it there at the next start. A ghost on its way to
/// its body through that entrance takes it, and so does a bot whose controller opted in (<see cref="AllowTeleports"/>), or whose tram
/// trip heads for the Deeprun Tram (<see cref="PlayerbotTram"/>, through <see cref="AllowTeleportsTo"/>). Triggers
/// that teleport nowhere (taverns, quest exploration) are always reported.
/// </para>
/// <para>
/// A bot that arrives inside a volume without walking into it (a teleport, a login, a scenario placement) does not fire it: the
/// first position seen on a map only sets what the bot is inside. That keeps a bot landing at a dungeon exit, beside the entrance
/// box, from bouncing straight back in.
/// </para>
/// <para>
/// The triggers of each map are cached per <see cref="WorldMaps"/> in a coarse grid (<see cref="PlayerbotTriggerIndex"/>), rebuilt
/// when the trigger table is reloaded, so the per-tick check of a moving bot only tests the few volumes near it.
/// </para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
internal static class PlayerbotAreaTriggers
{
    /// <summary>
    /// An area trigger with a teleport that the bot walked into and that did not take it anywhere: <see cref="Update"/> noted it
    /// (<see cref="TryConsumeTeleportRefusal(Player, out TeleportRefusal)"/>). <paramref name="AtMs"/> is the world time it was noted.
    /// </summary>
    internal readonly record struct TeleportRefusal(uint Trigger, string Why, uint AtMs);

    private static readonly ConditionalWeakTable<WorldMaps, MapCache> Caches = [];
    private static readonly ConditionalWeakTable<Player, PlayerbotTriggerTracker> Trackers = [];

    private sealed class MapCache
    {
        internal IReadOnlyCollection<AreaTriggerTemplate>? Source;
        internal readonly Dictionary<uint, PlayerbotTriggerIndex> ByMap = [];
        internal readonly Dictionary<(uint FromMap, uint TargetMap), AreaTriggerTemplate[]> ByTargetMap = [];
        internal readonly Dictionary<uint, (AreaTriggerTemplate Trigger, AreaTriggerTeleport Teleport)[]> SameMap = [];
    }

    /// <summary>
    /// The area triggers on <paramref name="mapId"/> whose teleport leads within that map (a portal such as Teldrassil's Darnassus 527 and
    /// Rut'theran 542), each with its teleport row (cached per map like <see cref="EntrancesTo"/>; no allocation on a cache hit).
    /// </summary>
    internal static IReadOnlyList<(AreaTriggerTemplate Trigger, AreaTriggerTeleport Teleport)> SameMapPortals(WorldMaps maps, uint mapId)
    {
        PlayerbotTriggerIndex index = Index(maps, mapId);
        MapCache cache = Caches.GetValue(maps, static _ => new MapCache());
        if (!cache.SameMap.TryGetValue(mapId, out (AreaTriggerTemplate, AreaTriggerTeleport)[]? list))
        {
            var found = new List<(AreaTriggerTemplate, AreaTriggerTeleport)>();
            foreach (AreaTriggerTemplate trigger in index.All)
            {
                if (maps.FindAreaTriggerTeleport(trigger.Id) is { } teleport && teleport.TargetMap == mapId) found.Add((trigger, teleport));
            }

            list = [.. found];
            cache.SameMap[mapId] = list;
        }

        return list;
    }

    /// <summary>The area triggers on <paramref name="mapId"/>, in id order (cached per map; rebuilt after a reload of the trigger table).</summary>
    internal static IReadOnlyList<AreaTriggerTemplate> OnMap(WorldMaps maps, uint mapId) => Index(maps, mapId).All;

    /// <summary>The area triggers on <paramref name="fromMap"/> whose teleport leads into <paramref name="targetMap"/> (cached per map pair).</summary>
    internal static IReadOnlyList<AreaTriggerTemplate> EntrancesTo(WorldMaps maps, uint fromMap, uint targetMap)
    {
        PlayerbotTriggerIndex index = Index(maps, fromMap);
        MapCache cache = Caches.GetValue(maps, _ => new MapCache());
        if (!cache.ByTargetMap.TryGetValue((fromMap, targetMap), out AreaTriggerTemplate[]? list))
        {
            list = [.. index.All.Where(t => maps.FindAreaTriggerTeleport(t.Id)?.TargetMap == targetMap)];
            cache.ByTargetMap[(fromMap, targetMap)] = list;
        }

        return list;
    }

    /// <summary>The grid of the area triggers on <paramref name="mapId"/> (cached like <see cref="OnMap"/>).</summary>
    // Separate so Index allocates nothing on a cache hit: a lambda capturing 'mapId' in Index cost a closure per call (~11 MB/min on live).
    private static PlayerbotTriggerIndex BuildIndex(IReadOnlyCollection<AreaTriggerTemplate> source, uint mapId)
        => new(source.Where(t => t.MapId == mapId));

    internal static PlayerbotTriggerIndex Index(WorldMaps maps, uint mapId)
    {
        MapCache cache = Caches.GetValue(maps, static _ => new MapCache());
        IReadOnlyCollection<AreaTriggerTemplate> source = maps.AreaTriggers;
        if (!ReferenceEquals(cache.Source, source))
        {
            cache.ByMap.Clear();
            cache.ByTargetMap.Clear();
            cache.SameMap.Clear();
            cache.Source = source;
        }

        if (!cache.ByMap.TryGetValue(mapId, out PlayerbotTriggerIndex? index))
        {
            index = BuildIndex(source, mapId);
            cache.ByMap[mapId] = index;
        }

        return index;
    }

    /// <summary>
    /// Let the bot take (or stop it taking) teleports that lead off <see cref="PlayerbotOptions.AllowedMaps"/>: for a controller
    /// that brings the bot back out itself (a scenario, a party controller). Kept across teleports; false by default.
    /// </summary>
    internal static void AllowTeleports(Player player, bool allowed)
    {
        ArgumentNullException.ThrowIfNull(player);
        Trackers.GetValue(player, _ => new PlayerbotTriggerTracker()).TeleportsAllowed = allowed;
    }

    /// <summary>
    /// Let the bot take the teleports that lead to <paramref name="targetMap"/> only (null: none), whatever its
    /// <see cref="PlayerbotOptions.AllowedMaps"/>: a follower walking into the entrance its leader took (<see cref="Party.PlayerbotFollower"/>).
    /// Every other trigger keeps the ordinary rule. Kept across teleports.
    /// </summary>
    internal static void AllowTeleportsTo(Player player, uint? targetMap)
    {
        ArgumentNullException.ThrowIfNull(player);
        Trackers.GetValue(player, _ => new PlayerbotTriggerTracker()).TeleportTarget = targetMap;
    }

    /// <summary>The one map whose teleports the bot may take besides its AllowedMaps (<see cref="AllowTeleportsTo"/>), or null.</summary>
    internal static uint? TeleportTargetOf(Player player)
        => Trackers.TryGetValue(player, out PlayerbotTriggerTracker? tracker) ? tracker.TeleportTarget : null;

    /// <summary>
    /// The latest teleport trigger the bot walked into that did not teleport it, if any, and forget it. A walker that expects a particular
    /// entrance (<see cref="PlayerbotTram"/>) fails its trip at once instead of standing in the volume until its progress bound; the
    /// <see cref="TeleportRefusal.AtMs"/> stamp lets a consumer ignore a refusal older than what it is doing. Recorded for a trigger blocked by
    /// <see cref="PlayerbotMapPolicy.AllowsTrigger"/> and for a dispatched trigger after which the bot had not moved (level or faction
    /// requirement, an ineligible ghost, a refused teleport); one slot per bot, the newest wins. Not recorded during a chaos probe.
    /// </summary>
    internal static bool TryConsumeTeleportRefusal(Player player, out TeleportRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(player);
        refusal = default;
        if (!Trackers.TryGetValue(player, out PlayerbotTriggerTracker? tracker) || tracker.Refusal is not { } noted) return false;
        tracker.Refusal = null;
        refusal = noted;
        return true;
    }

    /// <summary><see cref="TryConsumeTeleportRefusal(Player, out TeleportRefusal)"/> split into its parts.</summary>
    internal static bool TryConsumeTeleportRefusal(Player player, out uint trigger, out string why)
    {
        bool found = TryConsumeTeleportRefusal(player, out TeleportRefusal refusal);
        trigger = refusal.Trigger;
        why = refusal.Why ?? string.Empty;
        return found;
    }

    /// <summary>Consume the noted refusal only when it is of <paramref name="trigger"/> and not older than <paramref name="notBeforeMs"/> (world time); another trigger's refusal stays for its own walker.</summary>
    internal static bool TryConsumeTeleportRefusal(Player player, uint trigger, uint notBeforeMs, out string why)
    {
        ArgumentNullException.ThrowIfNull(player);
        why = string.Empty;
        if (!Trackers.TryGetValue(player, out PlayerbotTriggerTracker? tracker) || tracker.Refusal is not { } noted || noted.Trigger != trigger) return false;
        tracker.Refusal = null;
        if (unchecked((int)(noted.AtMs - notBeforeMs)) < 0) return false;
        why = noted.Why;
        return true;
    }

    /// <summary>Forget a noted refusal (a walker starts a new attempt).</summary>
    internal static void ClearTeleportRefusal(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (Trackers.TryGetValue(player, out PlayerbotTriggerTracker? tracker)) tracker.Refusal = null;
    }

    /// <summary>Forget what the bot is inside (teleport, death): the next position seen sets it again without firing.</summary>
    internal static void Reset(Player player)
    {
        if (Trackers.TryGetValue(player, out PlayerbotTriggerTracker? tracker)) tracker.Forget();
    }

    internal static PlayerbotTriggerTracker TrackerOf(Player player) => Trackers.GetValue(player, static _ => new PlayerbotTriggerTracker());

    /// <summary>
    /// A route starts at <paramref name="position"/>: note the maps the bot may teleport to, and, when nothing is known for this
    /// map yet, what the bot stands in (no trigger fires), so a volume it walks into from here is an entry.
    /// </summary>
    internal static void Begin(WorldSession session, Player player, Map map, Vector3 position, PlayerbotOptions options,
        PlayerbotTriggerTracker? tracker = null)
    {
        tracker ??= TrackerOf(player);
        tracker.AllowedMaps = options.AllowedMaps;
        if (!tracker.Knows(map))
            tracker.Observe(map, map.MapId, position, tracker.Near(session.World, map, position), fire: false);
    }

    /// <summary>Whether moving to <paramref name="position"/> enters a trigger the bot is not in (nothing changes).</summary>
    internal static bool WouldEnter(WorldSession session, Player player, Map map, Vector3 position, PlayerbotTriggerTracker? tracker = null)
        => (tracker is not null || Trackers.TryGetValue(player, out tracker))
            && tracker.WouldEnter(map, map.MapId, position, tracker.Near(session.World, map, position));

    /// <summary>
    /// The bot reported <paramref name="position"/>: send CMSG_AREATRIGGER for each volume it has just entered and may take
    /// (<see cref="MayTake"/>; never refused for lack of the shared action budget: a client always reports its own triggers) and
    /// note the ones it left. Returns the triggers sent.
    /// </summary>
    internal static IReadOnlyList<uint> Update(WorldSession session, Player player, Map map, Vector3 position,
        PlayerbotTriggerTracker? tracker = null)
    {
        tracker ??= TrackerOf(player);
        IReadOnlyList<uint> entered = tracker.Observe(map, map.MapId, position, tracker.Near(session.World, map, position), fire: true);
        if (entered.Count == 0) return entered;
        WorldMaps maps = tracker.MapsOf(session.World);
        var sent = new List<uint>(entered.Count);
        foreach (uint trigger in entered)
        {
            // The handler may teleport the bot away; a far teleport leaves the map, so stop reporting on this one.
            if (!ReferenceEquals(player.Map, map) || !player.IsInWorld) break;
            AreaTriggerTeleport? teleport = maps.FindAreaTriggerTeleport(trigger);
            if (!MayTake(maps, player, tracker, trigger))
            {
                if (teleport is not null && !session.IsInChaosProbe)
                {
                    tracker.Refusal = new TeleportRefusal(trigger, string.Create(CultureInfo.InvariantCulture,
                        $"area trigger {trigger} to map {teleport.TargetMap} blocked by policy"), session.World.NowMs);
                }

                if (teleport is not null && !session.IsInChaosProbe
                    && (tracker.TeleportTarget is not null || (!player.IsAlive && player.Combat.Corpse is { } c && c.MapId != map.MapId)))
                {
                    PlayerbotAnomalies.Report(player, "nav", "areatrigger-failed",
                        string.Create(CultureInfo.InvariantCulture,
                            $"area trigger {trigger} to map {teleport.TargetMap} ({teleport.TargetX:F1},{teleport.TargetY:F1},{teleport.TargetZ:F1}) blocked by policy (target={tracker.TeleportTarget?.ToString(CultureInfo.InvariantCulture) ?? "none"}, alive={player.IsAlive})"),
                        AnomalySeverity.Warning);
                }

                continue;
            }

            Vector3 before = new(player.X, player.Y, player.Z);
            ManagedActionBudget? budget = session.ManagedBudget;
            session.ManagedBudget = null;
            bool dispatched;
            try { dispatched = session.TryManagedAction(WorldOpcode.CmsgAreatrigger, BitConverter.GetBytes(trigger)); }
            finally { session.ManagedBudget = budget; }
            sent.Add(trigger);
            if (teleport is not null && !session.IsInChaosProbe && !Teleported(session, player, map, before))
            {
                tracker.Refusal = new TeleportRefusal(trigger, string.Create(CultureInfo.InvariantCulture,
                    $"area trigger {trigger} to map {teleport.TargetMap} did not teleport the bot (dispatched={dispatched}, level={player.Level}/{teleport.RequiredLevel})"),
                    session.World.NowMs);
                PlayerbotAnomalies.Report(player, "nav", "areatrigger-failed",
                    string.Create(CultureInfo.InvariantCulture,
                        $"area trigger {trigger} to map {teleport.TargetMap} ({teleport.TargetX:F1},{teleport.TargetY:F1},{teleport.TargetZ:F1}) did not teleport bot (dispatched={dispatched}, level={player.Level}/{teleport.RequiredLevel}, alive={player.IsAlive})"),
                    AnomalySeverity.Warning);
            }
        }

        return sent;
    }

    private static bool Teleported(WorldSession session, Player player, Map map, Vector3 before)
    {
        if (!ReferenceEquals(player.Map, map) || !player.IsInWorld) return true;
        if (session.Services.GetService<TeleportFeature>()?.Teleports.IsBeingTeleported(player) == true) return true;
        return Vector3.DistanceSquared(new Vector3(player.X, player.Y, player.Z), before) > 0.25f;
    }

    /// <summary>Whether the bot reports <paramref name="trigger"/> (<see cref="PlayerbotMapPolicy.AllowsTrigger"/>).</summary>
    private static bool MayTake(WorldMaps maps, Player player, PlayerbotTriggerTracker tracker, uint trigger)
    {
        if (maps.FindAreaTriggerTeleport(trigger) is not { } teleport) return true;
        bool leadsToCorpse = !player.IsAlive && player.Combat.Corpse is { } corpse
            && PlayerbotMapPolicy.LeadsTo(maps.Registry, corpse.MapId, teleport.TargetMap);
        bool optedIn = tracker.TeleportsAllowed || tracker.TeleportTarget == teleport.TargetMap;
        return PlayerbotMapPolicy.AllowsTrigger(tracker.AllowedMaps, teleport.TargetMap, optedIn, leadsToCorpse);
    }
}

/// <summary>
/// The area triggers of one map in a coarse grid of <see cref="CellYards"/>-yard cells: each trigger is listed in every cell its
/// bounding circle touches, so <see cref="Near"/> returns a superset of the triggers that can contain a point, without a scan of
/// the whole map. A trigger too large for the grid is listed everywhere. Immutable once built.
/// </summary>
internal sealed class PlayerbotTriggerIndex
{
    /// <summary>The side of one grid cell.</summary>
    internal const float CellYards = 64f;

    /// <summary>A trigger whose bounding circle spans more cells than this per axis is listed in every cell instead.</summary>
    private const int MaxCellsPerAxis = 16;

    private readonly Dictionary<long, AreaTriggerTemplate[]> _cells = [];
    private readonly AreaTriggerTemplate[] _everywhere;

    internal PlayerbotTriggerIndex(IEnumerable<AreaTriggerTemplate> triggers)
    {
        All = [.. triggers.OrderBy(t => t.Id)];
        var everywhere = new List<AreaTriggerTemplate>();
        var cells = new Dictionary<long, List<AreaTriggerTemplate>>();
        foreach (AreaTriggerTemplate trigger in All)
        {
            float reach = BoundingRadius(trigger);
            if (!float.IsFinite(reach) || !float.IsFinite(trigger.X) || !float.IsFinite(trigger.Y))
            {
                everywhere.Add(trigger);
                continue;
            }

            int x0 = Cell(trigger.X - reach), x1 = Cell(trigger.X + reach);
            int y0 = Cell(trigger.Y - reach), y1 = Cell(trigger.Y + reach);
            if (x1 - x0 >= MaxCellsPerAxis || y1 - y0 >= MaxCellsPerAxis)
            {
                everywhere.Add(trigger);
                continue;
            }

            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    if (!cells.TryGetValue(Key(x, y), out List<AreaTriggerTemplate>? list)) cells[Key(x, y)] = list = [];
                    list.Add(trigger);
                }
            }
        }

        _everywhere = [.. everywhere];
        foreach ((long key, List<AreaTriggerTemplate> list) in cells)
            _cells[key] = [.. list.Concat(_everywhere).OrderBy(t => t.Id)];
    }

    /// <summary>Every trigger of the map, in id order.</summary>
    internal IReadOnlyList<AreaTriggerTemplate> All { get; }

    /// <summary>The triggers that may contain <paramref name="position"/> (no allocation).</summary>
    internal IReadOnlyList<AreaTriggerTemplate> Near(Vector3 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)) return [];
        return NearCell(Key(Cell(position.X), Cell(position.Y)));
    }

    internal IReadOnlyList<AreaTriggerTemplate> NearCell(long cellKey)
        => cellKey == long.MinValue ? [] : _cells.TryGetValue(cellKey, out AreaTriggerTemplate[]? here) ? here : _everywhere;

    internal static long CellKeyOf(Vector3 position)
        => float.IsFinite(position.X) && float.IsFinite(position.Y) ? Key(Cell(position.X), Cell(position.Y)) : long.MinValue;

    /// <summary>The radius of a sphere around the trigger's centre that holds the whole volume (a box: half its diagonal).</summary>
    internal static float BoundingRadius(AreaTriggerTemplate trigger)
        => trigger.Radius > 0 ? trigger.Radius
            : 0.5f * MathF.Sqrt((trigger.BoxX * trigger.BoxX) + (trigger.BoxY * trigger.BoxY) + (trigger.BoxZ * trigger.BoxZ));

    private static int Cell(float coordinate) => (int)MathF.Floor(coordinate / CellYards);

    private static long Key(int x, int y) => ((long)x << 32) | (uint)y;
}

/// <summary>
/// Which trigger volumes one bot is inside, and the entries a new position makes (pure; <see cref="PlayerbotAreaTriggers"/>).
/// The first position on a map (or after <see cref="Forget"/>) only records the volumes it is in. Also holds what the bot may
/// teleport to (<see cref="AllowedMaps"/>, <see cref="TeleportsAllowed"/>).
/// </summary>
internal sealed class PlayerbotTriggerTracker
{
    private HashSet<uint> _inside = [];
    private HashSet<uint> _next = [];
    private readonly List<uint> _entered = [];
    private object? _map;
    private WorldRuntime? _nearWorld;
    private WorldMaps? _nearMaps;
    private object? _nearMap;
    private IReadOnlyCollection<AreaTriggerTemplate>? _nearSource;
    private long _nearCellKey = long.MinValue;
    private IReadOnlyList<AreaTriggerTemplate> _nearTriggers = [];

    internal IReadOnlyCollection<uint> Inside => _inside;

    /// <summary>The bot's <see cref="PlayerbotOptions.AllowedMaps"/> (set when a route starts); null: none known yet.</summary>
    internal uint[]? AllowedMaps { get; set; }

    /// <summary>A controller lets the bot take teleports off <see cref="AllowedMaps"/> (<see cref="PlayerbotAreaTriggers.AllowTeleports"/>).</summary>
    internal bool TeleportsAllowed { get; set; }

    /// <summary>The one map whose teleports the bot may take besides (<see cref="PlayerbotAreaTriggers.AllowTeleportsTo"/>); null: none.</summary>
    internal uint? TeleportTarget { get; set; }

    /// <summary>The latest teleport trigger that did not take the bot (<see cref="PlayerbotAreaTriggers.TryConsumeTeleportRefusal(Player, out PlayerbotAreaTriggers.TeleportRefusal)"/>).</summary>
    internal PlayerbotAreaTriggers.TeleportRefusal? Refusal { get; set; }

    internal bool Knows(object map) => ReferenceEquals(_map, map);

    internal void Forget()
    {
        _map = null;
        _nearMap = null;
        _inside.Clear();
    }

    internal WorldMaps MapsOf(WorldRuntime world)
    {
        if (ReferenceEquals(_nearWorld, world) && _nearMaps is { } cached) return cached;
        _nearWorld = world;
        return _nearMaps = WorldMaps.Of(world);
    }

    internal IReadOnlyList<AreaTriggerTemplate> Near(WorldRuntime world, Map map, Vector3 position)
    {
        long cellKey = PlayerbotTriggerIndex.CellKeyOf(position);
        if (cellKey == long.MinValue) return [];
        WorldMaps maps = MapsOf(world);
        IReadOnlyCollection<AreaTriggerTemplate> source = maps.AreaTriggers;
        if (ReferenceEquals(_nearMap, map) && ReferenceEquals(_nearSource, source) && _nearCellKey == cellKey)
            return _nearTriggers;
        _nearMap = map;
        _nearSource = source;
        _nearCellKey = cellKey;
        return _nearTriggers = PlayerbotAreaTriggers.Index(maps, map.MapId).NearCell(cellKey);
    }

    /// <summary>Whether <paramref name="position"/> lies in a volume of <paramref name="triggers"/> the bot is not inside.</summary>
    internal bool WouldEnter(object map, uint mapId, Vector3 position, IReadOnlyList<AreaTriggerTemplate> triggers)
    {
        if (!ReferenceEquals(_map, map)) return false;
        for (int i = 0; i < triggers.Count; i++)
        {
            if (!_inside.Contains(triggers[i].Id) && Contains(triggers[i], mapId, position)) return true;
        }

        return false;
    }

    /// <summary>
    /// Move to <paramref name="position"/>: the triggers entered (in id order; none when <paramref name="fire"/> is false or this
    /// is the first position on <paramref name="map"/>); triggers left are forgotten, so walking back in fires them again.
    /// <paramref name="triggers"/> must hold every volume that can contain the position (it may hold more). Allocates only when
    /// a trigger is entered.
    /// </summary>
    internal IReadOnlyList<uint> Observe(object map, uint mapId, Vector3 position, IReadOnlyList<AreaTriggerTemplate> triggers, bool fire)
    {
        bool first = !ReferenceEquals(_map, map);
        if (first)
        {
            _map = map;
            _inside.Clear();
        }

        _entered.Clear();
        _next.Clear();
        for (int i = 0; i < triggers.Count; i++)
        {
            AreaTriggerTemplate trigger = triggers[i];
            if (!Contains(trigger, mapId, position)) continue;
            _next.Add(trigger.Id);
            if (!_inside.Contains(trigger.Id) && fire && !first) _entered.Add(trigger.Id);
        }

        (_inside, _next) = (_next, _inside);
        if (_entered.Count == 0) return [];
        _entered.Sort();
        return [.. _entered];
    }

    /// <summary>
    /// The client's own test: the exact volume, without the server's 5-yard lag tolerance. A point outside the trigger's bounding
    /// sphere is rejected before the box's rotation is worked out.
    /// </summary>
    internal static bool Contains(AreaTriggerTemplate trigger, uint mapId, Vector3 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) || trigger.MapId != mapId) return false;
        float reach = PlayerbotTriggerIndex.BoundingRadius(trigger);
        float dx = position.X - trigger.X, dy = position.Y - trigger.Y, dz = position.Z - trigger.Z;
        if (float.IsFinite(reach) && (dx * dx) + (dy * dy) + (dz * dz) > (reach * reach) + 0.01f) return false;
        return AreaTriggerZone.Contains(trigger, mapId, position.X, position.Y, position.Z);
    }
}
