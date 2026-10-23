using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

public sealed class GameObjectPerfAndLootTests
{
    private const uint DoorEntry = 100;
    private const uint ZeroSizeDoorEntry = 101;
    private const uint ChestEntry = 102;
    private const uint QuestChestEntry = 103;
    private const uint GooberEntry = 104;
    private const uint ChestLoot = 500;
    private const uint QuestChestLoot = 501;
    private const uint CreatureLoot = 600;
    private const uint QuestId = 77;

    [Fact]
    public void HealthyDoors_SkipUpdateObjects_WhileUnhealthyDoorAuditsOnceAndRecovers()
    {
        GameObjectTemplate healthyDoor = GoTemplate(DoorEntry, GameObjectType.Door, (2, 3 * 65536));
        GameObjectTemplate zeroSizeDoor = new()
        {
            Entry = ZeroSizeDoorEntry,
            Type = (uint)GameObjectType.Door,
            DisplayId = 1101,
            Name = "Zero Size Door",
            Size = 0f,
            Data = new uint[GameObjectTemplate.DataCount],
        };
        var content = new GameObjectContent(
            [healthyDoor, zeroSizeDoor],
            [GoSpawn(1, DoorEntry, 2, 0), GoSpawn(2, DoorEntry, 4, 0)],
            [],
            [],
            []);
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var system = new GameObjectMapSystem(map, content);
        map.AddUpdater(system);

        (Player player, _) = Player(1);
        world.AddPlayer(player);
        world.RunTick(50);

        Assert.Equal(2, system.GameObjects.Count);
        Assert.Equal(0, system.LastUpdatedObjectCount);

        world.RunTick(50);
        Assert.Equal(0, system.LastUpdatedObjectCount);

        GameObject badDoor = system.Summon(ZeroSizeDoorEntry, 3, 0, 83.5f, 0)!;
        Assert.NotNull(badDoor);

        // First tick after spawning the unhealthy closed door audits and reports it once.
        world.RunTick(50);
        Assert.Equal(1, system.LastUpdatedObjectCount);

        // Subsequent ticks while still unhealthy do not re-enter UpdateObjects.
        world.RunTick(50);
        Assert.Equal(0, system.LastUpdatedObjectCount);

        // Opening the door clears the closed-without-collision state: audited once to clear the reported set, then quiet again.
        badDoor.State = GameObjectState.Active;
        world.RunTick(50);
        Assert.Equal(1, system.LastUpdatedObjectCount);

        world.RunTick(50);
        Assert.Equal(0, system.LastUpdatedObjectCount);
    }

    [Fact]
    public void RefreshQuestFlags_SkipsBotOnlyMaps_AndUpdatesRealViewers()
    {
        GameObjectTemplate[] templates =
        [
            GoTemplate(QuestChestEntry, GameObjectType.Chest, (1, QuestChestLoot)),
            GoTemplate(GooberEntry, GameObjectType.Goober, (1, QuestId)),
        ];
        var content = new GameObjectContent(
            templates,
            [GoSpawn(1, QuestChestEntry, 2, 0), GoSpawn(2, GooberEntry, 3, 0)],
            [],
            [(GooberEntry, QuestId)],
            []);
        var lootContent = new LootContent(
        [
            (LootTableKind.GameObject, Row(QuestChestLoot, QuestItem, -100)),
        ], []);
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var quests = new FakeQuestJournal();
        var loot = new LootService(lootContent, random: new Random(1)) { Items = ItemStore, Quests = quests };
        var system = new GameObjectMapSystem(map, content, loot, quests);
        map.AddUpdater(system);

        (Player bot, FakeSession botSession) = Player(1);
        botSession.IsPlayerbot = true;
        botSession.WantsUpdateObjects = false;
        quests.Needs.Add((bot.Guid, QuestItem));
        quests.Incomplete.Add((bot.Guid, QuestId));
        world.AddPlayer(bot);
        world.RunTick(50);
        botSession.Clear();

        world.RunTick(1000);
        Assert.Equal(0, system.QuestFlagsTrackedObjectCount);
        Assert.Empty(DrainBlocks(botSession));

        (Player viewer, FakeSession viewerSession) = Player(2, 1, 0);
        world.AddPlayer(viewer);
        world.RunTick(50);
        Assert.Equal(2, system.QuestFlagsTrackedObjectCount);
        viewerSession.Clear();

        quests.Needs.Add((viewer.Guid, QuestItem));
        quests.Incomplete.Add((viewer.Guid, QuestId));
        world.RunTick(1000);

        List<ParsedBlock> blocks = DrainBlocks(viewerSession);
        uint expectedFlags = (uint)(GameObjectDynFlags.Activate | GameObjectDynFlags.Sparkle);
        Assert.Equal(2, blocks.Count(b =>
            b.Type == ObjectUpdateType.Values
            && b.Values.TryGetValue(UpdateFields.GameobjectDynFlags, out uint flags)
            && flags == expectedFlags));
    }

