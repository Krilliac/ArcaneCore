using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Ops;

/// <summary>
/// The operator scripts that gate and diagnose builds (tools/ops/ci, tools/ops/diag, scripts). Each test runs the real script against a temp
/// directory: nothing here touches the real lanes root, the live allowlist or the caller's checkout. Each test declares the tools it needs
/// (<c>pwsh</c>, <c>bash</c>, <c>git</c>, Windows for run-in-slot.ps1) through <see cref="OpsFactAttribute"/>; a host without one reports the
/// test as SKIPPED with the reason. A skip is not a pass: the test did not run there, so read the skipped count.
/// </summary>
public sealed class OpsScriptTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "arcane-ops-" + Guid.NewGuid().ToString("N"));

    public OpsScriptTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ArcaneCore.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("ArcaneCore.slnx not found above " + AppContext.BaseDirectory);
    }

    private static string Script(string relative) => Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));

    private static ProcessStartInfo Start(string file, IEnumerable<string> args, string? workDir = null, IDictionary<string, string>? env = null)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        if (workDir is not null)
        {
            info.WorkingDirectory = workDir;
        }

        if (env is not null)
        {
            foreach ((string k, string v) in env)
            {
                info.Environment[k] = v;
            }
        }

        return info;
    }

    private static (int Exit, string Output) Run(ProcessStartInfo info, int timeoutMs = 120_000)
    {
        using Process p = Process.Start(info)!;
        p.StandardInput.Close();
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            p.Kill(entireProcessTree: true);
            Assert.Fail("timed out: " + info.FileName + " " + string.Join(' ', info.ArgumentList));
        }

        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    private static (int Exit, string Output) Pwsh(string script, IEnumerable<string> args, int timeoutMs = 120_000)
        => Run(Start("pwsh", ["-NoProfile", "-NonInteractive", "-File", script, .. args]), timeoutMs);

    /// <summary>The first bash on the PATH that is not Windows' WSL launcher (System32\bash.exe), which cannot run these scripts.</summary>
    private static string? FindBash()
    {
        string name = OperatingSystem.IsWindows() ? "bash.exe" : "bash";
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, name);
            if (File.Exists(candidate) && !candidate.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Bash() => FindBash() ?? throw new InvalidOperationException("no bash on the PATH (the test should have been skipped)");

    [Flags]
    private enum Needs
    {
        None = 0,
        Pwsh = 1,
        Bash = 2,
        Git = 4,
        Windows = 8,
    }

    /// <summary>A fact that is skipped, with the reason, when the host lacks a tool it needs. A skip is not a pass.</summary>
    private sealed class OpsFactAttribute : FactAttribute
    {
        public OpsFactAttribute(Needs needs = Needs.None)
        {
            var missing = new List<string>();
            if (needs.HasFlag(Needs.Windows) && !OperatingSystem.IsWindows())
            {
                missing.Add("Windows (run-in-slot.ps1 uses Get-Counter, taskkill and USERPROFILE)");
            }

            if (needs.HasFlag(Needs.Pwsh) && !OnPath("pwsh"))
            {
                missing.Add("pwsh");
            }

            if (needs.HasFlag(Needs.Bash) && FindBash() is null)
            {
                missing.Add("bash");
            }

            if (needs.HasFlag(Needs.Git) && !OnPath("git"))
            {
                missing.Add("git");
            }

            if (missing.Count > 0)
            {
                Skip = "not run on this host, needs " + string.Join(", ", missing) + ". A skip is not a pass.";
            }
        }
    }

    private static bool OnPath(string tool)
    {
        string[] names = OperatingSystem.IsWindows() ? [tool + ".exe", tool + ".cmd"] : [tool];
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string name in names)
            {
                if (File.Exists(Path.Combine(dir, name)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    // ---- full-suite-gate.ps1 --------------------------------------------------------------------------------------------------------

    [OpsFact]
    public void TheFullSuiteGate_DefaultSuites_AreExactlyCiYmlsSolutionFilters()
    {
        string gate = File.ReadAllText(Script("tools/ops/ci/full-suite-gate.ps1"));
        Match m = Regex.Match(gate, @"\[string\[\]\]\$Suites\s*=\s*@\(([^)]*)\)");
        Assert.True(m.Success, "the default -Suites list was not found");
        string[] defaults = [.. Regex.Matches(m.Groups[1].Value, "'([^']+)'").Select(x => x.Groups[1].Value).Order(StringComparer.Ordinal)];

        string[] filters = [.. Directory.GetFiles(Script("tests/ci"), "*.slnf").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal)];
        Assert.NotEmpty(filters);
        Assert.Equal(filters, defaults);

        Match matrix = Regex.Match(File.ReadAllText(Script(".github/workflows/ci.yml")), @"suite:\s*\[([^\]]*)\]");
        Assert.True(matrix.Success, "ci.yml's suite matrix was not found");
        string[] ci = [.. matrix.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)];
        Assert.Equal(ci, defaults);

        // The gate must run the filters, not per-project folders (which is how Atlas and Gateway were skipped).
        Assert.Contains("tests/ci/$s.slnf", gate, StringComparison.Ordinal);
    }

    // ---- hotmodule-hash.ps1 ---------------------------------------------------------------------------------------------------------

    private (string Dll, string Sha) Module()
    {
        string dll = Path.Combine(_temp, "Mod.dll");
        byte[] bytes = Encoding.ASCII.GetBytes("module bytes " + Guid.NewGuid());
        File.WriteAllBytes(dll, bytes);
        return (dll, Sha(bytes));
    }

    private string[] ApproveInto(string dll, string allowlistText)
    {
        string list = Path.Combine(_temp, "allowlist.txt");
        File.WriteAllText(list, allowlistText);
        (int exit, string output) = Pwsh(Script("scripts/hotmodule-hash.ps1"), ["-Name", "Mod", "-Dll", dll, "-Allowlist", list, "-Label", "lbl"]);
        Assert.True(exit == 0, output);
        return [.. File.ReadAllLines(list).Where(l => l.Length > 0)];
    }

    [OpsFact(Needs.Pwsh)]
    public void HotModuleHash_AHashOnlyInACommentLine_IsNotApproved_AndIsAppended()
    {
        (string dll, string sha) = Module();
        string[] lines = ApproveInto(dll, "# header\n# " + sha + "  Mod revoked\n");
        Assert.Equal(3, lines.Length);
        Assert.StartsWith(sha, lines[2], StringComparison.OrdinalIgnoreCase);
    }

    [OpsFact(Needs.Pwsh)]
    public void HotModuleHash_AHashOnlyInAnotherLinesLabel_IsNotApproved_AndIsAppended()
    {
        (string dll, string sha) = Module();
        string other = new('a', 64);
        string[] lines = ApproveInto(dll, other + "  Other superseded-by-" + sha + "\n");
        Assert.Equal(2, lines.Length);
        Assert.StartsWith(sha, lines[1], StringComparison.OrdinalIgnoreCase);
    }

    [OpsFact(Needs.Pwsh)]
    public void HotModuleHash_AListedHash_IsLeftAlone_InEitherCase()
    {
        (string dll, string sha) = Module();
        string[] lines = ApproveInto(dll, "# header\n" + sha.ToLowerInvariant() + "  Mod earlier\n");
        Assert.Equal(2, lines.Length);
    }

    [OpsFact(Needs.Pwsh)]
    public void HotModuleHash_AFileWithoutAFinalNewline_GetsTheEntryOnItsOwnLine()
    {
        (string dll, string sha) = Module();
        string other = new('b', 64);
        string[] lines = ApproveInto(dll, other + "  Other");
        Assert.Equal(2, lines.Length);
        Assert.Equal(other + "  Other", lines[0]);
        Assert.StartsWith(sha, lines[1], StringComparison.OrdinalIgnoreCase);
    }

    // ---- bisect-one.sh --------------------------------------------------------------------------------------------------------------

    [OpsFact(Needs.Bash | Needs.Git)]
    public void BisectOne_RefusesADirtyWorktree_AndKeepsItsEdits()
    {
        string repo = Path.Combine(_temp, "repo");
        Directory.CreateDirectory(repo);
        void Git(params string[] args)
        {
            (int exit, string output) = Run(Start("git", ["-c", "user.name=t", "-c", "user.email=t@example.invalid", .. args], repo));
            Assert.True(exit == 0, output);
        }

        Git("init", "-q");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "committed\n");
        Git("add", "a.txt");
        Git("commit", "-q", "-m", "one");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "uncommitted work\n");
        File.WriteAllText(Path.Combine(repo, "new.txt"), "untracked work\n");

        var env = new Dictionary<string, string>
        {
            ["ARCANECORE_BISECT_WORKTREE"] = repo,
            ["ARCANECORE_DATA_ROOT"] = _temp,
            ["ARCANECORE_BISECT_LOG_DIR"] = Path.Combine(_temp, "logs"),
        };
        (int exit, string output) = Run(Start(Bash(), [Script("tools/ops/ci/bisect-one.sh"), "HEAD"], env: env));

        Assert.Equal(97, exit);
        Assert.Contains("REFUSED", output, StringComparison.Ordinal);
        Assert.Equal("uncommitted work\n", File.ReadAllText(Path.Combine(repo, "a.txt")).ReplaceLineEndings("\n"));
        Assert.True(File.Exists(Path.Combine(repo, "new.txt")), "an untracked file was deleted");
    }

    // ---- hang-stacks.sh -------------------------------------------------------------------------------------------------------------

    /// <summary>Runs hang-stacks.sh (one capture) against a fake dotnet-stack, lets it settle, then calls <paramref name="touchLog"/>.</summary>
    private string[] HangStacks(string initialLog, Action<string> touchLog)
    {
        string home = Path.Combine(_temp, "home");
        string tools = Path.Combine(home, ".dotnet", "tools");
        Directory.CreateDirectory(tools);
        string fake = Path.Combine(tools, "dotnet-stack.exe");
        File.WriteAllText(fake, "#!/usr/bin/env bash\necho fake stack for $3\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        string log = Path.Combine(_temp, "world.log");
        File.WriteAllText(log, initialLog);
        string outDir = Path.Combine(_temp, "stacks");
        var env = new Dictionary<string, string> { ["USERPROFILE"] = home };
        using Process p = Process.Start(Start(Bash(), [Script("tools/ops/diag/hang-stacks.sh"), "4242", log, "1", outDir], env: env))!;
        p.StandardInput.Close();
        using var started = new ManualResetEventSlim(false);
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null && e.Data.StartsWith("watching ", StringComparison.Ordinal))
            {
                started.Set();
            }
        };
        p.BeginOutputReadLine();
        _ = p.StandardError.ReadToEndAsync();
        Assert.True(started.Wait(30_000), "hang-stacks.sh never reported that it was watching the log"); // it has recorded the log size by now
        touchLog(log);
        if (!p.WaitForExit(20_000))
        {
            p.Kill(entireProcessTree: true);
        }

        return Directory.Exists(outDir) ? Directory.GetFiles(outDir) : [];
    }

    [OpsFact(Needs.Bash)]
    public void HangStacks_CapturesWhenTheHangLineIsFollowedByALargeBurst()
    {
        string[] captured = HangStacks("start\n", log =>
        {
            File.AppendAllText(log, "world tick has been running for 9000 ms\n" + new string('x', 2_000_000) + "\n");
        });
        Assert.Single(captured);
    }

    [OpsFact(Needs.Bash)]
    public void HangStacks_RescansATruncatedLogFromItsStart()
    {
        string[] captured = HangStacks(new string('y', 300_000) + "\n", log =>
        {
            File.WriteAllText(log, "world tick has been running for 9000 ms\n");
        });
        Assert.Single(captured);
    }

    // ---- run-in-slot.ps1 ------------------------------------------------------------------------------------------------------------

    private string SlotRoot() => Path.Combine(_temp, "lanes");

    private string WriteLock(string content, DateTime lastWrite)
    {
        string locks = Path.Combine(SlotRoot(), "slot-locks");
        Directory.CreateDirectory(locks);
        string path = Path.Combine(locks, "slot-1.lock");
        File.WriteAllText(path, content);
        File.SetLastWriteTime(path, lastWrite);
        return path;
    }

    private static string[] SlotArgs(string root, string command, params string[] extra)
        => ["-Lane", "t", "-WorkDir", AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), "-Command", command, "-Slots", "1", "-MinAvailableMB", "1", "-LanesRoot", root, .. extra];

    [OpsFact(Needs.Windows | Needs.Pwsh)]
    public void RunInSlot_AHolderThatIsGone_IsReaped_AndTheSlotIsTaken()
    {
        using Process dead = Process.Start(Start("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "exit 0"]))!;
        dead.WaitForExit();
        WriteLock($"other pid={dead.Id} since=x timeout=45", DateTime.Now.AddMinutes(-2));

        (int exit, string output) = Pwsh(Script("tools/ops/ci/run-in-slot.ps1"), SlotArgs(SlotRoot(), "Write-Output ran"));

        Assert.True(exit == 0, output);
        Assert.Contains("holding slot-1.lock", output, StringComparison.Ordinal);
    }

    [OpsFact(Needs.Windows | Needs.Pwsh)]
    [Trait("Category", "Timing")]
    public void RunInSlot_ALiveHolder_IsNeverReapedForAge_ButIsOnceItOutlivedItsOwnTimeout()
    {
        using Process live = Process.Start(Start("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 300"]))!;
        try
        {
            Thread.Sleep(32_000); // a lock must be older than 30 s to be judged, and not older than its holder process
            DateTime written = DateTime.Now.AddSeconds(-31);

            // Alive, no declared timeout, StaleMin 0 (everything counts as old): the old code freed this slot at once.
            string path = WriteLock($"other pid={live.Id} since=x", written);
            string root = SlotRoot();
            using (Process waiter = Process.Start(Start("pwsh", ["-NoProfile", "-NonInteractive", "-File", Script("tools/ops/ci/run-in-slot.ps1"), .. SlotArgs(root, "Write-Output ran", "-StaleMin", "0")]))!)
            {
                waiter.StandardInput.Close();
                var seen = new StringBuilder();
                waiter.OutputDataReceived += (_, e) =>
                {
                    lock (seen)
                    {
                        seen.AppendLine(e.Data);
                    }
                };
                waiter.BeginOutputReadLine();
                _ = waiter.StandardError.ReadToEndAsync();
                SpinWait.SpinUntil(() => { lock (seen) { return seen.ToString().Contains("waiting for a build slot", StringComparison.Ordinal) || seen.ToString().Contains("holding", StringComparison.Ordinal); } }, 60_000);
                string seenText;
                lock (seen)
                {
                    seenText = seen.ToString();
                }

                waiter.Kill(entireProcessTree: true);
                Assert.Contains("waiting for a build slot", seenText, StringComparison.Ordinal);
                Assert.DoesNotContain("holding", seenText, StringComparison.Ordinal);
                Assert.True(File.Exists(path), "the live holder's slot file was removed");
            }

            // Alive but past the timeout it declared (grace 0): reaped, and the slot is taken.
            WriteLock($"other pid={live.Id} since=x timeout=0", written);
            (int exit, string output) = Pwsh(Script("tools/ops/ci/run-in-slot.ps1"), SlotArgs(root, "Write-Output ran", "-TimeoutGraceMin", "0"));
            Assert.True(exit == 0, output);
            Assert.Contains("holding slot-1.lock", output, StringComparison.Ordinal);
        }
        finally
        {
            live.Kill(entireProcessTree: true);
        }
    }

    [OpsFact(Needs.Windows | Needs.Pwsh)]
    public void RunInSlot_TheRelease_LeavesASlotFileThatNowBelongsToAnotherRun()
    {
        string root = SlotRoot();
        string lockPath = Path.Combine(root, "slot-locks", "slot-1.lock");
        string command = $"Set-Content -LiteralPath '{lockPath.Replace("'", "''")}' -Value 'other pid=1 since=x'";

        (int exit, string output) = Pwsh(Script("tools/ops/ci/run-in-slot.ps1"), SlotArgs(root, command));

        Assert.True(exit == 0, output);
        Assert.True(File.Exists(lockPath), "the release deleted a lock that is not this run's");
    }
}
