using System.Buffers.Binary;
using System.Text;

namespace ArcaneCore.Atlas.Reference.Client.Models;

/// <summary>
/// One chunk of a WoW chunked file (WMO root, WMO group): its logical four-character tag, where its payload starts
/// and how many payload bytes it really has. <see cref="Truncated"/> is true when the size field promised more bytes
/// than the container holds; the size is then clamped to what is there.
/// </summary>
public readonly record struct WowChunk(string Tag, int Offset, int Size, bool Truncated)
{
    public int End => Offset + Size;
}

/// <summary>
/// Walks the "tag, u32 size, payload" chunk layout used by WMO files. The tag is stored byte-reversed on disk
/// (<c>REVM</c> reads as <c>MVER</c>). Stateless and thread-safe.
/// </summary>
public static class WowChunks
{
    /// <summary>Bytes of a chunk header: four tag bytes and a u32 size.</summary>
    public const int HeaderBytes = 8;

    /// <summary>
    /// Every chunk whose header starts inside [<paramref name="start"/>, <paramref name="end"/>). A final chunk whose
    /// size overruns <paramref name="end"/> is returned with its size clamped and <see cref="WowChunk.Truncated"/>
    /// set (the real client file Undercity_144.wmo has a trailing MOCV chunk one byte longer than the file);
    /// fewer than <see cref="HeaderBytes"/> trailing bytes are ignored.
    /// </summary>
    public static List<WowChunk> Scan(ReadOnlySpan<byte> data, int start, int end)
    {
        if (start < 0 || end > data.Length || start > end)
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"Chunk range {start}..{end} does not fit a {data.Length}-byte file.");
        }

        var chunks = new List<WowChunk>();
        int position = start;
        while (end - position >= HeaderBytes)
        {
            // ref: WoW-RE-Research/formats/wmo.md:44-47 (tag + u32 size + payload; tag stored reversed)
            // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:61-67 (read fourcc, read size, flipcc, nextpos = pos + size)
            string tag = ReadTag(data.Slice(position, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 4)..]);
            int payload = position + HeaderBytes;
            long remaining = end - payload;
            bool truncated = size > remaining;
            int clamped = truncated ? (int)remaining : (int)size;
            chunks.Add(new WowChunk(tag, payload, clamped, truncated));
            position = payload + clamped;
        }

        return chunks;
    }

    /// <summary>The payload of a chunk a reader depends on; throws when the file ended inside it.</summary>
    public static ReadOnlySpan<byte> Required(ReadOnlySpan<byte> data, WowChunk chunk, string context)
    {
        if (chunk.Truncated)
        {
            throw new InvalidDataException($"{context}: chunk {chunk.Tag} is cut off by the end of the file.");
        }

        return data.Slice(chunk.Offset, chunk.Size);
    }

    /// <summary>The NUL-terminated Latin-1 string starting at <paramref name="offset"/> of a string blob, or null past its end.</summary>
    public static string? StringAt(ReadOnlySpan<byte> blob, long offset)
    {
        if (offset < 0 || offset >= blob.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> tail = blob[(int)offset..];
        int nul = tail.IndexOf((byte)0);
        return Encoding.Latin1.GetString(nul < 0 ? tail : tail[..nul]);
    }

    private static string ReadTag(ReadOnlySpan<byte> raw)
        => string.Create(4, raw.ToArray(), static (span, bytes) =>
        {
            span[0] = (char)bytes[3];
            span[1] = (char)bytes[2];
            span[2] = (char)bytes[1];
            span[3] = (char)bytes[0];
        });
}
