using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.PlayerStats;
using ArcaneCore.Kernel.WorldData.PlayerStats;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The player base data tables (world schema via <see cref="PlayerStatsDataModule"/>), the store, the
/// classic-db / vmangos dump importer and the vmangos migration replay. All SQL is hand-written with
/// synthetic numbers in the layouts of classic-db <c>player_*</c> tables and vmangos
/// <c>sql/migrations/*_world.sql</c>; no GPL rows are copied.
/// </summary>
public sealed class PlayerStatsImportTests : IAsyncLifetime
{
    private const string Dump = """
        CREATE TABLE `player_classlevelstats` (
          `class` tinyint unsigned NOT NULL,
          `level` tinyint unsigned NOT NULL,
          `basehp` smallint unsigned NOT NULL,
          `basemana` smallint unsigned NOT NULL,
          PRIMARY KEY (`class`,`level`)
        ) ENGINE=MyISAM;
        INSERT INTO `player_classlevelstats` VALUES (1,1,91,0),(1,2,92,0),(1,3,93,0),(8,1,81,71),(8,2,82,72),(8,3,83,73);
        CREATE TABLE `player_levelstats` (
          `race` tinyint unsigned NOT NULL,
          `class` tinyint unsigned NOT NULL,
          `level` tinyint unsigned NOT NULL,
          `str` tinyint unsigned NOT NULL,
          `agi` tinyint unsigned NOT NULL,
          `sta` tinyint unsigned NOT NULL,
          `inte` tinyint unsigned NOT NULL,
          `spi` tinyint unsigned NOT NULL,
          PRIMARY KEY (`race`,`class`,`level`)
        ) ENGINE=MyISAM;
        INSERT INTO `player_levelstats` VALUES
        (1,1,1,11,12,13,14,15),(1,1,2,21,22,23,24,25),(1,1,3,31,32,33,34,35),
        (1,8,1,41,42,43,44,45),(1,8,2,51,52,53,54,55),(1,8,3,61,62,63,64,65),
        (5,8,1,71,72,73,74,75),(5,8,2,81,82,83,84,85),(5,8,3,91,92,93,94,95);
        CREATE TABLE `player_xp_for_level` (
          `lvl` int unsigned NOT NULL,
          `xp_for_next_level` int unsigned NOT NULL,
          PRIMARY KEY (`lvl`)
        ) ENGINE=MyISAM;
        INSERT INTO `player_xp_for_level` VALUES (1,401),(2,902),(3,1403);
        """;

    private const string MigrationHead = """
        DROP PROCEDURE IF EXISTS add_migration;
        DELIMITER ??
        CREATE PROCEDURE `add_migration`()
        BEGIN
        DECLARE v INT DEFAULT 1;
        SET v = (SELECT COUNT(*) FROM `migrations` WHERE `id`='20220917141705');
        IF v = 0 THEN
        INSERT INTO `migrations` VALUES ('20220917141705');
        -- Add your query below.

        """;

