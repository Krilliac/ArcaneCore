using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using static ArcaneCore.Game.Battlegrounds.AlteracValley;

namespace ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;

/// <summary>A gossip line of the collectors' menus: its icon, broadcast text and the action it carries back.</summary>
public readonly record struct AvGossipLine(byte Icon, uint BroadcastTextId, uint Action);

/// <summary>
/// The collector's answer to a hello: <see cref="Default"/> asks for the creature's own database menu (the script returned false),
/// <see cref="Silent"/> sends nothing (the script returned true without a menu), otherwise the quest list when <see cref="ShowQuests"/>, the
/// lines and the npc text (0: the creature's own).
/// </summary>
public sealed record AvCollectorHello(bool Default, bool Silent, bool ShowQuests, uint NpcTextId, IReadOnlyList<AvGossipLine> Items)
{
    public static AvCollectorHello UseDefault { get; } = new(true, false, false, 0, []);

    public static AvCollectorHello Nothing { get; } = new(false, true, false, 0, []);
}

/// <summary>What a collector's menu choice asks for: close the menu, open the vendor list, show an npc text, give the player an item.</summary>
public readonly record struct AvCollectorChoice(bool Close, bool Vendor, uint NpcTextId, uint GiveItem);

/// <summary>
/// The collectors' gossip and quest scripts (vmangos GossipHello_npc_AVBlood_collector, GossipSelect_npc_AVBlood_collector,
/// QuestComplete_npc_AVBlood_collector and QuestComplete_AV_npc_troops_chief, scripts/battlegrounds/battleground_alterac.cpp:2477-2560,
/// 2617-3124, 3299-3341, 3360-3660) for the creatures other than Murgot Deepforge and Regzar, whose menu is
/// <see cref="AlteracValley.QuartermasterMenu"/>: once a challenge's offerings are complete the collector offers to launch its assault to a
/// player at least neutral with Frostwolf or Stormpike; launching it spends the offerings, sets the team's go flag the assault scripts watch,
/// hands out the beacon or the assault orders, or starts the cavalry commander's ride. The world boss summoners show how close the offering
/// is. World thread.
/// </summary>
public static class AvCollectorGossip
{
    // battleground_alterac.cpp:2562-2615
    public const uint GossipGroundAssault = 9128;
    public const uint GossipBeaconWestHorde = 8669;
    public const uint GossipBeaconWestAlliance = 8799;
    public const uint GossipBeaconEastAlliance = 8796;
    public const uint GossipBeaconEastHorde = 8667;
    public const uint GossipBeaconSnowfallHorde = 8671;
    public const uint GossipBeaconSnowfallAlliance = 8793;
    public const uint GossipAssaultCavalry = 8903;
    public const uint GossipAssaultGround = 9050;
    public const uint GossipAssaultAirGuse = 10341;
    public const uint GossipAssaultAirSlidore = 10351;
    public const uint GossipAssaultAirJeztor = 10343;
    public const uint GossipAssaultAirVipore = 10353;
    public const uint GossipAssaultAirMulverick = 10346;
    public const uint GossipAssaultAirIchman = 10349;
    public const uint GossipRenferalBoss1 = 8757;
    public const uint GossipRenferalBoss2 = 8759;
    public const uint GossipRenferalBoss3 = 8761;
    public const uint GossipThurlogaBoss1 = 8641;
    public const uint GossipThurlogaBoss2 = 8643;
    public const uint GossipThurlogaBoss3 = 8645;

    // battleground_alterac.cpp:3346-3357: the commander's shout when an assault is launched.
    public const int SayWolfRider = 8887;
    public const int SayRamRider = 8905;
    public const int SayPatrol = 8907;
    public const int SayReavers = 8913;
    public const int SayGuse = 10339;
    public const int SayJeztor = 10342;
    public const int SayMulverick = 10347;
    public const int SayVipore = 10357;
    public const int SayIchman = 10358;

    /// <summary>The quartermasters' npc text with an assault offer (SEND_GOSSIP_MENU(6255)).</summary>
    public const uint NpcTextQuartermaster = 6255;

    /// <summary>The faction template a wing commander takes for a moment when it sets off (1194), before the player's.</summary>
    public const uint WingCommanderEscortFaction = 1194;

    /// <summary>Vipore's spell when he sets off (5759).</summary>
    public const uint SpellViporeSetOff = 5759;

    private const byte IconChat = 0;
    private const byte IconVendor = 1;
    private const int WrongValue = -1;

