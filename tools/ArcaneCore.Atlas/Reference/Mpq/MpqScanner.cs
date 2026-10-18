using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Reference.Mpq;

/// <summary>
/// What an archive's block, hash and (optionally) sectors contain: counts per flag combination, per locale and per
/// compression method. Produced by <see cref="MpqScanner"/>; the counters are plain dictionaries keyed by a readable label.
/// </summary>
public sealed class MpqScanReport(string archive)
{
    /// <summary>Archive file name.</summary>
    public string Archive { get; } = archive;

    /// <summary>True when the sectors were read (the per-method, checksum and implode counters are meaningful); false for the table-only scan.</summary>
    public bool SectorsScanned { get; init; }

    /// <summary>Raw MPQ format version field.</summary>
    public int FormatVersion { get; init; }

    /// <summary>Header size field.</summary>
    public uint HeaderSize { get; init; }

    /// <summary>Sector size in bytes.</summary>
    public int SectorSize { get; init; }

    /// <summary>Hash table slots.</summary>
    public int HashEntries { get; init; }

    /// <summary>Hash slots that resolve to an existing block.</summary>
    public int ValidHashEntries { get; set; }

    /// <summary>Block table entries.</summary>
    public int BlockEntries { get; init; }

    /// <summary>Blocks flagged as existing.</summary>
    public int ExistingBlocks { get; set; }

    /// <summary>Existing blocks whose file size is zero.</summary>
    public int ZeroLengthFiles { get; set; }

    /// <summary>Existing blocks reachable by a name from the archive's own listfile.</summary>
    public int NamedBlocks { get; set; }

    /// <summary>Sum of the uncompressed sizes of existing blocks.</summary>
    public long FileBytes { get; set; }

    /// <summary>Sum of the stored sizes of existing blocks.</summary>
    public long StoredBytes { get; set; }

    /// <summary>Compressed sectors stored as is because compression did not shrink them.</summary>
    public long RawSectors { get; set; }

    /// <summary>Sectors with a zero-length stored form.</summary>
    public long EmptySectors { get; set; }

    /// <summary>Files whose sectors were inspected (sector scan only).</summary>
    public long InspectedFiles { get; set; }

    /// <summary>Files that could not be inspected, with the reason (encrypted without a known name, damaged table...).</summary>
    public long UninspectedFiles { get; set; }

    /// <summary>Files with the checksum flag whose sector offset table locates a usable checksum table.</summary>
    public long ChecksumTables { get; set; }

    /// <summary>Files with the checksum flag but no usable checksum table (StormLib then skips verification).</summary>
    public long ChecksumTablesAbsent { get; set; }

    /// <summary>Sectors whose recorded checksum matched the Adler-32 of the stored sector.</summary>
    public long ChecksumSectorsVerified { get; set; }

    /// <summary>Sectors whose recorded checksum is 0 or 0xFFFFFFFF ("not recorded").</summary>
    public long ChecksumSectorsNotRecorded { get; set; }

    /// <summary>Sectors whose recorded checksum differs from the computed one.</summary>
    public long ChecksumMismatches { get; set; }

    /// <summary>Sectors of encrypted files, which the scan does not decrypt and so cannot check.</summary>
    public long ChecksumSectorsUnchecked { get; set; }

    /// <summary>Checksum tables per storage form: <c>plain</c>, or the method mask of a compressed table.</summary>
    public SortedDictionary<string, long> ChecksumTableMethods { get; } = new(StringComparer.Ordinal);

    /// <summary>Existing blocks per block-flag value, labelled like <c>0x80000200 exists+compress</c>.</summary>
    public SortedDictionary<string, long> FlagCombinations { get; } = new(StringComparer.Ordinal);

    /// <summary>Resolving hash entries per locale/platform.</summary>
    public SortedDictionary<string, long> Locales { get; } = new(StringComparer.Ordinal);

    /// <summary>Compressed sectors (and compressed single units) per leading method mask.</summary>
    public SortedDictionary<string, long> SectorMethods { get; } = new(StringComparer.Ordinal);

    /// <summary>Files per set of method masks used by their sectors.</summary>
    public SortedDictionary<string, long> FileMethods { get; } = new(StringComparer.Ordinal);

    /// <summary>Imploded sectors per PKWARE header (literal mode and dictionary bits).</summary>
    public SortedDictionary<string, long> ImplodeHeaders { get; } = new(StringComparer.Ordinal);

    /// <summary>Anything the scan noticed that a reader must know about.</summary>
    public List<string> Notes { get; } = [];

