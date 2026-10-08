using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using Xunit;
using static ArcaneCore.Game.Tests.Battlegrounds.BgTestData;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>
/// Alterac Valley against vmangos BattleGroundAV.cpp/.h: the initial owners, assaults and defenses of graveyards and towers, the five minute
/// capture and tower destruction, reinforcements (deaths, towers, captains, mines), the general kill that ends the match, the end-of-match
/// bonuses, graveyards and world states.
/// </summary>
public sealed class AlteracValleyTests
{
    /// <summary>The vmangos row of battleground_template id 1 (20 to 40 per team, levels 51 to 60, start locations 611/610).</summary>
    public static BattlegroundTemplate AvTemplate() => new()
    {
        Type = BattlegroundType.AlteracValley,
        MapId = 30,
        Name = "Alterac Valley",
        MinPlayersPerTeam = 1,
        MaxPlayersPerTeam = 40,
        MinLevel = 51,
        MaxLevel = 60,
        AllianceWinSpell = 24955,
        AllianceLoseSpell = 24954,
        HordeWinSpell = 24955,
        HordeLoseSpell = 24954,
    };

    private static (AlteracValley Bg, RecordingHost Host, RecordingPorts Ports) NewAv(bool weekend = false)
    {
        var host = new RecordingHost();
        var ports = new RecordingPorts { Weekend = weekend };
        var bg = new AlteracValley(AvTemplate(), bracket: 0, instanceId: 301, clientInstanceId: 1, new BattlegroundOptions(), ports.ToPorts(host));
        return (bg, host, ports);
    }

    private static void Start(AlteracValley bg)
    {
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
    }

    /// <summary>A banner of <paramref name="node"/> in its current (owner, state) event.</summary>
    private static BattlegroundObjectUse Banner(AlteracValley bg, int node)
        => new(178925, (byte)node, (byte)((bg.NodeOwner(node) * 2) + bg.NodeState(node)), WithinTenYards: true);

    [Fact]
    public void Reset_GivesTheAllianceTheNorth_TheHordeTheSouth_SnowfallToNobody_And600Reinforcements()
    {
        var (bg, _, _) = NewAv();
        for (int node = AlteracValley.NodeFirstAidStation; node <= AlteracValley.NodeStoneheartGrave; node++)
        {
            Assert.Equal(0, bg.NodeOwner(node));
            Assert.False(bg.IsTower(node));
        }

        for (int node = AlteracValley.NodeDunBaldarSouth; node <= AlteracValley.NodeStoneheartBunker; node++)
        {
            Assert.Equal(0, bg.NodeOwner(node));
            Assert.True(bg.IsTower(node));
        }

        for (int node = AlteracValley.NodeIcebloodTower; node <= AlteracValley.NodeFrostwolfWestTower; node++)
        {
            Assert.Equal(1, bg.NodeOwner(node));
        }

        Assert.Equal(AlteracValley.TeamNeutral, bg.NodeOwner(AlteracValley.NodeSnowfallGrave));
        Assert.Equal(AlteracValley.InitialPoints, bg.TeamScore(Team.Alliance));
        Assert.Equal(AlteracValley.InitialPoints, bg.TeamScore(Team.Horde));
        Assert.True(bg.IsActiveEvent(AlteracValley.NodeIcebloodTower, (1 * 2) + AlteracValley.PointControlled));
        Assert.Equal(AlteracValley.TeamNeutral, bg.MineOwner(0));
    }

