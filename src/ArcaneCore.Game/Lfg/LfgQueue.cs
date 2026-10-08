using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Lfg;

/// <summary>The groups the meeting stone queue works with (the social area's <see cref="GroupManager"/> in the world). World thread.</summary>
public interface ILfgGroups
{
    /// <summary>The group <paramref name="player"/> belongs to, or null.</summary>
    Group? GroupOf(ObjectGuid player);

    /// <summary>A new party led by <paramref name="leader"/> with <paramref name="member"/> (vmangos Group::Create + AddMember(GROUP_LFG)).</summary>
    Group? CreateGroup(Player leader, Player member);

    /// <summary>vmangos Group::AddMember(guid, name, GROUP_LFG): add a member the queue found.</summary>
    bool AddMember(Group group, Player member);

    /// <summary>vmangos Group::BroadcastPacket to the online members.</summary>
    void Broadcast(Group group, WorldOpcode opcode, byte[] payload);

    /// <summary>The online player of <paramref name="guid"/>, or null.</summary>
    Player? FindOnlinePlayer(ObjectGuid guid);

    /// <summary>The class of a member (online or not; vmangos ObjectMgr::GetPlayerClassByGUID), 0 when unknown.</summary>
    Class ClassOf(ObjectGuid guid);
}

/// <summary>
/// The meeting stone queue (vmangos LFGMgr and LFGQueue, LFG/LFGMgr.cpp and LFG/LFGQueue.cpp; the group hooks of Group::AddMember, RemoveMember and
/// Disband, Group.cpp:418-605): solo players and parties queue for the dungeon area of a meeting stone; a queued party is filled with queued players
/// whose class covers a role it still lacks (one tank, one healer, three damage), and two queued players of one area and team start a party of their
/// own once five are waiting. vmangos runs the queue on its own thread once a second and hands the results to the world through messages; here it
/// runs on the world thread, once a second of the world clock, and its effects are immediate. Players and parties are taken in guid / group id
/// order, as vmangos' ordered maps take them.
/// </summary>
public sealed class LfgQueue(ILfgGroups groups)
{
    /// <summary>vmangos LFGQueue::Update sleeps a second between passes (LFGQueue.cpp:221).</summary>
    public const uint UpdateIntervalMs = 1000;

    /// <summary>After this long in the queue a player gets queue priority (LFGQueue.cpp:79-81).</summary>
    public const uint PriorityAfterMs = 30 * 60 * 1000;

    /// <summary>A waiting party hears SMSG_MEETINGSTONE_IN_PROGRESS this often (LFGMgr.cpp:52, LFGQueue.cpp:144-153).</summary>
    public const uint GroupReminderMs = 5 * 60 * 1000;

    private sealed class PlayerEntry
    {
        public required Team Team { get; init; }

        public required uint AreaId { get; init; }

        public required Class Class { get; init; }

        public required LfgRoles Roles { get; init; }

        public uint TimeInLfgMs { get; set; }

        public bool HasQueuePriority { get; set; }

        /// <summary>vmangos converts the role priority to bool before comparing (LFGQueue.cpp:296-312): a class either can or cannot fill it.</summary>
        public bool CanFill(LfgRoles role) => LfgRules.PriorityOf(Class, role) != LfgRolePriority.None;
    }

    private sealed class GroupEntry
    {
        /// <summary>The party (vmangos keeps its id and looks it up when it acts; the object stays the same while the party exists).</summary>
        public required Group Group { get; init; }

        public LfgRoles AvailableRoles { get; set; }

        public uint DpsCount { get; set; }

        public required Team Team { get; init; }

        public required uint AreaId { get; init; }

        public uint GroupTimerMs { get; set; }

        public int PlayerCount { get; set; }
    }

    private readonly SortedDictionary<ulong, PlayerEntry> _players = [];
    private readonly SortedDictionary<uint, GroupEntry> _groups = [];
    private readonly Dictionary<ObjectGuid, uint> _playerArea = [];
    private readonly Dictionary<uint, uint> _groupArea = [];
    private uint _sinceUpdateMs;

    /// <summary>The meeting stone area a player is queued (or marked) for, 0 for none (vmangos Player::GetLFGAreaId).</summary>
    public uint PlayerAreaOf(ObjectGuid player) => _playerArea.GetValueOrDefault(player);

    /// <summary>The meeting stone area a party is queued for, 0 for none (vmangos Group::GetLFGAreaId / IsInLFG).</summary>
    public uint GroupAreaOf(Group group) => group is null ? 0 : _groupArea.GetValueOrDefault(group.Id);

