using ArcaneCore.Data.Characters.Ownership;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The sidecar lock that keeps a second world process off a file-backed SQLite characters database. A file opened <see cref="FileShare.None"/> refuses a second opener
/// in the same process exactly as in another one, so these tests need no child process. Server providers are covered by the provider theories below.
/// </summary>
public sealed class CharacterDatabaseWriterLockTests
{
    private static DatabaseConnectionOptions Sqlite(string connectionString)
        => new() { Provider = DatabaseProvider.Sqlite, ConnectionString = connectionString };

    [Fact]
    public void ASecondHolder_IsRefusedWithTheLockPath_UntilTheFirstIsDisposed()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-writerlock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string database = Path.Combine(directory, "characters.db");
            DatabaseConnectionOptions options = Sqlite($"Data Source={database}");

            CharacterDatabaseWriterLock first = CharacterDatabaseWriterLock.TryAcquire(options)!;
            Assert.Equal(database + ".world.lock", first.Path);
            CharacterDatabaseAlreadyOwnedException refused = Assert.Throws<CharacterDatabaseAlreadyOwnedException>(() => CharacterDatabaseWriterLock.TryAcquire(options));
            Assert.Equal(first.Path, refused.LockPath);
            Assert.Contains(first.Path, refused.Message);

            first.Dispose();
            first.Dispose(); // idempotent
            using CharacterDatabaseWriterLock again = CharacterDatabaseWriterLock.TryAcquire(options)!;
            Assert.NotNull(again);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void AnUnusableLockPath_IsNotReportedAsAnotherWorldOwningTheDatabase()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-writerlock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string database = Path.Combine(directory, "characters.db");
            Directory.CreateDirectory(database + ".world.lock"); // a directory where the lock file must be: not a contended lock

            Exception failure = Assert.ThrowsAny<Exception>(() => CharacterDatabaseWriterLock.TryAcquire(Sqlite($"Data Source={database}")));

            Assert.IsNotType<CharacterDatabaseAlreadyOwnedException>(failure);
            Assert.IsAssignableFrom<IOException>(failure);
            Assert.Contains(database + ".world.lock", failure.Message);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=file:characters.db?mode=rwc")]
    [InlineData("Data Source=characters.db;Mode=ReadOnly")]
    [InlineData("Data Source=characters.db;Mode=Memory")]
    [InlineData("")]
    [InlineData("not a connection string")]
    public void DatabasesWithoutAWritableFile_GetNoLock(string connectionString)
        => Assert.Null(CharacterDatabaseWriterLock.TryAcquire(Sqlite(connectionString)));

    [Fact]
    public void ServerDatabasesWithoutADatabaseName_AndMissingDirectories_GetNoLock()
    {
        foreach (DatabaseProvider provider in new[] { DatabaseProvider.MariaDb, DatabaseProvider.MySql, DatabaseProvider.PostgreSql })
            Assert.Null(CharacterDatabaseWriterLock.TryAcquire(new DatabaseConnectionOptions { Provider = provider, ConnectionString = "Data Source=characters.db" }));

        string missing = Path.Combine(Path.GetTempPath(), "arcanecore-writerlock-missing-" + Guid.NewGuid().ToString("N"), "characters.db");
        Assert.Null(CharacterDatabaseWriterLock.TryAcquire(Sqlite($"Data Source={missing}")));
    }

