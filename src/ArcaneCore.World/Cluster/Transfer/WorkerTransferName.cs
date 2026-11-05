using ArcaneCore.Kernel.Cluster.Transfer;

namespace ArcaneCore.World.Cluster.Transfer;

/// <summary>
/// The name this process's transfer participants answer under. Journal rows, bundles and transfer verbs name a worker by its slot (<c>Gateway:Workers:N:NodeId</c>), and a process that took a slot over keeps its own
/// <c>Cluster:Worker:NodeId</c>, so the name starts as the process's own and the gateway replaces it (<see cref="Control.WorkerControlService.ServeSlot"/>). The participants are built before the first link is up,
/// which is why they read the name when they use it. Thread-safe: a link's receive loop writes it, any thread reads it.
/// </summary>
public sealed class WorkerTransferName(WorkerNodeId own)
{
    private string _value = own.Value;

    /// <summary>The name now.</summary>
    public WorkerNodeId Current => new(Volatile.Read(ref _value));

    /// <summary>Answers under <paramref name="slot"/> from now on. True when that changed the name.</summary>
    public bool Assign(WorkerNodeId slot) => !string.Equals(Interlocked.Exchange(ref _value, slot.Value), slot.Value, StringComparison.Ordinal);
}
