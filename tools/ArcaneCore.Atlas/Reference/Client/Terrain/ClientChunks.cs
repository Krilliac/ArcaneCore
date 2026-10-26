using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Reference.Client.Terrain;

/// <summary>
/// The chunk container shared by WDT and ADT files: a 4-byte tag stored reversed on disk (the chunk "MVER" starts with
/// the bytes "REVM"), a little-endian u32 payload size, then the payload. Every read is bounds-checked and refuses
/// with <see cref="InvalidDataException"/>; nothing here reads past the buffer it was given.
/// </summary>
internal static class ClientChunks
{
    // ref: vmangos@0e3ff01 contrib/extractor/loadlib/loadlib.h:35-40 (isChunk compares the tag reversed)
    // ref: vmangos@0e3ff01 contrib/extractor/loadlib/loadlib.h:45-54 (file_MVER: tag, u32 size, u32 version)
    // ref: D:\WoW-RE-Research\formats\terrain-adt.md:24-29 (all three terrain file types use this container)
    public const int HeaderBytes = 8;

    /// <summary>One chunk: its tag, where its 8-byte header starts, where the payload starts and the payload size.</summary>
    public readonly record struct Chunk(string Id, int Start, int DataStart, int Size)
    {
        /// <summary>First byte after the payload.</summary>
        public int End => DataStart + Size;
    }

    /// <summary>Reads the chunk header at <paramref name="start"/>; the payload must lie inside <paramref name="data"/>.</summary>
    public static Chunk Read(ReadOnlySpan<byte> data, int start, string context)
    {
        Need(data, start, HeaderBytes, context);
        string id = Tag(data.Slice(start, 4));
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(data[(start + 4)..]);
        int available = data.Length - (start + HeaderBytes);
        if (size > (uint)available)
        {
            throw new InvalidDataException($"{context}: chunk {id} at byte {start} declares {size} bytes but only {available} remain.");
        }

        return new Chunk(id, start, start + HeaderBytes, (int)size);
    }

    /// <summary>Reads the chunk at <paramref name="start"/> and requires it to be <paramref name="expectedId"/>.</summary>
    public static Chunk Expect(ReadOnlySpan<byte> data, int start, string expectedId, string context)
    {
        Chunk chunk = Read(data, start, context);
        if (!string.Equals(chunk.Id, expectedId, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{context}: expected chunk {expectedId} at byte {start}, found {chunk.Id}.");
        }

        return chunk;
    }

    /// <summary>All chunks laid back to back from <paramref name="start"/> to the end of the buffer.</summary>
    public static List<Chunk> Walk(ReadOnlySpan<byte> data, int start, string context)
    {
        var chunks = new List<Chunk>();
        int position = start;
        while (position < data.Length)
        {
            Chunk chunk = Read(data, position, context);
            chunks.Add(chunk);
            position = chunk.End;
        }

        return chunks;
    }

    /// <summary>The 4-character tag, un-reversed (the bytes "REVM" give "MVER").</summary>
    public static string Tag(ReadOnlySpan<byte> four)
    {
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            chars[i] = (char)four[3 - i];
        }

        return new string(chars);
    }

    /// <summary>Throws unless <paramref name="count"/> bytes are available at <paramref name="offset"/>.</summary>
    public static void Need(ReadOnlySpan<byte> data, long offset, long count, string context)
    {
        if (offset < 0 || count < 0 || offset + count > data.Length)
        {
            throw new InvalidDataException($"{context}: needs {count} bytes at byte {offset} but the data is {data.Length} bytes long.");
        }
    }

    public static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    public static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);

    public static float F32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);

    /// <summary>Three little-endian floats as a point in the file's own space.</summary>
    public static WorldPoint Vec3(ReadOnlySpan<byte> data, int offset)
        => new(F32(data, offset), F32(data, offset + 4), F32(data, offset + 8));

    /// <summary>
    /// The NUL-terminated strings of a name chunk (MTEX, MMDX, MWMO), in order, with the byte offset each starts at.
    /// A string without a terminator inside the chunk is refused.
    /// </summary>
    public static (List<string> Names, List<uint> Starts) Strings(ReadOnlySpan<byte> payload, string context)
    {
        var names = new List<string>();
        var starts = new List<uint>();
        int position = 0;
        while (position < payload.Length)
        {
            int length = payload[position..].IndexOf((byte)0);
            if (length < 0)
            {
                throw new InvalidDataException($"{context}: a name at byte {position} has no terminator inside its chunk.");
            }

            starts.Add((uint)position);
            names.Add(Encoding.Latin1.GetString(payload.Slice(position, length)));
            position += length + 1;
        }

        return (names, starts);
    }

    /// <summary>The NUL-terminated string at <paramref name="offset"/> inside a name chunk's payload.</summary>
    public static string StringAt(ReadOnlySpan<byte> payload, uint offset, string context)
    {
        if (offset >= (uint)payload.Length)
        {
            throw new InvalidDataException($"{context}: name offset {offset} is outside the {payload.Length}-byte name chunk.");
        }

        ReadOnlySpan<byte> tail = payload[(int)offset..];
        int length = tail.IndexOf((byte)0);
        if (length < 0)
        {
            throw new InvalidDataException($"{context}: the name at offset {offset} has no terminator inside its chunk.");
        }

        return Encoding.Latin1.GetString(tail[..length]);
    }
}