    [Fact]
    public void ATowerAssaultedAndLeftAlone_IsDestroyedAfterFiveMinutes_CostingItsOwner75Reinforcements()
    {
        var (bg, host, ports) = NewAv();
        Start(bg);

        bg.EventPlayerClickedOnFlag(Alliance[0], Banner(bg, AlteracValley.NodeTowerPoint));
        Assert.Equal(0, bg.NodeOwner(AlteracValley.NodeTowerPoint));
        Assert.Equal(AlteracValley.PointAssaulted, bg.NodeState(AlteracValley.NodeTowerPoint));
        Assert.Equal(AlteracValley.CaptureTimeMs, bg.NodeTimer(AlteracValley.NodeTowerPoint));
        Assert.Equal(1u, ((AvScore)bg.ScoreOf(Alliance[0])!).TowersAssaulted);
        Assert.Contains(host.Yells, y => y.Text == BattlegroundTexts.LangAvTowerAssaulted && y.Arg1 == BattlegroundTexts.LangAvNodeTowerPoint);
        Assert.Contains((AlteracValley.NodeWorldState(AlteracValley.NodeTowerPoint, AlteracValley.PointAssaulted, 0), 1u), host.WorldStates);
        Assert.Contains((AlteracValley.NodeWorldState(AlteracValley.NodeTowerPoint, AlteracValley.PointControlled, 1), 0u), host.WorldStates);
        Assert.Equal(AlteracValley.SoundAllianceAssaults, host.Sounds[^1]);
        Assert.Contains(((byte)AlteracValley.NodeTowerPoint, (byte)0, true, true, 1u), host.DelayedEvents);

        Tick(bg, AlteracValley.CaptureTimeMs - 1000, 1000);
        Assert.Equal(AlteracValley.PointAssaulted, bg.NodeState(AlteracValley.NodeTowerPoint));
        bg.Update(1000);

        // Destroyed: the Alliance owns the ruin for good, the Horde loses 75, the Alliance gets the reputation and honor of a tower.
        Assert.Equal(AlteracValley.PointControlled, bg.NodeState(AlteracValley.NodeTowerPoint));
        Assert.Equal(0, bg.NodeTotalOwner(AlteracValley.NodeTowerPoint));
        Assert.Equal(AlteracValley.InitialPoints - AlteracValley.ResourcesLostPerTower, bg.TeamScore(Team.Horde));
        Assert.Contains((AlteracValley.WorldStateHordeScore, (uint)(AlteracValley.InitialPoints - AlteracValley.ResourcesLostPerTower)), host.WorldStates);
        Assert.Contains((Alliance[0], AlteracValley.FactionStormpike, 12), ports.Reputation);
        Assert.Equal(bg.BonusHonorFromKill(2), ports.Honor[Alliance[0]]);
        Assert.Contains(7102u, host.QuestsCompleted);
        Assert.Contains(host.Yells, y => y.Text == BattlegroundTexts.LangAvTowerTaken);

        // The ruin's banner event is (node, Alliance * 2 + controlled); vmangos has no clickable banner in it, the rules do not refuse a click.
        Assert.True(bg.IsActiveEvent(AlteracValley.NodeTowerPoint, AlteracValley.PointControlled));
    }

    [Fact]
    public void TheOwnerDefendsAnAssaultedGraveyard_InTime()
    {
        var (bg, host, _) = NewAv();
        Start(bg);
        bg.EventPlayerClickedOnFlag(Horde[0], Banner(bg, AlteracValley.NodeStormpikeGrave));
        Assert.Equal(1, bg.NodeOwner(AlteracValley.NodeStormpikeGrave));
        Tick(bg, 60_000, 1000);

        bg.EventPlayerClickedOnFlag(Alliance[0], Banner(bg, AlteracValley.NodeStormpikeGrave));
        Assert.Equal(0, bg.NodeOwner(AlteracValley.NodeStormpikeGrave));
        Assert.Equal(AlteracValley.PointControlled, bg.NodeState(AlteracValley.NodeStormpikeGrave));
        Assert.Equal(0u, bg.NodeTimer(AlteracValley.NodeStormpikeGrave));
        Assert.Equal(1u, ((AvScore)bg.ScoreOf(Alliance[0])!).GraveyardsDefended);
        Assert.Contains(host.Yells, y => y.Text == BattlegroundTexts.LangAvGraveDefended);
        Assert.Equal(AlteracValley.SoundAllianceGood, host.Sounds[^1]);
    }

    [Fact]
    public void AnAssaultedGraveyard_ChangesHands_AndSnowfallsFirstClickIsAnAssaultForEitherSide()
    {
        var (bg, _, _) = NewAv();
        Start(bg);
        bg.EventPlayerClickedOnFlag(Alliance[0], Banner(bg, AlteracValley.NodeIcebloodGrave));
        Tick(bg, AlteracValley.CaptureTimeMs, 1000);
        Assert.Equal(0, bg.NodeOwner(AlteracValley.NodeIcebloodGrave));
        Assert.Equal(0, bg.NodeTotalOwner(AlteracValley.NodeIcebloodGrave));
        Assert.Equal(AlteracValley.InitialPoints, bg.TeamScore(Team.Horde));      // a graveyard costs no reinforcements

        // Snowfall: Horde clicks the neutral banner, the Alliance clicks the Horde-assaulted banner: still an assault (neutral total owner).
        bg.EventPlayerClickedOnFlag(Horde[0], Banner(bg, AlteracValley.NodeSnowfallGrave));
        Assert.Equal(1, bg.NodeOwner(AlteracValley.NodeSnowfallGrave));
        bg.EventPlayerClickedOnFlag(Alliance[0], Banner(bg, AlteracValley.NodeSnowfallGrave));
        Assert.Equal(0, bg.NodeOwner(AlteracValley.NodeSnowfallGrave));
        Assert.Equal(AlteracValley.PointAssaulted, bg.NodeState(AlteracValley.NodeSnowfallGrave));
        Assert.Equal(2u, ((AvScore)bg.ScoreOf(Alliance[0])!).GraveyardsAssaulted);
    }

