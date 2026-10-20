using System.Globalization;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Live;

/// <summary>An entity with its recent positions, as <see cref="LiveStore.Snapshot"/> returns it.</summary>
/// <param name="Entity">The latest state.</param>
/// <param name="Trail">Recent positions, oldest first, newest last; null when trails were not asked for.</param>
public sealed record StoredEntity(LiveEntity Entity, IReadOnlyList<WorldPoint>? Trail);

/// <summary>Frame and entity counts of one map/instance, as <see cref="LiveStore.Summaries"/> returns them.</summary>
/// <param name="Map">Map id.</param>
/// <param name="Instance">Instance id.</param>
/// <param name="Seq">Sequence number of the latest frame.</param>
/// <param name="ClockMs">World clock of the latest frame.</param>
/// <param name="Entities">Entities in the latest frame.</param>
/// <param name="ByKind">Entities per kind, indexed by <see cref="LiveKind"/>.</param>
/// <param name="Grids">Loaded grids in the latest frame.</param>
/// <param name="Counters">The server's counters of the latest frame.</param>
/// <param name="AgeMs">Milliseconds since the latest frame was read.</param>
/// <param name="MissedFrames">Frames the server numbered but this connection never saw (it drops frames to a slow reader).</param>
public sealed record MapSummary(
    uint Map,
    uint Instance,
    long Seq,
    long ClockMs,
    int Entities,
    int[] ByKind,
    int Grids,
    JsonObject? Counters,
    double AgeMs,
    long MissedFrames);

/// <summary>Filter criteria for <c>live.find</c>.</summary>
/// <param name="Map">Optional map id filter.</param>
/// <param name="Query">Optional text, guid, or entry query.</param>
/// <param name="Kind">Optional entity kind filter.</param>
/// <param name="InCombat">Optional combat state filter.</param>
/// <param name="Limit">Maximum entities to return (<c>1..500</c>).</param>
public sealed record LiveFindFilter(
    uint? Map = null,
    string? Query = null,
    LiveKind? Kind = null,
    bool? InCombat = null,
    int Limit = 50);

/// <summary>Result of <c>live.find</c>.</summary>
/// <param name="Entities">Matched entities up to the requested limit.</param>
/// <param name="TotalMatched">Total active entities matching the filter before truncation.</param>
public sealed record LiveFindResult(IReadOnlyList<LiveEntity> Entities, int TotalMatched)
{
    /// <summary>Number of entities returned in <see cref="Entities"/>.</summary>
    public int Count => Entities.Count;

    /// <summary>The find result as JSON.</summary>
    public JsonObject ToJson(bool connected = false)
    {
        var array = new JsonArray();
        foreach (LiveEntity entity in Entities)
        {
            JsonObject item = entity.ToJson();
            item["inCombat"] = LiveStore.IsInCombat(entity);
            array.Add(item);
        }

        return new JsonObject
        {
            ["connected"] = connected,
            ["count"] = Entities.Count,
            ["matched"] = TotalMatched,
            ["returned"] = Entities.Count,
            ["truncated"] = TotalMatched > Entities.Count,
            ["entities"] = array,
        };
    }
}

/// <summary>One spatial activity cell returned by <c>live.hotspots</c>.</summary>
/// <param name="Map">Map id of the cell.</param>
/// <param name="CellX">Cell X index (<c>floor(x / cellSize)</c>).</param>
/// <param name="CellY">Cell Y index (<c>floor(y / cellSize)</c>).</param>
/// <param name="CellSize">Edge length of the cell in yards.</param>
/// <param name="TotalCount">Total entities in the cell.</param>
/// <param name="PlayerCount">Players (and bots) in the cell.</param>
/// <param name="CreatureCount">Creatures in the cell.</param>
/// <param name="InCombatCount">Entities currently in combat in the cell.</param>
/// <param name="Centroid">Average world position <c>{x, y, z}</c> of the entities in the cell.</param>
public sealed record LiveHotspot(
    uint Map,
    int CellX,
    int CellY,
    float CellSize,
    int TotalCount,
    int PlayerCount,
    int CreatureCount,
    int InCombatCount,
    WorldPoint Centroid)
{
    /// <summary>The hotspot as JSON.</summary>
    public JsonObject ToJson() => new()
    {
        ["map"] = Map,
        ["cell"] = new JsonArray(CellX, CellY),
        ["cellX"] = CellX,
        ["cellY"] = CellY,
        ["cellSize"] = Math.Round((double)CellSize, 3),
        ["totalCount"] = TotalCount,
        ["playerCount"] = PlayerCount,
        ["creatureCount"] = CreatureCount,
        ["inCombatCount"] = InCombatCount,
        ["centroid"] = new JsonObject
        {
            ["x"] = Math.Round(Centroid.X, 3),
            ["y"] = Math.Round(Centroid.Y, 3),
            ["z"] = Math.Round(Centroid.Z, 3),
        },
        ["at"] = LiveJson.Point(new WorldPoint(Math.Round(Centroid.X, 3), Math.Round(Centroid.Y, 3), Math.Round(Centroid.Z, 3))),
    };
}

