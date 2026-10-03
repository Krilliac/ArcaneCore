using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Instances.InstanceFixture;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// After a far teleport the client is told whether it is saved to a raid and which maps
/// (vmangos Player::SendSavedInstances, Player.cpp:16002-16031; wow_messages
/// raid/smsg_update_instance_ownership.wowm = Bool32, smsg_update_last_instance.wowm = u32 map).
/// </summary>
public sealed class SavedInstancesPacketTests
{
    private static uint[] U32s(byte[] payload) =>
        [.. Enumerable.Range(0, payload.Length / 4).Select(i => BitConverter.ToUInt32(payload, i * 4))];

    [Fact]
    public void FarTeleportWithoutPermanentBind_SendsOwnershipZeroAndNoLastInstance()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1);
        f.ClearAll();

        Assert.True(f.EnterDungeon(a));

        byte[] ownership = Assert.Single(f.Sent(a, WorldOpcode.SmsgUpdateInstanceOwnership));
        Assert.Equal([0u], U32s(ownership));
        Assert.Equal(4, ownership.Length);
        Assert.Empty(f.Sent(a, WorldOpcode.SmsgUpdateLastInstance));
    }

    [Fact]
    public void FarTeleportWithPermanentRaidBind_SendsOwnershipOneAndOneLastInstancePerBind()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2);
        f.RaidGroup(a, b);
        Assert.True(f.EnterRaid(a));
        f.Manager.PermBindAllPlayers(a.Map!, a);
        f.ClearAll();

        // Leaving to a continent is also a far teleport: the packets follow it.
        Assert.True(f.LeaveToContinent(a));
        Assert.Equal([1u], U32s(Assert.Single(f.Sent(a, WorldOpcode.SmsgUpdateInstanceOwnership))));
        Assert.Equal([Raid], U32s(Assert.Single(f.Sent(a, WorldOpcode.SmsgUpdateLastInstance))));

        // The other member has no bind of its own: zero.
        Assert.Empty(f.Sent(b, WorldOpcode.SmsgUpdateInstanceOwnership));
        f.ClearAll();
        Assert.True(f.EnterDungeon(a));
        Assert.Equal([1u], U32s(Assert.Single(f.Sent(a, WorldOpcode.SmsgUpdateInstanceOwnership))));
        Assert.Equal([Raid], U32s(Assert.Single(f.Sent(a, WorldOpcode.SmsgUpdateLastInstance))));
    }
}
