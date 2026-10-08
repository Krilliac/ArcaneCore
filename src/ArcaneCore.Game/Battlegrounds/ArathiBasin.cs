using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Battlegrounds;

/// <summary>
/// Arathi Basin (vmangos BattleGroundAB.cpp/.h, client 1.7+): five nodes (stables, blacksmith, farm, lumber mill, gold mine) taken by clicking
/// their banner, held for a minute while contested, then occupied; occupied nodes tick resources (more nodes, faster ticks) and the first team
/// to 2000 wins. Honor and reputation follow the resources gathered. The node banners and their guards are the events
/// <c>(node, status)</c> of the <c>gameobject_battleground</c>/<c>creature_battleground</c> rows; the five buffs are objects of the match
/// (<see cref="IBattlegroundHost.AddBattlegroundObject"/>) whose type is re-rolled when one is taken.
/// <para>Not modelled (vmangos does not either): the peons of an occupied node ("TODO: working, scripted peons spawning").</para>
/// </summary>
public sealed class ArathiBasin : Battleground
{
    /// <summary>Number of nodes (vmangos <c>BG_AB_NODES_MAX</c>).</summary>
    public const int NodeCount = 5;

    public const int NodeStables = 0;
    public const int NodeBlacksmith = 1;
    public const int NodeFarm = 2;
    public const int NodeLumberMill = 3;
    public const int NodeGoldMine = 4;

    // BG_AB_NodeStatus (BattleGroundAB.h:118-127)
    public const byte StatusNeutral = 0;
    public const byte StatusAllianceContested = 1;
    public const byte StatusHordeContested = 2;
    public const byte StatusAllianceOccupied = 3;
    public const byte StatusHordeOccupied = 4;

    private const byte TypeContested = 1;
    private const byte TypeOccupied = 3;

    /// <summary>A contested node becomes its claimer's after a minute (vmangos <c>BG_AB_FLAG_CAPTURING_TIME</c>).</summary>
    public const uint FlagCapturingTimeMs = 60_000;

    /// <summary>"Near victory" is announced once a team passes this (vmangos <c>BG_AB_WARNING_NEAR_VICTORY_SCORE</c>).</summary>
    public const int WarningNearVictoryScore = 1800;

    /// <summary>Resources that win the match (vmangos <c>BG_AB_MAX_TEAM_SCORE</c>).</summary>
    public const int MaxTeamScore = 2000;

    // BG_AB_WorldStates (BattleGroundAB.h:31-38)
    public const uint WorldStateOccupiedBasesHorde = 1778;
    public const uint WorldStateOccupiedBasesAlliance = 1779;
    public const uint WorldStateResourcesAlliance = 1776;
    public const uint WorldStateResourcesHorde = 1777;
    public const uint WorldStateResourcesMax = 1780;
    public const uint WorldStateResourcesWarning = 1955;

    // BG_AB_Sounds (BattleGroundAB.h:129-138)
    public const uint SoundNodeClaimed = 8192;
    public const uint SoundNodeCapturedAlliance = 8173;
    public const uint SoundNodeCapturedHorde = 8213;
    public const uint SoundNodeAssaultedAlliance = 8212;
    public const uint SoundNodeAssaultedHorde = 8174;
    public const uint SoundNearVictoryAlliance = 8457;
    public const uint SoundNearVictoryHorde = 8456;

    /// <summary>The quest spells of holding four and five bases (vmangos <c>SPELL_AB_QUEST_REWARD_4_BASES</c>/<c>_5_BASES</c>, BattleGroundDefines.h:97-98).</summary>
    public const uint SpellQuestReward4Bases = 24061;
    public const uint SpellQuestReward5Bases = 24064;

    /// <summary>The Alliance (509, League of Arathor) and Horde (510, The Defilers) factions of the per-tick reputation (BattleGroundAB.cpp:111).</summary>
    public const uint FactionLeagueOfArathor = 509;
    public const uint FactionDefilers = 510;

