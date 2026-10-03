using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Tests.Items;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

public sealed class GameObjectInstanceOwnershipTests
{
    private const uint Dungeon = 36;
    private const uint ChestEntry = 990101;
    private const uint ChestSpawn = 77002;
    private const uint SharedChestSpawn = 77003;
    private const uint CreatureEntry = 990102;
    private const uint Cloth = 2589;

    [Fact]
    public async Task PairedInstances_OwnTheirLoot_AndRecreationCannotRerollAnUnsupportedChest()
    {
        await using WorldTestHost host = Start(out GameObjectTestContext context);
        await using WorldTestClient aliceClient = await host.EnterWorldAsync("LOOTMAPA", "Lootmapa");
        await using WorldTestClient bobClient = await host.EnterWorldAsync("LOOTMAPB", "Lootmapb");
        Player alice = await host.PlayerAsync("Lootmapa");
        Player bob = await host.PlayerAsync("Lootmapb");

        // The supported shared-copy path still uses the normal registered handlers.
        ulong sharedChest = ObjectGuid.WithEntry(HighGuid.GameObject, ChestEntry, SharedChestSpawn).Value;
        await aliceClient.SendAsync(WorldOpcode.CmsgGameobjUse, BitConverter.GetBytes(sharedChest));
        await aliceClient.ReadUntilAsync(WorldOpcode.SmsgLootResponse);
        await aliceClient.SendAsync(WorldOpcode.CmsgAutostoreLootItem, [0]);
        await aliceClient.ReadUntilAsync(WorldOpcode.SmsgLootRemoved);
        await aliceClient.SendAsync(WorldOpcode.CmsgLootRelease, BitConverter.GetBytes(sharedChest));
        await aliceClient.ReadUntilAsync(WorldOpcode.SmsgLootReleaseResponse);
        Assert.Equal(2u, await host.PlayerStateAsync("Lootmapa", p => p.Inventory.GetItemCount(Cloth)));

        var pair = await host.OnWorldAsync(() =>
        {
            Map a = host.World.GetMap(Dungeon, 201);
            Map b = host.World.GetMap(Dungeon, 202);
            alice.Map!.RemovePlayer(alice);
            bob.Map!.RemovePlayer(bob);
            alice.Relocate(0, 0, 0, 0, host.World.NowMs);
            bob.Relocate(0, 0, 0, 0, host.World.NowMs);
            a.AddPlayer(alice);
            b.AddPlayer(bob);
            GameObjectMapSystem sa = Assert.IsType<GameObjectMapSystem>(a.FindUpdater<GameObjectMapSystem>());
            GameObjectMapSystem sb = Assert.IsType<GameObjectMapSystem>(b.FindUpdater<GameObjectMapSystem>());
            Assert.NotSame(sa, sb);
            LootService lootA = Assert.IsType<LootService>(sa.Loot);
            LootService lootB = Assert.IsType<LootService>(sb.Loot);
            Assert.NotSame(lootA, lootB);
            Creature ca = AddCorpse(a, alice);
            Assert.Equal(LootResult.Ok, lootA.Open(alice, ca.Guid));
            LootBag before = Assert.IsType<LootBag>(lootA.OpenLootOf(alice));
            Creature cb = AddCorpse(b, bob);
            Assert.Equal(ca.Guid, cb.Guid);
            Assert.Same(before, lootA.OpenLootOf(alice));
            Assert.Same(before, lootA.FindLoot(ca.Guid));
            Assert.Equal(10u, before.Gold);
            Assert.Equal(LootResult.Ok, lootB.Open(bob, cb.Guid));
            Assert.NotSame(before, lootB.OpenLootOf(bob));
            GameObject chest = Assert.Single(sa.GameObjects);
            Assert.Equal(GameObjectUseResult.Unsupported, sa.Use(alice, chest.Guid));
            Assert.Equal(GameObjectUseResult.Unsupported, sa.OpenLock(alice, chest.Guid, LockType.Open));
            Assert.Null(chest.Loot);
            return (A: a, B: b, Source: ca.Guid, OldChest: chest, BobBag: lootB.OpenLootOf(bob));
        });

        await aliceClient.ReadUntilAsync(WorldOpcode.SmsgLootResponse);
        await bobClient.ReadUntilAsync(WorldOpcode.SmsgLootResponse);
        uint aliceBefore = await host.PlayerStateAsync("Lootmapa", p => p.Money);
        uint bobBefore = await host.PlayerStateAsync("Lootmapb", p => p.Money);
        ulong instanceChest = ObjectGuid.WithEntry(HighGuid.GameObject, ChestEntry, ChestSpawn).Value;
        await aliceClient.SendAsync(WorldOpcode.CmsgGameobjUse, BitConverter.GetBytes(instanceChest));
        await aliceClient.SendAsync(WorldOpcode.CmsgLootMoney, []);
        await aliceClient.ReadUntilAsync(WorldOpcode.SmsgLootClearMoney);
        Assert.Equal(aliceBefore + 10, await host.PlayerStateAsync("Lootmapa", p => p.Money));
        Assert.Equal(bobBefore, await host.PlayerStateAsync("Lootmapb", p => p.Money));
        Assert.Equal(10u, await host.OnWorldAsync(() => pair.BobBag!.Gold));
        Assert.Equal(2u, await host.PlayerStateAsync("Lootmapa", p => p.Inventory.GetItemCount(Cloth)));

        await host.OnWorldAsync(() =>
        {
            pair.A.RemovePlayer(alice);
            host.World.GetMap(0).AddPlayer(alice);
            host.World.UnloadMap(pair.A);
        });
        await host.WaitForWorldAsync(() => pair.A.IsUnloaded, "the exact empty instance unloads");
        await host.OnWorldAsync(() =>
        {
            var feature = context.Feature!;
            Assert.Null(feature.FindSystem(pair.A));
            Assert.Throws<InvalidOperationException>(() => feature.GetOrCreateSystem(pair.A));
            Map fresh = host.World.GetMap(Dungeon, 201);
            Assert.NotSame(pair.A, fresh);
            GameObjectMapSystem freshSystem = Assert.IsType<GameObjectMapSystem>(fresh.FindUpdater<GameObjectMapSystem>());
            alice.Map!.RemovePlayer(alice);
            alice.Relocate(0, 0, 0, 0, host.World.NowMs);
            fresh.AddPlayer(alice);
            GameObject chest = Assert.Single(freshSystem.GameObjects);
            Assert.False(freshSystem.Remove(pair.OldChest));
            Assert.Equal(GameObjectUseResult.Unsupported, freshSystem.Use(alice, chest.Guid));
            Assert.Equal(GameObjectUseResult.Unsupported, freshSystem.OpenLock(alice, chest.Guid, LockType.Open));
            Assert.Null(chest.Loot);
            GameObjectMapSystem b = pair.B.FindUpdater<GameObjectMapSystem>()!;
            Assert.Same(b, feature.FindSystem(pair.B));
            LootService lootB = Assert.IsType<LootService>(b.Loot);
            Assert.Same(pair.BobBag, lootB.OpenLootOf(bob));
            Assert.True(lootB.TakeMoney(bob));
            Assert.Equal(bobBefore + 10, bob.Money);
            Assert.Equal(0u, pair.BobBag!.Gold);
            Assert.Equal(2u, alice.Inventory.GetItemCount(Cloth));
        });
    }