    /// <summary>The report as JSON (counters as objects of label to count).</summary>
    public JsonObject ToJson()
    {
        static JsonObject Counters(SortedDictionary<string, long> source)
        {
            var o = new JsonObject();
            foreach ((string key, long count) in source)
            {
                o[key] = count;
            }

            return o;
        }

        return new JsonObject
        {
            ["archive"] = Archive,
            ["sectorsScanned"] = SectorsScanned,
            ["formatVersion"] = FormatVersion,
            ["headerSize"] = HeaderSize,
            ["sectorSize"] = SectorSize,
            ["hashEntries"] = HashEntries,
            ["validHashEntries"] = ValidHashEntries,
            ["blockEntries"] = BlockEntries,
            ["existingBlocks"] = ExistingBlocks,
            ["zeroLengthFiles"] = ZeroLengthFiles,
            ["namedBlocks"] = NamedBlocks,
            ["fileBytes"] = FileBytes,
            ["storedBytes"] = StoredBytes,
            ["rawSectors"] = RawSectors,
            ["emptySectors"] = EmptySectors,
            ["inspectedFiles"] = InspectedFiles,
            ["uninspectedFiles"] = UninspectedFiles,
            ["checksumTables"] = ChecksumTables,
            ["checksumTablesAbsent"] = ChecksumTablesAbsent,
            ["checksumSectorsVerified"] = ChecksumSectorsVerified,
            ["checksumSectorsNotRecorded"] = ChecksumSectorsNotRecorded,
            ["checksumMismatches"] = ChecksumMismatches,
            ["checksumSectorsUnchecked"] = ChecksumSectorsUnchecked,
            ["checksumTableMethods"] = Counters(ChecksumTableMethods),
            ["flagCombinations"] = Counters(FlagCombinations),
            ["locales"] = Counters(Locales),
            ["sectorMethods"] = Counters(SectorMethods),
            ["fileMethods"] = Counters(FileMethods),
            ["implodeHeaders"] = Counters(ImplodeHeaders),
            ["notes"] = new JsonArray([.. Notes.Select(n => (JsonNode?)n)]),
        };
    }

    internal static void Bump(SortedDictionary<string, long> counters, string key, long by = 1)
    {
        counters[key] = counters.GetValueOrDefault(key) + by;
    }
}

/// <summary>
/// Statistics over every block of an archive. The cheap form reads only the block and hash tables; the sector form also
/// reads each compressed file's sector table and the first byte of each compressed sector (its method mask), so it
/// touches every stored byte once. Thread-safe per archive (read-only).
/// </summary>
public static class MpqScanner
{
    /// <summary>Scans <paramref name="archive"/>.</summary>
    /// <param name="archive">The open archive.</param>
    /// <param name="sectors">True to inspect sector method masks (reads the whole archive).</param>
    /// <param name="cancellationToken">Observed between files.</param>
    public static MpqScanReport Scan(MpqArchive archive, bool sectors, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var report = new MpqScanReport(archive.Name)
        {
            SectorsScanned = sectors,
            FormatVersion = archive.Header.FormatVersion,
            HeaderSize = archive.Header.HeaderSize,
            SectorSize = archive.SectorSize,
            HashEntries = archive.Hashes.Length,
            BlockEntries = archive.Blocks.Length,
        };

        // Which blocks a listfile name reaches (and under what name, which an encrypted file's key needs).
        var names = new Dictionary<uint, string>();
        foreach (string name in archive.ListFile)
        {
            if (archive.TryFind(name, out MpqEntry entry))
            {
                names.TryAdd(entry.BlockIndex, name);
            }
        }

        report.NamedBlocks = names.Count;

        foreach (MpqHashEntry hash in archive.Hashes)
        {
            uint index = hash.BlockIndex & 0x0FFFFFFF;
            if (hash.BlockIndex < MpqArchive.HashEntryDeleted && index < archive.Blocks.Length && archive.Blocks[(int)index].Exists)
            {
                report.ValidHashEntries++;
                MpqScanReport.Bump(report.Locales, $"locale 0x{hash.Locale:X4} platform {hash.Platform}");
            }
        }

        long tableGap = archive.Header.BlockTableOffset - archive.Header.HashTableOffset;
        if (tableGap != (long)archive.Hashes.Length * 16)
        {
            report.Notes.Add($"hash table occupies {(long)archive.Hashes.Length * 16} bytes but the block table starts {tableGap} bytes after it");
        }

        for (int i = 0; i < archive.Blocks.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MpqBlock block = archive.Blocks[i];
            if (!block.Exists)
            {
                if (block.Flags != 0 || block.FileSize != 0 || block.CompressedSize != 0)
                {
                    MpqScanReport.Bump(report.FlagCombinations, $"{DescribeFlags(block.Flags)} (not existing)");
                }

                continue;
            }

            report.ExistingBlocks++;
            report.FileBytes += block.FileSize;
            report.StoredBytes += block.CompressedSize;
            if (block.FileSize == 0)
            {
                report.ZeroLengthFiles++;
            }

            MpqScanReport.Bump(report.FlagCombinations, DescribeFlags(block.Flags));
            if (sectors && block.FileSize > 0 && block.IsCompressed)
            {
                names.TryGetValue((uint)i, out string? name);
                ScanSectors(archive, block, name, report);
            }
        }

        return report;
    }

