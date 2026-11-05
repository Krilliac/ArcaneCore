using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Cluster.Tooling;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>
/// The battleground feature wired to a <see cref="ClusterBattlegroundLink"/> over one in-process <see cref="BattlegroundHub"/>, as the cluster hosting registers it (row 218):
/// the feature installs the link, a join from a plain worker reaches the holder's real queue and the holder's notice comes back to the worker's player, the holder node's own
/// players skip the hub, and the holder is chosen by the gateway's map rule (a list naming the battleground maps, or owning everything), not by instance-copy
/// ownership. The pumps run on their own threads; tests tick the worlds by hand inside a bounded wait. Unproven until the integration gate (no build in the lane).
/// </summary>
public sealed class BattlegroundFeatureClusterLinkTests
{
    private const uint WsgMap = 489;

    /// <summary>A shared map (MAP_COMMON, so instance 0 is where a player really stands) on the list of worker:instances: the holder owns no continent, so its own player stands here.</summary>
    private const uint DeeprunTramMap = 369;
    private const uint DeeprunTramZone = 2257;
    private const string HolderNode = "worker:instances";
    private const string WorkerNode = "worker:ek";

    /// <summary>The owned-map list of an entry of the node bridge's default endpoints, which is what the launcher and the cluster hosting give each worker.</summary>
    private static string DefaultListOf(string nodeId) =>
        ClusterNodeBridgeFeature.DefaultEndpointsSpec.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Single(e => e.StartsWith(nodeId + "|", StringComparison.Ordinal)).Split('|')[3];

    private static string RealInstancesList => DefaultListOf("worker:instances");

    private sealed class DirectHub(BattlegroundHub hub) : IBattlegroundHubTransport
    {
        public bool IsAuthorityReachable => true;

        public bool SubmitBattlegroundCommands(string workerId, IReadOnlyList<BattlegroundCommand> commands) => hub.Submit(workerId, commands);

        public BattlegroundTakeResult TakeBattlegroundCommands(string nodeId) => hub.Take(nodeId);

        public BattlegroundTakeResult TakeBattlegroundCommands(string nodeId, bool preferred) => hub.Take(nodeId, preferred);

        public bool PostBattlegroundNotices(string nodeId, IReadOnlyList<BattlegroundQueuedNotice> notices) => hub.Post(nodeId, notices);

        public IReadOnlyList<BattlegroundNotice> DrainBattlegroundNotices(string workerId) => hub.Drain(workerId);
    }

    private sealed class NoRouter : IClusterFarTeleportRouter
    {
        public bool TryBeginTransfer(Player player, TeleportDestination destination, TeleportOptions options) => false;
    }

