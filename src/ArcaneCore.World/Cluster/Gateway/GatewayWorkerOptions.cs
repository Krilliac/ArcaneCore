using ArcaneCore.Kernel.Cluster;

namespace ArcaneCore.World.Cluster.Gateway;

/// <summary>
/// Worker mode of the world daemon (Tier 3a gateway passthrough, <c>docs/areas/gateway.md</c>): an extra listener that an
/// <c>ArcaneCore.Gateway</c> process connects to and forwards its clients through. Off by default; with it off nothing is
/// registered and the world runs in Combined mode exactly as before. The world's own client listener (World:Port) stays open
/// either way.
/// </summary>
public sealed class GatewayWorkerOptions
{
    public const string SectionName = "Cluster:Worker";

    /// <summary>`true` opens the gateway listener (worker mode) and makes character fencing mandatory: Cluster:Fencing:Enabled must be true, and the characters write fence is attached either way. `false`, the default, is Combined mode: no listener, no change.</summary>
    public bool Enabled { get; set; }

    /// <summary>Standby (Tier 3b handoff target; needs Enabled): start the process, build its services and link to the gateway, but admit no session, claim no characters ownership, run no schema DDL, take no writer lock until promoted (promotion takes it and is refused while the old world holds it), open no World:Port listener, and attach no feature nor start the simulation until promoted by a handoff (features read the characters database, which the active world still owns). `false`, the default, is an active worker.</summary>
    public bool Standby { get; set; }

    /// <summary>Address of the gateway listener. Keep it on loopback or a private network: the link is not encrypted.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>Port of the gateway listener; must equal the gateway's Gateway:WorkerPort.</summary>
    public int Port { get; set; } = 8095;

    /// <summary>Shared secret a gateway proves (HMAC-SHA256 of a per-link challenge) before it may open sessions; must equal Gateway:LinkSecret. Required in worker mode on every bind address, loopback included: at least 16 characters, or the world refuses to start. An admitted gateway opens sessions for any account it authenticated, so treat the secret like a database password.</summary>
    public string LinkSecret { get; set; } = string.Empty;

    /// <summary>This worker's node id in the link manifest (shown in the gateway's log).</summary>
    public string NodeId { get; set; } = "world";

    /// <summary>Realm id both ends of the link must agree on.</summary>
    public int RealmId { get; set; } = 1;

    /// <summary>Content revision both ends of the link must agree on (an operator label; nothing reads it from the data).</summary>
    public string ContentRevision { get; set; } = "dev";

    /// <summary>Characters schema version both ends must agree on (an operator label, not read from the database).</summary>
    public int CharactersSchemaVersion { get; set; } = 1;

    /// <summary>World schema version both ends must agree on (an operator label, not read from the database).</summary>
    public int WorldSchemaVersion { get; set; } = 1;

    /// <summary>The manifest this worker admits gateways against.</summary>
    public ClusterNodeManifest ToManifest(Guid incarnation) => GatewayLinkManifest.Create(
        ClusterRole.Worker, NodeId, incarnation, RealmId, ContentRevision, CharactersSchemaVersion, WorldSchemaVersion);
}

/// <summary>Builds the <see cref="ClusterNodeManifest"/> both ends of a gateway link advertise.</summary>
public static class GatewayLinkManifest
{
    public static ClusterNodeManifest Create(
        ClusterRole role, string nodeId, Guid incarnation, int realmId, string contentRevision, int charactersSchemaVersion, int worldSchemaVersion)
        => new(realmId, nodeId, incarnation, role, ClusterNodeManifest.CurrentContractVersion, charactersSchemaVersion, worldSchemaVersion, contentRevision);
}