    /// <summary>Whether the creature's gossip is this script's (every collector but Murgot and Regzar).</summary>
    public static bool IsCollector(uint entry) => entry is NpcQuartermasterAlliance or NpcQuartermasterHorde
        or NpcWingCommanderGuse or NpcWingCommanderJeztor or NpcWingCommanderMulverick
        or NpcWingCommanderSlidore or NpcWingCommanderIchman or NpcWingCommanderVipore
        or NpcPrimalistThurloga or NpcArchDruidRenferal or NpcWolfRiderCommander or NpcRamRiderCommander;

    /// <summary>
    /// GossipHello_npc_AVBlood_collector for <paramref name="creature"/> (battleground_alterac.cpp:2617-3124 without the Murgot and Regzar
    /// part). <paramref name="questsStarted"/> are the quests the creature starts with their first required item (its creature_questrelation
    /// rows); <paramref name="reputationRank"/> answers a faction's reputation rank for the player.
    /// </summary>
    public static AvCollectorHello Hello(AlteracValley match, Creature creature, Player player, Team team,
        IEnumerable<(uint Quest, uint RequiredItem)> questsStarted, Func<uint, int> reputationRank)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(questsStarted);
        ArgumentNullException.ThrowIfNull(reputationRank);
        uint entry = creature.Template.Entry;
        var items = new List<AvGossipLine>();
        bool showQuests = false;
        bool objectiveReached = false;
        bool globalSoldier = false;
        bool globalLieutenant = false;
        bool globalCommander = false;
        int challenge = 0;
        uint message = 0;
        uint globalMessage = 0;

        if (entry is NpcQuartermasterAlliance or NpcQuartermasterHorde)
        {
            challenge = ChallengeCount;
            showQuests = true;
            uint irondeep = match.ChallengeCounter(team, ChallengeIrondeepGround);
            uint coldtooth = match.ChallengeCounter(team, ChallengeColdtoothGround);
            if (irondeep + coldtooth > 230)
            {
                challenge += 14;
            }
            else if (irondeep + coldtooth > 140)
            {
                challenge += 12;
            }
            else if (irondeep > 210 && coldtooth > 50)
            {
                challenge += 14;
            }
            else if (irondeep > 110 && coldtooth > 20)
            {
                challenge += 12;
            }
            else
            {
                challenge += 13;
            }

            items.Add(new AvGossipLine(IconChat, GossipGroundAssault, GossipActionInfoDef + (uint)challenge + 1));
        }

        if (entry is NpcWingCommanderGuse or NpcWingCommanderJeztor or NpcWingCommanderMulverick)
        {
            creature.SetUInt32(UpdateFields.UnitDynamicFlags, 0);
            if (AvScript.Distance(creature, -1338.6f, -328.16f, 90.8f) > 15.0f && team == Team.Horde)
            {
                SetOff(creature, player, castVipore: false);
                return AvCollectorHello.UseDefault;
            }

            if (AvScript.Distance(creature, -1338.6f, -328.16f, 90.8f) < 15.0f)
            {
                creature.NpcFlags |= (uint)NpcFlags.QuestGiver;
            }
        }
        else if (entry is NpcWingCommanderSlidore or NpcWingCommanderIchman or NpcWingCommanderVipore)
        {
            if (AvScript.Distance(creature, 575.116f, -51.90f, 37.62f) > 15.0f && team == Team.Alliance)
            {
                SetOff(creature, player, castVipore: entry == NpcWingCommanderVipore);
                return AvCollectorHello.UseDefault;
            }

            creature.NpcFlags |= (uint)NpcFlags.QuestGiver;
        }

