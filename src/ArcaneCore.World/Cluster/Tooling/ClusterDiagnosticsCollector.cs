using System.Text;
using ArcaneCore.Kernel.Cluster.Tooling;

namespace ArcaneCore.World.Cluster.Tooling;

/// <summary>Deterministic health, compatibility, and rollback-readiness report for a realm cluster (M15.5).</summary>
public sealed record ClusterDiagnosticsReport(
    int RealmId,
    DateTimeOffset GeneratedAtUtc,
    ClusterCorrelationContext Correlation,
    int TotalNodes,
    int ActiveNodes,
    int DrainingNodes,
    int DrainedOrStoppedNodes,
    IReadOnlyList<string> DistinctContentRevisions,
    int ActiveOwnershipLeases,
    int PendingTransfers,
    int PendingCharacterWrites,
    int RetainedWriteFailures,
    ClusterAdmissionVerdict FleetCompatibility,
    ClusterRollbackVerdict RollbackReadiness,
    IReadOnlyDictionary<string, string> RedactedAttributes)
{
    /// <summary>Formats a deterministic single-text diagnostic summary suitable for operator logs or CLI output.</summary>
    public string FormatDeterministicSummary()
    {
        var sb = new StringBuilder();
        sb.Append("cluster-diag ");
        sb.Append(Correlation.FormatLogPrefix());
        sb.Append(" ts=").Append(GeneratedAtUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
        sb.Append(" nodes=").Append(TotalNodes)
            .Append("(active=").Append(ActiveNodes)
            .Append(",draining=").Append(DrainingNodes)
            .Append(",drained=").Append(DrainedOrStoppedNodes).Append(')');
        sb.Append(" content=[").Append(string.Join(",", DistinctContentRevisions)).Append(']');
        sb.Append(" leases=").Append(ActiveOwnershipLeases);
        sb.Append(" xferPending=").Append(PendingTransfers);
        sb.Append(" writesPending=").Append(PendingCharacterWrites);
        sb.Append(" writeFailures=").Append(RetainedWriteFailures);
        sb.Append(" fleetCompat=").Append(FleetCompatibility.Code);
        sb.Append(" rollbackReady=").Append(RollbackReadiness.SafeToRollback ? "true" : "false");

        if (RedactedAttributes.Count > 0)
        {
            sb.Append(" attrs={");
            bool first = true;
            foreach ((string key, string value) in RedactedAttributes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (!first)
                    sb.Append(", ");
                first = false;
                sb.Append(key).Append('=').Append(value);
            }
            sb.Append('}');
        }

        return sb.ToString();
    }
}

/// <summary>Builds deterministic <see cref="ClusterDiagnosticsReport"/> snapshots with automatic credential and session-key redaction.</summary>
public sealed class ClusterDiagnosticsCollector
{
    private readonly ContentRevisionCompatibilityPolicy _compatibilityPolicy;
    private readonly ClusterRollbackCoordinator _rollbackCoordinator;

    public ClusterDiagnosticsCollector(
        ContentRevisionCompatibilityPolicy compatibilityPolicy,
        ClusterRollbackCoordinator? rollbackCoordinator = null)
    {
        ArgumentNullException.ThrowIfNull(compatibilityPolicy);
        _compatibilityPolicy = compatibilityPolicy;
        _rollbackCoordinator = rollbackCoordinator ?? new ClusterRollbackCoordinator();
    }

    public ClusterDiagnosticsReport Collect(
        ClusterCorrelationContext correlation,
        ClusterRollbackSnapshot snapshot,
        IReadOnlyDictionary<string, string?>? attributes = null,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(snapshot);

        DateTimeOffset timestamp = nowUtc ?? DateTimeOffset.UtcNow;
        IReadOnlyList<ClusterNodeDescriptor> nodes = snapshot.ClusterNodes ?? [];

        int active = nodes.Count(n => n.State is ClusterNodeLifecycleState.Active or ClusterNodeLifecycleState.Starting);
        int draining = nodes.Count(n => n.State == ClusterNodeLifecycleState.Draining);
        int drainedOrStopped = nodes.Count(n => n.IsDrainedOrStopped);

        string[] revisions = nodes
            .Where(n => !string.IsNullOrWhiteSpace(n.ContentRevision))
            .Select(n => n.ContentRevision.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToArray();

        ClusterAdmissionVerdict fleetCompat = _compatibilityPolicy.EvaluateFleetCompatibility(nodes);
        ClusterRollbackVerdict rollbackVerdict = _rollbackCoordinator.VerifySafeRollbackToLocal(snapshot);

        IReadOnlyDictionary<string, string> redacted = attributes is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : ClusterCorrelationContext.RedactSensitiveFields(attributes);

        return new ClusterDiagnosticsReport(
            RealmId: snapshot.RealmId,
            GeneratedAtUtc: timestamp,
            Correlation: correlation,
            TotalNodes: nodes.Count,
            ActiveNodes: active,
            DrainingNodes: draining,
            DrainedOrStoppedNodes: drainedOrStopped,
            DistinctContentRevisions: revisions,
            ActiveOwnershipLeases: snapshot.ActiveClusterOwnershipLeases,
            PendingTransfers: snapshot.PendingTransfers,
            PendingCharacterWrites: snapshot.PendingCharacterWrites,
            RetainedWriteFailures: snapshot.RetainedWriteFailures,
            FleetCompatibility: fleetCompat,
            RollbackReadiness: rollbackVerdict,
            RedactedAttributes: redacted);
    }
}