    [Fact]
    public void Deaths_CostAReinforcementEach_ExceptSpiritOfRedemption_AndNeverBelowZero_WithoutEndingTheMatch()
    {
        var (bg, _, ports) = NewAv();
        Start(bg);
        bg.HandleKillPlayer(Horde[0], Alliance[0]);
        Assert.Equal(AlteracValley.InitialPoints - 1, bg.TeamScore(Team.Horde));

        ports.Auras.Add((Horde[0], BattlegroundConstants.SpellSpiritOfRedemption));
        bg.HandleKillPlayer(Horde[0], Alliance[0]);
        Assert.Equal(AlteracValley.InitialPoints - 1, bg.TeamScore(Team.Horde));
        ports.Auras.Clear();

        for (int i = 0; i < 700; i++)
        {
            bg.HandleKillPlayer(Horde[0], Alliance[0]);
        }

        // vmangos (Nostalrius) removed the reinforcement-zero victory (BattleGroundAV.cpp:782-787).
        Assert.Equal(0, bg.TeamScore(Team.Horde));
        Assert.Equal(BattlegroundStatus.InProgress, bg.Status);
    }

    [Fact]
    public void KillingTheHordeCaptain_Costs100Once_AndKillingTheGeneral_EndsTheMatch()
    {
        var (bg, host, ports) = NewAv();
        Start(bg);
        bg.HandleKillUnit(11947, AlteracValley.EventCaptainHorde, Alliance[0]);
        Assert.Equal(AlteracValley.InitialPoints - AlteracValley.ResourcesLostPerCaptain, bg.TeamScore(Team.Horde));
        Assert.True(bg.IsActiveEvent(AlteracValley.EventCaptainDeadHorde, 0));
        Assert.Contains((Alliance[0], AlteracValley.FactionStormpike, 125), ports.Reputation);
        bg.HandleKillUnit(11947, AlteracValley.EventCaptainHorde, Alliance[0]);
        Assert.Equal(AlteracValley.InitialPoints - AlteracValley.ResourcesLostPerCaptain, bg.TeamScore(Team.Horde));

        bg.HandleKillUnit(11946, AlteracValley.EventBossHorde, Alliance[0]);
        Assert.Equal(BattlegroundStatus.WaitLeave, bg.Status);
        Assert.Equal(BattlegroundWinner.Alliance, bg.Winner);
        Assert.Contains((Alliance[0], AlteracValley.SpellBossKillQuest), ports.Casts);
        Assert.Contains((Alliance[0], AlteracValley.FactionStormpike, 350), ports.Reputation);
        Assert.Contains(host.Yells, y => y.Text == BattlegroundTexts.LangAvHordeGeneralDead);
        Assert.Contains(BattlegroundTexts.AvAllianceWins, host.Announcements.Select(a => a.Text));
    }

    [Fact]
    public void AMineBossKill_HandsTheMineOver_WhichFeedsAReinforcementEvery45Seconds_UntilReclaimed()
    {
        var (bg, host, _) = NewAv();
        Start(bg);
        bg.HandleKillUnit(13088, AlteracValley.EventMineBosses, Horde[0]);
        Assert.Equal(1, bg.MineOwner(0));
        Assert.Contains((1359u, 1u), host.WorldStates);
        Assert.Contains((1360u, 0u), host.WorldStates);
        Assert.Contains(7124u, host.QuestsCompleted);
        Assert.True(bg.IsActiveEvent(AlteracValley.EventMine, 1));

        Tick(bg, 45_000, 1000);
        Assert.Equal(AlteracValley.InitialPoints + 1, bg.TeamScore(Team.Horde));

        Tick(bg, AlteracValley.MineReclaimTimerMs, 1000);
        Assert.Equal(AlteracValley.TeamNeutral, bg.MineOwner(0));
    }

