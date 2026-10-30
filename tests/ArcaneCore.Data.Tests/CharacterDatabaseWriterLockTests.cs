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
}
