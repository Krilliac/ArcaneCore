using System.Collections.Concurrent;
using ArcaneCore.World.Cluster.Supervision;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

public sealed class ClusterSupervisorHostingTests
{
    private static IConfiguration Config(int min, int processCount, params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["Cluster:Supervisor:Enabled"] = "true",
            ["Cluster:Supervisor:MinClusterProcesses"] = min.ToString(),
            ["Cluster:Supervisor:MinFreeMemoryReserveMb"] = "0",
        };
        string[] roles = [SupervisedRoles.Realm, SupervisedRoles.Gateway, SupervisedRoles.Worker, SupervisedRoles.Worker];
        for (int i = 0; i < processCount; i++)
        {
            values[$"Cluster:Supervisor:Processes:p{i}:Role"] = roles[i];
            values[$"Cluster:Supervisor:Processes:p{i}:ExecutablePath"] = "dotnet";
            values[$"Cluster:Supervisor:Processes:p{i}:RequiredMemoryMb"] = "1";
        }

        foreach ((string key, string value) in extra)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ServiceProvider Build(IConfiguration configuration, RecordingLauncher launcher, long availableMb = 100_000)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISupervisedProcessLauncher>(launcher);
        services.AddSingleton<IClusterCapacityProbe>(new FixedProbe(availableMb));
        services.AddClusterSupervisor(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Start_BelowMinClusterProcesses_LaunchesNothing_AndThrowsNamingTheKey()
    {
        var launcher = new RecordingLauncher();
        // Three processes configured, but the process ceiling admits one of them: nothing may be launched. (A refusal for free memory alone waits instead, see below.)
        await using ServiceProvider provider = Build(Config(min: 2, processCount: 3, ("Cluster:Supervisor:MaxConcurrentProcesses", "1")), launcher);
        IHostedService hosted = provider.GetServices<IHostedService>().OfType<ClusterSupervisorHostedService>().Single();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => hosted.StartAsync(CancellationToken.None));

        Assert.Contains("Cluster:Supervisor:MinClusterProcesses", ex.Message, StringComparison.Ordinal);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task Start_AtMin_LaunchesAdmitted_StopDrainsAll()
    {
        var launcher = new RecordingLauncher();
        await using ServiceProvider provider = Build(Config(min: 3, processCount: 3), launcher);
        IHostedService hosted = provider.GetServices<IHostedService>().OfType<ClusterSupervisorHostedService>().Single();
        var supervisor = provider.GetRequiredService<ClusterHostSupervisor>();

        await hosted.StartAsync(CancellationToken.None);

        Assert.Equal(["p0", "p1", "p2"], launcher.Launched.ToArray());
        Assert.All(supervisor.GetSnapshots(), s => Assert.Equal(SupervisedProcessState.Running, s.State));

        await hosted.StopAsync(CancellationToken.None);

        Assert.All(supervisor.GetSnapshots(), s => Assert.Equal(SupervisedProcessState.Drained, s.State));
        Assert.Equal(3, launcher.Drained);
        Assert.Equal(["p0", "p1", "p2"], launcher.Launched.ToArray()); // none relaunched
    }

    [Fact]
    public async Task Stop_DisposesSupervisor_EvenWhenDrainThrows()
    {
        var launcher = new RecordingLauncher { DrainThrows = true };
        await using ServiceProvider provider = Build(Config(min: 1, processCount: 1), launcher);
        IHostedService hosted = provider.GetServices<IHostedService>().OfType<ClusterSupervisorHostedService>().Single();
        var supervisor = provider.GetRequiredService<ClusterHostSupervisor>();
        await hosted.StartAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => hosted.StopAsync(CancellationToken.None));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => supervisor.StartProcessAsync("p0"));
        Assert.True(launcher.Disposed >= 1);
    }

    [Fact]
    public async Task AddClusterSupervisor_RegistersHostedSupervisor_WithBoundOptions()
    {
        var launcher = new RecordingLauncher();
        await using ServiceProvider provider = Build(
            Config(min: 2, processCount: 2, ("Cluster:Supervisor:MaxConcurrentProcesses", "5")),
            launcher);

        ClusterSupervisorOptions options = provider.GetRequiredService<IOptions<ClusterSupervisorOptions>>().Value;
        Assert.True(options.Enabled);
        Assert.Equal(5, options.MaxConcurrentProcesses);
        Assert.Equal(2, options.Processes.Count);
        Assert.Single(provider.GetServices<IHostedService>().OfType<ClusterSupervisorHostedService>());
        Assert.Equal(["p0", "p1"], provider.GetRequiredService<ClusterHostSupervisor>().GetSnapshots().Select(s => s.Name));
        Assert.Same(launcher, provider.GetRequiredService<ISupervisedProcessLauncher>());
        Assert.True(ClusterSupervisorHosting.IsEnabled(Config(min: 1, processCount: 1)));
        Assert.False(ClusterSupervisorHosting.IsEnabled(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public async Task ShutdownTimeout_IsRaisedToTheSumOfDrainTimeouts_EvenWhenAppsettingsConfiguresLess()
    {
        // appsettings.json ships HostOptions:ShutdownTimeout = 00:01:00; three children with a 2 minute drain each need 6 minutes plus the margin.
        IConfiguration configuration = Config(min: 3, processCount: 3,
            ("HostOptions:ShutdownTimeout", "00:01:00"),
            ("Cluster:Supervisor:Processes:p0:DrainTimeoutMs", "120000"),
            ("Cluster:Supervisor:Processes:p1:DrainTimeoutMs", "120000"),
            ("Cluster:Supervisor:Processes:p2:DrainTimeoutMs", "120000"));
        await using ServiceProvider provider = Build(configuration, new RecordingLauncher());

        Assert.Equal(TimeSpan.FromSeconds(375), provider.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);

        // A larger configured value is kept, and nothing exceeds one hour.
        await using ServiceProvider larger = Build(Config(min: 3, processCount: 3, ("HostOptions:ShutdownTimeout", "00:10:00")), new RecordingLauncher());
        Assert.Equal(TimeSpan.FromMinutes(10), larger.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
        await using ServiceProvider capped = Build(Config(min: 3, processCount: 3, ("HostOptions:ShutdownTimeout", "05:00:00")), new RecordingLauncher());
        Assert.Equal(TimeSpan.FromHours(1), capped.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
    }

    [Fact]
    public async Task RunAsync_WhenTheDrainIsCancelledDuringStop_ReturnsFailure_AndDoesNotThrow()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddConfiguration(Config(min: 1, processCount: 1));
        builder.Services.AddSingleton<ISupervisedProcessLauncher>(sp => new StopOnLaunchLauncher(sp.GetRequiredService<IHostApplicationLifetime>()));
        var error = new StringWriter();

        int exit = await ClusterSupervisorHosting.RunAsync(builder, error).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(1, exit);
        Assert.Contains("failed while stopping", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("exiting with code 1: the stop failed", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenAStopSignalCancelsTheStartBetweenTwoLaunches_ReturnsSuccess_AndDrainsTheLaunchedChild()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddConfiguration(Config(min: 1, processCount: 2));
        var inner = new RecordingLauncher();
        builder.Services.AddSingleton<ISupervisedProcessLauncher>(sp => new StopAfterFirstLaunchLauncher(sp.GetRequiredService<IHostApplicationLifetime>(), inner));
        var error = new StringWriter();

        int exit = await ClusterSupervisorHosting.RunAsync(builder, error).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(0, exit); // an operator stop is a clean exit, not an unhandled OperationCanceledException
        Assert.Contains("cancelled", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("exiting with code 0: the start was cancelled by a stop signal", error.ToString(), StringComparison.Ordinal);
        Assert.Single(inner.Launched); // the second launch never ran
        Assert.Equal(1, inner.Drained);
    }

    private sealed class StopAfterFirstLaunchLauncher(IHostApplicationLifetime lifetime, ISupervisedProcessLauncher inner) : ISupervisedProcessLauncher
    {
        private int _launches;

        public ISupervisedProcess Launch(SupervisedProcessConfig config)
        {
            ISupervisedProcess process = inner.Launch(config);
            if (Interlocked.Increment(ref _launches) == 1)
            {
                lifetime.StopApplication(); // cancels ApplicationStopping, which the host links into StartAsync's token
            }

            return process;
        }
    }

    private sealed class StopOnLaunchLauncher(IHostApplicationLifetime lifetime) : ISupervisedProcessLauncher
    {
        public ISupervisedProcess Launch(SupervisedProcessConfig config)
        {
            _ = Task.Run(lifetime.StopApplication);
            return new CancellingProcess();
        }

        private sealed class CancellingProcess : ISupervisedProcess
        {
            private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int ProcessId => 1;

            public bool HasExited => false;

            public int? ExitCode => null;

            public event Action<string, bool>? OutputLineReceived
            {
                add { }
                remove { }
            }

            public Task<int> WaitForExitAsync(CancellationToken ct = default) => _exit.Task.WaitAsync(ct);

            public Task<bool> RequestDrainAsync(TimeSpan timeout, CancellationToken ct = default) => throw new OperationCanceledException("drain cancelled");

            public void KillOwnedProcess() => _exit.TrySetResult(-1);

            public void Dispose()
            {
            }
        }
    }

    [Fact]
    public async Task RunAsync_ARefusedStart_SaysItsExitCodeAndWhy_OnStderr()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddConfiguration(Config(min: 2, processCount: 1));
        builder.Services.AddSingleton<ISupervisedProcessLauncher>(new RecordingLauncher());
        var error = new StringWriter();

        int exit = await ClusterSupervisorHosting.RunAsync(builder, error).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(1, exit);
        Assert.Contains("exiting with code 1: the start was refused", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Cluster:Supervisor:MinClusterProcesses", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteExit_WritesTheCodeAndReason_AndNeverThrowsWhenStderrIsGone()
    {
        var error = new StringWriter();
        ClusterSupervisorHosting.WriteExit(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, error, 3, "unhandled exception: Boom", null);
        Assert.Equal("ArcaneCore.World supervisor exiting with code 3: unhandled exception: Boom" + Environment.NewLine, error.ToString());

        var gone = new StringWriter();
        gone.Dispose();
        ClusterSupervisorHosting.WriteExit(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, gone, 0, "done", null);
    }

    private static string ReadSharedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // The exit hooks run where nothing else flushes the queued file sink (a crash runs no ProcessExit; the provider's own ProcessExit flush runs before the hook's line is queued).
    [Fact]
    public async Task TheUnhandledExceptionHook_GetsItsLineToTheFileSink_WithoutAnyDisposeOrProcessExit_AndNamesNoExitCode()
    {
        string dir = Path.Combine(Path.GetTempPath(), "supervisor-exit-hook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string logPath = Path.Combine(dir, "supervisor.log");
        try
        {
            IConfiguration config = Config(min: 1, processCount: 1, ("Logging:ArcaneCore:File:Enabled", "true"), ("Logging:ArcaneCore:File:Path", logPath));
            await using ServiceProvider provider = Build(config, new RecordingLauncher());
            ILogger log = provider.GetRequiredService<ILoggerFactory>().CreateLogger(ClusterSupervisorHosting.ExitLoggerName);
            var error = new StringWriter();

            ClusterSupervisorHosting.ReportUnhandledException(log, error, () => ClusterSupervisorHosting.FlushLogProviders(provider), new UnhandledExceptionEventArgs(new InvalidOperationException("boom-for-the-hook"), isTerminating: true));

            string file = ReadSharedText(logPath);
            Assert.Contains("boom-for-the-hook", file, StringComparison.Ordinal);
            Assert.Contains("Supervisor exiting", file, StringComparison.Ordinal);
            Assert.Contains("boom-for-the-hook", error.ToString(), StringComparison.Ordinal);
            Assert.Contains("chosen by the runtime", error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("with code 1", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // A late handle on Windows: the temp directory is not the subject of the test.
            }
        }
    }

    [Fact]
    public async Task WriteExit_WithAFlush_GetsTheLineToTheFileSink_AndFlushRunsEvenWhenStderrIsGone()
    {
        string dir = Path.Combine(Path.GetTempPath(), "supervisor-exit-flush-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string logPath = Path.Combine(dir, "supervisor.log");
        try
        {
            IConfiguration config = Config(min: 1, processCount: 1, ("Logging:ArcaneCore:File:Enabled", "true"), ("Logging:ArcaneCore:File:Path", logPath));
            await using ServiceProvider provider = Build(config, new RecordingLauncher());
            ILogger log = provider.GetRequiredService<ILoggerFactory>().CreateLogger(ClusterSupervisorHosting.ExitLoggerName);
            var gone = new StringWriter();
            gone.Dispose();

            ClusterSupervisorHosting.WriteExit(log, gone, 7, "process-exit-reason", null, () => ClusterSupervisorHosting.FlushLogProviders(provider));

            string file = ReadSharedText(logPath);
            Assert.Contains("process-exit-reason", file, StringComparison.Ordinal);
            Assert.Contains("with code 7", file, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // A late handle on Windows: the temp directory is not the subject of the test.
            }
        }
    }

    [Fact]
    public async Task Start_WithAReserveNotBelowTheHostsMemory_ThrowsAConfigurationError_InsteadOfWaitingForever()
    {
        var launcher = new RecordingLauncher();
        ClusterSupervisorHostedService hosted = HostedWith(Config(min: 1, processCount: 1, ("Cluster:Supervisor:MinFreeMemoryReserveMb", "5000")), launcher, new SequenceProbe(10), out ServiceProvider provider);
        await using ServiceProvider owner = provider;
        hosted.TotalMemoryMb = () => 4000;
        hosted.DelayAsync = (_, _) => throw new InvalidOperationException("the supervisor waited");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => hosted.StartAsync(CancellationToken.None));

        Assert.Contains("MinFreeMemoryReserveMb", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("waited", ex.Message, StringComparison.Ordinal);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task Start_AMixedPlan_WhereMemoryRefusalsCannotReachTheMinimum_ThrowsInsteadOfWaiting()
    {
        var launcher = new RecordingLauncher();
        // Three configured, the ceiling admits one (the others are refused for the count, not memory), minimum 2: memory cannot help.
        ClusterSupervisorHostedService hosted = HostedWith(Config(min: 2, processCount: 3, ("Cluster:Supervisor:MaxConcurrentProcesses", "1"), ("Cluster:Supervisor:MinFreeMemoryReserveMb", "1000")), launcher, new SequenceProbe(1_500), out ServiceProvider provider);
        await using ServiceProvider owner = provider;
        hosted.TotalMemoryMb = () => 1_000_000;
        hosted.DelayAsync = (_, _) => throw new InvalidOperationException("the supervisor waited");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => hosted.StartAsync(CancellationToken.None));

        Assert.Contains("MinClusterProcesses", ex.Message, StringComparison.Ordinal);
        Assert.Empty(launcher.Launched);
    }

    // ---- a start refused for free memory waits ----

    private static ClusterSupervisorHostedService HostedWith(IConfiguration configuration, RecordingLauncher launcher, IClusterCapacityProbe probe, out ServiceProvider provider)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISupervisedProcessLauncher>(launcher);
        services.AddSingleton(probe);
        services.AddClusterSupervisor(configuration);
        provider = services.BuildServiceProvider();
        return provider.GetServices<IHostedService>().OfType<ClusterSupervisorHostedService>().Single();
    }

    [Fact]
    public async Task Start_RefusedOnlyForFreeMemory_WaitsAndPlansAgain_ThenLaunches_InsteadOfExiting()
    {
        var launcher = new RecordingLauncher();
        var probe = new SequenceProbe(10, 10, 100_000, 100_000); // two plans see 10 MiB free, the third plan and the child's admission see plenty
        ClusterSupervisorHostedService hosted = HostedWith(Config(min: 1, processCount: 1, ("Cluster:Supervisor:MinFreeMemoryReserveMb", "1000")), launcher, probe, out ServiceProvider provider);
        await using ServiceProvider owner = provider;
        hosted.TotalMemoryMb = () => 1_000_000;
        var delays = new List<TimeSpan>();
        hosted.DelayAsync = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };

        await hosted.StartAsync(CancellationToken.None);

        Assert.Equal([ClusterSupervisorHostedService.MemoryRetryInterval, ClusterSupervisorHostedService.MemoryRetryInterval], delays);
        Assert.Equal(["p0"], launcher.Launched.ToArray());
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Start_WaitingForFreeMemory_EndsWithTheStopSignal_AndLaunchesNothing()
    {
        var launcher = new RecordingLauncher();
        ClusterSupervisorHostedService hosted = HostedWith(Config(min: 1, processCount: 1, ("Cluster:Supervisor:MinFreeMemoryReserveMb", "1000")), launcher, new SequenceProbe(10), out ServiceProvider provider);
        await using ServiceProvider owner = provider;
        hosted.TotalMemoryMb = () => 1_000_000;
        using var stop = new CancellationTokenSource();
        hosted.DelayAsync = (_, ct) =>
        {
            stop.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hosted.StartAsync(stop.Token));

        Assert.Empty(launcher.Launched);
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Start_WhenALaunchedChildTookTheMemoryTheNextOneNeeds_WaitsThenLaunchesTheRest()
    {
        var launcher = new RecordingLauncher();
        // The plan (one read) and the first child's admission see plenty; the second child's admission sees 10 MiB; the retry sees plenty again.
        var probe = new SequenceProbe(100_000, 100_000, 10, 100_000);
        ClusterSupervisorHostedService hosted = HostedWith(Config(min: 1, processCount: 2, ("Cluster:Supervisor:MinFreeMemoryReserveMb", "50")), launcher, probe, out ServiceProvider provider);
        await using ServiceProvider owner = provider;
        hosted.TotalMemoryMb = () => 1_000_000;
        var delays = new List<TimeSpan>();
        hosted.DelayAsync = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };

        await hosted.StartAsync(CancellationToken.None);

        Assert.Equal(["p0", "p1"], launcher.Launched.ToArray()); // p0 is not launched a second time
        Assert.Single(delays);
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Start_WithAReserveThatLeavesNoRoomForTheMinimumProcesses_ThrowsAConfigurationError_InsteadOfWaitingForever()
    {
        var launcher = new RecordingLauncher();
        // Reserve 600 is below the 1000 MiB total, but 600 + the 500 MiB one process needs is above it: no free memory can ever admit it.
        ClusterSupervisorHostedService hosted = HostedWith(
            Config(min: 1, processCount: 1, ("Cluster:Supervisor:MinFreeMemoryReserveMb", "600"), ("Cluster:Supervisor:Processes:p0:RequiredMemoryMb", "500")),
            launcher, new SequenceProbe(10), out ServiceProvider provider);
        await using ServiceProvider owner = provider;
        hosted.TotalMemoryMb = () => 1000;
        hosted.DelayAsync = (_, _) => throw new InvalidOperationException("the supervisor waited");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => hosted.StartAsync(CancellationToken.None));

        Assert.Contains("smallest", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("waited", ex.Message, StringComparison.Ordinal);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task Start_WithAReserveAndTheSmallestMinimumExactlyFillingTheHost_StillWaitsForMemory()
    {
        var launcher = new RecordingLauncher();
        ClusterSupervisorHostedService hosted = HostedWith(
            Config(min: 1, processCount: 1, ("Cluster:Supervisor:MinFreeMemoryReserveMb", "500"), ("Cluster:Supervisor:Processes:p0:RequiredMemoryMb", "500")),
            launcher, new SequenceProbe(10, 1000, 1000), out ServiceProvider provider);
        await using ServiceProvider owner = provider;
        hosted.TotalMemoryMb = () => 1000;
        int delays = 0;
        hosted.DelayAsync = (_, _) =>
        {
            delays++;
            return Task.CompletedTask;
        };

        await hosted.StartAsync(CancellationToken.None);

        Assert.Equal(1, delays);
        Assert.Equal(["p0"], launcher.Launched.ToArray());
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TheDefaultTotalMemory_IsTheHostWideTotalOfTheSameSourceAsTheAdmissionProbe()
    {
        ClusterSupervisorHostedService hosted = HostedWith(Config(min: 1, processCount: 1), new RecordingLauncher(), new FixedProbe(1), out ServiceProvider provider);
        await using ServiceProvider owner = provider;

        long? hostBytes = HostMemory.TryGetTotalBytes();
        Assert.True(hostBytes is > 0, "this platform has an OS total memory source");
        Assert.Equal(hostBytes!.Value / (1024L * 1024L), hosted.TotalMemoryMb());
    }

    [Fact]
    public void HostMemory_ParsesMemTotalOfAProcMeminfoText_AndRejectsAMissingOrMalformedLine()
    {
        const string text = "MemTotal:       32768000 kB\nMemFree:         1000 kB\nMemAvailable:   20480000 kB\n";

        Assert.Equal(32768000L * 1024, HostMemory.ParseMemTotalBytes(text));
        Assert.Equal(20480000L * 1024, HostMemory.ParseMemAvailableBytes(text));
        Assert.Null(HostMemory.ParseMemTotalBytes("MemAvailable: 1 kB\n"));
        Assert.Null(HostMemory.ParseMemTotalBytes("MemTotal: lots kB\n"));
    }

    [Fact]
    public async Task Start_WhenMemoryNeverReturnsAfterAChildLaunched_GivesUpAfterTheRetryCap()
    {
        var launcher = new RecordingLauncher();
        // The plan and p0's admission see plenty; every later read (p1's admission) sees 10 MiB, so p1 is refused for the reserve each time.
        var probe = new SequenceProbe(100_000, 100_000, 10);
        ClusterSupervisorHostedService hosted = HostedWith(Config(min: 1, processCount: 2, ("Cluster:Supervisor:MinFreeMemoryReserveMb", "50")), launcher, probe, out ServiceProvider provider);
        await using ServiceProvider owner = provider;
        hosted.TotalMemoryMb = () => 1_000_000;
        int delays = 0;
        hosted.DelayAsync = (_, _) =>
        {
            delays++;
            return Task.CompletedTask;
        };

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => hosted.StartAsync(CancellationToken.None));

        Assert.Equal(ClusterSupervisorHostedService.MaxMidStartMemoryRetries, delays);
        Assert.Equal(["p0"], launcher.Launched.ToArray());
        Assert.Contains("never launched", ex.Message, StringComparison.Ordinal);
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Start_WhileWaitingForMemoryMidStart_RepeatsTheGroupReadyWaitOfTheGroupInProgress()
    {
        var launcher = new RecordingLauncher();
        // p0 and p1 share start order 0 (the config gives every process order 0): p0 launched, p1 refused once. No child ever sends a heartbeat, so the repeated wait ends at its timeout.
        var probe = new SequenceProbe(100_000, 100_000, 10, 100_000);
        ClusterSupervisorHostedService hosted = HostedWith(
            Config(min: 1, processCount: 2, ("Cluster:Supervisor:MinFreeMemoryReserveMb", "50"), ("Cluster:Supervisor:StartGroupReadySeconds", "1")),
            launcher, probe, out ServiceProvider provider);
        await using ServiceProvider owner = provider;
        hosted.TotalMemoryMb = () => 1_000_000;
        hosted.DelayAsync = (_, _) => Task.CompletedTask;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await hosted.StartAsync(CancellationToken.None);

        // Only the repeated wait can take the second (a lower bound, so no timing flake): the first pass launched p0 and p1 in one group without waiting.
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(800), $"the group-ready wait was skipped ({clock.Elapsed.TotalMilliseconds:F0} ms)");
        Assert.Equal(["p0", "p1"], launcher.Launched.ToArray());
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Start_WhileWaitingForMemoryMidStart_WatchesTheMinimum_SoAnExitDuringTheWaitIsSeen()
    {
        var launcher = new RecordingLauncher();
        var probe = new SequenceProbe(100_000, 100_000, 10, 100_000);
        ClusterSupervisorHostedService hosted = HostedWith(Config(min: 1, processCount: 2, ("Cluster:Supervisor:MinFreeMemoryReserveMb", "50")), launcher, probe, out ServiceProvider provider);
        await using ServiceProvider owner = provider;
        hosted.TotalMemoryMb = () => 1_000_000;
        ClusterHostSupervisor supervisor = provider.GetRequiredService<ClusterHostSupervisor>();
        var below = new TaskCompletionSource<SupervisorBelowMinimum>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.BelowMinimum += report => below.TrySetResult(report);
        // The only child launched so far ends during the wait: with the watch armed that is below the minimum of 1 and is reported; without it nothing would notice.
        hosted.DelayAsync = async (_, _) =>
        {
            launcher.ExitProcess("p0", 0);
            await below.Task.WaitAsync(TimeSpan.FromSeconds(30)); // the monitor loop sees the exit on its own thread; without the armed watch this never completes
        };

        await hosted.StartAsync(CancellationToken.None);

        SupervisorBelowMinimum report = await below.Task;
        Assert.Equal(0, report.Running);
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void FlushLogProviders_SwallowsAnOrdinaryFailure_ButNeverOutOfMemory()
    {
        ClusterSupervisorHosting.FlushLogProviders(new ThrowingServices(new NotSupportedException("a torn-down container")));

        Assert.Throws<OutOfMemoryException>(() => ClusterSupervisorHosting.FlushLogProviders(new ThrowingServices(new OutOfMemoryException())));
    }

    [Fact]
    public void WriteExit_SurvivesALoggerThatThrowsAnythingButOutOfMemory_AndStillWritesStderr()
    {
        var error = new StringWriter();

        ClusterSupervisorHosting.WriteExit(new ThrowingLogger(new NotSupportedException("a sink of its own")), error, 3, "why", null);

        Assert.Contains("with code 3", error.ToString(), StringComparison.Ordinal);
        Assert.Throws<OutOfMemoryException>(() => ClusterSupervisorHosting.WriteExit(new ThrowingLogger(new OutOfMemoryException()), new StringWriter(), 3, "why", null));
    }

    private sealed class ThrowingServices(Exception failure) : IServiceProvider
    {
        public object? GetService(Type serviceType) => throw failure;
    }

    private sealed class ThrowingLogger(Exception failure) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => throw failure;
    }

    [Theory]
    [InlineData("Cannot admit process 'w': available host memory (10 MiB) minus RequiredMemoryMb (1 MiB) is below MinFreeMemoryReserveMb (50 MiB).", true)]
    [InlineData("active process count (3) would exceed MaxConcurrentProcesses (2).", false)]
    [InlineData("map 1 is already assigned to worker 'a'.", false)]
    [InlineData(null, false)]
    public void IsMemoryReserveRefusal_OnlyTheReserveIsAWaitableRefusal(string? reason, bool expected)
        => Assert.Equal(expected, ClusterHostSupervisor.IsMemoryReserveRefusal(reason));

    private sealed class SequenceProbe(params long[] values) : IClusterCapacityProbe
    {
        private int _next;

        public long GetAvailableMemoryMb() => values[Math.Min(Interlocked.Increment(ref _next) - 1, values.Length - 1)];
    }


    private sealed class FixedProbe(long availableMb) : IClusterCapacityProbe
    {
        public long GetAvailableMemoryMb() => availableMb;
    }

    private sealed class RecordingLauncher : ISupervisedProcessLauncher
    {
        private int _drained;
        private int _disposed;

        public ConcurrentQueue<string> Launched { get; } = new();

        public bool DrainThrows { get; init; }

        public int Drained => Volatile.Read(ref _drained);

        public int Disposed => Volatile.Read(ref _disposed);

        public ISupervisedProcess Launch(SupervisedProcessConfig config)
        {
            Launched.Enqueue(config.Name);
            var process = new Process(this, 20_000 + Launched.Count);
            _processes[config.Name] = process;
            return process;
        }

        private readonly ConcurrentDictionary<string, Process> _processes = new(StringComparer.Ordinal);

        /// <summary>Makes the last launched process of <paramref name="name"/> exit on its own with <paramref name="code"/> (0 is a clean exit the supervisor leaves down).</summary>
        public void ExitProcess(string name, int code) => _processes[name].Exit(code);

        private sealed class Process(RecordingLauncher owner, int pid) : ISupervisedProcess
        {
            private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int ProcessId => pid;

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
                if (owner.DrainThrows)
                {
                    throw new InvalidOperationException("drain failed");
                }

                Interlocked.Increment(ref owner._drained);
                _exit.TrySetResult(0);
                return Task.FromResult(true);
            }

            public void KillOwnedProcess() => _exit.TrySetResult(-1);

            public void Exit(int code) => _exit.TrySetResult(code);

            public void Dispose() => Interlocked.Increment(ref owner._disposed);
        }
    }
}
