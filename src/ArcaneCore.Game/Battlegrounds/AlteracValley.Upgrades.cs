using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Battlegrounds;

/// <summary>
/// The Alterac Valley turn-ins, upgrades and challenge counters (vmangos BattleGroundAV.cpp:58-275, 433-772, 799-806; the collector and
/// quartermaster scripts of scripts/battlegrounds/battleground_alterac.cpp:2477-2560, 2617-2730, 3360-3660). Armor scraps raise the
/// defenders' upgrade level and pile supply crates at the base; the other turn-ins count towards the challenges (air, cavalry, ground and
/// world-boss assaults). The assault invocations themselves (the escorted troops, beacons, war riders and world bosses of the scripts) are
/// not ported: their counters, goals, go flags and resets are, so a later script can read them.
/// </summary>
public sealed partial class AlteracValley
{
    // BG_AV_QuestIds (BattleGroundAV.h:388-411).
    public const uint QuestAllianceScraps1 = 7223;
    public const uint QuestAllianceScraps2 = 6781;
    public const uint QuestHordeScraps1 = 7224;
    public const uint QuestHordeScraps2 = 6741;
    public const uint QuestAllianceCommander1 = 6942;
    public const uint QuestHordeCommander1 = 6825;
    public const uint QuestAllianceCommander2 = 6941;
    public const uint QuestHordeCommander2 = 6826;
    public const uint QuestAllianceCommander3 = 6943;
    public const uint QuestHordeCommander3 = 6827;
    public const uint QuestAllianceBoss1 = 7386;
    public const uint QuestHordeBoss1 = 7385;
    public const uint QuestAllianceBoss2 = 6881;
    public const uint QuestHordeBoss2 = 6801;
    public const uint QuestAllianceNearMine = 5892;
    public const uint QuestHordeNearMine = 5893;
    public const uint QuestAllianceOtherMine = 6982;
    public const uint QuestHordeOtherMine = 6985;
    public const uint QuestAllianceRiderHide = 7026;
    public const uint QuestHordeRiderHide = 7002;
    public const uint QuestAllianceRiderTame = 7027;
    public const uint QuestHordeRiderTame = 7001;

    /// <summary>The visual supply crates and tamed mounts (BattleGroundAV.h:115-123): + team index is the exact event.</summary>
    public const byte EventSupplies100 = 80;
    public const byte EventSupplies200 = 82;
    public const byte EventSupplies300 = 84;
    public const byte EventSupplies400 = 86;
    public const byte EventTamed05 = 90;
    public const byte EventTamed10 = 92;
    public const byte EventTamed15 = 94;
    public const byte EventTamed20 = 96;

    /// <summary>The spells the whole team gets at each defender upgrade (BattleGroundAV.cpp:461-487).</summary>
    public const uint SpellSeasonedUnits = 28418;
    public const uint SpellVeteranUnits = 28419;
    public const uint SpellChampionUnits = 28420;

    /// <summary>The quartermasters that take armor scraps and offer the upgrades (battleground_alterac.cpp:1281-1282).</summary>
    public const uint NpcRegzar = 13176;
    public const uint NpcMurgot = 13257;

    // The gossip texts of the upgrade menus (battleground_alterac.cpp:2570-2615, 2704-2738, 3590-3655).
    public const uint GossipItemUpgradeSeasoned = 8718;
    public const uint GossipItemUpgradeVeteran = 8719;
    public const uint GossipItemUpgradeChampion = 8723;
    public const uint GossipItemNextUpgrade = 9130;

    /// <summary>GOSSIP_ACTION_INFO_DEF (ScriptedGossip.h:53).</summary>
    public const uint GossipActionInfoDef = 1000;

    /// <summary>The troop levels the armor scraps buy (BG_AV_TROOPS_LEVEL).</summary>
    public const int TroopsBasic = 0;
    public const int TroopsSeasoned = 1;
    public const int TroopsVeteran = 2;
    public const int TroopsChampion = 3;

