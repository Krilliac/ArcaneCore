using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Classic;
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

    private static (Player Inside, Player Outside) RaidInFight(InstanceFixture f, bool gmOutside = false, bool encounter = true, bool combat = true)
    {
        Player a = f.AddPlayer(1), b = f.AddPlayer(2, gm: gmOutside);
        f.RaidGroup(a, b);
        Assert.True(f.EnterRaid(a));
        if (encounter)
        {
            a.Map!.FindUpdater<InstanceData>()!.SetData(0, EncounterState.InProgress);
        }

        if (combat)
        {
            a.UnitFlags |= UnitFlags.InCombat;
        }

        f.ClearAll();
        return (a, b);
    }

    [Fact]
    public void RaidEncounterInProgress_WithGroupInCombatInside_RefusesWithZoneInCombat()
    {
        using var f = new InstanceFixture();
        (_, Player b) = RaidInFight(f);
        Assert.False(f.EnterRaid(b));
        Assert.Equal((byte)TransferAbortReason.ZoneInCombat, Assert.Single(f.Sent(b, WorldOpcode.SmsgTransferAborted))[0]);
    }

    [Fact]
    public void RaidEntry_IsAllowedWhenNobodyInsideIsInCombat()
    {
        using var f = new InstanceFixture();
        (_, Player b) = RaidInFight(f, combat: false);
        Assert.True(f.EnterRaid(b));
    }

    [Fact]
    public void RaidEntry_IsAllowedWhenNoEncounterIsInProgress()
    {
        using var f = new InstanceFixture();
        (_, Player b) = RaidInFight(f, encounter: false);
        Assert.True(f.EnterRaid(b));
    }

    [Fact]
    public void RaidEntry_IsAllowedForAGhost()
    {
        using var f = new InstanceFixture();
        (_, Player b) = RaidInFight(f);
        b.Combat.DeathState = DeathState.Dead;
        Assert.True(f.Manager.CanEnter(b, InstanceFixture.Raid));
        Assert.Empty(f.Sent(b, WorldOpcode.SmsgTransferAborted));
    }

    [Fact]
    public void RaidEntry_IsAllowedForAGameMaster()
    {
        using var f = new InstanceFixture();
        (_, Player gm) = RaidInFight(f, gmOutside: true);
        Assert.True(f.EnterRaid(gm));
    }

    [Fact]
    public void DungeonEntry_IsNotRefusedForCombat()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2);
        f.Party(a, b);
        Assert.True(f.EnterDungeon(a));
        a.UnitFlags |= UnitFlags.InCombat;
        Assert.True(f.EnterDungeon(b));
    }

    [Fact]
    public void ZulGurub_EncounterInProgress_CountsInProgressAndSpecialOnly()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1);
        var zg = new ZulGurubInstance(a.Map!);
        Assert.False(zg.IsEncounterInProgress);
        zg.SetData(6, EncounterState.Done);
        Assert.False(zg.IsEncounterInProgress);
        zg.SetData(6, EncounterState.InProgress);
        Assert.True(zg.IsEncounterInProgress);
        zg.SetData(6, EncounterState.NotStarted);
        zg.SetData(7, EncounterState.Special);
        Assert.True(zg.IsEncounterInProgress);
    }
}
