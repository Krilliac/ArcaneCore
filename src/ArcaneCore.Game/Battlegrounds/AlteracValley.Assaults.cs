namespace ArcaneCore.Game.Battlegrounds;

/// <summary>
/// The match's side of the Alterac Valley assault scripts (vmangos scripts/battlegrounds/battleground_alterac.cpp:1230-4500): the creatures,
/// objects, items, spells, texts and gossip lines the scripts use, and the event and random sources they take from the match. The scripts
/// themselves (the escorted troops and cavalry, the wing commanders and war riders, the beacons, the world bosses and their summoners, the
/// collectors' menus) are in <c>Battlegrounds/AlteracValleyScripts</c>.
/// </summary>
public sealed partial class AlteracValley
{
    // ---- creatures (battleground_alterac.cpp:1282-1324) ----
    public const uint NpcWingCommanderMulverick = 13181;
    public const uint NpcWingCommanderJeztor = 13180;
    public const uint NpcWingCommanderGuse = 13179;
    public const uint NpcWingCommanderSlidore = 13438;
    public const uint NpcWingCommanderIchman = 13437;
    public const uint NpcWingCommanderVipore = 13439;
    public const uint NpcWarRiderMulverick = 14945;
    public const uint NpcWarRiderJeztor = 14944;
    public const uint NpcWarRiderGuse = 14943;
    public const uint NpcGryphonSlidore = 14946;
    public const uint NpcGryphonIchman = 14947;
    public const uint NpcGryphonVipore = 14948;
    public const uint NpcWarRider = 13178;
    public const uint NpcAerieGryphon = 13161;
    public const uint NpcWolfRider = 13440;
    public const uint NpcWolfRiderCommander = 13441;
    public const uint NpcRamRider = 13576;
    public const uint NpcRamRiderCommander = 13577;
    public const uint NpcQuartermasterHorde = 12097;
    public const uint NpcWarmasterGarrick = 13449;
    public const uint NpcFrostwolfReaver = 13528;
    public const uint NpcSeasonedReaver = 13529;
    public const uint NpcVeteranReaver = 13530;
    public const uint NpcChampionReaver = 13531;
    public const uint NpcQuartermasterAlliance = 12096;
    public const uint NpcFieldMarshalTeravaine = 13446;
    public const uint NpcStormpikeCommando = 13524;
    public const uint NpcSeasonedCommando = 13525;
    public const uint NpcVeteranCommando = 13526;
    public const uint NpcChampionCommando = 13527;
    public const uint NpcPrimalistThurloga = 13236;
    public const uint NpcFrostwolfShaman = 13284;
    public const uint NpcArchDruidRenferal = 13442;
    public const uint NpcDruidOfTheGrove = 13443;
    public const uint NpcLokholar = 13256;
    public const uint NpcIvus = 13419;

    // ---- objects ----
    /// <summary>The Altar of Summoning Thurloga places for the Ice Lord (178465) and the Circle of Calling Renferal places for Ivus (178670).</summary>
    public const uint GameObjectInvocationHorde = 178465;
    public const uint GameObjectInvocationAlliance = 178670;
    public const uint GameObjectBeaconMulverick = 178549;
    public const uint GameObjectBeaconGuse = 178545;
    public const uint GameObjectBeaconJeztor = 178547;
    public const uint GameObjectBeaconIchman = 178726;
    public const uint GameObjectBeaconVipore = 178724;
    public const uint GameObjectBeaconSlidore = 178725;

    // ---- items (battleground_alterac.cpp:1250-1278) ----
    public const uint ItemBeaconMulverick = 17323;
    public const uint ItemBeaconGuse = 17324;
    public const uint ItemBeaconJeztor = 17325;
    public const uint ItemBeaconIchman = 17505;
    public const uint ItemBeaconVipore = 17506;
    public const uint ItemBeaconSlidore = 17507;
    public const uint ItemAssaultOrdersFrostwolf = 17442;
    public const uint ItemAssaultOrdersStormpike = 17353;

    /// <summary>The troops chiefs' "launch the attack" quests (QUEST_TROOPS_ORDER_H / _A).</summary>
    public const uint QuestTroopsOrderHorde = 6901;
    public const uint QuestTroopsOrderAlliance = 6846;

    /// <summary>The world-boss events (BattleGroundAV.h:288-289): a second Ivus or Ice Lord is refused while one lives.</summary>
    public const byte EventBossIvus = 102;
    public const byte EventBossLokholar = 103;

    /// <summary>The summoners' channel (AV_INVOCATION_SPELL 11206), removed from the players when the world boss comes.</summary>
    public const uint SpellInvocation = 11206;

    /// <summary>GOSSIP_ACTION_TRADE (ScriptedGossip.h:41): the quartermasters' vendor line.</summary>
    public const uint GossipActionTrade = 1;

    /// <summary>GOSSIP_TEXT_BROWSE_GOODS (ScriptedGossip.h:13), the broadcast text of the vendor line.</summary>
    public const uint GossipTextBrowseGoods = 3370;

    /// <summary>The random source the scripts draw their timers from (vmangos urand): the match's.</summary>
    public Random ScriptRandom => Ports.Random;

    /// <summary>
    /// av_world_boss_baseai's construction (battleground_alterac.cpp:3928-3941): a world boss of <paramref name="bossEvent"/> may only come once
    /// at a time. False when the event is already active (the newcomer is deleted); otherwise the event becomes active.
    /// </summary>
    public bool TryClaimWorldBoss(byte bossEvent)
    {
        if (IsActiveEvent(bossEvent, 0))
        {
            return false;
        }

        SpawnEvent(bossEvent, 0, spawn: true, forcedDespawn: true);
        return true;
    }

    /// <summary>av_world_boss_baseai::JustDied: the world boss's event goes (the "double world boss protection" is lifted).</summary>
    public void ReleaseWorldBoss(byte bossEvent) => SpawnEvent(bossEvent, 0, spawn: false, forcedDespawn: true);
}
