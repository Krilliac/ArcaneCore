using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// The live launchers (tools/ops/live/start-single.ps1, start-cluster.ps1, LiveLaunch.ps1) under PowerShell 7. Nothing here starts a
/// server: every launcher run is a -Plan run (or a refused one) inside a wrapper that replaces Start-Process with a function that records
/// a marker and throws, and the marker's absence is asserted. The live home is a temporary directory of empty files.
/// </summary>
public sealed class LiveLaunchScriptTests : IDisposable
{
    private const string Secret = "tOpSecretLinkValue-0123456789";

    private static readonly string LiveDir = Path.Combine(RepositorySource.RequireRoot(), "tools", "ops", "live");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcane-livelaunch-" + Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly string _profile;

    public LiveLaunchScriptTests()
    {
        _home = Path.Combine(_root, "home");
        _profile = Path.Combine(_root, "profile");
        Directory.CreateDirectory(Path.Combine(_home, "bin"));
        Directory.CreateDirectory(_profile);
        File.WriteAllText(Path.Combine(_profile, "appsettings.json"), """{ "World": { "Port": 8090 } }""");
        foreach (string image in new[] { "Realm", "World", "Gateway" })
        {
            File.WriteAllBytes(Path.Combine(_home, "bin", $"ArcaneCore.{image}.exe"), []);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ---- build detection ------------------------------------------------------------------------------------------------------------

    [PwshFact]
    public void BuildConfiguration_OfThisTestsOwnBin_IsTheConfigurationItWasBuiltWith()
    {
        // ArcaneCore.Kernel.dll beside the test binaries is a real build output of the same configuration as this assembly.
        string bin = Path.Combine(_root, "realbin");
        Directory.CreateDirectory(bin);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "ArcaneCore.Kernel.dll"), Path.Combine(bin, "ArcaneCore.Kernel.dll"));
#if DEBUG
        const string expected = "Debug";
#else
        const string expected = "Release";
#endif
        Assert.Equal(expected, Eval($"(Get-LiveBuildConfiguration -BinDir '{Q(bin)}').Configuration"));
    }

    [PwshFact]
    public void BuildConfiguration_ReadsDebugAndReleaseFromTinyAssemblies()
    {
        string debug = EmitDirectory("debug", ("ArcaneCore.Tiny.dll", 0x107, "Debug"));
        string release = EmitDirectory("release", ("ArcaneCore.Tiny.dll", 0x2, "Release"));
        Assert.Equal("Debug", Eval($"(Get-LiveBuildConfiguration -BinDir '{Q(debug)}').Configuration"));
        Assert.Equal("Release", Eval($"(Get-LiveBuildConfiguration -BinDir '{Q(release)}').Configuration"));
        Assert.Equal("ArcaneCore.World (Debug, live)", Eval("Get-LiveWindowTitle -Name 'ArcaneCore.World' -Configuration 'Debug'"));
    }

    [PwshFact]
    public void BuildConfiguration_DebuggableAttributeDecides_ConfigurationNameIsTheFallback_AndUnknownIsTheDefault()
    {
        // Optimizer on but labelled Debug: how the JIT treats the code (Release) wins.
        string conflict = EmitDirectory("conflict", ("ArcaneCore.Tiny.dll", 0x2, "Debug"));
        // No DebuggableAttribute: the SDK's configuration name decides.
        string labelOnly = EmitDirectory("label", ("ArcaneCore.Tiny.dll", null, "Release"));
        // Neither attribute, an unrelated name, a missing directory and a non-assembly file.
        string nothing = EmitDirectory("nothing", ("ArcaneCore.Tiny.dll", null, null));
        string custom = EmitDirectory("custom", ("ArcaneCore.Tiny.dll", null, "Staging"));
        File.WriteAllText(Path.Combine(nothing, "ArcaneCore.NotAssembly.dll"), "not a PE file");
        Assert.Equal("Release", Eval($"(Get-LiveBuildConfiguration -BinDir '{Q(conflict)}').Configuration"));
        Assert.Equal("Release", Eval($"(Get-LiveBuildConfiguration -BinDir '{Q(labelOnly)}').Configuration"));
        Assert.Equal("unknown", Eval($"(Get-LiveBuildConfiguration -BinDir '{Q(nothing)}').Configuration"));
        Assert.Equal("unknown", Eval($"(Get-LiveBuildConfiguration -BinDir '{Q(custom)}').Configuration"));
        Assert.Equal("unknown", Eval($"(Get-LiveBuildConfiguration -BinDir '{Q(Path.Combine(_root, "missing"))}').Configuration"));
    }

