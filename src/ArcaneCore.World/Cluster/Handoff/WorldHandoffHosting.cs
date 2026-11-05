using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Social;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Cluster.Handoff;

/// <summary>
/// The one way worker names are compared by the handoff driver, the supervisor restart requester and the placement table: ordinal, case-sensitive.
/// <see cref="WorkerNodeId"/> is a record struct over a string, so the placement table's dictionary already compares ordinally; the driver and the requester follow it,
/// so a name the placement accepts is the name the driver drains and the requester restarts (and never a differently cased sibling).
/// </summary>
public static class WorkerNodeNames
{
    /// <summary>The comparer for worker names.</summary>
    public static StringComparer Comparer => StringComparer.Ordinal;

    /// <summary>True when both ids name the same worker.</summary>
    public static bool Equal(WorkerNodeId a, WorkerNodeId b) => Comparer.Equals(a.Value, b.Value);
}

/// <summary>This node has no whole-World handoff to run (no coordinator or no source registered): the operator restart answers "unavailable / not configured", not a failed handoff.</summary>
public sealed class WorldHandoffUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// The handoffs running on this node. A handoff past its epoch takeover is not cancellable (the coordinator ignores the caller's token from then on), so the host
/// must not stop under it: <see cref="WorldHandoffRecoveryService.StopAsync"/> waits for <see cref="WhenIdleAsync"/> (bounded by the host's shutdown timeout; a handoff
/// that still did not finish is rolled forward by <see cref="WorldHandoffCoordinator.RecoverAsync"/> on the next start). Thread-safe.
/// </summary>
public sealed class WorldHandoffInFlight
{
    private readonly Lock _gate = new();
    private readonly List<Task> _running = [];