    public bool IsPlayerQueued(ObjectGuid player) => _players.ContainsKey(player.Value);

    public bool IsGroupQueued(Group group) => group is not null && _groups.ContainsKey(group.Id);

    // --- client requests ----------------------------------------------------------------------

    /// <summary>
    /// LFGMgr::AddToQueue (LFGMgr.cpp:31-82) after the join checks of HandleMeetingStoneJoinOpcode: a party leader queues the party (its roles,
    /// team and size; SMSG_MEETINGSTONE_SETQUEUE JOINED to the party), a player without a party queues alone (SETQUEUE JOINED to him). A party
    /// member who does not lead is not queued (the handler has already refused him).
    /// </summary>
    public void Join(Player player, uint areaId)
    {
        ArgumentNullException.ThrowIfNull(player);
        Group? group = groups.GroupOf(player.Guid);
        if (group is not null)
        {
            if (!group.IsLeader(player.Guid))
            {
                return;
            }

            var entry = new GroupEntry { Group = group, Team = player.Team, AreaId = areaId, GroupTimerMs = GroupReminderMs };
            ApplyGroupRoles(group, entry);
            _groupArea[group.Id] = areaId;
            _groups[group.Id] = entry;
            groups.Broadcast(group, WorldOpcode.SmsgMeetingstoneSetqueue, MeetingStonePackets.SetQueue(areaId, MeetingStoneStatus.JoinedQueue));
            return;
        }

        _players[player.Guid.Value] = new PlayerEntry
        {
            Team = player.Team, AreaId = areaId, Class = player.Class, Roles = LfgRules.RolesOf(player.Class),
        };
        _playerArea[player.Guid] = areaId;
        player.Session.Send(WorldOpcode.SmsgMeetingstoneSetqueue, MeetingStonePackets.SetQueue(areaId, MeetingStoneStatus.JoinedQueue));
    }

    /// <summary>
    /// CMSG_MEETINGSTONE_LEAVE (WorldSession::HandleMeetingStoneLeaveOpcode, LFGHandler.cpp:73-98): the leader of a queued party takes it out of the
    /// queue (SETQUEUE LEAVE_QUEUE to the party); another party member just hears SETQUEUE NONE; a player alone leaves the queue (SETQUEUE
    /// LEAVE_QUEUE when he was in it).
    /// </summary>
    public void Leave(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (groups.GroupOf(player.Guid) is { } group)
        {
            if (group.IsLeader(player.Guid) && GroupAreaOf(group) != 0)
            {
                RemoveGroup(group, systemLeave: false);
            }
            else
            {
                player.Session.Send(WorldOpcode.SmsgMeetingstoneSetqueue, MeetingStonePackets.SetQueue(0, MeetingStoneStatus.None));
            }

            return;
        }

        RemovePlayer(player.Guid, systemLeave: false);
    }

    /// <summary>
    /// CMSG_MEETINGSTONE_INFO (HandleMeetingStoneInfoOpcode, LFGHandler.cpp:100-128): a party in the queue answers its area and JOINED, anyone
    /// else NONE. A player queued alone answers JOINED too: vmangos answers that from its offline-player store (LFGQueue::RestoreOfflinePlayer),
    /// which nothing fills, so a queued solo player would hear NONE; the queue state is answered here instead (deliberate, the client UI needs it).
    /// </summary>
    public void Info(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        uint area = groups.GroupOf(player.Guid) is { } group ? GroupAreaOf(group)
            : IsPlayerQueued(player.Guid) ? PlayerAreaOf(player.Guid) : 0;
        player.Session.Send(WorldOpcode.SmsgMeetingstoneSetqueue,
            MeetingStonePackets.SetQueue(area, area != 0 ? MeetingStoneStatus.JoinedQueue : MeetingStoneStatus.None));
    }

    /// <summary>WorldSession::LogoutPlayer (WorldSession.cpp:721-725): a player logging out leaves the solo queue silently.</summary>
    public void OnLoggingOut(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        RemovePlayer(player.Guid, systemLeave: true);
    }

    // --- group hooks --------------------------------------------------------------------------

    /// <summary>
    /// Group::AddMember (Group.cpp:418-424) for a member who did not come through the queue: a queued party recounts its roles and size
    /// (LFGQueue::UpdateGroup), and a party that became full leaves the queue complete.
    /// </summary>
    public void OnMemberJoined(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (GroupAreaOf(group) != 0)
        {
            UpdateGroup(group);
        }
    }