    [PwshFact]
    public void BuildConfiguration_AssembliesThatDisagree_AreMixed()
    {
        string mixed = EmitDirectory("mixed", ("ArcaneCore.A.dll", 0x107, "Debug"), ("ArcaneCore.B.dll", 0x2, "Release"));
        Assert.Equal("Mixed", Eval($"(Get-LiveBuildConfiguration -BinDir '{Q(mixed)}').Configuration"));
    }

    // ---- start-single ---------------------------------------------------------------------------------------------------------------

    [PwshFact]
    public void StartSingle_Plan_ListsRealmWorldStatusAndDebugger_AndStartsNothing()
    {
        BuildBin(("ArcaneCore.Kernel.dll", 0x107, "Debug"));
        (int code, string output, bool started) = RunLauncher("start-single.ps1", ["-Plan"]);

        Assert.Equal(0, code);
        Assert.False(started, "the plan started a process");
        Assert.StartsWith("PLAN start-single (nothing was started)", output, StringComparison.Ordinal);
        Assert.Contains("build:      Debug", output, StringComparison.Ordinal);
        Assert.Contains("ports:      auth 3724, world 8090", output, StringComparison.Ordinal); // the world port comes from the profile
        Assert.Contains("title:   ArcaneCore.Realm (Debug, live)", output, StringComparison.Ordinal);
        Assert.Contains("title:   ArcaneCore.World (Debug, live)", output, StringComparison.Ordinal);
        Assert.Contains("title:   ArcaneCore status (Debug, live)", output, StringComparison.Ordinal);
        Assert.Contains("title:   ArcaneCore Win32 debugger (Debug, live)", output, StringComparison.Ordinal);
        Assert.Contains("launch-daemon.ps1 -Role Realm", output, StringComparison.Ordinal);
        Assert.Contains("launch-daemon.ps1 -Role World", output, StringComparison.Ordinal);
        Assert.Contains("ARCANECORE_LIVE_SERVER=1", output, StringComparison.Ordinal);
        Assert.Contains("DOTNET_GCHighMemPercent=0x61", output, StringComparison.Ordinal);
        Assert.Contains("realm.debug.", output, StringComparison.Ordinal);
        Assert.Contains("world.debug.", output, StringComparison.Ordinal);
        Assert.Contains("(read-only)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("-Supervise", output, StringComparison.Ordinal);
        Assert.Contains("attach-debugger.ps1 -RealmPid", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.GetDirectories(_home), d => Path.GetFileName(d) != "bin"); // no logs, dumps or snapshots were created
    }

    [PwshFact]
    public void StartSingle_Plan_Switches_DropWindows_AndPassSupervise()
    {
        BuildBin(("ArcaneCore.Kernel.dll", 0x2, "Release"));
        (_, string noExtras, _) = RunLauncher("start-single.ps1", ["-Plan", "-NoStatus", "-NoDebugger"]);
        Assert.Contains("build:      Release", noExtras, StringComparison.Ordinal);
        Assert.DoesNotContain("server-status.ps1", noExtras, StringComparison.Ordinal);
        Assert.DoesNotContain("attach-debugger.ps1", noExtras, StringComparison.Ordinal);

        (_, string supervised, _) = RunLauncher("start-single.ps1", ["-WhatIf", "-NoDebugger", "-Supervise"]);
        Assert.Contains("status: server-status.ps1 -Supervise", supervised, StringComparison.Ordinal);
        Assert.Contains("server-status.ps1", supervised, StringComparison.Ordinal);
        Assert.Contains(" -Supervise -HangKillSeconds 90", supervised, StringComparison.Ordinal);
    }

    [PwshTheory]
    [InlineData("-Supervise -NoStatus", "-Supervise needs the status window")]
    [InlineData("-AuthPort 8090 -WorldPort 8090", "cannot share a port")]
    [InlineData("-AuthPort 70000", "70000")]
    [InlineData("-GCHighMemPercent 10", "10")]
    [InlineData("-Diagnostics Loud", "Loud")]
    public void StartSingle_RefusesBadParameters_BeforeStartingAnything(string arguments, string message)
    {
        BuildBin(("ArcaneCore.Kernel.dll", 0x107, "Debug"));
        (int code, string output, bool started) = RunLauncher("start-single.ps1", [.. arguments.Split(' ')]);
        Assert.NotEqual(0, code);
        Assert.Contains(message, output, StringComparison.Ordinal);
        Assert.False(started);
    }

    [PwshFact]
    public void StartSingle_RefusesAMissingExecutableAndAMissingProfile()
    {
        File.Delete(Path.Combine(_home, "bin", "ArcaneCore.World.exe"));
        (int code, string output, bool started) = RunLauncher("start-single.ps1", ["-Plan"]);
        Assert.NotEqual(0, code);
        Assert.Contains("ArcaneCore.World.exe", output, StringComparison.Ordinal);
        Assert.Contains("not found", output, StringComparison.Ordinal);
        Assert.False(started);

        File.WriteAllBytes(Path.Combine(_home, "bin", "ArcaneCore.World.exe"), []);
        File.Delete(Path.Combine(_profile, "appsettings.json"));
        (code, output, started) = RunLauncher("start-single.ps1", ["-Plan"]);
        Assert.NotEqual(0, code);
        Assert.Contains("no appsettings.json", output, StringComparison.Ordinal);
        Assert.False(started);
    }

    // ---- start-cluster --------------------------------------------------------------------------------------------------------------

    [PwshFact]
    public void StartCluster_Plan_Default_IsRealmPlusCombinedWorld_WithEnvironmentBuiltFromParameters()
    {
        BuildBin(("ArcaneCore.Kernel.dll", 0x107, "Debug"));
        (int code, string output, bool started) = RunLauncher("start-cluster.ps1", ["-Plan"]);

        Assert.Equal(0, code);
        Assert.False(started, "the plan started a process");
        Assert.StartsWith("PLAN start-cluster (nothing was started)", output, StringComparison.Ordinal);
        Assert.Contains("title:   ArcaneCore.World supervisor (Debug, live)", output, StringComparison.Ordinal);
        Assert.Contains("launch-daemon.ps1 -Role World -ClusterSupervisor", output, StringComparison.Ordinal);
        Assert.Contains("      Cluster__Supervisor__Enabled=true", output, StringComparison.Ordinal);
        Assert.Contains("      Cluster__Supervisor__MinClusterProcesses=2", output, StringComparison.Ordinal);
        Assert.Contains("      Cluster__Supervisor__Processes__realm__Role=realm", output, StringComparison.Ordinal);
        Assert.Contains("      Cluster__Supervisor__Processes__realm__StartOrder=0", output, StringComparison.Ordinal);
        Assert.Contains("      Cluster__Supervisor__Processes__world__Role=combined", output, StringComparison.Ordinal);
        Assert.Contains("      Cluster__Supervisor__Processes__world__StartOrder=1", output, StringComparison.Ordinal);
        Assert.Contains("      Cluster__Supervisor__Processes__world__RealmId=1", output, StringComparison.Ordinal);
        Assert.Contains("      Cluster__Supervisor__Processes__world__Console=Merged", output, StringComparison.Ordinal);
        Assert.Contains("      Cluster__Supervisor__Processes__realm__HangTimeoutSeconds=120", output, StringComparison.Ordinal);
        Assert.Contains("--Cluster:Supervisor:Enabled=false", output, StringComparison.Ordinal);
        Assert.Contains("supervisor.debug.", output, StringComparison.Ordinal);
        Assert.Contains("world.heartbeat", output, StringComparison.Ordinal);
        Assert.Contains("status: server-status.ps1 (read-only; the cluster supervisor owns restarts)", output, StringComparison.Ordinal);
        Assert.Contains("-HeartbeatPath", output, StringComparison.Ordinal);
        Assert.DoesNotContain(" -Supervise", output, StringComparison.Ordinal);
        Assert.Contains("attach-debugger.ps1 -SupervisorPid", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ArcaneCore.Gateway", output, StringComparison.Ordinal);
        Assert.DoesNotContain("LinkSecret", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.GetDirectories(_home), d => Path.GetFileName(d) != "bin");
    }

    [PwshFact]
    public void StartCluster_Plan_ShowsHangCapture_AndTheCrashDumpPatternTheChildrenInherit()
    {
        BuildBin(("ArcaneCore.Kernel.dll", 0x107, "Debug"));
        (_, string output, _) = RunLauncher("start-cluster.ps1", ["-Plan"]);
        Assert.Contains("hang capture: Dump of the world child into", output, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_home, "dumps"), output, StringComparison.Ordinal);
        Assert.Contains("      Cluster__Supervisor__Processes__world__HangCapture=Dump", output, StringComparison.Ordinal);
        Assert.Contains("      Cluster__Supervisor__HangCaptureTimeoutSeconds=300", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Processes__realm__HangCapture", output, StringComparison.Ordinal);
        Assert.Contains("crash dumps: inherited by every child", output, StringComparison.Ordinal);
        Assert.Contains("      DOTNET_DbgEnableMiniDump=1", output, StringComparison.Ordinal);
        Assert.Contains("world-%p-%t.dmp shared by all children; the pid tells them apart", output, StringComparison.Ordinal);

        (_, string none, _) = RunLauncher("start-cluster.ps1", ["-Plan", "-HangCapture", "None", "-Diagnostics", "Standard"]);
        Assert.Contains("hang capture: None", none, StringComparison.Ordinal);
        Assert.DoesNotContain("HangCaptureDirectory", none, StringComparison.Ordinal);
        Assert.Contains("crash dumps: not set by this launch", none, StringComparison.Ordinal);
    }

    [PwshFact]
    public void StartCluster_Plan_WithGatewayAndMapWorker_AddsTheGatewayChildAndTurnsTheWorldIntoAWorker()
    {
        BuildBin(("ArcaneCore.Kernel.dll", 0x2, "Release"));
        (int code, string output, bool started) = RunLauncher("start-cluster.ps1",
            ["-Plan", "-Gateway", "-MapWorker", "-NoDebugger", "-RealmId", "2", "-GatewayPort", "8100", "-WorkerPort", "8101", "-HangTimeoutSeconds", "300"],
            linkSecret: Secret);

        Assert.Equal(0, code);
        Assert.False(started);
        Assert.Contains("title:   ArcaneCore.World supervisor (Release, live)", output, StringComparison.Ordinal);
        Assert.Contains("      Cluster__Supervisor__MinClusterProcesses=3", output, StringComparison.Ordinal);
        Assert.Contains("      Cluster__Supervisor__Processes__world__Role=worker", output, StringComparison.Ordinal);
        Assert.Contains("      Cluster__Supervisor__Processes__world__RealmId=2", output, StringComparison.Ordinal);
        Assert.Contains("      Cluster__Supervisor__Processes__gateway__Role=gateway", output, StringComparison.Ordinal);
        Assert.Contains("      Cluster__Supervisor__Processes__gateway__StartOrder=2", output, StringComparison.Ordinal);
        Assert.Contains("      Cluster__Supervisor__Processes__gateway__HangTimeoutSeconds=300", output, StringComparison.Ordinal);
        Assert.Contains("--Cluster:Worker:Enabled=true", output, StringComparison.Ordinal);
        Assert.Contains("--Cluster:Worker:Port=8101", output, StringComparison.Ordinal);
        Assert.Contains("--Cluster:Fencing:Enabled=true", output, StringComparison.Ordinal);
        Assert.Contains("--Cluster:Node:Role=MapWorker", output, StringComparison.Ordinal);
        Assert.Contains("--Gateway:Port=8100", output, StringComparison.Ordinal);
        Assert.Contains("--Gateway:WorkerPort=8101", output, StringComparison.Ordinal);
        Assert.Contains("ArcaneCore.Gateway.exe alive as a child of the supervisor and listening on port 8100", output, StringComparison.Ordinal);
        Assert.Contains("topology:   realm + gateway + worker (MapWorker)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("attach-debugger.ps1", output, StringComparison.Ordinal);
    }

    [PwshFact]
    public void StartCluster_NeverPrintsTheLinkSecret_NotInThePlanNorInARefusal()
    {
        BuildBin(("ArcaneCore.Kernel.dll", 0x107, "Debug"));
        (int code, string output, _) = RunLauncher("start-cluster.ps1", ["-Plan", "-Gateway"], linkSecret: Secret);
        Assert.Equal(0, code);
        Assert.DoesNotContain(Secret, output, StringComparison.Ordinal);
        Assert.Contains("      Cluster__Worker__LinkSecret=***", output, StringComparison.Ordinal);
        Assert.Contains("      Gateway__LinkSecret=***", output, StringComparison.Ordinal);
        Assert.DoesNotContain("--Cluster:Worker:LinkSecret", output, StringComparison.Ordinal); // never on a command line

        // From the parameter instead of the environment: still masked.
        (code, output, _) = RunLauncher("start-cluster.ps1", ["-Plan", "-Gateway", "-LinkSecret", Secret]);
        Assert.Equal(0, code);
        Assert.DoesNotContain(Secret, output, StringComparison.Ordinal);

        // A refusal for another reason does not echo it either.
        (code, output, _) = RunLauncher("start-cluster.ps1", ["-Plan", "-Gateway", "-MapWorker", "-WorkerAddress", "not-an-ip"], linkSecret: Secret);
        Assert.NotEqual(0, code);
        Assert.DoesNotContain(Secret, output, StringComparison.Ordinal);
    }

    [PwshFact]
    public void SecretMasking_MasksByNameAndByValue()
    {
        Assert.Equal("***", Eval("Format-LiveEnvValue -Name 'Cluster__Worker__LinkSecret' -Value 'abc'"));
        Assert.Equal("***", Eval("Format-LiveEnvValue -Name 'Database__Password' -Value 'abc'"));
        Assert.Equal("***", Eval("Format-LiveEnvValue -Name 'ARCANECORE_SUPERVISOR_TOKEN' -Value 'abc'"));
        Assert.Equal("***", Eval("Format-LiveEnvValue -Name 'Some__Api_Key' -Value 'abc'"));
        Assert.Equal("1", Eval("Format-LiveEnvValue -Name 'ARCANECORE_LIVE_SERVER' -Value '1'"));
        Assert.Equal("Merged", Eval("Format-LiveEnvValue -Name 'Cluster__Supervisor__Processes__realm__Console' -Value 'Merged'"));
        Assert.Equal("a *** b ***", Eval("Hide-LiveSecrets -Text 'a hunter2 b hunter2' -Secrets 'hunter2'"));
    }

    [PwshTheory]
    [InlineData("-Gateway", "link secret", null)]
    [InlineData("-Gateway", "at least 16 characters", "short")]
    [InlineData("-MapWorker", "-MapWorker needs -Gateway", null)]
    [InlineData("-LinkSecret abcdefghijklmnopqrstuvwxyz", "only means something with -Gateway", null)]
    [InlineData("-AuthPort 8090 -WorldPort 8090", "both 8090", null)]
    [InlineData("-Gateway -GatewayPort 8090", "both 8090", Secret)]
    [InlineData("-Gateway -WorkerPort 3724", "both 3724", Secret)]
    [InlineData("-Gateway -WorkerAddress not-an-ip", "not an IP address", Secret)]
    [InlineData("-MinClusterProcesses 3", "more than the 2 process", null)]
    [InlineData("-HangTimeoutSeconds 10", "HangTimeoutSeconds", null)]
    [InlineData("-HangCaptureTimeoutSeconds 60", "HangCaptureTimeoutSeconds", null)]
    [InlineData("-HangCapture Everything", "Everything", null)]
    [InlineData("-ChildConsole Loud", "Loud", null)]
    [InlineData("-RealmId 0", "RealmId", null)]
    public void StartCluster_RefusesBadParameters_BeforeStartingAnything(string arguments, string message, string? linkSecret)
    {
        BuildBin(("ArcaneCore.Kernel.dll", 0x107, "Debug"));
        (int code, string output, bool started) = RunLauncher("start-cluster.ps1", [.. arguments.Split(' ')], linkSecret);
        Assert.NotEqual(0, code);
        Assert.Contains(message, output, StringComparison.Ordinal);
        Assert.False(started);
        if (linkSecret is not null)
        {
            Assert.DoesNotContain(linkSecret, output, StringComparison.Ordinal);
        }
    }

    [PwshFact]
    public void StartCluster_RefusesAMissingGatewayExecutable()
    {
        File.Delete(Path.Combine(_home, "bin", "ArcaneCore.Gateway.exe"));
        (int code, string output, bool started) = RunLauncher("start-cluster.ps1", ["-Plan", "-Gateway"], Secret);
        Assert.NotEqual(0, code);
        Assert.Contains("ArcaneCore.Gateway.exe", output, StringComparison.Ordinal);
        Assert.False(started);
    }

    [Fact]
    public void LaunchDaemon_TitlesWithTheDetectedBuild_AndTheSupervisorHasItsOwnLog()
    {
        string launch = File.ReadAllText(Path.Combine(LiveDir, "launch-daemon.ps1"));
        Assert.Contains("Get-LiveBuildConfiguration -BinDir", launch, StringComparison.Ordinal);
        Assert.Contains("Get-LiveWindowTitle -Name $titleName -Configuration $buildConfiguration", launch, StringComparison.Ordinal);
        Assert.DoesNotContain("(DEBUG live)", launch, StringComparison.Ordinal);
        Assert.Contains("-LogStem $(if ($ClusterSupervisor) { 'supervisor' } else { '' })", launch, StringComparison.Ordinal);
    }

    // ---- harness --------------------------------------------------------------------------------------------------------------------

    private static string Q(string path) => path.Replace("'", "''");

    /// <summary>Writes the build output the launchers detect: a tiny assembly per spec (DebuggableAttribute modes or null, AssemblyConfiguration or null).</summary>
    private void BuildBin(params (string File, int? Debuggable, string? Configuration)[] assemblies)
    {
        foreach ((string file, int? modes, string? configuration) in assemblies)
        {
            EmitAssembly(Path.Combine(_home, "bin", file), modes, configuration);
        }
    }

    private string EmitDirectory(string name, params (string File, int? Debuggable, string? Configuration)[] assemblies)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        foreach ((string file, int? modes, string? configuration) in assemblies)
        {
            EmitAssembly(Path.Combine(dir, file), modes, configuration);
        }

        return dir;
    }

    /// <summary>Saves an assembly with only the two build attributes: what the compiler writes for Debug (0x107, "Debug") and Release (0x2, "Release").</summary>
    private static void EmitAssembly(string path, int? debuggableModes, string? configuration)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        var builder = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        if (debuggableModes is int modes)
        {
            ConstructorInfo ctor = typeof(System.Diagnostics.DebuggableAttribute).GetConstructor([typeof(System.Diagnostics.DebuggableAttribute.DebuggingModes)])!;
            builder.SetCustomAttribute(new CustomAttributeBuilder(ctor, [(System.Diagnostics.DebuggableAttribute.DebuggingModes)modes]));
        }

        if (configuration is not null)
        {
            ConstructorInfo ctor = typeof(AssemblyConfigurationAttribute).GetConstructor([typeof(string)])!;
            builder.SetCustomAttribute(new CustomAttributeBuilder(ctor, [configuration]));
        }

        builder.DefineDynamicModule(name);
        builder.Save(path);
    }

    /// <summary>Runs a launcher with the temporary live home and profile, inside a wrapper whose Start-Process records a marker and throws.</summary>
    private (int Code, string Output, bool Started) RunLauncher(string script, IReadOnlyList<string> arguments, string? linkSecret = null)
    {
        string marker = Path.Combine(_root, "process-was-started");
        string wrapper = Path.Combine(_root, "wrapper.ps1");
        File.WriteAllText(wrapper, $$"""
            $ErrorActionPreference = 'Stop'
            function Start-Process { Set-Content -LiteralPath '{{Q(marker)}}' -Value ($args -join ' '); throw 'Start-Process must not run in a plan' }
            & '{{Q(Path.Combine(LiveDir, script))}}' {{string.Join(' ', arguments)}}
            exit $LASTEXITCODE
            """, new UTF8Encoding(false));
        var start = NewPwsh();
        start.Environment["ARCANECORE_LIVE_HOME"] = _home;
        start.Environment["ARCANECORE_LIVE_PROFILE"] = _profile;
        start.Environment.Remove("ARCANECORE_CLUSTER_LINK_SECRET");
        if (linkSecret is not null)
        {
            start.Environment["ARCANECORE_CLUSTER_LINK_SECRET"] = linkSecret;
        }

        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(wrapper);

        (int code, string output) = Execute(start);
        return (code, output, File.Exists(marker));
    }

    /// <summary>Dot-sources LiveLaunch.ps1, evaluates the expression and returns its string form.</summary>
    private string Eval(string expression)
    {
        string script = Path.Combine(_root, "eval-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(script, $$"""
            $ErrorActionPreference = 'Stop'
            . '{{Q(Path.Combine(LiveDir, "LiveLaunch.ps1"))}}'
            [string]({{expression}})
            """, new UTF8Encoding(false));
        var start = NewPwsh();
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        (int code, string output) = Execute(start);
        Assert.True(code == 0, $"pwsh exited {code}: {output}");
        return output.Trim();
    }

    private static ProcessStartInfo NewPwsh()
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
        return start;
    }

    private static (int Code, string Output) Execute(ProcessStartInfo start)
    {
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("pwsh did not finish within 120 s");
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

    private sealed class PwshTheoryAttribute : TheoryAttribute
    {
        public PwshTheoryAttribute()
        {
            if (!PwshProbe.Available)
            {
                Skip = "pwsh (PowerShell 7) is not on PATH on this host.";
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
