namespace ArcaneCore.Kernel.Cluster.Tooling;

/// <summary>Short-lived CLI and maintenance job kinds governed by the M15.5 job admission contracts.</summary>
public enum ClusterMaintenanceJobKind
{
    Unknown = 0,
    ContentImport = 1,
    DatabaseMigrate = 2,
    AccountAdmin = 3,
    SpellImport = 4,
    CodeGen = 5,
    MockLoadTest = 6,
}

/// <summary>Lifecycle state of a cluster gateway, worker, or authority node.</summary>
public enum ClusterNodeLifecycleState
{
    Unknown = 0,
    Starting = 1,
    Active = 2,
    Draining = 3,
    Drained = 4,
    Stopped = 5,
}

/// <summary>Identity, version, and lifecycle descriptor of a cluster node participating in a realm.</summary>
public sealed record ClusterNodeDescriptor(
    string NodeId,
    string Role,
    Guid NodeIncarnation,
    ClusterNodeLifecycleState State,
    int ContractVersion,
    int AuthSchemaVersion,
    int CharactersSchemaVersion,
    int WorldSchemaVersion,
    string ContentRevision,
    bool IsFencedWriter = true)
{
    /// <summary>True when the node is actively serving or starting up (not drained or stopped).</summary>
    public bool IsServingOrStarting =>
        State is ClusterNodeLifecycleState.Starting
            or ClusterNodeLifecycleState.Active
            or ClusterNodeLifecycleState.Draining
            or ClusterNodeLifecycleState.Unknown;

    /// <summary>True when the node has completed draining or stopped.</summary>
    public bool IsDrainedOrStopped =>
        State is ClusterNodeLifecycleState.Drained or ClusterNodeLifecycleState.Stopped;
}

/// <summary>Explicit coordinated rollout lease authorizing a content or schema maintenance job while workers are drained.</summary>
public sealed record CoordinatedRolloutLease(
    string LeaseId,
    int RealmId,
    string HolderId,
    long OwnershipEpoch,
    string TargetContentRevision,
    DateTimeOffset ExpiresAtUtc)
{
    public bool IsValidAt(DateTimeOffset nowUtc) =>
        !string.IsNullOrWhiteSpace(LeaseId)
        && RealmId > 0
        && !string.IsNullOrWhiteSpace(HolderId)
        && OwnershipEpoch > 0
        && !string.IsNullOrWhiteSpace(TargetContentRevision)
        && ExpiresAtUtc > nowUtc;
}

/// <summary>Request to run a short-lived maintenance or CLI job against a realm.</summary>
public sealed record ClusterMaintenanceJobRequest(
    ClusterMaintenanceJobKind JobKind,
    int RealmId,
    string RequestedBy,
    string? TargetContentRevision = null,
    bool ReplacesLiveContent = false,
    CoordinatedRolloutLease? RolloutLease = null,
    DateTimeOffset? RequestedAtUtc = null);

/// <summary>Verdict from <see cref="ClusterMaintenanceJobGuard"/> or <see cref="ContentRevisionCompatibilityPolicy"/>.</summary>
public sealed record ClusterAdmissionVerdict(
    bool Allowed,
    string Code,
    IReadOnlyList<string> Reasons)
{
    public static ClusterAdmissionVerdict Allow(string code = "allowed") =>
        new(true, code, []);

    public static ClusterAdmissionVerdict Deny(string code, params string[] reasons) =>
        new(false, code, reasons);

    public static ClusterAdmissionVerdict Deny(string code, IReadOnlyList<string> reasons) =>
        new(false, code, reasons);
}

/// <summary>
/// Enforces M15.5 mixed-version, schema-version, and content-revision admission rules across cluster nodes.
/// Unknown contract or schema versions fail closed; serving nodes on a realm must share an identical immutable <c>ContentRevision</c>.
/// </summary>
public sealed class ContentRevisionCompatibilityPolicy
{
    public int SupportedContractVersion { get; }
    public int ExpectedAuthSchemaVersion { get; }
    public int MinimumCompatibleCharactersSchemaVersion { get; }
    public int CurrentCharactersSchemaVersion { get; }
    public int ExpectedWorldSchemaVersion { get; }
    public string ExpectedContentRevision { get; }

