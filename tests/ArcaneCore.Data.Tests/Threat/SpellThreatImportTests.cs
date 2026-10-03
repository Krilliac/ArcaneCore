using ArcaneCore.Data.World.Threat;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Threat;
using Xunit;

namespace ArcaneCore.Data.Tests.Threat;

/// <summary>
/// The <c>spell_threat</c> importer and the rank fill (vmangos SpellMgr::LoadSpellThreats, Spells/SpellMgr.cpp:834-875, SpellRankHelper
/// :127-182, DoSpellThreat :770-827). The first rows of the cmangos fixture are the real classic-db z2815 rows (Full_DB, first and last
/// tuples of the table); the vmangos fixture is built from the columns of the loader's SELECT, since no vmangos dump is available here.
/// </summary>
public sealed class SpellThreatImportTests
{
    private const string ClassicDump = """
        DROP TABLE IF EXISTS `spell_threat`;
        CREATE TABLE `spell_threat` (
          `entry` mediumint unsigned NOT NULL,
          `Threat` smallint NOT NULL,
          `multiplier` float NOT NULL DEFAULT '1' COMMENT 'threat multiplier for damage/healing',
          `ap_bonus` float NOT NULL DEFAULT '0' COMMENT 'additional threat bonus from attack power',
          PRIMARY KEY (`entry`)
        ) ENGINE=MyISAM DEFAULT CHARSET=utf8mb3 ROW_FORMAT=FIXED;
        INSERT INTO `spell_threat` VALUES (72,180,1,0),(78,20,1,0),(8092,0,2,0),(21992,126,1.5,0);
        """;

    private const string VMangosDump = """
        CREATE TABLE `spell_threat` (
          `entry` int unsigned NOT NULL,
          `threat` smallint unsigned NOT NULL DEFAULT '0',
          `multiplier` float NOT NULL DEFAULT '1',
          `inverse_effect_mask` tinyint unsigned NOT NULL DEFAULT '0',
          `build_min` int unsigned NOT NULL DEFAULT '0',
          `build_max` int unsigned NOT NULL DEFAULT '9999',
          PRIMARY KEY (`entry`, `build_min`, `build_max`)
        ) ENGINE=InnoDB;
        INSERT INTO `spell_threat` VALUES (100,10,1,0,0,9999),(200,50,1,1,5464,5875),(200,60,1,0,6005,9999),(300,0,0,0,5875,5875);
        """;

    [Fact]
    public void TheClassicDbRows_ParseWithTheirMultipliers()
    {
        SpellThreatParseResult result = SpellThreatDumpImporter.Parse(new StringReader(ClassicDump));

        Assert.Equal((4, 0), (result.RowsRead, result.RowsFilteredByBuild));
        Assert.Equal(new SpellThreatRecord(72, 180, 1f, 0), result.Content.Find(72));
        Assert.Equal(new SpellThreatRecord(8092, 0, 2f, 0), result.Content.Find(8092));
        Assert.Equal(1.5f, result.Content.Find(21992)!.Multiplier);
        Assert.Equal(4, result.Content.Count);
    }

    [Fact]
    public void TheVMangosDialect_KeepsOnlyTheRowsWhoseBuildRangeHoldsBuild5875()
    {
        SpellThreatParseResult result = SpellThreatDumpImporter.Parse(new StringReader(VMangosDump));

        Assert.Equal((4, 1), (result.RowsRead, result.RowsFilteredByBuild));
        Assert.Equal(new SpellThreatRecord(200, 50, 1f, 1), result.Content.Find(200)); // 5464..5875 holds 5875, 6005..9999 does not
        Assert.Equal(new SpellThreatRecord(300, 0, 0f, 0), result.Content.Find(300)); // 5875..5875 holds it: multiplier 0 survives
        Assert.Equal(new SpellThreatRecord(100, 10, 1f, 0), result.Content.Find(100));
        Assert.Equal(3, result.Content.Count);
        Assert.Equal(60, SpellThreatDumpImporter.Parse(new StringReader(VMangosDump), build: 6005).Content.Find(200)!.Threat); // another build picks the other row
    }

    [Fact]
    public void ColumnsAreMatchedByNameWithoutCase_AndAnInsertMayNameItsOwnColumns()
    {
        const string Dump = "INSERT INTO `spell_threat` (`ENTRY`, `threat`, `Multiplier`, `inverse_effect_mask`) VALUES (5,12,1.25,3),(6,1,1,0);";

        SpellThreatContent content = SpellThreatDumpImporter.Parse(new StringReader(Dump)).Content;

        Assert.Equal(new SpellThreatRecord(5, 12, 1.25f, 3), content.Find(5));
        Assert.Equal(2, content.Count);
    }

    [Fact]
    public void AnUnusedAttackPowerBonus_IsRefused_RatherThanSilentlyIgnored()
    {
        const string Dump = "CREATE TABLE `spell_threat` (\n  `entry` int NOT NULL,\n  `Threat` smallint NOT NULL,\n  `multiplier` float NOT NULL,\n  `ap_bonus` float NOT NULL,\n  PRIMARY KEY (`entry`)\n);\nINSERT INTO `spell_threat` VALUES (1,5,1,0.3);";

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => SpellThreatDumpImporter.Parse(new StringReader(Dump)));

