using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.World.Cluster.Supervision;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Tests.Ops;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

/// <summary>
/// Audit findings 34, 47 and 48 on the host supervisor: a failing child log file must not stop the stdout pump, the default drain timeout
/// must outlast the world's own stop sequence, and a drain-timeout kill must be visible.
/// </summary>
public sealed class ClusterSupervisorAuditTests : IDisposable
{
    private const string LogFaultSite = "cluster.supervisor.log-file";
    private readonly string _tempDir;

    public ClusterSupervisorAuditTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ArcaneCore.AuditTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // A late handle on Windows: the temp directory is not the subject of the test.
        }
    }

    private static long FaultCount(string site) => FaultLedger.Shared.Snapshot().Where(f => f.Site == site).Sum(f => f.Count);

    /// <summary>A path whose open always fails: it names an existing directory, so FileStream throws UnauthorizedAccessException (Windows) or IOException/UnauthorizedAccess (Linux).</summary>
    private string UnopenableLogPath() => _tempDir;

    // ---- 34 ----

    [Fact]
    public void RouteLine_WhenTheLogFileCannotBeOpened_StillWritesTheConsole_AndRecordsAFault()
    {
        var console = new StringWriter();
        using var mux = new SupervisedOutputMultiplexer(console);
        var config = new SupervisedProcessConfig { Name = "world", ExecutablePath = "x", LogFilePath = UnopenableLogPath() };
        long before = FaultCount(LogFaultSite);

        mux.RouteLine(config, "hello");

        Assert.Contains("[world] hello", console.ToString(), StringComparison.Ordinal);
        Assert.True(FaultCount(LogFaultSite) > before, "the failed log write must be recorded as a fault");
    }

    [Fact]
    public async Task ChildOutput_WhenTheLogFileFails_DoesNotThrowIntoTheEmitter_AndLaterLinesStillArrive()
    {
        var launcher = new ScriptedLauncher();
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(configure: c => c.LogFilePath = UnopenableLogPath()), launcher, holdAllDelays: true);
        await rig.Supervisor.StartProcessAsync("world");
        ScriptedProcess child = launcher.Last;

        child.Emit("first");
        child.Emit("second");

        Assert.Contains("[world] first", rig.ConsoleText, StringComparison.Ordinal);
        Assert.Contains("[world] second", rig.ConsoleText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealPump_WhenTheLineHandlerThrows_KeepsReading_AndTheWaitForExitDoesNotFault()
    {
        var launcher = new SystemProcessSupervisedLauncher();
        var config = new SupervisedProcessConfig
        {
            Name = "fixture",
            Role = SupervisedRoles.Worker,
            ExecutablePath = ConfProcess.DotnetMuxer(),
            Arguments = $"\"{Path.Combine(AppContext.BaseDirectory, "SupervisedChildFixture.dll")}\" --lifetime-ms 1500",
            Console = SupervisedConsoleMode.None,
            DrainTimeoutMs = 500,
            RequiredMemoryMb = 1,
        };
        int lines = 0;
        ISupervisedProcess process = launcher.Launch(config, (_, _, _) =>
        {
            Interlocked.Increment(ref lines);
            throw new UnauthorizedAccessException("the child log is read-only");
        });
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            int exit = await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, exit);
            Assert.True(Volatile.Read(ref lines) >= 1, "the fixture's 'ready' line must have reached the handler");
        }
        finally
        {
            process.KillOwnedProcess();
            process.Dispose();
        }
    }

    // ---- 47 ----

    [Fact]
    public void DefaultDrainTimeout_ExceedsTheWorldStopSequence()
    {
        // WorldHost.StopCoreAsync: playerbot war wipe wait 10 s, feature lane stall 10 s, save lane stall 15 s, packet capture drain 10 s; the bot stop itself is unbounded.
        TimeSpan bounded = TimeSpan.FromSeconds(10) + StopDrainBudget.DefaultStallBudget + ShutdownDrain.SavesStallBudget + TimeSpan.FromSeconds(10);
        TimeSpan margin = TimeSpan.FromSeconds(30);

        var config = new SupervisedProcessConfig();

        Assert.True(TimeSpan.FromMilliseconds(config.DrainTimeoutMs) >= bounded + margin,
            $"default DrainTimeoutMs {config.DrainTimeoutMs} ms must be at least the bounded world stop {bounded.TotalSeconds:0} s plus {margin.TotalSeconds:0} s");
    }

    // ---- 48 ----

    [Fact]
    public async Task DrainTimeoutKill_IsReportedAsAnAnomaly_AndShownOnTheConsole()
    {
        var launcher = new ScriptedLauncher { Configure = (_, p) => p.Drain = DrainBehaviour.IgnoresDrain };
        await using var rig = new SupervisorRig(SupervisorRig.OneProcess(configure: c => c.DrainTimeoutMs = 1_000), launcher, holdAllDelays: true);
        await rig.Supervisor.StartProcessAsync("world");
        long before = AnomalyLedger.Shared.CountOf(AnomalyLedger.KeyOf("cluster", "supervisor-drain-timeout-kill"));

        await rig.Supervisor.DrainProcessAsync("world");

        Assert.Equal(1, launcher.Last.Kills);
        // The ledger is process-wide and other supervisor tests drain children that ignore the drain in parallel, so only a lower bound is race-free.
        Assert.True(AnomalyLedger.Shared.CountOf(AnomalyLedger.KeyOf("cluster", "supervisor-drain-timeout-kill")) >= before + 1);
        Assert.Contains("did not exit within its drain timeout", rig.ConsoleText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DrainTimeoutKill_IsLoggedAsAWarning_AndEachExitCodeIsLogged()
    {
        var log = new CaptureLogger();
        var launcher = new ScriptedLauncher { Configure = (_, p) => p.Drain = DrainBehaviour.IgnoresDrain };
        var options = SupervisorRig.OneProcess(configure: c => c.DrainTimeoutMs = 1_000);
        var console = new StringWriter();
        using var mux = new SupervisedOutputMultiplexer(console);
        var delays = new ManualDelays { AutoComplete = _ => false };
        await using var supervisor = new ClusterHostSupervisor(options, launcher, mux, new FixedMemoryProbe(), delays.Delay, new ManualClock(), logger: log);
        await supervisor.StartProcessAsync("world");

        await supervisor.DrainProcessAsync("world");

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("killed", StringComparison.Ordinal) && e.Message.Contains("world", StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("exited with code -1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnexpectedExit_IsLoggedWithItsCode()
    {
        var log = new CaptureLogger();
        var launcher = new ScriptedLauncher();
        var options = SupervisorRig.OneProcess();
        var delays = new ManualDelays { AutoComplete = _ => false };
        await using var supervisor = new ClusterHostSupervisor(options, launcher, new SupervisedOutputMultiplexer(new StringWriter()), new FixedMemoryProbe(), delays.Delay, new ManualClock(), logger: log);
        await supervisor.StartProcessAsync("world");

        launcher.Last.ExitWith(ExitCodes.Failure);
        await delays.ReleaseUntilAsync(() => launcher.Launches >= 2 || log.Entries.Any(e => e.Message.Contains("exited with code", StringComparison.Ordinal)), "the exit to be logged");

        Assert.Contains(log.Entries, e => e.Message.Contains($"exited with code {ExitCodes.Failure}", StringComparison.Ordinal));
    }

    [Fact]
    public void Warnings_ForAWorldChildWithADrainTimeoutBelowTheStopBudget_NameTheKey()
    {
        var low = new ClusterSupervisorOptions { Enabled = true, MinClusterProcesses = 1 };
        low.Processes["world"] = new SupervisedProcessConfig { Name = "world", Role = SupervisedRoles.Combined, ExecutablePath = "x", DrainTimeoutMs = 30_000 };
        low.Processes["realm"] = new SupervisedProcessConfig { Name = "realm", Role = SupervisedRoles.Realm, ExecutablePath = "x", DrainTimeoutMs = 30_000 };

        string warning = Assert.Single(low.Warnings());

        Assert.Contains("Processes:world:DrainTimeoutMs", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Warnings_ForTheDefaultDrainTimeout_AreEmpty()
    {
        var options = new ClusterSupervisorOptions { Enabled = true, MinClusterProcesses = 1 };
        options.Processes["world"] = new SupervisedProcessConfig { Name = "world", Role = SupervisedRoles.Worker, ExecutablePath = "x" };

        Assert.Empty(options.Warnings());
        Assert.True(TimeSpan.FromMilliseconds(SupervisedProcessConfig.DefaultDrainTimeoutMs) > WorldStopBudget.Recommended);
    }

    // ---- 35 ----

    [Fact]
    public void Probe_UsesTheOsReading_AndReadsItAgainOnEveryCall()
    {
        long bytes = 4096L * 1024 * 1024;
        var probe = new SystemClusterCapacityProbe(() => bytes, () => (0, 0));

        Assert.Equal(4096, probe.GetAvailableMemoryMb());
        bytes = 512L * 1024 * 1024;
        Assert.Equal(512, probe.GetAvailableMemoryMb());
        Assert.Equal("os", probe.Source);
    }

    [Fact]
    public void Probe_WithoutAnOsReading_FallsBackToTheGcSnapshot_AndSaysSo()
    {
        var probe = new SystemClusterCapacityProbe(() => null, () => (16384L * 1024 * 1024, 15000L * 1024 * 1024));

        Assert.Equal(1384, probe.GetAvailableMemoryMb());
        Assert.Equal("gc-estimate", probe.Source);
    }

    [Fact]
    public void Probe_WithNoDataAtAll_ReportsUnknown_NotAMadeUpFigure()
    {
        var probe = new SystemClusterCapacityProbe(() => null, () => (0, 0));

        Assert.Equal(SystemClusterCapacityProbe.UnknownAvailableMb, probe.GetAvailableMemoryMb());
        Assert.Equal("unknown", probe.Source);
    }

    [Fact]
    public void Probe_WithAGcTotalButNoLoad_DoesNotTreatTheWholeRamAsFree()
    {
        var probe = new SystemClusterCapacityProbe(() => null, () => (16384L * 1024 * 1024, 0));

        Assert.Equal(SystemClusterCapacityProbe.UnknownAvailableMb, probe.GetAvailableMemoryMb());
        Assert.Equal("unknown", probe.Source);
    }

    [Fact]
    public void HostMemory_ParsesMemAvailableFromProcMeminfo()
    {
        const string text = "MemTotal:       32768000 kB\nMemFree:         1000000 kB\nMemAvailable:   20480000 kB\nBuffers:          100 kB\n";

        Assert.Equal(20480000L * 1024, HostMemory.ParseMemAvailableBytes(text));
        Assert.Null(HostMemory.ParseMemAvailableBytes("MemTotal: 1 kB\n"));
        Assert.Null(HostMemory.ParseMemAvailableBytes("MemAvailable: lots kB\n"));
    }

    [Fact]
    public void HostMemory_OnWindowsAndLinux_ReportsAPlausibleFigure()
    {
        long? bytes = HostMemory.TryGetAvailableBytes();
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.NotNull(bytes);
        Assert.InRange(bytes.Value, 1L, 64L * 1024 * 1024 * 1024 * 1024);
        Assert.True(new SystemClusterCapacityProbe().GetAvailableMemoryMb() > 0);
    }

    private sealed class CaptureLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
