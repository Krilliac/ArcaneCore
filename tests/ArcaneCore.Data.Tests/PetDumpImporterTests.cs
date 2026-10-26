using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Pets;
using ArcaneCore.Kernel.WorldData.Pets;
using ArcaneCore.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The pet data import: <see cref="PetDumpImporter"/> reads the classic-db layout of <c>pet_levelstats</c>
/// (<c>creature_entry, level, hp, mana, armor, str, agi, sta, inte, spi</c>) and <c>petcreateinfo_spell</c>, and the shipped content
/// file <c>sql/content/world/&lt;provider&gt;/0002_pet_create_spells.sql</c> seeds the create spells of a clean database. The dump rows are
/// invented numbers in the real layout.
/// </summary>
public sealed class PetDumpImporterTests : IAsyncLifetime
{
    private const string Dump = """
        CREATE TABLE `pet_levelstats` (
          `creature_entry` mediumint unsigned NOT NULL,
          `level` tinyint unsigned NOT NULL,
          `hp` smallint unsigned NOT NULL,
          `mana` smallint unsigned NOT NULL,
          `armor` int unsigned NOT NULL DEFAULT '0',
          `str` smallint unsigned NOT NULL,
          `agi` smallint unsigned NOT NULL,
          `sta` smallint unsigned NOT NULL,
          `inte` smallint unsigned NOT NULL,
          `spi` smallint unsigned NOT NULL,
          PRIMARY KEY (`creature_entry`,`level`)
        ) ENGINE=MyISAM;
        INSERT INTO `pet_levelstats` VALUES (416,1,42,11,20,22,21,23,24,25),(416,2,55,12,21,26,27,28,29,30),(416,61,99,99,99,9,9,9,9,9),(416,0,1,1,1,1,1,1,1,1),(777,2,50,1,1,1,1,1,1,1);
        CREATE TABLE `pet_levelstats_copy` (`creature_entry` int, `level` int, `hp` int, `mana` int, `armor` int, `str` int, `agi` int, `sta` int, `inte` int, `spi` int);
        INSERT INTO `pet_levelstats_copy` VALUES (999,1,1,1,1,1,1,1,1,1);
        CREATE TABLE `petcreateinfo_spell` (
          `entry` mediumint unsigned NOT NULL DEFAULT '0',
          `Spell1` mediumint unsigned NOT NULL DEFAULT '0',
          `Spell2` mediumint unsigned NOT NULL DEFAULT '0',
          `Spell3` mediumint unsigned NOT NULL DEFAULT '0',
          `Spell4` mediumint unsigned NOT NULL DEFAULT '0',
          PRIMARY KEY (`entry`)
        ) ENGINE=MyISAM;
        INSERT INTO `petcreateinfo_spell` VALUES (416,3110,0,0,0),(510,6873,9672,0,0);
        """;

    private readonly TestDatabases _databases = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static PetDumpImporter ReadDump()
    {
        var importer = new PetDumpImporter();
        importer.Read(new StringReader(Dump));
        return importer;
    }

    [Fact]
    public void Importer_MapsTheClassicDbColumnsByName_AndLeavesTheDamageColumnsZero()
    {
        (IReadOnlyList<PetLevelStatsRow> stats, IReadOnlyList<PetCreateSpellRow> spells) = ReadDump().Resolve();

        Assert.Equal([(416u, (byte)1), (416u, (byte)2)], stats.Select(r => (r.Entry, r.Level)));
        PetLevelStatsRow first = stats[0];
        Assert.Equal((42u, 11u, 20u), (first.Health, first.Mana, first.Armor));
        Assert.Equal((22u, 21u, 23u, 24u, 25u), (first.Strength, first.Agility, first.Stamina, first.Intellect, first.Spirit));
        Assert.Equal((0f, 0f), (first.DmgMin, first.DmgMax));
        Assert.Equal([(416u, 3110u, 0u), (510u, 6873u, 9672u)], spells.Select(r => (r.Entry, r.Spell1, r.Spell2)));
    }

