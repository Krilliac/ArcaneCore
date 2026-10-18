using System.Text.Json;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Mpq;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Atlas.Tests.Reference.Mpq;

public sealed class MpqRealScanTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [ClientArchiveFact]
    public void ScanEveryArchive_ReportsMasksAndFlags()
    {
        string data = Path.Combine(ClientArchiveData.ClientRoot!, "Data");
        var totals = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var methodTotals = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(data, "*.MPQ").Order(StringComparer.OrdinalIgnoreCase))
        {
            using MpqArchive archive = MpqArchive.Open(file);
            MpqScanReport report = MpqScanner.Scan(archive, sectors: true);
            output.WriteLine(report.ToJson().ToJsonString(Indented));
            foreach ((string key, long count) in report.FlagCombinations)
            {
                totals[key] = totals.GetValueOrDefault(key) + count;
            }

            foreach ((string key, long count) in report.SectorMethods)
            {
                methodTotals[key] = methodTotals.GetValueOrDefault(key) + count;
            }
        }

        output.WriteLine("TOTAL flag combinations");
        foreach ((string key, long count) in totals)
        {
            output.WriteLine($"  {count,10}  {key}");
        }

        output.WriteLine("TOTAL sector methods");
        foreach ((string key, long count) in methodTotals)
        {
            output.WriteLine($"  {count,10}  {key}");
        }
    }
}
