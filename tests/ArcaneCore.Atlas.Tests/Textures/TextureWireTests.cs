using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Textures;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Textures;

/// <summary>
/// The ATX1 texture wire (design section 2.2), checked against literal byte offsets written from the layout:
/// <code>
/// u32 magic 0x31585441 ("ATX1") | u16 version 1 | u16 format (1 RGBA8, 2 DXT1, 3 DXT3, 4 DXT5, 5 R8)
/// u16 width | u16 height | u16 layers | u8 levels | u8 flags (bit0 hasAlpha, bit1 alpha1bit, bit2 generated, bit3 srgbHint)
/// u32 jsonBytes                                                                        (header = 20 bytes)
/// per level: u32 byteLength (unpadded) + payload (all layers of that level, layer-major), padded to 4 bytes
/// then the JSON object
/// </code>
/// </summary>
public sealed class TextureWireTests
{
    private static AtxTexture Rgba2x2() => new(
        AtxFormat.Rgba8,
        Width: 2,
        Height: 2,
        Layers: 1,
        AtxFlags.HasAlpha | AtxFlags.SrgbHint,
        Levels: [Enumerable.Range(0, 16).Select(i => (byte)(i + 1)).ToArray(), [201, 202, 203, 204]],
        Meta: new JsonObject { ["key"] = "client:textures\\x.blp", ["mean"] = new JsonArray(1, 2, 3, 4) });

