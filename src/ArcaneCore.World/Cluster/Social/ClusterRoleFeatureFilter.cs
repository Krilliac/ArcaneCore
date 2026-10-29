using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Features;
using ArcaneCore.World.ServerMail;
using ArcaneCore.World.Warden;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>
/// Process role of a node in the M15 cluster topology (<c>docs/CLUSTERING_DESIGN.md</c> §144-153, §346-355, §538-542, §565).
/// <see cref="Combined"/> is the default local single-process world daemon where every feature runs in one process.
/// </summary>
public enum ClusterNodeRole
{
    Combined = 0,
    Gateway = 1,
    MapWorker = 2,
    Social = 3,
    Economy = 4,
}

/// <summary>
/// Role-scoped feature and singleton-scheduler filter (M15.2, <c>docs/CLUSTERING_DESIGN.md</c> §346-394, §484-555, §565).
/// Prevents realm-singleton timers (<see cref="AutoBroadcastFeature"/>, <see cref="AuctionBotFeature"/>, and periodic
/// auction/mail expiration) from running once per map worker and ensures connection-bound features (<see cref="WardenFeature"/>)
/// stay on the connection owner.
/// </summary>
public sealed class ClusterRoleFeatureFilter
{
    private readonly IRealmAuthorityLeaseStore? _leaseStore;

    public ClusterRoleFeatureFilter(IRealmAuthorityLeaseStore? leaseStore = null)
    {
        _leaseStore = leaseStore;
    }

    /// <summary>
    /// Evaluate whether <paramref name="featureType"/> should be attached and active on a node running as
    /// <paramref name="role"/>. When the feature requires a realm-singleton lease (such as <c>"economy"</c> or
    /// <c>"social"</c>) and a lease store is configured, also verifies that <paramref name="nodeId"/> holds a
    /// valid <paramref name="fencingToken"/> at <paramref name="nowUtc"/>.
    /// </summary>
    public bool ShouldAttachFeature(
        Type featureType,
        ClusterNodeRole role,
        string nodeId = "local",
        long? fencingToken = null,
        DateTimeOffset nowUtc = default)
        => ShouldAttachFeature(featureType, role, nodeId, _ => fencingToken, nowUtc);

    /// <summary>
    /// The lease scope whose holder runs <paramref name="featureType"/> (<see cref="RealmAuthorityScopes.Social"/> for AutoBroadcast,
    /// <see cref="RealmAuthorityScopes.Economy"/> for the auction house bot), or null for a feature that is not a realm singleton.
    /// </summary>
    public static string? LeaseScopeFor(Type featureType)
    {
        ArgumentNullException.ThrowIfNull(featureType);
        if (featureType == typeof(AutoBroadcastFeature))
        {
            return RealmAuthorityScopes.Social;
        }

        return featureType == typeof(AuctionBotFeature) ? RealmAuthorityScopes.Economy : null;
    }

