using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Cluster.Transfer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Supervision;

/// <summary>
/// Drains one worker by the seamless whole-World handoff (<see cref="WorldHandoffCoordinator"/>): its players move to the warmed standby and their
/// gateway sessions are rerouted, so nobody is disconnected. Resolves only when the handoff finished (the old worker retired) or threw.
/// </summary>
public interface IWorkerHandoffDriver
{
    /// <summary>Run the handoff of <paramref name="worker"/> to completion. Throws <see cref="WorldHandoffAbortedException"/> when it aborted before the takeover (the worker kept its players).</summary>
    Task<WorldHandoffRecord> HandoffAsync(WorkerNodeId worker, CancellationToken cancellationToken);
}

/// <summary>Asks the cluster host supervisor to restart the (already drained and retired) worker process.</summary>
public interface IWorkerRestartRequester
{
    /// <summary>True when the request was queued toward a supervisor; false when it could not be sent. It does not wait for the supervisor's verdict (unknown realm, not running), which comes back asynchronously on the lifecycle channel.</summary>
    Task<bool> RequestRestartAsync(WorkerNodeId worker, string requestedBy, CancellationToken cancellationToken);
}

/// <summary>How an operator restart of a worker ended.</summary>
public enum ClusterRestartOutcome
{
    /// <summary>The invoker is below Administrator; nothing was done.</summary>
    Refused,

    /// <summary>The placement table does not know the worker; nothing was done.</summary>
    UnknownWorker,

    /// <summary>This node has no handoff coordinator configured (no standby target or router, or not an active worker): unavailable, nothing was done.</summary>
    HandoffUnavailable,

    /// <summary>The handoff aborted before the epoch takeover: the worker kept its players and keeps running; no restart was requested.</summary>
    HandoffAborted,

    /// <summary>The handoff threw after it began and did not finish (it may be committed and left for recovery); no restart was requested.</summary>
    HandoffFailed,

    /// <summary>The handoff finished but the supervisor did not accept the restart; the retired worker was not restarted.</summary>
    RestartNotAccepted,

    /// <summary>The worker was handed off (sessions kept) and the restart request was queued toward the supervisor. The supervisor's verdict arrives later on the lifecycle channel and is reported to the staff there; this is not a confirmed restart.</summary>
    RestartQueued,
}

/// <summary>The result of <see cref="ClusterRestartOrchestration.RunAsync"/>: the outcome, one operator-facing line and the handoff journal record when one ran.</summary>
public sealed record ClusterRestartResult(ClusterRestartOutcome Outcome, string Message, WorldHandoffRecord? Handoff);