        Assert.Contains("ap_bonus", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("INSERT INTO `spell_threat` VALUES (1,5,1,0);", "no CREATE TABLE")]
    [InlineData("INSERT INTO `spell_threat` (`entry`,`threat`) VALUES (1,2,3);", "values but")]
    [InlineData("INSERT INTO `spell_threat` (`entry`,`threat`) VALUES (1,-5);", "outside 0..65535")]
    [InlineData("INSERT INTO `spell_threat` (`entry`,`threat`) VALUES (1,70000);", "outside 0..65535")]
    [InlineData("INSERT INTO `spell_threat` (`entry`,`threat`) VALUES (1,NULL);", "not a number")]
    [InlineData("INSERT INTO `spell_threat` (`entry`,`threat`) VALUES (1,2),(1,3);", "twice")]
    [InlineData("INSERT INTO `spell_threat` (`entry`,`threat`,`multiplier`) VALUES (1,2,-1);", "multiplier")]
    [InlineData("INSERT INTO `spell_threat` (`entry`,`threat`,`inverse_effect_mask`) VALUES (1,2,256);", "inverse_effect_mask")]
    [InlineData("INSERT INTO `spell_threat` (`threat`) VALUES (1);", "no column 'entry'")]
    [InlineData("INSERT INTO `spell_threat` (`entry`) VALUES (1);", "no column 'threat'")]
    [InlineData("INSERT INTO `spell_threat` (`entry`,`threat`) VALUES (1,2) junk", "expected ','")]
    public void MalformedDumps_FailClosed(string dump, string expected)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => SpellThreatDumpImporter.Parse(new StringReader(dump)));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADumpWithoutThePrimaryTable_YieldsNothing()
    {
        SpellThreatParseResult result = SpellThreatDumpImporter.Parse(new StringReader("INSERT INTO `other` VALUES (1,2);"));

        Assert.Equal((0, 0, 0), (result.RowsRead, result.RowsFilteredByBuild, result.Content.Count));
    }

    // --- the rank fill (SpellMgr.cpp:127-182, :770-827) -----------------------------------------

    private static SpellRankChains Chain(params (uint Spell, uint Forward)[] links)
        => new(links.Select((l, i) => new SkillLineAbilityRecord((uint)i + 1, 26, l.Spell, 0, 0, 0, l.Forward, 0, 0, 0)));

    [Fact]
    public void AHigherRankWithoutItsOwnRow_InheritsTheFirstRanksData()
    {
        SpellRankChains ranks = Chain((100, 101), (101, 102), (102, 0));
        var content = new SpellThreatContent([new SpellThreatRecord(100, 80, 1f, 0)]);

        IReadOnlyDictionary<uint, SpellThreatRecord> table = content.Resolve(ranks);

        Assert.Equal(80, table[100].Threat);
        Assert.Equal(new SpellThreatRecord(101, 80, 1f, 0), table[101]);
        Assert.Equal(new SpellThreatRecord(102, 80, 1f, 0), table[102]);
    }

    [Fact]
    public void ACustomRankKeepsItsOwnData_AndTheRanksAboveItStillTakeTheFirstRanks()
    {
        SpellRankChains ranks = Chain((100, 101), (101, 102), (102, 0));
        var reports = new List<string>();
        var content = new SpellThreatContent([new SpellThreatRecord(100, 80, 1f, 0), new SpellThreatRecord(101, 120, 1f, 0)]);

        IReadOnlyDictionary<uint, SpellThreatRecord> table = content.Resolve(ranks, report: reports.Add);

        Assert.Equal(120, table[101].Threat);
        Assert.Equal(80, table[102].Threat); // fills from the first rank, not from the custom rank (SpellMgr.cpp:170-174)
        Assert.Empty(reports);
    }

    [Fact]
    public void ACustomRankIdenticalToTheFirstIsKeptAndReportedRedundant_AFlatlessOneIsDropped_AMissingFirstIsReported()
    {
        SpellRankChains ranks = Chain((100, 101), (101, 102), (102, 0), (200, 201), (201, 0));
        var reports = new List<string>();
        var content = new SpellThreatContent(
        [
            new SpellThreatRecord(100, 80, 1f, 0), new SpellThreatRecord(101, 80, 1f, 0), // redundant custom rank
            new SpellThreatRecord(102, 0, 2f, 0), // custom rank with no threat: dropped
            new SpellThreatRecord(201, 50, 1f, 0), // custom rank whose first rank (200) has no row
        ]);

        IReadOnlyDictionary<uint, SpellThreatRecord> table = content.Resolve(ranks, report: reports.Add);

        Assert.Equal(80, table[101].Threat);
        Assert.Equal(80, table[102].Threat); // the dropped row left room for the inherited data
        Assert.Equal(50, table[201].Threat);
        Assert.Contains(reports, r => r.Contains("Spell 101", StringComparison.Ordinal) && r.Contains("redundant", StringComparison.Ordinal));
        Assert.Contains(reports, r => r.Contains("Spell 102", StringComparison.Ordinal) && r.Contains("has no threat", StringComparison.Ordinal));
        Assert.Contains(reports, r => r.Contains("Spell 200", StringComparison.Ordinal) && r.Contains("must be listed", StringComparison.Ordinal));
    }

    [Fact]
    public void AListedSpellThatDoesNotExist_IsDroppedAndReported()
    {
        var reports = new List<string>();
        var content = new SpellThreatContent([new SpellThreatRecord(1, 5, 1f, 0), new SpellThreatRecord(2, 5, 1f, 0)]);

        IReadOnlyDictionary<uint, SpellThreatRecord> table = content.Resolve(SpellRankChains.Empty, spellExists: id => id == 1, report: reports.Add);

        Assert.Equal([1u], table.Keys);
        Assert.Contains("Spell 2", Assert.Single(reports), StringComparison.Ordinal);
    }
}
