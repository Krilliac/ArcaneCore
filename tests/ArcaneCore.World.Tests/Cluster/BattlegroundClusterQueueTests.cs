using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>
/// Battleground queues behind the holder (S3e): players queued on two workers are matched by the one manager on the holder, each gets its invitation as a
/// notice at its own worker, and accepting makes the worker teleport the player to the match's map, which a worker that does not own it hands to the far
/// transfer. Everything is driven by hand: links are in-memory recorders, the clock is <c>Update(diff)</c>.
/// </summary>
public sealed class BattlegroundClusterQueueTests
{
    private const string HolderId = "holder";
    private const string WorkerA = "worker-a";
    private const string WorkerB = "worker-b";
    private const uint WsgMap = 489;

    private static readonly ObjectGuid[] Alliance = [.. Enumerable.Range(1, 4).Select(i => ObjectGuid.Player((uint)i))];
    private static readonly ObjectGuid[] Horde = [.. Enumerable.Range(11, 4).Select(i => ObjectGuid.Player((uint)i))];

    /// <summary>The holder node's own host: its players are local, so nothing is sent for a remote one.</summary>
    private sealed class LocalHost : IBattlegroundManagerHost
    {
        public readonly List<ObjectGuid> LocalStatuses = [];
        public readonly List<(ObjectGuid Member, ObjectGuid Leader)> EntryPoints = [];
        private uint _next = 101;

        public bool IsOnline(ObjectGuid player) => false;

        public IBattlegroundHost CreateMatchHost(BattlegroundType type, uint instanceId) => InertBattlegroundHost.Instance;

        public uint AllocateInstanceId() => _next++;

        public void SendStatus(ObjectGuid player, uint queueSlot, BattlegroundStatusSubject subject, BattlegroundStatus status, uint time1, uint time2) => LocalStatuses.Add(player);

        public void SendGroupJoined(ObjectGuid player, uint result)
        {
        }

        public void SendJoinError(ObjectGuid player, BattlegroundJoinError error)
        {
        }

        public void GroupQueueLimitNotice(ObjectGuid player, uint limit)
        {
        }

        public void StoreEntryPoint(ObjectGuid member, ObjectGuid leader, bool queuedAtPortal) => EntryPoints.Add((member, leader));

        public void PrepareToPortIn(ObjectGuid player)
        {
        }

        public void SendToBattleground(ObjectGuid player, Battleground battleground, Team team)
        {
        }

        public void BattlegroundCreated(Battleground battleground)
        {
        }

        public void BattlegroundDeleted(Battleground battleground)
        {
        }
    }

    /// <summary>The holder-to-worker link: records the notices per worker.</summary>
    private sealed class Workers : IBattlegroundWorkerLink
    {
        public readonly List<(string Worker, BattlegroundNotice Notice)> Sent = [];

        public void Deliver(string workerId, BattlegroundNotice notice) => Sent.Add((workerId, notice));

        public IEnumerable<T> To<T>(string worker) where T : BattlegroundNotice => Sent.Where(s => s.Worker == worker).Select(s => s.Notice).OfType<T>();
    }

    /// <summary>The worker-to-holder link: straight into the holder's inbox.</summary>
    private sealed class ToHolder(BattlegroundClusterQueue holder) : IBattlegroundHolderLink
    {
        public void Submit(string workerId, BattlegroundCommand command) => holder.Submit(workerId, command);
    }

    /// <summary>The far-transfer seam of a worker: records every transfer the teleport service hands over.</summary>
    private sealed class RecordingRouter : IClusterFarTeleportRouter
    {
        public List<(Player Player, TeleportDestination Destination)> Calls { get; } = [];

        public bool TryBeginTransfer(Player player, TeleportDestination destination, TeleportOptions options)
        {
            Calls.Add((player, destination));
            return true;
        }
    }

