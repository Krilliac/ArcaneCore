using System.Net;
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

    /// <summary>The shared link secret (Cluster:Worker:LinkSecret on the worker): at least 16 characters, or the gateway refuses to start.</summary>
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

    /// <summary>Every problem with the bound values, as <c>Gateway:Key: problem</c> lines; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(BindAddress) || !IPAddress.TryParse(BindAddress, out _))
        {
            problems.Add($"{SectionName}:BindAddress: must be a valid IP address.");
        }

        if (Port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            problems.Add($"{SectionName}:Port: must be {IPEndPoint.MinPort}..{IPEndPoint.MaxPort}.");
        }

        if (MaxConnections < 0)
        {
            problems.Add($"{SectionName}:MaxConnections: must be 0 (unlimited) or positive.");
        }

        if (MaxConnectionsPerIp < 0)
        {
            problems.Add($"{SectionName}:MaxConnectionsPerIp: must be 0 (unlimited) or positive.");
        }

        if (PreAuthTimeout < TimeSpan.Zero)
        {
            problems.Add($"{SectionName}:PreAuthTimeout: must be 0 (disabled) or positive.");
        }

        if (MaxOutboundBytes <= 0)
        {
            problems.Add($"{SectionName}:MaxOutboundBytes: must be positive.");
        }

        if (string.IsNullOrWhiteSpace(WorkerAddress) || !IPAddress.TryParse(WorkerAddress, out _))
        {
            problems.Add($"{SectionName}:WorkerAddress: must be a valid IP address.");
        }

        if (WorkerPort is < 1 or > IPEndPoint.MaxPort)
        {
            problems.Add($"{SectionName}:WorkerPort: must be 1..{IPEndPoint.MaxPort}.");
        }

        if (LinkRetryDelay < TimeSpan.Zero)
        {
            problems.Add($"{SectionName}:LinkRetryDelay: must not be negative.");
        }

        if (string.IsNullOrWhiteSpace(NodeId))
        {
            problems.Add($"{SectionName}:NodeId: must not be empty.");
        }

        if (RealmId <= 0)
        {
            problems.Add($"{SectionName}:RealmId: must be positive.");
        }

        if (string.IsNullOrWhiteSpace(ContentRevision))
        {
            problems.Add($"{SectionName}:ContentRevision: must not be empty.");
        }

        if (CharactersSchemaVersion <= 0)
        {
            problems.Add($"{SectionName}:CharactersSchemaVersion: must be positive.");
        }

        if (WorldSchemaVersion <= 0)
        {
            problems.Add($"{SectionName}:WorldSchemaVersion: must be positive.");
        }

        return problems;
    }
}
