using ArcaneCore.Atlas.Textures;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Textures;

/// <summary>
/// The S3TC block decoders against hand-built blocks. Every expected value is derived from the format rules, not from a
/// decoder: RGB565 expands to 8 bits as <c>(v &lt;&lt; k) | (v &gt;&gt; (n - k))</c> (so 31 -> 255, 8 -> 66, 63 -> 255); with
/// <c>c0 &gt; c1</c> (compared as 16-bit values) the block has four colours <c>c2 = (2 c0 + c1) / 3</c>, <c>c3 = (c0 + 2 c1) / 3</c>,
/// otherwise three colours <c>c2 = (c0 + c1) / 2</c> and <c>c3</c> transparent black; indices are 2 bits per pixel, row-major, least
/// significant bits first (EXT_texture_compression_s3tc). DXT3 carries 4-bit explicit alpha (low nibble first, expanded x17) and is
/// always four-colour. DXT5 carries two alpha endpoints and 3-bit indices (48 bits, little-endian): with a0 &gt; a1 six interpolants
/// <c>((7 - k) a0 + k a1) / 7</c>, otherwise four interpolants <c>((5 - k) a0 + k a1) / 5</c> then 0 and 255. The colour channel values
/// were chosen so every division is exact, so no rounding convention is being assumed. Output is RGBA8, row-major, top row first.
/// </summary>
public sealed class DxtCodecTests
{
    // Colour words are little-endian in the block: c0 then c1, then four index bytes (one per pixel row).
    // 0xF800 = (255,0,0); 0x001F = (0,0,255); 0x0000 = black; 0x4000 = r5 8 -> (66,0,0); 0x07E0 = (0,255,0).
    private static readonly byte[] IndexRows = [0xE4, 0x1B, 0x00, 0xFF]; // rows [0,1,2,3], [3,2,1,0], [0,0,0,0], [3,3,3,3]

    private static byte[] Block(ushort c0, ushort c1) => [(byte)(c0 & 0xFF), (byte)(c0 >> 8), (byte)(c1 & 0xFF), (byte)(c1 >> 8), .. IndexRows];

    private static BlpTestWriter.Px[,] Pixels(byte[] rgba, int width, int height)
    {
        var px = new BlpTestWriter.Px[height, width];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                px[y, x] = new BlpTestWriter.Px(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
            }
        }