    // Client patch 1.10 and later (BattleGroundAB.h:149-157; the honor interval did not change).
    public const uint NormalHonorInterval = 330;
    public const uint WeekendHonorInterval = 200;
    public const uint NormalReputationInterval = 200;
    public const uint WeekendReputationInterval = 150;

    /// <summary>Areatrigger of the Alliance and Horde exits (BattleGroundAB.cpp:184-197).</summary>
    public const uint AreaTriggerAllianceExit = 3948;
    public const uint AreaTriggerHordeExit = 3949;

    /// <summary>The entrance graveyards before the start (BattleGroundAB.cpp:524-528).</summary>
    public const uint GraveyardAllianceEntrance = 890;
    public const uint GraveyardHordeEntrance = 889;

    private static readonly uint[] s_nodeStates = [1767, 1782, 1772, 1792, 1787];
    private static readonly uint[] s_nodeIcons = [1842, 1846, 1845, 1844, 1843];
    private static readonly byte[] s_plus = [0, 2, 3, 0, 1];
    private static readonly uint[] s_nodeCredits = [15001, 15002, 15003, 15004, 15005];
    private static readonly uint[] s_nodeNames = [BattlegroundTexts.LangAbNodeStables, BattlegroundTexts.LangAbNodeBlacksmith, BattlegroundTexts.LangAbNodeFarm, BattlegroundTexts.LangAbNodeLumberMill, BattlegroundTexts.LangAbNodeGoldMine];
    private static readonly uint[] s_tickIntervals = [0, 12000, 9000, 6000, 3000, 1000];
    private static readonly int[] s_tickPoints = [0, 10, 10, 10, 10, 30];

    // MAX_BATTLEGROUND_BRACKETS (6) entries with five initializers: the sixth is zero (BattleGroundAB.h:163-164).
    private static readonly uint[] s_perTickHonor = [41, 68, 113, 189, 198, 0];
    private static readonly uint[] s_winMatchHonor = [41, 68, 113, 189, 198, 0];

    /// <summary>WorldSafeLocs of the five nodes, then the Alliance and Horde start (vmangos <c>BG_AB_GraveyardIds</c>, BattleGroundAB.h:167).</summary>
    private static readonly uint[] s_graveyardIds = [895, 894, 893, 897, 896, 898, 899];

    /// <summary>The buff position of each node (vmangos <c>BG_AB_BuffPositions</c>, BattleGroundAB.h:171-177, retail 5.4.8 positions).</summary>
    private static readonly (float X, float Y, float Z, float O)[] s_buffPositions =
    [
        (1185.566f, 1184.629f, -56.36329f, 2.303831f),
        (989.939026f, 1008.75f, -42.60327f, 0.8203033f),
        (818.0089f, 842.3543f, -56.54062f, 3.176533f),
        (808.8463f, 1185.417f, 11.92161f, 5.619962f),
        (1147.091f, 816.8362f, -98.39896f, 6.056293f),
    ];

    /// <summary>The first match-object index of the buffs (vmangos <c>BG_AB_OBJECT_SPEEDBUFF_STABLES</c>); three per node.</summary>
    public const int FirstBuffObjectIndex = 1;

    private readonly byte[] _nodes = new byte[NodeCount];
    private readonly byte[] _prevNodes = new byte[NodeCount];
    private readonly uint[] _nodeTimers = new uint[NodeCount];
    private readonly uint[] _lastTick = new uint[2];
    private readonly uint[] _honorScoreTicks = new uint[2];
    private readonly uint[] _reputationScoreTicks = new uint[2];
    private readonly int[] _teamScores = new int[2];
    private readonly uint _honorTicks;
    private readonly uint _reputationTicks;
    private bool _informedNearVictory;