        if (entry != NpcFieldMarshalTeravaine)
        {
            foreach ((uint _, uint requiredItem) in questsStarted)
            {
                if (requiredItem == 0)
                {
                    continue;
                }

                (int questChallenge, int minReputation) = requiredItem switch
                {
                    17643 or 17642 => (ChallengeHideCavalry, match.MinReputationNeeded(AssaultCavalry)),
                    17326 or 17502 => (ChallengeSoldierAir, match.MinReputationNeeded(AssaultAirBeaconSoldier)),
                    17327 or 17503 => (ChallengeLieutenantAir, match.MinReputationNeeded(AssaultAirBeaconLieutenant)),
                    17328 or 17504 => (ChallengeCommanderAir, match.MinReputationNeeded(AssaultAirBeaconCommander)),
                    17522 => (ChallengeIrondeepGround, match.MinReputationNeeded(AssaultGround)),
                    17542 => (ChallengeColdtoothGround, match.MinReputationNeeded(AssaultGround)),
                    _ => (WrongValue, 0),
                };
                _ = minReputation; // computed by vmangos, then overwritten by the neutral minimum below
                challenge = questChallenge;

                (uint actual, uint objective) = challenge != WrongValue
                    ? (match.ChallengeCounter(team, challenge), match.ChallengeGoal(team, challenge))
                    : (0u, 1u);

                if (actual >= objective)
                {
                    switch (challenge)
                    {
                        case ChallengeSoldierAir:
                            objectiveReached = match.IsAerialChallengeReady(team, AssaultAirBeaconSoldier);
                            message = team == Team.Alliance ? GossipBeaconEastAlliance : GossipBeaconEastHorde;
                            break;
                        case ChallengeLieutenantAir:
                            objectiveReached = match.IsAerialChallengeReady(team, AssaultAirBeaconLieutenant);
                            message = team == Team.Alliance ? GossipBeaconWestAlliance : GossipBeaconWestHorde;
                            break;
                        case ChallengeCommanderAir:
                            objectiveReached = match.IsAerialChallengeReady(team, AssaultAirBeaconCommander);
                            message = team == Team.Alliance ? GossipBeaconSnowfallAlliance : GossipBeaconSnowfallHorde;
                            break;
                        case ChallengeHideCavalry or ChallengeTamedCavalry:
                            objectiveReached = match.IsCavalryChallengeReady(team);
                            message = GossipAssaultCavalry;
                            break;
                        case ChallengeIrondeepGround or ChallengeColdtoothGround:
                            objectiveReached = match.IsGroundChallengeReady(team);
                            message = GossipAssaultGround;
                            break;
                    }
                }

                if (match.IsAerialChallengeReady(team, AssaultAirGlobalSoldier) && entry is NpcWingCommanderGuse or NpcWingCommanderSlidore)
                {
                    globalSoldier = true;
                    globalMessage = entry == NpcWingCommanderGuse ? GossipAssaultAirGuse : GossipAssaultAirSlidore;
                }

                if (match.IsAerialChallengeReady(team, AssaultAirGlobalLieutenant) && entry is NpcWingCommanderJeztor or NpcWingCommanderVipore)
                {
                    globalLieutenant = true;
                    globalMessage = entry == NpcWingCommanderJeztor ? GossipAssaultAirJeztor : GossipAssaultAirVipore;
                }

                if (match.IsAerialChallengeReady(team, AssaultAirGlobalCommander) && entry is NpcWingCommanderMulverick or NpcWingCommanderIchman)
                {
                    globalCommander = true;
                    globalMessage = entry == NpcWingCommanderMulverick ? GossipAssaultAirMulverick : GossipAssaultAirIchman;
                }
            }
        }

        if (entry == NpcFieldMarshalTeravaine)
        {
            message = GossipAssaultCavalry;
            objectiveReached = true;
            challenge = ChallengeIrondeepGround;
        }

        bool quartermaster = entry is NpcQuartermasterAlliance or NpcQuartermasterHorde;
        bool vendor = (creature.NpcFlags & (uint)NpcFlags.Vendor) != 0;
        if (objectiveReached)
        {
            if (entry != NpcFieldMarshalTeravaine)
            {
                showQuests = true;
            }

            AvScript.Emote(creature, AvScript.EmoteBow); // "Emote showing end of resources gathering"
            if (reputationRank(FactionFrostwolf) >= RankNeutral || reputationRank(FactionStormpike) >= RankNeutral)
            {
                items.Add(new AvGossipLine(IconChat, message, GossipActionInfoDef + (uint)challenge + 1));
                if (globalSoldier && entry is NpcWingCommanderGuse or NpcWingCommanderSlidore)
                {
                    items.Add(new AvGossipLine(IconChat, globalMessage, GossipActionInfoDef + AssaultAirGlobalSoldier + 1 + 50));
                }
                else if (globalLieutenant && entry is NpcWingCommanderJeztor or NpcWingCommanderVipore)
                {
                    items.Add(new AvGossipLine(IconChat, globalMessage, GossipActionInfoDef + AssaultAirGlobalLieutenant + 1 + 50));
                }
                else if (globalCommander && entry is NpcWingCommanderMulverick or NpcWingCommanderIchman)
                {
                    items.Add(new AvGossipLine(IconChat, globalMessage, GossipActionInfoDef + AssaultAirGlobalCommander + 1 + 50));
                }

                if (quartermaster)
                {
                    if (vendor)
                    {
                        items.Add(new AvGossipLine(IconVendor, GossipTextBrowseGoods, GossipActionTrade));
                    }

                    return new AvCollectorHello(false, false, showQuests, NpcTextQuartermaster, items);
                }

                return new AvCollectorHello(false, false, showQuests, 0, items);
            }

            return AvCollectorHello.Nothing; // not even neutral: no menu at all, as in vmangos
        }

