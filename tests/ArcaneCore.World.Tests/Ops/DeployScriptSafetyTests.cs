using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// The safety gates of the Python deploy tools (tools/ops/deploy/deploy-live.py, deploy-post-checks.py) and the exception audit
/// (scripts/exception-audit/audit.py), run under <c>python -O</c>: an <c>assert</c> is compiled out there, so a gate written as one
/// stops checking and lets the deploy continue. Every run here uses -O on purpose.
/// </summary>
public sealed partial class DeployScriptSafetyTests
{
    private static readonly string Root = RepositorySource.RequireRoot();
    private static readonly string DeployLive = Path.Combine(Root, "tools", "ops", "deploy", "deploy-live.py");
    private static readonly string PostChecks = Path.Combine(Root, "tools", "ops", "deploy", "deploy-post-checks.py");
    private static readonly string Audit = Path.Combine(Root, "scripts", "exception-audit", "audit.py");

    [PythonFact]
    public void OptimizedPython_StripsAsserts()
    {
        // The premise of every other test here: if -O did not strip asserts, the assert-based gates would still fire and prove nothing.
        (int code, _) = Python(["-c", "assert False, 'still checked'"]);
        Assert.Equal(0, code);
    }

    [PythonTheory]
    [InlineData("tools/ops/deploy/deploy-live.py")]
    [InlineData("tools/ops/deploy/deploy-post-checks.py")]
    [InlineData("scripts/exception-audit/audit.py")]
    public void Script_HasNoAssertStatements(string relative)
    {
        (int code, string output) = Python(["-c",
            "import ast,sys; t=ast.parse(open(sys.argv[1],encoding='utf-8').read()); "
            + "print(sorted(n.lineno for n in ast.walk(t) if isinstance(n, ast.Assert)))",
            Path.Combine(Root, relative)]);
        Assert.True(code == 0, output);
        Assert.Equal("[]", output.Trim());
    }

