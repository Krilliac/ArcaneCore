using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.PlayerCreate;

/// <summary>
/// New-character content: start positions (<c>playercreateinfo</c>), starting spells
/// (<c>playercreateinfo_spell</c>), fixed teleport destinations (<c>spell_target_position</c>) and
/// the level-stats file the progression feature reads (<c>player_levelstats</c> joined with
/// <c>player_classlevelstats</c>). Dumps are hand-written in each source's layout.
/// </summary>
public sealed class PlayerCreateDumpImporterTests : IAsyncLifetime
{
    private const string CMangos = """
        CREATE TABLE `playercreateinfo` (`race` tinyint unsigned NOT NULL, `class` tinyint unsigned NOT NULL, `map` mediumint unsigned NOT NULL, `zone` mediumint unsigned NOT NULL, `position_x` float NOT NULL, `position_y` float NOT NULL, `position_z` float NOT NULL, `orientation` float NOT NULL, PRIMARY KEY (`race`, `class`));
        INSERT INTO `playercreateinfo` VALUES (1,1,0,12,-8949.95,-132.493,83.5312,0),(4,5,1,141,10311.3,832.463,1326.41,5.69632);
        CREATE TABLE `playercreateinfo_spell` (`race` tinyint unsigned NOT NULL, `class` tinyint unsigned NOT NULL, `Spell` mediumint unsigned NOT NULL, `Note` varchar(255), PRIMARY KEY (`race`, `class`, `Spell`));
        INSERT INTO `playercreateinfo_spell` VALUES (1,1,78,'Heroic Strike'),(1,1,81,'Dodge'),(4,5,585,'Smite');
        CREATE TABLE `spell_target_position` (`id` mediumint unsigned NOT NULL, `target_map` smallint unsigned NOT NULL, `target_position_x` float NOT NULL, `target_position_y` float NOT NULL, `target_position_z` float NOT NULL, `target_orientation` float NOT NULL, PRIMARY KEY (`id`));
        INSERT INTO `spell_target_position` VALUES (3561,0,-9003.01,874.04,29.62,5.75),(3562,0,-4613.62,-915.22,501.06,1.5);
        CREATE TABLE `player_classlevelstats` (`class` tinyint unsigned NOT NULL, `level` tinyint unsigned NOT NULL, `basehp` mediumint unsigned NOT NULL, `basemana` mediumint unsigned NOT NULL, PRIMARY KEY (`class`, `level`));
        INSERT INTO `player_classlevelstats` VALUES (1,1,60,0),(1,2,80,0),(5,1,52,85);
        CREATE TABLE `player_levelstats` (`race` tinyint unsigned NOT NULL, `class` tinyint unsigned NOT NULL, `level` tinyint unsigned NOT NULL, `str` tinyint unsigned NOT NULL, `agi` tinyint unsigned NOT NULL, `sta` tinyint unsigned NOT NULL, `inte` tinyint unsigned NOT NULL, `spi` tinyint unsigned NOT NULL, PRIMARY KEY (`race`, `class`, `level`));
        INSERT INTO `player_levelstats` VALUES (1,1,2,23,20,22,20,21),(1,1,1,22,20,22,20,21),(4,5,1,17,25,19,20,22);
        """;

