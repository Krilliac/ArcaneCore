using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using Xunit;
using static ArcaneCore.Game.Tests.Battlegrounds.BgTestData;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>
/// Arathi Basin against vmangos BattleGroundAB.cpp/.h: node claims, assaults, defenses and the one-minute capture, the resource ticks, the
/// win at 2000, honor and reputation by resources, graveyards by node, the world states and the re-rolled buffs. Time moves only through
/// the test's own <c>Update(diff)</c> calls.
/// </summary>
public sealed class ArathiBasinTests
{
    /// <summary>The vmangos row of battleground_template id 3 (6 to 15 players per team, levels 20 to 60, start locations 890/889).</summary>
    public static BattlegroundTemplate AbTemplate(uint minPerTeam = 1, uint maxPerTeam = 15) => new()
    {
        Type = BattlegroundType.ArathiBasin,
        MapId = 529,
        Name = "Arathi Basin",
        MinPlayersPerTeam = minPerTeam,
        MaxPlayersPerTeam = maxPerTeam,
        MinLevel = 20,
        MaxLevel = 60,
        AllianceWinSpell = 24953,
        AllianceLoseSpell = 24952,
        HordeWinSpell = 24953,
        HordeLoseSpell = 24952,
    };

    private static (ArathiBasin Bg, RecordingHost Host, RecordingPorts Ports) NewAb(int bracket = 4, bool weekend = false, int seed = 7)
    {
        var host = new RecordingHost();
        var ports = new RecordingPorts { Weekend = weekend };
        BattlegroundPorts all = ports.ToPorts(host);
        all = new BattlegroundPorts
        {
            Host = all.Host,
            Spells = all.Spells,
            Honor = all.Honor,
            Ranks = all.Ranks,
            Reputation = all.Reputation,
            Calendar = all.Calendar,
            Lifecycle = all.Lifecycle,
            Random = new Random(seed),
        };
        var bg = new ArathiBasin(AbTemplate(), bracket, instanceId: 201, clientInstanceId: 1, new BattlegroundOptions(), all);
        return (bg, host, ports);
    }

    private static void Start(ArathiBasin bg)
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

    private static BattlegroundObjectUse Banner(int node, byte status = 0) => new(180087, (byte)node, status, WithinTenYards: true);

    // ---------------------------------------------------------------- start

    [Fact]
    public void StartSequence_CreatesTheBuffs_HidesThem_ThenShowsOneRandomBuffPerNode()
    {
        var (bg, host, _) = NewAb();
        Start(bg);

        // SetupBattleGround: three objects per node at the node's buff position (BattleGroundAB.cpp:462-473).
        Assert.Equal(15, host.AddedObjects.Count);
        Assert.Equal([.. Enumerable.Range(1, 15)], host.AddedObjects.Select(o => o.Index).Order());
        Assert.Equal(BattlegroundConstants.SpeedBuffEntry, host.AddedObjects.Single(o => o.Index == 1).Entry);
        Assert.Equal(BattlegroundConstants.RegenBuffEntry, host.AddedObjects.Single(o => o.Index == 2).Entry);
        Assert.Equal(BattlegroundConstants.BerserkBuffEntry, host.AddedObjects.Single(o => o.Index == 3).Entry);

        // StartingEventCloseDoors despawns all 15; StartingEventOpenDoors shows one per node at once.
        Assert.Equal(15, host.ObjectSpawns.Count(s => s.Seconds == BattlegroundConstants.RespawnNeverSeconds));
        var shown = host.ObjectSpawns.Where(s => s.Seconds == 0).Select(s => s.Index).ToList();
        Assert.Equal(5, shown.Count);
        Assert.Equal([0, 1, 2, 3, 4], shown.Select(i => (i - 1) / 3).Order());

        Assert.Equal([BattlegroundTexts.AbStartOneMinute, BattlegroundTexts.AbStartHalfMinute, BattlegroundTexts.AbHasBegun], host.Announcements.Select(a => a.Text));
        Assert.Equal(1, host.OpenDoorsCalls);
        Assert.False(bg.IsActiveEvent(BattlegroundConstants.EventGhostGate, 0));
        for (int node = 0; node < ArathiBasin.NodeCount; node++)
        {
            Assert.True(bg.IsActiveEvent((byte)node, ArathiBasin.StatusNeutral));
        }
    }

