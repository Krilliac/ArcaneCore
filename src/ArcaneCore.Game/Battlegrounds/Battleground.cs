using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Battlegrounds;

/// <summary>
/// One battleground match: the state machine of vmangos <c>BattleGround</c> (BattleGround.cpp, BattleGround.h). It is pure rules:
/// time only moves through <see cref="Update"/>, and everything that touches a player, a map object or a packet goes through
/// <see cref="BattlegroundPorts"/>. A type derives and adds its objectives (<see cref="WarsongGulch"/>). World thread.
/// </summary>
public abstract partial class Battleground
{
    private sealed class Participant(Team team, BattlegroundScore score)
    {
        public Team Team { get; } = team;

        public BattlegroundScore Score { get; } = score;
    }

    private readonly Dictionary<ObjectGuid, Participant> _players = [];

    // Each team's participants in join order: the battleground raid group of vmangos (AddOrSetPlayerToCorrectBgGroup), whose
    // first member leads and whose next member leads when the leader leaves (Group::RemoveMember → _chooseLeader).
    private readonly List<ObjectGuid>[] _joinOrder = [[], []];
    private readonly Dictionary<byte, byte> _activeEvents = [];
    private readonly uint[] _invited = new uint[2];
    private readonly uint[] _playersCount = new uint[2];
    private readonly int[] _startDelays = [BattlegroundConstants.StartDelay2MinMs, BattlegroundConstants.StartDelay1MinMs, BattlegroundConstants.StartDelay30SecMs, BattlegroundConstants.StartDelayNoneMs];
    private BattlegroundStartEvents _events;
    private bool _prematureCountDown;
    private uint _prematureCountDownTimer;
    private uint _ageMs;

    protected Battleground(BattlegroundTemplate template, int bracket, uint instanceId, uint clientInstanceId, BattlegroundOptions options, BattlegroundPorts ports)
    {
        Template = template ?? throw new ArgumentNullException(nameof(template));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Ports = ports ?? throw new ArgumentNullException(nameof(ports));
        if (bracket is < 0 or > BattlegroundConstants.LastBracket)
        {
            throw new ArgumentOutOfRangeException(nameof(bracket), bracket, "a battleground bracket is 0 to 5");
        }

        Bracket = bracket;
        MinLevel = template.MinLevel;
        MaxLevel = template.MaxLevel;
        InstanceId = instanceId;
        ClientInstanceId = clientInstanceId;

        // BattleGroundMgr::CreateNewBattleGround (BattleGroundMgr.cpp:1269-1274): Reset(), then WAIT_JOIN.
        Status = BattlegroundStatus.WaitJoin;
        Winner = BattlegroundWinner.None;
        _activeEvents[BattlegroundConstants.EventDoor] = 0;   // door-event2 is always 0 (BattleGround.cpp:1013)
    }

    // ------------------------------------------------------------------ identity

    public BattlegroundTemplate Template { get; }

    public BattlegroundOptions Options { get; }

    protected BattlegroundPorts Ports { get; }

    public BattlegroundType Type => Template.Type;

    public string Name => Template.Name;

    public uint MapId => Template.MapId;

    /// <summary>The bracket (level range) of the match, 0 to 5.</summary>
    public int Bracket { get; }

    /// <summary>The map instance id (vmangos <c>GetInstanceID</c>).</summary>
    public uint InstanceId { get; }

    /// <summary>The instance id the client sees in the battleground list (vmangos <c>GetClientInstanceID</c>).</summary>
    public uint ClientInstanceId { get; }

    /// <summary>The lowest level of the match's bracket (vmangos <c>GetMinLevel</c>; set by <see cref="SetLevelRange"/>, the template's minimum before that).</summary>
    public uint MinLevel { get; private set; }

    /// <summary>The highest level of the match's bracket (vmangos <c>GetMaxLevel</c>).</summary>
    public uint MaxLevel { get; private set; }

    /// <summary>vmangos <c>SetLevelRange</c>: the bracket's levels, set when the queue creates the match.</summary>
    public void SetLevelRange(uint minLevel, uint maxLevel)
    {
        MinLevel = minLevel;
        MaxLevel = maxLevel;
    }

    public uint MinPlayersPerTeam => Template.MinPlayersPerTeam;

    public uint MaxPlayersPerTeam => Template.MaxPlayersPerTeam;

    public uint MinPlayers => Template.MinPlayersPerTeam * 2;

