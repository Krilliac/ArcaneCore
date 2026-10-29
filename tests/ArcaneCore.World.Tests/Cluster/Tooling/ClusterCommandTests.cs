using Microsoft.EntityFrameworkCore;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Tooling;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Cluster.Tooling;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Clustering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using static ArcaneCore.World.Tests.Gm.Audit.TicketTests;

namespace ArcaneCore.World.Tests.Cluster.Tooling;

/// <summary>Tests for the <c>.cluster</c> and <c>.arcane cluster</c> GM/operator inspection commands.</summary>
[Collection("ClusterEndpointsEnv")]
public sealed class ClusterCommandTests
{
    private static WorldTestHost StartWithClusterServices(
        ClusterMapPlacementTable? placement = null,
        IRealmAuthorityLeaseStore? leases = null,
        IRealmPresenceDirectory? presence = null,
        bool fencingEnabled = true,
        Action<IServiceCollection>? more = null)
    {
        placement ??= CreateDefaultPlacement();
        leases ??= CreateDefaultLeases();
        presence ??= new InMemoryRealmPresenceDirectory();
        var router = new InMemoryClusterSocialRouter(presence);
        var fencing = new CharacterFencingOptions
        {
            Enabled = fencingEnabled,
            OwnerNode = "world-node-alpha",
            ClaimWatchIntervalSeconds = 10,
        };

        return WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IOptions<CharacterFencingOptions>>(Microsoft.Extensions.Options.Options.Create(fencing));
            services.AddSingleton(placement);
            services.AddSingleton<IClusterMapRouter>(placement);
            services.AddSingleton(leases);
            services.AddSingleton(presence);
            services.AddSingleton<IClusterSocialRouter>(router);
            more?.Invoke(services);
        });
    }

    private static ClusterMapPlacementTable CreateDefaultPlacement()
    {
        var table = new ClusterMapPlacementTable();
        var ek = new WorkerNodeId("worker:EK");
        var kal = new WorkerNodeId("worker:Kalimdor");
        var inst = new WorkerNodeId("worker:instances");
        table.RegisterWorker(ek, isReady: true, maxPlayerCapacity: 1200, activePlayerCount: 478, maxInstanceCapacity: 10, activeInstanceCount: 1);
        table.RegisterWorker(kal, isReady: true, maxPlayerCapacity: 1200, activePlayerCount: 532, maxInstanceCapacity: 10, activeInstanceCount: 1);
        table.RegisterWorker(inst, isReady: true, maxPlayerCapacity: 500, activePlayerCount: 15, maxInstanceCapacity: 100, activeInstanceCount: 2);
        table.AssignStaticMap(0, ek);
        table.AssignStaticMap(1, kal);
        table.TryBindInstance(36, 1, inst, out _);
        return table;
    }

    private static IRealmAuthorityLeaseStore CreateDefaultLeases()
    {
        var store = new InMemoryRealmAuthorityLeaseStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan ttl = TimeSpan.FromMinutes(10);
        Guid inc = Guid.NewGuid();
        store.TryAcquire(RealmAuthorityScopes.Economy, "coordinator:primary", inc, ttl, now);
        store.TryAcquire(RealmAuthorityScopes.Social, "social:primary", inc, ttl, now);
        store.TryAcquire(RealmAuthorityScopes.Instances, "coordinator:primary", inc, ttl, now);
        return store;
    }

    private static async Task<WorldTestClient> Staff(WorldTestHost host, AccountSecurity security, string account = "STAFF", string name = "Staffer")
    {
        WorldTestClient client = await host.EnterWorldAsync(account, name, security);
        await client.CollectAsync();
        return client;
    }

    /// <summary>The lines of <c>.cluster status</c> (and of a bare <c>.cluster</c>) for one player on map 0 with every lease held.</summary>
    private const int StatusLines = 6;

    private static readonly System.Text.RegularExpressions.Regex ActiveMaps = new(@"^Active maps matching filter: (\d+) loaded,", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The whole answer of a <c>.cluster routes</c> command: every line up to "Active maps matching filter: N loaded" and the N map lines
    /// after it. Its replies are sent while the command runs, and between the route lines and the map lines it resolves the bot service and
    /// counts the online players (the first call in a fresh host JITs that path), so a fixed quiet window after the first line can end in
    /// that gap under load and drop the map lines (as it once did: only the four route lines came back).
    /// </summary>
    private static async Task<string[]> RoutesAsync(WorldTestClient client, string command)
    {
        await client.CollectAsync();
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        var lines = new List<string>();
        int? total = null;
        while (total is null || lines.Count < total)
        {
            string line = (await client.ReadChatAsync()).Text;
            lines.Add(line);
            if (total is null && ActiveMaps.Match(line) is { Success: true } active)
            {
                total = lines.Count + int.Parse(active.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return [.. lines];
    }

    [Fact]
    public async Task ClusterCommands_RequireGameMaster_AndDiagRequiresAdministrator()
    {
        await using WorldTestHost host = StartWithClusterServices();
        await using WorldTestClient mod = await Staff(host, AccountSecurity.Moderator, "MOD", "Moddy");
        await using WorldTestClient gm = await Staff(host, AccountSecurity.GameMaster, "GMA", "Gmaaa");
        await using WorldTestClient admin = await Staff(host, AccountSecurity.Administrator, "ADM", "Admiral");

        foreach (string cmd in new[] { ".cluster", ".cluster status", ".cluster node", ".cluster routes", ".cluster nodes", ".cluster leases", ".cluster social", ".arcane cluster" })
        {
            Assert.Equal("This command is not available to you.", (await LinesAsync(mod, cmd))[0]);
        }

        Assert.Equal("This command is not available to you.", (await LinesAsync(gm, ".cluster diag"))[0]);
        Assert.Equal("This command is not available to you.", (await LinesAsync(gm, ".arcane cluster diag"))[0]);

        string[] gmStatus = await LinesAsync(gm, ".cluster", StatusLines);
        Assert.Contains(gmStatus, l => l.StartsWith("Cluster node: world-node-alpha", StringComparison.Ordinal));

        string[] adminDiag = await LinesAsync(admin, ".cluster diag");
        Assert.Single(adminDiag);
        Assert.StartsWith("cluster-diag realm=1 node=world-node-alpha", adminDiag[0], StringComparison.Ordinal);
        Assert.Contains("fleetCompat=fleet_compatible", adminDiag[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ClusterDiag_RollbackSnapshot_UsesTheSchemaVersionsOfThisBuild()
    {
        ClusterCommands.DiagSchemaVersions schema = ClusterCommands.DiagSchemaVersions.Current;
        Assert.Equal(ArcaneCore.Data.Auth.AuthDbContext.Schema.CurrentVersion, schema.Auth);
        Assert.Equal(ArcaneCore.Data.Characters.CharacterDbContext.Schema.CurrentVersion, schema.Characters);
        Assert.Equal(ArcaneCore.Data.Content.WorldDbContext.Schema.CurrentVersion, schema.World);

        var drained = new ClusterNodeDescriptor("n", nameof(ClusterRole.Combined), Guid.NewGuid(), ClusterNodeLifecycleState.Drained,
            ClusterNodeManifest.CurrentContractVersion, schema.Auth, schema.Characters, schema.World, "rev", IsFencedWriter: true);
        ClusterRollbackSnapshot snapshot = ClusterCommands.BuildRollbackSnapshot(1, [drained], 0, 0, 0, fencingEnabled: true, hasOwnershipToken: false, schema);

        Assert.Equal(schema.Auth, snapshot.AuthSchemaVersion);
        Assert.Equal(schema.Characters, snapshot.CharactersSchemaVersion);
        Assert.Equal(schema.World, snapshot.WorldSchemaVersion);
        Assert.Equal(schema.Characters, snapshot.MaximumLocalCharactersSchemaVersion);
        Assert.True(snapshot.MinimumLocalCharactersSchemaVersion <= snapshot.CharactersSchemaVersion);

        // A drained, fenced, write-free realm at this build's own schema must read as safe to roll back.
        ClusterRollbackVerdict verdict = new ClusterRollbackCoordinator().VerifySafeRollbackToLocal(snapshot);
        Assert.True(verdict.SafeToRollback, string.Join(" | ", verdict.BlockingReasons));
    }

    [Fact]
    public async Task ClusterDiag_ReportsTheAggregateUnresolvedTransferCount_NotAHardCodedZero()
    {
        var store = new InMemoryTransferDecisionStore();
        await using WorldTestHost host = StartWithClusterServices(more: services => services.AddSingleton<ITransferDecisionStore>(store));
        await using WorldTestClient admin = await Staff(host, AccountSecurity.Administrator, "ADMT", "Admtransfer");

        string[] none = await LinesAsync(admin, ".cluster diag");
        Assert.Contains("xferPending=0", none[0], StringComparison.Ordinal);

        // Two characters mid-transfer, a third whose transfer was aborted (resolved).
        var a = new WorkerNodeId("worker:EK");
        var b = new WorkerNodeId("worker:Kalimdor");
        Guid aborted = Guid.NewGuid();
        await store.CreateReservationAsync(Guid.NewGuid(), 101, 1, 1, a, b, 0, 0, 1, 0, new CharacterOwnershipToken("char:101", 1, Guid.NewGuid()));
        await store.CreateReservationAsync(Guid.NewGuid(), 102, 1, 1, a, b, 0, 0, 1, 0, new CharacterOwnershipToken("char:102", 1, Guid.NewGuid()));
        await store.CreateReservationAsync(aborted, 103, 1, 1, a, b, 0, 0, 1, 0, new CharacterOwnershipToken("char:103", 1, Guid.NewGuid()));
        await store.AbortPreCommitAsync(aborted, "test");

        string[] two = await LinesAsync(admin, ".cluster diag");
        Assert.Contains("xferPending=2", two[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClusterDiag_WhenTheStoreCannotCount_RepliesThatTheCountIsUnavailable_InsteadOfFaulting()
    {
        await using WorldTestHost host = StartWithClusterServices(more: services => services.AddSingleton<ITransferDecisionStore>(new UncountableTransferStore()));
        await using WorldTestClient admin = await Staff(host, AccountSecurity.Administrator, "ADMU", "Admuncount");

        string[] lines = await LinesAsync(admin, ".cluster diag");

        Assert.Single(lines);
        Assert.Contains("Cluster diagnostics unavailable", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("xferPending", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClusterDiag_CountsTheUnresolvedTransfersOffTheWorldThread_AndRepliesWithTheCount()
    {
        var store = new ThreadRecordingTransferStore(5);
        await using WorldTestHost host = StartWithClusterServices(more: services => services.AddSingleton<ITransferDecisionStore>(store));
        await using WorldTestClient admin = await Staff(host, AccountSecurity.Administrator, "ADMW", "Admworld");
        int worldThread = await host.OnWorldAsync(() => Environment.CurrentManagedThreadId);

        string[] lines = await LinesAsync(admin, ".cluster diag");

        Assert.Contains("xferPending=5", lines[0], StringComparison.Ordinal);
        Assert.NotEqual(0, store.CountThread);
        Assert.NotEqual(worldThread, store.CountThread);
    }

    /// <summary>A decision store that answers the count and records the thread it was asked on.</summary>
    private sealed class ThreadRecordingTransferStore(int unresolved) : UncountableTransferStore
    {
        private int _countThread;

        public int CountThread => Volatile.Read(ref _countThread);

        public override Task<int> CountUnresolvedAsync(CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _countThread, Environment.CurrentManagedThreadId);
            return Task.FromResult(unresolved);
        }
    }

    /// <summary>A decision store whose aggregate count fails, as a database fault would.</summary>
    private class UncountableTransferStore : ITransferDecisionStore
    {
        public virtual Task<int> CountUnresolvedAsync(CancellationToken cancellationToken = default)
            => Task.FromException<int>(new InvalidOperationException("store offline"));

        public void SeedCharacterOwnership(ulong characterGuid, CharacterOwnershipToken ownershipToken) => throw new NotSupportedException();
        public CharacterOwnershipToken? GetActiveOwnership(ulong characterGuid) => throw new NotSupportedException();
        public Task<TransferDecisionRecord> CreateReservationAsync(Guid transferId, ulong characterGuid, int accountId, long sessionGeneration, WorkerNodeId sourceWorkerId, WorkerNodeId targetWorkerId, uint sourceMapId, uint sourceInstanceId, uint targetMapId, uint targetInstanceId, CharacterOwnershipToken sourceOwnershipToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TransferDecisionRecord> AdvancePreCommitStageAsync(Guid transferId, PlayerTransferStage nextStage, PlayerTransferBundle? capturedBundle = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TransferDecisionRecord> CommitDecisionAsync(Guid transferId, Guid targetIncarnation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TransferDecisionRecord> MarkCompletedAsync(Guid transferId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TransferDecisionRecord> AbortPreCommitAsync(Guid transferId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TransferDecisionRecord?> GetDecisionAsync(Guid transferId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TransferDecisionRecord>> GetUnresolvedForCharacterAsync(ulong characterGuid, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(true, "fleet_compatible", "rollbackReady=false")]
    [InlineData(false, "fleet_incompatible", "rollbackReady=false")]
    public async Task ClusterDiag_MarksTheLocalWriterFencedOnlyWhenFencingIsEnabled(bool fencingEnabled, string fleetCode, string rollback)
    {
        await using WorldTestHost host = StartWithClusterServices(fencingEnabled: fencingEnabled);
        await using WorldTestClient admin = await Staff(host, AccountSecurity.Administrator, "ADMF", "Admfence");

        string[] diag = await LinesAsync(admin, ".cluster diag");

        Assert.Single(diag);
        Assert.Contains("fleetCompat=" + fleetCode, diag[0], StringComparison.Ordinal);
        Assert.Contains(rollback, diag[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClusterStatus_AndNode_ReportCurrentPlayerPlacementAndOwnershipToken()
    {
        await using WorldTestHost host = StartWithClusterServices();
        var worldToken = new CharacterOwnershipToken("world", 7, Guid.Parse("11111111-2222-3333-4444-555555555555"));
        host.World.CharacterOwnership = worldToken;

        await using WorldTestClient gm = await Staff(host, AccountSecurity.GameMaster, "GMA", "Gmaaa");

        string[] statusLines = await LinesAsync(gm, ".cluster status", StatusLines);
        Assert.Equal(6, statusLines.Length);
        Assert.Equal(ClusterCommands.BridgeDisabledLine, statusLines[5]);
        Assert.Contains("Cluster node: world-node-alpha | role=Combined", statusLines[0], StringComparison.Ordinal);
        Assert.Contains("Ownership fence: enabled | worldToken=world:epoch=7:inc=11111111-2222-3333-4444-555555555555", statusLines[1], StringComparison.Ordinal);
        Assert.Contains("You / target: Gmaaa", statusLines[2], StringComparison.Ordinal);
        Assert.Contains("worker=worker:EK", statusLines[2], StringComparison.Ordinal);
        Assert.Contains("Authority leases: world=world-node-alpha(epoch=7) | economy=coordinator:primary(token=1) | social=social:primary(token=1) | instances=coordinator:primary(token=1)", statusLines[4], StringComparison.Ordinal);

        string[] nodeLines = await LinesAsync(gm, ".cluster node Gmaaa", 4);
        Assert.Equal(4, nodeLines.Length);
        Assert.Contains("Host node: world-node-alpha", nodeLines[0], StringComparison.Ordinal);
        Assert.Contains("Character: Gmaaa", nodeLines[1], StringComparison.Ordinal);
        Assert.Contains("Placement: node=world-node-alpha -> worker=worker:EK | map 0", nodeLines[2], StringComparison.Ordinal);
        Assert.Contains("effectiveToken=world:epoch=7:inc=11111111-2222-3333-4444-555555555555", nodeLines[3], StringComparison.Ordinal);

        Assert.Equal("Player not found!", (await LinesAsync(gm, ".cluster node NobodyHere"))[0]);
    }

    [Fact]
    public async Task ClusterStatus_AndNodes_SayEnabledWithNoValidEndpoints_WhenTheBridgeIsOnButNothingParsed()
    {
        using EndpointsEnv env = EndpointsEnv.Set(null);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cluster:NodeBridge:Enabled"] = "true",
            ["Cluster:NodeBridge:Endpoints"] = "garbage",
        }).Build();
        await using WorldTestHost host = StartWithClusterServices(more: services =>
            services.Configure<ClusterNodeBridgeOptions>(configuration.GetSection(ClusterNodeBridgeOptions.SectionName)));
        await using WorldTestClient admin = await Staff(host, AccountSecurity.Administrator, "ADMN", "Admnone");

        string[] statusLines = await LinesAsync(admin, ".cluster status", StatusLines);
        Assert.Equal(ClusterCommands.BridgeNoEndpointsLine, statusLines[5]);
        Assert.Equal("Cluster bridge: enabled, no valid endpoints", ClusterCommands.BridgeNoEndpointsLine);

        string[] nodesLines = await LinesAsync(admin, ".cluster nodes", 6);
        Assert.Contains(ClusterCommands.BridgeNoEndpointsLine, nodesLines);
        Assert.DoesNotContain(ClusterCommands.BridgeDisabledLine, nodesLines);
    }

    [Fact]
    public async Task ClusterRoutes_Nodes_Leases_Social_AndArcaneExtension_ReportRoutingAndTopology()
    {
        await using WorldTestHost host = StartWithClusterServices();
        await using WorldTestClient admin = await Staff(host, AccountSecurity.Administrator, "ADM", "Admiral");

        string[] routeLines = await RoutesAsync(admin, ".cluster routes");
        Assert.Contains("Placement table: 2 static map route(s), 1 bound instance(s).", routeLines);
        Assert.Contains(routeLines, l => l.Contains("route static map 0", StringComparison.Ordinal) && l.Contains("-> worker:EK", StringComparison.Ordinal));
        Assert.Contains(routeLines, l => l.Contains("route static map 1", StringComparison.Ordinal) && l.Contains("-> worker:Kalimdor", StringComparison.Ordinal));
        Assert.Contains(routeLines, l => l.Contains("route instance map 36 inst 1", StringComparison.Ordinal) && l.Contains("-> worker:instances", StringComparison.Ordinal));
        Assert.Contains(routeLines, l => l.StartsWith("map 0 inst 0", StringComparison.Ordinal) && l.Contains("-> worker:EK: 1 player(s) (1h/0b)", StringComparison.Ordinal));

        string[] filteredRoutes = await RoutesAsync(admin, ".cluster routes 0");
        Assert.DoesNotContain(filteredRoutes, l => l.Contains("route static map 1", StringComparison.Ordinal));
        Assert.Contains(filteredRoutes, l => l.Contains("route static map 0", StringComparison.Ordinal));

        string[] nodesLines = await LinesAsync(admin, ".cluster nodes", 6);
        Assert.Contains(ClusterCommands.BridgeDisabledLine, nodesLines);
        Assert.Contains(nodesLines, l => l.StartsWith("Local host: node=world-node-alpha role=Combined state=Active", StringComparison.Ordinal));
        Assert.Contains("Registered placement workers: 3", nodesLines);
        Assert.Contains(nodesLines, l => l.StartsWith("worker worker:EK: ready=True draining=False players=478/1200", StringComparison.Ordinal));
        Assert.Contains(nodesLines, l => l.StartsWith("worker worker:Kalimdor: ready=True draining=False players=532/1200", StringComparison.Ordinal));
        Assert.Contains(nodesLines, l => l.StartsWith("worker worker:instances: ready=True draining=False players=15/500", StringComparison.Ordinal));

        string[] leasesLines = await LinesAsync(admin, ".cluster leases", 3);
        Assert.Equal(3, leasesLines.Length);
        Assert.StartsWith("Character fencing: enabled=True ownerNode=world-node-alpha", leasesLines[0], StringComparison.Ordinal);
        Assert.StartsWith("Authority leases:", leasesLines[1], StringComparison.Ordinal);
        Assert.StartsWith("Write-behind queues:", leasesLines[2], StringComparison.Ordinal);

        string[] socialLines = await LinesAsync(admin, ".cluster social", 3);
        Assert.Equal(3, socialLines.Length);
        Assert.StartsWith("Realm presence: online=1 (1 human, 0 bots)", socialLines[0], StringComparison.Ordinal);
        Assert.Contains("worker:EK=1 | worker:Kalimdor=0 | worker:instances=0", socialLines[1], StringComparison.Ordinal);
        Assert.Contains("router=InMemoryClusterSocialRouter", socialLines[2], StringComparison.Ordinal);

        // Verify .arcane cluster delegates to the same subcommands
        string[] arcaneRoutes = await RoutesAsync(admin, ".arcane cluster routes 0");
        Assert.Equal(filteredRoutes, arcaneRoutes);

        string[] arcaneDiag = await LinesAsync(admin, ".arcane cluster diag");
        Assert.Single(arcaneDiag);
        Assert.StartsWith("cluster-diag realm=1 node=world-node-alpha", arcaneDiag[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leases_ShowsDurableHolder_WhenStoreRegistered()
    {
        using var directory = new ArcaneCore.TestSupport.TempDirectory("arcanecore-leasecmd-", sqlite: true);
        string database = System.IO.Path.Combine(directory.Path, "characters.db");
        var dbOptions = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<ArcaneCore.Data.Characters.CharacterDbContext>().UseSqlite($"Data Source={database}").Options;
        await using (var setup = new ArcaneCore.Data.Characters.CharacterDbContext(dbOptions))
            await ArcaneCore.Data.Schema.SchemaBootstrapper.EnsureAsync(setup, ArcaneCore.Data.Characters.CharacterDbContext.Schema);
        var scopes = new ServiceCollection().AddScoped(_ => new ArcaneCore.Data.Characters.CharacterDbContext(dbOptions)).BuildServiceProvider();
        var durable = new ArcaneCore.Data.Characters.Cluster.EfRealmAuthorityLeaseStore(scopes.GetRequiredService<IServiceScopeFactory>());
        durable.TryAcquire(RealmAuthorityScopes.Economy, "durable-node", Guid.NewGuid(), TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow);

        await using WorldTestHost host = StartWithClusterServices(leases: durable);
        await using WorldTestClient admin = await Staff(host, AccountSecurity.Administrator, "ADM", "Admiral");

        string[] leasesLines = await LinesAsync(admin, ".cluster leases", 3);
        Assert.Contains("economy=durable-node(token=1)", leasesLines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("social=durable-node", leasesLines[1], StringComparison.Ordinal); // the social scope has no holder in the table
    }
}
