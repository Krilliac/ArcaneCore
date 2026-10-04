using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The creature AI world-schema step (<see cref="CreatureAiDataModule"/>): EventAI tables,
/// <c>creature_template.AIName</c> and <c>creature_movement.Run</c>, through the dump importer and
/// the store on every provider. Dump rows are hand-written in the cmangos-classic column layout
/// (doc/EventAI.txt names); no GPL rows are copied.
/// </summary>
public sealed class CreatureAiDataTests : IAsyncLifetime
{
    private const string CMangosAiDump = """
        INSERT INTO `creature_template` (`Entry`,`Name`,`SubName`,`MinLevel`,`MaxLevel`,`Faction`,`AIName`) VALUES
        (910001,'Scripted Wolf','',5,5,32,'EventAI'),(910002,'Plain Bear','',6,6,32,'');
        INSERT INTO `creature` (`guid`,`id`,`map`,`position_x`,`position_y`,`position_z`,`orientation`,`spawntimesecsmin`,`spawntimesecsmax`,`spawndist`,`MovementType`) VALUES
        (21,910001,0,10,20,30,0,120,120,0,2);
        INSERT INTO `creature_movement` (`Id`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`Run`) VALUES
        (21,1,11,21,30,100,0,0),(21,2,15,25,30,100,0,1);
        INSERT INTO `creature_ai_scripts` (`id`,`creature_id`,`event_type`,`event_inverse_phase_mask`,`event_chance`,`event_flags`,`event_param1`,`event_param2`,`event_param3`,`event_param4`,`action1_type`,`action1_param1`,`action1_param2`,`action1_param3`,`action2_type`,`action2_param1`,`action2_param2`,`action2_param3`,`action3_type`,`action3_param1`,`action3_param2`,`action3_param3`,`comment`) VALUES
        (9100101,910001,4,0,50,0,0,0,0,0,1,-910001,-910002,0,0,0,0,0,0,0,0,0,'Scripted Wolf - Random Say on Aggro'),
        (9100102,910001,2,2,100,1,15,0,0,0,25,0,0,0,22,1,0,0,0,0,0,0,'Scripted Wolf - Flee at 15% HP');
        INSERT INTO `creature_ai_texts` (`entry`,`content_default`,`sound`,`type`,`language`,`emote`,`comment`) VALUES
        (-910001,'Grr!',0,0,0,0,'aggro 1'),(-910002,'%s howls.',0,2,0,1,'aggro 2'),(5,'not negative',0,0,0,0,NULL);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void WorldStep_IsTheCreatureAiChanges()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == CreatureAiDataModule.Version);
        Assert.Equal(["creature_ai_scripts", "creature_ai_texts"], step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.Equal(
            [("creature_template", "AIName"), ("creature_movement", "Run")],
            step.Changes.OfType<AddColumnChange>().Select(c => (c.Table, c.Column)));
        Assert.Equal(8, CreatureAiDataModule.ReservedVersion);
    }

    [Fact]
    public void Importer_ReadsEventAiRows_AndSkipsNonNegativeTexts()
    {
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader(CMangosAiDump));
        CreatureImportReport report = importer.BuildReport();
        Assert.Equal((2, 2), (report.AiEvents, report.AiTexts));
        Assert.Contains(report.Warnings, w => w.Contains("entry 5", StringComparison.Ordinal));

        (IReadOnlyCollection<CreatureAiScriptRow> scripts, IReadOnlyCollection<CreatureAiTextRow> texts) = importer.AiSnapshot();
        CreatureAiScriptRow flee = Assert.Single(scripts, s => s.Id == 9100102);
        Assert.Equal((910001u, (byte)2, 2u, (byte)100, (byte)1, 15), (flee.CreatureId, flee.EventType, flee.EventInversePhaseMask, flee.EventChance, flee.EventFlags, flee.EventParam1));
        Assert.Equal(((byte)25, (byte)22, 1), (flee.Action1Type, flee.Action2Type, flee.Action2Param1));
        Assert.Equal("Scripted Wolf - Flee at 15% HP", flee.Comment);
        Assert.Equal([-910002, -910001], texts.Select(t => t.Entry).Order());
    }

    [Fact]
    public void Importer_ReadsTheAiNameColumnOfAClassicDbPositionalRow()
    {
        // classic-db z2815 creature_template has AIName char(64) (column 87) and 4,325 rows say 'EventAI': the retail selector needs no bridge.
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader("""
            CREATE TABLE `creature_template` (
              `Entry` mediumint(8) unsigned NOT NULL DEFAULT '0',
              `Name` char(100) NOT NULL DEFAULT '',
              `MinLevel` tinyint(3) unsigned NOT NULL DEFAULT '1',
              `MaxLevel` tinyint(3) unsigned NOT NULL DEFAULT '1',
              `AIName` char(64) NOT NULL DEFAULT '',
              PRIMARY KEY (`Entry`)
            ) ENGINE=MyISAM;
            INSERT INTO `creature_template` VALUES (910001,'Scripted Wolf',5,5,'EventAI'),(910002,'Plain Bear',6,6,'');
            """));

        CreatureTemplateRow[] templates = [.. importer.Snapshot().Templates];
        Assert.Equal("EventAI", Assert.Single(templates, t => t.Entry == 910001).AIName);
        Assert.Equal(string.Empty, Assert.Single(templates, t => t.Entry == 910002).AIName);
    }

    [Fact]
    public void Importer_WarnsOnceAboutVMangosAiEvents()
    {
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader("""
            INSERT INTO `creature_ai_events` (`id`,`creature_id`,`condition_id`,`event_type`) VALUES (1,2,0,4),(2,2,0,1);
            """));
        Assert.Single(importer.BuildReport().Warnings, w => w.Contains("creature_ai_events", StringComparison.Ordinal));
        Assert.Equal(0, importer.BuildReport().AiEvents);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EventAiImport_RoundTripsThroughTheStore(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader(CMangosAiDump));
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            CreatureImportReport report = await importer.WriteAsync(db, replace: false);
            Assert.Equal((2, 1, 2, 2, 2), (report.Templates, report.Spawns, report.Waypoints, report.AiEvents, report.AiTexts));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            CreatureContent content = await new EfCreatureDataStore(db).LoadAsync();
            Assert.Equal("EventAI", content.FindTemplate(910001)!.AIName);
            Assert.Equal(string.Empty, content.FindTemplate(910002)!.AIName);
            Assert.Equal([false, true], content.GetWaypoints(21).Select(p => p.Run));

            IReadOnlyList<CreatureAiEvent> events = content.Ai.GetEvents(910001);
            Assert.Equal([9100101u, 9100102u], events.Select(e => e.Id).Order());
            CreatureAiEvent say = events.Single(e => e.Id == 9100101);
            Assert.Equal(((byte)4, (byte)50), (say.EventType, say.Chance));
            Assert.Equal(new CreatureAiAction(1, -910001, -910002, 0), say.Action1);
            Assert.Empty(content.Ai.GetEvents(910002));
            Assert.Equal(new CreatureAiText(-910002, "%s howls.", 2, 0, 1), content.Ai.FindText(-910002));
        }

        // A replace import empties the EventAI tables too.
        var second = new CreatureDumpImporter();
        second.Read(new StringReader("INSERT INTO `creature_template` (`Entry`,`Name`,`MinLevel`,`MaxLevel`) VALUES (5,'Only',1,1);"));
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await second.WriteAsync(db, replace: true);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            CreatureContent content = await new EfCreatureDataStore(db).LoadAsync();
            Assert.Empty(content.Ai.GetEvents(910001));
            Assert.Null(content.Ai.FindText(-910001));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PreviousWorldVersion_GainsTheAiColumns_KeepingRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<CreatureTemplateRow>().Add(new CreatureTemplateRow { Entry = 1, Name = "Before" });
            db.Set<CreatureSpawnRow>().Add(new CreatureSpawnRow { Guid = 1, Entry = 1, MapId = 0 });
            db.Set<CreatureMovementRow>().Add(new CreatureMovementRow { SpawnGuid = 1, Point = 1, X = 1 });
            await db.SaveChangesAsync();

            // Recreate a database from before this step: no AI tables or columns, version below it.
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            // Fixed identifiers only (no input reaches this DDL).
            string[] ddl =
            [
                $"DROP TABLE {sql.DelimitIdentifier("creature_ai_scripts")}",
                $"DROP TABLE {sql.DelimitIdentifier("creature_ai_texts")}",
                $"ALTER TABLE {sql.DelimitIdentifier("creature_template")} DROP COLUMN {sql.DelimitIdentifier("AIName")}",
                $"ALTER TABLE {sql.DelimitIdentifier("creature_movement")} DROP COLUMN {sql.DelimitIdentifier("Run")}",
            ];
            foreach (string statement in ddl)
            {
                await db.Database.ExecuteSqlRawAsync(statement);
            }

            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, CreatureAiDataModule.Version - 1));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);

            CreatureContent content = await new EfCreatureDataStore(db).LoadAsync();
            Assert.Equal(("Before", string.Empty), (content.FindTemplate(1)!.Name, content.FindTemplate(1)!.AIName));
            Assert.False(Assert.Single(content.GetWaypoints(1)).Run);
            Assert.Empty(content.Ai.GetEvents(1));

            db.Set<CreatureAiTextRow>().Add(new CreatureAiTextRow { Entry = -1, Content = "after" });
            await db.SaveChangesAsync();
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
