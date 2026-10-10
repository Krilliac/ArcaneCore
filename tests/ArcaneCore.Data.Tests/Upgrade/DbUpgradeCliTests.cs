using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Npgsql;
using Xunit;

namespace ArcaneCore.Data.Tests.Upgrade;

/// <summary>
/// <c>arcane-db</c> driven in-process (the library CLI the tool host forwards to): the exit-code contract, the
/// read-only commands, the backup gate, refusals before any change, secrets never printed. SQLite runs
/// everywhere; MariaDB/PostgreSQL theories only where ARCANECORE_TEST_MARIADB / ARCANECORE_TEST_POSTGRES are set (hosted CI).
/// </summary>
public sealed class DbUpgradeCliTests : IAsyncLifetime
{
    private const string Marker = "hunter2-marker";
    private readonly TestDatabases _databases = new();
    private readonly List<string> _directories = [];

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    private async Task<DatabaseConnectionOptions> NewLegacyCharactersAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharactersM5Context legacy = TestContexts.Create<CharactersM5Context>(connection);
        await SchemaBootstrapper.EnsureAsync(legacy, CharactersM5Context.Schema);
        return connection;
    }

    private async Task<DatabaseConnectionOptions> NewCurrentAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        foreach (string component in new[] { "auth", "characters", "world" })
        {
            await SchemaProbe.EnsureCurrentAsync(component, connection);
        }

        return connection;
    }

    // --- status ----------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Status_OnACurrentDatabase_ExitsZero_AndPrintsBothVersionsPerComponent(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await NewCurrentAsync(provider);

        (int code, string output, string error) = await UpgradeTestSupport.RunCliAsync(connection, "status");

        Assert.Equal(DbUpgradeExitCodes.Ok, code);
        Assert.Equal(string.Empty, error);
        foreach ((string component, int version) in Versions())
        {
            Assert.Matches($@"{component}\s+state Current, database version {version}, code version {version}", output);
        }

        Assert.True(output.IndexOf("auth ", StringComparison.Ordinal) < output.IndexOf("characters ", StringComparison.Ordinal));
        Assert.True(output.IndexOf("characters ", StringComparison.Ordinal) < output.IndexOf("world ", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Status_OnALegacyDatabase_ExitsThree_AndListsThePendingSteps(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await NewLegacyCharactersAsync(provider);
        string pending = string.Join(", ", CharacterDbContext.Schema.Steps.Where(s => s.Version > 1).Select(s => s.Version));

        (int code, string output, _) = await UpgradeTestSupport.RunCliAsync(connection, "status", "--component", "characters");

        Assert.Equal(DbUpgradeExitCodes.UpgradePending, code);
        Assert.Contains($"state Behind, database version 1, code version {CharacterDbContext.Schema.CurrentVersion}, pending: {pending}", output, StringComparison.Ordinal);
        Assert.Contains("an upgrade is pending", output, StringComparison.Ordinal);

        (int relaxed, _, _) = await UpgradeTestSupport.RunCliAsync(connection, "status", "--component", "characters", "--no-fail-on-pending");
        Assert.Equal(DbUpgradeExitCodes.Ok, relaxed);
    }

    [Fact]
    public async Task Status_OnAMissingSqliteDatabase_ReportsMissing_AndCreatesNoFile()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);

        (int code, string output, _) = await UpgradeTestSupport.RunCliAsync(connection, "status");

        Assert.Equal(DbUpgradeExitCodes.UpgradePending, code);
        Assert.Contains("state Missing", output, StringComparison.Ordinal);
        Assert.False(File.Exists(UpgradeTestSupport.SqlitePath(connection)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task StatusJson_ParsesAndCarriesTheSameVersionsAsTheText(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await NewLegacyCharactersAsync(provider);

        (int code, string json, _) = await UpgradeTestSupport.RunCliAsync(connection, "status", "--json", "--component", "characters");
        (_, string text, _) = await UpgradeTestSupport.RunCliAsync(connection, "status", "--component", "characters");

        Assert.Equal(DbUpgradeExitCodes.UpgradePending, code);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement component = Assert.Single(document.RootElement.GetProperty("components").EnumerateArray());
        Assert.Equal("characters", component.GetProperty("component").GetString());
        Assert.Equal("Behind", component.GetProperty("state").GetString());
        Assert.Equal(1, component.GetProperty("databaseVersion").GetInt32());
        Assert.Equal(CharacterDbContext.Schema.CurrentVersion, component.GetProperty("codeVersion").GetInt32());
        Assert.Equal(
            CharacterDbContext.Schema.Steps.Where(s => s.Version > 1).Select(s => s.Version),
            component.GetProperty("pendingVersions").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Contains($"database version {component.GetProperty("databaseVersion").GetInt32()}, code version {component.GetProperty("codeVersion").GetInt32()}", text, StringComparison.Ordinal);
    }

    // --- plan ------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Plan_WritesNothing_AndItsPendingListIsWhatUpgradeThenPerforms(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await NewLegacyCharactersAsync(provider);
        string? file = provider == DatabaseProvider.Sqlite ? UpgradeTestSupport.SqlitePath(connection) : null;
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        string before = await UpgradeTestSupport.SnapshotAsync(db, CharacterDbContext.Schema);
        string? fileBefore = file is null ? null : UpgradeTestSupport.SettledFileFingerprint(connection);

        (int code, string json, _) = await UpgradeTestSupport.RunCliAsync(connection, "plan", "--json", "--component", "characters");

        Assert.Equal(DbUpgradeExitCodes.UpgradePending, code);
        Assert.Equal(before, await UpgradeTestSupport.SnapshotAsync(db, CharacterDbContext.Schema));
        if (file is not null)
        {
            Assert.Equal(fileBefore, UpgradeTestSupport.SettledFileFingerprint(connection));
        }

        using JsonDocument document = JsonDocument.Parse(json);
        int[] planned = [.. document.RootElement.GetProperty("components")[0].GetProperty("pendingVersions").EnumerateArray().Select(e => e.GetInt32())];
        Assert.NotEmpty(planned);

        (int upgraded, string output, string error) = await UpgradeTestSupport.RunCliAsync(
            connection, "upgrade", "--component", "characters", "--confirm-backup", "--allow-active-sessions");
        Assert.True(upgraded == DbUpgradeExitCodes.Ok, error + output);
        int[] written = [.. Regex.Matches(output, @"characters: schema version (\d+) written").Select(m => int.Parse(m.Groups[1].Value))];
        Assert.Equal(planned, written);
    }

    [Fact]
    public async Task PlanScript_RunByHandOnACopy_YieldsTheCodeVersionAndACleanDriftCheck()
    {
        DatabaseConnectionOptions connection = await NewLegacyCharactersAsync(DatabaseProvider.Sqlite);
        string source = UpgradeTestSupport.SqlitePath(connection);
        string copy = Path.Combine(Path.GetDirectoryName(source)!, "copy-for-script.db");
        string directory = NewDirectory();
        string backup = await BackupAdvisor.BackupSqliteAsync(connection, directory, "script.db");
        File.Copy(backup, copy);

        (int code, string script, string error) = await UpgradeTestSupport.RunCliAsync(connection, "plan", "--script", "--component", "characters");

        Assert.Equal(DbUpgradeExitCodes.UpgradePending, code);
        Assert.Equal(string.Empty, error);
        Assert.Contains("CREATE TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(";", script.TrimEnd(), StringComparison.Ordinal);
        await using (var raw = new SqliteConnection($"Data Source={copy};Pooling=False"))
        {
            await raw.OpenAsync();
            await using SqliteCommand command = raw.CreateCommand();
            command.CommandText = script;
            await command.ExecuteNonQueryAsync();
        }

        var copyConnection = new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = $"Data Source={copy}" };
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(copyConnection);
        Assert.Equal(CharacterDbContext.Schema.CurrentVersion, await UpgradeTestSupport.ReadVersionAsync(db, "characters"));
        (int check, string output, _) = await UpgradeTestSupport.RunCliAsync(copyConnection, "check", "--component", "characters");
        Assert.True(check == DbUpgradeExitCodes.Ok, output);
    }

    // --- upgrade ---------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Upgrade_WithoutABackupConfirmation_ExitsEight_AndChangesNothing(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await NewLegacyCharactersAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        string before = await UpgradeTestSupport.SnapshotAsync(db, CharacterDbContext.Schema);

        (int code, string output, string error) = await UpgradeTestSupport.RunCliAsync(connection, "upgrade", "--component", "characters");

        Assert.Equal(DbUpgradeExitCodes.BackupNotConfirmed, code);
        Assert.Contains("--confirm-backup", error, StringComparison.Ordinal);
        Assert.Contains(provider == DatabaseProvider.Sqlite ? "--backup-dir" : "dump", error, StringComparison.Ordinal);
        Assert.Equal(before, await UpgradeTestSupport.SnapshotAsync(db, CharacterDbContext.Schema));
        Assert.DoesNotContain("upgrading", output, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Upgrade_AppliesInOrder_ChecksClean_AndASecondRunHasNothingToDo(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await NewLegacyCharactersAsync(provider);

        (int code, string output, string error) = await UpgradeTestSupport.RunCliAsync(connection, "upgrade", "--confirm-backup", "--allow-active-sessions");

        Assert.True(code == DbUpgradeExitCodes.Ok, error + output);
        // auth and world did not exist (Fresh) and are created too; the order is auth, characters, world.
        int auth = output.IndexOf("upgrading auth", StringComparison.Ordinal);
        int characters = output.IndexOf("upgrading characters", StringComparison.Ordinal);
        int world = output.IndexOf("upgrading world", StringComparison.Ordinal);
        Assert.True(auth >= 0 && auth < characters && characters < world, output);
        Assert.Matches(@"characters\s+clean", output);
        foreach ((string component, int version) in Versions())
        {
            await using DbContext db = SchemaProbe.CreateContext(component, connection);
            Assert.Equal(version, await UpgradeTestSupport.ReadVersionAsync(db, component));
        }

        string? file = provider == DatabaseProvider.Sqlite ? UpgradeTestSupport.SqlitePath(connection) : null;
        string? fileBefore = file is null ? null : UpgradeTestSupport.SettledFileFingerprint(connection);
        (int again, string second, _) = await UpgradeTestSupport.RunCliAsync(connection, "upgrade", "--allow-active-sessions");
        Assert.Equal(DbUpgradeExitCodes.Ok, again);
        Assert.Contains("nothing to do", second, StringComparison.Ordinal);
        Assert.DoesNotContain("upgrading", second, StringComparison.Ordinal);
        if (file is not null)
        {
            Assert.Equal(fileBefore, UpgradeTestSupport.SettledFileFingerprint(connection));
        }
    }

    [Fact]
    public async Task Upgrade_WithABackupDirectory_WritesAVerifiedSqliteCopyFirst()
    {
        DatabaseConnectionOptions connection = await NewLegacyCharactersAsync(DatabaseProvider.Sqlite);
        string directory = NewDirectory();

        (int code, string output, string error) = await UpgradeTestSupport.RunCliAsync(
            connection, "upgrade", "--component", "characters", "--backup-dir", directory);

        Assert.True(code == DbUpgradeExitCodes.Ok, error + output);
        string backup = Assert.Single(Directory.GetFiles(directory));
        Assert.Contains("backup written", output, StringComparison.Ordinal);
        var copy = new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = $"Data Source={backup};Pooling=False" };
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(copy);
        Assert.Equal(1, await UpgradeTestSupport.ReadVersionAsync(db, "characters")); // the copy is the database as it was before
    }

    [Fact]
    public async Task Upgrade_WithABackupDirectoryInsideTheRepository_IsRefusedWithExitEight_AndChangesNothing()
    {
        DatabaseConnectionOptions connection = await NewLegacyCharactersAsync(DatabaseProvider.Sqlite);
        string? root = FindWorkTreeRoot();
        if (root is null)
        {
            return;
        }

        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        string before = await UpgradeTestSupport.SnapshotAsync(db, CharacterDbContext.Schema);
        string directory = Path.Combine(root, "backup-cli-test-" + Guid.NewGuid().ToString("N"));

        (int code, _, string error) = await UpgradeTestSupport.RunCliAsync(
            connection, "upgrade", "--component", "characters", "--backup-dir", directory);

        Assert.Equal(DbUpgradeExitCodes.BackupNotConfirmed, code);
        Assert.Contains("git", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(directory));
        Assert.Equal(before, await UpgradeTestSupport.SnapshotAsync(db, CharacterDbContext.Schema));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DatabaseNewerThanTheCode_ExitsFour_SaysWhatToDo_AndChangesNothing(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await NewCurrentAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await UpgradeTestSupport.SetVersionAsync(db, "characters", CharacterDbContext.Schema.CurrentVersion + 1);
        string before = await UpgradeTestSupport.SnapshotAsync(db, CharacterDbContext.Schema);

        (int plan, string planOut, _) = await UpgradeTestSupport.RunCliAsync(connection, "plan", "--component", "characters");
        (int upgrade, _, string error) = await UpgradeTestSupport.RunCliAsync(connection, "upgrade", "--confirm-backup", "--allow-active-sessions");

        Assert.Equal(DbUpgradeExitCodes.Refused, plan);
        Assert.Contains("newer", planOut, StringComparison.Ordinal);
        Assert.Equal(DbUpgradeExitCodes.Refused, upgrade);
        Assert.Contains("Run a newer ArcaneCore or restore a matching backup", error, StringComparison.Ordinal);
        Assert.Equal(before, await UpgradeTestSupport.SnapshotAsync(db, CharacterDbContext.Schema));
    }

    [Fact]
    public async Task DuplicateGuildMembers_BlockThePlanAndTheUpgradeWithZeroChanges_UntilTheOperatorFixesThem()
    {
        DatabaseConnectionOptions connection = await UpgradeTestSupport.NewBaselineAsync(_databases, CandidateBaseline.Variant.HistoricUpgraded);
        string path = UpgradeTestSupport.SqlitePath(connection);
        await using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await raw.OpenAsync();
            await using SqliteCommand command = raw.CreateCommand();
            command.CommandText =
                "INSERT INTO \"guild_member\" SELECT 50, \"CharacterId\", \"Rank\", \"PublicNote\", \"OfficerNote\", \"Level\", \"ZoneId\", \"LogoutTime\" " +
                "FROM \"guild_member\" WHERE \"GuildId\" = 1";
            await command.ExecuteNonQueryAsync();
        }

        string fingerprint = UpgradeTestSupport.SettledFileFingerprint(connection);

        (int plan, string planOut, _) = await UpgradeTestSupport.RunCliAsync(connection, "plan", "--component", "characters");
        (int upgrade, _, string error) = await UpgradeTestSupport.RunCliAsync(connection, "upgrade", "--component", "characters", "--confirm-backup");

        Assert.Equal(DbUpgradeExitCodes.Refused, plan);
        Assert.Contains("guild_member", planOut, StringComparison.Ordinal);
        Assert.Matches(@"1 group\(s\) of rows share a value", planOut);
        Assert.Equal(DbUpgradeExitCodes.Refused, upgrade);
        Assert.Contains("nothing was changed", error, StringComparison.Ordinal);
        Assert.Equal(fingerprint, UpgradeTestSupport.SettledFileFingerprint(connection)); // not a byte written

        await using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await raw.OpenAsync();
            await using SqliteCommand command = raw.CreateCommand();
            command.CommandText = "DELETE FROM \"guild_member\" WHERE \"GuildId\" = 50";
            await command.ExecuteNonQueryAsync();
            command.CommandText = "DELETE FROM \"guild_member\" WHERE \"rowid\" NOT IN (SELECT MIN(\"rowid\") FROM \"guild_member\" GROUP BY \"CharacterId\")";
            await command.ExecuteNonQueryAsync();
        }

        (int fixedCode, string output, string fixedError) = await UpgradeTestSupport.RunCliAsync(
            connection, "upgrade", "--component", "characters", "--confirm-backup");
        Assert.True(fixedCode == DbUpgradeExitCodes.Ok, fixedError + output);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LockHeldElsewhere_ExitsSeven_WithinABoundedTime(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await NewLegacyCharactersAsync(provider);

        await using (await UpgradeTestSupport.HoldSchemaLockAsync(connection, "characters"))
        {
            var clock = Stopwatch.StartNew();
            (int code, _, string error) = await UpgradeTestSupport.RunCliAsync(
                connection, "upgrade", "--component", "characters", "--confirm-backup", "--allow-active-sessions", "--lock-timeout", "1");
            Assert.Equal(DbUpgradeExitCodes.LockTimeout, code);
            Assert.Contains("schema lock", error, StringComparison.Ordinal);
            Assert.Contains("characters", error, StringComparison.Ordinal);
            Assert.InRange(clock.Elapsed.TotalSeconds, 0.5, 30);
        }

        (int after, _, string afterError) = await UpgradeTestSupport.RunCliAsync(
            connection, "upgrade", "--component", "characters", "--confirm-backup", "--allow-active-sessions");
        Assert.True(after == DbUpgradeExitCodes.Ok, afterError);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ActiveSessions_AreRefusedWithExitFour_UnlessAllowed_HostedOnly(DatabaseProvider provider)
    {
        if (provider == DatabaseProvider.Sqlite)
        {
            return; // SQLite sessions are not detectable; the refusal is proven on hosted CI
        }

        DatabaseConnectionOptions connection = await NewLegacyCharactersAsync(provider);
        await using System.Data.Common.DbConnection other = provider == DatabaseProvider.PostgreSql
            ? new NpgsqlConnection(connection.ConnectionString + ";Pooling=false")
            : new MySqlConnection(connection.ConnectionString + ";Pooling=false");
        await other.OpenAsync();

        (int refused, _, string error) = await UpgradeTestSupport.RunCliAsync(connection, "upgrade", "--component", "characters", "--confirm-backup");
        Assert.Equal(DbUpgradeExitCodes.Refused, refused);
        Assert.Contains("other session", error, StringComparison.Ordinal);

        (int allowed, _, _) = await UpgradeTestSupport.RunCliAsync(connection, "upgrade", "--component", "characters", "--confirm-backup", "--allow-active-sessions");
        Assert.Equal(DbUpgradeExitCodes.Ok, allowed);
    }

    // --- check -----------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Check_ExitsFiveAndPrintsTheFinding_AfterADroppedIndex_AndZeroOnceItIsBack(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await NewCurrentAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        Assert.Equal(DbUpgradeExitCodes.Ok, (await UpgradeTestSupport.RunCliAsync(connection, "check")).Code);

        IndexShape[] indexes = [.. SchemaProbe.ModelIndexes(db).Where(i => i.Table == "guild_member")];
        await SchemaProbe.DropIndexesAsync(db, "guild_member");
        (int drift, string output, _) = await UpgradeTestSupport.RunCliAsync(connection, "check", "--component", "characters");

        Assert.Equal(DbUpgradeExitCodes.Drift, drift);
        Assert.Contains("MissingIndex", output, StringComparison.Ordinal);
        Assert.Contains("guild_member", output, StringComparison.Ordinal);

        foreach (IndexShape index in indexes)
        {
            string columns = string.Join(", ", index.Columns.Split(',').Select(c => TestContexts.Quote(db, c)));
            await SchemaProbe.ExecuteAsync(db,
                $"CREATE {(index.IsUnique ? "UNIQUE " : string.Empty)}INDEX {TestContexts.Quote(db, index.Name)} ON {TestContexts.Quote(db, index.Table)} ({columns})");
        }

        Assert.Equal(DbUpgradeExitCodes.Ok, (await UpgradeTestSupport.RunCliAsync(connection, "check", "--component", "characters")).Code);

        (int json, string report, _) = await UpgradeTestSupport.RunCliAsync(connection, "check", "--json", "--component", "characters");
        Assert.Equal(DbUpgradeExitCodes.Ok, json);
        using JsonDocument document = JsonDocument.Parse(report);
        Assert.True(document.RootElement.GetProperty("components")[0].GetProperty("clean").GetBoolean());
        Assert.True(document.RootElement.GetProperty("components")[0].GetProperty("indexesExamined").GetInt32() > 0);
    }

    [Fact]
    public async Task Check_OfAMissingDatabase_IsDrift_NotACrash()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);

        (int code, string output, _) = await UpgradeTestSupport.RunCliAsync(connection, "check", "--component", "characters");

        Assert.Equal(DbUpgradeExitCodes.Drift, code);
        Assert.Contains("does not exist", output, StringComparison.Ordinal);
        Assert.False(File.Exists(UpgradeTestSupport.SqlitePath(connection)));
    }

    // --- unreachable, secrets, usage -------------------------------------------------------

    [Theory]
    [InlineData(DatabaseProvider.MariaDb, "Server=127.0.0.1;Port=1;Database=arcane;User ID=arcane;Password=" + Marker + ";Connection Timeout=3")]
    [InlineData(DatabaseProvider.PostgreSql, "Host=127.0.0.1;Port=1;Database=arcane;Username=arcane;Password=" + Marker + ";Timeout=3")]
    public async Task UnreachableServer_ExitsSix_AndNoOutputHoldsThePasswordOrTheConnectionString(DatabaseProvider provider, string connectionString)
    {
        var options = new DatabaseOptions { Provider = provider, ConnectionString = connectionString };

        foreach (string[] args in new[]
        {
            new[] { "status" },
            new[] { "plan" },
            new[] { "check" },
            new[] { "upgrade", "--confirm-backup" },
        })
        {
            (int code, string output, string error) = await UpgradeTestSupport.RunCliAsync(options, args);
            Assert.Equal(DbUpgradeExitCodes.Unreachable, code);
            Assert.Contains("auth", error, StringComparison.Ordinal);
            Assert.DoesNotContain(Marker, output + error, StringComparison.Ordinal);
            Assert.DoesNotContain(connectionString, output + error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task BackupInfo_PrintsTheCommandWithoutThePassword()
    {
        var options = new DatabaseOptions
        {
            Provider = DatabaseProvider.MariaDb,
            ConnectionString = "Server=db.example;Port=3307;Database=arcane_all;User ID=arcane;Password=" + Marker,
        };

        (int code, string output, string error) = await UpgradeTestSupport.RunCliAsync(options, "backup-info");

        Assert.Equal(DbUpgradeExitCodes.Ok, code);
        Assert.Contains("mysqldump", output, StringComparison.Ordinal);
        Assert.Contains("MYSQL_PWD", output, StringComparison.Ordinal);
        Assert.Contains("--single-transaction", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, output + error, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(output, "mysqldump")); // one shared database is one backup, not three
    }

    [Fact]
    public async Task AFailureInOneComponent_StopsTheLaterOnes_AndNamesIt()
    {
        DatabaseConnectionOptions sqlite = await NewCurrentAsync(DatabaseProvider.Sqlite);
        var options = new DatabaseOptions
        {
            Provider = DatabaseProvider.Sqlite,
            ConnectionString = sqlite.ConnectionString,
            Characters = new DatabaseConnectionOptions
            {
                Provider = DatabaseProvider.MariaDb,
                ConnectionString = "Server=127.0.0.1;Port=1;Database=c;User ID=u;Password=" + Marker + ";Connection Timeout=3",
            },
        };

        (int code, string output, string error) = await UpgradeTestSupport.RunCliAsync(options, "status");

        Assert.Equal(DbUpgradeExitCodes.Unreachable, code);
        Assert.Contains("characters:", error, StringComparison.Ordinal);
        Assert.Contains("auth", output, StringComparison.Ordinal); // ran before the failure
        Assert.DoesNotContain("world ", output, StringComparison.Ordinal); // never reached
        Assert.DoesNotContain(Marker, output + error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("status", "--frobnicate")]
    [InlineData("status", "--confirm-backup")]
    [InlineData("plan", "--backup-dir", "x")]
    [InlineData("status", "--component")]
    [InlineData("status", "--component", "nope")]
    [InlineData("status", "stray")]
    [InlineData("upgrade", "--lock-timeout", "0")]
    [InlineData("upgrade", "--lock-timeout", "soon")]
    [InlineData("plan", "--script", "--json")]
    [InlineData("check", "--json=yes")]
    public async Task BadCommandLines_ExitTwo_WithAUsageHint(params string[] args)
    {
        var options = new DatabaseOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=:memory:" };

        (int code, string output, string error) = await UpgradeTestSupport.RunCliAsync(options, args);

        Assert.Equal(DbUpgradeExitCodes.Usage, code);
        Assert.Contains("error:", error, StringComparison.Ordinal);
        Assert.Contains("--help", error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public async Task NoArguments_PrintUsageToStderr_HelpToStdout_AndAMissingConnectionStringIsAUsageError()
    {
        var empty = new DatabaseOptions();

        (int none, string noneOut, string noneErr) = await UpgradeTestSupport.RunCliAsync(empty);
        Assert.Equal(DbUpgradeExitCodes.Usage, none);
        Assert.Equal(string.Empty, noneOut);
        Assert.Contains("usage: arcane-db", noneErr, StringComparison.Ordinal);

        (int help, string helpOut, _) = await UpgradeTestSupport.RunCliAsync(empty, "--help");
        Assert.Equal(DbUpgradeExitCodes.Ok, help);
        Assert.Contains("exit codes:", helpOut, StringComparison.Ordinal);

        (int missing, _, string missingErr) = await UpgradeTestSupport.RunCliAsync(empty, "status");
        Assert.Equal(DbUpgradeExitCodes.Usage, missing);
        Assert.Contains("no connection string for the auth database", missingErr, StringComparison.Ordinal);
        Assert.Contains("Database__Auth__ConnectionString", missingErr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheToolsVersionsAreTheSameStaticDefinitionsTheDaemonsUse()
    {
        // arcane-db and the daemons share one registry (the contexts' Schema definitions), so a module added to a
        // component moves both at once; nothing in the tool keeps its own copy of a version.
        Assert.Contains(nameof(AuthDbContext.Schema), typeof(AuthDbContext).GetFields().Select(f => f.Name));
        Assert.Equal(
            [AuthDbContext.Schema.CurrentVersion, CharacterDbContext.Schema.CurrentVersion, WorldDbContext.Schema.CurrentVersion],
            Versions().Select(v => v.Version));
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

    private static (string Component, int Version)[] Versions() =>
    [
        ("auth", AuthDbContext.Schema.CurrentVersion),
        ("characters", CharacterDbContext.Schema.CurrentVersion),
        ("world", WorldDbContext.Schema.CurrentVersion),
    ];

    private string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        return directory;
    }

    private static string? FindWorkTreeRoot()
        => RepositorySource.FindRoot(requireGit: true);
}