    // BG_AV_CHALLENGE (BattleGroundAV.h:92-104).
    public const int ChallengeSoldierAir = 0;
    public const int ChallengeLieutenantAir = 1;
    public const int ChallengeCommanderAir = 2;
    public const int ChallengeHideCavalry = 3;
    public const int ChallengeTamedCavalry = 4;
    public const int ChallengeIrondeepGround = 5;
    public const int ChallengeColdtoothGround = 6;
    public const int ChallengeBloodWorldBoss = 7;
    public const int ChallengeCount = 8;

    // BG_AV_ASSAULT (BattleGroundAV.h:106-119).
    public const int AssaultAirBeaconSoldier = 0;
    public const int AssaultAirBeaconLieutenant = 1;
    public const int AssaultAirBeaconCommander = 2;
    public const int AssaultAirGlobalSoldier = 3;
    public const int AssaultAirGlobalLieutenant = 4;
    public const int AssaultAirGlobalCommander = 5;
    public const int AssaultCavalry = 6;
    public const int AssaultGround = 7;
    public const int AssaultWorldBoss = 8;
    public const int AssaultCount = 10;

    /// <summary>Reputation ranks the challenges ask for (vmangos ReputationRank: neutral 3, honored 5, revered 6).</summary>
    public const int RankNeutral = 3;
    public const int RankHonored = 5;
    public const int RankRevered = 6;

    private const int TrackedQuests = 9;

    // m_teamQuestStatus (BattleGroundAV.h:551): 0 armor scraps, 1-3 commander quests, 4 boss items, 5 near mine, 6 other mine,
    // 7 rider hides, 8 tamed mounts.
    private readonly uint[,] _teamQuestStatus = new uint[2, TrackedQuests];
    private readonly int[] _reinforcementLevel = [TroopsBasic, TroopsBasic];
    private readonly uint[,] _challengeStatus = new uint[2, ChallengeCount];
    private readonly uint[,] _challengeGoals = new uint[2, ChallengeCount];
    private readonly int[] _challengeMinReputation = new int[AssaultCount];
    private readonly uint[] _challengeTimerStart = new uint[AssaultCount];
    private readonly bool[,] _challengePlayerGoStatus = new bool[2, AssaultCount];

    /// <summary>The quest giver a turn-in or a gossip choice spoke to, and the player (for the "%s" of a line).</summary>
    private readonly record struct Speaker(ObjectGuid Creature, ObjectGuid Player);

    /// <summary>
    /// vmangos initializeChallengeInvocationGoals (BattleGroundAV.cpp:98-157) without the captain buff timers, which the constructor draws: the
    /// counters at 0, both teams' troops basic, the goals, the minimum reputations (the cavalry needs revered before patch 1.6, honored after;
    /// this server is 1.12) and the repeat timers.
    /// </summary>
    private void InitializeChallengeGoals()
    {
        for (int team = 0; team < 2; team++)
        {
            _challengeGoals[team, ChallengeSoldierAir] = 90;
            _challengeGoals[team, ChallengeLieutenantAir] = 60;
            _challengeGoals[team, ChallengeCommanderAir] = 30;
            _challengeGoals[team, ChallengeHideCavalry] = 25;
            _challengeGoals[team, ChallengeTamedCavalry] = 25;
            _challengeGoals[team, ChallengeBloodWorldBoss] = 200;
        }

        _challengeGoals[0, ChallengeIrondeepGround] = 280;
        _challengeGoals[0, ChallengeColdtoothGround] = 70;
        _challengeGoals[1, ChallengeIrondeepGround] = 70;
        _challengeGoals[1, ChallengeColdtoothGround] = 280;

        Array.Fill(_challengeMinReputation, RankNeutral);
        _challengeMinReputation[AssaultCavalry] = RankHonored;
        _challengeMinReputation[AssaultGround] = RankHonored;
        _challengeMinReputation[AssaultWorldBoss] = RankNeutral;
        _challengeMinReputation[AssaultAirBeaconSoldier] = RankRevered;
        _challengeMinReputation[AssaultAirBeaconLieutenant] = RankRevered;
        _challengeMinReputation[AssaultAirBeaconCommander] = RankRevered;

        _challengeTimerStart[AssaultAirBeaconSoldier] = 1_800_000;
        _challengeTimerStart[AssaultAirBeaconLieutenant] = 1_800_000;
        _challengeTimerStart[AssaultAirBeaconCommander] = 1_800_000;
        _challengeTimerStart[AssaultAirGlobalSoldier] = 172_800_000;
        _challengeTimerStart[AssaultAirGlobalLieutenant] = 172_800_000;
        _challengeTimerStart[AssaultAirGlobalCommander] = 172_800_000;
        _challengeTimerStart[AssaultGround] = 1_800_000;
        _challengeTimerStart[AssaultCavalry] = 1_800_000;
        _challengeTimerStart[AssaultWorldBoss] = 172_800_000;
    }

