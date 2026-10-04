using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using Xunit;
using static ArcaneCore.Game.Tests.Battlegrounds.BgTestData;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>The common battleground rules (vmangos BattleGround.cpp), exercised through Warsong Gulch.</summary>
public sealed class BattlegroundCoreTests
{
    [Fact]
    public void AddingAPlayerCountsItTellsItsTeamAndRefusesADuplicate()
    {
        var (bg, host, _) = NewWsg();
        bg.StartBattleground();

        Assert.True(bg.AddPlayer(Alliance[0], Team.Alliance));
        Assert.True(bg.AddPlayer(Alliance[1], Team.Alliance));
        Assert.True(bg.AddPlayer(Horde[0], Team.Horde));
        Assert.False(bg.AddPlayer(Alliance[0], Team.Alliance));

        Assert.Equal(2u, bg.PlayersCountByTeam(Team.Alliance));
        Assert.Equal(1u, bg.PlayersCountByTeam(Team.Horde));
        Assert.Equal(3, bg.PlayerCount);
        Assert.Equal(Team.Alliance, bg.PlayerTeam(Alliance[0]));
        Assert.Null(bg.PlayerTeam(Horde[5]));
        Assert.Equal([(Team.Alliance, Alliance[0]), (Team.Alliance, Alliance[1]), (Team.Horde, Horde[0])], host.Joined);
        Assert.NotNull(bg.ScoreOf(Alliance[0]));
        Assert.IsType<WsgScore>(bg.ScoreOf(Alliance[0]));
    }

    [Fact]
    public void AJoinerIntoAnEndedMatchIsFrozenAndShownTheScoreAndTheLeaveTimer()
    {
        var (bg, host, _) = NewWsg();
        StartMatch(bg);
        Tick(bg, 5000, 1000);
        // Both sides below the minimum with no winner is the quickest way to end the match without flags.
        bg.RemovePlayerAtLeave(Horde[0], true, true);
        Tick(bg, 310_000, 1000);
        Assert.Equal(BattlegroundStatus.WaitLeave, bg.Status);
        host.Blocked.Clear();
        host.Statuses.Clear();

        bg.IncreaseInvitedCount(Team.Horde);
        Assert.True(bg.AddPlayer(Horde[7], Team.Horde));

        Assert.Equal([Horde[7]], host.Blocked);
        Assert.Equal(Horde[7], host.PvpLogs.Single().Player);
        Assert.True(host.PvpLogs.Single().Log.Ended);
        Assert.Equal((Horde[7], BattlegroundStatus.InProgress, (uint)bg.EndTimeMs, bg.StartTimeMs), host.Statuses.Single());
    }

    [Fact]
    public void LeavingAMatchInProgressUpdatesEveryoneAndFreesTheSlot()
    {
        var (bg, host, ports) = NewWsg();
        StartMatch(bg, perTeam: 3);
        host.Left.Clear();
        host.Statuses.Clear();
        int updates = ports.QueueUpdates;
        int adds = ports.FreeSlotAdds;

        bg.RemovePlayerAtLeave(Alliance[1], teleportToEntryPoint: true, sendStatus: true);

        Assert.Equal(2u, bg.PlayersCountByTeam(Team.Alliance));
        Assert.Equal(2u, bg.InvitedCount(Team.Alliance));
        Assert.Null(bg.ScoreOf(Alliance[1]));
        Assert.Equal([Alliance[1]], host.ResurrectDead);
        Assert.Equal((Alliance[1], BattlegroundStatus.None, 0u, 0u), host.Statuses.Single());
        Assert.Equal([(Team.Alliance, Alliance[1])], host.Left);
        Assert.Equal([Alliance[1]], host.Cleared);
        Assert.Equal([Alliance[1]], host.TeleportedToEntry);
        Assert.Equal(updates + 1, ports.QueueUpdates);
        Assert.True(ports.FreeSlotAdds >= adds);
        Assert.Contains(Alliance[1], host.Cleared);
    }

    [Fact]
    public void LeavingWithoutTeleportOrStatusSendsNeither()
    {
        var (bg, host, _) = NewWsg();
        StartMatch(bg, perTeam: 3);
        host.Statuses.Clear();

        bg.RemovePlayerAtLeave(Alliance[1], teleportToEntryPoint: false, sendStatus: false);

        Assert.Empty(host.TeleportedToEntry);
        Assert.Empty(host.Statuses);
        Assert.Equal([Alliance[1]], host.Cleared);
    }

