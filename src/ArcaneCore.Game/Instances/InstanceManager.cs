using System.Diagnostics.CodeAnalysis;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Instances;

/// <summary>
/// Dungeon and raid instances, 1.12 rules (vmangos <c>MapPersistentStateManager</c>,
/// <c>DungeonMap</c>, the instance parts of <c>Player</c> and <c>Group</c>): one
/// <see cref="Map"/> per (map, instance id), created when a player or group without a bind
/// enters and unloaded after it has been empty for <see cref="InstanceOptions.UnloadDelayMs"/>;
/// player and group binds; raid lockouts with a global reset schedule; normal dungeons that
/// reset two hours after creation while empty or when the group leader / solo player resets
/// them (CMSG_RESET_INSTANCES); raid group requirement, player cap and the homebind timer of
/// players who lost their right to be inside. See docs/integration/instances.md.
/// <para>Install with <see cref="Install"/> (sets <see cref="WorldRuntime.MapResolver"/>).</para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed partial class InstanceManager : IMapResolver
{
    /// <summary>vmangos <c>CREATURE_FLAG_EXTRA_INSTANCE_BIND</c>: killing the creature binds everyone inside permanently.</summary>
    public const uint CreatureFlagExtraInstanceBind = 0x1;

    /// <summary>Lowest instance id handed out: above vmangos <c>RESERVED_INSTANCES_LAST</c> (100); 0 is the shared copy of a map.</summary>
    public const uint FirstInstanceId = 101;

    private const long Day = 24 * 60 * 60;

    private readonly WorldRuntime _world;
    private readonly InstanceOptions _options;
    private readonly IInstancePersistence _persistence;
    private readonly Func<long> _unixNow;
    private readonly ILogger _logger;
    private readonly Dictionary<uint, InstanceSave> _saves = [];
    private readonly Dictionary<ObjectGuid, Dictionary<uint, InstanceBind>> _playerBinds = [];
    private readonly Dictionary<uint, Dictionary<uint, InstanceBind>> _groupBinds = [];

    // Permanent group binds that are stored, with the leader (character id) the row is stored under (vmangos group_instance.leader_guid).
    private readonly Dictionary<(uint GroupId, uint InstanceId), uint> _storedGroupBinds = [];

    // Stored permanent group binds loaded at startup that no group has taken back yet, by leader character id.
    private readonly Dictionary<uint, List<InstanceSave>> _pendingGroupBinds = [];
    private readonly Dictionary<Map, InstanceMapState> _mapStates = [];
    private readonly Dictionary<ObjectGuid, PlayerState> _players = [];
    private readonly Dictionary<ObjectGuid, (uint MapId, uint InstanceId)> _lastInstance = [];
    private readonly Dictionary<uint, RaidSchedule> _raidSchedules = [];
    private readonly Entry.InstanceEnterLimiter _enterLimiter = new();
    private uint _nextInstanceId = FirstInstanceId;
    private bool _installed;

    public InstanceManager(WorldRuntime world, InstanceOptions? options = null, IInstancePersistence? persistence = null, Func<long>? unixNow = null, ILogger? logger = null)
    {
        _world = world;
        _options = options ?? new InstanceOptions();
        _persistence = persistence ?? NullInstancePersistence.Instance;
        _unixNow = unixNow ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The group of a player (default: none; the world feature wires the social layer's group manager).</summary>
    public Func<ObjectGuid, Group?> GroupOf { get; set; } = static _ => null;

    /// <summary>Sends a player to its bind point (default: does nothing; the world feature wires <see cref="TeleportService.TeleportToHomebind"/>).</summary>
    public Func<Player, bool> TeleportToHomebind { get; set; } = static _ => false;

    /// <summary>Quest journal check for ScriptDev2's quest-only instance chests.</summary>
    public Func<Player, uint, bool> QuestCompleteUnrewarded { get; set; } = static (_, _) => false;

    /// <summary>The live game-event state for the scripts (vmangos <c>sGameEventMgr.IsActiveEvent</c>); nothing runs until the world wires it.</summary>
    public Func<ushort, bool> GameEventActive { get; set; } = static _ => false;

    /// <summary>The live holiday state for the scripts (cmangos <c>IsHolidayActive</c>); nothing runs until the world wires it.</summary>
    public Func<uint, bool> HolidayActive { get; set; } = static _ => false;

    /// <summary>Shows a system chat line to a player (default: none).</summary>
    public Action<Player, string> SystemMessage { get; set; } = static (_, _) => { };

    public InstanceOptions Options => _options;

    /// <summary>
    /// The resolver of battleground maps (the battleground feature installs it). A battleground map is not a dungeon, so without it a player
    /// would enter the shared copy (instance 0); with it every resolver call for a battleground map is delegated, and the instance rules of
    /// this manager never see one (vmangos <c>MapManager::CreateBgMap</c> keeps battleground maps apart from the dungeon instances).
    /// </summary>
    public IMapResolver? BattlegroundMaps { get; set; }

    /// <summary>
    /// Optional durable block allocator for instance IDs when cluster ID range reservation is enabled.
    /// </summary>
    public DurableIdBlockAllocator? IdBlockAllocator { get; set; }

    private uint NextRawInstanceId()
    {
        if (IdBlockAllocator is { } allocator)
        {
            uint allocated = (uint)allocator.NextInt32();
            if (allocated >= _nextInstanceId && allocated < (uint)int.MaxValue)
                _nextInstanceId = allocated + 1;
            return allocated;
        }

        if (_nextInstanceId == 0 || _nextInstanceId > (uint)int.MaxValue)
            throw new IdRangeExhaustedException(IdRangeScopes.Instance, "instance ID space exhausted");

        return _nextInstanceId++;
    }

    /// <summary>
    /// A fresh instance id from the counter the dungeon saves use, for a map that is not a dungeon save (a battleground match; vmangos
    /// <c>MapManager::GenerateInstanceId</c> serves both). World thread.
    /// </summary>
    public uint AllocateInstanceId()
    {
        uint id = NextRawInstanceId();
        while (_saves.ContainsKey(id))
        {
            id = NextRawInstanceId();
        }

        return id;
    }

    /// <summary>
    /// Raised on the world thread when a logical save is deleted for good (a real reset, a
    /// delete after nobody is bound, or the startup drop of an unbound or expired save), after
    /// its storage delete was queued. The state kept per save (chest loot) follows it.
    /// </summary>
    public event Action<uint>? InstanceDeleted;

    /// <summary>All live saves (loaded or not).</summary>
    public IReadOnlyCollection<InstanceSave> Saves => _saves.Values;

    private MapRegistry Registry => WorldMaps.Of(_world).Registry;

    private long Now => _unixNow();

    /// <summary>Become the world's map resolver and follow map unloads. Call <see cref="Load"/> once the map registry is loaded.</summary>
    public void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        _world.MapResolver = this;
        _world.MapUnloading += OnMapUnloading;
        _world.PlayerLoggingOut += OnPlayerLoggingOut;
    }

    /// <summary>
    /// Load persisted state (startup, after the map registry is loaded; vmangos
    /// <c>MapPersistentStateManager::LoadCreatureRespawnTimes</c>' neighbours
    /// <c>CleanupInstances</c>, <c>PackInstances</c>, <c>LoadResetTimes</c>, then
    /// <c>Player::_LoadBoundInstances</c>). Unknown maps, non-dungeon maps, expired instances and
    /// instances nobody is bound to are dropped (and deleted from storage).
    /// </summary>
    public void Load(InstanceStoreSnapshot snapshot)
    {
        long now = Now;
        InitializeRaidSchedules(snapshot.ResetTimes);
        var boundIds = snapshot.Binds.Select(b => b.InstanceId)
            .Concat(snapshot.GroupBinds.Where(b => b.Permanent).Select(b => b.InstanceId)).ToHashSet();
        foreach (InstanceRecord record in snapshot.Instances)
        {
            _nextInstanceId = Math.Max(_nextInstanceId, record.Id + 1);
            MapTemplate? template = Registry.Find(record.MapId);
            bool expired = template is not null && template.IsRaid
                ? record.ResetTime != 0 && record.ResetTime <= now
                : false;
            if (template is null || !template.IsDungeon || expired || !boundIds.Contains(record.Id))
            {
                _logger.LogInformation("dropping instance {Instance} of map {Map} (unknown map, expired or unbound)", record.Id, record.MapId);
                _persistence.InstanceDeleted(record.Id);
                continue;
            }

            var save = new InstanceSave(record.Id, template, record.ResetTime);
            save.Data = record.Data;
            if (!template.IsRaid)
            {
                save.ResetScheduled = true; // nothing is loaded: the normal reset is armed
            }

            _saves[save.InstanceId] = save;
        }

        foreach (CharacterInstanceBindRecord bind in snapshot.Binds)
        {
            if (!_saves.TryGetValue(bind.InstanceId, out InstanceSave? save))
            {
                _persistence.PlayerUnbound((uint)bind.CharacterId, bind.InstanceId);
                continue;
            }

            ObjectGuid guid = ObjectGuid.Player((uint)bind.CharacterId);
            Dictionary<uint, InstanceBind> binds = PlayerBindsOf(guid);
            if (binds.TryGetValue(save.MapId, out InstanceBind existing) && existing.Save != save)
            {
                // Two binds for one map: keep the permanent one (vmangos keeps the first loaded).
                if (existing.Permanent || !bind.Permanent)
                {
                    _persistence.PlayerUnbound((uint)bind.CharacterId, bind.InstanceId);
                    continue;
                }

                existing.Save.Players.Remove(guid);
                _persistence.PlayerUnbound((uint)bind.CharacterId, existing.Save.InstanceId);
            }

            binds[save.MapId] = new InstanceBind(save, bind.Permanent);
            save.Players.Add(guid);
            if (bind.Permanent)
            {
                save.CanReset = false;
            }
        }

        LoadStoredGroupBinds(snapshot.GroupBinds);

        foreach (InstanceSave save in _saves.Values.Where(s => !s.HasBinds).ToArray())
        {
            DeleteSave(save);
        }

        foreach (CharacterLastInstanceRecord last in snapshot.LastInstances)
        {
            _lastInstance[ObjectGuid.Player((uint)last.CharacterId)] = (last.MapId, last.InstanceId);
        }

        _logger.LogInformation("loaded {Saves} instance saves, {Binds} character binds", _saves.Count, _playerBinds.Values.Sum(b => b.Count));
        Loaded?.Invoke();
    }

    public InstanceSave? FindSave(uint instanceId) => _saves.GetValueOrDefault(instanceId);

    /// <summary>Whether the player may stay in its instance (vmangos <c>Player::m_instanceValid</c>).</summary>
    public bool IsInstanceValid(Player player) => !_players.TryGetValue(player.Guid, out PlayerState? state) || state.Valid;

    /// <summary>Milliseconds left on the player's homebind timer, 0 when none runs.</summary>
    public uint HomebindTimerMs(Player player) => _players.TryGetValue(player.Guid, out PlayerState? state) ? state.HomebindTimerMs : 0;

    // ---- IMapResolver -------------------------------------------------------------------

    // ---- packets ------------------------------------------------------------------------

    /// <summary>SMSG_RAID_INSTANCE_INFO: the player's permanent binds (vmangos <c>Player::SendRaidInfo</c>; login and CMSG_REQUEST_RAID_INFO).</summary>
    public void SendRaidInfo(Player player)
    {
        long now = Now;
        var entries = new List<InstancePackets.RaidInfo>();
        foreach (InstanceBind bind in GetPlayerBinds(player.Guid).Where(b => b.Permanent).OrderBy(b => b.Save.MapId))
        {
            long reset = GetRaidResetTime(bind.Save.MapId);
            if (reset == 0)
            {
                reset = bind.Save.ResetTime;
            }

            entries.Add(new InstancePackets.RaidInfo(bind.Save.MapId, (uint)Math.Max(0, reset - now), bind.Save.InstanceId));
        }

        player.Session.Send(WorldOpcode.SmsgRaidInstanceInfo, InstancePackets.BuildRaidInstanceInfo(entries));
    }

    /// <summary>
    /// CMSG_RESET_INSTANCES (vmangos <c>WorldSession::HandleResetInstancesOpcode</c>): the group
    /// leader resets the group's normal dungeons, an ungrouped player its own. Groupmates who
    /// are not the leader are ignored.
    /// </summary>
    public void HandleResetInstances(Player player)
    {
        Group? group = GroupOf(player.Guid);
        if (group is null)
        {
            ResetPlayerInstances(player, groupJoin: false);
        }
        else if (group.IsLeader(player.Guid))
        {
            ResetGroupInstances(group, disband: false, player);
        }
    }

    // ---- binds --------------------------------------------------------------------------

    /// <summary>
    /// Forget everything about a deleted character (binds and last instance). The character
    /// delete flow has no hook yet; storage rows of deleted characters are also dropped at
    /// startup by <see cref="IInstanceStore.LoadAsync"/>.
    /// </summary>
    public void DeleteCharacter(ObjectGuid player)
    {
        foreach (InstanceBind bind in GetPlayerBinds(player))
        {
            RemovePlayerBind(player, bind.Save);
        }

        _lastInstance.Remove(player);
        _players.Remove(player);
        ForgetStoredGroupBindsOfLeader(player.Counter);
    }

    // ---- group events (wired to GroupManager by the world feature) ----------------------

    /// <summary>
    /// A player joined a group, or a group was created with its leader (vmangos
    /// <c>Group::Create</c> → <c>ConvertInstancesToGroup</c>; <c>Group::AddMember</c> →
    /// <c>Player::ResetInstances(INSTANCE_RESET_GROUP_JOIN)</c> and the homebind cancel).
    /// </summary>
    public void OnGroupMemberAdded(Group group, ObjectGuid member)
    {
        if (group.IsLeader(member) && group.MemberCount <= 1)
        {
            ConvertInstancesToGroup(member, group);
            RestoreStoredGroupBinds(group);
            return;
        }

        Player? player = _world.FindOnlinePlayer(member);
        if (player is null)
        {
            return;
        }

        ResetPlayerInstances(player, groupJoin: true);

        // vmangos AddMember: rejoining the group that owns the instance the player is in makes
        // its presence valid again (cancels the homebind timer).
        if (player.Map is { } map && _mapStates.ContainsKey(map)
            && GetGroupBind(group, map.MapId) is { } groupBind && groupBind.Save.InstanceId == map.InstanceId)
        {
            StateFor(player).Valid = true;
        }
    }

    /// <summary>A member left or was removed (vmangos <c>Group::RemoveMember</c> → <c>_homebindIfInstance</c>).</summary>
    public void OnGroupMemberRemoved(Group group, ObjectGuid member)
    {
        if (_world.FindOnlinePlayer(member) is { } player)
        {
            HomebindIfInstance(player);
        }
    }

    /// <summary>
    /// A group is about to be disbanded; its members are still listed (vmangos
    /// <c>Group::Disband</c>: members other than <paramref name="initiator"/> who stay in the
    /// instance keep it; the group's binds are dropped and resettable instances reset).
    /// </summary>
    public void OnGroupDisbanding(Group group, ObjectGuid initiator)
    {
        Player? remaining = null;
        foreach (GroupMemberSlot slot in group.Members)
        {
            if (_world.FindOnlinePlayer(slot.Guid) is not { } player)
            {
                continue;
            }

            if (initiator.IsEmpty || slot.Guid == initiator)
            {
                HomebindIfInstance(player);
            }
            else
            {
                remaining = player;
            }
        }

        // vmangos Group::Disband: the player left in the dungeon gets the group's instance as a
        // solo bind, so the disband does not throw it out.
        if (remaining?.Map is { } map && _mapStates.ContainsKey(map)
            && GetGroupBind(group, map.MapId) is { Permanent: false } groupBind
            && groupBind.Save.InstanceId == map.InstanceId
            && GetPlayerBind(remaining.Guid, map.MapId) is null)
        {
            BindPlayer(remaining.Guid, groupBind.Save, permanent: false);
            RemoveGroupBind(group, groupBind.Save);
        }

        ResetGroupInstances(group, disband: true, null);
        foreach (InstanceBind left in GetGroupBinds(group))
        {
            ForgetStoredGroupBind(group.Id, left.Save.InstanceId, persist: true); // vmangos Group::Disband: DELETE FROM group_instance
        }

        _groupBinds.Remove(group.Id);
    }

    /// <summary>
    /// The leader changed (vmangos <c>Group::ChangeLeader</c>): permanent group binds are
    /// dropped, a temporary one too when the new leader has its own bind there (the old leader
    /// keeps it as a solo bind), and the new leader's binds become the group's.
    /// </summary>
    public void OnGroupLeaderChanged(Group group, ObjectGuid oldLeader)
    {
        ObjectGuid newLeader = group.LeaderGuid;
        foreach (InstanceBind bind in GetGroupBinds(group))
        {
            if (bind.Permanent)
            {
                RemoveGroupBind(group, bind.Save);
            }
            else if (GetPlayerBind(newLeader, bind.Save.MapId) is not null)
            {
                if (_world.IsOnline(oldLeader) && GetPlayerBind(oldLeader, bind.Save.MapId) is null)
                {
                    BindPlayer(oldLeader, bind.Save, permanent: false);
                }

                RemoveGroupBind(group, bind.Save);
            }
        }

        ConvertInstancesToGroup(newLeader, group);
    }

    // ---- schedule -----------------------------------------------------------------------

    // ---- per-map callbacks (InstanceMapUpdater) ----------------------------------------

    // ---- internals ----------------------------------------------------------------------

    // vmangos Player::ResetInstances (INSTANCE_RESET_ALL from CMSG_RESET_INSTANCES or GROUP_JOIN).
    private void ResetPlayerInstances(Player player, bool groupJoin)
    {
        foreach (InstanceBind bind in GetPlayerBinds(player.Guid))
        {
            InstanceSave save = bind.Save;
            if (!save.CanReset || bind.Permanent)
            {
                continue;
            }

            if (player.IsInWorld && player.MapId == save.MapId)
            {
                continue; // cannot reset the instance you are in
            }

            if (!groupJoin && save.Template.IsRaid)
            {
                continue; // "reset all instances" only resets normal dungeons
            }

            Map? map = _world.FindMap(save.MapId, save.InstanceId);
            if (map is { PlayerCount: > 0 } || map is { TransitCount: > 0 })
            {
                // Someone else is inside (the player itself was skipped above), or is still on its way out: a far
                // teleport that is not acknowledged yet can fail and send that player back in (vmangos
                // HandleReturnOnTeleportFail), which is why the timed reset waits for TransitCount too (UpdateSchedule).
                // The save is theirs too: do not delete it under them. A group join drops just the joiner's own bind, as
                // vmangos does. A reset request is an ArcaneCore choice: it is refused the way the group reset refuses an
                // occupied instance (SMSG_INSTANCE_RESET_FAILED, the players inside are asked to leave, at most once per
                // InstanceOptions.ResetRefusedNoticeSeconds) and the requester stays bound. vmangos Player::ResetInstances
                // instead sends SMSG_INSTANCE_RESET and drops only the requester's bind, which reports a reset that did not
                // happen and leaves the requester free to make a new instance beside the occupied one.
                if (groupJoin)
                {
                    RemovePlayerBind(player.Guid, save);
                }
                else
                {
                    AskToLeaveForReset(map, rateLimited: true);
                    player.Session.Send(WorldOpcode.SmsgInstanceResetFailed, InstancePackets.BuildInstanceResetFailed(InstanceResetFailedReason.General, save.MapId));
                }

                continue;
            }

            if (map is not null)
            {
                ResetLoadedMap(map, global: false, notifyInside: false);
            }

            if (!groupJoin)
            {
                player.Session.Send(WorldOpcode.SmsgInstanceReset, InstancePackets.BuildInstanceReset(save.MapId));
            }

            ResetSave(save);
        }
    }

    // vmangos Group::ResetInstances (INSTANCE_RESET_ALL from the leader, or GROUP_DISBAND).
    private void ResetGroupInstances(Group group, bool disband, Player? sendTo)
    {
        foreach (InstanceBind bind in GetGroupBinds(group))
        {
            InstanceSave save = bind.Save;
            if (!save.CanReset && !disband)
            {
                continue;
            }

            if (!disband)
            {
                if (save.Template.IsRaid)
                {
                    continue;
                }

                if (sendTo is not null && group.Members.Any(m => !_world.IsOnline(m.Guid)))
                {
                    sendTo.Session.Send(WorldOpcode.SmsgInstanceResetFailed, InstancePackets.BuildInstanceResetFailed(InstanceResetFailedReason.Offline, save.MapId));
                    continue;
                }
            }

            bool isEmpty = true;
            Map? map = _world.FindMap(save.MapId, save.InstanceId);
            if (map is not null && !(disband && !save.CanReset))
            {
                isEmpty = ResetLoadedMap(map, global: false, notifyInside: !disband);
            }

            if (sendTo is not null)
            {
                sendTo.Session.Send(
                    isEmpty ? WorldOpcode.SmsgInstanceReset : WorldOpcode.SmsgInstanceResetFailed,
                    isEmpty ? InstancePackets.BuildInstanceReset(save.MapId) : InstancePackets.BuildInstanceResetFailed(InstanceResetFailedReason.General, save.MapId));
            }

            if (isEmpty || disband)
            {
                if (save.CanReset)
                {
                    RemoveGroupBind(group, save);
                    ResetSave(save);
                }
                else
                {
                    RemoveGroupBind(group, save); // others are permanently bound: just unbind the group
                }
            }
        }
    }

    // vmangos DungeonMap::Reset: returns whether the map is empty.
    private bool ResetLoadedMap(Map map, bool global, bool notifyInside)
    {
        if (!_mapStates.TryGetValue(map, out InstanceMapState? state))
        {
            return map.PlayerCount == 0;
        }

        if (map.PlayerCount > 0)
        {
            if (notifyInside)
            {
                AskToLeaveForReset(map, rateLimited: false);
            }
            else
            {
                if (global)
                {
                    foreach (Player player in map.Players)
                    {
                        StateFor(player).Valid = false;
                    }
                }

                state.UnloadWhenEmpty = true;
                state.ResetAfterUnload = true;
            }
        }
        else
        {
            state.UnloadTimerMs = InstanceOptions.MinUnloadDelayMs;
            state.ResetAfterUnload = true;
        }

        return map.PlayerCount == 0;
    }

    /// <summary>
    /// vmangos Player::SendResetFailedNotify (LANG_LEAVE_TO_RESET_INSTANCE, Player.cpp:17077-17080) to everyone inside. The refused
    /// personal reset sends it at most once per <see cref="InstanceOptions.ResetRefusedNoticeSeconds"/> per instance map.
    /// </summary>
    private void AskToLeaveForReset(Map map, bool rateLimited)
    {
        if (rateLimited && _options.ResetRefusedNoticeSeconds > 0 && _mapStates.TryGetValue(map, out InstanceMapState? state))
        {
            long now = Now;
            if (state.LastRefusedResetNotice is long last && now - last < _options.ResetRefusedNoticeSeconds)
            {
                return;
            }

            state.LastRefusedResetNotice = now;
        }

        foreach (Player player in map.Players)
        {
            SystemMessage(player, "Please leave the instance so it can be reset.");
        }
    }

    // Unbind everyone and delete the save (vmangos DungeonPersistentState::DeleteFromDB + UnbindThisState).
    private void ResetSave(InstanceSave save)
    {
        if (save.IsDeleted)
        {
            return;
        }

        VerifyNobodyInside(save, ResetWithPlayersInsideCode, "save reset");

        foreach (ObjectGuid player in save.Players.ToArray())
        {
            TryDetachPlayerBind(player, save);
        }

        foreach (uint groupId in save.Groups.ToArray())
        {
            TryDetachGroupBind(groupId, save);
        }

        foreach (uint groupId in save.Groups)
        {
            ForgetStoredGroupBind(groupId, save.InstanceId, persist: false); // the instance delete removes the rows
        }

        ForgetPendingGroupLeaders(save, persist: false);
        save.Players.Clear();
        save.Groups.Clear();
        DeleteSave(save);
    }

    // Drop every player and group bind of a save whose map is still loaded, keeping the save itself
    // until that map unloads (vmangos DungeonPersistentState::UnbindThisState; the lockout rows go now).
    private void UnbindAll(InstanceSave save)
    {
        foreach (ObjectGuid player in save.Players.ToArray())
        {
            TryDetachPlayerBind(player, save);

            save.Players.Remove(player);
            _persistence.PlayerUnbound(player.Counter, save.InstanceId);
        }

        foreach (uint groupId in save.Groups.ToArray())
        {
            TryDetachGroupBind(groupId, save);

            save.Groups.Remove(groupId);
            ForgetStoredGroupBind(groupId, save.InstanceId, persist: true);
        }

        ForgetPendingGroupLeaders(save, persist: true);
    }

    private void ForgetPendingGroupLeaders(InstanceSave save, bool persist)
    {
        foreach (uint leader in save.StoredGroupLeaders.ToArray())
        {
            if (_pendingGroupBinds.TryGetValue(leader, out List<InstanceSave>? pending))
            {
                pending.Remove(save);
                if (pending.Count == 0)
                {
                    _pendingGroupBinds.Remove(leader);
                }
            }

            if (persist)
            {
                _persistence.GroupUnbound(leader, save.InstanceId);
            }
        }

        save.StoredGroupLeaders.Clear();
    }

    private void DeleteSave(InstanceSave save)
    {
        if (save.IsDeleted)
        {
            return;
        }

        save.IsDeleted = true;
        _saves.Remove(save.InstanceId);
        ForgetDeletedInstanceOfBodies(save);
        _persistence.InstanceDeleted(save.InstanceId);
        _logger.LogDebug("deleted {Save}", save);
        InstanceDeleted?.Invoke(save.InstanceId);
    }

    /// <summary>
    /// The bodies of online ghosts in the deleted instance stay where they are (vmangos keeps a corpse whatever happens to its
    /// instance), but they no longer name it: instance ids are handed out again after a restart, and a stale id would put the body
    /// into somebody else's new instance. A new instance map never adopts an instance-0 body (MapCombat.AdoptBodiesLeftOutside compares
    /// the instance id). At a login, though, MapCombat.RestoreGhost (ResolveCorpseMap) applies the legacy-row rule: an instance-0 body of
    /// an instanceable map goes into the ghost's current map when the map ids match, so a ghost that logs in inside a new instance of the
    /// same dungeon finds its body there. Entering the dungeon still revives the ghost (ReviveForDungeonEntry compares the map only). The
    /// stored corpse rows get the same change with the instance's delete (IInstanceStore.DeleteInstanceAsync).
    /// </summary>
    private void ForgetDeletedInstanceOfBodies(InstanceSave save)
    {
        foreach (Player player in _world.OnlinePlayers)
        {
            if (player.Combat.Corpse is { } body && body.MapId == save.MapId && body.InstanceId == save.InstanceId)
            {
                body.InstanceId = 0;
            }
        }
    }

    private void OnPlayerLoggingOut(Player player) => _players.Remove(player.Guid);

    // vmangos Group::_homebindIfInstance: no permanent bind to the dungeon the player is in →
    // its presence is no longer valid (the homebind timer starts on the next map update).
    private void HomebindIfInstance(Player player)
    {
        if (player.IsGameMaster || player.Map is not { } map || !_mapStates.ContainsKey(map))
        {
            return;
        }

        if (GetPlayerBind(player.Guid, map.MapId) is not { Permanent: true })
        {
            StateFor(player).Valid = false;
        }
    }

    // vmangos Player::UpdateHomebindTime.
    private void UpdateHomebindTimer(Player player, uint diffMs)
    {
        PlayerState state = StateFor(player);
        if (state.Valid || player.IsGameMaster)
        {
            if (state.HomebindTimerMs != 0)
            {
                state.HomebindTimerMs = 0;
                player.Session.Send(WorldOpcode.SmsgRaidGroupOnly, InstancePackets.BuildRaidGroupOnly(0, RaidGroupError.Required));
            }

            state.Valid = true;
            return;
        }

        if (state.HomebindTimerMs > 0)
        {
            if (diffMs >= state.HomebindTimerMs)
            {
                state.HomebindTimerMs = 0;
                TeleportToHomebind(player);
            }
            else
            {
                state.HomebindTimerMs -= diffMs;
            }
        }
        else
        {
            state.HomebindTimerMs = (uint)Math.Max(1, _options.HomebindTimerMs);
            player.Session.Send(WorldOpcode.SmsgRaidGroupOnly, InstancePackets.BuildRaidGroupOnly(state.HomebindTimerMs, RaidGroupError.Required));
        }
    }

    // vmangos Player::ResetPersonalInstanceOnLeaveDungeon: a grouped player leaving a dungeon
    // drops its personal non-permanent save of it, which the group's save replaces (a different
    // personal save is reset as on joining a group).
    private void ResetPersonalInstanceOnLeaveDungeon(Player player, uint mapId)
    {
        if (GroupOf(player.Guid) is not { } group)
        {
            return;
        }

        if (GetPlayerBind(player.Guid, mapId) is not { Permanent: false } bind || GetGroupBind(group, mapId) is not { } groupBind)
        {
            return;
        }

        if (bind.Save == groupBind.Save || !bind.Save.CanReset)
        {
            RemovePlayerBind(player.Guid, bind.Save);
            return;
        }

        if (_world.FindMap(mapId, bind.Save.InstanceId) is { } map)
        {
            ResetLoadedMap(map, global: false, notifyInside: false);
        }

        ResetSave(bind.Save);
    }

    // vmangos Player::LoadFromDB "relocate to the instance entrance": the area trigger on this
    // map leading to its ghost entrance map, else the bind point.
    private void RelocateToEntrance(Player player, MapTemplate template)
    {
        AreaTriggerTeleport? exit = GetGoBackTrigger(template.Entry);
        if (exit is not null)
        {
            player.MapId = exit.TargetMap;
            player.SetPosition(exit.TargetX, exit.TargetY, exit.TargetZ, exit.TargetOrientation);
        }
        else
        {
            player.MapId = player.Home.MapId;
            player.SetPosition(player.Home.X, player.Home.Y, player.Home.Z, player.Orientation);
        }

        _logger.LogInformation("{Player} logged in on dungeon map {Map} without its instance; relocated to map {Target}", player.Name, template.Entry, player.MapId);
    }

    private PlayerState StateFor(Player player)
    {
        if (!_players.TryGetValue(player.Guid, out PlayerState? state))
        {
            state = new PlayerState();
            _players[player.Guid] = state;
        }

        return state;
    }

    private sealed class PlayerState
    {
        public bool Valid { get; set; } = true;

        public uint HomebindTimerMs { get; set; }

        public uint? LeftDungeonMapId { get; set; }
    }
}
