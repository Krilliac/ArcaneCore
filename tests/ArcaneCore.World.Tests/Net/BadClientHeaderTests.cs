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
