using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// The live tooling (tools/ops/live) gives each daemon its own log files, and reads the World's log where the World writes it.
/// </summary>
/// <remarks>
/// Live 2026-10-16: the realm and the world both read the profile appsettings.json, which named world.debug.{log,jsonl}, so both wrote
/// one file. RollingFileWriter now refuses to share a file (the second writer falls back to name.&lt;process&gt;.ext), and redeploy.ps1 starts
/// the Realm first, so without per-role paths the Realm would own world.debug.log and livecheck/redeploy would read the Realm's lines
/// as the World's. launch-daemon.ps1 sets the paths per role through the host's environment configuration (Logging__ArcaneCore__*).
/// </remarks>
public sealed class LiveLogPathTests : IDisposable
{
    private static readonly string LiveDir = Path.Combine(RepositorySource.RequireRoot(), "tools", "ops", "live");
    private readonly string _home = Path.Combine(Path.GetTempPath(), "arcane-livehome-" + Guid.NewGuid().ToString("N"));

    public LiveLogPathTests() => Directory.CreateDirectory(_home);

    public void Dispose() => Directory.Delete(_home, recursive: true);

    /// <summary>
    /// The Realm starts first (redeploy.ps1 order) with the environment launch-daemon.ps1 gives it; the World, started next with its own,
    /// still owns world.debug.log and world.debug.jsonl, and nothing falls back to a per-process name.
    /// </summary>
    [PwshFact]
    public void RealmStartedFirst_WorldKeepsWorldDebugLog()
    {
        (string realmText, string realmJson) = LaunchEnvironment("Realm");
        (string worldText, string worldJson) = LaunchEnvironment("World");
        string logs = Path.Combine(_home, "logs");

        var diagnostics = new StringWriter();
        var policy = new RollingFilePolicy(0, false, 0);
        using RollingFileWriter realmTextWriter = new(realmText, policy, diagnostics: diagnostics);
        using RollingFileWriter realmJsonWriter = new(realmJson, policy, diagnostics: diagnostics);
        realmTextWriter.Write("realm\n");
        realmJsonWriter.Write("{\"d\":\"realm\"}\n");
        using RollingFileWriter worldTextWriter = new(worldText, policy, diagnostics: diagnostics);
        using RollingFileWriter worldJsonWriter = new(worldJson, policy, diagnostics: diagnostics);
        worldTextWriter.Write("world\n");
        worldJsonWriter.Write("{\"d\":\"world\"}\n");

        Assert.Equal(Path.Combine(logs, "world.debug.log"), worldTextWriter.ActivePath);
        Assert.Equal(Path.Combine(logs, "world.debug.jsonl"), worldJsonWriter.ActivePath);
        Assert.Equal(Path.Combine(logs, "realm.debug.log"), realmTextWriter.ActivePath);
        Assert.Equal(Path.Combine(logs, "realm.debug.jsonl"), realmJsonWriter.ActivePath);
        Assert.Equal(string.Empty, diagnostics.ToString());
        Assert.Equal(worldText, WorldLogPathFromLiveEnv());
    }

    /// <summary>launch-daemon.ps1 applies the role's log environment before it starts the daemon.</summary>
    [Fact]
    public void LaunchDaemon_AppliesTheRoleLogEnvironment_BeforeStartingTheDaemon()
    {
        string script = File.ReadAllText(Path.Combine(LiveDir, "launch-daemon.ps1"));
        int set = script.IndexOf("Set-LiveDaemonLogEnvironment -Role $Role -LiveHome $LiveHome", StringComparison.Ordinal);
        int start = script.IndexOf("& $exe", StringComparison.Ordinal);
        Assert.True(set >= 0, "launch-daemon.ps1 does not call Set-LiveDaemonLogEnvironment");
        Assert.True(start > set, "launch-daemon.ps1 starts the daemon before setting its log paths");
    }