    [Fact]
    public void TheEnd_PaysSurvivingTowers_OwnedGraveyards_Mines_AndALivingCaptain()
    {
        var (bg, _, ports) = NewAv();
        Start(bg);
        bg.HandleKillUnit(13088, (byte)(AlteracValley.EventMineBosses + 1), Alliance[0]);
        bg.HandleKillUnit(11949, AlteracValley.EventBossHorde, Alliance[0]);

        // Alliance: 4 towers, graveyards 1-2 plus the 4 controlled towers counted by the vmangos loop, 1 mine, the captain alive.
        Assert.Contains((Alliance[0], AlteracValley.FactionStormpike, 4 * 12), ports.Reputation);
        Assert.Contains((Alliance[0], AlteracValley.FactionStormpike, 6 * 12), ports.Reputation);
        Assert.Contains((Alliance[0], AlteracValley.FactionStormpike, 24), ports.Reputation);
        Assert.Contains((Alliance[0], AlteracValley.FactionStormpike, 125), ports.Reputation);

        // Horde: 4 towers, graveyards 4-5 and the towers, no mine, the captain alive.
        Assert.Contains((Horde[0], AlteracValley.FactionFrostwolf, 4 * 12), ports.Reputation);
        Assert.DoesNotContain(ports.Reputation, r => r.Player == Horde[0] && r.Amount == 24);
    }

    [Fact]
    public void Graveyards_TheCaveBeforeTheStart_ThenTheNearestControlledGraveyardOrTheCave()
    {
        var (bg, _, _) = NewAv();
        (float X, float Y)? Loc(uint id) => id switch
        {
            611 => (870f, -490f),       // Alliance cave
            689 => (670f, -294f),       // Stormpike graveyard
            729 => (77f, -404f),        // Stonehearth graveyard
            _ => null,
        };

        bg.StartBattleground();
        Assert.Equal(AlteracValley.GraveyardId(7), bg.ClosestGraveyard(Team.Alliance, 80, -400, Loc));
        Start(bg);
        Assert.Equal(729u, bg.ClosestGraveyard(Team.Alliance, 80, -400, Loc));
        Assert.Equal(611u, bg.ClosestGraveyard(Team.Alliance, 860, -480, Loc));

        bg.EventPlayerClickedOnFlag(Horde[0], Banner(bg, AlteracValley.NodeStoneheartGrave));
        Assert.Equal(689u, bg.ClosestGraveyard(Team.Alliance, 80, -400, Loc));   // assaulted: not controlled
    }

    [Fact]
    public void AlteracNeverFinishesEarly_ForLackOfPlayers()
    {
        var (bg, _, _) = NewAv();
        Start(bg);
        bg.RemovePlayerAtLeave(Horde[0], teleportToEntryPoint: false, sendStatus: false);
        Tick(bg, 10 * 60 * 1000, 1000);
        Assert.Equal(BattlegroundStatus.InProgress, bg.Status);
    }

    [Fact]
    public void InitialWorldStates_ShowTheScoresOnlyWhileRunning_AndSnowfallNeutral()
    {
        var (bg, _, _) = NewAv();
        var before = bg.InitialWorldStates().ToDictionary(s => s.Id, s => s.Value);
        Assert.Equal(0, before[AlteracValley.WorldStateShowAllianceScore]);
        Assert.Equal(1, before[AlteracValley.WorldStateSnowfallNeutral]);
        Assert.Equal(1, before[1360]);       // north mine neutral
        Assert.Equal(1, before[AlteracValley.NodeWorldState(AlteracValley.NodeDunBaldarSouth, AlteracValley.PointControlled, 0)]);
        Assert.Equal(AlteracValley.InitialPoints, before[AlteracValley.WorldStateHordeScore]);

        Start(bg);
        Assert.Contains((AlteracValley.WorldStateShowHordeScore, 1), bg.InitialWorldStates());
    }

    [Fact]
    public void TheCaptainsBuffTheirTeam_TwoToSixMinutesIn()
    {
        var (bg, _, ports) = NewAv();
        Start(bg);
        Tick(bg, 6 * 60 * 1000 + 1000, 1000);
        Assert.Contains((Alliance[0], AlteracValley.SpellAllianceCaptainBuff), ports.Casts);
        Assert.Contains((Horde[0], AlteracValley.SpellHordeCaptainBuff), ports.Casts);
    }
}