    public uint MaxPlayers => Template.MaxPlayersPerTeam * 2;

    // ------------------------------------------------------------------ state

    public BattlegroundStatus Status { get; protected set; }

    public BattlegroundWinner Winner { get; protected set; }

    /// <summary>Milliseconds since <see cref="StartBattleground"/>; it also counts the two minutes of preparation (vmangos <c>m_startTime</c>).</summary>
    public uint StartTimeMs { get; protected set; }

    /// <summary>Milliseconds until players are removed once the match ended (vmangos <c>m_endTime</c>).</summary>
    public int EndTimeMs { get; protected set; }

    /// <summary>The start countdown (vmangos <c>m_startDelayTime</c>); at or below zero the match begins.</summary>
    public int StartDelayMs { get; protected set; }

    /// <summary>Whether the battleground is in the manager's free-slot list (vmangos <c>m_inBGFreeSlotQueue</c>).</summary>
    public bool InFreeSlotQueue { get; private set; }

    /// <summary>The scoreboard frozen when the match ended (vmangos <c>m_finalScore</c>), or null while it runs.</summary>
    public PvpLogSnapshot? FinalScore { get; private set; }

    public int PlayerCount => _players.Count;

    public IEnumerable<ObjectGuid> Players => _players.Keys;

    public uint PlayersCountByTeam(Team team) => _playersCount[BattlegroundConstants.TeamIndex(team)];

    /// <summary>The team a participant fights for, or null when the guid is not in the match.</summary>
    public Team? PlayerTeam(ObjectGuid guid) => _players.TryGetValue(guid, out Participant? p) ? p.Team : null;

    /// <summary>A team's participants in join order: the members of its battleground raid group (vmangos); the first one leads.</summary>
    public IReadOnlyList<ObjectGuid> TeamMembers(Team team) => _joinOrder[BattlegroundConstants.TeamIndex(team)];

    public BattlegroundScore? ScoreOf(ObjectGuid guid) => _players.TryGetValue(guid, out Participant? p) ? p.Score : null;

    /// <summary>The message of each start event; index 0 is silent (vmangos <c>m_startMessageIds</c>).</summary>
    protected uint[] StartMessageIds { get; } = [0, 0, 0, 0];

    /// <summary>False for a type that does not use the premature finish (Alterac Valley, BattleGround.cpp:318).</summary>
    protected virtual bool UsesPrematureFinish => true;

    protected IBattlegroundHost Host => Ports.Host;

    // ------------------------------------------------------------------ invitations and slots

    public uint InvitedCount(Team team) => _invited[BattlegroundConstants.TeamIndex(team)];

    public void IncreaseInvitedCount(Team team) => _invited[BattlegroundConstants.TeamIndex(team)]++;

    /// <summary>Lower the invitation count; vmangos asserts it is positive, this ignores a call at zero.</summary>
    public void DecreaseInvitedCount(Team team)
    {
        ref uint count = ref _invited[BattlegroundConstants.TeamIndex(team)];
        if (count > 0)
        {
            count--;
        }
    }

    /// <summary>vmangos <c>GetFreeSlotsForTeam</c> (BattleGround.cpp:1186-1189): the maximum less the invited, while starting or running.</summary>
    public uint FreeSlotsForTeam(Team team)
    {
        if (Status is BattlegroundStatus.WaitJoin or BattlegroundStatus.InProgress)
        {
            uint invited = InvitedCount(team);
            return invited < MaxPlayersPerTeam ? MaxPlayersPerTeam - invited : 0;
        }

        return 0;
    }

    /// <summary>vmangos <c>HasFreeSlots</c>: fewer players inside than the maximum.</summary>
    public bool HasFreeSlots => _players.Count < MaxPlayers;

    /// <summary>vmangos <c>StartBattleGround</c> (BattleGround.cpp:1029-1040): the match clock starts at zero and the battleground takes new players.</summary>
    public void StartBattleground()
    {
        StartTimeMs = 0;
        AddToFreeSlotQueue();
    }

    /// <summary>vmangos <c>AddToBGFreeSlotQueue</c>: once.</summary>
    public void AddToFreeSlotQueue()
    {
        if (!InFreeSlotQueue)
        {
            InFreeSlotQueue = true;
            Ports.Lifecycle.AddToFreeSlotQueue(this);
        }
    }

