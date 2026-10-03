using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Instances.InstanceFixture;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// Instance entry rules in vmangos order: raid group (MapManager.cpp:190-205), per-account hourly
/// limit (MapManager.cpp:209-214, AccountMgr.cpp:441-472), then for a loaded map the player cap
/// (not for GMs) and the pending reset (for everybody) (Map.cpp:2124-2146).
/// </summary>
public sealed class EntryRulesTests
{
    private static void Enter(InstanceFixture f, Player p)
    {
        Assert.True(f.EnterDungeon(p));
        f.LeaveToContinent(p);
        f.Manager.HandleResetInstances(p); // solo: resets the empty personal instance, a new id next time
    }

    [Fact]
    public void SixthNewInstanceWithinAnHour_IsRefusedWithTooManyInstances()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1);
        for (int i = 0; i < 5; i++)
        {
            Enter(f, a);
        }

        f.ClearAll();
        Assert.False(f.EnterDungeon(a));
        byte[] abort = Assert.Single(f.Sent(a, WorldOpcode.SmsgTransferAborted));
        Assert.Equal((byte)TransferAbortReason.TooManyInstances, abort[0]);
        Assert.Equal(0u, a.Map!.InstanceId); // still on the continent
    }

    [Fact]
    public void AnHourLater_AnOldEntryFreesASlot()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1);
        for (int i = 0; i < 5; i++)
        {
            Enter(f, a);
            f.Now += 10;
        }

        f.Now += 3600; // vmangos: it2->second + 3600 < now frees one entry
        Assert.True(f.EnterDungeon(a));
    }

    [Fact]
    public void GameMastersAndDisabledLimit_AreNotLimited()
    {
        using var f = new InstanceFixture();
        Player gm = f.AddPlayer(1, gm: true);
        for (int i = 0; i < 7; i++)
        {
            Enter(f, gm);
        }

        using var off = new InstanceFixture(new InstanceOptions { PerHourLimit = 0 });
        Player b = off.AddPlayer(1);
        for (int i = 0; i < 7; i++)
        {
            Enter(off, b);
        }
    }

    [Fact]
    public void ReEnteringAnAlreadyEnteredInstance_IsAlwaysAllowed()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2);
        for (int i = 0; i < 4; i++)
        {
            Enter(f, a);
        }

        f.Party(a, b);
        Assert.True(f.EnterDungeon(a)); // 5th: group bind to a new instance
        Assert.True(f.LeaveToContinent(a));
        for (int i = 0; i < 3; i++)
        {
            Assert.True(f.EnterDungeon(a)); // the same bound instance id: no new slot
            Assert.True(f.LeaveToContinent(a));
        }
    }

    [Fact]
    public void FullInstanceWithPendingReset_AnswersMaxPlayersBeforeNotFound_AndGmGetsNotFound()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2), c = f.AddPlayer(3), d = f.AddPlayer(4), gm = f.AddPlayer(5, gm: true);
        f.Party(a, b, c);
        Assert.True(f.EnterDungeon(a));
        Assert.True(f.EnterDungeon(b));
        Assert.True(f.EnterDungeon(c)); // limit 3: full
        Map map = a.Map!;
        f.Manager.StateOf(map)!.ResetAfterUnload = true;
        f.Social.Groups.Invite(a, d.Name);
        f.Social.Groups.Accept(d);

        f.ClearAll();
        Assert.False(f.EnterDungeon(d));
        Assert.Equal((byte)TransferAbortReason.MaxPlayers, Assert.Single(f.Sent(d, WorldOpcode.SmsgTransferAborted))[0]);

        // vmangos Map.cpp:2141-2146: the pending reset is outside the GM exemption.
        f.Social.Groups.Invite(a, gm.Name);
        f.Social.Groups.Accept(gm);
        f.ClearAll();
        Assert.False(f.EnterDungeon(gm));
        Assert.Equal((byte)TransferAbortReason.NotFound, Assert.Single(f.Sent(gm, WorldOpcode.SmsgTransferAborted))[0]);
    }
}
