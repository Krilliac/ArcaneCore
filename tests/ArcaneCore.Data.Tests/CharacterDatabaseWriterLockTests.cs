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

    /// <summary>The server providers available in this environment (MariaDB, PostgreSQL); empty when neither variable is set, which <see cref="ServerProviderTheoryAttribute"/> reports as Skipped.</summary>
    public static IEnumerable<object[]> ServerProviders()
        => TestDatabases.AvailableProviders().Where(row => (DatabaseProvider)row[0] != DatabaseProvider.Sqlite);

    [ServerProviderTheory]
    [MemberData(nameof(ServerProviders))]
    public async Task ALostLockConnection_IsDetectedByTheHealthCheck_AndReportedOnce(DatabaseProvider provider)
    {
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
        Assert.Equal(1, Volatile.Read(ref reports)); // one loss, one report, however many ticks follow (the lock is then re-taken: CharacterDatabaseWriterLockHardeningTests)
    }

    internal static async Task KillLockSessionAsync(DatabaseConnectionOptions options, string databaseName, string? shard = null)
    {
        if (options.Provider == DatabaseProvider.PostgreSql)
        {
            long key = ServerAdvisoryWriterLock.PostgresLockKey(databaseName, shard);
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
            command.Parameters.AddWithValue("@name", ServerAdvisoryWriterLock.MySqlLockName(databaseName, shard));
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

    // ---- Shard locks (design step C2 of _wf/writer-lock-DESIGN.md): one advisory identity per worker shard, the realm identity unchanged.

    [Fact]
    public void ShardLockKeysAndNames_ArePinned_AndTheRealmKeysAreUnchanged()
    {
        // Realm identity: no shard, exactly the values the single-lock design always produced.
        Assert.Equal("arcanecore.world.writer:characters_r1", ServerAdvisoryWriterLock.MySqlLockName("characters_r1", null));
        Assert.Equal(-5420518811949707971L, ServerAdvisoryWriterLock.PostgresLockKey("characters_r1"));
        Assert.Equal(-5420518811949707971L, ServerAdvisoryWriterLock.PostgresLockKey("characters_r1", null));

        // PostgreSQL: LE64(SHA256("arcanecore.world.writer:" + db + "#shard:" + shard)); the shard keeps its case.
        Assert.Equal(6206709683818362613L, ServerAdvisoryWriterLock.PostgresLockKey("characters_r1", "w1"));
        Assert.Equal(-6126584325180618514L, ServerAdvisoryWriterLock.PostgresLockKey("characters_r1", "w2"));
        Assert.Equal(8850108496697634045L, ServerAdvisoryWriterLock.PostgresLockKey("characters_r1", "W1"));

        // MariaDB/MySQL: prefix + lower(db) + "#shard:" + lower(shard), SHA-256 fallback (32 hex of the folded identity) past 64 characters.
        Assert.Equal("arcanecore.world.writer:characters_r1#shard:w1", ServerAdvisoryWriterLock.MySqlLockName("Characters_R1", "W1"));
        Assert.Equal("arcanecore.world.writer:fde3a7f9c50eab24a8e5ac758915bf75", ServerAdvisoryWriterLock.MySqlLockName(new string('x', 80), "w1"));
        Assert.Equal(64, ServerAdvisoryWriterLock.MySqlLockName("characters_r1", new string('w', 20)).Length); // exactly at the limit: not hashed
        Assert.NotEqual(ServerAdvisoryWriterLock.MySqlLockName("characters_r1", null), ServerAdvisoryWriterLock.MySqlLockName("characters_r1", "w1"));
    }

    [Fact]
    public void TheSqliteShardSidecar_IsPinned_AndIsNotTheRealmSidecar()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-writerlock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string database = Path.Combine(directory, "characters.db");
            DatabaseConnectionOptions options = Sqlite($"Data Source={database}");

            Assert.Equal(database + ".world.60c5590f72eef292.lock", CharacterDatabaseWriterLock.ShardLockPathFor(options, "w1")); // first 8 bytes of SHA-256("w1")
            Assert.Equal(database + ".world.06f8faea3b5f6976.lock", CharacterDatabaseWriterLock.ShardLockPathFor(options, "w2"));
            Assert.Equal(database + ".world.lock", CharacterDatabaseWriterLock.LockPathFor(options));
            Assert.Null(CharacterDatabaseWriterLock.ShardLockPathFor(Sqlite("Data Source=:memory:"), "w1"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("w1", true)]
    [InlineData("Worker:1.a_b-c", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("semi;colon", false)]
    [InlineData("quote'", false)]
    public void AShardId_MustMatchTheScopeAlphabet(string shard, bool valid)
        => Assert.Equal(valid, CharacterDatabaseWriterLock.IsValidShardId(shard));

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TheShardProbe_SeesOnlyThatShardsHolder_AndNeverKeepsTheLock_OnEveryProvider(DatabaseProvider provider)
    {
        // The liveness proof of the login-time reap (design _wf/writer-lock-DESIGN.md section 5.4): a shard is dead only when its own lock can be taken.
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions options = await databases.CreateAsync(provider);
        Assert.True(CharacterDatabaseWriterLock.ProbeShardFree(options, "w1"));

        using (CharacterDatabaseWriterLock w1 = CharacterDatabaseWriterLock.TryAcquireShard(options, "w1")!)
        {
            Assert.NotNull(w1);
            Assert.False(CharacterDatabaseWriterLock.ProbeShardFree(options, "w1"));
            Assert.True(CharacterDatabaseWriterLock.ProbeShardFree(options, "w2")); // another shard's lock is not w1's
        }

        Assert.True(CharacterDatabaseWriterLock.ProbeShardFree(options, "w1")); // released by Dispose
        Assert.True(CharacterDatabaseWriterLock.ProbeShardFree(options, "w1")); // and the probe itself gave nothing back wrongly: it can run twice
        using CharacterDatabaseWriterLock again = CharacterDatabaseWriterLock.TryAcquireShard(options, "w1")!; // a probe never keeps the lock
        Assert.NotNull(again);
    }

    [Fact]
    public void TheShardProbe_RefusesAnInvalidShardId()
        => Assert.Throws<ArgumentException>(() => CharacterDatabaseWriterLock.ProbeShardFree(Sqlite("Data Source=:memory:"), "bad shard"));

    [Fact]
    public void AShardId_IsLimitedTo57Characters_AndAnInvalidOneIsRefusedBeforeAnyLockIsTaken()
    {
        Assert.True(CharacterDatabaseWriterLock.IsValidShardId(new string('a', 57)));
        Assert.False(CharacterDatabaseWriterLock.IsValidShardId(new string('a', 58)));
        Assert.False(CharacterDatabaseWriterLock.IsValidShardId(null));
        Assert.Throws<ArgumentException>(() => CharacterDatabaseWriterLock.TryAcquireShard(Sqlite("Data Source=:memory:"), "bad shard"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TwoShardsCoexist_AndTheSameShardIsRefused_OnEveryProvider(DatabaseProvider provider)
    {
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions options = await databases.CreateAsync(provider);
        CharacterDatabaseWriterLock first = CharacterDatabaseWriterLock.TryAcquireShard(options, "w1")!;
        Assert.NotNull(first);
        try
        {
            using CharacterDatabaseWriterLock second = CharacterDatabaseWriterLock.TryAcquireShard(options, "w2")!;
            Assert.NotNull(second);
            Assert.NotEqual(first.Path, second.Path);

            CharacterDatabaseAlreadyOwnedException refused = Assert.Throws<CharacterDatabaseAlreadyOwnedException>(() => CharacterDatabaseWriterLock.TryAcquireShard(options, "w1"));
            Assert.Equal(first.Path, refused.LockPath);
        }
        finally { first.Dispose(); }

        using CharacterDatabaseWriterLock again = CharacterDatabaseWriterLock.TryAcquireShard(options, "w1")!; // released by Dispose
        Assert.NotNull(again);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AShardLock_AndTheRealmLock_AreIndependent_OnEveryProvider(DatabaseProvider provider)
    {
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions options = await databases.CreateAsync(provider);
        using CharacterDatabaseWriterLock shard = CharacterDatabaseWriterLock.TryAcquireShard(options, "w1")!;
        using CharacterDatabaseWriterLock realm = CharacterDatabaseWriterLock.TryAcquire(options)!; // the exclusion between them is the claim-then-probe step, not the locks
        Assert.NotNull(shard);
        Assert.NotNull(realm);
        Assert.NotEqual(shard.Path, realm.Path);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TheRealmProbe_SeesTheRealmHolder_AndNotAShardHolder_OnEveryProvider(DatabaseProvider provider)
    {
        await using var databases = new TestDatabases();
        DatabaseConnectionOptions options = await databases.CreateAsync(provider);
        Assert.True(CharacterDatabaseWriterLock.ProbeRealmFree(options));

        using (CharacterDatabaseWriterLock shard = CharacterDatabaseWriterLock.TryAcquireShard(options, "w1")!)
        {
            Assert.NotNull(shard);
            Assert.True(CharacterDatabaseWriterLock.ProbeRealmFree(options)); // a worker's own shard lock is not the realm lock
        }

        CharacterDatabaseWriterLock realm = CharacterDatabaseWriterLock.TryAcquire(options)!;
        Assert.NotNull(realm);
        try
        {
            Assert.False(CharacterDatabaseWriterLock.ProbeRealmFree(options));
            Assert.False(CharacterDatabaseWriterLock.ProbeRealmFree(options)); // probing never takes the lock, so it stays seen
        }
        finally { realm.Dispose(); }

        Assert.True(CharacterDatabaseWriterLock.ProbeRealmFree(options));
        using CharacterDatabaseWriterLock again = CharacterDatabaseWriterLock.TryAcquire(options)!; // the probe left nothing held
        Assert.NotNull(again);
    }

    [Fact]
    public void TheRealmProbe_IsTrueWhenTheDatabaseHasNoLock()
    {
        Assert.True(CharacterDatabaseWriterLock.ProbeRealmFree(Sqlite("Data Source=:memory:")));
        Assert.True(CharacterDatabaseWriterLock.ProbeRealmFree(new DatabaseConnectionOptions { Provider = DatabaseProvider.MariaDb, ConnectionString = "Server=h;User=u" }));
    }
}
