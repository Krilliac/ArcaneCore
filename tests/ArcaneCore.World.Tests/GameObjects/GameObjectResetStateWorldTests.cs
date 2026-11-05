using ArcaneCore.Game;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// SMSG_GAMEOBJECT_RESET_STATE over the wire: an object whose lock has SLOW_OPEN in its second slot (the capture banners) tells the players that see it when it
/// respawns (vmangos GameObject::Update, GameObject.cpp:445-453; SendGameObjectReset, GameObject.cpp:2460-2465), read at a real client's socket on the manual clock.
/// Written without a build or test run (lane rule); unproven until the integration gate.
/// </summary>
public sealed class GameObjectResetStateWorldTests
{
    private const uint BannerEntry = 990401;
    private const uint PlainEntry = 990402;
    private const uint BannerSpawn = 990411;
    private const uint PlainSpawn = 990412;
    private const uint BannerLock = 990421;

    private static WorldTestHost Start()
    {
        uint[] bannerData = new uint[GameObjectTemplate.DataCount];
        bannerData[0] = BannerLock;
        var banner = new GameObjectTemplate { Entry = BannerEntry, Type = (uint)GameObjectType.Goober, DisplayId = 1328, Name = "Reset banner", Data = bannerData };
        var plain = new GameObjectTemplate { Entry = PlainEntry, Type = (uint)GameObjectType.Goober, DisplayId = 1329, Name = "Reset plain", Data = new uint[GameObjectTemplate.DataCount] };
        GameObjectSpawn[] spawns =
        [
            new GameObjectSpawn { Guid = BannerSpawn, Entry = BannerEntry, MapId = 0, X = -8948f, Y = -132.5f, Z = 83.5f, SpawnTimeSeconds = 60 },
            new GameObjectSpawn { Guid = PlainSpawn, Entry = PlainEntry, MapId = 0, X = -8947f, Y = -133f, Z = 83.5f, SpawnTimeSeconds = 60 },
        ];
        var slowOpen = new LockEntry(BannerLock, [2, 2, 0, 0, 0, 0, 0, 0], [(uint)LockType.Open, (uint)LockType.SlowOpen, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0]);
        GameObjectTestStore.Current.Value = new GameObjectTestContext(new GameObjectContent([banner, plain], spawns, [slowOpen], [], []), LootContent.Empty);
        try
        {
            return WorldTestHost.Start(clock: TestClock.Manual());
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
        }
    }

    private static ObjectGuid GuidOf(uint entry, uint spawn) => ObjectGuid.WithEntry(HighGuid.GameObject, entry, spawn);

    private static async Task RespawnAsync(WorldTestHost host, ObjectGuid guid)
    {
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Gorstone")!;
            GameObjectMapSystem system = player.Map!.FindUpdater<GameObjectMapSystem>()!;
            Assert.True(system.DespawnForRespawn(system.Find(guid)!));
        });
        await host.AdvanceAsync(20);
        await host.AdvanceAsync(20);
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Gorstone")!;
            GameObjectMapSystem system = player.Map!.FindUpdater<GameObjectMapSystem>()!;
            Assert.False(system.Find(guid)!.IsSpawned);
            system.SetRespawnIn(system.Find(guid)!, 1);
        });
        await host.AdvanceAsync(1100);
    }

    [Fact]
    public async Task ASlowOpenLockedObject_SendsTheResetStateToAClientInRangeWhenItRespawns_AndAPlainOneDoesNot()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("GORST", "Gorstone");
        ObjectGuid banner = GuidOf(BannerEntry, BannerSpawn);
        ObjectGuid plain = GuidOf(PlainEntry, PlainSpawn);
        await host.RunUntilAsync(() => host.World.FindOnlinePlayer("Gorstone")!.VisibleObjects.Contains(banner)
            && host.World.FindOnlinePlayer("Gorstone")!.VisibleObjects.Contains(plain), "both objects become visible", 5000);

        await RespawnAsync(host, plain);
        await client.AssertNoPacketsAsync(WorldOpcode.SmsgGameobjectResetState); // no SLOW_OPEN lock: nothing is sent

        await RespawnAsync(host, banner);
        Assert.Equal(GameObjectPackets.ResetState(banner), await client.ReadUntilAsync(WorldOpcode.SmsgGameobjectResetState));
    }
}
