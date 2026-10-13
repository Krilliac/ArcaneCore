namespace ArcaneCore.Kernel.Cluster;

/// <summary>
/// Runtime role hosted by an ArcaneCore node process (<c>docs/CLUSTERING_DESIGN.md</c> §3, §10 M15.1).
/// <see cref="Combined"/> is the default zero-config single-process mode.
/// </summary>
public enum ClusterRole
{
    Combined = 0,
    Gateway = 1,
    Worker = 2,
    Social = 3,
    Coordinator = 4,
}

/// <summary>
/// Versioned identity and compatibility manifest advertised when a node joins or registers with a peer
/// (<c>docs/CLUSTERING_DESIGN.md</c> §3.2, §9.1). Nodes with mismatched realm, contract version, database
/// schema versions, or static content revision are deterministically rejected before any session traffic flows.
/// </summary>
public sealed record ClusterNodeManifest(
    int RealmId,
    string NodeId,
    Guid NodeIncarnation,
    ClusterRole Role,
    int ContractVersion,
    int CharactersSchemaVersion,
    int WorldSchemaVersion,
    string ContentRevision)
{
    /// <summary>Current wire/RPC contract version for gateway–worker and coordinator handshakes.</summary>
    public const int CurrentContractVersion = 1;

    /// <summary>
    /// Validates whether this manifest itself is structurally well-formed.
    /// </summary>
    public bool IsValid(out string? invalidReason)
    {
        if (RealmId <= 0)
        {
            invalidReason = $"invalid RealmId ({RealmId}); must be positive";
            return false;
        }

        if (string.IsNullOrWhiteSpace(NodeId))
        {
            invalidReason = "NodeId must not be empty";
            return false;
        }

        if (NodeIncarnation == Guid.Empty)
        {
            invalidReason = "NodeIncarnation must not be Guid.Empty";
            return false;
        }

        if (!Enum.IsDefined(Role))
        {
            invalidReason = $"undefined ClusterRole ({(int)Role})";
            return false;
        }

        if (ContractVersion <= 0)
        {
            invalidReason = $"invalid ContractVersion ({ContractVersion}); must be positive";
            return false;
        }

        if (CharactersSchemaVersion <= 0)
        {
            invalidReason = $"invalid CharactersSchemaVersion ({CharactersSchemaVersion}); must be positive";
            return false;
        }

        if (WorldSchemaVersion <= 0)
        {
            invalidReason = $"invalid WorldSchemaVersion ({WorldSchemaVersion}); must be positive";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ContentRevision))
        {
            invalidReason = "ContentRevision must not be empty";
            return false;
        }

        invalidReason = null;
        return true;
    }

    /// <summary>
    /// Deterministically checks whether <paramref name="peer"/> is compatible with this node's manifest.
    /// </summary>
    public bool TryAdmit(ClusterNodeManifest? peer, out string? rejectionReason)
    {
        if (peer is null)
        {
            rejectionReason = "peer manifest is null";
            return false;
        }

        if (!IsValid(out string? selfInvalid))
        {
            rejectionReason = $"local manifest is invalid: {selfInvalid}";
            return false;
        }

        if (!peer.IsValid(out string? peerInvalid))
        {
            rejectionReason = $"peer manifest is invalid: {peerInvalid}";
            return false;
        }

        if (peer.RealmId != RealmId)
        {
            rejectionReason = $"realm mismatch: peer realm {peer.RealmId} != local realm {RealmId}";
            return false;
        }

        if (peer.ContractVersion != ContractVersion)
        {
            rejectionReason = $"contract version mismatch: peer v{peer.ContractVersion} != local v{ContractVersion}";
            return false;
        }

        if (peer.CharactersSchemaVersion != CharactersSchemaVersion)
        {
            rejectionReason = $"characters schema mismatch: peer {peer.CharactersSchemaVersion} != local {CharactersSchemaVersion}";
            return false;
        }

        if (peer.WorldSchemaVersion != WorldSchemaVersion)
        {
            rejectionReason = $"world schema mismatch: peer {peer.WorldSchemaVersion} != local {WorldSchemaVersion}";
            return false;
        }

        if (!string.Equals(peer.ContentRevision, ContentRevision, StringComparison.Ordinal))
        {
            rejectionReason = $"content revision mismatch: peer '{peer.ContentRevision}' != local '{ContentRevision}'";
            return false;
        }

        rejectionReason = null;
        return true;
    }
}