    [Fact]
    public void RemovingAnOfflinePlayerTouchesNoWorldObjectOfThePlayer()
    {
        var (bg, host, _) = NewWsg();
        StartMatch(bg, perTeam: 3);
        host.Statuses.Clear();

        bg.RemovePlayerAtLeave(Alliance[1], teleportToEntryPoint: true, sendStatus: true, online: false);

        Assert.Empty(host.ResurrectDead);
        Assert.Empty(host.Statuses);
        Assert.Empty(host.Cleared);
        Assert.Empty(host.TeleportedToEntry);
        // He still left: the team count, the invitation and the others' notification (vmangos BattleGround.cpp:945-984).
        Assert.Equal(2u, bg.PlayersCountByTeam(Team.Alliance));
        Assert.Equal([(Team.Alliance, Alliance[1])], host.Left);
    }

    [Fact]
    public void AGuidThatIsNotInTheMatchIsOnlyUnboundNotCountedAsAParticipant()
    {
        var (bg, host, ports) = NewWsg();
        StartMatch(bg, perTeam: 3);
        host.Left.Clear();
        host.Statuses.Clear();
        int updates = ports.QueueUpdates;

        // A GM who walked in with .goname is not a participant (BattleGround.cpp:920-921).
        bg.RemovePlayerAtLeave(Alliance[9], teleportToEntryPoint: true, sendStatus: true);

        Assert.Equal(3u, bg.InvitedCount(Team.Alliance));
        Assert.Empty(host.Left);
        Assert.Empty(host.Statuses);
        Assert.Equal(updates, ports.QueueUpdates);
        Assert.Equal([Alliance[9]], host.Cleared);
        Assert.Equal([Alliance[9]], host.TeleportedToEntry);
    }

    [Fact]
    public void FreeSlotsAreTheMaximumLessTheInvitedPlayersWhileStartingOrRunning()
    {
        var (bg, _, _) = NewWsg();
        Assert.Equal(10u, bg.FreeSlotsForTeam(Team.Alliance));
        bg.IncreaseInvitedCount(Team.Alliance);
        bg.IncreaseInvitedCount(Team.Alliance);
        Assert.Equal(8u, bg.FreeSlotsForTeam(Team.Alliance));
        Assert.Equal(10u, bg.FreeSlotsForTeam(Team.Horde));

        for (int i = 0; i < 20; i++)
        {
            bg.IncreaseInvitedCount(Team.Horde);
        }

        Assert.Equal(0u, bg.FreeSlotsForTeam(Team.Horde));
        Assert.True(bg.HasFreeSlots);
        Assert.Equal(20u, bg.MaxPlayers);
        Assert.Equal(4u, bg.MinPlayers);
    }

    [Fact]
    public void DecreasingTheInvitedCountBelowZeroIsIgnored()
    {
        var (bg, _, _) = NewWsg();
        bg.DecreaseInvitedCount(Team.Horde);
        Assert.Equal(0u, bg.InvitedCount(Team.Horde));
    }

    // ---------------------------------------------------------------- kills

    [Fact]
    public void AnEnemyKillCreditsTheKillerAndNearbyTeammatesAndCountsTheDeath()
    {
        var (bg, host, _) = NewWsg();
        StartMatch(bg, perTeam: 3);
        host.NearVictim.Add(Alliance[1]);   // within group reward distance
        // Alliance[2] is not near

        bg.HandleKillPlayer(Horde[0], Alliance[0]);

        Assert.Equal(1u, bg.ScoreOf(Alliance[0])!.KillingBlows);
        Assert.Equal(1u, bg.ScoreOf(Alliance[0])!.HonorableKills);
        Assert.Equal(0u, bg.ScoreOf(Alliance[1])!.KillingBlows);
        Assert.Equal(1u, bg.ScoreOf(Alliance[1])!.HonorableKills);
        Assert.Equal(0u, bg.ScoreOf(Alliance[2])!.HonorableKills);
        Assert.Equal(0u, bg.ScoreOf(Horde[1])!.HonorableKills);
        Assert.Equal(1u, bg.ScoreOf(Horde[0])!.Deaths);
        Assert.Equal([Horde[0]], host.Skinnable);
    }

    [Fact]
    public void AKillWithNoPlayerKillerStillCountsTheDeath()
    {
        var (bg, host, _) = NewWsg();
        StartMatch(bg);

        bg.HandleKillPlayer(Horde[0], null);

        Assert.Equal(1u, bg.ScoreOf(Horde[0])!.Deaths);
        Assert.Equal(0u, bg.ScoreOf(Alliance[0])!.KillingBlows);
        Assert.Single(host.Skinnable);
    }

