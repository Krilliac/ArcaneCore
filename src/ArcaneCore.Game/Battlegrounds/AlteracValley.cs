using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Battlegrounds;

/// <summary>
/// Alterac Valley (vmangos BattleGroundAV.cpp/.h): seven graveyards and eight towers or bunkers are assaulted by clicking their banner, become
/// the assaulter's after five minutes (a graveyard changes hands, a tower is destroyed) unless the owner defends them; Snowfall starts neutral;
/// two mines change hands with their boss and feed one reinforcement per 45 s; every death, destroyed tower and dead captain costs
/// reinforcements; the match ends when a general dies (vmangos removed the reinforcement-zero win, BattleGroundAV.cpp:782-787). Reputation and
/// bonus honor follow each objective and the surviving towers, captain, graveyards and mines at the end.
/// <para>
/// The turn-ins, the armor-scrap upgrades of the defenders (the defender events follow the owner's scraps), the challenge counters, the
/// landmine layers and experts, the shredder owner check, the respawn stop of the commanders, the explosives experts and the defenders of an
/// assaulted node, and the captains' and Snivvle's yells are in <c>AlteracValley.Upgrades.cs</c> and below. Not ported: the assault
/// invocations of the scripts (escorted troops, beacons, war riders and world bosses; their counters and go flags are kept) and the
/// start-time supply and tamed events (unreachable in vmangos: they test the third start event flag while the match is running,
/// BattleGroundAV.cpp:845).
/// </para>
/// </summary>
public sealed partial class AlteracValley : Battleground
{
    /// <summary>Number of nodes (vmangos <c>BG_AV_NODES_MAX</c>).</summary>
    public const int NodeCount = 15;

    // BG_AV_Nodes (BattleGroundAV.h:156-173)
    public const int NodeFirstAidStation = 0;
    public const int NodeStormpikeGrave = 1;
    public const int NodeStoneheartGrave = 2;
    public const int NodeSnowfallGrave = 3;
    public const int NodeIcebloodGrave = 4;
    public const int NodeFrostwolfGrave = 5;
    public const int NodeFrostwolfHut = 6;
    public const int NodeDunBaldarSouth = 7;
    public const int NodeDunBaldarNorth = 8;
    public const int NodeIcewingBunker = 9;
    public const int NodeStoneheartBunker = 10;
    public const int NodeIcebloodTower = 11;
    public const int NodeTowerPoint = 12;
    public const int NodeFrostwolfEastTower = 13;
    public const int NodeFrostwolfWestTower = 14;

    private const int TowersMaxEventBase = 23;      // BG_AV_TOWERS_MAX
    private const int MaxStates = 2;                // BG_AV_MAX_STATES
    private const int MaxGraveTypes = 4;            // BG_AV_MAX_GRAVETYPES

    /// <summary>The neutral owner of Snowfall and of an unclaimed mine (vmangos <c>BG_AV_TEAM_NEUTRAL</c>).</summary>
    public const int TeamNeutral = 2;

    /// <summary>A banner state (vmangos <c>BG_AV_States</c>).</summary>
    public const int PointAssaulted = 0;
    public const int PointControlled = 1;

    // BattleGroundAV.h:28-81
    public const uint CaptureTimeMs = 300_000;
    public const uint SnowfallFirstCaptureMs = 300_000;
    public const int InitialPoints = 600;
    public const int NearLoseScore = 120;
    public const int ResourcesLostPerCaptain = 100;
    public const int ResourcesLostPerTower = 75;
    public const uint MineTickTimerMs = 45_000;
    public const uint MineReclaimTimerMs = 1_200_000;
    public const uint FactionStormpike = 730;
    public const uint FactionFrostwolf = 729;
    public const uint SpellBossKillQuest = 23658;
    public const uint SpellHordeCaptainBuff = 22751;
    public const uint SpellAllianceCaptainBuff = 23693;

    // BG_AV_Spells and BG_AV_Creatures, BG_AV_GameObjects (BattleGroundAV.h:414-435).
    public const uint SpellSummonShredderHorde = 21544;
    public const uint SpellSummonShredderAlliance = 21565;
    public const uint NpcShredderAlliance = 13416;
    public const uint NpcShredderHorde = 13378;
    public const uint NpcLandminesLayerAlliance = 13356;
    public const uint NpcLandminesLayerHorde = 13357;
    public const uint NpcLandminesExpertAlliance = 13598;
    public const uint NpcLandminesExpertHorde = 13597;
    public const uint GameObjectLandmineHorde = 179324;
    public const uint GameObjectLandmineAlliance = 179325;

    /// <summary>The yells the captains give with their buff and Snivvle's at 70 s (mangos_string ids, BattleGroundAV.cpp:810-842).</summary>
    public const uint TextSnivvle = 791;
    public const uint TextHordeCaptainBuff = 792;
    public const uint TextAllianceCaptainBuff = 793;

    /// <summary>SPELL_FAILED_SPELL_UNAVAILABLE (SpellCastResult 0x63).</summary>
    public const byte CastFailedSpellUnavailable = 0x63;

    // BG_AV_Events used by the rules (BattleGroundAV.h:203-276)
    public const byte EventMineBosses = 46;
    public const byte EventCaptainAlliance = 48;
    public const byte EventCaptainHorde = 49;
    public const byte EventMine = 50;
    public const byte EventCommanderAllianceMortimer = 52;
    public const byte EventCommanderAllianceDuffy = 53;
    public const byte EventCommanderAllianceKarlPhilips = 54;
    public const byte EventCommanderAllianceRandolph = 55;
    public const byte EventCommanderHordeDardosh = 56;
    public const byte EventCommanderHordeLouisPhilips = 57;
    public const byte EventCommanderHordeMulfort = 58;
    public const byte EventCommanderHordeMalgor = 59;
    public const byte EventHerald = 60;
    public const byte EventBossAlliance = 61;
    public const byte EventBossHorde = 62;
    public const byte EventCaptainDeadAlliance = 63;
    public const byte EventCaptainDeadHorde = 64;
    public const byte EventSnivvle = 65;
    public const byte EventExplosivesExpertAlliance = 66;
    public const byte EventExplosivesExpertHorde = 67;
    public const byte EventLieutenantAlliance = 68;
    public const byte EventLieutenantHorde = 69;
    public const byte EventLandminesHorde = 100;
    public const byte EventLandminesAlliance = 101;

