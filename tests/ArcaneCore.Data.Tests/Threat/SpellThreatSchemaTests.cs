using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Threat;
using ArcaneCore.Kernel.WorldData.Threat;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests.Threat;

/// <summary>
/// The <c>spell_threat</c> world-schema step on every provider the CI offers. MariaDB DDL is not transactional (CREATE TABLE commits
/// implicitly, so the step is only re-runnable, never rolled back); PostgreSQL DDL is transactional and folds unquoted identifiers
/// to lower case, which is why EF quotes every identifier. The MariaDB and PostgreSQL cases only run on hosted CI: this machine has no
/// database server, so locally only the SQLite cases ran.
/// </summary>
public sealed class SpellThreatSchemaTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void WorldStep_IsTheSpellThreatTable_AndTheVersionsStayContiguous()
    {
        SchemaStep step = Assert.Single(WorldDbContext.Schema.Steps, s => s.Version == SpellThreatDataModule.Version);

        Assert.Equal(["spell_threat"], step.Changes.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.Empty(step.Changes.OfType<AddColumnChange>());
        Assert.True(WorldDbContext.Schema.CurrentVersion >= SpellThreatDataModule.Version);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Store_RoundTripsTheRows_OnEveryProvider(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(0, (await new EfSpellThreatStore(db).LoadAsync()).Count);
            db.Set<SpellThreatRow>().AddRange(
                new SpellThreatRow { Entry = 72, Threat = 180 },
                new SpellThreatRow { Entry = 8092, Threat = 0, Multiplier = 2f },
                new SpellThreatRow { Entry = 21992, Threat = 126, Multiplier = 1.5f, InverseEffectMask = 6, BuildMin = 5464, BuildMax = 5875 });
            await db.SaveChangesAsync();
        }

        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            SpellThreatContent content = await new EfSpellThreatStore(db).LoadAsync();

            Assert.Equal(new SpellThreatRecord(72, 180, 1f, 0), content.Find(72));
            Assert.Equal(new SpellThreatRecord(8092, 0, 2f, 0), content.Find(8092));
            Assert.Equal(new SpellThreatRecord(21992, 126, 1.5f, 6), content.Find(21992));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Import_ReplacesThePreviousRows_AndAFailedImportKeepsThem(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        SpellThreatContent first = new([new SpellThreatRecord(1, 10, 1f, 0), new SpellThreatRecord(2, 20, 0.5f, 1)]);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);

        Assert.Equal(2, await SpellThreatDumpImporter.ImportAsync(db, first));
        Assert.Equal(2, await SpellThreatDumpImporter.ImportAsync(db, first)); // a second import replaces, it does not collide

        // a threat above 65535 cannot reach the store as a record, but a hand-edited row can: loading it fails closed
        db.Set<SpellThreatRow>().Add(new SpellThreatRow { Entry = 3, Threat = 70000 });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => new EfSpellThreatStore(db).LoadAsync());
        db.Set<SpellThreatRow>().Remove(await db.Set<SpellThreatRow>().SingleAsync(r => r.Entry == 3));
        await db.SaveChangesAsync();

        Assert.Equal([1u, 2u], (await new EfSpellThreatStore(db).LoadAsync()).Rows.Select(r => r.Entry).Order());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PreviousWorldVersion_GainsTheTable_KeepingRows_AndTheStepRunsTwiceSafely(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);

            // Recreate a database from before this step: no spell_threat table, the version one below it. A MariaDB DROP
            // TABLE commits implicitly, so nothing here relies on a rollback.
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            string drop = $"DROP TABLE {sql.DelimitIdentifier("spell_threat")}"; // fixed identifier only: no input reaches this DDL
            await db.Database.ExecuteSqlRawAsync(drop);
            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, SpellThreatDataModule.Version - 1));
        }

        for (int pass = 0; pass < 2; pass++)
        {
            await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);
            if (pass == 0)
            {
                Assert.Empty(await db.Set<SpellThreatRow>().ToListAsync());
                db.Set<SpellThreatRow>().Add(new SpellThreatRow { Entry = 5, Threat = 7 });
                await db.SaveChangesAsync();
            }
            else
            {
                Assert.Equal(7, (await db.Set<SpellThreatRow>().SingleAsync()).Threat);
            }
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
