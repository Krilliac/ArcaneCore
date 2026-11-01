using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster.Supervision;

namespace ArcaneCore.World.Cluster.Handoff;

/// <summary>
/// <see cref="IWorkerHandoffDriver"/> over the node's <see cref="WorldHandoffCoordinator"/>: runs one whole-World handoff to completion for the worker
/// whose <see cref="IWorldHandoffSource"/> the coordinator drains. A coordinator is bound to one source/target pair, so this driver only serves that source's worker;
/// any other worker name is refused before the journal is touched. The coordinator and source are resolved lazily (per call) because they are only registered on a node that
/// carries the handoff participants.
/// <para>Thread-safe and stateless: each call journals its own handoff id (the store refuses a second unresolved handoff of the same scope). Runs on the caller's thread pool task.</para>
/// </summary>
public sealed class CoordinatorWorkerHandoffDriver(Func<WorldHandoffCoordinator?> coordinator, Func<IWorldHandoffSource?> source, WorldHandoffInFlight? inFlight = null) : IWorkerHandoffDriver
{
    public Task<WorldHandoffRecord> HandoffAsync(WorkerNodeId worker, CancellationToken cancellationToken)
    {
        WorldHandoffCoordinator coordinatorInstance = coordinator()
            ?? throw new WorldHandoffUnavailableException("this node has no world handoff coordinator configured (it needs a standby target and a gateway router)");
        IWorldHandoffSource sourceInstance = source()
            ?? throw new WorldHandoffUnavailableException("this node has no world handoff source configured (it is not an active worker)");
        if (!WorkerNodeNames.Equal(sourceInstance.WorkerId, worker))
        {
            throw new InvalidOperationException($"this node's handoff coordinator drains '{sourceInstance.WorkerId}', not '{worker}'");
        }

        // The token cancels the handoff only before its epoch takeover (the coordinator ignores it afterwards); a tracked handoff is awaited by the host's stop.
        Task<WorldHandoffRecord> run = coordinatorInstance.RunAsync(Guid.NewGuid(), afterStage: null, cancellationToken);
        return inFlight?.Track(run) ?? run;
    }
}