    // BG_AV_Sounds (BattleGroundAV.h:127-137)
    public const uint SoundAllianceAssaults = 8212;
    public const uint SoundHordeAssaults = 8174;
    public const uint SoundAllianceGood = 8173;
    public const uint SoundHordeGood = 8213;
    public const uint SoundBothTowerDefend = 8192;

    // BG_AV_WorldStates (BattleGroundAV.h:300-307)
    public const uint WorldStateAllianceScore = 3127;
    public const uint WorldStateHordeScore = 3128;
    public const uint WorldStateShowHordeScore = 3133;
    public const uint WorldStateShowAllianceScore = 3134;
    public const uint WorldStateSnowfallNeutral = 1966;

    /// <summary>Areatriggers of the exits (BattleGroundAV.cpp:1015-1037) and the six that only answer "handled" (3326-3331).</summary>
    public const uint AreaTriggerAllianceExit = 2608;
    public const uint AreaTriggerHordeExit = 2606;

    // The bonus "kills" (BattleGroundAV.h:37-81).
    private const uint KillBoss = 6;
    private const uint KillCaptain = 3;
    private const uint KillCommander = 1;
    private const uint KillTower = 2;
    private const uint KillSurvivingTower = 3;
    private const uint KillSurvivingCaptain = 3;
    private const uint KillSurvivingGrave = 1;
    private const uint KillSurvivingMine = 1;

    /// <summary>WorldSafeLocs of the seven graveyards, then the Alliance and Horde start caves (vmangos <c>BG_AV_GraveyardIds</c>).</summary>
    private static readonly uint[] s_graveyardIds = [751, 689, 729, 169, 749, 690, 750, 611, 610];

    /// <summary>Per node: Alliance controlled, Alliance assaulted, Horde controlled, Horde assaulted (vmangos <c>BG_AV_NodeWorldStates</c>).</summary>
    private static readonly uint[][] s_nodeWorldStates =
    [
        [1326, 1325, 1328, 1327], [1335, 1333, 1336, 1334], [1304, 1302, 1303, 1301], [1343, 1341, 1344, 1342],
        [1348, 1346, 1349, 1347], [1339, 1337, 1340, 1338], [1331, 1329, 1332, 1330], [1375, 1361, 1378, 1370],
        [1374, 1362, 1379, 1371], [1376, 1363, 1380, 1372], [1377, 1364, 1381, 1373], [1390, 1368, 1395, 1385],
        [1389, 1367, 1394, 1384], [1388, 1366, 1393, 1383], [1387, 1365, 1392, 1382],
    ];

    /// <summary>Per mine: Alliance, Horde, neutral control (vmangos <c>BG_AV_MineWorldStates</c>; north first).</summary>
    private static readonly uint[][] s_mineWorldStates = [[1358, 1359, 1360], [1355, 1356, 1357]];

    private static readonly uint[] s_nodeNames =
    [
        BattlegroundTexts.LangAvNodeGraveStormAid, BattlegroundTexts.LangAvNodeGraveStormpike, BattlegroundTexts.LangAvNodeGraveStone,
        BattlegroundTexts.LangAvNodeGraveSnow, BattlegroundTexts.LangAvNodeGraveIce, BattlegroundTexts.LangAvNodeGraveFrost,
        BattlegroundTexts.LangAvNodeGraveFrostHut, BattlegroundTexts.LangAvNodeTowerDunSouth, BattlegroundTexts.LangAvNodeTowerDunNorth,
        BattlegroundTexts.LangAvNodeTowerIcewing, BattlegroundTexts.LangAvNodeTowerStone, BattlegroundTexts.LangAvNodeTowerIce,
        BattlegroundTexts.LangAvNodeTowerPoint, BattlegroundTexts.LangAvNodeTowerFrostEast, BattlegroundTexts.LangAvNodeTowerFrostWest,
    ];

    private sealed class NodeInfo
    {
        public int TotalOwner;
        public int Owner;
        public int PrevOwner;
        public int PrevOtherOwner;
        public int State;
        public int PrevState;
        public uint Timer;
        public bool Tower;
    }

    private readonly NodeInfo[] _nodes = [.. Enumerable.Range(0, NodeCount).Select(_ => new NodeInfo())];
    private readonly int[] _mineOwner = [TeamNeutral, TeamNeutral];
    private readonly int[] _minePrevOwner = [TeamNeutral, TeamNeutral];
    private readonly int[] _mineTimer = [(int)MineTickTimerMs, (int)MineTickTimerMs];
    private readonly uint[] _mineReclaimTimer = new uint[2];
    private readonly int[] _teamScores = [InitialPoints, InitialPoints];
    private readonly bool[] _informedNearLose = new bool[2];
    private readonly uint _repTowerDestruction;
    private readonly uint _repCommander;
    private readonly uint _repCaptain;
    private readonly uint _repBoss;
    private readonly uint _repOwnedGrave;
    private readonly uint _repOwnedMine;
    private readonly uint _repSurviveCaptain;
    private readonly uint _repSurviveTower;
    private uint _buffTimerAlliance;
    private uint _buffTimerHorde;
    private uint _snivvleTimer;
    private bool _snivvleDone;
    private readonly ObjectGuid[] _shredderOwners = new ObjectGuid[2];

