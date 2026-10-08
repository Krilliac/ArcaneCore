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
/// The refused personal reset (an ArcaneCore choice, see <see cref="InstanceResetSafetyTests"/>): a player whose far teleport out of
/// the instance has not been acknowledged yet still counts as inside it, as the timed reset already holds
/// (<see cref="InstanceManager.UpdateSchedule"/>: <c>TransitCount == 0</c>); a failed arrival sends that player back into the
/// instance (vmangos HandleReturnOnTeleportFail). The "please leave" notice to the players inside is rate limited per instance
/// (<see cref="InstanceOptions.ResetRefusedNoticeSeconds"/>), because a requester outside can repeat CMSG_RESET_INSTANCES at will.
/// </summary>
public sealed class InstanceResetRefusalTests
{
    /// <summary>a is bound alone to the instance c is inside (see InstanceResetSafetyTests).</summary>
    private static (Player A, Player C, uint Instance) SoloBindToAnOccupiedInstance(InstanceFixture f)
    {
        Player a = f.AddPlayer(1), b = f.AddPlayer(2), c = f.AddPlayer(3);
        Assert.True(f.EnterDungeon(b));
        Group group = f.Party(a, b, c);
        Assert.True(f.EnterDungeon(a));
        Assert.True(f.EnterDungeon(c));
        uint id = a.Map!.InstanceId;
        Assert.True(f.LeaveToContinent(a));
        f.Social.Groups.SetLeader(a, b.Guid);
        f.Social.Groups.Leave(a);
        Assert.Null(f.Social.Groups.GetGroup(a.Guid));
        Assert.Equal(id, f.Manager.GetPlayerBind(a.Guid, Dungeon)!.Value.Save.InstanceId);
        f.ClearAll();
        f.SystemMessages.Clear();
        return (a, c, id);
    }

    [Fact]
    public void SoloReset_WhileTheLastPlayerInsideIsStillInTransitOut_IsRefused()
    {
        using var f = new InstanceFixture();
        (Player a, Player c, uint id) = SoloBindToAnOccupiedInstance(f);
        Map map = c.Map!;
        Assert.True(f.Teleports.TeleportTo(c, 0, -11209.6f, 1666.54f, 24.69f, 0));
        f.Tick();
        Assert.Equal(TeleportStage.Far, f.Teleports.StageOf(c)); // SMSG_NEW_WORLD sent, no worldport ack yet
        Assert.Equal((0, 1), (map.PlayerCount, map.TransitCount));

        f.Manager.HandleResetInstances(a);

        Assert.True(f.Manager.IsSaveLive(Dungeon, id));
        Assert.Equal(id, f.Manager.GetPlayerBind(a.Guid, Dungeon)!.Value.Save.InstanceId);
        Assert.False(f.Manager.StateOf(map)!.ResetAfterUnload);
        Assert.Empty(f.Sent(a, WorldOpcode.SmsgInstanceReset));
        Assert.Single(f.Sent(a, WorldOpcode.SmsgInstanceResetFailed));
    }

    [Fact]
    public void RefusedSoloResets_AskThePlayersInsideToLeave_AtMostOncePerInterval()
    {
        using var f = new InstanceFixture();
        (Player a, Player c, uint id) = SoloBindToAnOccupiedInstance(f);

        f.Manager.HandleResetInstances(a);
        f.Manager.HandleResetInstances(a);
        f.Now += f.Manager.Options.ResetRefusedNoticeSeconds - 1;
        f.Manager.HandleResetInstances(a);

        Assert.Equal(3, f.Sent(a, WorldOpcode.SmsgInstanceResetFailed).Count); // the requester hears every refusal
        Assert.Single(f.SystemMessages, m => ReferenceEquals(m.Player, c));

        f.Now += 1;
        f.Manager.HandleResetInstances(a);
        Assert.Equal(2, f.SystemMessages.Count(m => ReferenceEquals(m.Player, c)));
        Assert.True(f.Manager.IsSaveLive(Dungeon, id));
    }

    [Fact]
    public void WithoutARateLimit_EveryRefusalAsksThePlayersInsideToLeave()
    {
        using var f = new InstanceFixture(new InstanceOptions { ResetRefusedNoticeSeconds = 0 });
        (Player a, Player c, _) = SoloBindToAnOccupiedInstance(f);

        f.Manager.HandleResetInstances(a);
        f.Manager.HandleResetInstances(a);

        Assert.Equal(2, f.SystemMessages.Count(m => ReferenceEquals(m.Player, c)));
    }
}
