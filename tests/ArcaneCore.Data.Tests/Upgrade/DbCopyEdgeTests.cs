using System.Data.Common;
using System.Globalization;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using ArcaneCore.Data.Schema.Upgrade.Copy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests.Upgrade;

/// <summary>
/// The failure modes of <c>arcane-db copy</c>: values SQLite stores differently from the server engines, a verification mismatch, a rollback,
/// refused conditions that need a live server, and secrets in the output. Every seeded source holds extreme values (see <see cref="DbCopyTestSupport.SeedAsync"/>).
/// </summary>
public sealed class DbCopyEdgeTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> AllTargets() => TestDatabases.AvailableProviders();

    public static IEnumerable<object[]> ServerTargets() => TestDatabases.AvailableProviders().Where(p => (DatabaseProvider)p[0] != DatabaseProvider.Sqlite);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private async Task<DatabaseConnectionOptions> SeededAsync(string component, int bulk = 5)
    {
        DatabaseConnectionOptions source = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await DbCopyTestSupport.SeedAsync(component, source, bulk);
        return source;
    }

    private static string[] Args(string component, DatabaseConnectionOptions target, params string[] more)
        => ["copy", "--component", component, "--target-provider", DbCopyTestSupport.ProviderFlag(target.Provider), "--target-connection", target.ConnectionString, .. more];

    private static async Task<(int Code, string Out, string Err)> RunAsync(DatabaseConnectionOptions source, string[] args, DbCopyHooks? hooks = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await DbUpgradeCli.RunAsync(
            args, new DatabaseOptions { Provider = source.Provider, ConnectionString = source.ConnectionString }, output, error, null, null, hooks, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    [Theory]
    [MemberData(nameof(AllTargets))]
    public async Task UnsignedSixtyFourBitColumns_KeepTheirValue_OnEveryEngine(DatabaseProvider targetProvider)
    {
        int checkedColumns = 0;
        foreach (string component in new[] { "auth", "characters", "world" })
        {
            (string Table, string Column)[] ulongColumns = DbCopyTestSupport.ColumnsOfType(component, typeof(ulong));
            DatabaseConnectionOptions source = await SeededAsync(component);
            DatabaseConnectionOptions target = await _databases.CreateAsync(targetProvider);
            (int code, string output, string error) = await RunAsync(source, Args(component, target));
            Assert.True(code == DbUpgradeExitCodes.Ok, $"{component}: exit {code}\n{output}\n{error}");

            foreach ((string table, string column) in ulongColumns)
            {
                string[] raw = await DbCopyTestSupport.RawColumnAsync(component, source, table, column);
                string[] landed = await DbCopyTestSupport.RawColumnAsync(component, target, table, column);
                // SQLite holds the 64 bits as a signed integer (negative from 2^63); PostgreSQL numeric and MariaDB bigint unsigned hold the unsigned value.
                string[] expected = [.. raw.Select(v => targetProvider == DatabaseProvider.Sqlite ? v : unchecked((ulong)long.Parse(v, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal)];
                Assert.True(expected.Length >= 3, $"{table}.{column} was not seeded");
                Assert.Equal(expected, landed);
                if (targetProvider != DatabaseProvider.Sqlite)
                {
                    Assert.Contains(0xF130000000000001UL.ToString(CultureInfo.InvariantCulture), landed);
                }

                checkedColumns++;
            }

            DbCopyTestSupport.ClearPool(target);
        }

        Assert.True(checkedColumns >= 4, $"expected the model's ulong columns (character_aura.CasterGuid, creator guids, spell family flags ...), found {checkedColumns}");
    }

    [Fact]
    public void TheUnsignedColumnsOfTheModel_AreFoundFromTheModel()
    {
        string[] found = [.. new[] { "auth", "characters", "world" }.SelectMany(c => DbCopyTestSupport.ColumnsOfType(c, typeof(ulong)).Select(x => $"{x.Table}.{x.Column}"))];

        Assert.Contains(found, f => f.EndsWith(".CasterGuid", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".caster_guid", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(AllTargets))]
    public async Task SmallBatches_ByRowsAndBytes_GiveTheSameDatabase(DatabaseProvider targetProvider)
    {
        DatabaseConnectionOptions source = await SeededAsync("auth", bulk: 130);
        DatabaseConnectionOptions target = await _databases.CreateAsync(targetProvider);

        (int code, string output, string error) = await RunAsync(source, Args("auth", target), new DbCopyHooks { MaxBatchRows = 7, MaxBatchBytes = 100 });

        Assert.True(code == DbUpgradeExitCodes.Ok, $"exit {code}\n{output}\n{error}");
        Dictionary<string, string[]> expected = await DbCopyTestSupport.DumpAsync("auth", source);
        Dictionary<string, string[]> actual = await DbCopyTestSupport.DumpAsync("auth", target);
        foreach (string table in expected.Keys)
        {
            Assert.True(expected[table].SequenceEqual(actual[table], StringComparer.Ordinal), $"table {table} differs");
        }

        DbCopyTestSupport.ClearPool(target);
    }

    [Theory]
    [MemberData(nameof(AllTargets))]
    public async Task SqliteAffinityOddities_AreCopiedAsTheirModelValues(DatabaseProvider targetProvider)
    {
        // A bool column holding 2 (SQLite stores whatever integer it is given).
        string? component = null;
        (string Table, string Column) boolColumn = default;
        foreach (string candidate in new[] { "auth", "characters", "world" })
        {
            (string Table, string Column)[] bools = DbCopyTestSupport.ColumnsOfType(candidate, typeof(bool));
            if (bools.Length > 0)
            {
                component = candidate;
                boolColumn = bools[0];
                break;
            }
        }

        Assert.NotNull(component);
        DatabaseConnectionOptions source = await SeededAsync(component);
        await DbCopyTestSupport.ExecuteSqliteAsync(source, $"UPDATE \"{boolColumn.Table}\" SET \"{boolColumn.Column}\" = 2 WHERE \"{boolColumn.Column}\" = 0");
        DatabaseConnectionOptions target = await _databases.CreateAsync(targetProvider);

        (int code, string output, string error) = await RunAsync(source, Args(component, target));

        Assert.True(code == DbUpgradeExitCodes.Ok, $"exit {code}\n{output}\n{error}");
        Dictionary<string, string[]> expected = await DbCopyTestSupport.DumpAsync(component, source);
        Dictionary<string, string[]> actual = await DbCopyTestSupport.DumpAsync(component, target);
        foreach (string table in expected.Keys.Where(t => t != boolColumn.Table))
        {
            Assert.True(expected[table].SequenceEqual(actual[table], StringComparer.Ordinal), $"table {table} differs");
        }

        // The oddity itself: every row that held a non-zero integer is true on a server target (on SQLite the integer is kept as it was).
        string[] landed = await DbCopyTestSupport.RawColumnAsync(component!, target, boolColumn.Table!, boolColumn.Column!);
        string[] original = await DbCopyTestSupport.RawColumnAsync(component!, source, boolColumn.Table!, boolColumn.Column!);
        Assert.Equal(original.Length, landed.Length);
        Assert.Equal(original.Count(v => v != "0"), landed.Count(v => v is not ("0" or "False")));
        DbCopyTestSupport.ClearPool(target);
    }

    [Fact]
    public async Task AVerificationMismatch_ExitsNine_AndRollsTheTargetBack()
    {
        DatabaseConnectionOptions source = await SeededAsync("auth");
        DatabaseConnectionOptions target = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        var tamper = new DbCopyHooks
        {
            AfterInsert = async (db, ct) =>
            {
                await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
                command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
                command.CommandText = "UPDATE \"realmlist\" SET \"Name\" = 'tampered'";
                await command.ExecuteNonQueryAsync(ct);
            },
        };

        (int code, string output, string error) = await RunAsync(source, Args("auth", target), tamper);

        Assert.Equal(DbUpgradeExitCodes.CopyVerifyFailed, code);
        Assert.Contains("CONTENT DIFFERS", output, StringComparison.Ordinal);
        Assert.Contains("rolled back", error, StringComparison.Ordinal);
        Assert.DoesNotContain("copy complete", output, StringComparison.Ordinal);
        Assert.Equal(await DbCopyTestSupport.FreshCountsAsync(_databases, "auth"), await DbCopyTestSupport.CountsAsync("auth", target));
    }

    [Fact]
    public async Task AFailureAfterEveryRowWasInserted_RollsTheTargetBack()
    {
        DatabaseConnectionOptions source = await SeededAsync("auth");
        DatabaseConnectionOptions target = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        var fail = new DbCopyHooks { AfterInsert = (_, _) => throw new InvalidOperationException("forced failure") };

        (int code, _, string error) = await RunAsync(source, Args("auth", target), fail);

        Assert.Equal(DbUpgradeExitCodes.Failure, code);
        Assert.Contains("forced failure", error, StringComparison.Ordinal);
        Assert.Equal(await DbCopyTestSupport.FreshCountsAsync(_databases, "auth"), await DbCopyTestSupport.CountsAsync("auth", target));
    }

    [ServerProviderTheory]
    [MemberData(nameof(ServerTargets))]
    public async Task AnUnconvertibleSourceValue_NamesTableColumnAndRow_AndLeavesTheTargetAsItWas(DatabaseProvider targetProvider)
    {
        DatabaseConnectionOptions source = await SeededAsync("auth");
        await DbCopyTestSupport.ExecuteSqliteAsync(source, "UPDATE \"realmlist\" SET \"Type\" = 'abc' WHERE \"Id\" = (SELECT MAX(\"Id\") FROM \"realmlist\")");
        DatabaseConnectionOptions target = await _databases.CreateAsync(targetProvider);

        (int code, _, string error) = await RunAsync(source, Args("auth", target));

        Assert.Equal(DbUpgradeExitCodes.Failure, code);
        Assert.Contains("realmlist", error, StringComparison.Ordinal);
        Assert.Contains("'Type'", error, StringComparison.Ordinal);
        Assert.Contains("[Id=", error, StringComparison.Ordinal);
        Assert.Contains("rolled back", error, StringComparison.Ordinal);
        DbCopyTestSupport.ClearPool(target);
        Assert.Equal(await DbCopyTestSupport.FreshCountsAsync(_databases, "auth"), await DbCopyTestSupport.CountsAsync("auth", target));
        DbCopyTestSupport.ClearPool(target);
    }

    [PostgresFact]
    public async Task ANulCharacterInText_IsRefusedLoudlyOnPostgreSql_NamingColumnAndRow()
    {
        DatabaseConnectionOptions source = await SeededAsync("auth");
        await DbCopyTestSupport.ExecuteSqliteAsync(source, "UPDATE \"realmlist\" SET \"Name\" = 'a' || char(0) || 'b' WHERE \"Id\" = (SELECT MAX(\"Id\") FROM \"realmlist\")");
        DatabaseConnectionOptions target = await _databases.CreateAsync(DatabaseProvider.PostgreSql);

        (int code, _, string error) = await RunAsync(source, Args("auth", target));

        Assert.Equal(DbUpgradeExitCodes.Failure, code);
        Assert.Contains("NUL", error, StringComparison.Ordinal);
        Assert.Contains("'Name'", error, StringComparison.Ordinal);
        Assert.Contains("[Id=", error, StringComparison.Ordinal);
        DbCopyTestSupport.ClearPool(target);
        Assert.Equal(await DbCopyTestSupport.FreshCountsAsync(_databases, "auth"), await DbCopyTestSupport.CountsAsync("auth", target));
        DbCopyTestSupport.ClearPool(target);
    }

    [ServerProviderTheory]
    [MemberData(nameof(ServerTargets))]
    public async Task OtherSessionsOnTheTarget_RefuseTheCopy_UnlessAllowed(DatabaseProvider targetProvider)
    {
        DatabaseConnectionOptions source = await SeededAsync("auth");
        DatabaseConnectionOptions target = await _databases.CreateAsync(targetProvider);
        Assert.Equal(DbUpgradeExitCodes.Ok, (await RunAsync(source, Args("auth", target))).Code);
        DbCopyTestSupport.ClearPool(target);

        await using (DbConnection other = await DbCopyTestSupport.HoldSessionAsync(target))
        {
            (int code, _, string error) = await RunAsync(source, Args("auth", target, "--replace"));
            Assert.Equal(DbUpgradeExitCodes.Refused, code);
            Assert.Contains("other session", error, StringComparison.Ordinal);
            Assert.Contains("nothing was changed", error, StringComparison.Ordinal);

            (int allowed, string output, string error2) = await RunAsync(source, Args("auth", target, "--replace", "--allow-active-sessions"));
            Assert.True(allowed == DbUpgradeExitCodes.Ok, $"exit {allowed}\n{output}\n{error2}");
        }

        DbCopyTestSupport.ClearPool(target);
    }

    [ServerProviderTheory]
    [MemberData(nameof(ServerTargets))]
    public async Task ARealServerPassword_NeverAppearsInAnyOutput(DatabaseProvider targetProvider)
    {
        DatabaseConnectionOptions source = await SeededAsync("auth");
        DatabaseConnectionOptions target = await _databases.CreateAsync(targetProvider);
        string? password = DbCopyTestSupport.PasswordOf(target);
        Assert.False(string.IsNullOrEmpty(password), "the configured test server connection has no password to look for");
        var seen = new List<string>();

        (int ok, string o1, string e1) = await RunAsync(source, Args("auth", target));
        seen.Add(o1 + e1);
        DbCopyTestSupport.ClearPool(target);
        (int refused, string o2, string e2) = await RunAsync(source, Args("auth", target)); // not empty
        seen.Add(o2 + e2);
        (int usage, string o3, string e3) = await RunAsync(source, Args("auth", target, "--bogus"));
        seen.Add(o3 + e3);
        (int wrong, string o4, string e4) = await RunAsync(source, ["copy", "--component", "auth", "--target-provider", "oracle", "--target-connection", target.ConnectionString]);
        seen.Add(o4 + e4);
        var unreachable = new DatabaseConnectionOptions { Provider = target.Provider, ConnectionString = target.ConnectionString + ";Port=1;Timeout=2;Connection Timeout=2" };
        (int down, string o5, string e5) = await RunAsync(source, Args("auth", unreachable));
        seen.Add(o5 + e5);

        Assert.Equal([DbUpgradeExitCodes.Ok, DbUpgradeExitCodes.Refused, DbUpgradeExitCodes.Usage, DbUpgradeExitCodes.Usage], [ok, refused, usage, wrong]);
        Assert.NotEqual(DbUpgradeExitCodes.Ok, down);
        Assert.All(seen, text => Assert.DoesNotContain(password!, text, StringComparison.Ordinal));
        DbCopyTestSupport.ClearPool(target);
    }

    [Fact]
    public async Task TheSameServerDatabase_NamedThroughTwoLoopbackSpellings_IsRefusedAsTheSameDatabase()
    {
        DatabaseConnectionOptions source = await SeededAsync("auth");
        string[] args =
        [
            "copy", "--component", "auth",
            "--source-provider", "postgresql", "--source-connection", "Host=localhost;Port=5432;Database=realm;Username=a;Password=p;",
            "--target-provider", "postgresql", "--target-connection", "Host=127.0.0.1;Database=realm;Username=a;Password=p;",
        ];

        (int code, _, string error) = await RunAsync(source, args);

        Assert.Equal(DbUpgradeExitCodes.Usage, code);
        Assert.Contains("same database", error, StringComparison.Ordinal);
    }
}
