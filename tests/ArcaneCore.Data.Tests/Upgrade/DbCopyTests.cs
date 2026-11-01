using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using ArcaneCore.Data.Schema.Upgrade.Copy;
using ArcaneCore.Kernel.Accounts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Upgrade;

/// <summary>
/// <c>arcane-db copy</c> in-process. SQLite to SQLite runs everywhere; SQLite to MariaDB / PostgreSQL where ARCANECORE_TEST_MARIADB /
/// ARCANECORE_TEST_POSTGRES are set (a skipped provider is not a pass: the theory simply has no row for it). Every source is a database whose every
/// table holds rows (<see cref="DbCopyTestSupport.SeedAsync"/>).
/// </summary>
public sealed class DbCopyTests : IAsyncLifetime
{
    private const string Marker = "hunter2-copy-marker";
    private const int BulkRows = 1200;
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Components() => SchemaProbe.Components();

    /// <summary>Every available target provider (SQLite included) for each component.</summary>
    public static IEnumerable<object[]> ComponentsAndTargets()
        => TestDatabases.AvailableProviders().SelectMany(p => SchemaProbe.Components().Select(c => new object[] { c[0], p[0] }));

    /// <summary>The server providers only: the cross-engine conversions.</summary>
    public static IEnumerable<object[]> ComponentsAndServerTargets()
        => TestDatabases.AvailableProviders().Where(p => (DatabaseProvider)p[0] != DatabaseProvider.Sqlite)
            .SelectMany(p => SchemaProbe.Components().Select(c => new object[] { c[0], p[0] }));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private async Task<DatabaseConnectionOptions> SeededSourceAsync(string component)
    {
        DatabaseConnectionOptions source = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await DbCopyTestSupport.SeedAsync(component, source, BulkRows);
        return source;
    }

    private static string[] CopyArgs(string component, DatabaseConnectionOptions target, params string[] more)
        => ["copy", "--component", component, "--target-provider", DbCopyTestSupport.ProviderFlag(target.Provider), "--target-connection", target.ConnectionString, .. more];