    public AlteracValley(BattlegroundTemplate template, int bracket, uint instanceId, uint clientInstanceId, BattlegroundOptions options, BattlegroundPorts ports)
        : base(template, bracket, instanceId, clientInstanceId, options, ports)
    {
        if (template.Type != BattlegroundType.AlteracValley)
        {
            throw new ArgumentException("not an Alterac Valley template", nameof(template));
        }

        StartMessageIds[1] = BattlegroundTexts.AvStartOneMinute;
        StartMessageIds[2] = BattlegroundTexts.AvStartHalfMinute;
        StartMessageIds[3] = BattlegroundTexts.AvHasBegun;

        // Reset() (BattleGroundAV.cpp:1594-1668).
        bool weekend = Ports.Calendar.IsBattlegroundWeekend(Type);
        _repTowerDestruction = weekend ? 18u : 12u;
        _repCommander = weekend ? 18u : 12u;
        _repCaptain = weekend ? 185u : 125u;
        _repBoss = weekend ? 525u : 350u;
        _repOwnedGrave = weekend ? 18u : 12u;
        _repSurviveCaptain = weekend ? 175u : 125u;
        _repSurviveTower = weekend ? 18u : 12u;
        _repOwnedMine = weekend ? 36u : 24u;

        SetActiveEvent(EventCaptainDeadAlliance, BattlegroundConstants.EventNone);
        SetActiveEvent(EventCaptainDeadHorde, BattlegroundConstants.EventNone);
        for (int mine = 0; mine < 2; mine++)
        {
            SetActiveEvent((byte)(EventMineBosses + mine), TeamNeutral);
            SetActiveEvent((byte)(EventMine + mine), TeamNeutral);
        }

        foreach (byte ev in new[] { EventCaptainAlliance, EventCaptainHorde, EventHerald, EventSnivvle, EventBossAlliance, EventBossHorde, EventLandminesAlliance, EventLandminesHorde, EventExplosivesExpertAlliance, EventExplosivesExpertHorde, EventLieutenantAlliance, EventLieutenantHorde })
        {
            SetActiveEvent(ev, 0);
        }

        SetActiveEvent(BattlegroundConstants.EventGhostGate, 0);
        for (int node = NodeDunBaldarSouth; node <= NodeFrostwolfWestTower; node++)
        {
            SetActiveEvent((byte)(EventCommanderAllianceMortimer + node - NodeDunBaldarSouth), 0);    // the commanders are alive
        }

        for (int node = NodeFirstAidStation; node <= NodeStoneheartGrave; node++)
        {
            InitNode(node, 0, tower: false);
        }

        for (int node = NodeDunBaldarSouth; node <= NodeStoneheartBunker; node++)
        {
            InitNode(node, 0, tower: true);
        }

        for (int node = NodeIcebloodGrave; node <= NodeFrostwolfHut; node++)
        {
            InitNode(node, 1, tower: false);
        }

        for (int node = NodeIcebloodTower; node <= NodeFrostwolfWestTower; node++)
        {
            InitNode(node, 1, tower: true);
        }

        InitNode(NodeSnowfallGrave, TeamNeutral, tower: false);

        // initializeChallengeInvocationGoals (BattleGroundAV.cpp:98-157): the captain buffs come two to six minutes in.
        _buffTimerAlliance = 120_000 + ((uint)Ports.Random.Next(0, 5) * 60_000);
        _buffTimerHorde = 120_000 + ((uint)Ports.Random.Next(0, 5) * 60_000);
        InitializeChallengeGoals();
    }

    // ------------------------------------------------------------------ queries

    /// <summary>The reinforcements of a team (vmangos <c>m_teamScores</c>).</summary>
    public int TeamScore(Team team) => _teamScores[BattlegroundConstants.TeamIndex(team)];

    /// <summary>The current owner of a node: 0 Alliance, 1 Horde, 2 neutral.</summary>
    public int NodeOwner(int node) => _nodes[node].Owner;

    /// <summary>The owner a node had before the running assault (vmangos <c>totalOwner</c>).</summary>
    public int NodeTotalOwner(int node) => _nodes[node].TotalOwner;

    /// <summary>The state of a node: <see cref="PointAssaulted"/> or <see cref="PointControlled"/>.</summary>
    public int NodeState(int node) => _nodes[node].State;

    /// <summary>Milliseconds until an assaulted node is taken or destroyed.</summary>
    public uint NodeTimer(int node) => _nodes[node].Timer;

    /// <summary>Whether a node is a tower or bunker (destroyed when taken) rather than a graveyard.</summary>
    public bool IsTower(int node) => node < NodeCount && _nodes[node].Tower;

    /// <summary>The owner of a mine (0 north, 1 south): 0 Alliance, 1 Horde, 2 neutral.</summary>
    public int MineOwner(int mine) => _mineOwner[mine];

    /// <summary>The WorldSafeLocs id of a graveyard node (0-6) or of the Alliance (7) or Horde (8) start cave.</summary>
    public static uint GraveyardId(int index) => s_graveyardIds[index];

    private bool IsGrave(int node) => node < NodeCount && !_nodes[node].Tower;

    private static Team TeamOf(int index) => index == 0 ? Team.Alliance : Team.Horde;

    // ------------------------------------------------------------------ base overrides

    protected override BattlegroundScore CreateScore() => new AvScore();

    protected override uint WinnerText(Team winner) => winner == Team.Horde ? BattlegroundTexts.AvHordeWins : BattlegroundTexts.AvAllianceWins;

    /// <summary>Alterac Valley never finishes early for lack of players (BattleGround.cpp:318).</summary>
    protected override bool UsesPrematureFinish => false;

    /// <summary>vmangos <c>StartingEventOpenDoors</c> (BattleGroundAV.cpp:917-921).</summary>
    protected override void StartingEventOpenDoors()
    {
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
        // Snivvle yells 70 s after the match object exists, whatever its status (BattleGroundAV.cpp:810-815).
        _snivvleTimer += diffMs;
        if (_snivvleTimer >= 70_000 && !_snivvleDone)
        {
            Host.EventCreatureYell(EventSnivvle, TextSnivvle);
            _snivvleDone = true;
        }

        if (Status == BattlegroundStatus.InProgress)
        {
            UpdateRunning(diffMs);
        }

        return base.Update(diffMs);
    }

