namespace ArcaneCore.Kernel.WorldData.Pools;

/// <summary>A <c>pool_template</c> row.</summary>
public sealed record PoolTemplateData(uint Entry, uint MaxLimit, string Description);

/// <summary>A <c>pool_creature</c> / <c>pool_gameobject</c> row (a spawn guid), or a <c>pool_*_template</c> row (an entry: every spawn of it).</summary>
public sealed record PoolSpawnLink(uint Key, uint Pool, float Chance, string Description = "");

/// <summary>A <c>pool_pool</c> row: <paramref name="Child"/> is one of <paramref name="Mother"/>'s members.</summary>
public sealed record PoolPoolLink(uint Child, uint Mother, float Chance, string Description = "");

/// <summary>One member of a pool group: a database spawn guid, or a child pool id (cmangos PoolObject).</summary>
/// <param name="Id">The spawn guid or the child pool id.</param>
/// <param name="Chance">Percent (the absolute value of the row's chance).</param>
public sealed record PoolMember(uint Id, float Chance);

/// <summary>
/// One pool as cmangos PoolManager::LoadFromDB leaves it (Pools/PoolManager.cpp:598-1046; no code copied): its template, the spawns and child
/// pools that survived the load checks, each split into the explicitly chanced and the equally chanced list (PoolGroup::AddEntry: a non-zero
/// chance is explicit only when the pool's <c>max_limit</c> is 1), and whether it spawns by itself when a map is created (AutoSpawn).
/// </summary>
public sealed record PoolDefinition
{
    public required uint Id { get; init; }

    /// <summary>The most members (spawns and child pools together) in the world at once; 0 without a <c>pool_template</c> row.</summary>
    public uint MaxLimit { get; init; }

    public string Description { get; init; } = string.Empty;

    /// <summary>Whether a <c>pool_template</c> row exists (a pool named only by its members has MaxLimit 0 and never spawns).</summary>
    public bool HasTemplate { get; init; }

    /// <summary>The mother pool (<c>pool_pool.mother_pool</c>), or 0.</summary>
    public uint Mother { get; init; }

    /// <summary>The map of the pool's spawns (its children's included); null when it has none.</summary>
    public uint? MapId { get; init; }

    /// <summary>
    /// cmangos AutoSpawn: a template row, not a child pool, and chances that can pick (CheckPool). A pool whose spawns are listed in a game
    /// event is not auto-spawned in cmangos either; here the event gate refuses those spawns outside their event, which has the same effect.
    /// </summary>
    public bool AutoSpawn { get; init; }

    public IReadOnlyList<PoolMember> ExplicitlyChanced { get; init; } = [];

    public IReadOnlyList<PoolMember> EqualChanced { get; init; } = [];

    public IReadOnlyList<PoolMember> ExplicitlyChancedPools { get; init; } = [];

    public IReadOnlyList<PoolMember> EqualChancedPools { get; init; } = [];

    /// <summary>The spawns of the pool itself (not its children's).</summary>
    public IEnumerable<PoolMember> Spawns => ExplicitlyChanced.Concat(EqualChanced);

    /// <summary>The child pools.</summary>
    public IEnumerable<PoolMember> Children => ExplicitlyChancedPools.Concat(EqualChancedPools);
}

/// <summary>
/// The pools of one spawn kind (creatures, or game objects), read-only after load: which pool a database spawn belongs to and what each pool
/// holds. classic-db z2815 never mixes creatures and game objects in one pool tree, so each kind's content carries its own catalog; a
/// mixed tree would count each kind's members against the shared max_limit separately (docs/areas/content-import.md, pools).
/// <para>
/// <see cref="IsPooled"/> answers what cmangos ObjectMgr::LoadCreatures / LoadGameObjects answer with their <c>pool_*</c> joins: a spawn named
/// by any pool row (or whose entry is) is never placed in its grid by itself, even when PoolManager later drops the row (a pool with no
/// template: classic-db's 191 Eastern Plaguelands Plaguebloom spawns of pool 8648). Only its pool brings it.
/// </para>
/// </summary>
public sealed class PoolCatalog
{
    public static readonly PoolCatalog Empty = new([], [], new HashSet<uint>(), []);

