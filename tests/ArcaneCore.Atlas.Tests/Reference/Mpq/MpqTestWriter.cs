using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ArcaneCore.Atlas.Reference.Mpq;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

/// <summary>How a synthetic file's sector checksum table is stored.</summary>
internal enum ChecksumTableForm
{
    /// <summary>No checksum table: the extra offset entry marks an empty one (the form most of the client's files have).</summary>
    Empty,

    /// <summary>A plain array of Adler-32 values.</summary>
    Plain,

    /// <summary>A plain array with one wrong value.</summary>
    PlainCorrupt,

    /// <summary>All zero ("not recorded"), stored with the sparse method as the client's patch archives do.</summary>
    SparseZeros,

    /// <summary>The Adler-32 values, stored with the sparse method (literal runs).</summary>
    SparseValues,
}

/// <summary>One file to place in a synthetic archive.</summary>
internal sealed class TestMpqFile(string name, byte[] data)
{
    /// <summary>In-archive path as written.</summary>
    public string Name { get; } = name;

    /// <summary>Uncompressed content.</summary>
    public byte[] Data { get; } = data;

    /// <summary>Compress each sector (or the single unit) with zlib; a sector that does not shrink is stored raw.</summary>
    public bool Zlib { get; init; }

    /// <summary>Encrypt the file (and, for multi-sector compressed files, its sector offset table).</summary>
    public bool Encrypted { get; init; }

    /// <summary>Adjust the key by the block offset and file size (only with <see cref="Encrypted"/>).</summary>
    public bool FixKey { get; init; }

    /// <summary>Store the whole file as one unit.</summary>
    public bool SingleUnit { get; init; }

    /// <summary>Checksum table form for a compressed multi-sector file (null: the file does not carry the checksum flag).</summary>
    public ChecksumTableForm? Checksums { get; init; }

    /// <summary>Write a deletion marker instead of content.</summary>
    public bool Delete { get; init; }

    /// <summary>Hash entry locale.</summary>
    public ushort Locale { get; init; }

    /// <summary>Hash entry platform byte.</summary>
    public byte Platform { get; init; }

    /// <summary>
    /// Exact stored bytes per sector, used as is (no compression is applied). With <see cref="ImplodeFlag"/> they are PKWARE
    /// streams; otherwise they begin with a method-mask byte.
    /// </summary>
    public byte[][]? StoredSectors { get; init; }

    /// <summary>Block flag 0x100 (whole-file PKWARE implode) instead of the multi-method flag.</summary>
    public bool ImplodeFlag { get; init; }

    /// <summary>With <see cref="StoredSectors"/>: add the multi-method flag 0x200.</summary>
    public bool CompressFlag { get; init; }

    /// <summary>High 16 bits of the block offset, written to the hi-block table (version 1 archives).</summary>
    public ushort HighOffset { get; init; }
}

/// <summary>Options for <see cref="MpqTestWriter.Build"/>.</summary>
internal sealed class TestMpqOptions
{
    /// <summary>Header format version field: 0 (0x20-byte header) or 1 (0x2C-byte header).</summary>
    public ushort FormatVersion { get; init; }

    /// <summary>Sector size is 512 &lt;&lt; this.</summary>
    public ushort SectorShift { get; init; } = 3;

    /// <summary>Hash table entries (power of two); 0 picks the smallest power of two of at least twice the file count and 16.</summary>
    public int HashSize { get; init; }

    /// <summary>Precede the archive by a user-data header and a gap, as map archives do.</summary>
    public bool UserData { get; init; }

    /// <summary>Add a <c>(listfile)</c> (zlib-compressed) naming every file.</summary>
    public bool ListFile { get; init; } = true;

    /// <summary>Put a deleted hash entry on this path's start slot, so a correct lookup must walk past it.</summary>
    public string? DeletedHashBefore { get; init; }
}

/// <summary>
/// Builds MPQ archives in memory for the reader tests. Deliberately written from the format description rather than from the
/// reader: it encrypts (the reader only decrypts), places tables and files itself, and computes checksums its own way. Only
/// the crypt table and name hash are shared with the reader, and those are pinned to external constants by other tests.
/// </summary>
internal static class MpqTestWriter
{
    private const uint Exists = 0x80000000;
    private const uint Implode = 0x100;
    private const uint Compress = 0x200;
    private const uint Encrypted = 0x10000;
    private const uint FixKey = 0x20000;
    private const uint SingleUnit = 0x01000000;
    private const uint DeleteMarker = 0x02000000;
    private const uint SectorCrc = 0x04000000;

