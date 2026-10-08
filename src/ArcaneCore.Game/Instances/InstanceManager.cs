using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
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

    /// <summary>Seconds before a global raid reset at which warnings go out, then the reset itself (vmangos <c>ResetEventType</c>: 3600, 900, 300, 60).</summary>
    private static readonly long[] RaidEventOffsets = [3600, 900, 300, 60];

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
    /// A fresh instance id from the counter the dungeon saves use, for a map that is not a dungeon save (a battleground match; vmangos
    /// <c>MapManager::GenerateInstanceId</c> serves both). World thread.
    /// </summary>
    public uint AllocateInstanceId()
    {
        uint id = _nextInstanceId++;
        while (_saves.ContainsKey(id))
        {
            id = _nextInstanceId++;
        }

        return id;
    }

    private bool IsBattlegroundMap(uint mapId, out IMapResolver resolver)
    {
        resolver = BattlegroundMaps!;
        return BattlegroundMaps is not null && Registry.Find(mapId) is { IsBattleground: true };
    }

    /// <summary>
    /// Raised on the world thread when a logical save is deleted for good (a real reset, a
    /// delete after nobody is bound, or the startup drop of an unbound or expired save), after
    /// its storage delete was queued. The state kept per save (chest loot) follows it.
    /// </summary>
    public event Action<uint>? InstanceDeleted;

    /// <summary>All live saves (loaded or not).</summary>
    public IReadOnlyCollection<InstanceSave> Saves => _saves.Values;

    /// <summary>Instance maps currently loaded.</summary>
    public IReadOnlyCollection<Map> LoadedMaps => _mapStates.Keys;

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

    /// <summary>The next global reset of a raid map (Unix seconds), or 0 when it has none.</summary>
    public long GetRaidResetTime(uint mapId) => _raidSchedules.TryGetValue(mapId, out RaidSchedule? schedule) ? schedule.ResetTime : 0;

    /// <summary>Whether the player may stay in its instance (vmangos <c>Player::m_instanceValid</c>).</summary>
    public bool IsInstanceValid(Player player) => !_players.TryGetValue(player.Guid, out PlayerState? state) || state.Valid;

    /// <summary>Milliseconds left on the player's homebind timer, 0 when none runs.</summary>
    public uint HomebindTimerMs(Player player) => _players.TryGetValue(player.Guid, out PlayerState? state) ? state.HomebindTimerMs : 0;

    /// <summary>The instance map's runtime state (unload timer and reset flags), or null for other maps.</summary>
    public InstanceMapState? StateOf(Map map) => _mapStates.GetValueOrDefault(map);

    // ---- IMapResolver -------------------------------------------------------------------

    /// <inheritdoc />
    public Map ResolveLoginMap(Player player)
    {
        if (IsBattlegroundMap(player.MapId, out IMapResolver battlegrounds))
        {
            return battlegrounds.ResolveLoginMap(player);
        }

        MapTemplate? template = Registry.Find(player.MapId);
        if (template is null || !template.IsDungeon)
        {
            return _world.GetMap(player.MapId);
        }

        // vmangos Player::LoadFromDB: a dungeon login needs the save the player was in; an
        // instance reset meanwhile (or no bind at all) sends it to the entrance.
        InstanceSave? save = GetBoundSaveForSelfOrGroup(player.Guid, player.MapId);
        bool lastMatches = _lastInstance.TryGetValue(player.Guid, out (uint MapId, uint InstanceId) last)
            && save is not null && last.MapId == player.MapId && last.InstanceId == save.InstanceId;
        if (save is not null && lastMatches && CheckEntry(player, template, save, sendErrors: false) is null)
        {
            Map map = GetOrCreateInstanceMap(save);
            BindPlayerOrGroupOnEnter(player, save, map);
            _enterLimiter.Record(player.AccountId, save.InstanceId, Now);
            return map;
        }

        RelocateToEntrance(player, template);
        return _world.GetMap(player.MapId);
    }

    /// <inheritdoc />
    public bool CanEnter(Player player, uint mapId)
    {
        if (IsBattlegroundMap(mapId, out IMapResolver battlegrounds))
        {
            return battlegrounds.CanEnter(player, mapId);
        }

        MapTemplate? template = Registry.Find(mapId);
        if (template is null || !template.IsDungeon)
        {
            return true;
        }

        InstanceSave? save = GetBoundSaveForSelfOrGroup(player.Guid, mapId);
        return CheckEntry(player, template, save, sendErrors: true) is null;
    }

    /// <inheritdoc />
    public Map? ResolveEntry(Player player, uint mapId)
    {
        if (IsBattlegroundMap(mapId, out IMapResolver battlegrounds))
        {
            return battlegrounds.ResolveEntry(player, mapId);
        }

        MapTemplate? template = Registry.Find(mapId);
        if (template is null || !template.IsDungeon)
        {
            return _world.GetMap(mapId);
        }

        InstanceSave? save = GetBoundSaveForSelfOrGroup(player.Guid, mapId);
        if (CheckEntry(player, template, save, sendErrors: true) is not null)
        {
            return null;
        }

        save ??= CreateSave(template);
        Map map = GetOrCreateInstanceMap(save);
        BindPlayerOrGroupOnEnter(player, save, map);
        _enterLimiter.Record(player.AccountId, save.InstanceId, Now); // vmangos DungeonMap::Add
        return map;
    }

    /// <inheritdoc />
    public void OnEntered(Player player, Map map)
    {
        // The battleground resolver hears about every arrival (a far teleport out of a match leaves it, Player.cpp:2036-2043).
        BattlegroundMaps?.OnEntered(player, map);
        PlayerState state = StateFor(player);
        SendSavedInstances(player); // every far teleport, instance or not (vmangos SendNewWorld)
        if (!_mapStates.TryGetValue(map, out InstanceMapState? mapState))
        {
            // vmangos HandleMoveWorldportAckOpcode: "reset instance validity, except if going
            // to an instance inside an instance", then ResetPersonalInstanceOnLeaveDungeon.
            state.Valid = true;
            if (state.LeftDungeonMapId is uint left)
            {
                state.LeftDungeonMapId = null;
                ResetPersonalInstanceOnLeaveDungeon(player, left);
            }

            return;
        }

        state.LeftDungeonMapId = null;
        uint characterId = player.Guid.Counter;
        _lastInstance[player.Guid] = (map.MapId, map.InstanceId);
        _persistence.PlayerEnteredInstance(characterId, map.MapId, map.InstanceId);

        // vmangos: "raid instance welcome" (SendInstanceResetWarning on entering a raid).
        if (mapState.Save.Template.IsRaid)
        {
            long resetTime = GetRaidResetTime(map.MapId);
            if (resetTime > 0)
            {
                uint left = (uint)Math.Max(0, resetTime - Now);
                player.Session.Send(
                    WorldOpcode.SmsgRaidInstanceMessage,
                    InstancePackets.BuildRaidInstanceMessage(InstancePackets.MessageTypeFor(left), map.MapId, left));
            }
        }
    }

    /// <inheritdoc />
    public Map? ResolveCorpseMap(uint mapId, uint instanceId)
    {
        if (IsBattlegroundMap(mapId, out IMapResolver battlegrounds))
        {
            return battlegrounds.ResolveCorpseMap(mapId, instanceId);
        }

        if (_world.FindMap(mapId, instanceId) is { } loaded)
        {
            return loaded;
        }

        // A live save gets its map the way an entry creates it: managed, so it unloads on its timer and the timed reset of the
        // save still sees it. A deleted or unknown instance gets none (the body stays out of every map).
        return _saves.TryGetValue(instanceId, out InstanceSave? save) && save.MapId == mapId && !save.IsDeleted
            ? GetOrCreateInstanceMap(save)
            : null;
    }

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

    /// <summary>
    /// The instance reset schedule (vmangos <c>DungeonResetScheduler::Update</c>; the world
    /// feature runs it on a timer, tests call it directly): global raid warnings and resets,
    /// and the reset of normal dungeons whose time has come while they are empty.
    /// </summary>
    public void UpdateSchedule()
    {
        long now = Now;
        foreach ((uint mapId, RaidSchedule schedule) in _raidSchedules)
        {
            while (schedule.Stage < RaidEventOffsets.Length && now >= schedule.ResetTime - RaidEventOffsets[schedule.Stage])
            {
                if (schedule.Stage == RaidEventOffsets.Length - 1)
                {
                    GlobalRaidReset(mapId, schedule);
                    break;
                }

                // Skip warnings whose time already passed with the next one due.
                bool nextDue = now >= schedule.ResetTime - RaidEventOffsets[schedule.Stage + 1];
                if (!nextDue)
                {
                    SendResetWarnings(mapId, (uint)Math.Max(0, schedule.ResetTime - now));
                }

                schedule.Stage++;
            }
        }

        foreach (InstanceSave save in _saves.Values.Where(s => !s.Template.IsRaid && s.ResetScheduled && now >= s.ResetTime).ToArray())
        {
            Map? map = _world.FindMap(save.MapId, save.InstanceId);
            if (map is null)
            {
                ResetSave(save);
            }
            else if (map.PlayerCount == 0 && map.TransitCount == 0 && _mapStates.TryGetValue(map, out InstanceMapState? state))
            {
                state.ResetAfterUnload = true;
                state.UnloadTimerMs = InstanceOptions.MinUnloadDelayMs;
            }
        }
    }

    // ---- per-map callbacks (InstanceMapUpdater) ----------------------------------------

    internal void UpdateMap(Map map, uint diffMs)
    {
        if (!_mapStates.TryGetValue(map, out InstanceMapState? state))
        {
            return;
        }

        foreach (Player player in map.Players.ToArray())
        {
            UpdateHomebindTimer(player, diffMs);
        }

        if (state.UnloadTimerMs > 0)
        {
            if (diffMs >= state.UnloadTimerMs)
            {
                state.UnloadTimerMs = 0;
                _world.UnloadMap(map);
            }
            else
            {
                state.UnloadTimerMs -= diffMs;
            }
        }
    }

    internal void PlayerLeftMap(Map map, Player player)
    {
        if (!_mapStates.TryGetValue(map, out InstanceMapState? state))
        {
            return;
        }

        if (_players.TryGetValue(player.Guid, out PlayerState? playerState))
        {
            playerState.LeftDungeonMapId = map.MapId;
            if (playerState.HomebindTimerMs != 0)
            {
                playerState.HomebindTimerMs = 0;
                player.Session.Send(WorldOpcode.SmsgRaidGroupOnly, InstancePackets.BuildRaidGroupOnly(0, RaidGroupError.Required));
            }
        }

        // vmangos DungeonMap::Remove: the last player out starts the unload timer and arms
        // the normal reset.
        if (map.Players.All(p => ReferenceEquals(p, player)))
        {
            state.UnloadTimerMs = state.UnloadWhenEmpty ? InstanceOptions.MinUnloadDelayMs : _options.EffectiveUnloadDelayMs;
            if (!state.Save.Template.IsRaid)
            {
                state.Save.ResetScheduled = true;
            }
        }
    }

    // ---- internals ----------------------------------------------------------------------

    private TransferAbortReason? CheckEntry(Player player, MapTemplate template, InstanceSave? save, bool sendErrors)
    {
        // vmangos MapManager::CanPlayerEnter: raids need a raid group.
        if (template.IsRaid && !player.IsGameMaster && !_options.IgnoreRaidGroup && GroupOf(player.Guid) is not { IsRaid: true })
        {
            if (sendErrors)
            {
                player.Session.Send(WorldOpcode.SmsgRaidGroupOnly, InstancePackets.BuildRaidGroupOnly(0, RaidGroupError.Required));
            }

            return TransferAbortReason.Silently;
        }

        // vmangos MapManager::CanPlayerEnter (MapManager.cpp:209-214): the hourly per-account
        // limit, keyed by the id of the save the player would enter (0 when it will be a new one).
        // Checked on teleports only (sendErrors), not on the login re-entry.
        if (sendErrors && _options.PerHourLimit > 0 && !player.IsGameMaster
            && !_enterLimiter.CanEnter(player.AccountId, save?.InstanceId ?? 0, _options.PerHourLimit, Now))
        {
            player.Session.Send(WorldOpcode.SmsgTransferAborted, TeleportPackets.BuildTransferAborted(TransferAbortReason.TooManyInstances));
            return TransferAbortReason.TooManyInstances;
        }

        if (save is null)
        {
            return null;
        }

        Map? map = _world.FindMap(save.MapId, save.InstanceId);
        if (map is null)
        {
            return null;
        }

        // vmangos DungeonMap::CanEnter (Map.cpp:2134-2146): the player cap first (GMs neither
        // count nor are refused), then the pending reset, which also refuses GMs.
        TransferAbortReason? reason = null;
        if (!player.IsGameMaster && template.PlayerLimit > 0
            && map.Players.Count(p => !p.IsGameMaster && !ReferenceEquals(p, player)) >= template.PlayerLimit)
        {
            reason = TransferAbortReason.MaxPlayers;
        }
        else if (_mapStates.TryGetValue(map, out InstanceMapState? state) && state.ResetAfterUnload)
        {
            reason = TransferAbortReason.NotFound;
        }

        if (reason is { } abort && sendErrors)
        {
            player.Session.Send(WorldOpcode.SmsgTransferAborted, TeleportPackets.BuildTransferAborted(abort));
        }

        return reason;
    }

    private InstanceSave CreateSave(MapTemplate template)
    {
        uint id = _nextInstanceId++;
        while (_saves.ContainsKey(id) || _world.FindMap(template.Entry, id) is not null)
        {
            id = _nextInstanceId++;
        }

        long resetTime = template.IsRaid ? GetRaidResetTime(template.Entry) : Now + _options.NormalDungeonResetSeconds;
        var save = new InstanceSave(id, template, resetTime);
        _saves[id] = save;
        _persistence.InstanceSaved(save);
        _logger.LogDebug("created {Save}", save);
        SaveCreated?.Invoke(save);
        return save;
    }

    private Map GetOrCreateInstanceMap(InstanceSave save)
    {
        Map map = _world.GetMap(save.MapId, save.InstanceId);
        if (!_mapStates.ContainsKey(map))
        {
            var state = new InstanceMapState(save) { UnloadTimerMs = _options.EffectiveUnloadDelayMs };
            _mapStates[map] = state;
            map.AddUpdater(new InstanceMapUpdater(this));
            state.KillHandler = (killer, victim) => OnUnitKilled(map, killer, victim);
            if (map.FindUpdater<MapCombat>() is { } combat)
            {
                combat.UnitKilled += state.KillHandler;
            }
        }

        return map;
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
        if (playerBind is { } own && own.Save == save)
        {
            return; // already bound here (permanent or solo)
        }

        Group? group = GroupOf(player.Guid);
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

    private void RemovePlayerBind(ObjectGuid player, InstanceSave save)
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

        if (save.Players.Remove(player))
        {
            _persistence.PlayerUnbound(player.Counter, save.InstanceId);
        }

        ForgetIfUnused(save);
    }

    private void RemoveGroupBind(Group group, InstanceSave save)
    {
        if (_groupBinds.TryGetValue(group.Id, out Dictionary<uint, InstanceBind>? binds)
            && binds.TryGetValue(save.MapId, out InstanceBind bind) && bind.Save == save)
        {
            binds.Remove(save.MapId);
        }

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

        foreach (ObjectGuid player in save.Players.ToArray())
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

        foreach (uint groupId in save.Groups.ToArray())
        {
            if (_groupBinds.TryGetValue(groupId, out Dictionary<uint, InstanceBind>? binds)
                && binds.TryGetValue(save.MapId, out InstanceBind bind) && bind.Save == save)
            {
                binds.Remove(save.MapId);
            }
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
            if (_playerBinds.TryGetValue(player, out Dictionary<uint, InstanceBind>? binds)
                && binds.TryGetValue(save.MapId, out InstanceBind bind) && bind.Save == save)
            {
                binds.Remove(save.MapId);
                if (binds.Count == 0)
                {
                    _playerBinds.Remove(player);
                }
            }

            save.Players.Remove(player);
            _persistence.PlayerUnbound(player.Counter, save.InstanceId);
        }

        foreach (uint groupId in save.Groups.ToArray())
        {
            if (_groupBinds.TryGetValue(groupId, out Dictionary<uint, InstanceBind>? binds)
                && binds.TryGetValue(save.MapId, out InstanceBind bind) && bind.Save == save)
            {
                binds.Remove(save.MapId);
            }

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

    private void OnMapUnloading(Map map)
    {
        if (!_mapStates.Remove(map, out InstanceMapState? state))
        {
            return;
        }

        if (state.KillHandler is not null && map.FindUpdater<MapCombat>() is { } combat)
        {
            combat.UnitKilled -= state.KillHandler;
        }

        InstanceSave save = state.Save;
        if (state.ResetAfterUnload)
        {
            ResetSave(save);
        }
        else if (!save.HasBinds)
        {
            DeleteSave(save);
        }
        else if (!save.Template.IsRaid)
        {
            save.ResetScheduled = true;
        }

        _logger.LogDebug("unloaded map of {Save}", save);
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

    private void InitializeRaidSchedules(IReadOnlyList<InstanceResetRecord> stored)
    {
        long now = Now;
        long today = now / Day * Day;
        long diff = (long)Math.Clamp(_options.ResetTimeHour, 0, 23) * 3600;
        var storedByMap = stored.GroupBy(r => r.MapId).ToDictionary(g => g.Key, g => g.Last().ResetTime);
        foreach (MapTemplate template in Registry.All.Where(t => t.IsDungeon && t.ResetDelay > 0))
        {
            long period = template.ResetDelay * Day;
            long t = storedByMap.TryGetValue(template.Entry, out long value) && value > 0
                ? value
                : _raidSchedules.TryGetValue(template.Entry, out RaidSchedule? existing) ? existing.ResetTime : today + period + diff;
            if (t <= now)
            {
                t += ((now - t) / period + 1) * period; // keep the weekly phase
            }

            var schedule = new RaidSchedule(t);
            schedule.SkipPastEvents(now);
            _raidSchedules[template.Entry] = schedule;
            if (!storedByMap.TryGetValue(template.Entry, out long old) || old != t)
            {
                _persistence.RaidResetTimeChanged(template.Entry, t);
            }
        }
    }

    // vmangos MapPersistentStateManager::_ResetOrWarnAll (reset): everyone is unbound, players
    // inside are sent to their bind point, the maps unload, the next reset is scheduled. A save
    // whose map is loaded is deleted when that map unloads (ResetAfterUnload; vmangos removes the
    // state in ~Map), so players whose trip home is still pending or failed are not left in a
    // deleted instance; the homebind timer of the now invalid players retries the trip.
    private void GlobalRaidReset(uint mapId, RaidSchedule schedule)
    {
        foreach (InstanceSave save in _saves.Values.Where(s => s.MapId == mapId).ToArray())
        {
            if (_world.FindMap(save.MapId, save.InstanceId) is { } map && _mapStates.ContainsKey(map))
            {
                UnbindAll(save);
                ResetLoadedMap(map, global: true, notifyInside: false);
                foreach (Player player in map.Players.ToArray())
                {
                    TeleportToHomebind(player);
                }

                continue;
            }

            ResetSave(save);
        }

        MapTemplate? template = Registry.Find(mapId);
        long period = Math.Max(1, template?.ResetDelay ?? 7) * Day;
        long diff = (long)Math.Clamp(_options.ResetTimeHour, 0, 23) * 3600;
        long next = (schedule.ResetTime / Day * Day) + period + diff;
        long now = Now;
        if (next <= now)
        {
            next += ((now - next) / period + 1) * period;
        }

        schedule.ResetTime = next;
        schedule.Stage = 0;
        schedule.SkipPastEvents(now);
        _persistence.RaidResetTimeChanged(mapId, next);
        _logger.LogInformation("global reset of raid map {Map}; next at {Next}", mapId, DateTimeOffset.FromUnixTimeSeconds(next));
    }

    private void SendResetWarnings(uint mapId, uint timeLeft)
    {
        byte[] packet = InstancePackets.BuildRaidInstanceMessage(InstancePackets.MessageTypeFor(timeLeft), mapId, timeLeft);
        foreach (Map map in _mapStates.Keys.Where(m => m.MapId == mapId))
        {
            foreach (Player player in map.Players)
            {
                player.Session.Send(WorldOpcode.SmsgRaidInstanceMessage, packet);
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

    private sealed class RaidSchedule(long resetTime)
    {
        public long ResetTime { get; set; } = resetTime;

        /// <summary>Index into <see cref="RaidEventOffsets"/> of the next event.</summary>
        public int Stage { get; set; }

        public void SkipPastEvents(long now)
        {
            // Warnings already in the past are not replayed; the reset event is never skipped.
            while (Stage < RaidEventOffsets.Length - 1 && now >= ResetTime - RaidEventOffsets[Stage + 1])
            {
                Stage++;
            }

            if (Stage < RaidEventOffsets.Length - 1 && now >= ResetTime - RaidEventOffsets[Stage])
            {
                Stage++;
            }
        }
    }
}

/// <summary>Runtime state of one loaded instance map (vmangos <c>DungeonMap</c> members).</summary>
public sealed class InstanceMapState
{
    internal InstanceMapState(InstanceSave save) => Save = save;

    public InstanceSave Save { get; }

    /// <summary>Milliseconds until the map unloads; 0 while it is occupied (vmangos <c>m_unloadTimer</c>).</summary>
    public uint UnloadTimerMs { get; internal set; }

    /// <summary>Unload as soon as the last player leaves (vmangos <c>m_unloadWhenEmpty</c>).</summary>
    public bool UnloadWhenEmpty { get; internal set; }

    /// <summary>Reset (delete) the save when the map unloads; nobody may enter meanwhile (vmangos <c>m_resetAfterUnload</c>).</summary>
    public bool ResetAfterUnload { get; internal set; }

    /// <summary>Unix seconds of the last "please leave" notice of a refused personal reset (<see cref="InstanceOptions.ResetRefusedNoticeSeconds"/>).</summary>
    internal long? LastRefusedResetNotice { get; set; }

    internal Action<Unit?, Unit>? KillHandler { get; set; }
}

/// <summary>Drives one instance map's unload timer and its players' homebind timers.</summary>
internal sealed class InstanceMapUpdater(InstanceManager manager) : IMapUpdater
{
    public void Update(Map map, uint diffMs) => manager.UpdateMap(map, diffMs);

    public void OnPlayerRemoved(Map map, Player player) => manager.PlayerLeftMap(map, player);
}
