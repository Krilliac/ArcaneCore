using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// The once-a-second group stats pass keeps two snapshots per grouped player and refills the spare in place
/// (worldthread audit item 14.1): the packets are unchanged and a steady pass allocates nothing.
/// </summary>
public sealed class GroupStatsSnapshotReuseTests
{
    private static void MakeParty(SocialFixture f, Player leader, params Player[] members)
    {
        foreach (Player member in members)
        {
            f.Context.Groups.Invite(leader, member.Name);
            f.Context.Groups.Accept(member);
        }
    }

    [Fact]
    public void AMembersHpChange_SendsOnce_AndAnUnchangedMemberSendsNothing()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2, x: 5000, y: 5000);
        MakeParty(f, a, b);
        b.VisibleObjects.Remove(a.Guid);
        a.VisibleObjects.Remove(b.Guid);
        f.Context.Groups.UpdateOutOfRangeStats();
        f.ClearAll();
        uint fullHp = a.Health;

        // Several passes so the two buffers swap more than once; each change is reported exactly once.
        for (int round = 0; round < 4; round++)
        {
            a.Health = round % 2 == 0 ? fullHp - 1 : fullHp;
            f.Context.Groups.UpdateOutOfRangeStats();

            var reader = new PacketReader(f.Single(b, WorldOpcode.SmsgPartyMemberStats));
            Assert.Equal(a.Guid.Value, reader.ReadPackedGuid());
            Assert.Equal((uint)GroupUpdateFlags.CurrentHp, reader.ReadUInt32());
            Assert.Equal((ushort)a.Health, reader.ReadUInt16());
            Assert.Equal(0, reader.Remaining);
            f.ClearAll();

            f.Context.Groups.UpdateOutOfRangeStats();
            Assert.Empty(f.Sent(b, WorldOpcode.SmsgPartyMemberStats));
            Assert.Empty(f.Sent(a, WorldOpcode.SmsgPartyMemberStats));
        }
    }

    [Fact]
    public void AuraSlotChangesAndRemovals_AreStillReportedAfterTheBuffersSwap()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2, x: 5000, y: 5000);
        MakeParty(f, a, b);
        b.VisibleObjects.Remove(a.Guid);
        f.Context.Groups.UpdateOutOfRangeStats();

        a.SetUInt32(UpdateFields.UnitFieldAura + 2, 12345);
        f.Context.Groups.UpdateOutOfRangeStats();
        f.Context.Groups.UpdateOutOfRangeStats();
        f.ClearAll();

        a.SetUInt32(UpdateFields.UnitFieldAura + 2, 0);
        f.Context.Groups.UpdateOutOfRangeStats();

        var reader = new PacketReader(f.Single(b, WorldOpcode.SmsgPartyMemberStats));
        Assert.Equal(a.Guid.Value, reader.ReadPackedGuid());
        Assert.Equal((uint)GroupUpdateFlags.Auras, reader.ReadUInt32());
        Assert.Equal(1u << 2, reader.ReadUInt32());
        Assert.Equal((ushort)0, reader.ReadUInt16());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void ASteadyStatsPass_OverAGroupedFleet_AllocatesNothing()
    {
        using var f = new SocialFixture();
        uint next = 1;
        for (int partyIndex = 0; partyIndex < 8; partyIndex++)
        {
            Player leader = f.AddPlayer(next++, x: 5000 * partyIndex);
            for (int i = 0; i < 4; i++)
            {
                Player member = f.AddPlayer(next++, x: 5000 * partyIndex + 1 + i);
                MakeParty(f, leader, member);
            }
        }

        // The first passes create the buffers and send the initial stats.
        f.Context.Groups.UpdateOutOfRangeStats();
        f.Context.Groups.UpdateOutOfRangeStats();

        AllocationProbe.AssertEveryCleanWindowAtMost(
            () => f.Context.Groups.UpdateOutOfRangeStats(), 0, "steady group stats pass over 40 grouped players");
    }
}
