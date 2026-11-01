using System.Net;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Cluster.Gateway;

namespace ArcaneCore.Gateway;

/// <summary>
/// The <c>Gateway</c> section of the gateway daemon (docs/areas/gateway.md). The gateway also reads <c>Database:Auth:*</c>
/// (the realm's auth database: session keys, account status, bans), <c>Net:Protection:*</c> (the same transport protections
/// as the world listener) and <c>Logging</c>.
/// </summary>
[ConfigDaemons(ConfigDaemon.Gateway)]
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>Address the client listener binds.</summary>
    [ConfigAllowed("an IPv4 or IPv6 address such as 0.0.0.0, 127.0.0.1 or ::1 (host names are not resolved)")]
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

    /// <summary>
    /// The world workers this gateway links to, one link each (node id, address, port, optional maps). When empty, the single-worker keys
    /// <see cref="WorkerAddress"/> and <see cref="WorkerPort"/> describe one entry named <see cref="LegacyWorkerNodeId"/>; when set, those two
    /// keys are ignored. Every worker must accept the same <see cref="LinkSecret"/>, <see cref="RealmId"/> and manifest values.
    /// </summary>
    public List<GatewayWorkerEntry> Workers { get; set; } = [];

    /// <summary>
    /// Node id of the worker new sessions open on (it must name a <see cref="Workers"/> entry, or <see cref="LegacyWorkerNodeId"/> in single-worker mode). While it is
    /// down, sessions open on the ready link with the fewest sessions. Empty, the default, designates none: sessions always spread over the ready links.
    /// </summary>
    public string LoginWorker { get; set; } = string.Empty;

    /// <summary>Node id given to the entry made from <see cref="WorkerAddress"/> and <see cref="WorkerPort"/>.</summary>
    public const string LegacyWorkerNodeId = "worker";

    /// <summary>Address of the world worker's gateway listener (Cluster:Worker:BindAddress). Used only while <see cref="Workers"/> is empty.</summary>
    public string WorkerAddress { get; set; } = "127.0.0.1";

    /// <summary>Port of the world worker's gateway listener (Cluster:Worker:Port). Used only while <see cref="Workers"/> is empty.</summary>
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

    /// <summary>The worker entries the gateway links to: <see cref="Workers"/>, or one entry made from the single-worker keys.</summary>
    public IReadOnlyList<GatewayWorkerEntry> EffectiveWorkers() => Workers.Count > 0
        ? Workers
        : [new GatewayWorkerEntry { NodeId = LegacyWorkerNodeId, Address = WorkerAddress, Port = WorkerPort }];

    /// <summary>Every problem with <see cref="Workers"/> (empty when it is empty: the single-worker keys are validated instead).</summary>
    public IReadOnlyList<string> ValidateWorkers()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < Workers.Count; index++)
        {
            GatewayWorkerEntry? entry = Workers[index];
            string prefix = $"{SectionName}:Workers:{index}";
            if (entry is null)
            {
                problems.Add($"{prefix}: must not be empty.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.NodeId))
            {
                problems.Add($"{prefix}:NodeId: must not be empty.");
            }
            else if (!seen.Add(entry.NodeId))
            {
                problems.Add($"{prefix}:NodeId: '{entry.NodeId}' is used by an earlier entry.");
            }

            if (string.IsNullOrWhiteSpace(entry.Address) || !IPAddress.TryParse(entry.Address, out _))
            {
                problems.Add($"{prefix}:Address: must be a valid IP address.");
            }

            if (entry.Port is < 1 or > IPEndPoint.MaxPort)
            {
                problems.Add($"{prefix}:Port: must be 1..{IPEndPoint.MaxPort}.");
            }

            if (entry.Maps is null || entry.Maps.Any(map => map < 0))
            {
                problems.Add($"{prefix}:Maps: map ids must not be negative.");
            }
        }

        return problems;
    }

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

        if (Workers.Count > 0)
        {
            problems.AddRange(ValidateWorkers());
        }
        else
        {
            if (string.IsNullOrWhiteSpace(WorkerAddress) || !IPAddress.TryParse(WorkerAddress, out _))
            {
                problems.Add($"{SectionName}:WorkerAddress: must be a valid IP address.");
            }

            if (WorkerPort is < 1 or > IPEndPoint.MaxPort)
            {
                problems.Add($"{SectionName}:WorkerPort: must be 1..{IPEndPoint.MaxPort}.");
            }
        }

        if (!string.IsNullOrWhiteSpace(LoginWorker) && !EffectiveWorkers().Any(entry => string.Equals(entry?.NodeId, LoginWorker, StringComparison.Ordinal)))
        {
            problems.Add($"{SectionName}:LoginWorker: '{LoginWorker}' names no worker entry.");
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

/// <summary>One world worker the gateway links to (<c>Gateway:Workers:N</c>).</summary>
public sealed class GatewayWorkerEntry
{
    /// <summary>The worker's name in logs, metrics and link state; unique among the entries.</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Address of the worker's gateway listener (its Cluster:Worker:BindAddress).</summary>
    public string Address { get; set; } = "127.0.0.1";

    /// <summary>Port of the worker's gateway listener (its Cluster:Worker:Port).</summary>
    public int Port { get; set; } = 8095;

    /// <summary>Map ids this worker owns; empty means it is not map-restricted. Surfaced in the link state for map-aware routing.</summary>
    public int[] Maps { get; set; } = [];
}
