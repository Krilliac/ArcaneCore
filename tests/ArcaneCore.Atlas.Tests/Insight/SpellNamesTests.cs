using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Insight;
using Xunit;
using WdbcTestBuilder = ArcaneCore.Atlas.Tests.Reference.Mpq.WdbcTestBuilder;

namespace ArcaneCore.Atlas.Tests.Insight;

/// <summary>A fact that needs the extracted 1.12.1 client's Spell.dbc; skipped, visibly, without it.</summary>
public sealed class SpellDbcRealFactAttribute : FactAttribute
{
    public SpellDbcRealFactAttribute()
    {
        string? root = Environment.GetEnvironmentVariable(DirectoryClientFiles.TestEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "DBFilesClient", "Spell.dbc")))
        {
            Skip = $"{DirectoryClientFiles.TestEnvironmentVariable} is not set to an extracted 1.12.1 client holding DBFilesClient\\Spell.dbc.";
        }
    }
}

/// <summary>Spell names come from the client's Spell.dbc: 173 fields in build 5875, ID in field 0 and the enUS name in field 120.</summary>
public sealed class SpellNamesTests
{
    private const int SpellFields = 173;
    private const int NameField = 120;

    private static byte[] Table(params (uint Id, string Name)[] rows)
    {
        var builder = new WdbcTestBuilder(SpellFields);
        foreach ((uint id, string name) in rows)
        {
            builder.Row([(0, id), .. builder.Localized(NameField, name)]);
        }

        return builder.Build();
    }

    [Fact]
    public void FromBytes_MapsEachIdToItsEnglishName_AndUnknownIdsToNull()
    {
        SpellNames names = SpellNames.FromBytes(Table((133, "Fireball"), (1, "Word of Recall"), (7, "")));

        Assert.Equal(2, names.Count);
        Assert.Equal("Fireball", names.Name(133));
        Assert.Equal("Word of Recall", names.Name(1));
        Assert.Null(names.Name(99999));
        Assert.Null(names.Name(7));
    }

    [Fact]
    public void FromBytes_RefusesAnotherLayoutInsteadOfGuessingColumns()
    {
        byte[] wrong = new WdbcTestBuilder(42).Row((0, 1u)).Build();

        Assert.Throws<InvalidDataException>(() => SpellNames.FromBytes(wrong));
    }

    [Fact]
    public void TryLoad_ReadsTheTableFromClientFiles_AndIsNullWhenTheClientHasNone()
    {
        var files = new MemoryClientFiles();
        Assert.Null(SpellNames.TryLoad(files));

        files.Add(SpellNames.TablePath, Table((133, "Fireball")));

        Assert.Equal("Fireball", SpellNames.TryLoad(files)!.Name(133));
    }

    [SpellDbcRealFact]
    public void TryLoad_OfARealClient_NamesTheVanillaFireball()
    {
        var files = new DirectoryClientFiles(Environment.GetEnvironmentVariable(DirectoryClientFiles.TestEnvironmentVariable)!);

        SpellNames names = SpellNames.TryLoad(files)!;

        // Spell 133 is Fireball (Rank 1) in every vanilla client.
        Assert.Equal("Fireball", names.Name(133));
        Assert.True(names.Count > 20_000, $"only {names.Count} spells");
    }
}
