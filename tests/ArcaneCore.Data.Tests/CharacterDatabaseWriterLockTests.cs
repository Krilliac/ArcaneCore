using ArcaneCore.Data.Characters.Ownership;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The sidecar lock that keeps a second world process off a file-backed SQLite characters database. A file opened <see cref="FileShare.None"/> refuses a second opener
/// in the same process exactly as in another one, so these tests need no child process. Only SQLite is exercised here: the other providers get no lock by design.
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
    public void ServerDatabasesAndMissingDirectories_GetNoLock()
    {
        foreach (DatabaseProvider provider in new[] { DatabaseProvider.MariaDb, DatabaseProvider.MySql, DatabaseProvider.PostgreSql })
            Assert.Null(CharacterDatabaseWriterLock.TryAcquire(new DatabaseConnectionOptions { Provider = provider, ConnectionString = "Data Source=characters.db" }));

        string missing = Path.Combine(Path.GetTempPath(), "arcanecore-writerlock-missing-" + Guid.NewGuid().ToString("N"), "characters.db");
        Assert.Null(CharacterDatabaseWriterLock.TryAcquire(Sqlite($"Data Source={missing}")));
    }
}
