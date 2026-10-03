using System.Text.Json;
using ArcaneCore.Data.Content.Import;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Core;

/// <summary>The JSON run report: provenance, dialects, counts and the license notice; no host paths.</summary>
public sealed class ContentImportReportTests
{
    private const string Dump =
        "CREATE TABLE `db_version` (`version` varchar(120));\n"
        + "INSERT INTO `db_version` VALUES ('Synthetic DB 1');\n"
        + "CREATE TABLE `creature_template` (`Entry` int, `Name` text, `MinLevel` int, `MaxLevel` int, `ModelId1` int, `TrainerType` int);\n"
        + "INSERT INTO `creature_template` VALUES (1,'A',1,1,5,0),(2,'B',1,1,6,0);\n"
        + "UPDATE `creature_template` SET `Name`='x';\n";

    [Fact]
    public void Json_CarriesProvenanceDialectsCountsAndTheLicenseNotice()
    {
        ScanResult scan = ContentScanner.Scan([DumpInput.Text("synthetic.sql", Dump)]);
        var files = new[] { new SourceFileInfo("synthetic.sql", 123, new string('a', 64), Gzip: false) };

        ContentImportReport report = ContentImportReport.Create("plan", files, scan, [], dryRun: true);
        using JsonDocument json = JsonDocument.Parse(report.ToJson());
        JsonElement root = json.RootElement;

        Assert.Equal("plan", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("dryRun").GetBoolean());
        Assert.Equal(ContentImportReport.SpecVersion, root.GetProperty("specVersion").GetInt32());
        Assert.Equal("Synthetic DB 1", root.GetProperty("dbVersion").GetString());
        JsonElement file = root.GetProperty("inputs")[0];
        Assert.Equal("synthetic.sql", file.GetProperty("name").GetString());
        Assert.Equal(123, file.GetProperty("bytes").GetInt64());
        Assert.Equal(new string('a', 64), file.GetProperty("sha256").GetString());

        JsonElement table = root.GetProperty("tables").GetProperty("creature_template");
        Assert.Equal(2, table.GetProperty("rows").GetInt64());
        Assert.Equal("CMangosClassic", table.GetProperty("dialect").GetString());
        Assert.Contains("TrainerType", table.GetProperty("unmappedColumns").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1, root.GetProperty("unappliedStatements").GetProperty("UPDATE creature_template").GetInt32());

        string notice = root.GetProperty("licenseNotice").GetString()!;
        Assert.Contains("GPL", notice, StringComparison.Ordinal);
        Assert.Contains("Blizzard", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_NeverContainsAHostPath()
    {
        ScanResult scan = ContentScanner.Scan([DumpInput.Text("synthetic.sql", Dump)]);

        string json = ContentImportReport.Create("import", [], scan, ["a warning"], dryRun: false).ToJson();

        Assert.DoesNotContain(Path.GetTempPath(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a warning", json, StringComparison.Ordinal);
    }
}
