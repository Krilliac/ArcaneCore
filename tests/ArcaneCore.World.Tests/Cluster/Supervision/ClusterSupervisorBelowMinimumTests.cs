using ArcaneCore.Kernel.Ops;
using ArcaneCore.World.Cluster.Supervision;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

/// <summary>When fewer than <c>MinClusterProcesses</c> children are left, the supervisor stops everything: exit 0 if every stop was deliberate, 1 if a child was given up on.</summary>
public sealed class ClusterSupervisorBelowMinimumTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private int _stops;

        public int Stops => Volatile.Read(ref _stops);

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Interlocked.Increment(ref _stops);
    }

    private static IConfiguration Config(int min, params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["Cluster:Supervisor:Enabled"] = "true",
            ["Cluster:Supervisor:MinClusterProcesses"] = min.ToString(),
            ["Cluster:Supervisor:MinFreeMemoryReserveMb"] = "0",
        };
        string[] roles = [SupervisedRoles.Realm, SupervisedRoles.Gateway, SupervisedRoles.Worker];
        for (int i = 0; i < 3; i++)
        {
            values[$"Cluster:Supervisor:Processes:p{i}:Role"] = roles[i];
            values[$"Cluster:Supervisor:Processes:p{i}:ExecutablePath"] = "dotnet";
            values[$"Cluster:Supervisor:Processes:p{i}:RequiredMemoryMb"] = "1";
            values[$"Cluster:Supervisor:Processes:p{i}:StartOrder"] = i.ToString();
        }

        foreach ((string key, string value) in extra)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ServiceProvider Build(IConfiguration configuration, ScriptedLauncher launcher, FakeLifetime lifetime)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISupervisedProcessLauncher>(launcher);
        services.AddSingleton<IClusterCapacityProbe>(new FixedMemoryProbe());
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddClusterSupervisor(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task BelowMinimum_AfterShutdown_StopsSupervisorExit0()
    {
        var launcher = new ScriptedLauncher();
        var lifetime = new FakeLifetime();
        await using ServiceProvider provider = Build(Config(min: 3), launcher, lifetime);
        ClusterSupervisorHostedService hosted = provider.GetServices<IHostedService>().OfType<ClusterSupervisorHostedService>().Single();
        await hosted.StartAsync(CancellationToken.None);
        await launcher.WaitForLaunchesAsync(3);

        launcher.Processes[2].ExitWith(ExitCodes.Success); // an operator's .server shutdown on the worker: a deliberate stop, but now two of three remain

        await TestWait.UntilAsync(() => lifetime.Stops == 1, "the supervisor to stop itself");
        Assert.Equal(0, hosted.RequestedExitCode);
        Assert.Equal(1, lifetime.Stops); // once, not once per remaining exit
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BelowMinimum_AfterGiveUp_Exit1()
    {
        var launcher = new ScriptedLauncher();
        var lifetime = new FakeLifetime();
        await using ServiceProvider provider = Build(Config(min: 3), launcher, lifetime);
        ClusterSupervisorHostedService hosted = provider.GetServices<IHostedService>().OfType<ClusterSupervisorHostedService>().Single();
        await hosted.StartAsync(CancellationToken.None);
        await launcher.WaitForLaunchesAsync(3);

        launcher.Processes[1].ExitWith(ExitCodes.InvalidConfiguration); // the gateway refuses its configuration: given up on, not restarted

        await TestWait.UntilAsync(() => lifetime.Stops == 1, "the supervisor to stop itself");
        Assert.Equal(1, hosted.RequestedExitCode);
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BelowMinimum_WhenStopWhenBelowMinimumIsOff_KeepsRunning()
    {
        var launcher = new ScriptedLauncher();
        var lifetime = new FakeLifetime();
        await using ServiceProvider provider = Build(Config(min: 3, ("Cluster:Supervisor:StopWhenBelowMinimum", "false")), launcher, lifetime);
        ClusterSupervisorHostedService hosted = provider.GetServices<IHostedService>().OfType<ClusterSupervisorHostedService>().Single();
        await hosted.StartAsync(CancellationToken.None);
        await launcher.WaitForLaunchesAsync(3);

        launcher.Processes[2].ExitWith(ExitCodes.Success);
        var supervisor = provider.GetRequiredService<ClusterHostSupervisor>();
        await TestWait.UntilAsync(() => supervisor.GetSnapshot("p2").State == SupervisedProcessState.Stopped, "the worker to be left down");
        await Task.Delay(50);

        Assert.Equal(0, lifetime.Stops);
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BelowMinimum_IsNotRaised_WhileAChildIsOnlyBackingOff()
    {
        var launcher = new ScriptedLauncher();
        var lifetime = new FakeLifetime();
        await using ServiceProvider provider = Build(
            Config(min: 3, ("Cluster:Supervisor:Processes:p2:RestartBackoffMinMs", "10"), ("Cluster:Supervisor:Processes:p2:RestartBackoffMaxMs", "10")), launcher, lifetime);
        ClusterSupervisorHostedService hosted = provider.GetServices<IHostedService>().OfType<ClusterSupervisorHostedService>().Single();
        await hosted.StartAsync(CancellationToken.None);
        await launcher.WaitForLaunchesAsync(3);

        launcher.Processes[2].ExitWith(1); // a crash is restarted, so the child still counts
        await launcher.WaitForLaunchesAsync(4);
        await Task.Delay(50);

        Assert.Equal(0, lifetime.Stops);
        await hosted.StopAsync(CancellationToken.None);
    }
}
