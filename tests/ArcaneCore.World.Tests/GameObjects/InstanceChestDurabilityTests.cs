using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Loot;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Net;
using ArcaneCore.World.Social;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.GridTerrain;
using ArcaneCore.World.Tests.Instances;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// Chests of dungeon instances over real sessions, the real instance system and the real loot
/// settlement runner, with an in-memory loot state store that follows the real store's rules
/// (the real store runs in <c>LootStateStoreTests</c>): a chest opens after its generation
/// committed, a partial take survives the unload and recreation of the instance map, a real
/// reset clears the chest, and a held, lost or unreadable commit never duplicates or loses an award.
/// </summary>
public sealed class InstanceChestDurabilityTests
{
    private const uint Deadmines = 36;
    private const uint ChestEntry = 990201;
    private const uint ChestSpawn = 77102;
    private const uint CreatureEntry = 990202;
    private const uint Cloth = 2589;
    private const uint Silk = 4306;
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    private sealed class Fixture : IAsyncDisposable
    {
        public required WorldTestHost Host { get; init; }
        public required GameObjectTestContext Context { get; init; }
        public required ItemTestContent Items { get; init; }

        public InMemoryLootStateStore Store => Host.WorldServices.GetRequiredService<InMemoryLootStateStore>();

        public InMemoryInstanceStore Instances => Host.WorldServices.GetRequiredService<InMemoryInstanceStore>();

        public LootSettlements Settlements => Context.Feature!.Settlements!;

        public static Fixture Start(InstanceStoreSnapshot? storedInstances = null, IReadOnlyList<LootStateRecord>? storedChests = null)
        {
            var words = new uint[GameObjectTemplate.DataCount];
            words[1] = ChestEntry;
            words[15] = 1; // chest.groupLootRules: only such chests use the group round robin (vmangos Player.cpp:7680-7698)
            var template = new GameObjectTemplate { Entry = ChestEntry, Type = (uint)GameObjectType.Chest, Name = "Durable chest", Data = words };
            // The dungeon entrance puts the player at (-16.4, -383.07, 61.78): the chest is 2.4 yd away.
            var content = new GameObjectContent([template],
                [new GameObjectSpawn { Guid = ChestSpawn, Entry = ChestEntry, MapId = Deadmines, X = -14f, Y = -383.07f, Z = 61.78f, SpawnTimeSeconds = 600 }],
                [], [], []);
            var loot = new LootContent(
                [
                    (LootTableKind.GameObject, new LootStoreRow(ChestEntry, Cloth, 100, 0, 2, 2)),
                    (LootTableKind.GameObject, new LootStoreRow(ChestEntry, Silk, 100, 0, 1, 1)),
                ],
                [new CreatureLootInfo(CreatureEntry, 0, 0, 10, 10)]);
            var items = new ItemTestContent();
            items.Templates.Templates.Add(new ItemTemplate { Entry = Cloth, Class = 7, Name = "Linen Cloth", DisplayId = 3776, Stackable = 20 });
            items.Templates.Templates.Add(new ItemTemplate { Entry = Silk, Class = 7, Name = "Silk Cloth", DisplayId = 3777, Stackable = 20 });
            var context = new GameObjectTestContext(content, loot);
            GameObjectTestStore.Current.Value = context;
            InMemoryInstanceStore.Seed.Value = storedInstances;
            InMemoryLootStateStore.Seed.Value = storedChests;
            try
            {
                using (items.Use())
                {
                    return new Fixture { Host = WorldTestHost.Start(), Context = context, Items = items };
                }
            }
            finally
            {
                GameObjectTestStore.Current.Value = null;
                InMemoryInstanceStore.Seed.Value = null;
                InMemoryLootStateStore.Seed.Value = null;
            }
        }

        /// <summary>Log a player in and let its instance maps unload as soon as they are empty.</summary>
        public async Task<WorldTestClient> EnterWorldAsync(string account, string name)
        {
            WorldTestClient client = await Host.EnterWorldAsync(account, name);
            InstanceFeature instances = await Host.PlayerStateAsync(name, p => Services(p).GetRequiredService<InstanceFeature>());
            instances.Options.UnloadDelayMs = 1;
            return client;
        }

        public async Task<uint> EnterDungeonAsync(WorldTestClient client, string name)
        {
            await EnterThroughTriggerAsync(Host, client, name);
            return await Host.PlayerStateAsync(name, p => p.Map is { MapId: Deadmines } map ? map.InstanceId : throw new InvalidOperationException("not in the dungeon"));
        }

        public async Task LeaveDungeonAsync(WorldTestClient client, string name, uint instance)
        {
            await Host.OnWorldAsync(() => TeleportFeatureOf(Host.World.FindOnlinePlayer(name)!).Teleports
                .TeleportTo(Host.World.FindOnlinePlayer(name)!, 0, -8913.23f, 554.633f, 93.7944f, 0.5f));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(await client.ReadUntilAsync(WorldOpcode.SmsgTransferPending)));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(await client.ReadUntilAsync(WorldOpcode.SmsgNewWorld)));
            await client.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
            await Host.WaitForWorldAsync(() => Host.World.FindOnlinePlayer(name)?.Map is { MapId: 0, InstanceId: 0 }, "the player is out of the dungeon");
            await Host.WaitForWorldAsync(() => Host.World.FindMap(Deadmines, instance) is null, "the empty instance map unloads");
        }

        /// <summary>The object system of the loaded instance map (world thread).</summary>
        public GameObjectMapSystem SystemOf(uint instance) => Context.Feature!.FindSystem(Host.World.FindMap(Deadmines, instance)!)!;

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private sealed record WindowItem(byte Slot, uint ItemId, uint Count);

    private static List<WindowItem> Window(byte[] payload)
    {
        var reader = new PacketReader(payload);
        reader.ReadUInt64(); // source
        reader.ReadByte();   // type
        reader.ReadUInt32(); // gold
        byte count = reader.ReadByte();
        var items = new List<WindowItem>();
        for (int i = 0; i < count; i++)
        {
            byte slot = reader.ReadByte();
            uint item = reader.ReadUInt32();
            uint stack = reader.ReadUInt32();
            reader.Skip(4 + 4 + 4 + 1); // display, random suffix, random property, slot type
            items.Add(new WindowItem(slot, item, stack));
        }

        return items;
    }

    private static ulong ChestGuid() => ObjectGuid.WithEntry(HighGuid.GameObject, ChestEntry, ChestSpawn).Value;

    private static async Task UseChestAsync(WorldTestClient client) => await client.SendAsync(WorldOpcode.CmsgGameobjUse, BitConverter.GetBytes(ChestGuid()));

    private static Task TakeAsync(WorldTestClient client, byte slot) => client.SendAsync(WorldOpcode.CmsgAutostoreLootItem, [slot]);

    private static async Task ReleaseAsync(WorldTestClient client)
    {
        await client.SendAsync(WorldOpcode.CmsgLootRelease, BitConverter.GetBytes(ChestGuid()));
        await client.ReadUntilAsync(WorldOpcode.SmsgLootReleaseResponse);
    }

    private static uint Count(Player player, uint entry) => player.Inventory.GetItemCount(entry);

    [Fact]
    public async Task Use_OpensTheWindowAfterTheGenerationCommitted_EvenWhenTheInstanceRowIsStillBeingWritten()
    {
        await using Fixture f = Fixture.Start();
        await using WorldTestClient alice = await f.EnterWorldAsync("DLOOTA", "Dlootaa");
        f.Instances.SaveDelay = TimeSpan.FromMilliseconds(400); // the queued InstanceSaved lands after the first use
        uint instance = await f.EnterDungeonAsync(alice, "Dlootaa");

        await UseChestAsync(alice);
        List<WindowItem> window = Window(await alice.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        Assert.Equal([new WindowItem(0, Cloth, 2), new WindowItem(1, Silk, 1)], window);

        LootStateRecord stored = f.Store.States[new LootStateKey(instance, ChestSpawn)];
        Assert.Equal(1u, stored.Generation);
        Assert.Equal(ChestEntry, stored.SourceEntry);
        Assert.Equal(stored, f.Settlements.Find(stored.Key));
        Assert.True(f.Instances.Live.ContainsKey(instance)); // the commit waited for the queued instance write
        Assert.Single(f.Store.Commits, c => c.Result == LootCommitResult.Committed);
    }

    [Fact]
    public async Task PartialTake_SurvivesTheUnloadAndRecreationOfTheInstanceMap_WithoutReroll()
    {
        await using Fixture f = Fixture.Start();
        await using WorldTestClient alice = await f.EnterWorldAsync("DLOOTB", "Dlootbb");
        uint instance = await f.EnterDungeonAsync(alice, "Dlootbb");
        int character = (int)await f.Host.PlayerStateAsync("Dlootbb", p => p.Guid.Low);

        await UseChestAsync(alice);
        Window(await alice.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        await TakeAsync(alice, 0);
        Assert.Equal([0], await alice.ReadUntilAsync(WorldOpcode.SmsgLootRemoved));
        Assert.Equal(2u, await f.Host.PlayerStateAsync("Dlootbb", p => Count(p, Cloth)));
        var key = new LootStateKey(instance, ChestSpawn);
        Assert.True(f.Store.States[key].Items.Single(i => i.Slot == 0).IsLooted);
        // The award is durable with the state: the stored inventory holds exactly that stack.
        Assert.Equal(2u, f.Items.Items.Get(character).Where(r => r.Item.Entry == Cloth).Sum(r => r.Item.Count));
        await ReleaseAsync(alice);

        Map oldMap = (await f.Host.PlayerStateAsync("Dlootbb", p => p.Map!));
        await f.LeaveDungeonAsync(alice, "Dlootbb", instance);
        Assert.True(oldMap.IsUnloaded);
        Assert.NotNull(await f.Host.OnWorldAsync(() => Services(f.Host.World.FindOnlinePlayer("Dlootbb")!).GetRequiredService<InstanceFeature>().Instances.FindSave(instance)));

        Assert.Equal(instance, await f.EnterDungeonAsync(alice, "Dlootbb")); // the bound instance, recreated
        Assert.NotSame(oldMap, await f.Host.PlayerStateAsync("Dlootbb", p => p.Map!));
        await UseChestAsync(alice);
        Assert.Equal([new WindowItem(1, Silk, 1)], Window(await alice.ReadUntilAsync(WorldOpcode.SmsgLootResponse)));
        await TakeAsync(alice, 0); // already taken: nothing is awarded again
        Assert.DoesNotContain(await alice.CollectAsync(Quiet), p => p.Opcode == WorldOpcode.SmsgLootRemoved);
        Assert.Equal(2u, await f.Host.PlayerStateAsync("Dlootbb", p => Count(p, Cloth)));
        await TakeAsync(alice, 1);
        Assert.Equal([1], await alice.ReadUntilAsync(WorldOpcode.SmsgLootRemoved));
        await ReleaseAsync(alice);

        Assert.Equal(1u, f.Store.States[key].Generation); // never generated twice
        Assert.True(f.Store.States[key].Consumed);
        Assert.Equal(3, f.Store.Commits.Count(c => c.Result == LootCommitResult.Committed)); // generation and two takes
        await f.Host.WaitForWorldAsync(() => f.SystemOf(instance).Find(new ObjectGuid(ChestGuid())) is { IsSpawned: false }, "the consumed chest despawns");
    }

    [Fact]
    public async Task ConsumedChest_StaysDespawnedAcrossTheRecreationOfItsInstanceMap()
    {
        await using Fixture f = Fixture.Start();
        await using WorldTestClient alice = await f.EnterWorldAsync("DLOOTC", "Dlootcc");
        uint instance = await f.EnterDungeonAsync(alice, "Dlootcc");
        await UseChestAsync(alice);
        Window(await alice.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        await TakeAsync(alice, 0);
        await alice.ReadUntilAsync(WorldOpcode.SmsgLootRemoved);
        await TakeAsync(alice, 1);
        await alice.ReadUntilAsync(WorldOpcode.SmsgLootRemoved);
        await ReleaseAsync(alice);
        var key = new LootStateKey(instance, ChestSpawn);
        Assert.True(f.Store.States[key].Consumed);

        await f.LeaveDungeonAsync(alice, "Dlootcc", instance);
        Assert.Equal(instance, await f.EnterDungeonAsync(alice, "Dlootcc"));
        GameObject chest = await f.Host.OnWorldAsync(() => f.SystemOf(instance).GameObjects.Single());
        Assert.False(await f.Host.OnWorldAsync(() => chest.IsSpawned));
        await UseChestAsync(alice);
        Assert.DoesNotContain(await alice.CollectAsync(Quiet), p => p.Opcode == WorldOpcode.SmsgLootResponse);
        Assert.Equal(1u, f.Store.States[key].Generation);
    }

    [Fact]
    public async Task RealReset_ClearsTheChestState_AndTheFreshInstanceGeneratesAgain()
    {
        await using Fixture f = Fixture.Start();
        await using WorldTestClient alice = await f.EnterWorldAsync("DLOOTD", "Dlootdd");
        uint instance = await f.EnterDungeonAsync(alice, "Dlootdd");
        await UseChestAsync(alice);
        Window(await alice.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        await TakeAsync(alice, 0);
        await alice.ReadUntilAsync(WorldOpcode.SmsgLootRemoved);
        await ReleaseAsync(alice);
        var oldKey = new LootStateKey(instance, ChestSpawn);
        Assert.True(f.Store.States.ContainsKey(oldKey));

        await f.LeaveDungeonAsync(alice, "Dlootdd", instance);
        await alice.SendAsync(WorldOpcode.CmsgResetInstances, []);
        Assert.Equal(Deadmines, BinaryPrimitives.ReadUInt32LittleEndian(await alice.ReadUntilAsync(WorldOpcode.SmsgInstanceReset)));
        InstanceFeature instances = await f.Host.PlayerStateAsync("Dlootdd", p => Services(p).GetRequiredService<InstanceFeature>());
        await instances.FlushAsync();
        Assert.DoesNotContain(f.Store.States.Keys, k => k.InstanceId == instance);
        Assert.Null(await f.Host.OnWorldAsync(() => f.Settlements.Find(oldKey)));

        uint fresh = await f.EnterDungeonAsync(alice, "Dlootdd");
        Assert.NotEqual(instance, fresh);
        await UseChestAsync(alice);
        Assert.Equal([new WindowItem(0, Cloth, 2), new WindowItem(1, Silk, 1)], Window(await alice.ReadUntilAsync(WorldOpcode.SmsgLootResponse)));
        Assert.Equal(1u, f.Store.States[new LootStateKey(fresh, ChestSpawn)].Generation);
    }

    [Fact]
    public async Task HeldCommit_FreezesTheActor_BlocksTheChest_ThenTheTakeCompletesOnce()
    {
        await using Fixture f = Fixture.Start();
        await using WorldTestClient alice = await f.EnterWorldAsync("DLOOTE", "Dlootee");
        uint instance = await f.EnterDungeonAsync(alice, "Dlootee");
        await UseChestAsync(alice);
        Window(await alice.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        var key = new LootStateKey(instance, ChestSpawn);

        f.Store.Hold = InMemoryLootStateStore.NewSignal();
        f.Store.CommitEntered = InMemoryLootStateStore.NewSignal();
        await TakeAsync(alice, 0);
        await f.Store.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await f.Host.OnWorldAsync(() => f.Settlements.IsPending(key)));
        Assert.True(await f.Host.PlayerStateAsync("Dlootee", p => p.IsQuestSettlementPending));
        Assert.Equal(0u, await f.Host.PlayerStateAsync("Dlootee", p => Count(p, Cloth)));

        // Another take, another opening and the same slot all wait for the operation.
        await TakeAsync(alice, 1);
        Assert.Equal(GameObjectUseResult.InUse, await f.Host.OnWorldAsync(() => f.SystemOf(instance)
            .Use(f.Host.World.FindOnlinePlayer("Dlootee")!, new ObjectGuid(ChestGuid()))));
        Assert.DoesNotContain(await alice.CollectAsync(Quiet), p => p.Opcode == WorldOpcode.SmsgLootRemoved);

        f.Store.Hold.SetResult();
        Assert.Equal([0], await alice.ReadUntilAsync(WorldOpcode.SmsgLootRemoved));
        Assert.Equal(2u, await f.Host.PlayerStateAsync("Dlootee", p => Count(p, Cloth)));
        Assert.False(await f.Host.PlayerStateAsync("Dlootee", p => p.IsQuestSettlementPending));
        Assert.False(await f.Host.OnWorldAsync(() => f.Settlements.IsPending(key)));
        Assert.True(f.Store.States[key].Items.Single(i => i.Slot == 0).IsLooted);
        Assert.Equal(0u, await f.Host.PlayerStateAsync("Dlootee", p => Count(p, Silk)));
        await TakeAsync(alice, 1);
        Assert.Equal([1], await alice.ReadUntilAsync(WorldOpcode.SmsgLootRemoved));
    }

    [Fact]
    public async Task LostAcknowledgement_ReconcilesToAfter_WithoutDuplicateOrLoss()
    {
        await using Fixture f = Fixture.Start();
        await using WorldTestClient alice = await f.EnterWorldAsync("DLOOTF", "Dlootff");
        uint instance = await f.EnterDungeonAsync(alice, "Dlootff");
        int character = (int)await f.Host.PlayerStateAsync("Dlootff", p => p.Guid.Low);
        await UseChestAsync(alice);
        Window(await alice.ReadUntilAsync(WorldOpcode.SmsgLootResponse));

        f.Store.ThrowAfterApplying = true;
        await TakeAsync(alice, 0);
        Assert.Equal([0], await alice.ReadUntilAsync(WorldOpcode.SmsgLootRemoved));
        f.Store.ThrowAfterApplying = false;
        Assert.Equal(2u, await f.Host.PlayerStateAsync("Dlootff", p => Count(p, Cloth)));
        Assert.Equal(1, f.Items.Items.Get(character).Count(r => r.Item.Entry == Cloth)); // one stack, stored once
        Assert.True(f.Store.States[new LootStateKey(instance, ChestSpawn)].Items.Single(i => i.Slot == 0).IsLooted);
        Assert.Equal(2, f.Store.Commits.Count(c => c.Result == LootCommitResult.Committed));

        await TakeAsync(alice, 0); // the slot is gone
        Assert.DoesNotContain(await alice.CollectAsync(Quiet), p => p.Opcode == WorldOpcode.SmsgLootRemoved);
        Assert.Equal(2u, await f.Host.PlayerStateAsync("Dlootff", p => Count(p, Cloth)));
    }

    [Fact]
    public async Task RefusedGeneration_ReleasesTheClient_AndTheChestWorksOnceStorageRecovers()
    {
        await using Fixture f = Fixture.Start();
        await using WorldTestClient alice = await f.EnterWorldAsync("DLOOTG", "Dlootgg");
        uint instance = await f.EnterDungeonAsync(alice, "Dlootgg");

        f.Store.ThrowBeforeApplying = true; // the commit fails and the read-back says it did not commit
        await UseChestAsync(alice);
        await alice.ReadUntilAsync(WorldOpcode.SmsgLootReleaseResponse);
        Assert.Empty(f.Store.States);
        Assert.False(await f.Host.OnWorldAsync(() => f.Settlements.IsBlocked(new LootStateKey(instance, ChestSpawn))));

        f.Store.ThrowBeforeApplying = false;
        await UseChestAsync(alice);
        Assert.Equal(2, Window(await alice.ReadUntilAsync(WorldOpcode.SmsgLootResponse)).Count);
        Assert.Equal(1u, f.Store.States[new LootStateKey(instance, ChestSpawn)].Generation);
    }

    [Fact]
    public async Task UnreadableOutcome_KicksTheActor_AndBlocksTheChestUntilRestart()
    {
        await using Fixture f = Fixture.Start();
        await using WorldTestClient alice = await f.EnterWorldAsync("DLOOTH", "Dlootha");
        await using WorldTestClient bob = await f.EnterWorldAsync("DLOOTI", "Dlootib");
        await GroupAsync(f.Host, alice, bob, "Dlootha", "Dlootib");
        uint instance = await f.EnterDungeonAsync(alice, "Dlootha");
        await f.EnterDungeonAsync(bob, "Dlootib");

        // The group's round robin starts at the leader (Group::Create, vmangos Group.cpp:134): alice owns the first chest.
        await UseChestAsync(alice);
        Window(await alice.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        var key = new LootStateKey(instance, ChestSpawn);

        f.Store.ThrowAfterApplying = true;
        f.Store.FailReconciliation = true;
        await TakeAsync(alice, 0);
        Assert.True(await alice.IsClosedByServerAsync());
        await f.Host.WaitForWorldAsync(() => f.Settlements.IsBlocked(key), "the key is blocked");

        // Nothing is rebuilt or opened for the blocked key, and the committed cache is untouched.
        Assert.Equal(GameObjectUseResult.Unsupported, await f.Host.OnWorldAsync(() => f.SystemOf(instance)
            .Use(f.Host.World.FindOnlinePlayer("Dlootib")!, new ObjectGuid(ChestGuid()))));
        Assert.False(await f.Host.OnWorldAsync(() => f.Settlements.Find(key)!.Items.Single(i => i.Slot == 0).IsLooted));
        Assert.DoesNotContain(await bob.CollectAsync(Quiet), p => p.Opcode == WorldOpcode.SmsgLootResponse);
    }

    [Fact]
    public async Task HeldChestTake_DefersTheCorpseGoldSplit_ThenPaysTheOriginalShares()
    {
        await using Fixture f = Fixture.Start();
        await using WorldTestClient alice = await f.EnterWorldAsync("DLOOTJ", "Dlootjj");
        await using WorldTestClient bob = await f.EnterWorldAsync("DLOOTK", "Dlootkk");
        await GroupAsync(f.Host, alice, bob, "Dlootjj", "Dlootkk");
        await f.EnterDungeonAsync(alice, "Dlootjj");
        await f.EnterDungeonAsync(bob, "Dlootkk");
        Player aliceP = await f.Host.PlayerAsync("Dlootjj");

        // Alice kills a corpse worth 10 copper shared with the group. The round robin starts at the
        // leader, so alice owns the corpse (and the money) and bob then owns the chest.
        ulong corpse = await f.Host.OnWorldAsync(() => AddCorpse(aliceP.Map!, aliceP).Guid.Value);
        await alice.SendAsync(WorldOpcode.CmsgLoot, BitConverter.GetBytes(corpse));
        await alice.ReadUntilAsync(WorldOpcode.SmsgLootResponse);

        await UseChestAsync(bob);
        Window(await bob.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        uint aliceMoney = await f.Host.PlayerStateAsync("Dlootjj", p => p.Money);
        uint bobMoney = await f.Host.PlayerStateAsync("Dlootkk", p => p.Money);
        f.Store.Hold = InMemoryLootStateStore.NewSignal();
        f.Store.CommitEntered = InMemoryLootStateStore.NewSignal();
        await TakeAsync(bob, 0);
        await f.Store.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The split is deferred as a whole: nothing is paid, nothing is consumed, nobody is notified.
        await alice.CollectAsync(Quiet);
        await alice.SendAsync(WorldOpcode.CmsgLootMoney, []);
        Assert.DoesNotContain(await alice.CollectAsync(Quiet), p => p.Opcode is WorldOpcode.SmsgLootMoneyNotify or WorldOpcode.SmsgLootClearMoney);
        Assert.Equal(aliceMoney, await f.Host.PlayerStateAsync("Dlootjj", p => p.Money));
        Assert.Equal(bobMoney, await f.Host.PlayerStateAsync("Dlootkk", p => p.Money));
        Assert.Equal(10u, await f.Host.OnWorldAsync(() => f.Context.Feature!.FindSystem(aliceP.Map!)!.Loot!.FindLoot(new ObjectGuid(corpse))!.Gold));

        f.Store.Hold.SetResult();
        await bob.ReadUntilAsync(WorldOpcode.SmsgLootRemoved);
        await alice.CollectAsync(Quiet);
        await alice.SendAsync(WorldOpcode.CmsgLootMoney, []);
        await alice.ReadUntilAsync(WorldOpcode.SmsgLootClearMoney);
        Assert.Equal(aliceMoney + 5, await f.Host.PlayerStateAsync("Dlootjj", p => p.Money));
        Assert.Equal(bobMoney + 5, await f.Host.PlayerStateAsync("Dlootkk", p => p.Money));
        Assert.Equal(2u, await f.Host.PlayerStateAsync("Dlootkk", p => Count(p, Cloth)));
    }

    [Fact]
    public async Task WorldRestart_RestoresTheStoredChestOfABoundInstance_AndIgnoresRowsOfMissingInstances()
    {
        // The world starts over a stored instance (bound to the first character) and its partly looted chest;
        // another stored chest belongs to an instance that no longer exists (the startup purge removes it).
        const uint stored = 150;
        const uint vanished = 151;
        var chest = new LootStateRecord(new LootStateKey(stored, ChestSpawn), ChestEntry, 0, 1, false, 0, [1],
        [
            new LootStateItem(0, Cloth, 2, false, false, true, [], []),
            new LootStateItem(1, Silk, 1, false, false, false, [], []),
        ]);
        LootStateRecord orphan = chest with { Key = new LootStateKey(vanished, ChestSpawn) };
        var snapshot = new InstanceStoreSnapshot(
            [new InstanceRecord(stored, Deadmines, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 7200)],
            [new CharacterInstanceBindRecord(1, stored, false)], [], []);
        await using Fixture f = Fixture.Start(snapshot, [chest, orphan]);
        await using WorldTestClient alice = await f.EnterWorldAsync("DLOOTR", "Dlootrr");
        Assert.Equal(1, await f.Host.PlayerStateAsync("Dlootrr", p => (int)p.Guid.Low));

        Assert.Equal(stored, await f.EnterDungeonAsync(alice, "Dlootrr"));
        Assert.Equal(chest, await f.Host.OnWorldAsync(() => f.Settlements.Find(chest.Key)));
        Assert.Null(await f.Host.OnWorldAsync(() => f.Settlements.Find(orphan.Key)));
        await UseChestAsync(alice);
        Assert.Equal([new WindowItem(1, Silk, 1)], Window(await alice.ReadUntilAsync(WorldOpcode.SmsgLootResponse)));
        await TakeAsync(alice, 0); // already taken before the restart
        Assert.DoesNotContain(await alice.CollectAsync(Quiet), p => p.Opcode == WorldOpcode.SmsgLootRemoved);
        await TakeAsync(alice, 1);
        Assert.Equal([1], await alice.ReadUntilAsync(WorldOpcode.SmsgLootRemoved));
        LootCommitRequest take = Assert.Single(f.Store.Commits).Request;
        Assert.Equal(chest, take.Expected);
        Assert.True(take.Updated.Consumed);
        Assert.Equal(1u, take.Updated.Generation);
    }

    [Fact]
    public async Task ADeletedScope_IsNeverReinstatedInTheCache_ByACommitThatFinishesAfterTheReset()
    {
        await using Fixture f = Fixture.Start();
        await using WorldTestClient alice = await f.EnterWorldAsync("DLOOTL", "Dlootll");
        uint instance = await f.EnterDungeonAsync(alice, "Dlootll");
        var key = new LootStateKey(instance, ChestSpawn);

        // The generation commit is held while the logical instance is deleted (a reset), and the
        // store then still accepts it (a delete that raced the commit on an engine without SSI).
        f.Store.SkipScopeCheck = true;
        f.Store.Hold = InMemoryLootStateStore.NewSignal();
        f.Store.CommitEntered = InMemoryLootStateStore.NewSignal();
        await UseChestAsync(alice);
        await f.Store.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        InstanceFeature instances = await f.Host.PlayerStateAsync("Dlootll", p => Services(p).GetRequiredService<InstanceFeature>());
        await f.Host.OnWorldAsync(() =>
        {
            Assert.True(f.Settlements.IsPending(key));
            instances.Instances.UnbindPlayer(f.Host.World.FindOnlinePlayer("Dlootll")!.Guid, Deadmines);
        });
        await f.LeaveDungeonAsync(alice, "Dlootll", instance); // the empty map unloads: the unbound save is deleted
        await instances.FlushAsync();
        Assert.False(f.Instances.Live.ContainsKey(instance));

        f.Store.Hold.SetResult();
        await f.Host.WaitForWorldAsync(() => !f.Settlements.IsPending(key), "the held operation finishes");
        Assert.Contains(f.Store.Commits, c => c.Result == LootCommitResult.Committed);
        // The committed row is an orphan of the deleted instance (the next startup purges it); the live cache must not know it.
        Assert.Null(await f.Host.OnWorldAsync(() => f.Settlements.Find(key)));
        Assert.False(await f.Host.OnWorldAsync(() => f.Settlements.IsBlocked(key)));
    }

    // --- helpers -----------------------------------------------------------------------------

    private static Creature AddCorpse(Map map, Player killer)
    {
        var template = new CreatureTemplate { Entry = CreatureEntry, Name = "Gold corpse", MinLevelHealth = 10, MaxLevelHealth = 10 };
        var content = new CreatureContent([template], [], [], [], []);
        var system = new CreatureMapSystem(map, content, random: new Random(1));
        map.AddUpdater(system);
        Creature creature = system.SpawnTemporary(template, -17, -383, 61.78f, 0);
        uint health = creature.Health;
        Assert.Equal(health, map.Combat.DealDamage(killer, creature, health, direct: false));
        Assert.Equal(CreatureDeathState.Corpse, creature.DeathState);
        return creature;
    }

    private static IServiceProvider Services(Player player) => ((WorldSession)player.Session).Services;

    private static TeleportFeature TeleportFeatureOf(Player player) => Services(player).GetRequiredService<TeleportFeature>();

    private static byte[] CString(string text) => [.. Encoding.UTF8.GetBytes(text), 0];

    private static async Task GroupAsync(WorldTestHost host, WorldTestClient leader, WorldTestClient member, string leaderName, string memberName)
    {
        await leader.SendAsync(WorldOpcode.CmsgGroupInvite, CString(memberName));
        await member.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        await member.SendAsync(WorldOpcode.CmsgGroupAccept, []);
        await host.WaitForWorldAsync(
            () => host.World.FindOnlinePlayer(leaderName) is { } p && Services(p).GetRequiredService<SocialFeature>().Context.Groups.GetGroup(p.Guid)?.MemberCount == 2,
            "the group forms");
    }

    private static async Task EnterThroughTriggerAsync(WorldTestHost host, WorldTestClient client, string name)
    {
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer(name)!.Level = 20);
        await host.PlaceAsync(name, -8962f, -130f, 84f);
        await client.CollectAsync(Quiet);
        byte[] trigger = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(trigger, InMemoryMapDataStore.DeadminesTrigger);
        await client.SendAsync(WorldOpcode.CmsgAreatrigger, trigger);
        Assert.Equal(Deadmines, BinaryPrimitives.ReadUInt32LittleEndian(await client.ReadUntilAsync(WorldOpcode.SmsgTransferPending)));
        byte[] newWorld = await client.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
        Assert.Equal(Deadmines, BinaryPrimitives.ReadUInt32LittleEndian(newWorld));
        await client.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgInitWorldStates); // the last login packet on the new map
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name)?.Map?.MapId == Deadmines, $"{name} enters the dungeon");
    }
}
