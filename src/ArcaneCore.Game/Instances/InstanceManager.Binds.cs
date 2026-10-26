using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Instances;

public sealed partial class InstanceManager
{
    /// <summary>Resolves the credited player of a creature kill (default: the killer if it is a player).</summary>
    public IKillCreditResolver KillCreditResolver { get; set; } = PlayerKillCreditResolver.Instance;

    /// <summary>
    /// A creature died on an instance map (vmangos Unit.cpp:1253-1263 -> <c>Map::BindToInstanceOrRaid</c>,
    /// Map.cpp:3526-3545). Needs a credited player (owner, charmer or tapper through
    /// <see cref="KillCreditResolver"/>); nobody credited means no bind and no reset-time change.
    /// Raids bind permanently only for a creature flagged <see cref="CreatureFlagExtraInstanceBind"/>
    /// (the IsRaid gate; the flag is classic-db's <c>ExtraFlags</c> bit, vmangos's own
    /// LOCK_TAPPERS_TO_RAID_ON_DEATH static flag is not set in that data). A normal dungeon
    /// never binds permanently: its reset time becomes respawn time + 2 h when that is later
    /// ("the reset time is set but not added to the scheduler until the players leave").
    /// </summary>
    private void OnUnitKilled(Map map, Unit? killer, Unit victim)
    {
        if (victim is not Creature creature || !_mapStates.TryGetValue(map, out InstanceMapState? mapState) || mapState.Save.IsDeleted)
        {
            return;
        }

        Player? credited = KillCreditResolver.Resolve(killer, creature);
        if (credited is null)
        {
            return;
        }

        InstanceSave save = mapState.Save;
        if (save.Template.IsRaid)
        {
            if ((creature.Template.ExtraFlags & CreatureFlagExtraInstanceBind) != 0)
            {
                PermBindAllPlayers(map, credited);
            }

            return;
        }

        if (!_options.ResetExtendsOnKills)
        {
            return;
        }

        long resetTime = Now + SecondsUntilBack(map, creature) + (2 * 60 * 60);
        if (save.ResetTime < resetTime)
        {
            save.ResetTime = resetTime;
            _persistence.InstanceSaved(save);
        }
    }

    /// <summary>
    /// vmangos <c>Creature::GetRespawnTimeEx</c> (Creature.cpp:3305-3314) relative to now: the time left to the respawn
    /// when it is ahead; else, while the corpse lies, the respawn delay plus the corpse time left; else 0. The respawn time
    /// is a map-clock value, so it is measured against the clock of the creature's system (the map's when the creature is
    /// in none); without any clock it is not read at all.
    /// </summary>
    private static long SecondsUntilBack(Map map, Creature creature)
    {
        CreatureMapSystem? clock = creature.System ?? map.FindUpdater<CreatureMapSystem>();
        if (clock is not null && creature.RespawnAtMs > clock.ClockMs)
        {
            return (creature.RespawnAtMs - clock.ClockMs) / 1000;
        }

        return creature.CorpseDecayMs > 0 ? creature.RespawnDelaySeconds + (creature.CorpseDecayMs / 1000) : 0;
    }

    /// <summary>The player's bind to <paramref name="mapId"/> (vmangos <c>Player::GetBoundInstance</c>).</summary>
    public InstanceBind? GetPlayerBind(ObjectGuid player, uint mapId) =>
        _playerBinds.TryGetValue(player, out Dictionary<uint, InstanceBind>? binds) && binds.TryGetValue(mapId, out InstanceBind bind) ? bind : null;

    /// <summary>The group's bind to <paramref name="mapId"/> (vmangos <c>Group::GetBoundInstance</c>).</summary>
    public InstanceBind? GetGroupBind(Group group, uint mapId) =>
        _groupBinds.TryGetValue(group.Id, out Dictionary<uint, InstanceBind>? binds) && binds.TryGetValue(mapId, out InstanceBind bind) ? bind : null;

