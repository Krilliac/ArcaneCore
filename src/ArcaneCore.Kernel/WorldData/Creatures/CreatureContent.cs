namespace ArcaneCore.Kernel.WorldData.Creatures;

/// <summary>
/// One creature template (the server's view of a <c>creature_template</c> row). Column meanings
/// follow cmangos-classic <c>creature_template</c> (mangos.sql) and vmangos <c>CreatureInfo</c>
/// (CreatureDefines.h); ArcaneCore keeps the columns the creature, query and combat code read.
/// </summary>
public sealed record CreatureTemplate
{
    public required uint Entry { get; init; }

    public required string Name { get; init; }

    public string SubName { get; init; } = string.Empty;

    public byte MinLevel { get; init; } = 1;

    public byte MaxLevel { get; init; } = 1;

    /// <summary>Up to four display ids (cmangos DisplayId1-4, vmangos display_id1-4); 0 = unused.</summary>
    public IReadOnlyList<uint> DisplayIds { get; init; } = [];

    /// <summary>Selection weights for <see cref="DisplayIds"/> (all 0 = equal chance, vmangos ChooseDisplayId).</summary>
    public IReadOnlyList<uint> DisplayProbabilities { get; init; } = [];

    /// <summary>Object scale; 0 = 1.0 until display data is imported (cmangos Scale, vmangos display_scale).</summary>
    public float Scale { get; init; }

    public uint Faction { get; init; }

    public uint NpcFlags { get; init; }

    public uint UnitFlags { get; init; }

    public uint DynamicFlags { get; init; }

    /// <summary>Flags sent in SMSG_CREATURE_QUERY_RESPONSE (cmangos CreatureTypeFlags, vmangos GetTypeFlags()).</summary>
    public uint TypeFlags { get; init; }

    /// <summary>CreatureType.dbc id (1 beast … 11 totem).</summary>
    public uint CreatureType { get; init; }

    /// <summary>CreatureFamily.dbc id (0 = none).</summary>
    public uint Family { get; init; }

    /// <summary>0 normal, 1 elite, 2 rare elite, 3 world boss, 4 rare (vmangos CreatureEliteType).</summary>
    public uint Rank { get; init; }

    /// <summary>Class byte (only warrior, paladin, rogue and mage are known for creatures).</summary>
    public byte UnitClass { get; init; }

    public byte InhabitType { get; init; } = 3;

    public bool Civilian { get; init; }

    public bool RacialLeader { get; init; }

    /// <summary>Walk speed rate (× 2.5 yd/s); 0 = 1.0.</summary>
    public float SpeedWalk { get; init; } = 1.0f;

    /// <summary>Run speed rate (× 7 yd/s); 0 = 1.14286 (vmangos DEFAULT_NPC_RUN_SPEED_RATE).</summary>
    public float SpeedRun { get; init; } = 1.14286f;

    public uint MinLevelHealth { get; init; } = 1;

    public uint MaxLevelHealth { get; init; } = 1;

    public uint MinLevelMana { get; init; }

    public uint MaxLevelMana { get; init; }

    public uint Armor { get; init; }

    public float MinMeleeDamage { get; init; }

    public float MaxMeleeDamage { get; init; }

    public float MinRangedDamage { get; init; }

    public float MaxRangedDamage { get; init; }

    public uint MeleeAttackPower { get; init; }

    public uint RangedAttackPower { get; init; }

    public uint MeleeBaseAttackTime { get; init; } = 2000;

    public uint RangedBaseAttackTime { get; init; } = 2000;

    public uint DamageSchool { get; init; }

    /// <summary>CreatureSpellData.dbc id sent in the query response.</summary>
    public uint PetSpellDataId { get; init; }

    /// <summary>Default movement generator when the spawn does not set one (0 idle, 1 random, 2 waypoint).</summary>
    public byte MovementType { get; init; }

    /// <summary>Seconds a corpse stays (cmangos CorpseDecay); 0 = the rank default.</summary>
    public uint CorpseDecaySeconds { get; init; }

    /// <summary>vmangos CREATURE_FLAG_EXTRA_* / cmangos ExtraFlags (ALWAYS_RUN 0x40, INVISIBLE 0x80 are read here).</summary>
    public uint ExtraFlags { get; init; }

    /// <summary>
    /// Script selection (cmangos-classic <c>creature_template.AIName</c>, vmangos <c>ai_name</c>):
    /// "EventAI" runs the <c>creature_ai_scripts</c> rows; a registered C# AI name selects that AI;
    /// empty picks the default (docs/areas/creature-ai.md).
    /// </summary>
    public string AIName { get; init; } = string.Empty;
}

/// <summary>One placed creature (a <c>creature</c> row).</summary>
public sealed record CreatureSpawn
{
    /// <summary>Spawn id (the <c>creature.guid</c> column); the low part of the object GUID.</summary>
    public required uint Guid { get; init; }