    // vmangos: build ranges on the spell tables (ObjectMgr.cpp:4679, SpellMgr.cpp:52), lower-case `spell`.
    private const string VMangos = """
        CREATE TABLE `playercreateinfo_spell` (`race` tinyint unsigned NOT NULL, `class` tinyint unsigned NOT NULL, `spell` mediumint unsigned NOT NULL, `build_min` smallint NOT NULL, `build_max` smallint NOT NULL);
        INSERT INTO `playercreateinfo_spell` VALUES (1,1,78,0,5875),(1,1,81,5875,9999),(1,1,107,6005,9999),(1,1,203,0,5000);
        CREATE TABLE `spell_target_position` (`id` mediumint unsigned NOT NULL, `build_min` smallint NOT NULL, `build_max` smallint NOT NULL, `target_map` smallint unsigned NOT NULL, `target_position_x` float NOT NULL, `target_position_y` float NOT NULL, `target_position_z` float NOT NULL, `target_orientation` float NOT NULL);
        INSERT INTO `spell_target_position` VALUES (1,0,5875,0,1,2,3,4),(2,6005,9999,0,1,2,3,4);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Fact]
    public void StartPositions_MapByName_IncludingTheRenamedColumns()
    {
        PlayerCreateInfoRow row = Import(CMangos).Snapshot().StartPositions.Single(r => r.Race == 4);

        Assert.Equal(((byte)5, 1u, 141u, 10311.3f, 832.463f, 1326.41f, 5.69632f),
            (row.Class, row.MapId, row.ZoneId, row.X, row.Y, row.Z, row.Orientation));
    }

    [Fact]
    public void StartingSpells_KeepTheirNote()
    {
        PlayerCreateSpellRow[] spells = [.. Import(CMangos).Snapshot().CreateSpells];

        Assert.Equal(new Dictionary<uint, string?> { [78] = "Heroic Strike", [81] = "Dodge", [585] = "Smite" }, spells.ToDictionary(s => s.Spell, s => s.Note));
        PlayerCreateSpellRow smite = spells.Single(s => s.Spell == 585);
        Assert.Equal(((byte)4, (byte)5), (smite.Race, smite.Class));
    }

    [Fact]
    public void SpellTargetPositions_MapByName()
    {
        SpellTargetPositionRow row = Import(CMangos).Snapshot().SpellTargets.Single(r => r.Id == 3561);

        Assert.Equal((0u, -9003.01f, 874.04f, 29.62f, 5.75f),
            (row.TargetMap, row.TargetPositionX, row.TargetPositionY, row.TargetPositionZ, row.TargetOrientation));
    }

    [Fact]
    public void VMangosSpellTables_KeepOnlyRowsWhoseBuildRangeContains5875()
    {
        var snapshot = Import(VMangos).Snapshot();

        Assert.Equal([78u, 81u], snapshot.CreateSpells.Select(s => s.Spell).Order());
        Assert.Equal([1u], snapshot.SpellTargets.Select(s => s.Id));
        Assert.Equal(3, Import(VMangos).BuildReport().SkippedRows);
    }

    [Fact]
    public void LevelStatsText_JoinsRaceClassLevelRowsWithTheClassBaseValues_InKeyOrder()
    {
        var writer = new StringWriter();

        int rows = Import(CMangos).WriteLevelStats(writer);

        Assert.Equal(3, rows);
        string[] lines = [.. writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => !l.StartsWith('#'))];
        Assert.Equal(["1,1,1,60,0,22,20,22,20,21", "1,1,2,80,0,23,20,22,20,21", "4,5,1,52,85,17,25,19,20,22"], lines);
        Assert.StartsWith("#", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ALevelStatsRowWithoutClassBaseValues_IsSkippedWithAWarning()
    {
        var importer = new PlayerCreateDumpImporter();
        importer.Read(new StringReader(CMangos));
        importer.Read(new StringReader("INSERT INTO `player_levelstats` (`race`,`class`,`level`,`str`,`agi`,`sta`,`inte`,`spi`) VALUES (2,3,1,10,10,10,10,10);"));

        var writer = new StringWriter();
        int rows = importer.WriteLevelStats(writer);

        Assert.Equal(3, rows);
        Assert.DoesNotContain("2,3,1", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains(importer.BuildReport().Warnings, w => w.Contains("class 3", StringComparison.Ordinal) && w.Contains("level 1", StringComparison.Ordinal));
        Assert.Equal(3, importer.BuildReport().LevelStatRows);
    }

    [Fact]
    public void ARenamedKeyColumn_IsASchemaError()
    {
        var importer = new PlayerCreateDumpImporter();

        ImportSchemaException ex = Assert.Throws<ImportSchemaException>(() => importer.Read(new StringReader(
            "INSERT INTO `playercreateinfo` (`raceid`,`class`,`map`) VALUES (1,1,0);")));

        Assert.Equal("playercreateinfo", ex.Table);
        Assert.Equal("race", ex.Column);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_ReplacesTheDevSeeds_AndTheOtherTablesStay(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.PlayerCreateInfo.Add(new PlayerCreateInfoRow { Race = 9, Class = 9, MapId = 7 });
            db.Add(new PlayerCreateSpellRow { Race = 9, Class = 9, Spell = 1, Note = "seed" });
            db.Add(new SpellTemplateRow { Id = 42, SpellName = "kept" });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            PlayerCreateImportReport report = await Import(CMangos).WriteAsync(db, replace: true);

            Assert.Equal((2, 3, 2, 3), (report.StartPositions, report.CreateSpells, report.SpellTargetPositions, report.LevelStatRows));
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        Assert.Equal([(byte)1, (byte)4], (await verify.PlayerCreateInfo.AsNoTracking().ToListAsync()).Select(r => r.Race).Order());
        Assert.Equal(3, await verify.Set<PlayerCreateSpellRow>().CountAsync());
        Assert.Equal(2, await verify.Set<SpellTargetPositionRow>().CountAsync());
        Assert.Equal(42u, (await verify.Set<SpellTemplateRow>().AsNoTracking().SingleAsync()).Id);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Write_WithoutReplace_FailsOnAKeyConflict_AndChangesNothing(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.PlayerCreateInfo.Add(new PlayerCreateInfoRow { Race = 1, Class = 1, MapId = 7 });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await Assert.ThrowsAsync<DbUpdateException>(() => Import(CMangos).WriteAsync(db, replace: false));
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        Assert.Equal(7u, (await verify.PlayerCreateInfo.AsNoTracking().SingleAsync()).MapId);
        Assert.Equal(0, await verify.Set<PlayerCreateSpellRow>().CountAsync());
    }

    private static PlayerCreateDumpImporter Import(string dump)
    {
        var importer = new PlayerCreateDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }
}
