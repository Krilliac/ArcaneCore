using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Procs;
using ArcaneCore.Kernel.WorldData.Procs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests.Procs;

/// <summary>
/// The <c>spell_proc_event</c> world-schema step (world 41, the proc-engine lane's reserved number) on every provider the CI offers. The MariaDB
/// and PostgreSQL cases only run where their test connection strings are set; locally only SQLite runs.
/// </summary>
public sealed class SpellProcEventSchemaTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void WorldStep41_IsTheSpellProcEventTable_AndTheLaneGapStepsChangeNothing()
    {
        Assert.Equal(41, SpellProcEventDataModule.Version);
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == SpellProcEventDataModule.Version);
        Assert.Equal(["spell_proc_event"], step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        foreach (int gap in new[] { WorldSchemaLaneGap38.Version, WorldSchemaLaneGap39.Version, WorldSchemaLaneGap40.Version })
        {
            Assert.Empty(Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == gap).Changes);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Store_RoundTripsTheRows_IncludingMasksAboveTheSignedRange(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        SpellProcEventRecord ppm = new(15600, 0, 0, 0, 0, 0, 0, 0, 0.6f, 0, 3000);
        SpellProcEventRecord wide = new(14892, 1, 6, ulong.MaxValue, 17448312320, 1, 0x10000, 2, 0, 25, 0);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(0, (await new EfSpellProcEventStore(db).LoadAsync()).Count);
            Assert.Equal(2, await SpellProcEventDumpImporter.ImportAsync(db, new SpellProcEventContent([ppm, wide])));
            Assert.Equal(2, await SpellProcEventDumpImporter.ImportAsync(db, new SpellProcEventContent([ppm, wide]))); // a second import replaces
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            SpellProcEventContent content = await new EfSpellProcEventStore(db).LoadAsync();

            Assert.Equal(ppm, content.Find(15600));
            Assert.Equal(wide, content.Find(14892));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PreviousWorldVersion_GainsTheTable_KeepingRows_AndTheStepRunsTwiceSafely(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);

            // Recreate a database from before this step: no spell_proc_event table, the version one below it.
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            string drop = $"DROP TABLE {sql.DelimitIdentifier("spell_proc_event")}"; // fixed identifier only: no input reaches this DDL
            await db.Database.ExecuteSqlRawAsync(drop);
            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, SpellProcEventDataModule.Version - 1));
        }

        for (int pass = 0; pass < 2; pass++)
        {
            await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);
            if (pass == 0)
            {
                Assert.Empty(await db.Set<SpellProcEventRow>().ToListAsync());
                db.Set<SpellProcEventRow>().Add(new SpellProcEventRow { Entry = 5, ProcEx = 7 });
                await db.SaveChangesAsync();
            }
            else
            {
                Assert.Equal(7u, (await db.Set<SpellProcEventRow>().SingleAsync()).ProcEx);
            }
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