/// <summary>
/// What the feed last said: the newest frame of every map/instance, every entity by guid, and a ring of the last
/// <see cref="TrailLength"/> positions of each entity. A frame replaces its map/instance wholesale (an entity absent from the newest
/// frame is gone), so the store never shows an entity the server stopped sending.
/// <para>
/// Threading: one writer (the connection's reader loop) and any number of readers. Everything is guarded by one lock held only for dictionary
/// work; entities and frames are immutable, so readers keep their snapshot after the lock is released. Memory is bounded:
/// <see cref="MaxEntitiesPerMap"/> entities and <see cref="TrailLength"/> trail points each, and at most <see cref="MaxMaps"/> map/instances.
/// </para>
/// </summary>
public sealed class LiveStore
{
    /// <summary>Trail positions kept per entity.</summary>
    public const int TrailLength = 64;

    /// <summary>Entities kept per map/instance (the server caps a frame at 4096 by default); excess entities of a frame are dropped.</summary>
    public const int MaxEntitiesPerMap = 16_384;

    /// <summary>
    /// Map/instances kept; the least recently updated is dropped beyond this. A connection serves one subscription, so a handful is
    /// ample; the cap also bounds the worst case (8 x 16384 entities x a 768-byte trail ring, about 100 MB) if a server sends unasked.
    /// </summary>
    public const int MaxMaps = 8;

    private readonly Lock _gate = new();
    private readonly Dictionary<(uint Map, uint Instance), MapState> _maps = [];
    private long _dropped;

    /// <summary>Entities of the newest frames that were dropped because a map/instance already held <see cref="MaxEntitiesPerMap"/>.</summary>
    public long DroppedForCapacity
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    /// <summary>Total entities held.</summary>
    public int EntityCount
    {
        get
        {
            lock (_gate)
            {
                return _maps.Values.Sum(m => m.Entities.Count);
            }
        }
    }

