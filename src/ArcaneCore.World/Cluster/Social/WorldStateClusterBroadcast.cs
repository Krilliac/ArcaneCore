using System.Collections.Concurrent;
using ArcaneCore.Game.WorldState.States;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Cluster.Social;

// World states across workers (docs/integration/cluster-realm-state.md, S3e lane 9). A world state belongs to the node that runs its feature (the
// holder: the battleground worker for a match, the lease holder for the realm singletons); the client only learns a change through
// SMSG_UPDATE_WORLD_STATE on a session, and sessions are spread over the workers. The holder therefore publishes each change to its own sessions and
// to every registered worker, which sends the packet to its sessions in the zone. A worker that registers later (or reconnects) is replayed the
// current values so its players do not miss the changes made before.

/// <summary>One world state change (SMSG_UPDATE_WORLD_STATE: state id and value). <paramref name="ZoneId"/> limits the audience to the sessions in that zone; 0 means every session. Immutable.</summary>
public readonly record struct WorldStateChange(uint ZoneId, uint State, uint Value);

/// <summary>How the holder reaches a worker (the peer link, or in process in tests). <see cref="Deliver"/> runs on the holder's world thread and must not block.</summary>
public interface IWorldStateWorkerLink
{
    /// <summary>Send <paramref name="change"/> to <paramref name="workerId"/>, which applies it with <see cref="WorldStateClusterBroadcast.Receive"/>.</summary>
    void Deliver(string workerId, WorldStateChange change);
}

/// <summary>The sessions of this worker a world state packet goes to. Called on this worker's world thread.</summary>
public interface IWorldStateSessionSink
{
    /// <summary>Send <paramref name="payload"/> (an SMSG_UPDATE_WORLD_STATE body) to every local session in <paramref name="zoneId"/>, or to all of them when it is 0.</summary>
    void SendToZone(uint zoneId, WorldOpcode opcode, byte[] payload);
}

/// <summary>
/// The world state broadcast of one node: on the holder <see cref="Publish"/> reaches the local sessions and every registered worker; on a worker
/// <see cref="Receive"/> (any thread) queues what the holder delivered and <see cref="Pump"/> (world thread) sends it to the local sessions.
/// <para>
/// State: <c>_current</c> keeps the latest value per (zone, state) on the holder, for the replay to a registering worker; <c>_workers</c> are the registered
/// worker ids. Thread affinity: <see cref="Publish"/>, <see cref="RegisterWorker"/>, <see cref="UnregisterWorker"/> and <see cref="Pump"/> are world thread
/// only; <see cref="Receive"/> is safe from any thread (a lock-free queue). Nothing here blocks.
/// </para>
/// </summary>
public sealed class WorldStateClusterBroadcast(IWorldStateSessionSink local, IWorldStateWorkerLink? workers = null)
{
    private const int MaxChangesPerPump = 4096;

    private readonly ConcurrentQueue<WorldStateChange> _inbox = new();
    private readonly Dictionary<(uint ZoneId, uint State), uint> _current = [];
    private readonly List<string> _workers = [];

    /// <summary>The workers registered on this holder.</summary>
    public IReadOnlyList<string> Workers => _workers;

    /// <summary>The value the holder last published for a state in a zone, or null when it never did.</summary>
    public uint? CurrentValue(uint zoneId, uint state) => _current.TryGetValue((zoneId, state), out uint value) ? value : null;

    /// <summary>
    /// Holder: a worker joined the broadcast. It is replayed every value published so far (so a late or reconnecting worker's players see the current
    /// state), then receives every later change. Registering a worker twice replays again and keeps one entry.
    /// </summary>
    public void RegisterWorker(string workerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(workerId);
        if (workers is null)
        {
            throw new InvalidOperationException("this broadcast has no worker link, so it cannot serve workers");
        }

        if (!_workers.Contains(workerId))
        {
            _workers.Add(workerId);
        }

        foreach (KeyValuePair<(uint ZoneId, uint State), uint> entry in _current.OrderBy(e => e.Key.ZoneId).ThenBy(e => e.Key.State))
        {
            workers.Deliver(workerId, new WorldStateChange(entry.Key.ZoneId, entry.Key.State, entry.Value));
        }
    }

    /// <summary>Holder: a worker left (its link closed); it receives nothing more.</summary>
    public bool UnregisterWorker(string workerId) => _workers.Remove(workerId);

    /// <summary>
    /// Holder: a world state changed. The local sessions and every registered worker are told, unless the value is the one already published (the
    /// client has it). A worker whose delivery throws is recorded and skipped so one broken link does not stall the others.
    /// </summary>
    public void Publish(WorldStateChange change)
    {
        if (_current.TryGetValue((change.ZoneId, change.State), out uint known) && known == change.Value)
        {
            return;
        }

        _current[(change.ZoneId, change.State)] = change.Value;
        Apply(change);
        if (workers is null)
        {
            return;
        }

        foreach (string workerId in _workers.ToArray())
        {
            try
            {
                workers.Deliver(workerId, change);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("worldstate.cluster-broadcast", ex);
                AnomalyLedger.Shared.Report("worldstate", "feature-fault", "cluster-deliver", $"worker={workerId} error={ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Worker: queue a change the holder delivered (any thread).</summary>
    public void Receive(WorldStateChange change) => _inbox.Enqueue(change);

    /// <summary>Worker: send the queued changes to the local sessions, at most <see cref="MaxChangesPerPump"/> (world thread). Returns how many were sent.</summary>
    public int Pump()
    {
        int sent = 0;
        while (sent < MaxChangesPerPump && _inbox.TryDequeue(out WorldStateChange change))
        {
            sent++;
            try
            {
                Apply(change);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("worldstate.cluster-broadcast", ex);
                AnomalyLedger.Shared.Report("worldstate", "feature-fault", "cluster-apply", $"error={ex.GetType().Name}: {ex.Message}");
            }
        }

        return sent;
    }

    private void Apply(WorldStateChange change)
        => local.SendToZone(change.ZoneId, WorldOpcode.SmsgUpdateWorldState, WorldStatePackets.BuildUpdate(change.State, change.Value));
}
