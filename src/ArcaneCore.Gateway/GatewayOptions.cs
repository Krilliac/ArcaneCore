using ArcaneCore.Kernel.Cluster;
using ArcaneCore.World.Cluster.Gateway;

namespace ArcaneCore.Gateway;

/// <summary>
/// The <c>Gateway</c> section of the gateway daemon (docs/areas/gateway.md). The gateway also reads <c>Database:Auth:*</c>
/// (the realm's auth database: session keys, account status, bans), <c>Net:Protection:*</c> (the same transport protections
/// as the world listener) and <c>Logging</c>.
/// </summary>
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>Address the client listener binds.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Port clients connect to; the realm list must name it (realmlist address) instead of the world's World:Port.</summary>
    public int Port { get; set; } = 8086;

    /// <summary>Most simultaneous client connections; 0 means no cap of the gateway's own (Net:Protection still applies).</summary>
    public int MaxConnections { get; set; }

    /// <summary>Most simultaneous client connections from one address; 0 means no cap of the gateway's own.</summary>
    public int MaxConnectionsPerIp { get; set; }

    /// <summary>A connection that has not sent a valid CMSG_AUTH_SESSION within this time is closed (the world's World:PreAuthTimeout rule).</summary>
    public TimeSpan PreAuthTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>A client whose unsent data exceeds this many bytes (it stopped reading) is disconnected (World:MaxOutboundBytes).</summary>
    public long MaxOutboundBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>Address of the world worker's gateway listener (Cluster:Worker:BindAddress).</summary>
    public string WorkerAddress { get; set; } = "127.0.0.1";

    /// <summary>Port of the world worker's gateway listener (Cluster:Worker:Port).</summary>
    public int WorkerPort { get; set; } = 8095;

    /// <summary>The shared link secret (Cluster:Worker:LinkSecret on the worker).</summary>
    public string LinkSecret { get; set; } = string.Empty;

    /// <summary>First wait before reconnecting a lost or refused worker link; doubles up to 30 s.</summary>
    public TimeSpan LinkRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>This gateway's node id in the link manifest.</summary>
    public string NodeId { get; set; } = "gateway";

    /// <summary>Must equal the worker's Cluster:Worker:RealmId.</summary>
    public int RealmId { get; set; } = 1;

    /// <summary>Must equal the worker's Cluster:Worker:ContentRevision.</summary>
    public string ContentRevision { get; set; } = "dev";

    /// <summary>Must equal the worker's Cluster:Worker:CharactersSchemaVersion.</summary>
    public int CharactersSchemaVersion { get; set; } = 1;

    /// <summary>Must equal the worker's Cluster:Worker:WorldSchemaVersion.</summary>
    public int WorldSchemaVersion { get; set; } = 1;

    public ClusterNodeManifest ToManifest(Guid incarnation) => GatewayLinkManifest.Create(
        ClusterRole.Gateway, NodeId, incarnation, RealmId, ContentRevision, CharactersSchemaVersion, WorldSchemaVersion);
}
