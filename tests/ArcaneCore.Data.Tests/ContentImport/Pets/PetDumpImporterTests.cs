using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Pets;
using ArcaneCore.Kernel.WorldData.Pets;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Pets;

/// <summary>
/// <c>pet_levelstats</c> and <c>petcreateinfo_spell</c> import. The dumps are hand-written in the column
/// layouts of cmangos classic-db z2815 (<c>creature_entry, level, hp, mana, armor, str, agi, sta, inte, spi</c>)
/// and of vmangos (<c>entry, level, health, mana, armor, dmg_min, dmg_max, strength, ...</c>); the rows are
/// invented, not dump rows. The real dump is never committed, so the 1559 / 7 row counts of z2815 are not
/// asserted here.
/// </summary>
public sealed class PetDumpImporterTests : IAsyncLifetime
{
    private const string ClassicDb = """
        CREATE TABLE `pet_levelstats` (`creature_entry` mediumint unsigned NOT NULL, `level` tinyint unsigned NOT NULL, `hp` int unsigned NOT NULL, `mana` int unsigned NOT NULL, `armor` int unsigned NOT NULL, `str` int unsigned NOT NULL, `agi` int unsigned NOT NULL, `sta` int unsigned NOT NULL, `inte` int unsigned NOT NULL, `spi` int unsigned NOT NULL, PRIMARY KEY (`creature_entry`, `level`));
        INSERT INTO `pet_levelstats` VALUES (416,1,50,40,10,21,22,23,24,25),(416,20,300,260,90,31,32,33,34,35),(417,1,80,0,12,1,2,3,4,5);
        CREATE TABLE `petcreateinfo_spell` (`entry` mediumint unsigned NOT NULL, `Spell1` mediumint unsigned NOT NULL, `Spell2` mediumint unsigned NOT NULL, `Spell3` mediumint unsigned NOT NULL, `Spell4` mediumint unsigned NOT NULL, PRIMARY KEY (`entry`));
        INSERT INTO `petcreateinfo_spell` VALUES (416,3110,7799,0,0),(1860,3716,0,0,0);
        """;

