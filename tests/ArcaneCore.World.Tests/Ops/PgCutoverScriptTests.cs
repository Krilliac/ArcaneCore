using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ArcaneCore.Data;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using ArcaneCore.TestSupport;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// tools/ops/live/pg-cutover.ps1 under PowerShell 7. Nothing here touches a live database or starts a daemon: -Plan runs against an empty
/// temporary profile, and the verify runs compare fixture SQLite files (always) or fixture PostgreSQL databases (only where
/// <c>ARCANECORE_TEST_POSTGRES</c> names a server, as on the hosted CI). The canary password must never reach any output.
/// </summary>
public sealed class PgCutoverScriptTests : IDisposable
{
    private const string Canary = "cAnArY-pw-9f3b1c7d";
    private const string PostgresVariable = "ARCANECORE_TEST_POSTGRES";

    private static readonly string Script = Path.Combine(RepositorySource.RequireRoot(), "tools", "ops", "live", "pg-cutover.ps1");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcane-pgcutover-" + Guid.NewGuid().ToString("N"));
    private readonly string _profile;
    private readonly string _target;

    public PgCutoverScriptTests()
    {
        SQLitePCL.Batteries_V2.Init();
        _profile = Path.Combine(_root, "profile");
        _target = Path.Combine(_root, "target");
        Directory.CreateDirectory(_profile);
        Directory.CreateDirectory(_target);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    [PwshFact]
    public void Plan_PrintsEveryStep_AndTouchesNothing()
    {
        string secretFile = WriteSecretFile("localhost", "5432", "arcane", Canary);
        string backup = Path.Combine(_root, "backup");

        (int code, string output) = Run("-Plan", "-LiveProfile", _profile, "-BackupDir", backup, "-SecretFile", secretFile);

        Assert.True(code == 0, output);
        for (int step = 1; step <= 8; step++)
        {
            Assert.Contains($"STEP {step} ", output, StringComparison.Ordinal);
        }

        Assert.Contains("arcane-db copy", output, StringComparison.Ordinal);
        Assert.Contains("PostgreSql", output, StringComparison.Ordinal);
        Assert.Contains("Password=***", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(backup), "-Plan created the backup directory");
        Assert.Empty(Directory.GetFileSystemEntries(_profile));
    }

    [PwshFact]
    public void Plan_WithoutAnySecretFile_StillPrintsEveryStep()
    {
        (int code, string output) = Run("-Plan", "-LiveProfile", _profile, "-SecretFile", Path.Combine(_root, "absent.json"));

        Assert.True(code == 0, output);
        Assert.Contains("STEP 8 ", output, StringComparison.Ordinal);
    }

    [PwshFact]
    public void Backup_WithoutConfirm_IsRefused_AndWritesNothing()
    {
        string backup = Path.Combine(_root, "backup");

        (int code, string output) = Run("-Step", "Backup", "-LiveProfile", _profile, "-BackupDir", backup);

        Assert.NotEqual(0, code);
        Assert.Contains("-Confirm", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(backup));
    }

    [PwshFact]
    public async Task Verify_OfMatchingDatabases_ReportsEqualCounts_AndExitsZero()
    {
        await BuildFixtureAsync(_profile);
        CloneFixture(_profile, _target);

        (int code, string output) = Verify();

        Assert.True(code == 0, output);
        Assert.Contains("OK   auth.ip_banned  rows=3", output, StringComparison.Ordinal);
        Assert.Contains("OK   auth.account ", output, StringComparison.Ordinal);
        Assert.Contains("0 difference(s)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("FAIL", output, StringComparison.Ordinal);
    }

    [PwshFact]
    public async Task Verify_FailsWhenARowWasDropped()
    {
        await BuildFixtureAsync(_profile);
        CloneFixture(_profile, _target);
        Execute(Path.Combine(_target, "auth.db"), "DELETE FROM ip_banned WHERE Ip = '10.0.0.2'");

        (int code, string output) = Verify();

        Assert.Equal(1, code);
        Assert.Contains("FAIL auth.ip_banned  source rows=3  target rows=2", output, StringComparison.Ordinal);
        Assert.Contains("OK   auth.account", output, StringComparison.Ordinal);
    }

    [PwshFact]
    public async Task Verify_FailsWhenAKeyChanged_EvenWithTheSameCount()
    {
        await BuildFixtureAsync(_profile);
        CloneFixture(_profile, _target);
        Execute(Path.Combine(_target, "auth.db"), "UPDATE ip_banned SET Ip = '10.9.9.9' WHERE Ip = '10.0.0.1'");

        (int code, string output) = Verify();

        Assert.Equal(1, code);
        Assert.Contains("FAIL auth.ip_banned", output, StringComparison.Ordinal);
        Assert.Contains("CONTENT DIFFERS", output, StringComparison.Ordinal);
    }

    [PwshFact]
    public async Task Verify_FailsWhenAnOtherColumnChanged()
    {
        await BuildFixtureAsync(_profile);
        CloneFixture(_profile, _target);
        Execute(Path.Combine(_target, "auth.db"), "UPDATE ip_banned SET BanReason = 'changed' WHERE Ip = '10.0.0.3'");

        (int code, string output) = Verify();

        Assert.Equal(1, code);
        Assert.Contains("FAIL auth.ip_banned", output, StringComparison.Ordinal);
    }

    [PwshFact]
    public async Task Verify_WarnsWhenTheWorldContentIsEmpty()
    {
        await BuildFixtureAsync(_profile);
        CloneFixture(_profile, _target);

        (int code, string output) = Verify();

        Assert.True(code == 0, output);
        Assert.Contains("WARN world.creature_template is empty", output, StringComparison.Ordinal);
    }

    [PwshFact]
    public void Verify_WithAnIncompleteSecretFile_NamesTheKey_AndNeverEchoesThePassword()
    {
        string secretFile = WriteSecretFile(host: string.Empty, port: "5432", user: "arcane", password: Canary);

        // No arcane-db exists in -BinDir: the secret file is checked first, so its problem is the one reported.
        (int code, string output) = Run("-Step", "Verify", "-LiveProfile", _profile, "-BinDir", _root, "-SecretFile", secretFile);

        Assert.NotEqual(0, code);
        Assert.Contains("has no Host", output, StringComparison.Ordinal);
        Assert.DoesNotContain("arcane-db not found", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, output, StringComparison.Ordinal);
    }

    [PwshFact]
    public void Plan_WithAnIncompleteSecretFile_StillPrintsEveryStep()
    {
        string secretFile = WriteSecretFile(host: string.Empty, port: "5432", user: "arcane", password: Canary);

        (int code, string output) = Run("-Plan", "-LiveProfile", _profile, "-SecretFile", secretFile);

        Assert.True(code == 0, output);
        Assert.Contains("STEP 8 ", output, StringComparison.Ordinal);
        Assert.Contains("not read in -Plan", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, output, StringComparison.Ordinal);
    }

    [PwshFact]
    public async Task Backup_WritesVerifiedCopies_AndRollbackRestoresThem_FromAnotherWorkingDirectory()
    {
        // The profile names its databases with relative paths (the live configuration); this process, and so the script, does not run in the profile.
        await BuildFixtureAsync(_profile);
        string backup = Path.Combine(_root, "backup");
        string[] common = ["-LiveProfile", _profile, "-BackupDir", backup, "-BinDir", ArcaneDbDirectory(), "-Confirm", "-DaemonProcessName", "no-such-process-for-the-test"];

        (int code, string output) = Run(["-Step", "Backup", .. common]);

        Assert.True(code == 0, output);
        Assert.Contains("backup verified", output, StringComparison.Ordinal);
        foreach (string name in new[] { "auth.db", "characters.db", "world.db", "manifest.json" })
        {
            Assert.True(File.Exists(Path.Combine(backup, name)), $"{name} was not written to the backup directory\n{output}");
        }

        Execute(Path.Combine(_profile, "auth.db"), "DELETE FROM ip_banned");
        (int refused, string refusedOutput) = Run(["-Step", "Verify", "-LiveProfile", _profile, "-BinDir", ArcaneDbDirectory(), "-TargetSqliteDir", backup]);
        Assert.Equal(1, refused);
        Assert.Contains("FAIL auth.ip_banned", refusedOutput, StringComparison.Ordinal);

        (int restored, string restoredOutput) = Run(["-Step", "Rollback", .. common]);

        Assert.True(restored == 0, restoredOutput);
        Assert.Contains("restored auth.db", restoredOutput, StringComparison.Ordinal);
        Assert.NotEmpty(Directory.GetFiles(_profile, "auth.db.pre-rollback-*"));
        (int again, string againOutput) = Run(["-Step", "Verify", "-LiveProfile", _profile, "-BinDir", ArcaneDbDirectory(), "-TargetSqliteDir", backup]);
        Assert.True(again == 0, againOutput);
    }

    [PwshFact]
    public async Task Backup_RefusesADirectoryThatAlreadyHoldsDatabases()
    {
        await BuildFixtureAsync(_profile);
        string backup = Path.Combine(_root, "backup");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "auth.db"), "x");

        (int code, string output) = Run("-Step", "Backup", "-LiveProfile", _profile, "-BackupDir", backup, "-BinDir", ArcaneDbDirectory(), "-Confirm", "-DaemonProcessName", "no-such-process-for-the-test");

        Assert.Equal(2, code);
        Assert.Contains("already holds .db files", output, StringComparison.Ordinal);
    }