    public ContentRevisionCompatibilityPolicy(
        int supportedContractVersion,
        int expectedAuthSchemaVersion,
        int currentCharactersSchemaVersion,
        int expectedWorldSchemaVersion,
        string expectedContentRevision,
        int? minimumCompatibleCharactersSchemaVersion = null)
    {
        if (supportedContractVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(supportedContractVersion), "SupportedContractVersion must be positive.");
        if (expectedAuthSchemaVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedAuthSchemaVersion), "ExpectedAuthSchemaVersion must be positive.");
        if (currentCharactersSchemaVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(currentCharactersSchemaVersion), "CurrentCharactersSchemaVersion must be positive.");
        if (expectedWorldSchemaVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedWorldSchemaVersion), "ExpectedWorldSchemaVersion must be positive.");
        if (string.IsNullOrWhiteSpace(expectedContentRevision))
            throw new ArgumentException("ExpectedContentRevision must not be empty.", nameof(expectedContentRevision));

        int minChars = minimumCompatibleCharactersSchemaVersion ?? currentCharactersSchemaVersion;
        if (minChars <= 0 || minChars > currentCharactersSchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(minimumCompatibleCharactersSchemaVersion), "MinimumCompatibleCharactersSchemaVersion must be between 1 and CurrentCharactersSchemaVersion.");

        SupportedContractVersion = supportedContractVersion;
        ExpectedAuthSchemaVersion = expectedAuthSchemaVersion;
        MinimumCompatibleCharactersSchemaVersion = minChars;
        CurrentCharactersSchemaVersion = currentCharactersSchemaVersion;
        ExpectedWorldSchemaVersion = expectedWorldSchemaVersion;
        ExpectedContentRevision = expectedContentRevision.Trim();
    }

    /// <summary>Evaluates whether a candidate node can be admitted alongside existing active nodes.</summary>
    public ClusterAdmissionVerdict EvaluateNodeAdmission(
        ClusterNodeDescriptor candidate,
        IReadOnlyList<ClusterNodeDescriptor>? existingNodes = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var reasons = new List<string>();

        if (string.IsNullOrWhiteSpace(candidate.NodeId))
            reasons.Add("NodeId must not be empty.");
        if (string.IsNullOrWhiteSpace(candidate.Role))
            reasons.Add("Role must not be empty.");
        if (candidate.NodeIncarnation == Guid.Empty)
            reasons.Add($"Node '{candidate.NodeId}' has an empty NodeIncarnation.");
        if (candidate.State == ClusterNodeLifecycleState.Unknown)
            reasons.Add($"Node '{candidate.NodeId}' has an unknown lifecycle state.");

        if (candidate.ContractVersion <= 0 || candidate.ContractVersion != SupportedContractVersion)
        {
            reasons.Add(
                $"Node '{candidate.NodeId}' contract version {candidate.ContractVersion} is incompatible with supported contract version {SupportedContractVersion}.");
        }

        if (candidate.AuthSchemaVersion <= 0 || candidate.AuthSchemaVersion != ExpectedAuthSchemaVersion)
        {
            reasons.Add(
                $"Node '{candidate.NodeId}' auth schema v{candidate.AuthSchemaVersion} does not match expected v{ExpectedAuthSchemaVersion}.");
        }

        if (candidate.CharactersSchemaVersion < MinimumCompatibleCharactersSchemaVersion
            || candidate.CharactersSchemaVersion > CurrentCharactersSchemaVersion)
        {
            reasons.Add(
                $"Node '{candidate.NodeId}' characters schema v{candidate.CharactersSchemaVersion} is outside compatible range v{MinimumCompatibleCharactersSchemaVersion}..v{CurrentCharactersSchemaVersion}.");
        }

        if (candidate.WorldSchemaVersion <= 0 || candidate.WorldSchemaVersion != ExpectedWorldSchemaVersion)
        {
            reasons.Add(
                $"Node '{candidate.NodeId}' world schema v{candidate.WorldSchemaVersion} does not match expected v{ExpectedWorldSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(candidate.ContentRevision)
            || !string.Equals(candidate.ContentRevision.Trim(), ExpectedContentRevision, StringComparison.Ordinal))
        {
            reasons.Add(
                $"Node '{candidate.NodeId}' content revision '{candidate.ContentRevision}' does not match expected revision '{ExpectedContentRevision}'.");
        }

        if (!candidate.IsFencedWriter)
        {
            reasons.Add($"Node '{candidate.NodeId}' is an unfenced writer and cannot be admitted to a cluster.");
        }

        if (existingNodes is not null)
        {
            foreach (ClusterNodeDescriptor peer in existingNodes)
            {
                if (!peer.IsServingOrStarting)
                    continue;

                if (peer.ContractVersion != candidate.ContractVersion)
                {
                    reasons.Add(
                        $"Node '{candidate.NodeId}' contract v{candidate.ContractVersion} conflicts with active peer '{peer.NodeId}' contract v{peer.ContractVersion}.");
                }

                if (!string.Equals(peer.ContentRevision.Trim(), candidate.ContentRevision?.Trim(), StringComparison.Ordinal))
                {
                    reasons.Add(
                        $"Node '{candidate.NodeId}' content revision '{candidate.ContentRevision}' conflicts with active peer '{peer.NodeId}' content revision '{peer.ContentRevision}'.");
                }
            }
        }

        return reasons.Count == 0
            ? ClusterAdmissionVerdict.Allow("node_compatible")
            : ClusterAdmissionVerdict.Deny("node_incompatible", reasons);
    }

    /// <summary>Evaluates whether a fleet of nodes is mutually consistent and compatible with this policy.</summary>
    public ClusterAdmissionVerdict EvaluateFleetCompatibility(IReadOnlyList<ClusterNodeDescriptor> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var reasons = new List<string>();
        foreach (ClusterNodeDescriptor node in nodes)
        {
            if (node.IsDrainedOrStopped)
                continue;

            ClusterAdmissionVerdict verdict = EvaluateNodeAdmission(node, nodes);
            if (!verdict.Allowed)
                reasons.AddRange(verdict.Reasons);
        }

        return reasons.Count == 0
            ? ClusterAdmissionVerdict.Allow("fleet_compatible")
            : ClusterAdmissionVerdict.Deny("fleet_incompatible", reasons);
    }
}

