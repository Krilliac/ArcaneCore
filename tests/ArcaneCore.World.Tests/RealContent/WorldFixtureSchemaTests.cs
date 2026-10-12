using Microsoft.Data.Sqlite;
using Xunit;

namespace ArcaneCore.World.Tests.RealContent;

public sealed class WorldFixtureSchemaTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "arcane-fixture-schema-" + Guid.NewGuid().ToString("N"));

    public WorldFixtureSchemaTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }

    private string Create(string sql)
    {
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
        return path;
    }

    private string Create(string sql, bool wal)
    {
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = (wal ? "PRAGMA journal_mode=WAL;" : "") + sql;
            command.ExecuteNonQuery();
        }
        return path;
    }

    private string WithVersion(int version)
        => Create($"CREATE TABLE world_schema (Id INTEGER PRIMARY KEY, Version INTEGER NOT NULL); INSERT INTO world_schema (Id, Version) VALUES (1, {version});");

    [Fact]
    public void OlderFixtureIsSkippedWithTheRefreshMessage()
    {
        string? reason = WorldFixtureSchema.SkipReason(WithVersion(51), 53);

        Assert.Equal("ARCANECORE_TEST_WORLD_DB schema 51 is older than the code's world schema 53; refresh the fixture from a live world.db.", reason);
    }

    [Fact]
    public void EqualFixtureIsNotSkipped() => Assert.Null(WorldFixtureSchema.SkipReason(WithVersion(53), 53));

    [Fact]
    public void NewerFixtureIsNotSkipped() => Assert.Null(WorldFixtureSchema.SkipReason(WithVersion(60), 53));

    [Fact]
    public void MissingWorldSchemaTableGivesAClearMessage()
    {
        string? reason = WorldFixtureSchema.SkipReason(Create("CREATE TABLE other (Id INTEGER);"), 53);

        Assert.NotNull(reason);
        Assert.Contains("no readable world_schema table", reason);
        Assert.Contains("refresh the fixture", reason);
    }

    [Fact]
    public void EmptyWorldSchemaTableGivesAClearMessage()
    {
        string? reason = WorldFixtureSchema.SkipReason(Create("CREATE TABLE world_schema (Id INTEGER PRIMARY KEY, Version INTEGER NOT NULL);"), 53);

        Assert.NotNull(reason);
        Assert.Contains("no world_schema version row", reason);
    }

    [Fact]
    public void AWalFixtureIsNeitherModifiedNorGivenSidecarFiles()
    {
        string path = Create("CREATE TABLE world_schema (Id INTEGER PRIMARY KEY, Version INTEGER NOT NULL); INSERT INTO world_schema (Id, Version) VALUES (1, 51);", wal: true);
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
        byte[] bytes = File.ReadAllBytes(path);
        DateTime modified = File.GetLastWriteTimeUtc(path);

        string? reason = WorldFixtureSchema.SkipReason(path, 53);

        Assert.Contains("schema 51 is older", reason);
        Assert.False(File.Exists(path + "-wal"), "the helper left a -wal file next to the fixture");
        Assert.False(File.Exists(path + "-shm"), "the helper left a -shm file next to the fixture");
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void AFixtureInAReadOnlyDirectoryStillReportsItsVersion()
    {
        string path = Create("CREATE TABLE world_schema (Id INTEGER PRIMARY KEY, Version INTEGER NOT NULL); INSERT INTO world_schema (Id, Version) VALUES (1, 53);", wal: true);
        var info = new FileInfo(path) { IsReadOnly = true };
        try
        {
            Assert.Null(WorldFixtureSchema.SkipReason(path, 53));
        }
        finally
        {
            info.IsReadOnly = false;
        }
    }

    [Fact]
    public void AFixtureStillBeingCreatedHasItsOwnMessage()
    {
        string? reason = WorldFixtureSchema.SkipReason(WithVersion(0), 53);

        Assert.NotNull(reason);
        Assert.Contains("still being created", reason);
    }

    [Fact]
    public void ANonDatabaseFileGivesAMessageInsteadOfThrowing()
    {
        string path = Path.Combine(directory, "garbage.db");
        File.WriteAllText(path, "this is not a sqlite database, just text padding padding padding padding padding padding");

        string? reason = WorldFixtureSchema.SkipReason(path, 53);

        Assert.NotNull(reason);
        Assert.Contains("refresh the fixture", reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Z:/no/such/world.db")]
    public void AnUnsetOrMissingFixtureIsLeftToTheOtherGates(string? path)
        => Assert.Null(WorldFixtureSchema.SkipReasonForPath(path, 53));

    [Fact]
    public void SkipReasonForPathReadsTheNamedFile()
        => Assert.Contains("schema 51 is older", WorldFixtureSchema.SkipReasonForPath(WithVersion(51), 53));
}
