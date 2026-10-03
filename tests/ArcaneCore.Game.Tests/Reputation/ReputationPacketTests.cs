using System.Buffers.Binary;
using ArcaneCore.Game.Reputation;
using Xunit;
using static ArcaneCore.Game.Tests.Reputation.ReputationFixtures;

namespace ArcaneCore.Game.Tests.Reputation;

/// <summary>Build-5875 reputation packet layouts (vmangos ReputationMgr/Misc.cpp; gtker wowm for SMSG).</summary>
public sealed class ReputationPacketTests
{
    [Fact]
    public void InitializeFactions_WithoutState_IsSixtyFourEmptySlots()
    {
        byte[] packet = ReputationPackets.InitializeFactions(null);
        Assert.Equal(4 + (64 * 5), packet.Length);
        Assert.Equal(64u, BinaryPrimitives.ReadUInt32LittleEndian(packet));
        Assert.All(packet.Skip(4), b => Assert.Equal(0, b));
    }

    [Fact]
    public void InitializeFactions_CarriesFlagsAndRelativeStandingBySlot_AndClearsPendingSends()
    {
        PlayerReputation rep = Human();
        rep.Apply(Get(BootyBay), -300, incremental: true);
        rep.Apply(Get(ClassBased), 1000, incremental: true);
        byte[] packet = ReputationPackets.InitializeFactions(rep);
        Assert.Equal(324, packet.Length);
        (byte Flags, int Standing) Slot(int slot) => (packet[4 + (slot * 5)], BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(5 + (slot * 5))));
        Assert.Equal(((byte)0x01, -300), Slot(0));
        Assert.Equal(((byte)0x08, 0), Slot(5));
        Assert.Equal(((byte)0x01, 1000), Slot(6)); // relative to the warrior base 500 (reputation 1500)
        Assert.Equal(((byte)0x11, 0), Slot(7));
        Assert.Equal(((byte)0x04, 0), Slot(10));
        Assert.Equal(((byte)0, 0), Slot(63));
        Assert.Equal([(7, 0)], rep.TakeStandingUpdate(rep.State(Get(Stormwind))!));
    }

    [Fact]
    public void StandingAndVisible_UseU32SlotsAndSignedStandings()
    {
        Assert.Equal([2, 0, 0, 0, 7, 0, 0, 0, 0xFB, 0xFF, 0xFF, 0xFF, 63, 0, 0, 0, 3, 0, 0, 0],
            ReputationPackets.SetFactionStanding([(7, -5), (63, 3)]));
        Assert.Equal([0, 0, 0, 0], ReputationPackets.SetFactionStanding([]));
        Assert.Equal([9, 0, 0, 0], ReputationPackets.SetFactionVisible(9));
    }

    [Fact]
    public void AtWarAndInactive_ReadExactlyU32SlotAndByte()
    {
        Assert.True(ReputationPackets.TryReadSetAtWar([7, 0, 0, 0, 1], out int slot, out bool flag));
        Assert.Equal((7, true), (slot, flag));
        Assert.True(ReputationPackets.TryReadSetAtWar([63, 0, 0, 0, 0], out slot, out flag));
        Assert.Equal((63, false), (slot, flag));
        Assert.True(ReputationPackets.TryReadSetInactive([3, 0, 0, 0, 0xFF], out slot, out flag));
        Assert.Equal((3, true), (slot, flag));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 7, 0, 1 })]          // gtker's u16 shape is not accepted
    [InlineData(new byte[] { 7, 0, 0, 0 })]
    [InlineData(new byte[] { 7, 0, 0, 0, 1, 0 })]
    [InlineData(new byte[] { 64, 0, 0, 0, 1 })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 1 })]
    public void AtWarAndInactive_RejectOtherLengthsAndSlots(byte[] payload)
    {
        Assert.False(ReputationPackets.TryReadSetAtWar(payload, out _, out _));
        Assert.False(ReputationPackets.TryReadSetInactive(payload, out _, out _));
    }

    [Fact]
    public void SetWatched_ReadsExactlyOneSignedSlot()
    {
        Assert.True(ReputationPackets.TryReadSetWatched([0xFF, 0xFF, 0xFF, 0xFF], out int slot));
        Assert.Equal(-1, slot);
        Assert.True(ReputationPackets.TryReadSetWatched([12, 0, 0, 0], out slot));
        Assert.Equal(12, slot);
        Assert.False(ReputationPackets.TryReadSetWatched([12, 0, 0], out _));
        Assert.False(ReputationPackets.TryReadSetWatched([12, 0, 0, 0, 0], out _));
    }
}
