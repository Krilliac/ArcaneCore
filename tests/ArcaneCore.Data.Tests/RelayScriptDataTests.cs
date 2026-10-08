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
/// The relay DB script world-schema step (<see cref="RelayScriptDataModule"/>): cmangos <c>dbscripts_on_relay</c> and the relay (type 1)
/// rows of <c>dbscript_random_templates</c>, imported by name, stored on every provider and loaded into
/// <see cref="CreatureAiContent.RelayScripts"/>. The rows below are shaped like classic-db z2815's Elly Langston scripts (19958-19964:
/// pause waypoints, face the player, emote, unpause; data_flags 2) but invented.
/// </summary>
public sealed class RelayScriptDataTests : IAsyncLifetime
{
    private const string Columns =
        "`id`,`delay`,`priority`,`command`,`datalong`,`datalong2`,`datalong3`,`buddy_entry`,`search_radius`,`data_flags`,"
        + "`dataint`,`dataint2`,`dataint3`,`dataint4`,`datafloat`,`x`,`y`,`z`,`o`,`speed`,`condition_id`,`comments`";

    private const string Dump = $"""
        INSERT INTO `dbscripts_on_relay` ({Columns}) VALUES
        (919958,0,0,32,1,0,0,0,0,2,0,0,0,0,0,0,0,0,0,0,0,'pause wp'),
        (919958,1000,0,36,0,0,0,0,0,2,0,0,0,0,0,0,0,0,0,0,0,'face player'),
        (919958,1101,0,1,10,0,0,0,0,2,0,0,0,0,0,0,0,0,0,0,0,'emote'),
        (919958,1101,0,0,27,0,0,0,0,2,0,0,0,0,0,0,0,0,0,0,0,'random text'),
        (919958,4000,0,32,0,0,0,0,0,2,0,0,0,0,0,0,0,0,0,0,0,'unpause wp'),
        (919959,0,0,3,0,0,0,1328,20,1,0,0,0,0,0,10.5,-20.25,30,1.5,0,7,'buddy moves');
        INSERT INTO `dbscript_random_templates` (`id`,`type`,`target_id`,`chance`,`comments`) VALUES
        (39,1,919958,0,'relay a'),(39,1,919959,50,'relay b'),(39,0,991,0,'a string choice');
        """;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void WorldStep_IsTheTwoRelayTables()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == RelayScriptDataModule.Version);
        Assert.Equal(["dbscripts_on_relay", "dbscript_relay_template"], step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
    }

    [Fact]
    public void Importer_KeepsDumpOrderPerId_AndOnlyTheRelayTemplateRows()
    {
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader(Dump));

        (IReadOnlyCollection<RelayScriptRow> steps, IReadOnlyCollection<RelayScriptTemplateRow> templates) = importer.RelaySnapshot();
        RelayScriptRow[] elly = [.. steps.Where(s => s.Id == 919958).OrderBy(s => s.Ordinal)];
        Assert.Equal([32u, 36u, 1u, 0u, 32u], elly.Select(s => s.Command));
        Assert.Equal([0u, 1u, 2u, 3u, 4u], elly.Select(s => s.Ordinal));
        Assert.All(elly, s => Assert.Equal(2u, s.DataFlags));
        RelayScriptRow buddy = Assert.Single(steps, s => s.Id == 919959);
        Assert.Equal((1328u, 20u, 10.5f, -20.25f, 30f, 1.5f, 7u), (buddy.BuddyEntry, buddy.SearchRadius, buddy.X, buddy.Y, buddy.Z, buddy.O, buddy.ConditionId));
        Assert.Equal([(39u, 919958u, 0u), (39u, 919959u, 50u)], templates.Select(t => (t.Id, t.RelayId, t.Chance)).Order());
        CreatureImportReport report = importer.BuildReport();
        Assert.Equal((6, 2, 1), (report.RelayScriptSteps, report.RelayScriptTemplates, report.AiTextTemplates));
    }

    [Fact]
    public void ALaterDumpFile_ReplacesEveryRowOfARelayIdItCarries()
    {
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader(Dump));
        importer.Read(new StringReader($"INSERT INTO `dbscripts_on_relay` ({Columns}) VALUES (919958,0,0,1,4,0,0,0,0,2,0,0,0,0,0,0,0,0,0,0,0,'only');"));

        RelayScriptRow only = Assert.Single(importer.RelaySnapshot().Steps, s => s.Id == 919958);
        Assert.Equal((1u, 4u, 0u), (only.Command, only.DataLong, only.Ordinal));
        Assert.Single(importer.RelaySnapshot().Steps, s => s.Id == 919959);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RelayScripts_RoundTripIntoTheAiContent_InRunOrder(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader(Dump));
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            await importer.WriteAsync(db, replace: false);
        }

        await using WorldDbContext read = TestContexts.Create<WorldDbContext>(cs);
        RelayScriptCatalog relays = (await new EfCreatureDataStore(read).LoadAsync()).Ai.RelayScripts;

        Assert.Equal((2, 6, 1), (relays.ScriptCount, relays.StepCount, relays.TemplateCount));
        Assert.Equal([32u, 36u, 1u, 0u, 32u], relays.Get(919958).Select(s => s.Command));
        Assert.Equal(27u, relays.Get(919958)[3].DataLong);
        Assert.Equal(919959u, relays.SelectFromTemplate(39, 30f, _ => 0));  // inside the explicit 50 %
        Assert.Equal(919958u, relays.SelectFromTemplate(39, 80f, _ => 0));  // the rest goes to the chance-zero row
        Assert.Equal(0u, relays.SelectFromTemplate(40, 10f, _ => 0));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PreviousWorldVersion_GainsTheRelayTables_KeepingRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);

            // Recreate a database from before this step: no relay tables, the version one below it.
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            // Fixed identifiers only (no input reaches this DDL).
            string[] ddl =
            [
                "DROP TABLE " + sql.DelimitIdentifier(RelayScriptDataModule.ScriptTable),
                "DROP TABLE " + sql.DelimitIdentifier(RelayScriptDataModule.TemplateTable),
            ];
            foreach (string statement in ddl)
            {
                await db.Database.ExecuteSqlRawAsync(statement);
            }

            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, RelayScriptDataModule.Version - 1));
        }

        for (int pass = 0; pass < 2; pass++)
        {
            await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);
            if (pass == 0)
            {
                db.Set<RelayScriptTemplateRow>().Add(new RelayScriptTemplateRow { Id = 1, RelayId = 2, Chance = 3 });
                await db.SaveChangesAsync();
            }
            else
            {
                Assert.Equal(3u, (await db.Set<RelayScriptTemplateRow>().SingleAsync()).Chance);
            }
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