    private void UpdateRunning(uint diffMs)
    {
        // The captains buff their team every two to six minutes while alive (BattleGroundAV.cpp:819-842).
        if (_buffTimerHorde < diffMs)
        {
            if (!IsActiveEvent(EventCaptainDeadHorde, 0))
            {
                CastSpellOnTeam(SpellHordeCaptainBuff, Team.Horde);
                Host.EventCreatureYell(EventCaptainHorde, TextHordeCaptainBuff);
            }

            _buffTimerHorde = 120_000 + ((uint)Ports.Random.Next(0, 5) * 60_000);
        }
        else
        {
            _buffTimerHorde -= diffMs;
        }

        if (_buffTimerAlliance < diffMs)
        {
            if (!IsActiveEvent(EventCaptainDeadAlliance, 0))
            {
                CastSpellOnTeam(SpellAllianceCaptainBuff, Team.Alliance);
                Host.EventCreatureYell(EventCaptainAlliance, TextAllianceCaptainBuff);
            }

            _buffTimerAlliance = 120_000 + ((uint)Ports.Random.Next(0, 5) * 60_000);
        }
        else
        {
            _buffTimerAlliance -= diffMs;
        }

        // Mines: a reinforcement per tick to the owner, and the neutral team reclaims them after 20 minutes (BattleGroundAV.cpp:869-886).
        for (int mine = 0; mine < 2; mine++)
        {
            if (_mineOwner[mine] == TeamNeutral)
            {
                continue;
            }

            _mineTimer[mine] -= (int)diffMs;
            if (_mineTimer[mine] <= 0)
            {
                UpdateScore(_mineOwner[mine], 1);
                _mineTimer[mine] = (int)MineTickTimerMs;
            }

            if (_mineReclaimTimer[mine] > diffMs)
            {
                _mineReclaimTimer[mine] -= diffMs;
            }
            else
            {
                ChangeMineOwner(mine, TeamNeutral);
            }
        }

        // Assault timers: the graveyard changes hands, the tower is destroyed (BattleGroundAV.cpp:888-904).
        for (int node = 0; node < NodeCount; node++)
        {
            if (_nodes[node].State != PointAssaulted)
            {
                continue;
            }

            if (_nodes[node].Timer > diffMs)
            {
                _nodes[node].Timer -= diffMs;
            }
            else
            {
                if (node == NodeStoneheartBunker)
                {
                    SpawnEvent(25, 1, spawn: true, forcedDespawn: true);
                }

                EventPlayerDestroyedPoint(node);
                if (Status != BattlegroundStatus.InProgress)
                {
                    return;
                }
            }
        }
    }

    /// <summary>vmangos <c>UpdateScore</c> (BattleGroundAV.cpp:774-797): reinforcements change; at most down to 0, no win condition.</summary>
    private void UpdateScore(int teamIndex, int points)
    {
        _teamScores[teamIndex] += points;
        if (points < 0)
        {
            if (_teamScores[teamIndex] < 1)
            {
                _teamScores[teamIndex] = 0;
            }
            else if (!_informedNearLose[teamIndex] && _teamScores[teamIndex] < NearLoseScore)
            {
                _informedNearLose[teamIndex] = true;
            }
        }

        Host.UpdateWorldState(teamIndex == 1 ? WorldStateHordeScore : WorldStateAllianceScore, (uint)_teamScores[teamIndex]);
    }

    // ------------------------------------------------------------------ kills

    /// <summary>vmangos <c>HandleKillPlayer</c> (BattleGroundAV.cpp:47-55): every death but Spirit of Redemption costs a reinforcement.</summary>
    public override void HandleKillPlayer(ObjectGuid victim, ObjectGuid? killer)
    {
        if (Status != BattlegroundStatus.InProgress)
        {
            return;
        }

        base.HandleKillPlayer(victim, killer);
        if (!Ports.Spells.HasAura(victim, BattlegroundConstants.SpellSpiritOfRedemption) && PlayerTeam(victim) is { } team)
        {
            UpdateScore(BattlegroundConstants.TeamIndex(team), -1);
        }
    }

