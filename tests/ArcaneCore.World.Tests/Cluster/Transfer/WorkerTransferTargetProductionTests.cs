using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Protocol;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Cluster.Transfer;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
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
        var loader = new CharacterHooksShadowLoader(_ => session);
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
        var target = new WorkerTransferTarget(Target, host.World, new CharacterHooksShadowLoader(_ => session), new WorldSessionTransferEntry());
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