        return px;
    }

    private static void AssertRow(BlpTestWriter.Px[,] actual, int y, params BlpTestWriter.Px[] expected)
    {
        for (int x = 0; x < expected.Length; x++)
        {
            Assert.True(expected[x] == actual[y, x], $"pixel ({x},{y}): expected {expected[x]}, got {actual[y, x]}");
        }
    }

    [Fact]
    public void Dxt1_FourColourBlock_UsesThirdsAndIsFullyOpaque()
    {
        byte[] rgba = DxtCodec.DecodeDxt1(Block(0xF800, 0x001F), 4, 4, keepTransparency: true);

        Assert.Equal(4 * 4 * 4, rgba.Length);
        BlpTestWriter.Px[,] px = Pixels(rgba, 4, 4);
        BlpTestWriter.Px c0 = new(255, 0, 0), c1 = new(0, 0, 255), c2 = new(170, 0, 85), c3 = new(85, 0, 170);
        AssertRow(px, 0, c0, c1, c2, c3);
        AssertRow(px, 1, c3, c2, c1, c0);
        AssertRow(px, 2, c0, c0, c0, c0);
        AssertRow(px, 3, c3, c3, c3, c3);
    }

    [Fact]
    public void Dxt1_ThreeColourBlock_IsHalfwayPlusTransparentBlack_WhenAlphaIsKept()
    {
        // c0 (0x0000) < c1 (0x4000): three colours. Index 3 is transparent black, index 2 is the midpoint (33).
        byte[] rgba = DxtCodec.DecodeDxt1(Block(0x0000, 0x4000), 4, 4, keepTransparency: true);

        BlpTestWriter.Px[,] px = Pixels(rgba, 4, 4);
        BlpTestWriter.Px black = new(0, 0, 0), c1 = new(66, 0, 0), mid = new(33, 0, 0), clear = new(0, 0, 0, 0);
        AssertRow(px, 0, black, c1, mid, clear);
        AssertRow(px, 1, clear, mid, c1, black);
        AssertRow(px, 2, black, black, black, black);
        AssertRow(px, 3, clear, clear, clear, clear);
    }

    [Fact]
    public void Dxt1_ThreeColourBlock_IsForcedOpaque_WhenTheFileSaysNoAlpha()
    {
        // alphaDepth 0 DXT1 files must never show holes: the same block, transparency honoured = false.
        byte[] rgba = DxtCodec.DecodeDxt1(Block(0x0000, 0x4000), 4, 4, keepTransparency: false);

        BlpTestWriter.Px[,] px = Pixels(rgba, 4, 4);
        AssertRow(px, 0, new(0, 0, 0), new(66, 0, 0), new(33, 0, 0), new(0, 0, 0));
        AssertRow(px, 3, new(0, 0, 0), new(0, 0, 0), new(0, 0, 0), new(0, 0, 0));
        for (int i = 3; i < rgba.Length; i += 4)
        {
            Assert.Equal(255, rgba[i]);
        }
    }

    [Fact]
    public void Dxt1_EqualEndpoints_AreThreeColourMode()
    {
        // The test is c0 > c1; equal endpoints are not greater, so index 3 is transparent black. Green 0x07E0 = (0,255,0).
        byte[] block = [0xE0, 0x07, 0xE0, 0x07, 0xE4, 0xE4, 0xE4, 0xE4];

        BlpTestWriter.Px[,] px = Pixels(DxtCodec.DecodeDxt1(block, 4, 4, keepTransparency: true), 4, 4);

        AssertRow(px, 0, new(0, 255, 0), new(0, 255, 0), new(0, 255, 0), new(0, 0, 0, 0));
    }

    [Fact]
    public void Dxt1_EndpointsOneApart_StayFourColour()
    {
        // c0 = 0x4001 > c1 = 0x4000: four-colour, alpha stays 255 even when transparency is honoured.
        byte[] rgba = DxtCodec.DecodeDxt1(Block(0x4001, 0x4000), 4, 4, keepTransparency: true);

        for (int i = 3; i < rgba.Length; i += 4)
        {
            Assert.Equal(255, rgba[i]);
        }
    }

    [Fact]
    public void Dxt3_HasExplicitFourBitAlpha_AndIsAlwaysFourColour()
    {
        // Alpha rows (nibbles, pixel 0 in the low nibble): [0,5,10,15] [15,10,5,0] [8,8,8,8] [1,2,3,4], each as a little-endian u16.
        byte[] alpha = [0x50, 0xFA, 0xAF, 0x05, 0x88, 0x88, 0x21, 0x43];
        // c0 = 0x0000 <= c1 = 0x4000 would be three-colour in DXT1; DXT3 ignores that and uses thirds: c2 = 22, c3 = 44.
        byte[] data = [.. alpha, .. Block(0x0000, 0x4000)];

        BlpTestWriter.Px[,] px = Pixels(DxtCodec.DecodeDxt3(data, 4, 4), 4, 4);

        AssertRow(px, 0, new(0, 0, 0, 0), new(66, 0, 0, 85), new(22, 0, 0, 170), new(44, 0, 0, 255));
        AssertRow(px, 1, new(44, 0, 0, 255), new(22, 0, 0, 170), new(66, 0, 0, 85), new(0, 0, 0, 0));
        AssertRow(px, 2, new(0, 0, 0, 136), new(0, 0, 0, 136), new(0, 0, 0, 136), new(0, 0, 0, 136));
        AssertRow(px, 3, new(44, 0, 0, 17), new(44, 0, 0, 34), new(44, 0, 0, 51), new(44, 0, 0, 68));
    }

    [Fact]
    public void Dxt5_FirstEndpointLarger_InterpolatesSixValues()
    {
        // a0 = 70, a1 = 0 -> [70, 0, 60, 50, 40, 30, 20, 10]; pixel indices 0..7 then 7..0 (3 bits each, LSB first).
        byte[] alpha = [0x46, 0x00, 0x88, 0xC6, 0xFA, 0x77, 0x39, 0x05];
        byte[] data = [.. alpha, .. Block(0xF800, 0x001F)];

        BlpTestWriter.Px[,] px = Pixels(DxtCodec.DecodeDxt5(data, 4, 4), 4, 4);

        AssertRow(px, 0, new(255, 0, 0, 70), new(0, 0, 255, 0), new(170, 0, 85, 60), new(85, 0, 170, 50));
        Assert.Equal(40, px[1, 0].A);
        Assert.Equal(30, px[1, 1].A);
        Assert.Equal(20, px[1, 2].A);
        Assert.Equal(10, px[1, 3].A);
        // Second half of the index list runs 7,6,5,4 | 3,2,1,0 -> 10,20,30,40 | 50,60,0,70.
        Assert.Equal([10, 20, 30, 40], new[] { px[2, 0].A, px[2, 1].A, px[2, 2].A, px[2, 3].A });
        Assert.Equal([50, 60, 0, 70], new[] { px[3, 0].A, px[3, 1].A, px[3, 2].A, px[3, 3].A });
    }

    [Fact]
    public void Dxt5_FirstEndpointNotLarger_InterpolatesFour_ThenZeroAndOpaque()
    {
        // a0 = 0, a1 = 50 -> [0, 50, 10, 20, 30, 40, 0, 255].
        byte[] alpha = [0x00, 0x32, 0x88, 0xC6, 0xFA, 0x77, 0x39, 0x05];
        byte[] data = [.. alpha, .. Block(0xF800, 0x001F)];

        BlpTestWriter.Px[,] px = Pixels(DxtCodec.DecodeDxt5(data, 4, 4), 4, 4);

        Assert.Equal([0, 50, 10, 20], new[] { px[0, 0].A, px[0, 1].A, px[0, 2].A, px[0, 3].A });
        Assert.Equal([30, 40, 0, 255], new[] { px[1, 0].A, px[1, 1].A, px[1, 2].A, px[1, 3].A });
        Assert.Equal([255, 0, 40, 30], new[] { px[2, 0].A, px[2, 1].A, px[2, 2].A, px[2, 3].A });
        Assert.Equal([20, 10, 50, 0], new[] { px[3, 0].A, px[3, 1].A, px[3, 2].A, px[3, 3].A });
    }

    [Fact]
    public void WidthNotAMultipleOfFour_DecodesWholeBlocksAndCrops()
    {
        // 5x2 needs 2 blocks across x 1 down. Block 2 is solid green (equal endpoints, indices 0).
        byte[] solidGreen = [0xE0, 0x07, 0xE0, 0x07, 0, 0, 0, 0];
        byte[] data = [.. Block(0xF800, 0x001F), .. solidGreen];

        byte[] rgba = DxtCodec.DecodeDxt1(data, 5, 2, keepTransparency: true);

        Assert.Equal(5 * 2 * 4, rgba.Length);
        BlpTestWriter.Px[,] px = Pixels(rgba, 5, 2);
        AssertRow(px, 0, new(255, 0, 0), new(0, 0, 255), new(170, 0, 85), new(85, 0, 170), new(0, 255, 0));
        AssertRow(px, 1, new(85, 0, 170), new(170, 0, 85), new(0, 0, 255), new(255, 0, 0), new(0, 255, 0));
    }

    [Fact]
    public void OneByOneTexture_NeedsOneBlock()
    {
        byte[] rgba = DxtCodec.DecodeDxt1(Block(0xF800, 0x001F), 1, 1, keepTransparency: true);

        Assert.Equal([255, 0, 0, 255], rgba);
    }

    [Fact]
    public void ShortInput_IsRejectedWithInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => DxtCodec.DecodeDxt1(new byte[7], 4, 4, keepTransparency: true));
        Assert.Throws<InvalidDataException>(() => DxtCodec.DecodeDxt3(new byte[15], 4, 4));
        Assert.Throws<InvalidDataException>(() => DxtCodec.DecodeDxt5(new byte[15], 4, 4));
        Assert.Throws<InvalidDataException>(() => DxtCodec.DecodeDxt1(new byte[8], 8, 4, keepTransparency: true));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(-4, 4)]
    public void NonPositiveSize_IsRejectedWithInvalidData(int width, int height)
    {
        Assert.Throws<InvalidDataException>(() => DxtCodec.DecodeDxt1(new byte[64], width, height, keepTransparency: true));
    }
}
