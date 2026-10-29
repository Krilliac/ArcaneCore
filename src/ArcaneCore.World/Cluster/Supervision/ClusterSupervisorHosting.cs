using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Cluster.Supervision;

/// <summary>
/// Hosted face of <see cref="ClusterHostSupervisor"/> (M15.4): on start it plans the whole start, refuses (launching nothing)
/// when fewer than <see cref="ClusterSupervisorOptions.MinClusterProcesses"/> processes would be admitted, then launches the
/// admitted ones in configuration order; on stop it drains every child and always disposes the supervisor.
/// </summary>
public sealed class ClusterSupervisorHostedService : IHostedService
{
    private readonly ClusterHostSupervisor _supervisor;
    private readonly ClusterSupervisorOptions _options;
    private readonly ILogger<ClusterSupervisorHostedService> _logger;

    public ClusterSupervisorHostedService(
        ClusterHostSupervisor supervisor,
        IOptions<ClusterSupervisorOptions> options,
        ILogger<ClusterSupervisorHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _supervisor = supervisor;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        SupervisorStartPlan plan = _supervisor.PlanStartAll();
        foreach ((string name, string reason) in plan.Denied)
        {
            _logger.LogWarning("Supervisor will not start process {Process}: {Reason}", name, reason);
        }

        if (plan.Admitted.Count < _options.MinClusterProcesses)
        {
            throw new InvalidOperationException(
                $"The supervisor can admit {plan.Admitted.Count} of its {_options.Processes.Count} configured process(es), below Cluster:Supervisor:MinClusterProcesses ({_options.MinClusterProcesses}); nothing was launched.");
        }

        foreach (string name in plan.Admitted)
        {
            SupervisedProcessSnapshot snapshot = await _supervisor.StartProcessAsync(name, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Supervisor started {Process} (role {Role}, pid {ProcessId})", snapshot.Name, snapshot.Role, snapshot.ProcessId);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _supervisor.DrainAndStopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _supervisor.Dispose();
        }
    }
}

/// <summary>
/// Entry points of the supervisor mode of the World executable (<c>Cluster:Supervisor:Enabled</c>). This mode registers none of the
/// world's services: no database, listener, feature or world runtime is ever built (<c>AddWorldProductionServices</c> is not called).
/// </summary>
public static class ClusterSupervisorHosting
{
    /// <summary>Whether <c>Cluster:Supervisor:Enabled</c> is true in <paramref name="configuration"/>.</summary>
    public static bool IsEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetSection(ClusterSupervisorOptions.SectionName).GetValue<bool>(nameof(ClusterSupervisorOptions.Enabled));
    }

    /// <summary>
    /// Registers the supervisor and its hosted service. A launcher or capacity probe registered earlier wins (<c>TryAdd</c>).
    /// </summary>
    public static IServiceCollection AddClusterSupervisor(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddArcaneCoreLogging(configuration);
        services.Configure<ClusterSupervisorOptions>(configuration.GetSection(ClusterSupervisorOptions.SectionName));
        services.TryAddSingleton<ISupervisedProcessLauncher, SystemProcessSupervisedLauncher>();
        services.TryAddSingleton<IClusterCapacityProbe, SystemClusterCapacityProbe>();
        services.AddSingleton(sp => new ClusterHostSupervisor(
            sp.GetRequiredService<IOptions<ClusterSupervisorOptions>>().Value,
            sp.GetRequiredService<ISupervisedProcessLauncher>(),
            capacityProbe: sp.GetRequiredService<IClusterCapacityProbe>()));
        services.AddHostedService<ClusterSupervisorHostedService>();
        return services;
    }

    /// <summary>
    /// Runs the supervisor until the host is stopped. Refuses (78) inside a supervised child, so a copied key cannot make a child
    /// supervise again; returns 1 when the start is refused (for example below <c>MinClusterProcesses</c>), else 0.
    /// </summary>
    public static async Task<int> RunAsync(HostApplicationBuilder builder, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(error);

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SupervisedChild.EnvironmentVariable)))
        {
            await error.WriteLineAsync(
                $"ArcaneCore.World refuses to start: Cluster:Supervisor:Enabled is true in a process the supervisor launched ({SupervisedChild.EnvironmentVariable} is set); " +
                "a supervised child must not supervise. Remove Cluster:Supervisor:Enabled from the child's configuration.").ConfigureAwait(false);
            return ExitCodes.InvalidConfiguration; // EX_CONFIG: a restart would fail the same way
        }

        builder.Services.AddClusterSupervisor(builder.Configuration);
        using IHost host = builder.Build();
        try
        {
            await host.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await error.WriteLineAsync($"ArcaneCore.World supervisor refuses to start: {ex.Message}").ConfigureAwait(false);
            try
            {
                await host.StopAsync().ConfigureAwait(false);
            }
            catch (Exception stopEx) when (stopEx is not OperationCanceledException)
            {
                await error.WriteLineAsync($"ArcaneCore.World supervisor stop after the refused start failed: {stopEx.Message}").ConfigureAwait(false);
            }

            return ExitCodes.Failure;
        }

        await host.WaitForShutdownAsync().ConfigureAwait(false);
        return ExitCodes.Success;
    }
}
