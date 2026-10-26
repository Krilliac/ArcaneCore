using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Cluster.Tooling;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Clustering;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Diagnostics;
using ArcaneCore.World.Gm.Lookup;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Social;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Cluster.Tooling;

/// <summary>
/// Read-only in-game and console GM commands for inspecting cluster node identity, map and instance routing across workers,
/// character ownership fencing, realm-singleton authority leases, cross-worker social presence, and deterministic cluster diagnostics.
/// </summary>
public sealed class ClusterCommands : ICommandGroup
{
    public const string DefaultWorkerEasternKingdoms = "worker:EK";
    public const string DefaultWorkerKalimdor = "worker:Kalimdor";
    public const string DefaultWorkerInstances = "worker:instances";

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand(
            "cluster",
            AccountSecurity.GameMaster,
            "Syntax: .cluster [status|node|routes|nodes|leases|social|diag]\nInspect cluster node identity, map/instance worker routing, authority leases, social presence, and diagnostics.",
            RootOrStatus,
            Children:
            [
                new ChatCommand(
                    "status",
                    AccountSecurity.GameMaster,
                    "Syntax: .cluster status\nShow the cluster node identity, durable ownership fence, map routing summary, and authority leases.",
                    Status),
                new ChatCommand(
                    "node",
                    AccountSecurity.GameMaster,
                    "Syntax: .cluster node [$player]\nShow which cluster node and map worker host the selected or named player (or this daemon at the console), along with their ownership token and map coordinates.",
                    Node),
                new ChatCommand(
                    "routes",
                    AccountSecurity.GameMaster,
                    "Syntax: .cluster routes [$mapId]\nList the static continent and active instance map routes across cluster workers, with online player, bot, object, and transit counts.",
                    Routes),
                new ChatCommand(
                    "nodes",
                    AccountSecurity.GameMaster,
                    "Syntax: .cluster nodes\nList the active cluster worker and authority nodes, their readiness, drain state, and player/instance load.",
                    Nodes),
                new ChatCommand(
                    "leases",
                    AccountSecurity.GameMaster,
                    "Syntax: .cluster leases\nShow durable character ownership fencing, realm singleton authority leases (economy, social, instances), and write-behind queue depths.",
                    Leases),
                new ChatCommand(
                    "social",
                    AccountSecurity.GameMaster,
                    "Syntax: .cluster social\nShow cross-worker social presence counts by continent/worker, active groups, guilds, and social router state.",
                    Social),
                new ChatCommand(
                    "diag",
                    AccountSecurity.Administrator,
                    "Syntax: .cluster diag\nRun the deterministic cluster diagnostics collector and rollback-readiness check for this realm. The schema versions it checks are this build's (the versions its databases are composed at), not the version rows read from the databases; pending transfers are the transfer store's unresolved (PreCommit or Committed) count across all characters, and read 0 when no store is registered.",
                    Diag),
            ]),
    ];

    internal static bool RootOrStatus(CommandContext context, string args)
    {
        if (args.Trim().Length == 0)
        {
            return Status(context, string.Empty);
        }

        return false;
    }

    internal static bool DispatchFromArcane(CommandContext context, string args)
    {
        string trimmed = args.Trim();
        if (trimmed.Length == 0)
        {
            return Status(context, string.Empty);
        }

        int space = trimmed.IndexOf(' ');
        string sub = space < 0 ? trimmed : trimmed[..space];
        string rest = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();

        if ("status".Equals(sub, StringComparison.OrdinalIgnoreCase))
        {
            return Status(context, rest);
        }

        if ("node".Equals(sub, StringComparison.OrdinalIgnoreCase))
        {
            return Node(context, rest);
        }

        if ("routes".Equals(sub, StringComparison.OrdinalIgnoreCase))
        {
            return Routes(context, rest);
        }

        if ("nodes".Equals(sub, StringComparison.OrdinalIgnoreCase))
        {
            return Nodes(context, rest);
        }

        if ("leases".Equals(sub, StringComparison.OrdinalIgnoreCase))
        {
            return Leases(context, rest);
        }

        if ("social".Equals(sub, StringComparison.OrdinalIgnoreCase))
        {
            return Social(context, rest);
        }

        if ("diag".Equals(sub, StringComparison.OrdinalIgnoreCase))
        {
            if (context.Security < AccountSecurity.Administrator)
            {
                context.Reply(GmStrings.CommandUnavailable);
                return true;
            }

            return Diag(context, rest);
        }

        return false;
    }

    /// <summary>What <c>.cluster status</c> and <c>.cluster nodes</c> say instead of a node list while the node bridge is switched off.</summary>
    internal const string BridgeDisabledLine = "Cluster bridge: disabled (Cluster:NodeBridge:Enabled = false); no cluster nodes are monitored";

    /// <summary>What <c>.cluster status</c> and <c>.cluster nodes</c> say when the node bridge is on but no endpoint entry parsed.</summary>
    internal const string BridgeNoEndpointsLine = "Cluster bridge: enabled, no valid endpoints";

    internal static bool Status(CommandContext context, string args)
    {
        if (args.Trim().Length != 0)
        {
            return false;
        }

        CultureInfo c = CultureInfo.InvariantCulture;
        IServiceProvider services = context.Session.Services;
        CharacterFencingOptions fencing = GetFencingOptions(services);
        CharacterOwnershipToken? worldToken = context.World.CharacterOwnership;
        ClusterNodeManifest? manifest = services.GetService<ClusterNodeManifest>();
        string nodeId = manifest?.NodeId ?? fencing.OwnerNode;
        ClusterRole role = manifest?.Role ?? ClusterRole.Combined;
        Guid incarnation = manifest?.NodeIncarnation ?? worldToken?.Incarnation ?? Guid.Empty;
        int realmId = manifest?.RealmId ?? 1;

        context.Reply(string.Create(
            c,
            $"Cluster node: {nodeId} | role={role} | realm={realmId} | pid={Environment.ProcessId} | contract=v{ClusterNodeManifest.CurrentContractVersion} | inc={(incarnation == Guid.Empty ? "local" : incarnation.ToString("D"))}"));

        (int pendingWrites, int retainedFailures) = SumQueueDepths(services);
        context.Reply(string.Create(
            c,
            $"Ownership fence: {(fencing.Enabled ? "enabled" : $"disabled (claim-watch {fencing.ClaimWatchIntervalSeconds}s)")} | worldToken={FormatToken(worldToken)} | writes={pendingWrites} pending, {retainedFailures} retained"));

        if (!context.IsConsole)
        {
            Player target = context.SelectedPlayerOrSelf() ?? context.Player;
            uint instanceId = target.Map?.InstanceId ?? 0;
            string mapName = MapName(context.World, target.MapId, target.Map);
            string workerId = ResolveWorkerForMap(services, context.World, target.MapId, instanceId);
            context.Reply(string.Create(
                c,
                $"You / target: {target.Name} (guid {target.Guid.Counter}, acct {target.AccountId}) | map {target.MapId} [{mapName}] inst {instanceId} zone {target.ZoneId} -> worker={workerId} | charToken={FormatToken(target.CharacterOwnership ?? worldToken)}"));
        }

        HashSet<string> botNames = GetRunningBotNames(services);
        Player[] online = [.. context.World.OnlinePlayers];
        int ekTotal = 0, ekBots = 0, kalTotal = 0, kalBots = 0, instTotal = 0, instBots = 0;
        foreach (Player p in online)
        {
            bool isBot = botNames.Contains(p.Name);
            uint instId = p.Map?.InstanceId ?? 0;
            if (instId > 0 || (p.MapId != 0 && p.MapId != 1))
            {
                instTotal++;
                if (isBot) instBots++;
            }
            else if (p.MapId == 0)
            {
                ekTotal++;
                if (isBot) ekBots++;
            }
            else
            {
                kalTotal++;
                if (isBot) kalBots++;
            }
        }

        Map[] loadedMaps = [.. context.World.Maps];
        int instMaps = loadedMaps.Count(m => m.InstanceId > 0 || (m.MapId != 0 && m.MapId != 1));
        context.Reply(string.Create(
            c,
            $"Routing summary: {loadedMaps.Length} map(s) loaded, {online.Length} online ({online.Length - botNames.Count} human, {botNames.Count} bots) | {DefaultWorkerEasternKingdoms}: {ekTotal} ({ekTotal - ekBots}h/{ekBots}b) | {DefaultWorkerKalimdor}: {kalTotal} ({kalTotal - kalBots}h/{kalBots}b) | {DefaultWorkerInstances}: {instMaps} map(s), {instTotal} ({instTotal - instBots}h/{instBots}b)"));

        context.Reply(FormatLeasesSummaryLine(services, nodeId, worldToken));

        if (services.GetService<ClusterNodeBridgeFeature>() is { } bridge)
        {
            if (bridge.SnapshotNodes() is { Count: > 0 } bridgeNodes)
            {
                int connected = bridgeNodes.Count(n => n.IsConnected);
                string nodeSummary = string.Join(", ", bridgeNodes.Select(n => $"{n.NodeId}@{n.EndPoint.Port}:{(n.IsConnected ? "UP" : "DOWN")}"));
                context.Reply(string.Create(
                    c,
                    $"Cluster bridge: {connected}/{bridgeNodes.Count} node(s) connected to WorldServer [{nodeSummary}]"));
            }
            else if (!bridge.IsEnabled)
            {
                context.Reply(BridgeDisabledLine);
            }
            else
            {
                context.Reply(BridgeNoEndpointsLine);
            }
        }

        return true;
    }

    internal static bool Node(CommandContext context, string args)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        IServiceProvider services = context.Session.Services;
        CharacterFencingOptions fencing = GetFencingOptions(services);
        CharacterOwnershipToken? worldToken = context.World.CharacterOwnership;
        ClusterNodeManifest? manifest = services.GetService<ClusterNodeManifest>();
        string nodeId = manifest?.NodeId ?? fencing.OwnerNode;
        ClusterRole role = manifest?.Role ?? ClusterRole.Combined;

        Player? target = null;
        string trimmed = args.Trim();
        if (trimmed.Length > 0)
        {
            var parsed = new CommandArgs(trimmed);
            string? nameArg = parsed.ExtractLiteral();
            if (nameArg is null || !parsed.IsEmpty)
            {
                return false;
            }

            target = context.World.OnlinePlayers.FirstOrDefault(p => p.Name.Equals(nameArg, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                context.Reply(GmStrings.PlayerNotFound);
                return true;
            }
        }
        else if (!context.IsConsole)
        {
            target = context.SelectedPlayerOrSelf() ?? context.Player;
        }

        context.Reply(string.Create(
            c,
            $"Host node: {nodeId} | role={role} | pid={Environment.ProcessId} | uptime={GmDuration.SecsToTimeString((long)context.World.Uptime.TotalSeconds)} | fencing={(fencing.Enabled ? "enabled" : "disabled")} | worldToken={FormatToken(worldToken)}"));

        if (target is null)
        {
            context.Reply("Console invoker: pass a player name (.cluster node $player) to inspect a specific character's routed worker and ownership token.");
            return true;
        }

        uint instanceId = target.Map?.InstanceId ?? 0;
        string mapName = MapName(context.World, target.MapId, target.Map);
        string workerId = ResolveWorkerForMap(services, context.World, target.MapId, instanceId);
        HashSet<string> botNames = GetRunningBotNames(services);
        bool isBot = botNames.Contains(target.Name);

        context.Reply(string.Create(
            c,
            $"Character: {target.Name} (guid {target.Guid.Counter}, account {target.AccountId}, {(isBot ? "playerbot" : "human")}, security={target.Security})"));
        context.Reply(string.Create(
            c,
            $"Placement: node={nodeId} -> worker={workerId} | map {target.MapId} [{mapName}] instance {instanceId} zone {target.ZoneId} | pos=({target.X:F1}, {target.Y:F1}, {target.Z:F1})"));
        context.Reply(string.Create(
            c,
            $"Ownership: characterToken={FormatToken(target.CharacterOwnership)} | effectiveToken={FormatToken(target.CharacterOwnership ?? worldToken)}"));
        return true;
    }

    internal static bool Routes(CommandContext context, string args)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        IServiceProvider services = context.Session.Services;

        uint? filterMapId = null;
        string trimmed = args.Trim();
        if (trimmed.Length > 0)
        {
            var parsed = new CommandArgs(trimmed);
            if (!parsed.ExtractUInt32(out uint mapId) || !parsed.IsEmpty)
            {
                return false;
            }

            filterMapId = mapId;
        }

        ClusterMapPlacementTable? table = ResolvePlacementTable(services, context.World);
        if (table is not null)
        {
            IReadOnlyList<(uint MapId, WorkerNodeId WorkerId)> staticMaps = table.SnapshotStaticMaps();
            IReadOnlyList<(uint MapId, uint InstanceId, WorkerNodeId WorkerId)> instanceBindings = table.SnapshotInstanceBindings();
            context.Reply(string.Create(
                c,
                $"Placement table: {staticMaps.Count} static map route(s), {instanceBindings.Count} bound instance(s)."));
            foreach ((uint mapId, WorkerNodeId workerId) in staticMaps)
            {
                if (filterMapId is null || filterMapId.Value == mapId)
                {
                    context.Reply(string.Create(c, $"route static map {mapId} [{MapName(context.World, mapId, null)}] -> {workerId.Value}"));
                }
            }

            foreach ((uint mapId, uint instanceId, WorkerNodeId workerId) in instanceBindings)
            {
                if (filterMapId is null || filterMapId.Value == mapId)
                {
                    context.Reply(string.Create(c, $"route instance map {mapId} inst {instanceId} [{MapName(context.World, mapId, null)}] -> {workerId.Value}"));
                }
            }
        }
        else
        {
            context.Reply(string.Create(
                c,
                $"Static worker partitions: map 0 [Eastern Kingdoms] -> {DefaultWorkerEasternKingdoms} | map 1 [Kalimdor] -> {DefaultWorkerKalimdor} | instances/BGs -> {DefaultWorkerInstances}"));
        }

        HashSet<string> botNames = GetRunningBotNames(services);
        Dictionary<(uint MapId, uint InstanceId), (int Total, int Bots)> countsByMap = [];
        foreach (Player p in context.World.OnlinePlayers)
        {
            var key = (p.MapId, p.Map?.InstanceId ?? 0u);
            countsByMap.TryGetValue(key, out (int Total, int Bots) current);
            countsByMap[key] = (current.Total + 1, current.Bots + (botNames.Contains(p.Name) ? 1 : 0));
        }

        Map[] maps = [.. context.World.Maps
            .Where(m => filterMapId is null || m.MapId == filterMapId.Value)
            .OrderBy(m => m.MapId)
            .ThenBy(m => m.InstanceId)];

        context.Reply(string.Create(
            c,
            $"Active maps matching filter: {maps.Length} loaded, {context.World.OnlinePlayerCount} total online."));

        if (maps.Length == 0)
        {
            return true;
        }

        return LookupContentText.Send(
            context,
            maps.Select(map =>
            {
                string name = MapName(context.World, map.MapId, map);
                string worker = ResolveWorkerForMap(services, context.World, map.MapId, map.InstanceId);
                countsByMap.TryGetValue((map.MapId, map.InstanceId), out (int Total, int Bots) playerBreakdown);
                int humans = Math.Max(0, map.PlayerCount - playerBreakdown.Bots);
                return string.Create(
                    c,
                    $"map {map.MapId} inst {map.InstanceId} [{name}] -> {worker}: {map.PlayerCount} player(s) ({humans}h/{playerBreakdown.Bots}b), {map.ObjectCount} object(s), {map.TransitCount} in transit");
            }),
            string.Empty);
    }

    internal static bool Nodes(CommandContext context, string args)
    {
        if (args.Trim().Length != 0)
        {
            return false;
        }

        CultureInfo c = CultureInfo.InvariantCulture;
        IServiceProvider services = context.Session.Services;
        CharacterFencingOptions fencing = GetFencingOptions(services);
        ClusterNodeManifest? manifest = services.GetService<ClusterNodeManifest>();
        string localNodeId = manifest?.NodeId ?? fencing.OwnerNode;
        ClusterRole localRole = manifest?.Role ?? ClusterRole.Combined;
        HashSet<string> botNames = GetRunningBotNames(services);
        int onlineCount = context.World.OnlinePlayerCount;
        int mapCount = context.World.Maps.Count();

        context.Reply(string.Create(
            c,
            $"Local host: node={localNodeId} role={localRole} state=Active ready=true draining=false players={onlineCount} ({onlineCount - botNames.Count}h/{botNames.Count}b) maps={mapCount}"));

        if (services.GetService<ClusterNodeBridgeFeature>() is { } bridge)
        {
            if (bridge.SnapshotNodes() is { Count: > 0 } bridgeNodes)
            {
                int connected = bridgeNodes.Count(n => n.IsConnected);
                context.Reply(string.Create(c, $"Connected cluster nodes: {connected}/{bridgeNodes.Count}"));
                foreach (ConnectedClusterNodeStatus n in bridgeNodes)
                {
                    context.Reply(string.Create(
                        c,
                        $"node {n.NodeId}: role={n.RoleLabel} endpoint={n.EndPoint} state={(n.IsConnected ? "CONNECTED" : "DISCONNECTED")} maps=[{n.MapsSummary}] epoch={n.WorkerOwnerEpoch} heartbeats={n.RpcHeartbeats}"));
                }
            }
            else if (!bridge.IsEnabled)
            {
                context.Reply(BridgeDisabledLine);
            }
            else
            {
                context.Reply(BridgeNoEndpointsLine);
            }
        }

        ClusterMapPlacementTable? table = ResolvePlacementTable(services, context.World);
        if (table is not null)
        {
            IReadOnlyList<ClusterWorkerStatus> workers = table.SnapshotWorkers();
            context.Reply(string.Create(c, $"Registered placement workers: {workers.Count}"));
            foreach (ClusterWorkerStatus w in workers)
            {
                context.Reply(string.Create(
                    c,
                    $"worker {w.WorkerId.Value}: ready={w.IsReady} draining={w.IsDraining} players={w.ActivePlayerCount}/{w.MaxPlayerCapacity} instances={w.ActiveInstanceCount}/{w.MaxInstanceCapacity} inc={w.Incarnation:D}"));
            }

            return true;
        }

        int ekPlayers = 0, kalPlayers = 0, instPlayers = 0;
        foreach (Player p in context.World.OnlinePlayers)
        {
            uint instId = p.Map?.InstanceId ?? 0;
            if (instId > 0 || (p.MapId != 0 && p.MapId != 1)) instPlayers++;
            else if (p.MapId == 0) ekPlayers++;
            else kalPlayers++;
        }

        int instMaps = context.World.Maps.Count(m => m.InstanceId > 0 || (m.MapId != 0 && m.MapId != 1));
        context.Reply(string.Create(c, $"Partition {DefaultWorkerEasternKingdoms}: map=0 [Eastern Kingdoms] state=Active players={ekPlayers}"));
        context.Reply(string.Create(c, $"Partition {DefaultWorkerKalimdor}: map=1 [Kalimdor] state=Active players={kalPlayers}"));
        context.Reply(string.Create(c, $"Partition {DefaultWorkerInstances}: maps={instMaps} [Dungeons/Raids/BGs] state=Active players={instPlayers}"));
        return true;
    }

    internal static bool Leases(CommandContext context, string args)
    {
        if (args.Trim().Length != 0)
        {
            return false;
        }

        CultureInfo c = CultureInfo.InvariantCulture;
        IServiceProvider services = context.Session.Services;
        CharacterFencingOptions fencing = GetFencingOptions(services);
        CharacterOwnershipToken? worldToken = context.World.CharacterOwnership;

        context.Reply(string.Create(
            c,
            $"Character fencing: enabled={fencing.Enabled} ownerNode={fencing.OwnerNode} claimWatch={fencing.ClaimWatchIntervalSeconds}s token={FormatToken(worldToken)}"));

        context.Reply(FormatLeasesSummaryLine(services, fencing.OwnerNode, worldToken));

        var queues = new List<string>();
        foreach (PersistenceQueueSnapshot queue in QueueRegistry(services).Snapshot())
        {
            int retainedKeys = queue.RetainedKeys?.Count ?? 0;
            queues.Add(retainedKeys > 0
                ? string.Create(c, $"{queue.Name}={queue.Pending}(retained={retainedKeys})")
                : string.Create(c, $"{queue.Name}={queue.Pending}"));
        }

        context.Reply("Write-behind queues: " + (queues.Count > 0 ? string.Join(", ", queues) : "none registered"));
        return true;
    }

    internal static bool Social(CommandContext context, string args)
    {
        if (args.Trim().Length != 0)
        {
            return false;
        }

        CultureInfo c = CultureInfo.InvariantCulture;
        IServiceProvider services = context.Session.Services;
        HashSet<string> botNames = GetRunningBotNames(services);
        Player[] online = [.. context.World.OnlinePlayers];

        int ek = 0, kal = 0, inst = 0;
        foreach (Player p in online)
        {
            uint instId = p.Map?.InstanceId ?? 0;
            if (instId > 0 || (p.MapId != 0 && p.MapId != 1)) inst++;
            else if (p.MapId == 0) ek++;
            else kal++;
        }

        IRealmPresenceDirectory? directory = services.GetService<IRealmPresenceDirectory>();
        IClusterSocialRouter? router = services.GetService<IClusterSocialRouter>();
        SocialFeature? socialFeature = services.GetService<SocialFeature>();
        CharacterDirectory? charDir = services.GetService<CharacterDirectory>();

        context.Reply(string.Create(
            c,
            $"Realm presence: online={online.Length} ({online.Length - botNames.Count} human, {botNames.Count} bots) | knownCharacters={charDir?.Count ?? 0} | directoryEntries={directory?.Count ?? online.Length}"));
        context.Reply(string.Create(
            c,
            $"Worker presence breakdown: {DefaultWorkerEasternKingdoms}={ek} | {DefaultWorkerKalimdor}={kal} | {DefaultWorkerInstances}={inst}"));
        context.Reply(string.Create(
            c,
            $"Social authority: router={(router is not null ? router.GetType().Name : "local-world")} | pendingSocialWrites={socialFeature?.PendingWrites ?? 0}"));
        return true;
    }

    internal static bool Diag(CommandContext context, string args)
    {
        if (args.Trim().Length != 0)
        {
            return false;
        }

        IServiceProvider services = context.Session.Services;
        CharacterFencingOptions fencing = GetFencingOptions(services);
        CharacterOwnershipToken? worldToken = context.World.CharacterOwnership;
        ClusterNodeManifest? manifest = services.GetService<ClusterNodeManifest>();
        string nodeId = manifest?.NodeId ?? fencing.OwnerNode;
        string role = (manifest?.Role ?? ClusterRole.Combined).ToString();
        Guid incarnation = manifest?.NodeIncarnation ?? worldToken?.Incarnation ?? Guid.NewGuid();
        int realmId = manifest?.RealmId ?? 1;
        string contentRev = manifest?.ContentRevision ?? "w44-unified-1.12.1.5875";

        (int pendingWrites, int retainedFailures) = SumQueueDepths(services);
        if (services.GetService<ITransferDecisionStore>() is not { } transfers)
        {
            ReplyDiag(context, 0, pendingWrites, retainedFailures, fencing, worldToken, nodeId, role, incarnation, realmId, contentRev);
            return true;
        }

        // The decision store's COUNT would run inline on the world thread (the SQLite provider runs its async calls synchronously):
        // count on the pool and send the report from the world thread.
        WorldRuntime world = context.World;
        ILogger? logger = services.GetService<ILoggerFactory>()?.CreateLogger<ClusterCommands>(); // resolved here: the pool continuations below must not touch the session scope
        _ = Task.Run(async () =>
        {
            int pendingTransfers;
            try
            {
                pendingTransfers = await transfers.CountUnresolvedAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("cluster.diag-count", ex);
                logger?.LogWarning(ex, "cluster diag could not read the unresolved transfer count"); // logged here, not from the posted action: a stopped world never runs it
                world.Post(() => context.Reply("Cluster diagnostics unavailable: the pending transfer count could not be read from the transfer decision store."));
                return;
            }

            world.Post(() => ReplyDiag(context, pendingTransfers, pendingWrites, retainedFailures, fencing, worldToken, nodeId, role, incarnation, realmId, contentRev));
        }).ContinueWith(
            t =>
            {
                Exception ex = t.Exception!.GetBaseException();
                if (ex is OperationCanceledException)
                {
                    return;
                }

                FaultLedger.Shared.Record("cluster.diag-post", ex);
                logger?.LogFault("cluster-diag-post", ex, "cluster diag could not post its reply to the world thread");
                try
                {
                    world.Post(() => context.Reply("Cluster diagnostics unavailable: the report could not be delivered."));
                }
                catch (Exception replyEx) when (replyEx is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("cluster.diag-post", replyEx); // the world cannot take the GM reply either (stopped): the fault above stands
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return true;
    }

    /// <summary>World thread: build and send the <c>.cluster diag</c> report once the unresolved transfer count is known.</summary>
    private static void ReplyDiag(
        CommandContext context,
        int pendingTransfers,
        int pendingWrites,
        int retainedFailures,
        CharacterFencingOptions fencing,
        CharacterOwnershipToken? worldToken,
        string nodeId,
        string role,
        Guid incarnation,
        int realmId,
        string contentRev)
    {
        IServiceProvider services = context.Session.Services;
        DiagSchemaVersions schema = DiagSchemaVersions.Current;
        var nodes = new List<ClusterNodeDescriptor>
        {
            new(
                NodeId: nodeId,
                Role: role,
                NodeIncarnation: incarnation == Guid.Empty ? Guid.NewGuid() : incarnation,
                State: ClusterNodeLifecycleState.Active,
                ContractVersion: ClusterNodeManifest.CurrentContractVersion,
                AuthSchemaVersion: schema.Auth,
                CharactersSchemaVersion: schema.Characters,
                WorldSchemaVersion: schema.World,
                ContentRevision: contentRev,
                IsFencedWriter: fencing.Enabled),
        };

        if (ResolvePlacementTable(services, context.World) is { } table)
        {
            foreach (ClusterWorkerStatus w in table.SnapshotWorkers())
            {
                nodes.Add(new ClusterNodeDescriptor(
                    NodeId: w.WorkerId.Value,
                    Role: nameof(ClusterRole.Worker),
                    NodeIncarnation: w.Incarnation == Guid.Empty ? Guid.NewGuid() : w.Incarnation,
                    State: w.IsDraining ? ClusterNodeLifecycleState.Draining : (w.IsReady ? ClusterNodeLifecycleState.Active : ClusterNodeLifecycleState.Starting),
                    ContractVersion: ClusterNodeManifest.CurrentContractVersion,
                    AuthSchemaVersion: schema.Auth,
                    CharactersSchemaVersion: schema.Characters,
                    WorldSchemaVersion: schema.World,
                    ContentRevision: contentRev,
                    IsFencedWriter: true));
            }
        }

        var policy = new ContentRevisionCompatibilityPolicy(
            supportedContractVersion: ClusterNodeManifest.CurrentContractVersion,
            expectedAuthSchemaVersion: schema.Auth,
            currentCharactersSchemaVersion: schema.Characters,
            expectedWorldSchemaVersion: schema.World,
            expectedContentRevision: contentRev,
            minimumCompatibleCharactersSchemaVersion: schema.MinimumLocalCharacters);

        var collector = new ClusterDiagnosticsCollector(policy, new ClusterRollbackCoordinator());
        var correlation = new ClusterCorrelationContext(
            RealmId: realmId,
            NodeId: nodeId,
            NodeIncarnation: incarnation,
            SessionGeneration: 1,
            OwnershipEpoch: worldToken?.Epoch ?? 0,
            OperationId: "gm-cluster-diag");

        ClusterRollbackSnapshot snapshot = BuildRollbackSnapshot(realmId, nodes, pendingTransfers, pendingWrites, retainedFailures, fencing.Enabled, worldToken is not null, schema);

        ClusterDiagnosticsReport report = collector.Collect(
            correlation,
            snapshot,
            new Dictionary<string, string?>
            {
                ["onlinePlayers"] = context.World.OnlinePlayerCount.ToString(CultureInfo.InvariantCulture),
                ["loadedMaps"] = context.World.Maps.Count().ToString(CultureInfo.InvariantCulture),
            });

        context.Reply(report.FormatDeterministicSummary());
    }

    /// <summary>
    /// The rollback-readiness input of <c>.cluster diag</c>: the schema versions are this build's (the versions its databases are
    /// composed at, <see cref="DiagSchemaVersions.Current"/>), not literals that go stale when a schema module is added and not the
    /// version rows of the live databases, which the command cannot read cheaply.
    /// </summary>
    internal static ClusterRollbackSnapshot BuildRollbackSnapshot(
        int realmId,
        IReadOnlyList<ClusterNodeDescriptor> nodes,
        int pendingTransfers,
        int pendingWrites,
        int retainedFailures,
        bool fencingEnabled,
        bool hasOwnershipToken,
        DiagSchemaVersions schema)
        => new(
            RealmId: realmId,
            ClusterNodes: nodes,
            PendingTransfers: pendingTransfers,
            PendingCharacterWrites: pendingWrites,
            RetainedWriteFailures: retainedFailures,
            ActiveUnfencedWriters: fencingEnabled ? 0 : 1,
            ActiveClusterOwnershipLeases: hasOwnershipToken ? 1 : 0,
            AuthSchemaVersion: schema.Auth,
            CharactersSchemaVersion: schema.Characters,
            WorldSchemaVersion: schema.World,
            ExpectedAuthSchemaVersion: schema.Auth,
            MinimumLocalCharactersSchemaVersion: schema.MinimumLocalCharacters,
            MaximumLocalCharactersSchemaVersion: schema.Characters,
            ExpectedWorldSchemaVersion: schema.World);

    /// <summary>This build's schema versions of the auth, characters and world databases (what it requires, not what the databases record).</summary>
    internal readonly record struct DiagSchemaVersions(int Auth, int Characters, int World)
    {
        /// <summary>
        /// Oldest characters schema a local combined-mode takeover accepts: the additive-compatibility floor, a policy value
        /// (the newer steps only add tables and columns), not a measurement. Never above the current version.
        /// </summary>
        private const int LocalCharactersFloor = 59;

        public int MinimumLocalCharacters => Math.Min(LocalCharactersFloor, Characters);

        public static DiagSchemaVersions Current => new(
            ArcaneCore.Data.Auth.AuthDbContext.Schema.CurrentVersion,
            ArcaneCore.Data.Characters.CharacterDbContext.Schema.CurrentVersion,
            ArcaneCore.Data.Content.WorldDbContext.Schema.CurrentVersion);
    }

    private static CharacterFencingOptions GetFencingOptions(IServiceProvider services)
        => services.GetService<IOptions<CharacterFencingOptions>>()?.Value ?? new CharacterFencingOptions();

    private static string FormatToken(CharacterOwnershipToken? token)
        => token is null
            ? "unfenced"
            : string.Create(CultureInfo.InvariantCulture, $"{token.Scope}:epoch={token.Epoch}:inc={token.Incarnation:D}");

    private static string MapName(WorldRuntime world, uint mapId, Map? map)
    {
        if (map?.Template?.Name is { Length: > 0 } loadedName)
        {
            return loadedName;
        }

        if (WorldMaps.Of(world).Registry.Find(mapId) is { Name.Length: > 0 } entry)
        {
            return entry.Name;
        }

        return mapId switch
        {
            0 => "Eastern Kingdoms",
            1 => "Kalimdor",
            _ => "Instance",
        };
    }

    private static ClusterMapPlacementTable? ResolvePlacementTable(IServiceProvider services, WorldRuntime world)
    {
        _ = world;
        if (services.GetService<ClusterMapPlacementTable>() is { } explicitTable)
        {
            return explicitTable;
        }

        if (services.GetService<ClusterNodeBridgeFeature>() is { } bridge
            && bridge.Placement.SnapshotWorkers().Count > 0)
        {
            return bridge.Placement;
        }

        return null;
    }

    private static string ResolveWorkerForMap(IServiceProvider services, WorldRuntime world, uint mapId, uint instanceId)
    {
        if (ResolvePlacementTable(services, world) is { } table)
        {
            if (instanceId > 0 && table.TryGetInstanceBinding(mapId, instanceId, out WorkerNodeId boundWorker))
            {
                return boundWorker.Value;
            }

            if (table.TryGetStaticMapOwner(mapId, out WorkerNodeId staticWorker))
            {
                return staticWorker.Value;
            }
        }

        if (instanceId > 0)
        {
            return DefaultWorkerInstances;
        }

        return mapId switch
        {
            0 => DefaultWorkerEasternKingdoms,
            1 => DefaultWorkerKalimdor,
            _ => DefaultWorkerInstances,
        };
    }

    // The core bot registry (logged in and running), not the playerbot pack's feature: World does not reference the pack (tier 2b).
    private static HashSet<string> GetRunningBotNames(IServiceProvider services)
        => services.GetService<ManagedBotRegistry>()?.RunningNames() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static (int Pending, int Retained) SumQueueDepths(IServiceProvider services)
    {
        int pending = 0;
        int retained = 0;
        foreach (PersistenceQueueSnapshot queue in QueueRegistry(services).Snapshot())
        {
            pending += queue.Pending;
            retained += queue.RetainedKeys?.Count ?? 0;
        }

        return (pending, retained);
    }

    // The singleton registry when the world daemon registered one, else the built-in queues resolved against the same services.
    private static PersistenceProbeRegistry QueueRegistry(IServiceProvider services)
        => services.GetService<PersistenceProbeRegistry>() ?? WorldPersistenceProbes.CreateRegistry(services);

    private static string FormatLeasesSummaryLine(IServiceProvider services, string fallbackNode, CharacterOwnershipToken? worldToken)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IRealmAuthorityLeaseStore? leaseStore = services.GetService<IRealmAuthorityLeaseStore>();

        string FormatScope(string scope)
        {
            if (leaseStore?.GetCurrent(scope, now) is { } lease)
            {
                return string.Create(c, $"{lease.OwnerNodeId}(token={lease.FencingToken})");
            }

            return worldToken is not null
                ? string.Create(c, $"{fallbackNode}(epoch={worldToken.Epoch})")
                : string.Create(c, $"{fallbackNode}(combined)");
        }

        return string.Create(
            c,
            $"Authority leases: world={(worldToken is not null ? $"{fallbackNode}(epoch={worldToken.Epoch})" : $"{fallbackNode}(unfenced)")} | economy={FormatScope(RealmAuthorityScopes.Economy)} | social={FormatScope(RealmAuthorityScopes.Social)} | instances={FormatScope(RealmAuthorityScopes.Instances)}");
    }
}

/// <summary>
/// Extends <c>.arcane</c> with <c>.arcane cluster</c> so operators using ArcaneCore's <c>.arcane</c> namespace can
/// run all <c>.cluster</c> inspection sub-commands there as well.
/// </summary>
public sealed class ClusterArcaneCommands : ICommandExtension
{
    public string Path => "arcane";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand(
            "cluster",
            AccountSecurity.GameMaster,
            "Syntax: .arcane cluster [status|node|routes|nodes|leases|social|diag]\nInspect cluster node identity, map/instance worker routing, authority leases, social presence, and diagnostics.",
            ClusterCommands.DispatchFromArcane),
    ];
}
