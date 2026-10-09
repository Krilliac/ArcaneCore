using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.SpawnGroups;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// cmangos creature spawn groups (Maps/SpawnGroup.cpp CreatureGroup, Maps/SpawnManager.cpp): a database spawn that belongs to a group is
/// not created by its grid on its own. The group decides, at every map update, which of its members are in the world (up to its maximum,
/// none back before its respawn time) and which entry each becomes; a grid creates the members the group chose when it loads. A member
/// leaves the group when its corpse is removed (its respawn time is kept), when it is despawned or when its grid unloads, and the group
/// may then bring another member. cmangos does the same with its dynamic-guid spawns. The aggro, evade and respawn-together flags link the
/// members' fights and respawns. Formations, linked groups and world-state expressions are not implemented
/// (docs/areas/content-import.md, spawn groups).
/// </summary>
public sealed partial class CreatureMapSystem : ISpawnGroupHost
{
    private readonly Dictionary<uint, SpawnGroupState> _spawnGroups = [];
    private readonly Dictionary<uint, SpawnGroupState> _groupOfSpawn = [];
    private readonly Dictionary<uint, CreatureSpawn> _groupSpawns = [];
    private readonly HashSet<uint> _groupRespawnCleared = [];
    private readonly HashSet<(uint Group, ObjectGuid Target)> _groupAggroInProgress = [];

    /// <summary>
    /// Whether a spawn group's condition (<c>spawn_group.WorldState</c>, a <c>conditions</c> entry) holds now; null when it cannot be decided.
    /// Unset, or undecided, a group with a condition does not spawn (fail closed, as the condition evaluator does). The world wires it.
    /// </summary>
    public Func<SpawnGroupDefinition, bool?>? SpawnGroupCondition { get; set; }

    /// <summary>The spawn groups of this map.</summary>
    internal IReadOnlyCollection<SpawnGroupState> SpawnGroups => _spawnGroups.Values;

    /// <summary>The spawn group a database spawn of this map belongs to, if any.</summary>
    internal SpawnGroupState? SpawnGroupOf(uint spawnGuid) => _groupOfSpawn.GetValueOrDefault(spawnGuid);

    /// <summary>The ids of the members of spawn group <paramref name="groupId"/> that are in the world now, with their entries (GM, tests).</summary>
    public IReadOnlyDictionary<uint, uint> SpawnGroupObjects(uint groupId)
        => _spawnGroups.TryGetValue(groupId, out SpawnGroupState? state) ? state.Objects : new Dictionary<uint, uint>();

    private void InitializeSpawnGroups()
    {
        if (_content.SpawnGroups.Count == 0)
        {
            return;
        }

        var spawns = new Dictionary<uint, CreatureSpawn>();
        foreach (CreatureSpawn spawn in _content.GetSpawns(Map.MapId))
        {
            spawns[spawn.Guid] = spawn;
        }

        bool dungeon = Map.Template?.IsDungeon == true;
        foreach (SpawnGroupDefinition group in _content.SpawnGroups.OfType(SpawnGroupType.Creature))
        {
            // cmangos SpawnManager::Initialize: a group lives on the map of its first spawn.
            if (group.Members.Count == 0 || !spawns.ContainsKey(group.Members[0].Guid))
            {
                continue;
            }

            // cmangos ObjectMgr::LoadSpawnGroups skips a spawn that is part of a pool ("incompatible"): its pool owns it.
            SpawnGroupMember[] members = [.. group.Members.Where(m => spawns.ContainsKey(m.Guid) && !_content.Pools.IsPooled(m.Guid))];
            SpawnGroupRandomEntry[] entries = [.. group.RandomEntries.Where(e => _content.FindTemplate(e.Entry) is not null)];
            var state = new SpawnGroupState(group, members, entries, dungeon);
            _spawnGroups[group.Id] = state;
            foreach (SpawnGroupMember member in members)
            {
                _groupOfSpawn[member.Guid] = state;
                _groupSpawns[member.Guid] = spawns[member.Guid];
            }
        }

        UpdateSpawnGroups();
    }

    /// <summary>cmangos SpawnManager::Update → SpawnGroup::Update for every group of the map.</summary>
    private void UpdateSpawnGroups()
    {
        foreach (SpawnGroupState state in _spawnGroups.Values)
        {
            foreach ((uint guid, uint entry) in state.Spawn(this, _random))
            {
                CreateGroupMember(state, _groupSpawns[guid], entry);
            }
        }
    }

    /// <summary>A member the group chose: created now when its grid is loaded (else when the grid loads, <see cref="LoadSpawns"/>).</summary>
    private void CreateGroupMember(SpawnGroupState state, CreatureSpawn spawn, uint entry)
    {
        ForgetGroupRespawn(spawn.Guid);
        if (!_grids.TryGetValue(ComputeGrid(spawn.X, spawn.Y), out LoadedGrid? grid))
        {
            return;
        }

        if (_creatures.ContainsKey(ObjectGuid.WithEntry(HighGuid.Unit, entry, spawn.Guid)))
        {
            return;
        }

        if (_content.FindTemplate(entry) is not { } template)
        {
            state.Remove(spawn.Guid, this);
            return;
        }

        AddGroupMember(state, spawn, template, grid);
    }

