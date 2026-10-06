using System.Text;
using ArcaneCore.Data.Content.Import;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Core;

/// <summary>
/// The pre-write scan: required key columns, the distinct-key collapse guard and per-table
/// dialect detection. Dumps are synthetic, written in each source's column layout.
/// </summary>
public sealed class DumpScannerTests
{
    // cmangos classic-db z2815 creature_template column names (ModelId1, MinLevel), trimmed.
    private const string CMangosZ2815Template =
        "CREATE TABLE `creature_template` (`Entry` int unsigned NOT NULL, `Name` varchar(100), `MinLevel` tinyint, `MaxLevel` tinyint, `ModelId1` int, PRIMARY KEY (`Entry`));\n";

    private const string CMangosHeadTemplate =
        "CREATE TABLE `creature_template` (`Entry` int unsigned NOT NULL, `Name` varchar(100), `MinLevel` tinyint, `MaxLevel` tinyint, `DisplayId1` int, `DisplayIdProbability1` int, PRIMARY KEY (`Entry`));\n";

    private const string VMangosTemplate =
        "CREATE TABLE `creature_template` (`entry` int unsigned NOT NULL, `patch` tinyint, `name` varchar(100), `level_min` tinyint, `level_max` tinyint, `display_id1` int, `static_flags1` int, PRIMARY KEY (`entry`, `patch`));\n";

