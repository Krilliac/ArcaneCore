using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Loot;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// Chests of dungeon instances keep their consumed and remaining loot with the logical instance
/// save: generation and every take go through an <see cref="ILootStateCoordinator"/> (here a
/// fake that follows the contract of the world runner), contents are rebuilt from the committed
/// record when the map is recreated, and nothing is rebuilt, opened or taken for a key whose
/// operation is still in flight.
/// </summary>
public sealed class DurableChestTests
{
    private const uint Dungeon = 36;
    private const uint Instance = 201;
    private const uint ChestEntry = 990101;
    private const uint ChestSpawn = 77002;
    private const uint ChestLoot = 990101;
    private const long SpawnSeconds = 600;

    private static readonly LootStateKey Key = new(Instance, ChestSpawn);

    private sealed class Rig
    {
        public required WorldRuntime World { get; init; }
        public required FakeLootCoordinator Durable { get; init; }
        public required GameObjectContent Content { get; init; }
        public required LootContent Rows { get; init; }
        public required FakeQuestJournal Quests { get; init; }
        public required FakeGroups Groups { get; init; }
        public Map Map { get; private set; } = null!;
        public GameObjectMapSystem System { get; private set; } = null!;
        public LootService Loot { get; private set; } = null!;

        public GameObject Chest => System.GameObjects.Single(g => g.Spawn is not null);

        public void Attach()
        {
            Map = World.GetMap(Dungeon, Instance);
            Loot = new LootService(Rows, random: new Random(11)) { Items = ItemStore, Quests = Quests, Groups = Groups, Durable = Durable };
            System = new GameObjectMapSystem(Map, Content, Loot, Quests);
            Map.AddUpdater(System);
        }

        /// <summary>The empty instance map is unloaded and recreated with the same id (a fresh system and loot service).</summary>
        public void Recreate()
        {
            Map old = Map;
            Assert.Equal(0, old.PlayerCount);
            World.UnloadMap(old);
            World.RunTick(50);
            Assert.True(old.IsUnloaded);
            Attach();
            Assert.NotSame(old, Map);
        }

        public (Player Player, FakeSession Session) Join(uint guid, float x = 0, float y = 0)
        {
            var session = new FakeSession((int)guid);
            Player player = TestWorld.CreatePlayer(guid, x, y, session, Dungeon);
            player.Inventory.Templates = ItemStore;
            player.Inventory.GuidAllocator = new ItemGuidAllocator();
            player.Inventory.Load([]);
            Map.AddPlayer(player);
            World.RunTick(50);
            session.Clear();
            return (player, session);
        }

        public void Leave(Player player) => Map.RemovePlayer(player);

        /// <summary>Tick until the empty map's chest grid unloaded.</summary>
        public void UnloadGrids()
        {
            for (int i = 0; i < 400 && System.LoadedGridCount > 0; i++)
            {
                World.RunTick(1000);
            }

            Assert.Equal(0, System.LoadedGridCount);
        }
    }

    private static Rig CreateRig(IEnumerable<(LootTableKind, LootStoreRow)>? rows = null, long spawnSeconds = SpawnSeconds)
    {
        var template = GoTemplate(ChestEntry, GameObjectType.Chest, (1, ChestLoot));
        var spawn = new GameObjectSpawn
        {
            Guid = ChestSpawn, Entry = ChestEntry, MapId = Dungeon, X = 3, Y = 0, Z = 83.5f, SpawnTimeSeconds = (int)spawnSeconds,
        };
        WorldRuntime world = TestWorld.CreateRuntime();
        var rig = new Rig
        {
            World = world,
            Durable = new FakeLootCoordinator(world),
            Content = new GameObjectContent([template], [spawn], [], [], []),
            Rows = new LootContent(rows ??
                [
                    (LootTableKind.GameObject, Row(ChestLoot, ItemTestData.ToughJerky, 100, minOrRef: 2, max: 2)),
                    (LootTableKind.GameObject, Row(ChestLoot, Hide, 100)),
                ], []),
            Quests = new FakeQuestJournal(),
            Groups = new FakeGroups(),
        };
        rig.Attach();
        return rig;
    }

