using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using Xunit;
using static ArcaneCore.Game.Tests.Battlegrounds.BgTestData;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>
/// The Alterac Valley mechanics beyond the node core, against vmangos BattleGroundAV.cpp and scripts/battlegrounds/battleground_alterac.cpp:
/// the turn-ins (armor scraps and their supply crates, the commander, boss and stable quests), the defender upgrade and the defender
/// events it changes, the quartermaster's menu, the collectors' challenge counters, the respawn stops, the landmine layers and experts,
/// the shredder owner check and the captains' and Snivvle's yells.
/// </summary>
public sealed class AlteracValleyUpgradeTests
{
    private static readonly ObjectGuid Murgot = ObjectGuid.WithEntry(HighGuid.Unit, AlteracValley.NpcMurgot, 900);

    private static (AlteracValley Bg, RecordingHost Host, RecordingPorts Ports) NewAv()
    {
        var host = new RecordingHost();
        var ports = new RecordingPorts();
        var bg = new AlteracValley(AlteracValleyTests.AvTemplate(), bracket: 0, instanceId: 301, clientInstanceId: 1, new BattlegroundOptions(),
            ports.ToPorts(host));
        return (bg, host, ports);
    }

    private static (AlteracValley Bg, RecordingHost Host, RecordingPorts Ports) Running()
    {
        (AlteracValley bg, RecordingHost host, RecordingPorts ports) = NewAv();
        bg.StartBattleground();
        bg.IncreaseInvitedCount(Team.Alliance);
        bg.IncreaseInvitedCount(Team.Horde);
        Assert.True(bg.AddPlayer(Alliance[0], Team.Alliance));
        Assert.True(bg.AddPlayer(Horde[0], Team.Horde));
        for (int i = 0; i < 400 && bg.Status != BattlegroundStatus.InProgress; i++)
        {
            Assert.True(bg.Update(1000));
        }

        Assert.Equal(BattlegroundStatus.InProgress, bg.Status);
        host.Events.Clear();
        host.SpawnModes.Clear();
        host.Says.Clear();
        ports.Casts.Clear();
        ports.Reputation.Clear();
        return (bg, host, ports);
    }

    private static void TurnIn(AlteracValley bg, ObjectGuid player, uint quest, int times, uint item = 0, uint count = 0)
    {
        for (int i = 0; i < times; i++)
        {
            bg.HandleQuestComplete(player, Murgot, quest, item, count);
        }
    }

    [Fact]
    public void ArmorScraps_CountTwentyEach_PileCratesAtEachHundred_AndClearThemAtFiveHundred()
    {
        var (bg, host, ports) = Running();

        TurnIn(bg, Alliance[0], AlteracValley.QuestAllianceScraps1, 1);
        Assert.Equal(20u, bg.ArmorResources(Team.Alliance));
        // The piles were cleared 30 s before the start (StartingEventThird), so the first turn-in's clear finds them empty already.
        Assert.True(bg.IsActiveEvent(AlteracValley.EventSupplies100, 2));
        Assert.DoesNotContain((AlteracValley.EventSupplies100, (byte)0, true, false), host.Events);
        Assert.Contains((Alliance[0], AlteracValley.FactionStormpike, 1), ports.Reputation);

        TurnIn(bg, Alliance[0], AlteracValley.QuestAllianceScraps2, 4);
        Assert.Equal(100u, bg.ArmorResources(Team.Alliance));
        Assert.Contains((AlteracValley.EventSupplies100, (byte)0, true, false), host.Events);
        Assert.Contains(host.Says, s => s.Text == "Great! Let's keep those supplies coming, people!" && !s.Yell && s.Creature == Murgot);

        TurnIn(bg, Alliance[0], AlteracValley.QuestAllianceScraps2, 15);
        Assert.Equal(400u, bg.ArmorResources(Team.Alliance));
        Assert.Contains((AlteracValley.EventSupplies200, (byte)0, true, false), host.Events);
        Assert.Contains((AlteracValley.EventSupplies300, (byte)0, true, true), host.Events);
        Assert.Contains((AlteracValley.EventSupplies400, (byte)0, true, true), host.Events);
        Assert.Equal(4, host.Says.Count);

        host.Events.Clear();
        TurnIn(bg, Alliance[0], AlteracValley.QuestAllianceScraps2, 5);
        Assert.Equal(500u, bg.ArmorResources(Team.Alliance));
        Assert.Equal(4, host.Says.Count); // no line at a five hundred
        foreach (byte pile in new[] { AlteracValley.EventSupplies100, AlteracValley.EventSupplies200, AlteracValley.EventSupplies300, AlteracValley.EventSupplies400 })
        {
            Assert.Contains((pile, (byte)2, true, false), host.Events);
        }

        Assert.Equal(0u, bg.ArmorResources(Team.Horde));
        Assert.Equal(25, ports.Reputation.Count(r => r.Faction == AlteracValley.FactionStormpike));
    }

