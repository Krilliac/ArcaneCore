namespace ArcaneCore.Kernel.WorldData.GameObjects;

/// <summary>
/// One game object template (a <c>gameobject_template</c> row). Column meanings follow
/// cmangos-classic <c>gameobject_template</c> (mangos.sql) and vmangos <c>GameObjectInfo</c>
/// (GameObject.h): the 24 <c>data0..data23</c> values are interpreted per <see cref="Type"/>.
/// </summary>
public sealed record GameObjectTemplate
{
    /// <summary>Number of type-specific data columns (vmangos GameObjectInfo raw.data[24]).</summary>
    public const int DataCount = 24;

    public required uint Entry { get; init; }

    /// <summary>GameobjectTypes value (0 door … 25 fishing hole, vmangos SharedDefines.h).</summary>
    public required uint Type { get; init; }

    public uint DisplayId { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>FactionTemplate.dbc id written to GAMEOBJECT_FACTION.</summary>
    public uint Faction { get; init; }

    /// <summary>GAMEOBJECT_FLAGS at spawn (GO_FLAG_IN_USE 0x01, LOCKED 0x02, INTERACT_COND 0x04, …).</summary>
    public uint Flags { get; init; }

    /// <summary>Object scale (OBJECT_FIELD_SCALE_X); 0 reads as 1.</summary>
    public float Size { get; init; } = 1.0f;

    /// <summary>The type-specific data columns, always <see cref="DataCount"/> long.</summary>
    public IReadOnlyList<uint> Data { get; init; } = new uint[DataCount];

    /// <summary>A data column (0 when out of range).</summary>
    public uint GetData(int index) => index >= 0 && index < Data.Count ? Data[index] : 0;
}

/// <summary>One placed game object (a <c>gameobject</c> row).</summary>
public sealed record GameObjectSpawn
{
    /// <summary>Spawn id (<c>gameobject.guid</c>); the counter of the object GUID.</summary>
    public required uint Guid { get; init; }

    public required uint Entry { get; init; }

    public required uint MapId { get; init; }

    public required float X { get; init; }

    public required float Y { get; init; }

    public required float Z { get; init; }

    public float Orientation { get; init; }

    /// <summary>Quaternion (cmangos/vmangos rotation0..3); 0, 0, 0, 0 derives it from <see cref="Orientation"/>.</summary>
    public float Rotation0 { get; init; }

    public float Rotation1 { get; init; }

    public float Rotation2 { get; init; }

    public float Rotation3 { get; init; }

    /// <summary>
    /// Respawn delay in seconds (cmangos/vmangos <c>spawntimesecs</c>). A negative value means the
    /// object starts despawned and is only spawned by a script/event, as in vmangos.
    /// </summary>
    public int SpawnTimeSeconds { get; init; } = 300;

    /// <summary>GAMEOBJECT_ANIMPROGRESS at spawn (vmangos animprogress, 100 by default).</summary>
    public uint AnimProgress { get; init; } = 100;

    /// <summary>GAMEOBJECT_STATE at spawn (0 active/open, 1 ready/closed, 2 active alternative).</summary>
    public byte State { get; init; } = 1;
}

/// <summary>
/// One Lock.dbc record (vmangos DBCStructure LockEntry): up to eight alternative ways to open,
/// each a key type (0 none, 1 item, 2 skill), an index (item entry or LockType) and a required
/// skill value.
/// </summary>
public sealed record LockEntry(uint Id, IReadOnlyList<uint> Types, IReadOnlyList<uint> Indexes, IReadOnlyList<uint> Skills)
{
    /// <summary>MAX_LOCK_CASE (vmangos DBCStructure.h).</summary>
    public const int Cases = 8;
}

/// <summary>
/// Every game object row the world uses, loaded once at startup and read-only afterwards
/// (as <c>CreatureContent</c>): templates, spawns by map, Lock.dbc entries and the quest
/// relations of quest-giver objects (<c>gameobject_questrelation</c> / <c>gameobject_involvedrelation</c>).
/// </summary>
public sealed class GameObjectContent
{
    public static readonly GameObjectContent Empty = new([], [], [], [], []);

    private readonly Dictionary<uint, GameObjectTemplate> _templates;
    private readonly Dictionary<uint, LockEntry> _locks;
    private readonly Dictionary<uint, IReadOnlyList<GameObjectSpawn>> _spawnsByMap;
    private readonly Dictionary<uint, IReadOnlyList<uint>> _starters;
    private readonly Dictionary<uint, IReadOnlyList<uint>> _enders;

    public GameObjectContent(
        IEnumerable<GameObjectTemplate> templates,
        IEnumerable<GameObjectSpawn> spawns,
        IEnumerable<LockEntry> locks,
        IEnumerable<(uint Entry, uint Quest)> questStarters,
        IEnumerable<(uint Entry, uint Quest)> questEnders)
    {
        _templates = templates.ToDictionary(t => t.Entry);
        _locks = locks.ToDictionary(l => l.Id);
        GameObjectSpawn[] all = [.. spawns];
        SpawnCount = all.Length;
        _spawnsByMap = all.GroupBy(s => s.MapId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<GameObjectSpawn>)[.. g.OrderBy(s => s.Guid)]);
        _starters = questStarters.GroupBy(r => r.Entry)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<uint>)[.. g.Select(r => r.Quest).Distinct().Order()]);
        _enders = questEnders.GroupBy(r => r.Entry)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<uint>)[.. g.Select(r => r.Quest).Distinct().Order()]);
    }

    public int TemplateCount => _templates.Count;

    public int SpawnCount { get; }

    public int LockCount => _locks.Count;

    /// <summary>Maps that have at least one spawn.</summary>
    public IEnumerable<uint> MapsWithSpawns => _spawnsByMap.Keys;

    public GameObjectTemplate? FindTemplate(uint entry) => _templates.GetValueOrDefault(entry);

    public LockEntry? FindLock(uint lockId) => lockId == 0 ? null : _locks.GetValueOrDefault(lockId);

    public IReadOnlyList<GameObjectSpawn> GetSpawns(uint mapId) => _spawnsByMap.GetValueOrDefault(mapId) ?? [];

    /// <summary>Quests a quest-giver object starts (<c>gameobject_questrelation</c>).</summary>
    public IReadOnlyList<uint> QuestStartersOf(uint entry) => _starters.GetValueOrDefault(entry) ?? [];

    /// <summary>Quests a quest-giver object ends (<c>gameobject_involvedrelation</c>).</summary>
    public IReadOnlyList<uint> QuestEndersOf(uint entry) => _enders.GetValueOrDefault(entry) ?? [];
}

/// <summary>Loads the game object content from the world database.</summary>
public interface IGameObjectDataStore
{
    Task<GameObjectContent> LoadAsync(CancellationToken cancellationToken = default);
}