    public ArathiBasin(BattlegroundTemplate template, int bracket, uint instanceId, uint clientInstanceId, BattlegroundOptions options, BattlegroundPorts ports)
        : base(template, bracket, instanceId, clientInstanceId, options, ports)
    {
        if (template.Type != BattlegroundType.ArathiBasin)
        {
            throw new ArgumentException("not an Arathi Basin template", nameof(template));
        }

        StartMessageIds[1] = BattlegroundTexts.AbStartOneMinute;
        StartMessageIds[2] = BattlegroundTexts.AbStartHalfMinute;
        StartMessageIds[3] = BattlegroundTexts.AbHasBegun;

        // Reset() (BattleGroundAB.cpp:476-505): every node neutral, the ghost gates spawned.
        bool weekend = Ports.Calendar.IsBattlegroundWeekend(Type);
        _honorTicks = weekend ? WeekendHonorInterval : NormalHonorInterval;
        _reputationTicks = weekend ? WeekendReputationInterval : NormalReputationInterval;
        for (byte node = 0; node < NodeCount; node++)
        {
            SetActiveEvent(node, StatusNeutral);
        }

        SetActiveEvent(BattlegroundConstants.EventGhostGate, 0);
    }

    // ------------------------------------------------------------------ queries

    /// <summary>The status of a node: 0 neutral, 1/2 contested by the Alliance/Horde, 3/4 occupied by the Alliance/Horde.</summary>
    public byte NodeStatus(int node) => _nodes[node];

    /// <summary>Milliseconds until a contested node is occupied, 0 when no capture runs.</summary>
    public uint NodeTimer(int node) => _nodeTimers[node];

    /// <summary>The resources of a team (vmangos <c>m_teamScores</c>).</summary>
    public int TeamScore(Team team) => _teamScores[BattlegroundConstants.TeamIndex(team)];

    /// <summary>The buff type entry of a match-object index (<see cref="BattlegroundConstants.BuffEntries"/>, three per node).</summary>
    public static uint BuffObjectEntry(int objectIndex) => BattlegroundConstants.BuffEntries[(objectIndex - FirstBuffObjectIndex) % 3];

    /// <summary>The honor of one resource tick reward for a bracket (vmangos <c>BG_AB_PerTickHonor</c>).</summary>
    public static uint PerTickHonor(int bracket) => s_perTickHonor[bracket];

    /// <summary>The win honor for a bracket (vmangos <c>BG_AB_WinMatchHonor</c>).</summary>
    public static uint WinMatchHonor(int bracket) => s_winMatchHonor[bracket];

    /// <summary>The WorldSafeLocs id of a node's graveyard (0-4) or of the Alliance (5) or Horde (6) start.</summary>
    public static uint GraveyardId(int index) => s_graveyardIds[index];

    // ------------------------------------------------------------------ base overrides

    protected override BattlegroundScore CreateScore() => new AbScore();

    protected override uint WinnerText(Team winner) => winner == Team.Horde ? BattlegroundTexts.AbHordeWins : BattlegroundTexts.AbAllianceWins;

    /// <inheritdoc />
    public override bool BuffChange => true;

    /// <summary>vmangos <c>SetupBattleGround</c> (BattleGroundAB.cpp:462-473): the three buff objects of every node.</summary>
    protected override bool SetupBattleground()
    {
        for (int node = 0; node < NodeCount; node++)
        {
            (float x, float y, float z, float o) = s_buffPositions[node];
            for (int buff = 0; buff < 3; buff++)
            {
                Host.AddBattlegroundObject(FirstBuffObjectIndex + (3 * node) + buff, BattlegroundConstants.BuffEntries[buff], x, y, z, o);
            }
        }

        return true;
    }

    /// <summary>vmangos <c>StartingEventCloseDoors</c>: despawn every buff.</summary>
    protected override void StartingEventCloseDoors()
    {
        for (int i = 0; i < NodeCount * 3; i++)
        {
            Host.SpawnBattlegroundObject(FirstBuffObjectIndex + i, BattlegroundConstants.RespawnNeverSeconds);
        }
    }