    [Fact]
    public void Header_IsTwentyBytes_WithTheDocumentedFieldsAtTheDocumentedOffsets()
    {
        byte[] wire = TextureWire.Write(Rgba2x2());

        Assert.Equal(Encoding.ASCII.GetBytes("ATX1"), wire[..4]);
        Assert.Equal(0x31585441u, BinaryPrimitives.ReadUInt32LittleEndian(wire));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(6)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(8)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(10)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(12)));
        Assert.Equal(2, wire[14]);
        Assert.Equal(1 | 8, wire[15]);
        uint jsonBytes = BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(16));
        Assert.True(jsonBytes > 0);
        Assert.Equal(20, TextureWire.HeaderBytes);
    }

    [Fact]
    public void Levels_AreLengthPrefixed_AndTheJsonObjectComesLast()
    {
        byte[] wire = TextureWire.Write(Rgba2x2());

        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(20)));
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)(i + 1)).ToArray(), wire[24..40]);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(40)));
        Assert.Equal(new byte[] { 201, 202, 203, 204 }, wire[44..48]);

        uint jsonBytes = BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(16));
        Assert.Equal(48 + (int)jsonBytes, wire.Length);
        JsonObject meta = JsonNode.Parse(wire.AsSpan(48))!.AsObject();
        Assert.Equal("client:textures\\x.blp", meta["key"]!.GetValue<string>());
    }

    [Fact]
    public void OddLevelLengths_ArePaddedToFourBytes_ButTheLengthWordIsTheRealLength()
    {
        // R8 3x1 = 3 bytes (padded to 4), then a 1x1 level of 1 byte (padded to 4).
        var texture = new AtxTexture(AtxFormat.R8, 3, 1, 1, AtxFlags.None, [[7, 8, 9], [5]], new JsonObject());

        byte[] wire = TextureWire.Write(texture);

        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(20)));
        Assert.Equal(new byte[] { 7, 8, 9 }, wire[24..27]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(28)));
        Assert.Equal(5, wire[32]);
        uint jsonBytes = BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(16));
        Assert.Equal(36 + (int)jsonBytes, wire.Length);
        Assert.Equal(0, (wire.Length - (int)jsonBytes) % 4);
        AtxTexture back = TextureWire.Read(wire);
        Assert.Equal(new byte[] { 7, 8, 9 }, back.Levels[0]);
        Assert.Equal(new byte[] { 5 }, back.Levels[1]);
    }

    [Fact]
    public void RoundTrip_PreservesEverything_IncludingLayersAndFlags()
    {
        // A 2-layer DXT1 array, 4x4: 8 bytes per layer per level, layer-major.
        byte[] level0 = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];
        var texture = new AtxTexture(AtxFormat.Dxt1, 4, 4, 2, AtxFlags.HasAlpha | AtxFlags.Alpha1Bit | AtxFlags.Generated, [level0], new JsonObject { ["path"] = "gen" });

        AtxTexture back = TextureWire.Read(TextureWire.Write(texture));

        Assert.Equal(AtxFormat.Dxt1, back.Format);
        Assert.Equal((4, 4, 2), (back.Width, back.Height, back.Layers));
        Assert.Equal(AtxFlags.HasAlpha | AtxFlags.Alpha1Bit | AtxFlags.Generated, back.Flags);
        Assert.Single(back.Levels);
        Assert.Equal(level0, back.Levels[0]);
        Assert.Equal("gen", back.Meta["path"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(AtxFormat.Rgba8, 4, 4, 1, 64)]
    [InlineData(AtxFormat.Dxt1, 4, 4, 1, 8)]
    [InlineData(AtxFormat.Dxt3, 4, 4, 1, 16)]
    [InlineData(AtxFormat.Dxt5, 8, 4, 1, 32)]
    [InlineData(AtxFormat.R8, 4, 4, 1, 16)]
    [InlineData(AtxFormat.Rgba8, 2, 2, 3, 48)]
    [InlineData(AtxFormat.Dxt1, 1, 1, 1, 8)]
    public void Write_AcceptsExactlyTheLevelSizeTheFormatImplies(AtxFormat format, int width, int height, int layers, int expectedBytes)
    {
        var good = new AtxTexture(format, width, height, layers, AtxFlags.None, [new byte[expectedBytes]], new JsonObject());
        var tooShort = good with { Levels = [new byte[expectedBytes - 1]] };
        var tooLong = good with { Levels = [new byte[expectedBytes + 1]] };

        Assert.NotEmpty(TextureWire.Write(good));
        Assert.Throws<InvalidDataException>(() => TextureWire.Write(tooShort));
        Assert.Throws<InvalidDataException>(() => TextureWire.Write(tooLong));
    }

    [Fact]
    public void Read_RefusesCorruptInput()
    {
        byte[] good = TextureWire.Write(Rgba2x2());
        byte[] Patch(Action<byte[]> edit)
        {
            byte[] copy = (byte[])good.Clone();
            edit(copy);
            return copy;
        }

        Assert.Throws<InvalidDataException>(() => TextureWire.Read(Patch(b => b[0] = (byte)'X')));
        Assert.Throws<InvalidDataException>(() => TextureWire.Read(Patch(b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 2))));
        Assert.Throws<InvalidDataException>(() => TextureWire.Read(Patch(b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), 9))));
        Assert.Throws<InvalidDataException>(() => TextureWire.Read(Patch(b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), 0))));
        Assert.Throws<InvalidDataException>(() => TextureWire.Read(Patch(b => b[14] = 0)));
        Assert.Throws<InvalidDataException>(() => TextureWire.Read(Patch(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), 1_000_000))));
        Assert.Throws<InvalidDataException>(() => TextureWire.Read(Patch(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), (uint)b.Length))));
        Assert.Throws<InvalidDataException>(() => TextureWire.Read(good[..^1]));
        Assert.Throws<InvalidDataException>(() => TextureWire.Read(good[..19]));
        Assert.Throws<InvalidDataException>(() => TextureWire.Read([]));
        Assert.Throws<InvalidDataException>(() => TextureWire.Read([.. good, 0]));
    }

    [Fact]
    public void Read_RefusesAJsonTrailerThatIsNotAnObject()
    {
        byte[] good = TextureWire.Write(Rgba2x2());
        uint jsonBytes = BinaryPrimitives.ReadUInt32LittleEndian(good.AsSpan(16));
        byte[] array = [.. good[..(good.Length - (int)jsonBytes)], .. Encoding.UTF8.GetBytes("[1]")];
        BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(16), 3);

        Assert.Throws<InvalidDataException>(() => TextureWire.Read(array));
    }
}
