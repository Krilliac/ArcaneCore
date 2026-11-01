using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.World.Ops.Lifecycle;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Supervision;

/// <summary>
/// <see cref="IWorkerRestartRequester"/> over the cluster host supervisor's line protocol: writes a <see cref="SupervisorLifecycleRequest"/> with
/// <see cref="SupervisorLifecycleAction.Restart"/> (delay 0, <see cref="ExitCodes.Restart"/>) for the realm id the supervisor's configuration gave the worker's
/// child. The supervisor's verdict (forwarded, unknown realm, rejected) comes back asynchronously on this process's stdin and is reported to the staff by the
/// lifecycle feature, so "accepted" here means the request was queued toward a supervisor and the worker maps to a realm id; false means it could not be sent at all.
/// <para>Thread-safe: <see cref="SupervisorProtocolChannel.Send"/> only queues the line.</para>
/// </summary>
public sealed class SupervisorWorkerRestartRequester(
    SupervisorProtocolChannel channel,
    Func<WorkerNodeId, int> realmIdOf,
    ILogger<SupervisorWorkerRestartRequester>? logger = null) : IWorkerRestartRequester
{
    private readonly ILogger _logger = logger ?? NullLogger<SupervisorWorkerRestartRequester>.Instance;

    public Task<bool> RequestRestartAsync(WorkerNodeId worker, string requestedBy, CancellationToken cancellationToken)
    {
        if (!channel.IsSupervised)
        {
            _logger.LogWarning("Restart of {Worker} not requested: this process is not running under a cluster host supervisor", worker);
            return Task.FromResult(false);
        }

        int realmId = realmIdOf(worker);
        if (realmId < 1)
        {
            _logger.LogWarning("Restart of {Worker} not requested: no supervised child with a RealmId is configured for it", worker);
            return Task.FromResult(false);
        }

        var request = new SupervisorLifecycleRequest(channel.Token!, realmId, SupervisorLifecycleAction.Restart, 0, ExitCodes.Restart, requestedBy);
        channel.Send(SupervisorProtocol.FormatLifecycleRequest(request));
        return Task.FromResult(true);
    }

    /// <summary>The realm id of the supervised child named <paramref name="worker"/> (its <see cref="SupervisedProcessConfig.Name"/> or its key), or 0 when none matches.</summary>
    public static int RealmIdOf(ClusterSupervisorOptions options, WorkerNodeId worker)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach ((string key, SupervisedProcessConfig config) in options.Processes)
        {
            if (config.RealmId > 0
                && (string.Equals(config.Name, worker.Value, StringComparison.OrdinalIgnoreCase) || string.Equals(key, worker.Value, StringComparison.OrdinalIgnoreCase)))
            {
                return config.RealmId;
            }
        }

        return 0;
    }
}
