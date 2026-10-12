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
    public void TheFixtureIsOpenedReadOnly()
    {
        string path = WithVersion(51);
        DateTime before = File.GetLastWriteTimeUtc(path);
        long length = new FileInfo(path).Length;

        WorldFixtureSchema.SkipReason(path, 53);

        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        Assert.Equal(length, new FileInfo(path).Length);
    }
}