    // ---------------------------------------------------------------- nodes

    [Fact]
    public void ClaimingANeutralNode_ContestsIt_ThenAMinuteLaterItIsOccupied()
    {
        var (bg, host, _) = NewAb();
        Start(bg);
        host.WorldStates.Clear();

        bg.EventPlayerClickedOnFlag(Alliance[0], Banner(ArathiBasin.NodeFarm));

        Assert.Equal(ArathiBasin.StatusAllianceContested, bg.NodeStatus(ArathiBasin.NodeFarm));
        Assert.Equal(ArathiBasin.FlagCapturingTimeMs, bg.NodeTimer(ArathiBasin.NodeFarm));
        Assert.Contains(((byte)ArathiBasin.NodeFarm, (byte)1, true, true, 1u), host.DelayedEvents);
        Assert.Contains((1845u, 0u), host.WorldStates);      // the farm's neutral icon goes
        Assert.Contains((1772u + 2u, 1u), host.WorldStates);  // the farm's Alliance-contested state (plus[1] = 2)
        Assert.Contains((Alliance[0], 15003u), host.Credits);
        Assert.Contains(host.Formatted, f => f.Text == BattlegroundTexts.LangAbNodeClaimed && f.Arg1 == BattlegroundTexts.LangAbNodeFarm && f.Arg2 == BattlegroundTexts.LangBgAlliance);
        Assert.Equal(ArathiBasin.SoundNodeClaimed, host.Sounds[^1]);
        Assert.Equal(1u, ((AbScore)bg.ScoreOf(Alliance[0])!).BasesAssaulted);

        Tick(bg, ArathiBasin.FlagCapturingTimeMs - 1000, 1000);
        Assert.Equal(ArathiBasin.StatusAllianceContested, bg.NodeStatus(ArathiBasin.NodeFarm));

        bg.Update(1000);
        Assert.Equal(ArathiBasin.StatusAllianceOccupied, bg.NodeStatus(ArathiBasin.NodeFarm));
        Assert.Contains(((byte)ArathiBasin.NodeFarm, (byte)3, true, true, 5u), host.DelayedEvents);
        Assert.Contains((ArathiBasin.WorldStateOccupiedBasesAlliance, 1u), host.WorldStates);
        Assert.Contains(host.Formatted, f => f.Text == BattlegroundTexts.LangAbNodeTaken && f.Arg1 == BattlegroundTexts.LangBgAlliance);
        Assert.Equal(ArathiBasin.SoundNodeCapturedAlliance, host.Sounds[^1]);
    }

    [Fact]
    public void ATeamCannotClickItsOwnContestedOrOccupiedNode()
    {
        var (bg, host, _) = NewAb();
        Start(bg);
        bg.EventPlayerClickedOnFlag(Alliance[0], Banner(ArathiBasin.NodeStables));
        int sounds = host.Sounds.Count;

        bg.EventPlayerClickedOnFlag(Alliance[0], Banner(ArathiBasin.NodeStables));
        Assert.Equal(sounds, host.Sounds.Count);
        Assert.Equal(1u, ((AbScore)bg.ScoreOf(Alliance[0])!).BasesAssaulted);
    }

