using System.Buffers.Binary;

namespace ArcaneCore.Atlas.Reference.Mpq;

/// <summary>
/// The MPQ crypt table, name hashing and block decryption. Stateless and thread-safe: the 0x500-entry table is built
/// once by the type initializer and never written again.
/// </summary>
public static class MpqCrypto
{
    /// <summary>Hash type for the hash-table start index.</summary>
    // ref: StormLib src/StormCommon.h:215
    public const uint HashTableIndex = 0x000;

    /// <summary>Hash type for the first name hash (<c>dwName1</c>).</summary>
    // ref: StormLib src/StormCommon.h:216
    public const uint HashNameA = 0x100;

    /// <summary>Hash type for the second name hash (<c>dwName2</c>).</summary>
    // ref: StormLib src/StormCommon.h:217
    public const uint HashNameB = 0x200;

    /// <summary>Hash type for encrypted-file keys.</summary>
    // ref: StormLib src/StormCommon.h:218
    public const uint HashFileKey = 0x300;

    /// <summary>Offset of the key-mixing rows of the crypt table (used inside block decryption).</summary>
    // ref: StormLib src/StormCommon.h:219
    public const uint HashKeyMix = 0x400;

    /// <summary>Key of the hash table: <c>HashString("(hash table)", HashFileKey)</c>.</summary>
    // ref: StormLib src/StormLib.h:295 ; vmangos@0e3ff01 dep/libmpq/libmpq/mpq.c:223
    public const uint HashTableKey = 0xC3AF3770;

    /// <summary>Key of the block table: <c>HashString("(block table)", HashFileKey)</c>.</summary>
    // ref: StormLib src/StormLib.h:296 ; vmangos@0e3ff01 dep/libmpq/libmpq/mpq.c:242
    public const uint BlockTableKey = 0xEC83B3A3;

    /// <summary>Number of 32-bit entries of the crypt table (five 0x100-entry rows).</summary>
    // ref: StormLib src/SBaseCommon.cpp:213
    public const int CryptTableSize = 0x500;

    private static readonly uint[] CryptTable = BuildCryptTable();

    /// <summary>The crypt table (read-only view; for tests and diagnostics).</summary>
    public static ReadOnlySpan<uint> Table => CryptTable;

    /// <summary>
    /// Hashes a path with one of the <c>Hash*</c> types. Letters are upper-cased (ASCII only), a forward slash counts as a
    /// backslash, and every character above 0xFF hashes as '?' (names in the client's archives are single-byte).
    /// </summary>
    public static uint HashString(string name, uint hashType)
    {
        ArgumentNullException.ThrowIfNull(name);
        uint seed1 = 0x7FED7FED;
        uint seed2 = 0xEEEEEEEE;
        foreach (char c in name)
        {
            uint ch = Normalize(c > 0xFF ? (byte)'?' : (byte)c);
            seed1 = CryptTable[hashType + ch] ^ (seed1 + seed2);
            seed2 = ch + seed1 + seed2 + (seed2 << 5) + 3;
        }

        return seed1;
    }

    /// <summary>Byte-span form of <see cref="HashString(string, uint)"/> (for names read straight from a listfile).</summary>
    public static uint HashString(ReadOnlySpan<byte> name, uint hashType)
    {
        uint seed1 = 0x7FED7FED;
        uint seed2 = 0xEEEEEEEE;
        foreach (byte b in name)
        {
            uint ch = Normalize(b);
            seed1 = CryptTable[hashType + ch] ^ (seed1 + seed2);
            seed2 = ch + seed1 + seed2 + (seed2 << 5) + 3;
        }

        return seed1;
    }

    /// <summary>
    /// Decrypts <paramref name="data"/> in place with <paramref name="key"/>. Only whole little-endian 32-bit words are
    /// transformed; a trailing 1-3 bytes stay as stored.
    /// </summary>
    public static void Decrypt(Span<byte> data, uint key)
    {
        // ref: StormLib src/SBaseCommon.cpp:494-516 (DecryptMpqBlock)
        uint key1 = key;
        uint key2 = 0xEEEEEEEE;
        int words = data.Length >> 2;
        for (int i = 0; i < words; i++)
        {
            Span<byte> word = data.Slice(i * 4, 4);
            key2 += CryptTable[HashKeyMix + (key1 & 0xFF)];
            uint plain = BinaryPrimitives.ReadUInt32LittleEndian(word) ^ (key1 + key2);
            BinaryPrimitives.WriteUInt32LittleEndian(word, plain);
            key1 = ((~key1 << 0x15) + 0x11111111) | (key1 >> 0x0B);
            key2 = plain + key2 + (key2 << 5) + 3;
        }
    }

    /// <summary>
    /// The key of an encrypted file: the hash of its plain name (the part after the last slash), mixed with the block's
    /// archive-relative offset and file size when the fix-key flag is set.
    /// </summary>
    /// <param name="path">The in-archive path (either slash).</param>
    /// <param name="blockOffset">Archive-relative offset of the file data (only its low 32 bits count).</param>
    /// <param name="fileSize">Uncompressed size from the block table.</param>
    /// <param name="fixKey">True when the block carries <see cref="MpqFileFlags.FixKey"/>.</param>
    public static uint FileKey(string path, long blockOffset, uint fileSize, bool fixKey)
    {
        // ref: StormLib src/SBaseCommon.cpp:685-704 (DecryptFileKey); plain name: StormCommon.h:369-381
        ArgumentNullException.ThrowIfNull(path);
        int cut = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        string plain = cut >= 0 ? path[(cut + 1)..] : path;
        uint key = HashString(plain, HashFileKey);
        return fixKey ? (key + (uint)blockOffset) ^ fileSize : key;
    }