    [PythonFact]
    public void PostChecks_StatusConsole_IsOpenedSupervising_SoItCannotReplaceTheSupervisorWithAReadOnlyConsole()
    {
        // server-status.ps1 closes older consoles for the same World port; without -Supervise the new one is read-only (no crash/hang recovery).
        (int code, string output) = Python(["-c",
            "import importlib.util,sys; s=importlib.util.spec_from_file_location('pc',sys.argv[1]); m=importlib.util.module_from_spec(s); s.loader.exec_module(m); "
            + "a,e=m.status_console_launch('s.ps1','prof',8085,3724); print(a[-1]); print(e['ARCANECORE_LIVE_SERVER']); print(m.console_running_script(8085).count('8085'))",
            PostChecks]);
        Assert.True(code == 0, output);
        Assert.Equal(["-Supervise", "1", "2"], output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    [PythonFact]
    public void DeployLive_DbcHashMismatch_StopsBeforeTheRunDirectoryIsCreated()
    {
        using var box = new Sandbox();
        string art = box.Dir("artifacts");
        foreach (string rel in new[]
                 {
                     "bin/ArcaneCore.World/release/ArcaneCore.World.dll", "bin/ArcaneCore.Realm/release/ArcaneCore.Realm.dll",
                     "bin/ArcaneCore.DbUpgrade/release/arcane-db.exe", "bin/ArcaneCore.AccountTool/release/arcane-account.exe",
                     "bin/ArcaneCore.MockClient/release/arcane-mock.exe", "bin/ArcaneCore.ContentImporter/release/arcane-content-importer.dll",
                     "tools/content/refresh-world-content.ps1", "tools/content/set-optional-data.ps1",
                 })
        {
            box.File(Path.Combine(art, rel), "x");
        }

        string dbc = box.Dir("dbc");
        string[] needed = ["AreaTrigger.dbc", "WorldSafeLocs.dbc", "Map.dbc", "AreaTable.dbc", "WMOAreaTable.dbc", "TaxiNodes.dbc", "TaxiPath.dbc", "TaxiPathNode.dbc"];
        foreach (string n in needed)
        {
            box.File(Path.Combine(dbc, n), "not the listed bytes");
        }

        box.File(Path.Combine(dbc, "SHA256SUMS"), string.Concat(needed.Select(n => new string('0', 64) + "  " + n + "\n")));
        string dump = box.File(Path.Combine(box.Dir("refs"), "dump.sql.gz"), "x");
        string outDir = Path.Combine(box.Root, "run");

        (int code, string output) = Python([DeployLive, "--profile", box.Dir("profile"), "--label", "t", "--out", outDir, "--artifacts", art,
            "--terrain", "", "--dbc-dir", dbc, "--dump", dump, "--no-optional-data", "--no-elevators"]);

        Assert.NotEqual(0, code);
        Assert.False(Directory.Exists(outDir), "the run directory was created: the deploy went past the DBC hash check\n" + output);
        Assert.Contains("does not match", output);
    }

    [PythonFact]
    public void PostChecks_AlreadyHealthyRun_IsRefusedAndTheReportIsLeftAlone()
    {
        using var box = new Sandbox();
        (string deployDir, string reportPath) = PostCheckRun(box, healthy: true, worldPort: 1, authPort: 2);
        string before = File.ReadAllText(reportPath);

        (int code, string output) = Python([PostChecks, "--label", "t", "--deploy-dir", deployDir]);

        Assert.NotEqual(0, code);
        Assert.Equal(before, File.ReadAllText(reportPath));
        Assert.Contains("already marked healthy", output);
    }

    [WindowsPythonFact]
    public void PostChecks_NewWorldNotListening_IsRefusedAndTheReportIsLeftAlone()
    {
        // Nothing listens on port 1 on a Windows host; the report names PIDs that are not the listeners.
        using var box = new Sandbox();
        (string deployDir, string reportPath) = PostCheckRun(box, healthy: false, worldPort: 1, authPort: 2);
        string before = File.ReadAllText(reportPath);

        (int code, string output) = Python([PostChecks, "--label", "t", "--deploy-dir", deployDir]);

        Assert.NotEqual(0, code);
        Assert.Equal(before, File.ReadAllText(reportPath));
        Assert.Contains("is not the world listener", output);
    }

    [WindowsPythonFact]
    public void PostChecks_NonIntegerPort_NeverReachesPowerShell()
    {
        using var box = new Sandbox();
        string marker = Path.Combine(box.Root, "injected");
        // Closes the Get-NetTCPConnection expression, runs a command of its own and comments the rest out.
        string port = $"1) ; Set-Content -LiteralPath '{marker}' -Value pwned #";
        (string deployDir, string reportPath) = PostCheckRun(box, healthy: false, worldPort: port, authPort: 2);
        string before = File.ReadAllText(reportPath);

        // Without -O, as an operator runs it (`py -3`): under -O the old assert took the whole listener probe with it, so nothing ran.
        (int code, string output) = Python([PostChecks, "--label", "t", "--deploy-dir", deployDir], optimize: false);

        Assert.NotEqual(0, code);
        Assert.False(File.Exists(marker), "the Port value was executed by PowerShell\n" + output);
        Assert.Contains("Port", output);
        Assert.Equal(before, File.ReadAllText(reportPath));
    }

    [WindowsPythonFact]
    public void DeployLive_WorldDatabaseCheck_TakesThePathAsData()
    {
        // The exclusive-open probe before the content refresh: a free file whose path holds an apostrophe is free, and a path built to
        // break out of a quoted PowerShell string is only a (missing) file name.
        using var box = new Sandbox();
        string free = box.File(Path.Combine(box.Dir("it's here"), "world.db"), "x");
        string marker = Path.Combine(box.Root, "injected");
        string hostile = Path.Combine(box.Root, $"x'+$(Set-Content -LiteralPath '{marker}' -Value pwned)+'.db");

        (int code, string output) = Python(["-c",
            "import importlib.util,json,sys; s=importlib.util.spec_from_file_location('deploy_live', sys.argv[1]); "
            + "m=importlib.util.module_from_spec(s); s.loader.exec_module(m); "
            + "print(json.dumps([m.world_db_free(sys.argv[2]), m.world_db_free(sys.argv[3])]))",
            DeployLive, free, hostile]);

        Assert.False(File.Exists(marker), "the World database path was executed by PowerShell\n" + output);
        Assert.True(code == 0, output);
        Assert.Equal("[true, false]", output.Trim().Split('\n')[^1].Trim());
    }

    [GitPythonFact]
    public void Audit_OptionLikeBaseRef_IsRejectedBeforeGitSeesIt()
    {
        using var box = new Sandbox();
        string repo = AuditRepo(box);
        string written = Path.Combine(box.Root, "written-by-git.tar");

        (int code, string output) = Python([Audit, repo, "--output=" + written], TempEnv(box));

        Assert.NotEqual(0, code);
        Assert.False(File.Exists(written), "git archive took the base ref as an option and wrote a file\n" + output);
        Assert.Contains("base ref", output);
        Assert.False(File.Exists(Path.Combine(repo, "docs", "ops", "exception-audit.md")), output);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(box.Root, "tmp")));
    }

