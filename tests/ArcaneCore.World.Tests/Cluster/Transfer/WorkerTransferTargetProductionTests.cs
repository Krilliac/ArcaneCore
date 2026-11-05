using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Protocol;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Transfer;

/// <summary>
/// The production shadow loader, session entry and target worker over a full test host (every world feature attached, in-memory stores, the
/// world's own save queue) and a gateway-backed session: the shadow load must write nothing and must not run the battleground hook, and the
/// activation follows the retail far-teleport order (loading screen first, map add on the worldport ack).
/// </summary>
public sealed class WorkerTransferTargetProductionTests
{
    private const uint BattlegroundMapId = 489; // Warsong Gulch
    private static readonly WorkerNodeId Source = new("worker-a");
    private static readonly WorkerNodeId Target = new("worker-b");
    private static readonly CharacterOwnershipToken TargetToken = new("world", 4, Guid.NewGuid());

    private static async Task<(int AccountId, CharacterRecord Character, WorldSession Session, RecordingOutput Output, AsyncServiceScope Scope)> OpenAsync(
        WorldTestHost host, string account, string name, uint mapId, float x)
    {
        await host.AddAccountAsync(account);
        int accountId = (await host.Accounts.FindByUsernameAsync(account))!.Id;
        CharacterRecord record = await host.Characters.CreateAsync(new CharacterRecord
        {
            AccountId = accountId,
            Name = name,
            Race = 1,
            Class = 1,
            Gender = 0,
            Level = 40,
            MapId = mapId,
            ZoneId = mapId == 0 ? 12u : 3277u,
            X = x,
            Y = 2f,
            Z = 3f,
            Orientation = 0f,
            HomeMapId = 0,
            HomeZoneId = 12,
            HomeX = -8949.95f,
            HomeY = -132.493f,
            HomeZ = 83.5312f,
        });
        AsyncServiceScope scope = host.WorldServices.CreateAsyncScope();
        var output = new RecordingOutput();
        WorldSession session = WorldSession.CreateRemote(output, "127.0.0.1:41003", scope.ServiceProvider, host.Opcodes, host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance);
        Assert.Equal(AuthResponseCode.Ok, await session.AuthenticateRemoteAsync(accountId, account, 5875, CancellationToken.None));
        return (accountId, record, session, output, scope);
    }

    private static PlayerTransferBundle NewBundle(Guid id, int accountId, CharacterRecord character, uint targetMapId, TransferPosition at) => new()
    {
        TransferId = id,
        CharacterGuid = ObjectGuid.Player((uint)character.Id).Value,
        CharacterId = character.Id,
        AccountId = accountId,
        SessionGeneration = 1,
        SourceWorkerId = Source,
        TargetWorkerId = Target,
        SourceOwnershipToken = new CharacterOwnershipToken("world", 3, Guid.NewGuid()),
        TargetMapId = targetMapId,
        TargetInstanceId = 0,
        TargetPosition = at,
        Health = 100,
        Powers = [0, 0, 0, 0, 0],
        Level = 40,
    };

    [Fact]
    public async Task ShadowLoad_RunsTheProductionLoader_WritesNothing_AndSkipsTheBattlegroundHook()
    {
        await using var host = WorldTestHost.Start();
        (int accountId, CharacterRecord character, WorldSession session, _, AsyncServiceScope scope) = await OpenAsync(host, "XFERSHADOW", "Shadowone", BattlegroundMapId, 1f);
        await using AsyncServiceScope ownedScope = scope;
        var loader = new CharacterHooksShadowLoader(_ => session, host.WorldServices);
        var bundle = NewBundle(Guid.NewGuid(), accountId, character, 0, new TransferPosition(-8949f, -132f, 83f, 0f));

        Player shadow = await loader.LoadAsync(bundle, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        loader.Release(shadow);

        // Nothing durable and nothing shared: no character save (neither the store nor the world's queue saw one), no hold, not online.
        Assert.Equal(0, host.Characters.SaveCount);
        Assert.False(host.SaveQueue.IsHeld(character.Id));
        Assert.False(host.World.IsOnline(shadow.Guid));
        Assert.Equal(0, host.World.OnlinePlayerCount);
        Assert.Null(shadow.Map);

        // The battleground hook would have moved a character saved on a battleground map to its bind point and deleted its binding.
        Assert.Equal(BattlegroundMapId, character.MapId);
        Assert.Equal(1f, character.X);
    }

    [Fact]
    public async Task TheBattlegroundHook_WouldHaveMovedTheSameCharacter_SoTheSkipAboveIsLoadBearing()
    {
        await using var host = WorldTestHost.Start();
        (_, CharacterRecord character, WorldSession session, _, AsyncServiceScope scope) = await OpenAsync(host, "XFERCONTROL", "Controlone", BattlegroundMapId, 1f);
        await using AsyncServiceScope ownedScope = scope;
        BattlegroundFeature feature = host.WorldServices.GetRequiredService<BattlegroundFeature>();
        var player = new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), session);