    /// <summary>vmangos <c>StartingEventOpenDoors</c> (BattleGroundAB.cpp:162-172): one random buff per node, the doors, the ghost gates away.</summary>
    protected override void StartingEventOpenDoors()
    {
        for (int node = 0; node < NodeCount; node++)
        {
            int buff = Ports.Random.Next(0, 3);
            Host.SpawnBattlegroundObject(FirstBuffObjectIndex + buff + (node * 3), 0);
        }

        if (IsActiveEvent(BattlegroundConstants.EventDoor, 0))
        {
            Host.OpenDoors();
        }

        SpawnEvent(BattlegroundConstants.EventGhostGate, 0, spawn: false, forcedDespawn: true);
    }

    // ------------------------------------------------------------------ update

    /// <inheritdoc />
    public override bool Update(uint diffMs)
    {
        if (Status == BattlegroundStatus.InProgress)
        {
            UpdateNodes(diffMs);
        }

        // Last, it can report the battleground for deletion (BattleGroundAB.cpp:146-147).
        return base.Update(diffMs);
    }

    private void UpdateNodes(uint diffMs)
    {
        int[] teamPoints = new int[2];
        for (int node = 0; node < NodeCount; node++)
        {
            // A minute to occupy a node from contested.
            if (_nodeTimers[node] != 0)
            {
                if (_nodeTimers[node] > diffMs)
                {
                    _nodeTimers[node] -= diffMs;
                }
                else
                {
                    _nodeTimers[node] = 0;
                    int teamIndex = _nodes[node] - 1;
                    _prevNodes[node] = _nodes[node];
                    _nodes[node] += 2;
                    CreateBanner(node, TypeOccupied, teamIndex);
                    SendNodeUpdate(node);
                    NodeOccupied(node, teamIndex == 0 ? Team.Alliance : Team.Horde);
                    if (teamIndex == 0)
                    {
                        Host.AnnounceFormatted(BattlegroundTexts.LangAbNodeTaken, BattlegroundChatKind.Alliance, ObjectGuid.Empty, BattlegroundTexts.LangBgAlliance, s_nodeNames[node]);
                        Host.PlaySoundToAll(SoundNodeCapturedAlliance);
                    }
                    else
                    {
                        Host.AnnounceFormatted(BattlegroundTexts.LangAbNodeTaken, BattlegroundChatKind.Horde, ObjectGuid.Empty, BattlegroundTexts.LangBgHorde, s_nodeNames[node]);
                        Host.PlaySoundToAll(SoundNodeCapturedHorde);
                    }
                }
            }

            for (int team = 0; team < 2; team++)
            {
                if (_nodes[node] == team + TypeOccupied)
                {
                    teamPoints[team]++;
                }
            }
        }

        // Accumulate points.
        for (int team = 0; team < 2; team++)
        {
            int points = teamPoints[team];
            if (points == 0)
            {
                continue;
            }

            _lastTick[team] += diffMs;
            if (_lastTick[team] <= s_tickIntervals[points])
            {
                continue;
            }

            Team side = team == 0 ? Team.Alliance : Team.Horde;
            _lastTick[team] -= s_tickIntervals[points];
            _teamScores[team] += s_tickPoints[points];
            _honorScoreTicks[team] += (uint)s_tickPoints[points];
            _reputationScoreTicks[team] += (uint)s_tickPoints[points];
            if (_reputationScoreTicks[team] >= _reputationTicks)
            {
                RewardReputationToTeam(team == 0 ? FactionLeagueOfArathor : FactionDefilers, 10, side);
                _reputationScoreTicks[team] -= _reputationTicks;
            }

            if (_honorScoreTicks[team] >= _honorTicks)
            {
                RewardHonorToTeam(PerTickHonor(Bracket), side);
                _honorScoreTicks[team] -= _honorTicks;
            }

            if (!_informedNearVictory && _teamScores[team] > WarningNearVictoryScore)
            {
                Host.Announce(team == 0 ? BattlegroundTexts.AbAllianceNearVictory : BattlegroundTexts.AbHordeNearVictory, BattlegroundChatKind.Neutral, ObjectGuid.Empty);
                Host.PlaySoundToAll(team == 0 ? SoundNearVictoryAlliance : SoundNearVictoryHorde);
                _informedNearVictory = true;
            }

            if (_teamScores[team] > MaxTeamScore)
            {
                _teamScores[team] = MaxTeamScore;
            }

            Host.UpdateWorldState(team == 0 ? WorldStateResourcesAlliance : WorldStateResourcesHorde, (uint)_teamScores[team]);
        }

        // The win condition; vmangos tests both teams in turn (BattleGroundAB.cpp:140-143).
        if (_teamScores[0] >= MaxTeamScore)
        {
            EndBattleground(Team.Alliance);
        }

        if (_teamScores[1] >= MaxTeamScore && Status == BattlegroundStatus.InProgress)
        {
            EndBattleground(Team.Horde);
        }
    }