    // ------------------------------------------------------------------ queries

    /// <summary>A tracked turn-in count of a team (vmangos <c>m_teamQuestStatus[team][index]</c>; 0 is the armor scraps).</summary>
    public uint TeamQuestStatus(Team team, int index) => _teamQuestStatus[BattlegroundConstants.TeamIndex(team), index];

    /// <summary>The armor scraps a team turned in (vmangos <c>GetActualArmorRessources</c>).</summary>
    public uint ArmorResources(Team team) => _teamQuestStatus[BattlegroundConstants.TeamIndex(team), 0];

    /// <summary>The troop level a team has bought (vmangos <c>getReinforcementLevelGroundUnit</c>): <see cref="TroopsBasic"/> to <see cref="TroopsChampion"/>.</summary>
    public int ReinforcementLevel(Team team) => _reinforcementLevel[BattlegroundConstants.TeamIndex(team)];

    /// <summary>The defender type a team's armor scraps give (vmangos PopulateNode, BattleGroundAV.cpp:1220-1243: below 500, 1000, 1500, else champion).</summary>
    public int DefenderType(int teamIndex)
    {
        if (teamIndex == TeamNeutral)
        {
            return 0;
        }

        uint scraps = _teamQuestStatus[teamIndex, 0];
        return scraps < 500 ? 0 : scraps < 1000 ? 1 : scraps < 1500 ? 2 : 3;
    }

    /// <summary>vmangos <c>getChallengeInvocationCounter</c>.</summary>
    public uint ChallengeCounter(Team team, int challenge) => _challengeStatus[BattlegroundConstants.TeamIndex(team), challenge];

    /// <summary>vmangos <c>getChallengeInvocationGoals</c>.</summary>
    public uint ChallengeGoal(Team team, int challenge) => _challengeGoals[BattlegroundConstants.TeamIndex(team), challenge];

    /// <summary>vmangos <c>getMinReputationNeeded</c>: the reputation rank an assault needs.</summary>
    public int MinReputationNeeded(int assault) => _challengeMinReputation[assault];

    /// <summary>vmangos <c>getTimerNeeded</c>: how long an assault waits before it can be repeated.</summary>
    public uint TimerNeeded(int assault) => _challengeTimerStart[assault];

    /// <summary>vmangos <c>getPlayerGoStatus</c>: whether the team's players launched the assault.</summary>
    public bool PlayerGoStatus(Team team, int assault) => _challengePlayerGoStatus[BattlegroundConstants.TeamIndex(team), assault];

    /// <summary>vmangos <c>setPlayerGoStatus</c>.</summary>
    public void SetPlayerGoStatus(Team team, int assault, bool value) => _challengePlayerGoStatus[BattlegroundConstants.TeamIndex(team), assault] = value;

    /// <summary>vmangos <c>setChallengeInvocationCounter</c>: adds <paramref name="effortDone"/> (the name says set, the code adds).</summary>
    public void AddChallengeCounter(Team team, int challenge, uint effortDone) => _challengeStatus[BattlegroundConstants.TeamIndex(team), challenge] += effortDone;