    [Fact]
    public void TheEnemyAssaultsAContestedNode_AndTheOccupierDefendsItsAssaultedNode()
    {
        var (bg, host, _) = NewAb();
        Start(bg);

        // A claim contested from neutral can be taken over by the other side: assaulted, the timer restarts.
        bg.EventPlayerClickedOnFlag(Alliance[0], Banner(ArathiBasin.NodeGoldMine));
        Tick(bg, 30_000, 1000);
        bg.EventPlayerClickedOnFlag(Horde[0], Banner(ArathiBasin.NodeGoldMine));
        Assert.Equal(ArathiBasin.StatusHordeContested, bg.NodeStatus(ArathiBasin.NodeGoldMine));
        Assert.Equal(ArathiBasin.FlagCapturingTimeMs, bg.NodeTimer(ArathiBasin.NodeGoldMine));
        Assert.Equal(ArathiBasin.SoundNodeAssaultedHorde, host.Sounds[^1]);

        Tick(bg, ArathiBasin.FlagCapturingTimeMs, 1000);
        Assert.Equal(ArathiBasin.StatusHordeOccupied, bg.NodeStatus(ArathiBasin.NodeGoldMine));

        // The Alliance assaults the Horde's node, the Horde defends it: occupied again at once, no timer, a defense on the board.
        bg.EventPlayerClickedOnFlag(Alliance[0], Banner(ArathiBasin.NodeGoldMine));
        Assert.Equal(ArathiBasin.StatusAllianceContested, bg.NodeStatus(ArathiBasin.NodeGoldMine));
        bg.EventPlayerClickedOnFlag(Horde[0], Banner(ArathiBasin.NodeGoldMine));
        Assert.Equal(ArathiBasin.StatusHordeOccupied, bg.NodeStatus(ArathiBasin.NodeGoldMine));
        Assert.Equal(0u, bg.NodeTimer(ArathiBasin.NodeGoldMine));
        Assert.Equal(1u, ((AbScore)bg.ScoreOf(Horde[0])!).BasesDefended);
        Assert.Contains(host.Formatted, f => f.Text == BattlegroundTexts.LangAbNodeDefended && f.Source == Horde[0]);
    }

    [Fact]
    public void ABannerWithoutANodeEvent_OrBeforeTheStart_IsIgnored()
    {
        var (bg, host, _) = NewAb();
        bg.StartBattleground();
        bg.IncreaseInvitedCount(Team.Alliance);
        bg.AddPlayer(Alliance[0], Team.Alliance);
        bg.EventPlayerClickedOnFlag(Alliance[0], Banner(ArathiBasin.NodeFarm));
        Assert.Equal(ArathiBasin.StatusNeutral, bg.NodeStatus(ArathiBasin.NodeFarm));
        Assert.Empty(host.Credits);

        while (bg.Status != BattlegroundStatus.InProgress)
        {
            bg.Update(1000);
        }

        bg.EventPlayerClickedOnFlag(Alliance[0], new BattlegroundObjectUse(180087, BattlegroundConstants.EventDoor, 0, true));
        Assert.Empty(host.Credits);
    }

    // ---------------------------------------------------------------- resources

    [Fact]
    public void OneBase_Ticks10ResourcesAfterMoreThan12Seconds()
    {
        var (bg, host, _) = NewAb();
        Start(bg);
        bg.EventPlayerClickedOnFlag(Alliance[0], Banner(ArathiBasin.NodeStables));
        Tick(bg, ArathiBasin.FlagCapturingTimeMs, 1000);
        Assert.Equal(ArathiBasin.StatusAllianceOccupied, bg.NodeStatus(ArathiBasin.NodeStables));
        Assert.Equal(0, bg.TeamScore(Team.Alliance));

        // The tick fires once the accumulated time is strictly above the interval (BattleGroundAB.cpp:101-103); the update that occupied the node
        // already counted its 1000 ms.
        Tick(bg, 11_000, 1000);
        Assert.Equal(0, bg.TeamScore(Team.Alliance));
        bg.Update(1000);
        Assert.Equal(10, bg.TeamScore(Team.Alliance));
        Assert.Contains((ArathiBasin.WorldStateResourcesAlliance, 10u), host.WorldStates);
        Assert.Equal(0, bg.TeamScore(Team.Horde));
    }