    /// <summary>vmangos <c>RemoveFromBGFreeSlotQueue</c>.</summary>
    public void RemoveFromFreeSlotQueue()
    {
        InFreeSlotQueue = false;
        Ports.Lifecycle.RemoveFromFreeSlotQueue(this);
    }

    // ------------------------------------------------------------------ update

    /// <summary>
    /// Advance the match by <paramref name="diffMs"/> (vmangos <c>BattleGround::Update</c>, BattleGround.cpp:290-464). Returns false
    /// when the battleground is empty with nobody invited and must be deleted (vmangos <c>delete this</c>).
    /// </summary>
    public virtual bool Update(uint diffMs)
    {
        _ageMs += diffMs;
        if (_players.Count == 0)
        {
            if (InvitedCount(Team.Alliance) == 0 && InvitedCount(Team.Horde) == 0)
            {
                return false;
            }

            // BattleGround.cpp:307-308: an invited-but-empty battleground nudges the queue after two minutes so a player who logged out does not block it.
            if (Status <= BattlegroundStatus.WaitJoin && _ageMs > 2 * 60 * 1000)
            {
                Ports.Lifecycle.ScheduleQueueUpdate(this);
            }

            return true;
        }

        UpdatePrematureFinish(diffMs);

        if (Status == BattlegroundStatus.WaitJoin)
        {
            StartDelayMs -= (int)diffMs;
            if (!UpdateStartSequence())
            {
                return true;
            }
        }

        // BattleGround.cpp:433: two minutes of preparation plus one minute.
        if ((_events & BattlegroundStartEvents.DoorsDespawned) == 0 && Status == BattlegroundStatus.InProgress && StartTimeMs > BattlegroundConstants.DoorsDespawnStartTimeMs)
        {
            StartingEventDespawnDoors();
            _events |= BattlegroundStartEvents.DoorsDespawned;
        }

        if (Status == BattlegroundStatus.WaitLeave)
        {
            EndTimeMs -= (int)diffMs;
            if (EndTimeMs <= 0)
            {
                EndTimeMs = 0;
                foreach (ObjectGuid guid in _players.Keys.ToArray())
                {
                    RemovePlayerAtLeave(guid, teleportToEntryPoint: true, sendStatus: true);
                }
            }
        }

        StartTimeMs += diffMs;
        return true;
    }

    private void UpdatePrematureFinish(uint diffMs)
    {
        if (UsesPrematureFinish && Status == BattlegroundStatus.InProgress && Options.PrematureFinishTimerMs != 0
            && (PlayersCountByTeam(Team.Alliance) < MinPlayersPerTeam || PlayersCountByTeam(Team.Horde) < MinPlayersPerTeam))
        {
            if (!_prematureCountDown)
            {
                _prematureCountDown = true;
                _prematureCountDownTimer = Options.PrematureFinishTimerMs;
            }
            else if (_prematureCountDownTimer < diffMs)
            {
                Team? winner = null;
                if (PlayersCountByTeam(Team.Alliance) >= MinPlayersPerTeam)
                {
                    winner = Team.Alliance;
                }
                else if (PlayersCountByTeam(Team.Horde) >= MinPlayersPerTeam)
                {
                    winner = Team.Horde;
                }

                EndBattleground(winner);
                _prematureCountDown = false;
            }
            else
            {
                uint newTime = _prematureCountDownTimer - diffMs;
                const uint minute = 60 * 1000;
                if (newTime > minute)
                {
                    if (newTime / minute != _prematureCountDownTimer / minute)
                    {
                        Host.AnnouncePrematureFinish(_prematureCountDownTimer / minute, inMinutes: true);
                    }
                }
                else if (newTime / (15 * 1000) != _prematureCountDownTimer / (15 * 1000))
                {
                    Host.AnnouncePrematureFinish(_prematureCountDownTimer / 1000, inMinutes: false);
                }

                _prematureCountDownTimer = newTime;
            }
        }
        else if (_prematureCountDown)
        {
            _prematureCountDown = false;
        }
    }

