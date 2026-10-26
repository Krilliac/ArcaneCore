using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ArcaneCore.Atlas.Reference.Mpq;

/// <summary>An MPQ user-data header ('MPQ\x1B') that points at the real archive header.</summary>
/// <param name="Position">File offset of the user-data header.</param>
/// <param name="DataSize">Maximum size of the user data (<c>cbUserDataSize</c>).</param>
/// <param name="HeaderOffset">Offset of the archive header relative to <paramref name="Position"/>.</param>
/// <param name="HeaderSize">Size of the user-data header (<c>cbUserDataHeader</c>).</param>
public sealed record MpqUserData(long Position, uint DataSize, uint HeaderOffset, uint HeaderSize);

/// <summary>The fixed header of an MPQ archive (format version 0: 0x20 bytes; version 1: 0x2C bytes).</summary>
/// <param name="Position">File offset of the header (block offsets are relative to it).</param>
/// <param name="HeaderSize">Header size field.</param>
/// <param name="ArchiveSize">Deprecated 32-bit archive size field.</param>
/// <param name="FormatVersion">Raw format version field (0 = original, 1 = Burning Crusade extension; tools that print "format 2" count from one).</param>
/// <param name="SectorShift">File sectors are <c>512 &lt;&lt; SectorShift</c> bytes.</param>
/// <param name="HashTableOffset">Archive-relative offset of the hash table (high 16 bits from the extension).</param>
/// <param name="BlockTableOffset">Archive-relative offset of the block table (high 16 bits from the extension).</param>
/// <param name="HashTableEntries">Number of hash table entries (a power of two).</param>
/// <param name="BlockTableEntries">Number of block table entries.</param>
/// <param name="HiBlockTableOffset">Archive-relative offset of the 16-bit high-offset array, or 0 when absent.</param>
/// <param name="UserData">The user-data header that led here, if any.</param>
public sealed record MpqHeader(
    long Position,
    uint HeaderSize,
    uint ArchiveSize,
    ushort FormatVersion,
    ushort SectorShift,
    long HashTableOffset,
    long BlockTableOffset,
    uint HashTableEntries,
    uint BlockTableEntries,
    long HiBlockTableOffset,
    MpqUserData? UserData);

/// <summary>One block table entry: where a stored file lives and how it is stored.</summary>
/// <param name="Offset">Archive-relative file offset (32-bit low part plus the 16-bit high part when the archive has a hi-block table).</param>
/// <param name="CompressedSize">Stored size, including any sector offset table and checksum table.</param>
/// <param name="FileSize">Size after decompression.</param>
/// <param name="Flags">The <see cref="MpqFileFlags"/> bits.</param>
public readonly record struct MpqBlock(long Offset, uint CompressedSize, uint FileSize, uint Flags)
{
    /// <summary>True when the entry holds a file (not a free or deleted block).</summary>
    public bool Exists => (Flags & MpqFileFlags.Exists) != 0;

    /// <summary>True when the data is encrypted.</summary>
    public bool IsEncrypted => (Flags & MpqFileFlags.Encrypted) != 0;

    /// <summary>True when any compression bit is set (the file has a sector offset table unless it is a single unit).</summary>
    public bool IsCompressed => (Flags & MpqFileFlags.CompressMask) != 0;

    /// <summary>True when the file is one unit rather than sectors.</summary>
    public bool IsSingleUnit => (Flags & MpqFileFlags.SingleUnit) != 0;
}

/// <summary>One hash table entry (a file name hash pair plus locale and platform).</summary>
/// <param name="NameA">Hash of the path with type <see cref="MpqCrypto.HashNameA"/>.</param>
/// <param name="NameB">Hash of the path with type <see cref="MpqCrypto.HashNameB"/>.</param>
/// <param name="Locale">Windows LANGID; 0 is neutral.</param>
/// <param name="Platform">Platform byte; 0 is the default.</param>
/// <param name="BlockIndex">Index into the block table, or <see cref="MpqArchive.HashEntryFree"/> / <see cref="MpqArchive.HashEntryDeleted"/>.</param>
public readonly record struct MpqHashEntry(uint NameA, uint NameB, ushort Locale, byte Platform, uint BlockIndex);

