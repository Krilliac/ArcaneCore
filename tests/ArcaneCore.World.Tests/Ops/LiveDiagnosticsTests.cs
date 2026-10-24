using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// The live launch (tools/ops/live) captures the most diagnostics that are safe for a live server: launch-daemon.ps1 -Diagnostics Max
/// (the default) sets crash-dump, hang-stack and repo option variables per role, prints what is active, and a hidden sidecar keeps the
/// capture directories under their caps and collects counters (docs/ops/live-diagnostics.md). Nothing here starts a daemon: the plan,
/// retention and counters-argument functions run in a fresh pwsh against a temporary live home and a fake dotnet tools directory, and
/// the sidecar runs against a short-lived pwsh standing in for the daemon.
/// </summary>
public sealed class LiveDiagnosticsTests : IDisposable
{
    private static readonly string LiveDir = Path.Combine(RepositorySource.RequireRoot(), "tools", "ops", "live");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcane-livediag-" + Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly string _dotnet;
    private readonly string _userHome;

    public LiveDiagnosticsTests()
    {
        _home = Path.Combine(_root, "home");
        _dotnet = Path.Combine(_root, "dotnet");
        _userHome = Path.Combine(_root, "user");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_userHome);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void InstallTools(params string[] names)
    {
        string tools = Path.Combine(_dotnet, "tools");
        Directory.CreateDirectory(tools);
        foreach (string name in names)
        {
            File.WriteAllText(Path.Combine(tools, name + ".exe"), "x");
        }
    }

    /// <summary>The launcher defaults to Max, redeploy passes its level through, and the plan is applied before the daemon starts.</summary>
    [Fact]
    public void LaunchDaemon_DefaultsToMax_AppliesPlanBeforeStart_AndRedeployPassesItThrough()
    {
        string launch = File.ReadAllText(Path.Combine(LiveDir, "launch-daemon.ps1"));
        Assert.Contains("[string]$Diagnostics = 'Max'", launch, StringComparison.Ordinal);
        int plan = launch.IndexOf("Get-LiveDiagnosticsPlan", StringComparison.Ordinal);
        int apply = launch.IndexOf("Set-LiveDiagnosticsEnvironment", StringComparison.Ordinal);
        int start = launch.IndexOf("& $exe", StringComparison.Ordinal);
        Assert.True(plan >= 0 && apply > plan && start > apply, "launch-daemon.ps1 must plan, apply, then start the daemon");

        string redeploy = File.ReadAllText(Path.Combine(LiveDir, "redeploy.ps1"));
        Assert.Contains("[string]$Diagnostics = 'Max'", redeploy, StringComparison.Ordinal);
        Assert.Contains("'-Diagnostics', $Diagnostics", redeploy, StringComparison.Ordinal);
    }

