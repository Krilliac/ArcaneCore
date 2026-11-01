using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Cluster.Supervision;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Cluster.Tooling;

/// <summary>
/// Live snapshot of a cluster node endpoint monitored by <see cref="ClusterNodeBridgeFeature"/>.
/// </summary>
public sealed record ConnectedClusterNodeStatus(
    string NodeId,
    string RoleLabel,
    IPEndPoint EndPoint,
    bool IsConnected,
    long WorkerOwnerEpoch,
    long RpcHeartbeats,
    string MapsSummary,
    DateTimeOffset? ConnectedAtUtc);

/// <summary>
/// World feature that, only when <c>Cluster:NodeBridge:Enabled</c> is true (off by default; with it off the feature does nothing),
/// connects the running <c>ArcaneCore.World</c> server to the cluster node processes (by default the local
/// <c>worker:EK</c> on 19081, <c>worker:Kalimdor</c> on 19082, <c>worker:instances</c> on 19083, <c>gateway</c> on 19084,
/// <c>supervisor</c> on 19087; no process listens as social or coordinator, so they are not monitored), logs node connection events and
/// periodic topology heartbeats in the WorldServer console, and synchronizes live worker placement in
/// <see cref="Placement"/>.
/// </summary>
public sealed class ClusterNodeBridgeFeature(IServiceProvider services, ILogger<ClusterNodeBridgeFeature> logger)
    : IWorldFeature, IDisposable
{
    /// <summary>
    /// The fallback node list when neither an endpoint override nor supervisor children are configured: the three map workers, the gateway and the
    /// supervisor. Under a supervisor the bridge lists the children it was configured with instead (<see cref="BuildSpecFromSupervisor"/>); an
    /// operator can still override with <c>Cluster:NodeBridge:Endpoints</c> or <c>ARCANECORE_CLUSTER_ENDPOINTS</c>.
    /// </summary>
    public const string DefaultEndpointsSpec =
        "worker:EK|Worker|127.0.0.1:19081|0;" +
        "worker:Kalimdor|Worker|127.0.0.1:19082|1;" +
        "worker:instances|Worker|127.0.0.1:19083|33,34,36,43,47,48,70,90,109,129,189,209,229,230,249,289,309,329,349,389,409,429,469,489,509,529,531,533;" +
        "gateway|Gateway|127.0.0.1:19084|-;" +
        "supervisor|Supervisor|127.0.0.1:19087|-";

    private sealed class MonitoredNode(
        string nodeId,
        string roleLabel,
        IPEndPoint endPoint,
        uint[] assignedMaps,
        string mapsSummary,
         ulong probeCharacterGuid)
    {
        public string NodeId { get; } = nodeId;
        public string RoleLabel { get; } = roleLabel;
        public IPEndPoint EndPoint { get; } = endPoint;
        public uint[] AssignedMaps { get; } = assignedMaps;
        public string MapsSummary { get; } = mapsSummary;
        public ulong ProbeCharacterGuid { get; } = probeCharacterGuid;

        public LoopbackGatewayWorkerClient? Client { get; set; }
        public volatile bool IsConnected;
        public long WorkerOwnerEpoch;
        public volatile string? AnnouncedMaps;
        public long RpcHeartbeats;
        public DateTimeOffset? ConnectedAtUtc;
        public int FailedConnects;
        public DateTimeOffset NextConnectAttemptUtc;
    }

    /// <summary>Longest wait between connect attempts to a node that is not answering.</summary>
    internal static readonly TimeSpan MaxConnectBackoff = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long to wait before the next connect attempt after <paramref name="failedConnects"/> failures in a row: 5 s, doubling, capped at
    /// <see cref="MaxConnectBackoff"/>. A World with no cluster nodes running would otherwise retry every node every pass, and each attempt
    /// ends in a cancelled connect (several first-chance exceptions apiece).
    /// </summary>
    internal static TimeSpan ConnectBackoff(int failedConnects)
    {
        if (failedConnects <= 0)
        {
            return TimeSpan.Zero;
        }

        int shift = Math.Min(failedConnects - 1, 10);
        TimeSpan delay = TimeSpan.FromSeconds(5 * (1L << shift));
        return delay < MaxConnectBackoff ? delay : MaxConnectBackoff;
    }

    private readonly List<MonitoredNode> _nodes = [];
    private readonly CancellationTokenSource _cts = new();
    private WorldRuntime? _world;
    private Task? _monitorTask;
    private bool _allConnectedLogged;
    private int _disposed;

    public bool IsActive => _monitorTask is not null;

    /// <summary>True once <see cref="Attach"/> found <c>Cluster:NodeBridge:Enabled</c> on; false means the bridge is switched off and does nothing.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>How long the monitor waits for WorldHost to finish startup before its first connect pass (a test seam; set before <see cref="Attach"/>).</summary>
    internal TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(2);

    public ClusterMapPlacementTable Placement { get; } = new();

    public IReadOnlyList<ConnectedClusterNodeStatus> SnapshotNodes()
    {
        if (_nodes.Count == 0)
        {
            return [];
        }

        var result = new List<ConnectedClusterNodeStatus>(_nodes.Count);
        foreach (MonitoredNode n in _nodes)
        {
            result.Add(new ConnectedClusterNodeStatus(
                NodeId: n.NodeId,
                RoleLabel: n.RoleLabel,
                EndPoint: n.EndPoint,
                IsConnected: n.IsConnected,
                WorkerOwnerEpoch: Interlocked.Read(ref n.WorkerOwnerEpoch),
                RpcHeartbeats: Interlocked.Read(ref n.RpcHeartbeats),
                MapsSummary: n.MapsSummary,
                ConnectedAtUtc: n.ConnectedAtUtc));
        }

        return result;
    }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;

        // Cluster:NodeBridge:Enabled (off by default, read once at start): disabled means no sockets, no log lines, no task, no placement sync.
        ClusterNodeBridgeOptions options = services.GetService<IOptions<ClusterNodeBridgeOptions>>()?.Value ?? new ClusterNodeBridgeOptions();
        string? explicitSpec = Environment.GetEnvironmentVariable("ARCANECORE_CLUSTER_ENDPOINTS");
        if (!options.Enabled)
        {
            if (!string.IsNullOrWhiteSpace(explicitSpec))
            {
                logger.LogWarning("ARCANECORE_CLUSTER_ENDPOINTS is set but Cluster:NodeBridge:Enabled is false; the node bridge is off");
            }

            return;
        }

        IsEnabled = true;
        // Precedence: the environment variable, then an Endpoints value an operator changed, then the nodes the cluster supervisor was configured
        // to start (start-cluster.ps1 hands the world child that configuration), then the default.
        bool endpointsChanged = !string.IsNullOrWhiteSpace(options.Endpoints)
            && !string.Equals(options.Endpoints, DefaultEndpointsSpec, StringComparison.Ordinal);
        string spec;
        if (!string.IsNullOrWhiteSpace(explicitSpec))
        {
            spec = explicitSpec;
        }
        else if (endpointsChanged)
        {
            spec = options.Endpoints;
        }
        else
        {
            spec = BuildSpecFromSupervisor(services.GetService<IConfiguration>()) ?? DefaultEndpointsSpec;
        }

        ParseSpec(spec);
        if (_nodes.Count == 0)
        {
            logger.LogWarning("Cluster:NodeBridge:Enabled is true but no valid endpoints were parsed; the node bridge monitors nothing");
            return;
        }

        _monitorTask = Task.Run(() => MonitorLoopAsync(_cts.Token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already disposed during shutdown race.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("cluster.bridge", ex);
            }
        }

        if (_monitorTask is not null)
        {
            await _monitorTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        foreach (MonitoredNode node in _nodes)
        {
            if (node.Client is not null)
            {
                await node.Client.DisposeAsync().ConfigureAwait(false);
                node.Client = null;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed during shutdown race.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.bridge", ex);
        }

        _cts.Dispose();
    }

    /// <summary>
    /// The endpoint spec for the nodes the cluster supervisor is configured to start (<c>Cluster:Supervisor:Processes</c>, which the supervisor
    /// console's <c>Cluster__Supervisor__...</c> environment variables carry and the world child inherits): one entry per worker child (port from
    /// its <c>--Cluster:Worker:Port</c> argument, maps from <c>Maps</c> or <c>--Cluster:Worker:OwnedMaps</c>). Gateway children are not
    /// monitored (their port is the client listener, not a worker-link endpoint). Children of other roles, and children with no port in their arguments, are not monitored. Null when no supervisor
    /// children are configured (the caller then uses the default); an empty string when some are configured but none is a monitorable node
    /// (a combined world), so the bridge does not monitor nodes nobody started.
    /// </summary>
    internal static string? BuildSpecFromSupervisor(IConfiguration? configuration)
    {
        if (configuration is null)
        {
            return null;
        }

        var supervisor = new ClusterSupervisorOptions();
        try
        {
            configuration.GetSection(ClusterSupervisorOptions.SectionName).Bind(supervisor);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
        {
            return null;
        }

        if (supervisor.Processes.Count == 0)
        {
            return null;
        }

        var entries = new List<string>();
        foreach (SupervisedProcessConfig process in supervisor.Processes.Values.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            bool isWorker = string.Equals(process.Role, SupervisedRoles.Worker, StringComparison.OrdinalIgnoreCase);
            if (!isWorker)
            {
                // Gateway children are deliberately not monitored: their --Gateway:Port is the WoW client listener, not a
                // worker-link endpoint, and the monitor speaks the worker-link handshake to every node it lists.
                continue;
            }

            string? portText = ArgumentValue(process.Arguments, "Cluster:Worker:Port");
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
            {
                continue;
            }

            string nodeId = ArgumentValue(process.Arguments, "Cluster:Worker:NodeId") ?? process.Name;
            if (string.IsNullOrWhiteSpace(nodeId) || nodeId.AsSpan().IndexOfAny('|', ';') >= 0)
            {
                continue;
            }

            string host = "127.0.0.1";
            if (IPAddress.TryParse(ArgumentValue(process.Arguments, "Cluster:Worker:BindAddress"), out IPAddress? bind)
                && !bind.Equals(IPAddress.Any) && !bind.Equals(IPAddress.IPv6Any))
            {
                host = bind.ToString();
            }

            string maps = ExpandMapList(!string.IsNullOrWhiteSpace(process.Maps) ? process.Maps : ArgumentValue(process.Arguments, "Cluster:Worker:OwnedMaps"));

            string endpoint = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
            entries.Add(string.Create(CultureInfo.InvariantCulture, $"{nodeId.Trim()}|Worker|{endpoint}:{port}|{maps}"));
        }

        return string.Join(';', entries);
    }

    /// <summary>The value of <c>--&lt;key&gt;=value</c> in a child's command-line string (surrounding quotes dropped), or null.</summary>
    private static string? ArgumentValue(string arguments, string key)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        Match m = Regex.Match(arguments, "--" + Regex.Escape(key) + "=(?:\"([^\"]*)\"|(\\S+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success)
        {
            return null;
        }

        string value = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        return value.Length == 0 ? null : value;
    }

    /// <summary>Turns a map list with inclusive ranges (<c>0,1,30-32</c>) into the comma list of ids the endpoint spec takes; <c>-</c> when empty or unparseable.</summary>
    private static string ExpandMapList(string? maps)
    {
        if (string.IsNullOrWhiteSpace(maps))
        {
            return "-";
        }

        var ids = new List<uint>();
        foreach (string part in maps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dash = part.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0)
            {
                if (uint.TryParse(part.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out uint lo)
                    && uint.TryParse(part.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint hi)
                    && lo <= hi && hi - lo < 1024)
                {
                    for (uint id = lo; id <= hi; id++)
                    {
                        ids.Add(id);
                    }
                }
            }
            else if (uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out uint single))
            {
                ids.Add(single);
            }
        }

        return ids.Count == 0 ? "-" : string.Join(',', ids);
    }

    private void ParseSpec(string spec)
    {
        ulong nextGuid = 950_001;
        foreach (string rawEntry in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = rawEntry.Split('|', 4, StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || !IPEndPoint.TryParse(parts[2], out IPEndPoint? ep))
            {
                logger.LogWarning("Cluster node bridge dropped an invalid endpoint entry (expected nodeId|role|host:port|maps): {Entry}", rawEntry);
                continue;
            }

            string nodeId = parts[0];
            string roleLabel = parts[1];
            string mapsRaw = parts.Length >= 4 ? parts[3] : "-";
            uint[] maps = [];
            string mapsSummary = "realm-wide";
            if (!string.IsNullOrWhiteSpace(mapsRaw) && mapsRaw != "-")
            {
                maps = mapsRaw
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint m) ? m : uint.MaxValue)
                    .Where(m => m != uint.MaxValue)
                    .ToArray();
                mapsSummary = maps.Length switch
                {
                    1 when maps[0] == 0 => "0 (Eastern Kingdoms)",
                    1 when maps[0] == 1 => "1 (Kalimdor)",
                    <= 3 => string.Join(",", maps),
                    _ => $"{maps.Length} instance/BG maps",
                };
            }

            _nodes.Add(new MonitoredNode(nodeId, roleLabel, ep, maps, mapsSummary, nextGuid++));
        }
    }

    /// <summary>
    /// The manifest this world presents to the nodes it monitors. The characters and world schema versions are this build's (the
    /// versions the database contexts are composed at), not literals that go stale each time a schema module is added.
    /// </summary>
    internal static ClusterNodeManifest BuildWorldManifest(Guid nodeIncarnation)
    {
        ClusterCommands.DiagSchemaVersions schema = ClusterCommands.DiagSchemaVersions.Current;
        return new(
            RealmId: 1,
            NodeId: "world-1",
            NodeIncarnation: nodeIncarnation,
            Role: ClusterRole.Gateway,
            ContractVersion: ClusterNodeManifest.CurrentContractVersion,
            CharactersSchemaVersion: schema.Characters,
            WorldSchemaVersion: schema.World,
            ContentRevision: "dbc-1.12.1-5875-w44");
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        ClusterNodeManifest worldManifest = BuildWorldManifest(_world?.CharacterOwnership?.Incarnation ?? Guid.NewGuid());

        // Wait briefly for WorldHost to finish initial startup before logging node connections.
        try
        {
            await Task.Delay(StartupDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        long tick = 0;
        long sequence = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                tick++;
                int connectedCount = 0;
                long totalHeartbeats = 0;

                foreach (MonitoredNode node in _nodes)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    if ((!node.IsConnected || node.Client is null) && DateTimeOffset.UtcNow >= node.NextConnectAttemptUtc)
                    {
                        await TryConnectNodeAsync(worldManifest, node, ct).ConfigureAwait(false);
                    }

                    if (node.IsConnected && node.Client is not null)
                    {
                        long seq = Interlocked.Increment(ref sequence);
                        var ping = new ClusterSessionPacket(
                            SessionGeneration: 1,
                            AccountId: (int)(node.ProbeCharacterGuid - 950_000),
                            CharacterGuid: node.ProbeCharacterGuid,
                            OwnerEpoch: 1,
                            Sequence: seq,
                            Opcode: 0x1DC,
                            Payload: new byte[] { 1, 2, 3, 4 });

                        try
                        {
                            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            sendCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
                            bool ok = await node.Client.SendToWorkerAsync(ping, sendCts.Token).ConfigureAwait(false);
                            if (ok)
                            {
                                long hb = Interlocked.Increment(ref node.RpcHeartbeats);
                                totalHeartbeats += hb;
                                connectedCount++;
                            }
                            else
                            {
                                await HandleDisconnectAsync(node).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException)
                        {
                            if (ex is not (OperationCanceledException or IOException or ObjectDisposedException or System.Net.Sockets.SocketException))
                            {
                                FaultLedger.Shared.Record("cluster.bridge", ex);
                            }

                            await HandleDisconnectAsync(node).ConfigureAwait(false);
                        }
                    }
                }

                UpdateWorldPlacementLoads();

                if (connectedCount == _nodes.Count && _nodes.Count > 0)
                {
                    if (!_allConnectedLogged)
                    {
                        _allConnectedLogged = true;
                        string summaryList = string.Join(", ", _nodes.Select(n => $"{n.NodeId}@{n.EndPoint.Port}({n.RoleLabel})"));
                        logger.LogInformation(
                            "Cluster topology active: all {Connected}/{Total} cluster nodes connected to WorldServer [{Nodes}]",
                            connectedCount,
                            _nodes.Count,
                            summaryList);
                    }
                    else if (tick % 6 == 0)
                    {
                        (int ek, int kal, int inst) = CountPlayersByPartition();
                        logger.LogInformation(
                            "Cluster heartbeat: {Connected}/{Total} nodes connected | worker:EK={Ek} players, worker:Kalimdor={Kal} players, worker:instances={Inst} players | rpcHeartbeats={Heartbeats}",
                            connectedCount,
                            _nodes.Count,
                            ek,
                            kal,
                            inst,
                            totalHeartbeats);
                    }
                }
                else
                {
                    _allConnectedLogged = false;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AnomalyLedger.Shared.Report("cluster", "gateway-fault", "node-bridge", $"error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Warning);
                logger.LogFault("cluster.bridge", ex, "ClusterNodeBridgeFeature monitor iteration faulted");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task TryConnectNodeAsync(ClusterNodeManifest worldManifest, MonitoredNode node, CancellationToken ct)
    {
        var client = new LoopbackGatewayWorkerClient(worldManifest, node.EndPoint);
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
            await client.ConnectAndRegisterAsync(connectCts.Token).ConfigureAwait(false);

            ClusterSessionAttachResult attach = await client.AttachSessionAsync(
                accountId: (int)(node.ProbeCharacterGuid - 950_000),
                characterGuid: node.ProbeCharacterGuid,
                requestedGeneration: 1,
                expectedOwnerEpoch: 1,
                cancellationToken: connectCts.Token).ConfigureAwait(false);

            node.Client = client;
            node.AnnouncedMaps = client.AnnouncedOwnedMaps;
            node.IsConnected = true;
            node.ConnectedAtUtc = DateTimeOffset.UtcNow;
            node.FailedConnects = 0;
            node.NextConnectAttemptUtc = DateTimeOffset.MinValue;
            Interlocked.Exchange(ref node.WorkerOwnerEpoch, attach.WorkerOwnerEpoch);

            RegisterNodeInPlacementTable(node, isReady: true);

            logger.LogInformation(
                "Cluster node connected: {NodeId} (role={Role}, endpoint={EndPoint}, maps=[{Maps}], workerEpoch={Epoch})",
                node.NodeId,
                node.RoleLabel,
                node.EndPoint,
                node.MapsSummary,
                attach.WorkerOwnerEpoch);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not (OperationCanceledException or IOException or ObjectDisposedException or System.Net.Sockets.SocketException))
            {
                FaultLedger.Shared.Record("cluster.bridge", ex);
            }

            await client.DisposeAsync().ConfigureAwait(false);

            node.FailedConnects++;
            node.NextConnectAttemptUtc = DateTimeOffset.UtcNow + ConnectBackoff(node.FailedConnects);
            if (node.FailedConnects == 1)
            {
                logger.LogInformation(
                    "Cluster node {NodeId} (role={Role}) is not answering at {EndPoint} ({Reason}); retrying with backoff up to {MaxBackoff}",
                    node.NodeId,
                    node.RoleLabel,
                    node.EndPoint,
                    ex.GetType().Name,
                    MaxConnectBackoff);
            }
        }
    }

    private async Task HandleDisconnectAsync(MonitoredNode node)
    {
        bool wasConnected = node.IsConnected;
        node.IsConnected = false;
        node.FailedConnects = 0;
        node.NextConnectAttemptUtc = DateTimeOffset.MinValue;
        if (node.Client is not null)
        {
            await node.Client.DisposeAsync().ConfigureAwait(false);
            node.Client = null;
        }

        RegisterNodeInPlacementTable(node, isReady: false);

        if (wasConnected)
        {
            logger.LogWarning(
                "Cluster node disconnected from WorldServer: {NodeId} (role={Role}, endpoint={EndPoint})",
                node.NodeId,
                node.RoleLabel,
                node.EndPoint);
        }
    }

    private void RegisterNodeInPlacementTable(MonitoredNode node, bool isReady)
    {
        if (!string.Equals(node.RoleLabel, "Worker", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var workerId = new WorkerNodeId(node.NodeId);
        int maxPlayers = node.AssignedMaps.Length <= 1 ? 1200 : 600;
        int maxInstances = node.AssignedMaps.Length <= 1 ? 10 : 200;

        Placement.RegisterWorker(
            workerId,
            isReady: isReady,
            maxPlayerCapacity: maxPlayers,
            activePlayerCount: 0,
            maxInstanceCapacity: maxInstances);

        if (isReady && !string.IsNullOrWhiteSpace(node.AnnouncedMaps))
        {
            // The worker said which maps it owns in its HelloAck (Cluster:Worker:OwnedMaps): that list replaces the endpoint spec's.
            Placement.ApplyAnnouncedOwnership(workerId, node.AnnouncedMaps);
        }
        else if (isReady)
        {
            // No announcement this time: drop maps a previous announcement left assigned before applying the spec's.
            Placement.ReleaseStaticMaps(workerId);
            foreach (uint mapId in node.AssignedMaps)
            {
                if (mapId is 0 or 1)
                {
                    Placement.AssignStaticMap(mapId, workerId);
                }
            }
        }
    }

    private void UpdateWorldPlacementLoads()
    {
        if (_world is null)
        {
            return;
        }

        (int ek, int kal, int inst) = CountPlayersByPartition();
        foreach (MonitoredNode node in _nodes)
        {
            if (!node.IsConnected || !string.Equals(node.RoleLabel, "Worker", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int load = node.NodeId switch
            {
                ClusterCommands.DefaultWorkerEasternKingdoms => ek,
                ClusterCommands.DefaultWorkerKalimdor => kal,
                _ => inst,
            };
            Placement.UpdateWorkerLoad(new WorkerNodeId(node.NodeId), load);
        }
    }

    private (int Ek, int Kal, int Inst) CountPlayersByPartition()
    {
        if (_world is null)
        {
            return (0, 0, 0);
        }

        int ek = 0, kal = 0, inst = 0;
        foreach (Player p in _world.OnlinePlayers)
        {
            uint instId = p.Map?.InstanceId ?? 0;
            if (instId > 0 || (p.MapId != 0 && p.MapId != 1))
            {
                inst++;
            }
            else if (p.MapId == 0)
            {
                ek++;
            }
            else
            {
                kal++;
            }
        }

        return (ek, kal, inst);
    }
}