    /// <summary>
    /// vmangos <c>HandleKillUnit</c> (BattleGroundAV.cpp:277-431): the generals end the match, the captains cost reinforcements, the commanders and
    /// lieutenants give honor and reputation, a mine boss hands its mine to the killer's team. The creature is known by its event.
    /// </summary>
    public override void HandleKillUnit(uint creatureEntry, byte event1, ObjectGuid killer)
    {
        if (Status != BattlegroundStatus.InProgress)
        {
            return;
        }

        switch (creatureEntry)
        {
            case NpcLandminesLayerAlliance or NpcLandminesLayerHorde:
                // The landmines stop coming back (BattleGroundAV.cpp:283-290; the landmine object script reads the event).
                SetActiveEvent(creatureEntry == NpcLandminesLayerAlliance ? EventLandminesAlliance : EventLandminesHorde, 1);
                return;
            case NpcLandminesExpertAlliance or NpcLandminesExpertHorde:
                // Every landmine of the event goes for good (:291-301).
                Host.RemoveEventGameObjects(creatureEntry == NpcLandminesExpertAlliance ? EventLandminesAlliance : EventLandminesHorde, 0);
                return;
        }

        if (event1 == BattlegroundConstants.EventNone || PlayerTeam(killer) is not { } killerTeam)
        {
            return;
        }

        switch (event1)
        {
            case EventBossAlliance:
                CastSpellOnTeam(SpellBossKillQuest, Team.Horde);
                RewardReputationToTeam(FactionFrostwolf, (int)_repBoss, Team.Horde);
                RewardHonorToTeam((uint)(BonusHonorFromKill(KillBoss) * HonorModifier), Team.Horde);
                Host.HeraldYell(BattlegroundTexts.LangAvAllianceGeneralDead, 0, 0);
                EndBattleground(Team.Horde);
                break;
            case EventBossHorde:
                CastSpellOnTeam(SpellBossKillQuest, Team.Alliance);
                RewardReputationToTeam(FactionStormpike, (int)_repBoss, Team.Alliance);
                RewardHonorToTeam((uint)(BonusHonorFromKill(KillBoss) * HonorModifier), Team.Alliance);
                Host.HeraldYell(BattlegroundTexts.LangAvHordeGeneralDead, 0, 0);
                EndBattleground(Team.Alliance);
                break;
            case EventCaptainAlliance:
                if (IsActiveEvent(EventCaptainDeadAlliance, 0))
                {
                    return;
                }

                RewardReputationToTeam(FactionFrostwolf, (int)_repCaptain, Team.Horde);
                RewardHonorToTeam(BonusHonorFromKill(KillCaptain), Team.Horde);
                UpdateScore(0, -ResourcesLostPerCaptain);
                SpawnEvent(EventCaptainDeadAlliance, 0, spawn: true, forcedDespawn: true);
                break;
            case EventCaptainHorde:
                if (IsActiveEvent(EventCaptainDeadHorde, 0))
                {
                    return;
                }

                RewardReputationToTeam(FactionStormpike, (int)_repCaptain, Team.Alliance);
                RewardHonorToTeam(BonusHonorFromKill(KillCaptain), Team.Alliance);
                UpdateScore(1, -ResourcesLostPerCaptain);
                SpawnEvent(EventCaptainDeadHorde, 0, spawn: true, forcedDespawn: true);
                break;
            case >= EventCommanderAllianceMortimer and <= EventCommanderAllianceRandolph:
            case EventLieutenantAlliance:
                RewardReputationToTeam(FactionFrostwolf, (int)_repCommander, Team.Horde);
                RewardHonorToTeam(BonusHonorFromKill(KillCommander), Team.Horde);
                if (event1 != EventLieutenantAlliance)
                {
                    SetSpawnEventMode(event1, 0, BattlegroundSpawnMode.RespawnStop); // "despawn mobs" (:336-366)
                }

                if (event1 == EventCommanderAllianceKarlPhilips)
                {
                    Host.CompleteQuestForAll(7281);
                }

                break;
            case >= EventCommanderHordeDardosh and <= EventCommanderHordeMalgor:
            case EventLieutenantHorde:
                RewardReputationToTeam(FactionStormpike, (int)_repCommander, Team.Alliance);
                RewardHonorToTeam(BonusHonorFromKill(KillCommander), Team.Alliance);
                if (event1 != EventLieutenantHorde)
                {
                    SetSpawnEventMode(event1, 0, BattlegroundSpawnMode.RespawnStop); // (:367-390)
                }

                if (event1 == EventCommanderHordeLouisPhilips)
                {
                    Host.CompleteQuestForAll(7282);
                }

                break;
            case EventMineBosses:
            case EventMineBosses + 1:
                ChangeMineOwner(event1 - EventMineBosses, BattlegroundConstants.TeamIndex(killerTeam));
                Host.CompleteQuestForAll(killerTeam == Team.Alliance ? 7122u : 7124u);
                break;
            case EventExplosivesExpertAlliance:
                Host.CompleteQuestForAll(7367);
                SetSpawnEventMode(EventExplosivesExpertAlliance, 0, BattlegroundSpawnMode.RespawnStop);
                break;
            case EventExplosivesExpertHorde:
                Host.CompleteQuestForAll(7368);
                SetSpawnEventMode(EventExplosivesExpertHorde, 0, BattlegroundSpawnMode.RespawnStop);
                break;
        }
    }

    // ------------------------------------------------------------------ mines

    /// <summary>vmangos <c>ChangeMineOwner</c> (BattleGroundAV.cpp:1122-1153).</summary>
    private void ChangeMineOwner(int mine, int teamIndex)
    {
        _mineTimer[mine] = (int)MineTickTimerMs;
        if (_mineOwner[mine] == teamIndex)
        {
            return;
        }

        _minePrevOwner[mine] = _mineOwner[mine];
        _mineOwner[mine] = teamIndex;
        SendMineWorldStates(mine);
        SpawnEvent((byte)(EventMine + mine), (byte)teamIndex, spawn: true, forcedDespawn: false);
        SpawnEvent((byte)(EventMineBosses + mine), (byte)teamIndex, spawn: true, forcedDespawn: false);
        if (teamIndex != TeamNeutral)
        {
            Host.PlaySoundToAll(teamIndex == 0 ? SoundAllianceGood : SoundHordeGood);
            _mineReclaimTimer[mine] = MineReclaimTimerMs;
            Host.HeraldYell(BattlegroundTexts.LangAvMineTaken, teamIndex == 0 ? BattlegroundTexts.LangBgAlliance : BattlegroundTexts.LangBgHorde, mine == 0 ? BattlegroundTexts.LangAvMineNorth : BattlegroundTexts.LangAvMineSouth);
        }

        if (teamIndex == 0)
        {
            Host.CompleteQuestForAll(7122);
        }
        else if (teamIndex == 1)
        {
            Host.CompleteQuestForAll(7124);
        }
    }

    private void SendMineWorldStates(int mine)
    {
        Host.UpdateWorldState(s_mineWorldStates[mine][_mineOwner[mine]], 1);
        if (_mineOwner[mine] != _minePrevOwner[mine])
        {
            Host.UpdateWorldState(s_mineWorldStates[mine][_minePrevOwner[mine]], 0);
        }
    }

    // ------------------------------------------------------------------ nodes

    private void InitNode(int node, int teamIndex, bool tower)
    {
        NodeInfo n = _nodes[node];
        n.TotalOwner = teamIndex;
        n.Owner = teamIndex;
        n.PrevOwner = teamIndex;
        n.PrevOtherOwner = teamIndex;
        n.State = PointControlled;
        n.PrevState = PointControlled;
        n.Timer = 0;
        n.Tower = tower;
        SetActiveEvent((byte)node, (byte)((teamIndex * MaxStates) + n.State));
        if (!tower)
        {
            SetActiveEvent((byte)(node + NodeCount), (byte)(teamIndex * MaxGraveTypes));
        }
        else
        {
            SetActiveEvent((byte)(node + NodeCount), (byte)((teamIndex * 2) + 1));
            SetActiveEvent((byte)(node + TowersMaxEventBase), (byte)(teamIndex * MaxGraveTypes));
        }
    }