    /// <summary>
    /// Group::RemoveMember (Group.cpp:428-526) of a queued party, after the member left: a kicked member makes the party leave the queue
    /// (SETQUEUE MEMBER_REMOVED, then LEAVE_QUEUE, to the party) and is queued alone for the same area (SETQUEUE LOOKING_FOR_NEW_PARTY); a member
    /// who left hears NONE and, unless the leader changed, the party hears MEMBER_LEFT; a new leader takes the party out of the queue; otherwise
    /// the party recounts its roles.
    /// </summary>
    public void OnMemberRemoved(Group group, ObjectGuid member, bool kicked, bool leaderChanged)
    {
        ArgumentNullException.ThrowIfNull(group);
        uint area = GroupAreaOf(group);
        if (area == 0)
        {
            return;
        }

        bool leftGroup = false;
        Player? player = groups.FindOnlinePlayer(member);
        if (player is not null)
        {
            if (kicked)
            {
                groups.Broadcast(group, WorldOpcode.SmsgMeetingstoneSetqueue, MeetingStonePackets.SetQueue(0, MeetingStoneStatus.PartyMemberRemovedPartyRemoved));
                leftGroup = true;
                RemoveGroup(group, systemLeave: false);
                player.Session.Send(WorldOpcode.SmsgMeetingstoneSetqueue, MeetingStonePackets.SetQueue(area, MeetingStoneStatus.LookingForNewPartyInQueue));
                Join(player, area);
            }
            else
            {
                player.Session.Send(WorldOpcode.SmsgMeetingstoneSetqueue, MeetingStonePackets.SetQueue(0, MeetingStoneStatus.None));
                if (!leaderChanged)
                {
                    groups.Broadcast(group, WorldOpcode.SmsgMeetingstoneSetqueue, MeetingStonePackets.SetQueue(area, MeetingStoneStatus.PartyMemberLeftLfg));
                }
            }
        }

        if (leaderChanged)
        {
            leftGroup = true;
            RemoveGroup(group, systemLeave: false);
        }

        if (!leftGroup && GroupAreaOf(group) != 0)
        {
            UpdateGroup(group);
        }
    }

    /// <summary>Group::Disband (Group.cpp:543-605): every online member of a queued party hears NONE and the party leaves the queue.</summary>
    public void OnDisbanding(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (GroupAreaOf(group) == 0)
        {
            return;
        }

        groups.Broadcast(group, WorldOpcode.SmsgMeetingstoneSetqueue, MeetingStonePackets.SetQueue(0, MeetingStoneStatus.None));
        _groups.Remove(group.Id);
        _groupArea.Remove(group.Id);
    }

    // --- the queue pass -----------------------------------------------------------------------

    /// <summary>Advance the queue by <paramref name="diffMs"/> of world time; a pass runs every <see cref="UpdateIntervalMs"/>.</summary>
    public void Update(uint diffMs)
    {
        _sinceUpdateMs += diffMs;
        if (_sinceUpdateMs < UpdateIntervalMs)
        {
            return;
        }

        uint elapsed = _sinceUpdateMs;
        _sinceUpdateMs = 0;
        Pass(elapsed);
    }

    /// <summary>One pass of LFGQueue::Update (LFGQueue.cpp:64-218).</summary>
    private void Pass(uint diff)
    {
        if (_groups.Count == 0 && _players.Count == 0)
        {
            return;
        }

        foreach (PlayerEntry entry in _players.Values)
        {
            entry.TimeInLfgMs += diff;
            if (entry.TimeInLfgMs >= PriorityAfterMs)
            {
                entry.HasQueuePriority = true;
            }
        }

        foreach (uint groupId in _groups.Keys.ToArray())
        {
            if (!_groups.TryGetValue(groupId, out GroupEntry? queued))
            {
                continue;
            }

            foreach (ulong playerKey in _players.Keys.ToArray())
            {
                if (!_players.TryGetValue(playerKey, out PlayerEntry? candidate) || candidate.Team != queued.Team || candidate.AreaId != queued.AreaId)
                {
                    continue;
                }

                bool found = false;
                foreach (LfgRoles role in LfgRules.PotentialRoles)
                {
                    if ((candidate.Roles & role) == 0)
                    {
                        continue;
                    }

                    if ((role & queued.AvailableRoles) == role && FillRole(new ObjectGuid(playerKey), groupId, role))
                    {
                        found = true;
                        break;
                    }
                }

                if (found && queued.PlayerCount == LfgRules.GroupSize)
                {
                    break;
                }
            }

            if (queued.PlayerCount == LfgRules.GroupSize)
            {
                RemoveGroup(queued.Group, systemLeave: true);
                break; // one filled party per pass (LFGQueue.cpp:136-141)
            }

            if (queued.GroupTimerMs <= diff)
            {
                queued.GroupTimerMs = GroupReminderMs;
                groups.Broadcast(queued.Group, WorldOpcode.SmsgMeetingstoneInProgress, []);
            }
            else
            {
                queued.GroupTimerMs -= diff;
            }
        }

        FormPartyFromSoloPlayers();
    }