    private static byte Normalize(byte b)
    {
        // ref: StormLib src/SBaseCommon.cpp:55-73 (AsciiToUpperTable: a-z to A-Z, '/' to '\\', all else unchanged)
        return b switch
        {
            >= (byte)'a' and <= (byte)'z' => (byte)(b - 0x20),
            (byte)'/' => (byte)'\\',
            _ => b,
        };
    }

    private static uint[] BuildCryptTable()
    {
        // ref: StormLib src/SBaseCommon.cpp:219-244 (InitializeMpqCryptography); the same table is the
        // literal array in vmangos@0e3ff01 dep/libmpq/libmpq/crypt_buf.h.
        var table = new uint[CryptTableSize];
        uint seed = 0x00100001;
        for (uint index1 = 0; index1 < 0x100; index1++)
        {
            uint index2 = index1;
            for (int i = 0; i < 5; i++, index2 += 0x100)
            {
                seed = ((seed * 125) + 3) % 0x2AAAAB;
                uint high = (seed & 0xFFFF) << 0x10;
                seed = ((seed * 125) + 3) % 0x2AAAAB;
                uint low = seed & 0xFFFF;
                table[index2] = high | low;
            }
        }

        return table;
    }
}

/// <summary>Block-table flag bits of a stored file.</summary>
public static class MpqFileFlags
{
    /// <summary>Whole-file PKWARE DCL implode (<c>MPQ_FILE_IMPLODE</c>).</summary>
    // ref: StormLib src/StormLib.h:219 ; vmangos@0e3ff01 dep/libmpq/libmpq/mpq-internal.h:43
    public const uint Implode = 0x00000100;

    /// <summary>Per-sector compression with a leading method-mask byte (<c>MPQ_FILE_COMPRESS</c>).</summary>
    // ref: StormLib src/StormLib.h:220 ; mpq-internal.h:44
    public const uint Compress = 0x00000200;

    /// <summary>Mask that says "this file has a sector offset table" (<c>MPQ_FILE_COMPRESS_MASK</c>).</summary>
    // ref: StormLib src/StormLib.h:232 ; mpq-internal.h:42
    public const uint CompressMask = 0x0000FF00;

    /// <summary>The file is encrypted.</summary>
    // ref: StormLib src/StormLib.h:221 ; mpq-internal.h:41
    public const uint Encrypted = 0x00010000;

    /// <summary>The file key is adjusted by the block offset and size (StormLib: <c>MPQ_FILE_KEY_V2</c>, "fix key").</summary>
    // ref: StormLib src/StormLib.h:222,236 ; SBaseCommon.cpp:699-700
    public const uint FixKey = 0x00020000;

    /// <summary>The file is an incremental patch (Cataclysm and later; not used by the 1.12.1 client).</summary>
    // ref: StormLib src/StormLib.h:223
    public const uint PatchFile = 0x00100000;

    /// <summary>The file is one unit rather than a run of sectors.</summary>
    // ref: StormLib src/StormLib.h:224 ; mpq-internal.h:46
    public const uint SingleUnit = 0x01000000;

    /// <summary>The entry is a deletion marker used by patch archives.</summary>
    // ref: StormLib src/StormLib.h:225
    public const uint DeleteMarker = 0x02000000;

    /// <summary>The file has a checksum table after its sectors.</summary>
    // ref: StormLib src/StormLib.h:226 ; mpq-internal.h:47
    public const uint SectorCrc = 0x04000000;

    /// <summary>The entry is a file (cleared for a free or deleted block).</summary>
    // ref: StormLib src/StormLib.h:229 ; mpq-internal.h:40
    public const uint Exists = 0x80000000;
}

/// <summary>Bits of the method-mask byte that opens a compressed sector (<c>MPQ_COMPRESSION_*</c>).</summary>
public static class MpqCompressionMask
{
    /// <summary>Huffman (used on WAVE files).</summary>
    // ref: StormLib src/StormLib.h:275
    public const byte Huffman = 0x01;

    /// <summary>zlib.</summary>
    // ref: StormLib src/StormLib.h:276
    public const byte Zlib = 0x02;

    /// <summary>PKWARE DCL.</summary>
    // ref: StormLib src/StormLib.h:277
    public const byte Pkware = 0x08;

    /// <summary>bzip2.</summary>
    // ref: StormLib src/StormLib.h:278
    public const byte Bzip2 = 0x10;

    /// <summary>Sparse (run-length of zero runs).</summary>
    // ref: StormLib src/StormLib.h:279
    public const byte Sparse = 0x20;

    /// <summary>IMA ADPCM, mono.</summary>
    // ref: StormLib src/StormLib.h:280
    public const byte AdpcmMono = 0x40;

    /// <summary>IMA ADPCM, stereo.</summary>
    // ref: StormLib src/StormLib.h:281
    public const byte AdpcmStereo = 0x80;

    /// <summary>A readable name for each single bit (or the hex byte for unknown bits).</summary>
    public static string Describe(byte mask)
    {
        var parts = new List<string>(4);
        void Add(byte bit, string name)
        {
            if ((mask & bit) != 0)
            {
                parts.Add(name);
            }
        }

        Add(Huffman, "huffman");
        Add(Zlib, "zlib");
        Add(Pkware, "pkware");
        Add(Bzip2, "bzip2");
        Add(Sparse, "sparse");
        Add(AdpcmMono, "adpcm-mono");
        Add(AdpcmStereo, "adpcm-stereo");
        if ((mask & 0x04) != 0)
        {
            parts.Add("unknown-0x04");
        }

        return $"0x{mask:X2}({string.Join('+', parts)})";
    }
}