    [PostgresPwshFact]
    public async Task Verify_AgainstPostgreSql_ReportsMatch_ThenFailsOnADroppedRow()
    {
        string server = Environment.GetEnvironmentVariable(PostgresVariable)!;
        var builder = new NpgsqlConnectionStringBuilder(server.TrimEnd(';')) { Pooling = false, Database = "postgres" };
        string database = "arcane_pgcut_" + Guid.NewGuid().ToString("N")[..16];
        await BuildFixtureAsync(_profile);
        string secretFile = WriteSecretFile(
            builder.Host ?? "localhost",
            builder.Port.ToString(CultureInfo.InvariantCulture),
            builder.Username ?? "postgres",
            builder.Password ?? "unused",
            authDatabase: database);

        using (var admin = new NpgsqlConnection(builder.ConnectionString))
        {
            admin.Open();
            using NpgsqlCommand create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE {database}";
            create.ExecuteNonQuery();
        }

        try
        {
            builder.Database = database;
            string target = builder.ConnectionString;
            var source = new DatabaseOptions { Auth = SqliteSection(Path.Combine(_profile, "auth.db")) };
            var copyOutput = new StringWriter();
            var copyError = new StringWriter();
            int copied = await DbUpgradeCli.RunAsync(
                ["copy", "--component", "auth"],
                source,
                copyOutput,
                copyError,
                dbcDirectory: null,
                environment: name => name switch
                {
                    "ARCANE_DB_TARGET_PROVIDER" => "postgresql",
                    "ARCANE_DB_TARGET_AUTH_CONNECTION" => target,
                    _ => null,
                },
                CancellationToken.None);
            Assert.True(copied == 0, copyOutput.ToString() + copyError);

            string[] args = ["-Step", "Verify", "-LiveProfile", _profile, "-BinDir", ArcaneDbDirectory(), "-SecretFile", secretFile, "-Component", "auth"];
            (int ok, string okOutput) = Run(args);
            Assert.True(ok == 0, okOutput);
            Assert.Contains("OK   auth.ip_banned  rows=3", okOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(builder.Password ?? Canary, okOutput, StringComparison.Ordinal);

            using (var db = new NpgsqlConnection(target))
            {
                db.Open();
                using NpgsqlCommand drop = db.CreateCommand();
                drop.CommandText = "DELETE FROM ip_banned WHERE \"Ip\" = '10.0.0.3'";
                drop.ExecuteNonQuery();
            }

            (int failed, string failedOutput) = Run(args);
            Assert.Equal(1, failed);
            Assert.Contains("FAIL auth.ip_banned", failedOutput, StringComparison.Ordinal);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            builder.Database = "postgres";
            using var admin = new NpgsqlConnection(builder.ConnectionString);
            admin.Open();
            using NpgsqlCommand cleanup = admin.CreateCommand();
            cleanup.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)";
            cleanup.ExecuteNonQuery();
        }
    }