    /// <summary>
    /// LFGQueue.cpp:157-213: once five players wait, the first one and the first other player of his area and team, when four others wait for
    /// it, leave the solo queue and become a party (SMSG_MEETINGSTONE_MEMBER_ADDED to the leader), which is queued for the area at once.
    /// </summary>
    private void FormPartyFromSoloPlayers()
    {
        if (_players.Count < LfgRules.GroupSize)
        {
            return;
        }

        (ulong leaderKey, PlayerEntry leaderEntry) = _players.First();
        ulong[] sameArea = [.. _players.Where(p => p.Key != leaderKey && p.Value.AreaId == leaderEntry.AreaId && p.Value.Team == leaderEntry.Team)
            .Select(p => p.Key)];
        if (sameArea.Length < LfgRules.GroupSize - 1)
        {
            return;
        }

        var leaderGuid = new ObjectGuid(leaderKey);
        var memberGuid = new ObjectGuid(sameArea[0]);
        uint area = leaderEntry.AreaId;
        RemovePlayer(leaderGuid, systemLeave: true);
        RemovePlayer(memberGuid, systemLeave: true);
        if (groups.FindOnlinePlayer(leaderGuid) is not { } leader || groups.FindOnlinePlayer(memberGuid) is not { } member)
        {
            return;
        }

        leader.Session.Send(WorldOpcode.SmsgMeetingstoneMemberAdded, MeetingStonePackets.MemberAdded(memberGuid));
        if (groups.CreateGroup(leader, member) is not null)
        {
            Join(leader, area);
        }
    }

    /// <summary>
    /// LFGQueue::FindRoleToGroup (LFGQueue.cpp:282-384): a queued player takes a role of a queued party unless another queued player of the same
    /// area and team who can fill that role comes first (a class that can fill it against one that cannot; queue priority with a longer wait; a
    /// longer wait while this player has no priority). The party's open roles shrink (a third damage dealer closes damage), the player leaves the
    /// solo queue, the party hears SMSG_MEETINGSTONE_MEMBER_ADDED and the player joins it.
    /// </summary>
    private bool FillRole(ObjectGuid playerGuid, uint groupId, LfgRoles role)
    {
        if (!_groups.TryGetValue(groupId, out GroupEntry? queued) || !_players.TryGetValue(playerGuid.Value, out PlayerEntry? candidate))
        {
            return false;
        }

        bool timePriority = candidate.HasQueuePriority;
        bool classPriority = candidate.CanFill(role);
        foreach ((ulong otherKey, PlayerEntry other) in _players)
        {
            if (otherKey == playerGuid.Value || other.AreaId != candidate.AreaId || other.Team != candidate.Team || (other.Roles & role) != role)
            {
                continue;
            }

            bool otherLonger = other.TimeInLfgMs > candidate.TimeInLfgMs;
            if ((other.CanFill(role) && !classPriority) || (other.HasQueuePriority && otherLonger) || (!timePriority && otherLonger))
            {
                return false;
            }
        }

        switch (role)
        {
            case LfgRoles.Tank:
                queued.AvailableRoles &= ~LfgRoles.Tank;
                break;
            case LfgRoles.Healer:
                queued.AvailableRoles &= ~LfgRoles.Healer;
                break;
            case LfgRoles.Damage:
                if (queued.DpsCount < LfgRules.MaxDpsSlots)
                {
                    queued.DpsCount++;
                    if (queued.DpsCount >= LfgRules.MaxDpsSlots)
                    {
                        queued.AvailableRoles &= ~LfgRoles.Damage;
                    }
                }

                break;
            default:
                return false;
        }

        RemovePlayer(playerGuid, systemLeave: true);
        queued.PlayerCount++;
        if (groups.FindOnlinePlayer(playerGuid) is { } player)
        {
            groups.Broadcast(queued.Group, WorldOpcode.SmsgMeetingstoneMemberAdded, MeetingStonePackets.MemberAdded(playerGuid));
            groups.AddMember(queued.Group, player);
        }

        return true;
    }