    /// <summary>
    /// A real world with the teleport and battleground features attached, configured as a plain worker that owns map 0 only, so the battleground map
    /// is another node's: the production path from a holder's notice to the teleport service.
    /// </summary>
    private sealed class WorkerWorld : IDisposable
    {
        private static readonly MapContent Content = new(
            [
                new MapTemplate(0, 0, MapType.Common, 12, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
                new MapTemplate(WsgMap, 0, MapType.Battleground, 0, 10, 0, -1, 0, 0, "Battleground", ""),
            ],
            [new AreaTemplate(12, 0, 0, 0, 0, 0, "Elwynn", 0, 0)],
            [], [], []);

        private readonly ServiceProvider _services;

        public WorkerWorld(string workerId, IBattlegroundHolderLink holder)
        {
            ServiceCollection collection = new();
            collection.AddLogging();
            collection.AddSingleton<ILogger<TeleportFeature>>(NullLogger<TeleportFeature>.Instance);
            collection.AddSingleton<TeleportFeature>();
            _services = collection.BuildServiceProvider();
            World = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0, CommandTimeBudgetMs = 0 },
                new CharacterSaveQueue(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
                NullLogger<WorldRuntime>.Instance);
            Assert.True(MapOwnership.TryParse("0", allowInstanceMaps: false, out MapOwnership ownership, out string? error), error);
            // A worker with AllowInstanceMaps=true would own the match map and never far-transfer a holder-placed match.
            Assert.False(ownership.Owns(WsgMap, 1));
            World.Ownership = ownership;
            var teleport = _services.GetRequiredService<TeleportFeature>();
            teleport.Attach(World);
            teleport.Maps.Load(Content);
            teleport.Teleports.ClusterFarRouter = Router;
            Feature = new BattlegroundFeature(_services, _services.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
            Feature.Attach(World);
            Feature.ConfigureWorker(workerId, holder);
        }

        public WorldRuntime World { get; }

        public BattlegroundFeature Feature { get; }

        public RecordingRouter Router { get; } = new();

        public Player AddPlayer(uint id)
        {
            var character = new CharacterRecord
            {
                Id = (int)id, AccountId = 1, Name = "Bgworker" + id, Race = 1, Class = 1, Gender = 0, Level = 60, MapId = 0, ZoneId = 12, X = 0, Y = 0, Z = 83.5f,
            };
            var player = new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new NullPlayerSession());
            World.AddPlayer(player);
            World.RunTick(50);
            return player;
        }

