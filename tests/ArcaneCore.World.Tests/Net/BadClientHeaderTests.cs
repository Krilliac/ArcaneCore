using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Net;

/// <summary><see cref="BadClientHeader"/>: the three classes of a rejected client header and the plaintext-parse note of a desync.</summary>
public sealed class BadClientHeaderTests
{
    [Fact]
    public void KnownLowOpcodeWithJunkHighBits_IsClientMalformedOpcode()
    {
        BadClientHeaderKind kind = BadClientHeader.Classify(4, 0x3CE400DC);
        Assert.Equal(BadClientHeaderKind.ClientMalformedOpcode, kind);
        Assert.Equal("client-malformed-opcode", BadClientHeader.Label(kind));
        string detail = BadClientHeader.Describe(kind, new byte[6], 0x3CE400DC);
        Assert.Contains("MSG_MOVE_WORLDPORT_ACK", detail, StringComparison.Ordinal);
        Assert.Contains("0x3CE4", detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(0x2801)]
    [InlineData(0xFFFF)]
    public void SizeOutOfRange_IsCipherDesyncOrPlaintext(int size)
    {
        BadClientHeaderKind kind = BadClientHeader.Classify((ushort)size, 0x00DC);
        Assert.Equal(BadClientHeaderKind.CipherDesyncOrPlaintext, kind);
        Assert.Equal("cipher-desync-or-plaintext", BadClientHeader.Label(kind));
    }

    [Fact]
    public void SizeInRangeButLowOpcodeUnknown_IsUnknownOpcode()
    {
        // 0xFFFF is above the last opcode (SMSG_DEFENSE_MESSAGE = 827).
        BadClientHeaderKind kind = BadClientHeader.Classify(4, 0x1234FFFF);
        Assert.Equal(BadClientHeaderKind.UnknownOpcode, kind);
        Assert.Equal("unknown-opcode", BadClientHeader.Label(kind));
        Assert.Contains("0x1234FFFF", BadClientHeader.Describe(kind, new byte[6], 0x1234FFFF), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatWarning_PutsEachFieldInItsOwnPlace()
    {
        var trail = new ClientFrameTrail();
        trail.Record(WorldOpcode.CmsgPing, 10, 1000, cipherEngaged: true);
        trail.Record(WorldOpcode.CmsgCharEnum, 6, 1200, cipherEngaged: true);
        trail.Record(WorldOpcode.CmsgAuthSession, 99, 1300, cipherEngaged: false); // before the cipher engaged: not counted
        byte[] cipher = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66];
        byte[] plain = [0x00, 0x04, 0xDC, 0x00, 0xE4, 0x3C];

        string text = BadClientHeader.FormatWarning("9.8.7.6:5", 4, 0x3CE400DC, cipher, plain, (Index: 37, Previous: 201), trail, 1750, "InWorld", teleportPending: true);

        Assert.StartsWith("[9.8.7.6:5] bad packet header client-malformed-opcode (", text, StringComparison.Ordinal);
        Assert.Contains("size 4, opcode 0x3CE400DC, ", text, StringComparison.Ordinal);
        Assert.Contains("cipher 112233445566, plain 0004DC00E43C, ", text, StringComparison.Ordinal);
        Assert.Contains("recv cipher index 37 previous 201, ", text, StringComparison.Ordinal);
        Assert.Contains("2 frames / 16 bytes since the cipher engaged, ", text, StringComparison.Ordinal);
        Assert.Contains("last good CMSG_AUTH_SESSION 450 ms ago, ", text, StringComparison.Ordinal);
        Assert.Contains("state InWorld, ", text, StringComparison.Ordinal);
        Assert.EndsWith("teleport pending True; disconnecting", text, StringComparison.Ordinal);

        string none = BadClientHeader.FormatWarning("e", 4, 0x3CE400DC, cipher, plain, (0, 0), new ClientFrameTrail(), 5, "Connected", teleportPending: false);
        Assert.Contains("last good none, ", none, StringComparison.Ordinal);
        Assert.EndsWith("teleport pending False; disconnecting", none, StringComparison.Ordinal);
    }

    [Fact]
    public void Desync_ReportsWhetherTheCiphertextIsAValidPlaintextHeader()
    {
        // Ciphertext 00 04 DC 00 00 00 is a valid plaintext header (size 4, MSG_MOVE_WORLDPORT_ACK): a client that never engaged its cipher.
        byte[] valid = [0x00, 0x04, 0xDC, 0x00, 0x00, 0x00];
        string yes = BadClientHeader.Describe(BadClientHeaderKind.CipherDesyncOrPlaintext, valid, 0xFFFF0001);
        Assert.Equal("ciphertext parses as plaintext: yes (size 4, MSG_MOVE_WORLDPORT_ACK)", yes);

        byte[] junk = [0xFF, 0xFF, 0x01, 0x00, 0x00, 0x00];
        string no = BadClientHeader.Describe(BadClientHeaderKind.CipherDesyncOrPlaintext, junk, 0xFFFF0001);
        Assert.Equal("ciphertext parses as plaintext: no", no);
    }
}