    // ------------------------------------------------------------------ nodes

    /// <summary>vmangos <c>_CreateBanner</c> (BattleGroundAB.cpp:205-224): spawn the node's banner event, contested after 1 s, occupied after 5 s.</summary>
    private void CreateBanner(int node, byte type, int teamIndex)
    {
        uint delay = type switch
        {
            TypeContested => 1,
            TypeOccupied => 5,
            _ => 0,
        };

        byte status = type == StatusNeutral ? type : (byte)(type + teamIndex);
        SpawnEvent((byte)node, status, spawn: true, forcedDespawn: true, delay);
    }

    /// <summary>vmangos <c>_SendNodeUpdate</c> (BattleGroundAB.cpp:283-310): the node's map icon and both occupied-base counters.</summary>
    private void SendNodeUpdate(int node)
    {
        if (_prevNodes[node] != 0)
        {
            Host.UpdateWorldState(s_nodeStates[node] + s_plus[_prevNodes[node]], 0);
        }
        else
        {
            Host.UpdateWorldState(s_nodeIcons[node], 0);
        }

        Host.UpdateWorldState(s_nodeStates[node] + s_plus[_nodes[node]], 1);
        (uint ally, uint horde) = OccupiedCounts();
        Host.UpdateWorldState(WorldStateOccupiedBasesAlliance, ally);
        Host.UpdateWorldState(WorldStateOccupiedBasesHorde, horde);
    }

    private (uint Alliance, uint Horde) OccupiedCounts()
    {
        uint ally = 0;
        uint horde = 0;
        foreach (byte status in _nodes)
        {
            if (status == StatusAllianceOccupied)
            {
                ally++;
            }
            else if (status == StatusHordeOccupied)
            {
                horde++;
            }
        }

        return (ally, horde);
    }

    /// <summary>vmangos <c>_NodeOccupied</c> (BattleGroundAB.cpp:312-335): four and five held bases give the team the quest spells.</summary>
    private void NodeOccupied(int node, Team team)
    {
        int teamIndex = BattlegroundConstants.TeamIndex(team);
        int captured = 0;
        for (int i = 0; i < NodeCount; i++)
        {
            if (_nodes[i] == teamIndex + TypeOccupied && _nodeTimers[i] == 0)
            {
                captured++;
            }
        }

        if (captured >= 5)
        {
            CastSpellOnTeam(SpellQuestReward5Bases, team);
        }

        if (captured >= 4)
        {
            CastSpellOnTeam(SpellQuestReward4Bases, team);
        }
    }

