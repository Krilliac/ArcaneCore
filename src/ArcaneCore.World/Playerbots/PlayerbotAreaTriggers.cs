using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Area triggers the way a 1.12 client reports them: while the bot moves along a route (<see cref="PlayerbotMotion"/>), entering
/// an <c>areatrigger_template</c> volume on its map — a sphere, or a box turned by its orientation (<see cref="AreaTriggerZone"/>,
/// vmangos <c>IsPointInAreaTriggerZone</c>) — sends CMSG_AREATRIGGER once; the trigger fires again only after the bot has left the
/// volume and come back. The server half is the ordinary handler (<c>TeleportHandlers.HandleAreaTrigger</c>, vmangos
/// MiscHandler.cpp:653-800 with the ghost rules of MiscHandler.cpp:712-756), which checks the position again with its 5-yard lag
/// tolerance, so the bot reports where it is (a heartbeat) right before the trigger.
/// <para>
/// A bot that arrives inside a volume without walking into it (a teleport, a login, a scenario placement) does not fire it: the
/// first position seen on a map only sets what the bot is inside. That keeps a bot landing at a dungeon exit, beside the entrance
/// box, from bouncing straight back in.
/// </para>
/// <para>The triggers of each map are cached per <see cref="WorldMaps"/> and rebuilt when the trigger table is reloaded.</para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
internal static class PlayerbotAreaTriggers
{
    private static readonly ConditionalWeakTable<WorldMaps, MapCache> Caches = [];
    private static readonly ConditionalWeakTable<Player, PlayerbotTriggerTracker> Trackers = [];

    private sealed class MapCache
    {
        internal IReadOnlyCollection<AreaTriggerTemplate>? Source;
        internal readonly Dictionary<uint, AreaTriggerTemplate[]> ByMap = [];
    }

    /// <summary>The area triggers on <paramref name="mapId"/> (cached per map; rebuilt after a reload of the trigger table).</summary>
    internal static IReadOnlyList<AreaTriggerTemplate> OnMap(WorldMaps maps, uint mapId)
    {
        MapCache cache = Caches.GetValue(maps, _ => new MapCache());
        IReadOnlyCollection<AreaTriggerTemplate> source = maps.AreaTriggers;
        if (!ReferenceEquals(cache.Source, source))
        {
            cache.ByMap.Clear();
            cache.Source = source;
        }

        if (!cache.ByMap.TryGetValue(mapId, out AreaTriggerTemplate[]? triggers))
        {
            triggers = [.. source.Where(t => t.MapId == mapId).OrderBy(t => t.Id)];
            cache.ByMap[mapId] = triggers;
        }

        return triggers;
    }

    /// <summary>Forget what the bot is inside (teleport, death): the next position seen sets it again without firing.</summary>
    internal static void Reset(Player player)
    {
        if (Trackers.TryGetValue(player, out PlayerbotTriggerTracker? tracker)) tracker.Forget();
    }

    /// <summary>
    /// A route starts at <paramref name="position"/>: when nothing is known for this map yet, note what the bot stands in (no
    /// trigger fires), so a volume it walks into from here is an entry.
    /// </summary>
    internal static void Begin(WorldSession session, Player player, Map map, Vector3 position)
    {
        PlayerbotTriggerTracker tracker = Trackers.GetValue(player, _ => new PlayerbotTriggerTracker());
        if (!tracker.Knows(map)) tracker.Observe(map, map.MapId, position, OnMap(WorldMaps.Of(session.World), map.MapId), fire: false);
    }

    /// <summary>Whether moving to <paramref name="position"/> enters a trigger the bot is not in (nothing changes).</summary>
    internal static bool WouldEnter(WorldSession session, Player player, Map map, Vector3 position)
        => Trackers.TryGetValue(player, out PlayerbotTriggerTracker? tracker)
            && tracker.WouldEnter(map, map.MapId, position, OnMap(WorldMaps.Of(session.World), map.MapId));

    /// <summary>
    /// The bot reported <paramref name="position"/>: send CMSG_AREATRIGGER for each volume it has just entered (never refused
    /// for lack of the shared action budget: a client always reports its own triggers) and note the ones it left. Returns the
    /// triggers sent.
    /// </summary>
    internal static IReadOnlyList<uint> Update(WorldSession session, Player player, Map map, Vector3 position)
    {
        PlayerbotTriggerTracker tracker = Trackers.GetValue(player, _ => new PlayerbotTriggerTracker());
        IReadOnlyList<uint> entered = tracker.Observe(map, map.MapId, position, OnMap(WorldMaps.Of(session.World), map.MapId), fire: true);
        foreach (uint trigger in entered)
        {
            // The handler may teleport the bot away; a far teleport leaves the map, so stop reporting on this one.
            if (!ReferenceEquals(player.Map, map) || !player.IsInWorld) break;
            ManagedActionBudget? budget = session.ManagedBudget;
            session.ManagedBudget = null;
            try { session.TryManagedAction(WorldOpcode.CmsgAreatrigger, BitConverter.GetBytes(trigger)); }
            finally { session.ManagedBudget = budget; }
        }

        return entered;
    }
}

/// <summary>
/// Which trigger volumes one bot is inside, and the entries a new position makes (pure; <see cref="PlayerbotAreaTriggers"/>).
/// The first position on a map (or after <see cref="Forget"/>) only records the volumes it is in.
/// </summary>
internal sealed class PlayerbotTriggerTracker
{
    private readonly HashSet<uint> _inside = [];
    private object? _map;

    internal IReadOnlyCollection<uint> Inside => _inside;

    internal bool Knows(object map) => ReferenceEquals(_map, map);

    internal void Forget()
    {
        _map = null;
        _inside.Clear();
    }

    /// <summary>Whether <paramref name="position"/> lies in a volume of <paramref name="triggers"/> the bot is not inside.</summary>
    internal bool WouldEnter(object map, uint mapId, Vector3 position, IEnumerable<AreaTriggerTemplate> triggers)
    {
        if (!ReferenceEquals(_map, map)) return false;
        foreach (AreaTriggerTemplate trigger in triggers)
            if (!_inside.Contains(trigger.Id) && Contains(trigger, mapId, position)) return true;
        return false;
    }

    /// <summary>
    /// Move to <paramref name="position"/>: the triggers entered (in id order; none when <paramref name="fire"/> is false or this
    /// is the first position on <paramref name="map"/>); triggers left are forgotten, so walking back in fires them again.
    /// </summary>
    internal IReadOnlyList<uint> Observe(object map, uint mapId, Vector3 position, IEnumerable<AreaTriggerTemplate> triggers, bool fire)
    {
        bool first = !ReferenceEquals(_map, map);
        if (first)
        {
            _map = map;
            _inside.Clear();
        }

        var entered = new List<uint>();
        var now = new HashSet<uint>();
        foreach (AreaTriggerTemplate trigger in triggers)
        {
            if (!Contains(trigger, mapId, position)) continue;
            now.Add(trigger.Id);
            if (!_inside.Contains(trigger.Id) && fire && !first) entered.Add(trigger.Id);
        }

        _inside.Clear();
        _inside.UnionWith(now);
        entered.Sort();
        return entered;
    }

    /// <summary>The client's own test: the exact volume, without the server's 5-yard lag tolerance.</summary>
    internal static bool Contains(AreaTriggerTemplate trigger, uint mapId, Vector3 position)
        => float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z)
            && AreaTriggerZone.Contains(trigger, mapId, position.X, position.Y, position.Z);
}
