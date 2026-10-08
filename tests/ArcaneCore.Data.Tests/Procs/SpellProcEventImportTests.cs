using ArcaneCore.Data.World.Procs;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Procs;
using Xunit;

namespace ArcaneCore.Data.Tests.Procs;

/// <summary>
/// The <c>spell_proc_event</c> importer and the rank fill (vmangos SpellMgr::LoadSpellProcEvents, Spells/SpellMgr.cpp:316-365, SpellRankHelper
/// :127-182, DoSpellProcEvent :184-315). The classic-db fixture rows are real Full_DB z2815 tuples (Lightning Shield 324, Shield Block 2565, Fear Ward
/// 6346, Flurry 12319, a priest family row 14892 and Hand of Justice 15600, cooldowns in seconds before z2829); the vmangos fixture follows the
/// loader's SELECT and build range.
/// </summary>
public sealed class SpellProcEventImportTests
{
    private const string ClassicDump = """
        CREATE TABLE `spell_proc_event` (
          `entry` mediumint unsigned NOT NULL DEFAULT '0',
          `SchoolMask` tinyint unsigned NOT NULL DEFAULT '0',
          `SpellFamilyName` smallint unsigned NOT NULL DEFAULT '0',
          `SpellFamilyMask0` bigint unsigned NOT NULL DEFAULT '0',
          `SpellFamilyMask1` bigint unsigned NOT NULL DEFAULT '0',
          `SpellFamilyMask2` bigint unsigned NOT NULL DEFAULT '0',
          `procFlags` int unsigned NOT NULL DEFAULT '0',
          `procEx` int unsigned NOT NULL DEFAULT '0',
          `ppmRate` float NOT NULL DEFAULT '0',
          `CustomChance` float NOT NULL DEFAULT '0',
          `Cooldown` int unsigned NOT NULL DEFAULT '0',
          PRIMARY KEY (`entry`)
        ) ENGINE=MyISAM DEFAULT CHARSET=utf8mb3;
        INSERT INTO `spell_proc_event` VALUES (324,0,0,0,0,0,0,65536,0,0,3),(2565,0,0,0,0,0,0,64,0,0,0),(6346,127,0,0,0,0,0,256,0,0,0),(12319,0,0,0,0,0,0,2,0,0,0),(14892,0,6,17448312320,17448312320,17448312320,0,2,0,0,0),(15600,0,0,0,0,0,0,0,0.6,0,3);
        """;

    private const string VMangosDump = """
        INSERT INTO `spell_proc_event` (`entry`,`SchoolMask`,`SpellFamilyName`,`SpellFamilyMask0`,`SpellFamilyMask1`,`SpellFamilyMask2`,`procFlags`,`procEx`,`ppmRate`,`CustomChance`,`Cooldown`,`build_min`,`build_max`) VALUES (100,0,0,0,0,0,4,2,0,0,0,0,5464),(100,0,0,0,0,0,4,1,0,0,0,5875,9999),(200,4,3,'18446744073709551615',0,0,0,0,'2.5',0,10000,0,9999);
        """;

    [Fact]
    public void TheClassicDbRows_Parse_AndSecondCooldownsBecomeMilliseconds()
    {
        SpellProcEventParseResult result = SpellProcEventDumpImporter.Parse(new StringReader(ClassicDump), cooldownUnit: ProcCooldownUnit.Seconds);

        Assert.Equal((6, 0), (result.RowsRead, result.RowsFilteredByBuild));
        Assert.Equal(new SpellProcEventRecord(324, 0, 0, 0, 0, 0, 0, 65536, 0, 0, 3000), result.Content.Find(324));
        Assert.Equal(64u, result.Content.Find(2565)!.ProcEx); // PROC_EX_BLOCK
        Assert.Equal(127u, result.Content.Find(6346)!.SchoolMask);
        Assert.Equal(17448312320UL, result.Content.Find(14892)!.SpellFamilyMask0); // above 32 bits: the table keeps 64-bit masks
        Assert.Equal(0.6f, result.Content.Find(15600)!.PpmRate);
        Assert.Equal(3000u, result.Content.Find(15600)!.Cooldown);
    }

