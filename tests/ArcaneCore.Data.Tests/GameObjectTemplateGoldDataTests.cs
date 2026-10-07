using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Lane L9: the chest money columns of <c>gameobject_template</c> (world schema step <see cref="GameObjectTemplateGoldDataModule"/>),
/// their import from the cmangos classic-db and vmangos layouts (<c>mingold</c>, <c>maxgold</c>; the reference core reads them as
/// the last two fields of its template, mangos Object/GameObject.h:415-416) and the upgrade of a database from before the step.
/// Dump snippets are hand-written; no GPL rows are copied. Provider theories run on every provider <see cref="TestDatabases.AvailableProviders"/>
/// offers (SQLite here; MariaDB and PostgreSQL on hosted CI).
/// </summary>
public sealed class GameObjectTemplateGoldDataTests : IAsyncLifetime
{
    private const string CMangosDump = """
        INSERT INTO `gameobject_template` (`entry`,`type`,`displayId`,`name`,`faction`,`flags`,`size`,`data0`,`data1`,`data2`,`mingold`,`maxgold`) VALUES
        (920001,3,259,'Gilded Chest',0,0,1,0,920001,0,30,40),
        (920002,3,259,'Plain Chest',0,0,1,0,920002,0,0,0),
        (920003,0,411,'Door',0,0,1,0,0,3000,0,0);
        """;

    private const string VmangosDump = """
        INSERT INTO `gameobject_template` (`entry`,`patch`,`type`,`displayId`,`name`,`faction`,`flags`,`size`,`data0`,`data1`,`mingold`,`maxgold`) VALUES
        (920010,0,3,1,'Old Chest',0,0,1,0,1,5,6),(920010,7,3,2,'Patched Chest',0,0,1,0,2,7,8),(920010,11,3,3,'TBC Chest',0,0,1,0,3,9,10);
        """;

    // A dump from before the columns existed: nothing to read, the template pays nothing.
    private const string LegacyDump = """
        INSERT INTO `gameobject_template` (`entry`,`type`,`displayId`,`name`,`faction`,`flags`,`size`,`data0`,`data1`) VALUES (920020,3,1,'Legacy Chest',0,0,1,0,4);
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static GameObjectLootDumpImporter Import(string dump)
    {
        var importer = new GameObjectLootDumpImporter();
        importer.Read(new StringReader(dump));
        return importer;
    }

    [Fact]
    public void TheModule_IsAWorldStepWithTwoAdditiveColumns_AndIsRegistered()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == GameObjectTemplateGoldDataModule.Version);
        Assert.Equal(
            [("gameobject_template", nameof(GameObjectTemplateRow.MinGold)), ("gameobject_template", nameof(GameObjectTemplateRow.MaxGold))],
            step.Changes.Cast<AddColumnChange>().Select(c => (c.Table, c.Column)));
        Assert.Contains(DataModules.For(DatabaseComponent.World), m => m is GameObjectTemplateGoldDataModule);
        Assert.True(GameObjectTemplateGoldDataModule.Version > GameObjectSpawnDataModule.Version);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FreshDatabase_ImportsAndLoadsTheGoldRange_FromBothLayouts_AndZeroWithoutTheColumns(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        GameObjectLootDumpImporter importer = Import(CMangosDump + VmangosDump + LegacyDump);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            GameObjectLootImportReport report = await importer.WriteAsync(db, replace: false);
            Assert.Equal(5, report.Templates);
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            GameObjectContent content = await new EfGameObjectDataStore(db).LoadAsync();
            Assert.Equal((30u, 40u), Gold(content, 920001));
            Assert.Equal((0u, 0u), Gold(content, 920002));
            Assert.Equal((0u, 0u), Gold(content, 920003));
            Assert.Equal((7u, 8u), Gold(content, 920010)); // the highest patch <= 10 wins, with its own gold
            Assert.Equal((0u, 0u), Gold(content, 920020));
            Assert.Equal(920001u, content.FindTemplate(920001)!.GetData(1)); // the data columns are untouched
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PreviousWorldVersion_GainsBothColumns_KeepingRows_AndStartupIsRepeatable(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            var row = new GameObjectTemplateRow { Entry = 920030, Type = 3, DisplayId = 5, Name = "Old Row", MinGold = 11, MaxGold = 12 };
            row.SetData([0, 77]);
            db.Set<GameObjectTemplateRow>().Add(row);
            await db.SaveChangesAsync();

            // A database from before the step: the columns are gone and the version is below the step.
            await DropStepColumnsAsync(db);
            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, GameObjectTemplateGoldDataModule.Version - 1));
        }

        for (int pass = 0; pass < 2; pass++)
        {
            await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);

            GameObjectContent content = await new EfGameObjectDataStore(db).LoadAsync();
            GameObjectTemplate template = Assert.Single(content.Templates);
            // Rows from before the step pay nothing; nothing else changed.
            Assert.Equal((920030u, "Old Row", 77u, 0u, 0u), (template.Entry, template.Name, template.GetData(1), template.MinGold, template.MaxGold));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AStepThatWasOnlyPartlyApplied_IsCompletedByTheNextStartup(DatabaseProvider provider)
    {
        // MariaDB DDL implicitly commits: a crash between the two ALTERs leaves the first applied and the version unchanged. The rerun must
        // complete the step on every engine.
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            string statement = $"ALTER TABLE {sql.DelimitIdentifier("gameobject_template")} DROP COLUMN {sql.DelimitIdentifier(nameof(GameObjectTemplateRow.MaxGold))}";
            await db.Database.ExecuteSqlRawAsync(statement);
            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, GameObjectTemplateGoldDataModule.Version - 1));
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Set<GameObjectTemplateRow>().Add(new GameObjectTemplateRow { Entry = 920040, Type = 3, Name = "Repaired", MinGold = 3, MaxGold = 9 });
            await db.SaveChangesAsync();
            Assert.Equal((3u, 9u), Gold(await new EfGameObjectDataStore(db).LoadAsync(), 920040));
        }
    }

    private static (uint Min, uint Max) Gold(GameObjectContent content, uint entry)
    {
        GameObjectTemplate template = content.FindTemplate(entry) ?? throw new InvalidOperationException($"template {entry} missing");
        return (template.MinGold, template.MaxGold);
    }

    private static async Task DropStepColumnsAsync(WorldDbContext db)
    {
        ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
        foreach (AddColumnChange change in WorldDbContext.Schema.Steps.Single(s => s.Version == GameObjectTemplateGoldDataModule.Version).Changes.OfType<AddColumnChange>())
        {
            string statement = $"ALTER TABLE {sql.DelimitIdentifier(change.Table)} DROP COLUMN {sql.DelimitIdentifier(change.Column)}";
            await db.Database.ExecuteSqlRawAsync(statement);
        }
    }
}
