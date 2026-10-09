using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using Xunit;
using static ArcaneCore.Game.Tests.Battlegrounds.BgTestData;

namespace ArcaneCore.Game.Tests.Battlegrounds;

public sealed class BattlegroundGmControlTests
{
    [Fact]
    public void ForceStart_UsesTheNormalDoorSequenceWithoutWaitingTwoMinutes()
    {
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
    public void ForceStop_EndsTheMatchAfterOneHundredMilliseconds()
    {
        var (bg, _, _) = NewWsg();
        StartMatch(bg);
        bg.ForceStop();
        bg.Update(99);
        Assert.Equal(BattlegroundStatus.InProgress, bg.Status);
        bg.Update(1);
        Assert.Equal(BattlegroundStatus.WaitLeave, bg.Status);
        Assert.Equal(BattlegroundWinner.None, bg.Winner);
    }
}
