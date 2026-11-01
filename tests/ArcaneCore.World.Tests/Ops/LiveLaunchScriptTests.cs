using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Game.Maps;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster.Supervision;
using Microsoft.Extensions.Configuration;
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
        Assert.Contains("(read-only; nothing restarts a daemon here: use start-cluster.ps1 for that)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("-Supervise", output, StringComparison.Ordinal);
        Assert.Contains("attach-debugger.ps1 -RealmPid", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.GetDirectories(_home), d => Path.GetFileName(d) != "bin"); // no logs, dumps or snapshots were created
    }

    [PwshFact]
    public void StartSingle_Plan_Switches_DropWindows()
    {
        BuildBin(("ArcaneCore.Kernel.dll", 0x2, "Release"));
        (_, string noExtras, _) = RunLauncher("start-single.ps1", ["-Plan", "-NoStatus", "-NoDebugger"]);
        Assert.Contains("build:      Release", noExtras, StringComparison.Ordinal);
        Assert.DoesNotContain("server-status.ps1", noExtras, StringComparison.Ordinal);
        Assert.DoesNotContain("attach-debugger.ps1", noExtras, StringComparison.Ordinal);

        (_, string statusOnly, _) = RunLauncher("start-single.ps1", ["-WhatIf", "-NoDebugger"]);
        Assert.Contains("server-status.ps1", statusOnly, StringComparison.Ordinal);
        Assert.DoesNotContain(" -Supervise", statusOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("HangKillSeconds", statusOnly, StringComparison.Ordinal);
    }

    [PwshTheory]
    [InlineData("-Plan -Supervise")]
    [InlineData("-WhatIf -Supervise -NoDebugger")]
    [InlineData("-Supervise -NoStatus")]
    public void StartSingle_NoLongerOffersSupervise_ItIsRefusedWithAMessagePointingAtStartCluster(string arguments)
    {
        BuildBin(("ArcaneCore.Kernel.dll", 0x107, "Debug"));
        (int code, string output, bool started) = RunLauncher("start-single.ps1", [.. arguments.Split(' ')]);
        Assert.NotEqual(0, code);
        Assert.Contains("-Supervise is not offered by start-single.ps1", output, StringComparison.Ordinal);
        Assert.Contains("start-cluster.ps1", output, StringComparison.Ordinal);
        Assert.False(started);
        Assert.DoesNotContain("PLAN start-single", output, StringComparison.Ordinal);
    }

    [PwshTheory]
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
        Assert.DoesNotContain("Cluster:Fencing", output, StringComparison.Ordinal); // the single combined world stays unfenced: a crashed fenced world cannot be restarted
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
    [InlineData("-HangCaptureTimeoutSeconds 0", "HangCaptureTimeoutSeconds", null)]
    [InlineData("-HangCaptureTimeoutSeconds 3601", "HangCaptureTimeoutSeconds", null)]
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
    public void StartCluster_Plan_AcceptsTheWorldsMinimumHangCaptureTimeout_AndWarnsOnlyForDumpUnder120()
    {
        BuildBin(("ArcaneCore.Kernel.dll", 0x107, "Debug"));
        (int code, string one, bool started) = RunLauncher("start-cluster.ps1", ["-Plan", "-HangCaptureTimeoutSeconds", "1"]);
        Assert.Equal(0, code);
        Assert.False(started);
        Assert.Contains("      Cluster__Supervisor__HangCaptureTimeoutSeconds=1", one, StringComparison.Ordinal);
        Assert.Contains("WARNING: -HangCapture Dump with -HangCaptureTimeoutSeconds 1 (under 120)", one, StringComparison.Ordinal);

        (_, string edge, _) = RunLauncher("start-cluster.ps1", ["-Plan", "-HangCaptureTimeoutSeconds", "119"]);
        Assert.Contains("WARNING: -HangCapture Dump with -HangCaptureTimeoutSeconds 119", edge, StringComparison.Ordinal);
        (_, string ok, _) = RunLauncher("start-cluster.ps1", ["-Plan", "-HangCaptureTimeoutSeconds", "120"]);
        Assert.DoesNotContain("WARNING", ok, StringComparison.Ordinal);
        (_, string def, _) = RunLauncher("start-cluster.ps1", ["-Plan"]);
        Assert.DoesNotContain("WARNING", def, StringComparison.Ordinal);
        (_, string none, _) = RunLauncher("start-cluster.ps1", ["-Plan", "-HangCapture", "None", "-HangCaptureTimeoutSeconds", "1"]);
        Assert.DoesNotContain("WARNING", none, StringComparison.Ordinal);
    }

    // ---- the plan's Cluster__ environment and child arguments are what the World and the daemons accept -----------------------------------

    [PwshTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartCluster_PlanEnvironment_BindsLikeTheWorldDoes_Validates_AndTheChildArgumentsParse(bool gatewayMapWorker)
    {
        BuildBin(("ArcaneCore.Kernel.dll", 0x107, "Debug"));
        string[] arguments = gatewayMapWorker ? ["-Plan", "-Gateway", "-MapWorker"] : ["-Plan"];
        (int code, string output, bool started) = RunLauncher("start-cluster.ps1", arguments, gatewayMapWorker ? Secret : null);
        Assert.Equal(0, code);
        Assert.False(started);

        // The supervisor console's environment, as the plan prints it. The environment-variable provider turns "__" into ":".
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in output.Split('\n'))
        {
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(line.TrimEnd('\r'), "^ {6}(Cluster__[A-Za-z0-9_]+)=(.*)$");
            if (match.Success)
            {
                settings[match.Groups[1].Value.Replace("__", ":", StringComparison.Ordinal)] = match.Groups[2].Value;
            }
        }

        Assert.Equal("true", settings["Cluster:Supervisor:Enabled"]);
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = new ClusterSupervisorOptions();
        // Every emitted key must be a property of the options: a misspelt key (WorkingDirectory, LogFilePath, StartOrder, HangTimeoutSeconds,
        // HangCaptureDirectory, HangCaptureTimeoutSeconds, StackCaptureTool, ...) would otherwise bind to nothing and be ignored.
        configuration.GetSection(ClusterSupervisorOptions.SectionName).Bind(options, binder => binder.ErrorOnUnknownConfiguration = true);
        Assert.True(options.Enabled);
        Assert.Equal(gatewayMapWorker ? 3 : 2, options.Processes.Count);
        Assert.Equal(options.Processes.Count, options.MinClusterProcesses);
        Assert.Empty(options.Validate());
        Assert.DoesNotContain(new ClusterSupervisorConfigChecks().Check(configuration), issue => issue.Severity == ConfigSeverity.Error);
        Assert.All(options.Processes.Values, process => process.Validate());
        Assert.Equal(gatewayMapWorker ? "worker" : "combined", options.Processes["world"].Role);
        Assert.Equal(SupervisedConsoleMode.Merged, options.Processes["realm"].Console);
        Assert.Equal(SupervisedHangCaptureMode.Dump, options.Processes["world"].HangCapture);

        // Each child's command line goes through the daemon's own pre-host parser; the gateway has no such parser, so its tokens are only
        // checked for the --Section:Key=value shape.
        foreach ((string key, SupervisedProcessConfig process) in options.Processes)
        {
            string[] childArguments = SplitWindowsCommandLine(process.Arguments);
            Assert.NotEmpty(childArguments);
            if (key == "gateway")
            {
                Assert.All(childArguments, token => Assert.Matches("^--[A-Za-z][A-Za-z0-9:_.]*=.+$", token));
                continue;
            }

            ConfigDaemon daemon = key == "realm" ? ConfigDaemon.Realm : ConfigDaemon.World;
            DaemonCommand command = DaemonCommandLine.Parse(childArguments, daemon, ConfCatalog.Embedded);
            Assert.True(command.Kind == DaemonCommandKind.Run, $"{key}: {command.Kind} {command.Message} (arguments: {process.Arguments})");
            Assert.Equal(childArguments.Length, command.HostArgs.Count);
        }

        // The strict bind is not vacuous: one mistyped key is an error.
        var typo = new Dictionary<string, string?>(settings) { ["Cluster:Supervisor:Processes:world:HangTimeoutSecond"] = "120" };
        IConfigurationRoot typoConfiguration = new ConfigurationBuilder().AddInMemoryCollection(typo).Build();
        Assert.Throws<InvalidOperationException>(() => typoConfiguration.GetSection(ClusterSupervisorOptions.SectionName).Bind(new ClusterSupervisorOptions(), binder => binder.ErrorOnUnknownConfiguration = true));

        // The parser is not vacuous: a mistyped key is refused.
        Assert.Equal(DaemonCommandKind.UsageError, DaemonCommandLine.Parse(["--Cluster:Supervisor:Enabeld=false"], ConfigDaemon.World, ConfCatalog.Embedded).Kind);
    }

    // ---- start-cluster -Topology Split ------------------------------------------------------------------------------------------------

    private const string PgPassword = "pgPassSentinel-9876543210";

    private string WritePgSecretFile()
    {
        string path = Path.Combine(_root, "pg-secret.json");
        File.WriteAllText(path, $$"""{"Host":"db.example.invalid","Port":"5432","Username":"arcane_sentinel_user","Password":"{{PgPassword}}"}""", new UTF8Encoding(false));
        return path;
    }

    private static Dictionary<string, string> SplitWorldArguments(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in output.Split('\n'))
        {
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
                line.TrimEnd('\r'), "^ {6}Cluster__Supervisor__Processes__(worker-[A-Za-z0-9]+)__Arguments=(.*)$");
            if (match.Success)
            {
                result[match.Groups[1].Value] = match.Groups[2].Value;
            }
        }

        return result;
    }

    private static string ArgumentValue(string arguments, string key)
    {
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(arguments, "--" + System.Text.RegularExpressions.Regex.Escape(key) + "=(\\S+)");
        Assert.True(match.Success, $"{key} is not in: {arguments}");
        return match.Groups[1].Value;
    }

    [PwshFact]
    public void StartCluster_Plan_Split_ListsOneGatewayAndThreeWorlds_WithDisjointOwnedMaps_AndNoSecretValues()
    {
        BuildBin(("ArcaneCore.Kernel.dll", 0x107, "Debug"));
        (int code, string output, bool started) = RunLauncher("start-cluster.ps1", ["-Plan", "-Topology", "Split", "-PgSecretFile", $"'{Q(WritePgSecretFile())}'"], Secret);

        Assert.Equal(0, code);
        Assert.False(started, "the plan started a process");
        Assert.DoesNotContain(Secret, output, StringComparison.Ordinal);
        Assert.DoesNotContain(PgPassword, output, StringComparison.Ordinal);
        Assert.DoesNotContain("arcane_sentinel_user", output, StringComparison.Ordinal);
        Assert.DoesNotContain("db.example.invalid", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", output, StringComparison.Ordinal);
        Assert.Contains("      Database__Characters__Provider=PostgreSql", output, StringComparison.Ordinal);
        Assert.Contains("      Cluster__Supervisor__MinClusterProcesses=5", output, StringComparison.Ordinal);
        Assert.Contains("topology:   split: realm + gateway + 3 workers", output, StringComparison.Ordinal);

        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(output, "^ {6}Cluster__Supervisor__Processes__gateway__Role=gateway\\r?$", System.Text.RegularExpressions.RegexOptions.Multiline).Count);
        Dictionary<string, string> worlds = SplitWorldArguments(output);
        Assert.Equal(3, worlds.Count);
        Assert.True(worlds.ContainsKey("worker-EK") && worlds.ContainsKey("worker-Kalimdor") && worlds.ContainsKey("worker-instances"));

        var maps = new HashSet<uint>();
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var ports = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string key, string arguments) in worlds)
        {
            Assert.True(MapOwnership.TryParse(ArgumentValue(arguments, "Cluster:Worker:OwnedMaps"), true, out MapOwnership ownership, out string? error), error);
            Assert.False(ownership.OwnsEverything, key);
            foreach (MapOwnership.Range range in ownership.Ranges)
            {
                for (uint map = range.First; map <= range.Last; map++)
                {
                    Assert.True(maps.Add(map), $"map {map} is owned by two worlds");
                }
            }

            Assert.True(nodeIds.Add(ArgumentValue(arguments, "Cluster:Worker:NodeId")), $"{key}: NodeId is shared");
            Assert.Equal(ArgumentValue(arguments, "Cluster:Worker:NodeId"), ArgumentValue(arguments, "Cluster:Fencing:OwnerNode"));
            Assert.True(ports.Add(ArgumentValue(arguments, "Cluster:Worker:Port")), $"{key}: link port is shared");
            Assert.True(ports.Add(ArgumentValue(arguments, "World:Port")), $"{key}: client port is shared");
            Assert.Equal("true", ArgumentValue(arguments, "Cluster:Fencing:Enabled"));
        }

        // Only the lease holder is the map worker.
        Assert.Contains("--Cluster:Node:Role=MapWorker", worlds["worker-EK"], StringComparison.Ordinal);
        Assert.DoesNotContain("--Cluster:Node:Role", worlds["worker-Kalimdor"], StringComparison.Ordinal);
        Assert.DoesNotContain("--Cluster:Node:Role", worlds["worker-instances"], StringComparison.Ordinal);
        Assert.Contains("--Gateway:Workers:2:NodeId=worker:instances", output, StringComparison.Ordinal);
        Assert.Contains("--Gateway:LoginWorker=worker:EK", output, StringComparison.Ordinal);
    }

    [PwshFact]
    public void StartCluster_Plan_Split_OwnedMapsAreTheBridgeDefaults_AndTheEnvironmentValidates()
    {
        BuildBin(("ArcaneCore.Kernel.dll", 0x107, "Debug"));
        (int code, string output, _) = RunLauncher("start-cluster.ps1", ["-Plan", "-Topology", "Split", "-LeaseHolder", "Kalimdor", "-PgSecretFile", $"'{Q(WritePgSecretFile())}'"], Secret);
        Assert.Equal(0, code);

        // One table: the script's node ids and maps are the worker entries of the node bridge's default endpoints.
        Dictionary<string, string> worlds = SplitWorldArguments(output);
        int checkedWorkers = 0;
        foreach (string entry in ArcaneCore.World.Cluster.Tooling.ClusterNodeBridgeFeature.DefaultEndpointsSpec.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = entry.Split('|');
            if (parts[1] != "Worker")
            {
                continue;
            }

            string arguments = worlds.Values.Single(a => ArgumentValue(a, "Cluster:Worker:NodeId") == parts[0]);
            Assert.Equal(parts[3], ArgumentValue(arguments, "Cluster:Worker:OwnedMaps"));
            checkedWorkers++;
        }

        Assert.Equal(3, checkedWorkers);
        Assert.Contains("--Gateway:LoginWorker=worker:Kalimdor", output, StringComparison.Ordinal);
        Assert.Contains("MapWorker on worker:Kalimdor", output, StringComparison.Ordinal);

        // The supervisor accepts the environment: strict bind, per-child validation, and its own start-up checks (distinct owners, disjoint maps).
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in output.Split('\n'))
        {
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(line.TrimEnd('\r'), "^ {6}(Cluster__[A-Za-z0-9_-]+)=(.*)$");
            if (match.Success)
            {
                settings[match.Groups[1].Value.Replace("__", ":", StringComparison.Ordinal)] = match.Groups[2].Value;
            }
        }

        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = new ClusterSupervisorOptions();
        configuration.GetSection(ClusterSupervisorOptions.SectionName).Bind(options, binder => binder.ErrorOnUnknownConfiguration = true);
        Assert.Equal(5, options.Processes.Count);
        Assert.Equal(5, options.MinClusterProcesses);
        Assert.Empty(options.Validate());
        Assert.All(options.Processes.Values, process => process.Validate());
        Assert.DoesNotContain(new ClusterSupervisorConfigChecks().Check(configuration), issue => issue.Severity == ConfigSeverity.Error);
        Assert.Equal(1, options.Processes["worker-Kalimdor"].RealmId);
        Assert.Equal(0, options.Processes["worker-EK"].RealmId);
    }

    [PwshTheory]
    [InlineData("-Topology Split -MapWorker", "does not combine with -Topology Split")]
    [InlineData("-LeaseHolder EK", "only mean something with -Topology Split")]
    [InlineData("-Topology Split -LeaseHolder Nowhere", "Nowhere")]
    [InlineData("-Topology Split -WorkerPort 8086", "both 8086")]
    public void StartCluster_Split_RefusesBadParameters(string arguments, string message)
    {
        BuildBin(("ArcaneCore.Kernel.dll", 0x107, "Debug"));
        (int code, string output, bool started) = RunLauncher("start-cluster.ps1", ["-Plan", .. arguments.Split(' ')], Secret);
        Assert.NotEqual(0, code);
        Assert.Contains(message, output, StringComparison.Ordinal);
        Assert.False(started);
    }

    [PwshFact]
    public void StartCluster_Plan_Split_WarnsAboutAMissingPgSecretFile_WithoutPrintingAnything()
    {
        BuildBin(("ArcaneCore.Kernel.dll", 0x107, "Debug"));
        string missing = Path.Combine(_root, "no-such-secret.json");
        (int code, string output, bool started) = RunLauncher("start-cluster.ps1", ["-Plan", "-Topology", "Split", "-PgSecretFile", $"'{Q(missing)}'"], Secret);
        Assert.Equal(0, code);
        Assert.False(started);
        Assert.Contains("WARNING: the PostgreSQL secret file", output, StringComparison.Ordinal);
        Assert.Contains("a real run would refuse", output, StringComparison.Ordinal);
    }

    // ---- waiting for a supervised child that is relaunched during startup ---------------------------------------------------------------

    [PwshFact]
    public void WaitLiveDaemonUp_FollowRelaunch_WaitsForTheNextProcess_AndWithoutItAnEarlyExitThrows()
    {
        const string preamble = """
            $hide = if ($IsWindows) { @{ WindowStyle = 'Hidden' } } else { @{} } # -WindowStyle is Windows-only
            $quick = Start-Process pwsh -ArgumentList '-NoProfile','-Command','exit 3' -PassThru @hide
            $quick.WaitForExit()
            $long = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 120' -PassThru @hide
            $script:calls = 0
            function Find-LiveChildProcess { param($ParentPid, $ImageName) $script:calls++; if ($script:calls -eq 1) { return $quick } elseif ($script:calls -le 3) { return $null } else { return $long } }
            function Get-LivePortOwner { param($Port) if ($script:calls -ge 4) { return $long.Id } else { return 0 } }
            """;
        string followed = RunPwsh(preamble + """

            try {
                $up = Wait-LiveDaemonUp -Label 'child w' -ParentPid $PID -ImageName 'x.exe' -Port 4242 -TimeoutSec 30 -SettleSeconds 0 -FollowRelaunch
                "followed: $($up.Id -eq $long.Id) calls: $($script:calls)"
            } finally { $long.Kill() }
            """);
        Assert.Contains("followed: True calls: 4", followed, StringComparison.Ordinal);
        Assert.Contains("exited before it was up (exit code 3)", followed, StringComparison.Ordinal);
        Assert.Contains("its supervisor relaunches it", followed, StringComparison.Ordinal);

        string strict = RunPwsh(preamble + """

            try {
                Wait-LiveDaemonUp -Label 'daemon w' -ParentPid $PID -ImageName 'x.exe' -Port 4242 -TimeoutSec 30 -SettleSeconds 0
                'returned'
            } catch { "threw: $($_.Exception.Message)" } finally { $long.Kill() }
            """);
        Assert.Contains("threw: daemon w (pid", strict, StringComparison.Ordinal);
        Assert.Contains("exited before it was up (exit code 3)", strict, StringComparison.Ordinal);
        Assert.DoesNotContain("returned", strict, StringComparison.Ordinal);
    }

    [PwshFact]
    public void WaitLiveDaemonUp_FollowRelaunch_StillStopsWhenTheSupervisorIsGone()
    {
        string output = RunPwsh("""
            $hide = if ($IsWindows) { @{ WindowStyle = 'Hidden' } } else { @{} } # -WindowStyle is Windows-only
            $gone = Start-Process pwsh -ArgumentList '-NoProfile','-Command','exit 0' -PassThru @hide
            $gone.WaitForExit()
            function Find-LiveChildProcess { param($ParentPid, $ImageName) return $null }
            try { Wait-LiveDaemonUp -Label 'child w' -ParentPid $PID -ImageName 'x.exe' -Port 4242 -TimeoutSec 30 -SettleSeconds 0 -WatchPid $gone.Id -FollowRelaunch; 'returned' }
            catch { "threw: $($_.Exception.Message)" }
            """);
        Assert.Contains("threw: child w is not up and the process that launches it", output, StringComparison.Ordinal);
    }

    // ---- the debugger script ------------------------------------------------------------------------------------------------------------

    [PwshFact]
    public void AttachDebugger_RunTwiceInOneSession_EachRunLogsToItsOwnFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // a Win32 debugger
        }

        // The Add-Type'd type outlives the script in a 'pwsh -NoExit' window: a second run must not inherit the first run's shut-down queues
        // or its log path. Targets are throwaway pwsh sleepers started here.
        string script = Path.Combine(RepositorySource.RequireRoot(), "tools", "ops", "diag", "attach-debugger.ps1");
        string shared = Path.Combine(_root, "shared.log");
        string other = Path.Combine(_root, "other.log");
        string driver = Path.Combine(_root, "twice.ps1");
        File.WriteAllText(driver, $$"""
            $ErrorActionPreference = 'Stop'
            foreach ($log in '{{Q(shared)}}', '{{Q(shared)}}', '{{Q(other)}}') {
                $t = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 3' -PassThru -WindowStyle Hidden
                try { & '{{Q(script)}}' -WorldPid $t.Id -LogPath $log -LiveHome '{{Q(_home)}}' } finally { if (-not $t.HasExited) { $t.Kill() } }
            }
            """, new UTF8Encoding(false));
        var start = NewPwsh();
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(driver);
        (int code, string output) = Execute(start);
        Assert.True(code == 0, $"pwsh exited {code}: {output}");

        string sharedText = File.ReadAllText(shared);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(sharedText, "Successfully attached").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(sharedText, "EXIT_PROCESS_DEBUG_EVENT").Count);
        string otherText = File.ReadAllText(other);
        Assert.Contains("Successfully attached", otherText, StringComparison.Ordinal);
        Assert.Contains("EXIT_PROCESS_DEBUG_EVENT", otherText, StringComparison.Ordinal);
    }

    [PwshFact]
    public void AttachDebugger_TypeSource_ReAddsQuietlyWhenIdentical_AndFailsLoudlyWhenChangedUnderTheSameName()
    {
        string source = File.ReadAllText(Path.Combine(RepositorySource.RequireRoot(), "tools", "ops", "diag", "attach-debugger.ps1"));
        int begin = source.IndexOf("$code = @\"", StringComparison.Ordinal);
        Assert.True(begin >= 0);
        begin = source.IndexOf('\n', begin) + 1;
        int end = source.IndexOf("\n\"@", begin, StringComparison.Ordinal);
        string csharp = source[begin..end];
        string probe = Path.Combine(_root, "type-probe.ps1");
        File.WriteAllText(probe, "$code = @'\n" + csharp + "\n'@\n" + """
            Add-Type -TypeDefinition $code -Language CSharp
            Add-Type -TypeDefinition $code -Language CSharp
            'identical: ok'
            try { Add-Type -TypeDefinition $code.Replace('QueueCapacity = 8192', 'QueueCapacity = 4096') -Language CSharp; 'changed: no error' }
            catch { "changed: $($_.Exception.Message)" }
            """, new UTF8Encoding(false));
        var start = NewPwsh();
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(probe);
        (int code, string output) = Execute(start);
        Assert.Equal(0, code);
        Assert.Contains("identical: ok", output, StringComparison.Ordinal);
        Assert.Contains("changed: ", output, StringComparison.Ordinal);
        Assert.Contains("already exists", output, StringComparison.Ordinal);
        Assert.DoesNotContain("no error", output, StringComparison.Ordinal);
    }

    [PwshFact]
    public void AttachDebugger_AnUnopenableLogFile_IsReportedOnTheConsole_AndItsLinesAreCountedAsDropped()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string script = Path.Combine(RepositorySource.RequireRoot(), "tools", "ops", "diag", "attach-debugger.ps1");
        string directoryAsLog = Path.Combine(_root, "a-directory");
        Directory.CreateDirectory(directoryAsLog);
        string driver = Path.Combine(_root, "unopenable.ps1");
        File.WriteAllText(driver, $$"""
            $ErrorActionPreference = 'Stop'
            $t = Start-Process pwsh -ArgumentList '-NoProfile','-Command','Start-Sleep 3' -PassThru -WindowStyle Hidden
            try { & '{{Q(script)}}' -WorldPid $t.Id -LogPath '{{Q(directoryAsLog)}}' -LiveHome '{{Q(_home)}}' } finally { if (-not $t.HasExited) { $t.Kill() } }
            """, new UTF8Encoding(false));
        var start = NewPwsh();
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(driver);
        (int code, string output) = Execute(start);
        Assert.True(code == 0, $"pwsh exited {code}: {output}");
        Assert.Contains("cannot open the log file", output, StringComparison.Ordinal);
        System.Text.RegularExpressions.Match dropped = System.Text.RegularExpressions.Regex.Match(output, @"(\d+) console lines and (\d+) log-file lines dropped");
        Assert.True(dropped.Success, output);
        Assert.True(int.Parse(dropped.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) >= 3, output);
    }

    [Fact]
    public void AttachDebugger_LogsThroughBoundedQueues_AndNeverAttachesToTheSupervisor()
    {
        string source = File.ReadAllText(Path.Combine(RepositorySource.RequireRoot(), "tools", "ops", "diag", "attach-debugger.ps1"));
        Assert.DoesNotContain("LogLock", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.AppendAllText(logPath", source, StringComparison.Ordinal);
        Assert.Contains("new BlockingCollection<string>(QueueCapacity)", source, StringComparison.Ordinal);
        Assert.Contains("queue.TryAdd(line)", source, StringComparison.Ordinal); // never Add(): a full queue must not block a debug loop
        Assert.DoesNotContain("queue.Add(", source, StringComparison.Ordinal);
        Assert.Contains("lines dropped", source, StringComparison.Ordinal);
        // The only AttachAsync calls are the two daemon PIDs and the supervisor's children.
        Assert.DoesNotContain("AttachAsync($SupervisorPid", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public static class", source, StringComparison.Ordinal); // per-run instance state, not statics that outlive the script
        Assert.Contains("$currentStart -eq $supervisorStart", source, StringComparison.Ordinal);
        Assert.DoesNotContain("-as [type]", source, StringComparison.Ordinal); // no guard: a changed body under the same type name must fail loudly
        Assert.Contains("$debugLog.ProcessExited($childPid)", source, StringComparison.Ordinal);
        Assert.Contains("if ($childPid -eq $SupervisorPid) { continue }", source, StringComparison.Ordinal);
    }

    [PwshFact]
    public void AttachDebugger_EveryWatchAndWaitLoop_RunsInsideATryWhoseFinallyIsTheOnlyShutdown()
    {
        // A throw or Ctrl-C inside the loops must still stop the writer threads and write the drop totals: Shutdown is called once, from a
        // finally, and both the watch loop and the plain wait loop are inside that try. Read with the PowerShell parser, not by text.
        string script = Path.Combine(RepositorySource.RequireRoot(), "tools", "ops", "diag", "attach-debugger.ps1");
        string probe = Path.Combine(_root, "finally-probe.ps1");
        File.WriteAllText(probe, $$"""
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile('{{Q(script)}}', [ref]$null, [ref]$errors)
            if ($errors.Count -gt 0) { "parse errors: $($errors.Count)"; exit 1 }
            $isShutdown = { param($n) $n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and $n.Member.Value -eq 'Shutdown' }
            $isLoop = { param($n) $n -is [System.Management.Automation.Language.WhileStatementAst] }
            $isGuard = { param($n) $n -is [System.Management.Automation.Language.TryStatementAst] -and $null -ne $n.Finally -and @($n.Finally.FindAll($isShutdown, $true)).Count -gt 0 }
            "shutdown-calls: $(@($ast.FindAll($isShutdown, $true)).Count)"
            $guarded = @($ast.FindAll($isGuard, $true))
            "guarded-tries: $($guarded.Count)"
            if ($guarded.Count -eq 1) {
                "shutdown-in-finally: $(@($guarded[0].Finally.FindAll($isShutdown, $true)).Count)"
                "loops-in-guarded-try: $(@($guarded[0].Body.FindAll($isLoop, $true)).Count)"
                "loops-total: $(@($ast.FindAll($isLoop, $true)).Count)"
            }
            """, new UTF8Encoding(false));
        var start = NewPwsh();
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(probe);
        (int code, string output) = Execute(start);
        Assert.True(code == 0, $"pwsh exited {code}: {output}");
        Assert.Contains("shutdown-calls: 1", output, StringComparison.Ordinal);
        Assert.Contains("guarded-tries: 1", output, StringComparison.Ordinal);
        Assert.Contains("shutdown-in-finally: 1", output, StringComparison.Ordinal);
        Assert.Contains("loops-in-guarded-try: 2", output, StringComparison.Ordinal);
        Assert.Contains("loops-total: 2", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachDebugger_AStartTimeThatCannotBeRead_IsALiveSupervisor_NotAReusedPid()
    {
        string source = File.ReadAllText(Path.Combine(RepositorySource.RequireRoot(), "tools", "ops", "diag", "attach-debugger.ps1"));
        // Unreadable now (an elevated or exiting process) or at the start proves nothing about PID reuse: only two readable, different
        // start times end the watch.
        Assert.Contains("$supervisorUp = $null -ne $supervisorProcess -and ($null -eq $supervisorStart -or $null -eq $currentStart -or $currentStart -eq $supervisorStart)", source, StringComparison.Ordinal);
        Assert.Contains("function Get-ProcessStartTime($Process) { try { $Process.StartTime } catch { $null } }", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachDebugger_AReusedChildPid_StartsWithFreshRetries_BeforeTheRetryCapIsChecked()
    {
        // A relaunched child can get the PID of one that exited: the exit (or a vanished process) forgets its attempts, and that happens
        // before $tries is read, so the new process is attached instead of being skipped for having used up the old one's three tries.
        string source = File.ReadAllText(Path.Combine(RepositorySource.RequireRoot(), "tools", "ops", "diag", "attach-debugger.ps1"));
        int exited = source.IndexOf("$debugLog.ProcessExited($childPid)", StringComparison.Ordinal);
        int forget = source.IndexOf("$attached.Remove($childPid); $attempts.Remove($childPid); $known = $false", StringComparison.Ordinal);
        int tries = source.IndexOf("$tries = if ($attempts.ContainsKey($childPid))", StringComparison.Ordinal);
        int cap = source.IndexOf("if ($known -and $tries -ge 3) { continue }", StringComparison.Ordinal);
        Assert.True(exited >= 0 && forget > exited, "the exit check must precede the reset");
        Assert.True(tries > forget, "the retry count is read after the reset");
        Assert.True(cap > tries, "the cap applies to the fresh count");
        Assert.Contains("if ($known -and $attached[$childPid].IsAlive) { continue }", source, StringComparison.Ordinal);
    }

    // ---- same-name Add-Type guards --------------------------------------------------------------------------------------------------

    [PwshFact]
    public void LaunchDaemon_ConsoleModeHelper_AnOlderSessionsTypeWithADifferentBody_IsLeftAloneWithoutAnError()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // kernel32 console APIs
        }

        // launch-daemon.ps1 can run again in a 'pwsh -NoExit' window whose session already holds Win32.ConsoleModeHelper from an older body.
        // Add-Type of that name with a different member list is an error that -ErrorAction does not silence, so the script must skip the
        // Add-Type when the type exists. The older type is defined first; the guarded statement is cut out of the script, not copied.
        string source = File.ReadAllText(Path.Combine(LiveDir, "launch-daemon.ps1")).Replace("\r\n", "\n", StringComparison.Ordinal);
        int begin = source.IndexOf("if (-not ('Win32.ConsoleModeHelper' -as [type])) {", StringComparison.Ordinal);
        Assert.True(begin >= 0, "the Add-Type must sit behind the type-name guard");
        const string Terminator = "'@\n}";
        int end = source.IndexOf(Terminator, begin, StringComparison.Ordinal);
        Assert.True(end > begin);
        string statement = source[begin..(end + Terminator.Length)];
        string probe = Path.Combine(_root, "console-helper-probe.ps1");
        File.WriteAllText(probe, "Add-Type -Name ConsoleModeHelper -Namespace Win32 -MemberDefinition 'public static int Older() { return 1; }'\n$Error.Clear()\n" + statement + "\n" + """
            "errors: $($Error.Count)"
            "older: $([Win32.ConsoleModeHelper]::Older())"
            """, new UTF8Encoding(false));
        var start = NewPwsh();
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(probe);
        (int code, string output) = Execute(start);
        Assert.True(code == 0, $"pwsh exited {code}: {output}");
        Assert.Contains("errors: 0", output, StringComparison.Ordinal);
        Assert.Contains("older: 1", output, StringComparison.Ordinal);
    }

    [PwshFact]
    public void RedeployPlan_InitializePipeInterop_AnOlderSessionsTypeWithADifferentBody_ReturnsWithoutThrowing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // kernel32 pipe API
        }

        // The session already holds ArcaneCoreOps.PipeInterop from an older body (no GetNamedPipeServerProcessId). Initialize-PipeInterop
        // must return on the type-name guard instead of running Add-Type, which would throw 'type name already exists'.
        string library = Path.Combine(LiveDir, "RedeployPlan.ps1");
        string probe = Path.Combine(_root, "pipe-interop-probe.ps1");
        File.WriteAllText(probe, $$"""
            $ErrorActionPreference = 'Stop'
            Add-Type -Namespace ArcaneCoreOps -Name PipeInterop -MemberDefinition 'public static int Older() { return 1; }'
            . '{{Q(library)}}'
            Initialize-PipeInterop
            Initialize-PipeInterop
            "older: $([ArcaneCoreOps.PipeInterop]::Older())"
            'guard: ok'
            """, new UTF8Encoding(false));
        var start = NewPwsh();
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(probe);
        (int code, string output) = Execute(start);
        Assert.True(code == 0, $"pwsh exited {code}: {output}");
        Assert.Contains("older: 1", output, StringComparison.Ordinal);
        Assert.Contains("guard: ok", output, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDaemonEntryPoint_InstallsTheCrashHandler_BeforeTheHostIsBuilt()
    {
        // The crash hooks (and with them the crash dialog) exist only where Program.cs calls the diagnostics wiring, and it must come
        // before builder.Build() so a failure while the host is built is already reported. The World wraps the Kernel call in
        // UseWorldDiagnostics, which is itself pinned to call UseArcaneDiagnostics (the Kernel's own tests pin that one to CrashHandler.Install).
        (string Program, string Call)[] entries =
        [
            ("src/ArcaneCore.Realm/Program.cs", "UseArcaneDiagnostics(builder)"),
            ("src/ArcaneCore.Gateway/Program.cs", "UseArcaneDiagnostics(builder)"),
            ("src/ArcaneCore.World/Program.cs", "UseWorldDiagnostics(builder)"),
        ];
        foreach ((string program, string call) in entries)
        {
            string text = File.ReadAllText(Path.Combine(RepositorySource.RequireRoot(), program.Replace('/', Path.DirectorySeparatorChar)));
            int install = text.IndexOf(call, StringComparison.Ordinal);
            int build = text.IndexOf("builder.Build()", StringComparison.Ordinal);
            Assert.True(install >= 0, program + " must call " + call);
            Assert.True(build > install, program + " must install diagnostics before builder.Build()");
        }

        string world = File.ReadAllText(Path.Combine(RepositorySource.RequireRoot(), "src", "ArcaneCore.World", "Ops", "Diagnostics", "WorldDiagnosticsExtensions.cs"));
        Assert.Contains("builder.UseArcaneDiagnostics();", world, StringComparison.Ordinal);
        string hosting = File.ReadAllText(Path.Combine(RepositorySource.RequireRoot(), "src", "ArcaneCore.Kernel", "Diagnostics", "DiagnosticsHostingExtensions.cs"));
        Assert.Contains("CrashHandler.Install(options, StandardErrorCrashSink.Instance);", hosting, StringComparison.Ordinal);
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
            # A refusal is printed as its plain message: the default error view word-wraps it to the console width (80 on Linux CI),
            # which splits the text the tests look for.
            try { & '{{Q(Path.Combine(LiveDir, script))}}' {{string.Join(' ', arguments)}} }
            catch { Write-Output ('ERROR: ' + $_.Exception.Message); exit 1 }
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

    /// <summary>Dot-sources LiveLaunch.ps1, runs the script body in the same scope (so it can redefine its helper functions) and returns everything it wrote.</summary>
    private string RunPwsh(string body)
    {
        string script = Path.Combine(_root, "run-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(script, "$ErrorActionPreference = 'Stop'\n. '" + Q(Path.Combine(LiveDir, "LiveLaunch.ps1")) + "'\n" + body, new UTF8Encoding(false));
        var start = NewPwsh();
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        (int code, string output) = Execute(start);
        Assert.True(code == 0, $"pwsh exited {code}: {output}");
        return output;
    }

    /// <summary>Splits a Windows command line the way the C runtime does (quotes group, backslashes before a quote escape it).</summary>
    private static string[] SplitWindowsCommandLine(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        bool started = false;
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '\\')
            {
                int slashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    slashes++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else
                {
                    current.Append('\\', slashes);
                    i--;
                }

                started = true;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (started)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (started)
        {
            result.Add(current.ToString());
        }

        return [.. result];
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
