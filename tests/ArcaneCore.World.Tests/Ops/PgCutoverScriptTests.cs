using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
    public void Verify_OfMatchingDatabases_ReportsEqualCounts_AndExitsZero()
    {
        BuildFixture(_profile);
        BuildFixture(_target);

        (int code, string output) = Verify();

        Assert.True(code == 0, output);
        Assert.Contains("OK   auth.account  rows=3", output, StringComparison.Ordinal);
        Assert.Contains("OK   auth.account_access  rows=2", output, StringComparison.Ordinal);
        Assert.Contains("OK   characters.character  rows=2", output, StringComparison.Ordinal);
        Assert.Contains("OK   world.creature_template  rows=2", output, StringComparison.Ordinal);
        Assert.Contains("0 difference(s)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("FAIL", output, StringComparison.Ordinal);
    }

    [PwshFact]
    public void Verify_FailsWhenARowWasDropped()
    {
        BuildFixture(_profile);
        BuildFixture(_target);
        Execute(Path.Combine(_target, "auth.db"), "DELETE FROM account WHERE id = 2");

        (int code, string output) = Verify();

        Assert.Equal(1, code);
        Assert.Contains("FAIL auth.account  source rows=3", output, StringComparison.Ordinal);
        Assert.Contains("target rows=2", output, StringComparison.Ordinal);
        Assert.Contains("OK   characters.character", output, StringComparison.Ordinal);
    }

    [PwshFact]
    public void Verify_FailsWhenAKeyChanged_EvenWithTheSameCount()
    {
        BuildFixture(_profile);
        BuildFixture(_target);
        Execute(Path.Combine(_target, "auth.db"), "UPDATE account_access SET realm = 9 WHERE account = 1 AND realm = 1");

        (int code, string output) = Verify();

        Assert.Equal(1, code);
        Assert.Contains("FAIL auth.account_access", output, StringComparison.Ordinal);
    }

    [PwshFact]
    public void Verify_WarnsWhenTheWorldContentIsEmpty()
    {
        BuildFixture(_profile);
        BuildFixture(_target);
        Execute(Path.Combine(_profile, "world.db"), "DELETE FROM creature_template");
        Execute(Path.Combine(_target, "world.db"), "DELETE FROM creature_template");

        (int code, string output) = Verify();

        Assert.True(code == 0, output);
        Assert.Contains("WARN world.creature_template is empty", output, StringComparison.Ordinal);
    }

    [PwshFact]
    public void Verify_WithAnIncompleteSecretFile_NamesTheKey_AndNeverEchoesThePassword()
    {
        BuildFixture(_profile);
        string secretFile = WriteSecretFile(host: string.Empty, port: "5432", user: "arcane", password: Canary);

        (int code, string output) = Run("-Step", "Verify", "-LiveProfile", _profile, "-BinDir", AppContext.BaseDirectory, "-SecretFile", secretFile);

        Assert.NotEqual(0, code);
        Assert.Contains("has no Host", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, output, StringComparison.Ordinal);
    }

    [PostgresPwshFact]
    public void Verify_AgainstPostgreSql_ReportsMatch_ThenFailsOnADroppedRow()
    {
        string server = Environment.GetEnvironmentVariable(PostgresVariable)!;
        var builder = new NpgsqlConnectionStringBuilder(server.TrimEnd(';')) { Pooling = false, Database = "postgres" };
        string database = "arcane_pgcut_" + Guid.NewGuid().ToString("N")[..16];
        BuildFixture(_profile);
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
            using (var db = new NpgsqlConnection(builder.ConnectionString))
            {
                db.Open();
                using NpgsqlCommand fill = db.CreateCommand();
                fill.CommandText =
                    "CREATE TABLE auth_schema (version integer); INSERT INTO auth_schema VALUES (1);" +
                    "CREATE TABLE account (id bigint PRIMARY KEY, name text);" +
                    "INSERT INTO account VALUES (1, 'a'), (2, 'b'), (3, 'c');" +
                    "CREATE TABLE account_access (account bigint, realm bigint, PRIMARY KEY (account, realm));" +
                    "INSERT INTO account_access VALUES (1, 1), (1, 2);";
                fill.ExecuteNonQuery();
            }

            string[] args = ["-Step", "Verify", "-LiveProfile", _profile, "-BinDir", AppContext.BaseDirectory, "-SecretFile", secretFile, "-Component", "auth"];
            (int ok, string okOutput) = Run(args);
            Assert.True(ok == 0, okOutput);
            Assert.Contains("OK   auth.account  rows=3", okOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(builder.Password ?? Canary, okOutput, StringComparison.Ordinal);

            using (var db = new NpgsqlConnection(builder.ConnectionString))
            {
                db.Open();
                using NpgsqlCommand drop = db.CreateCommand();
                drop.CommandText = "DELETE FROM account WHERE id = 3";
                drop.ExecuteNonQuery();
            }

            (int failed, string failedOutput) = Run(args);
            Assert.Equal(1, failed);
            Assert.Contains("FAIL auth.account", failedOutput, StringComparison.Ordinal);
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
        Assert.DoesNotMatch(new Regex("(?i)(api[_-]?key|token|secret)\\s*[:=]\\s*['\"][^'\"]{8,}"), script);
    }

    private (int Code, string Output) Verify()
        => Run("-Step", "Verify", "-LiveProfile", _profile, "-BinDir", AppContext.BaseDirectory, "-TargetSqliteDir", _target);

    private static void BuildFixture(string directory)
    {
        Execute(
            Path.Combine(directory, "auth.db"),
            "CREATE TABLE auth_schema (version integer);",
            "INSERT INTO auth_schema VALUES (1);",
            "CREATE TABLE account (id integer PRIMARY KEY, name text);",
            "INSERT INTO account VALUES (1, 'a'), (2, 'b'), (3, 'c');",
            "CREATE TABLE account_access (account integer, realm integer, PRIMARY KEY (account, realm));",
            "INSERT INTO account_access VALUES (1, 1), (1, 2);");
        Execute(
            Path.Combine(directory, "characters.db"),
            "CREATE TABLE character (guid integer PRIMARY KEY, name text);",
            "INSERT INTO character VALUES (10, 'x'), (11, 'y');");
        Execute(
            Path.Combine(directory, "world.db"),
            "CREATE TABLE creature_template (entry integer PRIMARY KEY, name text);",
            "INSERT INTO creature_template VALUES (100, 'p'), (101, 'q');");
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