    private readonly Dictionary<uint, PoolDefinition> _pools;
    private readonly Dictionary<uint, uint> _poolOfSpawn;
    private readonly HashSet<uint> _pooled;

    private PoolCatalog(Dictionary<uint, PoolDefinition> pools, Dictionary<uint, uint> poolOfSpawn, HashSet<uint> pooled, IReadOnlyList<string> issues)
    {
        _pools = pools;
        _poolOfSpawn = poolOfSpawn;
        _pooled = pooled;
        Issues = issues;
    }

    /// <summary>Pools with at least one spawn or child (and every template), by id.</summary>
    public int Count => _pools.Count;

    public IEnumerable<PoolDefinition> Pools => _pools.Values;

    /// <summary>The rows the load dropped, one line each (cmangos logs them as database errors).</summary>
    public IReadOnlyList<string> Issues { get; }

    public PoolDefinition? Find(uint poolId) => _pools.GetValueOrDefault(poolId);

    /// <summary>The pool a spawn is a member of after the load checks, or 0.</summary>
    public uint PoolOf(uint spawnGuid) => _poolOfSpawn.GetValueOrDefault(spawnGuid);

    /// <summary>Whether a spawn is left to the pool system (cmangos <c>GuidPoolId || EntryPoolId</c>), whatever became of its row.</summary>
    public bool IsPooled(uint spawnGuid) => _pooled.Contains(spawnGuid);

    /// <summary>The top pool of <paramref name="poolId"/> (itself when it has no mother).</summary>
    public uint TopPoolOf(uint poolId)
    {
        uint current = poolId;
        for (int guard = 0; guard < 64 && _pools.TryGetValue(current, out PoolDefinition? pool) && pool.Mother != 0; guard++)
        {
            current = pool.Mother;
        }

        return current;
    }

    /// <summary>
    /// Build the catalog as cmangos PoolManager::LoadFromDB loads one kind: <paramref name="guidRows"/> (<c>pool_creature</c> /
    /// <c>pool_gameobject</c>) first, then <paramref name="entryRows"/> (<c>pool_*_template</c>, expanded to every spawn of the entry),
    /// then <paramref name="poolRows"/>. A row is dropped (and reported) when its spawn does not exist, its pool id is above the largest
    /// <c>pool_template</c> entry, its chance is outside 0..100, an entry row names a spawn already pooled, or its spawn is on another map
    /// than the pool's others; a pool link when either id is above the largest entry, a pool includes itself, its chance is out of range,
    /// its child's spawns are on another map than the mother's, or it closes a cycle.
    /// </summary>
    /// <param name="spawns">Every spawn of the kind: guid to (entry, map).</param>
    public static PoolCatalog Build(
        IEnumerable<PoolTemplateData> templates, IEnumerable<PoolSpawnLink> guidRows, IEnumerable<PoolSpawnLink> entryRows, IEnumerable<PoolPoolLink> poolRows,
        IReadOnlyDictionary<uint, (uint Entry, uint MapId)> spawns)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(guidRows);
        ArgumentNullException.ThrowIfNull(entryRows);
        ArgumentNullException.ThrowIfNull(poolRows);
        ArgumentNullException.ThrowIfNull(spawns);

        var issues = new List<string>();
        var templateOf = new Dictionary<uint, PoolTemplateData>();
        foreach (PoolTemplateData template in templates)
        {
            templateOf[template.Entry] = template;
        }

        var guidList = guidRows.OrderBy(r => r.Key).ToList();
        var entryList = entryRows.OrderBy(r => r.Key).ToList();
        var guidsOfEntry = new Dictionary<uint, List<uint>>();
        foreach ((uint guid, (uint entry, _)) in spawns.OrderBy(s => s.Key))
        {
            if (!guidsOfEntry.TryGetValue(entry, out List<uint>? list))
            {
                guidsOfEntry[entry] = list = [];
            }

            list.Add(guid);
        }