    [Fact]
    public void TheUpgrade_NeedsFiveHundredScraps_BuffsTheTeam_AndRepopulatesItsNodesWithSeasonedDefenders()
    {
        var (bg, host, ports) = Running();

        // Below the threshold the reference sets the level from 0 resources (basic) and still thanks the player.
        TurnIn(bg, Alliance[0], AlteracValley.QuestAllianceScraps1, 24);
        bg.UpgradeArmor(Murgot, Alliance[0]);
        Assert.Equal(AlteracValley.TroopsBasic, bg.ReinforcementLevel(Team.Alliance));
        Assert.Contains(host.Says, s => s.Text == "Thanks for the supplies, %s" && s.Player == Alliance[0]);
        Assert.DoesNotContain(ports.Casts, c => c.Spell == AlteracValley.SpellSeasonedUnits);

        TurnIn(bg, Alliance[0], AlteracValley.QuestAllianceScraps1, 1);
        host.Events.Clear();
        host.SpawnModes.Clear();
        bg.UpgradeArmor(Murgot, Alliance[0]);

        Assert.Equal(AlteracValley.TroopsSeasoned, bg.ReinforcementLevel(Team.Alliance));
        Assert.Contains((Alliance[0], AlteracValley.SpellSeasonedUnits), ports.Casts);
        Assert.DoesNotContain(ports.Casts, c => c.Player == Horde[0] && c.Spell == AlteracValley.SpellSeasonedUnits);
        Assert.Contains(host.Says, s => s.Text == "Seasoned units are entering the battle!" && s.Yell);

        // The Stormpike graveyard (node 1): its defenders become type 1 (event 16, Alliance * 4 + 1), forced to respawn.
        Assert.Contains(((byte)(AlteracValley.NodeCount + AlteracValley.NodeStormpikeGrave), (byte)1, true, true), host.Events);
        Assert.Contains(((byte)(AlteracValley.NodeCount + AlteracValley.NodeStormpikeGrave), (byte)1, BattlegroundSpawnMode.RespawnForced), host.SpawnModes);

        // A bunker (node 7): base defenders (22, 1), already active, and the tower defenders at type 1 (30, 1).
        Assert.Contains(((byte)(AlteracValley.NodeCount + AlteracValley.NodeDunBaldarSouth), (byte)1, BattlegroundSpawnMode.RespawnForced), host.SpawnModes); // already active: no respawn event
        Assert.Contains(((byte)(23 + AlteracValley.NodeDunBaldarSouth), (byte)1, true, true), host.Events);

        // Horde nodes are not repopulated.
        Assert.DoesNotContain(host.Events, e => e.E1 == AlteracValley.NodeCount + AlteracValley.NodeFrostwolfGrave);

        // Once seasoned, a second upgrade at 500 scraps does nothing (needs 1000) and the level falls back to basic (the reference quirk).
        bg.UpgradeArmor(Murgot, Alliance[0]);
        Assert.Equal(AlteracValley.TroopsBasic, bg.ReinforcementLevel(Team.Alliance));
    }

