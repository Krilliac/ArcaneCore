using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using Xunit;
using static ArcaneCore.Game.Tests.Battlegrounds.BgTestData;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>
/// The Alterac Valley assault rules of the match (vmangos BattleGroundAV.cpp and the collector scripts of
/// scripts/battlegrounds/battleground_alterac.cpp): the start-time supply and tamed events, and the collectors' menus and choices that launch
/// the air, cavalry, ground and world-boss assaults.
/// </summary>
public sealed class AlteracValleyAssaultTests
{
    private static (AlteracValley Bg, RecordingHost Host, RecordingPorts Ports) Joined()
    {
        var host = new RecordingHost();
        var ports = new RecordingPorts();
        var bg = new AlteracValley(AlteracValleyTests.AvTemplate(), bracket: 0, instanceId: 302, clientInstanceId: 1, new BattlegroundOptions(),
            ports.ToPorts(host));
        bg.StartBattleground();
        bg.IncreaseInvitedCount(Team.Alliance);
        bg.IncreaseInvitedCount(Team.Horde);
        Assert.True(bg.AddPlayer(Alliance[0], Team.Alliance));
        Assert.True(bg.AddPlayer(Horde[0], Team.Horde));
        return (bg, host, ports);
    }

    private static readonly byte[] s_supplyAndTamedEvents =
    [
        AlteracValley.EventSupplies100, AlteracValley.EventSupplies200, AlteracValley.EventSupplies300, AlteracValley.EventSupplies400,
        AlteracValley.EventTamed05, AlteracValley.EventTamed10, AlteracValley.EventTamed15, AlteracValley.EventTamed20,
    ];

    [Fact]
    public void HalfAMinuteBeforeTheStart_TheSupplyPilesAndTamedMountsOfBothTeamsGo()
    {
        var (bg, host, _) = Joined();
        host.Events.Clear(); // the nodes' initial events

        // The countdown runs from two minutes; nothing is cleared before the 30 second warning.
        while (bg.StartDelayMs > 31_000 || bg.StartDelayMs == 0)
        {
            Assert.True(bg.Update(1000));
            Assert.DoesNotContain(host.Events, e => e.E1 >= AlteracValley.EventSupplies100 && e.E1 <= AlteracValley.EventTamed20 + 1);
        }

        Assert.True(bg.Update(1000));
        Assert.True(bg.Update(1000));
        Assert.True(bg.StartDelayMs <= 30_000);
        Assert.Equal(BattlegroundStatus.WaitJoin, bg.Status);

        foreach (byte ev in s_supplyAndTamedEvents)
        {
            for (byte team = 0; team < 2; team++)
            {
                Assert.Contains(((byte)(ev + team), (byte)2, true, false), host.Events);
                Assert.True(bg.IsActiveEvent((byte)(ev + team), 2));
            }
        }

        // The two guard despawns of the same vmangos block are not ported: in this event layout (15, 0) are the first aid station's own
        // starting defenders (BattleGroundAV.h:235), so they stay.
        Assert.DoesNotContain(host.Events, e => e.E1 is 15 or 28 && !e.Spawn);
        Assert.True(bg.IsActiveEvent(15, 0));

        // It happens once.
        int count = host.Events.Count;
        for (int i = 0; i < 40 && bg.Status != BattlegroundStatus.InProgress; i++)
        {
            Assert.True(bg.Update(1000));
        }

        Assert.Equal(BattlegroundStatus.InProgress, bg.Status);
        Assert.DoesNotContain(host.Events.Skip(count), e => e.E1 >= AlteracValley.EventSupplies100 && e.E1 <= AlteracValley.EventTamed20 + 1);
    }
}