    /// <summary>The WAIT_JOIN countdown (BattleGround.cpp:363-431); returns false when the match had to be ended at once.</summary>
    private bool UpdateStartSequence()
    {
        if ((_events & BattlegroundStartEvents.First) == 0)
        {
            _events |= BattlegroundStartEvents.First;

            // Set up when at least one player has ported to the map.
            if (!SetupBattleground())
            {
                EndNow();
                return false;
            }

            StartingEventCloseDoors();
            StartDelayMs = _startDelays[0];
            if (StartMessageIds[0] != 0)
            {
                Host.Announce(StartMessageIds[0], BattlegroundChatKind.Neutral, ObjectGuid.Empty);
            }
        }
        else if (StartDelayMs <= _startDelays[1] && (_events & BattlegroundStartEvents.Second) == 0)
        {
            _events |= BattlegroundStartEvents.Second;
            Host.Announce(StartMessageIds[1], BattlegroundChatKind.Neutral, ObjectGuid.Empty);
        }
        else if (StartDelayMs <= _startDelays[2] && (_events & BattlegroundStartEvents.Third) == 0)
        {
            _events |= BattlegroundStartEvents.Third;
            Host.Announce(StartMessageIds[2], BattlegroundChatKind.Neutral, ObjectGuid.Empty);
        }
        else if (StartDelayMs <= 0 && (_events & BattlegroundStartEvents.Fourth) == 0)
        {
            _events |= BattlegroundStartEvents.Fourth;

            StartingEventOpenDoors();
            ReturnPlayersToHomeGraveyard();
            Host.Announce(StartMessageIds[3], BattlegroundChatKind.Neutral, ObjectGuid.Empty);
            Status = BattlegroundStatus.InProgress;
            StartDelayMs = _startDelays[3];
            Host.PlaySoundToAll(BattlegroundConstants.SoundStart);
        }

        return true;
    }

    /// <summary>Called once, when the first player has ported in; false ends the battleground at once (vmangos <c>SetupBattleGround</c>).</summary>
    protected virtual bool SetupBattleground() => true;

    protected virtual void StartingEventCloseDoors()
    {
    }

    protected virtual void StartingEventOpenDoors()
    {
    }

    /// <summary>vmangos <c>StartingEventDespawnDoors</c> (BattleGround.cpp:1432-1447).</summary>
    protected virtual void StartingEventDespawnDoors()
    {
        if (!IsActiveEvent(BattlegroundConstants.EventDoor, 0))
        {
            return;
        }

        Host.DespawnDoors();
    }

    private void ReturnPlayersToHomeGraveyard()
    {
        foreach ((ObjectGuid guid, Participant p) in _players.ToArray())
        {
            Host.ReturnToStartIfFar(guid, p.Team);
        }
    }

    /// <summary>vmangos <c>EndNow</c>: end without a winner and remove the players at the next update.</summary>
    protected void EndNow()
    {
        RemoveFromFreeSlotQueue();
        Status = BattlegroundStatus.WaitLeave;
        EndTimeMs = 0;
        FinalScore ??= BuildPvpLog();
    }

    // ------------------------------------------------------------------ players

    /// <summary>Per-type score row (vmangos creates it in each subclass's <c>AddPlayer</c>).</summary>
    protected virtual BattlegroundScore CreateScore() => new();

    /// <summary>
    /// A player enters the match (vmangos <c>AddPlayer</c> run from the world-port acknowledgement, BattleGround.cpp:1042-1067).
    /// Returns false when the guid is already in the match.
    /// </summary>
    public virtual bool AddPlayer(ObjectGuid guid, Team team)
    {
        if (_players.ContainsKey(guid))
        {
            return false;
        }

        _players[guid] = new Participant(team, CreateScore());
        _playersCount[BattlegroundConstants.TeamIndex(team)]++;
        _joinOrder[BattlegroundConstants.TeamIndex(team)].Add(guid);
        Host.PlayerJoinedTeam(team, guid);

        // PlayerAddedToBGCheckIfBGIsRunning (BattleGround.cpp:1808-1822): a late joiner into an ended match is frozen and shown the result.
        if (Status == BattlegroundStatus.WaitLeave)
        {
            Host.BlockMovement(guid);
            // The frozen board, as MSG_PVP_LOG_DATA answers during WAIT_LEAVE (BattleGroundHandler.cpp:341). vmangos rebuilds it here
            // (BattleGround.cpp:1818), which drops whoever left after the end from this one client's board.
            Host.SendPvpLog(guid, FinalScore ?? BuildPvpLog());
            Host.SendStatus(guid, BattlegroundStatus.InProgress, (uint)EndTimeMs, StartTimeMs);
        }

        return true;
    }