    private static ParsedLoot Window(FakeSession session)
        => ParsedLoot.Parse(Assert.Single(Packets(session, WorldOpcode.SmsgLootResponse)).Payload);

    private static ParsedLootItem Slot(ParsedLoot window, uint item) => window.Items.Single(i => i.ItemId == item);

    [Fact]
    public void Use_GeneratesThroughTheCoordinator_AndShowsTheWindowOnlyOnceCommitted()
    {
        Rig rig = CreateRig();
        rig.Durable.Manual = true;
        (Player alice, FakeSession session) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        GameObject chest = rig.Chest;

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(alice, chest.Guid)); // accepted
        Assert.Empty(Packets(session, WorldOpcode.SmsgLootResponse));
        Assert.Equal(GameObjectLootState.Ready, chest.LootState);
        Assert.Null(chest.Loot);
        LootOperation generation = Assert.Single(rig.Durable.Started);
        Assert.Null(generation.Expected);
        Assert.Empty(generation.Awards);
        Assert.Null(generation.Actor);
        Assert.Equal(1u, generation.Updated.Generation);
        Assert.Equal(ChestEntry, generation.Updated.SourceEntry);
        Assert.Equal([1], generation.Updated.Recipients);
        Assert.Equal([(ItemTestData.ToughJerky, 2u), (Hide, 1u)], generation.Updated.Items.Select(i => (i.ItemId, i.Count)));

        // Nobody else can open or rebuild the chest while its generation is in flight.
        Assert.Equal(GameObjectUseResult.InUse, rig.System.Use(bob, chest.Guid));
        Assert.Equal(GameObjectUseResult.InUse, rig.System.OpenLock(bob, chest.Guid, LockType.Open));
        Assert.Single(rig.Durable.Started);