    private const string MigrationTail = """

        END IF;
        END
        ??
        DELIMITER ;
        CALL add_migration();
        DROP PROCEDURE IF EXISTS add_migration;
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    private static string Migration(string body) => MigrationHead + body + MigrationTail;

    private static PlayerStatsDumpImporter Imported(string dump = Dump)
    {
        var importer = new PlayerStatsDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }

    private static void Apply(PlayerStatsDumpImporter importer, string name, string body)
        => importer.ApplyMigration(name, new StringReader(Migration(body)));

    [Fact]
    public void WorldSchema_IntroducesThePlayerStatsTablesAtTheModuleVersion()
    {
        Assert.True(WorldDbContext.Schema.CurrentVersion >= PlayerStatsDataModule.Version);
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == PlayerStatsDataModule.Version);
        Assert.Equal(PlayerStatsDataModule.Tables, step.Changes.Cast<CreateTableChange>().Select(c => c.Table));
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is PlayerStatsDataModule);
    }

    [Fact]
    public void Dump_IsReadByColumnNameFromTheTableDefinitions()
    {
        PlayerStatsContent content = Imported().ToContent();

        Assert.Equal((6, 9, 3, 0, 0), (content.ClassLevelStatsCount, content.LevelStatsCount, content.XpRowCount, content.CritPerAgility.Count, content.DodgePerAgility.Count));
        Assert.Equal(new ClassLevelStats(8, 2, 82, 72), content.FindClassLevelExact(8, 2));
        Assert.Equal(new LevelStats(5, 8, 3, 91, 92, 93, 94, 95), content.FindLevelExact(5, 8, 3));
        Assert.Equal(new LevelStats(1, 1, 2, 21, 22, 23, 24, 25), content.FindLevelExact(1, 1, 2));
        Assert.Equal(902u, content.XpForNextLevel(2));
        Assert.Null(content.XpForNextLevel(4));
    }

    [Fact]
    public void Dump_WithAnExplicitColumnListMapsEvenWhenTheOrderDiffers()
    {
        var importer = new PlayerStatsDumpImporter();
        importer.Read(new StringReader("INSERT INTO `player_levelstats` (`level`,`race`,`class`,`spi`,`inte`,`sta`,`agi`,`str`) VALUES (7,3,2,1,2,3,4,5);"));
        Assert.Equal(new LevelStats(3, 2, 7, 5, 4, 3, 2, 1), importer.ToContent().FindLevelExact(3, 2, 7));
    }

    [Fact]
    public void Dump_IgnoresRowsTheReferenceLoaderRejects()
    {
        var importer = new PlayerStatsDumpImporter();
        importer.Read(new StringReader("""
            INSERT INTO `player_classlevelstats` (`class`,`level`,`basehp`,`basemana`) VALUES (6,1,1,1),(1,0,1,1),(12,1,1,1),(1,1,50,0);
            INSERT INTO `player_levelstats` (`race`,`class`,`level`,`str`,`agi`,`sta`,`inte`,`spi`) VALUES (9,1,1,1,1,1,1,1),(1,10,1,1,1,1,1,1),(1,1,300,1,1,1,1,1),(1,1,1,256,1,1,1,1),(1,1,1,5,5,5,5,5);
            INSERT INTO `player_crit_per_agility` (`class`,`level`,`rate`) VALUES (1,1,0),(1,2,-3),(1,3,2.5);
            """));

        PlayerStatsContent content = importer.ToContent();
        Assert.Equal((1, 1, 1), (content.ClassLevelStatsCount, content.LevelStatsCount, content.CritPerAgility.Count));
        PlayerStatsImportReport report = importer.BuildReport();
        Assert.Equal(9, report.SkippedRows);
        Assert.Equal(9, report.Warnings.Count);
    }

    [Fact]
    public void Migration_UpdateChangesExactlyTheNamedCells()
    {
        PlayerStatsDumpImporter importer = Imported();
        PlayerStatsContent before = importer.ToContent();

        Apply(importer, "20220917141705_world.sql",
            "UPDATE `player_levelstats` SET `inte` = 21, `spi` = 27 WHERE `race` = 5 AND `class` = 8 AND `level` = 1;");

        PlayerStatsContent after = importer.ToContent();
        LevelStats[] changed = [.. after.LevelRows.Except(before.LevelRows)];
        LevelStats row = Assert.Single(changed);
        Assert.Equal(new LevelStats(5, 8, 1, 71, 72, 73, 21, 27), row);
        Assert.Equal(before.LevelStatsCount, after.LevelStatsCount);
        Assert.Equal(before.ClassLevelRows, after.ClassLevelRows);
        Assert.Equal(before.XpRows, after.XpRows);
        PlayerStatsImportReport report = importer.BuildReport();
        Assert.Equal((1, 1, 0), (report.MigrationStatements, report.UpdatedRows, report.UnmatchedUpdates));
    }

    [Fact]
    public void Migration_AcceptsTheAmpersandFormAndTrailingCommentsAndSeveralStatements()
    {
        PlayerStatsDumpImporter importer = Imported();
        Apply(importer, "20221022144513_world.sql", """
            UPDATE `player_classlevelstats` SET `basehp`=18, `basemana`=60 WHERE `class`=8 && `level`=1; -- Old HP 28 Mana 59
            UPDATE `player_classlevelstats` SET `basemana`=65 WHERE `class`=1 && `level`=3; -- Old HP 26 Mana 63
            UPDATE `player_levelstats` SET `sta`=40 WHERE `race`=1 && `class`=8 && `level`=2; # same cell family
            /* block comment with a ; semicolon inside */ UPDATE `player_levelstats` SET `str`=1, `agi`=2 WHERE `race`=1 && `class`=1 && `level`=3;
            """);

        PlayerStatsContent content = importer.ToContent();
        Assert.Equal(new ClassLevelStats(8, 1, 18, 60), content.FindClassLevelExact(8, 1));
        Assert.Equal(new ClassLevelStats(1, 3, 93, 65), content.FindClassLevelExact(1, 3));
        Assert.Equal(new LevelStats(1, 8, 2, 51, 52, 40, 54, 55), content.FindLevelExact(1, 8, 2));
        Assert.Equal(new LevelStats(1, 1, 3, 1, 2, 33, 34, 35), content.FindLevelExact(1, 1, 3));
        Assert.Equal((4, 4, 0), (importer.BuildReport().MigrationStatements, importer.BuildReport().UpdatedRows, importer.BuildReport().UnmatchedUpdates));
    }

    [Fact]
    public void Migration_UpdateThatMatchesNoRowIsReportedNotSilent()
    {
        PlayerStatsDumpImporter importer = Imported();
        Apply(importer, "20230212062229_world.sql", "UPDATE `player_levelstats` SET `inte`=1 WHERE `race`=2 && `class`=2 && `level`=59;");

        PlayerStatsImportReport report = importer.BuildReport();
        Assert.Equal((1, 0, 1), (report.MigrationStatements, report.UpdatedRows, report.UnmatchedUpdates));
        Assert.Contains(report.Warnings, w => w.Contains("20230212062229_world.sql", StringComparison.Ordinal) && w.Contains("matched no row", StringComparison.Ordinal));
    }

    [Fact]
    public void Migration_InsertsSniffedRateRowsFromAMultiRowStatementAndLaterRowsWin()
    {
        PlayerStatsDumpImporter importer = Imported();
        Apply(importer, "20260703210621_world.sql", """
            CREATE TABLE IF NOT EXISTS `player_crit_per_agility` (
              `class` tinyint(3) unsigned NOT NULL,
              `level` tinyint(3) unsigned NOT NULL,
              `rate` float NOT NULL DEFAULT '0',
              PRIMARY KEY (`class`,`level`)
            ) ENGINE=MyISAM DEFAULT CHARSET=utf8 COMMENT='Rates; it''s per agility point.';

