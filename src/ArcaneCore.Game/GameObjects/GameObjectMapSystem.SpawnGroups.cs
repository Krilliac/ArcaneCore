using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps.SpawnGroups;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// Alternative entries and cmangos game object spawn groups (Maps/SpawnGroup.cpp GameObjectGroup). A spawn with
/// <c>gameobject_spawn_entry</c> rows becomes one of their entries, chosen uniformly when the object is created (cmangos
/// ObjectMgr::GetRandomGameObjectEntry, an <c>irand</c> over the list: the rows carry no weight). A spawn that belongs to a group is
/// created only when its group chose it: the group keeps up to its maximum of members in the world and picks each one's entry (always
/// anew, game objects keep no choice). A member that despawns (looted, used up) leaves the world with its respawn time, and the group
/// brings it, or another member, back once that time has passed, so the one-of-N quest objects and chests move between their spots.
/// </summary>
public sealed partial class GameObjectMapSystem : ISpawnGroupHost
{
    private readonly Dictionary<uint, SpawnGroupState> _spawnGroups = [];
    private readonly Dictionary<uint, SpawnGroupState> _groupOfSpawn = [];
    private readonly Dictionary<uint, GameObjectSpawn> _groupSpawns = [];

    /// <summary>
    /// Whether a spawn group's condition (<c>spawn_group.WorldState</c>) holds now; null when it cannot be decided. Unset, or undecided,
    /// a group with a condition does not spawn. The world wires it.
    /// </summary>
    public Func<SpawnGroupDefinition, bool?>? SpawnGroupCondition { get; set; }

    /// <summary>The members of spawn group <paramref name="groupId"/> in the world now, with their entries (GM, tests).</summary>
    public IReadOnlyDictionary<uint, uint> SpawnGroupObjects(uint groupId)
        => _spawnGroups.TryGetValue(groupId, out SpawnGroupState? state) ? state.Objects : new Dictionary<uint, uint>();

    /// <summary>The effective maximum of spawn group <paramref name="groupId"/> on this map, or null when the map does not hold it.</summary>
    public int? SpawnGroupMaxCount(uint groupId) => _spawnGroups.TryGetValue(groupId, out SpawnGroupState? state) ? state.MaxCount : null;

    /// <summary>One of the spawn's alternative entries that has a template, uniformly; null when none has one.</summary>
    private GameObjectTemplate? ChooseSpawnEntry(IReadOnlyList<uint> entries)
    {
        var usable = new List<GameObjectTemplate>(entries.Count);
        foreach (uint entry in entries)
        {
            if (_content.FindTemplate(entry) is { } template)
            {
                usable.Add(template);
            }
        }

        return usable.Count switch
        {
            0 => null,
            1 => usable[0],
            _ => usable[Random.Next(usable.Count)],
        };
    }

