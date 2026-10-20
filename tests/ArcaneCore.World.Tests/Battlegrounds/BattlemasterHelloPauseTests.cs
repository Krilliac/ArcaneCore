using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Battlegrounds;

/// <summary>
/// CMSG_BATTLEMASTER_HELLO stops a wandering battlemaster like any NPC a player talks to (vmangos HandleBattlemasterHelloOpcode,
/// BattleGroundHandler.cpp:50-52 → Creature::PauseOutOfCombatMovement for NPC_MOVEMENT_PAUSE_TIME, CreatureDefines.h:222), through the
/// socket and the real opcode table.
/// </summary>
public sealed class BattlemasterHelloPauseTests
{
    private const uint Entry = 900710;
    private const uint SpawnGuid = 900720;
    private static readonly ObjectGuid Guid = ObjectGuid.WithEntry(HighGuid.Unit, Entry, SpawnGuid);

    [Fact]
    public async Task BattlemasterHello_PausesAWanderingBattlemasterFor180Seconds()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new Store());
            services.AddSingleton(new FactionTemplateCatalog([new(1, 1, 0, 1, 0, 0), new(900711, 0, 0, 8, 0, 0)]));
        });
        await using WorldTestClient client = await host.EnterWorldAsync("BMPAUSE", "Bmpause");
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Bmpause")!.VisibleObjects.Contains(Guid), "the battlemaster becomes visible");
        await client.CollectAsync(TimeSpan.FromMilliseconds(200));

        int before = await host.OnWorldAsync(() => Wander(host).NextMoveMs);
        Assert.True(before <= 10_000, $"next leg in {before} ms before the hello");

        await client.SendAsync(WorldOpcode.CmsgBattlemasterHello, BitConverter.GetBytes(Guid.Value));
        await host.WaitForWorldAsync(() => Wander(host).NextMoveMs > 170_000, "the wander to pause");
        Assert.False(await host.OnWorldAsync(() => Battlemaster(host).IsMoving));
        Assert.InRange(await host.OnWorldAsync(() => Wander(host).NextMoveMs), 170_000, Creature.NpcMovementPauseMs);
    }

    private static Creature Battlemaster(WorldTestHost host) => (Creature)host.World.FindOnlinePlayer("Bmpause")!.Map!.FindObject(Guid)!;

    private static RandomMovementGenerator Wander(WorldTestHost host) => Assert.IsType<RandomMovementGenerator>(Battlemaster(host).Motion.Top);

    private sealed class Store : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CreatureContent(
            [new CreatureTemplate { Entry = Entry, Name = "Wandering battlemaster", Faction = 900711, DisplayIds = [49],
                NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.BattleMaster) }],
            [new CreatureSpawn { Guid = SpawnGuid, Entry = Entry, MapId = 0, X = -8948.95f, Y = -132.49f, Z = 83.53f, MovementType = 1, WanderDistance = 1 }],
            [], [], []));
    }
}
