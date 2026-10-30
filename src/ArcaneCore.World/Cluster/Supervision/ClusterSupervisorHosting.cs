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
/// admitted ones in start order (<see cref="SupervisedProcessConfig.StartOrder"/>, then key); on stop it drains every child and always disposes the supervisor.
/// </summary>
public sealed class ClusterSupervisorHostedService : IHostedService
{
    private readonly ClusterHostSupervisor _supervisor;
    private readonly ClusterSupervisorOptions _options;
    private readonly ILogger<ClusterSupervisorHostedService> _logger;
    private readonly IHostApplicationLifetime? _lifetime;
    private int _requestedExitCode;

    public ClusterSupervisorHostedService(
        ClusterHostSupervisor supervisor,
        IOptions<ClusterSupervisorOptions> options,
        ILogger<ClusterSupervisorHostedService> logger,
        IHostApplicationLifetime? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _supervisor = supervisor;
        _options = options.Value;
        _logger = logger;
        _lifetime = lifetime;
    }

    /// <summary>The exit code the supervisor process should return: 0, or 1 when it stopped itself because a child was given up on.</summary>
    public int RequestedExitCode => Volatile.Read(ref _requestedExitCode);

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

        _supervisor.BelowMinimum += OnBelowMinimum;
        foreach (string name in plan.Admitted)
        {
            SupervisedProcessSnapshot snapshot = await _supervisor.StartProcessAsync(name, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Supervisor started {Process} (role {Role}, pid {ProcessId})", snapshot.Name, snapshot.Role, snapshot.ProcessId);
        }

        _supervisor.BeginMinimumWatch(); // an exit during the launches above must not stop the supervisor half-started
    }

    private void OnBelowMinimum(SupervisorBelowMinimum report)
    {
        Volatile.Write(ref _requestedExitCode, report.AnyGaveUp ? ExitCodes.Failure : ExitCodes.Success);
        _logger.LogError(
            "Only {Running} supervised process(es) remain, below Cluster:Supervisor:MinClusterProcesses ({Minimum}); stopping the supervisor and the rest ({Outcome}).",
            report.Running, report.Minimum, report.AnyGaveUp ? "a process was given up on" : "all stops were deliberate");
        _lifetime?.StopApplication();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _supervisor.BelowMinimum -= OnBelowMinimum;
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
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

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

        // HostOptions is not bound by the default builder, and appsettings.json ships a 1 minute ShutdownTimeout. The hosted service drains its
        // children one after the other, so the effective timeout is never below the sum of the children's DrainTimeoutMs plus a margin
        // (a larger configured value is kept), capped at one hour.
        services.Configure<HostOptions>(configuration.GetSection("HostOptions"));
        var sized = new ClusterSupervisorOptions();
        configuration.GetSection(ClusterSupervisorOptions.SectionName).Bind(sized);
        TimeSpan needed = sized.TotalDrainBudget + ClusterSupervisorOptions.ShutdownMargin;
        services.PostConfigure<HostOptions>(o => o.ShutdownTimeout = Min(Max(o.ShutdownTimeout, needed), ClusterSupervisorOptions.MaxShutdown));
        services.TryAddSingleton<ISupervisedProcessLauncher, SystemProcessSupervisedLauncher>();
        services.TryAddSingleton<IClusterCapacityProbe, SystemClusterCapacityProbe>();
        services.TryAddSingleton<ISupervisedHangCapture, SystemSupervisedHangCapture>();
        services.AddSingleton(sp => new ClusterHostSupervisor(
            sp.GetRequiredService<IOptions<ClusterSupervisorOptions>>().Value,
            sp.GetRequiredService<ISupervisedProcessLauncher>(),
            capacityProbe: sp.GetRequiredService<IClusterCapacityProbe>(),
            timeProvider: sp.GetService<TimeProvider>(),
            hangCapture: sp.GetRequiredService<ISupervisedHangCapture>()));
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

        try
        {
            await host.WaitForShutdownAsync().ConfigureAwait(false);
            return host.Services.GetServices<IHostedService>().OfType<ClusterSupervisorHostedService>().FirstOrDefault()?.RequestedExitCode ?? ExitCodes.Success;
        }
        catch (Exception ex)
        {
            // Host.StopAsync rethrows a single hosted-service stop failure unchanged, a cancellation included: none may escape Program.cs.
            await error.WriteLineAsync($"ArcaneCore.World supervisor failed while stopping: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Failure;
        }
    }
}