    private void InitializeSpawnGroups()
    {
        if (_content.SpawnGroups.Count == 0)
        {
            return;
        }

        var spawns = new Dictionary<uint, GameObjectSpawn>();
        foreach (GameObjectSpawn spawn in _content.GetSpawns(Map.MapId))
        {
            spawns[spawn.Guid] = spawn;
        }

        bool dungeon = Map.Template?.IsDungeon == true;
        foreach (SpawnGroupDefinition group in _content.SpawnGroups.OfType(SpawnGroupType.GameObject))
        {
            if (group.Members.Count == 0 || !spawns.ContainsKey(group.Members[0].Guid))
            {
                continue; // cmangos SpawnManager::Initialize: a group lives on the map of its first spawn
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

    private void UpdateSpawnGroups()
    {
        foreach (SpawnGroupState state in _spawnGroups.Values)
        {
            foreach ((uint guid, uint entry) in state.Spawn(this, Random))
            {
                GameObjectSpawn spawn = _groupSpawns[guid];
                _respawnAt.Remove(guid);
                if (_grids.TryGetValue(CreatureMapSystem.ComputeGrid(spawn.X, spawn.Y), out List<GameObject>? list))
                {
                    CreateGroupMember(list, state, spawn, entry);
                }
            }
        }
    }

    /// <summary>The grid of a group member loads: it is created only when the group chose it, as the entry the group chose.</summary>
    private void LoadGroupMember(List<GameObject> list, SpawnGroupState state, GameObjectSpawn spawn)
    {
        if (state.TryGetEntry(spawn.Guid, out uint entry))
        {
            CreateGroupMember(list, state, spawn, entry);
        }
    }

    private void CreateGroupMember(List<GameObject> list, SpawnGroupState state, GameObjectSpawn spawn, uint entry)
    {
        if (_objects.ContainsKey(SpawnObjectGuid(entry, spawn.Guid)))
        {
            return;
        }

        if (_content.FindTemplate(entry) is not { } template)
        {
            state.Remove(spawn.Guid, this);
            return;
        }

        _respawnAt.Remove(spawn.Guid);
        LoadSpawn(list, spawn, template);
    }

    /// <summary>
    /// A group member despawned (cmangos: a dynamic-guid object is removed and its group brings it back): it leaves the world with its respawn
    /// time kept for the spawn id, and the group may choose it, or another member, when that time has passed.
    /// </summary>
    private void DespawnGroupMember(GameObject go, GameObjectSpawn spawn)
    {
        if (go.IsSpawned)
        {
            if (go.Template.IsDespawnAtAction() || go.GetUInt32(UpdateFields.GameobjectAnimprogress) > 0)
            {
                byte[] anim = GameObjectPackets.DespawnAnim(go.Guid);
                foreach (Entities.Player observer in Map.ObserversOf(go))
                {
                    observer.Session.Send(WorldOpcode.SmsgGameobjectDespawnAnim, anim);
                }
            }
        }

        _respawnAt[spawn.Guid] = spawn.SpawnTimeSeconds >= 0 ? _clockMs + RespawnDelayMs(go) : long.MaxValue;
        Remove(go);
    }

    /// <summary>A group member is no longer tracked (despawned, removed, its grid unloaded): the group may bring another.</summary>
    private void OnGroupMemberUntracked(GameObject go)
    {
        if (go.Spawn is { } spawn && _groupOfSpawn.TryGetValue(spawn.Guid, out SpawnGroupState? state)
            && state.TryGetEntry(spawn.Guid, out uint entry) && entry == go.Entry)
        {
            state.Remove(spawn.Guid, this);
        }
    }

    // --- ISpawnGroupHost ------------------------------------------------------------------------------

    bool ISpawnGroupHost.IsRespawnPending(uint spawnGuid) => _respawnAt.TryGetValue(spawnGuid, out long at) && at > _clockMs;

    void ISpawnGroupHost.ClearRespawn(uint spawnGuid) => _respawnAt.Remove(spawnGuid);

    void ISpawnGroupHost.CopyRespawn(uint fromGuid, uint spawnGuid)
    {
        if (_respawnAt.TryGetValue(fromGuid, out long at))
        {
            _respawnAt[spawnGuid] = at;
        }
    }

    uint ISpawnGroupHost.OwnEntry(uint spawnGuid)
        => _groupSpawns.TryGetValue(spawnGuid, out GameObjectSpawn? spawn) && spawn.Entry != 0 && _content.FindTemplate(spawn.Entry) is not null ? spawn.Entry : 0;

    uint ISpawnGroupHost.RandomSpawnEntry(uint spawnGuid) => ChooseSpawnEntry(_content.GetSpawnEntries(spawnGuid))?.Entry ?? 0;

    bool ISpawnGroupHost.HasSpawnEntries(uint spawnGuid) => _content.GetSpawnEntries(spawnGuid).Count > 0;

    bool ISpawnGroupHost.ConditionHolds(SpawnGroupDefinition group) => CreatureMapSystem.SpawnGroupConditionHolds(group, SpawnGroupCondition);

    void ISpawnGroupHost.DespawnMembers(SpawnGroupState group)
    {
        // cmangos GameObjectGroup::Despawn(0, forcedDespawnTime = 1): out at once, back a second later when the condition holds again.
        foreach ((uint guid, uint entry) in group.Objects.ToArray())
        {
            if (_objects.TryGetValue(SpawnObjectGuid(entry, guid), out GameObject? member))
            {
                Remove(member);
            }

            _respawnAt[guid] = _clockMs + 1000;
        }

        group.Clear();
    }
}
