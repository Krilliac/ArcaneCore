namespace ArcaneCore.Kernel.WorldData.SpawnGroups;

/// <summary>What a spawn group holds (cmangos <c>spawn_group.Type</c>, Maps/SpawnGroupDefines.h SpawnGroupType).</summary>
public enum SpawnGroupType : byte
{
    Creature = 0,
    GameObject = 1,
}

/// <summary>
/// <c>spawn_group.Flags</c> (cmangos Maps/SpawnGroupDefines.h SpawnGroupFlags and CreatureGroupFlags). The three creature flags only act
/// on creature groups.
/// </summary>
[Flags]
public enum SpawnGroupFlags : uint
{
    None = 0,

    /// <summary>CREATURE_GROUP_AGGRO_TOGETHER: a member's aggro pulls every member into the fight.</summary>
    AggroTogether = 0x01,

    /// <summary>CREATURE_GROUP_RESPAWN_TOGETHER: a member that respawns, or a group that returns home, clears every member's respawn time.</summary>
    RespawnTogether = 0x02,

    /// <summary>CREATURE_GROUP_EVADE_TOGETHER: one member's evade sends the others home too.</summary>
    EvadeTogether = 0x04,

    /// <summary>SPAWN_GROUP_DESPAWN_ON_COND_FAIL: the group's live members leave when its condition stops holding.</summary>
    DespawnOnConditionFail = 0x08,

    /// <summary>CREATURE_GROUP_FORMATION_MIRRORING (formation movement, not implemented).</summary>
    FormationMirroring = 0x10,
}

/// <summary>A <c>spawn_group_spawn</c> row: one database spawn of the group.</summary>
/// <param name="Guid">The <c>creature.guid</c> or <c>gameobject.guid</c> (by the group's type).</param>
/// <param name="SlotId">The formation slot (0 the leader, -1 none).</param>
/// <param name="Chance">Percent chance that the spawn is used at all while the group is full (0 always).</param>
public sealed record SpawnGroupMember(uint Guid, int SlotId, uint Chance);

/// <summary>A <c>spawn_group_entry</c> row: one entry the group's entry-less members may become.</summary>
/// <param name="Entry">The creature or game object entry.</param>
/// <param name="MinCount">How many members must have this entry before the others are rolled.</param>
/// <param name="MaxCount">The most members that may have this entry at once (0 no limit).</param>
/// <param name="Chance">Percent chance of this entry; 0 is "equally chanced" among the other 0 rows.</param>
public sealed record SpawnGroupRandomEntry(uint Entry, uint MinCount, uint MaxCount, uint Chance);

/// <summary>A <c>spawn_group_formation</c> row (imported; formation movement is not implemented).</summary>
public sealed record SpawnGroupFormation(byte FormationType, float Spread, uint Options, uint PathId, byte MovementType, string Comment);

/// <summary>One <c>spawn_group</c> row with its spawns, entries, formation and linked groups.</summary>
public sealed record SpawnGroupDefinition
{
    public required uint Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public required SpawnGroupType Type { get; init; }

    /// <summary><c>MaxCount</c> as stored: 0 means "derive it from the entries and spawns" (see the runtime's effective maximum).</summary>
    public uint MaxCount { get; init; }

    /// <summary><c>WorldState</c>: a <c>conditions.condition_entry</c> that must hold for the group to spawn (0 none).</summary>
    public uint WorldStateCondition { get; init; }

    /// <summary><c>WorldStateExpression</c> (cmangos world-state expressions; not implemented, a group with one never spawns).</summary>
    public uint WorldStateExpression { get; init; }

    public SpawnGroupFlags Flags { get; init; }

    public uint StringId { get; init; }

    public IReadOnlyList<SpawnGroupMember> Members { get; init; } = [];

    public IReadOnlyList<SpawnGroupRandomEntry> RandomEntries { get; init; } = [];

    public SpawnGroupFormation? Formation { get; init; }

    public IReadOnlyList<uint> LinkedGroups { get; init; } = [];
}

/// <summary>
/// Every cmangos spawn group of the world (<c>spawn_group</c>, <c>spawn_group_spawn</c>, <c>spawn_group_entry</c>,
/// <c>spawn_group_formation</c>, <c>spawn_group_linked_group</c>), read-only after load. A database spawn belongs to at most one group
/// of its type: a later row naming a guid already taken is dropped, as cmangos ObjectMgr::LoadSpawnGroups does ("belongs to more than
/// one spawn_group").
/// </summary>
public sealed class SpawnGroupCatalog
{
    public static readonly SpawnGroupCatalog Empty = new([]);

    private readonly Dictionary<uint, SpawnGroupDefinition> _groups;
    private readonly Dictionary<(SpawnGroupType, uint), SpawnGroupDefinition> _byMember = [];

    public SpawnGroupCatalog(IEnumerable<SpawnGroupDefinition> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        _groups = [];
        foreach (SpawnGroupDefinition group in groups.OrderBy(g => g.Id))
        {
            var members = new List<SpawnGroupMember>(group.Members.Count);
            foreach (SpawnGroupMember member in group.Members)
            {
                if (_byMember.ContainsKey((group.Type, member.Guid)))
                {
                    continue;
                }

                members.Add(member);
            }

            SpawnGroupDefinition kept = members.Count == group.Members.Count ? group : group with { Members = members };
            _groups[group.Id] = kept;
            foreach (SpawnGroupMember member in kept.Members)
            {
                _byMember[(group.Type, member.Guid)] = kept;
            }
        }
    }

    public int Count => _groups.Count;

    public IEnumerable<SpawnGroupDefinition> Groups => _groups.Values;

    public SpawnGroupDefinition? Find(uint id) => _groups.GetValueOrDefault(id);

    /// <summary>The group a database spawn of <paramref name="type"/> belongs to, or null.</summary>
    public SpawnGroupDefinition? GroupOf(SpawnGroupType type, uint spawnGuid) => _byMember.GetValueOrDefault((type, spawnGuid));

    /// <summary>The groups of one type.</summary>
    public IEnumerable<SpawnGroupDefinition> OfType(SpawnGroupType type) => _groups.Values.Where(g => g.Type == type);
}