        rig.Durable.Complete();
        ParsedLoot window = Window(session);
        Assert.Equal(generation.Updated.Items.Select(i => (i.Slot, i.ItemId, i.Count)),
            window.Items.Select(i => (i.Slot, i.ItemId, i.Count)));
        Assert.Equal(GameObjectLootState.Activated, chest.LootState);
        Assert.Equal(generation.Updated, rig.Durable.Cache[Key]);
        Assert.Same(chest.Loot, rig.Loot.OpenLootOf(alice));
    }

    [Fact]
    public void OpenLock_IsDurableToo()
    {
        Rig rig = CreateRig();
        (Player alice, FakeSession session) = rig.Join(1);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.OpenLock(alice, rig.Chest.Guid, LockType.Open));
        Assert.Equal(2, Window(session).Items.Count);
        Assert.Equal(1u, Assert.Single(rig.Durable.Started).Updated.Generation);
    }

    [Fact]
    public void RefusedGeneration_ReleasesTheClient_AndDoesNotAdvanceTheGroupLooter()
    {
        Rig rig = CreateRig();
        (Player alice, FakeSession session) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        Group group = rig.Groups.Create(LootMethod.RoundRobin, alice, bob);
        Assert.True(group.LooterGuid.IsEmpty);

        rig.Durable.NextOutcome = LootOutcome.Before;
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(alice, rig.Chest.Guid));
        Assert.Single(Packets(session, WorldOpcode.SmsgLootReleaseResponse));
        Assert.True(group.LooterGuid.IsEmpty);
        Assert.Null(rig.Chest.Loot);
        Assert.Null(rig.Durable.Find(Key));

        rig.Durable.NextOutcome = LootOutcome.After;
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(alice, rig.Chest.Guid));
        Assert.Equal(alice.Guid, group.LooterGuid);
        Assert.Equal(1, rig.Durable.Find(Key)!.LootOwnerCharacterId);
        Assert.Equal([1, 2], rig.Durable.Find(Key)!.Recipients.Order());
    }

    [Fact]
    public void PartialTake_SurvivesMapRecreation_WithTheSameContents_WithoutReroll()
    {
        Rig rig = CreateRig();
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        GameObject chest = rig.Chest;
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(alice, chest.Guid));
        ParsedLoot initial = Window(aliceSession);
        ParsedLootItem jerky = Slot(initial, ItemTestData.ToughJerky);
        ParsedLootItem hide = Slot(initial, Hide);

        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(alice, jerky.Slot));
        Assert.Equal(2u, alice.Inventory.GetItemCount(ItemTestData.ToughJerky));
        Assert.Equal([jerky.Slot], Assert.Single(Packets(aliceSession, WorldOpcode.SmsgLootRemoved)).Payload);
        Assert.Contains((alice.Guid, ItemTestData.ToughJerky, 2u), rig.Quests.Looted);
        LootStateRecord stored = rig.Durable.Find(Key)!;
        Assert.True(stored.Items.Single(i => i.Slot == jerky.Slot).IsLooted);
        Assert.False(stored.Items.Single(i => i.Slot == hide.Slot).IsLooted);
        Assert.False(stored.Consumed);
        rig.Loot.Release(alice, chest.Guid);
        Assert.Equal(GameObjectLootState.Ready, chest.LootState);

        rig.Leave(alice);
        rig.Recreate();
        (Player bob, FakeSession bobSession) = rig.Join(2);
        GameObject fresh = rig.Chest;
        Assert.NotSame(chest, fresh);
        Assert.True(fresh.IsSpawned);
        Assert.Null(fresh.Loot); // rebuilt when opened, from the committed record

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(bob, fresh.Guid));
        ParsedLoot remaining = Window(bobSession);
        ParsedLootItem left = Assert.Single(remaining.Items);
        Assert.Equal((hide.Slot, Hide, 1u, hide.DisplayId), (left.Slot, left.ItemId, left.Count, left.DisplayId));
        Assert.Equal(InventoryResult.AlreadyLooted, rig.Loot.TakeItem(bob, jerky.Slot));
        Assert.Equal(0u, bob.Inventory.GetItemCount(ItemTestData.ToughJerky));
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(bob, hide.Slot));
        Assert.Equal(1u, bob.Inventory.GetItemCount(Hide));
        rig.Loot.Release(bob, fresh.Guid);

        // Never generated twice: one generation and the two takes.
        Assert.Equal([1u, 1u, 1u], rig.Durable.Started.Select(o => o.Updated.Generation));
        Assert.Equal(3, rig.Durable.Started.Count);
        Assert.True(rig.Durable.Find(Key)!.Consumed);
    }

    [Fact]
    public void ConsumedChest_StaysDespawnedAcrossRecreation_UntilItsStoredRespawn_ThenRegenerates()
    {
        Rig rig = CreateRig();
        (Player alice, FakeSession session) = rig.Join(1);
        GameObject chest = rig.Chest;
        rig.System.Use(alice, chest.Guid);
        ParsedLoot window = Window(session);
        foreach (ParsedLootItem item in window.Items)
        {
            Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(alice, item.Slot));
        }

        rig.Loot.Release(alice, chest.Guid);
        rig.World.RunTick(50);
        Assert.False(chest.IsSpawned);
        long respawnAt = rig.Durable.Now + SpawnSeconds;
        LootStateRecord consumed = rig.Durable.Find(Key)!;
        Assert.True(consumed.Consumed);
        Assert.Equal(respawnAt, consumed.RespawnAtUnix);

        rig.Leave(alice);
        rig.Recreate();
        (Player bob, FakeSession bobSession) = rig.Join(2);
        GameObject down = rig.Chest;
        Assert.False(down.IsSpawned);
        Assert.Equal(GameObjectUseResult.NotFound, rig.System.Use(bob, down.Guid));
        for (int i = 0; i < 598; i++)
        {
            rig.World.RunTick(1000);
        }

        Assert.False(down.IsSpawned);
        rig.Durable.Now = respawnAt + 1;
        for (int i = 0; i < 4; i++)
        {
            rig.World.RunTick(1000);
        }

        Assert.True(down.IsSpawned);
        bobSession.Clear();
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(bob, down.Guid));
        Assert.Equal(2, Window(bobSession).Items.Count); // a full new generation
        Assert.Equal(2u, rig.Durable.Find(Key)!.Generation);
        Assert.Equal(consumed, rig.Durable.Started.Last(o => o.Updated.Generation == 1).Updated);
    }

    [Fact]
    public void ConsumedChest_WhoseRespawnPassedWhileTheMapWasDown_SpawnsAndRegenerates()
    {
        Rig rig = CreateRig();
        (Player alice, FakeSession session) = rig.Join(1);
        rig.System.Use(alice, rig.Chest.Guid);
        foreach (ParsedLootItem item in Window(session).Items)
        {
            rig.Loot.TakeItem(alice, item.Slot);
        }

        rig.Loot.Release(alice, rig.Chest.Guid);
        rig.Leave(alice);
        rig.UnloadGrids();
        rig.Recreate();
        rig.Durable.Now += SpawnSeconds + 1;
        (Player bob, FakeSession bobSession) = rig.Join(2);
        Assert.True(rig.Chest.IsSpawned);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(bob, rig.Chest.Guid));
        Assert.Equal(2, Window(bobSession).Items.Count);
        Assert.Equal(2u, rig.Durable.Find(Key)!.Generation);
    }

    [Fact]
    public void PendingTake_WhoseGridReloads_CannotTakeTheSameSlotTwice_AndTheFinalizerBindsToTheRegisteredState()
    {
        Rig rig = CreateRig();
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        GameObject chest = rig.Chest;
        rig.System.Use(alice, chest.Guid);
        ParsedLoot initial = Window(aliceSession);
        ParsedLootItem jerky = Slot(initial, ItemTestData.ToughJerky);

        rig.Durable.Manual = true;
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(alice, jerky.Slot)); // accepted, not yet committed
        Assert.Equal(0u, alice.Inventory.GetItemCount(ItemTestData.ToughJerky));
        Assert.True(alice.IsQuestSettlementPending);
        Assert.False(rig.Durable.Find(Key)!.Items.Single(i => i.Slot == jerky.Slot).IsLooted);

        // The take cannot be repeated while it is in flight, by the actor or by anyone else.
        Assert.Equal(InventoryResult.LootCantLootThatNow, rig.Loot.TakeItem(alice, jerky.Slot));
        Assert.Single(rig.Durable.Started, o => o.Awards.Count > 0);

        // The actor leaves and the chest's grid unloads and reloads before the commit finished.
        rig.Leave(alice);
        rig.UnloadGrids();
        (Player bob, FakeSession bobSession) = rig.Join(2);
        GameObject fresh = rig.Chest;
        Assert.NotSame(chest, fresh);
        Assert.Null(fresh.Loot);
        Assert.Equal(GameObjectUseResult.InUse, rig.System.Use(bob, fresh.Guid)); // not rebuilt from stale contents
        Assert.Empty(Packets(bobSession, WorldOpcode.SmsgLootResponse));

        rig.Durable.Complete();
        Assert.True(rig.Durable.Find(Key)!.Items.Single(i => i.Slot == jerky.Slot).IsLooted);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(bob, fresh.Guid));
        ParsedLootItem leftover = Assert.Single(Window(bobSession).Items);
        Assert.Equal(Hide, leftover.ItemId);
        Assert.Equal(InventoryResult.AlreadyLooted, rig.Loot.TakeItem(bob, jerky.Slot));
        Assert.Equal(0u, bob.Inventory.GetItemCount(ItemTestData.ToughJerky));
        Assert.Single(rig.Durable.Started, o => o.Awards.Count > 0); // exactly one award in total
    }

    [Fact]
    public void PendingTake_OfTheLastSlot_ReleaseDefersTheDespawnToTheFinalizer_AndALateGridReloadStillDespawnsTheChest()
    {
        Rig rig = CreateRig([(LootTableKind.GameObject, Row(ChestLoot, Hide, 100))]);
        (Player alice, FakeSession session) = rig.Join(1);
        GameObject chest = rig.Chest;
        rig.System.Use(alice, chest.Guid);
        ParsedLootItem hide = Assert.Single(Window(session).Items);

        rig.Durable.Manual = true;
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(alice, hide.Slot));
        rig.Loot.Release(alice, chest.Guid);
        Assert.Equal(GameObjectLootState.Activated, chest.LootState); // not decided yet
        rig.World.RunTick(50);
        Assert.True(chest.IsSpawned);

        rig.Durable.Complete();
        Assert.Equal(GameObjectLootState.JustDeactivated, chest.LootState);
        Assert.Equal(1u, alice.Inventory.GetItemCount(Hide));
        rig.World.RunTick(50);
        Assert.False(chest.IsSpawned);

        // The same take, finishing after the object was replaced by a reloaded one: it despawns on completion.
        Rig other = CreateRig([(LootTableKind.GameObject, Row(ChestLoot, Hide, 100))]);
        (Player carol, FakeSession carolSession) = other.Join(3);
        other.System.Use(carol, other.Chest.Guid);
        ParsedLootItem only = Assert.Single(Window(carolSession).Items);
        other.Durable.Manual = true;
        Assert.Equal(InventoryResult.Ok, other.Loot.TakeItem(carol, only.Slot));
        other.Leave(carol);
        other.UnloadGrids();
        (Player dave, _) = other.Join(4);
        GameObject reloaded = other.Chest;
        Assert.True(reloaded.IsSpawned);
        other.Durable.Complete();
        Assert.False(reloaded.IsSpawned);
        Assert.Equal(GameObjectUseResult.NotFound, other.System.Use(dave, reloaded.Guid));
        Assert.True(other.Durable.Find(Key)!.Consumed);
    }

    [Fact]
    public void RefusedTake_LeavesTheInventoryAndTheSlotUntouched_AndTheClientIsTold()
    {
        Rig rig = CreateRig();
        (Player alice, FakeSession session) = rig.Join(1);
        rig.System.Use(alice, rig.Chest.Guid);
        ParsedLootItem jerky = Slot(Window(session), ItemTestData.ToughJerky);

        rig.Durable.NextOutcome = LootOutcome.Before;
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(alice, jerky.Slot));
        Assert.False(alice.IsQuestSettlementPending);
        Assert.Equal(0u, alice.Inventory.GetItemCount(ItemTestData.ToughJerky));
        IReadOnlyList<(WorldOpcode Opcode, byte[] Payload)> sent = NonUpdatePackets(session);
        Assert.Contains(sent, p => p.Opcode == WorldOpcode.SmsgInventoryChangeFailure);
        Assert.DoesNotContain(sent, p => p.Opcode == WorldOpcode.SmsgLootRemoved);
        Assert.Empty(rig.Quests.Looted);
        Assert.False(rig.Durable.Find(Key)!.Items.Single(i => i.Slot == jerky.Slot).IsLooted);
        Assert.False(rig.Chest.Loot!.FindSlot(jerky.Slot)!.IsLooted);

        rig.Durable.NextOutcome = LootOutcome.After;
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(alice, jerky.Slot));
        Assert.Equal(2u, alice.Inventory.GetItemCount(ItemTestData.ToughJerky));
    }

    [Fact]
    public void RefusedActor_RefusesTheTake()
    {
        Rig rig = CreateRig();
        (Player alice, FakeSession session) = rig.Join(1);
        rig.System.Use(alice, rig.Chest.Guid);
        ParsedLootItem jerky = Slot(Window(session), ItemTestData.ToughJerky);
        rig.Durable.RefuseActors = true;
        Assert.Equal(InventoryResult.LootCantLootThatNow, rig.Loot.TakeItem(alice, jerky.Slot));
        Assert.Equal(0u, alice.Inventory.GetItemCount(ItemTestData.ToughJerky));
        Assert.Single(rig.Durable.Started); // only the generation
    }

    [Fact]
    public void UnknownOutcome_BlocksTheChest_AndKicksTheActor()
    {
        Rig rig = CreateRig();
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        rig.System.Use(alice, rig.Chest.Guid);
        ParsedLootItem jerky = Slot(Window(aliceSession), ItemTestData.ToughJerky);

        rig.Durable.NextOutcome = LootOutcome.Unknown;
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(alice, jerky.Slot));
        Assert.True(aliceSession.Kicked);
        Assert.True(alice.IsQuestSettlementPending); // stays quarantined until a fresh login
        Assert.Equal(0u, alice.Inventory.GetItemCount(ItemTestData.ToughJerky));
        Assert.Equal(GameObjectUseResult.Unsupported, rig.System.Use(bob, rig.Chest.Guid));
        Assert.Equal(GameObjectUseResult.Unsupported, rig.System.OpenLock(bob, rig.Chest.Guid, LockType.Open));
    }

    [Fact]
    public void RoundRobinOwner_IsStored_AndAReleaseOpensTheLeftoversToTheGroup()
    {
        Rig rig = CreateRig();
        (Player alice, FakeSession aliceSession) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.RoundRobin, alice, bob);
        rig.System.Use(alice, rig.Chest.Guid);
        ParsedLoot window = Window(aliceSession);
        Assert.Equal(1, rig.Durable.Find(Key)!.LootOwnerCharacterId);
        Assert.Equal(GameObjectUseResult.InUse, rig.System.Use(bob, rig.Chest.Guid)); // only the owner may take shared stacks

        rig.Loot.Release(alice, rig.Chest.Guid); // leftovers: everybody may loot them
        LootOperation release = rig.Durable.Started.Last();
        Assert.Equal(0, release.Updated.LootOwnerCharacterId);
        Assert.Empty(release.Awards);
        Assert.Null(release.Actor);
        Assert.Equal(0, rig.Durable.Find(Key)!.LootOwnerCharacterId);

        rig.Leave(alice);
        rig.Leave(bob);
        rig.Recreate();
        (Player carol, FakeSession carolSession) = rig.Join(3);
        rig.Groups.ByMember.Clear();
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(carol, rig.Chest.Guid));
        Assert.Equal(2, Window(carolSession).Items.Count);
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(carol, Slot(window, ItemTestData.ToughJerky).Slot));
    }

    [Fact]
    public void OwnerRelease_WhileATakeIsPending_IsStoredWhenItFinishes_AndTheTakeDoesNotReinstateTheOwner()
    {
        Rig rig = CreateRig();
        (Player alice, FakeSession session) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.RoundRobin, alice, bob);
        rig.System.Use(alice, rig.Chest.Guid);
        ParsedLootItem jerky = Slot(Window(session), ItemTestData.ToughJerky);

        rig.Durable.Manual = true;
        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(alice, jerky.Slot));
        rig.Loot.Release(alice, rig.Chest.Guid); // clears the live owner; the stored one cannot change while pending
        Assert.Single(rig.Durable.Open);
        Assert.True(rig.Chest.Loot!.Owner.IsEmpty);

        rig.Durable.Complete(); // the take
        Assert.True(rig.Chest.Loot.Owner.IsEmpty);
        Assert.Single(rig.Durable.Open); // the owner release follows
        rig.Durable.Complete();
        Assert.Equal(0, rig.Durable.Find(Key)!.LootOwnerCharacterId);
        Assert.True(rig.Durable.Find(Key)!.Items.Single(i => i.Slot == jerky.Slot).IsLooted);
        Assert.Equal(2u, alice.Inventory.GetItemCount(ItemTestData.ToughJerky));
    }

    [Fact]
    public void Take_RaisesTheItemCountChange_AndTellsTheQuestJournal_AfterTheCommit()
    {
        Rig rig = CreateRig();
        (Player alice, FakeSession session) = rig.Join(1);
        var changes = new List<(uint Entry, int Delta)>();
        alice.Inventory.ItemCountChanged += (entry, delta) => changes.Add((entry, delta));
        rig.System.Use(alice, rig.Chest.Guid);
        ParsedLootItem jerky = Slot(Window(session), ItemTestData.ToughJerky);

        rig.Durable.Manual = true;
        rig.Loot.TakeItem(alice, jerky.Slot);
        Assert.Empty(changes);
        Assert.Empty(rig.Quests.Looted);
        rig.Durable.Complete();
        Assert.Equal([(ItemTestData.ToughJerky, 2)], changes);
        Assert.Equal([(alice.Guid, ItemTestData.ToughJerky, 2u)], rig.Quests.Looted);
        Assert.Contains(NonUpdatePackets(session), p => p.Opcode == WorldOpcode.SmsgItemPushResult);
    }

    [Fact]
    public void EmptyChest_IsStoredConsumed_AndDespawnsAfterTheRelease()
    {
        Rig rig = CreateRig(rows: []);
        (Player alice, FakeSession session) = rig.Join(1);
        GameObject chest = rig.Chest;
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(alice, chest.Guid));
        LootStateRecord record = rig.Durable.Find(Key)!;
        Assert.True(record.Consumed);
        Assert.Empty(record.Items);
        Assert.Equal(rig.Durable.Now + SpawnSeconds, record.RespawnAtUnix);
        Assert.Empty(Window(session).Items);

        rig.Loot.Release(alice, chest.Guid);
        rig.World.RunTick(50);
        Assert.False(chest.IsSpawned);
        rig.Leave(alice);
        rig.Recreate();
        rig.Join(2);
        Assert.False(rig.Chest.IsSpawned);
    }

    [Fact]
    public void ChestsWithoutAStoreOrALiveSave_AndRuntimeChests_StayUnsupported()
    {
        Rig rig = CreateRig();
        (Player alice, FakeSession session) = rig.Join(1);
        GameObject chest = rig.Chest;

        rig.Durable.Persist = false; // no live logical save for the map
        Assert.Equal(GameObjectUseResult.Unsupported, rig.System.Use(alice, chest.Guid));
        Assert.Equal(GameObjectUseResult.Unsupported, rig.System.OpenLock(alice, chest.Guid, LockType.Open));
        rig.Durable.Persist = true;

        GameObject summoned = rig.System.Summon(ChestEntry, 2, 0, 83.5f, 0)!;
        Assert.Equal(GameObjectUseResult.Unsupported, rig.System.Use(alice, summoned.Guid));
        Assert.Equal(GameObjectUseResult.Unsupported, rig.System.OpenLock(alice, summoned.Guid, LockType.Open));

        rig.Loot.Durable = null; // no store at all
        Assert.Equal(GameObjectUseResult.Unsupported, rig.System.Use(alice, chest.Guid));
        Assert.Empty(rig.Durable.Started);
        Assert.Empty(Packets(session, WorldOpcode.SmsgLootResponse));
        Assert.Null(chest.Loot);
    }

    [Fact]
    public void StoredChestOfAnotherEntry_IsNotRestoredFromAMismatchingRecord()
    {
        Rig rig = CreateRig();
        (Player alice, FakeSession session) = rig.Join(1);
        rig.System.Use(alice, rig.Chest.Guid);
        Window(session);
        rig.Loot.Release(alice, rig.Chest.Guid);
        rig.Leave(alice);
        rig.Durable.Cache[Key] = rig.Durable.Cache[Key] with { SourceEntry = ChestEntry + 1 };
        rig.Recreate();
        (Player bob, _) = rig.Join(2);
        Assert.Equal(GameObjectUseResult.Unsupported, rig.System.Use(bob, rig.Chest.Guid));
    }
}