    [Fact]
    public void AnAssault_StopsThePreviousDefenders_AndTheCaptureSpawnsTheNewOwnersAtItsScrapLevel()
    {
        var (bg, host, _) = Running();
        TurnIn(bg, Horde[0], AlteracValley.QuestHordeScraps1, 50); // 1000: veteran defenders for the Horde
        host.SpawnModes.Clear();
        host.Events.Clear();

        var banner = new BattlegroundObjectUse(178925, AlteracValley.NodeStoneheartGrave, (byte)((0 * 2) + AlteracValley.PointControlled), true);
        bg.EventPlayerClickedOnFlag(Horde[0], banner);

        // The Alliance defenders of the graveyard (17, 0 * 4 + 0) stop respawning; nothing despawns them.
        Assert.Contains(((byte)(AlteracValley.NodeCount + AlteracValley.NodeStoneheartGrave), (byte)0, BattlegroundSpawnMode.RespawnStop), host.SpawnModes);
        Assert.DoesNotContain(host.Events, e => e.E1 == AlteracValley.NodeCount + AlteracValley.NodeStoneheartGrave);

        Tick(bg, AlteracValley.CaptureTimeMs, 1000);

        Assert.Equal(1, bg.NodeOwner(AlteracValley.NodeStoneheartGrave));
        Assert.Contains(((byte)(AlteracValley.NodeCount + AlteracValley.NodeStoneheartGrave), (byte)((1 * 4) + 2), true, true), host.Events);
        Assert.Equal(2, bg.DefenderType(1));
    }

    [Fact]
    public void Commanders_And_ExplosivesExperts_StopRespawning_Lieutenants_DoNot()
    {
        var (bg, host, _) = Running();

        bg.HandleKillUnit(13319, AlteracValley.EventCommanderAllianceDuffy, Horde[0]);
        bg.HandleKillUnit(13153, AlteracValley.EventExplosivesExpertHorde, Alliance[0]);
        bg.HandleKillUnit(13153, AlteracValley.EventLieutenantHorde, Alliance[0]);

        Assert.Equal(
            [(AlteracValley.EventCommanderAllianceDuffy, (byte)0, BattlegroundSpawnMode.RespawnStop), (AlteracValley.EventExplosivesExpertHorde, (byte)0, BattlegroundSpawnMode.RespawnStop)],
            host.SpawnModes);
        Assert.Contains(7368u, host.QuestsCompleted);
    }

    [Fact]
    public void ALandmineLayer_StopsItsMinesComingBack_AnExpert_RemovesThem()
    {
        var (bg, host, _) = Running();
        Assert.True(bg.IsActiveEvent(AlteracValley.EventLandminesAlliance, 0));

        bg.HandleKillUnit(AlteracValley.NpcLandminesLayerAlliance, BattlegroundConstants.EventNone, Horde[0]);
        Assert.True(bg.IsActiveEvent(AlteracValley.EventLandminesAlliance, 1));
        Assert.Empty(host.Events); // m_activeEvents is set without spawning anything

        bg.HandleKillUnit(AlteracValley.NpcLandminesExpertHorde, BattlegroundConstants.EventNone, Alliance[0]);
        Assert.Equal([(AlteracValley.EventLandminesHorde, (byte)0)], host.RemovedEventObjects);
    }

    [Fact]
    public void ATeamHasOneShredder_ASecondSummonFails_WhileTheFirstSummonerStillControlsOne()
    {
        var (bg, host, _) = Running();

        Assert.Null(bg.CheckSpellCast(Alliance[0], AlteracValley.SpellSummonShredderAlliance));
        Assert.Equal(Alliance[0], bg.ShredderOwner(Team.Alliance));

        host.Charms[Alliance[0]] = AlteracValley.NpcShredderAlliance;
        Assert.Equal(AlteracValley.CastFailedSpellUnavailable, bg.CheckSpellCast(Alliance[1], AlteracValley.SpellSummonShredderAlliance));
        Assert.Equal(Alliance[0], bg.ShredderOwner(Team.Alliance));
        Assert.Null(bg.CheckSpellCast(Horde[0], AlteracValley.SpellSummonShredderHorde)); // the other team has its own

        host.Charms.Remove(Alliance[0]);
        Assert.Null(bg.CheckSpellCast(Alliance[1], AlteracValley.SpellSummonShredderAlliance));
        Assert.Equal(Alliance[1], bg.ShredderOwner(Team.Alliance));
        Assert.Null(bg.CheckSpellCast(Alliance[0], 133)); // any other spell
    }

    [Fact]
    public void Snivvle_YellsSeventySecondsIn_AndTheCaptains_YellWithTheirBuffs()
    {
        var (bg, host, ports) = NewAv();
        bg.Update(69_000);
        Assert.Empty(host.EventYells);
        bg.Update(1_000);
        Assert.Equal([(AlteracValley.EventSnivvle, AlteracValley.TextSnivvle)], host.EventYells);

        (bg, host, ports) = Running();
        Tick(bg, (6 * 60_000) + 2_000, 1000);
        Assert.Contains((AlteracValley.EventCaptainHorde, AlteracValley.TextHordeCaptainBuff), host.EventYells);
        Assert.Contains((AlteracValley.EventCaptainAlliance, AlteracValley.TextAllianceCaptainBuff), host.EventYells);
        Assert.Contains((Horde[0], AlteracValley.SpellHordeCaptainBuff), ports.Casts);
    }

