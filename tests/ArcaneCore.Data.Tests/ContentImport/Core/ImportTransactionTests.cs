using System.Transactions;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Core;

/// <summary>
/// One transaction across several importers: a later failure or cancellation restores the rows
/// every earlier importer replaced. The importers keep their own savepoint contract (they see
/// the outer transaction as a caller's). Databases come from the existing provider matrix;
/// all dump rows are synthetic.
/// </summary>
public sealed class ImportTransactionTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AllImporters_Commit_Together(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SeedAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await ImportTransaction.RunAsync(db, async ct =>
            {
                await CreatureImporter().WriteAsync(db, replace: true, ct);
                await GameObjectImporter().WriteAsync(db, replace: true, ct);
            });

            Assert.Null(db.Database.CurrentTransaction);
            Assert.Empty(db.ChangeTracker.Entries());
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        Assert.Equal(901u, (await verify.Set<CreatureTemplateRow>().AsNoTracking().SingleAsync()).Entry);
        Assert.Equal(902u, (await verify.Set<GameObjectTemplateRow>().AsNoTracking().SingleAsync()).Entry);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FailureInTheThirdMapper_RestoresEveryEarlierMappersRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SeedAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await Assert.ThrowsAsync<IOException>(() => ImportTransaction.RunAsync(db, async ct =>
            {
                await CreatureImporter().WriteAsync(db, replace: true, ct);
                await GameObjectImporter().WriteAsync(db, replace: true, ct);
                // Both earlier importers have committed their own savepoints by now.
                Assert.Equal(901u, (await db.Set<CreatureTemplateRow>().AsNoTracking().SingleAsync()).Entry);
                throw new IOException("synthetic failure in the third mapper");
            }));

            Assert.Null(db.Database.CurrentTransaction);
            Assert.Empty(db.ChangeTracker.Entries());
        }

        await AssertOriginalAsync(connection);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CancellationMidRun_LeavesThePreviousContent(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SeedAsync(provider);
        using var cancellation = new CancellationTokenSource();
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImportTransaction.RunAsync(db, async ct =>
            {
                await CreatureImporter().WriteAsync(db, replace: true, ct);
                await cancellation.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }, cancellation.Token));

            Assert.Null(db.Database.CurrentTransaction);
        }

        await AssertOriginalAsync(connection);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CallerTransaction_IsProtectedByASavepoint_AndStaysTheCallers(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SeedAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await using var caller = await db.Database.BeginTransactionAsync();
            db.ClassInfo.Add(new ClassInfoRow { Class = 2, BaseHealth = 222 });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await Assert.ThrowsAsync<IOException>(() => ImportTransaction.RunAsync(db, async ct =>
            {
                await CreatureImporter().WriteAsync(db, replace: true, ct);
                throw new IOException("synthetic failure");
            }));

            // The caller's own work survives, the import is gone, the transaction is still theirs.
            Assert.Same(caller, db.Database.CurrentTransaction);
            Assert.Equal(7u, (await db.Set<CreatureTemplateRow>().AsNoTracking().SingleAsync()).Entry);
            Assert.Equal(222u, (await db.ClassInfo.AsNoTracking().SingleAsync(r => r.Class == 2)).BaseHealth);

            await ImportTransaction.RunAsync(db, ct => CreatureImporter().WriteAsync(db, replace: true, ct));
            Assert.Same(caller, db.Database.CurrentTransaction);
            Assert.Equal(901u, (await db.Set<CreatureTemplateRow>().AsNoTracking().SingleAsync()).Entry);
            await caller.RollbackAsync();
        }

        await AssertOriginalAsync(connection);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TrackedCallerState_AndAmbientTransactions_AreRefusedBeforeAnyWrite(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await SeedAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            db.ClassInfo.Add(new ClassInfoRow { Class = 2, BaseHealth = 222 });
            bool ran = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => ImportTransaction.RunAsync(db, _ =>
            {
                ran = true;
                return Task.CompletedTask;
            }));
            Assert.False(ran);
            db.ChangeTracker.Clear();

            using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ImportTransaction.RunAsync(db, _ =>
            {
                ran = true;
                return Task.CompletedTask;
            }));
            Assert.False(ran);
        }

        await AssertOriginalAsync(connection);
    }

    private async Task<DatabaseConnectionOptions> SeedAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Add(new CreatureTemplateRow { Entry = 7, Name = "Original" });
        db.Add(new GameObjectTemplateRow { Entry = 8, Name = "Original object" });
        await db.SaveChangesAsync();
        return connection;
    }

    private async Task AssertOriginalAsync(DatabaseConnectionOptions connection)
    {
        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        Assert.Equal("Original", (await verify.Set<CreatureTemplateRow>().AsNoTracking().SingleAsync()).Name);
        Assert.Equal("Original object", (await verify.Set<GameObjectTemplateRow>().AsNoTracking().SingleAsync()).Name);
    }

    private static CreatureDumpImporter CreatureImporter()
    {
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader("INSERT INTO `creature_template` (`Entry`,`Name`,`MinLevel`,`MaxLevel`) VALUES (901,'Imported',1,1);"));
        return importer;
    }

    private static GameObjectLootDumpImporter GameObjectImporter()
    {
        var importer = new GameObjectLootDumpImporter();
        importer.Read(new StringReader("INSERT INTO `gameobject_template` (`entry`,`type`,`displayId`,`name`) VALUES (902,3,1,'Imported object');"));
        return importer;
    }
}
