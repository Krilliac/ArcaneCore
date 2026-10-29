namespace ArcaneCore.World.Cluster.Social;

/// <summary>
/// Which part of the M15 cluster this World process runs as (Cluster:Node, <c>docs/areas/gateway.md</c>). Off by default:
/// <see cref="ClusterNodeRole.Combined"/> runs every feature in this process exactly as before. The owner id of a node's
/// leases is its <c>Cluster:Worker:NodeId</c>.
/// </summary>
public sealed class ClusterNodeOptions
{
    public const string SectionName = "Cluster:Node";

    /// <summary>
    /// The role this World process runs as: `Combined` (the default) runs every feature here. `MapWorker` needs Cluster:Worker:Enabled = true; the realm-wide
    /// singletons (the auction house bot and AutoBroadcast) then run on this node only while it holds their database lease. `Social`, `Economy` and
    /// `Gateway` are refused on a World process: the shared auction, mail and presence state they would need does not exist yet.
    /// </summary>
    public ClusterNodeRole Role { get; set; } = ClusterNodeRole.Combined;

    /// <summary>How long a realm singleton lease stays valid without a renewal (5 to 600). A node that dies releases its singletons to another node after this long. Only used when Role is not Combined.</summary>
    public int AuthorityLeaseTtlSeconds { get; set; } = 30;

    /// <summary>How often a held lease is renewed (1 to a third of the TTL). A node that cannot renew in time stops itself rather than run a singleton without the lease. Only used when Role is not Combined.</summary>
    public int AuthorityLeaseRenewSeconds { get; set; } = 10;

    /// <summary>The lease TTL as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan LeaseTtl => TimeSpan.FromSeconds(AuthorityLeaseTtlSeconds);

    /// <summary>The renewal interval as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan LeaseRenew => TimeSpan.FromSeconds(AuthorityLeaseRenewSeconds);

    /// <summary>What is wrong with these values for a World process; empty when valid. <paramref name="workerEnabled"/> is Cluster:Worker:Enabled.</summary>
    public IEnumerable<string> Validate(bool workerEnabled)
    {
        if (!Enum.IsDefined(Role))
        {
            yield return $"Role {(int)Role} is not a cluster role (Combined or MapWorker)";
            yield break;
        }

        switch (Role)
        {
            case ClusterNodeRole.Social:
                yield return "Role Social is not hosted by a World process: the presence, whisper and guild routing and AutoBroadcast's all-player broadcast only reach this process's own sessions, so a social-only node would serve nobody; use Combined or MapWorker";
                break;
            case ClusterNodeRole.Economy:
                yield return "Role Economy is not hosted by a World process: auctions, mail and escrow live in this process's memory (EconomyFeature keeps them in-process), so an economy-only node has no shared state to serve; use Combined or MapWorker";
                break;
            case ClusterNodeRole.Gateway:
                yield return "Role Gateway is not a World role: the client gateway is the separate ArcaneCore.Gateway executable; use Combined or MapWorker";
                break;
            case ClusterNodeRole.MapWorker when !workerEnabled:
                yield return "Role MapWorker needs Cluster:Worker:Enabled = true: a map worker is the world a gateway links to";
                break;
        }

        if (AuthorityLeaseTtlSeconds is < 5 or > 600)
        {
            yield return $"AuthorityLeaseTtlSeconds {AuthorityLeaseTtlSeconds} must be between 5 and 600";
        }

        if (AuthorityLeaseRenewSeconds < 1 || AuthorityLeaseRenewSeconds > AuthorityLeaseTtlSeconds / 3)
        {
            yield return $"AuthorityLeaseRenewSeconds {AuthorityLeaseRenewSeconds} must be between 1 and a third of AuthorityLeaseTtlSeconds ({AuthorityLeaseTtlSeconds / 3})";
        }
    }
}