    /// <summary>vmangos <c>isAerialChallengeInvocationReady</c> (beacon or global of the same rank).</summary>
    public bool IsAerialChallengeReady(Team team, int aerialAssault)
    {
        int challenge = aerialAssault switch
        {
            AssaultAirBeaconSoldier or AssaultAirGlobalSoldier => ChallengeSoldierAir,
            AssaultAirBeaconLieutenant or AssaultAirGlobalLieutenant => ChallengeLieutenantAir,
            AssaultAirBeaconCommander or AssaultAirGlobalCommander => ChallengeCommanderAir,
            _ => -1,
        };
        return challenge >= 0 && ChallengeCounter(team, challenge) >= ChallengeGoal(team, challenge);
    }

    /// <summary>vmangos <c>isCavalryChallengeInvocationReady</c>: both the hides and the tamed mounts.</summary>
    public bool IsCavalryChallengeReady(Team team)
        => ChallengeCounter(team, ChallengeHideCavalry) >= ChallengeGoal(team, ChallengeHideCavalry)
            && ChallengeCounter(team, ChallengeTamedCavalry) >= ChallengeGoal(team, ChallengeTamedCavalry);

    /// <summary>vmangos <c>isGroundChallengeInvocationReady</c>: either mine's supplies.</summary>
    public bool IsGroundChallengeReady(Team team)
        => ChallengeCounter(team, ChallengeIrondeepGround) >= ChallengeGoal(team, ChallengeIrondeepGround)
            || ChallengeCounter(team, ChallengeColdtoothGround) >= ChallengeGoal(team, ChallengeColdtoothGround);

    /// <summary>vmangos <c>isWorldBossChallengeInvocationReady</c>.</summary>
    public bool IsWorldBossChallengeReady(Team team) => ChallengeCounter(team, ChallengeBloodWorldBoss) >= ChallengeGoal(team, ChallengeBloodWorldBoss);

    /// <summary>vmangos <c>resetAerialChallengeInvocation</c>: only a beacon assault resets its counter (a global one cannot be repeated).</summary>
    public void ResetAerialChallenge(Team team, int aerialAssault)
    {
        int t = BattlegroundConstants.TeamIndex(team);
        switch (aerialAssault)
        {
            case AssaultAirBeaconSoldier:
                _challengeStatus[t, ChallengeSoldierAir] = 0;
                break;
            case AssaultAirBeaconLieutenant:
                _challengeStatus[t, ChallengeLieutenantAir] = 0;
                break;
            case AssaultAirBeaconCommander:
                _challengeStatus[t, ChallengeCommanderAir] = 0;
                break;
        }
    }

    /// <summary>vmangos <c>resetGroundChallengeInvocation</c>.</summary>
    public void ResetGroundChallenge(Team team)
    {
        int t = BattlegroundConstants.TeamIndex(team);
        _challengeStatus[t, ChallengeIrondeepGround] = 0;
        _challengeStatus[t, ChallengeColdtoothGround] = 0;
    }

    /// <summary>vmangos <c>resetCavalryChallengeInvocation</c>: the counters, and the tamed mounts in the stables go (events + team, 2).</summary>
    public void ResetCavalryChallenge(Team team)
    {
        int t = BattlegroundConstants.TeamIndex(team);
        _challengeStatus[t, ChallengeHideCavalry] = 0;
        _challengeStatus[t, ChallengeTamedCavalry] = 0;
        foreach (byte tamed in new[] { EventTamed05, EventTamed10, EventTamed15, EventTamed20 })
        {
            SpawnEvent((byte)(tamed + t), 2, spawn: true, forcedDespawn: true);
        }
    }

    /// <summary>vmangos <c>resetWorldBossChallengeInvocation</c>.</summary>
    public void ResetWorldBossChallenge(Team team) => _challengeStatus[BattlegroundConstants.TeamIndex(team), ChallengeBloodWorldBoss] = 0;