    [Fact]
    public void CommanderAndBossQuests_CountAndYellAtTheirGoals_AndTheCollectorsFillTheChallenges()
    {
        var (bg, host, ports) = Running();

        TurnIn(bg, Alliance[0], AlteracValley.QuestAllianceCommander1, 89, 17326, 1);
        Assert.Empty(host.Says);
        TurnIn(bg, Alliance[0], AlteracValley.QuestAllianceCommander1, 1, 17326, 1);
        Assert.Equal("Soldiers of Stormpike, come to my aid! The beacon must be planted.", Assert.Single(host.Says).Text);
        Assert.Equal(90u, bg.TeamQuestStatus(Team.Alliance, 1));
        Assert.Equal(90u, bg.ChallengeCounter(Team.Alliance, AlteracValley.ChallengeSoldierAir));
        Assert.True(bg.IsAerialChallengeReady(Team.Alliance, AlteracValley.AssaultAirBeaconSoldier));
        bg.ResetAerialChallenge(Team.Alliance, AlteracValley.AssaultAirGlobalSoldier); // a global assault resets nothing
        Assert.True(bg.IsAerialChallengeReady(Team.Alliance, AlteracValley.AssaultAirGlobalSoldier));
        bg.ResetAerialChallenge(Team.Alliance, AlteracValley.AssaultAirBeaconSoldier);
        Assert.False(bg.IsAerialChallengeReady(Team.Alliance, AlteracValley.AssaultAirBeaconSoldier));

        // The five-item boss quest counts five and gives five reputation; 40 of them complete the world-boss offering, which resets it and
        // sets the team's go flag.
        ports.Reputation.Clear();
        TurnIn(bg, Horde[0], AlteracValley.QuestHordeBoss1, 40, 17306, 5);
        Assert.Equal(200u, bg.TeamQuestStatus(Team.Horde, 4));
        Assert.Contains(host.Says, s => s.Yell && s.Text.StartsWith("Soldiers of Frostwolf, come to my aid! The Ice Lord", StringComparison.Ordinal));
        Assert.Equal(40, ports.Reputation.Count(r => r.Player == Horde[0] && r.Faction == AlteracValley.FactionFrostwolf && r.Amount == 5));
        Assert.Equal(0u, bg.ChallengeCounter(Team.Horde, AlteracValley.ChallengeBloodWorldBoss));
        Assert.True(bg.PlayerGoStatus(Team.Horde, AlteracValley.AssaultWorldBoss));
        Assert.False(bg.PlayerGoStatus(Team.Alliance, AlteracValley.AssaultWorldBoss));

        // Ground: either mine's supplies; the Alliance needs 280 Irondeep or 70 Coldtooth.
        TurnIn(bg, Alliance[0], AlteracValley.QuestAllianceOtherMine, 7, 17542, 10);
        Assert.True(bg.IsGroundChallengeReady(Team.Alliance));
        bg.ResetGroundChallenge(Team.Alliance);
        Assert.False(bg.IsGroundChallengeReady(Team.Alliance));
    }

    [Fact]
    public void BeforeTheStart_ACollectorStillCountsTheTurnIn_ButTheMatchCountsNothing()
    {
        // vmangos: BattleGroundAV::HandleQuestComplete returns unless STATUS_IN_PROGRESS (BattleGroundAV.cpp:505-506), but the collector's own
        // quest-rewarded script, QuestComplete_npc_AVBlood_collector (battleground_alterac.cpp:2477-2545), only asks that the player be in AV.
        var (bg, host, _) = NewAv();
        bg.StartBattleground();
        bg.IncreaseInvitedCount(Team.Alliance);
        Assert.True(bg.AddPlayer(Alliance[0], Team.Alliance));
        Assert.NotEqual(BattlegroundStatus.InProgress, bg.Status);

        TurnIn(bg, Alliance[0], AlteracValley.QuestAllianceCommander1, 3, 17326, 1);
        TurnIn(bg, Alliance[0], AlteracValley.QuestAllianceScraps1, 1, 17422, 20);

        Assert.Equal(3u, bg.ChallengeCounter(Team.Alliance, AlteracValley.ChallengeSoldierAir));
        Assert.Equal(0u, bg.TeamQuestStatus(Team.Alliance, 1));
        Assert.Equal(0u, bg.ArmorResources(Team.Alliance));
        Assert.Empty(host.Says);
    }

