using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
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
    private const uint RulesChestEntry = 109;
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
            GoTemplate(DoorEntry, GameObjectType.Door, (2, 3 * 65536)), // data2 is seconds * 0x10000 (vmangos GetAutoCloseTime)
            GoTemplate(ChestEntry, GameObjectType.Chest, (1, ChestLoot)),
            GoTemplate(HerbEntry, GameObjectType.Chest, (0, HerbLock), (1, ChestLoot), (3, 1)),
            GoTemplate(GooberEntry, GameObjectType.Goober, (1, QuestId), (4, 2), (5, 0), (6, 10), (7, 33)),
            GoTemplate(KeyDoorEntry, GameObjectType.Door, (1, KeyLock)),
            GoTemplate(TextEntry, GameObjectType.Text, (0, 44)),
            GoTemplate(MailboxEntry, GameObjectType.Mailbox),
            GoTemplate(FocusEntry, GameObjectType.SpellFocus, (0, 4), (1, 10)),
            GoTemplate(QuestChestEntry, GameObjectType.Chest, (1, QuestChestLoot)),
            GoTemplate(RulesChestEntry, GameObjectType.Chest, (1, ChestLoot), (15, 1)),
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

        rig.World.RunTick(4000); // whole-second clock: closes 3 to 4 seconds after use
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

    [Theory]
    [InlineData(GameObjectType.Door)]
    [InlineData(GameObjectType.Button)]
    [InlineData(GameObjectType.Chest)]
    [InlineData(GameObjectType.Goober)]
    public void UnknownNonzeroLock_RefusesUseAndOpenLock_WhileZeroLockStillWorks(GameObjectType type)
    {
        int lockWord = type is GameObjectType.Door or GameObjectType.Button ? 1 : 0;
        (int, uint)[] lootData = type == GameObjectType.Chest ? [(1, ChestLoot)] : [];
        var content = new GameObjectContent(
            [GoTemplate(901, type, [.. lootData, (lockWord, 999u)]), GoTemplate(902, type, lootData)],
            [GoSpawn(1, 901, 3, 0), GoSpawn(2, 902, 3, 0)], [], [], []);
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var quests = new FakeQuestJournal();
        var loot = new LootService(new LootContent([(LootTableKind.GameObject, Row(ChestLoot, Hide, 100))], []))
            { Items = ItemStore, Quests = quests };
        var system = new GameObjectMapSystem(map, content, loot, quests);
        map.AddUpdater(system);
        (Player player, FakeSession session) = Player(1);
        world.AddPlayer(player);
        world.RunTick(50);
        session.Clear();
        GameObject locked = system.GameObjects.Single(go => go.Entry == 901);
        GameObject unlocked = system.GameObjects.Single(go => go.Entry == 902);
        var used = new List<GameObject>();
        system.Used += (_, go) => used.Add(go);

        Assert.Equal(GameObjectUseResult.Locked, system.Use(player, locked.Guid));
        Assert.Equal(GameObjectUseResult.Locked, system.OpenLock(player, locked.Guid, LockType.Open));
        Assert.Equal(GameObjectState.Ready, locked.State);
        Assert.Equal(GameObjectLootState.Ready, locked.LootState);
        Assert.Null(locked.Loot);
        Assert.Empty(used);
        Assert.Empty(quests.Used);
        Assert.Empty(player.Inventory.AllItems);
        Assert.Equal(0u, player.Money);
        Assert.Empty(Packets(session, WorldOpcode.SmsgLootResponse));
        Assert.Equal(GameObjectUseResult.Ok, system.Use(player, unlocked.Guid));
        Assert.Equal([unlocked], used);
    }

    [Fact]
    public void GroupLootChest_AdvancesTheRoundRobinPointer_ButAnOrdinaryNodeAndAChestWithoutRulesDoNot()
    {
        // vmangos Player.cpp:7680-7698: the pointer moves only for GAMEOBJECT_TYPE_CHEST with chest.groupLootRules (data15).
        Rig rig = CreateRig([GoSpawn(1, ChestEntry, 3, 0), GoSpawn(2, HerbEntry, 4, 0), GoSpawn(3, RulesChestEntry, 5, 0)]);
        var groups = new FakeGroups();
        rig.Loot.Groups = groups;
        (Player alice, _) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        Group group = groups.Create(LootMethod.RoundRobin, alice, bob);

        Assert.Equal(LootResult.Ok, rig.Loot.OpenGameObject(alice, rig.Single(HerbEntry), ChestLoot));
        Assert.Equal(LootResult.Ok, rig.Loot.OpenGameObject(alice, rig.Single(ChestEntry), ChestLoot));
        Assert.Equal(alice.Guid, group.LooterGuid);
        Assert.Empty(groups.LooterUpdates);
        Assert.True(rig.Single(HerbEntry).Loot!.Owner.IsEmpty);

        Assert.Equal(LootResult.Ok, rig.Loot.OpenGameObject(alice, rig.Single(RulesChestEntry), ChestLoot));
        Assert.Equal(bob.Guid, group.LooterGuid);
        Assert.Equal(alice.Guid, rig.Single(RulesChestEntry).Loot!.Owner);
        Assert.Equal([group], groups.LooterUpdates);
    }

    [Theory]
    [InlineData(LootMethod.GroupLoot, LootPermission.Roll)]
    [InlineData(LootMethod.NeedBeforeGreed, LootPermission.Roll)]
    [InlineData(LootMethod.MasterLoot, LootPermission.Master)]
    public void GroupRulesChest_UnderRollsOrMasterLoot_IsNotWidenedToAPasserby(LootMethod method, LootPermission permission)
    {
        Rig rig = CreateRig([GoSpawn(1, RulesChestEntry, 3, 0)]);
        var groups = new FakeGroups();
        rig.Loot.Groups = groups;
        (Player alice, _) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        (Player stranger, _) = rig.Join(3, 2, 0); // not in the group
        groups.Create(method, alice, bob);
        GameObject chest = rig.Single(RulesChestEntry);

        Assert.Equal(LootResult.Ok, rig.Loot.OpenGameObject(alice, chest, ChestLoot));
        LootBag bag = chest.Loot!;
        Assert.Equal(permission, bag.Permission);
        Assert.True(bag.Owner.IsEmpty); // rolls and master gives carry no owner, so the owner cannot be what keeps strangers out

        Assert.Equal(LootResult.NotAllowed, rig.Loot.OpenGameObject(stranger, chest, ChestLoot));
        Assert.DoesNotContain(stranger.Guid, bag.Recipients);
        Assert.Equal(InventoryResult.LootCantLootThatNow, rig.Loot.TakeItem(stranger, 0));
        if (method == LootMethod.MasterLoot)
        {
            Assert.Equal(MasterGiveResult.TargetNotEligible, rig.Loot.GiveMasterLoot(alice, chest.Guid, 0, stranger.Guid));
        }
    }

    [Fact]
    public void GroupRulesChest_UnderFreeForAll_StillLetsALateOpenerShare()
    {
        Rig rig = CreateRig([GoSpawn(1, RulesChestEntry, 3, 0)]);
        var groups = new FakeGroups();
        rig.Loot.Groups = groups;
        (Player alice, _) = rig.Join(1);
        (Player bob, _) = rig.Join(2, 1, 0);
        (Player stranger, _) = rig.Join(3, 2, 0);
        groups.Create(LootMethod.FreeForAll, alice, bob);
        GameObject chest = rig.Single(RulesChestEntry);

        Assert.Equal(LootResult.Ok, rig.Loot.OpenGameObject(alice, chest, ChestLoot));
        Assert.Equal(LootResult.Ok, rig.Loot.OpenGameObject(stranger, chest, ChestLoot)); // vmangos ALL_PERMISSION for chests
        Assert.Contains(stranger.Guid, chest.Loot!.Recipients);
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
    public void Chest_ReleasedWithLootLeft_StaysActivated_AndKeepsTheSameLoot()
    {
        Rig rig = CreateRig([GoSpawn(1, ChestEntry, 3, 0)]);
        (Player alice, _) = rig.Join(1);
        (Player bob, FakeSession bobSession) = rig.Join(2, 1, 0);
        GameObject chest = rig.Single(ChestEntry);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(alice, chest.Guid));
        LootBag bag = chest.Loot!;
        rig.Loot.Release(alice, chest.Guid);
        Assert.Equal(GameObjectLootState.Activated, chest.LootState);
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
        // data4 (customAnim) without an auto-close time is no custom animation (GameObject.cpp:1601-1604): the goober shows its active state.
        Assert.Empty(Packets(watcher, WorldOpcode.SmsgGameobjectCustomAnim));
        Assert.Equal(GameObjectState.Active, goober.State);

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
        Assert.True(goober.IsSpawned); // activated until its auto-close time (0 s) passes on the whole-second clock
        world.RunTick(1000);
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
    public void PartialChest_GridReloadKeepsTakenState_AndBindsTheFreshSource()
    {
        Rig rig = CreateRig([GoSpawn(1, ChestEntry, 3, 0, spawnTimeSeconds: 600)],
        [
            (LootTableKind.GameObject, Row(ChestLoot, ItemTestData.ToughJerky, 100)),
            (LootTableKind.GameObject, Row(ChestLoot, Hide, 100)),
        ]);

        // The grid must unload before the partly looted chest's five-minute despawn (the default grid delay is five minutes too).
        rig.Map.Grids.Options.GridCleanUpDelayMs = ArcaneCore.Game.Maps.Grid.MapOptions.MinGridDelayMs;
        (Player original, FakeSession originalSession) = rig.Join(1);
        GameObject oldChest = rig.Single(ChestEntry);
        originalSession.Clear();
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(original, oldChest.Guid));
        ParsedLoot initial = ParsedLoot.Parse(Assert.Single(Packets(originalSession, WorldOpcode.SmsgLootResponse)).Payload);
        ParsedLootItem taken = initial.Items.Single(item => item.ItemId == ItemTestData.ToughJerky);
        Assert.Equal(Items.InventoryResult.Ok, rig.Loot.TakeItem(original, taken.Slot));
        LootBag bag = oldChest.Loot!;
        rig.Loot.Release(original, oldChest.Guid);
        rig.World.RemovePlayer(original);
        for (int i = 0; i < 400 && rig.System.LoadedGridCount > 0; i++)
        {
            rig.World.RunTick(1000);
        }

        Assert.Equal(0, rig.System.LoadedGridCount);
        Assert.True(rig.System.ClockMs < 600_000);
        Assert.Null(oldChest.Map);
        Assert.Null(rig.Loot.FindLoot(oldChest.Guid));
        Assert.Empty(bag.Viewers);

        (Player replacement, FakeSession replacementSession) = rig.Join(1);
        GameObject freshChest = rig.Single(ChestEntry);
        Assert.NotSame(oldChest, freshChest);
        Assert.Same(bag, freshChest.Loot);
        Assert.Same(bag, rig.Loot.FindLoot(freshChest.Guid));
        Assert.False(rig.System.Remove(oldChest));
        rig.System.Despawn(oldChest);
        Assert.Same(freshChest, rig.System.Find(freshChest.Guid));
        Assert.Same(freshChest, rig.Map.FindObject(freshChest.Guid));
        replacementSession.Clear();
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(replacement, freshChest.Guid));
        ParsedLoot remaining = ParsedLoot.Parse(Assert.Single(Packets(replacementSession, WorldOpcode.SmsgLootResponse)).Payload);
        ParsedLootItem leftover = Assert.Single(remaining.Items);
        Assert.Equal(Hide, leftover.ItemId);
        Assert.Equal(Items.InventoryResult.LootCantLootThatNow, rig.Loot.TakeItem(original, leftover.Slot));
        Assert.Equal(Items.InventoryResult.AlreadyLooted, rig.Loot.TakeItem(replacement, taken.Slot));
        Assert.Equal(0u, replacement.Inventory.GetItemCount(ItemTestData.ToughJerky));
        Assert.Equal(Items.InventoryResult.Ok, rig.Loot.TakeItem(replacement, leftover.Slot));
        Assert.Equal(1u, replacement.Inventory.GetItemCount(Hide));
        Assert.Equal(1u, original.Inventory.GetItemCount(ItemTestData.ToughJerky));
        rig.Loot.Release(replacement, freshChest.Guid);
        rig.World.RunTick(50);
        Assert.False(freshChest.IsSpawned);
        rig.System.ForceRespawn(oldChest);
        Assert.Null(rig.Map.FindObject(freshChest.Guid));
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
    public void Relocate_IntoAnUnloadedGrid_LoadsIt_SoTheObjectLeavesWithThatGrid()
    {
        Rig rig = CreateRig([]);
        rig.Join(1);
        GameObject door = rig.System.Summon(DoorEntry, 2, 0, 83.5f, 0)!;
        Assert.False(rig.System.IsGridLoaded(5000, 5000)); // nobody is near: the game object system has not loaded that grid

        Assert.True(rig.System.Relocate(door, 5000, 5000, 83.5f, 1f));
        Assert.True(rig.System.IsGridLoaded(5000, 5000));
        Assert.Same(door, rig.System.Find(door.Guid));
        Assert.Same(rig.Map, door.Map);
        Assert.Equal((5000f, 5000f, 83.5f, 1f), (door.X, door.Y, door.Z, door.Orientation));

        // Before the fix the object was in _objects and in the map but in no grid list, so unloading its grid left it behind for good.
        ArcaneCore.Game.Maps.Grid.GridCoord coord = rig.Map.Grids.CellOf(door)!.Value.Grid;
        Assert.True(rig.Map.Grids.UnloadGrid(coord, force: true));
        Assert.False(rig.System.IsGridLoaded(5000, 5000));
        Assert.Null(rig.System.Find(door.Guid));
        Assert.Null(rig.Map.FindObject(door.Guid));
        Assert.DoesNotContain(door, rig.System.GameObjects);
    }

    [Fact]
    public void Summon_IntoAnUnloadedGrid_LoadsIt_SoTheObjectLeavesWithThatGrid()
    {
        Rig rig = CreateRig([]);
        rig.Join(1);
        GameObject door = rig.System.Summon(DoorEntry, 5000, 5000, 83.5f, 0)!;
        Assert.True(rig.System.IsGridLoaded(5000, 5000));

        Assert.True(rig.Map.Grids.UnloadGrid(rig.Map.Grids.CellOf(door)!.Value.Grid, force: true));
        Assert.Null(rig.System.Find(door.Guid));
        Assert.Null(rig.Map.FindObject(door.Guid));
    }

    [Fact]
    public void Relocate_RefusesANonFiniteOrOutOfMapPosition_AndLeavesTheObjectWhereItWas()
    {
        Rig rig = CreateRig([]);
        rig.Join(1);
        GameObject door = rig.System.Summon(DoorEntry, 2, 0, 83.5f, 0)!;

        Assert.False(rig.System.Relocate(door, float.PositiveInfinity, 0, 83.5f, 0)); // "1e40" parsed to a float
        Assert.False(rig.System.Relocate(door, 1e9f, 0, 83.5f, 0)); // finite, far outside the map
        Assert.False(rig.System.Relocate(door, 2, float.NaN, 83.5f, 0));
        Assert.False(rig.System.Relocate(door, 2, 0, 500_000f, 0)); // beyond IsValidZCoord
        Assert.False(rig.System.Relocate(door, 2, 0, 83.5f, float.NegativeInfinity));

        Assert.True(door.IsSpawned);
        Assert.Same(rig.Map, door.Map);
        Assert.Equal((2f, 0f, 83.5f, 0f), (door.X, door.Y, door.Z, door.Orientation));
        Assert.Equal(2f, door.GetFloat(UpdateFields.GameobjectPosX));
        Assert.True(rig.System.Relocate(door, 3, 0, 83.5f, 0));
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
        Assert.Equal(GameObjectLootState.Activated, chest.LootState); // nothing taken: activated with its loot (DoLootRelease)
        Assert.Equal(GameObjectState.Ready, chest.State);
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
