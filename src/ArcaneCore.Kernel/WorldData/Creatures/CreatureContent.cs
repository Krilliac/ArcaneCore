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

    /// <summary>vmangos CreatureDefines.h:242,272-275 NPC service metadata.</summary>
    public uint GossipMenuId { get; init; }

    public uint TrainerType { get; init; }

    public byte TrainerClass { get; init; }

    public byte TrainerRace { get; init; }

    public uint TrainerSpell { get; init; }

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

    /// <summary>vmangos <c>CreatureInfo::detection_range</c> default (Objects/CreatureDefines.h:250); classic-db <c>Detection</c> column default.</summary>
    public const float DefaultDetectionRange = 18.0f;

    /// <summary>
    /// Proximity-aggro detection range in yards (cmangos <c>Detection</c>, vmangos <c>detection_range</c>);
    /// the base of the aggro radius (vmangos Objects/Creature.cpp:2193-2240, cmangos Entities/Unit.cpp:11784).
    /// </summary>
    public float Detection { get; init; } = DefaultDetectionRange;

    /// <summary>Call-for-help range in yards (cmangos <c>CallForHelp</c>, vmangos <c>call_for_help_range</c>); 0 = the creature calls nobody.</summary>
    public float CallForHelp { get; init; }

    /// <summary>cmangos <c>Pursuit</c>: milliseconds without a hit refresh after which the creature evades; 0 = unset.</summary>
    public uint Pursuit { get; init; }

    /// <summary>Hard leash range in yards from the combat start point (cmangos <c>Leash</c>, vmangos <c>leash_range</c>); 0 = unset.</summary>
    public float Leash { get; init; }

    /// <summary>cmangos <c>Timeout</c>: milliseconds a leash refresh lasts before evade; 0 = unset.</summary>
    public uint Timeout { get; init; }

    /// <summary>vmangos <c>static_flags1</c> / cmangos <c>StaticFlags1</c> (CREATURE_STATIC_FLAG_*; identical bits in both).</summary>
    public uint StaticFlags1 { get; init; }

    /// <summary>vmangos <c>static_flags2</c> / cmangos <c>StaticFlags2</c>.</summary>
    public uint StaticFlags2 { get; init; }

    /// <summary>Which engine authored <see cref="ExtraFlags"/>; decode through <see cref="Behaviour"/>.</summary>
    public CreatureExtraFlagsDialect ExtraFlagsDialect { get; init; }

    /// <summary>
    /// The behaviour switches decoded once from the dialect-specific flag columns; AI and movement code
    /// reads this instead of raw <see cref="ExtraFlags"/> bits.
    /// </summary>
    public CreatureBehaviourFlags Behaviour
        => CreatureBehaviour.Normalize(ExtraFlagsDialect, ExtraFlags, StaticFlags1, StaticFlags2, Civilian);
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
/// The replaceable half of a <see cref="CreatureContent"/>: templates, model infos, addons,
/// waypoints and EventAI. Immutable; obtained from <see cref="CreatureContent.SwapDefinitions"/>
/// to undo a swap.
/// </summary>
public sealed class CreatureDefinitions
{
    internal CreatureDefinitions(
        Dictionary<uint, CreatureTemplate> templates,
        Dictionary<uint, CreatureModelInfo> models,
        Dictionary<uint, CreatureAddon> addons,
        Dictionary<uint, IReadOnlyList<CreatureWaypoint>> waypoints,
        CreatureAiContent ai)
    {
        Templates = templates;
        Models = models;
        Addons = addons;
        Waypoints = waypoints;
        Ai = ai;
    }

    internal Dictionary<uint, CreatureTemplate> Templates { get; }

    internal Dictionary<uint, CreatureModelInfo> Models { get; }

    internal Dictionary<uint, CreatureAddon> Addons { get; }

    internal Dictionary<uint, IReadOnlyList<CreatureWaypoint>> Waypoints { get; }

    internal CreatureAiContent Ai { get; }
}

