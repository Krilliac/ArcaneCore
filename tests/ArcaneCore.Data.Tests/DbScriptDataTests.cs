using System.Text;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The quest, gossip and event DB script world-schema step (<see cref="DbScriptDataModule"/>, world 42): the four cmangos script tables,
/// ScriptDev2's <c>script_waypoint</c> and the script id columns of <c>quest_template</c>, <c>gossip_menu</c> and <c>gossip_menu_option</c>,
/// imported by name, stored on every provider, loaded by the stores, reached by the content importer's <c>refresh</c>, and added to a world
/// from before the step. The rows are real classic-db z2815 tuples (the quest and option texts shortened or left out): quest 2843's start
/// script, quest 8984's end script 9028 (its first two steps), gossip script 21 (QUEST_EXPLORED 6981, run by option 1 of menu 21) and 1405
/// (run by menu 1405's text), event script 364 (Gazban), and the first three points of Ruul Snowhoof's escort path.
/// </summary>
public sealed class DbScriptDataTests : IAsyncLifetime
{
    private const string ScriptColumns =
        "`id`,`delay`,`priority`,`command`,`datalong`,`datalong2`,`datalong3`,`buddy_entry`,`search_radius`,`data_flags`,"
        + "`dataint`,`dataint2`,`dataint3`,`dataint4`,`datafloat`,`x`,`y`,`z`,`o`,`speed`,`condition_id`,`comments`";

    private const string Scripts = $"""
        INSERT INTO `dbscripts_on_quest_start` ({ScriptColumns}) VALUES (2843,10000,0,7,2843,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'');
        INSERT INTO `dbscripts_on_quest_end` ({ScriptColumns}) VALUES
        (9028,1000,0,29,2,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'Staffron - Remove NPC Flag Questgiver'),
        (9028,2000,0,10,16110,40000,0,0,0,0,0,0,0,0,0,95.6559,-1713.36,220.826,4.26772,0,0,'Summon Annalise Lerent');
        INSERT INTO `dbscripts_on_gossip` ({ScriptColumns}) VALUES
        (21,0,0,7,6981,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'quest complete 6981'),
        (1405,0,0,8,8612,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'give killcredit - Screecher Spirit');
        INSERT INTO `dbscripts_on_event` ({ScriptColumns}) VALUES
        (364,0,0,10,2624,90000,0,0,0,0,0,0,0,0,0,-12179.4,644.22,-67.1,5.18,0,0,'spawn Gazban'),
        (364,1000,0,22,14,0,0,2624,400,0,0,0,0,0,0,0,0,0,0,0,0,'change faction to hostile'),
        (364,2000,0,26,0,0,0,2624,400,3,0,0,0,0,0,0,0,0,0,0,0,'attacks player');
        INSERT INTO `script_waypoint` (`Entry`,`PathId`,`Point`,`PositionX`,`PositionY`,`PositionZ`,`Orientation`,`WaitTime`,`ScriptId`,`Comment`) VALUES
        (12818,0,1,3347.35,-694.701,159.926,0,0,0,''),(12818,0,2,3371.25,-685.893,159.882,0,1000,0,''),(12818,0,3,3381.88,-676.531,160.637,0,0,0,'');
        """;

    private const string QuestsAndGossip = """
        INSERT INTO `quest_template` (`entry`,`Method`,`MinLevel`,`QuestLevel`,`RequiredRaces`,`QuestFlags`,`SpecialFlags`,`PrevQuestId`,`Title`,`RewMoneyMaxLevel`,`StartScript`,`CompleteScript`) VALUES
        (2843,2,20,35,178,2,2,2842,'Gnomer-gooooone!',0,2843,0),(8984,2,1,60,178,8,0,8983,'The Source Revealed',0,0,9028);
        INSERT INTO `gossip_menu` (`entry`,`text_id`,`script_id`,`condition_id`) VALUES (1405,2039,1405,0);
        INSERT INTO `gossip_menu_option` (`menu_id`,`id`,`option_icon`,`option_text`,`option_broadcast_text`,`option_id`,`npc_option_npcflag`,`action_menu_id`,`action_poi_id`,`action_script_id`,`box_coded`,`box_money`,`box_text`,`box_broadcast_text`,`condition_id`) VALUES
        (21,1,0,'Can you tell me about this shard?',0,1,1,20025,0,21,0,0,NULL,0,316);
        """;

    private readonly TestDatabases _databases = new();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-dbscripts-" + Guid.NewGuid().ToString("N"));

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void WorldStep42_IsTheFiveTables_AndTheFourScriptIdColumns()
    {
        Assert.Equal(42, DbScriptDataModule.Version);
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == DbScriptDataModule.Version);
        Assert.Equal(["dbscripts_on_quest_start", "dbscripts_on_quest_end", "dbscripts_on_gossip", "dbscripts_on_event", "script_waypoint"],
            step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.Equal([("quest_template", "StartScript"), ("quest_template", "CompleteScript"), ("gossip_menu", "script_id"), ("gossip_menu_option", "action_script_id")],
            step.Changes.OfType<AddColumnChange>().Select(c => (c.Table, c.Column)));
        Assert.True(WorldDbContext.Schema.CurrentVersion >= DbScriptDataModule.Version);
    }