    [Fact]
    public void TheVMangosDialect_KeepsOnlyTheBuild5875Rows_AndReadsQuotedNumbersAndFullWidthMasks()
    {
        SpellProcEventParseResult result = SpellProcEventDumpImporter.Parse(new StringReader(VMangosDump));

        Assert.Equal((3, 1), (result.RowsRead, result.RowsFilteredByBuild));
        Assert.Equal(1u, result.Content.Find(100)!.ProcEx); // the 5875..9999 row, not the 0..5464 one
        SpellProcEventRecord row = result.Content.Find(200)!;
        Assert.Equal(ulong.MaxValue, row.SpellFamilyMask0);
        Assert.Equal((2.5f, 10000u), (row.PpmRate, row.Cooldown));
    }

    [Theory]
    [InlineData("INSERT INTO `spell_proc_event` VALUES (1,0,0,0,0,0,0,0,0,0,0);", "no CREATE TABLE")]
    [InlineData("INSERT INTO `spell_proc_event` (`entry`,`procFlags`) VALUES (1,2,3);", "values but")]
    [InlineData("INSERT INTO `spell_proc_event` (`entry`,`procFlags`) VALUES (1,2),(1,3);", "twice")]
    [InlineData("INSERT INTO `spell_proc_event` (`entry`,`ppmRate`) VALUES (1,-1);", "non-negative")]
    [InlineData("INSERT INTO `spell_proc_event` (`entry`,`SpellFamilyMask0`) VALUES (1,-1);", "unsigned 64-bit")]
    [InlineData("INSERT INTO `spell_proc_event` (`entry`,`procFlags`) VALUES (1,NULL);", "not a number")]
    [InlineData("INSERT INTO `spell_proc_event` (`procFlags`) VALUES (1);", "no column 'entry'")]
    public void MalformedDumps_FailClosed(string dump, string expected)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => SpellProcEventDumpImporter.Parse(new StringReader(dump)));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    // --- the rank fill (SpellMgr.cpp:127-315) -----------------------------------------------------

    private static SpellRankChains Chain(params (uint Spell, uint Forward)[] links)
        => new(links.Select((l, i) => new SkillLineAbilityRecord((uint)i + 1, 26, l.Spell, 0, 0, 0, l.Forward, 0, 0, 0)));

    private static SpellProcEventRecord Row(uint entry, uint procEx = 0, float ppm = 0, uint cooldown = 0)
        => new(entry, 0, 0, 0, 0, 0, 0, procEx, ppm, 0, cooldown);

    [Fact]
    public void AHigherRankWithoutItsOwnRow_InheritsTheFirstRanksConditions()
    {
        SpellRankChains ranks = Chain((100, 101), (101, 102), (102, 0));

        IReadOnlyDictionary<uint, SpellProcEventRecord> table = new SpellProcEventContent([Row(100, procEx: 2, cooldown: 500)]).Resolve(ranks);

        Assert.Equal(Row(101, procEx: 2, cooldown: 500), table[101]);
        Assert.Equal(Row(102, procEx: 2, cooldown: 500), table[102]);
    }

    [Fact]
    public void OnlyAPpmRow_MayStandForAHigherRank()
    {
        // DoSpellProcEvent::IsValidCustomRank: "let have independent data in table for spells with ppm rates".
        SpellRankChains ranks = Chain((100, 101), (101, 102), (102, 0));
        var reports = new List<string>();
        var content = new SpellProcEventContent([Row(100, ppm: 1), Row(101, ppm: 2), Row(102, procEx: 4)]);

        IReadOnlyDictionary<uint, SpellProcEventRecord> table = content.Resolve(ranks, report: reports.Add);

        Assert.Equal(2f, table[101].PpmRate);
        Assert.Equal(Row(102, ppm: 1), table[102]); // the non-PPM custom row is refused and the rank inherits rank 1
        Assert.Contains(reports, r => r.Contains("102", StringComparison.Ordinal) && r.Contains("not first rank", StringComparison.Ordinal));
    }

    [Fact]
    public void AListedSpellThatDoesNotExist_IsDropped_AndReported()
    {
        var reports = new List<string>();

        IReadOnlyDictionary<uint, SpellProcEventRecord> table = new SpellProcEventContent([Row(7, procEx: 1)])
            .Resolve(SpellRankChains.Empty, spellExists: _ => false, report: reports.Add);

        Assert.Empty(table);
        Assert.Contains(reports, r => r.Contains("does not exist", StringComparison.Ordinal));
    }
}