    [Fact]
    public void Importer_DropsLevelsOutsideTheRange_AndCreaturesWithoutLevelOne_AndIgnoresTheCopyTable()
    {
        PetImportReport report = ReadDump().BuildReport();

        Assert.Equal(2, report.LevelStats);       // 416 levels 1 and 2; level 0 and 61 are ignored, 777 has no level 1, the _copy table is not read
        Assert.Equal(2, report.CreateSpells);
        Assert.Equal(2, report.SkippedRows);      // levels 0 and 61
        Assert.Contains(report.Warnings, w => w.Contains("no level 1", StringComparison.Ordinal));

        // what the importer writes must construct PetContent (an entry without level 1 is a hard error there)
        (IReadOnlyList<PetLevelStatsRow> stats, IReadOnlyList<PetCreateSpellRow> spells) = ReadDump().Resolve();
        PetContent content = new(
            stats.Select(r => new PetLevelStats(r.Entry, r.Level, r.Health, r.Mana, r.Armor, r.DmgMin, r.DmgMax, r.Strength, r.Agility, r.Stamina, r.Intellect, r.Spirit)),
            spells.Select(r => new PetCreateSpells(r.Entry, [r.Spell1, r.Spell2, r.Spell3, r.Spell4])));
        Assert.Equal(1, content.LevelStatsEntryCount);
        Assert.Equal([6873u, 9672u], content.GetCreateSpells(510));
    }

    [Fact]
    public async Task WriteAsync_RoundTripsThroughTheStore_AndReplaceEmptiesBothTables()
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            PetImportReport report = await ReadDump().WriteAsync(db, replace: false);
            Assert.Equal((2, 2), (report.LevelStats, report.CreateSpells));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            PetContent content = await new EfPetDataStore(db).LoadAsync();
            Assert.Equal(42u, content.FindLevelStats(416, 1)!.Health);
            Assert.Equal([3110u], content.GetCreateSpells(416));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => ReadDump().WriteAsync(db, replace: false)); // keys exist
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            var smaller = new PetDumpImporter();
            smaller.Read(new StringReader("INSERT INTO `petcreateinfo_spell` VALUES (1860,3716,0,0,0);"));
            await smaller.WriteAsync(db, replace: true);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            PetContent content = await new EfPetDataStore(db).LoadAsync();
            Assert.Equal(0, content.LevelStatsEntryCount);
            Assert.Equal(1, content.CreateSpellEntryCount);
            Assert.Equal([3716u], content.GetCreateSpells(1860));
        }
    }

    [Fact]
    public void TableSpecs_NameBothPetTablesAndTheirKeys()
    {
        TableSpec stats = ContentTableSpecs.Find("pet_levelstats")!;
        Assert.Equal(["creature_entry", "level"], stats.Keys.Select(k => k.Canonical));
        Assert.True(stats.IsMapped("hp"));
        Assert.False(stats.IsMapped("comment"));
        TableSpec spells = ContentTableSpecs.Find("petcreateinfo_spell")!;
        Assert.Equal(["entry"], spells.Keys.Select(k => k.Canonical));
        Assert.True(spells.IsMapped("Spell4"));
    }

    [Fact]
    public async Task ShippedContent_SeedsTheCreateSpellsOnACleanDatabase_AndInstallsAgainToTheSameRows()
    {
        string root = RepositorySource.FindRoot() ?? throw new Xunit.Sdk.XunitException("the ArcaneCore checkout was not found");
        string script = await File.ReadAllTextAsync(Path.Combine(root, "sql", "content", "world", "sqlite", "0002_pet_create_spells.sql"));
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        }

        await Run(cs, script);
        await Run(cs, script);

        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        PetContent content = await new EfPetDataStore(read).LoadAsync();
        Assert.Equal(7, content.CreateSpellEntryCount);
        Assert.Equal([3110u], content.GetCreateSpells(416));
        Assert.Equal([6873u, 9672u], content.GetCreateSpells(510));
        Assert.Equal([25163u], content.GetCreateSpells(15429));
    }

    [Theory]
    [InlineData("sqlite", "\"")]
    [InlineData("mariadb", "`")]
    [InlineData("postgresql", "\"")]
    public async Task EveryProviderShipsTheContent_ThatDeletesBeforeInserting(string provider, string quote)
    {
        string root = RepositorySource.FindRoot() ?? throw new Xunit.Sdk.XunitException("the ArcaneCore checkout was not found");
        string script = await File.ReadAllTextAsync(Path.Combine(root, "sql", "content", "world", provider, "0002_pet_create_spells.sql"));
        int delete = script.IndexOf($"DELETE FROM {quote}petcreateinfo_spell{quote}", StringComparison.Ordinal);
        int insert = script.IndexOf($"INSERT INTO {quote}petcreateinfo_spell{quote}", StringComparison.Ordinal);
        Assert.True(delete >= 0, $"{provider}: no DELETE");
        Assert.True(insert > delete, $"{provider}: INSERT must follow the DELETE");
        Assert.Contains("(416, 3110, 0, 0, 0)", script, StringComparison.Ordinal);
    }

    private static async Task Run(DatabaseConnectionOptions cs, string script)
    {
        await using var connection = new SqliteConnection(cs.ConnectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
    }
}