    /// <summary>livecheck.ps1 and redeploy.ps1 read the World's log through LiveEnv.ps1, not a fixed name, and redeploy checks for fallbacks.</summary>
    [Theory]
    [InlineData("livecheck.ps1")]
    [InlineData("redeploy.ps1")]
    public void LiveTools_ResolveTheWorldLogThroughLiveEnv_AndReportFallbacks(string name)
    {
        string script = File.ReadAllText(Path.Combine(LiveDir, name));
        Assert.DoesNotContain("world.debug.log", script, StringComparison.Ordinal);
        Assert.Contains("Get-LiveDaemonLogPath -Role World", script, StringComparison.Ordinal);
        Assert.Contains("Get-LiveLogFallback", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// Get-LiveLogFallback lists exactly the per-process fallback files (name.&lt;process&gt;[.&lt;pid&gt;].ext and their rolled segments) written
    /// since the given time: not the role's own active file, its rolled segments, a lock file, an older fallback, or another role's files.
    /// </summary>
    [PwshFact]
    public void LogFallback_ListsOnlyFallbackNamesWrittenSince()
    {
        string logs = Path.Combine(_home, "logs");
        Directory.CreateDirectory(logs);
        DateTime since = new(2026, 10, 16, 20, 0, 0, DateTimeKind.Utc);
        string[] recent =
        [
            "world.debug.log", "world.debug.jsonl", "world.debug-20261016-001.log", "world.debug.log.lock",
            "world.debug.ArcaneCore.Realm.log", "world.debug.ArcaneCore.World.4242.jsonl", "world.debug.ArcaneCore.Realm-20261016-001.log",
            "world.debug.ArcaneCore.Realm.log.lock", "realm.debug.log", "realm.debug.ArcaneCore.World.log",
        ];
        foreach (string file in recent)
        {
            Touch(Path.Combine(logs, file), since.AddMinutes(1));
        }

        Touch(Path.Combine(logs, "world.debug.ArcaneCore.Old.log"), since.AddMinutes(-1));

        string output = RunLiveEnv($"""
            @(Get-LiveLogFallback -Role World -LiveHome '{Quote(_home)}' -SinceUtc ([DateTime]::new({since.Ticks}, 'Utc')) | ForEach-Object Name) | Sort-Object | ConvertTo-Json -Compress -AsArray
            """);
        string[] names = JsonSerializer.Deserialize<string[]>(output.Trim())!;
        Assert.Equal(["world.debug.ArcaneCore.Realm-20261016-001.log", "world.debug.ArcaneCore.Realm.log", "world.debug.ArcaneCore.World.4242.jsonl"], names);
    }

    private static void Touch(string path, DateTime utc)
    {
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, utc);
    }

    /// <summary>The text and JSON log paths a daemon of <paramref name="role"/> resolves after launch-daemon.ps1's environment.</summary>
    private (string Text, string Json) LaunchEnvironment(string role)
    {
        string output = RunLiveEnv($"""
            Set-LiveDaemonLogEnvironment -Role {role} -LiveHome '{Quote(_home)}'
            @($env:Logging__ArcaneCore__File__Path, $env:Logging__ArcaneCore__Json__Path) | ConvertTo-Json -Compress -AsArray
            """);
        string[] paths = JsonSerializer.Deserialize<string[]>(output.Trim())!;
        Assert.Equal(2, paths.Length);
        return (paths[0], paths[1]);
    }

    private string WorldLogPathFromLiveEnv()
        => RunLiveEnv($"Get-LiveDaemonLogPath -Role World -LiveHome '{Quote(_home)}'").Trim();

    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>Dot-sources LiveEnv.ps1 in a fresh pwsh (so environment changes stay in that process) and returns its stdout.</summary>
    private string RunLiveEnv(string body)
    {
        string script = Path.Combine(_home, "run-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(script, $"""
            $ErrorActionPreference = 'Stop'
            . '{Quote(Path.Combine(LiveDir, "LiveEnv.ps1"))}'
            {body}
            """, new UTF8Encoding(false));
        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-File", script })
        {
            start.ArgumentList.Add(a);
        }

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("pwsh did not finish within 60 s");
        }

        Assert.True(process.ExitCode == 0, $"pwsh exited {process.ExitCode}: {stdout.Result}{stderr.Result}");
        return stdout.Result;
    }

    /// <summary>Runs only where PowerShell 7 (<c>pwsh</c>) is on PATH; CI (ubuntu-latest) has it.</summary>
    private sealed class PwshFactAttribute : FactAttribute
    {
        private static readonly Lazy<bool> Available = new(Probe);

        public PwshFactAttribute()
        {
            if (!Available.Value)
            {
                Skip = "pwsh (PowerShell 7) is not on PATH on this host.";
            }
        }

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