/// <summary>The three hashes that locate a path in an MPQ hash table. Compute once, probe any number of archives.</summary>
/// <param name="TableIndex">Hash of type <see cref="MpqCrypto.HashTableIndex"/> (the start slot before masking).</param>
/// <param name="NameA">Hash of type <see cref="MpqCrypto.HashNameA"/>.</param>
/// <param name="NameB">Hash of type <see cref="MpqCrypto.HashNameB"/>.</param>
public readonly record struct MpqNameHash(uint TableIndex, uint NameA, uint NameB)
{
    /// <summary>Hashes <paramref name="path"/> (case-insensitive, either slash).</summary>
    public static MpqNameHash Of(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new MpqNameHash(
            MpqCrypto.HashString(path, MpqCrypto.HashTableIndex),
            MpqCrypto.HashString(path, MpqCrypto.HashNameA),
            MpqCrypto.HashString(path, MpqCrypto.HashNameB));
    }
}

/// <summary>A path resolved to its hash slot and block.</summary>
/// <param name="HashIndex">Slot in the hash table.</param>
/// <param name="BlockIndex">Index in the block table.</param>
/// <param name="Locale">Locale of the chosen entry.</param>
/// <param name="Block">The block.</param>
public readonly record struct MpqEntry(int HashIndex, uint BlockIndex, ushort Locale, MpqBlock Block);

/// <summary>
/// A read-only MPQ archive (format versions 0 and 1) opened by path. All reads are positional, so a single instance can
/// be used from many threads; tables are decoded once at <see cref="Open"/> and never change.
/// </summary>
public sealed class MpqArchive : IDisposable
{
    /// <summary>Hash entry that never held a file; terminates a search.</summary>
    // ref: StormLib src/StormLib.h:172 ; vmangos@0e3ff01 dep/libmpq/libmpq/mpq-internal.h:50
    public const uint HashEntryFree = 0xFFFFFFFF;

    /// <summary>Hash entry that held a file once; does not terminate a search.</summary>
    // ref: StormLib src/StormLib.h:171
    public const uint HashEntryDeleted = 0xFFFFFFFE;

    /// <summary>Largest file this reader will materialise (a guard against a corrupt size field).</summary>
    public const long MaxFileBytes = 1L << 30;

    // ref: StormLib src/StormLib.h:149-150 ; mpq-internal.h:33
    private const uint HeaderMagic = 0x1A51504D;
    private const uint UserDataMagic = 0x1B51504D;

    // ref: StormLib src/StormLib.h:511-512
    private const int HeaderSizeV1 = 0x20;
    private const int HeaderSizeV2 = 0x2C;

    // The header is searched on 0x200 boundaries (ref: SFileOpenArchive.cpp:345,414); the search here is bounded to the
    // first MiB, which covers every archive of the client (header at offset 0).
    private const long HeaderSearchLimit = 1 << 20;

    // ref: StormLib src/StormLib.h:568-570 (hash table below 2^20 entries in the extended format)
    private const uint MaxHashEntries = 1 << 20;
    private const uint MaxBlockEntries = 1 << 22;

    private readonly SafeFileHandle _handle;
    private readonly long _fileLength;
    private readonly long _offsetMask;
    private readonly MpqHashEntry[] _hashes;
    private readonly MpqBlock[] _blocks;
    private readonly Lazy<IReadOnlyList<string>> _listFile;

