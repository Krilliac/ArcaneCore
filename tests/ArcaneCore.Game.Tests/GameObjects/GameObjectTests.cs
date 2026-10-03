using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

public sealed class GameObjectTests
{
    private const uint DoorEntry = 100;
    private const uint ChestEntry = 101;
    private const uint HerbEntry = 102;
    private const uint GooberEntry = 103;
    private const uint KeyDoorEntry = 104;
    private const uint TextEntry = 105;
    private const uint MailboxEntry = 106;
    private const uint FocusEntry = 107;
    private const uint QuestChestEntry = 108;
    private const uint ChestLoot = 500;
    private const uint QuestChestLoot = 501;
    private const uint QuestId = 77;
    private const uint HerbLock = 10;
    private const uint KeyLock = 11;
    private const uint OpenLock = 12;

    private static readonly LockEntry[] Locks =
    [
        new(HerbLock, [2, 0, 0, 0, 0, 0, 0, 0], [(uint)LockType.Herbalism, 0, 0, 0, 0, 0, 0, 0], [50, 0, 0, 0, 0, 0, 0, 0]),
        new(KeyLock, [1, 0, 0, 0, 0, 0, 0, 0], [ItemTestData.UniqueKey, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0]),
        new(OpenLock, [2, 0, 0, 0, 0, 0, 0, 0], [(uint)LockType.Open, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0]),
    ];

    private sealed class Rig
    {
        public required WorldRuntime World { get; init; }
        public required Map Map { get; init; }
        public required GameObjectMapSystem System { get; init; }
        public required LootService Loot { get; init; }
        public required FakeQuestJournal Quests { get; init; }

        public (Player Player, FakeSession Session) Join(uint guid, float x = 0, float y = 0)
        {
            (Player player, FakeSession session) = Player(guid, x, y);
            World.AddPlayer(player);
            World.RunTick(50);
            return (player, session);
        }

        public GameObject Single(uint entry) => System.GameObjects.Single(g => g.Entry == entry);
    }

    private static Rig CreateRig(IEnumerable<GameObjectSpawn> spawns, IEnumerable<(LootTableKind, LootStoreRow)>? loot = null, int seed = 3)
    {
        GameObjectTemplate[] templates =
        [
            GoTemplate(DoorEntry, GameObjectType.Door, (2, 3000)),
            GoTemplate(ChestEntry, GameObjectType.Chest, (1, ChestLoot)),
            GoTemplate(HerbEntry, GameObjectType.Chest, (0, HerbLock), (1, ChestLoot), (3, 1)),
            GoTemplate(GooberEntry, GameObjectType.Goober, (1, QuestId), (4, 2), (5, 0), (6, 10), (7, 33)),
            GoTemplate(KeyDoorEntry, GameObjectType.Door, (1, KeyLock)),
            GoTemplate(TextEntry, GameObjectType.Text, (0, 44)),
            GoTemplate(MailboxEntry, GameObjectType.Mailbox),
            GoTemplate(FocusEntry, GameObjectType.SpellFocus, (0, 4), (1, 10)),
            GoTemplate(QuestChestEntry, GameObjectType.Chest, (1, QuestChestLoot)),
        ];
        var content = new GameObjectContent(templates, spawns, Locks, [(GooberEntry, QuestId)], []);
        var lootContent = new LootContent(loot ??
        [
            (LootTableKind.GameObject, Row(ChestLoot, ItemTestData.ToughJerky, 100, minOrRef: 2, max: 2)),
            (LootTableKind.GameObject, Row(QuestChestLoot, QuestItem, -100)),
        ], []);
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var quests = new FakeQuestJournal();
        var service = new LootService(lootContent, random: new Random(seed)) { Items = ItemStore, Quests = quests };
        var system = new GameObjectMapSystem(map, content, service, quests);
        map.AddUpdater(system);
        return new Rig { World = world, Map = map, System = system, Loot = service, Quests = quests };
    }

