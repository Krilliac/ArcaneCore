using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Quests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The advanced-quests world step (quest_template.RewMailTemplateId / RewMailDelaySecs). MariaDB DDL implicitly
/// commits and PostgreSQL DDL is transactional; both run only on hosted CI, this machine runs SQLite.
/// </summary>
public sealed class QuestAdvancedSchemaTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FreshDatabase_HasTheMailColumns_WithZeroDefaults(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using WorldDbContext world = TestContexts.Create<WorldDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(world, WorldDbContext.Schema);
        world.Set<QuestTemplate>().Add(new QuestTemplate { Entry = 4243, Method = 2 });
        await world.SaveChangesAsync();
        world.ChangeTracker.Clear();
        QuestTemplate quest = await world.Set<QuestTemplate>().SingleAsync(q => q.Entry == 4243);
        Assert.Equal((0, 0u), (quest.RewMailTemplateId, quest.RewMailDelaySecs));
        Assert.True(WorldDbContext.Schema.CurrentVersion >= QuestAdvancedWorldModule.Version);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DatabaseBeforeTheStep_UpgradesKeepsRows_AndSurvivesARepeatedStartup(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        string[] columns = [nameof(QuestTemplate.RewMailTemplateId), nameof(QuestTemplate.RewMailDelaySecs)];
        await using (WorldDbContext world = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(world, WorldDbContext.Schema);
            world.Set<QuestTemplate>().Add(new QuestTemplate { Entry = 4242, Method = 2, Title = "Keeps" });
            await world.SaveChangesAsync();
            ISqlGenerationHelper sql = world.GetService<ISqlGenerationHelper>();
            foreach (string column in columns)
            {
                string statement = $"ALTER TABLE {sql.DelimitIdentifier("quest_template")} DROP COLUMN {sql.DelimitIdentifier(column)}";
                await world.Database.ExecuteSqlRawAsync(statement);
            }

            // The version row reads one step earlier, as on a database created before this step. Only the
            // quest columns are missing, so on every provider the step finds exactly what it must add.
            SchemaVersionRow row = await world.Set<SchemaVersionRow>().SingleAsync();
            row.Version = QuestAdvancedWorldModule.Version - 1;
            await world.SaveChangesAsync();
            world.ChangeTracker.Clear();
        }

        for (int pass = 0; pass < 2; pass++)
        {
            await using WorldDbContext world = TestContexts.Create<WorldDbContext>(connection);
            await SchemaBootstrapper.EnsureAsync(world, WorldDbContext.Schema);
            QuestTemplate quest = await world.Set<QuestTemplate>().SingleAsync(q => q.Entry == 4242);
            Assert.Equal(("Keeps", 0, 0u), (quest.Title, quest.RewMailTemplateId, quest.RewMailDelaySecs));
            Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await world.Set<SchemaVersionRow>().SingleAsync()).Version);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task MailColumns_RoundTripThroughTheStore_IncludingANegativeSenderTemplate(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (WorldDbContext world = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(world, WorldDbContext.Schema);
            world.Set<QuestTemplate>().Add(new QuestTemplate { Entry = 4244, Method = 2, RewMailTemplateId = -84, RewMailDelaySecs = 60 });
            await world.SaveChangesAsync();
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        QuestTemplate quest = await verify.Set<QuestTemplate>().AsNoTracking().SingleAsync(q => q.Entry == 4244);
        Assert.Equal((-84, 60u), (quest.RewMailTemplateId, quest.RewMailDelaySecs));
    }

    [Fact]
    public void DumpImporter_MapsTheMailColumnsByName()
    {
        // vmangos ObjectMgr.cpp:5558 selects both columns; classic-db quests with mail carry positive template ids.
        const string dump = """
            CREATE TABLE `quest_template` (`entry` mediumint unsigned NOT NULL, `Method` tinyint, `QuestLevel` smallint, `Title` text, `RewMoneyMaxLevel` int, `RewMailTemplateId` int, `RewMailDelaySecs` int, PRIMARY KEY (`entry`));
            INSERT INTO `quest_template` VALUES (900,2,10,'Mailed',100,84,60),(901,2,10,'Plain',100,0,0);
            """;
        var importer = new ItemQuestDumpImporter();
        importer.Read(new StringReader(dump));
        QuestTemplate[] quests = [.. importer.Snapshot().Quests];
        Assert.Equal((84, 60u), (quests.Single(q => q.Entry == 900).RewMailTemplateId, quests.Single(q => q.Entry == 900).RewMailDelaySecs));
        Assert.Equal((0, 0u), (quests.Single(q => q.Entry == 901).RewMailTemplateId, quests.Single(q => q.Entry == 901).RewMailDelaySecs));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