    /// <summary>
    /// A participant used a banner (vmangos <c>EventPlayerClickedOnFlag</c>, BattleGroundAV.cpp:1288-1309): a controlled banner is assaulted, an
    /// assaulted one is defended. The banner's events are (node, owner * 2 + state).
    /// </summary>
    public override void EventPlayerClickedOnFlag(ObjectGuid player, BattlegroundObjectUse target)
    {
        if (Status != BattlegroundStatus.InProgress || target.Event1 >= NodeCount || PlayerTeam(player) is null)
        {
            return;
        }

        switch (target.Event2 % MaxStates)
        {
            case PointControlled:
                EventPlayerAssaultsPoint(player, target.Event1);
                break;
            case PointAssaulted:
                EventPlayerDefendsPoint(player, target.Event1);
                break;
        }
    }

    private void EventPlayerDefendsPoint(ObjectGuid player, int node)
    {
        int teamIndex = BattlegroundConstants.TeamIndex(PlayerTeam(player)!.Value);
        NodeInfo n = _nodes[node];
        if (n.Owner == teamIndex || n.State != PointAssaulted)
        {
            return;
        }

        if (n.TotalOwner == TeamNeutral)
        {
            // The initial Snowfall capture is an assault (BattleGroundAV.cpp:1319-1326).
            EventPlayerAssaultsPoint(player, node);
            return;
        }

        if (n.PrevOwner != teamIndex)
        {
            return;     // vmangos logs "player defends point which doesn't belong to his team"
        }

        n.PrevOwner = n.Owner;
        n.PrevOtherOwner = n.Owner;
        n.Owner = teamIndex;
        n.PrevState = n.State;
        n.State = PointControlled;
        n.Timer = 0;
        PopulateNode(node);
        UpdateNodeWorldState(node);

        uint teamName = teamIndex == 0 ? BattlegroundTexts.LangBgAlliance : BattlegroundTexts.LangBgHorde;
        if (IsTower(node))
        {
            Host.HeraldYell(BattlegroundTexts.LangAvTowerDefended, s_nodeNames[node], teamName);
            UpdatePlayerScore(player, BattlegroundScoreType.TowersDefended, 1);
            Host.PlaySoundToAll(SoundBothTowerDefend);
        }
        else
        {
            Host.HeraldYell(BattlegroundTexts.LangAvGraveDefended, s_nodeNames[node], teamName);
            UpdatePlayerScore(player, BattlegroundScoreType.GraveyardsDefended, 1);
            Host.PlaySoundToAll(teamIndex == 0 ? SoundAllianceGood : SoundHordeGood);
        }
    }

    private void EventPlayerAssaultsPoint(ObjectGuid player, int node)
    {
        int teamIndex = BattlegroundConstants.TeamIndex(PlayerTeam(player)!.Value);
        NodeInfo n = _nodes[node];
        if (n.Owner == teamIndex || teamIndex == n.TotalOwner)
        {
            return;
        }

        // vmangos AssaultNode (BattleGroundAV.cpp:1530-1544) asserts an assaulted node is only re-assaulted while it has no total owner.
        if (n.State == PointAssaulted && n.TotalOwner != TeamNeutral)
        {
            return;
        }

        n.Timer = n.PrevOwner != TeamNeutral ? CaptureTimeMs : SnowfallFirstCaptureMs;
        n.PrevOwner = n.Owner;
        n.PrevOtherOwner = n.Owner;
        n.Owner = teamIndex;
        n.PrevState = n.State;
        n.State = PointAssaulted;
        UpdateNodeWorldState(node);
        PopulateNode(node);

        uint teamName = teamIndex == 0 ? BattlegroundTexts.LangBgAlliance : BattlegroundTexts.LangBgHorde;
        if (IsTower(node))
        {
            Host.HeraldYell(BattlegroundTexts.LangAvTowerAssaulted, s_nodeNames[node], teamName);
            UpdatePlayerScore(player, BattlegroundScoreType.TowersAssaulted, 1);
        }
        else
        {
            Host.HeraldYell(BattlegroundTexts.LangAvGraveAssaulted, s_nodeNames[node], teamName);
            UpdatePlayerScore(player, BattlegroundScoreType.GraveyardsAssaulted, 1);
        }

        Host.PlaySoundToAll(teamIndex == 0 ? SoundAllianceAssaults : SoundHordeAssaults);
    }

    /// <summary>vmangos <c>EventPlayerDestroyedPoint</c> (BattleGroundAV.cpp:1082-1120).</summary>
    private void EventPlayerDestroyedPoint(int node)
    {
        NodeInfo n = _nodes[node];
        if (n.Owner == TeamNeutral)
        {
            return;     // vmangos asserts
        }

        int ownerIndex = n.Owner;
        Team owner = TeamOf(ownerIndex);

        // DestroyNode (BattleGroundAV.cpp:1546-1556).
        n.TotalOwner = n.Owner;
        n.PrevOwner = n.Owner;
        n.PrevState = n.State;
        n.State = PointControlled;
        n.Timer = 0;
        PopulateNode(node);
        UpdateNodeWorldState(node);

        uint teamName = owner == Team.Alliance ? BattlegroundTexts.LangBgAlliance : BattlegroundTexts.LangBgHorde;
        if (IsTower(node))
        {
            UpdateScore(1 - ownerIndex, -ResourcesLostPerTower);
            RewardReputationToTeam(owner == Team.Alliance ? FactionStormpike : FactionFrostwolf, (int)_repTowerDestruction, owner);
            RewardHonorToTeam(BonusHonorFromKill(KillTower), owner);
            Host.HeraldYell(BattlegroundTexts.LangAvTowerTaken, s_nodeNames[node], teamName);
            Host.CompleteQuestForAll(owner == Team.Alliance ? 7102u : 7101u);
        }
        else
        {
            Host.HeraldYell(BattlegroundTexts.LangAvGraveTaken, s_nodeNames[node], teamName);
            Host.CompleteQuestForAll(owner == Team.Alliance ? 7081u : 7082u);
        }
    }