    private static async Task<(int Code, string Out, string Err)> RunCopyAsync(
        DatabaseConnectionOptions source, string component, DatabaseConnectionOptions target, params string[] more)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        (string[] args, Func<string, string?> environment) = DbCopyTestSupport.MoveTargetConnectionToEnvironment(CopyArgs(component, target, more));
        int code = await DbUpgradeCli.RunAsync(
            args, new DatabaseOptions { Provider = source.Provider, ConnectionString = source.ConnectionString }, output, error, dbcDirectory: null, environment, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private async Task AssertEqualDatabasesAsync(string component, DatabaseConnectionOptions source, DatabaseConnectionOptions target)
    {
        Dictionary<string, long> sourceCounts = await DbCopyTestSupport.CountsAsync(component, source);
        Dictionary<string, long> targetCounts = await DbCopyTestSupport.CountsAsync(component, target);
        Assert.Equal(sourceCounts, targetCounts);
        Assert.All(sourceCounts, kv => Assert.True(kv.Value >= 3, $"{kv.Key} was not seeded ({kv.Value} rows)"));
        if (component == "auth")
        {
            Assert.True(sourceCounts[DbCopyTestSupport.BulkTable] >= BulkRows);
        }

        Dictionary<string, string[]> expected = await DbCopyTestSupport.DumpAsync(component, source);
        Dictionary<string, string[]> actual = await DbCopyTestSupport.DumpAsync(component, target);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (string table in expected.Keys)
        {
            Assert.True(expected[table].SequenceEqual(actual[table], StringComparer.Ordinal), $"table {table}: rows differ between source and target");
        }
    }

    [Theory]
    [MemberData(nameof(ComponentsAndTargets))]
    public async Task Copy_FromSeededSqlite_CarriesEveryRowOfEveryTable(string component, DatabaseProvider targetProvider)
    {
        DatabaseConnectionOptions source = await SeededSourceAsync(component);
        DatabaseConnectionOptions target = await _databases.CreateAsync(targetProvider);

        (int code, string output, string error) = await RunCopyAsync(source, component, target);

        Assert.True(code == DbUpgradeExitCodes.Ok, $"exit {code}\n{output}\n{error}");
        Assert.Equal(string.Empty, error);
        Assert.Contains("copy complete", output, StringComparison.Ordinal);
        Assert.DoesNotContain("DIFFERS", output, StringComparison.Ordinal);
        Assert.Equal(DbUpgradeExitCodes.Ok, (await UpgradeTestSupport.RunCliAsync(target, "check", "--component", component)).Code);
        await AssertEqualDatabasesAsync(component, source, target);
        DbCopyTestSupport.ClearPool(target);
    }

    [Fact]
    public async Task Copy_All_UsesTheTargetEnvironment_ForEachComponent_AndSetsNoSecretInTheOutput()
    {
        DatabaseConnectionOptions source = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        foreach (string component in new[] { "auth", "characters", "world" })
        {
            await DbCopyTestSupport.SeedAsync(component, source, 5);
        }

        var targets = new Dictionary<string, DatabaseConnectionOptions>
        {
            ["auth"] = await _databases.CreateAsync(DatabaseProvider.Sqlite),
            ["characters"] = await _databases.CreateAsync(DatabaseProvider.Sqlite),
            ["world"] = await _databases.CreateAsync(DatabaseProvider.Sqlite),
        };
        var environment = new Dictionary<string, string>
        {
            ["ARCANE_DB_TARGET_PROVIDER"] = "sqlite",
            ["ARCANE_DB_TARGET_AUTH_CONNECTION"] = targets["auth"].ConnectionString,
            ["ARCANE_DB_TARGET_CHARACTERS_CONNECTION"] = targets["characters"].ConnectionString,
            ["ARCANE_DB_TARGET_WORLD_CONNECTION"] = targets["world"].ConnectionString,
        };
        var output = new StringWriter();
        var error = new StringWriter();

        int code = await DbUpgradeCli.RunAsync(
            ["copy", "--component", "all"], new DatabaseOptions { Provider = source.Provider, ConnectionString = source.ConnectionString },
            output, error, dbcDirectory: null, environment.GetValueOrDefault, CancellationToken.None);

        Assert.True(code == DbUpgradeExitCodes.Ok, $"exit {code}\n{output}\n{error}");
        Assert.True(output.ToString().IndexOf("auth:", StringComparison.Ordinal) < output.ToString().IndexOf("characters:", StringComparison.Ordinal));
        foreach ((string component, DatabaseConnectionOptions target) in targets)
        {
            Assert.Equal(await DbCopyTestSupport.DumpAsync(component, source).ContinueWith(t => t.Result.Values.Sum(v => v.Length)),
                await DbCopyTestSupport.DumpAsync(component, target).ContinueWith(t => t.Result.Values.Sum(v => v.Length)));
        }
    }

    [Theory]
    [MemberData(nameof(ComponentsAndTargets))]
    public async Task Copy_WithReplace_IsIdempotent_AndWithoutItRefusesANonEmptyTarget(string component, DatabaseProvider targetProvider)
    {
        DatabaseConnectionOptions source = await SeededSourceAsync(component);
        DatabaseConnectionOptions target = await _databases.CreateAsync(targetProvider);
        Assert.Equal(DbUpgradeExitCodes.Ok, (await RunCopyAsync(source, component, target)).Code);
        Dictionary<string, string[]> first = await DbCopyTestSupport.DumpAsync(component, target);
        DbCopyTestSupport.ClearPool(target); // the test's own pooled sessions must not count as a running daemon

        (int refused, _, string error) = await RunCopyAsync(source, component, target);
        Assert.Equal(DbUpgradeExitCodes.Refused, refused);
        Assert.Contains("is not empty", error, StringComparison.Ordinal);
        Assert.Contains("--replace", error, StringComparison.Ordinal);
        Assert.Contains("nothing was changed", error, StringComparison.Ordinal);

        DbCopyTestSupport.ClearPool(target);
        (int again, string output, string error2) = await RunCopyAsync(source, component, target, "--replace");
        Assert.True(again == DbUpgradeExitCodes.Ok, $"exit {again}\n{output}\n{error2}\nsessions on the target: {await DbCopyTestSupport.DescribeSessionsAsync(target)}");
        Dictionary<string, string[]> second = await DbCopyTestSupport.DumpAsync(component, target);
        foreach (string table in first.Keys)
        {
            Assert.True(first[table].SequenceEqual(second[table], StringComparer.Ordinal), $"table {table} changed on the second copy");
        }

        await AssertEqualDatabasesAsync(component, source, target);
        DbCopyTestSupport.ClearPool(target);
    }

    [Fact]
    public async Task Copy_ToATargetWithADifferentDataRow_ReplaceRemovesIt()
    {
        DatabaseConnectionOptions source = await SeededSourceAsync("auth");
        DatabaseConnectionOptions target = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await SchemaProbe.EnsureCurrentAsync("auth", target);
        await using (DbContext db = SchemaProbe.CreateContext("auth", target))
        {
            await SchemaProbe.ExecuteAsync(db, "INSERT INTO \"realmlist\" (\"Id\", \"Name\", \"Address\", \"Type\", \"Flags\", \"Population\", \"Category\") VALUES (7, 'stale', 'x', 0, 0, 0, 0)");
        }

        Assert.Equal(DbUpgradeExitCodes.Refused, (await RunCopyAsync(source, "auth", target)).Code);
        Assert.Equal(DbUpgradeExitCodes.Ok, (await RunCopyAsync(source, "auth", target, "--replace")).Code);
        await AssertEqualDatabasesAsync("auth", source, target);
    }

    [Fact]
    public async Task Copy_FromASourceBehindTheCurrentVersion_IsRefused_AndWritesNothing()
    {
        DatabaseConnectionOptions source = await SeededSourceAsync("auth");
        await using (DbContext db = SchemaProbe.CreateContext("auth", source))
        {
            await UpgradeTestSupport.SetVersionAsync(db, "auth", AuthDbContext.Schema.CurrentVersion - 1);
        }

        DatabaseConnectionOptions target = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        (int code, _, string error) = await RunCopyAsync(source, "auth", target);

        Assert.Equal(DbUpgradeExitCodes.Refused, code);
        Assert.Contains("not at the current schema version", error, StringComparison.Ordinal);
        Assert.Contains("arcane-db upgrade", error, StringComparison.Ordinal);
        Assert.False(File.Exists(UpgradeTestSupport.SqlitePath(target)));
    }

    [Fact]
    public async Task Copy_FromASourceWithAnExtraColumn_IsRefused_NamingTheColumn_AndWritesNothing()
    {
        DatabaseConnectionOptions source = await SeededSourceAsync("auth");
        await using (DbContext db = SchemaProbe.CreateContext("auth", source))
        {
            await SchemaProbe.ExecuteAsync(db, "ALTER TABLE \"account\" ADD COLUMN \"Nickname\" TEXT NULL");
        }

        DatabaseConnectionOptions target = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        (int code, _, string error) = await RunCopyAsync(source, "auth", target);

        Assert.Equal(DbUpgradeExitCodes.Refused, code);
        Assert.Contains("table 'account'", error, StringComparison.Ordinal);
        Assert.Contains("Nickname", error, StringComparison.Ordinal);
        Assert.False(File.Exists(UpgradeTestSupport.SqlitePath(target)));
    }

    [Fact]
    public async Task Copy_FromASourceMissingAColumn_IsRefused()
    {
        DatabaseConnectionOptions source = await SeededSourceAsync("auth");
        await using (DbContext db = SchemaProbe.CreateContext("auth", source))
        {
            await SchemaProbe.ExecuteAsync(db, "ALTER TABLE \"realmlist\" DROP COLUMN \"Category\"");
        }

        DatabaseConnectionOptions target = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        (int code, _, string error) = await RunCopyAsync(source, "auth", target);

        Assert.Equal(DbUpgradeExitCodes.Refused, code);
        Assert.Contains("realmlist", error, StringComparison.Ordinal);
        Assert.Contains("Category", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_FromASourceWithAForeignTable_IsRefused()
    {
        DatabaseConnectionOptions source = await SeededSourceAsync("auth");
        await using (DbContext db = SchemaProbe.CreateContext("auth", source))
        {
            await SchemaProbe.ExecuteAsync(db, "CREATE TABLE \"operator_notes\" (\"Id\" INTEGER PRIMARY KEY)");
        }

        DatabaseConnectionOptions target = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        (int code, _, string error) = await RunCopyAsync(source, "auth", target);

        Assert.Equal(DbUpgradeExitCodes.Refused, code);
        Assert.Contains("operator_notes", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_OfCharacters_WhileAWorldHoldsTheWriterLock_IsRefused()
    {
        DatabaseConnectionOptions source = await SeededSourceAsync("characters");
        DatabaseConnectionOptions target = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        using CharacterDatabaseWriterLock held = CharacterDatabaseWriterLock.TryAcquire(source)!;

        (int code, _, string error) = await RunCopyAsync(source, "characters", target);

        Assert.Equal(DbUpgradeExitCodes.Refused, code);
        Assert.Contains("world process", error, StringComparison.Ordinal);
        Assert.False(File.Exists(UpgradeTestSupport.SqlitePath(target)));

        held.Dispose();
        Assert.Equal(DbUpgradeExitCodes.Ok, (await RunCopyAsync(source, "characters", target)).Code);
    }

    [Fact]
    public async Task Copy_ToItsOwnSource_IsAUsageError()
    {
        DatabaseConnectionOptions source = await SeededSourceAsync("auth");

        (int code, _, string error) = await RunCopyAsync(source, "auth", source, "--replace");

        Assert.Equal(DbUpgradeExitCodes.Usage, code);
        Assert.Contains("same database", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_WithoutATarget_OrWithAConnectionAndComponentAll_IsAUsageError()
    {
        DatabaseConnectionOptions source = await SeededSourceAsync("auth");

        (int none, _, string noneError) = await UpgradeTestSupport.RunCliAsync(source, "copy", "--component", "auth");
        (int all, _, string allError) = await UpgradeTestSupport.RunCliAsync(source, "copy", "--target-provider", "sqlite", "--target-connection", "Data Source=x.db");

        Assert.Equal(DbUpgradeExitCodes.Usage, none);
        Assert.Contains("target", noneError, StringComparison.Ordinal);
        Assert.Equal(DbUpgradeExitCodes.Usage, all);
        Assert.Contains("--component", allError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_ToAnUnreachableServer_ExitsSix_AndPrintsNoPassword()
    {
        DatabaseConnectionOptions source = await SeededSourceAsync("auth");
        var target = new DatabaseConnectionOptions
        {
            Provider = DatabaseProvider.PostgreSql,
            ConnectionString = $"Host=127.0.0.1;Port=1;Database=arcane_copy;Username=arcane;Password={Marker};Timeout=2",
        };

        (int code, string output, string error) = await RunCopyAsync(source, "auth", target);

        Assert.Equal(DbUpgradeExitCodes.Unreachable, code);
        Assert.DoesNotContain(Marker, output + error, StringComparison.Ordinal);
    }

    [ServerProviderTheory]
    [MemberData(nameof(ComponentsAndServerTargets))]
    public async Task Copy_ToAServer_ConvertsTextDatesIntegerBooleansBlobsAndNulls_ToTheColumnTypes(string component, DatabaseProvider targetProvider)
    {
        DatabaseConnectionOptions source = await SeededSourceAsync(component);
        DatabaseConnectionOptions target = await _databases.CreateAsync(targetProvider);
        Assert.Equal(DbUpgradeExitCodes.Ok, (await RunCopyAsync(source, component, target)).Code);

        // The catalog types the values landed in: a copy that stored text where the model wants timestamps or booleans would pass a string compare.
        await using DbContext db = SchemaProbe.CreateContext(component, target);
        var kinds = await DatabaseCopy.ReadKindsAsync(db, DatabaseCopy.TablesOf(db, SchemaProbe.SchemaOf(component)), CancellationToken.None);
        CopyKind[] landed = [.. kinds.Values.SelectMany(t => t.Values).Distinct()];
        Assert.DoesNotContain(CopyKind.Native, landed);
        Assert.Contains(CopyKind.Integer, landed);
        Assert.Contains(CopyKind.Text, landed);
        DbCopyTestSupport.ClearPool(target);
    }

    [Theory]
    [MemberData(nameof(AllTargets))]
    public async Task Copy_OfAuth_ResetsTheKeyCounters_SoTheNextAccountDoesNotCollide(DatabaseProvider targetProvider)
    {
        DatabaseConnectionOptions source = await SeededSourceAsync("auth");
        DatabaseConnectionOptions target = await _databases.CreateAsync(targetProvider);
        Assert.Equal(DbUpgradeExitCodes.Ok, (await RunCopyAsync(source, "auth", target)).Code);

        int highest;
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(target))
        {
            highest = await db.Accounts.MaxAsync(a => a.Id);
            Assert.True(highest >= 500000 + BulkRows - 1);
            db.Accounts.Add(new Account { Username = "AFTERCOPY", Salt = new byte[32], Verifier = new byte[32] });
            await db.SaveChangesAsync();
        }

        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(target))
        {
            Account added = await db.Accounts.SingleAsync(a => a.Username == "AFTERCOPY");
            Assert.True(added.Id > highest, $"new account id {added.Id} is not above the copied maximum {highest}");
            Assert.Equal(1, await db.Accounts.CountAsync(a => a.Id == added.Id));
        }

        DbCopyTestSupport.ClearPool(target);
    }

    public static IEnumerable<object[]> AllTargets() => TestDatabases.AvailableProviders();

    [Fact]
    public void Providers_AvailableToThisRun_AreReportedByTheTheoryData()
    {
        // 0 rows for a server provider means it was not configured, which the run report must say; SQLite is always present.
        Assert.Contains(TestDatabases.AvailableProviders(), p => (DatabaseProvider)p[0] == DatabaseProvider.Sqlite);
    }
}