    /// <summary>
    /// Take a player out (vmangos <c>RemovePlayerAtLeave</c>, BattleGround.cpp:910-1000). <paramref name="online"/> is false when the
    /// character is no longer in the world (logged out), so none of its world objects is touched.
    /// </summary>
    public virtual void RemovePlayerAtLeave(ObjectGuid guid, bool teleportToEntryPoint, bool sendStatus, bool online = true)
    {
        bool participant = false;
        Team team = default;
        if (_players.Remove(guid, out Participant? entry))
        {
            participant = true;
            team = entry.Team;
            _playersCount[BattlegroundConstants.TeamIndex(team)]--;
            _joinOrder[BattlegroundConstants.TeamIndex(team)].Remove(guid);
        }

        if (online)
        {
            Ports.Spells.RemoveSpiritOfRedemption(guid);
            Host.ResurrectIfDead(guid);
        }

        if (participant)
        {
            OnPlayerRemoved(guid, team, online);
            if (online && sendStatus)
            {
                Host.SendStatus(guid, BattlegroundStatus.None, 0, 0);
            }

            DecreaseInvitedCount(team);
            if (Status < BattlegroundStatus.WaitLeave)
            {
                // A player left, so there is a free slot again: back in the free-slot list, queue it (BattleGround.cpp:973-982).
                AddToFreeSlotQueue();
                Ports.Lifecycle.ScheduleQueueUpdate(this);
                Host.PlayerLeftTeam(team, guid);
            }
        }

        if (online)
        {
            Host.ClearPlayerBinding(guid);
            if (teleportToEntryPoint)
            {
                Host.TeleportToEntryPoint(guid);
            }
        }
    }

    /// <summary>Type-specific cleanup of a leaver, after it left the player maps (vmangos <c>RemovePlayer</c>).</summary>
    protected virtual void OnPlayerRemoved(ObjectGuid guid, Team team, bool online)
    {
    }

    // ------------------------------------------------------------------ scoring and rewards

    /// <summary>Change a scoreboard value (vmangos <c>UpdatePlayerScore</c>, BattleGround.cpp:1254-1282).</summary>
    public virtual void UpdatePlayerScore(ObjectGuid player, BattlegroundScoreType type, uint value)
    {
        if (!_players.TryGetValue(player, out Participant? p))
        {
            return;
        }

        switch (type)
        {
            case BattlegroundScoreType.KillingBlows:
                p.Score.KillingBlows += value;
                break;
            case BattlegroundScoreType.Deaths:
                p.Score.Deaths += value;
                break;
            case BattlegroundScoreType.HonorableKills:
                p.Score.HonorableKills += value;
                break;
            case BattlegroundScoreType.BonusHonor:
                // Honor is rewarded instantly and only shows on the scoreboard when it was added.
                if (Ports.Honor.TryAddBonusHonor(player, value))
                {
                    p.Score.BonusHonor += value;
                }

                break;
        }
    }

    /// <summary>vmangos <c>RewardHonorToTeam</c>.</summary>
    protected void RewardHonorToTeam(uint honor, Team team)
    {
        foreach ((ObjectGuid guid, Participant p) in _players.ToArray())
        {
            if (p.Team == team)
            {
                UpdatePlayerScore(guid, BattlegroundScoreType.BonusHonor, honor);
            }
        }
    }

    /// <summary>vmangos <c>RewardReputationToTeam</c>.</summary>
    protected void RewardReputationToTeam(uint factionId, int reputation, Team team)
    {
        foreach ((ObjectGuid guid, Participant p) in _players.ToArray())
        {
            if (p.Team == team)
            {
                Ports.Reputation.Reward(guid, factionId, reputation);
            }
        }
    }

    private void RewardMark(ObjectGuid guid, Team team, bool winner)
    {
        uint spell = winner
            ? (team == Team.Horde ? Template.HordeWinSpell : Template.AllianceWinSpell)
            : (team == Team.Horde ? Template.HordeLoseSpell : Template.AllianceLoseSpell);
        if (spell != 0)
        {
            Ports.Spells.CastOnSelf(guid, spell);
        }
    }

