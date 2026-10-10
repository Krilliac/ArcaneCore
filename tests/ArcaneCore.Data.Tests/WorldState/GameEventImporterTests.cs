using System.IO.Compression;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.WorldState;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.WorldState;

/// <summary>
/// <see cref="GameEventDumpImporter"/>: both dialects by column name, malformed input rejected before any write, the CLI
/// reaches it, and the real classic-db dump (skipped, visibly, without it) has the counts the design was built on.
/// </summary>
public sealed class GameEventImporterTests : IDisposable
{
    // classic-db z2815 layout (trimmed): schedule_type, linkedTo, game_event_time, creature_data.modelid; columns deliberately reordered.
    private const string CMangosDump = """
        CREATE TABLE `game_event` (
          `description` varchar(255) DEFAULT NULL,
          `entry` mediumint unsigned NOT NULL,
          `schedule_type` int NOT NULL DEFAULT '0',
          `occurence` bigint unsigned NOT NULL DEFAULT '86400',
          `length` bigint unsigned NOT NULL DEFAULT '43200',
          `holiday` mediumint unsigned NOT NULL DEFAULT '0',
          `linkedTo` mediumint unsigned NOT NULL DEFAULT '0',
          `extra_column_nobody_reads` int,
          PRIMARY KEY (`entry`)
        ) ENGINE=MyISAM;
        INSERT INTO `game_event` VALUES ('Midsummer Fire Festival',1,1,525600,20160,341,0,9),('Hallow\'s End',12,1,525600,21600,324,0,9),('Child event',24,1,525600,60,0,12,9),('Easter',9,13,524160,7200,181,0,9);
        CREATE TABLE `game_event_time` (`entry` mediumint unsigned NOT NULL, `start_time` datetime NOT NULL, `end_time` datetime NOT NULL, PRIMARY KEY (`entry`));
        INSERT INTO `game_event_time` VALUES (1,'2020-06-21 20:00:00','2030-12-31 22:59:59'),(12,'2020-10-18 00:00:00','2030-12-31 22:59:59');
        CREATE TABLE `game_event_creature` (`guid` int unsigned NOT NULL, `event` smallint NOT NULL DEFAULT '0', PRIMARY KEY (`guid`,`event`));
        INSERT INTO `game_event_creature` VALUES (100,1),(100,-12),(101,12);
        CREATE TABLE `game_event_gameobject` (`guid` int unsigned NOT NULL, `event` smallint NOT NULL DEFAULT '0', PRIMARY KEY (`guid`,`event`));
        INSERT INTO `game_event_gameobject` VALUES (5000,1),(5001,-12);
        CREATE TABLE `game_event_creature_data` (`guid` int unsigned NOT NULL, `entry_id` mediumint unsigned NOT NULL, `modelid` mediumint unsigned NOT NULL, `equipment_id` mediumint unsigned NOT NULL, `spell_start` mediumint unsigned NOT NULL, `spell_end` mediumint unsigned NOT NULL, `event` smallint unsigned NOT NULL, PRIMARY KEY (`guid`,`event`));
        INSERT INTO `game_event_creature_data` VALUES (100,5,6,7,8,9,1);
        CREATE TABLE `game_event_quest` (`quest` mediumint unsigned NOT NULL, `event` smallint unsigned NOT NULL, PRIMARY KEY (`quest`,`event`));
        INSERT INTO `game_event_quest` VALUES (7001,1),(7001,12);
        CREATE TABLE `game_event_mail` (`event` smallint NOT NULL, `raceMask` mediumint unsigned NOT NULL, `quest` mediumint unsigned NOT NULL, `mailTemplateId` mediumint unsigned NOT NULL, `senderEntry` mediumint unsigned NOT NULL, PRIMARY KEY (`event`,`raceMask`,`quest`));
        INSERT INTO `game_event_mail` VALUES (17,255,0,171,16285);
        """;

