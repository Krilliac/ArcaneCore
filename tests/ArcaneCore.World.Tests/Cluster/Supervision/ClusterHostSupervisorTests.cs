using System.Collections.Concurrent;
using System.IO;
using ArcaneCore.World.Cluster.Supervision;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

public sealed class ClusterHostSupervisorTests : IDisposable
{
    private readonly string _tempDir;

    public ClusterHostSupervisorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ArcaneCore.SupervisorTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup locks on Windows temp files.
        }
    }

    [Fact]
    public void Multiplexer_MergedMode_PrefixesStdoutAndStderr_AndWritesLogFile()
    {
        string logPath = Path.Combine(_tempDir, "worker-ek.log");
        var consoleWriter = new StringWriter();
        var cfg = new SupervisedProcessConfig
        {
            Name = "worker:EK",
            Role = SupervisedRoles.Worker,
            ExecutablePath = "ArcaneCore.World.exe",
            Console = SupervisedConsoleMode.Merged,
            LogFilePath = logPath,
        };

        using (var mux = new SupervisedOutputMultiplexer(consoleWriter))
        {
            mux.RouteLine(cfg, "Loaded 1423 creature spawns", isStdErr: false);
            mux.RouteLine(cfg, "Socket warning on port 8085", isStdErr: true);
        }

        string consoleText = consoleWriter.ToString();
        Assert.Contains("[worker:EK] Loaded 1423 creature spawns", consoleText);
        Assert.Contains("[worker:EK] Socket warning on port 8085", consoleText);

        string fileText = File.ReadAllText(logPath);
        Assert.Contains("[worker:EK] Loaded 1423 creature spawns", fileText);
        Assert.Contains("[worker:EK] Socket warning on port 8085", fileText);
    }

    [Theory]
    [InlineData(SupervisedConsoleMode.None)]
    [InlineData(SupervisedConsoleMode.Window)]
    public void Multiplexer_NoneAndWindowModes_SuppressOrdinaryInfoFromMergedConsole_ButSurfaceErrorsAndStderr(
        SupervisedConsoleMode mode)
    {
        string logPath = Path.Combine(_tempDir, $"worker-{mode}.log");
        var consoleWriter = new StringWriter();
        var cfg = new SupervisedProcessConfig
        {
            Name = "worker:Kalimdor",
            Role = SupervisedRoles.Worker,
            ExecutablePath = "ArcaneCore.World.exe",
            Console = mode,
            LogFilePath = logPath,
        };

        using (var mux = new SupervisedOutputMultiplexer(consoleWriter))
        {
            mux.RouteLine(cfg, "[INFO] Map 1 tick completed in 4ms", isStdErr: false);
            mux.RouteLine(cfg, "[ERROR] Failed to reserve item block", isStdErr: false);
            mux.RouteLine(cfg, "Unhandled exception: System.InvalidOperationException: Boom", isStdErr: false);
            mux.RouteLine(cfg, "Native stderr diagnostic line", isStdErr: true);
        }

        string consoleText = consoleWriter.ToString();
        Assert.DoesNotContain("Map 1 tick completed in 4ms", consoleText);
        Assert.Contains("[worker:Kalimdor] [ERROR] Failed to reserve item block", consoleText);
        Assert.Contains("[worker:Kalimdor] Unhandled exception: System.InvalidOperationException: Boom", consoleText);
        Assert.Contains("[worker:Kalimdor] Native stderr diagnostic line", consoleText);

        // Log file receives every line regardless of console mode.
        string fileText = File.ReadAllText(logPath);
        Assert.Contains("[worker:Kalimdor] [INFO] Map 1 tick completed in 4ms", fileText);
        Assert.Contains("[worker:Kalimdor] [ERROR] Failed to reserve item block", fileText);
        Assert.Contains("[worker:Kalimdor] Unhandled exception: System.InvalidOperationException: Boom", fileText);
        Assert.Contains("[worker:Kalimdor] Native stderr diagnostic line", fileText);
    }

    [Fact]
    public void SystemProcessSupervisedLauncher_BuildStartInfo_RespectsConsoleModeWindowSetting()
    {
        var mergedCfg = new SupervisedProcessConfig
        {
            Name = "worker:EK",
            Role = SupervisedRoles.Worker,
            ExecutablePath = "dotnet",
            Arguments = "--info",
            Console = SupervisedConsoleMode.Merged,
        };
        var windowCfg = new SupervisedProcessConfig
        {
            Name = "worker:Kalimdor",
            Role = SupervisedRoles.Worker,
            ExecutablePath = "dotnet",
            Arguments = "--info",
            Console = SupervisedConsoleMode.Window,
        };
        var noneCfg = new SupervisedProcessConfig
        {
            Name = "social",
            Role = SupervisedRoles.Social,
            ExecutablePath = "dotnet",
            Arguments = "--info",
            Console = SupervisedConsoleMode.None,
        };

        Assert.True(SystemProcessSupervisedLauncher.BuildStartInfo(mergedCfg).CreateNoWindow);
        Assert.False(SystemProcessSupervisedLauncher.BuildStartInfo(windowCfg).CreateNoWindow);
        Assert.True(SystemProcessSupervisedLauncher.BuildStartInfo(noneCfg).CreateNoWindow);
    }

    [Fact]
    public void ComputeBackoffDelayMs_FollowsCappedExponentialSchedule_1sTo60s()
    {
        Assert.Equal(1_000, ClusterSupervisorOptions.ComputeBackoffDelayMs(1));
        Assert.Equal(2_000, ClusterSupervisorOptions.ComputeBackoffDelayMs(2));
        Assert.Equal(4_000, ClusterSupervisorOptions.ComputeBackoffDelayMs(3));
        Assert.Equal(8_000, ClusterSupervisorOptions.ComputeBackoffDelayMs(4));
        Assert.Equal(16_000, ClusterSupervisorOptions.ComputeBackoffDelayMs(5));
        Assert.Equal(32_000, ClusterSupervisorOptions.ComputeBackoffDelayMs(6));
        Assert.Equal(60_000, ClusterSupervisorOptions.ComputeBackoffDelayMs(7));
        Assert.Equal(60_000, ClusterSupervisorOptions.ComputeBackoffDelayMs(20));
    }

    [Fact]
    public async Task UnexpectedExit_TriggersCappedExponentialBackoff_AndIncrementsIncarnationAndRestartCount()
    {
        var recordedDelays = new ConcurrentQueue<int>();
        var launcher = new FakeSupervisedProcessLauncher();
        var probe = new FakeCapacityProbe(availableMemoryMb: 16_384);

        var options = new ClusterSupervisorOptions();
        var workerCfg = new SupervisedProcessConfig
        {
            Name = "worker:EK",
            Role = SupervisedRoles.Worker,
            ExecutablePath = "ArcaneCore.World.exe",
            Console = SupervisedConsoleMode.None,
            RestartBackoffMinMs = 1_000,
            RestartBackoffMaxMs = 60_000,
            HangTimeoutSeconds = 0, // this test records every delay the supervisor asks for: the hang watch would add its own
            MapAssignments = [0],
        };
        options.Processes[workerCfg.Name] = workerCfg;

        var consoleWriter = new StringWriter();
        using var mux = new SupervisedOutputMultiplexer(consoleWriter);
        await using var supervisor = new ClusterHostSupervisor(
            options,
            launcher,
            mux,
            probe,
            delayAsync: (ts, _) =>
            {
                recordedDelays.Enqueue((int)ts.TotalMilliseconds);
                return Task.CompletedTask;
            });

        FakeSupervisedProcess firstProc = launcher.PreCreateProcess(index: 0);
        FakeSupervisedProcess secondProc = launcher.PreCreateProcess(index: 1);
        FakeSupervisedProcess thirdProc = launcher.PreCreateProcess(index: 2);
        FakeSupervisedProcess fourthProc = launcher.PreCreateProcess(index: 3);

        await supervisor.StartProcessAsync("worker:EK");
        await firstProc.WaitForStartSignalAsync();

        var snap1 = supervisor.GetSnapshot("worker:EK");
        Assert.Equal(1, snap1.Incarnation);
        Assert.Equal(0, snap1.RestartCount);

        firstProc.SimulateUnexpectedExit(exitCode: 1);
        await secondProc.WaitForStartSignalAsync();

        secondProc.SimulateUnexpectedExit(exitCode: 1);
        await thirdProc.WaitForStartSignalAsync();

        thirdProc.SimulateUnexpectedExit(exitCode: 1);
        await fourthProc.WaitForStartSignalAsync();

        var snap4 = supervisor.GetSnapshot("worker:EK");
        Assert.Equal(4, snap4.Incarnation);
        Assert.Equal(3, snap4.RestartCount);
        Assert.Equal([1_000, 2_000, 4_000], recordedDelays.ToArray());

        // Even in None console mode, unexpected crash/restart errors must be surfaced in the merged console.
        Assert.Contains("[worker:EK] ERROR: Process 'worker:EK'", consoleWriter.ToString());

        await supervisor.DrainAndStopAsync();
        Assert.Equal(SupervisedProcessState.Drained, supervisor.GetSnapshot("worker:EK").State);
    }

    [Fact]
    public async Task CapacityAdmission_RefusesProcess_WhenMemoryOrProcessCeilingOrMapOwnershipViolated()
    {
        var launcher = new FakeSupervisedProcessLauncher();
        var probe = new FakeCapacityProbe(availableMemoryMb: 2_048);

        var options = new ClusterSupervisorOptions
        {
            MaxConcurrentProcesses = 2,
            MaxTotalAllocatedMemoryMb = 1_500,
            MinFreeMemoryReserveMb = 1_024,
        };

        var workerEk = new SupervisedProcessConfig
        {
            Name = "worker:EK",
            Role = SupervisedRoles.Worker,
            ExecutablePath = "ArcaneCore.World.exe",
            RequiredMemoryMb = 800,
            MapAssignments = [0, 30],
        };
        var workerDuplicateMap = new SupervisedProcessConfig
        {
            Name = "worker:Duplicate",
            Role = SupervisedRoles.Worker,
            ExecutablePath = "ArcaneCore.World.exe",
            RequiredMemoryMb = 200,
            MapAssignments = [0],
        };
        var workerOverBudget = new SupervisedProcessConfig
        {
            Name = "worker:OverBudget",
            Role = SupervisedRoles.Worker,
            ExecutablePath = "ArcaneCore.World.exe",
            RequiredMemoryMb = 900,
            MapAssignments = [1],
        };
        var workerKalimdor = new SupervisedProcessConfig
        {
            Name = "worker:Kalimdor",
            Role = SupervisedRoles.Worker,
            ExecutablePath = "ArcaneCore.World.exe",
            RequiredMemoryMb = 500,
            MapAssignments = [1],
        };
        var socialThirdProc = new SupervisedProcessConfig
        {
            Name = "social",
            Role = SupervisedRoles.Social,
            ExecutablePath = "ArcaneCore.World.exe",
            RequiredMemoryMb = 100,
        };

        options.Processes[workerEk.Name] = workerEk;
        options.Processes[workerDuplicateMap.Name] = workerDuplicateMap;
        options.Processes[workerOverBudget.Name] = workerOverBudget;
        options.Processes[workerKalimdor.Name] = workerKalimdor;
        options.Processes[socialThirdProc.Name] = socialThirdProc;

        using var mux = new SupervisedOutputMultiplexer(TextWriter.Null);
        await using var supervisor = new ClusterHostSupervisor(
            options,
            launcher,
            mux,
            probe);

        await supervisor.StartProcessAsync("worker:EK");

        // 1. Duplicate map ownership must be refused.
        CapacityAdmissionResult dupResult = supervisor.CheckAdmission(workerDuplicateMap);
        Assert.False(dupResult.Allowed);
        Assert.Contains("map 0", dupResult.Reason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.StartProcessAsync("worker:Duplicate"));

        // 2. Allocated memory budget (800 + 900 > 1500) must be refused.
        CapacityAdmissionResult memBudgetResult = supervisor.CheckAdmission(workerOverBudget);
        Assert.False(memBudgetResult.Allowed);
        Assert.Contains("MaxTotalAllocatedMemoryMb", memBudgetResult.Reason);

        // 3. Free host memory reserve violation must be refused.
        probe.AvailableMemoryMb = 1_200; // 1200 - 500 = 700 < 1024 reserve
        CapacityAdmissionResult reserveResult = supervisor.CheckAdmission(workerKalimdor);
        Assert.False(reserveResult.Allowed);
        Assert.Contains("MinFreeMemoryReserveMb", reserveResult.Reason);

        // Restore host memory and start second worker.
        probe.AvailableMemoryMb = 8_192;
        await supervisor.StartProcessAsync("worker:Kalimdor");

        // 4. MaxConcurrentProcesses (2 active) must refuse 3rd process.
        CapacityAdmissionResult countResult = supervisor.CheckAdmission(socialThirdProc);
        Assert.False(countResult.Allowed);
        Assert.Contains("MaxConcurrentProcesses", countResult.Reason);

        // 5. Worker map placement helpers resolve and assign maps deterministically.
        Assert.Equal("worker:EK", supervisor.ResolveWorkerForMap(0)?.Name);
        Assert.Equal("worker:Kalimdor", supervisor.ResolveWorkerForMap(1)?.Name);
        Assert.Equal("worker:Kalimdor", supervisor.AssignEmptyMapToLeastLoadedWorker(530).Name);
        Assert.Equal("worker:Kalimdor", supervisor.ResolveWorkerForMap(530)?.Name);
    }

    [Fact]
    public async Task DrainAndStopAsync_DrainsRealOwnedSubprocessCleanly_WithoutRestarting()
    {
        string logPath = Path.Combine(_tempDir, "real-subproc.log");
        var consoleWriter = new StringWriter();

        var options = new ClusterSupervisorOptions();
        var procCfg = new SupervisedProcessConfig
        {
            Name = "worker:Instances",
            Role = SupervisedRoles.Worker,
            ExecutablePath = "dotnet",
            Arguments = "--info",
            Console = SupervisedConsoleMode.Merged,
            LogFilePath = logPath,
            DrainTimeoutMs = 5_000,
            RequiredMemoryMb = 64,
            MapAssignments = [36, 43],
        };
        options.Processes[procCfg.Name] = procCfg;

        using var mux = new SupervisedOutputMultiplexer(consoleWriter);
        await using var supervisor = new ClusterHostSupervisor(
            options,
            new SystemProcessSupervisedLauncher(),
            mux,
            new FakeCapacityProbe(availableMemoryMb: 16_384));

        await supervisor.StartAllAsync();
        await supervisor.DrainAndStopAsync();

        var snapshot = supervisor.GetSnapshot("worker:Instances");
        Assert.Equal(SupervisedProcessState.Drained, snapshot.State);
        Assert.Equal(1, snapshot.Incarnation);
        Assert.Equal(0, snapshot.RestartCount);
    }

    [Fact]
    public void PlanStartAll_AccumulatesCountAndMemory_AcrossCandidates()
    {
        var options = new ClusterSupervisorOptions
        {
            MaxConcurrentProcesses = 4,
            MaxTotalAllocatedMemoryMb = 700,
            MinFreeMemoryReserveMb = 1_000,
        };
        options.Processes["realm"] = new SupervisedProcessConfig { Name = "realm", Role = SupervisedRoles.Realm, ExecutablePath = "x", RequiredMemoryMb = 300, StartOrder = 0 };
        options.Processes["gateway"] = new SupervisedProcessConfig { Name = "gateway", Role = SupervisedRoles.Gateway, ExecutablePath = "x", RequiredMemoryMb = 300, StartOrder = 1 };
        options.Processes["w-a"] = new SupervisedProcessConfig { Name = "w-a", Role = SupervisedRoles.Worker, ExecutablePath = "x", RequiredMemoryMb = 200, MapAssignments = [0], StartOrder = 2 };
        options.Processes["w-b"] = new SupervisedProcessConfig { Name = "w-b", Role = SupervisedRoles.Worker, ExecutablePath = "x", RequiredMemoryMb = 10, MapAssignments = [0], StartOrder = 3 };
        options.Processes["w-c"] = new SupervisedProcessConfig { Name = "w-c", Role = SupervisedRoles.Worker, ExecutablePath = "x", RequiredMemoryMb = 10, MapAssignments = [0], StartOrder = 4 };
        options.Processes["w-d"] = new SupervisedProcessConfig { Name = "w-d", Role = SupervisedRoles.Worker, ExecutablePath = "x", RequiredMemoryMb = 10, MapAssignments = [1], StartOrder = 5 };
        options.Processes["w-e"] = new SupervisedProcessConfig { Name = "w-e", Role = SupervisedRoles.Worker, ExecutablePath = "x", RequiredMemoryMb = 10, MapAssignments = [2], StartOrder = 6 };

        using var mux = new SupervisedOutputMultiplexer(TextWriter.Null);
        using var supervisor = new ClusterHostSupervisor(options, new FakeSupervisedProcessLauncher(), mux, new FakeCapacityProbe(availableMemoryMb: 100_000));

        SupervisorStartPlan plan = supervisor.PlanStartAll();

        // realm + gateway use 600 of 700 MiB; w-a (200) would reach 800 and is denied, so its map 0 stays free for w-b; w-c wants map 0 again;
        // w-d is the fourth process; w-e would be the fifth.
        Assert.Equal(["realm", "gateway", "w-b", "w-d"], plan.Admitted);
        Assert.Equal(["w-a", "w-c", "w-e"], plan.Denied.Select(d => d.Name));
        Assert.Contains("MaxTotalAllocatedMemoryMb", plan.Denied[0].Reason);
        Assert.Contains("map 0", plan.Denied[1].Reason);
        Assert.Contains("MaxConcurrentProcesses", plan.Denied[2].Reason);

        // Planning launches nothing.
        Assert.All(supervisor.GetSnapshots(), s => Assert.Equal(SupervisedProcessState.Stopped, s.State));

        // The free-memory reserve is applied cumulatively against the one probe read.
        var reserve = new ClusterSupervisorOptions { MinFreeMemoryReserveMb = 500 };
        reserve.Processes["one"] = new SupervisedProcessConfig { Name = "one", Role = SupervisedRoles.Realm, ExecutablePath = "x", RequiredMemoryMb = 300 };
        reserve.Processes["two"] = new SupervisedProcessConfig { Name = "two", Role = SupervisedRoles.Gateway, ExecutablePath = "x", RequiredMemoryMb = 300 };
        using var reserveMux = new SupervisedOutputMultiplexer(TextWriter.Null);
        using var reserveSupervisor = new ClusterHostSupervisor(reserve, new FakeSupervisedProcessLauncher(), reserveMux, new FakeCapacityProbe(availableMemoryMb: 1_000));
        SupervisorStartPlan reservePlan = reserveSupervisor.PlanStartAll();
        Assert.Equal(["one"], reservePlan.Admitted); // 1000 - 300 - 300 = 400 < 500
        Assert.Contains("MinFreeMemoryReserveMb", Assert.Single(reservePlan.Denied).Reason);
    }

    [Fact]
    public async Task ChildExit78_IsFaulted_NotRestarted()
    {
        var launcher = new FakeSupervisedProcessLauncher();
        var options = new ClusterSupervisorOptions();
        options.Processes["gateway"] = new SupervisedProcessConfig { Name = "gateway", Role = SupervisedRoles.Gateway, ExecutablePath = "x", RequiredMemoryMb = 1, HangTimeoutSeconds = 0 };
        var delays = new ConcurrentQueue<int>();
        var console = new StringWriter();
        using var mux = new SupervisedOutputMultiplexer(console);
        await using var supervisor = new ClusterHostSupervisor(
            options,
            launcher,
            mux,
            new FakeCapacityProbe(availableMemoryMb: 16_384),
            delayAsync: (ts, _) =>
            {
                delays.Enqueue((int)ts.TotalMilliseconds);
                return Task.CompletedTask;
            });

        FakeSupervisedProcess first = launcher.PreCreateProcess(index: 0);
        await supervisor.StartProcessAsync("gateway");
        await first.WaitForStartSignalAsync();
        first.SimulateUnexpectedExit(exitCode: ArcaneCore.Kernel.Ops.ExitCodes.InvalidConfiguration);

        SupervisedProcessSnapshot snapshot = await WaitForStateAsync(supervisor, "gateway", SupervisedProcessState.Faulted);
        Assert.Equal(SupervisedProcessState.Faulted, snapshot.State);
        Assert.Equal(78, snapshot.LastExitCode);
        Assert.Equal(1, snapshot.Incarnation); // never relaunched
        Assert.Equal(0, snapshot.RestartCount);
        Assert.Empty(delays);
        Assert.Contains("invalid configuration", console.ToString());
    }

    [Fact]
    public void BuildStartInfo_MarksSupervisedChildEnvironment()
    {
        var cfg = new SupervisedProcessConfig { Name = "worker:EK", Role = SupervisedRoles.Worker, ExecutablePath = "dotnet", Arguments = "--info" };

        System.Diagnostics.ProcessStartInfo info = SystemProcessSupervisedLauncher.BuildStartInfo(cfg);

        Assert.Equal("ARCANECORE_SUPERVISED_CHILD", SupervisedChild.EnvironmentVariable);
        Assert.Equal("worker:EK", info.Environment[SupervisedChild.EnvironmentVariable]);
    }

    [Fact]
    public async Task StartOrder_DecidesLaunchDrainAndAdmissionOrder_NotTheSortedKeyOrder()
    {
        // Configuration binding enumerates keys sorted (gateway, realm, worker-EK); StartOrder is what the supervisor follows.
        ClusterSupervisorOptions Build(int max)
        {
            var o = new ClusterSupervisorOptions { MaxConcurrentProcesses = max, MinFreeMemoryReserveMb = 0 };
            o.Processes["realm"] = new SupervisedProcessConfig { Name = "realm", Role = SupervisedRoles.Realm, ExecutablePath = "x", RequiredMemoryMb = 1, StartOrder = 0 };
            o.Processes["gateway"] = new SupervisedProcessConfig { Name = "gateway", Role = SupervisedRoles.Gateway, ExecutablePath = "x", RequiredMemoryMb = 1, StartOrder = 1 };
            o.Processes["worker-EK"] = new SupervisedProcessConfig { Name = "worker-EK", Role = SupervisedRoles.Worker, ExecutablePath = "x", RequiredMemoryMb = 1, StartOrder = 2 };
            return o;
        }

        var launcher = new OrderRecordingLauncher();
        using var mux = new SupervisedOutputMultiplexer(TextWriter.Null);
        await using var supervisor = new ClusterHostSupervisor(Build(9), launcher, mux, new FakeCapacityProbe(availableMemoryMb: 16_384));

        Assert.Equal(["realm", "gateway", "worker-EK"], supervisor.PlanStartAll().Admitted);
        Assert.Equal(["realm", "gateway", "worker-EK"], supervisor.GetSnapshots().Select(s => s.Name));

        await supervisor.StartAllAsync();
        Assert.Equal(["realm", "gateway", "worker-EK"], launcher.Launched.ToArray());

        await supervisor.DrainAndStopAsync();
        Assert.Equal(["worker-EK", "gateway", "realm"], launcher.Drained.ToArray());

        // With room for two, the highest StartOrder is the one denied.
        using var mux2 = new SupervisedOutputMultiplexer(TextWriter.Null);
        await using var limited = new ClusterHostSupervisor(Build(2), new OrderRecordingLauncher(), mux2, new FakeCapacityProbe(availableMemoryMb: 16_384));
        SupervisorStartPlan plan = limited.PlanStartAll();
        Assert.Equal(["realm", "gateway"], plan.Admitted);
        Assert.Equal("worker-EK", Assert.Single(plan.Denied).Name);
    }

    [Fact]
    public async Task PlanStartAll_DoesNotCountRunningProcessesAgainstTheProbedReserve()
    {
        var options = new ClusterSupervisorOptions { MinFreeMemoryReserveMb = 500 };
        options.Processes["one"] = new SupervisedProcessConfig { Name = "one", Role = SupervisedRoles.Realm, ExecutablePath = "x", RequiredMemoryMb = 300, StartOrder = 0 };
        options.Processes["two"] = new SupervisedProcessConfig { Name = "two", Role = SupervisedRoles.Gateway, ExecutablePath = "x", RequiredMemoryMb = 300, StartOrder = 1 };
        using var mux = new SupervisedOutputMultiplexer(TextWriter.Null);
        await using var supervisor = new ClusterHostSupervisor(options, new OrderRecordingLauncher(), mux, new FakeCapacityProbe(availableMemoryMb: 1_000));
        await supervisor.StartProcessAsync("one");

        // "one" is running, so its 300 MiB is already missing from the 1000 MiB the probe reports: 1000 - 300 = 700 >= 500.
        SupervisorStartPlan plan = supervisor.PlanStartAll();

        Assert.Equal(["one", "two"], plan.Admitted);
        Assert.Empty(plan.Denied);
    }

    [Fact]
    public async Task Drain_WhenTheCallersTokenFires_StillKillsTheChild_AndDoesNotThrow()
    {
        var launcher = new OrderRecordingLauncher { DrainWaitsForToken = true };
        var options = new ClusterSupervisorOptions();
        options.Processes["gateway"] = new SupervisedProcessConfig { Name = "gateway", Role = SupervisedRoles.Gateway, ExecutablePath = "x", RequiredMemoryMb = 1 };
        using var mux = new SupervisedOutputMultiplexer(TextWriter.Null);
        await using var supervisor = new ClusterHostSupervisor(options, launcher, mux, new FakeCapacityProbe(availableMemoryMb: 16_384));
        await supervisor.StartProcessAsync("gateway");

        using var cts = new CancellationTokenSource();
        Task drain = supervisor.DrainAndStopAsync(cts.Token);
        await cts.CancelAsync();
        await drain;

        Assert.Equal(1, launcher.Killed);
        Assert.Equal(SupervisedProcessState.Drained, supervisor.GetSnapshot("gateway").State);
    }

    [Fact]
    public async Task Drain_WhenTheKillLeavesTheChildRunning_IsFaulted_Reports_AndDoesNotThrow()
    {
        var launcher = new SurvivorLauncher();
        var options = new ClusterSupervisorOptions();
        options.Processes["gateway"] = new SupervisedProcessConfig { Name = "gateway", Role = SupervisedRoles.Gateway, ExecutablePath = "x", RequiredMemoryMb = 1 };
        var console = new StringWriter();
        using var mux = new SupervisedOutputMultiplexer(console);
        await using var supervisor = new ClusterHostSupervisor(options, launcher, mux, new FakeCapacityProbe(availableMemoryMb: 16_384));
        await supervisor.StartProcessAsync("gateway");

        SupervisedProcessSnapshot snapshot = await supervisor.DrainProcessAsync("gateway");

        Assert.Equal(SupervisedProcessState.Faulted, snapshot.State);
        Assert.Equal(SupervisedProcessState.Faulted, supervisor.GetSnapshot("gateway").State);
        Assert.True(launcher.Process.KillCalls >= 1);
        Assert.Contains("still running after its drain timeout", console.ToString());
        Assert.True(launcher.Process.Disposed); // the wrapper is disposed (outside the gate) even though it survived
    }

    private sealed class SurvivorLauncher : ISupervisedProcessLauncher
    {
        public SurvivorProcess Process { get; } = new();

        public ISupervisedProcess Launch(SupervisedProcessConfig config) => Process;
    }

    /// <summary>A child the OS refuses to kill: it never drains, and KillOwnedProcess leaves HasExited false.</summary>
    private sealed class SurvivorProcess : ISupervisedProcess
    {
        private int _killCalls;
        private int _disposed;

        public int KillCalls => Volatile.Read(ref _killCalls);

        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public int ProcessId => 4242;

        public bool HasExited => false;

        public int? ExitCode => null;

        public event Action<string, bool>? OutputLineReceived
        {
            add { }
            remove { }
        }

        public Task<int> WaitForExitAsync(CancellationToken ct = default) => new TaskCompletionSource<int>().Task.WaitAsync(ct);

        public Task<bool> RequestDrainAsync(TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(false);

        public void KillOwnedProcess() => Interlocked.Increment(ref _killCalls);

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class OrderRecordingLauncher : ISupervisedProcessLauncher
    {
        public ConcurrentQueue<string> Launched { get; } = new();

        public ConcurrentQueue<string> Drained { get; } = new();

        public bool DrainWaitsForToken { get; init; }

        private int _killed;

        public int Killed => Volatile.Read(ref _killed);

        public ISupervisedProcess Launch(SupervisedProcessConfig config)
        {
            Launched.Enqueue(config.Name);
            return new Child(this, config.Name);
        }

        private sealed class Child(OrderRecordingLauncher owner, string name) : ISupervisedProcess
        {
            private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int ProcessId => 1;

            public bool HasExited => _exit.Task.IsCompleted;

            public int? ExitCode => _exit.Task.IsCompleted ? _exit.Task.Result : null;

            public event Action<string, bool>? OutputLineReceived
            {
                add { }
                remove { }
            }

            public Task<int> WaitForExitAsync(CancellationToken ct = default) => _exit.Task.WaitAsync(ct);

            public Task<bool> RequestDrainAsync(TimeSpan timeout, CancellationToken ct = default)
            {
                owner.Drained.Enqueue(name);
                if (owner.DrainWaitsForToken)
                {
                    return WaitForTokenAsync(ct);
                }

                _exit.TrySetResult(0);
                return Task.FromResult(true);
            }

            private static async Task<bool> WaitForTokenAsync(CancellationToken ct)
            {
                await Task.Delay(Timeout.Infinite, ct);
                return true;
            }

            public void KillOwnedProcess()
            {
                Interlocked.Increment(ref owner._killed);
                _exit.TrySetResult(-1);
            }

            public void Dispose()
            {
            }
        }
    }

    private static async Task<SupervisedProcessSnapshot> WaitForStateAsync(ClusterHostSupervisor supervisor, string name, SupervisedProcessState state)
    {
        for (int i = 0; i < 500; i++)
        {
            SupervisedProcessSnapshot snapshot = supervisor.GetSnapshot(name);
            if (snapshot.State == state)
            {
                return snapshot;
            }

            await Task.Delay(10);
        }

        return supervisor.GetSnapshot(name);
    }

    private sealed class FakeCapacityProbe : IClusterCapacityProbe
    {
        public FakeCapacityProbe(long availableMemoryMb)
        {
            AvailableMemoryMb = availableMemoryMb;
        }

        public long AvailableMemoryMb { get; set; }

        public long GetAvailableMemoryMb() => AvailableMemoryMb;
    }

    private sealed class FakeSupervisedProcessLauncher : ISupervisedProcessLauncher
    {
        private readonly ConcurrentDictionary<int, FakeSupervisedProcess> _processes = new();
        private int _launchCounter = -1;

        public FakeSupervisedProcess PreCreateProcess(int index)
        {
            return _processes.GetOrAdd(index, i => new FakeSupervisedProcess(10_000 + i));
        }

        public ISupervisedProcess Launch(SupervisedProcessConfig config)
        {
            int idx = Interlocked.Increment(ref _launchCounter);
            FakeSupervisedProcess proc = PreCreateProcess(idx);
            proc.SignalStarted();
            return proc;
        }
    }

    private sealed class FakeSupervisedProcess : ISupervisedProcess
    {
        private readonly TaskCompletionSource _startedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _hasExited;
        private int? _exitCode;

        public FakeSupervisedProcess(int pid)
        {
            ProcessId = pid;
        }

        public int ProcessId { get; }

        public bool HasExited => Volatile.Read(ref _hasExited) != 0;

        public int? ExitCode => _exitCode;

        public event Action<string, bool>? OutputLineReceived;

        public void SignalStarted() => _startedTcs.TrySetResult();

        public Task WaitForStartSignalAsync() => _startedTcs.Task;

        public void SimulateUnexpectedExit(int exitCode)
        {
            OutputLineReceived?.Invoke($"[ERROR] Process {ProcessId} unexpected exit ({exitCode})", true);
            _exitCode = exitCode;
            if (Interlocked.Exchange(ref _hasExited, 1) == 0)
            {
                _exitTcs.TrySetResult(exitCode);
            }
        }

        public Task<int> WaitForExitAsync(CancellationToken ct = default)
        {
            return _exitTcs.Task.WaitAsync(ct);
        }

        public Task<bool> RequestDrainAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            OutputLineReceived?.Invoke("[INFO] Graceful drain complete", false);
            _exitCode = 0;
            if (Interlocked.Exchange(ref _hasExited, 1) == 0)
            {
                _exitTcs.TrySetResult(0);
            }
            return Task.FromResult(true);
        }

        public void KillOwnedProcess()
        {
            _exitCode = -1;
            if (Interlocked.Exchange(ref _hasExited, 1) == 0)
            {
                _exitTcs.TrySetResult(-1);
            }
        }

        public void Dispose()
        {
        }
    }
}
