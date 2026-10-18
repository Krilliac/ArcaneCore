using System.Text.Json;
using ArcaneCore.Atlas.Reference.Mpq;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

/// <summary>
/// Reads every block of every archive of the real 1.12.1 client (sector tables, every sector's method byte, every checksum
/// table and every recorded checksum) and pins what was found. These counts are what justify which compression methods the
/// reader implements and which it refuses: a method the scan never meets is refused by name rather than implemented blind.
/// </summary>
public sealed class MpqRealScanTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [ClientArchiveFact]
    public void ScanEveryArchive_ReportsMasksAndFlags()
    {
        string data = Path.Combine(ClientArchiveData.ClientRoot!, "Data");
        var flags = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var sectorMethods = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var fileMethods = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var tableMethods = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var locales = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var formats = new SortedSet<string>(StringComparer.Ordinal);
        var notes = new List<string>();
        long blocks = 0, uninspected = 0, mismatches = 0, verified = 0, implodeHeaders = 0;
        int archives = 0;
        foreach (string file in Directory.EnumerateFiles(data, "*.MPQ").Order(StringComparer.OrdinalIgnoreCase))
        {
            using MpqArchive archive = MpqArchive.Open(file);
            MpqScanReport report = MpqScanner.Scan(archive, sectors: true);
            output.WriteLine(report.ToJson().ToJsonString(Indented));
            archives++;
            formats.Add($"v{report.FormatVersion}/{report.HeaderSize}");
            blocks += report.ExistingBlocks;
            uninspected += report.UninspectedFiles;
            mismatches += report.ChecksumMismatches;
            verified += report.ChecksumSectorsVerified;
            implodeHeaders += report.ImplodeHeaders.Count;
            notes.AddRange(report.Notes.Select(n => $"{report.Archive}: {n}"));
            Add(flags, report.FlagCombinations);
            Add(sectorMethods, report.SectorMethods);
            Add(fileMethods, report.FileMethods);
            Add(tableMethods, report.ChecksumTableMethods);
            Add(locales, report.Locales);
        }

        Print("flag combinations", flags);
        Print("sector methods", sectorMethods);
        Print("file methods", fileMethods);
        Print("checksum table methods", tableMethods);
        output.WriteLine($"archives {archives}, blocks {blocks}, checksum sectors verified {verified}, mismatches {mismatches}");

        // 14 archives: base, backup, dbc, fonts, interface, misc, model, patch, patch-2, sound, speech, terrain, texture, wmo.
        Assert.Equal(14, archives);
        Assert.Equal(97373, blocks);

        // Format version 0 with the 32-byte header, and version 1 (StormLib's MPQ_FORMAT_VERSION_2, which mpqcli prints as
        // "Format version: 2") with the 44-byte header (StormLib.h:303-304,511-512).
        Assert.Equal(new[] { "v0/32", "v1/44" }, formats);

        // Every block: exists, optionally compressed (0x200), sector CRC (0x04000000) or deletion marker (0x02000000).
        // Never encrypted, never imploded, never single-unit.
        Assert.Equal(new[] { "0x80000000", "0x80000200", "0x82000000", "0x84000200" }, flags.Keys.Select(k => k[..10]));
        Assert.Equal(5708, flags.Single(f => f.Key.StartsWith("0x82000000", StringComparison.Ordinal)).Value);

        // Every compressed sector of every file uses zlib alone; the checksum tables use sparse or are stored.
        Assert.Equal(new[] { "0x02(zlib)" }, sectorMethods.Keys);
        Assert.Equal(2396222, sectorMethods["0x02(zlib)"]);
        Assert.Subset(new HashSet<string>(["(all sectors stored raw)", "0x02(zlib)"]), new HashSet<string>(fileMethods.Keys));
        Assert.Equal(new[] { "0x20(sparse)", "plain" }, tableMethods.Keys);
        Assert.Equal(new[] { "locale 0x0000 platform 0" }, locales.Keys);
        Assert.Equal(0, implodeHeaders);

        // Every file was inspected; every recorded sector checksum matches.
        Assert.Equal(0, uninspected);
        Assert.True(verified > 0, "no sector checksum was verified");
        Assert.Equal(0, mismatches);
        Assert.Empty(notes);
    }

    private static void Add(SortedDictionary<string, long> into, SortedDictionary<string, long> from)
    {
        foreach ((string key, long count) in from)
        {
            into[key] = into.GetValueOrDefault(key) + count;
        }
    }

    private void Print(string title, SortedDictionary<string, long> counts)
    {
        output.WriteLine($"TOTAL {title}");
        foreach ((string key, long count) in counts)
        {
            output.WriteLine($"  {count,10}  {key}");
        }
    }
}