    /// <summary>A label for a flag word: hex value and the names of its bits.</summary>
    public static string DescribeFlags(uint flags)
    {
        var parts = new List<string>(6);
        void Add(uint bit, string label)
        {
            if ((flags & bit) != 0)
            {
                parts.Add(label);
            }
        }

        Add(MpqFileFlags.Exists, "exists");
        Add(MpqFileFlags.Implode, "implode");
        Add(MpqFileFlags.Compress, "compress");
        Add(MpqFileFlags.Encrypted, "encrypted");
        Add(MpqFileFlags.FixKey, "fixkey");
        Add(MpqFileFlags.PatchFile, "patch");
        Add(MpqFileFlags.SingleUnit, "single");
        Add(MpqFileFlags.DeleteMarker, "delete");
        Add(MpqFileFlags.SectorCrc, "crc");
        uint known = MpqFileFlags.Exists | MpqFileFlags.Implode | MpqFileFlags.Compress | MpqFileFlags.Encrypted | MpqFileFlags.FixKey
            | MpqFileFlags.PatchFile | MpqFileFlags.SingleUnit | MpqFileFlags.DeleteMarker | MpqFileFlags.SectorCrc;
        if ((flags & ~known) != 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"other=0x{flags & ~known:X8}"));
        }

        return string.Create(CultureInfo.InvariantCulture, $"0x{flags:X8} {(parts.Count == 0 ? "none" : string.Join('+', parts))}");
    }

    private static void ScanSectors(MpqArchive archive, MpqBlock block, string? name, MpqScanReport report)
    {
        if (block.IsEncrypted && name is null)
        {
            report.UninspectedFiles++;
            MpqScanReport.Bump(report.FlagCombinations, "uninspected: encrypted file without a listfile name");
            return;
        }

        if (block.CompressedSize > MpqArchive.MaxFileBytes)
        {
            report.UninspectedFiles++;
            report.Notes.Add($"block with {block.CompressedSize} stored bytes skipped (over the reader's limit)");
            return;
        }

        int stored = (int)block.CompressedSize;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(stored);
        try
        {
            Span<byte> data = buffer.AsSpan(0, stored);
            archive.ReadRaw(block.Offset, data);
            uint key = block.IsEncrypted
                ? MpqCrypto.FileKey(name!, block.Offset, block.FileSize, (block.Flags & MpqFileFlags.FixKey) != 0)
                : 0;

            var methods = new SortedSet<string>(StringComparer.Ordinal);
            if (block.IsSingleUnit)
            {
                Span<byte> head = stackalloc byte[HeadBytes];
                ReadHead(data, 0, data.Length, block.IsEncrypted, key, head);
                Classify(block, head, block.CompressedSize, block.FileSize, report, methods);
            }
            else
            {
                int sectorSize = archive.SectorSize;
                int sectorCount = (int)((block.FileSize + (uint)sectorSize - 1) / (uint)sectorSize);
                bool hasChecksumFlag = (block.Flags & MpqFileFlags.SectorCrc) != 0;
                int tableBytes = ((sectorCount + 1) * 4) + (hasChecksumFlag ? 4 : 0);
                if (data.Length < tableBytes)
                {
                    report.UninspectedFiles++;
                    report.Notes.Add($"compressed block of {data.Length} bytes is shorter than its {tableBytes}-byte sector table");
                    return;
                }

                byte[] raw = data[..tableBytes].ToArray();
                if (block.IsEncrypted)
                {
                    MpqCrypto.Decrypt(raw, key - 1);
                }

                var offsets = new uint[tableBytes / 4];
                for (int s = 0; s < offsets.Length; s++)
                {
                    offsets[s] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(s * 4));
                }

                for (int s = 0; s < sectorCount; s++)
                {
                    if (offsets[s + 1] < offsets[s] || offsets[s + 1] > (uint)data.Length)
                    {
                        report.UninspectedFiles++;
                        report.Notes.Add($"sector table of a {name ?? "(unnamed)"} block is not monotonic at sector {s}");
                        return;
                    }
                }

                Span<byte> head = stackalloc byte[HeadBytes];
                for (int s = 0; s < sectorCount; s++)
                {
                    int start = (int)offsets[s];
                    int length = (int)offsets[s + 1] - start;
                    uint expected = (uint)Math.Min(sectorSize, (long)block.FileSize - ((long)s * sectorSize));
                    ReadHead(data, start, length, block.IsEncrypted, key + (uint)s, head);
                    Classify(block, head, (uint)length, expected, report, methods);
                }

                if (hasChecksumFlag)
                {
                    CheckChecksums(archive, block, name, offsets, sectorCount, data, report);
                }
            }

            report.InspectedFiles++;
            MpqScanReport.Bump(report.FileMethods, methods.Count == 0 ? "(all sectors stored raw)" : string.Join(' ', methods));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void CheckChecksums(MpqArchive archive, MpqBlock block, string? name, uint[] offsets, int sectorCount, ReadOnlySpan<byte> data, MpqScanReport report)
    {
        uint[]? checksums;
        try
        {
            checksums = archive.LoadChecksums(block, offsets, sectorCount, name ?? "(unnamed)");
        }
        catch (InvalidDataException e)
        {
            report.ChecksumMismatches++;
            report.Notes.Add($"checksum table of {name ?? "(unnamed)"} is unreadable: {e.Message}");
            return;
        }

        if (checksums is null)
        {
            report.ChecksumTablesAbsent++;
            return;
        }

        report.ChecksumTables++;
        MpqSectorTable.TryLocateChecksums(offsets, sectorCount, archive.SectorSize, out uint tableAt, out uint tableStored);
        if (tableStored < sectorCount * 4)
        {
            MpqScanReport.Bump(
                report.ChecksumTableMethods,
                tableAt < data.Length ? MpqCompressionMask.Describe(data[(int)tableAt]) : "outside the block");
        }
        else
        {
            MpqScanReport.Bump(report.ChecksumTableMethods, "plain");
        }

        for (int s = 0; s < sectorCount; s++)
        {
            uint recorded = checksums[s];
            if (recorded == 0 || recorded == 0xFFFFFFFF)
            {
                report.ChecksumSectorsNotRecorded++;
            }
            else if (block.IsEncrypted)
            {
                report.ChecksumSectorsUnchecked++;
            }
            else
            {
                int start = (int)offsets[s];
                uint actual = MpqChecksum.Adler32(MpqChecksum.SectorSeed, data.Slice(start, (int)offsets[s + 1] - start));
                if (actual == recorded)
                {
                    report.ChecksumSectorsVerified++;
                }
                else
                {
                    report.ChecksumMismatches++;
                    if (report.Notes.Count < 20)
                    {
                        report.Notes.Add($"sector {s} of {name ?? "(unnamed)"}: recorded 0x{recorded:X8}, computed 0x{actual:X8}");
                    }
                }
            }
        }
    }

    // The first bytes of a stored sector: a method mask byte, or a PKWARE header (mode, dictionary bits), or the mask plus
    // the PKWARE header behind it. Decryption is a stream over dwords, so decrypting the first two dwords alone yields the
    // same bytes as decrypting the whole sector.
    private const int HeadBytes = 8;

    private static void ReadHead(ReadOnlySpan<byte> data, int start, int length, bool encrypted, uint key, Span<byte> head)
    {
        head.Clear();
        int take = Math.Min(HeadBytes, length);
        data.Slice(start, take).CopyTo(head);
        if (encrypted)
        {
            MpqCrypto.Decrypt(head[..(take & ~3)], key);
        }
    }

    // One sector (or unit): `stored` bytes on disk that expand to `expected` bytes.
    private static void Classify(MpqBlock block, ReadOnlySpan<byte> head, uint stored, uint expected, MpqScanReport report, SortedSet<string> methods)
    {
        if (stored == 0)
        {
            report.EmptySectors++;
            return;
        }

        if (stored == expected)
        {
            report.RawSectors++;
            return;
        }

        if ((block.Flags & MpqFileFlags.Compress) != 0)
        {
            string label = MpqCompressionMask.Describe(head[0]);
            MpqScanReport.Bump(report.SectorMethods, label);
            methods.Add(label);

            // PKWARE is the first method undone unless bzip2 precedes it, so its header sits right behind the mask byte.
            if ((head[0] & MpqCompressionMask.Pkware) != 0 && (head[0] & MpqCompressionMask.Bzip2) == 0 && stored >= 3)
            {
                MpqScanReport.Bump(report.ImplodeHeaders, $"mask {label} mode {head[1]} dictionary bits {head[2]}");
            }
        }
        else
        {
            // Whole-file implode: the sector itself begins with the PKWARE header.
            MpqScanReport.Bump(report.ImplodeHeaders, $"flag 0x100 mode {head[0]} dictionary bits {head[1]}");
            MpqScanReport.Bump(report.SectorMethods, "implode (flag 0x100)");
            methods.Add("implode");
        }
    }
}