        if ((creature.NpcFlags & (uint)NpcFlags.QuestGiver) == 0)
        {
            return AvCollectorHello.Nothing;
        }

        if (quartermaster)
        {
            if (vendor)
            {
                items.Add(new AvGossipLine(IconVendor, GossipTextBrowseGoods, GossipActionTrade));
            }

            return new AvCollectorHello(false, false, showQuests, NpcTextQuartermaster, items);
        }

        if (entry is NpcArchDruidRenferal or NpcPrimalistThurloga)
        {
            uint counter = match.ChallengeCounter(team, ChallengeBloodWorldBoss);
            bool alliance = team == Team.Alliance;
            (uint option, uint text) = counter >= 160
                ? (2u, alliance ? GossipRenferalBoss3 : GossipThurlogaBoss3)
                : counter >= 100
                    ? (1u, alliance ? GossipRenferalBoss2 : GossipThurlogaBoss2)
                    : (0u, alliance ? GossipRenferalBoss1 : GossipThurlogaBoss1);
            items.Add(new AvGossipLine(IconChat, text, GossipActionInfoDef + ChallengeBloodWorldBoss + 200 + option));
        }

        return new AvCollectorHello(false, false, true, 0, items);
    }

    /// <summary>
    /// A wing commander away from his post sets off when a player of his side speaks to him (battleground_alterac.cpp:2742-2760, 2768-2786):
    /// his escort starts, he can be attacked, runs, takes the player's faction and stops giving quests.
    /// </summary>
    private static void SetOff(Creature creature, Player player, bool castVipore)
    {
        if (creature.AI is not AvEventAI escort)
        {
            return;
        }

        if (castVipore)
        {
            creature.System?.CastSpell(creature, SpellViporeSetOff, creature, triggered: false);
        }

        creature.FactionTemplate = WingCommanderEscortFaction;
        escort.Start(run: true);
        creature.UnitFlags = (creature.UnitFlags & ~(UnitFlags.Spawning | UnitFlags.ImmuneToPlayer)) | UnitFlags.Pvp;
        AvScript.SetWalk(creature, false);
        creature.FactionTemplate = player.FactionTemplate;
        creature.NpcFlags &= ~(uint)NpcFlags.QuestGiver;
    }

    /// <summary>
    /// GossipSelect_npc_AVBlood_collector (battleground_alterac.cpp:3360-3660) for a collector other than Murgot and Regzar. The beacon and the
    /// assault orders are given only when <paramref name="hasItem"/> says the player has none (HasItemCount(item, 1, true)).
    /// </summary>
    public static AvCollectorChoice Select(AlteracValley match, Creature creature, Team team, uint action, Func<uint, bool> hasItem)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(hasItem);
        if (action == GossipActionTrade)
        {
            return new AvCollectorChoice(false, true, 0, 0);
        }

        if (action < GossipActionInfoDef + 1)
        {
            return default;
        }

        uint entry = creature.Template.Entry;
        uint challenge = action - (GossipActionInfoDef + 1);
        bool close = false;
        if (challenge < ChallengeCount + 10)
        {
            close = true;
            int say = entry switch
            {
                NpcRamRiderCommander => SayRamRider,
                NpcWolfRiderCommander => SayWolfRider,
                NpcQuartermasterAlliance => SayPatrol,
                NpcQuartermasterHorde => SayReavers,
                NpcWingCommanderJeztor => SayJeztor,
                NpcWingCommanderGuse => SayGuse,
                NpcWingCommanderMulverick => SayMulverick,
                NpcWingCommanderIchman => SayIchman,
                NpcWingCommanderVipore => SayVipore,
                _ => 0,
            };
            AvScript.Say(creature, say);
            AvScript.Emote(creature, AvScript.EmoteShout);
        }

        uint give = 0;
        uint text = 0;
        switch (challenge)
        {
            case ChallengeSoldierAir:
                give = LaunchBeacon(match, creature, team, AssaultAirBeaconSoldier, team == Team.Horde ? ItemBeaconGuse : ItemBeaconVipore, hasItem);
                break;
            case ChallengeLieutenantAir:
                give = LaunchBeacon(match, creature, team, AssaultAirBeaconLieutenant, team == Team.Horde ? ItemBeaconJeztor : ItemBeaconSlidore, hasItem);
                break;
            case ChallengeCommanderAir:
                give = LaunchBeacon(match, creature, team, AssaultAirBeaconCommander, team == Team.Horde ? ItemBeaconMulverick : ItemBeaconIchman, hasItem);
                break;
            case AssaultAirGlobalSoldier + 50 or AssaultAirGlobalLieutenant + 50 or AssaultAirGlobalCommander + 50:
            {
                int assault = (int)challenge - 50;
                match.ResetAerialChallenge(team, assault);
                creature.NpcFlags |= (uint)NpcFlags.QuestGiver;
                match.SetPlayerGoStatus(team, assault, true);
                break;
            }

            case ChallengeHideCavalry or ChallengeTamedCavalry:
                match.ResetCavalryChallenge(team);
                creature.NpcFlags |= (uint)NpcFlags.QuestGiver;
                match.SetPlayerGoStatus(team, AssaultCavalry, true);
                if (creature.AI is AvEventAI escort)
                {
                    escort.Start(run: true);
                    creature.UnitFlags = (creature.UnitFlags & ~(UnitFlags.Spawning | UnitFlags.ImmuneToPlayer)) | UnitFlags.Pvp;
                }

                break;
            case ChallengeIrondeepGround or ChallengeColdtoothGround:
            {
                match.ResetGroundChallenge(team);
                creature.NpcFlags |= (uint)NpcFlags.QuestGiver;
                match.SetPlayerGoStatus(team, AssaultGround, true);
                uint orders = team == Team.Horde ? ItemAssaultOrdersFrostwolf : ItemAssaultOrdersStormpike;
                give = hasItem(orders) ? 0 : orders;
                break;
            }

            case ChallengeCount + 12:
                text = 6732;
                break;
            case ChallengeCount + 13:
                text = 6733;
                break;
            case ChallengeCount + 14:
                text = 6731;
                break;
            case ChallengeBloodWorldBoss + 199:
                text = entry == NpcPrimalistThurloga ? 6098u : 6175u;
                break;
            case ChallengeBloodWorldBoss + 200:
                text = entry == NpcPrimalistThurloga ? 6099u : 6176u;
                break;
            case ChallengeBloodWorldBoss + 201:
                text = entry == NpcPrimalistThurloga ? 6100u : 6177u;
                break;
        }

        return new AvCollectorChoice(close, false, text, give);
    }

    private static uint LaunchBeacon(AlteracValley match, Creature creature, Team team, int assault, uint beacon, Func<uint, bool> hasItem)
    {
        match.ResetAerialChallenge(team, assault);
        creature.NpcFlags |= (uint)NpcFlags.QuestGiver;
        match.SetPlayerGoStatus(team, assault, true);
        return hasItem(beacon) ? 0 : beacon;
    }

    /// <summary>
    /// The quest scripts once a quest was rewarded (vmangos pQuestRewardedNPC): the world boss summoner sets off when the offering made the
    /// world boss assault (<paramref name="worldBossLaunched"/>, QuestComplete_npc_AVBlood_collector :2541-2554), and a troops chief shouts
    /// at any turn-in of an item and leads the attack for its assault orders (QuestComplete_AV_npc_troops_chief).
    /// </summary>
    public static void QuestRewarded(Creature giver, Team team, uint questId, uint requiredItem, uint requiredCount, bool worldBossLaunched)
    {
        ArgumentNullException.ThrowIfNull(giver);
        if (worldBossLaunched && giver.AI is AvEventAI summoner)
        {
            summoner.Start(run: true);
            giver.UnitFlags = (giver.UnitFlags & ~(UnitFlags.Spawning | UnitFlags.ImmuneToPlayer)) | UnitFlags.Pvp;
        }

        if (giver.AI is AvTroopsChiefAI chief && requiredItem != 0 && requiredCount != 0)
        {
            AvScript.Emote(giver, AvScript.EmoteShout);
            if (questId is QuestTroopsOrderHorde or QuestTroopsOrderAlliance)
            {
                chief.LaunchAttack(team);
            }
        }
    }
}
