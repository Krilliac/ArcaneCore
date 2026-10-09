using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Battlegrounds;

/// <summary>
/// A group (or a lone player) waiting in a battleground queue (vmangos <c>GroupQueueInfo</c>, BattleGroundMgr.h:57-66).
/// </summary>
public sealed class QueuedGroup
{
    internal QueuedGroup(BattlegroundType type, Team team, uint joinTimeMs, uint desiredInstanceId)
    {
        Type = type;
        Team = team;
        JoinTimeMs = joinTimeMs;
        DesiredInstanceId = desiredInstanceId;
    }

    internal List<ObjectGuid> PlayerList { get; } = [];

    public IReadOnlyList<ObjectGuid> Players => PlayerList;

    /// <summary>The side the group fights on.</summary>
    public Team Team { get; internal set; }

    public BattlegroundType Type { get; }

    /// <summary>When the group joined, in manager milliseconds (vmangos <c>joinTime</c>).</summary>
    public uint JoinTimeMs { get; }

    /// <summary>When the invitation lapses (vmangos <c>removeInviteTime</c>); meaningful once invited.</summary>
    public uint RemoveInviteTimeMs { get; internal set; }

    /// <summary>The instance the group was invited to, 0 when it is still waiting (vmangos <c>isInvitedToBgInstanceGuid</c>).</summary>
    public uint InvitedToInstanceId { get; internal set; }

    /// <summary>The instance the group asked for specifically, 0 for first available (vmangos <c>desiredInstanceId</c>).</summary>
    public uint DesiredInstanceId { get; }

    public int Size => PlayerList.Count;
}

/// <summary>The four lists of a bracket (vmangos <c>BattleGroundQueueGroupTypes</c>, BattleGroundMgr.h:68-75).</summary>
internal static class QueueLists
{
    public const int PremadeAlliance = 0;
    public const int PremadeHorde = 1;
    public const int NormalAlliance = 2;
    public const int NormalHorde = 3;
    public const int Count = 4;
}

/// <summary>
/// The selection pool of one side while a match is being put together (vmangos <c>BattleGroundQueue::SelectionPool</c>,
/// BattleGroundMgr.cpp:81-138).
/// </summary>
internal sealed class SelectionPool
{
    public List<QueuedGroup> SelectedGroups { get; } = [];

    public uint PlayerCount { get; private set; }

    public void Init()
    {
        SelectedGroups.Clear();
        PlayerCount = 0;
    }