    /// <summary>vmangos <c>ResetTamedEvent</c> (BattleGroundAV.cpp:799-806): the stables' mounts go (event + team, 2), without a forced despawn.</summary>
    public void ResetTamedEvent(Team team)
    {
        int t = BattlegroundConstants.TeamIndex(team);
        foreach (byte tamed in new[] { EventTamed05, EventTamed10, EventTamed15, EventTamed20 })
        {
            SpawnEvent((byte)(tamed + t), 2, spawn: true, forcedDespawn: false);
        }
    }

    // ------------------------------------------------------------------ turn-ins

    /// <summary>
    /// A participant was rewarded a quest by a creature of the match (vmangos Player::RewardQuest → <c>HandleQuestComplete</c>, Player.cpp:13093-13095,
    /// BattleGroundAV.cpp:501-772, then the quest giver's own <c>pQuestRewardedNPC</c> script: <see cref="CollectorQuestComplete"/>).
    /// <paramref name="requiredItem"/> and <paramref name="requiredCount"/> are the quest's first required item and its count.
    /// </summary>
    public void HandleQuestComplete(ObjectGuid player, ObjectGuid questGiver, uint questId, uint requiredItem, uint requiredCount)
    {
        if (Status != BattlegroundStatus.InProgress || PlayerTeam(player) is not { } team)
        {
            return;
        }

        int t = BattlegroundConstants.TeamIndex(team);
        var speaker = new Speaker(questGiver, player);
        string beacon = t == 0
            ? "Soldiers of Stormpike, come to my aid! The beacon must be planted."
            : "Soldiers of the Horde, come to my aid! The beacon must be planted.";
        int reputation;
        switch (questId)
        {
            case QuestAllianceScraps1 or QuestAllianceScraps2 or QuestHordeScraps1 or QuestHordeScraps2:
                reputation = ScrapsTurnedIn(t, speaker);
                break;
            case QuestAllianceCommander1 or QuestHordeCommander1:
                reputation = 1;
                if (++_teamQuestStatus[t, 1] == 90)
                {
                    Host.CreatureSay(questGiver, beacon, yell: true, player);
                }

                break;
            case QuestAllianceCommander2 or QuestHordeCommander2:
                reputation = 2;
                if (++_teamQuestStatus[t, 2] == 60)
                {
                    Host.CreatureSay(questGiver, beacon, yell: true, player);
                }

                break;
            case QuestAllianceCommander3 or QuestHordeCommander3:
                reputation = 5;
                if (++_teamQuestStatus[t, 3] == 30)
                {
                    Host.CreatureSay(questGiver, beacon, yell: true, player);
                }

                break;
            case QuestAllianceBoss1 or QuestHordeBoss1 or QuestAllianceBoss2 or QuestHordeBoss2:
            {
                // The five-item quest counts 4 here and falls through to the one-item quest's 1 (BattleGroundAV.cpp:600-606).
                bool five = questId is QuestAllianceBoss1 or QuestHordeBoss1;
                _teamQuestStatus[t, 4] += five ? 5u : 1u;
                reputation = five ? 5 : 1;
                if (_teamQuestStatus[t, 4] == 200)
                {
                    Host.CreatureSay(questGiver, t == 0
                        ? "Soldiers of Stormpike, aid and protect us! The Forest Lord has granted us his protection. The portal must now be opened!"
                        : "Soldiers of Frostwolf, come to my aid! The Ice Lord has granted us his protection. He's accepted the offering! The time has come to unleash him upon the Stormpike Army!",
                        yell: true, player);
                }

                break;
            }

            case QuestAllianceNearMine or QuestHordeNearMine:
                _teamQuestStatus[t, 5]++;
                reputation = 2;
                break;
            case QuestAllianceOtherMine or QuestHordeOtherMine:
                _teamQuestStatus[t, 6]++;
                reputation = 3;
                break;
            case QuestAllianceRiderHide or QuestHordeRiderHide:
                _teamQuestStatus[t, 7]++;
                reputation = 1;
                break;
            case QuestAllianceRiderTame or QuestHordeRiderTame:
                reputation = TamedMountTurnedIn(t, speaker);
                break;
            case 7402 or 7428 or 7364 or 7424 or 7365 or 7425 or 7426 or 7366 or 7367 or 7368 or 7401 or 7427 or 7361 or 7421 or 7422 or 7362
                or 7423 or 7363:
                reputation = 1;
                break;
            default:
                reputation = 0;
                break;
        }

        if (reputation != 0)
        {
            RewardReputationToTeam(team == Team.Alliance ? FactionStormpike : FactionFrostwolf, reputation, team);
        }

        CollectorQuestComplete(team, questId, requiredItem, requiredCount);
    }