        public void Dispose()
        {
            Feature.StopAsync().GetAwaiter().GetResult();
            World.Dispose();
            _services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class Rig
    {
        public LocalHost Local { get; } = new();

        public Workers Links { get; } = new();

        public BattlegroundClusterQueue Holder { get; }

        public BattlegroundManager Manager { get; }

        public BattlegroundWorkerClient A { get; }

        public BattlegroundWorkerClient B { get; }

        public Rig()
        {
            Holder = new BattlegroundClusterQueue(Local, (member, _) => Local.EntryPoints.Add((member, member)))
            {
                LocalWorkerId = HolderId,
                Workers = Links,
            };
            Manager = new BattlegroundManager(new BattlegroundOptions(), Holder);
            Manager.RegisterTemplate(new BattlegroundTemplate
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
            Holder.Bind(Manager);
            A = new BattlegroundWorkerClient(WorkerA, new ToHolder(Holder));
            B = new BattlegroundWorkerClient(WorkerB, new ToHolder(Holder));
        }

        public void Join(BattlegroundWorkerClient worker, ObjectGuid guid, Team team)
        {
            var member = new BattlegroundMember(guid, team, 60, InWorld: true, Deserter: false);
            worker.Login(member);
            worker.Join(new BattlegroundJoinRequest(member, BattlegroundType.WarsongGulch, 0, AsGroup: false, QueuedAtPortal: false, []),
                new BattlegroundEntryPoint(0, 10f, 20f, 30f, 0.25f));
        }

        /// <summary>Two Alliance players queue on worker A and two Horde players on worker B.</summary>
        public void QueueTwoWorkers()
        {
            Join(A, Alliance[0], Team.Alliance);
            Join(A, Alliance[1], Team.Alliance);
            Join(B, Horde[0], Team.Horde);
            Join(B, Horde[1], Team.Horde);
            Assert.Equal(8, Holder.Pump());
        }
    }

    [Fact]
    public void PlayersQueuedOnTwoWorkers_AreMatchedByTheHolder_AndEachGetsTheInvite()
    {
        var rig = new Rig();
        rig.QueueTwoWorkers();

        // Joining answered each player at its own worker with a queued status, never at the holder.
        Assert.All(rig.Links.Sent, s => Assert.IsType<BattlegroundStatusNotice>(s.Notice));
        Assert.Empty(rig.Local.LocalStatuses);
        Assert.Equal(4, rig.Holder.RemotePlayerCount);
        rig.Links.Sent.Clear();

        rig.Manager.Update(1000);

        Battleground bg = Assert.Single(rig.Manager.RunningBattlegrounds);
        Assert.Equal(2u, bg.InvitedCount(Team.Alliance));
        Assert.Equal(2u, bg.InvitedCount(Team.Horde));
        Assert.Empty(rig.Local.LocalStatuses);

        foreach ((string worker, ObjectGuid[] players) in new[] { (WorkerA, Alliance[..2]), (WorkerB, Horde[..2]) })
        {
            List<BattlegroundStatusNotice> invites = [.. rig.Links.To<BattlegroundStatusNotice>(worker)];
            Assert.Equal(2, invites.Count);
            Assert.Equal(players.OrderBy(g => g.Value), invites.Select(n => n.Player).OrderBy(g => g.Value));
            Assert.All(invites, n =>
            {
                Assert.Equal(BattlegroundStatus.WaitJoin, n.Status);
                Assert.Equal(new BattlegroundStatusSubject(WsgMap, 5, 1), n.Subject);
            });
        }

        // The return point each worker sent with the join was stored for its players.
        Assert.Equal(4, rig.Local.EntryPoints.Count);
    }

    [Fact]
    public void AcceptingAnInvite_TeleportsThePlayerToTheMatchMap_AsAFarTransferFromItsWorker()
    {
        var rig = new Rig();
        rig.QueueTwoWorkers();
        rig.Manager.Update(1000);
        rig.Links.Sent.Clear();
        using var worker = new WorkerWorld(WorkerA, new ToHolder(rig.Holder));
        Player player = worker.AddPlayer(1);
        Assert.Equal(Alliance[0], player.Guid);

        rig.A.Port(new BattlegroundPortRequest(Alliance[0], WsgMap, Action: 1, Level: 60, Deserter: false));
        rig.B.Port(new BattlegroundPortRequest(Horde[0], WsgMap, Action: 1, Level: 60, Deserter: false));
        Assert.Equal(2, rig.Holder.Pump());

        // The worker is first told to prepare, then to enter; the entering player is served by the holder from then on.
        Assert.Single(rig.Links.To<BattlegroundPrepareToPortNotice>(WorkerA));
        BattlegroundEnterNotice allianceEnter = Assert.Single(rig.Links.To<BattlegroundEnterNotice>(WorkerA));
        BattlegroundEnterNotice hordeEnter = Assert.Single(rig.Links.To<BattlegroundEnterNotice>(WorkerB));
        Assert.Equal(Alliance[0], allianceEnter.Player);
        Assert.Equal(Horde[0], hordeEnter.Player);
        Assert.Equal(2, rig.Holder.RemotePlayerCount);

        Assert.Equal(Assert.Single(rig.Manager.RunningBattlegrounds).InstanceId, allianceEnter.InstanceId);
        Assert.Equal(new TeleportDestination(WsgMap, -1.5f, -2.5f, -3.5f, 1.5f), new TeleportDestination(hordeEnter.MapId, hordeEnter.X, hordeEnter.Y, hordeEnter.Z, hordeEnter.Orientation));

        // The notice reaches the worker's feature, which runs the production path: the teleport service sees a map this node does not own
        // and hands the player to the far-transfer router with the match's map and the team's start location.
        worker.Feature.ReceiveNotice(allianceEnter);
        worker.World.RunTick(50);
        (Player moved, TeleportDestination destination) = Assert.Single(worker.Router.Calls);
        Assert.Same(player, moved);
        Assert.Equal(new TeleportDestination(WsgMap, 1.5f, 2.5f, 3.5f, 0.5f), destination);

        // The holder's manager bound both players to the match, so the arrival on the holder adds them.
        Assert.True(rig.Manager.EnterBattleground(Alliance[0]));
        Assert.True(rig.Manager.EnterBattleground(Horde[0]));
    }

    [Fact]
    public void ARetireAfterATransfer_DoesNotLogTheAcceptedPlayerOutAtTheHolder()
    {
        var rig = new Rig();
        rig.QueueTwoWorkers();
        rig.Manager.Update(1000);
        using var worker = new WorkerWorld(WorkerA, new ToHolder(rig.Holder));
        Player player = worker.AddPlayer(1);
        Assert.Equal(Alliance[0], player.Guid);

        rig.A.Port(new BattlegroundPortRequest(Alliance[0], WsgMap, Action: 1, Level: 60, Deserter: false));
        rig.Holder.Pump();

        // The far transfer to the holder committed: the worker retires the player. That is a handoff, not a logout, so the holder keeps its invitation.
        Assert.True(worker.World.DetachPlayerForTransfer(player));
        Assert.True(worker.World.RetireDetachedPlayer(player));
        rig.Holder.Pump();

        Assert.Equal(3, rig.Holder.RemotePlayerCount);
        Assert.True(rig.Manager.EnterBattleground(Alliance[0]));
    }

    [Fact]
    public void LeavingTheQueueFromAWorker_ClearsTheSlotAtItsWorker()
    {
        var rig = new Rig();
        rig.QueueTwoWorkers();
        rig.Links.Sent.Clear();

        rig.A.Port(new BattlegroundPortRequest(Alliance[0], WsgMap, Action: 0, Level: 60, Deserter: false));
        rig.Holder.Pump();

        BattlegroundStatusNotice left = Assert.Single(rig.Links.To<BattlegroundStatusNotice>(WorkerA));
        Assert.Equal(BattlegroundStatus.None, left.Status);
        Assert.Equal(Alliance[0], left.Player);
        Assert.False(rig.Manager.StateOf(Alliance[0]).InAnyQueue);
    }

    [Fact]
    public void ALogout_DropsTheRemotePlayer_AndItsQueueEntry()
    {
        var rig = new Rig();
        rig.QueueTwoWorkers();

        rig.A.Logout(Alliance[0]);
        rig.Holder.Pump();

        Assert.Equal(3, rig.Holder.RemotePlayerCount);
        Assert.Equal(1, rig.Manager.QueuedTeamCounts(BattlegroundType.WarsongGulch).Alliance);
    }

    [Fact]
    public void AStatusPoll_IsAnsweredAtTheAskingWorker()
    {
        var rig = new Rig();
        rig.QueueTwoWorkers();
        rig.Links.Sent.Clear();

        rig.B.Poll(Horde[0], 60);
        rig.Holder.Pump();

        BattlegroundStatusNotice poll = Assert.Single(rig.Links.To<BattlegroundStatusNotice>(WorkerB));
        Assert.Equal(BattlegroundStatus.WaitQueue, poll.Status);
        Assert.Empty(rig.Links.To<BattlegroundStatusNotice>(WorkerA));
    }

    [Fact]
    public void WithoutWorkers_EveryPlayerIsLocal()
    {
        var local = new LocalHost();
        var holder = new BattlegroundClusterQueue(local, (_, _) => { }) { LocalWorkerId = HolderId };
        var manager = new BattlegroundManager(new BattlegroundOptions(), holder);
        holder.Bind(manager);

        holder.Submit(WorkerA, new BattlegroundLoginCommand(new BattlegroundMember(Alliance[0], Team.Alliance, 60, true, false)));
        holder.Pump();

        Assert.Equal(0, holder.RemotePlayerCount);
    }
}