    /// <summary>
    /// Remove the group of about <paramref name="size"/> players that fits best: the last one within one of the size, else the largest
    /// (BattleGroundMgr.cpp:92-119). True when the caller should try to add a new group.
    /// </summary>
    public bool KickGroup(uint size)
    {
        // Find the max group or the LAST group with size == size and kick it.
        bool found = false;
        int groupToKick = 0;
        for (int i = 0; i < SelectedGroups.Count; i++)
        {
            if (Math.Abs(SelectedGroups[i].Size - (int)size) <= 1)
            {
                groupToKick = i;
                found = true;
            }
            else if (!found && SelectedGroups[i].Size >= SelectedGroups[groupToKick].Size)
            {
                groupToKick = i;
            }
        }

        // If the pool is empty, do nothing.
        if (PlayerCount != 0)
        {
            QueuedGroup group = SelectedGroups[groupToKick];
            SelectedGroups.RemoveAt(groupToKick);
            PlayerCount -= (uint)group.Size;

            // False if a smaller group was kicked or the pool has enough players.
            if (group.Size <= size + 1)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Add a group to the pool (BattleGroundMgr.cpp:125-138). True means "keep offering groups": the group was added or there is still room.
    /// A group is added only when it is not already invited, fits the desired instance and does not overflow <paramref name="desiredCount"/>.
    /// </summary>
    public bool AddGroup(QueuedGroup group, uint desiredCount, uint bgClientInstanceId)
    {
        if (group.InvitedToInstanceId == 0
            && (group.DesiredInstanceId == 0 || group.DesiredInstanceId == bgClientInstanceId)
            && desiredCount >= PlayerCount + (uint)group.Size)
        {
            SelectedGroups.Add(group);
            PlayerCount += (uint)group.Size;
            return true;
        }

        return PlayerCount < desiredCount;
    }
}

/// <summary>
/// The queue of one battleground type: groups per bracket, the match selection and the invitations (vmangos
/// <c>BattleGroundQueue</c>, BattleGroundMgr.cpp:49-975). Owned and driven by <see cref="BattlegroundManager"/>; world thread.
/// </summary>
internal sealed class BattlegroundQueue
{
    private const int CountOfPlayersToAverageWaitTime = 10;
    private const uint OfflineQueueTimeMs = 60 * 1000;

    private sealed class PlayerInfo(QueuedGroup group)
    {
        public bool Online { get; set; } = true;

        public uint LastOnlineTimeMs { get; set; }

        public QueuedGroup Group { get; set; } = group;
    }

    private readonly BattlegroundManager _manager;
    private readonly Dictionary<ObjectGuid, PlayerInfo> _players = [];
    private readonly List<QueuedGroup>[,] _groups = new List<QueuedGroup>[BattlegroundConstants.BracketCount, QueueLists.Count];
    private readonly SelectionPool[] _pools = [new SelectionPool(), new SelectionPool()];
    private readonly uint[,,] _waitTimes = new uint[2, BattlegroundConstants.BracketCount, CountOfPlayersToAverageWaitTime];
    private readonly uint[,] _waitTimeLastPlayer = new uint[2, BattlegroundConstants.BracketCount];
    private readonly uint[,] _sumOfWaitTimes = new uint[2, BattlegroundConstants.BracketCount];

    public BattlegroundQueue(BattlegroundManager manager)
    {
        _manager = manager;
        for (int b = 0; b < BattlegroundConstants.BracketCount; b++)
        {
            for (int t = 0; t < QueueLists.Count; t++)
            {
                _groups[b, t] = [];
            }
        }
    }

    private BattlegroundOptions Options => _manager.Options;

    public int PlayersQueued => _players.Count;

    /// <summary>Players waiting in each team's lists of a level bracket (vmangos HandleBGStatusCommand, MiscCommands.cpp:1767-1797).</summary>
    public (int Alliance, int Horde) TeamCounts(int bracket)
        => (_groups[bracket, QueueLists.PremadeAlliance].Sum(g => g.Size) + _groups[bracket, QueueLists.NormalAlliance].Sum(g => g.Size),
            _groups[bracket, QueueLists.PremadeHorde].Sum(g => g.Size) + _groups[bracket, QueueLists.NormalHorde].Sum(g => g.Size));

    public bool Contains(ObjectGuid guid) => _players.ContainsKey(guid);

    /// <summary>The queued group of a player (vmangos <c>GetPlayerGroupInfoData</c>), or null.</summary>
    public QueuedGroup? GroupOf(ObjectGuid guid) => _players.TryGetValue(guid, out PlayerInfo? info) ? info.Group : null;

    public int GroupsIn(int bracket, int list) => _groups[bracket, list].Count;

    // ------------------------------------------------------------------ joining and leaving

    /// <summary>
    /// Add a group, or a lone player when <paramref name="members"/> has one entry (vmangos <c>AddGroup</c>, BattleGroundMgr.cpp:145-259).
    /// A group larger than the configured limit (<paramref name="groupSize"/>, the whole party) is queued as individuals; the returned group then has no players.
    /// </summary>
    public QueuedGroup AddGroup(ObjectGuid leader, Team leaderTeam, IReadOnlyList<ObjectGuid> members, int groupSize, BattlegroundType type, int bracket, bool isPremade, uint instanceId, uint nowMs, bool isGroup)
    {
        var group = new QueuedGroup(type, leaderTeam, nowMs, instanceId);

        // Index of the list: premade or normal, alliance or horde.
        int index = isPremade ? 0 : QueueLists.NormalAlliance;
        if (leaderTeam == Team.Horde)
        {
            index++;
        }

        if (isGroup)
        {
            uint limit = Options.GroupQueueLimit;
            foreach (ObjectGuid member in members)
            {
                if (groupSize > limit)
                {
                    // Queue the players solo if the group is above the limit set in the configuration.
                    AddGroup(member, leaderTeam, [member], 1, type, bracket, false, instanceId, nowMs, isGroup: false);
                    _manager.Host.GroupQueueLimitNotice(member, limit);
                }
                else
                {
                    Register(group, member);
                }
            }
        }
        else
        {
            Register(group, leader);
        }

        if (group.PlayerList.Count != 0)
        {
            _groups[bracket, index].Add(group);
        }

        return group;
    }

    private void Register(QueuedGroup group, ObjectGuid player)
    {
        _players[player] = new PlayerInfo(group) { Online = true, LastOnlineTimeMs = 0 };
        group.PlayerList.Add(player);
    }

    /// <summary>
    /// Remove a player from the queue and from its group; an empty group goes too (vmangos <c>RemovePlayer</c>, BattleGroundMgr.cpp:296-373).
    /// When <paramref name="decreaseInvitedCount"/> is set and the group was invited to a running battleground, the invitation is given back.
    /// </summary>
    public void RemovePlayer(ObjectGuid guid, bool decreaseInvitedCount)
    {
        if (!_players.TryGetValue(guid, out PlayerInfo? info))
        {
            return;
        }

        QueuedGroup group = info.Group;
        int foundBracket = -1;
        int foundList = -1;

        // Most players are in the higher brackets, so count down; premade and normal lists are both searched because a premade that joined
        // a battleground leaves its group info.
        int firstList = group.Team == Team.Alliance ? 0 : 1;
        for (int bracket = BattlegroundConstants.BracketCount - 1; bracket >= 0 && foundBracket == -1; bracket--)
        {
            for (int list = firstList; list < QueueLists.Count; list += QueueLists.NormalAlliance)
            {
                if (_groups[bracket, list].Contains(group))
                {
                    foundBracket = bracket;
                    foundList = list;
                    break;
                }
            }
        }

        if (foundBracket == -1)
        {
            // A player cannot be in the queue without a group; vmangos logs and keeps the player entry.
            return;
        }

        group.PlayerList.Remove(guid);

        if (decreaseInvitedCount && group.InvitedToInstanceId != 0)
        {
            _manager.GetBattleground(group.InvitedToInstanceId, group.Type)?.DecreaseInvitedCount(group.Team);
        }

        _players.Remove(guid);

        if (group.PlayerList.Count == 0)
        {
            _groups[foundBracket, foundList].Remove(group);
        }
    }

    /// <summary>True when the player is queued and invited to <paramref name="instanceId"/> with this removal time (vmangos <c>IsPlayerInvited</c>).</summary>
    public bool IsPlayerInvited(ObjectGuid guid, uint instanceId, uint removeTimeMs)
        => _players.TryGetValue(guid, out PlayerInfo? info) && info.Group.InvitedToInstanceId == instanceId && info.Group.RemoveInviteTimeMs == removeTimeMs;

    public void PlayerLoggedOut(ObjectGuid guid, uint nowMs)
    {
        if (_players.TryGetValue(guid, out PlayerInfo? info))
        {
            info.LastOnlineTimeMs = nowMs;
            info.Online = false;
        }
    }

    public bool PlayerLoggedIn(ObjectGuid guid)
    {
        if (_players.TryGetValue(guid, out PlayerInfo? info))
        {
            info.Online = true;
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------------ wait time

    private void PlayerInvitedUpdateAverageWaitTime(QueuedGroup group, int bracket, uint nowMs)
    {
        uint timeInQueue = nowMs - group.JoinTimeMs;
        int team = BattlegroundConstants.TeamIndex(group.Team);

        // Remove the oldest time from the sum, store the new one and move on (a ring of the last 10 invited players).
        uint last = _waitTimeLastPlayer[team, bracket];
        _sumOfWaitTimes[team, bracket] -= _waitTimes[team, bracket, last];
        _waitTimes[team, bracket, last] = timeInQueue;
        _sumOfWaitTimes[team, bracket] += timeInQueue;
        _waitTimeLastPlayer[team, bracket] = (last + 1) % CountOfPlayersToAverageWaitTime;
    }

    /// <summary>The average wait of the last invited players of the group's side (vmangos <c>GetAverageQueueWaitTime</c>); 0 until ten were invited.</summary>
    public uint AverageQueueWaitTime(QueuedGroup group, int bracket)
    {
        int team = BattlegroundConstants.TeamIndex(group.Team);
        return _waitTimes[team, bracket, CountOfPlayersToAverageWaitTime - 1] != 0
            ? _sumOfWaitTimes[team, bracket] / CountOfPlayersToAverageWaitTime
            : 0;
    }

    // ------------------------------------------------------------------ invitations

    /// <summary>Invite a group to a battleground (vmangos <c>InviteGroupToBG</c>, BattleGroundMgr.cpp:395-449).</summary>
    public bool InviteGroup(QueuedGroup group, Battleground bg, Team side)
    {
        group.Team = side;
        if (group.InvitedToInstanceId != 0)
        {
            return false;
        }

        group.InvitedToInstanceId = bg.InstanceId;
        group.RemoveInviteTimeMs = _manager.NowMs + BattlegroundConstants.InviteAcceptWaitTimeMs;

        foreach (ObjectGuid guid in group.PlayerList.ToArray())
        {
            bg.IncreaseInvitedCount(group.Team);
            if (!_manager.Host.IsOnline(guid))
            {
                continue;
            }

            PlayerInvitedUpdateAverageWaitTime(group, bg.Bracket, _manager.NowMs);
            _manager.OnInvited(guid, bg, group);
        }

        return true;
    }

    // ------------------------------------------------------------------ the update

    /// <summary>One pass of the queue for a type and bracket (vmangos <c>BattleGroundQueue::Update</c>, BattleGroundMgr.cpp:870-891).</summary>
    public void Update(BattlegroundType type, int bracket)
    {
        RemoveOfflinePlayers();

        if (!HasPlayersInQueue(bracket))
        {
            return;
        }

        // Can the players join a battleground that already runs?
        CheckFreeSlots(type, bracket);

        // When all running battlegrounds are full, or none runs, can a new one be created?
        if (CheckCreateNewBattleground(type, bracket))
        {
            // The players left over may fit into the battleground that was just created.
            CheckFreeSlots(type, bracket);
        }
    }

    private bool HasPlayersInQueue(int bracket)
    {
        for (int list = 0; list < QueueLists.Count; list++)
        {
            if (_groups[bracket, list].Count != 0)
            {
                return true;
            }
        }

        return false;
    }

    private void RemoveOfflinePlayers()
    {
        foreach ((ObjectGuid guid, PlayerInfo info) in _players.ToArray())
        {
            bool remove = false;
            if (!info.Online && _manager.NowMs - info.LastOnlineTimeMs > OfflineQueueTimeMs)
            {
                remove = true;
            }
            else if (info.Group.InvitedToInstanceId != 0
                && _manager.GetBattleground(info.Group.InvitedToInstanceId, info.Group.Type) is { Status: BattlegroundStatus.WaitLeave } ended)
            {
                // The match ended: its invited players leave the queue (BattleGroundMgr.cpp:642-667).
                if (info.Online)
                {
                    _manager.ReleaseQueueSlot(guid, ended.Type, ended);
                }

                remove = true;
            }

            if (remove)
            {
                RemovePlayer(guid, true);
            }
        }
    }

    private void CheckFreeSlots(BattlegroundType type, int bracket)
    {
        List<Battleground> full = [];
        foreach (Battleground bg in _manager.FreeSlotBattlegrounds(type))
        {
            if (bg.Type != type || bg.Bracket != bracket)
            {
                continue;
            }

            if (bg.Status <= BattlegroundStatus.WaitQueue || bg.Status >= BattlegroundStatus.WaitLeave)
            {
                continue;
            }

            if (!bg.HasFreeSlots)
            {
                full.Add(bg);
                continue;
            }

            _pools[0].Init();
            _pools[1].Init();

            FillPlayersToBattleground(bg, bracket);

            foreach (QueuedGroup group in _pools[0].SelectedGroups.ToArray())
            {
                InviteGroup(group, bg, group.Team);
            }

            foreach (QueuedGroup group in _pools[1].SelectedGroups.ToArray())
            {
                InviteGroup(group, bg, group.Team);
            }

            if (!bg.HasFreeSlots)
            {
                full.Add(bg);
            }
        }

        foreach (Battleground bg in full)
        {
            bg.RemoveFromFreeSlotQueue();
        }
    }

    /// <summary>Select the groups to invite to an already running battleground (vmangos <c>FillPlayersToBg</c>, BattleGroundMgr.cpp:456-529).</summary>
    private void FillPlayersToBattleground(Battleground bg, int bracket)
    {
        int hordeFree = (int)bg.FreeSlotsForTeam(Team.Horde);
        int allyFree = (int)bg.FreeSlotsForTeam(Team.Alliance);

        List<QueuedGroup> ally = _groups[bracket, QueueLists.NormalAlliance];
        int aliCount = ally.Count;
        int aliIndex = 0;
        while (aliIndex < aliCount && _pools[0].AddGroup(ally[aliIndex], (uint)allyFree, bg.ClientInstanceId))
        {
            aliIndex++;
        }

        List<QueuedGroup> horde = _groups[bracket, QueueLists.NormalHorde];
        int hordeCount = horde.Count;
        int hordeIndex = 0;
        while (hordeIndex < hordeCount && _pools[1].AddGroup(horde[hordeIndex], (uint)hordeFree, bg.ClientInstanceId))
        {
            hordeIndex++;
        }

        // Invitation type 0 is happy with that.
        if (Options.InvitationType == 0)
        {
            return;
        }

        // Compare the free space with the selection pools and rebalance (the subset-sum heuristics of vmangos, BattleGroundMgr.cpp:480-528).
        int diffAli = allyFree - (int)_pools[0].PlayerCount;
        int diffHorde = hordeFree - (int)_pools[1].PlayerCount;
        while (Math.Abs(diffAli - diffHorde) > 1 && (_pools[1].PlayerCount > 0 || _pools[0].PlayerCount > 0))
        {
            // Each pass kicks at least one group.
            if (diffAli < diffHorde)
            {
                // Kick an Alliance group, add a new one to the pool if needed.
                if (_pools[0].KickGroup((uint)(diffHorde - diffAli)))
                {
                    while (aliIndex < aliCount && _pools[0].AddGroup(ally[aliIndex], allyFree >= diffHorde ? (uint)(allyFree - diffHorde) : 0, bg.ClientInstanceId))
                    {
                        aliIndex++;
                    }
                }

                // If the Alliance selection is empty now, kick a Horde group; but with fewer Horde than Alliance in the battleground, stop.
                if (_pools[0].PlayerCount == 0)
                {
                    if (allyFree <= diffHorde + 1)
                    {
                        break;
                    }

                    _pools[1].KickGroup((uint)(diffHorde - diffAli));
                }
            }
            else
            {
                if (_pools[1].KickGroup((uint)(diffAli - diffHorde)))
                {
                    while (hordeIndex < hordeCount && _pools[1].AddGroup(horde[hordeIndex], hordeFree >= diffAli ? (uint)(hordeFree - diffAli) : 0, bg.ClientInstanceId))
                    {
                        hordeIndex++;
                    }
                }

                if (_pools[1].PlayerCount == 0)
                {
                    if (hordeFree <= diffAli + 1)
                    {
                        break;
                    }

                    _pools[0].KickGroup((uint)(diffAli - diffHorde));
                }
            }

            diffAli = allyFree - (int)_pools[0].PlayerCount;
            diffHorde = hordeFree - (int)_pools[1].PlayerCount;
        }
    }

    // ------------------------------------------------------------------ creating a battleground

    private bool CheckCreateNewBattleground(BattlegroundType type, int bracket)
    {
        BattlegroundTemplate? template = _manager.TemplateOf(type);
        if (template is null)
        {
            return false;
        }

        uint minPlayersPerTeam = template.MinPlayersPerTeam;
        uint maxPlayersPerTeam = template.MaxPlayersPerTeam;

        uint qMinLevel = BattlegroundConstants.MinLevelOfBracket(type, bracket, template.MinLevel);
        uint qMaxLevel = BattlegroundConstants.MaxLevelOfBracket(type, bracket, template.MinLevel) - 1;

        bool created = false;

        // A premade-versus-premade match first.
        _pools[0].Init();
        _pools[1].Init();
        if (CheckPremadeMatch(bracket, minPlayersPerTeam, maxPlayersPerTeam))
        {
            created = CreateAndInvite(type, bracket, qMinLevel, qMaxLevel);
            if (!created)
            {
                return false;
            }
        }

        // A normal match only when no premade one was created.
        if (!created)
        {
            _pools[0].Init();
            _pools[1].Init();
            if (CheckNormalMatch(bracket, minPlayersPerTeam, maxPlayersPerTeam))
            {
                created = CreateAndInvite(type, bracket, qMinLevel, qMaxLevel);
            }
        }

        return created;
    }

    private bool CreateAndInvite(BattlegroundType type, int bracket, uint minLevel, uint maxLevel)
    {
        Battleground? bg = _manager.CreateNewBattleground(type, bracket);
        if (bg is null)
        {
            return false;
        }

        for (int i = 0; i < 2; i++)
        {
            foreach (QueuedGroup group in _pools[i].SelectedGroups.ToArray())
            {
                InviteGroup(group, bg, group.Team);
            }
        }

        bg.SetLevelRange(minLevel, maxLevel);
        _manager.StartBattleground(bg);
        return true;
    }

    /// <summary>Premade versus premade (vmangos <c>CheckPremadeMatch</c>, BattleGroundMgr.cpp:534-584).</summary>
    private bool CheckPremadeMatch(int bracket, uint minPlayersPerTeam, uint maxPlayersPerTeam)
    {
        for (int queueType = 0; queueType < 2; queueType++)
        {
            for (int i = 0; i < 2; i++)
            {
                foreach (QueuedGroup group in _groups[bracket, (2 * queueType) + i])
                {
                    if (group.InvitedToInstanceId == 0 && group.Size >= Options.PremadeQueueMinGroupSize)
                    {
                        _pools[i].AddGroup(group, maxPlayersPerTeam, 0);
                        if (_pools[i].PlayerCount >= minPlayersPerTeam)
                        {
                            break;
                        }
                    }
                }
            }
        }

        if (_pools[0].PlayerCount >= minPlayersPerTeam && _pools[1].PlayerCount >= minPlayersPerTeam)
        {
            return true;
        }

        // Move a premade group that waited long enough, or shrank, to the normal queue (the first group of each premade list only).
        long timeBefore = (long)_manager.NowMs - Options.PremadeGroupWaitForMatchMs;
        for (int i = 0; i < 2; i++)
        {
            List<QueuedGroup> premade = _groups[bracket, QueueLists.PremadeAlliance + i];
            if (premade.Count != 0)
            {
                QueuedGroup first = premade[0];
                if (first.InvitedToInstanceId == 0 && (first.JoinTimeMs < timeBefore || first.Size < minPlayersPerTeam))
                {
                    _groups[bracket, QueueLists.NormalAlliance + i].Add(first);
                    premade.RemoveAt(0);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// A battleground with the minimum against the minimum (vmangos <c>CheckNormalMatch</c>, BattleGroundMgr.cpp:587-628).
    /// With balanced invitations (type 1) the side with fewer players is topped up and a difference above two players refuses the match.
    /// </summary>
    private bool CheckNormalMatch(int bracket, uint minPlayers, uint maxPlayers)
    {
        int[] position = [0, 0];
        for (int i = 0; i < 2; i++)
        {
            List<QueuedGroup> list = _groups[bracket, QueueLists.NormalAlliance + i];
            for (; position[i] < list.Count; position[i]++)
            {
                QueuedGroup group = list[position[i]];
                if (group.InvitedToInstanceId == 0)
                {
                    _pools[i].AddGroup(group, maxPlayers, 0);
                    if (_pools[i].PlayerCount >= minPlayers)
                    {
                        break;
                    }
                }
            }
        }

        // Try to invite the same number of players on both sides: top up the side with fewer.
        int j = 0;
        if (_pools[1].PlayerCount < _pools[0].PlayerCount)
        {
            j = 1;
        }

        if (Options.InvitationType != 0 && _pools[1].PlayerCount >= minPlayers && _pools[0].PlayerCount >= minPlayers)
        {
            List<QueuedGroup> list = _groups[bracket, QueueLists.NormalAlliance + j];
            position[j]++;      // the loop above reached its break
            for (; position[j] < list.Count; position[j]++)
            {
                QueuedGroup group = list[position[j]];
                if (group.InvitedToInstanceId == 0 && !_pools[j].AddGroup(group, _pools[(j + 1) % 2].PlayerCount, 0))
                {
                    break;
                }
            }

            // Do not start a battleground with more than two players more on one side.
            if (Math.Abs((int)_pools[1].PlayerCount - (int)_pools[0].PlayerCount) > 2)
            {
                return false;
            }
        }

        return _pools[0].PlayerCount >= minPlayers && _pools[1].PlayerCount >= minPlayers;
    }
}