    /// <summary>
    /// The armor scraps (BattleGroundAV.cpp:511-583): 20 a turn-in; every hundred that is not a five hundred brings a line, the crate piles grow
    /// at 100 to 400 of each five hundred and go at the five hundred (and are cleared at the first turn-in). One reputation.
    /// </summary>
    private int ScrapsTurnedIn(int t, Speaker speaker)
    {
        uint scraps = _teamQuestStatus[t, 0] += 20;
        if (scraps % 100 == 0 && scraps % 500 != 0)
        {
            Host.CreatureSay(speaker.Creature, "Great! Let's keep those supplies coming, people!", yell: false, speaker.Player);
        }

        switch (scraps % 500)
        {
            case 100:
                SpawnEvent((byte)(EventSupplies100 + t), 0, spawn: true, forcedDespawn: false);
                break;
            case 200:
                SpawnEvent((byte)(EventSupplies200 + t), 0, spawn: true, forcedDespawn: false);
                break;
            case 300:
                SpawnEvent((byte)(EventSupplies300 + t), 0, spawn: true, forcedDespawn: true);
                break;
            case 400:
                SpawnEvent((byte)(EventSupplies400 + t), 0, spawn: true, forcedDespawn: true);
                break;
            case 0 when scraps != 0:
                ClearSupplies(t);
                break;
            default:
                if (scraps == 20)
                {
                    ClearSupplies(t);
                }

                break;
        }

        return 1;
    }

    private void ClearSupplies(int t)
    {
        foreach (byte supplies in new[] { EventSupplies100, EventSupplies200, EventSupplies300, EventSupplies400 })
        {
            SpawnEvent((byte)(supplies + t), 2, spawn: true, forcedDespawn: false);
        }
    }

    /// <summary>
    /// A tamed mount brought to the stables (BattleGroundAV.cpp:665-713): every 25th fills them (a yell), every 5th brings a thanks, and a mount
    /// shows in the stables at 5 to 20 of each 25 (they all go at the first). One reputation.
    /// </summary>
    private int TamedMountTurnedIn(int t, Speaker speaker)
    {
        uint tamed = ++_teamQuestStatus[t, 8];
        if (tamed % 25 == 0)
        {
            Host.CreatureSay(speaker.Creature, "The stables are filled up!", yell: true, speaker.Player);
        }

        if (tamed % 5 == 0)
        {
            Host.CreatureSay(speaker.Creature, "Thanks for the supplies, %s", yell: false, speaker.Player);
        }

        switch (tamed % 25)
        {
            case 5:
                SpawnEvent((byte)(EventTamed05 + t), 0, spawn: true, forcedDespawn: false);
                break;
            case 10:
                SpawnEvent((byte)(EventTamed10 + t), 0, spawn: true, forcedDespawn: false);
                break;
            case 15:
                SpawnEvent((byte)(EventTamed15 + t), 0, spawn: true, forcedDespawn: true);
                break;
            case 20:
                SpawnEvent((byte)(EventTamed20 + t), 0, spawn: true, forcedDespawn: true);
                break;
            default:
                if (tamed == 1)
                {
                    foreach (byte mount in new[] { EventTamed05, EventTamed10, EventTamed15, EventTamed20 })
                    {
                        SpawnEvent((byte)(mount + t), 2, spawn: true, forcedDespawn: false);
                    }
                }

                break;
        }

        return 1;
    }