    /// <summary>
    /// Credit a kill (vmangos <c>BattleGround::HandleKillPlayer</c>, BattleGround.cpp:1759-1790). A kill by an enemy gives the killer a
    /// killing blow and honorable kill and the teammates within group reward distance an honorable kill; the victim gets a death unless in
    /// Spirit of Redemption. vmangos compares faction templates for "enemy"; this compares the match teams (they only differ for
    /// a mind-controlled player).
    /// </summary>
    public virtual void HandleKillPlayer(ObjectGuid victim, ObjectGuid? killer)
    {
        if (killer is { } k && _players.TryGetValue(k, out Participant? killerEntry) && _players.TryGetValue(victim, out Participant? victimEntry) && victimEntry.Team != killerEntry.Team)
        {
            UpdatePlayerScore(k, BattlegroundScoreType.HonorableKills, 1);
            UpdatePlayerScore(k, BattlegroundScoreType.KillingBlows, 1);

            foreach ((ObjectGuid guid, Participant p) in _players.ToArray())
            {
                if (guid != k && p.Team == killerEntry.Team && Host.IsAtGroupRewardDistance(guid, victim))
                {
                    UpdatePlayerScore(guid, BattlegroundScoreType.HonorableKills, 1);
                }
            }
        }

        if (!Ports.Spells.HasAura(victim, BattlegroundConstants.SpellSpiritOfRedemption))
        {
            UpdatePlayerScore(victim, BattlegroundScoreType.Deaths, 1);
            Host.MarkSkinnable(victim);
        }
    }

    // ------------------------------------------------------------------ the end

    /// <summary>Per-type texts of the winner announcement (vmangos <c>GetWinnerText</c>).</summary>
    protected virtual uint WinnerText(Team winner) => 0;

    /// <summary>
    /// End the match (vmangos <c>EndBattleGround</c>, BattleGround.cpp:651-769). <paramref name="winner"/> null means nobody won.
    /// </summary>
    public virtual void EndBattleground(Team? winner)
    {
        RemoveFromFreeSlotQueue();

        if (winner == Team.Alliance)
        {
            Host.PlaySoundToAll(BattlegroundConstants.SoundAllianceWins);
            Winner = BattlegroundWinner.Alliance;
        }
        else if (winner == Team.Horde)
        {
            Host.PlaySoundToAll(BattlegroundConstants.SoundHordeWins);
            Winner = BattlegroundWinner.Horde;
        }
        else
        {
            Winner = BattlegroundWinner.None;
        }

        Status = BattlegroundStatus.WaitLeave;
        EndTimeMs = BattlegroundConstants.TimeToAutoRemoveMs;
        FinalScore ??= BuildPvpLog();

        foreach ((ObjectGuid guid, Participant p) in _players.ToArray())
        {
            Ports.Spells.RemoveSpiritOfRedemption(guid);
            Host.ResurrectOrStopCombat(guid);

            if (p.Team == winner)
            {
                RewardMark(guid, p.Team, winner: true);
            }
            else if (StartTimeMs > BattlegroundConstants.LoserMarkMinStartTimeMs)
            {
                // Client patch 1.8.4: the losers only get a mark after a battle of ten minutes (BattleGround.cpp:705-709).
                RewardMark(guid, p.Team, winner: false);
            }

            Host.StopCombatWithPets(guid);
            Host.BlockMovement(guid);
            Host.SendEndOfMatch(guid, p.Team == winner, FinalScore, BattlegroundConstants.TimeToAutoRemoveMs, StartTimeMs);
        }

        // vmangos announces "Alliance wins" for a draw too (GetWinnerText falls through to the Alliance text); nobody announces nothing here.
        if (winner is { } w)
        {
            uint text = WinnerText(w);
            if (text != 0)
            {
                Host.Announce(text, BattlegroundChatKind.Neutral, ObjectGuid.Empty);
            }
        }

        // BattleGround.cpp:766-768: players still invited are dropped from the queue when the match ends.
        if (InvitedCount(Team.Horde) != 0 || InvitedCount(Team.Alliance) != 0)
        {
            Ports.Lifecycle.ScheduleQueueUpdate(this);
        }
    }

    /// <summary>The scoreboard now (vmangos <c>BuildPvpLogDataPacket</c>): at most 80 rows in guid order.</summary>
    public PvpLogSnapshot BuildPvpLog()
    {
        List<PvpLogRow> rows = [];
        foreach ((ObjectGuid guid, Participant p) in _players.OrderBy(kv => kv.Key.Value))
        {
            if (rows.Count >= BattlegroundConstants.PvpLogMaxPlayers)
            {
                break;
            }

            BattlegroundScore s = p.Score;
            rows.Add(new PvpLogRow(guid, Ports.Ranks.RankOf(guid) ?? BattlegroundConstants.DefaultPvpLogRank, s.KillingBlows, s.HonorableKills, s.Deaths, s.BonusHonor, [.. s.ExtraFields]));
        }

        return new PvpLogSnapshot(Status == BattlegroundStatus.WaitLeave, Winner, rows);
    }