    [Fact]
    public void FiveBases_Tick30PerSecond_WinAt2000_WithTheNearVictoryWarningOnce_AndTheQuestSpells()
    {
        var (bg, host, ports) = NewAb(bracket: 4);
        Start(bg);
        for (int node = 0; node < ArathiBasin.NodeCount; node++)
        {
            bg.EventPlayerClickedOnFlag(Alliance[0], Banner(node));
        }

        Tick(bg, ArathiBasin.FlagCapturingTimeMs, 1000);
        Assert.All(Enumerable.Range(0, 5), n => Assert.Equal(ArathiBasin.StatusAllianceOccupied, bg.NodeStatus(n)));

        // _NodeOccupied: four and five bases held give the Alliance the quest spells (BattleGroundAB.cpp:331-334).
        Assert.Contains((Alliance[0], ArathiBasin.SpellQuestReward4Bases), ports.Casts);
        Assert.Contains((Alliance[0], ArathiBasin.SpellQuestReward5Bases), ports.Casts);
        Assert.DoesNotContain((Horde[0], ArathiBasin.SpellQuestReward5Bases), ports.Casts);

        int ticks = 0;
        while (bg.Status == BattlegroundStatus.InProgress && ticks++ < 200)
        {
            bg.Update(1000);
        }

        Assert.Equal(BattlegroundStatus.WaitLeave, bg.Status);
        Assert.Equal(BattlegroundWinner.Alliance, bg.Winner);
        Assert.Equal(ArathiBasin.MaxTeamScore, bg.TeamScore(Team.Alliance));
        Assert.Single(host.Announcements, a => a.Text == BattlegroundTexts.AbAllianceNearVictory);
        Assert.Contains(BattlegroundTexts.AbAllianceWins, host.Announcements.Select(a => a.Text));

        // 2000 resources: an honor tick per 330 (6 ticks, 41-198 by bracket), a reputation tick of 10 per 200 (10 ticks) with League of Arathor,
        // plus the win honor (BattleGroundAB.cpp:105-118, 507-523).
        uint expectedHonor = (6 * ArathiBasin.PerTickHonor(4)) + ArathiBasin.WinMatchHonor(4);
        Assert.Equal(expectedHonor, ports.Honor[Alliance[0]]);
        Assert.Equal(10, ports.Reputation.Count(r => r.Player == Alliance[0] && r.Faction == ArathiBasin.FactionLeagueOfArathor && r.Amount == 10));
        Assert.False(ports.Honor.ContainsKey(Horde[0]));
    }

    [Fact]
    public void AWeekendPaysTheWinHonorTwice_AndTicksHonorEvery200Resources()
    {
        var (bg, _, ports) = NewAb(bracket: 4, weekend: true);
        Start(bg);
        for (int node = 0; node < ArathiBasin.NodeCount; node++)
        {
            bg.EventPlayerClickedOnFlag(Horde[0], Banner(node));
        }

        Tick(bg, 400_000, 1000);
        Assert.Equal(BattlegroundWinner.Horde, bg.Winner);
        uint expected = (10 * ArathiBasin.PerTickHonor(4)) + (2 * ArathiBasin.WinMatchHonor(4));
        Assert.Equal(expected, ports.Honor[Horde[0]]);
        Assert.Equal(13, ports.Reputation.Count(r => r.Faction == ArathiBasin.FactionDefilers));   // 2000 / 150
    }

    // ---------------------------------------------------------------- graveyards

    [Fact]
    public void Graveyards_TheEntranceBeforeTheStart_ThenTheNearestOccupiedNode_ElseTheStart()
    {
        var (bg, _, _) = NewAb();
        (float X, float Y)? Loc(uint id) => id switch
        {
            895 => (1201f, 1163f),      // stables
            893 => (834f, 784f),        // farm
            _ => (0f, 0f),
        };

        bg.StartBattleground();
        Assert.Equal(ArathiBasin.GraveyardAllianceEntrance, bg.ClosestGraveyard(Team.Alliance, 1000, 1000, Loc));
        Assert.Equal(ArathiBasin.GraveyardHordeEntrance, bg.ClosestGraveyard(Team.Horde, 1000, 1000, Loc));

        Start(bg);
        Assert.Equal(ArathiBasin.GraveyardId(5), bg.ClosestGraveyard(Team.Alliance, 1000, 1000, Loc));
        Assert.Equal(ArathiBasin.GraveyardId(6), bg.ClosestGraveyard(Team.Horde, 1000, 1000, Loc));

        bg.EventPlayerClickedOnFlag(Alliance[0], Banner(ArathiBasin.NodeStables));
        bg.EventPlayerClickedOnFlag(Alliance[0], Banner(ArathiBasin.NodeFarm));
        Assert.Equal(ArathiBasin.GraveyardId(5), bg.ClosestGraveyard(Team.Alliance, 900, 900, Loc));   // contested is not occupied
        Tick(bg, ArathiBasin.FlagCapturingTimeMs, 1000);
        Assert.Equal(893u, bg.ClosestGraveyard(Team.Alliance, 900, 900, Loc));
        Assert.Equal(895u, bg.ClosestGraveyard(Team.Alliance, 1150, 1150, Loc));
    }

