using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Render;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Render;

/// <summary>A PNG reader written separately from the encoder, following the same specification (W3C PNG 2nd edition).</summary>
internal sealed class DecodedPng
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required byte BitDepth { get; init; }

    public required byte ColourType { get; init; }

    public required byte CompressionMethod { get; init; }

    public required byte FilterMethod { get; init; }

    public required byte InterlaceMethod { get; init; }

    /// <summary>Chunk types in file order.</summary>
    public required List<string> ChunkTypes { get; init; }

    public required int IdatChunks { get; init; }

    /// <summary>The inflated IDAT stream: per scanline a filter byte then the filtered bytes.</summary>
    public required byte[] Inflated { get; init; }

    /// <summary>RGBA pixels with filter type 0 removed.</summary>
    public byte[] Pixels()
    {
        int stride = 1 + (4 * Width);
        var rgba = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            Assert.Equal((byte)0, Inflated[y * stride]);
            Array.Copy(Inflated, (y * stride) + 1, rgba, y * Width * 4, Width * 4);
        }

        return rgba;
    }

    /// <summary>Parses and fully verifies a PNG datastream: signature, chunk framing, every CRC, chunk order and IEND at the end.</summary>
    public static DecodedPng Parse(byte[] data)
    {
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        Assert.True(data.AsSpan(0, 8).SequenceEqual(signature), "PNG signature");
        int offset = 8;
        var types = new List<string>();
        var idat = new MemoryStream();
        int idatChunks = 0;
        int width = 0;
        int height = 0;
        byte[] ihdr = [];
        while (offset < data.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
            Assert.True(length <= int.MaxValue);
            string type = Encoding.ASCII.GetString(data, offset + 4, 4);
            Assert.All(type, c => Assert.True(char.IsAsciiLetter(c)));
            ReadOnlySpan<byte> body = data.AsSpan(offset + 8, (int)length);
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 8 + (int)length));
            Assert.Equal(ReferenceCrc(data.AsSpan(offset + 4, 4 + (int)length)), stored);
            types.Add(type);
            if (type == "IHDR")
            {
                Assert.Equal(13, (int)length);
                ihdr = body.ToArray();
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
            }
            else if (type == "IDAT")
            {
                idat.Write(body);
                idatChunks++;
            }

            offset += 12 + (int)length;
        }

        Assert.Equal(data.Length, offset);
        Assert.Equal("IHDR", types[0]);
        Assert.Equal("IEND", types[^1]);
        Assert.Equal(1, types.Count(t => t == "IEND"));
        int firstIdat = types.IndexOf("IDAT");
        int lastIdat = types.LastIndexOf("IDAT");
        Assert.True(firstIdat > 0);
        Assert.Equal(lastIdat - firstIdat + 1, idatChunks);

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var inflated = new MemoryStream();
        zlib.CopyTo(inflated);
        return new DecodedPng
        {
            Width = width,
            Height = height,
            BitDepth = ihdr[8],
            ColourType = ihdr[9],
            CompressionMethod = ihdr[10],
            FilterMethod = ihdr[11],
            InterlaceMethod = ihdr[12],
            ChunkTypes = types,
            IdatChunks = idatChunks,
            Inflated = inflated.ToArray(),
        };
    }

    /// <summary>The bit-at-a-time CRC of PNG section 5.5 (polynomial 0xEDB88320, reflected, all-ones start, inverted result).</summary>
    public static uint ReferenceCrc(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }
}

public sealed class PngTests
{
    private static byte[] Gradient(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                rgba[i] = (byte)(x * 255 / Math.Max(1, width - 1));
                rgba[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                rgba[i + 2] = (byte)((x + y) & 0xFF);
                rgba[i + 3] = (byte)(255 - (x & 3));
            }
        }

