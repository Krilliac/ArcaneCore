using System.Collections.Concurrent;
using ArcaneCore.World.Cluster.Supervision;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        // Three processes configured, but the reserve admits none of them: nothing may be launched.
        await using ServiceProvider provider = Build(Config(min: 2, processCount: 3, ("Cluster:Supervisor:MinFreeMemoryReserveMb", "999999999")), launcher);
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
            return new Process(this, 20_000 + Launched.Count);
        }

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

            public void Dispose() => Interlocked.Increment(ref owner._disposed);
        }
    }
}
