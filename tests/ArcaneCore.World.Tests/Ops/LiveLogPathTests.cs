using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// The live tooling (tools/ops/live) gives each daemon its own log file (one file per daemon, in the configured format), and reads the World's log where the World writes it.
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
    /// still owns world.debug.&lt;ext&gt;, and nothing falls back to a per-process name. Each daemon writes ONE file, so only File:Path is
    /// set (an explicit Format leaves the deprecated Json:Path alias unset) and the extension follows the format (Json: .jsonl, Text: .log).
    /// </summary>
    [PwshTheory]
    [InlineData("Json", "jsonl")]
    [InlineData("Text", "log")]
    public void RealmStartedFirst_WorldKeepsWorldDebugLog(string format, string extension)
    {
        Dictionary<string, string> env = new() { ["Logging__ArcaneCore__Format"] = format };
        (string realmFile, string realmJson) = LaunchEnvironment("Realm", env);
        (string worldFile, string worldJson) = LaunchEnvironment("World", env);
        string logs = Path.Combine(_home, "logs");

        // An explicit Format means one key: File:Path. The deprecated Json:Path alias is left unset.
        Assert.Equal(string.Empty, realmJson);
        Assert.Equal(string.Empty, worldJson);

        var diagnostics = new StringWriter();
        var policy = new RollingFilePolicy(0, false, 0);
        using RollingFileWriter realmWriter = new(realmFile, policy, diagnostics: diagnostics);
        realmWriter.Write("realm\n");
        using RollingFileWriter worldWriter = new(worldFile, policy, diagnostics: diagnostics);
        worldWriter.Write("world\n");

        Assert.Equal(Path.Combine(logs, "world.debug." + extension), worldWriter.ActivePath);
        Assert.Equal(Path.Combine(logs, "realm.debug." + extension), realmWriter.ActivePath);
        Assert.Equal(string.Empty, diagnostics.ToString());
        Assert.Equal(worldFile, WorldLogPathFromLiveEnv(env));
    }

    /// <summary>
    /// Get-LiveLogFormat follows the daemons: environment Format, else the profile's Format, else the legacy switches (File only: Text;
    /// Json only or both: Json), else Json.
    /// </summary>
    [PwshTheory]
    [InlineData("Text", null, "false", "true", "Text")]
    [InlineData("json", null, "true", "false", "Json")]
    [InlineData("TEXT", "Json", "false", "true", "Text")]
    [InlineData(null, "Text", null, null, "Text")]
    [InlineData(null, "Json", "true", "false", "Json")]
    [InlineData(null, null, "true", "false", "Text")]
    [InlineData(null, null, "false", "true", "Json")]
    [InlineData(null, null, "true", "true", "Json")]
    [InlineData(null, null, "false", "false", "Json")]
    [InlineData(null, null, null, null, "Json")]
    public void LiveLogFormat_FollowsTheDaemonResolution(string? envFormat, string? profileFormat, string? profileFile, string? profileJson, string expected)
    {
        string profile = WriteProfile(profileFormat, profileFile, profileJson);
        Dictionary<string, string> env = envFormat is null ? [] : new() { ["Logging__ArcaneCore__Format"] = envFormat };
        Assert.Equal(expected, RunLiveEnv($"Get-LiveLogFormat -LiveProfile '{Quote(profile)}'", env).Trim());
    }

    /// <summary>The environment's Enabled flags beat the profile's; an unrecognised Format is ignored; no readable profile means Json.</summary>
    [PwshFact]
    public void LiveLogFormat_EnvironmentFlagsBeatTheProfile_AndBadInputFallsBack()
    {
        string textProfile = WriteProfile(null, "true", "false");
        Assert.Equal("Json", RunLiveEnv($"Get-LiveLogFormat -LiveProfile '{Quote(textProfile)}'", new() { ["Logging__ArcaneCore__Json__Enabled"] = "true" }).Trim());
        Assert.Equal("Json", RunLiveEnv($"Get-LiveLogFormat -LiveProfile '{Quote(textProfile)}'", new() { ["Logging__ArcaneCore__File__Enabled"] = "false", ["Logging__ArcaneCore__Json__Enabled"] = "true" }).Trim());
        Assert.Equal("Text", RunLiveEnv("Get-LiveLogFormat", new() { ["Logging__ArcaneCore__File__Enabled"] = "true" }).Trim());

        Assert.Equal("Text", RunLiveEnv($"Get-LiveLogFormat -LiveProfile '{Quote(textProfile)}'", new() { ["Logging__ArcaneCore__Format"] = "Xml" }).Trim());
        string badFormat = WriteProfile("Xml", "true", "false");
        Assert.Equal("Text", RunLiveEnv($"Get-LiveLogFormat -LiveProfile '{Quote(badFormat)}'", []).Trim());

        Assert.Equal("Json", RunLiveEnv($"Get-LiveLogFormat -LiveProfile '{Quote(Path.Combine(_home, "missing"))}'", []).Trim());
        string broken = Path.Combine(_home, "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "appsettings.json"), "{ not json");
        Assert.Equal("Json", RunLiveEnv($"Get-LiveLogFormat -LiveProfile '{Quote(broken)}'", []).Trim());

        Assert.Equal("Text", RunLiveEnv("Get-LiveLogFormat", new() { ["ARCANECORE_LIVE_PROFILE"] = textProfile }).Trim());
    }

    /// <summary>Get-LiveDaemonLogPath: -Kind Auto follows the format (profile included), Text and Json force an extension.</summary>
    [PwshFact]
    public void LiveDaemonLogPath_AutoFollowsTheFormat_AndKindForcesOne()
    {
        string logs = Path.Combine(_home, "logs");
        string textProfile = Quote(WriteProfile("Text", null, null));
        string home = Quote(_home);
        Assert.Equal(Path.Combine(logs, "world.debug.log"), RunLiveEnv($"Get-LiveDaemonLogPath -Role World -LiveHome '{home}' -LiveProfile '{textProfile}'", []).Trim());
        Assert.Equal(Path.Combine(logs, "realm.debug.jsonl"), RunLiveEnv($"Get-LiveDaemonLogPath -Role Realm -LiveHome '{home}' -LiveProfile '{textProfile}' -Kind Json", []).Trim());
        Assert.Equal(Path.Combine(logs, "realm.debug.log"), RunLiveEnv($"Get-LiveDaemonLogPath -Role Realm -LiveHome '{home}' -Kind Text", []).Trim());
        Assert.Equal(Path.Combine(logs, "world.debug.jsonl"), RunLiveEnv($"Get-LiveDaemonLogPath -Role World -LiveHome '{home}'", []).Trim());
    }

    private string[] SetEnvironmentValues(string role, string profile, Dictionary<string, string> env)
    {
        string output = RunLiveEnv($"""
            Set-LiveDaemonLogEnvironment -Role {role} -LiveHome '{Quote(_home)}' -LiveProfile '{Quote(profile)}'
            @([string]$env:Logging__ArcaneCore__File__Path, [string]$env:Logging__ArcaneCore__Json__Path, [string]$env:Logging__ArcaneCore__Format, [string]$env:Logging__ArcaneCore__File__Enabled, [string]$env:Logging__ArcaneCore__Json__Enabled) | ConvertTo-Json -Compress -AsArray
            """, env);
        return JsonSerializer.Deserialize<string[]>(output.Trim())!;
    }

    /// <summary>
    /// Set-LiveDaemonLogEnvironment takes -LiveProfile and sets only the path: with the profile's explicit Format the single File:Path
    /// (extension follows the format), no deprecated Json:Path, and neither Format nor Enabled.
    /// </summary>
    [PwshTheory]
    [InlineData("Json", "jsonl")]
    [InlineData("Text", "log")]
    public void SetLiveDaemonLogEnvironment_UsesTheProfileFormat_AndSetsOnlyTheFilePath(string format, string extension)
    {
        string profile = WriteProfile(format, null, null);
        // A stale Json:Path inherited from the caller's environment must be cleared, or the two keys could disagree.
        string[] values = SetEnvironmentValues("World", profile, new() { ["Logging__ArcaneCore__Json__Path"] = Path.Combine(_home, "stale.jsonl") });

        Assert.Equal([Path.Combine(_home, "logs", "world.debug." + extension), string.Empty, string.Empty, string.Empty, string.Empty], values);
    }

    /// <summary>A legacy profile (no Format; File and Json both on) still gets both keys, each with its own extension.</summary>
    [PwshFact]
    public void SetLiveDaemonLogEnvironment_LegacyProfile_SetsFilePathAndJsonPath()
    {
        string profile = WriteProfile(null, "true", "true");
        string[] values = SetEnvironmentValues("Realm", profile, []);

        string logs = Path.Combine(_home, "logs");
        Assert.Equal([Path.Combine(logs, "realm.debug.log"), Path.Combine(logs, "realm.debug.jsonl"), string.Empty, string.Empty, string.Empty], values);
    }

    /// <summary>
    /// A legacy profile with only File on gets no Json:Path: a Format set in a layer the launcher cannot see (appsettings.{Environment}.json,
    /// .conf) must not be handed a deprecated key to warn about, so a stale inherited Json__Path is removed too.
    /// </summary>
    [PwshFact]
    public void SetLiveDaemonLogEnvironment_LegacyProfile_JsonOff_SetsOnlyTheFilePath()
    {
        string profile = WriteProfile(null, "true", "false");
        string[] values = SetEnvironmentValues("Realm", profile, new() { ["Logging__ArcaneCore__Json__Path"] = "stale.jsonl" });

        Assert.Equal([Path.Combine(_home, "logs", "realm.debug.log"), string.Empty, string.Empty, string.Empty, string.Empty], values);
    }

    /// <summary>Get-LiveLogFormatSetting answers only for an explicit Format (environment first, then the profile) and is $null otherwise.</summary>
    [PwshTheory]
    [InlineData("Json", "Text", "Json")]
    [InlineData("text", "Json", "Text")]
    [InlineData(null, "Text", "Text")]
    [InlineData(null, "json", "Json")]
    [InlineData(null, null, "<null>")]
    public void LiveLogFormatSetting_IsOnlyAnExplicitFormat(string? envFormat, string? profileFormat, string expected)
    {
        string profile = WriteProfile(profileFormat, "true", "false");
        Dictionary<string, string> env = envFormat is null ? [] : new() { ["Logging__ArcaneCore__Format"] = envFormat };
        string body = $"$f = Get-LiveLogFormatSetting -LiveProfile '{Quote(profile)}'; if ($null -eq $f) {{ '<null>' }} else {{ $f }}";

        Assert.Equal(expected, RunLiveEnv(body, env).Trim());
    }

    [PwshFact]
    public void LiveLogFormatSetting_IsNull_WithoutAReadableProfileOrFormat()
    {
        const string body = "$f = Get-LiveLogFormatSetting -LiveProfile '{0}'; if ($null -eq $f) {{ '<null>' }} else {{ $f }}";
        Assert.Equal("<null>", RunLiveEnv(string.Format(System.Globalization.CultureInfo.InvariantCulture, body, Quote(Path.Combine(_home, "missing"))), []).Trim());
        Assert.Equal("<null>", RunLiveEnv("$f = Get-LiveLogFormatSetting; if ($null -eq $f) { '<null>' } else { $f }", new() { ["Logging__ArcaneCore__Format"] = "Xml" }).Trim());
    }

    /// <summary>
    /// livecheck.ps1 folds a JSON log into the text-level vocabulary (TRCE DBUG INFO WARN FAIL CRIT, as LogLineFormatter writes it) so its
    /// ERROR section lists Error and Critical records; a record whose ts does not parse must not abort the run.
    /// </summary>
    [PwshFact]
    public void Livecheck_ReadsAJsonLog_ListsErrorAndCritical_AndSurvivesAnUnparsableTimestamp()
    {
        string profile = WriteProfile("Json", "true", null);
        string logs = Path.Combine(_home, "logs");
        Directory.CreateDirectory(logs);
        string Stamp(int secondsAgo) => DateTime.UtcNow.AddSeconds(-secondsAgo).ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        string Line(string ts, string level, string message) => JsonSerializer.Serialize(new Dictionary<string, string> { ["ts"] = ts, ["level"] = level, ["message"] = message });
        File.WriteAllLines(Path.Combine(logs, "world.debug.jsonl"),
        [
            Line(Stamp(50), "Information", "routine startup note"),
            Line("not-a-timestamp", "Information", "line with a broken stamp"),
            Line(Stamp(40), "Warning", "careful now"),
            Line(Stamp(30), "Error", "disk exploded"),
            Line(Stamp(20), "Critical", "world melted"),
        ]);

        string output = RunPwsh([Path.Combine(LiveDir, "livecheck.ps1"), "-LiveHome", _home, "-LiveProfile", profile], []);

        string[] lines = output.Split('\n', StringSplitOptions.TrimEntries);
        int start = Array.FindIndex(lines, l => l.StartsWith("--- ERROR", StringComparison.Ordinal));
        Assert.True(start >= 0, "livecheck output has no ERROR section:\n" + output);
        int end = Array.FindIndex(lines, start + 1, l => l.StartsWith("--- ", StringComparison.Ordinal));
        string[] section = [.. lines.Skip(start + 1).Take((end < 0 ? lines.Length : end) - start - 1)];

        Assert.Contains(section, l => l.Contains("disk exploded", StringComparison.Ordinal));
        Assert.Contains(section, l => l.Contains("world melted", StringComparison.Ordinal));
        Assert.DoesNotContain(section, l => l.Contains("routine startup note", StringComparison.Ordinal) || l.Contains("careful now", StringComparison.Ordinal));
        Assert.Contains("careful now", output, StringComparison.Ordinal); // the warning landed in the WARN section
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
            """, []);
        string[] names = JsonSerializer.Deserialize<string[]>(output.Trim())!;
        Assert.Equal(["world.debug.ArcaneCore.Realm-20261016-001.log", "world.debug.ArcaneCore.Realm.log", "world.debug.ArcaneCore.World.4242.jsonl"], names);
    }

    private static void Touch(string path, DateTime utc)
    {
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, utc);
    }

    /// <summary>The File:Path and Json:Path a daemon of <paramref name="role"/> resolves after launch-daemon.ps1's environment.</summary>
    private (string File, string Json) LaunchEnvironment(string role, Dictionary<string, string> env)
    {
        string output = RunLiveEnv($"""
            Set-LiveDaemonLogEnvironment -Role {role} -LiveHome '{Quote(_home)}'
            @([string]$env:Logging__ArcaneCore__File__Path, [string]$env:Logging__ArcaneCore__Json__Path) | ConvertTo-Json -Compress -AsArray
            """, env);
        string[] paths = JsonSerializer.Deserialize<string[]>(output.Trim())!;
        Assert.Equal(2, paths.Length);
        return (paths[0], paths[1]);
    }

    private string WorldLogPathFromLiveEnv(Dictionary<string, string> env)
        => RunLiveEnv($"Get-LiveDaemonLogPath -Role World -LiveHome '{Quote(_home)}'", env).Trim();

    /// <summary>Writes a profile directory whose appsettings.json holds the given Logging:ArcaneCore keys (null = key absent) and returns it.</summary>
    private string WriteProfile(string? format, string? fileEnabled, string? jsonEnabled)
    {
        string dir = Path.Combine(_home, "profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var arcane = new Dictionary<string, object>();
        if (format is not null)
        {
            arcane["Format"] = format;
        }

        if (fileEnabled is not null)
        {
            arcane["File"] = new Dictionary<string, object> { ["Enabled"] = bool.Parse(fileEnabled) };
        }

        if (jsonEnabled is not null)
        {
            arcane["Json"] = new Dictionary<string, object> { ["Enabled"] = bool.Parse(jsonEnabled) };
        }

        File.WriteAllText(Path.Combine(dir, "appsettings.json"), JsonSerializer.Serialize(new { Logging = new { ArcaneCore = arcane } }));
        return dir;
    }

    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>Dot-sources LiveEnv.ps1 in a fresh pwsh (so environment changes stay in that process) and returns its stdout.</summary>
    private string RunLiveEnv(string body, Dictionary<string, string> env)
    {
        string script = Path.Combine(_home, "run-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(script, $"""
            $ErrorActionPreference = 'Stop'
            . '{Quote(Path.Combine(LiveDir, "LiveEnv.ps1"))}'
            {body}
            """, new UTF8Encoding(false));
        return RunPwsh([script], env);
    }

    /// <summary>Runs <c>pwsh -File</c> with <paramref name="fileArgs"/> in a child whose Logging__ArcaneCore__* and ARCANECORE_LIVE_* variables are scrubbed first (the test host's own must not leak in), then <paramref name="env"/> applied.</summary>
    private static string RunPwsh(string[] fileArgs, Dictionary<string, string> env)
    {
        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("Logging__ArcaneCore__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ARCANECORE_LIVE_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(key);
        }

        foreach ((string key, string value) in env)
        {
            start.Environment[key] = value;
        }

        foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-File" }.Concat(fileArgs))
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

    /// <summary>Theory form of <see cref="PwshFactAttribute"/>.</summary>
    private sealed class PwshTheoryAttribute : TheoryAttribute
    {
        public PwshTheoryAttribute()
        {
            if (!PwshFactAttribute.IsAvailable)
            {
                Skip = "pwsh (PowerShell 7) is not on PATH on this host.";
            }
        }
    }

    /// <summary>Runs only where PowerShell 7 (<c>pwsh</c>) is on PATH; CI (ubuntu-latest) has it.</summary>
    private sealed class PwshFactAttribute : FactAttribute
    {
        private static readonly Lazy<bool> Available = new(Probe);

        internal static bool IsAvailable => Available.Value;

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
