using Microsoft.Data.Sqlite;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Every test class owns a <see cref="TestDatabases"/> and xunit runs classes in parallel, so one
/// fixture's disposal must only ever touch the databases that fixture created. Clearing every
/// connection pool in the process (<c>SqliteConnection.ClearAllPools</c>) closes pooled handles
/// that a different class is acquiring at that moment, which surfaces as a random
/// "database is locked" or disposed-SafeHandle failure in an unrelated test.
/// </summary>
public sealed class TestDatabasesIsolationTests
{
    private const int Lifecycles = 8;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(4);

    [Fact]
    public async Task ParallelFixtureLifecycles_NeverDisturbEachOther()
    {
        using var stop = new CancellationTokenSource(Duration);
        long completed = 0;

        // Each loop is what one test class does: create a fresh database (a new pool), use it in
        // a transaction, then dispose the fixture. Disposal must leave the other loops alone and
        // must release the files it created.
        Task[] lifecycles = Enumerable.Range(0, Lifecycles).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                string? directory;
                await using (var databases = new TestDatabases())
                {
                    DatabaseConnectionOptions db = await databases.CreateAsync(DatabaseProvider.Sqlite);
                    directory = Path.GetDirectoryName(new SqliteConnectionStringBuilder(db.ConnectionString).DataSource);

                    using var connection = new SqliteConnection(db.ConnectionString);
                    connection.Open();
                    using SqliteTransaction transaction = connection.BeginTransaction();
                    Execute(connection, transaction, "CREATE TABLE t (id INTEGER PRIMARY KEY, v INTEGER NOT NULL)");
                    Execute(connection, transaction, "INSERT INTO t (v) VALUES (1)");
                    transaction.Commit();
                }

                Assert.False(Directory.Exists(directory), $"fixture disposal left {directory} behind");
                Interlocked.Increment(ref completed);
            }
        })).ToArray();

        await Task.WhenAll(lifecycles);

        Assert.True(Interlocked.Read(ref completed) > 0, "no fixture lifecycle completed");
    }

    /// <summary>
    /// No test in this assembly clears every SQLite pool of the process. Closing another class's last pooled handle on
    /// a WAL database checkpoints it, so a "writes nothing" fingerprint in that class sees its file rewritten with the
    /// same content (SchemaPlannerTests, 2026-10-07, when CodexLineMigrationTests and the resilience tests did it).
    /// Use <see cref="TestDatabases.ClearSqlitePool"/> with the test's own connection string.
    /// </summary>
    [Fact]
    public void NoGlobalSqlitePoolClearing()
    {
        string? checkout = RepositorySource.FindRoot();
        string? root = checkout is null ? null : Path.Combine(checkout, "tests", "ArcaneCore.Data.Tests");
        Assert.True(root is not null && File.Exists(Path.Combine(root, "ArcaneCore.Data.Tests.csproj")),
            "the ArcaneCore.Data.Tests sources were not found in the ArcaneCore checkout");
        var call = new System.Text.RegularExpressions.Regex(@"\.ClearAllPools\s*\(");
        string separator = Path.DirectorySeparatorChar.ToString();
        string[] sources = [.. Directory.EnumerateFiles(root!, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(separator + "obj" + separator, StringComparison.Ordinal)
                && !p.Contains(separator + "bin" + separator, StringComparison.Ordinal))];
        Assert.True(sources.Length > 100, $"only {sources.Length} test sources scanned under {root}");

        string[] offenders = [.. sources.SelectMany(p => File.ReadLines(p).Select((line, i) => (Path: p, Line: line, Number: i + 1)))
            .Where(x => !x.Line.TrimStart().StartsWith("//", StringComparison.Ordinal) && call.IsMatch(x.Line))
            .Select(x => $"{Path.GetRelativePath(root!, x.Path)}:{x.Number}")];
        Assert.True(offenders.Length == 0, "SqliteConnection.ClearAllPools in: " + string.Join(", ", offenders));
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