    /// <summary>Max for the World: dump variables, hang-stack options with the resolved dotnet-stack, dumps command and the repo options.</summary>
    [PwshFact]
    public void MaxWorld_SetsDumpHangStackAndRepoOptions()
    {
        InstallTools("dotnet-stack", "dotnet-counters");
        Plan plan = GetPlan("World", "Max", extra: "-HangStackIntervalSeconds 45 -DumpFreeBytes 1TB");

        string stack = Path.Combine(_dotnet, "tools", "dotnet-stack.exe");
        Assert.Equal("1", plan.Value("DOTNET_DbgEnableMiniDump"));
        Assert.Equal("4", plan.Value("DOTNET_DbgMiniDumpType"));
        Assert.Equal(Path.Combine(_home, "dumps", "world-%p-%t.dmp"), plan.Value("DOTNET_DbgMiniDumpName"));
        Assert.Equal("1", plan.Value("DOTNET_CreateDumpDiagnostics"));
        Assert.Equal("1", plan.Value("DOTNET_CreateDumpVerboseDiagnostics"));
        Assert.Equal(Path.Combine(_home, "dumps", "world-createdump.log"), plan.Value("DOTNET_CreateDumpLogToFile"));
        Assert.Equal(stack, plan.Value("Ops__Watchdog__TickMonitor__StackCaptureTool"));
        Assert.Equal(Path.Combine(_home, "logs", "hangstacks"), plan.Value("Ops__Watchdog__TickMonitor__StackCaptureDirectory"));
        Assert.Equal("45", plan.Value("Ops__Watchdog__TickMonitor__StackCaptureIntervalSeconds"));
        Assert.Equal("true", plan.Value("Ops__Diagnostics__Dumps__Enabled"));
        Assert.Equal(Path.Combine(_home, "dumps"), plan.Value("Ops__Diagnostics__Dumps__Directory"));
        Assert.Equal("50", plan.Value("Diagnostics__FaultLogLimit"));
        Assert.Equal("500", plan.Value("Database__SlowTransactionWarnMs"));
        Assert.Equal("500", plan.Value("Database__SlowCommandWarnMs"));
        Assert.Equal("60000", plan.Value("Database__Sqlite__SingleWriter__StatsIntervalMs"));
        Assert.Empty(plan.Warnings);
        Assert.Contains(plan.Active, l => l.StartsWith("hang stacks: ", StringComparison.Ordinal));
        Assert.Contains(plan.Active, l => l.StartsWith("counters: ", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Settings, s => s.Name == "Diagnostics__FirstChanceExceptions");
    }

    /// <summary>The Realm has no tick and no .debug dump command: only the dump variables and the shared repo options apply.</summary>
    [PwshFact]
    public void MaxRealm_SetsOnlyTheSharedSettings()
    {
        InstallTools("dotnet-stack", "dotnet-counters");
        Plan plan = GetPlan("Realm", "Max");

        Assert.Equal("1", plan.Value("DOTNET_DbgEnableMiniDump"));
        Assert.Equal(Path.Combine(_home, "dumps", "realm-%p-%t.dmp"), plan.Value("DOTNET_DbgMiniDumpName"));
        Assert.Equal("50", plan.Value("Diagnostics__FaultLogLimit"));
        Assert.DoesNotContain(plan.Settings, s => s.Name.StartsWith("Ops__Watchdog__TickMonitor", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Settings, s => s.Name.StartsWith("Ops__Diagnostics", StringComparison.Ordinal));
    }

    /// <summary>Standard adds nothing and says so.</summary>
    [PwshFact]
    public void Standard_AddsNothing()
    {
        InstallTools("dotnet-stack", "dotnet-counters");
        Plan plan = GetPlan("World", "Standard");

        Assert.Empty(plan.Settings);
        Assert.Single(plan.Active);
        Assert.Contains("Standard", plan.Active[0], StringComparison.Ordinal);
    }

    /// <summary>No dotnet-stack: a warning, no stack settings, and the rest of Max still applies. No dotnet-counters: a warning, counters off.</summary>
    [PwshFact]
    public void MissingTools_WarnAndSkipOnlyTheirOwnCapture()
    {
        Plan plan = GetPlan("World", "Max");

        Assert.DoesNotContain(plan.Settings, s => s.Name.StartsWith("Ops__Watchdog__TickMonitor", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.Contains("dotnet-stack not found", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.Contains("dotnet-counters not found", StringComparison.Ordinal));
        Assert.Contains(plan.Active, l => l.Contains("hang stacks: off", StringComparison.Ordinal));
        Assert.Contains(plan.Active, l => l.Contains("counters: off", StringComparison.Ordinal));
        Assert.Equal("1", plan.Value("DOTNET_DbgEnableMiniDump"));
    }

    /// <summary>The tool is also found in the user profile's global tools directory when DOTNET_ROOT has none.</summary>
    [PwshFact]
    public void Tool_IsFoundInTheUserProfileToolsDirectory()
    {
        string userTools = Path.Combine(_userHome, ".dotnet", "tools");
        Directory.CreateDirectory(userTools);
        File.WriteAllText(Path.Combine(userTools, "dotnet-stack.exe"), "x");
        Plan plan = GetPlan("World", "Max");

        Assert.Equal(Path.Combine(userTools, "dotnet-stack.exe"), plan.Value("Ops__Watchdog__TickMonitor__StackCaptureTool"));
    }

    /// <summary>A variable the operator already set is reported as kept and is not replaced when the plan is applied.</summary>
    [PwshFact]
    public void AlreadySetVariable_IsKept()
    {
        InstallTools("dotnet-stack", "dotnet-counters");
        string output = RunPwsh($$"""
            $env:DOTNET_DbgMiniDumpType = '2'
            $plan = Get-LiveDiagnosticsPlan -Role World -LiveHome '{{Quote(_home)}}' -DotnetRoot '{{Quote(_dotnet)}}' -UserHome '{{Quote(_userHome)}}'
            $set = Set-LiveDiagnosticsEnvironment -Plan $plan
            [pscustomobject]@{
                Type = $env:DOTNET_DbgMiniDumpType; Kept = @($plan.Settings | Where-Object Kept | ForEach-Object Name)
                SetType = ($set -contains 'DOTNET_DbgMiniDumpType'); SetEnable = ($set -contains 'DOTNET_DbgEnableMiniDump')
                Enable = $env:DOTNET_DbgEnableMiniDump; Dirs = (Test-Path '{{Quote(Path.Combine(_home, "dumps"))}}')
            } | ConvertTo-Json -Compress
            """);
        using JsonDocument json = JsonDocument.Parse(output.Trim());
        JsonElement r = json.RootElement;
        Assert.Equal("2", r.GetProperty("Type").GetString());
        Assert.Equal("DOTNET_DbgMiniDumpType", Assert.Single(r.GetProperty("Kept").EnumerateArray().Select(e => e.GetString())));
        Assert.False(r.GetProperty("SetType").GetBoolean());
        Assert.True(r.GetProperty("SetEnable").GetBoolean());
        Assert.Equal("1", r.GetProperty("Enable").GetString());
        Assert.True(r.GetProperty("Dirs").GetBoolean());
    }

    /// <summary>A Full dump needs free space on the dumps volume; with less it is a Heap dump and the plan says why.</summary>
    [PwshFact]
    public void FullDump_FallsBackToHeap_WhenDiskIsLow()
    {
        Plan low = GetPlan("World", "Max", extra: "-DumpFreeBytes 1GB");
        Assert.Equal("2", low.Value("DOTNET_DbgMiniDumpType"));
        Assert.Contains(low.Warnings, w => w.Contains("Heap dump, not Full", StringComparison.Ordinal));

        Plan heapRequested = GetPlan("World", "Max", extra: "-DumpType Heap -DumpFreeBytes 1TB");
        Assert.Equal("2", heapRequested.Value("DOTNET_DbgMiniDumpType"));
        Assert.DoesNotContain(heapRequested.Warnings, w => w.Contains("Heap dump, not Full", StringComparison.Ordinal));
    }

    /// <summary>Retention deletes the oldest files beyond the count and byte caps, always keeps the newest, and ignores other file names.</summary>
    [PwshFact]
    public void Retention_DeletesOldestBeyondCountAndByteCaps()
    {
        string dir = Path.Combine(_home, "dumps");
        Directory.CreateDirectory(dir);
        DateTime t0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 1; i <= 5; i++)
        {
            string path = Path.Combine(dir, $"world-{i}.dmp");
            File.WriteAllBytes(path, new byte[1000]);
            File.SetLastWriteTimeUtc(path, t0.AddHours(i));
        }

        File.WriteAllText(Path.Combine(dir, "world-createdump.log"), "keep");

        string byCount = RunPwsh($"Invoke-DiagnosticsRetention -Directory '{Quote(dir)}' -Filter '*.dmp' -MaxFiles 3 | Sort-Object | ConvertTo-Json -Compress -AsArray");
        Assert.Equal(["world-1.dmp", "world-2.dmp"], JsonSerializer.Deserialize<string[]>(byCount.Trim())!);

        string byBytes = RunPwsh($"Invoke-DiagnosticsRetention -Directory '{Quote(dir)}' -Filter '*.dmp' -MaxBytes 1500 | Sort-Object | ConvertTo-Json -Compress -AsArray");
        Assert.Equal(["world-3.dmp", "world-4.dmp"], JsonSerializer.Deserialize<string[]>(byBytes.Trim())!);

        Assert.True(File.Exists(Path.Combine(dir, "world-5.dmp")));
        Assert.True(File.Exists(Path.Combine(dir, "world-createdump.log")));

        // A cap smaller than the newest file still keeps that file.
        string tiny = RunPwsh($"ConvertTo-Json -InputObject @(Invoke-DiagnosticsRetention -Directory '{Quote(dir)}' -Filter '*.dmp' -MaxBytes 10) -Compress");
        Assert.Equal("[]", tiny.Trim());
        Assert.True(File.Exists(Path.Combine(dir, "world-5.dmp")));
    }

    /// <summary>The retention rules cover the dumps, hang stacks and counters directories with their own filters and caps.</summary>
    [PwshFact]
    public void RetentionRules_CoverAllThreeDirectories_WithConfigurableCaps()
    {
        string output = RunPwsh($"Get-DiagnosticsRetentionRules -LiveHome '{Quote(_home)}' -MaxDumpFiles 2 -MaxDumpGB 5 -MaxHangStackMB 10 -MaxCountersFiles 7 | ConvertTo-Json -Compress -AsArray");
        using JsonDocument json = JsonDocument.Parse(output.Trim());
        var rules = json.RootElement.EnumerateArray().ToDictionary(e => e.GetProperty("Name").GetString()!);
        Assert.Equal(Path.Combine(_home, "dumps"), rules["dumps"].GetProperty("Directory").GetString());
        Assert.Equal(2, rules["dumps"].GetProperty("MaxFiles").GetInt32());
        Assert.Equal(5L * 1024 * 1024 * 1024, rules["dumps"].GetProperty("MaxBytes").GetInt64());
        Assert.Equal(Path.Combine(_home, "logs", "hangstacks"), rules["hangstacks"].GetProperty("Directory").GetString());
        Assert.Equal(10L * 1024 * 1024, rules["hangstacks"].GetProperty("MaxBytes").GetInt64());
        Assert.Equal(Path.Combine(_home, "logs", "counters"), rules["counters"].GetProperty("Directory").GetString());
        Assert.Equal(7, rules["counters"].GetProperty("MaxFiles").GetInt32());
    }

    /// <summary>One counters segment: System.Runtime and the repo's meters, CSV, a refresh interval, a bounded duration and a bounded series count.</summary>
    [PwshFact]
    public void CountersArguments_CollectSystemRuntimeAndRepoMeters_ForOneBoundedSegment()
    {
        string output = RunPwsh($"Get-CountersCollectArguments -ProcessId 4242 -OutputPath '{Quote(Path.Combine(_home, "c.csv"))}' -RefreshSeconds 10 -SegmentMinutes 90 | ConvertTo-Json -Compress -AsArray");
        string[] args = JsonSerializer.Deserialize<string[]>(output.Trim())!;
        Assert.Equal("collect", args[0]);
        Assert.Equal("4242", args[Array.IndexOf(args, "-p") + 1]);
        Assert.Equal("csv", args[Array.IndexOf(args, "--format") + 1]);
        Assert.Equal("10", args[Array.IndexOf(args, "--refresh-interval") + 1]);
        Assert.Equal("00:01:30:00", args[Array.IndexOf(args, "--duration") + 1]);
        string counters = args[Array.IndexOf(args, "--counters") + 1];
        Assert.Contains("System.Runtime", counters, StringComparison.Ordinal);
        Assert.Contains("ArcaneCore.World", counters, StringComparison.Ordinal);
        Assert.Contains("ArcaneCore.Runtime", counters, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sidecar trims the capture directories while its daemon runs and exits when the daemon does (here a short-lived pwsh stands in
    /// for it); it never needs the counters tool to do so.
    /// </summary>
    [PwshFact]
    public void Sidecar_TrimsCaptureDirectories_AndEndsWithTheDaemon()
    {
        string hang = Path.Combine(_home, "logs", "hangstacks");
        Directory.CreateDirectory(hang);
        DateTime t0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 1; i <= 4; i++)
        {
            string path = Path.Combine(hang, $"hang-stacks-{i}.txt");
            File.WriteAllText(path, "stack");
            File.SetLastWriteTimeUtc(path, t0.AddMinutes(i));
        }

        using Process daemon = StartPwsh("-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 4");
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in new[]
        {
            "-NoProfile", "-NonInteractive", "-File", Path.Combine(LiveDir, "diagnostics-sidecar.ps1"), "-Role", "World", "-LiveHome", _home,
            "-DaemonPid", daemon.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), "-PollSeconds", "1", "-MaxHangStackFiles", "2",
        })
        {
            start.ArgumentList.Add(a);
        }

        using Process sidecar = Process.Start(start)!;
        Task<string> stdout = sidecar.StandardOutput.ReadToEndAsync();
        Task<string> stderr = sidecar.StandardError.ReadToEndAsync();
        bool ended = sidecar.WaitForExit(90_000);
        if (!ended)
        {
            sidecar.Kill(entireProcessTree: true);
        }

        Assert.True(ended, "the sidecar outlived its daemon");
        Assert.True(sidecar.ExitCode == 0, $"sidecar exited {sidecar.ExitCode}: {stdout.Result}{stderr.Result}");
        Assert.Equal(["hang-stacks-3.txt", "hang-stacks-4.txt"], Directory.GetFiles(hang).Select(path => Path.GetFileName(path)!).Order().ToArray());
        string log = File.ReadAllText(Path.Combine(_home, "logs", "counters", "world-sidecar.log"));
        Assert.Contains("retention removed hangstacks/hang-stacks-1.txt", log, StringComparison.Ordinal);
        Assert.Contains("ended; sidecar exits", log, StringComparison.Ordinal);
    }

    private static Process StartPwsh(params string[] arguments)
    {
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in arguments)
        {
            start.ArgumentList.Add(a);
        }

        return Process.Start(start)!;
    }

    private Plan GetPlan(string role, string level, string extra = "")
    {
        string output = RunPwsh($$"""
            $plan = Get-LiveDiagnosticsPlan -Role {{role}} -LiveHome '{{Quote(_home)}}' -Level {{level}} -DotnetRoot '{{Quote(_dotnet)}}' -UserHome '{{Quote(_userHome)}}' -Environment @{} {{extra}}
            [pscustomobject]@{
                Settings = @($plan.Settings | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Value = $_.Value; Kept = $_.Kept } })
                Active = @($plan.Active); Warnings = @($plan.Warnings)
            } | ConvertTo-Json -Compress -Depth 5
            """);
        return JsonSerializer.Deserialize<Plan>(output.Trim(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed record Setting(string Name, string Value, bool Kept);

    private sealed record Plan(Setting[] Settings, string[] Active, string[] Warnings)
    {
        public string Value(string name) => Settings.Single(s => s.Name == name).Value;
    }

    /// <summary>Dot-sources LiveDiagnostics.ps1 in a fresh pwsh (so environment changes stay in that process) and returns its stdout.</summary>
    private string RunPwsh(string body)
    {
        string script = Path.Combine(_root, "run-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(script, $"""
            $ErrorActionPreference = 'Stop'
            . '{Quote(Path.Combine(LiveDir, "LiveDiagnostics.ps1"))}'
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
