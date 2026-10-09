using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using Xunit;
using static ArcaneCore.Game.Tests.Battlegrounds.BgTestData;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>The GM .bg start / .bg stop controls of a match (vmangos HandleBGStartCommand / HandleBGStopCommand, MiscCommands.cpp:1805-1836).</summary>
public sealed class BattlegroundGmControlTests
{
    [Fact]
    public void ForceStart_BeforeTheFirstStartEvent_KeepsTheDelayAtZero_ADeliberateDeviation()
    {
        // vmangos resets the delay to two minutes when the first start event fires after the command (BattleGround.cpp:367-379), so a
        // .bg start that early does nothing there. ArcaneCore remembers the request: the doors still open through the normal events.
        var (bg, host, _) = NewWsg();
        bg.StartBattleground();
        bg.AddPlayer(Alliance[0], Team.Alliance);
        bg.AddPlayer(Horde[0], Team.Horde);
        bg.ForceStart();

        Tick(bg, 20, 1);

        Assert.Equal(BattlegroundStatus.InProgress, bg.Status);
        Assert.True(host.OpenDoorsCalls > 0);
    }

    [Fact]
    public void ForceStop_WithATeamBelowItsMinimum_EndsAfterOneHundredMilliseconds_AndTheTeamAtItsMinimumWins()
    {
        // vmangos StopBattleGround arms the premature finish with 100 ms (BattleGround.cpp:1857-1861); Update ends the match once the
        // timer is below the diff, the winner being the team still at its minimum (BattleGround.cpp:325-335).
        var (bg, _, _) = NewWsg();
        StartMatch(bg, perTeam: 2);
        bg.RemovePlayerAtLeave(Horde[0], teleportToEntryPoint: true, sendStatus: true);

        Assert.True(bg.ForceStop());
        bg.Update(100);
        Assert.Equal(BattlegroundStatus.InProgress, bg.Status);
        bg.Update(1);
        Assert.Equal(BattlegroundStatus.WaitLeave, bg.Status);
        Assert.Equal(BattlegroundWinner.Alliance, bg.Winner);
    }

    [Fact]
    public void ForceStop_WithBothTeamsAtTheirMinimum_IsCancelled_AndALaterLeaverGetsTheFullCountdown()
    {
        // BattleGround.cpp:356-357: outside the premature-finish condition the armed countdown is dropped.
        var (bg, _, _) = NewWsg();
        StartMatch(bg, perTeam: 2);

        Assert.False(bg.ForceStop());
        Tick(bg, 5_000, 100);
        Assert.Equal(BattlegroundStatus.InProgress, bg.Status);
        Assert.Equal(BattlegroundWinner.None, bg.Winner);

        // The stop left nothing behind: a leaver now starts the configured five-minute countdown, not the GM's 100 ms.
        bg.RemovePlayerAtLeave(Horde[0], teleportToEntryPoint: true, sendStatus: true);
        Tick(bg, 60_000, 1000);
        Assert.Equal(BattlegroundStatus.InProgress, bg.Status);
    }

    [Fact]
    public void ForceStop_BeforeTheGatesOpen_IsCancelled()
    {
        var (bg, _, _) = NewWsg();
        bg.StartBattleground();
        bg.IncreaseInvitedCount(Team.Alliance);
        Assert.True(bg.AddPlayer(Alliance[0], Team.Alliance));
        bg.Update(1);
        Assert.Equal(BattlegroundStatus.WaitJoin, bg.Status);

        Assert.False(bg.ForceStop());
        Tick(bg, 1_000, 100);
        Assert.Equal(BattlegroundStatus.WaitJoin, bg.Status);
        Assert.Equal(BattlegroundWinner.None, bg.Winner);
    }

    [Fact]
    public void ForceStop_InAlteracValley_NeverEndsTheMatch_AndPaysNothing()
    {
        // Alterac Valley has no premature finish (BattleGround.cpp:317 GetTypeID() != BATTLEGROUND_AV), so a GM stop is cancelled even
        // with both teams below their minimum: no end-of-match tower and captain honor.
        var host = new RecordingHost();
        var ports = new RecordingPorts();
        var bg = new AlteracValley(AlteracValleyTests.AvTemplate() with { MinPlayersPerTeam = 2 }, bracket: 0, instanceId: 303, clientInstanceId: 1,
            new BattlegroundOptions(), ports.ToPorts(host));
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
        long honorBefore = ports.Honor.Values.Sum(h => (long)h);

        Assert.False(bg.ForceStop());
        Tick(bg, 5_000, 100);

        Assert.Equal(BattlegroundStatus.InProgress, bg.Status);
        Assert.Equal(BattlegroundWinner.None, bg.Winner);
        Assert.Equal(honorBefore, ports.Honor.Values.Sum(h => (long)h));
    }
}