    private void AddGroupMember(SpawnGroupState state, CreatureSpawn spawn, CreatureTemplate template, LoadedGrid grid)
    {
        var creature = new Creature(spawn.Guid, template, spawn, _content, _random, displayModelResolver: _displayModelResolver, statRates: _options.Rates);
        ApplyEventData(creature);
        if (_options.Respawn.DrawDelayAtLoad)
        {
            creature.DrawRespawnDelay();
        }

        _respawnAt.Remove(spawn.Guid);
        AddToWorld(creature, grid);
        OnGroupMemberRespawned(state); // cmangos Creature::LoadFromDB → TriggerLinkingEvent(CREATURE_GROUP_EVENT_RESPAWN)
    }

    /// <summary>The grid of a group member loads: the member is created only when the group chose it, with the entry it chose.</summary>
    private void LoadGroupMember(SpawnGroupState state, CreatureSpawn spawn, LoadedGrid grid)
    {
        if (!state.TryGetEntry(spawn.Guid, out uint entry))
        {
            return;
        }

        if (_creatures.TryGetValue(ObjectGuid.WithEntry(HighGuid.Unit, entry, spawn.Guid), out Creature? moved))
        {
            grid.Creatures.Add(moved); // a live member walked away before its home grid unloaded
            return;
        }

        if (_content.FindTemplate(entry) is not { } template)
        {
            state.Remove(spawn.Guid, this);
            return;
        }

        AddGroupMember(state, spawn, template, grid);
    }

    /// <summary>A group member left the world (despawn, grid unload, corpse removal): the group may bring another.</summary>
    private void OnGroupMemberRemoved(Creature creature)
    {
        if (creature.Spawn is not { } spawn || !_groupOfSpawn.TryGetValue(spawn.Guid, out SpawnGroupState? state))
        {
            return;
        }

        if (_groupRespawnCleared.Remove(spawn.Guid))
        {
            ForgetGroupRespawn(spawn.Guid);
        }

        if (state.TryGetEntry(spawn.Guid, out uint entry) && entry == creature.Guid.Entry)
        {
            state.Remove(spawn.Guid, this);
        }
    }

    /// <summary>
    /// The corpse of a group member is removed: cmangos removes the object (it is a dynamic-guid spawn) and the group brings it, or another
    /// member, back once the respawn time has passed. The respawn time stays with the spawn id.
    /// </summary>
    private void RemoveGroupMemberCorpse(Creature creature, CreatureSpawn spawn)
    {
        if (_groupRespawnCleared.Remove(spawn.Guid))
        {
            ForgetGroupRespawn(spawn.Guid); // the group respawned together while this one lay dead: it comes back with the next spawn
        }
        else if (creature.RespawnAtMs > _clockMs)
        {
            _respawnAt[spawn.Guid] = creature.RespawnAtMs;
            SaveRespawnOnRemoval(creature);
        }

        foreach (LoadedGrid grid in _grids.Values)
        {
            grid.Creatures.Remove(creature);
        }

        RemoveFromWorld(creature);
    }

    /// <summary>cmangos CREATURE_GROUP_EVENT_RESPAWN / HOME with CREATURE_GROUP_RESPAWN_TOGETHER: CreatureGroup::ClearRespawnTimes.</summary>
    private void OnGroupMemberRespawned(SpawnGroupState state)
    {
        if (!state.Has(SpawnGroupFlags.RespawnTogether))
        {
            return;
        }

        foreach (SpawnGroupMember member in state.Members)
        {
            ForgetGroupRespawn(member.Guid);
            if (state.TryGetEntry(member.Guid, out uint entry)
                && _creatures.TryGetValue(ObjectGuid.WithEntry(HighGuid.Unit, entry, member.Guid), out Creature? dead)
                && dead.DeathState != CreatureDeathState.Alive)
            {
                _groupRespawnCleared.Add(member.Guid);
            }
        }
    }

    /// <summary>A member reached home after an evade (cmangos CREATURE_GROUP_EVENT_HOME).</summary>
    private void OnGroupMemberReachedHome(Creature creature)
    {
        if (creature.Spawn is { } spawn && _groupOfSpawn.TryGetValue(spawn.Guid, out SpawnGroupState? state))
        {
            OnGroupMemberRespawned(state);
        }
    }