    [Fact]
    public void ADeathInSpiritOfRedemptionDoesNotCount()
    {
        var (bg, host, ports) = NewWsg();
        StartMatch(bg);
        ports.Auras.Add((Horde[0], 27827u));

        bg.HandleKillPlayer(Horde[0], Alliance[0]);

        Assert.Equal(0u, bg.ScoreOf(Horde[0])!.Deaths);
        Assert.Empty(host.Skinnable);
        // The kill itself still credits the killer (BattleGround.cpp:1767-1782).
        Assert.Equal(1u, bg.ScoreOf(Alliance[0])!.KillingBlows);
    }

    [Fact]
    public void AKillBetweenTeammatesCreditsNoOne()
    {
        var (bg, _, _) = NewWsg();
        StartMatch(bg);

        bg.HandleKillPlayer(Alliance[1], Alliance[0]);

        Assert.Equal(0u, bg.ScoreOf(Alliance[0])!.KillingBlows);
        Assert.Equal(1u, bg.ScoreOf(Alliance[1])!.Deaths);
    }

    [Fact]
    public void KillsOutsideAMatchInProgressAreNotScoredByWarsongGulch()
    {
        var (bg, _, _) = NewWsg();
        bg.StartBattleground();
        bg.AddPlayer(Alliance[0], Team.Alliance);
        bg.AddPlayer(Horde[0], Team.Horde);

        bg.HandleKillPlayer(Horde[0], Alliance[0]);

        Assert.Equal(0u, bg.ScoreOf(Horde[0])!.Deaths);
    }

    // ---------------------------------------------------------------- scoreboard

    [Fact]
    public void TheScoreboardListsPlayersByGuidWithRankAndWarsongColumns()
    {
        var (bg, _, ports) = NewWsg();
        StartMatch(bg);
        ports.Ranks[Alliance[0]] = 9;
        ((WsgScore)bg.ScoreOf(Horde[1])!).FlagReturns = 4;
        ((WsgScore)bg.ScoreOf(Horde[1])!).FlagCaptures = 2;
        bg.ScoreOf(Alliance[1])!.KillingBlows = 6;
        bg.ScoreOf(Alliance[1])!.HonorableKills = 7;
        bg.ScoreOf(Alliance[1])!.Deaths = 8;
        bg.ScoreOf(Alliance[1])!.BonusHonor = 9;

        PvpLogSnapshot log = bg.BuildPvpLog();

        Assert.False(log.Ended);
        Assert.Equal(4, log.Rows.Count);
        Assert.Equal(log.Rows.Select(r => r.Player.Value).Order().ToArray(), log.Rows.Select(r => r.Player.Value).ToArray());
        PvpLogRow a0 = log.Rows.Single(r => r.Player == Alliance[0]);
        Assert.Equal(9u, a0.Rank);
        PvpLogRow a1 = log.Rows.Single(r => r.Player == Alliance[1]);
        Assert.Equal((BattlegroundConstants.DefaultPvpLogRank, 6u, 7u, 8u, 9u), (a1.Rank, a1.KillingBlows, a1.HonorableKills, a1.Deaths, a1.BonusHonor));
        Assert.Equal([0u, 0u], a1.ExtraFields);
        Assert.Equal([2u, 4u], log.Rows.Single(r => r.Player == Horde[1]).ExtraFields);
    }

    [Fact]
    public void TheScoreboardShowsAtMostEightyPlayers()
    {
        var (bg, _, _) = NewWsg();
        bg.StartBattleground();
        for (uint i = 1; i <= 90; i++)
        {
            bg.AddPlayer(ObjectGuid.Player(1000 + i), i % 2 == 0 ? Team.Alliance : Team.Horde);
        }

        Assert.Equal(BattlegroundConstants.PvpLogMaxPlayers, bg.BuildPvpLog().Rows.Count);
    }

    [Fact]
    public void TheFinalScoreIsFrozenAtTheEndOfTheMatch()
    {
        var (bg, host, _) = NewWsg();
        StartMatch(bg);
        for (int i = 0; i < 3; i++)
        {
            bg.OnFlagClicked(Alliance[0], new WsgFlagObject(WarsongGulch.HordeFlagBaseEntry, WarsongGulch.EventFlagHorde, false));
            bg.HandleAreaTrigger(Alliance[0], WarsongGulch.AreaTriggerAllianceFlagSpawn);
            if (i < 2)
            {
                Tick(bg, 24_000, 1000);
            }
        }

        PvpLogSnapshot? final = bg.FinalScore;
        Assert.NotNull(final);
        Assert.True(final.Ended);
        Assert.Equal(BattlegroundWinner.Alliance, final.Winner);
        Assert.Equal(3u, final.Rows.Single(r => r.Player == Alliance[0]).ExtraFields[0]);

        bg.ScoreOf(Alliance[0])!.KillingBlows = 99;
        Assert.Same(final, bg.FinalScore);
        Assert.All(host.EndPackets, p => Assert.Same(final, p.Log));
        Assert.Equal(0u, final.Rows.Single(r => r.Player == Alliance[0]).KillingBlows);
        Assert.Equal(99u, bg.BuildPvpLog().Rows.Single(r => r.Player == Alliance[0]).KillingBlows);
    }