    /// <summary>
    /// As <see cref="ShouldAttachFeature(Type, ClusterNodeRole, string, long?, DateTimeOffset)"/>, with the fencing token looked up per
    /// lease scope by <paramref name="tokenForScope"/> (a node holds one lease per realm singleton). A <see cref="ClusterNodeRole.MapWorker"/>
    /// runs a realm singleton only while the store confirms <paramref name="nodeId"/> holds that scope's lease at <paramref name="nowUtc"/>.
    /// </summary>
    public bool ShouldAttachFeature(
        Type featureType,
        ClusterNodeRole role,
        string nodeId,
        Func<string, long?> tokenForScope,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(featureType);
        ArgumentNullException.ThrowIfNull(tokenForScope);
        if (!typeof(IWorldFeature).IsAssignableFrom(featureType))
        {
            throw new ArgumentException($"{featureType.FullName} does not implement {nameof(IWorldFeature)}.", nameof(featureType));
        }

        // AutoBroadcast is a realm-wide timer that sends to every player (docs/CLUSTERING_DESIGN.md:538-542):
        // running it on every map worker would broadcast each message once per worker. It runs only on Combined or Social.
        if (featureType == typeof(AutoBroadcastFeature))
        {
            if (role == ClusterNodeRole.Combined)
            {
                return ValidateOptionalLease(RealmAuthorityScopes.Social, nodeId, tokenForScope(RealmAuthorityScopes.Social), nowUtc, requireLeaseWhenStorePresent: false);
            }

            if (role == ClusterNodeRole.Social)
            {
                return ValidateOptionalLease(RealmAuthorityScopes.Social, nodeId, tokenForScope(RealmAuthorityScopes.Social), nowUtc, requireLeaseWhenStorePresent: true);
            }

            return role == ClusterNodeRole.MapWorker && HoldsLease(RealmAuthorityScopes.Social, nodeId, tokenForScope, nowUtc);
        }

        // AuctionBotFeature (and its periodic sell/buy/custody reconciliation timer) runs only on Combined or the
        // fenced Economy authority (docs/CLUSTERING_DESIGN.md:484-528, 565).
        if (featureType == typeof(AuctionBotFeature))
        {
            if (role == ClusterNodeRole.Combined)
            {
                return ValidateOptionalLease(RealmAuthorityScopes.Economy, nodeId, tokenForScope(RealmAuthorityScopes.Economy), nowUtc, requireLeaseWhenStorePresent: false);
            }

            if (role == ClusterNodeRole.Economy)
            {
                return ValidateOptionalLease(RealmAuthorityScopes.Economy, nodeId, tokenForScope(RealmAuthorityScopes.Economy), nowUtc, requireLeaseWhenStorePresent: true);
            }

            return role == ClusterNodeRole.MapWorker && HoldsLease(RealmAuthorityScopes.Economy, nodeId, tokenForScope, nowUtc);
        }

        // Warden derives its RC4 streams from the 40-byte session key K and belongs on the connection owner
        // (Combined in local mode, Gateway in cluster mode; docs/CLUSTERING_DESIGN.md:348, 356-394). A MapWorker keeps it too:
        // the gateway passes the client stream through to the worker (Tier 3a), so the worker still owns the Warden session.
        if (featureType == typeof(WardenFeature))
        {
            return role is ClusterNodeRole.Combined or ClusterNodeRole.Gateway or ClusterNodeRole.MapWorker;
        }

        // Ordinary world/map simulation features (spells, creatures, movement, quests, ServerMail login hooks, etc.)
        // run on Combined and MapWorker nodes, not on standalone Gateway, Social, or Economy nodes.
        return role is ClusterNodeRole.Combined or ClusterNodeRole.MapWorker;
    }

    /// <summary>
    /// Evaluate whether periodic realm-wide auction/mail expiration and custody reconciliation ticks may execute on
    /// <paramref name="nodeId"/> at <paramref name="nowUtc"/>. In cluster mode, requires an active <c>"economy"</c>
    /// lease with a valid <paramref name="fencingToken"/> so two workers never settle or expire the same auction/mail row.
    /// </summary>
    public bool CanExecuteEconomyAuthorityTick(
        ClusterNodeRole role,
        string nodeId,
        long fencingToken,
        DateTimeOffset nowUtc)
    {
        if (role is not (ClusterNodeRole.Combined or ClusterNodeRole.Economy or ClusterNodeRole.MapWorker))
        {
            return false;
        }

        if (_leaseStore is null)
        {
            return role == ClusterNodeRole.Combined && fencingToken > 0;
        }

        return _leaseStore.ValidateFencingToken(RealmAuthorityScopes.Economy, nodeId, fencingToken, nowUtc);
    }

    /// <summary>Filter a sequence of discovered <see cref="IWorldFeature"/> types down to those admitted for <paramref name="role"/>.</summary>
    public IReadOnlyList<Type> FilterFeatures(
        IEnumerable<Type> featureTypes,
        ClusterNodeRole role,
        string nodeId = "local",
        long? fencingToken = null,
        DateTimeOffset nowUtc = default)
    {
        ArgumentNullException.ThrowIfNull(featureTypes);
        return [.. featureTypes.Where(t => ShouldAttachFeature(t, role, nodeId, fencingToken, nowUtc))];
    }

    private bool HoldsLease(string scope, string nodeId, Func<string, long?> tokenForScope, DateTimeOffset nowUtc)
        => _leaseStore is not null && tokenForScope(scope) is { } token && _leaseStore.ValidateFencingToken(scope, nodeId, token, nowUtc);

    private bool ValidateOptionalLease(
        string scope,
        string nodeId,
        long? fencingToken,
        DateTimeOffset nowUtc,
        bool requireLeaseWhenStorePresent)
    {
        if (_leaseStore is null)
        {
            return !requireLeaseWhenStorePresent || fencingToken is > 0;
        }

        if (fencingToken is not { } token)
        {
            return !requireLeaseWhenStorePresent;
        }

        return _leaseStore.ValidateFencingToken(scope, nodeId, token, nowUtc);
    }
}
