using System.Data.Common;
using System.Diagnostics;
using System.Text;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MySqlConnector;
using Npgsql;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The fail-closed edges of the bootstrapper's idempotent create and index repair, the schema
/// lock, and the production catalog reader (checked against the independent test reader).
/// </summary>
public sealed class SchemaBootstrapGuardsTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void IndexRepairVersions_AreInlineSteps_DistinctFromTheModules()
    {
        // Modules may be numbered after the repair: their tables are created with their indexes.
        Assert.True(CharacterDbContext.Schema.CurrentVersion >= CharacterDbContext.IndexRepairVersion);
        Assert.True(WorldDbContext.Schema.CurrentVersion >= WorldDbContext.IndexRepairVersion);
        Assert.Equal(2, AuthDbContext.Schema.CurrentVersion); // auth has no index repair: its one index is a version-1 index

        foreach ((SchemaDefinition schema, int repairVersion) in new[]
        {
            (CharacterDbContext.Schema, CharacterDbContext.IndexRepairVersion),
            (WorldDbContext.Schema, WorldDbContext.IndexRepairVersion),
        })
        {
            SchemaStep repair = schema.Steps.Single(s => s.Version == repairVersion);
            Assert.All(repair.Changes, c => Assert.IsType<EnsureIndexesChange>(c));
        }

        // The tables a repair names are exactly the tables whose indexes upgrades used to lose
        // (plus characters, harmlessly). A model index on any other table created after version 1
        // would be a table the repair forgot.
        using CharacterDbContext characters = TestContexts.Create<CharacterDbContext>(
            new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=:memory:" });
        using WorldDbContext world = TestContexts.Create<WorldDbContext>(
            new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=:memory:" });
        AssertRepairCoversIndexedTables(characters, CharacterDbContext.Schema, CharacterDbContext.IndexRepairVersion);
        AssertRepairCoversIndexedTables(world, WorldDbContext.Schema, WorldDbContext.IndexRepairVersion);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ProductionCatalogReader_AgreesWithTheIndependentReader(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        foreach (string component in new[] { "auth", "characters", "world" })
        {
            await SchemaProbe.EnsureCurrentAsync(component, connection);
            await using DbContext db = SchemaProbe.CreateContext(component, connection);
            foreach (string table in SchemaProbe.ModelTables(db))
            {
                Assert.True(await SchemaCatalog.TableExistsAsync(db, table));
                IReadOnlyList<CatalogIndex> product = await SchemaCatalog.ReadIndexesAsync(db, table);
                IndexShape[] fromProduct = [.. product.Select(i => new IndexShape(table, i.Name, i.IsUnique, string.Join(",", i.Columns)))
                    .OrderBy(s => s.ToString(), StringComparer.Ordinal)];
                Assert.Equal(await SchemaProbe.ActualIndexesAsync(db, [table]), fromProduct);

                string[] columns = [.. (await SchemaCatalog.ReadColumnsAsync(db, table)).Order(StringComparer.OrdinalIgnoreCase)];
                Assert.Equal(await SchemaProbe.ActualColumnsAsync(db, table), columns, StringComparer.OrdinalIgnoreCase);
                Assert.True(await SchemaCatalog.ColumnExistsAsync(db, table, columns[0]));
            }

            Assert.False(await SchemaCatalog.TableExistsAsync(db, "no_such_table"));
            Assert.Empty(await SchemaCatalog.ReadIndexesAsync(db, "no_such_table"));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task IndexUnderAnotherName_SatisfiesTheRepair_AndIsNotDuplicated(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("characters", connection);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaProbe.DropIndexesAsync(db, "guild_member");
            await SchemaProbe.ExecuteAsync(db,
                $"CREATE UNIQUE INDEX {TestContexts.Quote(db, "dba_member_once")} ON {TestContexts.Quote(db, "guild_member")} ({TestContexts.Quote(db, "CharacterId")})");
            await RollBackVersionAsync(db, "characters", CharacterDbContext.IndexRepairVersion - 1);
        }

        await SchemaProbe.EnsureCurrentAsync("characters", connection);

        await using CharacterDbContext after = TestContexts.Create<CharacterDbContext>(connection);
        IndexShape index = Assert.Single(await SchemaProbe.ActualIndexesAsync(after, ["guild_member"]));
        Assert.Equal("dba_member_once", index.Name);
        Assert.True(index.IsUnique);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SameNameIndexOfAnotherShape_FailsClosed_AndIsNotReplaced(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("characters", connection);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaProbe.DropIndexesAsync(db, "guild_member");
            await SchemaProbe.ExecuteAsync(db,
                $"CREATE INDEX {TestContexts.Quote(db, "IX_guild_member_CharacterId")} ON {TestContexts.Quote(db, "guild_member")} ({TestContexts.Quote(db, "CharacterId")})");
            await RollBackVersionAsync(db, "characters", CharacterDbContext.IndexRepairVersion - 1);
        }

        SchemaMismatchException ex = await Assert.ThrowsAsync<SchemaMismatchException>(() => SchemaProbe.EnsureCurrentAsync("characters", connection));
        Assert.Contains("IX_guild_member_CharacterId", ex.Message, StringComparison.Ordinal);

        await using CharacterDbContext after = TestContexts.Create<CharacterDbContext>(connection);
        IndexShape index = Assert.Single(await SchemaProbe.ActualIndexesAsync(after, ["guild_member"]));
        Assert.False(index.IsUnique); // untouched
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ExistingTableWithUnexpectedColumn_IsNotAdopted(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("world", connection);
        await using (WorldDbContext db = TestContexts.Create<WorldDbContext>(connection))
        {
            await SchemaProbe.ExecuteAsync(db, $"ALTER TABLE {TestContexts.Quote(db, "creature_spawn")} ADD COLUMN {TestContexts.Quote(db, "Stray")} INTEGER NULL");
            await RollBackVersionAsync(db, "world", 1);
        }

        SchemaMismatchException ex = await Assert.ThrowsAsync<SchemaMismatchException>(() => SchemaProbe.EnsureCurrentAsync("world", connection));
        Assert.Contains("creature_spawn", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Stray", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ForeignTableOfTheSameName_IsNotAdopted(DatabaseProvider provider)
    {
        // Components may share a database; a table another application owns must not be taken over.
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("characters", connection);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaProbe.ExecuteAsync(db, $"DROP TABLE {TestContexts.Quote(db, "item_text")}");
            await SchemaProbe.ExecuteAsync(db, $"CREATE TABLE {TestContexts.Quote(db, "item_text")} ({TestContexts.Quote(db, "other")} INTEGER NOT NULL)");
            await RollBackVersionAsync(db, "characters", CharacterDbContext.IndexRepairVersion - 2);
        }

        SchemaMismatchException ex = await Assert.ThrowsAsync<SchemaMismatchException>(() => SchemaProbe.EnsureCurrentAsync("characters", connection));
        Assert.Contains("item_text", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SchemaLock_HeldElsewhere_FailsClosedAtTheDeadline_AndReleases(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("auth", connection);

        await using (await HoldSchemaLockAsync(connection, "auth"))
        {
            await using AuthDbContext db = TestContexts.Create<AuthDbContext>(connection);
            var clock = Stopwatch.StartNew();
            SchemaMismatchException ex = await Assert.ThrowsAsync<SchemaMismatchException>(
                () => SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema, TimeSpan.FromSeconds(1)));
            Assert.Contains("schema lock", ex.Message, StringComparison.Ordinal);
            Assert.InRange(clock.Elapsed.TotalSeconds, 0.5, 20); // the deadline, not a provider command timeout
        }

        // A different component is not blocked by this component's lock, and the lock is free again.
        await SchemaProbe.EnsureCurrentAsync("auth", connection);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SchemaLock_OfAnotherComponent_DoesNotBlock(DatabaseProvider provider)
    {
        if (provider == DatabaseProvider.Sqlite)
        {
            // SQLite's lock is the file's write lock: one writer at a time whatever the component.
            return;
        }

        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await SchemaProbe.EnsureCurrentAsync("auth", connection);
        await using (await HoldSchemaLockAsync(connection, "auth"))
        {
            await SchemaProbe.EnsureCurrentAsync("world", connection);
        }
    }

    [Fact]
    public async Task OfflineModelDiffer_EmitsOnlyTablesAndIndexes_ForEveryProvider_AndEachOperationGeneratesSql()
    {
        // The bootstrapper executes CREATE TABLE and CREATE INDEX operations itself and refuses any other
        // operation the differ yields. Pomelo and Npgsql need no server to produce their operations, so
        // the provider-specific part of that contract is proved here without MariaDB or PostgreSQL.
        var configs = new (string Name, Func<DbContextOptionsBuilder, DbContextOptionsBuilder> Configure)[]
        {
            ("MariaDB 10.11", b => b.UseMySql("Server=localhost;Database=x", new MariaDbServerVersion(new Version(10, 11, 0)))),
            ("MySQL 8.0", b => b.UseMySql("Server=localhost;Database=x", new MySqlServerVersion(new Version(8, 0, 36)))),
            ("PostgreSQL", b => b.UseNpgsql("Host=localhost;Database=x")),
            ("SQLite", b => b.UseSqlite("Data Source=:memory:")),
        };

        foreach ((string name, Func<DbContextOptionsBuilder, DbContextOptionsBuilder> configure) in configs)
        {
            foreach (DbContext db in OfflineContexts(configure))
            {
                await using (db)
                {
                    IRelationalModel model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
                    IReadOnlyList<MigrationOperation> operations = db.GetService<IMigrationsModelDiffer>().GetDifferences(null, model);
                    Assert.NotEmpty(operations);
                    // Pomelo adds the database's default character set; only a fresh create applies it.
                    Assert.All(operations, o => Assert.True(o is CreateTableOperation or CreateIndexOperation or AlterDatabaseOperation,
                        $"{name} {db.GetType().Name}: unexpected {o.GetType().Name}"));
                    Assert.Contains(operations, o => o is CreateTableOperation);

                    foreach (MigrationOperation operation in operations)
                    {
                        IReadOnlyList<MigrationCommand> commands = db.GetService<IMigrationsSqlGenerator>()
                            .Generate([operation], db.GetService<IDesignTimeModel>().Model);
                        Assert.NotEmpty(commands);
                        string expectedStart = operation is AlterDatabaseOperation ? "ALTER DATABASE" : "CREATE ";
                        Assert.All(commands, c => Assert.StartsWith(expectedStart, c.CommandText.TrimStart(), StringComparison.OrdinalIgnoreCase));
                    }
                }
            }
        }
    }

    [Fact]
    public void AdvisoryKey_IsTheDocumentedFnv1aOfTheComponentName()
    {
        Assert.Equal(AdvisoryKeyOf("auth"), SchemaBootstrapper.AdvisoryLockKey("auth"));
        Assert.NotEqual(AdvisoryKeyOf("auth"), AdvisoryKeyOf("world"));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static IEnumerable<DbContext> OfflineContexts(Func<DbContextOptionsBuilder, DbContextOptionsBuilder> configure)
    {
        var auth = new DbContextOptionsBuilder<AuthDbContext>();
        configure(auth);
        yield return new AuthDbContext(auth.Options);
        var characters = new DbContextOptionsBuilder<CharacterDbContext>();
        configure(characters);
        yield return new CharacterDbContext(characters.Options);
        var world = new DbContextOptionsBuilder<WorldDbContext>();
        configure(world);
        yield return new WorldDbContext(world.Options);
    }

    private static void AssertRepairCoversIndexedTables(DbContext db, SchemaDefinition schema, int repairVersion)
    {
        string[] repaired = [.. schema.Steps.Single(s => s.Version == repairVersion).Changes.OfType<EnsureIndexesChange>().Select(c => c.Table)];
        string[] createdAfterV1 = [.. schema.Steps.Where(s => s.Version < repairVersion).SelectMany(s => s.Changes)
            .OfType<CreateTableChange>().Select(c => c.Table)];
        string[] indexedAfterV1 = [.. SchemaProbe.ModelIndexes(db).Select(i => i.Table).Distinct(StringComparer.Ordinal)
            .Where(t => createdAfterV1.Contains(t, StringComparer.Ordinal))];
        Assert.NotEmpty(indexedAfterV1);
        Assert.Empty(indexedAfterV1.Except(repaired, StringComparer.Ordinal));
    }

    private static async Task RollBackVersionAsync(DbContext db, string component, int version)
        => await SchemaProbe.ExecuteAsync(db,
            $"UPDATE {TestContexts.Quote(db, component + "_schema")} SET {TestContexts.Quote(db, "Version")} = {version}");

    private static long AdvisoryKeyOf(string component)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte b in Encoding.UTF8.GetBytes("arcanecore_schema:" + component))
        {
            hash ^= b;
            hash = unchecked(hash * 1099511628211UL);
        }

        return unchecked((long)hash);
    }

    /// <summary>Take the component's schema lock from a separate connection, the way another process would.</summary>
    private static async Task<IAsyncDisposable> HoldSchemaLockAsync(DatabaseConnectionOptions options, string component)
    {
        DbConnection connection = options.Provider switch
        {
            DatabaseProvider.Sqlite => new SqliteConnection(options.ConnectionString + ";Pooling=False"),
            DatabaseProvider.PostgreSql => new NpgsqlConnection(options.ConnectionString + ";Pooling=false"),
            _ => new MySqlConnection(options.ConnectionString + ";Pooling=false"),
        };
        await connection.OpenAsync();
        // pg_try_advisory_lock returns a boolean; pg_advisory_lock returns void and so cannot say whether it took the lock.
        string sql = options.Provider switch
        {
            DatabaseProvider.Sqlite => "BEGIN IMMEDIATE",
            DatabaseProvider.PostgreSql => $"SELECT pg_try_advisory_lock({AdvisoryKeyOf(component)})",
            _ => $"SELECT GET_LOCK(SHA1(CONCAT('arcanecore_schema:', DATABASE(), ':{component}')), 0)",
        };
        await using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = sql;
            object? result = await command.ExecuteScalarAsync();
            Assert.True(options.Provider == DatabaseProvider.Sqlite || result is true || (result is not (null or DBNull or string) && Convert.ToInt64(result) == 1),
                $"could not take the lock for the test ({options.Provider}: {sql} returned {result ?? "null"} [{result?.GetType().Name}])");
        }

        return new Held(connection);
    }

    private sealed class Held(DbConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // Closing the session releases a session lock, and rolls back SQLite's open transaction.
            await connection.DisposeAsync();
        }
    }
}
