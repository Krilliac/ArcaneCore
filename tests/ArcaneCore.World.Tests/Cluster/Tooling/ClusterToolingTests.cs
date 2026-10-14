using ArcaneCore.Kernel.Cluster.Tooling;
using ArcaneCore.World.Cluster.Tooling;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Tooling;

/// <summary>
/// Unit tests for M15.5 cluster tooling, content-revision compatibility, maintenance job admission,
/// safe cluster-to-local rollback verification, and correlation/redaction diagnostics.
/// </summary>
public sealed class ClusterToolingTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 13, 22, 0, 0, TimeSpan.Zero);

    private static ClusterNodeDescriptor ActiveWorker(
        string nodeId = "worker-eastern-1",
        int contractVersion = 1,
        int authSchema = 2,
        int charSchema = 60,
        int worldSchema = 6,
        string contentRev = "dbc-5875-r1",
        bool fenced = true) =>
        new(
            NodeId: nodeId,
            Role: "GameWorker",
            NodeIncarnation: Guid.Parse("11111111-2222-3333-4444-555555555555"),
            State: ClusterNodeLifecycleState.Active,
            ContractVersion: contractVersion,
            AuthSchemaVersion: authSchema,
            CharactersSchemaVersion: charSchema,
            WorldSchemaVersion: worldSchema,
            ContentRevision: contentRev,
            IsFencedWriter: fenced);

    private static ClusterNodeDescriptor DrainedWorker(
        string nodeId = "worker-eastern-1",
        string contentRev = "dbc-5875-r1") =>
        ActiveWorker(nodeId: nodeId, contentRev: contentRev) with
        {
            State = ClusterNodeLifecycleState.Drained,
        };

    [Fact]
    public void MaintenanceJobGuard_RefusesLiveContentReplacement_WhenServingNodesAreActive()
    {
        var guard = new ClusterMaintenanceJobGuard();
        ClusterNodeDescriptor[] nodes = [ActiveWorker("worker-1"), DrainedWorker("worker-2")];

        var request = new ClusterMaintenanceJobRequest(
            JobKind: ClusterMaintenanceJobKind.ContentImport,
            RealmId: 1,
            RequestedBy: "operator-cli",
            TargetContentRevision: "dbc-5875-r2",
            ReplacesLiveContent: true,
            RequestedAtUtc: FixedNow);

        ClusterAdmissionVerdict verdict = guard.Evaluate(request, nodes, FixedNow);

        Assert.False(verdict.Allowed);
        Assert.Equal("live_content_replacement_refused", verdict.Code);
        Assert.Single(verdict.Reasons);
        Assert.Contains("worker-1(GameWorker:Active)", verdict.Reasons[0], StringComparison.Ordinal);
    }

    [Fact]
    public void MaintenanceJobGuard_AllowsContentReplacement_WhenOfflineOrCoordinatedWithDrainedWorkers()
    {
        var guard = new ClusterMaintenanceJobGuard();
        ClusterNodeDescriptor[] drainedNodes =
        [
            DrainedWorker("worker-1"),
            DrainedWorker("gateway-1") with { Role = "Gateway", State = ClusterNodeLifecycleState.Stopped },
        ];

        // 1. Offline window (all nodes drained/stopped, no lease required).
        var offlineReq = new ClusterMaintenanceJobRequest(
            JobKind: ClusterMaintenanceJobKind.SpellImport,
            RealmId: 1,
            RequestedBy: "operator-cli",
            TargetContentRevision: "dbc-5875-r2",
            ReplacesLiveContent: true,
            RequestedAtUtc: FixedNow);

        ClusterAdmissionVerdict offlineVerdict = guard.Evaluate(offlineReq, drainedNodes, FixedNow);
        Assert.True(offlineVerdict.Allowed);
        Assert.Equal("offline_window_allowed", offlineVerdict.Code);

        // 2. Coordinated rollout lease with matching revision and drained workers.
        var lease = new CoordinatedRolloutLease(
            LeaseId: "lease-rollout-42",
            RealmId: 1,
            HolderId: "coordinator-1",
            OwnershipEpoch: 19,
            TargetContentRevision: "dbc-5875-r2",
            ExpiresAtUtc: FixedNow.AddMinutes(15));

        var leaseReq = offlineReq with { RolloutLease = lease };
        ClusterAdmissionVerdict leaseVerdict = guard.Evaluate(leaseReq, drainedNodes, FixedNow);
        Assert.True(leaseVerdict.Allowed);
        Assert.Equal("coordinated_rollout_allowed", leaseVerdict.Code);
    }

    [Fact]
    public void MaintenanceJobGuard_RefusesCoordinatedRollout_WhenLeaseExpiredOrRevisionMismatches()
    {
        var guard = new ClusterMaintenanceJobGuard();
        ClusterNodeDescriptor[] drainedNodes = [DrainedWorker("worker-1")];

        var expiredLease = new CoordinatedRolloutLease(
            LeaseId: "lease-expired",
            RealmId: 1,
            HolderId: "coordinator-1",
            OwnershipEpoch: 19,
            TargetContentRevision: "dbc-5875-r1",
            ExpiresAtUtc: FixedNow.AddSeconds(-1));

        var req = new ClusterMaintenanceJobRequest(
            JobKind: ClusterMaintenanceJobKind.ContentImport,
            RealmId: 1,
            RequestedBy: "operator-cli",
            TargetContentRevision: "dbc-5875-r2",
            ReplacesLiveContent: true,
            RolloutLease: expiredLease,
            RequestedAtUtc: FixedNow);

        ClusterAdmissionVerdict verdict = guard.Evaluate(req, drainedNodes, FixedNow);
        Assert.False(verdict.Allowed);
        Assert.Equal("invalid_rollout_lease", verdict.Code);
        Assert.Equal(2, verdict.Reasons.Count);
    }

    [Fact]
    public void MaintenanceJobGuard_AllowsOfflineCliAndAccountTools_WhileClusterIsServing()
    {
        var guard = new ClusterMaintenanceJobGuard();
        ClusterNodeDescriptor[] activeNodes = [ActiveWorker("worker-1")];

        foreach (ClusterMaintenanceJobKind kind in new[]
                 {
                     ClusterMaintenanceJobKind.CodeGen,
                     ClusterMaintenanceJobKind.AccountAdmin,
                     ClusterMaintenanceJobKind.MockLoadTest,
                 })
        {
            var req = new ClusterMaintenanceJobRequest(
                JobKind: kind,
                RealmId: 1,
                RequestedBy: "ci-runner",
                ReplacesLiveContent: false,
                RequestedAtUtc: FixedNow);

            ClusterAdmissionVerdict verdict = guard.Evaluate(req, activeNodes, FixedNow);
            Assert.True(verdict.Allowed);
            Assert.Equal("non_destructive_cli_allowed", verdict.Code);
        }
    }

    [Fact]
    public void CompatibilityPolicy_AdmitsMatchingNode_AndRejectsUnknownOrMismatchedVersions()
    {
        var policy = new ContentRevisionCompatibilityPolicy(
            supportedContractVersion: 1,
            expectedAuthSchemaVersion: 2,
            currentCharactersSchemaVersion: 60,
            expectedWorldSchemaVersion: 6,
            expectedContentRevision: "dbc-5875-r1",
            minimumCompatibleCharactersSchemaVersion: 59);

        ClusterNodeDescriptor validPeer = ActiveWorker("worker-1", charSchema: 59);
        ClusterNodeDescriptor candidate = ActiveWorker("worker-2", charSchema: 60);

        ClusterAdmissionVerdict ok = policy.EvaluateNodeAdmission(candidate, [validPeer]);
        Assert.True(ok.Allowed);
        Assert.Equal("node_compatible", ok.Code);

        // Unknown/mismatched contract version, schema version, content revision, and unfenced writer all fail closed.
        ClusterNodeDescriptor badCandidate = ActiveWorker(
            nodeId: "worker-bad",
            contractVersion: 99,
            authSchema: 1,
            charSchema: 58,
            worldSchema: 99,
            contentRev: "dbc-5875-stale",
            fenced: false);

        ClusterAdmissionVerdict denied = policy.EvaluateNodeAdmission(badCandidate, [validPeer]);
        Assert.False(denied.Allowed);
        Assert.Equal("node_incompatible", denied.Code);
        Assert.Contains(denied.Reasons, r => r.Contains("contract version 99", StringComparison.Ordinal));
        Assert.Contains(denied.Reasons, r => r.Contains("auth schema v1", StringComparison.Ordinal));
        Assert.Contains(denied.Reasons, r => r.Contains("characters schema v58", StringComparison.Ordinal));
        Assert.Contains(denied.Reasons, r => r.Contains("world schema v99", StringComparison.Ordinal));
        Assert.Contains(denied.Reasons, r => r.Contains("dbc-5875-stale", StringComparison.Ordinal));
        Assert.Contains(denied.Reasons, r => r.Contains("unfenced writer", StringComparison.Ordinal));
    }

    [Fact]
    public void RollbackCoordinator_BlocksWhenUndrainedOrPendingTransfersOrWritesOrUnfencedWritersExist()
    {
        var coordinator = new ClusterRollbackCoordinator();

        var blockedSnapshot = new ClusterRollbackSnapshot(
            RealmId: 1,
            ClusterNodes: [ActiveWorker("worker-1"), DrainedWorker("worker-2")],
            PendingTransfers: 2,
            PendingCharacterWrites: 5,
            RetainedWriteFailures: 1,
            ActiveUnfencedWriters: 1,
            ActiveClusterOwnershipLeases: 3,
            AuthSchemaVersion: 2,
            CharactersSchemaVersion: 60,
            WorldSchemaVersion: 6,
            ExpectedAuthSchemaVersion: 2,
            MinimumLocalCharactersSchemaVersion: 59,
            MaximumLocalCharactersSchemaVersion: 60,
            ExpectedWorldSchemaVersion: 6);

        ClusterRollbackVerdict blocked = coordinator.VerifySafeRollbackToLocal(blockedSnapshot);
        Assert.False(blocked.SafeToRollback);
        Assert.Equal("rollback_blocked", blocked.Code);
        Assert.Equal(6, blocked.BlockingReasons.Count);
        Assert.Contains(blocked.BlockingReasons, r => r.Contains("worker-1(GameWorker:Active)", StringComparison.Ordinal));
        Assert.Contains(blocked.BlockingReasons, r => r.Contains("Pending cross-worker transfers remain (2)", StringComparison.Ordinal));
        Assert.Contains(blocked.BlockingReasons, r => r.Contains("Pending character/instance persistence writes remain (5)", StringComparison.Ordinal));
        Assert.Contains(blocked.BlockingReasons, r => r.Contains("Retained write failures remain (1)", StringComparison.Ordinal));
        Assert.Contains(blocked.BlockingReasons, r => r.Contains("Active cluster ownership leases remain (3)", StringComparison.Ordinal));
        Assert.Contains(blocked.BlockingReasons, r => r.Contains("Active unfenced writers detected (1)", StringComparison.Ordinal));
    }

    [Fact]
    public void RollbackCoordinator_AllowsLocalTakeover_WhenAllNodesDrainedAndQueuesFlushedAndSchemasCompatible()
    {
        var coordinator = new ClusterRollbackCoordinator();

        var cleanSnapshot = new ClusterRollbackSnapshot(
            RealmId: 1,
            ClusterNodes:
            [
                DrainedWorker("worker-1"),
                DrainedWorker("gateway-1") with { Role = "Gateway", State = ClusterNodeLifecycleState.Stopped },
            ],
            PendingTransfers: 0,
            PendingCharacterWrites: 0,
            RetainedWriteFailures: 0,
            ActiveUnfencedWriters: 0,
            ActiveClusterOwnershipLeases: 0,
            AuthSchemaVersion: 2,
            CharactersSchemaVersion: 60,
            WorldSchemaVersion: 6,
            ExpectedAuthSchemaVersion: 2,
            MinimumLocalCharactersSchemaVersion: 59,
            MaximumLocalCharactersSchemaVersion: 60,
            ExpectedWorldSchemaVersion: 6);

        ClusterRollbackVerdict allowed = coordinator.VerifySafeRollbackToLocal(cleanSnapshot);
        Assert.True(allowed.SafeToRollback);
        Assert.Equal("safe_to_rollback", allowed.Code);
        Assert.Empty(allowed.BlockingReasons);
    }

    [Fact]
    public void DiagnosticsCollector_FormatsCorrelationAndRedactsCredentialsAndSessionKeys()
    {
        var policy = new ContentRevisionCompatibilityPolicy(
            supportedContractVersion: 1,
            expectedAuthSchemaVersion: 2,
            currentCharactersSchemaVersion: 60,
            expectedWorldSchemaVersion: 6,
            expectedContentRevision: "dbc-5875-r1");
        var collector = new ClusterDiagnosticsCollector(policy);

        var correlation = new ClusterCorrelationContext(
            RealmId: 1,
            NodeId: "worker-eastern-1",
            NodeIncarnation: Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeffff"),
            SessionGeneration: 7,
            TransferId: "xfer-901",
            OwnershipEpoch: 14,
            OperationId: "op-save-88");

        var snapshot = new ClusterRollbackSnapshot(
            RealmId: 1,
            ClusterNodes: [DrainedWorker("worker-eastern-1")],
            PendingTransfers: 0,
            PendingCharacterWrites: 0,
            RetainedWriteFailures: 0,
            ActiveUnfencedWriters: 0,
            ActiveClusterOwnershipLeases: 0,
            AuthSchemaVersion: 2,
            CharactersSchemaVersion: 60,
            WorldSchemaVersion: 6,
            ExpectedAuthSchemaVersion: 2,
            MinimumLocalCharactersSchemaVersion: 60,
            MaximumLocalCharactersSchemaVersion: 60,
            ExpectedWorldSchemaVersion: 6);

        var rawAttributes = new Dictionary<string, string?>
        {
            ["SessionKey"] = "0123456789ABCDEF0123456789ABCDEF",
            ["AccountPassword"] = "hunter2",
            ["Note"] = "connected with session_key=DEADBEEF and password=supersecret ok",
            ["MapPartition"] = "0:0",
        };

        ClusterDiagnosticsReport report = collector.Collect(correlation, snapshot, rawAttributes, FixedNow);
        string summary = report.FormatDeterministicSummary();

        Assert.Equal("[REDACTED]", report.RedactedAttributes["SessionKey"]);
        Assert.Equal("[REDACTED]", report.RedactedAttributes["AccountPassword"]);
        Assert.Equal("connected with session_key=[REDACTED] and password=[REDACTED] ok", report.RedactedAttributes["Note"]);
        Assert.Equal("0:0", report.RedactedAttributes["MapPartition"]);

        Assert.DoesNotContain("0123456789ABCDEF0123456789ABCDEF", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("DEADBEEF", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("supersecret", summary, StringComparison.Ordinal);
        Assert.Contains("realm=1 node=worker-eastern-1 inc=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeffff sessGen=7 xfer=xfer-901 epoch=14 op=op-save-88", summary, StringComparison.Ordinal);
        Assert.Contains("rollbackReady=true", summary, StringComparison.Ordinal);
    }
}