    // ---- Server providers: a session advisory lock (ServerAdvisoryWriterLock). MariaDB and PostgreSQL run only where ARCANECORE_TEST_MARIADB / ARCANECORE_TEST_POSTGRES are set (hosted CI);
    // locally the theory runs its SQLite case, which proves the same contract through the same TryAcquire entry point.

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ASecondAcquirer_IsRefused_UntilTheFirstReleases_OnEveryProvider(DatabaseProvider provider)
    {
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions options = await databases.CreateAsync(provider);
        // The characters database does not exist yet: the lock must not need it (the schema bootstrapper creates it after the lock is taken).
        CharacterDatabaseWriterLock first = CharacterDatabaseWriterLock.TryAcquire(options)!;
        Assert.NotNull(first);
        try
        {
            CharacterDatabaseAlreadyOwnedException refused = Assert.Throws<CharacterDatabaseAlreadyOwnedException>(() => CharacterDatabaseWriterLock.TryAcquire(options));
            Assert.Equal(first.Path, refused.LockPath);
            Assert.StartsWith("the characters database is already owned by another world process", refused.Message);
            Assert.DoesNotContain("Password", refused.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { first.Dispose(); }

        first.Dispose(); // idempotent
        using CharacterDatabaseWriterLock again = CharacterDatabaseWriterLock.TryAcquire(options)!;
        Assert.NotNull(again);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DifferentCharactersDatabases_DoNotCollide_OnEveryProvider(DatabaseProvider provider)
    {
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions one = await databases.CreateAsync(provider);
        DatabaseConnectionOptions two = await databases.CreateAsync(provider);
        using CharacterDatabaseWriterLock a = CharacterDatabaseWriterLock.TryAcquire(one)!;
        using CharacterDatabaseWriterLock b = CharacterDatabaseWriterLock.TryAcquire(two)!;
        Assert.NotEqual(a.Path, b.Path);
    }

    [Fact]
    public void TheServerLockIdentity_IsDerivedFromTheDatabaseName()
    {
        Assert.Equal("arcanecore.world.writer:characters_r1", ServerAdvisoryWriterLock.MySqlLockName("characters_r1"));
        string longName = new('x', 80);
        string hashed = ServerAdvisoryWriterLock.MySqlLockName(longName);
        Assert.Equal(ServerAdvisoryWriterLock.NamePrefix.Length + 32, hashed.Length);
        Assert.True(hashed.Length <= 64);
        Assert.Equal(hashed, ServerAdvisoryWriterLock.MySqlLockName(longName));
        Assert.NotEqual(hashed, ServerAdvisoryWriterLock.MySqlLockName(longName + "y"));
        Assert.Equal(ServerAdvisoryWriterLock.PostgresLockKey("characters_r1"), ServerAdvisoryWriterLock.PostgresLockKey("characters_r1"));
        Assert.NotEqual(ServerAdvisoryWriterLock.PostgresLockKey("characters_r1"), ServerAdvisoryWriterLock.PostgresLockKey("characters_r2"));

        Assert.Equal("chars", ServerAdvisoryWriterLock.DatabaseNameOf(new DatabaseConnectionOptions { Provider = DatabaseProvider.MariaDb, ConnectionString = "Server=h;Database=chars;User=u;Password=p" }));
        Assert.Equal("chars", ServerAdvisoryWriterLock.DatabaseNameOf(new DatabaseConnectionOptions { Provider = DatabaseProvider.PostgreSql, ConnectionString = "Host=h;Database=chars;Username=u;Password=p" }));
        Assert.Null(ServerAdvisoryWriterLock.DatabaseNameOf(new DatabaseConnectionOptions { Provider = DatabaseProvider.MariaDb, ConnectionString = "Server=h;User=u" }));
        Assert.Null(ServerAdvisoryWriterLock.DatabaseNameOf(new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=x.db" }));
    }

    private static async Task CreateDatabaseAsync(DatabaseConnectionOptions options)
    {
        var builder = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Microsoft.EntityFrameworkCore.DbContext>();
        DataServiceCollectionExtensions.ConfigureProvider(builder, options);
        await using var db = new Microsoft.EntityFrameworkCore.DbContext(builder.Options);
        await db.Database.EnsureCreatedAsync();
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TheLockTakenBeforeTheDatabaseExists_StillExcludesAWorldStartedAfterItIsCreated(DatabaseProvider provider)
    {
        // The first bootstrap: world A locks while the characters database does not exist, the schema step then creates it, and world B starts later.
        // PostgreSQL scopes advisory locks to the database of the session, so both must lock in the same fixed maintenance database.
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions options = await databases.CreateAsync(provider);
        using CharacterDatabaseWriterLock first = CharacterDatabaseWriterLock.TryAcquire(options)!;
        Assert.NotNull(first);

        await CreateDatabaseAsync(options);

        Assert.Throws<CharacterDatabaseAlreadyOwnedException>(() => CharacterDatabaseWriterLock.TryAcquire(options));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TheLockTakenAfterTheDatabaseExists_ExcludesASecondWorld_AndAWorldFromBeforeItExisted(DatabaseProvider provider)
    {
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions options = await databases.CreateAsync(provider);
        await CreateDatabaseAsync(options);

        using CharacterDatabaseWriterLock first = CharacterDatabaseWriterLock.TryAcquire(options)!;
        Assert.NotNull(first);
        Assert.Throws<CharacterDatabaseAlreadyOwnedException>(() => CharacterDatabaseWriterLock.TryAcquire(options));
    }

    public static IEnumerable<object[]> ServerProviders()
    {
        bool any = false;
        foreach (object[] row in TestDatabases.AvailableProviders())
        {
            if ((DatabaseProvider)row[0] == DatabaseProvider.Sqlite) continue;
            any = true;
            yield return row;
        }

        // Without a server in the environment the theory has no data; hand it SQLite, whose case returns at once (the SQLite lock has no connection to lose).
        if (!any) yield return [DatabaseProvider.Sqlite];
    }

    [Theory]
    [MemberData(nameof(ServerProviders))]
    public async Task ALostLockConnection_IsDetectedByTheHealthCheck_AndReportedOnce(DatabaseProvider provider)
    {
        if (provider == DatabaseProvider.Sqlite) return;
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions options = await databases.CreateAsync(provider);
        string name = ServerAdvisoryWriterLock.DatabaseNameOf(options)!;
        var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reports = 0;
        using ServerAdvisoryWriterLock held = ServerAdvisoryWriterLock.TryAcquire(options, name, reason =>
        {
            Interlocked.Increment(ref reports);
            lost.TrySetResult(reason);
        }, TimeSpan.FromMilliseconds(200));
        Assert.True(held.IsHeld());

        await KillLockSessionAsync(options, name);

        string reason = await lost.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains(held.Description, reason);
        await Task.Delay(700);
        Assert.Equal(1, Volatile.Read(ref reports));
        Assert.False(held.IsHeld());
    }

    private static async Task KillLockSessionAsync(DatabaseConnectionOptions options, string databaseName)
    {
        if (options.Provider == DatabaseProvider.PostgreSql)
        {
            long key = ServerAdvisoryWriterLock.PostgresLockKey(databaseName);
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(options.ConnectionString) { Pooling = false, Database = CharacterDatabaseWriterLock.DefaultPostgresLockDatabase };
            await using var connection = new Npgsql.NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_terminate_backend(pid) FROM pg_locks WHERE locktype = 'advisory' AND objsubid = 1 AND classid::bigint = @hi AND objid::bigint = @lo";
            command.Parameters.AddWithValue("hi", (long)(uint)(key >> 32));
            command.Parameters.AddWithValue("lo", (long)(uint)key);
            Assert.NotNull(await command.ExecuteScalarAsync());
        }
        else
        {
            var builder = new MySqlConnector.MySqlConnectionStringBuilder(options.ConnectionString) { Pooling = false, Database = string.Empty };
            await using var connection = new MySqlConnector.MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT IS_USED_LOCK(@name)";
            command.Parameters.AddWithValue("@name", ServerAdvisoryWriterLock.MySqlLockName(databaseName));
            object? id = await command.ExecuteScalarAsync();
            Assert.NotNull(id);
            await using var kill = connection.CreateCommand();
            kill.CommandText = "KILL " + Convert.ToInt64(id, System.Globalization.CultureInfo.InvariantCulture);
            await kill.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void TheMariaDbLockName_IsCaseFolded()
        => Assert.Equal(ServerAdvisoryWriterLock.MySqlLockName("Characters_R1"), ServerAdvisoryWriterLock.MySqlLockName("characters_r1"));
}
