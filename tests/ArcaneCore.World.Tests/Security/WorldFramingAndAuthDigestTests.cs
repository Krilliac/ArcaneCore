using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Xunit;

namespace ArcaneCore.World.Tests.Security;

/// <summary>
/// Pins the world-socket header bounds and the CMSG_AUTH_SESSION digest shared by WorldSession and the gateway
/// (vmangos WorldSocket::handle_input_header / HandleAuthSession). The digest oracle here is independent of
/// the production helper: it uses System.Security.Cryptography.SHA1 over hand-laid bytes.
/// </summary>
public sealed class WorldFramingAndAuthDigestTests
{
    [Fact]
    public void SizeConstants_AreExact_AndWorldSessionForwardsThem()
    {
        Assert.Equal(0x2800, WorldSession.MaxClientPacketSize);
        Assert.Equal(ushort.MaxValue - 2, WorldSession.MaxServerPayload);
        Assert.Equal(10240, WorldSession.MaxClientPacketSize);
        Assert.Equal(65533, WorldSession.MaxServerPayload);
    }

    // Header: size big-endian u16, opcode little-endian u32.
    [Theory]
    [InlineData(new byte[] { 0x00, 0x04, 0x00, 0x00, 0x00, 0x00 }, true, 4, 0u)]
    [InlineData(new byte[] { 0x00, 0x03, 0x00, 0x00, 0x00, 0x00 }, false, 3, 0u)]
    [InlineData(new byte[] { 0x28, 0x00, 0x00, 0x00, 0x00, 0x00 }, true, 0x2800, 0u)]
    [InlineData(new byte[] { 0x28, 0x01, 0x00, 0x00, 0x00, 0x00 }, false, 0x2801, 0u)]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, false, 0, 0u)]
    [InlineData(new byte[] { 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00 }, false, 0xFFFF, 0u)]
    [InlineData(new byte[] { 0x00, 0x04, 0xFF, 0xFF, 0x00, 0x00 }, true, 4, 0xFFFFu)]
    [InlineData(new byte[] { 0x00, 0x04, 0x00, 0x00, 0x01, 0x00 }, false, 4, 0x10000u)]
    [InlineData(new byte[] { 0x00, 0x04, 0x37, 0x04, 0x00, 0x00 }, true, 4, 0x0437u)]
    [InlineData(new byte[] { 0x00, 0x04, 0xFF, 0xFF, 0xFF, 0xFF }, false, 4, 0xFFFFFFFFu)]
    public void TryParseClientHeader_AppliesTheSizeAndOpcodeBounds(byte[] header, bool ok, int size, uint opcode)
    {
        bool result = WorldFrameCodec.TryParseClientHeader(header, out ushort parsedSize, out uint parsedOpcode);

        Assert.Equal(ok, result);
        Assert.Equal(size, parsedSize);
        Assert.Equal(opcode, parsedOpcode);
    }

    [Theory]
    [InlineData(0, new byte[] { 0x00, 0x02 })]
    [InlineData(1, new byte[] { 0x00, 0x03 })]
    [InlineData(254, new byte[] { 0x01, 0x00 })]
    [InlineData(ushort.MaxValue - 2, new byte[] { 0xFF, 0xFF })]
    public void WriteServerSize_IsBigEndianPayloadPlusTwo(int payload, byte[] expected)
    {
        byte[] frame = new byte[4];
        WorldFrameCodec.WriteServerSize(frame, payload);

        Assert.Equal(expected, frame[..2]);
        Assert.Equal(new byte[] { 0, 0 }, frame[2..]);
    }

    [Fact]
    public void WriteServerOpcode_IsLittleEndianU16AfterTheSize_AndLeavesSizeAlone()
    {
        byte[] frame = { 0xAA, 0xBB, 0, 0, 0xCC };
        WorldFrameCodec.WriteServerOpcode(frame, 0x1234);

        Assert.Equal(new byte[] { 0xAA, 0xBB, 0x34, 0x12, 0xCC }, frame);
    }

    [Fact]
    public void AuthDigest_MatchesAnIndependentSha1Layout()
    {
        byte[] key = new byte[40];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(i * 7 + 1);
        const uint clientSeed = 0x1BADD00D;
        const uint serverSeed = 0x01020304;

        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("TESTER"));
        ms.Write(new byte[4]);
        ms.Write(new byte[] { 0x0D, 0xD0, 0xAD, 0x1B }); // clientSeed little-endian
        ms.Write(new byte[] { 0x04, 0x03, 0x02, 0x01 }); // serverSeed little-endian
        ms.Write(key);
        byte[] expected = SHA1.HashData(ms.ToArray());

        Assert.Equal(expected, WowAuthDigest.Compute("TESTER", clientSeed, serverSeed, key));
    }

    [Fact]
    public void AuthDigest_IsSensitiveToSeedOrderAndAccountCase()
    {
        byte[] key = new byte[40];
        byte[] a = WowAuthDigest.Compute("ACC", 1, 2, key);

        Assert.NotEqual(a, WowAuthDigest.Compute("ACC", 2, 1, key));
        Assert.NotEqual(a, WowAuthDigest.Compute("acc", 1, 2, key));
        Assert.Equal(20, a.Length);
    }

    [Fact]
    public void Le_WritesFourLittleEndianBytes()
    {
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, WowAuthDigest.Le(0));
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, WowAuthDigest.Le(0x12345678));
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, WowAuthDigest.Le(uint.MaxValue));
    }
}