    // ---------------------------------------------------------------- buffs, world states, scoreboard

    [Fact]
    public void AUsedBuff_IsReplacedByARandomType_ThatAppearsAfterThreeMinutes()
    {
        var (bg, host, _) = NewAb(seed: 3);
        Start(bg);
        host.ObjectSpawns.Clear();

        // The farm's speed buff (index 1 + 3 * 2) was used.
        int index = ArathiBasin.FirstBuffObjectIndex + (3 * ArathiBasin.NodeFarm);
        Assert.True(bg.HandleTriggerBuff(index, BattlegroundConstants.SpeedBuffEntry));
        (int Index, uint Seconds) respawn = host.ObjectSpawns.Single(s => s.Seconds == BattlegroundConstants.BuffRespawnTimeSeconds);
        Assert.InRange(respawn.Index, index, index + 2);
        if (respawn.Index != index)
        {
            Assert.Contains((index, BattlegroundConstants.RespawnNeverSeconds), host.ObjectSpawns);
        }

        // Warsong Gulch buffs are static: the caller deactivates the database object itself.
        var (wsg, _, _) = NewWsg();
        Assert.False(wsg.HandleTriggerBuff(-1, BattlegroundConstants.SpeedBuffEntry));
    }

    [Fact]
    public void InitialWorldStates_FollowVmangos()
    {
        var (bg, _, _) = NewAb();
        Start(bg);
        bg.EventPlayerClickedOnFlag(Horde[0], Banner(ArathiBasin.NodeBlacksmith));
        var states = bg.InitialWorldStates().ToDictionary(s => s.Id, s => s.Value);

        Assert.Equal(0, states[1846]);                  // blacksmith icon: not neutral any more
        Assert.Equal(1, states[1842]);                  // stables icon: neutral
        Assert.Equal(1, states[1782 + 3]);              // blacksmith Horde contested (plus[2] = 3)
        Assert.Equal(0, states[1782 + 2]);
        Assert.Equal(ArathiBasin.MaxTeamScore, states[ArathiBasin.WorldStateResourcesMax]);
        Assert.Equal(ArathiBasin.WarningNearVictoryScore, states[ArathiBasin.WorldStateResourcesWarning]);
        Assert.Equal(2, states[0x745]);
        Assert.Equal(5 + 20 + 7, bg.InitialWorldStates().Count);
    }

    [Fact]
    public void TheScoreboardCarriesBasesAssaultedAndDefended()
    {
        var (bg, _, _) = NewAb();
        Start(bg);
        bg.EventPlayerClickedOnFlag(Alliance[0], Banner(ArathiBasin.NodeLumberMill));
        PvpLogRow row = bg.BuildPvpLog().Rows.Single(r => r.Player == Alliance[0]);
        Assert.Equal([1u, 0u], row.ExtraFields);
    }

    [Fact]
    public void TheExitTriggers_LeaveOnlyTheirOwnTeam()
    {
        var (bg, host, _) = NewAb();
        Start(bg);
        Assert.False(bg.HandleAreaTrigger(Horde[0], ArathiBasin.AreaTriggerAllianceExit));
        Assert.True(bg.HandleAreaTrigger(Alliance[0], ArathiBasin.AreaTriggerAllianceExit));
        Assert.True(bg.HandleAreaTrigger(Horde[0], ArathiBasin.AreaTriggerHordeExit));
        Assert.Equal([Alliance[0], Horde[0]], host.Leaves);
        Assert.False(bg.HandleAreaTrigger(Alliance[0], 3866));   // a node trigger: deliberately unhandled
    }
}