    [Fact]
    public void SecretScan_ScriptAndRunbookHoldNoCredentialValue()
    {
        string root = RepositorySource.RequireRoot();
        string script = File.ReadAllText(Script);
        string runbook = File.ReadAllText(Path.Combine(root, "docs", "ops", "database-upgrade.md"));
        int section = runbook.IndexOf("### Cutover of the live realm", StringComparison.Ordinal);
        Assert.True(section >= 0, "the cutover section is missing from the runbook");

        // A password is only ever a variable or a mask, never a literal.
        var literal = new Regex("Password=[^$*<\\s;\"'.]");
        Assert.DoesNotMatch(literal, script);
        Assert.DoesNotMatch(literal, runbook[section..]);

        // Nor a Pwd= form or a JSON or assignment literal ("Password": "value", Password = value).
        var other = new Regex("(?i)(?<![$.])\\b(?:Password|Pwd)\"?\\s*[:=]\\s*\"?[A-Za-z0-9]");
        Assert.DoesNotMatch(other, script);
        Assert.DoesNotMatch(other, runbook[section..]);
        Assert.DoesNotMatch(new Regex("(?i)(api[_-]?key|token|secret)\\s*[:=]\\s*['\"][^'\"]{8,}"), script);
    }

    private (int Code, string Output) Verify()
        => Run("-Step", "Verify", "-LiveProfile", _profile, "-BinDir", ArcaneDbDirectory(), "-TargetSqliteDir", _target);