    /// <summary>
    /// vmangos <c>PopulateNode</c> (BattleGroundAV.cpp:1209-1286): the banner event (node, owner * 2 + state), shown after 5 s when controlled and
    /// 1 s when assaulted; the defenders of a controlled graveyard (node + 15, owner * 4 + defender type) or tower (the base defenders
    /// node + 15, owner * 2 + 1, and the tower defenders node + 23, owner * 4 + defender type) are spawned, respawn at once when dead and come
    /// back two minutes after a death; those of an assaulted node, at the previous owner's defender type, stop coming back
    /// (SetSpawnEventMode RESPAWN_FORCED and RESPAWN_STOP). The defender type follows the owner's armor scraps (<see cref="DefenderType"/>).
    /// </summary>
    private void PopulateNode(int node)
    {
        NodeInfo n = _nodes[node];
        int owner = n.Owner;
        int previous = n.PrevOtherOwner;
        int typeNew = DefenderType(owner);
        int typeOld = DefenderType(previous);
        uint delay = 0;
        if (IsGrave(node))
        {
            if (n.State == PointControlled)
            {
                SetSpawnEventMode((byte)(NodeCount + node), (byte)((owner * MaxGraveTypes) + typeNew), BattlegroundSpawnMode.RespawnForced);
                SpawnEvent((byte)(NodeCount + node), (byte)((owner * MaxGraveTypes) + typeNew), spawn: true, forcedDespawn: true);
                delay = 5;
            }
            else
            {
                SetSpawnEventMode((byte)(NodeCount + node), (byte)((previous * MaxGraveTypes) + typeOld), BattlegroundSpawnMode.RespawnStop);
                delay = 1;
            }
        }

        if (IsTower(node) && owner != TeamNeutral)
        {
            if (n.State == PointControlled)
            {
                SetSpawnEventMode((byte)(NodeCount + node), (byte)((owner * MaxStates) + 1), BattlegroundSpawnMode.RespawnForced);
                SpawnEvent((byte)(NodeCount + node), (byte)((owner * MaxStates) + 1), spawn: true, forcedDespawn: true);
                SetSpawnEventMode((byte)(TowersMaxEventBase + node), (byte)((owner * MaxGraveTypes) + typeNew), BattlegroundSpawnMode.RespawnForced);
                SpawnEvent((byte)(TowersMaxEventBase + node), (byte)((owner * MaxGraveTypes) + typeNew), spawn: true, forcedDespawn: true);
                delay = 5;
            }
            else
            {
                SetSpawnEventMode((byte)(NodeCount + node), (byte)((previous * MaxStates) + 1), BattlegroundSpawnMode.RespawnStop);
                SetSpawnEventMode((byte)(TowersMaxEventBase + node), (byte)((previous * MaxGraveTypes) + typeOld), BattlegroundSpawnMode.RespawnStop);
                delay = 1;
            }
        }

        SpawnEvent((byte)node, (byte)((owner * MaxStates) + n.State), spawn: true, forcedDespawn: true, delay);
    }

    /// <summary>vmangos <c>UpdateNodeWorldState</c> (BattleGroundAV.cpp:1428-1435).</summary>
    private void UpdateNodeWorldState(int node)
    {
        NodeInfo n = _nodes[node];
        Host.UpdateWorldState(NodeWorldState(node, n.State, n.Owner), 1);
        if (n.PrevOwner == TeamNeutral)
        {
            Host.UpdateWorldState(WorldStateSnowfallNeutral, 0);
        }
        else
        {
            Host.UpdateWorldState(NodeWorldState(node, n.PrevState, n.PrevOwner), 0);
        }
    }

    /// <summary>The world state of a node for (state, owner) (vmangos <c>BG_AV_NodeWorldStates[node][GetWorldStateType(state, team)]</c>).</summary>
    public static uint NodeWorldState(int node, int state, int teamIndex) => s_nodeWorldStates[node][(teamIndex * MaxStates) + state];

    // ------------------------------------------------------------------ scoring and the end

    /// <inheritdoc />
    public override void UpdatePlayerScore(ObjectGuid player, BattlegroundScoreType type, uint value)
    {
        if (ScoreOf(player) is not AvScore score)
        {
            return;
        }

        switch (type)
        {
            case BattlegroundScoreType.GraveyardsAssaulted:
                score.GraveyardsAssaulted += value;
                break;
            case BattlegroundScoreType.GraveyardsDefended:
                score.GraveyardsDefended += value;
                break;
            case BattlegroundScoreType.TowersAssaulted:
                score.TowersAssaulted += value;
                break;
            case BattlegroundScoreType.TowersDefended:
                score.TowersDefended += value;
                break;
            case BattlegroundScoreType.SecondaryObjectives:
                score.SecondaryObjectives += value;
                break;
            default:
                base.UpdatePlayerScore(player, type, value);
                break;
        }
    }

    /// <summary>vmangos <c>EndBattleGround</c> (BattleGroundAV.cpp:931-1005): the surviving towers, owned graveyards and mines and a living captain pay out.</summary>
    public override void EndBattleground(Team? winner)
    {
        uint[] towersSurvived = new uint[2];
        uint[] gravesOwned = new uint[2];
        uint[] minesOwned = new uint[2];
        for (int node = NodeDunBaldarSouth; node <= NodeStoneheartBunker; node++)
        {
            if (_nodes[node].State == PointControlled && _nodes[node].TotalOwner == 0)
            {
                towersSurvived[0]++;
            }
        }

        for (int node = NodeIcebloodTower; node <= NodeFrostwolfWestTower; node++)
        {
            if (_nodes[node].State == PointControlled && _nodes[node].TotalOwner == 1)
            {
                towersSurvived[1]++;
            }
        }

        // The loop runs over every node, towers included (BattleGroundAV.cpp:951-954); only the two side graveyards are left out.
        for (int node = 0; node < NodeCount; node++)
        {
            if (_nodes[node].State == PointControlled && _nodes[node].Owner != TeamNeutral && node != NodeFrostwolfHut && node != NodeFirstAidStation)
            {
                gravesOwned[_nodes[node].Owner]++;
            }
        }

        for (int mine = 0; mine < 2; mine++)
        {
            if (_mineOwner[mine] != TeamNeutral)
            {
                minesOwned[_mineOwner[mine]]++;
            }
        }

        uint[] faction = [FactionStormpike, FactionFrostwolf];
        for (int i = 0; i < 2; i++)
        {
            Team team = TeamOf(i);
            if (towersSurvived[i] != 0)
            {
                RewardReputationToTeam(faction[i], (int)(towersSurvived[i] * _repSurviveTower), team);
                RewardHonorToTeam((uint)(BonusHonorFromKill(towersSurvived[i] * KillSurvivingTower) * HonorModifier), team);
            }

            // Client patch 1.7.0: owned graveyards are rewarded.
            if (gravesOwned[i] != 0)
            {
                RewardReputationToTeam(faction[i], (int)(gravesOwned[i] * _repOwnedGrave), team);
                RewardHonorToTeam((uint)(BonusHonorFromKill(gravesOwned[i] * KillSurvivingGrave) * HonorModifier), team);
            }

            if (minesOwned[i] != 0)
            {
                RewardReputationToTeam(faction[i], (int)(minesOwned[i] * _repOwnedMine), team);
                RewardHonorToTeam((uint)(BonusHonorFromKill(minesOwned[i] * KillSurvivingMine) * HonorModifier), team);
            }

            if (!IsActiveEvent((byte)(EventCaptainDeadAlliance + i), 0))
            {
                RewardReputationToTeam(faction[i], (int)_repSurviveCaptain, team);
                RewardHonorToTeam(BonusHonorFromKill(KillSurvivingCaptain), team);
            }
        }

        if (Ports.Calendar.IsBattlegroundWeekend(Type))
        {
            RewardHonorToTeam(1584, Team.Alliance);
            RewardHonorToTeam(1584, Team.Horde);
            if (winner is { } w)
            {
                RewardHonorToTeam(396, w);
            }
        }

        base.EndBattleground(winner);
    }