/// <summary>
/// Every creature row the world uses, read-only for the world thread's lookups (no locks, no
/// database round trips; ROADMAP § Content). The spawns are fixed for the life of the object. The
/// definitions (templates, models, addons, waypoints, EventAI) can be swapped as a whole with
/// <see cref="SwapDefinitions"/> (<c>.reload creature_template</c>), so everything holding this
/// object sees the new definitions at once, the way vmangos' <c>LoadCreatureTemplates</c>
/// overwrites the <c>CreatureInfo</c> table that live creatures point into (ObjectMgr.cpp:1190).
/// </summary>
public sealed class CreatureContent
{
    public static readonly CreatureContent Empty = new([], [], [], [], []);

    private readonly Dictionary<uint, IReadOnlyList<CreatureSpawn>> _spawnsByMap;
    private volatile CreatureDefinitions _definitions;
    private int _version;

    public CreatureContent(
        IEnumerable<CreatureTemplate> templates,
        IEnumerable<CreatureSpawn> spawns,
        IEnumerable<(uint SpawnGuid, CreatureWaypoint Point)> waypoints,
        IEnumerable<CreatureModelInfo> models,
        IEnumerable<CreatureAddon> addons,
        CreatureAiContent? ai = null)
    {
        _definitions = new CreatureDefinitions(
            templates.ToDictionary(t => t.Entry),
            models.ToDictionary(m => m.DisplayId),
            addons.ToDictionary(a => a.Guid),
            waypoints
                .GroupBy(w => w.SpawnGuid)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<CreatureWaypoint>)[.. g.Select(w => w.Point).OrderBy(p => p.Point)]),
            ai ?? CreatureAiContent.Empty);
        CreatureSpawn[] all = [.. spawns];
        SpawnCount = all.Length;
        _spawnsByMap = all.GroupBy(s => s.MapId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CreatureSpawn>)[.. g.OrderBy(s => s.Guid)]);
    }

    public int TemplateCount => _definitions.Templates.Count;

    /// <summary>EventAI scripts and texts (<c>creature_ai_scripts</c>, <c>creature_ai_texts</c>).</summary>
    public CreatureAiContent Ai => _definitions.Ai;

    public int SpawnCount { get; }

    /// <summary>Changes every time the definitions are swapped; a holder that caches a lookup compares it to know when to look again.</summary>
    public int DefinitionsVersion => Volatile.Read(ref _version);

    /// <summary>Maps that have at least one spawn.</summary>
    public IEnumerable<uint> MapsWithSpawns => _spawnsByMap.Keys;

    /// <summary>Every creature template (GM lookups; unordered).</summary>
    public IEnumerable<CreatureTemplate> Templates => _definitions.Templates.Values;

    public CreatureTemplate? FindTemplate(uint entry) => _definitions.Templates.GetValueOrDefault(entry);

    public CreatureModelInfo? FindModel(uint displayId) => _definitions.Models.GetValueOrDefault(displayId);

    public CreatureAddon? FindAddon(uint spawnGuid) => _definitions.Addons.GetValueOrDefault(spawnGuid);

    public IReadOnlyList<CreatureWaypoint> GetWaypoints(uint spawnGuid) => _definitions.Waypoints.GetValueOrDefault(spawnGuid) ?? [];

    public IReadOnlyList<CreatureSpawn> GetSpawns(uint mapId) => _spawnsByMap.GetValueOrDefault(mapId) ?? [];

    /// <summary>
    /// Make the definitions of <paramref name="fresh"/> this content's definitions (its spawns are
    /// ignored) and return the ones replaced, for <see cref="RestoreDefinitions"/>. The shared
    /// <see cref="Empty"/> instance refuses: it belongs to every host that has no creature data.
    /// Call on the world thread; readers on other threads see either generation.
    /// </summary>
    public CreatureDefinitions SwapDefinitions(CreatureContent fresh)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        if (ReferenceEquals(this, Empty))
        {
            throw new InvalidOperationException("the shared empty creature content cannot be changed");
        }

        CreatureDefinitions previous = _definitions;
        _definitions = fresh._definitions;
        Interlocked.Increment(ref _version);
        return previous;
    }

    /// <summary>Put back definitions returned by <see cref="SwapDefinitions"/>.</summary>
    public void RestoreDefinitions(CreatureDefinitions previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (ReferenceEquals(this, Empty))
        {
            throw new InvalidOperationException("the shared empty creature content cannot be changed");
        }

        _definitions = previous;
        Interlocked.Increment(ref _version);
    }
}

/// <summary>Loads the creature content from the world database.</summary>
public interface ICreatureDataStore
{
    Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default);
}
