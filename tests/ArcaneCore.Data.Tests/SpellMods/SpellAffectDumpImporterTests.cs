using ArcaneCore.Data.Content.Import;
using Xunit;

namespace ArcaneCore.Data.Tests.SpellMods;

/// <summary>
/// The class-mask overlay writer: <c>spell_affect</c> of a cmangos classic-db dump (entry, effectId, SpellFamilyMask as a 64-bit
/// unsigned) becomes the text file <c>Spells:Mods:ClassMaskFile</c> reads (one "spell effect 0xmask" line per row). The 32-bit
/// spell DBC cannot hold the masks above bit 31 (about one row in nine), which is why the overlay exists. The dumps here are
/// synthetic; real data is GPL and is never committed.
/// </summary>
public sealed class SpellAffectDumpImporterTests
{
    private const string Dump = """
        CREATE TABLE `spell_affect` (`entry` smallint unsigned NOT NULL, `effectId` tinyint unsigned NOT NULL, `SpellFamilyMask` bigint unsigned NOT NULL, PRIMARY KEY (`entry`,`effectId`));
        INSERT INTO `spell_affect` VALUES (11083,0,12714007),(12042,1,551557879),(12536,0,275427498743),(16870,0,4512339900743679),(17904,0,0),(99999,2,18446744073709551615);
        """;

    private static SpellAffectDumpImporter Read(string dump)
    {
        var importer = new SpellAffectDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }

    private static string[] Lines(SpellAffectDumpImporter importer)
    {
        var writer = new StringWriter { NewLine = "\n" };
        importer.WriteOverlay(writer);
        return [.. writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith('#'))];
    }

    [Fact]
    public void RowsBecomeOverlayLines_WithTheFullSixtyFourBitMask()
    {
        string[] lines = Lines(Read(Dump));

        Assert.Equal(
            [
                $"11083 0 0x{12714007UL:X16}",
                $"12042 1 0x{551557879UL:X16}",
                $"12536 0 0x{275427498743UL:X16}",
                $"16870 0 0x{4512339900743679UL:X16}",
                $"17904 0 0x{0UL:X16}",
                $"99999 2 0x{ulong.MaxValue:X16}",
            ],
            lines);
    }

    [Fact]
    public void Masks_RoundTripBitForBit_IncludingBitSixtyThree()
    {
        SpellAffectDumpImporter importer = Read(Dump);

        Assert.Equal(275427498743UL, importer.Masks[(12536u, 0)]);
        Assert.Equal(4512339900743679UL, importer.Masks[(16870u, 0)]);
        Assert.Equal(ulong.MaxValue, importer.Masks[(99999u, 2)]);
        Assert.Contains($"99999 2 0x{ulong.MaxValue:X16}", Lines(importer));
        Assert.Contains($"12536 0 0x{275427498743UL:X16}", Lines(importer));
    }

    [Fact]
    public void TheReport_CountsWideAndZeroMasks()
    {
        SpellAffectImportReport report = Read(Dump).BuildReport();

        Assert.Equal(6, report.Rows);
        Assert.Equal(3, report.WideMasks);   // 12536, 16870 and 99999 need more than 32 bits
        Assert.Equal(1, report.ZeroMasks);
    }

    [Fact]
    public void LinesAreSortedBySpellThenEffect_AndLaterRowsReplaceEarlierOnes()
    {
        const string Again = "CREATE TABLE `spell_affect` (`entry` int, `effectId` int, `SpellFamilyMask` bigint);\nINSERT INTO `spell_affect` VALUES (20,1,5),(10,0,1),(20,0,2),(10,0,7);";

        string[] lines = Lines(Read(Again));

        Assert.Equal(["10 0 0x0000000000000007", "20 0 0x0000000000000002", "20 1 0x0000000000000005"], lines);
    }

    [Fact]
    public void OtherTables_AreIgnored()
    {
        SpellAffectDumpImporter importer = Read("CREATE TABLE `spell_chain` (`spell_id` int, `prev_spell` int);\nINSERT INTO `spell_chain` VALUES (1,2);");

        Assert.Empty(importer.Masks);
        Assert.Equal(0, importer.BuildReport().Rows);
    }

    [Fact]
    public void ARowWithoutAParsableMask_IsSkippedAndReported()
    {
        const string Bad = "CREATE TABLE `spell_affect` (`entry` int, `effectId` int, `SpellFamilyMask` varchar(20));\nINSERT INTO `spell_affect` VALUES (1,0,'xyz'),(2,0,'8'),(3,5,'8');";

        SpellAffectImportReport report = Read(Bad).BuildReport();

        Assert.Equal(1, report.Rows);
        Assert.Equal(2, report.SkippedRows);   // an unparsable mask and an effect index above 2
        Assert.NotEmpty(report.Warnings);
    }

    [Fact]
    public void TheOverlayHeader_NamesTheSourceAndTheConfigurationKey()
    {
        var writer = new StringWriter();
        Read(Dump).WriteOverlay(writer);

        Assert.Contains("Spells:Mods:ClassMaskFile", writer.ToString(), StringComparison.Ordinal);
    }
}
