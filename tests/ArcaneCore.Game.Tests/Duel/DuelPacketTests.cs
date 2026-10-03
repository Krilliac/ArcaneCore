using ArcaneCore.Game.Combat;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Duel;

/// <summary>
/// Duel wire formats against vmangos src/game/Server/Packets/Duel.cpp:20-65 and Spells/SpellEffects.cpp:4732-4736
/// (SMSG_DUEL_REQUESTED = flag guid then challenger guid), gtker wow_messages world/duel/*.wowm.
/// </summary>
public sealed class DuelPacketTests
{
    [Fact]
    public void Requested_IsArbiterThenChallenger_FullGuids()
    {
        byte[] bytes = DuelPackets.Requested(0x0102030405060708UL, 0x1112131415161718UL);

        Assert.Equal(
            [0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01, 0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11],
            bytes);
    }

    [Theory]
    [InlineData(true, (byte)1)]
    [InlineData(false, (byte)0)]
    public void Complete_IsOneByte(bool started, byte expected)
        => Assert.Equal([expected], DuelPackets.Complete(started));

    [Fact]
    public void Winner_IsFledByteThenTwoCStrings()
    {
        Assert.Equal(
            [0, .. "Alice"u8, 0, .. "Bob"u8, 0],
            DuelPackets.Winner(fled: false, winnerName: "Alice", loserName: "Bob"));
        Assert.Equal(
            [1, .. "Alice"u8, 0, .. "Bob"u8, 0],
            DuelPackets.Winner(fled: true, winnerName: "Alice", loserName: "Bob"));
    }

    [Fact]
    public void Countdown_Is3000Milliseconds()
    {
        Assert.Equal([0xB8, 0x0B, 0x00, 0x00], DuelPackets.Countdown(3000));
        Assert.Equal(3000u, DuelPackets.CountdownMilliseconds);
    }

    [Fact]
    public void OutOfBoundsAndInBounds_AreEmptyBodies()
    {
        Assert.Empty(DuelPackets.OutOfBounds());
        Assert.Empty(DuelPackets.InBounds());
    }

    [Fact]
    public void ParseArbiterGuid_ReadsEightBytes_AndReturnsNullWhenShort()
    {
        byte[] guid = [1, 2, 3, 4, 5, 6, 7, 8];
        Assert.Equal(0x0807060504030201UL, DuelPackets.ParseArbiterGuid(guid));
        for (int length = 0; length < 8; length++)
        {
            Assert.Null(DuelPackets.ParseArbiterGuid(guid.AsSpan(0, length)));
        }
    }
}