    [Fact]
    public void CreatureImporter_ReadsTheFourNamespacesAndThePath_InDumpOrder()
    {
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader(Scripts));

        CreatureImportReport report = importer.BuildReport();
        Assert.Equal((1 + 2 + 2 + 3, 3), (report.DbScriptSteps, report.ScriptWaypoints));
        Assert.Equal(3, report.DbScriptTables["dbscripts_on_event"]);
        Assert.Equal([10u, 22u, 26u], importer.DbScripts.ScriptRows.OfType<EventScriptRow>().OrderBy(r => r.Ordinal).Select(r => r.Command));
        Assert.Equal([0u, 1u, 2u], importer.DbScripts.ScriptRows.OfType<EventScriptRow>().Select(r => r.Ordinal).Order());
        Assert.Equal((2624u, 400u, 3u), importer.DbScripts.ScriptRows.OfType<EventScriptRow>().Where(r => r.Ordinal == 2).Select(r => (r.BuddyEntry, r.SearchRadius, r.DataFlags)).Single());
        Assert.Equal(1000u, importer.DbScripts.WaypointRowsToWrite.Single(w => w.Point == 2).WaitTimeMs);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ScriptsAndScriptIds_RoundTripIntoTheContent(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            var creatures = new CreatureDumpImporter();
            creatures.Read(new StringReader(Scripts));
            await creatures.WriteAsync(db, replace: false);
            var questImporter = new ItemQuestDumpImporter();
            questImporter.Read(new StringReader(QuestsAndGossip));
            await questImporter.WriteAsync(db, replace: false);
            var npcs = new NpcDumpImporter();
            npcs.Read(new StringReader(QuestsAndGossip));
            Assert.Equal((1, 1, 0), (npcs.BuildReport().GossipMenus, npcs.BuildReport().GossipOptions, npcs.BuildReport().Skipped));
            await npcs.WriteAsync(db, replace: false);
        }

        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        CreatureContent content = await new EfCreatureDataStore(read).LoadAsync();
        DbScriptCatalog scripts = content.Ai.DbScripts;
        Assert.Equal([7u], scripts.Get(DbScriptKind.QuestStart, 2843).Select(s => s.Command));
        Assert.Equal([29u, 10u], scripts.Get(DbScriptKind.QuestEnd, 9028).Select(s => s.Command));
        Assert.Equal((16110u, 95.6559f, 4.26772f), scripts.Get(DbScriptKind.QuestEnd, 9028)[1] is var spawn ? (spawn.DataLong, spawn.X, spawn.Orientation) : default);
        Assert.Equal(6981u, Assert.Single(scripts.Get(DbScriptKind.Gossip, 21)).DataLong);
        Assert.Equal([10u, 22u, 26u], scripts.Get(DbScriptKind.Event, 364).Select(s => s.Command));
        Assert.Empty(scripts.Get(DbScriptKind.Event, 21));
        Assert.Equal([1u, 2u, 3u], content.GetScriptWaypoints(12818).Select(p => p.Point));
        Assert.Equal(1000u, content.GetScriptWaypoints(12818)[1].WaitTimeMs);

        QuestContent quests = await new EfQuestContentStore(read).LoadAsync();
        Assert.Equal((2843u, 0u), quests.Templates.Single(q => q.Entry == 2843) is var q1 ? (q1.StartScript, q1.CompleteScript) : default);
        Assert.Equal((0u, 9028u), quests.Templates.Single(q => q.Entry == 8984) is var q2 ? (q2.StartScript, q2.CompleteScript) : default);
        NpcContent npc = await new EfNpcContentStore(read).LoadAsync();
        Assert.Equal(1405u, Assert.Single(npc.GossipMenus).ScriptId);
        Assert.Equal(21u, Assert.Single(npc.GossipMenuOptions).ActionScriptId);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task World41_GainsTheTablesAndColumns_KeepingItsRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<QuestTemplate>().Add(new QuestTemplate { Entry = 2843, Method = 2, Title = "Kept" });
            db.Set<GossipMenu>().Add(new GossipMenu { Entry = 1405, TextId = 2039 });
            await db.SaveChangesAsync();

            // A world database from before this step: none of its tables or columns, the version one below it. Fixed identifiers only.
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            SchemaStep step = WorldDbContext.Schema.Steps.Single(s => s.Version == DbScriptDataModule.Version);
            foreach (CreateTableChange table in step.Changes.OfType<CreateTableChange>())
            {
                await db.Database.ExecuteSqlRawAsync("DROP TABLE " + sql.DelimitIdentifier(table.Table));
            }