    /// <summary>
    /// cmangos CREATURE_GROUP_EVENT_AGGRO with CREATURE_GROUP_AGGRO_TOGETHER (CreatureGroup::TriggerLinkingEvent): every member in the world
    /// engages the enemy, without calling for help itself; a member is never pulled onto another member.
    /// </summary>
    private void OnGroupMemberAggro(Creature creature, Unit enemy)
    {
        if (creature.Spawn is not { } spawn || !_groupOfSpawn.TryGetValue(spawn.Guid, out SpawnGroupState? state)
            || !state.Has(SpawnGroupFlags.AggroTogether))
        {
            return;
        }

        if (enemy is Creature { Spawn: { } enemySpawn } && state.Objects.ContainsKey(enemySpawn.Guid) && enemy.CharmerGuid.IsEmpty)
        {
            return;
        }

        if (!_groupAggroInProgress.Add((state.Id, enemy.Guid)))
        {
            return;
        }

        try
        {
            foreach ((uint guid, uint entry) in state.Objects.ToArray())
            {
                if (_creatures.TryGetValue(ObjectGuid.WithEntry(HighGuid.Unit, entry, guid), out Creature? member)
                    && !ReferenceEquals(member, creature) && member.IsAlive && !member.IsEvading)
                {
                    member.CalledAssistance = true;
                    EnterCombatWithTarget(member, enemy);
                }
            }
        }
        finally
        {
            _groupAggroInProgress.Remove((state.Id, enemy.Guid));
        }
    }

    /// <summary>cmangos CREATURE_GROUP_EVENT_EVADE with CREATURE_GROUP_EVADE_TOGETHER: the other members evade too.</summary>
    private void OnGroupMemberEvaded(Creature creature)
    {
        if (creature.Spawn is not { } spawn || !_groupOfSpawn.TryGetValue(spawn.Guid, out SpawnGroupState? state)
            || !state.Has(SpawnGroupFlags.EvadeTogether))
        {
            return;
        }

        foreach ((uint guid, uint entry) in state.Objects.ToArray())
        {
            if (_creatures.TryGetValue(ObjectGuid.WithEntry(HighGuid.Unit, entry, guid), out Creature? member) && !ReferenceEquals(member, creature))
            {
                EnterEvadeMode(member);
            }
        }
    }

    private void ForgetGroupRespawn(uint spawnGuid)
    {
        _respawnAt.Remove(spawnGuid);
        if (Persists)
        {
            _persistence!.Delete(Map.MapId, Map.InstanceId, spawnGuid);
        }
    }

    // --- ISpawnGroupHost ------------------------------------------------------------------------------

    bool ISpawnGroupHost.IsRespawnPending(uint spawnGuid) => _respawnAt.TryGetValue(spawnGuid, out long at) && at > _clockMs;

    void ISpawnGroupHost.ClearRespawn(uint spawnGuid) => ForgetGroupRespawn(spawnGuid);

    void ISpawnGroupHost.CopyRespawn(uint fromGuid, uint spawnGuid)
    {
        if (_respawnAt.TryGetValue(fromGuid, out long at))
        {
            _respawnAt[spawnGuid] = at;
            if (Persists && at > _clockMs && at != long.MaxValue)
            {
                _persistence!.Save(Map.MapId, Map.InstanceId, spawnGuid, _respawnClock.UnixSeconds + ((at - _clockMs) / 1000));
            }
        }
    }

    uint ISpawnGroupHost.OwnEntry(uint spawnGuid)
        => _groupSpawns.TryGetValue(spawnGuid, out CreatureSpawn? spawn) && spawn.Entry != 0 && _content.FindTemplate(spawn.Entry) is not null ? spawn.Entry : 0;

    uint ISpawnGroupHost.RandomSpawnEntry(uint spawnGuid)
        => _options.Respawn.AlternateEntries && SpawnEntryChooser.Choose(_content, _content.GetSpawnEntries(spawnGuid), _random) is { } template ? template.Entry : 0;

    bool ISpawnGroupHost.HasSpawnEntries(uint spawnGuid) => _options.Respawn.AlternateEntries && _content.GetSpawnEntries(spawnGuid).Count > 0;

    bool ISpawnGroupHost.ConditionHolds(SpawnGroupDefinition group) => SpawnGroupConditionHolds(group, SpawnGroupCondition);

    void ISpawnGroupHost.DespawnMembers(SpawnGroupState group)
    {
        // cmangos CreatureGroup::Despawn(0, onlyAlive = false, forcedDespawnTime = 1): out at once, back in a second when the condition holds again.
        foreach ((uint guid, uint entry) in group.Objects.ToArray())
        {
            if (_creatures.TryGetValue(ObjectGuid.WithEntry(HighGuid.Unit, entry, guid), out Creature? member))
            {
                foreach (LoadedGrid grid in _grids.Values)
                {
                    grid.Creatures.Remove(member);
                }

                RemoveFromWorld(member);
            }

            _respawnAt[guid] = _clockMs + 1000;
        }

        group.Clear();
        _logger.LogDebug("spawn group {Group} on map {MapId}: condition failed, members despawned", group.Id, Map.MapId);
    }

    /// <summary>
    /// cmangos SpawnGroup::IsWorldstateConditionSatisfied: no condition holds; a <c>conditions</c> entry asks <paramref name="condition"/>
    /// (unset or undecided: false); a world-state expression is not implemented here and never holds.
    /// </summary>
    internal static bool SpawnGroupConditionHolds(SpawnGroupDefinition group, Func<SpawnGroupDefinition, bool?>? condition)
    {
        if (group.WorldStateCondition != 0)
        {
            return condition?.Invoke(group) == true;
        }

        return group.WorldStateExpression == 0;
    }
}