    // ---------------------------------------------------------------- the end

    [Fact]
    public void EndingTheMatchPreparesEveryPlayerAndEndsSpiritOfRedemption()
    {
        var (bg, host, ports) = NewWsg();
        StartMatch(bg);
        bg.RemovePlayerAtLeave(Horde[0], true, true);
        ports.SpiritOfRedemptionEnded.Clear();
        Tick(bg, 310_000, 1000);

        Assert.Equal(BattlegroundStatus.WaitLeave, bg.Status);
        Assert.Equal(3, host.ResurrectOrStop.Count);
        Assert.Equal(3, host.Blocked.Count);
        Assert.Equal(3, host.StoppedPets.Count);
        Assert.Equal(3, ports.SpiritOfRedemptionEnded.Count);
        Assert.Equal(3, host.EndPackets.Count);
        Assert.True(ports.FreeSlotRemoves >= 1);
    }

    [Fact]
    public void ATemplateWithoutMarkSpellsCastsNothingAtTheEnd()
    {
        var host = new RecordingHost();
        var ports = new RecordingPorts();
        WsgTemplateNoSpells(out BattlegroundTemplate template);
        var bg = new WarsongGulch(template, 5, 101, 1, new BattlegroundOptions(), ports.ToPorts(host));
        StartMatch(bg);
        bg.RemovePlayerAtLeave(Horde[0], true, true);
        Tick(bg, 310_000, 1000);

        Assert.Equal(BattlegroundStatus.WaitLeave, bg.Status);
        Assert.Empty(ports.Casts);
    }

    private static void WsgTemplateNoSpells(out BattlegroundTemplate template) =>
        template = WsgTemplate() with { AllianceWinSpell = 0, AllianceLoseSpell = 0, HordeWinSpell = 0, HordeLoseSpell = 0 };

    [Fact]
    public void AnEmptyBattlegroundWithInvitedPlayersWaitsAndAsksForAQueueUpdateAfterTwoMinutes()
    {
        var (bg, _, ports) = NewWsg();
        bg.StartBattleground();
        bg.IncreaseInvitedCount(Team.Alliance);

        Assert.True(bg.Update(60_000));
        Assert.Equal(0, ports.QueueUpdates);
        Assert.True(bg.Update(61_000));
        Assert.True(ports.QueueUpdates > 0);
        Assert.Equal(BattlegroundStatus.WaitJoin, bg.Status);
    }

    [Fact]
    public void StartingTheBattlegroundPutsItInTheFreeSlotListOnce()
    {
        var (bg, _, ports) = NewWsg();
        bg.StartBattleground();
        bg.StartBattleground();

        Assert.Equal(1, ports.FreeSlotAdds);
        Assert.True(bg.InFreeSlotQueue);
    }

    [Fact]
    public void TheNewBattlegroundCarriesItsTemplateAndBracket()
    {
        var (bg, _, _) = NewWsg(bracket: 3);

        Assert.Equal(BattlegroundType.WarsongGulch, bg.Type);
        Assert.Equal(489u, bg.MapId);
        Assert.Equal(3, bg.Bracket);
        Assert.Equal(101u, bg.InstanceId);
        Assert.Equal(1u, bg.ClientInstanceId);
        Assert.Equal(BattlegroundStatus.WaitJoin, bg.Status);
        Assert.Equal(BattlegroundWinner.None, bg.Winner);
        Assert.Equal("Warsong Gulch", bg.Name);
    }

    [Fact]
    public void OptionsDefaultToRetailAndClamp()
    {
        var options = new BattlegroundOptions();
        Assert.True(options.CastDeserter);
        Assert.Equal(300_000u, options.PrematureFinishTimerMs);
        Assert.Equal(1u, options.InvitationType);
        Assert.Equal(0u, options.PremadeGroupWaitForMatchMs);
        Assert.Equal(6u, options.PremadeQueueMinGroupSize);
        Assert.Equal(3u, options.EffectiveQueuesCount);
        Assert.True(options.TagInBattlegrounds);
        Assert.Equal(40u, options.GroupQueueLimit);

        options.QueuesCount = 9;
        Assert.Contains(nameof(BattlegroundOptions.QueuesCount), options.Normalize());
        Assert.Equal(3u, options.QueuesCount);
        options.QueuesCount = 1;
        Assert.Equal(1u, options.EffectiveQueuesCount);
    }
}
