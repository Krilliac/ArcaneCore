using System.Buffers.Binary;
using ArcaneCore.Protocol;
using ArcaneCore.Protocol.Versioning;
using Xunit;

namespace ArcaneCore.World.Tests.Versioning;

public sealed class FrameCodecTests
{
    [Fact]
    public void VanillaAdapterPreservesExistingHeaderBytes()
    {
        Span<byte> header = stackalloc byte[4];
        Vanilla112FrameCodec.Instance.WriteServerHeader(header, 3, 0x1234);
        Assert.Equal(new byte[] { 0, 5, 0x34, 0x12 }, header.ToArray());
        Assert.True(Vanilla112FrameCodec.Instance.TryParseClientHeader(
            new byte[] { 0, 4, 0xDC, 0x00, 0, 0 }, out int size, out uint opcode, out int payload));
        Assert.Equal(4, size);
        Assert.Equal(0xDCu, opcode);
        Assert.Equal(0, payload);
    }

    [Fact]
    public void LanternfallHeaderUsesLittleEndianSizeAndOpcode()
    {
        Span<byte> header = stackalloc byte[LanternfallFrameCodec.HeaderBytes];
        LanternfallFrameCodec.Instance.WriteServerHeader(header, 3, 0x1234);
        Assert.Equal(new byte[] { 5, 0, 0, 0, 0x34, 0x12 }, header.ToArray());
        Assert.True(LanternfallFrameCodec.Instance.TryParseClientHeader(header, out int size, out uint opcode, out int payload));
        Assert.Equal(5, size);
        Assert.Equal(0x1234u, opcode);
        Assert.Equal(3, payload);
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, false)]
    [InlineData(2u, true)]
    [InlineData(16380u, true)]
    [InlineData(16381u, false)]
    [InlineData(uint.MaxValue, false)]
    public void LanternfallParserBoundsSizeBeforePayloadAllocation(uint size, bool valid)
    {
        byte[] header = new byte[LanternfallFrameCodec.HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header, size);
        Assert.Equal(valid, LanternfallFrameCodec.Instance.TryParseClientHeader(header, out _, out _, out _));
    }

    [Fact]
    public void LanternfallParserRejectsTruncatedHeaders()
    {
        Assert.False(LanternfallFrameCodec.Instance.TryParseClientHeader(new byte[5], out _, out _, out _));
        Assert.False(Vanilla112FrameCodec.Instance.TryParseClientHeader(new byte[5], out _, out _, out _));
        Assert.False(Vanilla112FrameCodec.Instance.TryParseClientHeader([], out _, out _, out _));
    }

    [Fact]
    public void LanternfallWriterAllowsTheServerLimitAndRejectsInvalidLengths()
    {
        byte[] header = new byte[LanternfallFrameCodec.HeaderBytes];
        LanternfallFrameCodec codec = LanternfallFrameCodec.Instance;
        codec.WriteServerHeader(header, codec.MaxServerPayloadBytes, 0x1234);
        Assert.Equal((uint)LanternfallFrameCodec.MaxServerFrameBytes - 4, BinaryPrimitives.ReadUInt32LittleEndian(header));
        Assert.Throws<ArgumentOutOfRangeException>(() => codec.WriteServerHeader(header, codec.MaxServerPayloadBytes + 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => codec.WriteServerHeader(header, -1, 0));
        Assert.Throws<ArgumentException>(() => codec.WriteServerHeader(new byte[5], 0, 0));
    }

    [Fact]
    public void InvalidLanternfallSizeRetainsDecodedDiagnosticFields()
    {
        byte[] header = new byte[LanternfallFrameCodec.HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 16381u);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 0x1234);
        Assert.False(LanternfallFrameCodec.Instance.TryParseClientHeader(header, out int size, out uint opcode, out int payload));
        Assert.Equal(16381, size);
        Assert.Equal(0x1234u, opcode);
        Assert.Equal(0, payload);
    }
}