    /// <summary>All binds of a player.</summary>
    public IReadOnlyCollection<InstanceBind> GetPlayerBinds(ObjectGuid player) =>
        _playerBinds.TryGetValue(player, out Dictionary<uint, InstanceBind>? binds) ? binds.Values.ToArray() : [];

    /// <summary>All binds of a group.</summary>
    public IReadOnlyCollection<InstanceBind> GetGroupBinds(Group group) =>
        _groupBinds.TryGetValue(group.Id, out Dictionary<uint, InstanceBind>? binds) ? binds.Values.ToArray() : [];

    /// <summary>
    /// The save a player would enter on <paramref name="mapId"/> (vmangos
    /// <c>Player::GetBoundInstanceSaveForSelfOrGroup</c>): its own bind if permanent or if it is
    /// ungrouped, else its group's bind, else its own bind.
    /// </summary>
    public InstanceSave? GetBoundSaveForSelfOrGroup(ObjectGuid player, uint mapId)
    {
        InstanceBind? own = GetPlayerBind(player, mapId);
        Group? group = GroupOf(player);
        if (own is { Permanent: true } || group is null)
        {
            return own?.Save;
        }

        return GetGroupBind(group, mapId)?.Save ?? own?.Save;
    }

    /// <summary>
    /// Bind everyone inside <paramref name="map"/> permanently (vmangos
    /// <c>DungeonMap::PermBindAllPlayers</c>, on the death of a creature flagged
    /// <see cref="CreatureFlagExtraInstanceBind"/>). The killer's group gets a permanent bind if
    /// its leader is inside.
    /// </summary>
    public void PermBindAllPlayers(Map map, Player? killer)
    {
        if (!_mapStates.TryGetValue(map, out InstanceMapState? mapState) || mapState.Save.IsDeleted)
        {
            return;
        }

        InstanceSave save = mapState.Save;
        Group? group = killer is null ? null : GroupOf(killer.Guid);
        foreach (Player player in map.Players.ToArray())
        {
            if (mapState.ResetAfterUnload)
            {
                // vmangos: the instance is pending reset; do not let anyone get stuck in it.
                TeleportToHomebind(player);
                continue;
            }

            InstanceBind? bind = GetPlayerBind(player.Guid, save.MapId);
            if (bind is not { Permanent: true } || bind.Value.Save != save)
            {
                BindPlayer(player.Guid, save, permanent: true);
                player.Session.Send(WorldOpcode.SmsgInstanceSaveCreated, InstancePackets.BuildInstanceSaveCreated());
            }

            if (group is not null && group.IsLeader(player.Guid))
            {
                BindGroup(group, save, permanent: true);
            }
        }
    }

    /// <summary>Drop a character's bind to a map (GM command; vmangos <c>.instance unbind</c>). Returns false when none.</summary>
    public bool UnbindPlayer(ObjectGuid player, uint mapId)
    {
        if (GetPlayerBind(player, mapId) is not { } bind)
        {
            return false;
        }

        RemovePlayerBind(player, bind.Save);
        return true;
    }

    /// <summary>
    /// The dungeon half of vmangos <c>HandleGonameCommand</c> (TeleportCommands.cpp:1262-1303): a GM going to <paramref name="target"/> inside a
    /// dungeon instance must enter that very instance. A GM in a group may go only when it is the target's group
    /// (<see cref="GmInstanceGo.GroupMismatch"/>). A GM with no bind of its own or its group's to the map is bound to the target's instance
    /// (the group when the GM leads one, else the GM; permanent when the instance cannot reset), so <see cref="ResolveEntry"/> finds it instead
    /// of creating an empty instance. A GM that is already bound keeps its bind, as vmangos does. World thread.
    /// </summary>
    public GmInstanceGo PrepareGmGoToInstanceOf(Player gm, Player target)
    {
        ArgumentNullException.ThrowIfNull(gm);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Map is not { } map || !_mapStates.TryGetValue(map, out InstanceMapState? state) || state.Save.IsDeleted)
        {
            return GmInstanceGo.NotInstance;
        }

