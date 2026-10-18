using System.Buffers.Binary;
using System.IO.Compression;

namespace ArcaneCore.Atlas.Render;

/// <summary>
/// Writes an 8-bit RGBA image as a PNG datastream.
/// <para>
/// ref: W3C PNG Specification Second Edition (ISO/IEC 15948:2003), https://www.w3.org/TR/2003/REC-PNG-20031110/.
/// Section 5.2 (signature 137 80 78 71 13 10 26 10), 5.3 (chunk = 4-byte big-endian length of the data field, 4-byte
/// type, data, 4-byte CRC over type and data), 5.5 and Annex D (CRC, see <see cref="Crc32"/>), 9.1 and 9.2 Table 9.1
/// (filter method 0; every scanline is one filter-type byte followed by the scanline bytes; type 0 is "None"), 10.1
/// (compression method 0 is deflate stored in the zlib format of RFC 1950, window at most 32768 bytes), 11.2.2
/// (IHDR: width, height as 4-byte unsigned integers, bit depth, colour type, compression method, filter method,
/// interlace method), Table 11.1 (colour type 6 is truecolour with alpha, allowed bit depths 8 and 16), 11.2.4 (IDAT
/// is the compressed stream; several IDAT chunks concatenate) and 11.2.5 (IEND has an empty data field).
/// </para>
/// </summary>
public static class PngEncoder
{
    /// <summary>ref: PNG 2nd ed. 5.2 "PNG signature".</summary>
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>ref: PNG 2nd ed. 11.2.2, 11.2.4 and 11.2.5: the chunk types as ASCII (73 72 68 82, 73 68 65 84, 73 69 78 68).</summary>
    private static readonly byte[] Ihdr = "IHDR"u8.ToArray();

    private static readonly byte[] Idat = "IDAT"u8.ToArray();

    private static readonly byte[] Iend = "IEND"u8.ToArray();

    /// <summary>Bytes of one pixel: colour type 6 at bit depth 8 is red, green, blue, alpha, one byte each.</summary>
    public const int BytesPerPixel = 4;

    /// <summary>Largest compressed data field one IDAT chunk may carry (PNG 5.3: a length never exceeds 2^31 - 1); the encoder splits well below it.</summary>
    private const int MaxIdatBytes = 1 << 20;

    /// <summary>Encodes <paramref name="rgba"/> (row-major, top row first, 4 bytes per pixel) as a PNG.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width < 1 || height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"A PNG needs at least one pixel (got {width} x {height}).");
        }

        long rowBytes = (long)width * BytesPerPixel;
        if (rgba.Length != rowBytes * height)
        {
            throw new ArgumentException($"Expected {rowBytes * height} bytes for {width} x {height} RGBA pixels, got {rgba.Length}.", nameof(rgba));
        }

        if ((1 + rowBytes) * height > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"{width} x {height} does not fit one in-memory scanline buffer.");
        }

        // ref: PNG 2nd ed. 9.1: filtering turns each scanline into the filter-type byte followed by its bytes.
        byte[] scanlines = new byte[(1 + rowBytes) * height];
        for (int y = 0; y < height; y++)
        {
            long target = y * (1 + rowBytes);
            scanlines[target] = 0; // filter type 0, None (Table 9.1)
            rgba.Slice((int)(y * rowBytes), (int)rowBytes).CopyTo(scanlines.AsSpan((int)target + 1));
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(scanlines);
        }

        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = 8; // bit depth
        header[9] = 6; // colour type: truecolour with alpha (Table 11.1)
        header[10] = 0; // compression method 0 (10.1)
        header[11] = 0; // filter method 0 (9.1)
        header[12] = 0; // interlace method 0: no interlace (11.2.2)
        WriteChunk(output, Ihdr, header);

        ReadOnlySpan<byte> data = compressed.GetBuffer().AsSpan(0, (int)compressed.Length);
        for (int offset = 0; offset < data.Length; offset += MaxIdatBytes)
        {
            WriteChunk(output, Idat, data.Slice(offset, Math.Min(MaxIdatBytes, data.Length - offset)));
        }

        WriteChunk(output, Iend, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, byte[] type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        output.Write(word);
        output.Write(type);
        output.Write(data);
        uint crc = ~Crc32.Update(Crc32.Update(Crc32.Initial, type), data);
        BinaryPrimitives.WriteUInt32BigEndian(word, crc);
        output.Write(word);
    }
}