    /// <summary>Remember <paramref name="task"/> until it completes (any outcome); returns it unchanged.</summary>
    public Task<T> Track<T>(Task<T> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
        {
            _running.Add(task);
        }

        _ = task.ContinueWith(
            finished =>
            {
                lock (_gate)
                {
                    _running.Remove(finished);
                }
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    /// <summary>The number of handoffs still running.</summary>
    public int Count
    {
        get
        {
            // A task that has completed is no longer running even if its removal continuation has not run yet (it can lag the awaiting waiter on the thread pool). Counted, not removed: a getter changes
            // nothing, and the continuation of Track is the only thing that removes a task.
            lock (_gate)
            {
                int running = 0;
                foreach (Task task in _running)
                {
                    if (!task.IsCompleted)
                        running++;
                }

                return running;
            }
        }
    }

    /// <summary>Completes when every handoff running now has ended (faulted ones included); throws only when <paramref name="cancellationToken"/> is cancelled.</summary>
    public async Task WhenIdleAsync(CancellationToken cancellationToken)
    {
        Task[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _running];
        }

        foreach (Task task in snapshot)
        {
            try
            {
                await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The handoff's own outcome belongs to its caller; this only waits for it to end.
            }
        }
    }
}

/// <summary>
/// Builds the production <see cref="WorldHandoffCoordinator"/> from the participants the node registered: the journal (<see cref="IWorldHandoffStore"/>, the
/// characters database's <c>EfTransferDecisionStore</c> with its per-scope handoff), the draining <see cref="IWorldHandoffSource"/> (registered by
/// <see cref="WorldHandoffHosting.AddWorldHandoff"/> for every worker; a standby resolves it only after its promotion), the warmed standby <see cref="IWorldHandoffTarget"/> and the
/// <see cref="IWorldHandoffRouter"/> (the gateway's router). The coordinator exists only when all four resolve: a node with no standby configured has no coordinator and
/// <see cref="Coordinator"/> is null, which the driver reports as unavailable. Resolved on first use, so participants registered after this object was built still count.
/// Thread-safe; the coordinator is stateless between handoffs.
/// </summary>
public sealed class WorldHandoffCoordinatorProvider(IServiceProvider services)
{
    private readonly Lazy<WorldHandoffCoordinator?> _coordinator = new(() => Build(services), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The coordinator, or null when this node has no standby target or router (or no journal / source).</summary>
    public WorldHandoffCoordinator? Coordinator => _coordinator.Value;

    /// <summary>The journal, or null when none is registered.</summary>
    public IWorldHandoffStore? Store => services.GetService<IWorldHandoffStore>();

    /// <summary>The draining source, or null when none is registered.</summary>
    public IWorldHandoffSource? Source => services.GetService<IWorldHandoffSource>();

    private static WorldHandoffCoordinator? Build(IServiceProvider services)
    {
        IWorldHandoffStore? store = services.GetService<IWorldHandoffStore>();
        IWorldHandoffSource? source = services.GetService<IWorldHandoffSource>();
        IWorldHandoffTarget? target = services.GetService<IWorldHandoffTarget>();
        IWorldHandoffRouter? router = services.GetService<IWorldHandoffRouter>();
        if (store is null || source is null || target is null || router is null)
        {
            return null;
        }

        return new WorldHandoffCoordinator(store, source, target, router, services.GetService<ILoggerFactory>()?.CreateLogger<WorldHandoffCoordinator>());
    }
}

/// <summary>
/// At host start, finishes this worker's interrupted whole-World handoffs from the journal through <see cref="WorldHandoffCoordinator.RecoverAsync"/>: one that had not
/// taken the epoch over aborts and resumes the worker; one that had rolls forward (promote the standby, reroute the gateway, retire). The recovery runs in the background
/// (a roll-forward waits on the standby and the gateway, which must not hold the host's start); <see cref="RecoveryCompleted"/> is its task and never faults. On stop
/// this waits (bounded by the host's shutdown token) for the recovery and for any handoff still running, and never cancels either: after the epoch takeover a handoff
/// finishes or is recovered on the next start. Does nothing when the node has no coordinator.
/// </summary>
public sealed class WorldHandoffRecoveryService(
    WorldHandoffCoordinatorProvider provider,
    WorldHandoffInFlight inFlight,
    ILogger<WorldHandoffRecoveryService>? logger = null) : IHostedService
{
    private readonly ILogger _logger = logger ?? NullLogger<WorldHandoffRecoveryService>.Instance;
    private Task _recovery = Task.CompletedTask;

    /// <summary>Completes when the start-up recovery pass ended (a failure is logged, not thrown).</summary>
    public Task RecoveryCompleted => _recovery;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        WorldHandoffCoordinator? coordinator = provider.Coordinator;
        IWorldHandoffStore? store = provider.Store;
        IWorldHandoffSource? source = provider.Source;
        if (coordinator is null || store is null || source is null)
        {
            _logger.LogDebug("No world handoff coordinator on this node; nothing to recover");
            return Task.CompletedTask;
        }

        _recovery = Task.Run(() => RecoverAllAsync(coordinator, store, source));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _recovery.WaitAsync(cancellationToken).ConfigureAwait(false);
            await inFlight.WhenIdleAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("The host stopped while a world handoff was still finishing; it will be recovered from its journal on the next start");
        }
    }

    private async Task RecoverAllAsync(WorldHandoffCoordinator coordinator, IWorldHandoffStore store, IWorldHandoffSource source)
    {
        try
        {
            IReadOnlyList<WorldHandoffRecord> unresolved = await store.GetUnresolvedAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (WorldHandoffRecord record in unresolved)
            {
                // Another shard's handoff is recovered by that shard's own node.
                if (!WorkerNodeNames.Equal(record.SourceWorkerId, source.WorkerId))
                {
                    continue;
                }

                try
                {
                    WorldHandoffRecord done = await coordinator.RecoverAsync(record.HandoffId, CancellationToken.None).ConfigureAwait(false);
                    _logger.LogInformation("Recovered world handoff {Handoff} at start: now {Stage}", record.HandoffId, done.Stage);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogFault("cluster.handoff-recovery", ex, "Recovering world handoff {Handoff} at start failed; it stays journalled for the next start", record.HandoffId);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("cluster.handoff-recovery", ex, "Reading the unresolved world handoffs at start failed");
        }
    }
}

/// <summary>
/// Production registration of the whole-World handoff for a worker node. Registered only in worker mode (<c>Cluster:Worker:Enabled</c>) (on a standby only the source, resolved after its promotion). <c>TryAdd</c>: a source, provider or in-flight tracker registered earlier (a test, a custom host) wins.
/// <para>
/// Registers the draining source over this process's own world (<see cref="LocalWorldHandoffSource"/>), the coordinator provider and the start-up recovery. The journal comes
/// from the characters data module (<c>EfTransferDecisionStore</c>). The standby target (<see cref="IWorldHandoffTarget"/>) and the router (<see cref="IWorldHandoffRouter"/>,
/// the gateway's) are registered by the host that holds a warmed standby and the gateway; until both are registered the provider has no coordinator and an operator restart
/// answers "unavailable" (see <c>docs/design/hot-reload-tiers.md</c> section 5.6: there is no cross-process control channel yet).
/// </para>
/// </summary>
public static class WorldHandoffHosting
{
    /// <summary>Register the handoff source, provider and recovery. Call once while building the world's services, after the world host and the gateway worker.</summary>
    public static IServiceCollection AddWorldHandoff(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection worker = configuration.GetSection(GatewayWorkerOptions.SectionName);
        if (!worker.GetValue<bool>(nameof(GatewayWorkerOptions.Enabled)))
        {
            return services;
        }

        // The source is registered for a standby too, but only resolved once it is promoted (WorkerTransferHosting hands it to the control service then): a standby is the handoff's
        // target while it waits, and the draining source of the next handoff after its promotion. The coordinator provider, the in-flight tracker and the start-up recovery stay off a standby, so a promoted
        // standby differs from a native active worker: it cannot coordinate or recover a handoff locally; the gateway drives the control verbs against it (docs/design/hot-reload-tiers.md section 5.6).
        services.TryAddSingleton<IWorldHandoffSource>(sp => new LocalWorldHandoffSource(
            new WorkerNodeId(sp.GetRequiredService<IOptions<GatewayWorkerOptions>>().Value.NodeId),
            sp.GetRequiredService<WorldRuntime>(),
            sp.GetRequiredService<CharacterSaveQueue>(),
            sp.GetRequiredService<WorkerAdmission>(),
            sp.GetRequiredService<WorldWriterGuard>(),
            sp.GetService<CharacterPersistenceBarrier>(),
            sp.GetService<CharacterRetirementRegistry>(),
            sp.GetService<SocialGroupPersistenceFeature>(),
            sp.GetService<WarEffortFeature>(),
            sp.GetService<ScourgeInvasionFeature>()));
        if (worker.GetValue<bool>(nameof(GatewayWorkerOptions.Standby)))
        {
            return services;
        }

        services.TryAddSingleton<WorldHandoffInFlight>();
        services.TryAddSingleton(sp => new WorldHandoffCoordinatorProvider(sp));
        services.AddHostedService(sp => new WorldHandoffRecoveryService(
            sp.GetRequiredService<WorldHandoffCoordinatorProvider>(),
            sp.GetRequiredService<WorldHandoffInFlight>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<WorldHandoffRecoveryService>()));
        return services;
    }
}