    [Fact]
    public void GridLoad_CreatesSpawnedObjects_WithGameObjectFields()
    {
        Rig rig = CreateRig([GoSpawn(1, DoorEntry, 10, 0, orientation: 2.0f), GoSpawn(2, DoorEntry, 5000, 5000)]);
        rig.World.RunTick(50);
        Assert.Empty(rig.System.GameObjects);

        (Player player, FakeSession session) = rig.Join(1);
        GameObject door = Assert.Single(rig.System.GameObjects);
        Assert.True(door.IsSpawned);
        Assert.Equal(HighGuid.GameObject, door.Guid.High);
        Assert.Equal(1u, door.Guid.Counter);
        ParsedBlock create = Assert.Single(DrainBlocks(session), b => b.Guids.Contains(door.Guid.Value));
        Assert.Equal(TypeId.GameObject, create.TypeId);
        Assert.Equal(DoorEntry, create.Values[UpdateFields.ObjectFieldEntry]);
        Assert.Equal(1000 + DoorEntry, create.Values[UpdateFields.GameobjectDisplayid]);
        Assert.Equal((uint)GameObjectState.Ready, create.Values[UpdateFields.GameobjectState]);
        Assert.Equal((uint)GameObjectType.Door, create.Values.GetValueOrDefault(UpdateFields.GameobjectTypeId));
        Assert.Equal(100u, create.Values[UpdateFields.GameobjectAnimprogress]);
        Assert.Equal(BitConverter.SingleToUInt32Bits(MathF.Sin(1.0f)), create.Values[UpdateFields.GameobjectRotation + 2]);
        Assert.Equal(BitConverter.SingleToUInt32Bits(MathF.Cos(1.0f)), create.Values[UpdateFields.GameobjectRotation + 3]);
        Assert.Contains(door.Guid, player.VisibleObjects);
    }

    [Fact]
    public void ComputeRotation_KeepsStoredQuaternion_AndDerivesFromOrientationWhenZero()
    {
        Assert.Equal((0.1f, 0.2f, 0.3f, 0.4f), GameObject.ComputeRotation(1.0f, 0.1f, 0.2f, 0.3f, 0.4f));
        (float x, float y, float z, float w) = GameObject.ComputeRotation(MathF.PI, 0, 0, 0, 0);
        Assert.Equal(0f, x);
        Assert.Equal(0f, y);
        Assert.Equal(1f, z, 5);
        Assert.Equal(0f, w, 5);
    }

    [Fact]
    public void QueryResponse_HasNameFourZeroBytesAndTwentyFourDataFields_UnknownSetsHighBit()
    {
        GameObjectTemplate t = GoTemplate(ChestEntry, GameObjectType.Chest, (0, 7), (23, 9));
        var reader = new PacketReader(GameObjectPackets.QueryResponse(t));
        Assert.Equal(ChestEntry, reader.ReadUInt32());
        Assert.Equal(3u, reader.ReadUInt32());
        Assert.Equal(1000 + ChestEntry, reader.ReadUInt32());
        Assert.Equal($"GO {ChestEntry}", reader.ReadCString());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(7u, reader.ReadUInt32());
        for (int i = 1; i < 23; i++)
        {
            Assert.Equal(0u, reader.ReadUInt32());
        }

        Assert.Equal(9u, reader.ReadUInt32());
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(BitConverter.GetBytes(0x80000000u | 1234), GameObjectPackets.QueryUnknown(1234));
    }

