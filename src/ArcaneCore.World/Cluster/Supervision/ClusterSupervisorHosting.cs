using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private string? _selfStopReason;

    /// <summary>How long a start that was refused only for free memory (<see cref="ClusterSupervisorOptions.MinFreeMemoryReserveMb"/>) waits before it plans again.</summary>
    internal static readonly TimeSpan MemoryRetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>The delay seam of the memory wait; tests replace it. Called from the host's start thread only.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = static (delay, ct) => Task.Delay(delay, ct);

    /// <summary>Why the supervisor stopped itself (below its minimum), or null when the stop came from outside (a stop signal). Read after the host has stopped.</summary>
    internal string? SelfStopReason => Volatile.Read(ref _selfStopReason);

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
        // Before anything is planned or launched: children a crashed predecessor left running are adopted or terminated, so no orphan worker keeps its leases.
        SupervisorReconciliationReport reconciled = _supervisor.ReconcileOrphans();
        if (reconciled.Adopted.Count + reconciled.Terminated.Count + reconciled.Failed.Count > 0)
        {
            _logger.LogWarning(
                "Supervisor restart reconciliation: adopted [{Adopted}], terminated [{Terminated}], could not terminate [{Failed}]",
                string.Join(", ", reconciled.Adopted), string.Join(", ", reconciled.Terminated), string.Join(", ", reconciled.Failed));
        }

        SupervisorStartPlan plan = _supervisor.PlanStartAll();
        while (true)
        {
            foreach ((string name, string reason) in plan.Denied)
            {
                _logger.LogWarning("Supervisor will not start process {Process}: {Reason}", name, reason);
            }

            if (plan.Admitted.Count >= _options.MinClusterProcesses)
            {
                break;
            }

            // Free memory comes back (another process exits, a build finishes): a start refused for the reserve waits and plans again, instead of the supervisor
            // exiting and leaving the operator an empty exit code. Any other refusal (process count, map ownership, total allocation) is a configuration
            // error that waiting cannot cure. A stop signal ends the wait (the host cancels the start token).
            if (!plan.Denied.Any(d => ClusterHostSupervisor.IsMemoryReserveRefusal(d.Reason)))
            {
                throw new InvalidOperationException(
                    $"The supervisor can admit {plan.Admitted.Count} of its {_options.Processes.Count} configured process(es), below Cluster:Supervisor:MinClusterProcesses ({_options.MinClusterProcesses}); nothing was launched.");
            }

            _logger.LogWarning(
                "Supervisor waits for free memory: it can admit {Admitted} of its {Configured} configured process(es), below Cluster:Supervisor:MinClusterProcesses ({Minimum}); nothing is launched yet, planning again in {Seconds} s",
                plan.Admitted.Count, _options.Processes.Count, _options.MinClusterProcesses, (int)MemoryRetryInterval.TotalSeconds);
            await DelayAsync(MemoryRetryInterval, cancellationToken).ConfigureAwait(false);
            plan = _supervisor.PlanStartAll();
        }

        _supervisor.BelowMinimum += OnBelowMinimum;
        IReadOnlyList<string> pending = plan.Admitted;
        var launched = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            try
            {
                await _supervisor.StartInGroupsAsync(
                    pending,
                    snapshot =>
                    {
                        launched.Add(snapshot.Name);
                        _logger.LogInformation("Supervisor started {Process} (role {Role}, pid {ProcessId})", snapshot.Name, snapshot.Role, snapshot.ProcessId);
                    },
                    cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (InvalidOperationException ex) when (ClusterHostSupervisor.IsMemoryReserveRefusal(ex.Message))
            {
                // A child already launched took the memory the plan counted on: launch the rest once it is free (the group-ready wait of the group in progress is not repeated).
                pending = [.. pending.Where(n => !launched.Contains(n))];
                _logger.LogWarning("Supervisor waits for free memory before launching {Remaining} more process(es): {Reason} Planning again in {Seconds} s", pending.Count, ex.Message, (int)MemoryRetryInterval.TotalSeconds);
                await DelayAsync(MemoryRetryInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        _supervisor.BeginMinimumWatch(); // an exit during the launches above must not stop the supervisor half-started
    }

    private void OnBelowMinimum(SupervisorBelowMinimum report)
    {
        Volatile.Write(ref _requestedExitCode, report.AnyGaveUp ? ExitCodes.Failure : ExitCodes.Success);
        Volatile.Write(ref _selfStopReason, $"only {report.Running} supervised process(es) remain, below MinClusterProcesses ({report.Minimum}); {(report.AnyGaveUp ? "a process was given up on" : "all stops were deliberate")}");
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
        services.TryAddSingleton(sp => CreateCrashDialog(configuration, sp));
        services.AddSingleton(sp => new ClusterHostSupervisor(
            sp.GetRequiredService<IOptions<ClusterSupervisorOptions>>().Value,
            sp.GetRequiredService<ISupervisedProcessLauncher>(),
            capacityProbe: sp.GetRequiredService<IClusterCapacityProbe>(),
            timeProvider: sp.GetService<TimeProvider>(),
            hangCapture: sp.GetRequiredService<ISupervisedHangCapture>(),
            crashDialog: sp.GetService<SupervisorCrashDialog>(),
            logger: sp.GetService<ILoggerFactory>()?.CreateLogger<ClusterHostSupervisor>(),
            childStateFile: CreateChildStateFile(sp.GetRequiredService<IOptions<ClusterSupervisorOptions>>().Value),
            processInspector: sp.GetService<ISupervisorProcessInspector>()));
        services.AddHostedService<ClusterSupervisorHostedService>();
        return services;
    }

    /// <summary>The state file named by <see cref="ClusterSupervisorOptions.ChildStateFile"/>, or null when that is empty (the record is off).</summary>
    private static SupervisorChildStateFile? CreateChildStateFile(ClusterSupervisorOptions options)
        => string.IsNullOrWhiteSpace(options.ChildStateFile) ? null : new SupervisorChildStateFile(options.ChildStateFile);

    /// <summary>
    /// The supervisor's own crash dialog for children that exit unexpectedly. The supervisor mode never calls <c>UseArcaneDiagnostics</c>, so the
    /// <c>Diagnostics</c> section is read here; a section the binder cannot convert counts as off (start-up validation reports it).
    /// </summary>
    private static SupervisorCrashDialog CreateCrashDialog(IConfiguration configuration, IServiceProvider services)
    {
        bool enabled;
        try
        {
            enabled = configuration.GetSection(DiagnosticsOptions.SectionName).Get<DiagnosticsOptions>()?.CrashDialog ?? false;
        }
        catch (InvalidOperationException)
        {
            enabled = false;
        }

        string? dumpNote = null;
        if (Environment.GetEnvironmentVariable("DOTNET_DbgEnableMiniDump") == "1")
        {
            string? dumpName = Environment.GetEnvironmentVariable("DOTNET_DbgMiniDumpName");
            dumpNote = "DOTNET_DbgEnableMiniDump=1 is set for the children: a crash dump goes to "
                + (string.IsNullOrEmpty(dumpName) ? "the runtime's default location (the temp directory)" : dumpName);
        }

        ILogger logger = (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger("ArcaneCore.Cluster.Supervisor.CrashDialog");
        var dialog = new SupervisorCrashDialog(enabled, logger, dumpNote: dumpNote);
        dialog.LogAvailability();
        return dialog;
    }

    /// <summary>
    /// Runs the supervisor until the host is stopped. Refuses (78) inside a supervised child, so a copied key cannot make a child
    /// supervise again; returns 1 when the start is refused (for example below <c>MinClusterProcesses</c>), else 0. A stop signal that
    /// cancels the start between two launches is a clean stop: the children already launched are drained and 0 is returned.
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
        ILogger log = host.Services.GetService<ILoggerFactory>()?.CreateLogger(ExitLoggerName) ?? NullLogger.Instance;

        // The supervisor mode never calls UseArcaneDiagnostics, so nothing else records why this process ended. Every way out writes the reason and the exit
        // code to the log and to stderr; a crash is written by the unhandled-exception hook and an outside end (console closed, SIGTERM that skips the host's
        // own handler) by the process-exit hook. A hard kill (TerminateProcess, the OOM killer) can be seen by no hook.
        int exitLogged = 0;
        UnhandledExceptionEventHandler onUnhandled = (_, e) =>
        {
            Exception? crash = e.ExceptionObject as Exception;
            WriteExit(log, error, ExitCodes.Failure, $"unhandled exception{(e.IsTerminating ? " (the runtime is terminating the process)" : string.Empty)}: {crash?.GetType().Name}: {crash?.Message}", crash);
        };
        EventHandler onProcessExit = (_, _) =>
        {
            if (Interlocked.Exchange(ref exitLogged, 1) == 0)
            {
                WriteExit(log, error, Environment.ExitCode, "the process was ended from outside before the supervisor reached its own shutdown path (console closed or a termination signal); the code is Environment.ExitCode at that moment", null);
            }
        };
        AppDomain.CurrentDomain.UnhandledException += onUnhandled;
        AppDomain.CurrentDomain.ProcessExit += onProcessExit;
        try
        {
            (int code, string reason) = await RunHostAsync(host, error).ConfigureAwait(false);
            if (Interlocked.Exchange(ref exitLogged, 1) == 0)
            {
                WriteExit(log, error, code, reason, null);
            }

            return code;
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= onUnhandled;
            AppDomain.CurrentDomain.ProcessExit -= onProcessExit;
        }
    }

    /// <summary>The logger category of the supervisor's exit line.</summary>
    internal const string ExitLoggerName = "ArcaneCore.Cluster.Supervisor";

    /// <summary>Writes the supervisor's exit line (the exit code and why) to <paramref name="log"/> and <paramref name="error"/>; never throws.</summary>
    internal static void WriteExit(ILogger log, TextWriter error, int code, string reason, Exception? exception)
    {
        string text = $"ArcaneCore.World supervisor exiting with code {code}: {reason}";
        try
        {
            if (code == ExitCodes.Success)
            {
                log.LogInformation("Supervisor exiting with code {ExitCode}: {Reason}", code, reason);
            }
            else
            {
                log.LogError(exception, "Supervisor exiting with code {ExitCode}: {Reason}", code, reason);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException)
        {
            // The logging providers are already down: stderr below still says it.
        }

        try
        {
            error.WriteLine(text);
            error.Flush();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException)
        {
            // stderr is gone: nothing left to tell.
        }
    }

    private static async Task<(int Code, string Reason)> RunHostAsync(IHost host, TextWriter error)
    {
        try
        {
            await host.StartAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C or SIGTERM during the launches: ApplicationStopping is linked into StartAsync's token, so the next launch throws.
            await error.WriteLineAsync("ArcaneCore.World supervisor start was cancelled by a stop signal; draining the processes already launched.").ConfigureAwait(false);
            try
            {
                await host.StopAsync().ConfigureAwait(false);
            }
            catch (Exception stopEx) when (stopEx is not OutOfMemoryException)
            {
                await error.WriteLineAsync($"ArcaneCore.World supervisor stop after the cancelled start failed: {stopEx.Message}").ConfigureAwait(false);
                return (ExitCodes.Failure, $"the start was cancelled by a stop signal and the stop that followed failed: {stopEx.Message}");
            }

            return (ExitCodes.Success, "the start was cancelled by a stop signal; the processes already launched were drained");
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"ArcaneCore.World supervisor refuses to start: {ex.Message}").ConfigureAwait(false);
            try
            {
                await host.StopAsync().ConfigureAwait(false);
            }
            catch (Exception stopEx) when (stopEx is not OutOfMemoryException)
            {
                await error.WriteLineAsync($"ArcaneCore.World supervisor stop after the refused start failed: {stopEx.Message}").ConfigureAwait(false);
            }

            return (ExitCodes.Failure, $"the start was refused: {ex.Message}");
        }

        try
        {
            await host.WaitForShutdownAsync().ConfigureAwait(false);
            ClusterSupervisorHostedService? service = host.Services.GetServices<IHostedService>().OfType<ClusterSupervisorHostedService>().FirstOrDefault();
            return (service?.RequestedExitCode ?? ExitCodes.Success, service?.SelfStopReason is { } selfStop
                ? "the supervisor stopped itself: " + selfStop
                : "the host was stopped from outside (a stop signal)");
        }
        catch (Exception ex)
        {
            // Host.StopAsync rethrows a single hosted-service stop failure unchanged, a cancellation included: none may escape Program.cs.
            await error.WriteLineAsync($"ArcaneCore.World supervisor failed while stopping: {ex.Message}").ConfigureAwait(false);
            return (ExitCodes.Failure, $"the stop failed: {ex.Message}");
        }
    }
}
