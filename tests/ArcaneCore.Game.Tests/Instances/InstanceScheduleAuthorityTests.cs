using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Instances.InstanceFixture;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// In a cluster only one node holds the instances realm authority lease, but the node that hosts an instance map is usually another one.
/// <see cref="ArcaneCore.Game.Instances.InstanceManager.UpdateSchedule"/> without realm authority must still do the local effects for the maps
/// it hosts (warnings, unbind, teleport home, unload) and leave the realm-wide raid reset time row to the holder.
/// </summary>
public sealed class InstanceScheduleAuthorityTests
{
    private const long Day = 86400;

    private const long FirstRaidReset = Start + (7 * Day) + (4 * 3600);

    [Fact]
    public void NodeWithoutRealmAuthority_StillWarnsItsOwnPlayers()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2);
        f.RaidGroup(a, b);
        Assert.True(f.EnterRaid(a));
        f.ClearAll();

        f.Now = FirstRaidReset - 900;
        f.Manager.UpdateSchedule(realmAuthority: false);

        Assert.Single(f.Sent(a, WorldOpcode.SmsgRaidInstanceMessage));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GlobalRaidReset_RunsLocallyOnEveryNode_ButOnlyTheHolderWritesTheNextResetTime(bool realmAuthority)
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2);
        f.RaidGroup(a, b);
        Assert.True(f.EnterRaid(a));
        Map map = a.Map!;
        f.Manager.PermBindAllPlayers(map, a);
        Assert.Equal(FirstRaidReset, f.Persistence.ResetTimes[Raid]); // written when the schedule was created at load

        f.Now = FirstRaidReset - 60;
        f.Manager.UpdateSchedule(realmAuthority);

        // The node hosting the map unbinds, marks the map for reset and the schedule moves on, whoever holds the lease.
        Assert.Null(f.Manager.GetPlayerBind(a.Guid, Raid));
        Assert.True(f.Manager.StateOf(map)!.ResetAfterUnload);
        long next = f.Manager.GetRaidResetTime(Raid);
        Assert.True(next > FirstRaidReset);

        // The realm-wide row is written by the holder only.
        Assert.Equal(realmAuthority ? next : FirstRaidReset, f.Persistence.ResetTimes[Raid]);
    }
}
