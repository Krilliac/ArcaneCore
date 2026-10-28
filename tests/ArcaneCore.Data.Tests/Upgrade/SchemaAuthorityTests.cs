using System.Data.Common;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Data.Tests.Upgrade;

/// <summary>
/// M15.0 B2 — Schema Authority: coordinated schema initialization (including concurrent Realm/World startup
/// seeding under the per-component schema lock) and validate-only serving mode (<c>Database:Upgrade:ValidateOnly</c>
/// and <see cref="SchemaBootstrapper.ValidateAsync"/>).
/// </summary>
public sealed class SchemaAuthorityTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void ValidateOnly_DefaultsToFalse_AndMapsToPolicyNeverWhenEnabled()
    {
        var defaults = new DatabaseUpgradeOptions();
        Assert.False(defaults.ValidateOnly);
        Assert.Equal(SchemaPolicy.Always, defaults.Policy);

        SchemaUpgradeOptions defaultSchemaOptions = defaults.ToSchemaOptions();
        Assert.False(defaultSchemaOptions.ValidateOnly);
        Assert.Equal(SchemaPolicy.Always, defaultSchemaOptions.Policy);
        Assert.Equal(SchemaPolicy.Always, defaultSchemaOptions.EffectivePolicy);

        var validateOnly = new DatabaseUpgradeOptions { ValidateOnly = true };
        SchemaUpgradeOptions mapped = validateOnly.ToSchemaOptions();
        Assert.True(mapped.ValidateOnly);
        Assert.Equal(SchemaPolicy.Never, mapped.Policy);
        Assert.Equal(SchemaPolicy.Never, mapped.EffectivePolicy);
    }

    [Trait("Category", "Timing")]
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FourConcurrentEnsureAsync_OnSharedDatabase_SerializeAndConvergeWithoutDuplicateDdl(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (CharactersM5Context legacy = TestContexts.Create<CharactersM5Context>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(legacy, CharactersM5Context.Schema);
        }

        const int concurrency = 4;
        var barrier = new StartupBarrier(concurrency, TimeSpan.FromMilliseconds(250));
        var taps = Enumerable.Range(0, concurrency).Select(_ => new CommandTap(barrier: barrier)).ToArray();
        CharacterDbContext[] contexts = [.. taps.Select(tap => SchemaProbe.CreateWith<CharacterDbContext>(connection, tap))];
        try
        {
            await Task.WhenAll(contexts.Select(ctx => Task.Run(() => SchemaBootstrapper.EnsureAsync(ctx, CharacterDbContext.Schema))));

            Assert.Equal(concurrency, barrier.Arrived);
            Assert.Equal(1, taps.Count(t => t.Ddl > 0));
            Assert.Equal(concurrency - 1, taps.Count(t => t.Ddl == 0));

            await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(connection);
            Assert.Equal(CharacterDbContext.Schema.CurrentVersion, await UpgradeTestSupport.ReadVersionAsync(verify, "characters"));
            Assert.True((await SchemaDriftChecker.CheckAsync(verify, CharacterDbContext.Schema)).IsClean);
        }
        finally
        {
            foreach (CharacterDbContext ctx in contexts)
            {
                await ctx.DisposeAsync();
            }
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ConcurrentRealmAndWorldStartups_AgainstSharedAuthAndWorldDatabases_SerializeSeedingWithoutDuplicates(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        const int daemons = 4;
        using var startGate = new ManualResetEventSlim(false);
        ServiceProvider[] providers = [.. Enumerable.Range(0, daemons).Select(_ => BuildServices(connection, seedRealms: true))];
        try
        {
            Task[] tasks =
            [
                .. providers.Select((sp, index) => Task.Run(async () =>
                {
                    startGate.Wait(TimeSpan.FromSeconds(10));
                    await sp.GetRequiredService<AuthDbInitializer>().InitializeAsync();
                    if (index % 2 == 1)
                    {
                        await sp.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
                        await sp.GetRequiredService<WorldDbInitializer>().InitializeAsync();
                    }
                })),
            ];

            startGate.Set();
            await Task.WhenAll(tasks);

            await using AuthDbContext auth = TestContexts.Create<AuthDbContext>(connection);
            Assert.Equal(AuthDbContext.Schema.CurrentVersion, await UpgradeTestSupport.ReadVersionAsync(auth, "auth"));
            List< ArcaneCore.Kernel.Realms.RealmEntry> realms = await auth.Realms.AsNoTracking().ToListAsync();
            Assert.Equal(2, realms.Count);

            await using WorldDbContext world = TestContexts.Create<WorldDbContext>(connection);
            Assert.Equal(WorldDbContext.Schema.CurrentVersion, await UpgradeTestSupport.ReadVersionAsync(world, "world"));
            Assert.True(await world.PlayerCreateInfo.AnyAsync());
            Assert.True(await world.RaceInfo.AnyAsync());
            Assert.True(await world.ClassInfo.AnyAsync());
        }
        finally
        {
            foreach (ServiceProvider sp in providers)
            {
                await sp.DisposeAsync();
            }
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ValidateOnly_RefusesMissingDatabase_MissingVersionTable_RowlessVersionTable_Behind_Ahead_AndMissingModuleTable(DatabaseProvider provider)
    {
        var validateOnly = new SchemaUpgradeOptions { ValidateOnly = true };
        SchemaDefinition schema = CharacterDbContext.Schema;

        // 1. Missing database: refuses before creating the database or executing any DDL.
        DatabaseConnectionOptions missing = await _databases.CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(missing))
        {
            InvalidOperationException ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => SchemaBootstrapper.EnsureAsync(db, schema, validateOnly));
            SchemaPolicyException policyEx = Assert.IsType<SchemaPolicyException>(ex);
            Assert.Equal(SchemaState.Missing, policyEx.State);
            Assert.Contains("validate-only", policyEx.Message, StringComparison.OrdinalIgnoreCase);
            if (provider == DatabaseProvider.Sqlite)
            {
                Assert.False(File.Exists(UpgradeTestSupport.SqlitePath(missing)));
            }
        }

        // 2. Empty database (missing <component>_schema / schema_versions table): refuses with zero DDL.
        DatabaseConnectionOptions empty = await _databases.CreateAsync(provider);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(empty))
        {
            await ((Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator)
                Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Storage.IDatabaseCreator>(db)).CreateAsync();
        }

        var emptyTap = new DdlAndDmlTap();
        await using (CharacterDbContext db = CreateWithTap<CharacterDbContext>(empty, emptyTap))
        {
            InvalidOperationException ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => SchemaBootstrapper.ValidateAsync(db, schema));
            SchemaPolicyException policyEx = Assert.IsType<SchemaPolicyException>(ex);
            Assert.Equal(SchemaState.Fresh, policyEx.State);
            Assert.Contains("schema_versions", policyEx.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, emptyTap.Ddl);
            Assert.Equal(0, emptyTap.Dml);
            Assert.False(await SchemaCatalog.TableExistsAsync(db, schema.VersionTable));
        }

        // 3. Behind database (version < CurrentVersion): refuses with zero DDL and leaves version untouched.
        DatabaseConnectionOptions behindConn = await _databases.CreateAsync(provider);
        await using (CharactersM5Context legacy = TestContexts.Create<CharactersM5Context>(behindConn))
        {
            await SchemaBootstrapper.EnsureAsync(legacy, CharactersM5Context.Schema);
        }

        var behindTap = new DdlAndDmlTap();
        await using (CharacterDbContext behind = CreateWithTap<CharacterDbContext>(behindConn, behindTap))
        {
            InvalidOperationException ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => SchemaBootstrapper.EnsureAsync(behind, schema, validateOnly));
            SchemaPolicyException policyEx = Assert.IsType<SchemaPolicyException>(ex);
            Assert.Equal(SchemaState.Behind, policyEx.State);
            Assert.Equal(1, policyEx.DatabaseVersion);
            Assert.Equal(schema.CurrentVersion, policyEx.CodeVersion);
            Assert.Contains("validate-only", policyEx.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, behindTap.Ddl);
            Assert.Equal(0, behindTap.Dml);
            Assert.Equal(1, await UpgradeTestSupport.ReadVersionAsync(behind, "characters"));
        }

        // 4. Ahead database (version > CurrentVersion): refuses with zero DDL.
        DatabaseConnectionOptions currentConn = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("characters", currentConn);
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(currentConn))
        {
            await UpgradeTestSupport.SetVersionAsync(setup, "characters", schema.CurrentVersion + 1);
        }

        var aheadTap = new DdlAndDmlTap();
        await using (CharacterDbContext ahead = CreateWithTap<CharacterDbContext>(currentConn, aheadTap))
        {
            InvalidOperationException ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => SchemaBootstrapper.ValidateAsync(ahead, schema));
            Assert.IsType<SchemaMismatchException>(ex);
            Assert.Contains((schema.CurrentVersion + 1).ToString(), ex.Message, StringComparison.Ordinal);
            Assert.Contains("newer", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, aheadTap.Ddl);
            Assert.Equal(0, aheadTap.Dml);
        }

        // 5. Current version recorded, but a registered module table was dropped: refuses with zero DDL.
        await using (CharacterDbContext restore = TestContexts.Create<CharacterDbContext>(currentConn))
        {
            await UpgradeTestSupport.SetVersionAsync(restore, "characters", schema.CurrentVersion);
            await SchemaProbe.ExecuteAsync(restore, $"DROP TABLE {TestContexts.Quote(restore, "character_ownership")}");
        }

        var missingTableTap = new DdlAndDmlTap();
        await using (CharacterDbContext missingTable = CreateWithTap<CharacterDbContext>(currentConn, missingTableTap))
        {
            InvalidOperationException ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => SchemaBootstrapper.ValidateAsync(missingTable, schema));
            Assert.IsType<SchemaMismatchException>(ex);
            Assert.Contains("character_ownership", ex.Message, StringComparison.Ordinal);
            Assert.Contains("validate-only", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, missingTableTap.Ddl);
            Assert.Equal(0, missingTableTap.Dml);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ValidateOnly_SucceedsWhenCurrent_WithZeroDdlAndZeroSeedInserts_AcrossAllInitializers(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("auth", connection);
        await SchemaProbe.EnsureCurrentAsync("characters", connection);
        await SchemaProbe.EnsureCurrentAsync("world", connection);

        // Ensure seed tables are empty before running initializers in ValidateOnly mode, proving zero seed rows are inserted.
        await using (AuthDbContext authSetup = TestContexts.Create<AuthDbContext>(connection))
        {
            await SchemaProbe.ExecuteAsync(authSetup, $"DELETE FROM {TestContexts.Quote(authSetup, "realmlist")}");
            authSetup.Set<AccountBanRow>().Add(new AccountBanRow
            {
                AccountId = 42,
                BanDate = 100,
                UnbanDate = 200, // expired
                BannedBy = "test",
                BanReason = "expired",
                Active = true,
                Realm = 0,
            });
            await authSetup.SaveChangesAsync();
        }

        await using (WorldDbContext worldSetup = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaProbe.ExecuteAsync(worldSetup, $"DELETE FROM {TestContexts.Quote(worldSetup, "player_create_info")}");
            await SchemaProbe.ExecuteAsync(worldSetup, $"DELETE FROM {TestContexts.Quote(worldSetup, "race_info")}");
            await SchemaProbe.ExecuteAsync(worldSetup, $"DELETE FROM {TestContexts.Quote(worldSetup, "class_info")}");
        }

        var tap = new DdlAndDmlTap();
        await using (AuthDbContext auth = CreateWithTap<AuthDbContext>(connection, tap))
        await using (CharacterDbContext chars = CreateWithTap<CharacterDbContext>(connection, tap))
        await using (WorldDbContext world = CreateWithTap<WorldDbContext>(connection, tap))
        {
            await SchemaBootstrapper.ValidateAsync(auth, AuthDbContext.Schema);
            await SchemaBootstrapper.ValidateAsync(chars, CharacterDbContext.Schema);
            await SchemaBootstrapper.ValidateAsync(world, WorldDbContext.Schema);
        }

        Assert.Equal(0, tap.Ddl);
        Assert.Equal(0, tap.Dml);

        // Also run the three DI initializers with Database:Upgrade:ValidateOnly = true and RealmSeedOptions configured.
        await using ServiceProvider validateServices = BuildServices(
            connection,
            seedRealms: true,
            ("Database:Upgrade:ValidateOnly", "true"));

        await validateServices.GetRequiredService<AuthDbInitializer>().InitializeAsync();
        await validateServices.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
        await validateServices.GetRequiredService<WorldDbInitializer>().InitializeAsync();

        await using AuthDbContext authAfter = TestContexts.Create<AuthDbContext>(connection);
        Assert.False(await authAfter.Realms.AnyAsync());
        Assert.True((await authAfter.Set<AccountBanRow>().SingleAsync(b => b.AccountId == 42)).Active);

        await using WorldDbContext worldAfter = TestContexts.Create<WorldDbContext>(connection);
        Assert.False(await worldAfter.PlayerCreateInfo.AnyAsync());
        Assert.False(await worldAfter.RaceInfo.AnyAsync());
        Assert.False(await worldAfter.ClassInfo.AnyAsync());

        // On SQLite, ValidateAsync even succeeds on a physical Mode=ReadOnly connection.
        if (provider == DatabaseProvider.Sqlite)
        {
            var readOnlyBuilder = new SqliteConnectionStringBuilder(connection.ConnectionString)
            {
                Mode = SqliteOpenMode.ReadOnly,
            };
            var readOnlyOptions = new DatabaseConnectionOptions
            {
                Provider = DatabaseProvider.Sqlite,
                ConnectionString = readOnlyBuilder.ToString(),
            };
            await using CharacterDbContext readOnlyDb = TestContexts.Create<CharacterDbContext>(readOnlyOptions);
            await SchemaBootstrapper.ValidateAsync(readOnlyDb, CharacterDbContext.Schema);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static ServiceProvider BuildServices(
        DatabaseConnectionOptions connection,
        bool seedRealms,
        params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = connection.Provider.ToString(),
            ["Database:ConnectionString"] = connection.ConnectionString,
        };
        if (seedRealms)
        {
            settings["Realms:Seed:0:Name"] = "ArcaneRealm-A";
            settings["Realms:Seed:0:Address"] = "127.0.0.1:8085";
            settings["Realms:Seed:1:Name"] = "ArcaneRealm-B";
            settings["Realms:Seed:1:Address"] = "127.0.0.1:8086";
        }

        foreach ((string key, string value) in extra)
        {
            settings[key] = value;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<RealmSeedOptions>(configuration.GetSection(RealmSeedOptions.SectionName));
        services.AddAuthDatabase(configuration);
        services.AddCharacterDatabase(configuration);
        services.AddWorldDatabase(configuration);
        return services.BuildServiceProvider();
    }

    private static TContext CreateWithTap<TContext>(DatabaseConnectionOptions connection, DbCommandInterceptor interceptor)
        where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        switch (connection.Provider)
        {
            case DatabaseProvider.Sqlite:
                builder.UseSqlite(connection.ConnectionString);
                break;
            case DatabaseProvider.PostgreSql:
                builder.UseNpgsql(connection.ConnectionString);
                break;
            default:
                builder.UseMySql(connection.ConnectionString, new MariaDbServerVersion(new Version(11, 4, 0)));
                break;
        }

        builder.AddInterceptors(interceptor);
        return (TContext)Activator.CreateInstance(typeof(TContext), builder.Options)!;
    }

    private sealed class DdlAndDmlTap : DbCommandInterceptor
    {
        private int _ddl;
        private int _dml;

        public int Ddl => Volatile.Read(ref _ddl);

        public int Dml => Volatile.Read(ref _dml);

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Observe(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Observe(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Observe(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Observe(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Observe(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Observe(command.CommandText);
            return ValueTask.FromResult(result);
        }

        private void Observe(string sql)
        {
            if (CommandTap.IsDdl(sql))
            {
                Interlocked.Increment(ref _ddl);
            }

            ReadOnlySpan<char> trimmed = sql.AsSpan().TrimStart();
            if (trimmed.StartsWith("INSERT ", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("UPDATE ", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("DELETE ", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("REPLACE ", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("PRAGMA journal_mode", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _dml);
            }
        }
    }
}
