using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using Xunit;
using static ArcaneCore.Game.Tests.Battlegrounds.BgTestData;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>
/// Warsong Gulch flag rules against vmangos BattleGroundWS.cpp / BattleGroundWS.h. All time is driven by the test's own
/// <c>Update(diff)</c> calls, so nothing here depends on a wall clock.
/// </summary>
public sealed class WarsongGulchTests
{
    private static readonly WsgFlagObject HordeFlagOnBase = new(WarsongGulch.HordeFlagBaseEntry, WarsongGulch.EventFlagHorde, WithinTenYards: false);
    private static readonly WsgFlagObject AllianceFlagOnBase = new(WarsongGulch.AllianceFlagBaseEntry, WarsongGulch.EventFlagAlliance, WithinTenYards: false);
    private static readonly WsgFlagObject HordeFlagOnGround = new(WarsongGulch.HordeFlagGroundEntry, BattlegroundConstants.EventNone, WithinTenYards: true);
    private static readonly WsgFlagObject AllianceFlagOnGround = new(WarsongGulch.AllianceFlagGroundEntry, BattlegroundConstants.EventNone, WithinTenYards: true);

    // ---------------------------------------------------------------- start sequence

    [Fact]
    public void StartSequence_RunsTheVmangosDelaysAndEvents()
    {
        var (bg, host, _) = NewWsg();
        bg.StartBattleground();
        bg.IncreaseInvitedCount(Team.Alliance);
        Assert.True(bg.AddPlayer(Alliance[0], Team.Alliance));

        // First update with a player: the countdown starts at two minutes (BattleGround.cpp:378-387), no text for the first event.
        Assert.True(bg.Update(1000));
        Assert.Equal(BattlegroundConstants.StartDelay2MinMs, bg.StartDelayMs);
        Assert.Empty(host.Announcements);
        Assert.Equal(BattlegroundStatus.WaitJoin, bg.Status);
        Assert.Equal(0, host.OpenDoorsCalls);

        int minuteTick = 0;
        int halfTick = 0;
        for (int tick = 2; tick < 400 && bg.Status != BattlegroundStatus.InProgress; tick++)
        {
            bg.Update(1000);
            if (minuteTick == 0 && host.Announcements.Count == 1)
            {
                minuteTick = tick;
                Assert.Equal(BattlegroundTexts.WsStartOneMinute, host.Announcements[0].Text);
                Assert.True(bg.StartDelayMs <= BattlegroundConstants.StartDelay1MinMs);
            }

            if (halfTick == 0 && host.Announcements.Count == 2)
            {
                halfTick = tick;
                Assert.Equal(BattlegroundTexts.WsStartHalfMinute, host.Announcements[1].Text);
                Assert.True(bg.StartDelayMs <= BattlegroundConstants.StartDelay30SecMs);
            }
        }

        Assert.True(minuteTick > 0 && halfTick > minuteTick, "the one minute and 30 second texts come in order before the start");
        Assert.Equal(BattlegroundStatus.InProgress, bg.Status);
        Assert.True(bg.StartDelayMs <= 0);
        Assert.Equal(3, host.Announcements.Count);
        Assert.Equal(BattlegroundTexts.WsHasBegun, host.Announcements[2].Text);
        Assert.All(host.Announcements, a => Assert.Equal(BattlegroundChatKind.Neutral, a.Kind));
        Assert.Equal(1, host.OpenDoorsCalls);
        Assert.Contains(BattlegroundConstants.SoundStart, host.Sounds);
        Assert.Single(host.ReturnToStart);

        // Open doors: spirit guides and both flags spawn, the ghost gates despawn (BattleGroundWS.cpp:102-112).
        Assert.True(bg.IsActiveEvent(WarsongGulch.EventSpiritGuides, 0));
        Assert.True(bg.IsActiveEvent(WarsongGulch.EventFlagAlliance, 0));
        Assert.True(bg.IsActiveEvent(WarsongGulch.EventFlagHorde, 0));
        Assert.False(bg.IsActiveEvent(BattlegroundConstants.EventGhostGate, 0));
        Assert.Contains((BattlegroundConstants.EventGhostGate, (byte)0, false, true), host.Events);
    }

    [Fact]
    public void DoorsAreRemovedOnlyOnceStartTimeExceedsThreeMinutes()
    {
        var (bg, host, _) = NewWsg();
        StartMatch(bg);
        Assert.Equal(0, host.DespawnDoorsCalls);

        uint before = 0;
        while (host.DespawnDoorsCalls == 0)
        {
            before = bg.StartTimeMs;
            Assert.True(bg.Update(1000));
        }

        // The check runs before the tick's time is added (BattleGround.cpp:433 then 463).
        Assert.True(before > BattlegroundConstants.DoorsDespawnStartTimeMs);
        Tick(bg, 20_000, 1000);
        Assert.Equal(1, host.DespawnDoorsCalls);
    }

    [Fact]
    public void StartingWithNoPlayersNeverBeginsAndTheEmptyBattlegroundIsDeletedWhenNobodyIsInvited()
    {
        var (bg, _, _) = NewWsg();
        bg.StartBattleground();
        Assert.False(bg.Update(1000));

        bg.IncreaseInvitedCount(Team.Horde);
        Assert.True(bg.Update(1000));
        Assert.Equal(BattlegroundStatus.WaitJoin, bg.Status);
        Assert.Equal(0u, bg.StartTimeMs);
    }

    // ---------------------------------------------------------------- picking up and capturing

    [Fact]
    public void AllianceTakesTheHordeFlagFromItsBase()
    {
        var (bg, host, ports) = NewWsg();
        StartMatch(bg);
        host.WorldStates.Clear();
        host.Announcements.Clear();
        host.Sounds.Clear();

        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);