    /// <summary>
    /// The collectors' quest-rewarded script (QuestComplete_npc_AVBlood_collector, battleground_alterac.cpp:2477-2560): a turn-in of blood or
    /// crystals, hides, flesh or medals, or mine supplies adds its required count to the matching challenge, a tamed mount adds one to the
    /// tamed cavalry; when the world-boss offering is complete it is reset and the team's go flag for the world boss is set (the escorted
    /// summoning that follows in the script is not ported). Other quests are not a collector's.
    /// </summary>
    public void CollectorQuestComplete(Team team, uint questId, uint requiredItem, uint requiredCount)
    {
        if (Status != BattlegroundStatus.InProgress)
        {
            return;
        }

        int challenge;
        uint delivered = requiredCount;
        if (questId is QuestHordeRiderTame or QuestAllianceRiderTame)
        {
            challenge = ChallengeTamedCavalry;
            delivered = 1;
        }
        else
        {
            if (requiredItem == 0 || requiredCount == 0)
            {
                return;
            }

            challenge = requiredItem switch
            {
                17306 or 17423 => ChallengeBloodWorldBoss,
                17643 or 17642 => ChallengeHideCavalry,
                17326 or 17502 => ChallengeSoldierAir,
                17327 or 17503 => ChallengeLieutenantAir,
                17328 or 17504 => ChallengeCommanderAir,
                17522 => ChallengeIrondeepGround,
                17542 => ChallengeColdtoothGround,
                _ => -1,
            };
            if (challenge < 0)
            {
                return;
            }
        }

        AddChallengeCounter(team, challenge, delivered);
        if (challenge == ChallengeBloodWorldBoss && IsWorldBossChallengeReady(team))
        {
            ResetWorldBossChallenge(team);
            SetPlayerGoStatus(team, AssaultWorldBoss, true);
        }
    }

    // ------------------------------------------------------------------ the upgrade

    /// <summary>
    /// vmangos <c>UpgradeArmor</c> (BattleGroundAV.cpp:439-499), chosen at the quartermaster: with 500, 1000 or 1500 scraps and the level below it,
    /// the troops become seasoned, veteran or champion (the whole team gets the matching spell and the quartermaster thanks the player and
    /// yells it); otherwise the level is set from 0 resources, which is basic (a quirk of the reference kept as it is, as is the thanks line it
    /// says even then). When the scraps are exactly 500, 1000 or 1500 every graveyard and tower the team controls is populated again with the
    /// new defenders.
    /// </summary>
    public void UpgradeArmor(ObjectGuid questGiver, ObjectGuid player)
    {
        if (PlayerTeam(player) is not { } team)
        {
            return;
        }

        int t = BattlegroundConstants.TeamIndex(team);
        uint scraps = _teamQuestStatus[t, 0];
        uint resources = 0;
        if (scraps >= 500 && _reinforcementLevel[t] == TroopsBasic)
        {
            resources = 500;
        }
        else if (scraps >= 1000 && _reinforcementLevel[t] == TroopsSeasoned)
        {
            resources = 1000;
        }
        else if (scraps >= 1500 && _reinforcementLevel[t] == TroopsVeteran)
        {
            resources = 1500;
        }

        _reinforcementLevel[t] = resources < 500 ? TroopsBasic : resources < 1000 ? TroopsSeasoned : resources < 1500 ? TroopsVeteran : TroopsChampion;
        if (resources % 500 == 0 && scraps != 0 && !questGiver.IsEmpty)
        {
            Host.CreatureSay(questGiver, "Thanks for the supplies, %s", yell: false, player);
            (uint spell, string line) = resources switch
            {
                500 => (SpellSeasonedUnits, "Seasoned units are entering the battle!"),
                1000 => (SpellVeteranUnits, "Veteran units are entering the battle!"),
                1500 => (SpellChampionUnits, "Champion units are entering the battle!"),
                _ => (0u, string.Empty),
            };
            if (spell != 0)
            {
                CastSpellOnTeam(spell, team);
                Host.CreatureSay(questGiver, line, yell: true, player);
            }
        }

        if (scraps is 500 or 1000 or 1500)
        {
            for (int node = NodeFirstAidStation; node <= NodeFrostwolfWestTower; node++)
            {
                if (_nodes[node].Owner == t && _nodes[node].State == PointControlled)
                {
                    PopulateNode(node);
                }
            }
        }
    }

