using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>A map with chests, goobers, a mailbox and a quest herb, wired to its game object and loot systems (use rules and lifecycle tests).</summary>
internal sealed class GameObjectUseRig
{
    public const uint ChestEntry = 201;
    public const uint GooberEntry = 202;
    public const uint AnimGooberEntry = 203;
    public const uint ConsumableGooberEntry = 204;
    public const uint MailboxEntry = 205;
    public const uint QuestHerbEntry = 206;
    public const uint ChestLoot = 600;
    public const uint QuestId = 88;
    public const uint HerbLock = 20;

    private static readonly LockEntry[] Locks =
    [
        new(HerbLock, [2, 0, 0, 0, 0, 0, 0, 0], [(uint)LockType.Herbalism, 0, 0, 0, 0, 0, 0, 0], [1, 0, 0, 0, 0, 0, 0, 0]),
    ];

    private GameObjectUseRig(WorldRuntime world, Map map, GameObjectMapSystem system, LootService loot, FakeQuestJournal quests)
    {
        World = world;
        Map = map;
        System = system;
        Loot = loot;
        Quests = quests;
    }

    public WorldRuntime World { get; }

    public Map Map { get; }

    public GameObjectMapSystem System { get; }

    public LootService Loot { get; }

    public FakeQuestJournal Quests { get; }

    public static GameObjectUseRig Create(IEnumerable<GameObjectSpawn> spawns)
    {
        GameObjectTemplate[] templates =
        [
            GoTemplate(ChestEntry, GameObjectType.Chest, (1, ChestLoot)),
            GoTemplate(GooberEntry, GameObjectType.Goober),                                           // no auto-close, no custom anim
            GoTemplate(AnimGooberEntry, GameObjectType.Goober, (3, 3 * 65536), (4, 1)),               // auto-close 3 s, custom anim flag
            GoTemplate(ConsumableGooberEntry, GameObjectType.Goober, (3, 3 * 65536), (5, 1)),         // auto-close 3 s, consumable
            GoTemplate(MailboxEntry, GameObjectType.Mailbox),
            GoTemplate(QuestHerbEntry, GameObjectType.Chest, (0, HerbLock), (1, ChestLoot), (8, QuestId)),
        ];
        var content = new GameObjectContent(templates, spawns, Locks, [], []);
        var lootContent = new LootContent(
        [
            (LootTableKind.GameObject, Row(ChestLoot, ItemTestData.ToughJerky, 100)),
            (LootTableKind.GameObject, Row(ChestLoot, Hide, 100)),
        ], []);
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var quests = new FakeQuestJournal();
        var service = new LootService(lootContent, random: new Random(3)) { Items = ItemStore, Quests = quests };
        var system = new GameObjectMapSystem(map, content, service, quests);
        map.AddUpdater(system);
        return new GameObjectUseRig(world, map, system, service, quests);
    }

    public (Player Player, FakeSession Session) Join(uint guid, float x = 0, float y = 0)
    {
        (Player player, FakeSession session) = Player(guid, x, y);
        World.AddPlayer(player);
        World.RunTick(50);
        return (player, session);
    }

    public GameObject Single(uint entry) => System.GameObjects.Single(g => g.Entry == entry);

    /// <summary>Advance the map in one-second steps (the goober and chest timers read whole seconds).</summary>
    public void Seconds(int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            World.RunTick(1000);
        }
    }
}