    /// <summary>
    /// A participant used a node banner (vmangos <c>EventPlayerClickedOnFlag</c>, BattleGroundAB.cpp:337-458): a neutral node is claimed, an enemy
    /// node is assaulted, an own node that the enemy contests is defended. The banner's first event is its node.
    /// </summary>
    public override void EventPlayerClickedOnFlag(ObjectGuid player, BattlegroundObjectUse target)
    {
        if (Status != BattlegroundStatus.InProgress || PlayerTeam(player) is not { } team || target.Event1 >= NodeCount)
        {
            return;
        }

        int node = target.Event1;
        int teamIndex = BattlegroundConstants.TeamIndex(team);

        // Check that the player really could use this banner, not cheated.
        if (!(_nodes[node] == 0 || teamIndex == _nodes[node] % 2))
        {
            return;
        }

        Host.KilledMonsterCredit(player, s_nodeCredits[node]);
        BattlegroundChatKind kind = teamIndex == 0 ? BattlegroundChatKind.Alliance : BattlegroundChatKind.Horde;
        uint sound;

        if (_nodes[node] == StatusNeutral)
        {
            // Neutral: claimed.
            UpdatePlayerScore(player, BattlegroundScoreType.BasesAssaulted, 1);
            _prevNodes[node] = _nodes[node];
            _nodes[node] = (byte)(teamIndex + 1);
            CreateBanner(node, TypeContested, teamIndex);
            SendNodeUpdate(node);
            _nodeTimers[node] = FlagCapturingTimeMs;
            Host.AnnounceFormatted(BattlegroundTexts.LangAbNodeClaimed, kind, player, s_nodeNames[node], teamIndex == 0 ? BattlegroundTexts.LangBgAlliance : BattlegroundTexts.LangBgHorde);
            sound = SoundNodeClaimed;
        }
        else if (_nodes[node] is StatusAllianceContested or StatusHordeContested)
        {
            if (_prevNodes[node] < TypeOccupied)
            {
                // Contested from neutral: now contested by this team.
                UpdatePlayerScore(player, BattlegroundScoreType.BasesAssaulted, 1);
                _prevNodes[node] = _nodes[node];
                _nodes[node] = (byte)(teamIndex + TypeContested);
                CreateBanner(node, TypeContested, teamIndex);
                SendNodeUpdate(node);
                _nodeTimers[node] = FlagCapturingTimeMs;
                Host.AnnounceFormatted(BattlegroundTexts.LangAbNodeAssaulted, kind, player, s_nodeNames[node], 0);
            }
            else
            {
                // Contested from this team's occupation: defended, occupied again at once.
                UpdatePlayerScore(player, BattlegroundScoreType.BasesDefended, 1);
                _prevNodes[node] = _nodes[node];
                _nodes[node] = (byte)(teamIndex + TypeOccupied);
                CreateBanner(node, TypeOccupied, teamIndex);
                SendNodeUpdate(node);
                _nodeTimers[node] = 0;
                NodeOccupied(node, team);
                Host.AnnounceFormatted(BattlegroundTexts.LangAbNodeDefended, kind, player, s_nodeNames[node], 0);
            }

            sound = teamIndex == 0 ? SoundNodeAssaultedAlliance : SoundNodeAssaultedHorde;
        }
        else
        {
            // Occupied by the enemy: assaulted.
            UpdatePlayerScore(player, BattlegroundScoreType.BasesAssaulted, 1);
            _prevNodes[node] = _nodes[node];
            _nodes[node] = (byte)(teamIndex + TypeContested);
            CreateBanner(node, TypeContested, teamIndex);
            SendNodeUpdate(node);
            _nodeTimers[node] = FlagCapturingTimeMs;
            Host.AnnounceFormatted(BattlegroundTexts.LangAbNodeAssaulted, kind, player, s_nodeNames[node], 0);
            sound = teamIndex == 0 ? SoundNodeAssaultedAlliance : SoundNodeAssaultedHorde;
        }

        // Occupied again: "The X has taken the Y".
        if (_nodes[node] >= TypeOccupied)
        {
            Host.AnnounceFormatted(BattlegroundTexts.LangAbNodeTaken, kind, ObjectGuid.Empty, teamIndex == 0 ? BattlegroundTexts.LangBgAlliance : BattlegroundTexts.LangBgHorde, s_nodeNames[node]);
        }

        Host.PlaySoundToAll(sound);
    }

    // ------------------------------------------------------------------ scoring and the end

    /// <inheritdoc />
    public override void UpdatePlayerScore(ObjectGuid player, BattlegroundScoreType type, uint value)
    {
        if (ScoreOf(player) is not AbScore score)
        {
            return;
        }

        switch (type)
        {
            case BattlegroundScoreType.BasesAssaulted:
                score.BasesAssaulted += value;
                break;
            case BattlegroundScoreType.BasesDefended:
                score.BasesDefended += value;
                break;
            default:
                base.UpdatePlayerScore(player, type, value);
                break;
        }
    }