        Assert.Equal(WsgFlagState.OnPlayer, bg.FlagState(Team.Horde));
        Assert.Equal(Alliance[0], bg.FlagPicker(Team.Horde));
        Assert.Contains((Alliance[0], 23333u), ports.Casts);
        Assert.Contains((2339u, 2u), host.WorldStates);
        Assert.Contains((1546u, 1u), host.WorldStates);
        Assert.Contains(WarsongGulch.SoundHordeFlagPickedUp, host.Sounds);
        Assert.Equal((BattlegroundTexts.WsPickedUpHordeFlag, BattlegroundChatKind.Alliance, Alliance[0]), host.Announcements.Single());
        Assert.False(bg.IsActiveEvent(WarsongGulch.EventFlagHorde, 0));
    }

    [Fact]
    public void HordeTakesTheAllianceFlagFromItsBase()
    {
        var (bg, host, ports) = NewWsg();
        StartMatch(bg);
        host.WorldStates.Clear();

        bg.OnFlagClicked(Horde[0], AllianceFlagOnBase);

        Assert.Equal(WsgFlagState.OnPlayer, bg.FlagState(Team.Alliance));
        Assert.Equal(Horde[0], bg.FlagPicker(Team.Alliance));
        Assert.Contains((Horde[0], 23335u), ports.Casts);
        Assert.Contains((2338u, 2u), host.WorldStates);
        Assert.Contains((1545u, 1u), host.WorldStates);
    }

    [Fact]
    public void OwnTeamCannotTakeItsOwnFlagAndTheWrongFlagObjectIsIgnored()
    {
        var (bg, _, ports) = NewWsg();
        StartMatch(bg);

        bg.OnFlagClicked(Alliance[0], AllianceFlagOnBase);
        bg.OnFlagClicked(Horde[0], HordeFlagOnBase);
        // Alliance player clicks a Horde-team object that is not the Horde base flag event.
        bg.OnFlagClicked(Alliance[0], new WsgFlagObject(WarsongGulch.HordeFlagBaseEntry, WarsongGulch.EventFlagAlliance, false));

        Assert.Equal(WsgFlagState.OnBase, bg.FlagState(Team.Alliance));
        Assert.Equal(WsgFlagState.OnBase, bg.FlagState(Team.Horde));
        Assert.Empty(ports.Casts);
    }

    [Fact]
    public void FlagsCannotBeTakenBeforeTheMatchIsInProgress()
    {
        var (bg, _, ports) = NewWsg();
        bg.StartBattleground();
        bg.IncreaseInvitedCount(Team.Alliance);
        bg.AddPlayer(Alliance[0], Team.Alliance);
        bg.Update(1000);

        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);

        Assert.Equal(WsgFlagState.OnBase, bg.FlagState(Team.Horde));
        Assert.Empty(ports.Casts);
    }

    [Fact]
    public void CaptureIsRefusedWhileTheCapturersOwnFlagIsNotOnBase()
    {
        var (bg, host, _) = NewWsg();
        StartMatch(bg);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);     // Alliance carries the Horde flag
        bg.OnFlagClicked(Horde[0], AllianceFlagOnBase);     // Horde carries the Alliance flag: Alliance flag is not on base

        Assert.True(bg.HandleAreaTrigger(Alliance[0], WarsongGulch.AreaTriggerAllianceFlagSpawn));

        Assert.Equal(0, bg.TeamScore(Team.Alliance));
        Assert.Equal(WsgFlagState.OnPlayer, bg.FlagState(Team.Horde));
        Assert.Equal(Alliance[0], bg.FlagPicker(Team.Horde));
        Assert.DoesNotContain(host.Sounds, s => s == WarsongGulch.SoundFlagCapturedAlliance);
    }

    [Fact]
    public void CaptureRequiresTheCarrierToBeTheOneInTheTrigger()
    {
        var (bg, _, _) = NewWsg();
        StartMatch(bg);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);

        Assert.True(bg.HandleAreaTrigger(Alliance[1], WarsongGulch.AreaTriggerAllianceFlagSpawn));
        Assert.Equal(0, bg.TeamScore(Team.Alliance));

        // The Horde trigger is the Horde base: an Alliance carrier there does nothing either.
        Assert.True(bg.HandleAreaTrigger(Alliance[0], WarsongGulch.AreaTriggerHordeFlagSpawn));
        Assert.Equal(0, bg.TeamScore(Team.Alliance));
    }

    [Fact]
    public void ACaptureScoresRewardsAndStartsTheFlagRespawnTimer()
    {
        var (bg, host, ports) = NewWsg(bracket: 5);
        StartMatch(bg);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);
        host.WorldStates.Clear();
        host.Announcements.Clear();
        host.Sounds.Clear();
        host.Events.Clear();

        Assert.True(bg.HandleAreaTrigger(Alliance[0], WarsongGulch.AreaTriggerAllianceFlagSpawn));

        Assert.Equal(1, bg.TeamScore(Team.Alliance));
        Assert.Equal(WsgFlagState.WaitRespawn, bg.FlagState(Team.Horde));
        Assert.True(bg.FlagPicker(Team.Horde).IsEmpty);
        Assert.Contains((Alliance[0], 23333u), ports.Removed);
        Assert.Equal(1u, ((WsgScore)bg.ScoreOf(Alliance[0])!).FlagCaptures);
        Assert.Equal(0u, ((WsgScore)bg.ScoreOf(Alliance[1])!).FlagCaptures);

        // Reputation 35 with Silverwing Sentinels (890) for every Alliance player, none for the Horde (BattleGroundWS.cpp:212,610).
        Assert.Equal(2, ports.Reputation.Count);
        Assert.All(ports.Reputation, r => Assert.Equal((890u, 35), (r.Faction, r.Amount)));
        Assert.Equal(Alliance.Take(2).ToHashSet(), ports.Reputation.Select(r => r.Player).ToHashSet());

        // Honor 396 for the bracket 5 capture (BattleGroundWS.h:111).
        Assert.Equal(396u, ports.Honor[Alliance[0]]);
        Assert.Equal(396u, ports.Honor[Alliance[1]]);
        Assert.False(ports.Honor.ContainsKey(Horde[0]));
        Assert.Equal(396u, bg.ScoreOf(Alliance[0])!.BonusHonor);

        Assert.Contains(WarsongGulch.SoundFlagCapturedAlliance, host.Sounds);
        Assert.Contains((1546u, 0u), host.WorldStates);
        Assert.Contains((1581u, 1u), host.WorldStates);
        Assert.Contains((2339u, 1u), host.WorldStates);
        Assert.Contains(host.Announcements, a => a.Text == BattlegroundTexts.WsCapturedHordeFlag && a.Kind == BattlegroundChatKind.Alliance && a.Source == Alliance[0]);
        // Both flags are gone after a capture; the Horde flag was already despawned when it was taken, so only the Alliance event changes.
        Assert.Contains((WarsongGulch.EventFlagAlliance, (byte)0, false, true), host.Events);
        Assert.False(bg.IsActiveEvent(WarsongGulch.EventFlagAlliance, 0));
        Assert.False(bg.IsActiveEvent(WarsongGulch.EventFlagHorde, 0));
    }

    [Fact]
    public void HordeCaptureAwardsTheWarsongOutridersAndTheMirroredStates()
    {
        var (bg, host, ports) = NewWsg();
        StartMatch(bg);
        bg.OnFlagClicked(Horde[0], AllianceFlagOnBase);
        host.WorldStates.Clear();

        Assert.True(bg.HandleAreaTrigger(Horde[0], WarsongGulch.AreaTriggerHordeFlagSpawn));

        Assert.Equal(1, bg.TeamScore(Team.Horde));
        Assert.Equal(WsgFlagState.WaitRespawn, bg.FlagState(Team.Alliance));
        Assert.All(ports.Reputation, r => Assert.Equal((889u, 35), (r.Faction, r.Amount)));
        Assert.Contains((Horde[0], 23335u), ports.Removed);
        Assert.Contains((1545u, 0u), host.WorldStates);
        Assert.Contains((1582u, 1u), host.WorldStates);
        Assert.Contains((2338u, 1u), host.WorldStates);
        Assert.Contains(WarsongGulch.SoundFlagCapturedHorde, host.Sounds);
    }

    [Fact]
    public void BattlegroundWeekendRaisesTheCaptureReputationToFortyFive()
    {
        // The weekend is read when the battleground is reset (BattleGroundWS.cpp:608-610), so it is on before the battleground exists.
        var (bg, _, ports) = NewWsg(weekend: true);
        StartMatch(bg);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);

        bg.HandleAreaTrigger(Alliance[0], WarsongGulch.AreaTriggerAllianceFlagSpawn);

        Assert.Equal(2, ports.Reputation.Count);
        Assert.All(ports.Reputation, r => Assert.Equal(45, r.Amount));
    }

    [Fact]
    public void BattlegroundWeekendPaysEveryoneTheCompletionBonusAndTheWinnerTheHolidayHonor()
    {
        var (bg, _, ports) = NewWsg(bracket: 5, weekend: true);
        StartMatch(bg);
        for (int i = 0; i < 3; i++)
        {
            Capture(bg, Alliance[0], Team.Alliance);
            if (i < 2)
            {
                Tick(bg, WarsongGulch.FlagRespawnTimeMs + 1);
            }
        }

        // BattleGroundWS.cpp:622-641: 594 to both sides, then 198 and 396 to the winner.
        Assert.Equal((3 * 396u) + 594u + 198u + 396u, ports.Honor[Alliance[0]]);
        Assert.Equal(594u, ports.Honor[Horde[0]]);
    }

    [Fact]
    public void ACaptureWithNoHonorSinkLeavesTheScoreboardHonorAtZero()
    {
        var (bg, _, ports) = NewWsg();
        ports.HonorAccepted = false;
        StartMatch(bg);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);

        bg.HandleAreaTrigger(Alliance[0], WarsongGulch.AreaTriggerAllianceFlagSpawn);

        Assert.Equal(0u, bg.ScoreOf(Alliance[0])!.BonusHonor);
    }

    // ---------------------------------------------------------------- timers

    [Fact]
    public void TheCapturedFlagRespawnsWhenMoreThanTwentyThreeSecondsHavePassed()
    {
        var (bg, host, _) = NewWsg();
        StartMatch(bg);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);
        bg.HandleAreaTrigger(Alliance[0], WarsongGulch.AreaTriggerAllianceFlagSpawn);
        host.Announcements.Clear();
        host.Sounds.Clear();

        // BattleGroundWS.cpp:55-61: the timer is decremented and the flag respawns when it is below zero.
        Tick(bg, WarsongGulch.FlagRespawnTimeMs);
        Assert.Equal(WsgFlagState.WaitRespawn, bg.FlagState(Team.Horde));
        Tick(bg, 1);
        Assert.Equal(WsgFlagState.OnBase, bg.FlagState(Team.Horde));
        Assert.True(bg.IsActiveEvent(WarsongGulch.EventFlagHorde, 0));
        Assert.True(bg.IsActiveEvent(WarsongGulch.EventFlagAlliance, 0));
        Assert.Contains(host.Announcements, a => a.Text == BattlegroundTexts.WsFlagsPlaced && a.Kind == BattlegroundChatKind.Neutral);
        Assert.Contains(WarsongGulch.SoundFlagsRespawned, host.Sounds);
    }

    [Fact]
    public void ADroppedFlagIsReturnedByTheServerWhenMoreThanTenSecondsHavePassed()
    {
        var (bg, host, ports) = NewWsg();
        StartMatch(bg);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);
        host.WorldStates.Clear();
        host.Announcements.Clear();

        bg.OnPlayerDroppedFlag(Alliance[0]);

        Assert.Equal(WsgFlagState.OnGround, bg.FlagState(Team.Horde));
        Assert.True(bg.FlagPicker(Team.Horde).IsEmpty);
        Assert.Contains((Alliance[0], 23333u), ports.Removed);
        Assert.Contains((Alliance[0], 23334u), ports.Casts);
        Assert.Contains((2339u, 1u), host.WorldStates);
        Assert.Contains((1546u, uint.MaxValue), host.WorldStates);
        Assert.Contains(host.Announcements, a => a.Text == BattlegroundTexts.WsDroppedHordeFlag && a.Kind == BattlegroundChatKind.Horde);

        // The spell effect creates the dropped-flag game object and tells the battleground its guid.
        var groundFlag = ObjectGuid.WithEntry(HighGuid.GameObject, WarsongGulch.HordeFlagGroundEntry, 7);
        bg.SetDroppedFlagGuid(groundFlag, Team.Horde);
        host.WorldStates.Clear();
        host.Announcements.Clear();

        Tick(bg, WarsongGulch.FlagDropTimeMs);
        Assert.Equal(WsgFlagState.OnGround, bg.FlagState(Team.Horde));
        Tick(bg, 1);

        Assert.Equal(WsgFlagState.OnBase, bg.FlagState(Team.Horde));
        Assert.Contains(groundFlag, host.DeletedObjects);
        Assert.Contains((1546u, 0u), host.WorldStates);
        Assert.Contains(host.Announcements, a => a.Text == BattlegroundTexts.WsHordeFlagRespawned);
        Assert.Contains(WarsongGulch.SoundFlagsRespawned, host.Sounds);
        Assert.True(bg.DroppedFlagGuid(Team.Horde).IsEmpty);
    }

    [Fact]
    public void ADroppedFlagCanBePickedUpAgainByTheEnemyWithinTenYardsAndTheTimerIsMoot()
    {
        var (bg, host, ports) = NewWsg();
        StartMatch(bg);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);
        bg.OnPlayerDroppedFlag(Alliance[0]);
        ports.Casts.Clear();
        host.Announcements.Clear();

        bg.OnFlagClicked(Alliance[1], HordeFlagOnGround);

        Assert.Equal(WsgFlagState.OnPlayer, bg.FlagState(Team.Horde));
        Assert.Equal(Alliance[1], bg.FlagPicker(Team.Horde));
        Assert.Contains((Alliance[1], 23333u), ports.Casts);
        Assert.Contains(host.Announcements, a => a.Text == BattlegroundTexts.WsPickedUpHordeFlag);

        Tick(bg, 30_000, 100);
        Assert.Equal(WsgFlagState.OnPlayer, bg.FlagState(Team.Horde));
    }

    [Fact]
    public void ADroppedFlagIsReturnedByItsOwnTeamWithinTenYardsAndCountsAsAReturn()
    {
        var (bg, host, _) = NewWsg();
        StartMatch(bg);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);
        bg.OnPlayerDroppedFlag(Alliance[0]);
        host.WorldStates.Clear();
        host.Announcements.Clear();
        host.Sounds.Clear();

        bg.OnFlagClicked(Horde[0], HordeFlagOnGround);

        Assert.Equal(WsgFlagState.OnBase, bg.FlagState(Team.Horde));
        Assert.Equal(1u, ((WsgScore)bg.ScoreOf(Horde[0])!).FlagReturns);
        Assert.Contains(WarsongGulch.SoundFlagReturned, host.Sounds);
        Assert.Contains(host.Announcements, a => a.Text == BattlegroundTexts.WsReturnedHordeFlag && a.Kind == BattlegroundChatKind.Horde);
        // UpdateFlagState(ALLIANCE, WAIT_RESPAWN) is the literal value 1 of the opposite team's icon.
        Assert.Contains((2339u, 1u), host.WorldStates);
    }

    [Fact]
    public void AnEnemyOutOfRangeCannotTouchAGroundFlag()
    {
        var (bg, _, ports) = NewWsg();
        StartMatch(bg);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);
        bg.OnPlayerDroppedFlag(Alliance[0]);
        ports.Casts.Clear();

        bg.OnFlagClicked(Alliance[1], HordeFlagOnGround with { WithinTenYards = false });
        bg.OnFlagClicked(Horde[0], HordeFlagOnGround with { WithinTenYards = false });

        Assert.Equal(WsgFlagState.OnGround, bg.FlagState(Team.Horde));
        Assert.Empty(ports.Casts);
    }

    // ---------------------------------------------------------------- drop causes

    [Fact]
    public void TheCarrierDropsTheFlagWhenKilled()
    {
        var (bg, _, ports) = NewWsg();
        StartMatch(bg);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);

        bg.HandleKillPlayer(Alliance[0], Horde[0]);

        Assert.Equal(WsgFlagState.OnGround, bg.FlagState(Team.Horde));
        Assert.Contains((Alliance[0], 23334u), ports.Casts);
    }

    [Fact]
    public void ANonCarrierDeathDropsNothing()
    {
        var (bg, _, ports) = NewWsg();
        StartMatch(bg);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);

        bg.HandleKillPlayer(Alliance[1], Horde[0]);

        Assert.Equal(WsgFlagState.OnPlayer, bg.FlagState(Team.Horde));
        Assert.DoesNotContain(ports.Casts, c => c.Spell == 23334);
    }

    [Fact]
    public void AnOnlineCarrierWhoLeavesDropsTheFlag()
    {
        var (bg, _, ports) = NewWsg();
        StartMatch(bg, perTeam: 3);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);

        bg.RemovePlayerAtLeave(Alliance[0], teleportToEntryPoint: true, sendStatus: true);

        Assert.Equal(WsgFlagState.OnGround, bg.FlagState(Team.Horde));
        Assert.True(bg.FlagPicker(Team.Horde).IsEmpty);
        Assert.Contains((Alliance[0], 23334u), ports.Casts);
    }

    [Fact]
    public void AnOfflineCarrierClearsThePickerAndTheFlagRespawnsAtOnceWithNoDropSpell()
    {
        var (bg, host, ports) = NewWsg();
        StartMatch(bg, perTeam: 3);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);
        ports.Casts.Clear();
        host.Events.Clear();

        bg.RemovePlayerAtLeave(Alliance[0], teleportToEntryPoint: false, sendStatus: false, online: false);

        Assert.Equal(WsgFlagState.OnBase, bg.FlagState(Team.Horde));
        Assert.True(bg.FlagPicker(Team.Horde).IsEmpty);
        Assert.Empty(ports.Casts);
        Assert.Contains((WarsongGulch.EventFlagHorde, (byte)0, true, true), host.Events);
    }

    [Fact]
    public void DroppingWhenNotInProgressOnlyRemovesTheAura()
    {
        var (bg, host, ports) = NewWsg();
        StartMatch(bg, perTeam: 2);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);
        // The Horde side falls below the minimum and the match ends for the Alliance while their carrier still holds the flag.
        bg.RemovePlayerAtLeave(Horde[0], teleportToEntryPoint: true, sendStatus: true);
        Tick(bg, 310_000, 1000);
        Assert.Equal(BattlegroundStatus.WaitLeave, bg.Status);
        Assert.Equal(Alliance[0], bg.FlagPicker(Team.Horde));
        ports.Casts.Clear();
        host.WorldStates.Clear();

        bg.OnPlayerDroppedFlag(Alliance[0]);

        // BattleGroundWS.cpp:271-296: no dropped-flag spell, no messages, no state change; just the aura and the picker go.
        Assert.DoesNotContain(ports.Casts, c => c.Spell == 23334);
        Assert.Contains((Alliance[0], 23333u), ports.Removed);
        Assert.True(bg.FlagPicker(Team.Horde).IsEmpty);
        Assert.Empty(host.WorldStates);
    }

    // ---------------------------------------------------------------- the end of the match

    private static void Capture(WarsongGulch bg, ObjectGuid carrier, Team team)
    {
        bg.OnFlagClicked(carrier, team == Team.Alliance ? HordeFlagOnBase : AllianceFlagOnBase);
        bg.HandleAreaTrigger(carrier, team == Team.Alliance ? WarsongGulch.AreaTriggerAllianceFlagSpawn : WarsongGulch.AreaTriggerHordeFlagSpawn);
    }

    private static void ThreeCaptures(WarsongGulch bg)
    {
        for (int i = 0; i < 3; i++)
        {
            Capture(bg, Alliance[0], Team.Alliance);
            if (i < 2)
            {
                Tick(bg, 24_000, 1000);
            }
        }
    }

    [Fact]
    public void TheThirdCaptureEndsTheMatchAndNoFurtherCaptureCounts()
    {
        var (bg, host, ports) = NewWsg(bracket: 5);
        StartMatch(bg);
        uint startTimeAtEnd = 0;
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(BattlegroundStatus.InProgress, bg.Status);
            Capture(bg, Alliance[0], Team.Alliance);
            Assert.Equal(i + 1, bg.TeamScore(Team.Alliance));
            if (i < 2)
            {
                Tick(bg, WarsongGulch.FlagRespawnTimeMs + 1);
            }
        }

        startTimeAtEnd = bg.StartTimeMs;
        Assert.Equal(BattlegroundStatus.WaitLeave, bg.Status);
        Assert.Equal(BattlegroundWinner.Alliance, bg.Winner);
        Assert.Equal(BattlegroundConstants.TimeToAutoRemoveMs, bg.EndTimeMs);
        Assert.Contains(BattlegroundConstants.SoundAllianceWins, host.Sounds);
        Assert.Contains((2339u, 1u), host.WorldStates);
        Assert.Contains((2338u, 1u), host.WorldStates);
        Assert.Contains(ports.Casts, c => c.Player == Alliance[0] && c.Spell == 24951);
        Assert.Contains(host.Announcements, a => a.Text == BattlegroundTexts.WsAllianceWins);

        // Winner honor 198 (bracket 5) once, in addition to the three capture awards of 396 (BattleGroundWS.h:111-112).
        Assert.Equal((3 * 396u) + 198u, ports.Honor[Alliance[0]]);
        Assert.False(ports.Honor.ContainsKey(Horde[0]));

        // A fourth capture does nothing: the match is no longer in progress.
        Capture(bg, Alliance[0], Team.Alliance);
        Assert.Equal(3, bg.TeamScore(Team.Alliance));
        Assert.Equal(startTimeAtEnd, bg.StartTimeMs);
    }

    [Fact]
    public void TheLoserGetsNoMarkWhenTheMatchLastedTenMinutesOrLess()
    {
        var (bg, host, ports) = NewWsg();
        StartMatch(bg);
        Assert.True(bg.StartTimeMs <= BattlegroundConstants.LoserMarkMinStartTimeMs);
        ThreeCaptures(bg);

        Assert.Equal(BattlegroundStatus.WaitLeave, bg.Status);
        Assert.Contains(ports.Casts, c => c.Spell == 24951 && Alliance.Contains(c.Player));
        Assert.DoesNotContain(ports.Casts, c => Horde.Contains(c.Player));
        Assert.All(host.EndPackets, p => Assert.Equal(Alliance.Contains(p.Player), p.Won));
        Assert.Equal(4, host.EndPackets.Count);
        Assert.All(host.EndPackets, p => Assert.Equal((uint)BattlegroundConstants.TimeToAutoRemoveMs, p.AutoLeave));
    }

    [Fact]
    public void TheLoserGetsTheLoserMarkWhenTheMatchLastedMoreThanTenMinutes()
    {
        var (bg, _, ports) = NewWsg();
        StartMatch(bg);
        Tick(bg, 11 * 60 * 1000, 1000);
        Assert.True(bg.StartTimeMs > BattlegroundConstants.LoserMarkMinStartTimeMs);

        ThreeCaptures(bg);

        Assert.Equal(BattlegroundStatus.WaitLeave, bg.Status);
        Assert.Contains(ports.Casts, c => Horde.Contains(c.Player) && c.Spell == 24950);
        Assert.Contains(ports.Casts, c => Alliance.Contains(c.Player) && c.Spell == 24951);
    }

    [Fact]
    public void PlayersAreRemovedTwoMinutesAfterTheEndWithoutALeftMessageOrAnotherQueueUpdate()
    {
        var (bg, host, ports) = NewWsg();
        StartMatch(bg);
        ThreeCaptures(bg);

        host.Left.Clear();
        int updatesBefore = ports.QueueUpdates;
        Tick(bg, 119_000, 1000);
        Assert.Equal(4, bg.PlayerCount);
        Assert.Empty(host.TeleportedToEntry);

        bg.Update(1000);

        Assert.Equal(0, bg.PlayerCount);
        Assert.Equal(4, host.TeleportedToEntry.Count);
        Assert.Equal(4, host.Cleared.Count);
        Assert.Empty(host.Left);
        Assert.Equal(updatesBefore, ports.QueueUpdates);
    }

    // ---------------------------------------------------------------- premature finish

    [Fact]
    public void ASideBelowTheMinimumForFiveMinutesLosesTheMatch()
    {
        var (bg, _, _) = NewWsg();
        StartMatch(bg, perTeam: 2);
        bg.RemovePlayerAtLeave(Horde[0], teleportToEntryPoint: true, sendStatus: true);

        // The countdown starts on the next update (BattleGround.cpp:320-324) and ends once it is below one tick.
        uint elapsed = 0;
        while (bg.Status == BattlegroundStatus.InProgress && elapsed < 400_000)
        {
            bg.Update(1000);
            elapsed += 1000;
        }

        Assert.Equal(BattlegroundStatus.WaitLeave, bg.Status);
        Assert.Equal(BattlegroundWinner.Alliance, bg.Winner);
        Assert.InRange(elapsed, 299_000u, 303_000u);
    }

    [Fact]
    public void TheCountdownRestartsFromFullWhenTheSideIsRefilled()
    {
        var (bg, _, _) = NewWsg();
        StartMatch(bg, perTeam: 2);
        bg.RemovePlayerAtLeave(Horde[0], teleportToEntryPoint: true, sendStatus: true);
        Tick(bg, 200_000, 1000);
        Assert.Equal(BattlegroundStatus.InProgress, bg.Status);

        bg.IncreaseInvitedCount(Team.Horde);
        bg.AddPlayer(Horde[5], Team.Horde);
        Tick(bg, 1000, 1000);
        bg.RemovePlayerAtLeave(Horde[5], teleportToEntryPoint: true, sendStatus: true);

        // 200 s already passed; had the countdown continued it would end within 100 s.
        Tick(bg, 250_000, 1000);
        Assert.Equal(BattlegroundStatus.InProgress, bg.Status);
        Tick(bg, 60_000, 1000);
        Assert.Equal(BattlegroundStatus.WaitLeave, bg.Status);
    }

    [Fact]
    public void BothSidesBelowTheMinimumEndsWithNoWinnerAndNoWinnerText()
    {
        var (bg, host, _) = NewWsg();
        StartMatch(bg, perTeam: 2);
        foreach (ObjectGuid g in new[] { Alliance[0], Horde[0] })
        {
            bg.RemovePlayerAtLeave(g, teleportToEntryPoint: true, sendStatus: true);
        }

        Tick(bg, 310_000, 1000);

        Assert.Equal(BattlegroundStatus.WaitLeave, bg.Status);
        Assert.Equal(BattlegroundWinner.None, bg.Winner);
        Assert.DoesNotContain(host.Sounds, s => s is BattlegroundConstants.SoundAllianceWins or BattlegroundConstants.SoundHordeWins);
        Assert.DoesNotContain(host.Announcements, a => a.Text is BattlegroundTexts.WsAllianceWins or BattlegroundTexts.WsHordeWins);
    }

    [Fact]
    public void ThePrematureFinishTimerCanBeTurnedOff()
    {
        var (bg, _, _) = NewWsg(options: new BattlegroundOptions { PrematureFinishTimerMs = 0 });
        StartMatch(bg, perTeam: 2);
        bg.RemovePlayerAtLeave(Horde[0], teleportToEntryPoint: true, sendStatus: true);

        Tick(bg, 900_000, 1000);

        Assert.Equal(BattlegroundStatus.InProgress, bg.Status);
    }

    [Fact]
    public void ThePrematureFinishTimerFollowsTheConfiguredValue()
    {
        var (bg, _, _) = NewWsg(options: new BattlegroundOptions { PrematureFinishTimerMs = 30_000 });
        StartMatch(bg, perTeam: 2);
        bg.RemovePlayerAtLeave(Horde[0], teleportToEntryPoint: true, sendStatus: true);

        Tick(bg, 25_000, 1000);
        Assert.Equal(BattlegroundStatus.InProgress, bg.Status);
        Tick(bg, 10_000, 1000);
        Assert.Equal(BattlegroundStatus.WaitLeave, bg.Status);
    }

    // ---------------------------------------------------------------- world states, graveyards, brackets

    private static void AssertStates((uint Id, int Value)[] expected, WarsongGulch bg) => Assert.Equal(expected, bg.InitialWorldStates().ToArray());

    [Fact]
    public void InitialWorldStatesAreTheVmangosTableWithTheLiteralIconValues()
    {
        var (bg, _, _) = NewWsg();
        StartMatch(bg);

        // BattleGroundWS.cpp:693-723, in vmangos' order; 1601 is the capture limit and 2338/2339 are literal icon values 1 and 2.
        AssertStates([(1581u, 0), (1582u, 0), (1545u, 0), (1546u, 0), (1601u, 3), (2339u, 1), (2338u, 1)], bg);

        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);
        AssertStates([(1581u, 0), (1582u, 0), (1545u, 0), (1546u, 1), (1601u, 3), (2339u, 2), (2338u, 1)], bg);

        bg.OnFlagClicked(Horde[0], AllianceFlagOnBase);
        bg.OnPlayerDroppedFlag(Alliance[0]);
        AssertStates([(1581u, 0), (1582u, 0), (1545u, 1), (1546u, -1), (1601u, 3), (2339u, 1), (2338u, 2)], bg);
    }

    [Theory]
    [InlineData(9u, -1)]
    [InlineData(10u, 0)]
    [InlineData(19u, 0)]
    [InlineData(20u, 1)]
    [InlineData(39u, 2)]
    [InlineData(59u, 4)]
    [InlineData(60u, 5)]
    [InlineData(79u, 5)]
    public void TheBracketIsTheLevelOffsetInTens(uint level, int expected) => Assert.Equal(expected, BattlegroundConstants.BracketOfLevel(level, 10));

    [Fact]
    public void TheBracketFollowsTheTemplateMinimumLevelNotALiteral()
    {
        Assert.Equal(0, BattlegroundConstants.BracketOfLevel(20, 20));
        Assert.Equal(-1, BattlegroundConstants.BracketOfLevel(19, 20));
        Assert.Equal(1, BattlegroundConstants.BracketOfLevel(30, 20));
    }

    [Theory]
    [InlineData(0, 48u, 24u)]
    [InlineData(1, 82u, 41u)]
    [InlineData(2, 136u, 68u)]
    [InlineData(3, 226u, 113u)]
    [InlineData(4, 378u, 189u)]
    [InlineData(5, 396u, 198u)]
    public void HonorTablesAreIndexedByBracket(int bracket, uint capture, uint win)
    {
        Assert.Equal(capture, WarsongGulch.FlagCapturedHonor(bracket));
        Assert.Equal(win, WarsongGulch.WinMatchHonor(bracket));
        Assert.Equal(capture, WarsongGulch.WinMatchHonorHoliday(bracket));
        Assert.Equal(new uint[] { 72, 123, 204, 339, 567, 594 }[bracket], WarsongGulch.WinMatchHonorBonusCompleteHoliday(bracket));
    }

    [Fact]
    public void CaptureHonorUsesTheBattlegroundsBracket()
    {
        var (bg, _, ports) = NewWsg(bracket: 2);
        StartMatch(bg);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);

        bg.HandleAreaTrigger(Alliance[0], WarsongGulch.AreaTriggerAllianceFlagSpawn);

        Assert.Equal(136u, ports.Honor[Alliance[0]]);
    }

    [Fact]
    public void TheGraveyardIsTheFlagRoomUntilTheMatchRunsThenTheMainOne()
    {
        var (bg, _, _) = NewWsg();
        bg.StartBattleground();
        Assert.Equal(769u, bg.ClosestGraveyard(Team.Alliance));
        Assert.Equal(770u, bg.ClosestGraveyard(Team.Horde));

        StartMatch(bg);
        Assert.Equal(771u, bg.ClosestGraveyard(Team.Alliance));
        Assert.Equal(772u, bg.ClosestGraveyard(Team.Horde));
    }

    // ---------------------------------------------------------------- exits and unhandled triggers

    [Fact]
    public void ExitTriggersLeaveForTheirOwnTeamOnly()
    {
        var (bg, host, _) = NewWsg();
        StartMatch(bg);

        Assert.True(bg.HandleAreaTrigger(Horde[0], 3669));
        Assert.False(bg.HandleAreaTrigger(Alliance[0], 3669));
        Assert.True(bg.HandleAreaTrigger(Alliance[0], 3671));
        Assert.False(bg.HandleAreaTrigger(Horde[1], 3671));

        Assert.Equal([Horde[0], Alliance[0]], host.Leaves);
    }

    [Theory]
    [InlineData(3686u)]
    [InlineData(3687u)]
    [InlineData(3706u)]
    [InlineData(3707u)]
    [InlineData(3708u)]
    [InlineData(3709u)]
    public void PowerUpTriggersAreHandledAndDoNothing(uint trigger)
    {
        var (bg, host, _) = NewWsg();
        StartMatch(bg);
        Assert.True(bg.HandleAreaTrigger(Alliance[0], trigger));
        Assert.Empty(host.Unhandled);
    }

    [Fact]
    public void AnUnknownTriggerIsReportedAndNotHandled()
    {
        var (bg, host, _) = NewWsg();
        StartMatch(bg);
        Assert.False(bg.HandleAreaTrigger(Alliance[0], 4628));
        Assert.Equal((Alliance[0], 4628u), host.Unhandled.Single());
    }

    [Fact]
    public void TriggersAreIgnoredOutsideAMatchInProgress()
    {
        var (bg, host, _) = NewWsg();
        bg.StartBattleground();
        bg.IncreaseInvitedCount(Team.Alliance);
        bg.AddPlayer(Alliance[0], Team.Alliance);

        Assert.False(bg.HandleAreaTrigger(Alliance[0], 3669));
        Assert.Empty(host.Leaves);
    }

    // ---------------------------------------------------------------- forcing the base trigger after a respawn

    [Fact]
    public void ACarrierStandingInTheBaseCapturesAtOnceWhenItsOwnFlagIsBackOnBase()
    {
        var (bg, host, _) = NewWsg();
        StartMatch(bg, perTeam: 2);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);   // Alliance carries the Horde flag
        bg.OnFlagClicked(Horde[0], AllianceFlagOnBase);   // Horde carries the Alliance flag
        bg.OnPlayerDroppedFlag(Horde[0]);                 // dropped: Alliance flag on the ground
        host.InTriggers.Add((Alliance[0], WarsongGulch.AreaTriggerAllianceFlagSpawn));

        // The Alliance return their flag: the carrier is already in the base, so ForceFlagAreaTrigger captures (BattleGroundWS.cpp:183-191, 413-417).
        bg.OnFlagClicked(Alliance[1], AllianceFlagOnGround);

        Assert.Equal(1, bg.TeamScore(Team.Alliance));
        Assert.Equal(WsgFlagState.WaitRespawn, bg.FlagState(Team.Horde));
    }

    // ---------------------------------------------------------------- review findings (misc-systems lane)

    [Fact]
    public void AfterACapture_TheDespawnedHomeFlagCannotBeTakenUntilBothFlagsRespawn()
    {
        var (bg, _, ports) = NewWsg();
        StartMatch(bg);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);
        bg.HandleAreaTrigger(Alliance[0], WarsongGulch.AreaTriggerAllianceFlagSpawn);
        Assert.False(bg.IsActiveEvent(WarsongGulch.EventFlagAlliance, 0));
        ports.Casts.Clear();

        // A capture despawns both flag stands for the 23 s respawn (BattleGroundWS.cpp:225-227); a despawned stand is not a flag to take.
        bg.OnFlagClicked(Horde[0], AllianceFlagOnBase);

        Assert.Equal(WsgFlagState.OnBase, bg.FlagState(Team.Alliance));
        Assert.True(bg.FlagPicker(Team.Alliance).IsEmpty);
        Assert.Empty(ports.Casts);

        Tick(bg, WarsongGulch.FlagRespawnTimeMs + 1);
        bg.OnFlagClicked(Horde[0], AllianceFlagOnBase);
        Assert.Equal(Horde[0], bg.FlagPicker(Team.Alliance));
    }

    [Fact]
    public void ReturningADroppedFlag_ClearsItsTakenWorldState()
    {
        var (bg, host, _) = NewWsg();
        StartMatch(bg);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);
        bg.OnPlayerDroppedFlag(Alliance[0]);
        host.WorldStates.Clear();

        bg.OnFlagClicked(Horde[0], HordeFlagOnGround);

        // The timeout return writes 0 (BattleGroundWS.cpp:157); a player return must too (mangos-classic ProcessDroppedFlagActions sets it ON_BASE).
        Assert.Contains((WarsongGulch.WorldStateFlagTakenHorde, 0u), host.WorldStates);
    }

    [Fact]
    public void AnOfflineCarrier_PutsTheFlagBackWithTheIconAndTakenWorldStatesCleared()
    {
        var (bg, host, _) = NewWsg();
        StartMatch(bg, perTeam: 3);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);
        host.WorldStates.Clear();

        bg.RemovePlayerAtLeave(Alliance[0], teleportToEntryPoint: false, sendStatus: false, online: false);

        Assert.Contains((WarsongGulch.WorldStateFlagStateAlliance, 1u), host.WorldStates);
        Assert.Contains((WarsongGulch.WorldStateFlagTakenHorde, 0u), host.WorldStates);
    }

    [Fact]
    public void ACaptureEvent_ScoresOnlyForTheCarrier_AndOnlyWithItsOwnFlagOnBase()
    {
        var (bg, _, ports) = NewWsg();
        StartMatch(bg);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);

        bg.OnPlayerCapturedFlag(Alliance[1]);   // a teammate who carries nothing

        Assert.Equal(0, bg.TeamScore(Team.Alliance));
        Assert.Equal(Alliance[0], bg.FlagPicker(Team.Horde));
        Assert.Equal(WsgFlagState.OnPlayer, bg.FlagState(Team.Horde));
        Assert.Empty(ports.Removed);

        bg.OnFlagClicked(Horde[0], AllianceFlagOnBase);
        bg.OnPlayerCapturedFlag(Alliance[0]);   // the carrier, but the Alliance flag is away

        Assert.Equal(0, bg.TeamScore(Team.Alliance));
        Assert.Equal(Alliance[0], bg.FlagPicker(Team.Horde));
    }

    [Fact]
    public void ADropAfterTheEnd_DoesNotLeaveTheFlagCarriedWithoutACarrier()
    {
        var (bg, host, _) = NewWsg();
        StartMatch(bg, perTeam: 2);
        bg.OnFlagClicked(Alliance[0], HordeFlagOnBase);
        bg.RemovePlayerAtLeave(Horde[0], teleportToEntryPoint: true, sendStatus: true);
        Tick(bg, 310_000, 1000);
        Assert.Equal(BattlegroundStatus.WaitLeave, bg.Status);
        host.WorldStates.Clear();

        bg.OnPlayerDroppedFlag(Alliance[0]);

        Assert.True(bg.FlagPicker(Team.Horde).IsEmpty);
        Assert.Equal(WsgFlagState.OnBase, bg.FlagState(Team.Horde));
        Assert.Contains((WarsongGulch.WorldStateFlagTakenHorde, 0), bg.InitialWorldStates());
        Assert.Contains((WarsongGulch.WorldStateFlagStateAlliance, 1), bg.InitialWorldStates());
        Assert.Empty(host.WorldStates);   // vmangos sends nothing once the match is over (BattleGroundWS.cpp:271-296)
    }
}