    private sealed record Placed(TestMpqFile File, uint BlockIndex, long Offset, byte[] Stored, uint Flags, uint FileSize);

    /// <summary>Builds the archive bytes.</summary>
    public static byte[] Build(IReadOnlyList<TestMpqFile> files, TestMpqOptions? options = null)
    {
        options ??= new TestMpqOptions();
        var all = new List<TestMpqFile>(files);
        if (options.ListFile)
        {
            string text = string.Concat(files.Where(f => !f.Delete || true).Select(f => f.Name + "\r\n"));
            all.Add(new TestMpqFile("(listfile)", Encoding.Latin1.GetBytes(text)) { Zlib = true });
        }

        int sectorSize = 512 << options.SectorShift;
        int headerSize = options.FormatVersion == 0 ? 0x20 : 0x2C;
        var body = new MemoryStream();
        var placed = new List<Placed>();
        long offset = headerSize;
        uint blockIndex = 0;
        foreach (TestMpqFile file in all)
        {
            (byte[] stored, uint flags, uint fileSize) = file.Delete ? ([], Exists | DeleteMarker, 0u) : Store(file, offset, sectorSize);
            placed.Add(new Placed(file, blockIndex++, offset, stored, flags, fileSize));
            body.Write(stored);
            offset += stored.Length;
        }

        int hashSize = options.HashSize != 0 ? options.HashSize : Math.Max(16, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)all.Count * 2));
        long hashOffset = offset;
        long blockOffset = hashOffset + (hashSize * 16L);
        long hiOffset = blockOffset + (placed.Count * 16L);
        long end = options.FormatVersion == 1 ? hiOffset + (placed.Count * 2L) : hiOffset;

        byte[] hashTable = BuildHashTable(placed, hashSize, options.DeletedHashBefore);
        byte[] blockTable = new byte[placed.Count * 16];
        for (int i = 0; i < placed.Count; i++)
        {
            Span<byte> e = blockTable.AsSpan(i * 16, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(e, (uint)placed[i].Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], (uint)placed[i].Stored.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], placed[i].FileSize);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], placed[i].Flags);
        }

        Encrypt(hashTable, MpqCrypto.HashTableKey);
        Encrypt(blockTable, MpqCrypto.BlockTableKey);

        var header = new byte[headerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x1A51504D);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)end);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), options.FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), options.SectorShift);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)hashOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)blockOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)hashSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)placed.Count);
        if (options.FormatVersion == 1)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), (ulong)hiOffset);
        }

        var archive = new MemoryStream();
        const int userGap = 0x200;
        if (options.UserData)
        {
            var user = new byte[userGap];
            BinaryPrimitives.WriteUInt32LittleEndian(user, 0x1B51504D);
            BinaryPrimitives.WriteUInt32LittleEndian(user.AsSpan(4), 0x20);
            BinaryPrimitives.WriteUInt32LittleEndian(user.AsSpan(8), userGap);
            BinaryPrimitives.WriteUInt32LittleEndian(user.AsSpan(12), 0x10);
            archive.Write(user);
        }

        archive.Write(header);
        archive.Write(body.ToArray());
        archive.Write(hashTable);
        archive.Write(blockTable);
        if (options.FormatVersion == 1)
        {
            var hi = new byte[placed.Count * 2];
            for (int i = 0; i < placed.Count; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(hi.AsSpan(i * 2), placed[i].File.HighOffset);
            }

            archive.Write(hi);
        }

        return archive.ToArray();
    }

    /// <summary>The name hash of the format description, for expected-slot arithmetic in tests.</summary>
    public static uint Hash(string name, uint type) => MpqCrypto.HashString(name, type);

    /// <summary>Encrypts <paramref name="data"/> in place (the inverse of the reader's decryption).</summary>
    public static void Encrypt(Span<byte> data, uint key)
    {
        // The stream cipher of the format: key2 starts at 0xEEEEEEEE, advances by the table row 4 entry selected by key1's
        // low byte, and both keys are updated from the plaintext word. ref: StormLib src/SBaseCommon.cpp:436-474
        uint key1 = key;
        uint key2 = 0xEEEEEEEE;
        ReadOnlySpan<uint> table = MpqCrypto.Table;
        for (int i = 0; i + 4 <= data.Length; i += 4)
        {
            key2 += table[0x400 + (int)(key1 & 0xFF)];
            uint plain = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            BinaryPrimitives.WriteUInt32LittleEndian(data[i..], plain ^ (key1 + key2));
            key1 = ((~key1 << 0x15) + 0x11111111) | (key1 >> 0x0B);
            key2 = plain + key2 + (key2 << 5) + 3;
        }
    }

    /// <summary>Builds a sparse stream (without the method mask byte) for <paramref name="data"/>.</summary>
    public static byte[] SparseEncode(ReadOnlySpan<byte> data)
    {
        // The operations of the format: a high-bit byte plus (n & 0x7F) + 1 literal bytes, or a clear byte for (n & 0x7F) + 3 zeros.
        // ref: StormLib src/sparse/sparse.cpp:225-287
        var output = new List<byte>
        {
            (byte)(data.Length >> 24),
            (byte)(data.Length >> 16),
            (byte)(data.Length >> 8),
            (byte)data.Length,
        };
        int i = 0;
        while (i < data.Length)
        {
            int zeros = 0;
            while (i + zeros < data.Length && data[i + zeros] == 0 && zeros < 130)
            {
                zeros++;
            }

            if (zeros >= 3)
            {
                output.Add((byte)(zeros - 3));
                i += zeros;
                continue;
            }

            int literals = 0;
            while (i + literals < data.Length && literals < 128 && !(i + literals + 2 < data.Length && data[i + literals] == 0 && data[i + literals + 1] == 0 && data[i + literals + 2] == 0))
            {
                literals++;
            }

            output.Add((byte)(0x80 | (literals - 1)));
            output.AddRange(data.Slice(i, literals).ToArray());
            i += literals;
        }

        return [.. output];
    }

    private static (byte[] Stored, uint Flags, uint FileSize) Store(TestMpqFile file, long offset, int sectorSize)
    {
        uint flags = Exists;
        if (file.Encrypted)
        {
            flags |= Encrypted;
            if (file.FixKey)
            {
                flags |= FixKey;
            }
        }

        if (file.SingleUnit)
        {
            flags |= SingleUnit;
        }

        uint fileSize = (uint)file.Data.Length;
        if (file.Data.Length == 0 && file.StoredSectors is null)
        {
            return ([], Exists, 0);
        }

        uint key = file.Encrypted ? MpqCrypto.FileKey(file.Name, offset, fileSize, file.FixKey) : 0;

        if (file.StoredSectors is { } given)
        {
            flags |= file.ImplodeFlag ? Implode : 0;
            flags |= file.CompressFlag ? Compress : 0;
            flags |= file.Checksums is not null && (flags & (Compress | Implode)) != 0 ? SectorCrc : 0;
            return (Pack(file, given, flags, key, sectorSize), flags, fileSize);
        }

        if (file.SingleUnit)
        {
            byte[] unit = [.. file.Zlib ? Smaller(CompressZlib(file.Data), file.Data) : file.Data];
            flags |= file.Zlib ? Compress : 0;
            if (file.Encrypted)
            {
                Encrypt(unit, key);
            }

            return (unit, flags, fileSize);
        }

        int count = (file.Data.Length + sectorSize - 1) / sectorSize;
        var sectors = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            byte[] plain = file.Data.AsSpan(i * sectorSize, Math.Min(sectorSize, file.Data.Length - (i * sectorSize))).ToArray();
            sectors[i] = file.Zlib ? Smaller(CompressZlib(plain), plain) : plain;
        }

        flags |= file.Zlib ? Compress : 0;
        flags |= file.Checksums is not null && file.Zlib ? SectorCrc : 0;
        return (Pack(file, sectors, flags, key, sectorSize), flags, fileSize);
    }

    // Lays out the sectors (with the offset table of compressed files and the optional checksum table) and encrypts them.
    private static byte[] Pack(TestMpqFile file, byte[][] sectors, uint flags, uint key, int sectorSize)
    {
        bool compressed = (flags & (Compress | Implode)) != 0;
        bool crc = (flags & SectorCrc) != 0;

        var output = new MemoryStream();
        if (!compressed)
        {
            for (int i = 0; i < sectors.Length; i++)
            {
                byte[] copy = [.. sectors[i]];
                if (file.Encrypted)
                {
                    Encrypt(copy, key + (uint)i);
                }

                output.Write(copy);
            }

            return output.ToArray();
        }

        int entries = sectors.Length + 1 + (crc ? 1 : 0);
        var offsets = new uint[entries];
        offsets[0] = (uint)(entries * 4);
        for (int i = 0; i < sectors.Length; i++)
        {
            offsets[i + 1] = offsets[i] + (uint)sectors[i].Length;
        }

        byte[] checksumTable = [];
        if (crc)
        {
            checksumTable = ChecksumBytes(file.Checksums!.Value, sectors);
            offsets[^1] = offsets[sectors.Length] + (uint)checksumTable.Length;
        }

        var table = new byte[entries * 4];
        for (int i = 0; i < entries; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(i * 4), offsets[i]);
        }

        if (file.Encrypted)
        {
            Encrypt(table, key - 1);
        }

        output.Write(table);
        for (int i = 0; i < sectors.Length; i++)
        {
            byte[] copy = [.. sectors[i]];
            if (file.Encrypted)
            {
                Encrypt(copy, key + (uint)i);
            }

            output.Write(copy);
        }

        output.Write(checksumTable);
        return output.ToArray();
    }

    private static byte[] ChecksumBytes(ChecksumTableForm form, byte[][] sectors)
    {
        if (form == ChecksumTableForm.Empty)
        {
            return [];
        }

        var values = new byte[sectors.Length * 4];
        for (int i = 0; i < sectors.Length; i++)
        {
            // SparseValues records only the first sector's checksum, so that the sparse form is shorter than the plain table.
            bool recorded = form switch
            {
                ChecksumTableForm.SparseZeros => false,
                ChecksumTableForm.SparseValues => i == 0,
                _ => true,
            };
            uint adler = recorded ? MpqChecksum.Adler32(MpqChecksum.SectorSeed, sectors[i]) : 0;
            if (form == ChecksumTableForm.PlainCorrupt && i == sectors.Length - 1)
            {
                adler ^= 0x00010000;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(values.AsSpan(i * 4), adler);
        }

        if (form is ChecksumTableForm.Plain or ChecksumTableForm.PlainCorrupt)
        {
            return values;
        }

        // Sparse: a mask byte 0x20 and the sparse stream. The reader tells a compressed table from a plain one by its length,
        // so a sparse table must be shorter than the plain one.
        byte[] sparse = [0x20, .. SparseEncode(values)];
        return sparse.Length < values.Length
            ? sparse
            : throw new InvalidOperationException($"A sparse checksum table of {sparse.Length} bytes is not shorter than the plain {values.Length}; use more sectors.");
    }

    private static byte[] CompressZlib(byte[] plain)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(plain);
        }

        return [0x02, .. output.ToArray()];
    }

    // A compressed sector that is not smaller than the plain one is stored plain (the reader tells them apart by length).
    private static byte[] Smaller(byte[] compressed, byte[] plain) => compressed.Length < plain.Length ? compressed : plain;

    private static byte[] BuildHashTable(List<Placed> placed, int size, string? deletedBefore)
    {
        var table = new byte[size * 16];
        for (int i = 0; i < size; i++)
        {
            Span<byte> e = table.AsSpan(i * 16, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(e, 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt16LittleEndian(e[8..], 0xFFFF);
            e[10] = 0xFF;
            e[11] = 0xFF;
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], 0xFFFFFFFF);
        }

        bool IsFree(int slot) => BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan((slot * 16) + 12)) == 0xFFFFFFFF;

        if (deletedBefore is not null)
        {
            int slot = (int)(MpqCrypto.HashString(deletedBefore, MpqCrypto.HashTableIndex) & (uint)(size - 1));
            Span<byte> e = table.AsSpan(slot * 16, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(e, 0x12345678);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], 0x9ABCDEF0);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], 0xFFFFFFFE);
        }

        foreach (Placed p in placed)
        {
            int slot = (int)(MpqCrypto.HashString(p.File.Name, MpqCrypto.HashTableIndex) & (uint)(size - 1));
            while (!IsFree(slot))
            {
                // A deleted slot is not free: the writer skips it too, which is what makes the reader's walk matter.
                slot = (slot + 1) & (size - 1);
            }

            Span<byte> e = table.AsSpan(slot * 16, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(e, MpqCrypto.HashString(p.File.Name, MpqCrypto.HashNameA));
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], MpqCrypto.HashString(p.File.Name, MpqCrypto.HashNameB));
            BinaryPrimitives.WriteUInt16LittleEndian(e[8..], p.File.Locale);
            e[10] = p.File.Platform;
            e[11] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], p.BlockIndex);
        }

        return table;
    }
}