    private static Creature AddCorpse(Map map, Player killer)
    {
        var template = new CreatureTemplate { Entry = CreatureEntry, Name = "Loot owner test", MinLevelHealth = 10, MaxLevelHealth = 10 };
        var content = new CreatureContent([template], [], [], [], []);
        var creature = new Creature(42, template, null, content, new Random(1));
        creature.Relocate(2, 0, 0, 0, 0);
        map.AddObject(creature);
        map.Combat.DealDamage(killer, creature, creature.Health, direct: false);
        Assert.Equal(CreatureDeathState.Corpse, creature.DeathState);
        return creature;
    }

    private static WorldTestHost Start(out GameObjectTestContext context)
    {
        var words = new uint[GameObjectTemplate.DataCount];
        words[1] = ChestEntry;
        var template = new GameObjectTemplate { Entry = ChestEntry, Type = (uint)GameObjectType.Chest, Name = "Owned chest", Data = words };
        var content = new GameObjectContent([template],
        [
            new GameObjectSpawn { Guid = ChestSpawn, Entry = ChestEntry, MapId = Dungeon, X = 2, Y = 0, Z = 0 },
            new GameObjectSpawn { Guid = SharedChestSpawn, Entry = ChestEntry, MapId = 0, X = -8948, Y = -132.5f, Z = 83.5f },
        ], [], [], []);
        var loot = new LootContent([(LootTableKind.GameObject, new LootStoreRow(ChestEntry, Cloth, 100, 0, 2, 2))],
            [new CreatureLootInfo(CreatureEntry, 0, 0, 10, 10)]);
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = Cloth, Class = 7, Name = "Linen Cloth", DisplayId = 3776, Stackable = 20 });
        context = new GameObjectTestContext(content, loot);
        GameObjectTestStore.Current.Value = context;
        try
        {
            using (items.Use())
            {
                return WorldTestHost.Start();
            }
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
        }
    }
}
