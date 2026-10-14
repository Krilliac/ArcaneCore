using ArcaneCore.Kernel.Cluster.Tooling;

namespace ArcaneCore.World.Cluster.Tooling;

/// <summary>Snapshot of cluster state inspected before allowing a local combined-mode World to claim the realm (M15.5).</summary>
public sealed record ClusterRollbackSnapshot(
    int RealmId,
    IReadOnlyList<ClusterNodeDescriptor> ClusterNodes,
    int PendingTransfers,
    int PendingCharacterWrites,
    int RetainedWriteFailures,
    int ActiveUnfencedWriters,
    int ActiveClusterOwnershipLeases,
    int AuthSchemaVersion,
    int CharactersSchemaVersion,
    int WorldSchemaVersion,
    int ExpectedAuthSchemaVersion,
    int MinimumLocalCharactersSchemaVersion,
    int MaximumLocalCharactersSchemaVersion,
    int ExpectedWorldSchemaVersion);

/// <summary>Result of evaluating whether a realm can safely roll back from cluster hosting to local combined hosting.</summary>
public sealed record ClusterRollbackVerdict(
    bool SafeToRollback,
    string Code,
    IReadOnlyList<string> BlockingReasons)
{
    public static ClusterRollbackVerdict Allow() =>
        new(true, "safe_to_rollback", []);

    public static ClusterRollbackVerdict Block(string code, IReadOnlyList<string> blockingReasons) =>
        new(false, code, blockingReasons);
}

/// <summary>
/// Enforces the M15.5 cluster-to-local rollback safety gate (<c>docs/CLUSTERING_DESIGN.md:594-598</c>):
/// drains cluster ownership, verifies zero active/starting cluster nodes, zero pending transfers, zero pending or retained writes,
/// zero concurrent unfenced writers alongside cluster ownership leases, and explicit schema compatibility before local mode claims the realm.
/// </summary>
public sealed class ClusterRollbackCoordinator
{
    /// <summary>Verifies that the realm described by <paramref name="snapshot"/> is safe for local combined-mode takeover.</summary>
    public ClusterRollbackVerdict VerifySafeRollbackToLocal(ClusterRollbackSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var reasons = new List<string>();

        if (snapshot.RealmId <= 0)
            reasons.Add("RealmId must be positive.");

        if (snapshot.ClusterNodes is null)
        {
            reasons.Add("ClusterNodes list must not be null.");
        }
        else
        {
            ClusterNodeDescriptor[] undrained = snapshot.ClusterNodes
                .Where(n => !n.IsDrainedOrStopped)
                .ToArray();

            if (undrained.Length > 0)
            {
                string summary = string.Join(", ", undrained.Select(n => $"{n.NodeId}({n.Role}:{n.State})"));
                reasons.Add($"Cluster nodes are not all Drained or Stopped: {summary}.");
            }
        }

        if (snapshot.PendingTransfers != 0)
        {
            reasons.Add($"Pending cross-worker transfers remain ({snapshot.PendingTransfers}); wait for transfers to complete or abort.");
        }

        if (snapshot.PendingCharacterWrites != 0)
        {
            reasons.Add($"Pending character/instance persistence writes remain ({snapshot.PendingCharacterWrites}); flush all write queues before rollback.");
        }

        if (snapshot.RetainedWriteFailures != 0)
        {
            reasons.Add($"Retained write failures remain ({snapshot.RetainedWriteFailures}); resolve or recover failed writes before rollback.");
        }

        if (snapshot.ActiveClusterOwnershipLeases != 0)
        {
            reasons.Add($"Active cluster ownership leases remain ({snapshot.ActiveClusterOwnershipLeases}); release or drain cluster ownership first.");
        }

        if (snapshot.ActiveUnfencedWriters > 0)
        {
            reasons.Add($"Active unfenced writers detected ({snapshot.ActiveUnfencedWriters}); never run unfenced writers alongside cluster writers or before drain completes.");
        }

        if (snapshot.AuthSchemaVersion <= 0 || snapshot.AuthSchemaVersion != snapshot.ExpectedAuthSchemaVersion)
        {
            reasons.Add(
                $"Auth schema v{snapshot.AuthSchemaVersion} is incompatible with local combined mode (expected v{snapshot.ExpectedAuthSchemaVersion}).");
        }

        if (snapshot.CharactersSchemaVersion <= 0
            || snapshot.CharactersSchemaVersion < snapshot.MinimumLocalCharactersSchemaVersion
            || snapshot.CharactersSchemaVersion > snapshot.MaximumLocalCharactersSchemaVersion)
        {
            reasons.Add(
                $"Characters schema v{snapshot.CharactersSchemaVersion} is outside local combined-mode additive compatibility range v{snapshot.MinimumLocalCharactersSchemaVersion}..v{snapshot.MaximumLocalCharactersSchemaVersion}.");
        }

        if (snapshot.WorldSchemaVersion <= 0 || snapshot.WorldSchemaVersion != snapshot.ExpectedWorldSchemaVersion)
        {
            reasons.Add(
                $"World schema v{snapshot.WorldSchemaVersion} is incompatible with local combined mode (expected v{snapshot.ExpectedWorldSchemaVersion}).");
        }

        return reasons.Count == 0
            ? ClusterRollbackVerdict.Allow()
            : ClusterRollbackVerdict.Block("rollback_blocked", reasons);
    }
}