        // cmangos ObjectMgr's pool joins: any row naming the spawn (or its entry) keeps it out of the grid.
        var pooled = new HashSet<uint>(guidList.Select(r => r.Key).Where(spawns.ContainsKey));
        foreach (PoolSpawnLink row in entryList)
        {
            if (guidsOfEntry.TryGetValue(row.Key, out List<uint>? guids))
            {
                pooled.UnionWith(guids);
            }
        }

        if (templateOf.Count == 0)
        {
            return new PoolCatalog([], [], pooled, issues);
        }

        uint maxPoolId = templateOf.Keys.Max();
        var builders = new Dictionary<uint, Builder>();
        var poolOfSpawn = new Dictionary<uint, uint>();
        Builder Of(uint id)
        {
            if (!builders.TryGetValue(id, out Builder? builder))
            {
                templateOf.TryGetValue(id, out PoolTemplateData? template);
                builders[id] = builder = new Builder(id, template);
            }

            return builder;
        }

        foreach (PoolTemplateData template in templateOf.Values)
        {
            Of(template.Entry);
        }

        void AddSpawn(uint guid, uint poolId, float chance, string table, bool fromEntry)
        {
            if (!spawns.TryGetValue(guid, out (uint Entry, uint MapId) data))
            {
                issues.Add($"{table}: spawn {guid} does not exist (pool {poolId}); skipped");
                return;
            }

            if (poolId > maxPoolId)
            {
                issues.Add($"{table}: pool {poolId} of spawn {guid} is above the largest pool_template entry {maxPoolId}; skipped");
                return;
            }

            if (chance < 0 || chance > 100)
            {
                issues.Add($"{table}: spawn {guid} has chance {chance} in pool {poolId}; skipped");
                return;
            }

            if (fromEntry && poolOfSpawn.TryGetValue(guid, out uint other))
            {
                issues.Add($"{table}: spawn {guid} is already in pool {other}; skipped");
                return;
            }

            Builder pool = Of(poolId);
            if (!pool.Remember(data.MapId))
            {
                issues.Add($"{table}: spawn {guid} is on map {data.MapId}, pool {poolId}'s others on map {pool.MapId}; skipped");
                return;
            }

            pool.Add(new PoolMember(guid, Math.Abs(chance)), pool: false);
            poolOfSpawn[guid] = poolId;
        }

        foreach (PoolSpawnLink row in guidList)
        {
            AddSpawn(row.Key, row.Pool, row.Chance, "pool_spawn", fromEntry: false);
        }

        foreach (PoolSpawnLink row in entryList)
        {
            foreach (uint guid in guidsOfEntry.GetValueOrDefault(row.Key) ?? [])
            {
                AddSpawn(guid, row.Pool, row.Chance, "pool_template_spawn", fromEntry: true);
            }
        }

        var motherOf = new Dictionary<uint, uint>();
        foreach (PoolPoolLink row in poolRows.OrderBy(r => r.Child))
        {
            if (row.Mother > maxPoolId || row.Child > maxPoolId)
            {
                issues.Add($"pool_pool: pool {row.Child} in mother {row.Mother} is above the largest pool_template entry {maxPoolId}; skipped");
                continue;
            }

            if (row.Mother == row.Child)
            {
                issues.Add($"pool_pool: pool {row.Child} includes itself; skipped");
                continue;
            }

            if (row.Chance < 0 || row.Chance > 100)
            {
                issues.Add($"pool_pool: pool {row.Child} has chance {row.Chance} in mother {row.Mother}; skipped");
                continue;
            }

            Of(row.Mother).Add(new PoolMember(row.Child, Math.Abs(row.Chance)), pool: true);
            Of(row.Child).IsChild = true;
            motherOf[row.Child] = row.Mother;
        }