    /// <summary>
    /// The quartermaster's menu (GossipHello_npc_AVBlood_collector for Murgot and Regzar, battleground_alterac.cpp:2667-2738): the quest list,
    /// "what is the next upgrade" whose answer depends on how close the scraps are, and the upgrade itself for a player honored with either
    /// faction once the scraps reach the next level. Null when the creature is not a quartermaster or the match is not running.
    /// </summary>
    public AvGossipMenu? QuartermasterMenu(uint creatureEntry, ObjectGuid player, Func<uint, int> reputationRank)
    {
        if (creatureEntry is not (NpcMurgot or NpcRegzar) || PlayerTeam(player) is not { } team)
        {
            return null;
        }

        uint scraps = ArmorResources(team);
        uint answer = ChallengeCount;
        if (scraps < 500)
        {
            answer += scraps <= 200 ? 0u : scraps < 400 ? 1u : 4u;
        }
        else if (scraps < 1000)
        {
            answer += scraps <= 700 ? 0u : scraps < 900 ? 2u : 5u;
        }
        else if (scraps < 1500)
        {
            answer += scraps <= 1200 ? 0u : scraps < 1400 ? 3u : 6u;
        }

        bool honored = reputationRank(FactionFrostwolf) >= RankHonored || reputationRank(FactionStormpike) >= RankHonored;
        var items = new List<AvGossipItem>();
        (uint textId, uint threshold, uint upgradeText) = ReinforcementLevel(team) switch
        {
            TroopsBasic => (6073u, 500u, GossipItemUpgradeSeasoned),
            TroopsSeasoned => (6217u, 1000u, GossipItemUpgradeVeteran),
            TroopsVeteran => (6218u, 1500u, GossipItemUpgradeChampion),
            _ => (0u, 0u, 0u),
        };
        if (textId == 0)
        {
            return new AvGossipMenu(0, items); // champion: the creature's own gossip text
        }

        items.Add(new AvGossipItem(GossipItemNextUpgrade, GossipActionInfoDef + answer + 1));
        if (honored && scraps >= threshold)
        {
            items.Add(new AvGossipItem(upgradeText, GossipActionInfoDef + threshold + 1));
        }

        return new AvGossipMenu(textId, items);
    }

    /// <summary>
    /// A quartermaster's menu choice (GossipSelect_npc_AVBlood_collector, battleground_alterac.cpp:3360-3660 for Murgot and Regzar): the next
    /// upgrade answers with an npc text, an upgrade choice runs <see cref="UpgradeArmor"/>. Returns the npc text to show, or 0 for none.
    /// </summary>
    public uint QuartermasterSelect(ObjectGuid questGiver, ObjectGuid player, uint action)
    {
        if (action < GossipActionInfoDef + 1)
        {
            return 0;
        }

        uint choice = action - (GossipActionInfoDef + 1);
        switch (choice)
        {
            case ChallengeCount: return 6784;
            case ChallengeCount + 1: return 6780;
            case ChallengeCount + 2: return 6781;
            case ChallengeCount + 3: return 6783;
            case ChallengeCount + 4: return 6778;
            case ChallengeCount + 5: return 6779;
            case ChallengeCount + 6: return 6782;
            case 500 or 1000 or 1500:
                UpgradeArmor(questGiver, player);
                return 0;
            default:
                return 0;
        }
    }
}

/// <summary>A gossip line of an Alterac Valley script menu: its broadcast text and the action it carries back.</summary>
public readonly record struct AvGossipItem(uint BroadcastTextId, uint Action);

/// <summary>An Alterac Valley script menu: the npc text (0 = the creature's own) and its lines, after the quest list.</summary>
public sealed record AvGossipMenu(uint NpcTextId, IReadOnlyList<AvGossipItem> Items);
