using ArcaneCore.Data.Content.Import;
using ArcaneCore.World.Spells.Mods;
using Xunit;

namespace ArcaneCore.World.Tests.SpellMods;

/// <summary>
/// The class-mask overlay file (<c>Spells:Mods:ClassMaskFile</c>): "spell effect 0xmask" per line. A malformed line or a duplicate
/// (spell, effect) fails the load (charter: fail closed): a silently skipped line would give a talent the wrong spells.
/// </summary>
public sealed class FileClassMaskSourceTests
{
    [Fact]
    public void Parse_ReadsHexAndDecimalMasks_SkipsCommentsAndBlankLines()
    {
        FileClassMaskSource source = FileClassMaskSource.Parse(
            ["# header", "", "11083 0 0x0000000000C2E297", "12536 1 275427498743", "  16870   0   0x10000000000001  # trailing note"], "test");

        Assert.Equal(0xC2E297UL, source.TryGetMask(11083, 0));
        Assert.Equal(275427498743UL, source.TryGetMask(12536, 1));
        Assert.Equal(0x10000000000001UL, source.TryGetMask(16870, 0));
        Assert.Equal(3, source.Count);
        Assert.Equal(2, source.WideCount);   // 12536 and 16870 need more than 32 bits
    }

    [Fact]
    public void AnUnknownSpellOrEffect_HasNoOverlayRow()
    {
        FileClassMaskSource source = FileClassMaskSource.Parse(["11083 0 0x5"], "test");

        Assert.Null(source.TryGetMask(11083, 1));
        Assert.Null(source.TryGetMask(99, 0));
    }

    [Fact]
    public void AZeroMaskRow_IsKept_ItOverridesTheDbcValueWithNothing()
    {
        FileClassMaskSource source = FileClassMaskSource.Parse(["17904 0 0x0"], "test");

        Assert.Equal(0UL, source.TryGetMask(17904, 0));
    }

    [Theory]
    [InlineData("11083 0")]
    [InlineData("11083 0 0x5 extra")]
    [InlineData("abc 0 0x5")]
    [InlineData("11083 3 0x5")]
    [InlineData("11083 0 0xZZ")]
    [InlineData("11083 0 -5")]
    [InlineData("11083 0 18446744073709551616")]
    public void AMalformedLine_FailsTheLoad_NamingTheLine(string line)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => FileClassMaskSource.Parse(["# ok", line], "masks.txt"));

        Assert.Contains("masks.txt", error.Message, StringComparison.Ordinal);
        Assert.Contains("line 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADuplicateRow_FailsTheLoad()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => FileClassMaskSource.Parse(["5 0 0x1", "5 0 0x2"], "masks.txt"));

        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_ReadsTheFileTheImporterWrites()
    {
        var importer = new SpellAffectDumpImporter();
        importer.Read(new StringReader("CREATE TABLE `spell_affect` (`entry` int, `effectId` int, `SpellFamilyMask` bigint);\nINSERT INTO `spell_affect` VALUES (12536,0,275427498743),(7,2,1);"));
        string path = Path.Combine(Path.GetTempPath(), "arcanecore-mask-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            using (var writer = new StreamWriter(path))
            {
                importer.WriteOverlay(writer);
            }

            FileClassMaskSource source = FileClassMaskSource.Load(path);

            Assert.Equal(275427498743UL, source.TryGetMask(12536, 0));
            Assert.Equal(1UL, source.TryGetMask(7, 2));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_OfAMissingFile_Fails()
    {
        Assert.Throws<FileNotFoundException>(() => FileClassMaskSource.Load(Path.Combine(Path.GetTempPath(), "arcanecore-no-such-mask-file.txt")));
    }
}