    /// <summary>A world with the teleport and battleground features attached and a link registered in its services, as in cluster mode.</summary>
    private sealed class Node : IDisposable
    {
        private static readonly MapContent Content = new(
            [
                new MapTemplate(0, 0, MapType.Common, 12, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
                new MapTemplate(DeeprunTramMap, 0, MapType.Common, DeeprunTramZone, 0, 0, -1, 0, 0, "Deeprun Tram", ""),
                new MapTemplate(WsgMap, 0, MapType.Battleground, 0, 10, 0, -1, 0, 0, "Battleground", ""),
            ],
            [new AreaTemplate(12, 0, 0, 0, 0, 0, "Elwynn", 0, 0), new AreaTemplate(DeeprunTramZone, DeeprunTramMap, 0, 0, 0, 0, "Deeprun Tram", 0, 0)],
            [], [], []);

        private readonly ServiceProvider _services;

        public Node(string nodeId, string ownedMaps, bool allowInstanceMaps, BattlegroundHub hub)
        {
            Link = new ClusterBattlegroundLink(new DirectHub(hub), nodeId);
            ServiceCollection collection = new();
            collection.AddLogging();
            collection.AddSingleton<ILogger<TeleportFeature>>(NullLogger<TeleportFeature>.Instance);
            collection.AddSingleton<TeleportFeature>();
            collection.AddSingleton(Link);
            _services = collection.BuildServiceProvider();
            World = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0, CommandTimeBudgetMs = 0 },
                new CharacterSaveQueue(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
                NullLogger<WorldRuntime>.Instance);
            Assert.True(MapOwnership.TryParse(ownedMaps, allowInstanceMaps, out MapOwnership ownership, out string? error), error);
            World.Ownership = ownership;
            var teleport = _services.GetRequiredService<TeleportFeature>();
            teleport.Attach(World);
            teleport.Maps.Load(Content);
            teleport.Teleports.ClusterFarRouter = new NoRouter();
            Feature = new BattlegroundFeature(_services, _services.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
            Feature.Attach(World);
            World.RunTick(50);   // Install: registers the templates and starts the link
            if (Feature.Manager.TemplateOf(BattlegroundType.WarsongGulch) is null)
            {
                Feature.Manager.RegisterTemplate(new BattlegroundTemplate
                {
                    Type = BattlegroundType.WarsongGulch,
                    MapId = WsgMap,
                    Name = "Warsong Gulch",
                    MinPlayersPerTeam = 2,
                    MaxPlayersPerTeam = 10,
                    MinLevel = 10,
                    MaxLevel = 60,
                    AllianceStart = new BattlegroundStartLocation(1.5f, 2.5f, 3.5f, 0.5f),
                    HordeStart = new BattlegroundStartLocation(-1.5f, -2.5f, -3.5f, 1.5f),
                });
            }
        }

        public ClusterBattlegroundLink Link { get; }

        public WorldRuntime World { get; }

        public BattlegroundFeature Feature { get; }

        /// <summary>Logs a character in on a map this node owns: the default is Elwynn on Eastern Kingdoms, which a node without map 0 on its list must not use (GetMap throws MapNotOwnedException).</summary>
        public (Player Player, RecordingPlayerSession Session) AddPlayer(uint id, uint mapId = 0, uint zoneId = 12)
        {
            var session = new RecordingPlayerSession();
            var character = new CharacterRecord
            {
                Id = (int)id, AccountId = 1, Name = "Bgnode" + id, Race = 1, Class = 1, Gender = 0, Level = 60, MapId = mapId, ZoneId = zoneId, X = 0, Y = 0, Z = 83.5f,
            };
            var player = new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), session);
            World.AddPlayer(player);
            World.RunTick(50);
            return (player, session);
        }

        public void Join(Player player)
        {
            var member = new BattlegroundMember(player.Guid, Team.Alliance, 60, InWorld: true, Deserter: false);
            Feature.RequestJoin(player, new BattlegroundJoinRequest(member, BattlegroundType.WarsongGulch, 0, AsGroup: false, QueuedAtPortal: false, []));
        }

        public void Dispose()
        {
            Feature.StopAsync().GetAwaiter().GetResult();
            World.Dispose();
            _services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static async Task Eventually(Func<bool> condition, params Node[] ticking)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new Xunit.Sdk.XunitException("the condition did not hold within 15 s");
            }

            foreach (Node node in ticking)
            {
                node.World.RunTick(50);
            }

