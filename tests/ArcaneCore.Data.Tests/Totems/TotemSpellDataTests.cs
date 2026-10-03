using System.IO.Compression;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Totems;
using ArcaneCore.Kernel.WorldData.Totems;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Totems;

/// <summary>
/// A fact reported Skipped (visibly, never green) unless ARCANECORE_CLASSIC_DB names a classic-db 1.12.1
/// dump (.sql or .sql.gz); the dump is a GPL reference that never enters the repository.
/// </summary>
public sealed class ClassicDbFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_CLASSIC_DB";

    public ClassicDbFactAttribute()
    {
        if (DumpPath is not { } path || !File.Exists(path))
        {
            Skip = Variable + " is not set to an existing classic-db dump; real-data audit not run.";
        }
    }

    public static string? DumpPath => Environment.GetEnvironmentVariable(Variable);

    public static TextReader Open()
    {
        Stream stream = File.OpenRead(DumpPath!);
        return DumpPath!.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new StreamReader(new GZipStream(stream, CompressionMode.Decompress))
            : new StreamReader(stream);
    }
}

/// <summary>
/// <c>totem_spell</c> (world schema step <see cref="TotemWorldDataModule.Version"/>), its importer and store.
/// Hand-written dump rows in the classic-db column layout; mangos-classic Entities/Totem.cpp:171-178 takes the
/// first spell of the creature spell list, which is what the importer resolves.
/// </summary>
public sealed class TotemSpellDataTests : IAsyncLifetime
{
    // spell_template/creature_template reduced to the columns the importer reads; creature_template_spells
    // without a column list (as in the real dump) so the CREATE TABLE order must be honoured.
    private const string Dump = """
        CREATE TABLE `creature_template_spells` (
          `entry` mediumint unsigned NOT NULL,
          `setId` int unsigned NOT NULL DEFAULT '0',
          `spell1` mediumint unsigned NOT NULL,
          `spell2` mediumint unsigned NOT NULL DEFAULT '0',
          PRIMARY KEY (`entry`,`setId`)
        );
        INSERT INTO `spell_template` (`Id`,`Effect1`,`Effect2`,`Effect3`,`EffectMiscValue1`,`EffectMiscValue2`,`EffectMiscValue3`) VALUES
        (8071,87,0,0,3,0,0),(8075,88,0,0,5874,0,0),(6495,74,0,0,3968,0,0),(2,6,87,0,5878,5879,0),(3,6,0,0,777,0,0);
        INSERT INTO `creature_template` (`entry`,`name`,`AIName`,`SpellList`) VALUES
        (3,'Stoneclaw Totem','TotemAI',0),(5874,'Strength of Earth Totem','TotemAI',0),(3968,'Sentry Totem','TotemAI',0),
        (5878,'Totem via list','TotemAI',9100),(5879,'Totem set1 zero, no list','TotemAI',0),(777,'Not a totem','',0);
        INSERT INTO `creature_template_spells` VALUES (3,0,5728,0),(5874,0,8076,0),(5874,1,111,0),(5879,0,0,0),(777,0,999,0);
        INSERT INTO `creature_spell_list` (`Id`,`Position`,`SpellId`) VALUES (9100,2,1111),(9100,0,2222),(9100,1,3333);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Fact]
    public void Importer_SyntheticDump_ReadsSpell1_FallsBackToSpellList_SkipsZero_ReportsMissing()
    {
        var importer = new TotemSpellDumpImporter();
        importer.Read(new StringReader(Dump));

        (IReadOnlyList<TotemSpellRow> rows, TotemSpellImportReport report) = importer.Resolve();

        Assert.Equal(
            [(3u, 5728u), (5874u, 8076u), (5878u, 2222u)],
            rows.Select(r => (r.CreatureEntry, r.SpellId)));
        Assert.Equal(1, report.FromSpellList);
        Assert.Equal(3, report.Rows);

        // 3968 (no spell row at all) and 5879 (spell1 0, no list) are summoned by an effect yet have no spell: both are reported.
        Assert.Equal(2, report.SkippedWithoutSpell);
        Assert.Equal([3968u, 5879u], report.SummonedWithoutRow);
    }

    [Fact]
    public void Importer_IgnoresNonDefaultSpellSets()
    {
        var importer = new TotemSpellDumpImporter();
        importer.Read(new StringReader(
            "INSERT INTO `spell_template` (`Id`,`Effect1`,`EffectMiscValue1`) VALUES (1,88,50);"
            + "INSERT INTO `creature_template_spells` (`entry`,`setId`,`spell1`) VALUES (50,3,777);"));

        (IReadOnlyList<TotemSpellRow> rows, _) = importer.Resolve();

        Assert.Empty(rows);
    }

    [Fact]
    public void WorldSchema_HasTheTotemStep_WithItsTable()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == TotemWorldDataModule.Version);
        Assert.Equal("totem_spell", Assert.IsType<CreateTableChange>(Assert.Single(step.Changes)).Table);
        Assert.True(WorldDbContext.Schema.CurrentVersion >= TotemWorldDataModule.Version);
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is TotemWorldDataModule);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ImportAndLoad_RoundTrip_ReplacesPreviousRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        var importer = new TotemSpellDumpImporter();
        importer.Read(new StringReader(Dump));

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<TotemSpellRow>().Add(new TotemSpellRow { CreatureEntry = 1, SpellId = 1 });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            TotemSpellImportReport report = await importer.WriteAsync(db);
            Assert.Equal(3, report.Rows);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            TotemContent content = await new EfTotemDataStore(db).LoadAsync();
            Assert.Equal(3, content.Count);
            Assert.Equal(8076u, content.GetSpell(5874));
            Assert.Equal(2222u, content.GetSpell(5878));
            Assert.Null(content.GetSpell(3968));
            Assert.Null(content.GetSpell(1));
        }
    }

    [Fact]
    public async Task WriteAsync_RefusesAContextWithTrackedChanges()
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(TestDatabases.AvailableProviders().Select(p => (DatabaseProvider)p[0]).First());
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Set<TotemSpellRow>().Add(new TotemSpellRow { CreatureEntry = 9, SpellId = 9 });

        await Assert.ThrowsAsync<InvalidOperationException>(() => new TotemSpellDumpImporter().WriteAsync(db));
    }

    /// <summary>Real data (env-gated, measured on ClassicDB_1_12_1_z2815): 95 totem creatures get a spell from creature_template_spells (none needs the list fallback), 8 have none (Sentry Totem 3968 among them).</summary>
    [ClassicDbFact]
    public void RealClassicDb_TotemSpellCounts_AreMeasured_AndSentryHasNone()
    {
        var importer = new TotemSpellDumpImporter();
        using (TextReader dump = ClassicDbFactAttribute.Open())
        {
            importer.Read(dump);
        }

        (IReadOnlyList<TotemSpellRow> rows, TotemSpellImportReport report) = importer.Resolve();

        Assert.Equal(8076u, rows.Single(r => r.CreatureEntry == 5874).SpellId);
        Assert.Contains(3968u, report.SummonedWithoutRow);
        Assert.Equal((95, 0, 8), (rows.Count, report.FromSpellList, report.SkippedWithoutSpell));
    }
}