    private MpqArchive(
        string path,
        SafeFileHandle handle,
        long fileLength,
        MpqHeader header,
        MpqHashEntry[] hashes,
        MpqBlock[] blocks,
        bool verifyChecksums)
    {
        Path = path;
        _handle = handle;
        _fileLength = fileLength;
        Header = header;
        _hashes = hashes;
        _blocks = blocks;
        VerifyChecksums = verifyChecksums;

        // ref: StormLib src/SBaseFileTable.cpp:411-414,419 (version 0 offsets are 32-bit and wrap)
        _offsetMask = header.FormatVersion == 0 ? 0xFFFFFFFFL : -1L;
        SectorSize = 0x200 << header.SectorShift;
        _listFile = new Lazy<IReadOnlyList<string>>(LoadListFile, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Full path the archive was opened from.</summary>
    public string Path { get; }

    /// <summary>File name of the archive (for example <c>patch-2.MPQ</c>).</summary>
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>The parsed header.</summary>
    public MpqHeader Header { get; }

    /// <summary>Size in bytes of a file sector.</summary>
    // ref: StormLib src/SFileOpenArchive.cpp:505 ; mpq-internal.h:72 (512 * 2^block_size)
    public int SectorSize { get; }

    /// <summary>Length of the archive file on disk.</summary>
    public long FileLength => _fileLength;

    /// <summary>The block table in file order.</summary>
    public ReadOnlySpan<MpqBlock> Blocks => _blocks;

    /// <summary>The hash table in file order.</summary>
    public ReadOnlySpan<MpqHashEntry> Hashes => _hashes;

    /// <summary>
    /// Names from the archive's <c>(listfile)</c> that resolve to a file in this archive (empty when it has no listfile).
    /// Built on first use.
    /// </summary>
    public IReadOnlyList<string> ListFile => _listFile.Value;

    /// <summary>
    /// True when reads check each sector of a file that carries the checksum flag against the file's checksum table
    /// (Adler-32 of the stored sector, see <see cref="MpqChecksum"/>) and fail on a mismatch.
    /// </summary>
    public bool VerifyChecksums { get; }

    /// <summary>Opens <paramref name="path"/> and decodes its header, hash table and block table.</summary>
    /// <param name="path">The archive file.</param>
    /// <param name="verifyChecksums">Check sector checksums on every read (default); see <see cref="VerifyChecksums"/>.</param>
    /// <exception cref="InvalidDataException">Not an MPQ, or the tables are damaged.</exception>
    /// <exception cref="NotSupportedException">The format version is newer than the client's (2 and above).</exception>
    public static MpqArchive Open(string path, bool verifyChecksums = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = System.IO.Path.GetFullPath(path);
        SafeFileHandle handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try
        {
            long length = RandomAccess.GetLength(handle);
            MpqHeader header = ReadHeader(handle, length, full);
            long mask = header.FormatVersion == 0 ? 0xFFFFFFFFL : -1L;
            MpqHashEntry[] hashes = ReadHashTable(handle, length, header, mask, full);
            MpqBlock[] blocks = ReadBlockTable(handle, length, header, mask, full);
            return new MpqArchive(full, handle, length, header, hashes, blocks, verifyChecksums);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Finds <paramref name="path"/> in this archive. False when the name is absent or its hash slot points at a free or
    /// invalid block. A block carrying <see cref="MpqFileFlags.DeleteMarker"/> is found like any other (check the flag).
    /// </summary>
    public bool TryFind(string path, out MpqEntry entry)
    {
        ArgumentNullException.ThrowIfNull(path);
        return TryFind(MpqNameHash.Of(path), out entry);
    }

    /// <summary>Finds a name whose hashes were computed once (to probe several archives with one hashing pass).</summary>
    public bool TryFind(in MpqNameHash hash, out MpqEntry entry)
    {
        entry = default;
        if (_hashes.Length == 0)
        {
            return false;
        }

        // ref: StormLib src/SBaseCommon.cpp:787-817,819-846 (chain walk: start slot from hash type 0,
        // match on both name hashes, stop at a free slot, wrap around once)
        uint mask = (uint)_hashes.Length - 1;
        uint start = hash.TableIndex & mask;
        uint nameA = hash.NameA;
        uint nameB = hash.NameB;

        // Locale policy: StormLib's GetHashEntryLocale with the preferred locale enUS (0x0409) and platform 0. An entry that is
        // exactly enUS wins at once; otherwise the last entry whose locale is neutral or enUS and whose platform is 0 wins;
        // an entry of any other locale or platform is never returned. The 1.12.1 client's own Storm.dll is not disassembled
        // here, so this is StormLib's documented behaviour (lane policy), not a verified client fact; the client's archives
        // hold locale 0 and platform 0 only.
        // ref: StormLib src/SBaseFileTable.cpp:811-850 (GetHashEntryLocale)
        bool found = false;
        uint index = start;
        do
        {
            MpqHashEntry h = _hashes[index];
            if (h.NameA == nameA && h.NameB == nameB && IsValidBlock(h.BlockIndex))
            {
                bool exact = h.Locale == PreferredLocale && h.Platform == 0;
                if (exact || ((h.Locale == 0 || h.Locale == PreferredLocale) && h.Platform == 0))
                {
                    entry = new MpqEntry((int)index, h.BlockIndex & BlockIndexMask, h.Locale, _blocks[h.BlockIndex & BlockIndexMask]);
                    found = true;
                    if (exact)
                    {
                        return true;
                    }
                }
            }
            else if (h.BlockIndex == HashEntryFree)
            {
                break;
            }

            index = (index + 1) & mask;
        }
        while (index != start);

        return found;
    }

    /// <summary>True when <paramref name="path"/> resolves to a file of this archive.</summary>
    public bool Contains(string path) => TryFind(path, out _);

    /// <summary>Reads the file at <paramref name="path"/>, or returns null when this archive does not hold it.</summary>
    public byte[]? TryRead(string path) => TryFind(path, out MpqEntry entry) ? Read(entry, path) : null;

    /// <summary>Reads the file at <paramref name="path"/>.</summary>
    /// <exception cref="FileNotFoundException">The archive does not hold the path.</exception>
    public byte[] Read(string path)
        => TryRead(path) ?? throw new FileNotFoundException($"'{path}' is not in {Name}.");

    /// <summary>
    /// Reads and fully decodes one entry. <paramref name="path"/> is needed to derive the key of encrypted files.
    /// </summary>
    /// <exception cref="InvalidDataException">The stored data is damaged.</exception>
    /// <exception cref="NotSupportedException">The entry uses a feature this reader does not implement (named in the message).</exception>
    public byte[] Read(MpqEntry entry, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        MpqBlock block = entry.Block;

        // ref: StormLib src/StormLib.h:223 (incremental patch: raw data begins with a patch header; Cataclysm-era)
        if ((block.Flags & MpqFileFlags.PatchFile) != 0)
        {
            throw new NotSupportedException($"{Name}: '{path}' is an incremental patch file (flag 0x{MpqFileFlags.PatchFile:X8}).");
        }

        if ((block.Flags & MpqFileFlags.DeleteMarker) != 0)
        {
            throw new NotSupportedException($"{Name}: '{path}' is a deletion marker (flag 0x{MpqFileFlags.DeleteMarker:X8}).");
        }

        if (block.FileSize == 0)
        {
            return [];
        }

        if (block.FileSize > MaxFileBytes)
        {
            throw new InvalidDataException($"{Name}: '{path}' claims {block.FileSize} bytes.");
        }

        int fileSize = (int)block.FileSize;
        uint key = block.IsEncrypted
            ? MpqCrypto.FileKey(path, block.Offset, block.FileSize, (block.Flags & MpqFileFlags.FixKey) != 0)
            : 0;
        var result = new byte[fileSize];
        if (block.IsSingleUnit)
        {
            ReadSingleUnit(block, key, path, result);
        }
        else if (block.IsCompressed)
        {
            ReadCompressedSectors(block, key, path, result);
        }
        else
        {
            ReadStoredSectors(block, key, result);
        }

        return result;
    }

    /// <summary>
    /// Reads raw archive bytes (relative to the header) without any decoding; used by the archive scanner.
    /// </summary>
    /// <exception cref="InvalidDataException">The range lies past the end of the file.</exception>
    public void ReadRaw(long archiveOffset, Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return;
        }

        long absolute = (Header.Position + archiveOffset) & _offsetMask;
        if (archiveOffset < 0 || absolute + destination.Length > _fileLength)
        {
            throw new InvalidDataException($"{Name}: {destination.Length} bytes at offset 0x{archiveOffset:X} lie outside the archive file.");
        }

        ReadExactly(_handle, absolute, destination, Name);
    }

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();

    // ref: StormLib src/StormLib.h:271 (BLOCK_INDEX_MASK)
    private const uint BlockIndexMask = 0x0FFFFFFF;

    // en-US LANGID; ref: StormLib src/SBaseFileTable.cpp:816-850 (the locale a lookup prefers)
    private const ushort PreferredLocale = 0x0409;

    private bool IsValidBlock(uint blockIndex)
    {
        // ref: StormLib src/SBaseFileTable.cpp:784-809 (IsValidHashEntry1: index below the block table,
        // block flagged as existing, data starting inside the file)
        uint index = blockIndex & BlockIndexMask;
        if (blockIndex >= HashEntryDeleted || index >= _blocks.Length)
        {
            return false;
        }

        MpqBlock block = _blocks[index];
        return block.Exists && ((Header.Position + block.Offset) & _offsetMask) < _fileLength;
    }

    private IReadOnlyList<string> LoadListFile()
    {
        const string listFileName = "(listfile)";
        if (!TryFind(listFileName, out MpqEntry entry))
        {
            return [];
        }

        byte[] bytes = Read(entry, listFileName);

        // ref: StormLib src/SFileListFile.cpp:275-301 (lines end at CR or LF; runs of them are skipped)
        var names = new List<string>();
        int position = 0;
        while (position < bytes.Length)
        {
            while (position < bytes.Length && bytes[position] is 0x0A or 0x0D)
            {
                position++;
            }

            int begin = position;
            while (position < bytes.Length && bytes[position] is not (0x0A or 0x0D))
            {
                position++;
            }

            if (position > begin)
            {
                string name = Encoding.Latin1.GetString(bytes, begin, position - begin);
                if (TryFind(name, out _))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    private void ReadStoredSectors(MpqBlock block, uint key, byte[] result)
    {
        // No sector offset table: the sectors are laid out back to back and (if encrypted) each has its own key.
        // ref: StormLib src/SFileReadFile.cpp:99-141 (offset = sector index * sector size; key + sector index)
        ReadRaw(block.Offset, result);
        if (!block.IsEncrypted)
        {
            return;
        }

        int sectorSize = SectorSize;
        for (int index = 0, start = 0; start < result.Length; index++, start += sectorSize)
        {
            MpqCrypto.Decrypt(result.AsSpan(start, Math.Min(sectorSize, result.Length - start)), key + (uint)index);
        }
    }

    private void ReadSingleUnit(MpqBlock block, uint key, string path, byte[] result)
    {
        // ref: StormLib src/SFileReadFile.cpp:239-348 (one block of CompressedSize bytes, decrypted with the
        // plain file key, then decompressed when the compression bits are set)
        int storedSize = checked((int)Math.Min(block.CompressedSize, MaxFileBytes));
        if (!block.IsCompressed && storedSize < result.Length)
        {
            throw new InvalidDataException($"{Name}: '{path}' stores {storedSize} bytes for a {result.Length}-byte file.");
        }

        byte[] stored = new byte[block.IsCompressed ? storedSize : result.Length];
        ReadRaw(block.Offset, stored);
        if (block.IsEncrypted)
        {
            MpqCrypto.Decrypt(stored, key);
        }

        if (!block.IsCompressed)
        {
            stored.CopyTo(result, 0);
            return;
        }

        DecompressUnit(block, stored, result, path);
    }

    private void ReadCompressedSectors(MpqBlock block, uint key, string path, byte[] result)
    {
        int sectorSize = SectorSize;
        int sectorCount = (result.Length + sectorSize - 1) / sectorSize;

        // The sector offset table: sectorCount + 1 little-endian offsets, plus one more dword when the file has checksums.
        // ref: StormLib src/SBaseCommon.cpp:1242-1251,1277-1305 ; vmangos@0e3ff01 dep/libmpq/libmpq/mpq.c:627-642,705
        int tableBytes = (sectorCount + 1) * 4;
        int readBytes = tableBytes + ((block.Flags & MpqFileFlags.SectorCrc) != 0 ? 4 : 0);
        byte[] tableRaw = new byte[readBytes];
        ReadRaw(block.Offset, tableRaw);
        if (block.IsEncrypted)
        {
            // The table is encrypted with the file key minus one.
            MpqCrypto.Decrypt(tableRaw, key - 1);
        }

        var offsets = new uint[readBytes / 4];
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(tableRaw.AsSpan(i * 4));
        }

        // ref: SBaseCommon.cpp:1314-1343 (offsets never decrease and no sector spans more than the sector size)
        if (offsets[0] < tableBytes)
        {
            throw new InvalidDataException($"{Name}: '{path}' sector table is corrupt (first sector at {offsets[0]} inside the {tableBytes}-byte table).");
        }

        for (int i = 0; i < sectorCount; i++)
        {
            if (offsets[i + 1] < offsets[i] || offsets[i + 1] - offsets[i] > (uint)sectorSize)
            {
                throw new InvalidDataException($"{Name}: '{path}' sector table is corrupt at sector {i}{(block.IsEncrypted ? " (wrong file key?)" : string.Empty)}.");
            }
        }

        uint total = offsets[sectorCount];
        if (total > block.CompressedSize || total > MaxFileBytes)
        {
            throw new InvalidDataException($"{Name}: '{path}' sectors end at {total}, past its stored size {block.CompressedSize}.");
        }

        byte[] stored = new byte[total];
        ReadRaw(block.Offset, stored);
        uint[]? checksums = VerifyChecksums && (block.Flags & MpqFileFlags.SectorCrc) != 0
            ? LoadChecksums(block, offsets, sectorCount, path)
            : null;

        for (int i = 0; i < sectorCount; i++)
        {
            int start = (int)offsets[i];
            int rawLength = (int)offsets[i + 1] - start;
            int outputStart = i * sectorSize;
            int expected = Math.Min(sectorSize, result.Length - outputStart);

            if (block.IsEncrypted)
            {
                // ref: SFileReadFile.cpp:123-141 (each sector uses file key + sector index)
                MpqCrypto.Decrypt(stored.AsSpan(start, rawLength), key + (uint)i);
            }

            if (checksums is not null)
            {
                // The checksum covers the stored sector after decryption and before decompression; a stored value of 0 or
                // 0xFFFFFFFF means "not recorded". ref: SFileReadFile.cpp:144-161
                uint recorded = checksums[i];
                if (recorded != 0 && recorded != 0xFFFFFFFF)
                {
                    uint actual = MpqChecksum.Adler32(MpqChecksum.SectorSeed, stored.AsSpan(start, rawLength));
                    if (actual != recorded)
                    {
                        throw new InvalidDataException(
                            $"{Name}: '{path}' sector {i} fails its checksum (stored 0x{recorded:X8}, computed 0x{actual:X8}).");
                    }
                }
            }

            if (rawLength == 0)
            {
                // ref: SFileReadFile.cpp:205-208 (an empty sector is all zeros)
                continue;
            }

            if (rawLength > expected)
            {
                throw new InvalidDataException($"{Name}: '{path}' sector {i} stores {rawLength} bytes for {expected}.");
            }

            DecompressUnit(block, stored.AsMemory(start, rawLength), result.AsSpan(outputStart, expected), path, i);
        }
    }

    /// <summary>
    /// Reads the per-sector checksum table of a compressed file that has the checksum flag, or returns null when the file
    /// has no usable table (StormLib then skips verification too).
    /// </summary>
    /// <param name="block">The file's block.</param>
    /// <param name="offsets">The decoded sector offset table including the extra checksum entry.</param>
    /// <param name="sectorCount">Number of data sectors.</param>
    /// <param name="path">For messages.</param>
    internal uint[]? LoadChecksums(MpqBlock block, ReadOnlySpan<uint> offsets, int sectorCount, string path)
    {
        // ref: StormLib src/SBaseCommon.cpp:1073-1079,1405-1450 (the table is run through the multi-method
        // SCompDecompress2 when shorter than sectorCount * 4 bytes, otherwise plain; it is never encrypted)
        if (!MpqSectorTable.TryLocateChecksums(offsets, sectorCount, SectorSize, out uint at, out uint storedSize))
        {
            return null;
        }

        int tableBytes = sectorCount * 4;
        byte[] raw = new byte[storedSize];
        ReadRaw(block.Offset + at, raw);
        byte[] table;
        if (storedSize < tableBytes)
        {
            table = new byte[tableBytes];
            try
            {
                MpqCompression.Decompress(raw, table, Header.FormatVersion);
            }
            catch (InvalidDataException e)
            {
                throw new InvalidDataException($"{Name}: '{path}' checksum table: {e.Message}", e);
            }
        }
        else
        {
            table = raw;
        }

        var checksums = new uint[sectorCount];
        for (int i = 0; i < sectorCount; i++)
        {
            checksums[i] = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 4));
        }

        return checksums;
    }

    private void DecompressUnit(MpqBlock block, ReadOnlyMemory<byte> stored, Span<byte> output, string path, int sector = 0)
    {
        try
        {
            if (stored.Length == output.Length)
            {
                // ref: SFileReadFile.cpp:165,210-214 (a sector as long as its output is stored as is)
                stored.Span.CopyTo(output);
            }
            else if ((block.Flags & MpqFileFlags.Compress) != 0)
            {
                // ref: SFileReadFile.cpp:174-183
                MpqCompression.Decompress(stored, output, Header.FormatVersion);
            }
            else if ((block.Flags & MpqFileFlags.Implode) != 0)
            {
                // ref: SFileReadFile.cpp:186-189 ; SCompression.cpp:706-738 (SCompExplode)
                int produced = PkwareExplode.Decompress(stored.Span, output);
                if (produced != output.Length)
                {
                    throw new InvalidDataException($"imploded data expanded to {produced} bytes, expected {output.Length}");
                }
            }
            else
            {
                throw new NotSupportedException($"compression flags 0x{block.Flags & MpqFileFlags.CompressMask:X4} name neither implode nor multi-method compression");
            }
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"{Name}: '{path}' sector {sector}: {e.Message}", e);
        }
        catch (NotSupportedException e)
        {
            throw new NotSupportedException($"{Name}: '{path}' sector {sector}: {e.Message}", e);
        }
    }

    private static MpqHeader ReadHeader(SafeFileHandle handle, long length, string path)
    {
        string name = System.IO.Path.GetFileName(path);
        if (length < HeaderSizeV1)
        {
            throw new InvalidDataException($"{name}: {length} bytes is too small for an MPQ header.");
        }

        MpqUserData? userData = null;
        Span<byte> buffer = stackalloc byte[HeaderSizeV2];
        long limit = Math.Min(length, HeaderSearchLimit);
        for (long position = 0; position + HeaderSizeV1 <= length && position < limit; position += 0x200)
        {
            int available = (int)Math.Min(HeaderSizeV2, length - position);
            buffer.Clear();
            ReadExactly(handle, position, buffer[..available], name);
            long headerPosition = position;
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(buffer);

            // ref: StormLib src/SFileOpenArchive.cpp:350-378,92-117 (user data: sizes ordered, header within the file)
            if (id == UserDataMagic && userData is null)
            {
                var candidate = new MpqUserData(
                    position,
                    BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..]));
                if (candidate.HeaderSize <= candidate.DataSize
                    && candidate.DataSize <= candidate.HeaderOffset
                    && position + candidate.HeaderOffset + HeaderSizeV1 < length)
                {
                    userData = candidate;
                    headerPosition = position + candidate.HeaderOffset;
                    available = (int)Math.Min(HeaderSizeV2, length - headerPosition);
                    buffer.Clear();
                    ReadExactly(handle, headerPosition, buffer[..available], name);
                    id = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
                }
            }

            uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
            if (id == HeaderMagic && headerSize >= HeaderSizeV1)
            {
                return ParseHeader(buffer, headerPosition, length, userData, name);
            }

            userData = null;
        }

        throw new InvalidDataException($"{name}: no MPQ header found in the first {limit} bytes.");
    }