    [GitPythonFact]
    public void Audit_UnknownBaseRef_IsRejected()
    {
        using var box = new Sandbox();
        string repo = AuditRepo(box);

        (int code, string output) = Python([Audit, repo, "no-such-ref"], TempEnv(box));

        Assert.NotEqual(0, code);
        Assert.Contains("base ref", output);
        Assert.False(File.Exists(Path.Combine(repo, "docs", "ops", "exception-audit.md")), output);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(box.Root, "tmp")));
    }

    [GitPythonFact]
    public void Audit_ValidBaseRef_WritesTheTableAndRemovesItsWorkDirectory()
    {
        using var box = new Sandbox();
        string repo = AuditRepo(box);

        (int code, string output) = Python([Audit, repo, "HEAD"], TempEnv(box));

        Assert.True(code == 0, output);
        string table = File.ReadAllText(Path.Combine(repo, "docs", "ops", "exception-audit.md"));
        Assert.Contains("| total | 1 |", table);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(box.Root, "tmp")));
    }

    private static (string DeployDir, string ReportPath) PostCheckRun(Sandbox box, bool healthy, object worldPort, object authPort)
    {
        string profile = box.Dir("profile");
        string chars = box.File(Path.Combine(box.Dir("db"), "characters.db"), "");
        box.File(Path.Combine(profile, "appsettings.json"), JsonSerializer.Serialize(new
        {
            World = new { Port = worldPort },
            Auth = new { Port = authPort },
            Database = new { Characters = new { ConnectionString = "Data Source=" + chars } },
        }));
        string deployDir = box.Dir("deploy");
        string reportPath = box.File(Path.Combine(box.Dir("deploy/t"), "report.json"), JsonSerializer.Serialize(new
        {
            healthy,
            profile,
            artifacts = box.Dir("artifacts"),
            new_world = 2_000_000_001,
            new_realm = 2_000_000_002,
            bots_stopped = Array.Empty<object>(),
            characters_stopped = Array.Empty<object>(),
            population_stopped = Array.Empty<object>(),
        }));
        return (deployDir, reportPath);
    }

    /// <summary>A one-commit git repository with one catch site under src/ and the docs/ops directory audit.py writes into.</summary>
    private static string AuditRepo(Sandbox box)
    {
        string repo = box.Dir("repo");
        box.File(Path.Combine(repo, "src", "A.cs"), "class A { void M() { try { } catch (System.Exception e) { _ = e; throw; } } }\n");
        box.Dir("repo/docs/ops");
        foreach (string[] argv in new[]
                 {
                     new[] { "init", "-q" },
                     new[] { "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", "add", "src" },
                     new[] { "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "init" },
                 })
        {
            (int code, string output) = Run("git", ["-C", repo, .. argv], null);
            Assert.True(code == 0, "git " + string.Join(' ', argv) + ": " + output);
        }

        box.Dir("tmp");
        return repo;
    }

    private static Dictionary<string, string> TempEnv(Sandbox box)
    {
        string tmp = Path.Combine(box.Root, "tmp");
        return new Dictionary<string, string> { ["TMP"] = tmp, ["TEMP"] = tmp, ["TMPDIR"] = tmp };
    }

    /// <summary>Runs the interpreter isolated (-I), with asserts stripped (-O) and no .pyc written into the checkout (-B).</summary>
    private static (int Code, string Output) Python(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? env = null, bool optimize = true)
    {
        (string exe, string[] prefix) = PythonProbe.Interpreter!.Value;
        string[] flags = optimize ? ["-I", "-O", "-B"] : ["-I", "-B"];
        return Run(exe, [.. prefix, .. flags, .. arguments], env);
    }

    private static (int Code, string Output) Run(string exe, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? env)
    {
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in arguments)
        {
            start.ArgumentList.Add(a);
        }

        foreach (string name in new[] { "ARCANECORE_LIVE_PROFILE", "ARCANECORE_LIVE_ROOT", "ARCANECORE_DEPLOY_ROOT", "ARCANECORE_DATA_ROOT", "ARCANECORE_REFS_ROOT" })
        {
            start.Environment.Remove(name);
        }

        foreach ((string k, string v) in env ?? new Dictionary<string, string>())
        {
            start.Environment[k] = v;
        }

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{exe} did not finish within 120 s");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "arcane-deploy-gates-" + Guid.NewGuid().ToString("N"));

        public Sandbox() => Directory.CreateDirectory(Root);

        public string Dir(string relative)
        {
            string path = Path.Combine(Root, relative);
            Directory.CreateDirectory(path);
            return path;
        }

        public string File(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A child process may still hold a handle for a moment; the OS temp directory is cleaned eventually.
            }
            catch (UnauthorizedAccessException)
            {
                // git marks its object files read-only on Windows.
            }
        }
    }

    private static partial class PythonProbe
    {
        /// <summary>A Python 3.10+ interpreter (the scripts target 3.12), or null when the host has none.</summary>
        public static readonly (string Exe, string[] Prefix)? Interpreter = Find();

        public static readonly bool Git = Works("git", ["--version"], out _);

        private static (string, string[])? Find()
        {
            (string, string[])[] candidates = OperatingSystem.IsWindows()
                ? [("python", []), ("py", ["-3"])]
                : [("python3", []), ("python", [])];
            foreach ((string exe, string[] prefix) in candidates)
            {
                if (Works(exe, [.. prefix, "--version"], out string text)
                    && VersionPattern().Match(text) is { Success: true } m
                    && int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 10)
                {
                    return (exe, prefix);
                }
            }

            return null;
        }

        private static bool Works(string exe, string[] arguments, out string output)
        {
            output = "";
            try
            {
                var start = new ProcessStartInfo(exe)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (string a in arguments)
                {
                    start.ArgumentList.Add(a);
                }

                using Process process = Process.Start(start)!;
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(15_000))
                {
                    process.Kill(entireProcessTree: true);
                    return false;
                }

                output += stderr.Result;
                return process.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        [GeneratedRegex(@"Python 3\.(\d+)")]
        private static partial Regex VersionPattern();
    }

    /// <summary>Runs only where a Python 3.10+ interpreter is on PATH (CI's ubuntu-latest has python3).</summary>
    private sealed class PythonFactAttribute : FactAttribute
    {
        public PythonFactAttribute()
        {
            if (PythonProbe.Interpreter is null)
            {
                Skip = "no Python 3.10+ interpreter on PATH on this host.";
            }
        }
    }

    private sealed class PythonTheoryAttribute : TheoryAttribute
    {
        public PythonTheoryAttribute()
        {
            if (PythonProbe.Interpreter is null)
            {
                Skip = "no Python 3.10+ interpreter on PATH on this host.";
            }
        }
    }

    /// <summary>The PowerShell gates (Get-NetTCPConnection, the exclusive-open probe) exist only on Windows.</summary>
    private sealed class WindowsPythonFactAttribute : FactAttribute
    {
        public WindowsPythonFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Windows PowerShell gate; not on a Windows host.";
            }
            else if (PythonProbe.Interpreter is null)
            {
                Skip = "no Python 3.10+ interpreter on PATH on this host.";
            }
        }
    }

    private sealed class GitPythonFactAttribute : FactAttribute
    {
        public GitPythonFactAttribute()
        {
            if (PythonProbe.Interpreter is null || !PythonProbe.Git)
            {
                Skip = "needs Python 3.10+ and git on PATH.";
            }
        }
    }
}