    // ------------------------------------------------------------------ spells

    /// <summary>
    /// vmangos <c>CheckSpellCast</c> (BattleGroundAV.cpp:1722-1744): a team has one shredder at a time. A summon is refused while the team's
    /// last summoner still controls a shredder (SPELL_FAILED_SPELL_UNAVAILABLE); otherwise the caster becomes the team's shredder owner.
    /// </summary>
    public override byte? CheckSpellCast(ObjectGuid caster, uint spellId)
    {
        if (spellId is not (SpellSummonShredderAlliance or SpellSummonShredderHorde))
        {
            return null;
        }

        int team = spellId == SpellSummonShredderAlliance ? 0 : 1;
        ObjectGuid owner = _shredderOwners[team];
        if (!owner.IsEmpty && Host.CharmedEntryOf(owner) is NpcShredderAlliance or NpcShredderHorde)
        {
            return CastFailedSpellUnavailable;
        }

        _shredderOwners[team] = caster;
        return null;
    }

    /// <summary>The player that summoned a team's shredder last (vmangos <c>m_shredderOwners</c>).</summary>
    public ObjectGuid ShredderOwner(Team team) => _shredderOwners[BattlegroundConstants.TeamIndex(team)];

    // ------------------------------------------------------------------ triggers, graveyards, world states

    /// <inheritdoc />
    public override bool HandleAreaTrigger(ObjectGuid player, uint areaTriggerId)
    {
        if (PlayerTeam(player) is not { } team)
        {
            return false;
        }

        switch (areaTriggerId)
        {
            case AreaTriggerAllianceExit when team == Team.Alliance:
            case AreaTriggerHordeExit when team == Team.Horde:
                Host.LeaveBattleground(player);
                return true;
            case 3326 or 3327 or 3328 or 3329 or 3330 or 3331:
                return true;
            default:
                return false;
        }
    }

    /// <inheritdoc />
    public override uint ClosestGraveyard(Team team) => team == Team.Alliance ? s_graveyardIds[7] : s_graveyardIds[8];

    /// <summary>
    /// vmangos <c>GetClosestGraveYard</c> (BattleGroundAV.cpp:1448-1488): the start cave before the start; then the nearest of the cave and the
    /// graveyards the team controls (squared 2D distance).
    /// </summary>
    public override uint ClosestGraveyard(Team team, float x, float y, Func<uint, (float X, float Y)?> safeLoc)
    {
        int teamIndex = BattlegroundConstants.TeamIndex(team);
        uint cave = s_graveyardIds[teamIndex + 7];
        if (Status != BattlegroundStatus.InProgress)
        {
            return cave;
        }

        uint best = cave;
        float minDist = 9999999.0f;
        if (safeLoc(cave) is { } home)
        {
            minDist = ((home.X - x) * (home.X - x)) + ((home.Y - y) * (home.Y - y));
        }

        for (int node = NodeFirstAidStation; node <= NodeFrostwolfHut; node++)
        {
            if (_nodes[node].Owner != teamIndex || _nodes[node].State != PointControlled || safeLoc(s_graveyardIds[node]) is not { } at)
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

        return best;
    }

    /// <inheritdoc />
    public override IReadOnlyList<(uint Id, int Value)> InitialWorldStates()
    {
        // BattleGroundAV.cpp:1388-1426.
        List<(uint Id, int Value)> states = [];
        for (int node = 0; node < NodeCount; node++)
        {
            for (int state = 0; state < MaxStates; state++)
            {
                bool stateOk = _nodes[node].State == state;
                states.Add((NodeWorldState(node, state, 0), _nodes[node].Owner == 0 && stateOk ? 1 : 0));
                states.Add((NodeWorldState(node, state, 1), _nodes[node].Owner == 1 && stateOk ? 1 : 0));
            }
        }

        if (_nodes[NodeSnowfallGrave].Owner == TeamNeutral)
        {
            states.Add((WorldStateSnowfallNeutral, 1));
        }

        states.Add((WorldStateAllianceScore, _teamScores[0]));
        states.Add((WorldStateHordeScore, _teamScores[1]));
        int show = Status == BattlegroundStatus.InProgress ? 1 : 0;
        states.Add((WorldStateShowAllianceScore, show));
        states.Add((WorldStateShowHordeScore, show));
        for (int mine = 0; mine < 2; mine++)
        {
            states.Add((s_mineWorldStates[mine][_mineOwner[mine]], 1));
            if (_mineOwner[mine] != _minePrevOwner[mine])
            {
                states.Add((s_mineWorldStates[mine][_minePrevOwner[mine]], 0));
            }
        }

        return states;
    }
}