    private static MpqHeader ParseHeader(ReadOnlySpan<byte> h, long position, long length, MpqUserData? userData, string name)
    {
        // Layout: ref: StormLib src/StormLib.h:538-576 ; vmangos@0e3ff01 dep/libmpq/libmpq/mpq-internal.h:67-84
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
        uint archiveSize = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]);
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(h[12..]);
        ushort shift = BinaryPrimitives.ReadUInt16LittleEndian(h[14..]);
        uint hashPos = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
        uint blockPos = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
        uint hashCount = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
        uint blockCount = BinaryPrimitives.ReadUInt32LittleEndian(h[28..]);

        long hashOffset = hashPos;
        long blockOffset = blockPos;
        long hiBlockOffset = 0;
        if (version == 0)
        {
            // Version 0 has no extension; StormLib masks the sector size to its low byte.
            // ref: StormLib src/SBaseFileTable.cpp:495-538
            shift &= 0xFF;
        }
        else if (version == 1)
        {
            if (headerSize < HeaderSizeV2)
            {
                throw new InvalidDataException($"{name}: format version 1 needs a {HeaderSizeV2}-byte header, found {headerSize}.");
            }

            // ref: StormLib.h:577-584 ; libmpq mpq-internal.h:80-84 ; libmpq mpq.c:207,226,245-248
            hiBlockOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(h[32..]);
            hashOffset |= (long)BinaryPrimitives.ReadUInt16LittleEndian(h[40..]) << 32;
            blockOffset |= (long)BinaryPrimitives.ReadUInt16LittleEndian(h[42..]) << 32;
        }
        else
        {
            throw new NotSupportedException(
                $"{name}: MPQ format version {version} (Cataclysm-era HET/BET tables) is not used by the 1.12.1 client and is not supported.");
        }

        if (shift > 15)
        {
            throw new InvalidDataException($"{name}: sector size exponent {shift} is out of range.");
        }

        if (hashCount == 0 || !BitOperations.IsPow2(hashCount) || hashCount > MaxHashEntries)
        {
            throw new InvalidDataException($"{name}: hash table size {hashCount} is not a power of two up to {MaxHashEntries}.");
        }

        if (blockCount > MaxBlockEntries)
        {
            throw new InvalidDataException($"{name}: block table size {blockCount} is out of range.");
        }

        if (position + hashOffset > length || position + blockOffset > length)
        {
            throw new InvalidDataException($"{name}: a table starts past the end of the file.");
        }

        return new MpqHeader(position, headerSize, archiveSize, version, shift, hashOffset, blockOffset, hashCount, blockCount, hiBlockOffset, userData);
    }

    private static MpqHashEntry[] ReadHashTable(SafeFileHandle handle, long length, MpqHeader header, long mask, string path)
    {
        string name = System.IO.Path.GetFileName(path);
        int count = (int)header.HashTableEntries;
        byte[] raw = ReadTable(handle, length, header, header.HashTableOffset, (long)count * 16, mask, name, "hash");

        // ref: StormLib src/SBaseFileTable.cpp:2414-2419 ; libmpq mpq.c:223
        MpqCrypto.Decrypt(raw, MpqCrypto.HashTableKey);

        // Entry: u32 nameA, u32 nameB, u16 locale, u8 platform, u8 reserved, u32 blockIndex
        // ref: StormLib.h:629-663 ; libmpq mpq-internal.h:87-93
        var hashes = new MpqHashEntry[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = raw.AsSpan(i * 16, 16);
            hashes[i] = new MpqHashEntry(
                BinaryPrimitives.ReadUInt32LittleEndian(e),
                BinaryPrimitives.ReadUInt32LittleEndian(e[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(e[8..]),
                e[10],
                BinaryPrimitives.ReadUInt32LittleEndian(e[12..]));
        }

        return hashes;
    }

    private static MpqBlock[] ReadBlockTable(SafeFileHandle handle, long length, MpqHeader header, long mask, string path)
    {
        string name = System.IO.Path.GetFileName(path);
        int count = (int)header.BlockTableEntries;
        if (count == 0)
        {
            return [];
        }

        byte[] raw = ReadTable(handle, length, header, header.BlockTableOffset, (long)count * 16, mask, name, "block");

        // ref: StormLib src/SBaseFileTable.cpp:2478-2483 ; libmpq mpq.c:242
        MpqCrypto.Decrypt(raw, MpqCrypto.BlockTableKey);

        // The optional hi-block table: one u16 per block holding bits 32-47 of the file offset, neither encrypted nor compressed.
        // ref: SBaseFileTable.cpp:2604-2632 ; libmpq mpq.c:245-248,433
        byte[]? hi = null;
        if (header.HiBlockTableOffset != 0)
        {
            hi = ReadTable(handle, length, header, header.HiBlockTableOffset, (long)count * 2, mask, name, "hi-block");
        }

        // Entry: u32 file position, u32 compressed size, u32 file size, u32 flags
        // ref: StormLib.h:666-680 ; libmpq mpq-internal.h:96-101
        var blocks = new MpqBlock[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = raw.AsSpan(i * 16, 16);
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(e);
            if (hi is not null)
            {
                offset |= (long)BinaryPrimitives.ReadUInt16LittleEndian(hi.AsSpan(i * 2)) << 32;
            }

            blocks[i] = new MpqBlock(
                offset,
                BinaryPrimitives.ReadUInt32LittleEndian(e[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[12..]));
        }

        return blocks;
    }

    private static byte[] ReadTable(SafeFileHandle handle, long length, MpqHeader header, long offset, long size, long mask, string name, string what)
    {
        long absolute = (header.Position + offset) & mask;
        if (absolute + size > length)
        {
            throw new InvalidDataException($"{name}: the {what} table ({size} bytes at 0x{absolute:X}) extends past the end of the file.");
        }

        byte[] raw = new byte[size];
        ReadExactly(handle, absolute, raw, name);
        return raw;
    }

    private static void ReadExactly(SafeFileHandle handle, long fileOffset, Span<byte> destination, string name)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = RandomAccess.Read(handle, destination[total..], fileOffset + total);
            if (read == 0)
            {
                throw new InvalidDataException($"{name}: unexpected end of file reading {destination.Length} bytes at 0x{fileOffset:X}.");
            }

            total += read;
        }
    }
}