        Group? group = GroupOf(gm.Guid);
        if (group is not null && GroupOf(target.Guid)?.Id != group.Id)
        {
            return GmInstanceGo.GroupMismatch;
        }

        InstanceSave save = state.Save;
        if (state.ResetAfterUnload || GetBoundSaveForSelfOrGroup(gm.Guid, save.MapId) is not null)
        {
            return GmInstanceGo.Ready;
        }

        if (group is not null && group.IsLeader(gm.Guid))
        {
            BindGroup(group, save, permanent: !save.CanReset);
        }
        else
        {
            BindPlayer(gm.Guid, save, permanent: !save.CanReset);
        }

        return GmInstanceGo.Ready;
    }

    // vmangos DungeonMap::BindPlayerOrGroupOnEnter.
    private void BindPlayerOrGroupOnEnter(Player player, InstanceSave save, Map map)
    {
        if (_mapStates.TryGetValue(map, out InstanceMapState? state))
        {
            state.UnloadTimerMs = 0; // somebody is in: no unload
            save.ResetScheduled = false; // and no normal reset while occupied
        }

        InstanceBind? playerBind = GetPlayerBind(player.Guid, save.MapId);
        Group? group = GroupOf(player.Guid);
        // vmangos DungeonMap::BindPlayerOrGroupOnEnter skips the rest only for a permanent bind
        // (or, here, a solo player already bound to this save). A non-permanent bind kept because
        // the player joined a group while inside this dungeon is not the group's yet: this entry
        // is what binds the group and drops the personal save (the group had none to replace it
        // on the way out).
        if (playerBind is { } own && own.Save == save && (own.Permanent || group is null))
        {
            return;
        }

        if (group is null)
        {
            BindPlayer(player.Guid, save, permanent: false);
            return;
        }

        InstanceBind? groupBind = GetGroupBind(group, save.MapId);
        if (groupBind is null)
        {
            BindGroup(group, save, permanent: false);

            // The group save replaces the members' personal non-permanent saves of this map,
            // except for members currently inside one of them.
            foreach (GroupMemberSlot slot in group.Members)
            {
                if (GetPlayerBind(slot.Guid, save.MapId) is { Permanent: false } memberBind && memberBind.Save != save)
                {
                    Player? member = _world.FindOnlinePlayer(slot.Guid);
                    if (member?.Map is { } memberMap && memberMap.MapId == save.MapId)
                    {
                        continue;
                    }

                    RemovePlayerBind(slot.Guid, memberBind.Save);
                }
            }
        }
        else if (groupBind.Value.Save != save)
        {
            _logger.LogWarning("{Player} entered {Save} while the group is bound to {GroupSave}", player.Name, save, groupBind.Value.Save);
            return;
        }

        if (GetPlayerBind(player.Guid, save.MapId) is { Permanent: false } solo && solo.Save == save)
        {
            RemovePlayerBind(player.Guid, save);
        }

        if (GetGroupBind(group, save.MapId) is { Permanent: true })
        {
            BindPlayer(player.Guid, save, permanent: true);
            player.Session.Send(WorldOpcode.SmsgInstanceSaveCreated, InstancePackets.BuildInstanceSaveCreated());
        }
    }

    private void BindPlayer(ObjectGuid player, InstanceSave save, bool permanent)
    {
        Dictionary<uint, InstanceBind> binds = PlayerBindsOf(player);
        if (binds.TryGetValue(save.MapId, out InstanceBind old) && old.Save != save)
        {
            RemovePlayerBind(player, old.Save);
            binds = PlayerBindsOf(player);
        }

        binds[save.MapId] = new InstanceBind(save, permanent);
        save.Players.Add(player);
        if (permanent)
        {
            save.CanReset = false;
        }

        _persistence.PlayerBound(player.Counter, save.InstanceId, permanent);
        VerifyBindHasSave(save, "player", player.Value);
    }

    private void BindGroup(Group group, InstanceSave save, bool permanent)
    {
        if (!_groupBinds.TryGetValue(group.Id, out Dictionary<uint, InstanceBind>? binds))
        {
            binds = [];
            _groupBinds[group.Id] = binds;
        }

        if (binds.TryGetValue(save.MapId, out InstanceBind old) && old.Save != save)
        {
            RemoveGroupBind(group, old.Save);
        }

        binds[save.MapId] = new InstanceBind(save, permanent);
        save.Groups.Add(group.Id);
        if (permanent)
        {
            save.CanReset = false;
            StoreGroupBind(group, save);
        }
        else
        {
            ForgetStoredGroupBind(group.Id, save.InstanceId, persist: true);
        }

        VerifyBindHasSave(save, "group", group.Id);
    }

    /// <summary>
    /// vmangos Group::BindToInstance (Group.cpp:2231-2250): the permanent bind is written under the group's leader. A row stored under
    /// another leader (the leader changed since) moves to the current one.
    /// </summary>
    private void StoreGroupBind(Group group, InstanceSave save)
    {
        uint leader = group.LeaderGuid.Counter;
        (uint, uint) key = (group.Id, save.InstanceId);
        if (_storedGroupBinds.TryGetValue(key, out uint stored))
        {
            if (stored == leader)
            {
                return;
            }

            _persistence.GroupUnbound(stored, save.InstanceId);
        }

        _storedGroupBinds[key] = leader;
        _persistence.GroupBound(leader, save.InstanceId, permanent: true);
    }

    private void ForgetStoredGroupBind(uint groupId, uint instanceId, bool persist)
    {
        if (_storedGroupBinds.Remove((groupId, instanceId), out uint leader) && persist)
        {
            _persistence.GroupUnbound(leader, instanceId);
        }
    }

    // vmangos ObjectMgr::LoadGroups, group_instance part (ObjectMgr.cpp:5463-5513): a row of an instance that is gone is dropped.
    private void LoadStoredGroupBinds(IReadOnlyList<GroupInstanceBindRecord> rows)
    {
        foreach (GroupInstanceBindRecord row in rows)
        {
            uint leader = (uint)row.LeaderCharacterId;
            if (!row.Permanent || !_saves.TryGetValue(row.InstanceId, out InstanceSave? save))
            {
                _persistence.GroupUnbound(leader, row.InstanceId);
                continue;
            }

            if (!_pendingGroupBinds.TryGetValue(leader, out List<InstanceSave>? pending))
            {
                pending = [];
                _pendingGroupBinds[leader] = pending;
            }

            if (pending.Any(p => p.MapId == save.MapId))
            {
                _persistence.GroupUnbound(leader, row.InstanceId); // one bind per map and group (vmangos keeps the first loaded)
                continue;
            }

            pending.Add(save);
            save.StoredGroupLeaders.Add(leader);
            save.CanReset = false;
        }
    }

    /// <summary>
    /// Give the stored permanent binds of <paramref name="group"/>'s leader back to the group (vmangos attaches the <c>group_instance</c>
    /// rows of a leader to the group it reloads, ObjectMgr.cpp:5463-5513). Groups are not stored yet, so this runs when the leader
    /// forms a group (<see cref="OnGroupMemberAdded"/>); a group loader calls it for every group it restores. A stored bind to a map
    /// the group is already permanently bound to elsewhere is dropped. Members become permanently bound as they enter
    /// (<see cref="BindPlayerOrGroupOnEnter"/>).
    /// </summary>
    public void RestoreStoredGroupBinds(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);
        uint leader = group.LeaderGuid.Counter;
        if (!_pendingGroupBinds.Remove(leader, out List<InstanceSave>? pending))
        {
            return;
        }

        foreach (InstanceSave save in pending)
        {
            save.StoredGroupLeaders.Remove(leader);
            if (save.IsDeleted)
            {
                continue;
            }

            if (GetGroupBind(group, save.MapId) is { Permanent: true } existing && existing.Save != save)
            {
                _persistence.GroupUnbound(leader, save.InstanceId);
                ForgetIfUnused(save);
                continue;
            }

            _storedGroupBinds[(group.Id, save.InstanceId)] = leader; // the row is there already
            BindGroup(group, save, permanent: true);
        }
    }

    // A deleted character's stored group binds (the rows go with the character's data, GroupInstanceBindDataModule).
    private void ForgetStoredGroupBindsOfLeader(uint leader)
    {
        if (_pendingGroupBinds.Remove(leader, out List<InstanceSave>? pending))
        {
            foreach (InstanceSave save in pending)
            {
                save.StoredGroupLeaders.Remove(leader);
                ForgetIfUnused(save);
            }
        }
    }

    // Dictionary removal only: no HashSet, persistence or ForgetIfUnused step, so ResetSave, UnbindAll and Remove*Bind each keep their own.
    // The player variant drops its emptied inner dictionary; the group variant never has.
    private void TryDetachPlayerBind(ObjectGuid player, InstanceSave save)
    {
        if (_playerBinds.TryGetValue(player, out Dictionary<uint, InstanceBind>? binds)
            && binds.TryGetValue(save.MapId, out InstanceBind bind) && bind.Save == save)
        {
            binds.Remove(save.MapId);
            if (binds.Count == 0)
            {
                _playerBinds.Remove(player);
            }
        }
    }

    private void TryDetachGroupBind(uint groupId, InstanceSave save)
    {
        if (_groupBinds.TryGetValue(groupId, out Dictionary<uint, InstanceBind>? binds)
            && binds.TryGetValue(save.MapId, out InstanceBind bind) && bind.Save == save)
        {
            binds.Remove(save.MapId);
        }
    }

    private void RemovePlayerBind(ObjectGuid player, InstanceSave save)
    {
        TryDetachPlayerBind(player, save);

        if (save.Players.Remove(player))
        {
            _persistence.PlayerUnbound(player.Counter, save.InstanceId);
        }

        ForgetIfUnused(save);
    }

    private void RemoveGroupBind(Group group, InstanceSave save)
    {
        TryDetachGroupBind(group.Id, save);

        save.Groups.Remove(group.Id);
        ForgetStoredGroupBind(group.Id, save.InstanceId, persist: true);
        ForgetIfUnused(save);
    }

    // vmangos MapPersistentState::UnloadIfEmpty: a save nobody is bound to and no map uses goes.
    private void ForgetIfUnused(InstanceSave save)
    {
        if (!save.IsDeleted && !save.HasBinds && _world.FindMap(save.MapId, save.InstanceId) is null)
        {
            DeleteSave(save);
        }
    }

    private void ConvertInstancesToGroup(ObjectGuid leader, Group group)
    {
        foreach (InstanceBind bind in GetPlayerBinds(leader))
        {
            BindGroup(group, bind.Save, bind.Permanent);
            if (!bind.Permanent)
            {
                RemovePlayerBind(leader, bind.Save);
            }
        }
    }

    private Dictionary<uint, InstanceBind> PlayerBindsOf(ObjectGuid player)
    {
        if (!_playerBinds.TryGetValue(player, out Dictionary<uint, InstanceBind>? binds))
        {
            binds = [];
            _playerBinds[player] = binds;
        }

        return binds;
    }
}

/// <summary>The outcome of <see cref="InstanceManager.PrepareGmGoToInstanceOf"/>.</summary>
public enum GmInstanceGo
{
    /// <summary>The target is not in a dungeon instance: nothing was done.</summary>
    NotInstance,

    /// <summary>The GM may go: it is bound to (or already holds a bind that decides) the instance it enters.</summary>
    Ready,

    /// <summary>The GM is in a group other than the target's (vmangos <c>LANG_CANNOT_GO_TO_INST_PARTY</c>).</summary>
    GroupMismatch,
}
