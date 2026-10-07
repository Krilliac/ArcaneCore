using ArcaneCore.Data.Tests.Upgrade;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Cli;

public sealed class MapAreaPoolIsolationTests
{
    [Fact]
    public void ImportFixtureDisposalDoesNotCheckpointAnotherFixturesPooledWal()
    {
        string file = Path.Combine(Path.GetTempPath(), "arcane-pool-isolation-" + Guid.NewGuid().ToString("N") + ".db");
        string connectionString = "Data Source=" + file;
        try
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE probe(id INTEGER); INSERT INTO probe VALUES(1);";
                command.ExecuteNonQuery();
            }
            Assert.True(File.Exists(file + "-wal"));
            string before = UpgradeTestSupport.FileFingerprint(file);
            using (var unrelated = new MapAreaDbcCliTests()) { }
            Assert.Equal(before, UpgradeTestSupport.FileFingerprint(file));
        }
        finally
        {
            using var connection = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(connection);
            foreach (string path in new[] { file, file + "-wal", file + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}