        await feature.OnPlayerLoadingAsync(session, character, player).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotEqual(BattlegroundMapId, character.MapId); // moved to the bind point
        Assert.IsAssignableFrom<ISkippedOnTransferShadowLoad>(feature);
    }

    [Fact]
    public async Task Activation_WithTheProductionLoaderAndEntry_ShowsTheLoadingScreenFirst_AndEntersTheMapOnTheWorldportAck()
    {
        await using var host = WorldTestHost.Start();
        (int accountId, CharacterRecord character, WorldSession session, RecordingOutput output, AsyncServiceScope scope) = await OpenAsync(host, "XFERARRIVE", "Arriveone", 0, 100f);
        await using AsyncServiceScope ownedScope = scope;
        TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
        var target = new WorkerTransferTarget(Target, host.World, new CharacterHooksShadowLoader(_ => session, host.WorldServices), new WorldSessionTransferEntry(host.WorldServices));
        Guid id = Guid.NewGuid();
        var at = new TransferPosition(-8949f, -132f, 83f, 0f);
        PlayerTransferBundle bundle = NewBundle(id, accountId, character, 0, at);
        ObjectGuid guid = new(bundle.CharacterGuid);

        Assert.True(await target.TryReserveSlotAsync(id, bundle.CharacterGuid, 0, 0));
        await target.InstallPassiveShadowAsync(id, bundle).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, host.Characters.SaveCount);
        Assert.False(host.World.IsOnline(guid));

        output.Clear(); // only what activation sends
        await target.ActivateTargetPlayerAsync(id, bundle, TargetToken).WaitAsync(TimeSpan.FromSeconds(30));

        // The client is told to load the map, and nothing else yet.
        Assert.Equal(SessionState.InWorld, session.State);
        WorldOpcode[] beforeAck = output.Opcodes.ToArray();
        Assert.Equal(new[] { WorldOpcode.SmsgTransferPending, WorldOpcode.SmsgNewWorld }, beforeAck.Take(2).ToArray());
        Assert.DoesNotContain(WorldOpcode.SmsgUpdateObject, beforeAck);
        Assert.DoesNotContain(WorldOpcode.SmsgInitWorldStates, beforeAck);
        Player player = (await host.World.InvokeAsync(() => host.World.FindOnlinePlayer(guid)))!;
        Assert.Null(player.Map);
        Assert.Equal(TeleportStage.Far, await host.World.InvokeAsync(() => teleports.StageOf(player)));

        Assert.True(await host.World.InvokeAsync(() => teleports.HandleWorldportAck(player)));
        await WorldTestHost.WaitForAsync(() => player.Map is not null && output.Opcodes.Contains(WorldOpcode.SmsgUpdateObject), "the player to enter its map");

        Assert.Equal(0u, player.Map!.MapId);
        Assert.Equal(TargetToken, player.CharacterOwnership);
        Assert.False(host.SaveQueue.IsHeld(character.Id));
    }

    [Fact]
    public async Task ASessionThatClosesDuringTheArrival_StillResumesTheSaves_SoTheLogoutSnapshotIsWritten()
    {
        await using var host = WorldTestHost.Start();
        (int accountId, CharacterRecord character, WorldSession session, _, AsyncServiceScope scope) = await OpenAsync(host, "XFERCLOSED", "Closedone", 0, 100f);
        await using AsyncServiceScope ownedScope = scope;
        TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
        var target = new WorkerTransferTarget(Target, host.World, new CharacterHooksShadowLoader(_ => session, host.WorldServices), new WorldSessionTransferEntry(host.WorldServices));
        Guid id = Guid.NewGuid();
        PlayerTransferBundle bundle = NewBundle(id, accountId, character, 0, new TransferPosition(-8949f, -132f, 83f, 0f));
        ObjectGuid guid = new(bundle.CharacterGuid);
        Assert.True(await target.TryReserveSlotAsync(id, bundle.CharacterGuid, 0, 0));
        await target.InstallPassiveShadowAsync(id, bundle).WaitAsync(TimeSpan.FromSeconds(30));
        await target.ActivateTargetPlayerAsync(id, bundle, TargetToken).WaitAsync(TimeSpan.FromSeconds(30));
        Player player = (await host.World.InvokeAsync(() => host.World.FindOnlinePlayer(guid)))!;
        Assert.Equal(TeleportStage.Far, await host.World.InvokeAsync(() => teleports.StageOf(player)));

        // The character comes back to a worker it left earlier, whose save queue refuses it since that retire; only the arrival lifts that.
        host.SaveQueue.QuarantineCharacter(character.Id);

        // The client is gone before its worldport ack: the session's scope ends at once, the logout runs later on the world thread.
        await scope.DisposeAsync();
        await host.World.InvokeAsync(() =>
        {
            host.World.RemovePlayer(player);
            return true;
        });

        Assert.False(host.SaveQueue.IsQuarantined(character.Id));
        await WorldTestHost.WaitForAsync(() => host.Characters.SaveCount > 0, "the logout snapshot to be written");
    }

    [Fact]
    public async Task AClientThatLeavesJustBeforeTheActivationStep_ArrivesOffline_AndTheRetryIsANoOp()
    {
        await using var host = WorldTestHost.Start();
        (int accountId, CharacterRecord character, WorldSession session, _, AsyncServiceScope scope) = await OpenAsync(host, "XFERLEFT", "Leftone", 0, 100f);
        await using AsyncServiceScope ownedScope = scope;
        bool sessionHere = true;
        var offline = new RecordingOfflineArrival();
        var target = new WorkerTransferTarget(
            Target, host.World, new CharacterHooksShadowLoader(_ => session, host.WorldServices), new WorldSessionTransferEntry(host.WorldServices),
            sessionReady: _ => sessionHere, offlineArrival: offline, claims: host.WorldServices.GetService<CharacterClaimService>())
        {
            SessionOpenGrace = TimeSpan.FromMilliseconds(50),
        };
        Guid id = Guid.NewGuid();
        PlayerTransferBundle bundle = NewBundle(id, accountId, character, 0, new TransferPosition(-8949f, -132f, 83f, 0f));
        Assert.True(await target.TryReserveSlotAsync(id, bundle.CharacterGuid, 0, 0));
        await target.InstallPassiveShadowAsync(id, bundle).WaitAsync(TimeSpan.FromSeconds(30));

        // The session probe still saw the client, and then it closed: its scope ends before the activation reaches the world thread.
        session.Kick();
        await scope.DisposeAsync();
        Exception? first = await Record.ExceptionAsync(() => target.ActivateTargetPlayerAsync(id, bundle, TargetToken));

        // The retry of a committed transfer finds the session gone.
        sessionHere = false;
        Exception? second = await Record.ExceptionAsync(() => target.ActivateTargetPlayerAsync(id, bundle, TargetToken));

        Assert.True(first is null && second is null, $"first: {first?.Message ?? "ok"}; retry: {second?.Message ?? "ok"}");
        Assert.Equal(1, offline.Completions); // the character is written to its target: nobody else will, and a login is routed by the stored map
        Assert.False(host.World.IsOnline(new ObjectGuid(bundle.CharacterGuid)));
    }

    [Fact]
    public async Task AnActivationThatThrowsInItsWorldThreadStep_LeavesTheSlotOpenForTheRetry()
    {
        await using var host = WorldTestHost.Start();
        (int accountId, CharacterRecord character, WorldSession session, _, AsyncServiceScope scope) = await OpenAsync(host, "XFERTHROW", "Throwone", 0, 100f);
        await using AsyncServiceScope ownedScope = scope;
        bool sessionHere = true;
        var offline = new RecordingOfflineArrival();
        var target = new WorkerTransferTarget(
            Target, host.World, new CharacterHooksShadowLoader(_ => session, host.WorldServices), new ThrowingEntry(), sessionReady: _ => sessionHere, offlineArrival: offline)
        {
            SessionOpenGrace = TimeSpan.FromMilliseconds(50),
        };
        Guid id = Guid.NewGuid();
        PlayerTransferBundle bundle = NewBundle(id, accountId, character, 0, new TransferPosition(-8949f, -132f, 83f, 0f));
        Assert.True(await target.TryReserveSlotAsync(id, bundle.CharacterGuid, 0, 0));
        await target.InstallPassiveShadowAsync(id, bundle).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<ArithmeticException>(() => target.ActivateTargetPlayerAsync(id, bundle, TargetToken));

        sessionHere = false;
        Exception? retry = await Record.ExceptionAsync(() => target.ActivateTargetPlayerAsync(id, bundle, TargetToken));
        Assert.True(retry is null, retry?.Message);
        Assert.Equal(1, offline.Completions);
    }

    /// <summary>
    /// The target half: the life of a ghost is restored from the characters database, not the bundle (the bundle's copy is the journal's record): the production loader reads what the source's
    /// save wrote, and the arrival's logged-in notification puts the body back (DeathFeature, vmangos Player::LoadCorpse). So the spirit arrives as a spirit, with its body where it was stored.
    /// The source half and the real save are in <c>GatewayCrossWorkerTeleportTests</c>.
    /// </summary>
    [Fact]
    public async Task AGhostThroughATransfer_ArrivesAsAGhost_WithItsBodyAtTheStoredPlace()
    {
        await using var host = WorldTestHost.Start();
        (int accountId, CharacterRecord character, WorldSession session, RecordingOutput output, AsyncServiceScope scope) = await OpenAsync(host, "XFERGHOST", "Ghostmover", 0, 100f);
        await using AsyncServiceScope ownedScope = scope;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = new CorpseSnapshot(0, -8940f, -130f, 83f, 1f, now - 100, (byte)CorpseType.ResurrectablePve);
        host.Characters.SetLife(character.Id, new CharacterLife(1, [0, 0, 0, 0, 0], 0, now + 300, true, body));
        TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
        var target = new WorkerTransferTarget(Target, host.World, new CharacterHooksShadowLoader(_ => session, host.WorldServices), new WorldSessionTransferEntry(host.WorldServices));
        Guid id = Guid.NewGuid();
        PlayerTransferBundle bundle = NewBundle(id, accountId, character, 0, new TransferPosition(-8949f, -132f, 83f, 0f)) with { Health = 1, IsGhost = true, Corpse = body };
        ObjectGuid guid = new(bundle.CharacterGuid);

        Assert.True(await target.TryReserveSlotAsync(id, bundle.CharacterGuid, 0, 0));
        await target.InstallPassiveShadowAsync(id, bundle).WaitAsync(TimeSpan.FromSeconds(30));
        await target.ActivateTargetPlayerAsync(id, bundle, TargetToken).WaitAsync(TimeSpan.FromSeconds(30));
        Player player = (await host.World.InvokeAsync(() => host.World.FindOnlinePlayer(guid)))!;
        Assert.True(await host.World.InvokeAsync(() => teleports.HandleWorldportAck(player)));
        await WorldTestHost.WaitForAsync(() => player.Map is not null && output.Opcodes.Contains(WorldOpcode.SmsgUpdateObject), "the ghost to enter its map");
        await host.WaitForWorldAsync(() => player.Combat.Corpse is not null, "the body to be put back");

        await host.OnWorldAsync(() =>
        {
            Assert.NotEqual(0u, (uint)(player.Flags & PlayerFlags.Ghost));
            Assert.Equal(DeathState.Dead, player.Combat.DeathState);
            Assert.False(player.IsAlive);
            Assert.Equal(1u, player.Health);
            Corpse corpse = Assert.Single(player.Map!.Combat.Corpses);
            Assert.Equal((body.X, body.Y, body.Z), (corpse.X, corpse.Y, corpse.Z));
            Assert.Equal(player.Guid.Value, corpse.Owner.Value);
        });
    }

    /// <summary>
    /// A ghost entering the dungeon of its body (vmangos Player::TeleportTo, Player.cpp:1953-1966) is revived by the target once it has arrived: the source no longer does it, because a refused
    /// transfer could not undo the revive. It comes back at half health and the body goes. The whole way runs against a real dungeon template and the production allocator: the map is the
    /// managed instance a worker reserves for an instance-0 request (never an unmanaged instance 0 of the dungeon), and the body lies in that instance.
    /// </summary>
    [Fact]
    public async Task AGhostArrivingInTheMapOfItsBody_IsRevivedOnTheTarget_AndItsBodyGoes()
    {
        const uint Dungeon = 36;
        await using var host = WorldTestHost.Start(configureServices: services => services.AddSingleton<ArcaneCore.Kernel.WorldData.IMapDataStore, GridTerrain.InMemoryMapDataStore>());
        (int accountId, CharacterRecord character, WorldSession session, RecordingOutput output, AsyncServiceScope scope) = await OpenAsync(host, "XFERREVIVE", "Ghostrevived", 0, 100f);
        await using AsyncServiceScope ownedScope = scope;
        var instances = host.WorldServices.GetRequiredService<ArcaneCore.World.Instances.InstanceFeature>().Instances;
        Assert.True(instances.IsInstancedMap(Dungeon)); // the registry holds the dungeon template: without it the arrival kicks the session
        TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
        var target = new WorkerTransferTarget(
            Target, host.World, new CharacterHooksShadowLoader(_ => session, host.WorldServices), new WorldSessionTransferEntry(host.WorldServices), instances: new InstanceManagerTransferAllocator(instances));
        Guid id = Guid.NewGuid();

        TransferSlotReservation reservation = await target.ReserveSlotAsync(id, ObjectGuid.Player((uint)character.Id).Value, Dungeon, 0);
        Assert.True(reservation.Reserved);
        Assert.NotEqual(0u, reservation.InstanceId);

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = new CorpseSnapshot(Dungeon, -16f, -383f, 61f, 1f, now - 100, (byte)CorpseType.ResurrectablePve, reservation.InstanceId);
        host.Characters.SetLife(character.Id, new CharacterLife(1, [0, 0, 0, 0, 0], 0, now + 300, true, body));
        PlayerTransferBundle bundle = NewBundle(id, accountId, character, Dungeon, new TransferPosition(-16f, -383f, 61f, 0f))
            with { TargetInstanceId = reservation.InstanceId, SourceMapId = 0, Health = 1, IsGhost = true, Corpse = body };
        ObjectGuid guid = new(bundle.CharacterGuid);

        await target.InstallPassiveShadowAsync(id, bundle).WaitAsync(TimeSpan.FromSeconds(30));
        await target.ActivateTargetPlayerAsync(id, bundle, TargetToken).WaitAsync(TimeSpan.FromSeconds(30));
        Player player = (await host.World.InvokeAsync(() => host.World.FindOnlinePlayer(guid)))!;
        Assert.True(await host.World.InvokeAsync(() => teleports.HandleWorldportAck(player)));
        await host.WaitForWorldAsync(() => player.IsAlive, "the ghost to be revived on arrival");

        await host.OnWorldAsync(() =>
        {
            Assert.Equal(0u, (uint)(player.Flags & PlayerFlags.Ghost));
            Assert.Equal((Dungeon, reservation.InstanceId), (player.Map!.MapId, player.Map.InstanceId));
            Assert.True(player.Health > 1);
            Assert.Null(player.Combat.Corpse);
            Assert.Empty(player.Map.Combat.Corpses);
        });
    }

    [Fact]
    public async Task ReleasingAShadow_AfterItsSessionClosed_StillWorks()
    {
        await using var host = WorldTestHost.Start();
        (int accountId, CharacterRecord character, WorldSession session, _, AsyncServiceScope scope) = await OpenAsync(host, "XFERREL", "Relone", 0, 100f);
        await using AsyncServiceScope ownedScope = scope;
        var loader = new CharacterHooksShadowLoader(_ => session, host.WorldServices);
        Player shadow = await loader.LoadAsync(NewBundle(Guid.NewGuid(), accountId, character, 0, new TransferPosition(-8949f, -132f, 83f, 0f)), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));
        HonorService honor = host.WorldServices.GetRequiredService<HonorFeature>().ActiveService!;
        Assert.NotNull(honor.For(shadow)); // the load tracked it, so the check below can fail

        await scope.DisposeAsync();

        loader.Release(shadow); // the honor service is shared: a shadow it keeps tracking is a leak
        Assert.Null(honor.For(shadow));
    }

    private sealed class RecordingOfflineArrival : ITransferOfflineArrival
    {
        private int _completions;

        public int Completions => Volatile.Read(ref _completions);

        public Task CompleteAsync(PlayerTransferBundle bundle, CharacterOwnershipToken ownershipToken, uint zoneId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _completions);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingEntry : ITransferSessionEntry
    {
        public bool TryAttach(Player player) => throw new ArithmeticException("attach failed");

        public void ResumeSaves(Player player)
        {
        }
    }

    private sealed class RecordingOutput : IRemoteSessionOutput
    {
        private readonly ConcurrentQueue<WorldOpcode> _opcodes = new();

        public IReadOnlyCollection<WorldOpcode> Opcodes => _opcodes;

        public void Clear()
        {
            while (_opcodes.TryDequeue(out _))
            {
            }
        }

        public bool TrySend(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
            _opcodes.Enqueue(opcode);
            return true;
        }
    }
}