/// <summary>
/// Enforces M15.5 CLI and maintenance job admission contracts.
/// Offline tools (<c>CodeGen</c>) and bounded administrative/test jobs (<c>AccountAdmin</c>, <c>MockLoadTest</c>)
/// retain direct CLI use when they do not replace live content. Exclusive content or schema jobs
/// (<c>ContentImport</c>, <c>SpellImport</c>, <c>DatabaseMigrate</c>, or any request with <c>ReplacesLiveContent = true</c>)
/// are refused against an active serving cluster unless all serving nodes are drained/stopped or a valid coordinated rollout lease is presented with zero active serving workers.
/// </summary>
public sealed class ClusterMaintenanceJobGuard
{
    /// <summary>Evaluates whether the maintenance job is permitted given the current realm cluster nodes.</summary>
    public ClusterAdmissionVerdict Evaluate(
        ClusterMaintenanceJobRequest request,
        IReadOnlyList<ClusterNodeDescriptor> realmNodes,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(realmNodes);

        DateTimeOffset now = nowUtc ?? request.RequestedAtUtc ?? DateTimeOffset.UtcNow;
        var reasons = new List<string>();

        if (request.JobKind == ClusterMaintenanceJobKind.Unknown || !Enum.IsDefined(request.JobKind))
            reasons.Add("Maintenance job kind is unknown.");
        if (request.RealmId <= 0)
            reasons.Add("RealmId must be positive.");
        if (string.IsNullOrWhiteSpace(request.RequestedBy))
            reasons.Add("RequestedBy must not be empty.");

        if (reasons.Count > 0)
            return ClusterAdmissionVerdict.Deny("invalid_job_request", reasons);

        bool exclusiveContentOrSchemaJob = request.ReplacesLiveContent
            || request.JobKind is ClusterMaintenanceJobKind.ContentImport
                or ClusterMaintenanceJobKind.SpellImport
                or ClusterMaintenanceJobKind.DatabaseMigrate;

        if (!exclusiveContentOrSchemaJob)
        {
            return ClusterAdmissionVerdict.Allow("non_destructive_cli_allowed");
        }

        if ((request.JobKind is ClusterMaintenanceJobKind.ContentImport or ClusterMaintenanceJobKind.SpellImport || request.ReplacesLiveContent)
            && string.IsNullOrWhiteSpace(request.TargetContentRevision))
        {
            return ClusterAdmissionVerdict.Deny(
                "missing_content_revision",
                "Content replacement jobs must declare a non-empty TargetContentRevision.");
        }

        ClusterNodeDescriptor[] activeServing = realmNodes
            .Where(n => n.IsServingOrStarting)
            .ToArray();

        if (activeServing.Length > 0)
        {
            string activeList = string.Join(", ", activeServing.Select(n => $"{n.NodeId}({n.Role}:{n.State})"));
            return ClusterAdmissionVerdict.Deny(
                "live_content_replacement_refused",
                $"Refusing {request.JobKind} on realm {request.RealmId} while cluster nodes are serving or starting: {activeList}. Drain or stop all serving nodes first.");
        }

        if (request.RolloutLease is not null)
        {
            CoordinatedRolloutLease lease = request.RolloutLease;
            if (!lease.IsValidAt(now))
            {
                reasons.Add($"Rollout lease '{lease.LeaseId}' is expired or invalid at {now:O}.");
            }
            if (lease.RealmId != request.RealmId)
            {
                reasons.Add($"Rollout lease realm {lease.RealmId} does not match job realm {request.RealmId}.");
            }
            if (!string.IsNullOrWhiteSpace(request.TargetContentRevision)
                && !string.Equals(lease.TargetContentRevision.Trim(), request.TargetContentRevision.Trim(), StringComparison.Ordinal))
            {
                reasons.Add(
                    $"Rollout lease target revision '{lease.TargetContentRevision}' does not match job target revision '{request.TargetContentRevision}'.");
            }

            if (reasons.Count > 0)
                return ClusterAdmissionVerdict.Deny("invalid_rollout_lease", reasons);

            return ClusterAdmissionVerdict.Allow("coordinated_rollout_allowed");
        }

        return ClusterAdmissionVerdict.Allow("offline_window_allowed");
    }
}