    /// <summary>
    /// Group::CalculateLFGRoles with FillPremadeLFG (Group.cpp:645-740): each member takes the first role of the tank, healer, damage order that
    /// his class can fill and the party still has open, unless an unprocessed member of the party is wanted more in that role.
    /// </summary>
    private void ApplyGroupRoles(Group group, GroupEntry entry)
    {
        LfgRoles open = LfgRoles.Tank | LfgRoles.Healer | LfgRoles.Damage;
        uint dps = 0;
        var processed = new HashSet<ObjectGuid>();
        foreach (GroupMemberSlot slot in group.Members)
        {
            Class memberClass = groups.ClassOf(slot.Guid);
            LfgRoles roles = LfgRules.RolesOf(memberClass);
            foreach (LfgRoles role in LfgRules.PotentialRoles)
            {
                if ((roles & role) == 0 || (role & open) != role)
                {
                    continue;
                }

                LfgRolePriority priority = LfgRules.PriorityOf(memberClass, role);
                bool outranked = group.Members.Any(other => other.Guid != slot.Guid && !processed.Contains(other.Guid)
                    && priority < LfgRules.PriorityOf(groups.ClassOf(other.Guid), role));
                if (outranked)
                {
                    continue;
                }

                switch (role)
                {
                    case LfgRoles.Tank:
                        open &= ~LfgRoles.Tank;
                        break;
                    case LfgRoles.Healer:
                        open &= ~LfgRoles.Healer;
                        break;
                    case LfgRoles.Damage when dps < LfgRules.MaxDpsSlots:
                        dps++;
                        if (dps >= LfgRules.MaxDpsSlots)
                        {
                            open &= ~LfgRoles.Damage;
                        }

                        break;
                }

                processed.Add(slot.Guid);
                break;
            }
        }

        entry.AvailableRoles = open;
        entry.DpsCount = dps;
        entry.PlayerCount = group.MemberCount;
    }

    /// <summary>LFGQueue::UpdateGroup (LFGQueue.cpp:234-250): a changed party recounts; a full one leaves the queue complete.</summary>
    private void UpdateGroup(Group group)
    {
        if (!_groups.TryGetValue(group.Id, out GroupEntry? queued) || queued.PlayerCount == group.MemberCount)
        {
            return;
        }

        if (group.MemberCount < LfgRules.GroupSize)
        {
            ApplyGroupRoles(group, queued);
        }
        else
        {
            queued.PlayerCount = group.MemberCount;
        }

        if (queued.PlayerCount >= LfgRules.GroupSize)
        {
            RemoveGroup(group, systemLeave: true);
        }
    }

    /// <summary>
    /// LFGQueue::RemovePlayerFromQueue (LFGQueue.cpp:386-418): a client leave sends SETQUEUE LEAVE_QUEUE and clears the player's area; a leave by
    /// the queue itself is silent and keeps it.
    /// </summary>
    private void RemovePlayer(ObjectGuid guid, bool systemLeave)
    {
        if (!_players.Remove(guid.Value))
        {
            return;
        }

        if (!systemLeave)
        {
            groups.FindOnlinePlayer(guid)?.Session.Send(WorldOpcode.SmsgMeetingstoneSetqueue, MeetingStonePackets.SetQueue(0, MeetingStoneStatus.LeaveQueue));
            _playerArea.Remove(guid);
        }
    }

    /// <summary>
    /// LFGQueue::RemoveGroupFromQueue (LFGQueue.cpp:420-470): a client leave sends SETQUEUE LEAVE_QUEUE to the party; the queue's own removal (the
    /// party is full) sends SMSG_MEETINGSTONE_COMPLETE and SETQUEUE NONE. The party's area is cleared either way.
    /// </summary>
    private void RemoveGroup(Group group, bool systemLeave)
    {
        if (!_groups.Remove(group.Id))
        {
            return;
        }

        if (!systemLeave)
        {
            groups.Broadcast(group, WorldOpcode.SmsgMeetingstoneSetqueue, MeetingStonePackets.SetQueue(0, MeetingStoneStatus.LeaveQueue));
        }
        else
        {
            groups.Broadcast(group, WorldOpcode.SmsgMeetingstoneComplete, []);
            groups.Broadcast(group, WorldOpcode.SmsgMeetingstoneSetqueue, MeetingStonePackets.SetQueue(0, MeetingStoneStatus.None));
        }

        _groupArea.Remove(group.Id);
    }
}
