namespace ArcaneCore.Atlas.Textures;

/// <summary>
/// S3TC (DXT1, DXT3, DXT5) block decoders producing RGBA8, row-major, top row first. Pure functions; any thread. Colour
/// endpoints are RGB565 expanded to 8 bits by bit replication; interpolants use integer division (EXT_texture_compression_s3tc).
/// Every failure is an <see cref="InvalidDataException"/> so hostile input cannot surface another exception type.
/// </summary>
public static class DxtCodec
{
    /// <summary>Largest width or height accepted, so an image's byte size always fits an int.</summary>
    public const int MaxDimension = 16384;

    /// <summary>Bytes of a DXT1 block.</summary>
    public const int Dxt1BlockBytes = 8;

    /// <summary>Bytes of a DXT3 or DXT5 block.</summary>
    public const int Dxt35BlockBytes = 16;

    /// <summary>Bytes a DXT level of the given size occupies (whole 4x4 blocks).</summary>
    public static long LevelBytes(int width, int height, int bytesPerBlock)
        => (long)((Math.Max(1, width) + 3) / 4) * ((Math.Max(1, height) + 3) / 4) * bytesPerBlock;

    public static byte[] DecodeDxt1(ReadOnlySpan<byte> data, int width, int height, bool keepTransparency)
    {
        byte[] rgba = Allocate(data, width, height, Dxt1BlockBytes);
        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        Span<uint> colours = stackalloc uint[4];
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                ReadOnlySpan<byte> block = data.Slice(((by * blocksX) + bx) * Dxt1BlockBytes, Dxt1BlockBytes);
                ColourPalette(block, colours, fourColourOnly: false, transparentIndex3: keepTransparency);
                WriteColours(block[4..], colours, rgba, width, height, bx, by);
            }
        }

        return rgba;
    }

    public static byte[] DecodeDxt3(ReadOnlySpan<byte> data, int width, int height)
    {
        byte[] rgba = Allocate(data, width, height, Dxt35BlockBytes);
        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        Span<uint> colours = stackalloc uint[4];
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                ReadOnlySpan<byte> block = data.Slice(((by * blocksX) + bx) * Dxt35BlockBytes, Dxt35BlockBytes);
                ColourPalette(block[8..], colours, fourColourOnly: true, transparentIndex3: false);
                WriteColours(block[12..], colours, rgba, width, height, bx, by);
                for (int p = 0; p < 16; p++)
                {
                    int x = (bx * 4) + (p & 3);
                    int y = (by * 4) + (p >> 2);
                    if (x < width && y < height)
                    {
                        int nibble = (block[p >> 1] >> ((p & 1) * 4)) & 0xF;
                        rgba[((((y * width) + x) * 4)) + 3] = (byte)(nibble * 17);
                    }
                }
            }
        }

        return rgba;
    }

    public static byte[] DecodeDxt5(ReadOnlySpan<byte> data, int width, int height)
    {
        byte[] rgba = Allocate(data, width, height, Dxt35BlockBytes);
        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        Span<uint> colours = stackalloc uint[4];
        Span<byte> alphas = stackalloc byte[8];
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                ReadOnlySpan<byte> block = data.Slice(((by * blocksX) + bx) * Dxt35BlockBytes, Dxt35BlockBytes);
                ColourPalette(block[8..], colours, fourColourOnly: true, transparentIndex3: false);
                WriteColours(block[12..], colours, rgba, width, height, bx, by);
                int a0 = block[0];
                int a1 = block[1];
                alphas[0] = (byte)a0;
                alphas[1] = (byte)a1;
                if (a0 > a1)
                {
                    for (int k = 1; k <= 6; k++)
                    {
                        alphas[1 + k] = (byte)((((7 - k) * a0) + (k * a1)) / 7);
                    }
                }
                else
                {
                    for (int k = 1; k <= 4; k++)
                    {
                        alphas[1 + k] = (byte)((((5 - k) * a0) + (k * a1)) / 5);
                    }

                    alphas[6] = 0;
                    alphas[7] = 255;
                }

                ulong bits = 0;
                for (int i = 0; i < 6; i++)
                {
                    bits |= (ulong)block[2 + i] << (8 * i);
                }

                for (int p = 0; p < 16; p++)
                {
                    int x = (bx * 4) + (p & 3);
                    int y = (by * 4) + (p >> 2);
                    if (x < width && y < height)
                    {
                        rgba[(((y * width) + x) * 4) + 3] = alphas[(int)((bits >> (3 * p)) & 7)];
                    }
                }
            }
        }

        return rgba;
    }

    private static byte[] Allocate(ReadOnlySpan<byte> data, int width, int height, int bytesPerBlock)
    {
        if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension)
        {
            throw new InvalidDataException($"DXT image size {width}x{height} is outside 1..{MaxDimension}.");
        }

        long needed = LevelBytes(width, height, bytesPerBlock);
        if (data.Length < needed)
        {
            throw new InvalidDataException($"DXT data is {data.Length} bytes but a {width}x{height} image needs {needed}.");
        }

        // 16384 * 16384 * 4 is 1 GiB, which fits an int; the caller's own size limits are far lower.
        return new byte[width * height * 4];
    }

    /// <summary>
    /// Builds the four colours of the block whose colour part starts at <paramref name="block"/> (two RGB565 words). Each colour is
    /// packed as R | G &lt;&lt; 8 | B &lt;&lt; 16 | A &lt;&lt; 24.
    /// </summary>
    private static void ColourPalette(ReadOnlySpan<byte> block, Span<uint> colours, bool fourColourOnly, bool transparentIndex3)
    {
        int c0 = block[0] | (block[1] << 8);
        int c1 = block[2] | (block[3] << 8);
        Expand(c0, out int r0, out int g0, out int b0);
        Expand(c1, out int r1, out int g1, out int b1);
        colours[0] = Pack(r0, g0, b0, 255);
        colours[1] = Pack(r1, g1, b1, 255);
        if (fourColourOnly || c0 > c1)
        {
            colours[2] = Pack(((2 * r0) + r1) / 3, ((2 * g0) + g1) / 3, ((2 * b0) + b1) / 3, 255);
            colours[3] = Pack((r0 + (2 * r1)) / 3, (g0 + (2 * g1)) / 3, (b0 + (2 * b1)) / 3, 255);
        }
        else
        {
            colours[2] = Pack((r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2, 255);
            colours[3] = Pack(0, 0, 0, transparentIndex3 ? 0 : 255);
        }
    }

    /// <summary>Writes the colour of every pixel of one block from its four index bytes (one row per byte, 2 bits per pixel, low bits first).</summary>
    private static void WriteColours(ReadOnlySpan<byte> indexRows, ReadOnlySpan<uint> colours, byte[] rgba, int width, int height, int bx, int by)
    {
        for (int row = 0; row < 4; row++)
        {
            int y = (by * 4) + row;
            if (y >= height)
            {
                break;
            }

            int bits = indexRows[row];
            for (int col = 0; col < 4; col++)
            {
                int x = (bx * 4) + col;
                if (x >= width)
                {
                    break;
                }

                uint colour = colours[(bits >> (2 * col)) & 3];
                int at = ((y * width) + x) * 4;
                rgba[at] = (byte)colour;
                rgba[at + 1] = (byte)(colour >> 8);
                rgba[at + 2] = (byte)(colour >> 16);
                rgba[at + 3] = (byte)(colour >> 24);
            }
        }
    }

    private static void Expand(int rgb565, out int r, out int g, out int b)
    {
        int r5 = (rgb565 >> 11) & 0x1F;
        int g6 = (rgb565 >> 5) & 0x3F;
        int b5 = rgb565 & 0x1F;
        r = (r5 << 3) | (r5 >> 2);
        g = (g6 << 2) | (g6 >> 4);
        b = (b5 << 3) | (b5 >> 2);
    }

    private static uint Pack(int r, int g, int b, int a) => (uint)(r | (g << 8) | (b << 16) | (a << 24));
}