            await Task.Delay(20);
        }
    }

    private static bool GotStatus(RecordingPlayerSession session) => session.Sent.Any(p => p.Opcode == WorldOpcode.SmsgBattlefieldStatus);

    [Theory]
    [InlineData("", false, true)]              // owns everything
    [InlineData("30,489,529", false, true)]    // a list naming all three battleground maps
    [InlineData("0,1", true, false)]           // a continent worker with instance copies on: not where the gateway sends battleground transfers
    [InlineData("0,1", false, false)]
    [InlineData("489", true, false)]           // one map of three is not enough
    [InlineData("30,489", true, false)]
    public void CanHoldQueues_FollowsTheGatewayMapRule(string owned, bool allowInstanceMaps, bool expected)
    {
        Assert.True(MapOwnership.TryParse(owned, allowInstanceMaps, out MapOwnership ownership, out string? error), error);

        Assert.Equal(expected, BattlegroundFeature.CanHoldQueues(ownership));
    }

    [Fact]
    public void TheShippedTopology_HasExactlyTheInstancesWorkerAsHolder_AndEveryBattlegroundMapOnIt()
    {
        // AllowInstanceMaps is on for all three here, which is stricter than the launcher (start-cluster.ps1 sets it only on the instances world): instance copies must not make a continent worker a holder.
        Assert.True(MapOwnership.TryParse(DefaultListOf("worker:instances"), true, out MapOwnership instances, out string? error), error);
        Assert.True(MapOwnership.TryParse(DefaultListOf("worker:EK"), true, out MapOwnership ek, out error), error);
        Assert.True(MapOwnership.TryParse(DefaultListOf("worker:Kalimdor"), true, out MapOwnership kalimdor, out error), error);

        Assert.True(BattlegroundFeature.CanHoldQueues(instances));
        Assert.False(BattlegroundFeature.CanHoldQueues(ek));
        Assert.False(BattlegroundFeature.CanHoldQueues(kalimdor));
        // The gateway sends instance 0 for a battleground transfer: each of the three maps must be listed, not reached through instance-copy ownership.
        foreach (BattlegroundType type in new[] { BattlegroundType.AlteracValley, BattlegroundType.WarsongGulch, BattlegroundType.ArathiBasin })
        {
            Assert.True(instances.ListsMap(BattlegroundManager.MapOfType(type)), type.ToString());
        }
    }

    [Fact]
    public void InstallingTheFeature_StartsTheLink_AndOnlyTheNodeThatListsTheBattlegroundMapsOffersItself()
    {
        var hub = new BattlegroundHub();
        using var holder = new Node(HolderNode, RealInstancesList, allowInstanceMaps: false, hub);
        using var continent = new Node(WorkerNode, "0,1", allowInstanceMaps: true, hub);

        Assert.True(holder.Link.CanHold);
        Assert.False(continent.Link.CanHold);
    }

    [Fact]
    public async Task APlainWorkersJoin_ReachesTheHoldersQueue_AndTheHoldersNoticeComesBackToThePlayer()
    {
        var hub = new BattlegroundHub();
        using var holder = new Node(HolderNode, RealInstancesList, allowInstanceMaps: false, hub);
        // AllowInstanceMaps on a continent worker (the option's default) must not make it a holder.
        using var worker = new Node(WorkerNode, "0,1", allowInstanceMaps: true, hub);
        (Player player, RecordingPlayerSession session) = worker.AddPlayer(1);
        await Eventually(() => holder.Link.IsHolder, holder, worker);

        worker.Join(player);
        await Eventually(() => holder.Feature.Manager.StateOf(player.Guid).InAnyQueue && GotStatus(session), holder, worker);

        Assert.Equal(HolderNode, hub.Holder);
        Assert.False(worker.Feature.Manager.StateOf(player.Guid).InAnyQueue);   // the worker keeps no queue state
        Assert.False(worker.Feature.HoldsQueueState);
        Assert.True(holder.Feature.HoldsQueueState);
        Assert.Equal(0, worker.Link.CommandsDropped);
    }

    [Fact]
    public async Task TheHolderNodesOwnPlayer_SkipsTheHub_AndIsQueuedWithItsStatus()
    {
        var hub = new BattlegroundHub();
        using var holder = new Node(HolderNode, RealInstancesList, allowInstanceMaps: false, hub);
        Assert.False(holder.World.Owns(0));   // the rig keeps the real list: no continent on the holder, so the player cannot use the default map
        (Player player, RecordingPlayerSession session) = holder.AddPlayer(2, DeeprunTramMap, DeeprunTramZone);
        Assert.Equal(DeeprunTramMap, player.MapId);
        await Eventually(() => holder.Link.IsHolder, holder);

        holder.Join(player);
        await Eventually(() => holder.Feature.Manager.StateOf(player.Guid).InAnyQueue && GotStatus(session), holder);

        Assert.Equal(HolderNode, hub.Holder);
        Assert.Equal(0, holder.Link.NoticesDropped);
        // The join stored the return point where the player stood.
        Assert.True(holder.Feature.EntryPointOf(player.Guid) is { MapId: DeeprunTramMap });
    }

    [Fact]
    public async Task ANodeThatCannotHold_NeverGetsTheHolderRole_AndKeepsNoQueueState()
    {
        var hub = new BattlegroundHub();
        using var worker = new Node(WorkerNode, "0,1", allowInstanceMaps: true, hub);
        await Task.Delay(300);   // several pump passes

        Assert.False(worker.Link.IsHolder);
        Assert.Null(hub.Holder);
        Assert.False(worker.Feature.HoldsQueueState);
    }

    private static uint ListedInstances(RecordingPlayerSession session)
    {
        byte[] payload = session.Sent.Last(p => p.Opcode == WorldOpcode.SmsgBattlefieldList).Payload;
        return BitConverter.ToUInt32(payload, 13);   // u64 battlemaster, u32 map, u8 bracket, then the u32 count
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("30,489,529", true)]
    [InlineData("0,1", false)]
    [InlineData("489,529", false)]
    public void ListsBattlegroundMaps_NeedsAllThreeOnTheList(string owned, bool expected)
    {
        Assert.True(MapOwnership.TryParse(owned, false, out MapOwnership ownership, out string? error), error);

        Assert.Equal(expected, BattlegroundFeature.ListsBattlegroundMaps(ownership));
    }

    [Fact]
    public async Task OnlyTheNodeThatListsTheMaps_IsPreferredForTheHolderRole()
    {
        var hub = new BattlegroundHub();
        using var lists = new Node(HolderNode, RealInstancesList, allowInstanceMaps: false, hub);
        using var everything = new Node("worker:everything", "", allowInstanceMaps: false, hub);

        Assert.True(lists.Link.Preferred);
        Assert.True(everything.Link.CanHold);
        Assert.False(everything.Link.Preferred);
        await Eventually(() => hub.Holder == HolderNode, lists, everything);
        Assert.True(lists.Link.IsHolder);
    }

    [Fact]
    public async Task APlainWorkersBattlemasterList_NamesNoInstance_WhileTheHoldersListsItsRunningMatch()
    {
        var hub = new BattlegroundHub();
        using var holder = new Node(HolderNode, RealInstancesList, allowInstanceMaps: false, hub);
        // Instance copies on: this worker's own manager can build a match, which a plain worker must still not list.
        using var worker = new Node(WorkerNode, "0,1", allowInstanceMaps: true, hub);
        (Player holderPlayer, RecordingPlayerSession holderSession) = holder.AddPlayer(2, DeeprunTramMap, DeeprunTramZone);
        (Player workerPlayer, RecordingPlayerSession workerSession) = worker.AddPlayer(1);
        await Eventually(() => holder.Link.IsHolder, holder, worker);
        int holderBracket = BattlegroundConstants.BracketOfLevel(holderPlayer.Level, 10);
        int workerBracket = BattlegroundConstants.BracketOfLevel(workerPlayer.Level, 10);
        Assert.NotNull(holder.Feature.Manager.CreateNewBattleground(BattlegroundType.WarsongGulch, holderBracket));
        Assert.NotNull(worker.Feature.Manager.CreateNewBattleground(BattlegroundType.WarsongGulch, workerBracket));

        holder.Feature.SendBattlefieldList(holderPlayer, ObjectGuid.Empty, BattlegroundType.WarsongGulch);
        worker.Feature.SendBattlefieldList(workerPlayer, ObjectGuid.Empty, BattlegroundType.WarsongGulch);

        Assert.Equal(1u, ListedInstances(holderSession));
        Assert.Equal(0u, ListedInstances(workerSession));
    }
}