        // cmangos: walk each chain upwards; a child whose spawns are on another map than its mother's, or a link that closes a cycle, is cut.
        foreach (uint start in motherOf.Keys.Order().ToArray())
        {
            var seen = new HashSet<uint> { start };
            for (uint child = start; motherOf.TryGetValue(child, out uint mother); child = mother)
            {
                if (Of(child).MapId is { } childMap && !Of(mother).Remember(childMap))
                {
                    issues.Add($"pool_pool: pool {child} is on map {childMap}, its mother {mother} on map {Of(mother).MapId}; link cut");
                    Of(mother).RemovePool(child);
                    motherOf.Remove(child);
                    break;
                }

                if (!seen.Add(mother))
                {
                    issues.Add($"pool_pool: pools {string.Join(' ', seen.Order())} form a cycle; the link of {child} to {mother} is cut");
                    Of(mother).RemovePool(child);
                    motherOf.Remove(child);
                    break;
                }
            }
        }

        var pools = new Dictionary<uint, PoolDefinition>(builders.Count);
        foreach (Builder builder in builders.Values)
        {
            bool auto = builder.Template is not null && !builder.IsChild;
            if (auto && !builder.ChancesCanPick())
            {
                issues.Add($"pool {builder.Id}: every member has an explicit chance and they do not add up to 100; it cannot pick one and is not spawned");
                auto = false;
            }

            pools[builder.Id] = builder.Build(motherOf.GetValueOrDefault(builder.Id), auto);
        }

        return new PoolCatalog(pools, poolOfSpawn, pooled, issues);
    }

    private sealed class Builder(uint id, PoolTemplateData? template)
    {
        private readonly List<PoolMember> _explicit = [];
        private readonly List<PoolMember> _equal = [];
        private readonly List<PoolMember> _explicitPools = [];
        private readonly List<PoolMember> _equalPools = [];

        public uint Id { get; } = id;

        public PoolTemplateData? Template { get; } = template;

        public uint? MapId { get; private set; }

        public bool IsChild { get; set; }

        /// <summary>cmangos PoolMapChecker::CheckAndRemember: the first spawn decides the pool's map.</summary>
        public bool Remember(uint mapId)
        {
            MapId ??= mapId;
            return MapId == mapId;
        }

        /// <summary>cmangos PoolGroup::AddEntry: explicit only with a chance in a pool whose max_limit is 1.</summary>
        public void Add(PoolMember member, bool pool)
        {
            bool explicitChance = member.Chance != 0 && Template?.MaxLimit == 1;
            (pool ? (explicitChance ? _explicitPools : _equalPools) : (explicitChance ? _explicit : _equal)).Add(member);
        }

        public void RemovePool(uint child)
        {
            _explicitPools.RemoveAll(m => m.Id == child);
            _equalPools.RemoveAll(m => m.Id == child);
        }

        /// <summary>cmangos PoolGroup::CheckPool for both groups: with no equally chanced member, the explicit chances add up to 0 or 100.</summary>
        public bool ChancesCanPick() => CanPick(_explicit, _equal) && CanPick(_explicitPools, _equalPools);

        private static bool CanPick(List<PoolMember> explicitChanced, List<PoolMember> equal)
        {
            if (equal.Count > 0)
            {
                return true;
            }

            float sum = 0;
            foreach (PoolMember member in explicitChanced)
            {
                sum += member.Chance;
            }

            return sum == 100f || sum == 0f;
        }

        public PoolDefinition Build(uint mother, bool autoSpawn) => new()
        {
            Id = Id,
            MaxLimit = Template?.MaxLimit ?? 0,
            Description = Template?.Description ?? string.Empty,
            HasTemplate = Template is not null,
            Mother = mother,
            MapId = MapId,
            AutoSpawn = autoSpawn,
            ExplicitlyChanced = [.. _explicit],
            EqualChanced = [.. _equal],
            ExplicitlyChancedPools = [.. _explicitPools],
            EqualChancedPools = [.. _equalPools],
        };
    }
}