    [Fact]
    public void RenamedKeyColumn_AbortsWithTheTableAndColumn_InsteadOfWritingOneEntryZeroRow()
    {
        // `Entry` renamed to `id`: the importers' lookups for "Entry" fall back to 0 for every row.
        string dump = "CREATE TABLE `creature_template` (`id` int unsigned NOT NULL, `Name` varchar(100), `MinLevel` tinyint, `MaxLevel` tinyint, `ModelId1` int);\n"
            + "INSERT INTO `creature_template` VALUES (1,'A',1,1,10),(2,'B',1,1,11),(3,'C',1,1,12);\n";

        ImportSchemaException ex = Assert.Throws<ImportSchemaException>(() => Scan(dump));

        Assert.Equal("creature_template", ex.Table);
        Assert.Equal("Entry", ex.Column);
        Assert.Contains("id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyColumnAliases_AreAccepted_CaseInsensitively()
    {
        ScanResult result = Scan(VMangosTemplate + "INSERT INTO `creature_template` VALUES (1,0,'A',1,1,5,0),(2,0,'B',1,1,6,0);\n");

        Assert.Equal(2, result.Tables["creature_template"].Rows);
    }

    [Fact]
    public void NullOrEmptyKeyValue_Aborts()
    {
        string dump = CMangosZ2815Template + "INSERT INTO `creature_template` VALUES (1,'A',1,1,10),(NULL,'B',1,1,11);\n";

        ImportSchemaException ex = Assert.Throws<ImportSchemaException>(() => Scan(dump));

        Assert.Equal("Entry", ex.Column);
        Assert.Contains("NULL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThousandRowsCollapsingToOneKey_Aborts()
    {
        var dump = new StringBuilder(CMangosZ2815Template).Append("INSERT INTO `creature_template` VALUES ");
        for (int i = 0; i < 1000; i++)
        {
            dump.Append(i == 0 ? "" : ",").Append("(0,'Same',1,1,10)");
        }

        ImportSchemaException ex = Assert.Throws<ImportSchemaException>(() => Scan(dump.Append(";\n").ToString()));

        Assert.Equal("creature_template", ex.Table);
        Assert.Contains("1000", ex.Message, StringComparison.Ordinal);
        Assert.Contains("collapse", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARepeatedKeyAmongManyDistinctKeys_IsCountedNotFatal()
    {
        string dump = CMangosZ2815Template
            + "INSERT INTO `creature_template` VALUES (1,'A',1,1,10),(2,'B',1,1,11),(2,'B again',1,1,11),(3,'C',1,1,12);\n";

        TableScan table = Scan(dump).Tables["creature_template"];

        Assert.Equal((4L, 3L, 1L), (table.Rows, table.DistinctKeys, table.DuplicateKeys));
    }

    [Fact]
    public void CompositeKeys_AreDistinctOnTheWholeKey()
    {
        string dump = "CREATE TABLE `creature_movement` (`Id` int, `Point` int, `PositionX` float);\n"
            + "INSERT INTO `creature_movement` VALUES (7,1,0.5),(7,2,0.6),(7,3,0.7);\n";

        TableScan table = Scan(dump).Tables["creature_movement"];

        Assert.Equal((3L, 3L, 0L), (table.Rows, table.DistinctKeys, table.DuplicateKeys));
    }

    [Fact]
    public void Dialect_IsDetectedPerTable_FromSignatureColumns()
    {
        Assert.Equal(ContentDialect.CMangosClassic, Dialect(CMangosZ2815Template + "INSERT INTO `creature_template` VALUES (1,'A',1,1,10);"));
        Assert.Equal(ContentDialect.CMangosHead, Dialect(CMangosHeadTemplate + "INSERT INTO `creature_template` VALUES (1,'A',1,1,10,100);"));
        Assert.Equal(ContentDialect.VMangos, Dialect(VMangosTemplate + "INSERT INTO `creature_template` VALUES (1,0,'A',1,1,10,0);"));
    }

    [Fact]
    public void Dialect_IsIndependentPerTable()
    {
        string dump = CMangosZ2815Template + "INSERT INTO `creature_template` VALUES (1,'A',1,1,10);\n"
            + "CREATE TABLE `creature` (`guid` int, `id` int, `map` int, `patch_min` int, `patch_max` int, `wander_distance` float);\n"
            + "INSERT INTO `creature` VALUES (1,1,0,0,10,5);\n";

        ScanResult result = Scan(dump);

        Assert.Equal(ContentDialect.CMangosClassic, result.Tables["creature_template"].Dialect);
        Assert.Equal(ContentDialect.VMangos, result.Tables["creature"].Dialect);
    }

    [Fact]
    public void AmbiguousSignature_IsRejected_WithTheColumnsFound()
    {
        // Both a cmangos (ModelId1, MinLevel) and a vmangos (level_min, display_id1) signature match.
        string dump = "CREATE TABLE `creature_template` (`Entry` int, `ModelId1` int, `MinLevel` int, `level_min` int, `display_id1` int);\n"
            + "INSERT INTO `creature_template` VALUES (1,2,3,4,5);\n";

        ImportSchemaException ex = Assert.Throws<ImportSchemaException>(() => Scan(dump));

        Assert.Equal("creature_template", ex.Table);
        Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("level_min", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSignatureMatch_IsRejected_WithTheColumnsFound()
    {
        string dump = "CREATE TABLE `creature_template` (`Entry` int, `Whatever` int);\nINSERT INTO `creature_template` VALUES (1,2);\n";

        ImportSchemaException ex = Assert.Throws<ImportSchemaException>(() => Scan(dump));

        Assert.Contains("Whatever", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no known dialect", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TableWithoutRows_NeedsNoDialect()
    {
        ScanResult result = Scan("CREATE TABLE `creature_template` (`Entry` int, `Whatever` int);\n");

        Assert.Equal(0, result.Tables["creature_template"].Rows);
        Assert.Equal(ContentDialect.Unknown, result.Tables["creature_template"].Dialect);
    }

    [Fact]
    public void TablesWithoutASpec_AreCountedAndReportedAsNotImported()
    {
        ScanResult result = Scan("CREATE TABLE `some_unknown` (`a` int, `b` int);\nINSERT INTO `some_unknown` VALUES (1,1),(1,1),(1,1);\n");

        TableScan table = result.Tables["some_unknown"];
        Assert.False(table.HasSpec);
        Assert.Equal(3, table.Rows);
        Assert.Empty(table.MappedColumns);
        Assert.Equal(["a", "b"], table.UnmappedColumns);
    }

    [Fact]
    public void MappedAndUnmappedColumns_AreSplitAgainstTheSpec()
    {
        TableScan table = Scan(CMangosZ2815Template.Replace("`ModelId1` int", "`ModelId1` int, `TrainerType` int", StringComparison.Ordinal)
            + "INSERT INTO `creature_template` VALUES (1,'A',1,1,10,0);\n").Tables["creature_template"];

        Assert.Contains("Entry", table.MappedColumns);
        Assert.Contains("ModelId1", table.MappedColumns);
        Assert.Contains("TrainerType", table.MappedColumns);
        Assert.Empty(table.UnmappedColumns);
    }

    [Fact]
    public void ScanAcrossFiles_UsesOneRegistry_AndKeepsFileOrder()
    {
        ScanResult result = ContentScanner.Scan(
        [
            DumpInput.Text("schema.sql", CMangosZ2815Template),
            DumpInput.Text("rows.sql", "INSERT INTO `creature_template` VALUES (1,'A',1,1,10),(2,'B',1,1,11);"),
        ]);

        Assert.Equal(2, result.Tables["creature_template"].Rows);
        Assert.Equal(["schema.sql", "rows.sql"], result.Inputs);
    }

    [Fact]
    public void DbVersion_IsCapturedFromTheDbVersionTable()
    {
        ScanResult result = Scan("CREATE TABLE `db_version` (`version` varchar(120), `creature_ai_version` varchar(120));\n"
            + "INSERT INTO `db_version` VALUES ('Classic DB version test','ACID test');\n");

        Assert.Equal("Classic DB version test", result.DbVersion);
    }

    [Fact]
    public void UnappliedStatements_AreReportedByTheScan()
    {
        ScanResult result = Scan("CREATE TABLE `t` (`a` int);\nUPDATE `t` SET `a`=1;\n");

        Assert.Equal(1, result.UnappliedStatements["UPDATE t"]);
    }

    private static ScanResult Scan(string dump) => ContentScanner.Scan([DumpInput.Text("test.sql", dump)]);

    private static ContentDialect Dialect(string dump) => Scan(dump).Tables["creature_template"].Dialect;
}
