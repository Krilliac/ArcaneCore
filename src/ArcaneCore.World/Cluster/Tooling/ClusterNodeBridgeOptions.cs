namespace ArcaneCore.World.Cluster.Tooling;

/// <summary>
/// Configuration of <see cref="ClusterNodeBridgeFeature"/>, the monitor that connects the World to local cluster node processes.
/// Off by default: a single-process World has no nodes to answer, so the monitor would only log retries.
/// </summary>
public sealed class ClusterNodeBridgeOptions
{
    public const string SectionName = "Cluster:NodeBridge";

    /// <summary>`true` starts the node bridge: the World connects to each endpoint in `Endpoints`, logs node connection events and heartbeats, and syncs live worker placement. `false`, the default, does nothing at all: no sockets, no log lines, no background task. Read once when the daemon starts.</summary>
    public bool Enabled { get; set; }

    /// <summary>The nodes the bridge monitors, as `;`-separated entries of `nodeId|role|host:port|maps`, where `maps` is a comma list of map ids or `-` for a realm-wide node. The default is the seven local nodes: worker:EK 19081, worker:Kalimdor 19082, worker:instances 19083, gateway 19084, social 19085, coordinator 19086, supervisor 19087. The ARCANECORE_CLUSTER_ENDPOINTS environment variable, when set, replaces it. Only read when `Enabled` is true.</summary>
    public string Endpoints { get; set; } = ClusterNodeBridgeFeature.DefaultEndpointsSpec;
}