    /// <summary>vmangos <c>EndBattleGround</c> (BattleGroundAB.cpp:507-523): the win honor, twice on a battleground weekend.</summary>
    public override void EndBattleground(Team? winner)
    {
        if (winner is { } w)
        {
            if (Ports.Calendar.IsBattlegroundWeekend(Type))
            {
                RewardHonorToTeam(WinMatchHonor(Bracket), w);
            }

            RewardHonorToTeam(WinMatchHonor(Bracket), w);
        }

        base.EndBattleground(winner);
    }

    // ------------------------------------------------------------------ triggers, graveyards, world states

    /// <inheritdoc />
    public override bool HandleAreaTrigger(ObjectGuid player, uint areaTriggerId)
    {
        if (PlayerTeam(player) is not { } team)
        {
            return false;
        }

        // BattleGroundAB.cpp:177-203; the node triggers (3866-3870, 4020, 4021) are deliberately unhandled.
        switch (areaTriggerId)
        {
            case AreaTriggerAllianceExit when team == Team.Alliance:
            case AreaTriggerHordeExit when team == Team.Horde:
                Host.LeaveBattleground(player);
                return true;
            default:
                return false;
        }
    }

    /// <inheritdoc />
    public override uint ClosestGraveyard(Team team) => team == Team.Alliance ? GraveyardAllianceEntrance : GraveyardHordeEntrance;

    /// <summary>
    /// vmangos <c>GetClosestGraveYard</c> (BattleGroundAB.cpp:525-569): the entrance before the start; then the nearest graveyard of a node the
    /// team occupies (squared 2D distance), else the team's start graveyard.
    /// </summary>
    public override uint ClosestGraveyard(Team team, float x, float y, Func<uint, (float X, float Y)?> safeLoc)
    {
        if (Status != BattlegroundStatus.InProgress)
        {
            return ClosestGraveyard(team);
        }

        int teamIndex = BattlegroundConstants.TeamIndex(team);
        uint best = 0;
        float minDist = 999999.0f;
        for (int node = 0; node < NodeCount; node++)
        {
            if (_nodes[node] != teamIndex + TypeOccupied || safeLoc(s_graveyardIds[node]) is not { } at)
            {
                continue;
            }

            float dist = ((at.X - x) * (at.X - x)) + ((at.Y - y) * (at.Y - y));
            if (minDist > dist)
            {
                minDist = dist;
                best = s_graveyardIds[node];
            }
        }

        return best != 0 ? best : s_graveyardIds[teamIndex + 5];
    }

    /// <inheritdoc />
    public override IReadOnlyList<(uint Id, int Value)> InitialWorldStates()
    {
        // BattleGroundAB.cpp:244-281.
        List<(uint Id, int Value)> states = [];
        for (int node = 0; node < NodeCount; node++)
        {
            states.Add((s_nodeIcons[node], _nodes[node] == 0 ? 1 : 0));
        }

        for (int node = 0; node < NodeCount; node++)
        {
            for (int i = 1; i < NodeCount; i++)
            {
                states.Add((s_nodeStates[node] + s_plus[i], _nodes[node] == i ? 1 : 0));
            }
        }

        (uint ally, uint horde) = OccupiedCounts();
        states.Add((WorldStateOccupiedBasesAlliance, (int)ally));
        states.Add((WorldStateOccupiedBasesHorde, (int)horde));
        states.Add((WorldStateResourcesMax, MaxTeamScore));
        states.Add((WorldStateResourcesWarning, WarningNearVictoryScore));
        states.Add((WorldStateResourcesAlliance, _teamScores[0]));
        states.Add((WorldStateResourcesHorde, _teamScores[1]));
        states.Add((0x745, 0x2));    // "37 1861 unk" (BattleGroundAB.cpp:280)
        return states;
    }
}