    // vmangos layout: dates, hardcoded, disabled and the patch range on game_event; display_id and patch on creature_data; patch_min on quests.
    private const string VMangosDump = """
        CREATE TABLE `game_event` (`entry` int unsigned NOT NULL, `start_time` timestamp NULL, `end_time` timestamp NULL, `occurence` bigint unsigned NOT NULL, `length` bigint unsigned NOT NULL, `holiday` int unsigned NOT NULL, `description` varchar(255), `hardcoded` tinyint NOT NULL, `disabled` tinyint NOT NULL, `patch_min` tinyint NOT NULL, `patch_max` tinyint NOT NULL, PRIMARY KEY (`entry`));
        INSERT INTO `game_event` VALUES (1,'2020-06-21 20:00:00','2030-12-31 22:59:59',525600,20160,341,'Midsummer',0,0,0,10),(2,'2020-12-16 23:00:00','2030-12-31 22:59:59',525600,27360,141,'Winter Veil',1,1,0,10);
        CREATE TABLE `game_event_creature_data` (`guid` int unsigned NOT NULL, `patch` tinyint NOT NULL, `entry_id` int unsigned NOT NULL, `display_id` int unsigned NOT NULL, `equipment_id` int unsigned NOT NULL, `spell_start` int unsigned NOT NULL, `spell_end` int unsigned NOT NULL, `event` smallint unsigned NOT NULL, PRIMARY KEY (`guid`,`event`,`patch`));
        INSERT INTO `game_event_creature_data` VALUES (100,0,1,2,3,4,5,8),(100,6,10,20,30,40,50,8),(100,11,99,99,99,99,99,8),(101,7,0,0,0,27654,0,8);
        CREATE TABLE `game_event_quest` (`quest` int unsigned NOT NULL, `event` smallint unsigned NOT NULL, `patch_min` tinyint NOT NULL, PRIMARY KEY (`quest`,`event`));
        INSERT INTO `game_event_quest` VALUES (8548,10,0),(8572,10,11);
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-gevimport-" + Guid.NewGuid().ToString("N"));

    public GameEventImporterTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static GameEventDumpImporter Read(string dump)
    {
        var importer = new GameEventDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }

    [Fact]
    public void CMangosDump_IsReadByColumnName_NotPosition()
    {
        GameEventContent content = Read(CMangosDump).BuildContent();

        Assert.Equal([1u, 9u, 12u, 24u], content.Events.Select(e => e.Entry));
        GameEventRecord midsummer = content.Events[0];
        Assert.Equal((1, 525600u, 20160u, 341u, 0u, "Midsummer Fire Festival"), (midsummer.ScheduleType, midsummer.OccurenceMinutes, midsummer.LengthMinutes, midsummer.Holiday, midsummer.LinkedTo, midsummer.Description));
        Assert.Equal(13, content.Events.Single(e => e.Entry == 9).ScheduleType);
        Assert.Equal(12u, content.Events.Single(e => e.Entry == 24).LinkedTo);
        Assert.Equal("Hallow's End", content.Events.Single(e => e.Entry == 12).Description);
        Assert.All(content.Events, e => Assert.Null(e.StartTime)); // cmangos dates live in game_event_time

        Assert.Equal([new GameEventTimeRecord(1, "2020-06-21 20:00:00", "2030-12-31 22:59:59"), new GameEventTimeRecord(12, "2020-10-18 00:00:00", "2030-12-31 22:59:59")], content.Times);
        Assert.Equal([(100u, -12), (100u, 1), (101u, 12)], content.Creatures.Select(c => (c.Guid, c.Event)));
        Assert.Equal([(5001u, -12), (5000u, 1)], content.GameObjects.Select(c => (c.Guid, c.Event)));
        Assert.Equal(new GameEventCreatureDataRecord(100, 1, 5, 6, 7, 8, 9), Assert.Single(content.CreatureData));
        Assert.Equal([(7001u, 1), (7001u, 12)], content.Quests.Select(q => (q.Quest, q.Event)));
        Assert.Equal(new GameEventMailRecord(17, 255, 0, 171, 16285), Assert.Single(content.Mails));
    }

    [Fact]
    public void VMangosDump_ReadsDatesFlagsAndPatchRows()
    {
        GameEventImporterReport(out GameEventContent content, out GameEventImportReport report, VMangosDump);

        GameEventRecord midsummer = content.Events[0];
        Assert.Equal(("2020-06-21 20:00:00", "2030-12-31 22:59:59"), (midsummer.StartTime, midsummer.EndTime));
        Assert.Equal(1, midsummer.ScheduleType); // a vmangos table has no schedule type: every event is a date event
        Assert.False(midsummer.Hardcoded);
        Assert.True(content.Events[1].Hardcoded);
        Assert.True(content.Events[1].Disabled);
        Assert.Empty(content.Times);

        // per (guid, event) the highest patch not above 10 wins; patch 11 is skipped (and counted); display_id is the model
        GameEventCreatureDataRecord first = content.CreatureData.Single(d => d.Guid == 100);
        Assert.Equal((10u, 20u, 30u, 40u, 50u), (first.EntryId, first.ModelId, first.EquipmentId, first.SpellStart, first.SpellEnd));
        Assert.Equal(27654u, content.CreatureData.Single(d => d.Guid == 101).SpellStart);
        Assert.Equal(2, report.SkippedRows); // the patch 11 creature data row and the quest with patch_min 11

        Assert.Equal([8548u], content.Quests.Select(q => q.Quest)); // patch_min 11 is not for 1.12
    }

    private static void GameEventImporterReport(out GameEventContent content, out GameEventImportReport report, string dump)
    {
        GameEventDumpImporter importer = Read(dump);
        content = importer.BuildContent();
        report = importer.BuildReport();
    }

    [Fact]
    public void LaterRowsReplaceEarlierOnes_WithTheSameKey()
    {
        var importer = new GameEventDumpImporter();
        importer.Read(new StringReader(CMangosDump));
        importer.Read(new StringReader(
            "CREATE TABLE `game_event_quest` (`quest` mediumint unsigned NOT NULL, `event` smallint unsigned NOT NULL);\nINSERT INTO `game_event_quest` VALUES (7001,1),(9999,3);"));

        Assert.Equal([(7001u, 1), (9999u, 3), (7001u, 12)], importer.BuildContent().Quests.Select(q => (q.Quest, q.Event))); // ordered by event
        Assert.Equal(3, importer.BuildReport().Quests);
    }

    [Theory]
    [InlineData("CREATE TABLE `game_event_creature` (`guid` int, `event` smallint);\nINSERT INTO `game_event_creature` VALUES (1,x);", "not a valid")]
    [InlineData("CREATE TABLE `game_event_creature` (`guid` int);\nINSERT INTO `game_event_creature` VALUES (1);", "no column `event`")]
    [InlineData("CREATE TABLE `game_event_creature` (`guid` int, `event` smallint);\nINSERT INTO `game_event_creature` (`guid`,`event`) VALUES (1,2,3);", "")]
    [InlineData("CREATE TABLE `game_event` (`entry` int, `length` int);\nINSERT INTO `game_event` VALUES (1,2);", "no column `occurence`")]
    public void MangledInput_IsRejected_BeforeAnythingIsWritten(string dump, string message)
    {
        Exception ex = Assert.ThrowsAny<Exception>(() => Read(dump));
        Assert.True(ex is ImportSchemaException or FormatException or InvalidDataException, ex.GetType().Name);
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInsertWithoutACreateTable_AndNoColumnList_IsRejected()
    {
        Assert.ThrowsAny<Exception>(() => Read("INSERT INTO `game_event_creature` VALUES (1,2);"));
    }

    [Fact]
    public async Task TheCommandLine_ImportsTheTables_AndReplaceEmptiesThemFirst()
    {
        string dump = Path.Combine(_directory, "dump.sql");
        File.WriteAllText(dump, CMangosDump);
        string db = Path.Combine(_directory, "world.db");
        var output = new StringWriter();
        var error = new StringWriter();

        int code = await ContentImporterCli.RunAsync(["import", dump, "--database", db], output, error, CancellationToken.None);

        Assert.True(code == 0, error + output.ToString());
        Assert.Matches(@"game_event\s+4", output.ToString());
        Assert.Matches(@"game_event_creature\s+3", output.ToString());
        Assert.Matches(@"game_event_mail\s+1", output.ToString());
        await using (WorldDbContext world = new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={db};Pooling=False").Options))
        {
            GameEventContent loaded = await new EfGameEventDataStore(world).LoadAsync();
            Assert.Equal(4, loaded.Events.Count);
            Assert.Equal(2, loaded.Times.Count);
            Assert.Equal(3, loaded.Creatures.Count);
        }

        // a second import without --replace hits the primary keys and changes nothing; with it the old rows go first
        Assert.Equal(ExitCodes.Database, await ContentImporterCli.RunAsync(["import", dump, "--database", db], new StringWriter(), new StringWriter(), CancellationToken.None));
        string smaller = "CREATE TABLE `game_event` (`entry` int, `occurence` int, `length` int);\nINSERT INTO `game_event` VALUES (7,100,10);";
        File.WriteAllText(Path.Combine(_directory, "smaller.sql"), smaller);
        Assert.Equal(0, await ContentImporterCli.RunAsync(["import", Path.Combine(_directory, "smaller.sql"), "--database", db, "--replace"], new StringWriter(), new StringWriter(), CancellationToken.None));
        await using WorldDbContext after = new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={db};Pooling=False").Options);
        GameEventContent replaced = await new EfGameEventDataStore(after).LoadAsync();
        Assert.Equal([7u], replaced.Events.Select(e => e.Entry));
        Assert.Empty(replaced.Creatures);
        Assert.Equal(7u, Assert.Single(replaced.Events).Entry);
    }

    [ClassicDbDumpFact]
    public void RealDump_HasTheRowCountsTheDesignRestsOn_AndTheOrphansAreDroppedAndCounted()
    {
        using FileStream file = File.OpenRead(ClassicDbDumpFactAttribute.DumpPath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);

        // One pass: the importer's tables, plus the guid columns of creature and gameobject for the orphan accounting.
        var creatureGuids = new HashSet<uint>();
        var gameObjectGuids = new HashSet<uint>();
        var registry = MySqlDumpReader.NewTableRegistry();
        foreach (object item in new MySqlDumpReader(reader, registry).Read())
        {
            if (item is not DumpRow row)
            {
                continue;
            }

            if (row.Table.Equals("creature", StringComparison.OrdinalIgnoreCase) && row.TryGet(out string? cg, "guid"))
            {
                creatureGuids.Add(uint.Parse(cg!, System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (row.Table.Equals("gameobject", StringComparison.OrdinalIgnoreCase) && row.TryGet(out string? og, "guid"))
            {
                gameObjectGuids.Add(uint.Parse(og!, System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        // second pass for the importer (the first stream is consumed)
        using FileStream file2 = File.OpenRead(ClassicDbDumpFactAttribute.DumpPath);
        using var gzip2 = new GZipStream(file2, CompressionMode.Decompress);
        using var reader2 = new StreamReader(gzip2);
        var importer = new GameEventDumpImporter();
        importer.Read(reader2);
        GameEventContent content = importer.BuildContent();

        Assert.Equal(67, content.Events.Count);
        Assert.Equal(38, content.Times.Count);
        // 3219 and 12274 rows in the dump; the 33 and 1126 whose guid is in no spawn table are dropped (classic-db Updates/4498)
        Assert.Equal(3186, content.Creatures.Count);
        Assert.Equal(11148, content.GameObjects.Count);
        Assert.Equal((33, 1126), (importer.BuildReport().OrphanCreatureRows, importer.BuildReport().OrphanGameObjectRows));
        Assert.Equal(977, content.CreatureData.Count);
        Assert.Equal(61, content.Quests.Count);
        Assert.Single(content.Mails);
        Assert.Equal(108, content.Creatures.Count(c => c.Event < 0));
        Assert.Equal(2, content.GameObjects.Count(c => c.Event < 0));

        // schedule types as designed: 26 serverside, 36 date, 3 yearly, 1 lunar new year, 1 Easter
        IReadOnlyDictionary<int, int> types = content.Events.GroupBy(e => e.ScheduleType).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal((26, 36, 3, 1, 1), (types[0], types[1], types[11], types[12], types[13]));
        Assert.Equal(2, content.Events.Count(e => e.LinkedTo == 12));

        // orphans: none is left for the world's loader to skip
        Assert.Equal(0, content.Creatures.Count(c => !creatureGuids.Contains(c.Guid)));
        Assert.Equal(0, content.GameObjects.Count(g => !gameObjectGuids.Contains(g.Guid)));
    }
}