        return rgba;
    }

    [Fact]
    public void Crc32_OfTheCheckString_IsTheStandardCheckValue()
    {
        // CRC-32/ISO-HDLC, the CRC of PNG 5.5: the catalogued check value for "123456789".
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
        Assert.Equal(0xCBF43926u, DecodedPng.ReferenceCrc("123456789"u8));
        Assert.Equal(0u, Crc32.Compute([]));
    }

    [Fact]
    public void Crc32_Update_ContinuesAcrossBuffers()
    {
        byte[] data = Gradient(31, 17);

        uint whole = Crc32.Compute(data);
        uint split = ~Crc32.Update(Crc32.Update(Crc32.Initial, data.AsSpan(0, 1000)), data.AsSpan(1000));

        Assert.Equal(whole, split);
        Assert.Equal(DecodedPng.ReferenceCrc(data), whole);
    }

    [Fact]
    public void TheIendChunk_HasTheWellKnownCrc()
    {
        // An IEND chunk is length 0, type "IEND", no data: its CRC is the same in every PNG file ever written.
        byte[] png = PngEncoder.Encode(Gradient(2, 2), 2, 2);

        Assert.True(png.AsSpan(png.Length - 12).SequenceEqual(new byte[] { 0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 }));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 5)]
    [InlineData(64, 48)]
    public void Encoded_Png_HasTheDeclaredStructure_AndTheSamePixels(int width, int height)
    {
        byte[] rgba = Gradient(width, height);

        DecodedPng png = DecodedPng.Parse(PngEncoder.Encode(rgba, width, height));

        Assert.Equal(width, png.Width);
        Assert.Equal(height, png.Height);
        Assert.Equal(8, png.BitDepth);
        Assert.Equal(6, png.ColourType);
        Assert.Equal(0, png.CompressionMethod);
        Assert.Equal(0, png.FilterMethod);
        Assert.Equal(0, png.InterlaceMethod);
        Assert.Equal(height * (1 + (4 * width)), png.Inflated.Length);
        Assert.Equal(rgba, png.Pixels());
    }

    [Fact]
    public void LargeImages_SplitIntoSeveralIdatChunks_ThatStillDecode()
    {
        // Incompressible data (a fixed xorshift stream) so the compressed size passes the 1 MiB chunk limit.
        const int width = 700;
        const int height = 700;
        var rgba = new byte[width * height * 4];
        uint state = 0x2545F491;
        for (int i = 0; i < rgba.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            rgba[i] = (byte)state;
        }

        DecodedPng png = DecodedPng.Parse(PngEncoder.Encode(rgba, width, height));

        Assert.True(png.IdatChunks >= 2, $"expected several IDAT chunks, got {png.IdatChunks}");
        Assert.Equal(rgba, png.Pixels());
    }

    [Fact]
    public void Encode_RefusesSizesThatDoNotMatchTheBuffer()
    {
        Assert.Throws<ArgumentException>(() => PngEncoder.Encode(new byte[10], 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => PngEncoder.Encode([], 0, 4));
    }

    [Fact]
    public void RenderedImage_IsAValidPng_OfTheRenderedPixels()
    {
        TileKey tile = Views.TileOf(Views.TopCamera);
        var rig = new RenderRig(new FakeSource().Add(LayerIds.Terrain, tile, Geometry.Rect(LayerIds.Terrain, tile, 90, 110, 190, 210, 0)));
        ViewState view = Views.Of(Views.TopCamera, (LayerIds.Terrain, Views.Style(mode: RenderMode.Both))) with { ShowHud = true };

        RenderedImage image = rig.Renderer.Render(view, 128, 96, CancellationToken.None);
        RenderedFrame frame = rig.Frame(view, 128, 96);

        DecodedPng png = DecodedPng.Parse(image.Png);
        Assert.Equal(128, image.Width);
        Assert.Equal(96, image.Height);
        Assert.Equal(128, png.Width);
        Assert.Equal(96, png.Height);
        Assert.Equal(96 * (1 + (4 * 128)), png.Inflated.Length);
        Assert.Equal(frame.Buffer.Pixels, png.Pixels());
        Assert.All(frame.Buffer.Pixels.Where((_, i) => i % 4 == 3), a => Assert.Equal(255, a));
        Assert.True(image.Stats["elapsedMs"]!.GetValue<double>() >= 0);
    }
}