    /// <summary>
    /// The directory of the arcane-db the solution build wrote (tools/ArcaneCore.DbUpgrade, same configuration as this test run). The script runs that tool
    /// on its own runtime; the CI builds the whole solution before it tests, so it exists there. A missing build fails the test, it is never skipped.
    /// </summary>
    private static string ArcaneDbDirectory()
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        string configuration = AppContext.BaseDirectory.Contains(separator + "Debug" + separator, StringComparison.Ordinal) ? "Debug" : "Release";
        string bin = Path.Combine(RepositorySource.RequireRoot(), "tools", "ArcaneCore.DbUpgrade", "bin", configuration);
        string? dll = Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, "arcane-db.dll", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        Assert.True(dll is not null, $"arcane-db has not been built under {bin}; build ArcaneCore.slnx ({configuration}) before running these tests");
        return Path.GetDirectoryName(dll)!;
    }

    private static DatabaseConnectionOptions SqliteSection(string path)
        => new() { Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=" + path };

    /// <summary>
    /// Real auth, characters and world databases (the schema bootstrap of arcane-db upgrade) in <paramref name="directory"/>, three rows in auth ip_banned,
    /// and an appsettings.json that names the files with relative paths, as the live profile does.
    /// </summary>
    private static async Task BuildFixtureAsync(string directory)
    {
        var options = new DatabaseOptions
        {
            Auth = SqliteSection(Path.Combine(directory, "auth.db")),
            Characters = SqliteSection(Path.Combine(directory, "characters.db")),
            World = SqliteSection(Path.Combine(directory, "world.db")),
        };
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await DbUpgradeCli.RunAsync(["upgrade"], options, output, error, CancellationToken.None);
        Assert.True(code == 0, output.ToString() + error);
        SqliteConnection.ClearAllPools();
        Execute(
            Path.Combine(directory, "auth.db"),
            "INSERT INTO ip_banned (Ip, BanDate, UnbanDate, BannedBy, BanReason) VALUES ('10.0.0.1', 0, 0, 'gm', 'a'), ('10.0.0.2', 0, 0, 'gm', 'b'), ('10.0.0.3', 0, 0, 'gm', 'c')");
        File.WriteAllText(
            Path.Combine(directory, "appsettings.json"),
            "{ \"Database\": {"
            + " \"Auth\": { \"Provider\": \"Sqlite\", \"ConnectionString\": \"Data Source=auth.db\" },"
            + " \"Characters\": { \"Provider\": \"Sqlite\", \"ConnectionString\": \"Data Source=characters.db\" },"
            + " \"World\": { \"Provider\": \"Sqlite\", \"ConnectionString\": \"Data Source=world.db\" } } }",
            new UTF8Encoding(false));
    }

    /// <summary>The three database files of <paramref name="from"/> (and any journal beside them) copied into <paramref name="to"/>.</summary>
    private static void CloneFixture(string from, string to)
    {
        SqliteConnection.ClearAllPools();
        foreach (string file in Directory.GetFiles(from, "*.db*"))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
    }

    private static void Execute(string path, params string[] statements)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        foreach (string statement in statements)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }

    private string WriteSecretFile(string host, string port, string user, string password, string? authDatabase = null)
    {
        string path = Path.Combine(_root, "secret-" + Guid.NewGuid().ToString("N") + ".json");
        string auth = authDatabase is null ? string.Empty : $", \"AuthDatabase\": \"{authDatabase}\"";
        File.WriteAllText(
            path,
            "{ \"Host\": \"" + host + "\", \"Port\": \"" + port + "\", \"Username\": \"" + user + "\", \"Password\": \"" + password + "\"" + auth + " }",
            new UTF8Encoding(false));
        return path;
    }

    private static (int Code, string Output) Run(params string[] scriptArguments)
    {
        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(Script);
        foreach (string argument in scriptArguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("pg-cutover.ps1 did not finish within 180 s");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private sealed class PwshFactAttribute : FactAttribute
    {
        public PwshFactAttribute()
        {
            if (!PwshProbe.Available)
            {
                Skip = "pwsh (PowerShell 7) is not on PATH on this host.";
            }
        }
    }

    private sealed class PostgresPwshFactAttribute : FactAttribute
    {
        public PostgresPwshFactAttribute()
        {
            if (!PwshProbe.Available)
            {
                Skip = "pwsh (PowerShell 7) is not on PATH on this host.";
            }
            else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresVariable)))
            {
                Skip = PostgresVariable + " is not set (the PostgreSQL provider tests run on the hosted CI).";
            }
        }
    }

    private static class PwshProbe
    {
        public static readonly bool Available = Probe();

        private static bool Probe()
        {
            try
            {
                var start = new ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.Major"])
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using Process process = Process.Start(start)!;
                string text = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return process.ExitCode == 0 && int.TryParse(text, out int major) && major >= 7;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
    }
}
