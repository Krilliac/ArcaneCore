using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Upgrade;

/// <summary>The backup gate: command lines without secrets, and a verified SQLite copy that never lands in a repository.</summary>
public sealed class BackupAdvisorTests : IAsyncLifetime
{
    private const string Secret = "hunter2;sw0rdfish";
    private readonly TestDatabases _databases = new();
    private readonly List<string> _directories = [];

    [Theory]
    [InlineData(DatabaseProvider.MariaDb, "Server=db.example;Port=3307;Database=arcane_chars;User ID=arcane;Password=\"hunter2;sw0rdfish\"", "mysqldump", "MYSQL_PWD")]
    [InlineData(DatabaseProvider.MySql, "Server=db.example;Database=arcane_chars;User ID=arcane;Password=\"hunter2;sw0rdfish\"", "mysqldump", "MYSQL_PWD")]
    [InlineData(DatabaseProvider.PostgreSql, "Host=db.example;Port=5433;Database=arcane_chars;Username=arcane;Password=\"hunter2;sw0rdfish\"", "pg_dump", "PGPASSWORD")]
    public void ServerBackup_NamesTheTool_HostPortDatabase_AndNeverTheSecret(DatabaseProvider provider, string connectionString, string tool, string variable)
    {
        BackupAdvisor.BackupInstructions instructions = BackupAdvisor.Describe(
            new DatabaseConnectionOptions { Provider = provider, ConnectionString = connectionString }, "out.bak");

        Assert.StartsWith(tool, instructions.Command, StringComparison.Ordinal);
        Assert.Equal(variable, instructions.PasswordVariable);
        Assert.Equal("db.example", instructions.Host);
        Assert.Equal("arcane_chars", instructions.Database);
        Assert.Contains("--host=db.example", instructions.Command, StringComparison.Ordinal);
        Assert.Contains(provider == DatabaseProvider.PostgreSql ? "--username=arcane" : "--user=arcane", instructions.Command, StringComparison.Ordinal);
        Assert.Contains("arcane_chars", instructions.Command, StringComparison.Ordinal);
        Assert.Contains("out.bak", instructions.Command, StringComparison.Ordinal);
        if (provider != DatabaseProvider.PostgreSql)
        {
            Assert.Contains("--single-transaction", instructions.Command, StringComparison.Ordinal);
            Assert.Contains("--quick", instructions.Command, StringComparison.Ordinal);
            Assert.Contains("--order-by-primary", instructions.Command, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("--format=custom", instructions.Command, StringComparison.Ordinal);
        }

        bool customPort = connectionString.Contains("Port=", StringComparison.Ordinal);
        Assert.Equal(customPort, instructions.Command!.Contains("--port=", StringComparison.Ordinal));

        string everything = string.Join('\n', instructions.Command, instructions.Host, instructions.Database, instructions.Note, instructions.PasswordVariable);
        Assert.DoesNotContain("hunter2", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("sw0rdfish", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("--password", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("-p", instructions.Command.Split(' '));
        Assert.DoesNotContain(Secret, everything, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerBackup_AcceptsAnIpv6HostAndAnAwkwardDatabaseName()
    {
        BackupAdvisor.BackupInstructions maria = BackupAdvisor.Describe(new DatabaseConnectionOptions
        {
            Provider = DatabaseProvider.MariaDb,
            ConnectionString = "Server=::1;Database=my db;User ID=o'brien;Password=x",
        }, "o.sql");
        Assert.Equal("::1", maria.Host);
        Assert.Contains("--host=::1", maria.Command, StringComparison.Ordinal);
        Assert.Contains("\"my db\"", maria.Command, StringComparison.Ordinal);
        Assert.Contains("--user=\"o'brien\"", maria.Command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqliteBackup_IsAVerifiedCopy_WithEveryRowAndTheVersionRow()
    {
        DatabaseConnectionOptions connection = await UpgradeTestSupport.NewBaselineAsync(_databases, CandidateBaseline.Variant.HistoricUpgraded);
        string sourcePath = UpgradeTestSupport.SqlitePath(connection);
        string directory = NewDirectory();

        string backup = await BackupAdvisor.BackupSqliteAsync(connection, directory);

        Assert.True(File.Exists(backup));
        Assert.Equal(directory, Path.GetDirectoryName(backup));
        IReadOnlyDictionary<string, string[]> tables = await CandidateBaseline.TablesAsync(sourcePath);
        Assert.True(tables.Count >= 40);
        var copy = new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = $"Data Source={backup};Pooling=False" };
        await using var original = new DbContext(TestContexts.Options<DbContext>(connection));
        await using var copied = new DbContext(TestContexts.Options<DbContext>(copy));
        foreach ((string table, string[] columns) in tables)
        {
            Assert.Equal(await SchemaProbe.SnapshotAsync(original, table, columns), await SchemaProbe.SnapshotAsync(copied, table, columns));
        }

        Assert.Equal(CandidateBaseline.CharactersVersion, await UpgradeTestSupport.ReadVersionAsync(copied, "characters"));
    }

    [Fact]
    public async Task SqliteBackup_NeverOverwrites_AndCreatesAMissingDirectory()
    {
        DatabaseConnectionOptions connection = await UpgradeTestSupport.NewBaselineAsync(_databases, CandidateBaseline.Variant.HistoricUpgraded);
        string directory = Path.Combine(NewDirectory(), "nested", "deeper");

        string first = await BackupAdvisor.BackupSqliteAsync(connection, directory, "one.db");
        Assert.True(File.Exists(first));
        byte[] before = await File.ReadAllBytesAsync(first);

        BackupException ex = await Assert.ThrowsAsync<BackupException>(() => BackupAdvisor.BackupSqliteAsync(connection, directory, "one.db"));
        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(first));

        await Assert.ThrowsAsync<BackupException>(() => BackupAdvisor.BackupSqliteAsync(connection, directory, Path.Combine("..", "x.db")));
    }

    [Fact]
    public async Task SqliteBackup_IntoAnUnignoredGitWorkTree_IsRefused_AndNothingIsWritten()
    {
        DatabaseConnectionOptions connection = await UpgradeTestSupport.NewBaselineAsync(_databases, CandidateBaseline.Variant.HistoricUpgraded);
        string? root = FindWorkTreeRoot();
        if (root is null)
        {
            return; // built from an exported source tree: there is no repository to protect here
        }

        string directory = Path.Combine(root, "backup-guard-test-" + Guid.NewGuid().ToString("N"));
        BackupException ex = await Assert.ThrowsAsync<BackupException>(() => BackupAdvisor.BackupSqliteAsync(connection, directory, "player-data.db"));

        Assert.Contains("git", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task SqliteBackup_OfAMissingOrInMemoryDatabase_IsAnError_NotAnEmptyBackup()
    {
        string directory = NewDirectory();
        string missing = Path.Combine(NewDirectory(), "absent.db");
        BackupException absent = await Assert.ThrowsAsync<BackupException>(() => BackupAdvisor.BackupSqliteAsync(
            new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = $"Data Source={missing}" }, directory));
        Assert.Contains("does not exist", absent.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(missing)); // not created by the attempt
        Assert.Empty(Directory.GetFiles(directory));

        await Assert.ThrowsAsync<BackupException>(() => BackupAdvisor.BackupSqliteAsync(
            new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=:memory:" }, directory));
        await Assert.ThrowsAsync<BackupException>(() => BackupAdvisor.BackupSqliteAsync(
            new DatabaseConnectionOptions { Provider = DatabaseProvider.MariaDb, ConnectionString = "Server=x;Database=y" }, directory));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _databases.DisposeAsync();
        foreach (string directory in _directories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    private string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        return directory;
    }

    private static string? FindWorkTreeRoot()
        => RepositorySource.FindRoot(requireGit: true);
}
