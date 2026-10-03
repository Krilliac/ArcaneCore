using System.Text;
using System.Transactions;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Replacement imports preserve the previous five-table snapshot when a later batch fails.
/// Every database is disposable and comes from the existing provider matrix; all dump rows
/// are synthetic. 2001 templates cross the importer's 2000-row SaveChanges boundary.
/// </summary>
public sealed class CreatureImportAtomicTests : IAsyncLifetime
{
    private const int ImportedTemplates = 2001;
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public static IEnumerable<object[]> Interruptions()
    {
        foreach (object[] provider in TestDatabases.AvailableProviders())
        {
            yield return [provider[0], false];
            yield return [provider[0], true];
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Replace_SucceedsAcrossBatchBoundary_AndReplacesAllFiveTables(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateSeededAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            CreatureImportReport report = await Importer().WriteAsync(db, replace: true);
            Assert.Equal((ImportedTemplates, 1, 1, 1, 1),
                (report.Templates, report.Spawns, report.Waypoints, report.Models, report.Addons));
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.True(db.ChangeTracker.AutoDetectChangesEnabled);
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        await AssertImportedAsync(verify);
        Assert.Equal(70u, (await verify.ClassInfo.AsNoTracking().SingleAsync()).BaseHealth);
    }

    [Theory]
    [MemberData(nameof(Interruptions))]
    public async Task Replace_InterruptedAfterFirstBatch_RestoresPriorContent_AndClearsFailedTracking(
        DatabaseProvider provider, bool cancel)
    {
        DatabaseConnectionOptions connection = await CreateSeededAsync(provider);
        using var cancellation = new CancellationTokenSource();
        var interceptor = new AfterFirstBatch(() =>
        {
            if (cancel)
            {
                cancellation.Cancel();
            }
            else
            {
                throw new IOException("Synthetic import interruption after the first batch.");
            }
        });
        await using (WorldDbContext db = Context(connection, interceptor))
        {
            // Restoring false is as important as restoring the usual true setting.
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Importer().WriteAsync(db, true, cancellation.Token));
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(() => Importer().WriteAsync(db, true));
            }

            Assert.Equal(1, interceptor.CompletedBatches);
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.False(db.ChangeTracker.AutoDetectChangesEnabled);
            Assert.Null(db.Database.CurrentTransaction);
            await AssertOriginalAsync(db);

            // A failed import leaves no pending entities that a later SaveChanges can
            // accidentally publish. The same context remains usable after rollback.
            db.ClassInfo.Add(new ClassInfoRow { Class = 2, BaseHealth = 222 });
            await db.SaveChangesAsync();
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        await AssertOriginalAsync(verify);
        Assert.Equal(222u, (await verify.ClassInfo.AsNoTracking().SingleAsync(r => r.Class == 2)).BaseHealth);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Replace_InvalidRowInLaterBatch_RollsBackAlreadySavedRowsAndDeletes(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateSeededAsync(provider);
        CreatureDumpImporter importer = Importer(invalidLastRow: true);
        var interceptor = new AfterFirstBatch(() => { });
        await using (WorldDbContext db = Context(connection, interceptor))
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => importer.WriteAsync(db, replace: true));
            Assert.Equal(1, interceptor.CompletedBatches);
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.True(db.ChangeTracker.AutoDetectChangesEnabled);
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        await AssertOriginalAsync(verify);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SuccessfulImport_InCallerTransaction_DoesNotCommitCallerWork(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateSeededAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await using var caller = await db.Database.BeginTransactionAsync();
            await SaveCallerWorkAsync(db);
            await Importer().WriteAsync(db, replace: true);
            Assert.Same(caller, db.Database.CurrentTransaction);
            Assert.Empty(db.ChangeTracker.Entries());
            await AssertImportedAsync(db);
            Assert.Equal(222u, (await db.ClassInfo.AsNoTracking().SingleAsync(r => r.Class == 2)).BaseHealth);
            await caller.RollbackAsync();
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        await AssertOriginalAsync(verify);
        Assert.False(await verify.ClassInfo.AnyAsync(r => r.Class == 2));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FailedImport_InCallerTransaction_RollsBackOnlyImportSavepoint(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateSeededAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await using var caller = await db.Database.BeginTransactionAsync();
            await SaveCallerWorkAsync(db);
            await Assert.ThrowsAsync<DbUpdateException>(() => Importer(invalidLastRow: true).WriteAsync(db, replace: true));
            Assert.Same(caller, db.Database.CurrentTransaction);
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.True(db.ChangeTracker.AutoDetectChangesEnabled);
            await AssertOriginalAsync(db);
            Assert.Equal(222u, (await db.ClassInfo.AsNoTracking().SingleAsync(r => r.Class == 2)).BaseHealth);

            // A second import proves the caller transaction remains usable after the
            // provider rejected a row (including PostgreSQL's aborted-statement state).
            await Importer().WriteAsync(db, replace: true);
            await AssertImportedAsync(db);
            await caller.CommitAsync();
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        await AssertImportedAsync(verify);
        Assert.Equal(222u, (await verify.ClassInfo.AsNoTracking().SingleAsync(r => r.Class == 2)).BaseHealth);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CancelledImport_InCallerTransaction_RollsBackWithUncancelledCleanup(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateSeededAsync(provider);
        using var cancellation = new CancellationTokenSource();
        var interceptor = new AfterFirstBatch(cancellation.Cancel);
        await using (WorldDbContext db = Context(connection, interceptor))
        {
            await using var caller = await db.Database.BeginTransactionAsync();
            await SaveCallerWorkAsync(db);
            interceptor.Arm();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Importer().WriteAsync(db, true, cancellation.Token));
            Assert.Same(caller, db.Database.CurrentTransaction);
            Assert.Empty(db.ChangeTracker.Entries());
            await AssertOriginalAsync(db);
            Assert.Equal(222u, (await db.ClassInfo.AsNoTracking().SingleAsync(r => r.Class == 2)).BaseHealth);
            await caller.CommitAsync();
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        await AssertOriginalAsync(verify);
        Assert.Equal(222u, (await verify.ClassInfo.AsNoTracking().SingleAsync(r => r.Class == 2)).BaseHealth);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Import_RejectsTrackedCallerState_BeforeDeletingOrSavingAnything(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateSeededAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            var pending = new ClassInfoRow { Class = 2, BaseHealth = 222 };
            db.ClassInfo.Add(pending);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Importer().WriteAsync(db, replace: true));
            Assert.Same(pending, Assert.Single(db.ChangeTracker.Entries()).Entity);
            Assert.Equal(EntityState.Added, db.Entry(pending).State);
            Assert.True(db.ChangeTracker.AutoDetectChangesEnabled);
            Assert.Null(db.Database.CurrentTransaction);
            await AssertOriginalAsync(db);
            await db.SaveChangesAsync();
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        await AssertOriginalAsync(verify);
        Assert.Equal(222u, (await verify.ClassInfo.AsNoTracking().SingleAsync(r => r.Class == 2)).BaseHealth);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Import_RejectsAmbientTransactionWithoutEfTransaction_BeforeMutation(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await CreateSeededAsync(provider);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Importer().WriteAsync(db, replace: true));
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using WorldDbContext verify = TestContexts.Create<WorldDbContext>(connection);
        await AssertOriginalAsync(verify);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private async Task<DatabaseConnectionOptions> CreateSeededAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        db.Add(new CreatureTemplateRow { Entry = 7, Name = "Original", SubName = "Preserve" });
        db.Add(new CreatureSpawnRow { Guid = 11, Entry = 7, MapId = 1, X = 12.5f });
        db.Add(new CreatureMovementRow { SpawnGuid = 11, Point = 1, X = 13.5f, WaitTimeMs = 23 });
        db.Add(new CreatureModelInfoRow { DisplayId = 42, BoundingRadius = 0.6f, CombatReach = 1.7f, Gender = 1 });
        db.Add(new CreatureAddonRow { Guid = 11, MountDisplayId = 55, StandState = 8, SheathState = 1, EmoteState = 10 });
        db.ClassInfo.Add(new ClassInfoRow { Class = 1, BaseHealth = 70 });
        await db.SaveChangesAsync();
        return connection;
    }

    private static CreatureDumpImporter Importer(bool invalidLastRow = false)
    {
        var dump = new StringBuilder("INSERT INTO `creature_template` (`Entry`,`Name`,`SubName`,`MinLevel`,`MaxLevel`) VALUES ");
        for (int i = 1; i <= ImportedTemplates; i++)
        {
            if (i > 1)
            {
                dump.Append(',');
            }

            dump.Append('(').Append(1000 + i).Append(",'Imported ").Append(i).Append("','',1,1)");
        }

        dump.Append(";\nINSERT INTO `creature_model_info` (`modelid`,`bounding_radius`,`combat_reach`,`gender`) VALUES (10001,0.4,1.5,0);\n")
            .Append("INSERT INTO `creature` (`guid`,`id`,`map`,`position_x`) VALUES (10001,1001,0,99);\n")
            .Append("INSERT INTO `creature_movement` (`Id`,`Point`,`PositionX`,`WaitTime`) VALUES (10001,1,100,500);\n")
            .Append("INSERT INTO `creature_addon` (`guid`,`mount`,`stand_state`,`sheath_state`,`emote`) VALUES (10001,10001,0,1,2);\n");
        var importer = new CreatureDumpImporter();
        importer.Read(new StringReader(dump.ToString()));
        if (invalidLastRow)
        {
            // A synthetic invalid entity forces a real NOT NULL rejection on every engine.
            // It occurs only after the first 2000 rows have been saved successfully.
            importer.Snapshot().Templates.Last().Name = null!;
        }

        return importer;
    }

    private static WorldDbContext Context(DatabaseConnectionOptions connection, SaveChangesInterceptor interceptor)
        => new(new DbContextOptionsBuilder<WorldDbContext>(TestContexts.Options<WorldDbContext>(connection))
            .AddInterceptors(interceptor).Options);

    private static async Task SaveCallerWorkAsync(WorldDbContext db)
    {
        db.ClassInfo.Add(new ClassInfoRow { Class = 2, BaseHealth = 222 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task AssertOriginalAsync(WorldDbContext db)
    {
        CreatureTemplateRow template = await db.Set<CreatureTemplateRow>().AsNoTracking().SingleAsync();
        Assert.Equal((7u, "Original", "Preserve"), (template.Entry, template.Name, template.SubName));
        CreatureSpawnRow spawn = await db.Set<CreatureSpawnRow>().AsNoTracking().SingleAsync();
        Assert.Equal((11u, 7u, 1u, 12.5f), (spawn.Guid, spawn.Entry, spawn.MapId, spawn.X));
        CreatureMovementRow movement = await db.Set<CreatureMovementRow>().AsNoTracking().SingleAsync();
        Assert.Equal((11u, 1u, 13.5f, 23u), (movement.SpawnGuid, movement.Point, movement.X, movement.WaitTimeMs));
        CreatureModelInfoRow model = await db.Set<CreatureModelInfoRow>().AsNoTracking().SingleAsync();
        Assert.Equal((42u, 0.6f, 1.7f, (byte)1), (model.DisplayId, model.BoundingRadius, model.CombatReach, model.Gender));
        CreatureAddonRow addon = await db.Set<CreatureAddonRow>().AsNoTracking().SingleAsync();
        Assert.Equal((11u, 55u, (byte)8, (byte)1, 10u), (addon.Guid, addon.MountDisplayId, addon.StandState, addon.SheathState, addon.EmoteState));
    }

    private static async Task AssertImportedAsync(WorldDbContext db)
    {
        Assert.Equal(ImportedTemplates, await db.Set<CreatureTemplateRow>().CountAsync());
        Assert.False(await db.Set<CreatureTemplateRow>().AnyAsync(t => t.Entry == 7));
        Assert.Equal("Imported 2001", (await db.Set<CreatureTemplateRow>().AsNoTracking().SingleAsync(t => t.Entry == 3001)).Name);
        Assert.Equal(10001u, (await db.Set<CreatureSpawnRow>().AsNoTracking().SingleAsync()).Guid);
        Assert.Equal(10001u, (await db.Set<CreatureMovementRow>().AsNoTracking().SingleAsync()).SpawnGuid);
        Assert.Equal(10001u, (await db.Set<CreatureModelInfoRow>().AsNoTracking().SingleAsync()).DisplayId);
        Assert.Equal(10001u, (await db.Set<CreatureAddonRow>().AsNoTracking().SingleAsync()).Guid);
    }

    private sealed class AfterFirstBatch(Action action) : SaveChangesInterceptor
    {
        private bool _armed = true;

        public int CompletedBatches { get; private set; }

        public void Arm()
        {
            CompletedBatches = 0;
            _armed = true;
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (_armed && eventData.Context?.ChangeTracker.Entries<CreatureTemplateRow>().Any() == true)
            {
                CompletedBatches++;
                if (CompletedBatches == 1)
                {
                    _armed = false;
                    action();
                }
            }

            return ValueTask.FromResult(result);
        }
    }
}