    [Fact]
    public void Door_OpensWithInUse_RefusesWhileActive_AndAutoClosesAfterData2()
    {
        Rig rig = CreateRig([GoSpawn(1, DoorEntry, 3, 0)]);
        (Player player, FakeSession session) = rig.Join(1);
        GameObject door = rig.Single(DoorEntry);
        session.Clear();

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, door.Guid));
        Assert.Equal(GameObjectState.Active, door.State);
        Assert.True(door.Flags.HasFlag(GameObjectFlags.InUse));
        Assert.Equal(GameObjectUseResult.InUse, rig.System.Use(player, door.Guid));
        rig.World.RunTick(50);
        ParsedBlock opened = Assert.Single(DrainBlocks(session), b => b.Type == ObjectUpdateType.Values);
        Assert.Equal((uint)GameObjectState.Active, opened.Values[UpdateFields.GameobjectState]);

        rig.World.RunTick(3000);
        Assert.Equal(GameObjectState.Ready, door.State);
        Assert.False(door.Flags.HasFlag(GameObjectFlags.InUse));
        Assert.Equal(GameObjectLootState.Ready, door.LootState);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, door.Guid));
    }

    [Fact]
    public void Use_IsRefused_WhenTooFar_Dead_Unknown_OrNoInteract()
    {
        Rig rig = CreateRig([GoSpawn(1, DoorEntry, 3, 0), GoSpawn(2, DoorEntry, 40, 0)]);
        (Player player, _) = rig.Join(1);
        GameObject near = rig.System.GameObjects.Single(g => g.Spawn!.Guid == 1);
        GameObject far = rig.System.GameObjects.Single(g => g.Spawn!.Guid == 2);

        Assert.Equal(GameObjectUseResult.TooFar, rig.System.Use(player, far.Guid));
        Assert.Equal(GameObjectUseResult.NotFound, rig.System.Use(player, new ObjectGuid(0xF110000000000999)));
        near.Flags |= GameObjectFlags.NoInteract;
        Assert.Equal(GameObjectUseResult.NotUsable, rig.System.Use(player, near.Guid));
        near.Flags &= ~GameObjectFlags.NoInteract;
        player.Health = 0;
        Assert.Equal(GameObjectUseResult.Dead, rig.System.Use(player, near.Guid));
        Assert.Equal(GameObjectState.Ready, near.State);
    }

    [Fact]
    public void KeyLockedDoor_NeedsTheKeyInTheBags()
    {
        Rig rig = CreateRig([GoSpawn(1, KeyDoorEntry, 3, 0)]);
        (Player player, _) = rig.Join(1);
        GameObject door = rig.Single(KeyDoorEntry);

        Assert.Equal(GameObjectUseResult.MissingKey, rig.System.Use(player, door.Guid));
        Assert.Equal(GameObjectState.Ready, door.State);
        ItemTestData.Give(player.Inventory, ItemTestData.UniqueKey);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, door.Guid));
        Assert.Equal(GameObjectState.Active, door.State);
    }

    [Fact]
    public void LockRules_DirectUseAndOpenLock()
    {
        (Player player, _) = Player(1);
        Assert.Equal(GameObjectUseResult.Ok, GameObjectLocks.CheckDirectUse(null, player));
        Assert.Equal(GameObjectUseResult.Ok, GameObjectLocks.CheckDirectUse(Locks[2], player));
        Assert.Equal(GameObjectUseResult.Locked, GameObjectLocks.CheckDirectUse(Locks[0], player));
        Assert.Equal(GameObjectUseResult.MissingKey, GameObjectLocks.CheckDirectUse(Locks[1], player));

        Assert.Equal(GameObjectUseResult.SkillTooLow, GameObjectLocks.CheckOpenLock(Locks[0], player, LockType.Herbalism, 0, (_, _) => 49));
        Assert.Equal(GameObjectUseResult.Ok, GameObjectLocks.CheckOpenLock(Locks[0], player, LockType.Herbalism, 0, (_, _) => 50));
        Assert.Equal(GameObjectUseResult.Locked, GameObjectLocks.CheckOpenLock(Locks[0], player, LockType.Mining, 0, (_, _) => 300));
        Assert.Equal(GameObjectUseResult.Ok, GameObjectLocks.CheckOpenLock(Locks[1], player, LockType.Open, ItemTestData.UniqueKey, (_, _) => 0));
        Assert.Equal(GameObjectUseResult.Locked, GameObjectLocks.CheckOpenLock(Locks[1], player, LockType.Open, 1234, (_, _) => 0));
        Assert.Equal(LockSkills.Herbalism, LockSkills.ForLockType(LockType.Herbalism));
        Assert.Equal(LockSkills.Mining, LockSkills.ForLockType(LockType.Mining));
        Assert.Equal(LockSkills.Lockpicking, LockSkills.ForLockType(LockType.PickLock));
        Assert.Equal(0u, LockSkills.ForLockType(LockType.Open));
        Assert.Equal(HerbLock, GameObjectLocks.LockIdOf(GoTemplate(HerbEntry, GameObjectType.Chest, (0, HerbLock))));
        Assert.Equal(KeyLock, GameObjectLocks.LockIdOf(GoTemplate(KeyDoorEntry, GameObjectType.Door, (1, KeyLock))));
        Assert.Equal(0u, GameObjectLocks.LockIdOf(GoTemplate(MailboxEntry, GameObjectType.Mailbox, (0, 5))));
    }

    [Fact]
    public void HerbNode_NeedsTheOpenLockSpell_AndTheSkill()
    {
        Rig rig = CreateRig([GoSpawn(1, HerbEntry, 3, 0)]);
        (Player player, FakeSession session) = rig.Join(1);
        GameObject herb = rig.Single(HerbEntry);
        session.Clear();

        Assert.Equal(GameObjectUseResult.Locked, rig.System.Use(player, herb.Guid));
        rig.System.SkillValue = (_, skill) => skill == LockSkills.Herbalism ? 10u : 0u;
        Assert.Equal(GameObjectUseResult.SkillTooLow, rig.System.OpenLock(player, herb.Guid, LockType.Herbalism));
        Assert.Equal(GameObjectUseResult.Locked, rig.System.OpenLock(player, herb.Guid, LockType.Mining));
        Assert.Empty(Packets(session, WorldOpcode.SmsgLootResponse));

        rig.System.SkillValue = (_, _) => 75;
        Assert.Equal(GameObjectUseResult.Ok, rig.System.OpenLock(player, herb.Guid, LockType.Herbalism));
        ParsedLoot loot = ParsedLoot.Parse(Assert.Single(Packets(session, WorldOpcode.SmsgLootResponse)).Payload);
        Assert.Equal(herb.Guid.Value, loot.Guid);
        Assert.Equal(ItemTestData.ToughJerky, Assert.Single(loot.Items).ItemId);
    }

    [Fact]
    public void Chest_LootedOut_Despawns_AndRespawnsAfterSpawnTime()
    {
        Rig rig = CreateRig([GoSpawn(1, ChestEntry, 3, 0, spawnTimeSeconds: 5)]);
        (Player player, FakeSession session) = rig.Join(1);
        GameObject chest = rig.Single(ChestEntry);
        session.Clear();

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));
        Assert.Equal(GameObjectLootState.Activated, chest.LootState);
        Assert.True(player.UnitFlags.HasFlag(UnitFlags.Looting));
        ParsedLoot loot = ParsedLoot.Parse(Assert.Single(Packets(session, WorldOpcode.SmsgLootResponse)).Payload);
        ParsedLootItem jerky = Assert.Single(loot.Items);
        Assert.Equal((ItemTestData.ToughJerky, 2u, LootSlotType.AllowLoot), (jerky.ItemId, jerky.Count, jerky.SlotType));

        Assert.Equal(Items.InventoryResult.Ok, rig.Loot.TakeItem(player, jerky.Slot));
        Assert.Equal(2u, player.Inventory.GetItemCount(ItemTestData.ToughJerky));
        Assert.Equal([jerky.Slot], Assert.Single(Packets(session, WorldOpcode.SmsgLootRemoved)).Payload);
        rig.Loot.Release(player, chest.Guid);
        Assert.False(player.UnitFlags.HasFlag(UnitFlags.Looting));
        Assert.Equal(GameObjectLootState.JustDeactivated, chest.LootState);

        rig.World.RunTick(50);
        Assert.False(chest.IsSpawned);
        Assert.Null(rig.Map.FindObject(chest.Guid));
        Assert.Equal(chest.Guid.Value, BitConverter.ToUInt64(Assert.Single(Packets(session, WorldOpcode.SmsgDestroyObject)).Payload));
        Assert.Equal(GameObjectUseResult.NotFound, rig.System.Use(player, chest.Guid));

        rig.World.RunTick(4000);
        Assert.False(chest.IsSpawned);
        rig.World.RunTick(1000);
        Assert.True(chest.IsSpawned);
        Assert.Equal(GameObjectLootState.Ready, chest.LootState);
        Assert.Null(chest.Loot);
        rig.World.RunTick(50);
        Assert.Contains(DrainBlocks(session), b => b.Type is ObjectUpdateType.CreateObject or ObjectUpdateType.CreateObject2 && b.Guids.Contains(chest.Guid.Value));
    }

    [Fact]
    public void Chest_ReleasedWithLootLeft_IsReady_AndKeepsTheSameLoot()
    {
        Rig rig = CreateRig([GoSpawn(1, ChestEntry, 3, 0)]);
        (Player alice, _) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        GameObject chest = rig.Single(ChestEntry);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(alice, chest.Guid));
        LootBag bag = chest.Loot!;
        rig.Loot.Release(alice, chest.Guid);
        Assert.Equal(GameObjectLootState.Ready, chest.LootState);
        rig.World.RunTick(50);
        Assert.True(chest.IsSpawned);

        bobSession.Clear();
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(bob, chest.Guid));
        Assert.Same(bag, chest.Loot);
        Assert.Single(ParsedLoot.Parse(Assert.Single(Packets(bobSession, WorldOpcode.SmsgLootResponse)).Payload).Items);
    }

    [Fact]
    public void Goober_QuestGate_Credit_PageText_Anim_Cooldown()
    {
        Rig rig = CreateRig([GoSpawn(1, GooberEntry, 3, 0)]);
        (Player player, FakeSession session) = rig.Join(1);
        (_, FakeSession watcher) = rig.Join(2, 2, 0);
        GameObject goober = rig.Single(GooberEntry);
        session.Clear();
        watcher.Clear();
        var used = new List<GameObject>();
        rig.System.Used += (_, go) => used.Add(go);

        Assert.Equal(GameObjectUseResult.NeedsQuest, rig.System.Use(player, goober.Guid));
        Assert.Empty(rig.Quests.Used);

        rig.Quests.Incomplete.Add((player.Guid, QuestId));
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, goober.Guid));
        Assert.Equal([(player.Guid, GooberEntry, goober.Guid)], rig.Quests.Used);
        Assert.Equal([goober], used);
        Assert.Equal(BitConverter.GetBytes(goober.Guid.Value), Assert.Single(Packets(session, WorldOpcode.SmsgGameobjectPagetext)).Payload);
        byte[] anim = Assert.Single(Packets(watcher, WorldOpcode.SmsgGameobjectCustomAnim)).Payload;
        Assert.Equal(goober.Guid.Value, BitConverter.ToUInt64(anim, 0));
        Assert.Equal(2u, BitConverter.ToUInt32(anim, 8));

        Assert.Equal(GameObjectUseResult.OnCooldown, rig.System.Use(player, goober.Guid));
        rig.World.RunTick(10_000);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, goober.Guid));
    }

    [Fact]
    public void ConsumableGoober_DespawnsAfterUse()
    {
        GameObjectTemplate consumable = GoTemplate(GooberEntry, GameObjectType.Goober, (5, 1));
        var content = new GameObjectContent([consumable], [GoSpawn(1, GooberEntry, 3, 0, spawnTimeSeconds: 2)], [], [], []);
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var system = new GameObjectMapSystem(map, content);
        map.AddUpdater(system);
        (Player player, _) = Player(1);
        world.AddPlayer(player);
        world.RunTick(50);
        GameObject goober = Assert.Single(system.GameObjects);

        Assert.Equal(GameObjectUseResult.Ok, system.Use(player, goober.Guid));
        world.RunTick(50);
        Assert.False(goober.IsSpawned);
        world.RunTick(2000);
        Assert.True(goober.IsSpawned);
    }

    [Fact]
    public void QuestChest_SparklesOnlyForViewersWhoNeedItsLoot_AndRefreshesWhenThatChanges()
    {
        Rig rig = CreateRig([GoSpawn(1, QuestChestEntry, 3, 0)]);
        (Player needy, FakeSession needySession) = Player(1);
        (Player other, FakeSession otherSession) = Player(2, 1, 0);
        rig.Quests.Needs.Add((needy.Guid, QuestItem));
        rig.World.AddPlayer(needy);
        rig.World.AddPlayer(other);
        rig.World.RunTick(50);
        GameObject chest = rig.Single(QuestChestEntry);

        const uint sparkle = (uint)(GameObjectDynFlags.Activate | GameObjectDynFlags.Sparkle);
        ParsedBlock forNeedy = Assert.Single(DrainBlocks(needySession), b => b.Guids.Contains(chest.Guid.Value));
        ParsedBlock forOther = Assert.Single(DrainBlocks(otherSession), b => b.Guids.Contains(chest.Guid.Value));
        Assert.Equal(sparkle, forNeedy.Values[UpdateFields.GameobjectDynFlags]);
        Assert.False(forOther.Values.ContainsKey(UpdateFields.GameobjectDynFlags));
        Assert.Equal(GameObjectDynFlags.None, rig.System.QuestFlagsFor(chest, other));

        rig.World.RunTick(1000); // snapshot taken
        DrainBlocks(needySession);
        rig.Quests.Needs.Add((other.Guid, QuestItem));
        rig.Quests.Needs.Remove((needy.Guid, QuestItem));
        rig.World.RunTick(1000);
        ParsedBlock updatedOther = Assert.Single(DrainBlocks(otherSession), b => b.Type == ObjectUpdateType.Values && b.Guids.Contains(chest.Guid.Value));
        ParsedBlock updatedNeedy = Assert.Single(DrainBlocks(needySession), b => b.Type == ObjectUpdateType.Values && b.Guids.Contains(chest.Guid.Value));
        Assert.Equal(sparkle, updatedOther.Values[UpdateFields.GameobjectDynFlags]);
        Assert.Equal(0u, updatedNeedy.Values[UpdateFields.GameobjectDynFlags]);
        Assert.Equal(0u, chest.GetUInt32(UpdateFields.GameobjectDynFlags)); // stored value stays 0
    }

    [Fact]
    public void QuestChest_WithQuestItemOnlyForOthers_ShowsNothingToANonNeedingOpener()
    {
        Rig rig = CreateRig([GoSpawn(1, QuestChestEntry, 3, 0)]);
        (Player player, FakeSession session) = rig.Join(1);
        GameObject chest = rig.Single(QuestChestEntry);
        session.Clear();

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chest.Guid));
        Assert.Empty(ParsedLoot.Parse(Assert.Single(Packets(session, WorldOpcode.SmsgLootResponse)).Payload).Items);
        rig.Loot.Release(player, chest.Guid);
        Assert.Equal(GameObjectLootState.JustDeactivated, chest.LootState); // nothing generated: empty
    }

    [Fact]
    public void NegativeSpawnTime_StartsDespawned_UntilForcedRespawn_ThenStaysDownAfterUse()
    {
        Rig rig = CreateRig([GoSpawn(1, ChestEntry, 3, 0, spawnTimeSeconds: -30)]);
        (Player player, _) = rig.Join(1);
        GameObject chest = rig.Single(ChestEntry);
        Assert.False(chest.IsSpawned);

        rig.System.ForceRespawn(chest);
        Assert.True(chest.IsSpawned);
        rig.System.Use(player, chest.Guid);
        rig.Loot.TakeItem(player, 0);
        rig.Loot.Release(player, chest.Guid);
        rig.World.RunTick(50);
        Assert.False(chest.IsSpawned);
        Assert.Equal(0, chest.RespawnAtMs);
        rig.World.RunTick(60_000);
        Assert.False(chest.IsSpawned);
    }

    [Fact]
    public void GridUnload_KeepsADespawnedSpawnsRespawnTime()
    {
        Rig rig = CreateRig([GoSpawn(1, ChestEntry, 3, 0, spawnTimeSeconds: 600)]);
        (Player player, _) = rig.Join(1);
        GameObject chest = rig.Single(ChestEntry);
        rig.System.Despawn(chest);
        long respawnAt = chest.RespawnAtMs;
        Assert.True(respawnAt > rig.System.ClockMs);

        rig.World.RemovePlayer(player);
        for (int i = 0; i < 400 && rig.System.LoadedGridCount > 0; i++)
        {
            rig.World.RunTick(1000);
        }

        Assert.Equal(0, rig.System.LoadedGridCount);
        Assert.Empty(rig.System.GameObjects);
        Assert.Equal(respawnAt, rig.System.PendingRespawnAt(1));
    }

    [Fact]
    public void Summon_DespawnsForGoodAfterItsTimer_AndRemoveTakesItOut()
    {
        Rig rig = CreateRig([]);
        (Player player, _) = rig.Join(1);
        GameObject? summoned = rig.System.Summon(ChestEntry, 2, 0, 83.5f, 0, despawnAfterSeconds: 2);
        Assert.NotNull(summoned);
        Assert.Null(rig.System.Summon(9999, 0, 0, 0, 0));
        Assert.True(summoned.IsSpawned);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, summoned.Guid));
        rig.World.RunTick(2000);
        Assert.False(summoned.IsSpawned);
        Assert.Null(rig.System.Find(summoned.Guid));
        Assert.False(player.UnitFlags.HasFlag(UnitFlags.Looting)); // forgotten loot closed the window

        GameObject other = rig.System.Summon(DoorEntry, 2, 0, 83.5f, 0)!;
        Assert.True(rig.System.Remove(other));
        Assert.False(rig.System.Remove(other));
        Assert.Null(rig.Map.FindObject(other.Guid));
    }

    [Fact]
    public void TextMailboxAndSpellFocus_Seams()
    {
        Rig rig = CreateRig([GoSpawn(1, TextEntry, 3, 0), GoSpawn(2, MailboxEntry, 0, 3), GoSpawn(3, FocusEntry, -3, 0)]);
        (Player player, FakeSession session) = rig.Join(1);
        GameObject text = rig.Single(TextEntry);
        GameObject mailbox = rig.Single(MailboxEntry);
        session.Clear();

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, text.Guid));
        Assert.Single(Packets(session, WorldOpcode.SmsgGameobjectPagetext));
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, mailbox.Guid));
        Assert.Same(mailbox, rig.System.FindInteractable(player, mailbox.Guid, GameObjectType.Mailbox));
        Assert.Null(rig.System.FindInteractable(player, mailbox.Guid, GameObjectType.AuctionHouse));
        Assert.Null(rig.System.FindInteractable(player, text.Guid, GameObjectType.Mailbox));
        Assert.Equal(GameObjectUseResult.NotUsable, rig.System.Use(player, rig.Single(FocusEntry).Guid));
        Assert.True(rig.System.HasSpellFocusNearby(player, 4));
        Assert.False(rig.System.HasSpellFocusNearby(player, 5));
        player.Relocate(30, 0, 83.5f, 0, 0);
        Assert.False(rig.System.HasSpellFocusNearby(player, 4));
    }

    [Fact]
    public void QuestGiverObject_UsesTheQuestGiverSeam_OrIsUnsupported()
    {
        var content = new GameObjectContent([GoTemplate(1, GameObjectType.QuestGiver)], [GoSpawn(1, 1, 3, 0)], [], [(1, 5)], [(1, 6)]);
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var system = new GameObjectMapSystem(map, content);
        map.AddUpdater(system);
        (Player player, _) = Player(1);
        world.AddPlayer(player);
        world.RunTick(50);
        GameObject giver = Assert.Single(system.GameObjects);

        Assert.Equal(GameObjectUseResult.Unsupported, system.Use(player, giver.Guid));
        var seam = new RecordingQuestGiver();
        system.QuestGiver = seam;
        Assert.Equal(GameObjectUseResult.Ok, system.Use(player, giver.Guid));
        Assert.Equal([giver], seam.Opened);
        Assert.Equal([5u], content.QuestStartersOf(1));
        Assert.Equal([6u], content.QuestEndersOf(1));
    }

    [Fact]
    public void ChestWithoutLootService_IsUnsupported()
    {
        var content = new GameObjectContent([GoTemplate(ChestEntry, GameObjectType.Chest, (1, 1))], [GoSpawn(1, ChestEntry, 3, 0)], [], [], []);
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var system = new GameObjectMapSystem(map, content);
        map.AddUpdater(system);
        (Player player, _) = Player(1);
        world.AddPlayer(player);
        world.RunTick(50);
        Assert.Equal(GameObjectUseResult.Unsupported, system.Use(player, Assert.Single(system.GameObjects).Guid));
    }

    [Fact]
    public void LeavingTheMap_ClosesTheLootWindow()
    {
        Rig rig = CreateRig([GoSpawn(1, ChestEntry, 3, 0)]);
        (Player player, _) = rig.Join(1);
        GameObject chest = rig.Single(ChestEntry);
        rig.System.Use(player, chest.Guid);
        Assert.NotNull(rig.Loot.OpenLootOf(player));

        rig.World.RemovePlayer(player);
        rig.World.RunTick(50);
        Assert.Null(rig.Loot.OpenLootOf(player));
        Assert.False(player.UnitFlags.HasFlag(UnitFlags.Looting));
        Assert.Equal(GameObjectLootState.Ready, chest.LootState);
    }

    private sealed class RecordingQuestGiver : IGameObjectQuestGiver
    {
        public List<GameObject> Opened { get; } = [];

        public bool OpenQuestMenu(Player player, GameObject go)
        {
            Opened.Add(go);
            return true;
        }
    }
}