    public required uint Entry { get; init; }

    public required uint MapId { get; init; }

    public required float X { get; init; }

    public required float Y { get; init; }

    public required float Z { get; init; }

    public float Orientation { get; init; }

    /// <summary>Respawn delay range in seconds (cmangos/vmangos spawntimesecsmin/max).</summary>
    public uint SpawnTimeMinSeconds { get; init; } = 120;

    public uint SpawnTimeMaxSeconds { get; init; } = 120;

    /// <summary>Random-movement radius in yards (cmangos spawndist, vmangos wander_distance).</summary>
    public float WanderDistance { get; init; }

    /// <summary>0 idle, 1 random, 2 waypoint (cmangos/vmangos MovementType).</summary>
    public byte MovementType { get; init; }
}

/// <summary>One point of a spawn's waypoint path (<c>creature_movement</c>).</summary>
public sealed record CreatureWaypoint(uint Point, float X, float Y, float Z, float Orientation, uint WaitTimeMs)
{
    /// <summary>Travel to this node at run speed (world schema creature-AI step, <c>creature_movement.Run</c>).</summary>
    public bool Run { get; init; }
}

/// <summary>Per-display model data (cmangos creature_model_info, vmangos creature_display_info_addon).</summary>
public sealed record CreatureModelInfo(uint DisplayId, float BoundingRadius, float CombatReach, byte Gender, uint DisplayIdOtherGender);

/// <summary>Per-spawn extras (cmangos/vmangos creature_addon).</summary>
public sealed record CreatureAddon(uint Guid, uint MountDisplayId, byte StandState, byte SheathState, uint EmoteState);

/// <summary>
/// Every creature row the world uses, loaded once at startup and read-only afterwards, so the
/// world thread looks things up without locks or database round trips (ROADMAP § Content).
/// </summary>
public sealed class CreatureContent
{
    public static readonly CreatureContent Empty = new([], [], [], [], []);

    private readonly Dictionary<uint, CreatureTemplate> _templates;
    private readonly Dictionary<uint, CreatureModelInfo> _models;
    private readonly Dictionary<uint, CreatureAddon> _addons;
    private readonly Dictionary<uint, IReadOnlyList<CreatureWaypoint>> _waypoints;
    private readonly Dictionary<uint, IReadOnlyList<CreatureSpawn>> _spawnsByMap;

    public CreatureContent(
        IEnumerable<CreatureTemplate> templates,
        IEnumerable<CreatureSpawn> spawns,
        IEnumerable<(uint SpawnGuid, CreatureWaypoint Point)> waypoints,
        IEnumerable<CreatureModelInfo> models,
        IEnumerable<CreatureAddon> addons,
        CreatureAiContent? ai = null)
    {
        Ai = ai ?? CreatureAiContent.Empty;
        _templates = templates.ToDictionary(t => t.Entry);
        _models = models.ToDictionary(m => m.DisplayId);
        _addons = addons.ToDictionary(a => a.Guid);
        _waypoints = waypoints
            .GroupBy(w => w.SpawnGuid)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CreatureWaypoint>)[.. g.Select(w => w.Point).OrderBy(p => p.Point)]);
        CreatureSpawn[] all = [.. spawns];
        SpawnCount = all.Length;
        _spawnsByMap = all.GroupBy(s => s.MapId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CreatureSpawn>)[.. g.OrderBy(s => s.Guid)]);
    }

    public int TemplateCount => _templates.Count;

    /// <summary>EventAI scripts and texts (<c>creature_ai_scripts</c>, <c>creature_ai_texts</c>).</summary>
    public CreatureAiContent Ai { get; }

    public int SpawnCount { get; }

    /// <summary>Maps that have at least one spawn.</summary>
    public IEnumerable<uint> MapsWithSpawns => _spawnsByMap.Keys;

    /// <summary>Every creature template (GM lookups; unordered).</summary>
    public IEnumerable<CreatureTemplate> Templates => _templates.Values;

    public CreatureTemplate? FindTemplate(uint entry) => _templates.GetValueOrDefault(entry);

    public CreatureModelInfo? FindModel(uint displayId) => _models.GetValueOrDefault(displayId);

    public CreatureAddon? FindAddon(uint spawnGuid) => _addons.GetValueOrDefault(spawnGuid);

    public IReadOnlyList<CreatureWaypoint> GetWaypoints(uint spawnGuid) => _waypoints.GetValueOrDefault(spawnGuid) ?? [];

    public IReadOnlyList<CreatureSpawn> GetSpawns(uint mapId) => _spawnsByMap.GetValueOrDefault(mapId) ?? [];
}

/// <summary>Loads the creature content from the world database.</summary>
public interface ICreatureDataStore
{
    Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default);
}