    /// <summary>Makes <paramref name="frame"/> the newest of its map/instance. Returns the number of frames missed since the previous one.</summary>
    public long Apply(LiveFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            (uint, uint) key = (frame.Map, frame.Instance);
            if (!_maps.TryGetValue(key, out MapState? state))
            {
                if (_maps.Count >= MaxMaps)
                {
                    EvictOldest();
                }

                state = new MapState();
                _maps[key] = state;
            }

            long missed = 0;
            if (state.HasSeq && frame.Seq > state.Frame!.Seq + 1)
            {
                missed = frame.Seq - state.Frame.Seq - 1;
                state.MissedFrames += missed;
            }

            state.HasSeq = frame.Seq > 0 || state.HasSeq;
            state.Generation++;
            foreach (LiveEntity entity in frame.Entities)
            {
                if (state.Entities.TryGetValue(entity.Id, out Record? record))
                {
                    record.Update(entity, state.Generation);
                }
                else if (state.Entities.Count < MaxEntitiesPerMap)
                {
                    state.Entities[entity.Id] = new Record(entity, state.Generation);
                }
                else
                {
                    _dropped++;
                }
            }

            foreach (KeyValuePair<ulong, Record> pair in state.Entities)
            {
                if (pair.Value.Generation != state.Generation)
                {
                    state.Entities.Remove(pair.Key);
                }
            }

            state.Frame = frame;
            return missed;
        }
    }

    /// <summary>
    /// Forgets the sequence position of a map/instance, for when it is subscribed again: the server may restart its numbering, and the
    /// first frame after must not be counted as a gap.
    /// </summary>
    public void ResetSequence(uint map, uint instance)
    {
        lock (_gate)
        {
            if (_maps.TryGetValue((map, instance), out MapState? state))
            {
                state.HasSeq = false;
            }
        }
    }

    /// <summary>
    /// Keeps only the state of one map/instance and drops the rest: the feed serves a connection one map at a time, so once the
    /// subscription moved the entities of the old map are no longer being updated and must not be shown as current.
    /// </summary>
    public void Retain(uint map, uint instance)
    {
        lock (_gate)
        {
            foreach ((uint Map, uint Instance) key in _maps.Keys.ToList())
            {
                if (key.Map != map || key.Instance != instance)
                {
                    _maps.Remove(key);
                }
            }
        }
    }

    /// <summary>Drops everything (a disconnect: stale positions must not outlive their connection).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _maps.Clear();
        }
    }

    /// <summary>The newest frame of a map/instance, or null.</summary>
    public LiveFrame? Latest(uint map, uint instance)
    {
        lock (_gate)
        {
            return _maps.TryGetValue((map, instance), out MapState? state) ? state.Frame : null;
        }
    }

    /// <summary>Per-map/instance summaries, ordered by map then instance.</summary>
    public IReadOnlyList<MapSummary> Summaries()
    {
        lock (_gate)
        {
            var list = new List<MapSummary>(_maps.Count);
            foreach (((uint map, uint instance), MapState state) in _maps.OrderBy(p => p.Key.Map).ThenBy(p => p.Key.Instance))
            {
                var byKind = new int[LiveKinds.Names.Count];
                foreach (Record record in state.Entities.Values)
                {
                    byKind[(int)record.Entity.Kind]++;
                }

                LiveFrame frame = state.Frame!;
                list.Add(new MapSummary(
                    map,
                    instance,
                    frame.Seq,
                    frame.ClockMs,
                    state.Entities.Count,
                    byKind,
                    frame.Grids.Count,
                    (JsonObject?)frame.Counters?.DeepClone(),
                    Stopwatch.GetElapsedTime(frame.ReceivedTimestamp).TotalMilliseconds,
                    state.MissedFrames));
            }

            return list;
        }
    }

    /// <summary>The latest state of an entity. When a teleporting entity is briefly in two maps, the more recently updated one wins.</summary>
    public bool TryGetEntity(ulong id, [NotNullWhen(true)] out LiveEntity? entity)
    {
        lock (_gate)
        {
            entity = Find(id)?.Entity;
            return entity is not null;
        }
    }

    /// <summary>An entity's recent positions, oldest first (empty when the entity is unknown).</summary>
    public IReadOnlyList<WorldPoint> Trail(ulong id)
    {
        lock (_gate)
        {
            return Find(id)?.Trail() ?? [];
        }
    }

    /// <summary>The entities passing <paramref name="where"/> (all when null), ordered by guid, each with its trail when asked for.</summary>
    public IReadOnlyList<StoredEntity> Snapshot(Func<LiveEntity, bool>? where = null, bool withTrails = false)
    {
        lock (_gate)
        {
            var list = new List<StoredEntity>();
            foreach (MapState state in _maps.Values)
            {
                foreach (Record record in state.Entities.Values)
                {
                    if (where is null || where(record.Entity))
                    {
                        list.Add(new StoredEntity(record.Entity, withTrails ? record.Trail() : null));
                    }
                }
            }

            list.Sort((a, b) => a.Entity.Id.CompareTo(b.Entity.Id));
            return list;
        }
    }
    /// <summary>Whether <paramref name="entity"/> is considered to be in combat (has a non-zero target or a combat motion such as chase or flee).</summary>
    public static bool IsInCombat(LiveEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.Target is > 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(entity.Motion))
        {
            return false;
        }

        return entity.Motion.Contains("chase", StringComparison.OrdinalIgnoreCase)
            || entity.Motion.Contains("flee", StringComparison.OrdinalIgnoreCase)
            || entity.Motion.Contains("combat", StringComparison.OrdinalIgnoreCase)
            || entity.Motion.Contains("attack", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Searches active entities in the store matching <paramref name="filter"/>: substring match on <c>Name</c>, exact match on <c>Guid</c>
    /// or <c>Entry</c> when <c>Query</c> parses as a uint/ulong, and filters by <c>Kind</c>, <c>Map</c>, and <c>InCombat</c>.
    /// </summary>
    public LiveFindResult FindEntities(LiveFindFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        int limit = Math.Clamp(filter.Limit, 1, 500);
        string? q = string.IsNullOrWhiteSpace(filter.Query) ? null : filter.Query.Trim();
        bool hasEntryQuery = false;
        uint entryQuery = 0;
        bool hasGuidDecQuery = false;
        ulong guidDecQuery = 0;
        bool hasGuidHexQuery = false;
        ulong guidHexQuery = 0;

        if (q is not null)
        {
            hasEntryQuery = uint.TryParse(q, NumberStyles.Integer, CultureInfo.InvariantCulture, out entryQuery);
            hasGuidDecQuery = ulong.TryParse(q, NumberStyles.Integer, CultureInfo.InvariantCulture, out guidDecQuery) && guidDecQuery != 0;
            hasGuidHexQuery = LiveGuid.TryParse(q, out guidHexQuery) && guidHexQuery != 0;
        }

        IReadOnlyList<StoredEntity> matched = Snapshot(e =>
        {
            if (filter.Map is { } m && e.Map != m)
            {
                return false;
            }

            if (filter.Kind is { } k && e.Kind != k)
            {
                return false;
            }

            if (filter.InCombat is { } ic && IsInCombat(e) != ic)
            {
                return false;
            }

            if (q is not null)
            {
                bool nameHit = e.Name is not null && e.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
                bool guidHit = (hasGuidHexQuery && e.Id == guidHexQuery) || (hasGuidDecQuery && e.Id == guidDecQuery);
                bool entryHit = hasEntryQuery && e.Entry != 0 && e.Entry == entryQuery;
                if (!nameHit && !guidHit && !entryHit)
                {
                    return false;
                }
            }

            return true;
        });

        var results = new List<LiveEntity>(Math.Min(limit, matched.Count));
        for (int i = 0; i < matched.Count && i < limit; i++)
        {
            results.Add(matched[i].Entity);
        }

        return new LiveFindResult(results, matched.Count);
    }

    /// <summary>
    /// Buckets live entities by <c>(map, floor(x / cellSize), floor(y / cellSize))</c> and returns the top <paramref name="limit"/>
    /// cells ranked by activity (<c>inCombatCount</c>, <c>playerCount</c>, <c>totalCount</c>, <c>creatureCount</c>).
    /// </summary>
    public IReadOnlyList<LiveHotspot> Hotspots(uint? map = null, float cellSize = 120f, int limit = 10)
    {
        float clampedSize = float.IsFinite(cellSize) ? Math.Clamp(cellSize, 16f, 2000f) : 120f;
        int clampedLimit = Math.Clamp(limit, 1, 100);

        IReadOnlyList<StoredEntity> snapshot = Snapshot(map is { } m ? e => e.Map == m : null);
        if (snapshot.Count == 0)
        {
            return [];
        }

        var buckets = new Dictionary<(uint Map, int CellX, int CellY), HotspotAccumulator>();
        foreach (StoredEntity stored in snapshot)
        {
            LiveEntity e = stored.Entity;
            int cx = (int)Math.Floor(e.Position.X / clampedSize);
            int cy = (int)Math.Floor(e.Position.Y / clampedSize);
            (uint, int, int) key = (e.Map, cx, cy);
            if (!buckets.TryGetValue(key, out HotspotAccumulator acc))
            {
                acc = default;
            }

            acc.TotalCount++;
            if (e.Kind is LiveKind.Player or LiveKind.Bot)
            {
                acc.PlayerCount++;
            }
            else if (e.Kind == LiveKind.Creature)
            {
                acc.CreatureCount++;
            }

            if (IsInCombat(e))
            {
                acc.InCombatCount++;
            }

            acc.SumX += e.Position.X;
            acc.SumY += e.Position.Y;
            acc.SumZ += e.Position.Z;
            buckets[key] = acc;
        }

        var hotspots = new List<LiveHotspot>(buckets.Count);
        foreach (((uint cellMap, int cx, int cy), HotspotAccumulator acc) in buckets)
        {
            var centroid = new WorldPoint(
                acc.SumX / acc.TotalCount,
                acc.SumY / acc.TotalCount,
                acc.SumZ / acc.TotalCount);
            hotspots.Add(new LiveHotspot(
                cellMap,
                cx,
                cy,
                clampedSize,
                acc.TotalCount,
                acc.PlayerCount,
                acc.CreatureCount,
                acc.InCombatCount,
                centroid));
        }

        hotspots.Sort(static (a, b) =>
        {
            int c = b.InCombatCount.CompareTo(a.InCombatCount);
            if (c != 0)
            {
                return c;
            }

            c = b.PlayerCount.CompareTo(a.PlayerCount);
            if (c != 0)
            {
                return c;
            }

            c = b.TotalCount.CompareTo(a.TotalCount);
            if (c != 0)
            {
                return c;
            }

            c = b.CreatureCount.CompareTo(a.CreatureCount);
            if (c != 0)
            {
                return c;
            }

            c = a.Map.CompareTo(b.Map);
            if (c != 0)
            {
                return c;
            }

            c = a.CellX.CompareTo(b.CellX);
            return c != 0 ? c : a.CellY.CompareTo(b.CellY);
        });

        if (hotspots.Count > clampedLimit)
        {
            hotspots.RemoveRange(clampedLimit, hotspots.Count - clampedLimit);
        }

        return hotspots;
    }


    private Record? Find(ulong id)
    {
        Record? best = null;
        long bestStamp = long.MinValue;
        foreach (MapState state in _maps.Values)
        {
            if (state.Entities.TryGetValue(id, out Record? record) && state.Frame!.ReceivedTimestamp >= bestStamp)
            {
                best = record;
                bestStamp = state.Frame.ReceivedTimestamp;
            }
        }

        return best;
    }

    private void EvictOldest()
    {
        (uint, uint)? oldest = null;
        long oldestStamp = long.MaxValue;
        foreach (KeyValuePair<(uint Map, uint Instance), MapState> pair in _maps)
        {
            long stamp = pair.Value.Frame?.ReceivedTimestamp ?? 0;
            if (stamp < oldestStamp)
            {
                oldestStamp = stamp;
                oldest = pair.Key;
            }
        }

        if (oldest is { } key)
        {
            _maps.Remove(key);
        }
    }

    private sealed class MapState
    {
        public Dictionary<ulong, Record> Entities { get; } = [];

        public LiveFrame? Frame { get; set; }

        public long Generation { get; set; }

        public bool HasSeq { get; set; }

        public long MissedFrames { get; set; }
    }

    private sealed class Record
    {
        private readonly TrailPoint[] _ring = new TrailPoint[TrailLength];
        private int _next;
        private int _count;

        public Record(LiveEntity entity, long generation)
        {
            Entity = entity;
            Generation = generation;
            Add(entity.Position);
        }

        public LiveEntity Entity { get; private set; }

        public long Generation { get; private set; }

        public void Update(LiveEntity entity, long generation)
        {
            if (entity.Position != Entity.Position)
            {
                Add(entity.Position);
            }

            Entity = entity;
            Generation = generation;
        }

        public WorldPoint[] Trail()
        {
            var points = new WorldPoint[_count];
            int first = (_next - _count + TrailLength) % TrailLength;
            for (int i = 0; i < _count; i++)
            {
                TrailPoint p = _ring[(first + i) % TrailLength];
                points[i] = new WorldPoint(p.X, p.Y, p.Z);
            }

            return points;
        }

        private void Add(WorldPoint position)
        {
            _ring[_next] = new TrailPoint((float)position.X, (float)position.Y, (float)position.Z);
            _next = (_next + 1) % TrailLength;
            _count = Math.Min(_count + 1, TrailLength);
        }
    }

    private readonly record struct TrailPoint(float X, float Y, float Z);
    private struct HotspotAccumulator
    {
        public int TotalCount;
        public int PlayerCount;
        public int CreatureCount;
        public int InCombatCount;
        public double SumX;
        public double SumY;
        public double SumZ;
    }
}