            foreach (AddColumnChange column in step.Changes.OfType<AddColumnChange>())
            {
                string statement = "ALTER TABLE " + sql.DelimitIdentifier(column.Table) + " DROP COLUMN " + sql.DelimitIdentifier(column.Column);
                await db.Database.ExecuteSqlRawAsync(statement);
            }

            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, DbScriptDataModule.Version - 1));
        }

        for (int pass = 0; pass < 2; pass++)
        {
            await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);
            QuestTemplate quest = await db.Set<QuestTemplate>().SingleAsync(q => q.Entry == 2843);
            Assert.Equal(("Kept", 0u, 0u), (quest.Title, quest.StartScript, quest.CompleteScript));
            Assert.Equal(0u, (await db.Set<GossipMenu>().SingleAsync()).ScriptId);
            if (pass == 0)
            {
                db.Set<QuestStartScriptRow>().Add(new QuestStartScriptRow { Id = 2843, Delay = 10000, Command = 7, DataLong = 2843 });
                db.Set<ScriptWaypointRow>().Add(new ScriptWaypointRow { Entry = 12818, Point = 1, X = 3347.35f });
                await db.SaveChangesAsync();
            }
            else
            {
                Assert.Equal(2843u, (await db.Set<QuestStartScriptRow>().SingleAsync()).DataLong);
                Assert.Equal(3347.35f, (await db.Set<ScriptWaypointRow>().SingleAsync()).X);
            }
        }
    }

    [Fact]
    public async Task Refresh_AddsTheScripts_SetsTheScriptIds_AndTheScriptedGossipOption_Idempotently()
    {
        // A world an older importer built: the quests and the menu without script ids, the scripted option skipped (it was refused then).
        string world = Path.Combine(_directory, "world.db");
        await using (WorldDbContext db = OpenFile(world))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<QuestTemplate>().AddRange(new QuestTemplate { Entry = 2843, Method = 2, Title = "Gnomer-gooooone!" },
                new QuestTemplate { Entry = 8984, Method = 2, Title = "The Source Revealed" }, new QuestTemplate { Entry = 1, Method = 2, StartScript = 77 });
            db.Set<GossipMenu>().Add(new GossipMenu { Entry = 1405, TextId = 2039 });
            await db.SaveChangesAsync();
        }

        string dump = Path.Combine(_directory, "world.sql");
        File.WriteAllText(dump, Scripts + QuestsAndGossip);
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await ContentImporterCli.RunAsync(["refresh", dump, "--database", world, "--cooldown-unit", "ms"], output, error, CancellationToken.None);
        Assert.True(code == ExitCodes.Ok, error.ToString() + output);
        Assert.Contains("  dbscripts_on_quest_end  2", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("  script_waypoint  3", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("  gossip_menu_option (action_script_id)  1", output.ToString(), StringComparison.Ordinal);
        string first = await SnapshotAsync(world);
        Assert.Contains("quest 1 0 0", first, StringComparison.Ordinal); // a script id the dump does not carry is cleared
        Assert.Contains("quest 2843 2843 0", first, StringComparison.Ordinal);
        Assert.Contains("quest 8984 0 9028", first, StringComparison.Ordinal);
        Assert.Contains("menu 1405 1405", first, StringComparison.Ordinal);
        Assert.Contains("option 21 1 21 Can you tell me about this shard?", first, StringComparison.Ordinal);
        Assert.Contains("steps 1 2 2 3 points 3", first, StringComparison.Ordinal);

        code = await ContentImporterCli.RunAsync(["refresh", dump, "--database", world, "--cooldown-unit", "ms"], new StringWriter(), error, CancellationToken.None);
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal(first, await SnapshotAsync(world));
    }

    private static WorldDbContext OpenFile(string path)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);

    private static async Task<string> SnapshotAsync(string world)
    {
        await using WorldDbContext db = OpenFile(world);
        var text = new StringBuilder();
        foreach (QuestTemplate q in await db.Set<QuestTemplate>().OrderBy(q => q.Entry).ToListAsync())
        {
            text.Append($"quest {q.Entry} {q.StartScript} {q.CompleteScript}\n");
        }

        foreach (GossipMenu m in await db.Set<GossipMenu>().ToListAsync())
        {
            text.Append($"menu {m.Entry} {m.ScriptId}\n");
        }

        foreach (GossipMenuOption o in await db.Set<GossipMenuOption>().ToListAsync())
        {
            text.Append($"option {o.MenuId} {o.Id} {o.ActionScriptId} {o.OptionText}\n");
        }

        text.Append($"steps {await db.Set<QuestStartScriptRow>().CountAsync()} {await db.Set<QuestEndScriptRow>().CountAsync()} ");
        text.Append($"{await db.Set<GossipScriptRow>().CountAsync()} {await db.Set<EventScriptRow>().CountAsync()} ");
        text.Append($"points {await db.Set<ScriptWaypointRow>().CountAsync()}\n");
        return text.ToString();
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _databases.DisposeAsync();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