    [Fact]
    public void TamedMounts_FillTheStables_AndTheCavalryNeedsHidesAndMounts()
    {
        var (bg, host, _) = Running();

        TurnIn(bg, Horde[0], AlteracValley.QuestHordeRiderTame, 1);
        Assert.True(bg.IsActiveEvent((byte)(AlteracValley.EventTamed05 + 1), 2)); // empty since the start (StartingEventThird)
        TurnIn(bg, Horde[0], AlteracValley.QuestHordeRiderTame, 4);
        Assert.Contains(((byte)(AlteracValley.EventTamed05 + 1), (byte)0, true, false), host.Events);
        Assert.Contains(host.Says, s => s.Text == "Thanks for the supplies, %s" && s.Player == Horde[0]);
        TurnIn(bg, Horde[0], AlteracValley.QuestHordeRiderTame, 20);
        Assert.Contains(host.Says, s => s.Text == "The stables are filled up!" && s.Yell);
        Assert.Equal(25u, bg.ChallengeCounter(Team.Horde, AlteracValley.ChallengeTamedCavalry));
        Assert.False(bg.IsCavalryChallengeReady(Team.Horde));

        TurnIn(bg, Horde[0], AlteracValley.QuestHordeRiderHide, 25, 17643, 1);
        Assert.True(bg.IsCavalryChallengeReady(Team.Horde));
        host.Events.Clear();
        bg.ResetCavalryChallenge(Team.Horde);
        Assert.False(bg.IsCavalryChallengeReady(Team.Horde));
        Assert.Contains(((byte)(AlteracValley.EventTamed20 + 1), (byte)2, true, true), host.Events);
    }

    [Fact]
    public void TheQuartermaster_OffersTheNextUpgradeAnswer_AndTheUpgradeToAnHonoredPlayerWithEnoughScraps()
    {
        var (bg, _, ports) = Running();
        Func<uint, int> neutral = _ => AlteracValley.RankNeutral;
        Func<uint, int> honored = faction => faction == AlteracValley.FactionStormpike ? AlteracValley.RankHonored : AlteracValley.RankNeutral;

        AvGossipMenu menu = bg.QuartermasterMenu(AlteracValley.NpcMurgot, Alliance[0], honored)!;
        Assert.Equal(6073u, menu.NpcTextId);
        AvGossipItem next = Assert.Single(menu.Items);
        Assert.Equal(AlteracValley.GossipItemNextUpgrade, next.BroadcastTextId);
        Assert.Equal(6784u, bg.QuartermasterSelect(Murgot, Alliance[0], next.Action)); // "I barely have any supplies"

        TurnIn(bg, Alliance[0], AlteracValley.QuestAllianceScraps1, 23); // 460: "almost enough to upgrade to seasoned"
        Assert.Equal(6778u, bg.QuartermasterSelect(Murgot, Alliance[0], bg.QuartermasterMenu(AlteracValley.NpcMurgot, Alliance[0], honored)!.Items[0].Action));

        TurnIn(bg, Alliance[0], AlteracValley.QuestAllianceScraps1, 2);
        Assert.Single(bg.QuartermasterMenu(AlteracValley.NpcMurgot, Alliance[0], neutral)!.Items); // not honored
        menu = bg.QuartermasterMenu(AlteracValley.NpcMurgot, Alliance[0], honored)!;
        Assert.Equal(AlteracValley.GossipItemUpgradeSeasoned, menu.Items[1].BroadcastTextId);
        Assert.Equal(0u, bg.QuartermasterSelect(Murgot, Alliance[0], menu.Items[1].Action));
        Assert.Equal(AlteracValley.TroopsSeasoned, bg.ReinforcementLevel(Team.Alliance));
        Assert.Contains((Alliance[0], AlteracValley.SpellSeasonedUnits), ports.Casts);
        Assert.Equal(6217u, bg.QuartermasterMenu(AlteracValley.NpcRegzar, Alliance[0], honored)!.NpcTextId);

        Assert.Null(bg.QuartermasterMenu(12096, Alliance[0], honored));
    }
}