/// <summary>
/// The operator-triggered restart of a worker (<c>.cluster restart &lt;worker&gt;</c>): authenticate (Administrator or above), drain the worker by handoff so
/// its sessions survive, and only then ask the supervisor to restart the retired process. The restart is requested strictly after a finished handoff:
/// an aborted or failed handoff never restarts the worker, because killing a worker that still holds players would disconnect them.
/// Stateless and thread-safe; the collaborators own their own threading. Runs on any thread (the command calls it off the world thread).
/// </summary>
public sealed class ClusterRestartOrchestration(
    IWorkerHandoffDriver handoff,
    IWorkerRestartRequester restart,
    ClusterMapPlacementTable? placement = null,
    ILogger<ClusterRestartOrchestration>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<ClusterRestartOrchestration>.Instance;

    public async Task<ClusterRestartResult> RunAsync(
        AccountSecurity invokerSecurity, string invokerName, string workerName, CancellationToken cancellationToken = default)
    {
        if (invokerSecurity < AccountSecurity.Administrator)
        {
            return new ClusterRestartResult(ClusterRestartOutcome.Refused, "This command is not available to you.", null);
        }

        var worker = new WorkerNodeId(workerName.Trim());
        if (worker.IsEmpty)
        {
            return new ClusterRestartResult(ClusterRestartOutcome.UnknownWorker, "No worker was named.", null);
        }

        if (worker.Value.Any(char.IsWhiteSpace))
        {
            return new ClusterRestartResult(ClusterRestartOutcome.UnknownWorker, "A worker name has no whitespace; nothing was done.", null);
        }

        if (placement is not null && placement.SnapshotWorkers().Count > 0)
        {
            if (!placement.TryGetWorker(worker, out _))
            {
                return new ClusterRestartResult(ClusterRestartOutcome.UnknownWorker, $"Unknown worker '{worker}'; nothing was done.", null);
            }
        }
        else
        {
            _logger.LogWarning(
                "Operator restart of {Worker} requested by {By}: no placement table with registered workers is available; the worker name was not checked against the cluster",
                worker, invokerName);
        }

        WorldHandoffRecord record;
        try
        {
            record = await handoff.HandoffAsync(worker, cancellationToken).ConfigureAwait(false);
        }
        catch (WorldHandoffAbortedException ex)
        {
            _logger.LogWarning(ex, "Operator restart of {Worker} requested by {By}: the handoff aborted; the worker keeps its players", worker, invokerName);
            return new ClusterRestartResult(
                ClusterRestartOutcome.HandoffAborted,
                $"Handoff of '{worker}' aborted ({ex.Record.AbortReason}); the worker keeps running with its players and was not restarted.",
                ex.Record);
        }
        catch (WorldHandoffUnavailableException ex)
        {
            _logger.LogWarning("Operator restart of {Worker} requested by {By}: no handoff is configured on this node ({Reason})", worker, invokerName, ex.Message);
            return new ClusterRestartResult(
                ClusterRestartOutcome.HandoffUnavailable,
                $"Cluster restart is unavailable: no worker handoff is configured on this node ({ex.Message}); nothing was done.",
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("cluster.operator-restart-handoff", ex, "Operator restart of {Worker} requested by {By}: the handoff failed", worker, invokerName);
            return new ClusterRestartResult(
                ClusterRestartOutcome.HandoffFailed,
                $"Handoff of '{worker}' failed ({ex.GetType().Name}); the worker was not restarted. Check the handoff journal before retrying.",
                null);
        }

        if (record.Stage != WorldHandoffStage.Retired)
        {
            return new ClusterRestartResult(
                ClusterRestartOutcome.HandoffFailed,
                $"Handoff of '{worker}' ended at stage {record.Stage}, not retired; the worker was not restarted.",
                record);
        }

        bool accepted;
        try
        {
            accepted = await restart.RequestRestartAsync(worker, invokerName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The handoff is complete and cannot be undone: report that, never "cancelled".
            _logger.LogWarning("Operator restart of {Worker} requested by {By}: cancelled after the handoff finished; the restart was not requested", worker, invokerName);
            accepted = false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("cluster.operator-restart-request", ex, "Operator restart of {Worker} requested by {By}: the supervisor request failed", worker, invokerName);
            accepted = false;
        }

        return accepted
            ? new ClusterRestartResult(ClusterRestartOutcome.RestartQueued, $"Worker '{worker}' was handed off (sessions kept) and a restart was queued toward the supervisor; its verdict is reported separately.", record)
            : new ClusterRestartResult(ClusterRestartOutcome.RestartNotAccepted, $"Worker '{worker}' was handed off (sessions kept) but its restart was not queued (not supervised, no realm id, or the host is stopping).", record);
    }
}

/// <summary>
/// Production registration of <see cref="ClusterRestartOrchestration"/> and its two seams. Registered only when <c>Cluster:Supervisor:Processes</c> names at
/// least one child (a node that belongs to a supervised cluster); elsewhere <c>.cluster restart</c> keeps answering "unavailable". <c>TryAdd</c>: a driver,
/// requester or orchestration registered earlier (a test, a custom host) wins.
/// </summary>
public static class ClusterRestartHosting
{
    /// <summary>Registers the handoff driver, the supervisor restart requester and the orchestration. Call once while building the world's services.</summary>
    public static IServiceCollection AddClusterRestart(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.GetSection(ClusterSupervisorOptions.SectionName + ":Processes").GetChildren().Any())
        {
            return services;
        }

        services.TryAddSingleton<IWorkerHandoffDriver>(sp => new CoordinatorWorkerHandoffDriver(
            () => sp.GetService<WorldHandoffCoordinator>() ?? sp.GetService<WorldHandoffCoordinatorProvider>()?.Coordinator,
            () => sp.GetService<IWorldHandoffSource>(),
            sp.GetService<WorldHandoffInFlight>()));
        services.TryAddSingleton<IWorkerRestartRequester>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ClusterSupervisorOptions>>();
            return new SupervisorWorkerRestartRequester(
                sp.GetService<ArcaneCore.World.Ops.Lifecycle.SupervisorProtocolChannel>() ?? ArcaneCore.World.Ops.Lifecycle.SupervisorProtocolChannel.Process,
                worker => SupervisorWorkerRestartRequester.RealmIdOf(options.Value, worker),
                sp.GetService<ILoggerFactory>()?.CreateLogger<SupervisorWorkerRestartRequester>());
        });
        services.TryAddSingleton(sp => new ClusterRestartOrchestration(
            sp.GetRequiredService<IWorkerHandoffDriver>(),
            sp.GetRequiredService<IWorkerRestartRequester>(),
            sp.GetService<ClusterMapPlacementTable>() ?? sp.GetService<ArcaneCore.World.Cluster.Tooling.ClusterNodeBridgeFeature>()?.Placement,
            sp.GetService<ILoggerFactory>()?.CreateLogger<ClusterRestartOrchestration>()));
        return services;
    }
}