            INSERT INTO `player_crit_per_agility` (`class`, `level`, `rate`) VALUES
            (1, 1, 4),
            (1, 2, 4.5),
            (1, 60, 20);
            """);
        Apply(importer, "20260711025640_world.sql", """
            INSERT INTO `player_crit_per_agility` (`class`, `level`, `rate`) VALUES (1, 2, 5.25);
            INSERT INTO `player_dodge_per_agility` (`class`, `level`, `rate`) VALUES (5, 14, 11.5);
            """);

        PlayerStatsContent content = importer.ToContent();
        Assert.Equal([new AgilityRateRow(1, 1, 4f), new AgilityRateRow(1, 2, 5.25f), new AgilityRateRow(1, 60, 20f)], content.CritPerAgility);
        Assert.Equal([new AgilityRateRow(5, 14, 11.5f)], content.DodgePerAgility);
    }

    [Fact]
    public void Migration_StatementsOnUntrackedTablesAreIgnoredAndOnTrackedTablesFailClosed()
    {
        PlayerStatsDumpImporter importer = Imported();
        Apply(importer, "ignored.sql", """
            UPDATE `creature_template` SET `level_min` = 5 WHERE `entry` = 1;
            DELETE FROM `item_template` WHERE `entry` = 3;
            INSERT INTO `player_levelstats_backup` VALUES (1,2,3);
            """);
        Assert.Equal(0, importer.BuildReport().MigrationStatements);

        Assert.Throws<NotSupportedException>(() => Apply(Imported(), "d.sql", "DELETE FROM `player_levelstats` WHERE `race` = 1;"));
        Assert.Throws<NotSupportedException>(() => Apply(Imported(), "t.sql", "TRUNCATE TABLE `player_xp_for_level`;"));
        Assert.Throws<NotSupportedException>(() => Apply(Imported(), "a.sql", "ALTER TABLE `player_levelstats` ADD COLUMN `x` int;"));
        Assert.Throws<NotSupportedException>(() => Apply(Imported(), "nw.sql", "UPDATE `player_levelstats` SET `spi` = 1;"));
        Assert.Throws<NotSupportedException>(() => Apply(Imported(), "expr.sql", "UPDATE `player_levelstats` SET `spi` = `spi` + 1 WHERE `race` = 1;"));
        Assert.Throws<NotSupportedException>(() => Apply(Imported(), "col.sql", "UPDATE `player_levelstats` SET `bogus` = 1 WHERE `race` = 1;"));
        Assert.Throws<NotSupportedException>(() => Apply(Imported(), "nocols.sql", "INSERT INTO `player_xp_for_level` VALUES (4, 2000);"));
        Assert.Throws<InvalidDataException>(() => Apply(Imported(), "range.sql", "UPDATE `player_levelstats` SET `spi` = 300 WHERE `race` = 1 AND `class` = 1 AND `level` = 1;"));
    }

    [Fact]
    public void MigrationDirectory_IsReplayedInFileNameOrderAndOnlyWorldFilesCount()
    {
        string dir = Path.Combine(Path.GetTempPath(), "arcanecore-migs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Written in reverse order: the later timestamp must still be applied last.
            File.WriteAllText(Path.Combine(dir, "20230101000000_world.sql"), Migration("UPDATE `player_levelstats` SET `str` = 99 WHERE `race` = 1 AND `class` = 1 AND `level` = 1;"));
            File.WriteAllText(Path.Combine(dir, "20200101000000_world.sql"), Migration("UPDATE `player_levelstats` SET `str` = 77, `agi` = 66 WHERE `race` = 1 AND `class` = 1 AND `level` = 1;"));
            File.WriteAllText(Path.Combine(dir, "20210101000000_logs.sql"), Migration("UPDATE `player_levelstats` SET `sta` = 1 WHERE `race` = 1 AND `class` = 1 AND `level` = 1;"));
            File.WriteAllText(Path.Combine(dir, "20220101000000_world.sql"), Migration("UPDATE `creature_template` SET `level_min` = 1 WHERE `entry` = 1;"));

            PlayerStatsDumpImporter importer = Imported();
            IReadOnlyList<string> applied = importer.ApplyMigrationDirectory(dir);

            Assert.Equal(["20200101000000_world.sql", "20230101000000_world.sql"], applied);
            Assert.Equal(new LevelStats(1, 1, 1, 99, 66, 13, 14, 15), importer.ToContent().FindLevelExact(1, 1, 1));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Content_LevelGapsUseTheNearestLowerLevelLikeTheReferenceFill()
    {
        PlayerStatsContent content = Imported().ToContent();
        Assert.Equal(new ClassLevelStats(8, 3, 83, 73), content.ClassLevel(8, 3));
        Assert.Equal(new ClassLevelStats(8, 3, 83, 73), content.ClassLevel(8, 40));
        Assert.Equal(new LevelStats(5, 8, 3, 91, 92, 93, 94, 95), content.Level(5, 8, 59));
        Assert.Null(content.ClassLevel(2, 10));
        Assert.Null(content.Level(2, 2, 10));
        Assert.Null(content.Level(1, 1, 0));
    }

    [Fact]
    public void Validate_ReportsTheProblemsTheReferenceExitsOn()
    {
        PlayerStatsContent content = Imported().ToContent();

        // Level 1 stats exist for (1,1), (1,8), (5,8) and level 1 health for classes 1 and 8; maxLevel 4 needs
        // XP rows 1-3 (present) but no rate tables exist in the dump.
        IReadOnlyList<string> problems = content.Validate([(1, 1), (1, 8), (5, 8)], maxLevel: 4);
        Assert.Equal(
        [
            "missing crit per agility rate for class 1 and level 1",
            "missing crit per agility rate for class 1 and level 4",
            "missing dodge per agility rate for class 1 and level 1",
            "missing dodge per agility rate for class 1 and level 4",
            "missing crit per agility rate for class 8 and level 1",
            "missing crit per agility rate for class 8 and level 4",
            "missing dodge per agility rate for class 8 and level 1",
            "missing dodge per agility rate for class 8 and level 4",
        ], problems);

        // A playable pair with no level 1 stats, a class without health data, and a missing XP level.
        IReadOnlyList<string> more = content.Validate([(3, 8), (1, 2)], maxLevel: 5);
        Assert.Contains("race 3 class 8 level 1 does not have stats data", more);
        Assert.Contains("race 1 class 2 level 1 does not have stats data", more);
        Assert.Contains("class 2 level 1 does not have health/mana data", more);
        Assert.Contains("level 4 does not have xp for next level data", more);
        Assert.DoesNotContain(more, p => p.StartsWith("class 8 level 1 does not", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PassesForACompleteSet()
    {
        PlayerStatsDumpImporter importer = Imported();
        Apply(importer, "rates.sql", """
            INSERT INTO `player_crit_per_agility` (`class`, `level`, `rate`) VALUES (1, 1, 4), (1, 4, 5), (8, 1, 6), (8, 4, 7);
            INSERT INTO `player_dodge_per_agility` (`class`, `level`, `rate`) VALUES (1, 1, 4), (1, 4, 5), (8, 1, 6), (8, 4, 7);
            """);
        Assert.Empty(importer.ToContent().Validate([(1, 1), (1, 8), (5, 8)], maxLevel: 4));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FreshWorldDatabase_ImportAndLoad_RoundTrip(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        PlayerStatsDumpImporter importer = Imported();
        Apply(importer, "m.sql", """
            UPDATE `player_levelstats` SET `inte` = 21, `spi` = 27 WHERE `race` = 5 AND `class` = 8 AND `level` = 1;
            INSERT INTO `player_crit_per_agility` (`class`, `level`, `rate`) VALUES (1, 1, 4.25), (1, 60, 20.5);
            INSERT INTO `player_dodge_per_agility` (`class`, `level`, `rate`) VALUES (1, 1, 3.5);
            """);

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            PlayerStatsImportReport report = await importer.WriteAsync(db, replace: false);
            Assert.Equal((6, 9, 3, 2, 1), (report.ClassLevelStats, report.LevelStats, report.XpRows, report.CritRows, report.DodgeRows));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            PlayerStatsContent loaded = await new EfPlayerStatsContentStore(db).LoadAsync();
            PlayerStatsContent expected = importer.ToContent();
            Assert.Equal(expected.ClassLevelRows, loaded.ClassLevelRows);
            Assert.Equal(expected.LevelRows, loaded.LevelRows);
            Assert.Equal(expected.XpRows, loaded.XpRows);
            Assert.Equal(expected.CritPerAgility, loaded.CritPerAgility);
            Assert.Equal(expected.DodgePerAgility, loaded.DodgePerAgility);
            Assert.Equal(new LevelStats(5, 8, 1, 71, 72, 73, 21, 27), loaded.FindLevelExact(5, 8, 1));
        }

        // A second import without "replace" collides on the keys and rolls back; the first rows survive.
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            var second = new PlayerStatsDumpImporter();
            second.Read(new StringReader("INSERT INTO `player_xp_for_level` (`lvl`,`xp_for_next_level`) VALUES (10,5),(1,999);"));
            await Assert.ThrowsAnyAsync<Exception>(() => second.WriteAsync(db, replace: false));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            PlayerStatsContent loaded = await new EfPlayerStatsContentStore(db).LoadAsync();
            Assert.Equal(401u, loaded.XpForNextLevel(1));
            Assert.Null(loaded.XpForNextLevel(10));
            Assert.Equal(6, loaded.ClassLevelStatsCount);
        }

        // A replace import empties every table first.
        var third = new PlayerStatsDumpImporter();
        third.Read(new StringReader("INSERT INTO `player_xp_for_level` (`lvl`,`xp_for_next_level`) VALUES (1,555);"));
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await third.WriteAsync(db, replace: true);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            PlayerStatsContent loaded = await new EfPlayerStatsContentStore(db).LoadAsync();
            Assert.Equal((0, 0, 1, 0, 0), (loaded.ClassLevelStatsCount, loaded.LevelStatsCount, loaded.XpRowCount, loaded.CritPerAgility.Count, loaded.DodgePerAgility.Count));
            Assert.Equal(555u, loaded.XpForNextLevel(1));
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
