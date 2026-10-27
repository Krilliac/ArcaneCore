using System.IO.Compression;
using System.Runtime.InteropServices;

namespace ArcaneCore.Atlas.Reference.Mpq;

/// <summary>
/// Decompression of one MPQ sector (or single-unit file) written with <see cref="MpqFileFlags.Compress"/>. The first byte
/// of such data is a mask of the methods applied; they are undone in a fixed order. Stateless and thread-safe.
/// </summary>
public static class MpqCompression
{
    // The methods are undone in this order, each one's output feeding the next.
    // ref: StormLib src/SCompression.cpp:911-920 (dcmp_table) and :1001-1025 (the loop)
    private static readonly byte[] UndoOrder =
    [
        MpqCompressionMask.Bzip2,
        MpqCompressionMask.Pkware,
        MpqCompressionMask.Zlib,
        MpqCompressionMask.Huffman,
        MpqCompressionMask.AdpcmStereo,
        MpqCompressionMask.AdpcmMono,
        MpqCompressionMask.Sparse,
    ];

    // Archives of format version 1 and newer use SCompDecompress2's fixed method set instead of the format-0 table. Masks:
    // zlib 0x02, pkware 0x08, bzip2 0x10, lzma 0x12, sparse 0x20, sparse+zlib 0x22, sparse+bzip2 0x30, huffman+adpcm-mono 0x41,
    // huffman+adpcm-stereo 0x81.
    // ref: StormLib src/SCompression.cpp:1066-1115 (the switch) and :1155-1161 (SCompDecompressX picks it
    // for wFormatVersion >= MPQ_FORMAT_VERSION_2, which StormLib.h:304 defines as 1)
    private const byte LzmaMask = 0x12;

    private static bool IsFormat1Mask(byte mask) => mask is 0x02 or 0x08 or 0x10 or LzmaMask or 0x20 or 0x22 or 0x30 or 0x41 or 0x81;

    /// <summary>
    /// Expands <paramref name="stored"/> into exactly <paramref name="output"/>. Stored data exactly as long as the output
    /// was never compressed and is copied.
    /// </summary>
    /// <param name="stored">The stored sector or unit, starting with the method-mask byte unless it is not compressed.</param>
    /// <param name="output">Receives the expanded bytes; its length is the expected size.</param>
    /// <param name="formatVersion">The archive header's raw format version (0 uses the original method table, 1 and newer the fixed set).</param>
    /// <exception cref="InvalidDataException">The data is corrupt or does not expand to <c>output.Length</c> bytes.</exception>
    /// <exception cref="NotSupportedException">A method in the mask is not implemented; the message names it.</exception>
    public static void Decompress(ReadOnlyMemory<byte> stored, Span<byte> output, int formatVersion = 0)
    {
        // ref: StormLib src/SCompression.cpp:945-955 (equal sizes mean stored as is; empty or larger input is an error)
        if (stored.Length == output.Length)
        {
            stored.Span.CopyTo(output);
            return;
        }

        if (stored.Length < 2 || stored.Length > output.Length)
        {
            throw new InvalidDataException($"Compressed sector of {stored.Length} bytes cannot expand to {output.Length} bytes.");
        }

        byte mask = stored.Span[0];
        if (formatVersion >= 1)
        {
            if (mask == LzmaMask)
            {
                throw new NotSupportedException($"MPQ compression method lzma (sector mask 0x12) is not implemented in this reader.");
            }

            if (!IsFormat1Mask(mask))
            {
                throw new NotSupportedException($"MPQ compression mask {MpqCompressionMask.Describe(mask)} is not in the method set of format version {formatVersion} archives (StormLib treats it as corrupt).");
            }
        }

        int stages = 0;
        byte known = 0;
        foreach (byte bit in UndoOrder)
        {
            if ((mask & bit) != 0)
            {
                stages++;
                known |= bit;
            }
        }

        // ref: SCompression.cpp:980-985 (no method, or a bit outside the table, is "not supported")
        if (stages == 0 || known != mask)
        {
            throw new NotSupportedException($"MPQ compression mask {MpqCompressionMask.Describe(mask)} contains no known method or an unknown bit.");
        }

        ReadOnlyMemory<byte> current = stored[1..];
        int done = 0;
        foreach (byte bit in UndoOrder)
        {
            if ((mask & bit) == 0)
            {
                continue;
            }

            done++;
            if (done == stages)
            {
                int produced = Apply(bit, mask, current, output);
                if (produced != output.Length)
                {
                    throw new InvalidDataException(
                        $"MPQ {MpqCompressionMask.Describe(mask)} sector expanded to {produced} bytes, expected {output.Length}.");
                }
            }
            else
            {
                byte[] scratch = new byte[output.Length];
                int produced = Apply(bit, mask, current, scratch);
                current = scratch.AsMemory(0, produced);
            }
        }
    }

    private static int Apply(byte bit, byte mask, ReadOnlyMemory<byte> input, Span<byte> target)
    {
        switch (bit)
        {
            case MpqCompressionMask.Zlib:
                return Inflate(input, target);
            case MpqCompressionMask.Pkware:
                return PkwareExplode.Decompress(input.Span, target);
            case MpqCompressionMask.Sparse:
                return MpqSparse.Decompress(input.Span, target);
            default:
                throw new NotSupportedException(
                    $"MPQ compression method {MpqCompressionMask.Describe(bit)} (sector mask {MpqCompressionMask.Describe(mask)}) is not implemented in this reader.");
        }
    }

    private static int Inflate(ReadOnlyMemory<byte> input, Span<byte> target)
    {
        // ref: StormLib src/SCompression.cpp:155-180 (Decompress_ZLIB: inflateInit = a zlib-wrapped deflate stream)
        if (!MemoryMarshal.TryGetArray(input, out ArraySegment<byte> segment) || segment.Array is null)
        {
            segment = new ArraySegment<byte>(input.ToArray());
        }

        using var source = new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        int total = 0;
        while (total < target.Length)
        {
            int read = zlib.Read(target[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total == target.Length)
        {
            Span<byte> probe = stackalloc byte[1];
            if (zlib.Read(probe) != 0)
            {
                throw new InvalidDataException("zlib sector expands past the expected size.");
            }
        }

        return total;
    }
}
