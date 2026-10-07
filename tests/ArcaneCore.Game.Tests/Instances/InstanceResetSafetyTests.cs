using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Instances.InstanceFixture;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// Resets never pull an instance out from under the players inside it: a personal reset refuses an occupied map the way the
/// group reset does, and the global raid reset unbinds everyone at once but deletes the save only when its map unloads
/// (vmangos <c>MapPersistantStateResetWorker</c>: the state goes in <c>~Map</c>).
/// </summary>
public sealed class InstanceResetSafetyTests
{
    private const long Day = 86400;

    private const long FirstRaidReset = Start + (7 * Day) + (4 * 3600);

    /// <summary>
    /// a ends up bound alone to the instance c is still inside: b (inside its own instance) joins a's group, a and c enter a new
    /// instance, a walks out, b is promoted (a keeps the group's instance as a solo bind, vmangos Group::ChangeLeader), a leaves the group.
    /// </summary>
    private static (Player A, Player C, uint Instance) SoloBindToAnOccupiedInstance(InstanceFixture f)
    {
        Player a = f.AddPlayer(1), b = f.AddPlayer(2), c = f.AddPlayer(3);
        Assert.True(f.EnterDungeon(b)); // b's own instance; b stays inside
        Group group = f.Party(a, b, c);
        Assert.True(f.EnterDungeon(a));
        Assert.True(f.EnterDungeon(c));
        uint id = a.Map!.InstanceId;
        Assert.NotEqual(id, b.Map!.InstanceId);
        Assert.Same(a.Map, c.Map);
        Assert.True(f.LeaveToContinent(a));

        f.Social.Groups.SetLeader(a, b.Guid);
        Assert.True(group.IsLeader(b.Guid));
        Assert.Equal(id, f.Manager.GetPlayerBind(a.Guid, Dungeon)!.Value.Save.InstanceId);
        f.Social.Groups.Leave(a);
        Assert.Null(f.Social.Groups.GetGroup(a.Guid));
        f.ClearAll();
        return (a, c, id);
    }

    [Fact]
    public void SoloReset_OfAnInstanceSomeoneElseIsInside_IsRefused_AndKeepsTheSave()
    {
        using var f = new InstanceFixture();
        (Player a, Player c, uint id) = SoloBindToAnOccupiedInstance(f);
        Map map = c.Map!;

        f.Manager.HandleResetInstances(a);

        Assert.True(f.Manager.IsSaveLive(Dungeon, id));
        Assert.Equal(id, f.Manager.GetPlayerBind(a.Guid, Dungeon)!.Value.Save.InstanceId);
        Assert.False(f.Manager.StateOf(map)!.ResetAfterUnload);
        Assert.Empty(f.Sent(a, WorldOpcode.SmsgInstanceReset));
        Assert.Single(f.Sent(a, WorldOpcode.SmsgInstanceResetFailed));
        Assert.Contains(f.SystemMessages, m => ReferenceEquals(m.Player, c)); // asked to leave (vmangos SendResetFailedNotify)
    }

    [Fact]
    public void GroupJoin_OfAPlayerBoundToAnOccupiedInstance_DropsOnlyItsOwnBind()
    {
        using var f = new InstanceFixture();
        (Player a, Player c, uint id) = SoloBindToAnOccupiedInstance(f);
        Player d = f.AddPlayer(4);
        f.Party(d, a); // a joins d's group: INSTANCE_RESET_GROUP_JOIN

        Assert.Null(f.Manager.GetPlayerBind(a.Guid, Dungeon));
        Assert.True(f.Manager.IsSaveLive(Dungeon, id));
        Assert.False(f.Manager.StateOf(c.Map!)!.ResetAfterUnload);
    }

    [Fact]
    public void GlobalRaidReset_WithAPlayerStillInside_DeletesTheSaveOnlyWhenTheMapUnloads()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2);
        f.RaidGroup(a, b);
        Assert.True(f.EnterRaid(a));
        Map map = a.Map!;
        uint id = map.InstanceId;
        f.Manager.PermBindAllPlayers(map, a);
        var deleted = new List<uint>();
        f.Manager.InstanceDeleted += deleted.Add;
        f.Manager.TeleportToHomebind = _ => false; // the trip home cannot start (unwired or refused)

        f.Now = FirstRaidReset - 60;
        f.Manager.UpdateSchedule();

        // Everyone is unbound at once (vmangos UnbindThisState), the lockout rows go with them,
        // and nobody can enter the instance any more ...
        Assert.Null(f.Manager.GetPlayerBind(a.Guid, Raid));
        Assert.DoesNotContain(f.Persistence.Binds, bind => bind.Instance == id);
        Assert.True(f.Manager.StateOf(map)!.ResetAfterUnload);
        Assert.False(f.Manager.IsInstanceValid(a));

        // ... but the save is deleted only once the map is empty and unloads.
        Assert.Same(map, a.Map);
        Assert.Empty(deleted);
        Assert.Contains(id, f.Persistence.Instances);

        f.Manager.TeleportToHomebind = f.Teleports.TeleportToHomebind;
        Assert.True(f.Teleports.TeleportToHomebind(a));
        f.Tick();
        f.Teleports.HandleWorldportAck(a);
        f.Tick();
        f.Tick();
        Assert.Null(f.World.FindMap(Raid, id));
        Assert.Equal([id], deleted);
        Assert.DoesNotContain(id, f.Persistence.Instances);
        Assert.Null(f.Manager.FindSave(id));
    }
}