    [Fact]
    public void CreatureCorpseDecay_AndRespawn_ForgetCorpseLootImmediately_WithoutStalePruneLog()
    {
        CreatureTemplate wolf = Template(WolfEntry);
        CreatureContent creatureContent = Content(
            [wolf],
            [Spawn(1, WolfEntry, 2, 0, respawnSeconds: 10), Spawn(2, WolfEntry, 4, 0, respawnSeconds: 10)]);
        var goContent = new GameObjectContent([GoTemplate(ChestEntry, GameObjectType.Chest)], [], [], [], []);
        var lootContent = new LootContent(
            [(LootTableKind.Creature, Row(CreatureLoot, ItemTestData.ToughJerky, 100))],
            [new CreatureLootInfo(WolfEntry, CreatureLoot, 0, 5, 5)]);

        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var creatures = new CreatureMapSystem(map, creatureContent, new CreatureOptions { CorpseDecayNormalSeconds = 1 }, random: new Random(1));
        map.AddUpdater(creatures);

        var logger = new RecordingLogger();
        var loot = new LootService(lootContent, random: new Random(1), logger: logger) { Items = ItemStore };
        var objects = new GameObjectMapSystem(map, goContent, loot, logger: logger);
        objects.SubscribeCreatureLifecycle(creatures);
        map.AddUpdater(objects);
        map.Combat.UnitKilled += (killer, victim) => loot.OnCreatureKilled(killer, victim);

        (Player player, _) = Player(1);
        world.AddPlayer(player);
        world.RunTick(50);

        Creature[] wolves = [.. creatures.Creatures.OrderBy(c => c.Guid.Counter)];
        Assert.Equal(2, wolves.Length);
        Creature first = wolves[0];
        Creature second = wolves[1];

        map.Combat.DealDamage(player, first, first.MaxHealth, direct: false);
        Assert.Equal(CreatureDeathState.Corpse, first.DeathState);
        Assert.NotNull(loot.FindLoot(first.Guid));

        // Advance past the 1-second corpse decay timer: CorpseRemoving forgets the corpse loot immediately.
        world.RunTick(1000);
        Assert.Equal(CreatureDeathState.Dead, first.DeathState);
        Assert.Null(loot.FindLoot(first.Guid));

        // Killing a second creature runs Prune(), which finds zero stale bags and logs no "pruned ... stale loot entries" message.
        map.Combat.DealDamage(player, second, second.MaxHealth, direct: false);
        Assert.NotNull(loot.FindLoot(second.Guid));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("pruned", StringComparison.OrdinalIgnoreCase));

        // ForceRespawn while the second corpse's loot is open closes the loot window and forgets the bag immediately.
        Assert.Equal(LootResult.Ok, loot.Open(player, second.Guid));
        Assert.NotNull(loot.OpenLootOf(player));
        creatures.ForceRespawn(second);
        Assert.Null(loot.FindLoot(second.Guid));
        Assert.Null(loot.OpenLootOf(player));
    }

    [Fact]
    public void NonTransportUnscriptedObjectChurn_DoesNotRebuildAiList_AndDespawnTimersWaitUntilDue()
    {
        var content = new GameObjectContent(
            [GoTemplate(DoorEntry, GameObjectType.Door), GoTemplate(ChestEntry, GameObjectType.Chest, (1, ChestLoot))],
            [GoSpawn(1, DoorEntry, 2, 0), GoSpawn(2, DoorEntry, 4, 0)],
            [],
            [],
            []);
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var fallback = new CountingFallbackAi();
        var system = new GameObjectMapSystem(map, content) { FallbackAi = fallback };
        map.AddUpdater(system);

        (Player player, _) = Player(1);
        world.AddPlayer(player);
        world.RunTick(50);
        int initialHandlesCalls = fallback.HandlesCalls;
        Assert.Equal(2, initialHandlesCalls);

        // Summoning and removing unscripted non-transport objects checks only the new object and does not rescan all map objects.
        GameObject temp = system.Summon(ChestEntry, 3, 0, 83.5f, 0, despawnAfterSeconds: 2)!;
        Assert.Equal(initialHandlesCalls + 1, fallback.HandlesCalls);

        world.RunTick(500);
        Assert.Equal(initialHandlesCalls + 1, fallback.HandlesCalls);
        Assert.True(temp.IsSpawned);

        world.RunTick(1000);
        Assert.True(temp.IsSpawned);

        world.RunTick(500);
        Assert.False(temp.IsSpawned);
        Assert.Null(system.Find(temp.Guid));
        Assert.Equal(initialHandlesCalls + 2, fallback.HandlesCalls);
    }

    private sealed class CountingFallbackAi : IGameObjectFallbackAi
    {
        private readonly object _key = new();

        public int HandlesCalls { get; private set; }

        public bool HandlesAny => true;

        public object? HandlesKey => _key;

        public bool Handles(GameObject go)
        {
            HandlesCalls++;
            return false;
        }

        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
        {
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
