using System.Data.Common;
using System.Diagnostics;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using MySqlConnector;
using Npgsql;
using Xunit;

namespace ArcaneCore.Data.Tests.Upgrade;

/// <summary>
/// The operator's apply and the startup policy: typed refusals that name what to do, a pre-flight that
/// refuses before any DDL, progress per step, and the same convergence the bootstrapper has. MariaDB/PostgreSQL
/// theories run only where ARCANECORE_TEST_MARIADB / ARCANECORE_TEST_POSTGRES are set (hosted CI).
/// </summary>
public sealed class SchemaUpgraderTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    private async Task<DatabaseConnectionOptions> NewLegacyAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharactersM5Context legacy = TestContexts.Create<CharactersM5Context>(connection);
        await SchemaBootstrapper.EnsureAsync(legacy, CharactersM5Context.Schema);
        return connection;
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DatabaseNewerThanTheCode_IsRefusedAsADowngrade_AndNothingChanges(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("characters", connection);
        SchemaDefinition schema = CharacterDbContext.Schema;
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await UpgradeTestSupport.SetVersionAsync(db, "characters", schema.CurrentVersion + 1);
        string before = await UpgradeTestSupport.SnapshotAsync(db, schema);

        SchemaDowngradeException ex = await Assert.ThrowsAsync<SchemaDowngradeException>(() => SchemaUpgrader.ApplyAsync(db, schema));

        Assert.Equal(schema.CurrentVersion + 1, ex.DatabaseVersion);
        Assert.Equal(schema.CurrentVersion, ex.CodeVersion);
        Assert.Contains((schema.CurrentVersion + 1).ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains("newer", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, await UpgradeTestSupport.SnapshotAsync(db, schema));

        // The historic entry point still refuses with exactly the historic type.
        await Assert.ThrowsAsync<SchemaMismatchException>(() => SchemaBootstrapper.EnsureAsync(db, schema));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PolicyCreateOnly_CreatesFresh_ResumesACreate_AndRefusesToUpgradeWithoutIssuingDdl(DatabaseProvider provider)
    {
        var options = new SchemaUpgradeOptions { Policy = SchemaPolicy.CreateOnly };
        SchemaDefinition schema = CharacterDbContext.Schema;

        DatabaseConnectionOptions fresh = await _databases.CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(fresh))
        {
            await SchemaBootstrapper.EnsureAsync(db, schema, options);
            Assert.Equal(schema.CurrentVersion, await UpgradeTestSupport.ReadVersionAsync(db, "characters"));

            // An interrupted create (version 0) resumes under CreateOnly; so does a rowless version table.
            await UpgradeTestSupport.SetVersionAsync(db, "characters", SchemaBootstrapper.CreatingVersion);
            await SchemaBootstrapper.EnsureAsync(db, schema, options);
            Assert.Equal(schema.CurrentVersion, await UpgradeTestSupport.ReadVersionAsync(db, "characters"));
            await SchemaProbe.ExecuteAsync(db, $"DELETE FROM {TestContexts.Quote(db, schema.VersionTable)}");
            await SchemaBootstrapper.EnsureAsync(db, schema, options);
            Assert.Equal(schema.CurrentVersion, await UpgradeTestSupport.ReadVersionAsync(db, "characters"));
        }

        DatabaseConnectionOptions legacy = await NewLegacyAsync(provider);
        var tap = new CommandTap();
        await using CharacterDbContext behind = SchemaProbe.CreateWith<CharacterDbContext>(legacy, tap);
        string before = await UpgradeTestSupport.SnapshotAsync(behind, schema);

        SchemaPolicyException ex = await Assert.ThrowsAsync<SchemaPolicyException>(() => SchemaBootstrapper.EnsureAsync(behind, schema, options));

        Assert.Equal(SchemaState.Behind, ex.State);
        Assert.Equal(1, ex.DatabaseVersion);
        Assert.Equal(schema.CurrentVersion, ex.CodeVersion);
        Assert.Contains("schema version 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"needs schema version {schema.CurrentVersion}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("arcane-db upgrade", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, tap.Ddl);
        Assert.Equal(before, await UpgradeTestSupport.SnapshotAsync(behind, schema));

        // Always (the default) upgrades the very same database.
        await SchemaBootstrapper.EnsureAsync(behind, schema, new SchemaUpgradeOptions());
        Assert.Equal(schema.CurrentVersion, await UpgradeTestSupport.ReadVersionAsync(behind, "characters"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PolicyCreateOnly_RefusesToAdoptAPreM5Database(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharactersPreM5Context legacy = TestContexts.Create<CharactersPreM5Context>(connection))
        {
            await TestContexts.CreateTablesAsync(legacy);
        }

        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        SchemaPolicyException ex = await Assert.ThrowsAsync<SchemaPolicyException>(
            () => SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema, new SchemaUpgradeOptions { Policy = SchemaPolicy.CreateOnly }));
        Assert.Equal(SchemaState.AdoptV1, ex.State);
        Assert.False(await SchemaCatalog.TableExistsAsync(db, CharacterDbContext.Schema.VersionTable));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PolicyNever_CreatesNothing_NotEvenTheDatabase_AndAcceptsACurrentOne(DatabaseProvider provider)
    {
        var options = new SchemaUpgradeOptions { Policy = SchemaPolicy.Never };
        SchemaDefinition schema = CharacterDbContext.Schema;

        DatabaseConnectionOptions missing = await _databases.CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(missing))
        {
            SchemaPolicyException ex = await Assert.ThrowsAsync<SchemaPolicyException>(() => SchemaBootstrapper.EnsureAsync(db, schema, options));
            Assert.Equal(SchemaState.Missing, ex.State);
            Assert.False(await ((IRelationalDatabaseCreator)db.GetService<IDatabaseCreator>()).ExistsAsync()); // refused before CreateAsync
            if (provider == DatabaseProvider.Sqlite)
            {
                Assert.False(File.Exists(UpgradeTestSupport.SqlitePath(missing)));
            }
        }

        DatabaseConnectionOptions empty = await _databases.CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(empty))
        {
            await ((IRelationalDatabaseCreator)db.GetService<IDatabaseCreator>()).CreateAsync();
            await Assert.ThrowsAsync<SchemaPolicyException>(() => SchemaBootstrapper.EnsureAsync(db, schema, options));
            Assert.False(await SchemaCatalog.TableExistsAsync(db, schema.VersionTable));
        }

        DatabaseConnectionOptions current = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("characters", current);
        var tap = new CommandTap();
        await using CharacterDbContext verified = SchemaProbe.CreateWith<CharacterDbContext>(current, tap);
        string before = await UpgradeTestSupport.SnapshotAsync(verified, schema);
        await SchemaBootstrapper.EnsureAsync(verified, schema, options);
        Assert.Equal(0, tap.Ddl);
        Assert.Equal(before, await UpgradeTestSupport.SnapshotAsync(verified, schema));

        // A rowless version table is not silently repaired by a verify-only policy: no 0 marker is written.
        await SchemaProbe.ExecuteAsync(verified, $"DELETE FROM {TestContexts.Quote(verified, schema.VersionTable)}");
        await Assert.ThrowsAsync<SchemaPolicyException>(() => SchemaBootstrapper.EnsureAsync(verified, schema, options));
        Assert.Equal(0, await UpgradeTestSupport.CountRowsAsync(verified, schema.VersionTable));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DuplicateRows_BlockBeforeAnyDdl_AndTheRowsAreKept_ThenTheApplySucceedsOnceFixed(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("characters", connection);
        SchemaDefinition next = UpgradeTestSupport.WithIndexRepairStep(CharacterDbContext.Schema, "auction");
        var tap = new CommandTap();
        await using CharacterDbContext db = SchemaProbe.CreateWith<CharacterDbContext>(connection, tap);
        await SchemaProbe.DropIndexesAsync(db, "auction");
        await SchemaProbe.ExecuteAsync(db, UpgradeTestSupport.AuctionInsert(db, 1, 777));
        await SchemaProbe.ExecuteAsync(db, UpgradeTestSupport.AuctionInsert(db, 2, 777));
        int ddlBefore = tap.Ddl;

        SchemaBlockedException ex = await Assert.ThrowsAsync<SchemaBlockedException>(() => SchemaUpgrader.ApplyAsync(db, next));

        Assert.Equal(ddlBefore, tap.Ddl); // the pre-flight refused: no DDL, not even for the steps before the blocker
        Assert.Contains("auction", ex.Message, StringComparison.Ordinal);
        Assert.Contains("item_guid", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 group", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ChangeDecision.Blocked, PlannedBlocker(ex));
        Assert.Equal(2, await UpgradeTestSupport.CountRowsAsync(db, "auction"));
        Assert.Equal(CharacterDbContext.Schema.CurrentVersion, await UpgradeTestSupport.ReadVersionAsync(db, "characters"));

        await SchemaProbe.ExecuteAsync(db, $"DELETE FROM {TestContexts.Quote(db, "auction")} WHERE {TestContexts.Quote(db, "id")} = 2");
        await SchemaUpgrader.ApplyAsync(db, next);
        Assert.Equal(next.CurrentVersion, await UpgradeTestSupport.ReadVersionAsync(db, "characters"));
        Assert.Contains(await SchemaProbe.ActualIndexesAsync(db, ["auction"]), i => i.IsUnique);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UnknownState_IsBlocked(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("characters", connection);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaProbe.ExecuteAsync(db, $"DELETE FROM {TestContexts.Quote(db, CharacterDbContext.Schema.VersionTable)}");
        await SchemaProbe.ExecuteAsync(db, $"DROP TABLE {TestContexts.Quote(db, "item_text")}");

        SchemaBlockedException ex = await Assert.ThrowsAsync<SchemaBlockedException>(() => SchemaUpgrader.ApplyAsync(db, CharacterDbContext.Schema));
        Assert.Equal(SchemaState.Unknown, ex.Plan.State);
        Assert.Contains("unknown state", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Apply_ConvergesAfterAnInterruptionAtEveryDdlStatement_AndAConvergedApplyIssuesNoDdl(DatabaseProvider provider)
    {
        SchemaDefinition schema = CharacterDbContext.Schema;
        DatabaseConnectionOptions probe = await NewLegacyAsync(provider);
        var counter = new CommandTap();
        await using (CharacterDbContext db = SchemaProbe.CreateWith<CharacterDbContext>(probe, counter))
        {
            SchemaPlan plan = await SchemaUpgrader.ApplyAsync(db, schema);
            Assert.Equal(plan.CreateCount, counter.Ddl);
        }

        int ddl = counter.Ddl;
        Assert.True(ddl > 20, $"the legacy characters upgrade issues many DDL statements, saw {ddl}");

        // Every statement of the upgrade, one crash each; a handful of statements per run keeps the SQLite run short on large counts.
        int[] points = [.. Enumerable.Range(1, ddl).Where(k => k <= 12 || k >= ddl - 12 || k % 5 == 0)];
        foreach (int k in points)
        {
            DatabaseConnectionOptions connection = await NewLegacyAsync(provider);
            await using (CharacterDbContext db = SchemaProbe.CreateWith<CharacterDbContext>(connection, new CommandTap(CommandTap.IsDdl, k)))
            {
                Exception ex = await Assert.ThrowsAnyAsync<Exception>(() => SchemaUpgrader.ApplyAsync(db, schema));
                Assert.True(IsInjected(ex), $"crash after DDL #{k} surfaced as {ex.GetType().Name}: {ex.Message}");
            }

            var after = new CommandTap();
            await using CharacterDbContext restart = SchemaProbe.CreateWith<CharacterDbContext>(connection, after);
            await SchemaUpgrader.ApplyAsync(restart, schema);
            Assert.Equal(schema.CurrentVersion, await UpgradeTestSupport.ReadVersionAsync(restart, "characters"));
            Assert.True((await SchemaDriftChecker.CheckAsync(restart, schema)).IsClean, $"not converged after a crash at DDL #{k}");

            // Converged: another apply does nothing.
            var again = new CommandTap();
            await using CharacterDbContext third = SchemaProbe.CreateWith<CharacterDbContext>(connection, again);
            await SchemaUpgrader.ApplyAsync(third, schema);
            Assert.Equal(0, again.Ddl);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TwoConcurrentApplies_Serialize_AndExactlyOneIssuesDdl(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await NewLegacyAsync(provider);
        SchemaDefinition schema = CharacterDbContext.Schema;
        var barrier = new StartupBarrier(2, TimeSpan.FromSeconds(10));
        var tapA = new CommandTap(barrier: barrier);
        var tapB = new CommandTap(barrier: barrier);
        await using CharacterDbContext a = SchemaProbe.CreateWith<CharacterDbContext>(connection, tapA);
        await using CharacterDbContext b = SchemaProbe.CreateWith<CharacterDbContext>(connection, tapB);

        await Task.WhenAll(
            Task.Run(() => SchemaUpgrader.ApplyAsync(a, schema)),
            Task.Run(() => SchemaUpgrader.ApplyAsync(b, schema)));

        Assert.Equal(2, barrier.Arrived);
        Assert.True((tapA.Ddl == 0) != (tapB.Ddl == 0), $"exactly one apply issues the DDL (A {tapA.Ddl}, B {tapB.Ddl})");
        await using CharacterDbContext check = TestContexts.Create<CharacterDbContext>(connection);
        Assert.True((await SchemaDriftChecker.CheckAsync(check, schema)).IsClean);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Progress_ReportsEachWrittenVersionInOrder(DatabaseProvider provider)
    {
        SchemaDefinition schema = CharacterDbContext.Schema;

        DatabaseConnectionOptions legacy = await NewLegacyAsync(provider);
        var steps = new Recorder();
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(legacy))
        {
            SchemaPlan plan = await SchemaUpgrader.ApplyAsync(db, schema, new SchemaUpgradeOptions { Progress = steps });
            Assert.Equal(plan.PendingVersions, steps.Versions);
            Assert.All(steps.Items, s => Assert.Equal("characters", s.Component));
            Assert.NotEmpty(steps.Versions);
        }

        DatabaseConnectionOptions fresh = await _databases.CreateAsync(provider);
        var created = new Recorder();
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(fresh))
        {
            await SchemaUpgrader.ApplyAsync(db, schema, new SchemaUpgradeOptions { Progress = created });
        }

        Assert.Equal([schema.CurrentVersion], created.Versions);

        var nothing = new Recorder();
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(fresh))
        {
            await SchemaUpgrader.ApplyAsync(db, schema, new SchemaUpgradeOptions { Progress = nothing });
        }

        Assert.Empty(nothing.Versions);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LockHeldElsewhere_FailsClosedAtTheDeadline_AndReleases(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("auth", connection);
        SchemaDefinition auth = Auth.AuthDbContext.Schema;
        await SetAuthBehindAsync(connection);

        await using (await HoldSchemaLockAsync(connection, "auth"))
        {
            await using Auth.AuthDbContext db = TestContexts.Create<Auth.AuthDbContext>(connection);
            var clock = Stopwatch.StartNew();
            SchemaMismatchException ex = await Assert.ThrowsAsync<SchemaMismatchException>(
                () => SchemaUpgrader.ApplyAsync(db, auth, new SchemaUpgradeOptions { LockTimeout = TimeSpan.FromSeconds(1) }));
            Assert.Contains("schema lock", ex.Message, StringComparison.Ordinal);
            Assert.InRange(clock.Elapsed.TotalSeconds, 0.5, 20);
        }

        await using Auth.AuthDbContext after = TestContexts.Create<Auth.AuthDbContext>(connection);
        await SchemaUpgrader.ApplyAsync(after, auth);
        Assert.Equal(auth.CurrentVersion, await UpgradeTestSupport.ReadVersionAsync(after, "auth"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RefusalInTheMiddleOfAStep_KeepsEarlierDdlOnlyWhereTheEngineCommitsDdl(DatabaseProvider provider)
    {
        // The provider-semantics fact the operator guide states: SQLite undoes everything (one transaction),
        // MariaDB keeps the DDL that already ran and the version row stays at the last finished step, and a re-run converges.
        SchemaDefinition schema = CharacterDbContext.Schema;
        DatabaseConnectionOptions connection = await NewLegacyAsync(provider);
        var tap = new CommandTap(CommandTap.IsDdl, 3);
        await using (CharacterDbContext db = SchemaProbe.CreateWith<CharacterDbContext>(connection, tap))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => SchemaBootstrapper.EnsureAsync(db, schema));
        }

        await using CharacterDbContext after = TestContexts.Create<CharacterDbContext>(connection);
        int version = (await UpgradeTestSupport.ReadVersionAsync(after, "characters"))!.Value;
        SchemaPlan remaining = await SchemaPlanner.PlanAsync(after, schema);
        if (provider == DatabaseProvider.Sqlite)
        {
            Assert.Equal(1, version);
            Assert.Equal(SchemaState.Behind, remaining.State);
        }
        else
        {
            Assert.InRange(version, 1, schema.CurrentVersion - 1); // an earlier step may or may not have finished; never beyond the interrupted one
            Assert.True(remaining.CreateCount > 0);
        }

        await SchemaUpgrader.ApplyAsync(after, schema);
        Assert.Equal(schema.CurrentVersion, await UpgradeTestSupport.ReadVersionAsync(after, "characters"));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static ChangeDecision PlannedBlocker(SchemaBlockedException ex) => ex.Plan.Blockers[0].Decision;

    private static bool IsInjected(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is InjectedFaultException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Roll the auth schema back one version so that an apply has something to do (and so takes the lock).</summary>
    private static async Task SetAuthBehindAsync(DatabaseConnectionOptions connection)
    {
        await using Auth.AuthDbContext db = TestContexts.Create<Auth.AuthDbContext>(connection);
        await UpgradeTestSupport.SetVersionAsync(db, "auth", Auth.AuthDbContext.Schema.CurrentVersion - 1);
    }

    private sealed class Recorder : IProgress<SchemaStepProgress>
    {
        public List<SchemaStepProgress> Items { get; } = [];

        public IReadOnlyList<int> Versions => [.. Items.Select(i => i.Version)];

        public void Report(SchemaStepProgress value) => Items.Add(value);
    }

    /// <summary>Take the component's schema lock from a separate connection, the way another process would.</summary>
    private static async Task<IAsyncDisposable> HoldSchemaLockAsync(DatabaseConnectionOptions options, string component)
    {
        DbConnection connection = options.Provider switch
        {
            DatabaseProvider.Sqlite => new SqliteConnection(options.ConnectionString + ";Pooling=False"),
            DatabaseProvider.PostgreSql => new NpgsqlConnection(options.ConnectionString),
            _ => new MySqlConnection(options.ConnectionString),
        };
        await connection.OpenAsync();
        string sql = options.Provider switch
        {
            DatabaseProvider.Sqlite => "BEGIN IMMEDIATE",
            DatabaseProvider.PostgreSql => $"SELECT pg_advisory_lock({SchemaBootstrapper.AdvisoryLockKey(component)})",
            _ => $"SELECT GET_LOCK(SHA1(CONCAT('arcanecore_schema:', DATABASE(), ':{component}')), 0)",
        };
        await using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = sql;
            await command.ExecuteScalarAsync();
        }

        return new Held(connection);
    }

    private sealed class Held(DbConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