    // ------------------------------------------------------------------ events (spawn gating)

    private byte ActiveEventValue(byte event1)
    {
        // std::map::operator[] creates a zero entry for an unknown key (BattleGround.cpp:1473).
        if (!_activeEvents.TryGetValue(event1, out byte value))
        {
            _activeEvents[event1] = 0;
            return 0;
        }

        return value;
    }

    /// <summary>vmangos <c>IsActiveEvent</c>.</summary>
    public bool IsActiveEvent(byte event1, byte event2) => _activeEvents.TryGetValue(event1, out byte active) && active == event2;

    /// <summary>Set the active event without spawning (vmangos <c>ActivateEventWithoutSpawn</c>, also what <c>Reset</c> does for a type's events).</summary>
    protected void SetActiveEvent(byte event1, byte event2) => _activeEvents[event1] = event2;

    /// <summary>
    /// (De)spawn the objects of an event (vmangos <c>SpawnEvent</c>, BattleGround.cpp:1469-1508). <paramref name="delaySeconds"/> is the respawn
    /// delay of the event's game objects when it spawns (vmangos passes it to <c>SpawnBGObject</c>; Arathi Basin banners use 1 and 5 s).
    /// </summary>
    protected void SpawnEvent(byte event1, byte event2, bool spawn, bool forcedDespawn, uint delaySeconds = 0)
    {
        byte active = ActiveEventValue(event1);
        if (event2 == BattlegroundConstants.EventNone || (spawn && active == event2) || (!spawn && active != event2))
        {
            return;
        }

        if (spawn)
        {
            // If the event gets spawned the current active event must despawn.
            SpawnEvent(event1, active, spawn: false, forcedDespawn);
            _activeEvents[event1] = event2;
        }
        else
        {
            _activeEvents[event1] = BattlegroundConstants.EventNone;
        }

        if (delaySeconds == 0)
        {
            Host.EventStateChanged(event1, event2, spawn, forcedDespawn);
        }
        else
        {
            Host.EventStateChanged(event1, event2, spawn, forcedDespawn, delaySeconds);
        }
    }

    /// <summary>vmangos <c>SetSpawnEventMode</c>: nothing for <see cref="BattlegroundConstants.EventNone"/>, otherwise the host applies the mode.</summary>
    protected void SetSpawnEventMode(byte event1, byte event2, BattlegroundSpawnMode mode)
    {
        if (event2 != BattlegroundConstants.EventNone)
        {
            Host.SetSpawnEventMode(event1, event2, mode);
        }
    }

    /// <summary>
    /// vmangos <c>BattleGround::CheckSpellCast</c>, asked by Spell::CheckCast after the range check for a participant's cast that no aura
    /// triggered (Spell.cpp:5705-5716): null lets the cast go on, a value is the SpellCastResult that refuses it. Nothing by default.
    /// </summary>
    public virtual byte? CheckSpellCast(ObjectGuid caster, uint spellId) => null;

    /// <summary>The world states a player entering the battleground starts with (vmangos <c>FillInitialWorldStates</c>); a value may be -1.</summary>
    public virtual IReadOnlyList<(uint Id, int Value)> InitialWorldStates() => [];

    /// <summary>
    /// The area trigger a participant entered (vmangos <c>HandleAreaTrigger</c>); true when the battleground handled it. A trigger by a
    /// guid that is not a participant is ignored.
    /// </summary>
    public virtual bool HandleAreaTrigger(ObjectGuid player, uint areaTriggerId) => false;

    /// <summary>The WorldSafeLocs id of the graveyard a dead player of the team repops at (vmangos <c>GetClosestGraveYard</c>), 0 for none.</summary>
    public virtual uint ClosestGraveyard(Team team) => 0;

    /// <summary>vmangos <c>BattleGround::ModifyStartDelayTime</c> is internal; this exposes the participant list for the daemon's position packet.</summary>
    public IReadOnlyList<(ObjectGuid Guid, Team Team)> Participants() => [.. _players.Select(kv => (kv.Key, kv.Value.Team))];
}
