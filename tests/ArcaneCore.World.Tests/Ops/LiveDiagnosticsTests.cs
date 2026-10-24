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

    /// <summary>
    /// The sidecar is started without the daemon's createdump variables (so it, its collectors and its other .NET tools cannot write dumps
    /// named like the daemon's), and the launcher's own environment gets them back.
    /// </summary>
    [PwshFact]
    public void SidecarStart_HasNoDumpVariables_AndTheyAreRestoredAfterwards()
    {
        string launch = File.ReadAllText(Path.Combine(LiveDir, "launch-daemon.ps1"));
        int wrapped = launch.IndexOf("Invoke-WithoutDumpEnvironment { Start-Process pwsh", StringComparison.Ordinal);
        Assert.True(wrapped > launch.IndexOf("Set-LiveDiagnosticsEnvironment", StringComparison.Ordinal), "the sidecar must be started without the dump variables");

        string output = RunPwsh($$"""
            $env:DOTNET_DbgEnableMiniDump = '1'; $env:DOTNET_DbgMiniDumpType = '4'; $env:DOTNET_DbgMiniDumpName = 'x.dmp'
            $env:DOTNET_CreateDumpDiagnostics = '1'; $env:DOTNET_CreateDumpLogToFile = 'x.log'; $env:DOTNET_gcServer = '0'
            $probe = '$e = Get-ChildItem Env: | Where-Object Name -match ''^DOTNET_(Dbg|CreateDump)''; ($e | ForEach-Object Name) -join '',''; [Environment]::GetEnvironmentVariable(''DOTNET_gcServer'')'
            $inside = Invoke-WithoutDumpEnvironment { pwsh -NoProfile -Command $probe }
            [pscustomobject]@{
                Inside = @($inside)
                After = @('DOTNET_DbgEnableMiniDump', 'DOTNET_DbgMiniDumpType', 'DOTNET_DbgMiniDumpName', 'DOTNET_CreateDumpDiagnostics', 'DOTNET_CreateDumpLogToFile' | ForEach-Object { [Environment]::GetEnvironmentVariable($_) })
            } | ConvertTo-Json -Compress
            """);
        using JsonDocument json = JsonDocument.Parse(output.Trim());
        string[] inside = json.RootElement.GetProperty("Inside").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        Assert.Equal(["", "0"], inside); // no DOTNET_Dbg*/CreateDump* names; an unrelated DOTNET_ variable is untouched
        Assert.Equal(["1", "4", "x.dmp", "1", "x.log"], json.RootElement.GetProperty("After").EnumerateArray().Select(e => e.GetString() ?? "").ToArray());
    }

    /// <summary>A DOTNET_DbgEnableMiniDump kept from the environment as 0 is not reported as an active crash dump.</summary>
    [PwshFact]
    public void KeptDisabledCrashDump_IsReportedOff_AndCountersLineNamesTheMetricsCondition()
    {
        InstallTools("dotnet-stack", "dotnet-counters");
        string output = RunPwsh($$"""
            $plan = Get-LiveDiagnosticsPlan -Role World -LiveHome '{{Quote(_home)}}' -DotnetRoot '{{Quote(_dotnet)}}' -UserHome '{{Quote(_userHome)}}' -Environment @{ DOTNET_DbgEnableMiniDump = '0' }
            $plan.Active | ConvertTo-Json -Compress -AsArray
            """);
        string[] active = JsonSerializer.Deserialize<string[]>(output.Trim())!;
        Assert.Contains(active, l => l.StartsWith("crash dump: off", StringComparison.Ordinal));
        Assert.DoesNotContain(active, l => l.StartsWith("crash dump: Full", StringComparison.Ordinal));
        Assert.Contains(active, l => l.StartsWith("counters:", StringComparison.Ordinal) && l.Contains("Ops:Metrics:Enabled", StringComparison.Ordinal));
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

    /// <summary>The Realm has no tick monitor, so a missing dotnet-stack is not worth a warning there; a missing dotnet-counters still is.</summary>
    [PwshFact]
    public void Realm_WithoutTools_WarnsOnlyAboutCounters()
    {
        Plan plan = GetPlan("Realm", "Max");

        Assert.DoesNotContain(plan.Warnings, w => w.Contains("dotnet-stack", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.Contains("dotnet-counters not found", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Active, l => l.Contains("hang stacks", StringComparison.Ordinal));
    }

    /// <summary>Tool lookup order: DOTNET_ROOT first, then the user profile; an extensionless file counts; nothing found is null.</summary>
    [PwshFact]
    public void FindDotnetTool_PrefersDotnetRoot_AcceptsExtensionless_AndReturnsNullWhenAbsent()
    {
        string userTools = Path.Combine(_userHome, ".dotnet", "tools");
        Directory.CreateDirectory(userTools);
        File.WriteAllText(Path.Combine(userTools, "dotnet-stack.exe"), "x");
        InstallTools("dotnet-stack");
        File.WriteAllText(Path.Combine(_dotnet, "tools", "dotnet-counters"), "x");

        string output = RunPwsh($$"""
            [pscustomobject]@{
                Stack = Find-DotnetTool -Name dotnet-stack -DotnetRoot '{{Quote(_dotnet)}}' -UserHome '{{Quote(_userHome)}}'
                Counters = Find-DotnetTool -Name dotnet-counters -DotnetRoot '{{Quote(_dotnet)}}' -UserHome '{{Quote(_userHome)}}'
                Missing = Find-DotnetTool -Name dotnet-trace -DotnetRoot '{{Quote(_dotnet)}}' -UserHome '{{Quote(_userHome)}}'
                NoRoots = Find-DotnetTool -Name dotnet-stack -DotnetRoot '' -UserHome ''
            } | ConvertTo-Json -Compress
            """);
        using JsonDocument json = JsonDocument.Parse(output.Trim());
        JsonElement r = json.RootElement;
        Assert.Equal(Path.Combine(_dotnet, "tools", "dotnet-stack.exe"), r.GetProperty("Stack").GetString());
        Assert.Equal(Path.Combine(_dotnet, "tools", "dotnet-counters"), r.GetProperty("Counters").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("Missing").ValueKind);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("NoRoots").ValueKind);
    }

    /// <summary>Operator-set stack-capture variables win over the resolved tool and the default interval, and the plan lists them as kept.</summary>
    [PwshFact]
    public void PresetHangStackVariables_AreKept_AndListedInTheActiveLines()
    {
        InstallTools("dotnet-stack", "dotnet-counters");
        string output = RunPwsh($$"""
            $plan = Get-LiveDiagnosticsPlan -Role World -LiveHome '{{Quote(_home)}}' -DotnetRoot '{{Quote(_dotnet)}}' -UserHome '{{Quote(_userHome)}}' -Environment @{
                Ops__Watchdog__TickMonitor__StackCaptureTool = 'custom-tool'; Ops__Watchdog__TickMonitor__StackCaptureIntervalSeconds = '300' }
            $set = Set-LiveDiagnosticsEnvironment -Plan $plan
            [pscustomobject]@{
                Set = @($set); Kept = @($plan.Settings | Where-Object Kept | ForEach-Object Name); Active = @($plan.Active)
            } | ConvertTo-Json -Compress
            """);
        using JsonDocument json = JsonDocument.Parse(output.Trim());
        JsonElement r = json.RootElement;
        string[] kept = r.GetProperty("Kept").EnumerateArray().Select(e => e.GetString()!).Order().ToArray();
        Assert.Equal(["Ops__Watchdog__TickMonitor__StackCaptureIntervalSeconds", "Ops__Watchdog__TickMonitor__StackCaptureTool"], kept);
        string[] set = r.GetProperty("Set").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.DoesNotContain("Ops__Watchdog__TickMonitor__StackCaptureTool", set);
        Assert.DoesNotContain("Ops__Watchdog__TickMonitor__StackCaptureIntervalSeconds", set);
        Assert.Contains("Ops__Watchdog__TickMonitor__StackCaptureDirectory", set);
        Assert.Contains(r.GetProperty("Active").EnumerateArray().Select(e => e.GetString()!), l => l == "kept from the environment: Ops__Watchdog__TickMonitor__StackCaptureTool=custom-tool");
    }

    /// <summary>A whitespace-only variable counts as unset, so the plan's value is applied.</summary>
    [PwshFact]
    public void WhitespaceVariable_CountsAsUnset()
    {
        string output = RunPwsh($$"""
            $plan = Get-LiveDiagnosticsPlan -Role Realm -LiveHome '{{Quote(_home)}}' -DotnetRoot '{{Quote(_dotnet)}}' -UserHome '{{Quote(_userHome)}}' -Environment @{ DOTNET_DbgMiniDumpType = '  ' }
            @($plan.Settings | Where-Object Kept).Count
            """);
        Assert.Equal("0", output.Trim());
    }

    /// <summary>The Full-dump free-space threshold is inclusive: exactly 16 GB stays Full, one byte less drops to Heap; a smaller custom threshold is honoured.</summary>
    [PwshFact]
    public void FullDump_FreeSpaceThreshold_IsInclusive()
    {
        long threshold = 16L * 1024 * 1024 * 1024;
        Assert.Equal("4", GetPlan("World", "Max", extra: $"-DumpFreeBytes {threshold}").Value("DOTNET_DbgMiniDumpType"));
        Plan below = GetPlan("World", "Max", extra: $"-DumpFreeBytes {threshold - 1}");
        Assert.Equal("2", below.Value("DOTNET_DbgMiniDumpType"));
        Assert.Contains(below.Warnings, w => w.Contains("Heap dump, not Full", StringComparison.Ordinal));
        Assert.Equal("4", GetPlan("World", "Max", extra: "-DumpFreeBytes 1GB -FullDumpMinFreeBytes 1GB").Value("DOTNET_DbgMiniDumpType"));
        Assert.Contains(GetPlan("World", "Max", extra: "-DumpFreeBytes 0").Active, l => l.StartsWith("crash dump: Heap", StringComparison.Ordinal));
    }

    /// <summary>Planning is read-only (no directories); applying creates the three capture directories for Max and nothing for Standard.</summary>
    [PwshFact]
    public void Plan_CreatesNothing_ApplyCreatesDirectories_StandardApplyDoesNothing()
    {
        string output = RunPwsh($$"""
            $live = '{{Quote(Path.Combine(_root, "fresh"))}}'
            $std = Get-LiveDiagnosticsPlan -Role World -LiveHome $live -Level Standard
            $stdSet = @(Set-LiveDiagnosticsEnvironment -Plan $std)
            $afterStandard = Test-Path $live
            $plan = Get-LiveDiagnosticsPlan -Role World -LiveHome $live -DotnetRoot '{{Quote(_dotnet)}}' -UserHome '{{Quote(_userHome)}}' -Environment @{}
            $afterPlan = Test-Path $live
            [void](Set-LiveDiagnosticsEnvironment -Plan $plan)
            [pscustomobject]@{
                StdSet = $stdSet.Count; AfterStandard = $afterStandard; AfterPlan = $afterPlan
                Dumps = Test-Path (Join-Path $live 'dumps'); Hang = Test-Path (Join-Path $live 'logs\hangstacks'); Counters = Test-Path (Join-Path $live 'logs\counters')
            } | ConvertTo-Json -Compress
            """);
        using JsonDocument json = JsonDocument.Parse(output.Trim());
        JsonElement r = json.RootElement;
        Assert.Equal(0, r.GetProperty("StdSet").GetInt32());
        Assert.False(r.GetProperty("AfterStandard").GetBoolean());
        Assert.False(r.GetProperty("AfterPlan").GetBoolean());
        Assert.True(r.GetProperty("Dumps").GetBoolean());
        Assert.True(r.GetProperty("Hang").GetBoolean());
        Assert.True(r.GetProperty("Counters").GetBoolean());
    }

    /// <summary>The hang-stack interval is range-checked (10 s to a day), so a typo cannot turn capture into a per-second stack dump.</summary>
    [PwshFact]
    public void HangStackInterval_BelowTenSeconds_IsRejected()
    {
        var (exit, text) = RunPwshRaw($"Get-LiveDiagnosticsPlan -Role World -LiveHome '{Quote(_home)}' -HangStackIntervalSeconds 9");
        Assert.NotEqual(0, exit);
        Assert.Contains("HangStackIntervalSeconds", text, StringComparison.Ordinal);
        Assert.Equal(0, RunPwshRaw($"Get-LiveDiagnosticsPlan -Role World -LiveHome '{Quote(_home)}' -HangStackIntervalSeconds 10").ExitCode);
    }

    /// <summary>Retention edge cases: a missing directory, an empty one, no caps, ties on timestamp and both caps at once.</summary>
    [PwshFact]
    public void Retention_EdgeCases()
    {
        Assert.Equal("[]", RunPwsh($"ConvertTo-Json -InputObject @(Invoke-DiagnosticsRetention -Directory '{Quote(Path.Combine(_home, "nope"))}' -MaxFiles 1) -Compress").Trim());

        string dir = Path.Combine(_home, "ret");
        Directory.CreateDirectory(dir);
        Assert.Equal("[]", RunPwsh($"ConvertTo-Json -InputObject @(Invoke-DiagnosticsRetention -Directory '{Quote(dir)}' -MaxFiles 1) -Compress").Trim());

        DateTime stamp = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (string name in new[] { "a.dmp", "b.dmp", "c.dmp", "d.dmp" })
        {
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, new byte[100]);
            File.SetLastWriteTimeUtc(path, stamp);
        }

        // 0 = no limit: nothing is deleted.
        Assert.Equal("[]", RunPwsh($"ConvertTo-Json -InputObject @(Invoke-DiagnosticsRetention -Directory '{Quote(dir)}' -Filter '*.dmp') -Compress").Trim());
        Assert.Equal(4, Directory.GetFiles(dir).Length);

        // Equal timestamps: the name breaks the tie, so the same files go every time (the highest names are the newest).
        string byCount = RunPwsh($"Invoke-DiagnosticsRetention -Directory '{Quote(dir)}' -Filter '*.dmp' -MaxFiles 2 | Sort-Object | ConvertTo-Json -Compress -AsArray");
        Assert.Equal(["a.dmp", "b.dmp"], JsonSerializer.Deserialize<string[]>(byCount.Trim())!);

        // Both caps: the count cap allows 2 (c, d) but the byte cap allows 1.
        string both = RunPwsh($"Invoke-DiagnosticsRetention -Directory '{Quote(dir)}' -Filter '*.dmp' -MaxFiles 2 -MaxBytes 150 | ConvertTo-Json -Compress -AsArray");
        Assert.Equal(["c.dmp"], JsonSerializer.Deserialize<string[]>(both.Trim())!);
        Assert.Equal(["d.dmp"], Directory.GetFiles(dir).Select(path => Path.GetFileName(path)!).ToArray());
    }

    /// <summary>The rules apply each directory's own filter: an old non-matching file is never touched, and deleted names carry the rule name.</summary>
    [PwshFact]
    public void RetentionRules_ApplyPerDirectoryFilters_AndReportRuleNames()
    {
        string hang = Path.Combine(_home, "logs", "hangstacks");
        string counters = Path.Combine(_home, "logs", "counters");
        Directory.CreateDirectory(hang);
        Directory.CreateDirectory(counters);
        DateTime t0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        Touch(Path.Combine(hang, "hang-stacks-1.txt"), t0);
        Touch(Path.Combine(hang, "hang-stacks-2.txt"), t0.AddMinutes(1));
        Touch(Path.Combine(hang, "notes.txt"), t0.AddMinutes(-5));
        Touch(Path.Combine(counters, "world-1.csv"), t0);
        Touch(Path.Combine(counters, "world-2.csv"), t0.AddMinutes(1));
        Touch(Path.Combine(counters, "world-sidecar.log"), t0.AddMinutes(-5));

        string output = RunPwsh($"Invoke-DiagnosticsRetentionRules -Rules (Get-DiagnosticsRetentionRules -LiveHome '{Quote(_home)}' -MaxHangStackFiles 1 -MaxCountersFiles 1) | Sort-Object | ConvertTo-Json -Compress -AsArray");
        Assert.Equal(["counters/world-1.csv", "hangstacks/hang-stacks-1.txt"], JsonSerializer.Deserialize<string[]>(output.Trim())!);
        Assert.True(File.Exists(Path.Combine(hang, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(counters, "world-sidecar.log")));
    }

    /// <summary>The counters segment duration is d:hh:mm:ss, and the series counts are bounded.</summary>
    [PwshFact]
    public void CountersArguments_DurationFormatsSegmentLengths()
    {
        foreach ((int minutes, string expected) in new[] { (60, "00:01:00:00"), (45, "00:00:45:00"), (1, "00:00:01:00"), (90, "00:01:30:00"), (1440, "01:00:00:00") })
        {
            string output = RunPwsh($"Get-CountersCollectArguments -ProcessId 1 -OutputPath 'x.csv' -SegmentMinutes {minutes} | ConvertTo-Json -Compress -AsArray");
            string[] args = JsonSerializer.Deserialize<string[]>(output.Trim())!;
            string duration = args[Array.IndexOf(args, "--duration") + 1];
            Assert.Equal(expected, duration);
            // dotnet-counters reads dd:hh:mm:ss as a TimeSpan; "00:24:00:00" would not parse.
            Assert.Equal(TimeSpan.FromMinutes(minutes), TimeSpan.ParseExact(duration, @"dd\:hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal("2000", args[Array.IndexOf(args, "--maxTimeSeries") + 1]);
            Assert.Equal("50", args[Array.IndexOf(args, "--maxHistograms") + 1]);
        }
    }

    /// <summary>The sidecar needs a daemon to follow: with neither pid it fails fast; with a pid that is already gone it exits cleanly without trimming anything.</summary>
    [PwshFact]
    public void Sidecar_RejectsMissingPid_AndExitsCleanlyForADeadDaemon()
    {
        string sidecar = Path.Combine(LiveDir, "diagnostics-sidecar.ps1");
        var (noPidExit, noPidText) = RunProcess("pwsh", "-NoProfile", "-NonInteractive", "-File", sidecar, "-Role", "World", "-LiveHome", _home);
        Assert.NotEqual(0, noPidExit);
        Assert.Contains("-ParentPid", noPidText, StringComparison.Ordinal);

        using Process gone = StartPwsh("-NoProfile", "-NonInteractive", "-Command", "exit 0");
        gone.WaitForExit();
        string dumps = Path.Combine(_home, "dumps");
        Directory.CreateDirectory(dumps);
        Touch(Path.Combine(dumps, "world-1.dmp"), DateTime.UtcNow.AddDays(-2));
        Touch(Path.Combine(dumps, "world-2.dmp"), DateTime.UtcNow.AddDays(-1));
        var (exit, text) = RunProcess("pwsh", "-NoProfile", "-NonInteractive", "-File", sidecar, "-Role", "World", "-LiveHome", _home,
            "-DaemonPid", gone.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), "-MaxDumpFiles", "1");
        Assert.True(exit == 0, text);
        Assert.Equal(2, Directory.GetFiles(dumps).Length);
        Assert.Contains("sidecar exits", File.ReadAllText(Path.Combine(_home, "logs", "counters", "world-sidecar.log")), StringComparison.Ordinal);
    }

    /// <summary>A counters tool that cannot start is logged and the sidecar still follows the daemon and trims dumps.</summary>
    [PwshFact]
    public void Sidecar_WithAnUnstartableCountersTool_LogsAndStillTrims()
    {
        string dumps = Path.Combine(_home, "dumps");
        Directory.CreateDirectory(dumps);
        DateTime t0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        Touch(Path.Combine(dumps, "realm-1.dmp"), t0);
        Touch(Path.Combine(dumps, "realm-2.dmp"), t0.AddHours(1));

        using Process daemon = StartPwsh("-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 4");
        var (exit, text) = RunProcess("pwsh", "-NoProfile", "-NonInteractive", "-File", Path.Combine(LiveDir, "diagnostics-sidecar.ps1"), "-Role", "Realm",
            "-LiveHome", _home, "-DaemonPid", daemon.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), "-PollSeconds", "1", "-MaxDumpFiles", "1",
            "-CountersTool", Path.Combine(_root, "no-such-dotnet-counters.exe"));
        Assert.True(exit == 0, text);
        Assert.Equal(["realm-2.dmp"], Directory.GetFiles(dumps).Select(path => Path.GetFileName(path)!).ToArray());
        string log = File.ReadAllText(Path.Combine(_home, "logs", "counters", "realm-sidecar.log"));
        Assert.Contains("counters collector did not start", log, StringComparison.Ordinal);
        Assert.Contains("retention removed dumps/realm-1.dmp", log, StringComparison.Ordinal);
        Assert.Contains("ended; sidecar exits", log, StringComparison.Ordinal);
    }

    /// <summary>The launcher rejects an unknown level or an out-of-range interval at parameter binding, before it looks for the executable or starts anything.</summary>
    [PwshFact]
    public void LaunchDaemon_RejectsInvalidParameters_BeforeStartingAnything()
    {
        string launch = Path.Combine(LiveDir, "launch-daemon.ps1");
        var (levelExit, levelText) = RunProcess("pwsh", "-NoProfile", "-NonInteractive", "-File", launch, "-Role", "World", "-LiveHome", _home, "-Diagnostics", "Bogus");
        Assert.NotEqual(0, levelExit);
        Assert.Contains("Bogus", levelText, StringComparison.Ordinal);

        var (intervalExit, intervalText) = RunProcess("pwsh", "-NoProfile", "-NonInteractive", "-File", launch, "-Role", "World", "-LiveHome", _home, "-HangStackIntervalSeconds", "1");
        Assert.NotEqual(0, intervalExit);
        Assert.Contains("HangStackIntervalSeconds", intervalText, StringComparison.Ordinal);

        Assert.False(Directory.Exists(Path.Combine(_home, "dumps")));
    }

    /// <summary>With valid parameters but no executable under the live home the launcher stops with "not found" before the plan creates any directory.</summary>
    [PwshFact]
    public void LaunchDaemon_WithoutAnExecutable_StopsBeforeApplyingTheDiagnosticsPlan()
    {
        string profile = Path.Combine(_root, "profile");
        Directory.CreateDirectory(profile);
        var (exit, text) = RunProcess("pwsh", "-NoProfile", "-NonInteractive", "-File", Path.Combine(LiveDir, "launch-daemon.ps1"), "-Role", "Realm",
            "-LiveHome", _home, "-Profile", profile);
        Assert.NotEqual(0, exit);
        Assert.Contains("not found", text, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_home, "dumps")));
    }

    private static void Touch(string path, DateTime utc)
    {
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, utc);
    }

    private static (int ExitCode, string Text) RunProcess(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in arguments)
        {
            start.ArgumentList.Add(a);
        }

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(90_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(file + " did not finish within 90 s");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private (int ExitCode, string Text) RunPwshRaw(string body)
    {
        string script = Path.Combine(_root, "raw-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(script, $"$ErrorActionPreference = 'Stop'{Environment.NewLine}. '{Quote(Path.Combine(LiveDir, "LiveDiagnostics.ps1"))}'{Environment.NewLine}{body}", new UTF8Encoding(false));
        return RunProcess("pwsh", "-NoProfile", "-NonInteractive", "-File", script);
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