    private const string VMangos = """
        CREATE TABLE `pet_levelstats` (`entry` int unsigned NOT NULL, `level` tinyint unsigned NOT NULL, `health` int unsigned NOT NULL, `mana` int unsigned NOT NULL, `armor` int unsigned NOT NULL, `dmg_min` float NOT NULL, `dmg_max` float NOT NULL, `strength` int unsigned NOT NULL, `agility` int unsigned NOT NULL, `stamina` int unsigned NOT NULL, `intellect` int unsigned NOT NULL, `spirit` int unsigned NOT NULL, PRIMARY KEY (`entry`, `level`));
        INSERT INTO `pet_levelstats` VALUES (416,1,50,40,10,2.5,4.5,21,22,23,24,25);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Fact]
    public void ClassicDbColumns_MapToTheRowProperties()
    {
        PetDumpImporter importer = Import(ClassicDb);

        PetLevelStatsRow imp20 = importer.LevelStats.Single(r => r is { Entry: 416, Level: 20 });
        Assert.Equal((300u, 260u, 90u, 31u, 32u, 33u, 34u, 35u), (imp20.Health, imp20.Mana, imp20.Armor, imp20.Strength, imp20.Agility, imp20.Stamina, imp20.Intellect, imp20.Spirit));
        Assert.Equal((0f, 0f), (imp20.DmgMin, imp20.DmgMax)); // classic-db has no damage columns
        Assert.Equal(3, importer.LevelStats.Count);

        Assert.Equal(2, importer.CreateSpells.Count);
        PetCreateSpellRow imp = importer.CreateSpells.Single(r => r.Entry == 416);
        Assert.Equal([3110u, 7799u, 0u, 0u], [imp.Spell1, imp.Spell2, imp.Spell3, imp.Spell4]);
        PetImportReport report = importer.BuildReport();
        Assert.Equal((3, 2, 0), (report.LevelStats, report.CreateSpells, report.SkippedRows));
    }

    [Fact]
    public void VMangosColumns_MapByName_IncludingDamage()
    {
        PetLevelStatsRow row = Import(VMangos).LevelStats.Single();

        Assert.Equal((50u, 40u, 10u, 2.5f, 4.5f, 21u, 22u, 23u, 24u, 25u),
            (row.Health, row.Mana, row.Armor, row.DmgMin, row.DmgMax, row.Strength, row.Agility, row.Stamina, row.Intellect, row.Spirit));
    }

    [Fact]
    public void ALaterRowWithTheSameKey_ReplacesTheEarlierOne()
    {
        PetDumpImporter importer = Import(ClassicDb + "\nINSERT INTO `pet_levelstats` VALUES (416,20,999,1,1,1,1,1,1,1);");

        Assert.Equal(3, importer.LevelStats.Count);
        Assert.Equal(999u, importer.LevelStats.Single(r => r is { Entry: 416, Level: 20 }).Health);
    }

    [Fact]
    public void ACreatureWithoutLevelOneStats_IsSkippedWithAWarning_SinceTheLoaderRejectsIt()
    {
        PetDumpImporter importer = Import(ClassicDb + "\nINSERT INTO `pet_levelstats` VALUES (500,5,10,10,10,1,1,1,1,1),(500,6,11,10,10,1,1,1,1,1);");

        PetImportReport report = importer.BuildReport();
        Assert.DoesNotContain(importer.LevelStats, r => r.Entry == 500);
        Assert.Equal(2, report.SkippedRows);
        Assert.Contains(report.Warnings, w => w.Contains("500", StringComparison.Ordinal) && w.Contains("level 1", StringComparison.Ordinal));
    }

    [Fact]
    public void ARenamedKeyColumn_IsASchemaError_NotEntryZero()
    {
        var importer = new PetDumpImporter();

        ImportSchemaException ex = Assert.Throws<ImportSchemaException>(() => importer.Read(new StringReader(
            "INSERT INTO `pet_levelstats` (`creature`,`level`,`hp`) VALUES (416,1,50);")));

        Assert.Equal("pet_levelstats", ex.Table);
    }

    [Fact]
    public void Specs_ExistForBothTables_AndReadTheClassicDbColumns()
    {
        TableSpec levels = ContentTableSpecs.Find("pet_levelstats")!;
        foreach (string column in new[] { "creature_entry", "level", "hp", "mana", "armor", "str", "agi", "sta", "inte", "spi", "dmg_min", "health" })
        {
            Assert.True(levels.IsMapped(column), column);
        }

        Assert.False(levels.IsMapped("comment"));
        TableSpec spells = ContentTableSpecs.Find("petcreateinfo_spell")!;
        Assert.True(spells.IsMapped("entry") && spells.IsMapped("Spell4"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_StoresTheRows_AndASummonedImpAtLevel20GetsTheImportedStatsAndSpells(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Add(new PetCreateSpellRow { Entry = 9999, Spell1 = 1 });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            PetImportReport report = await Import(ClassicDb).WriteAsync(db, replace: true);

            Assert.Equal((3, 2), (report.LevelStats, report.CreateSpells));
            Assert.Empty(db.ChangeTracker.Entries());
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        PetContent content = await new EfPetDataStore(verify).LoadAsync();
        PetLevelStats imp = content.FindLevelStats(416, 20)!;
        Assert.Equal((300u, 260u), (imp.Health, imp.Mana));
        Assert.Equal(50u, content.FindLevelStats(416, 19)!.Health);
        Assert.Equal([3110u, 7799u], content.GetCreateSpells(416));
        Assert.Empty(content.GetCreateSpells(9999)); // --replace emptied the table first
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_WithoutReplace_FailsOnAKeyConflict_AndChangesNothing(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await Import(ClassicDb).WriteAsync(db, replace: false);
            await Assert.ThrowsAsync<DbUpdateException>(() => Import(ClassicDb).WriteAsync(db, replace: false));
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        Assert.Equal(3, await verify.Set<PetLevelStatsRow>().CountAsync());
        Assert.Equal(2, await verify.Set<PetCreateSpellRow>().CountAsync());
    }

    private static PetDumpImporter Import(string dump)
    {
        var importer = new PetDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }
}
