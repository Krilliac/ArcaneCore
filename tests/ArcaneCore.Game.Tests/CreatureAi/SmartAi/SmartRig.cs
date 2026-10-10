using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi.SmartAi;

/// <summary>Row builders for the slice-2 smart-script tests (raw byte ids, so a test can state an id the engine enums do not define).</summary>
internal static class SmartRows
{
    public static SmartScriptRow Make(int entryOrGuid, byte source, ushort id, byte ev, byte action, byte target = 1,
        uint e1 = 0, uint e2 = 0, uint e3 = 0, uint e4 = 0, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint a4 = 0, uint a5 = 0, uint a6 = 0,
        uint t1 = 0, uint t2 = 0, uint t3 = 0, uint t4 = 0, uint condition = 0, ushort link = 0, uint flags = 0, uint phaseMask = 0)
        => new()
        {
            EntryOrGuid = entryOrGuid, SourceType = source, Id = id, Link = link, EventType = ev, EventPhaseMask = phaseMask, EventFlags = flags,
            EventParam1 = e1, EventParam2 = e2, EventParam3 = e3, EventParam4 = e4,
            ActionType = action, ActionParam1 = a1, ActionParam2 = a2, ActionParam3 = a3, ActionParam4 = a4, ActionParam5 = a5, ActionParam6 = a6,
            TargetType = target, TargetParam1 = t1, TargetParam2 = t2, TargetParam3 = t3, TargetParam4 = t4, ConditionId = condition,
        };

    public static SmartScriptRow CreatureRow(int entryOrGuid, ushort id, SmartEvent ev, SmartAction action, SmartTarget target = SmartTarget.Self,
        uint e1 = 0, uint e2 = 0, uint e3 = 0, uint e4 = 0, uint a1 = 0, uint a2 = 0, uint a3 = 0,
        uint t1 = 0, uint t2 = 0, uint t3 = 0, uint condition = 0, ushort link = 0)
        => Make(entryOrGuid, 0, id, (byte)ev, (byte)action, (byte)target, e1, e2, e3, e4, a1, a2, a3, t1: t1, t2: t2, t3: t3, condition: condition, link: link);

    /// <summary>A creature template entry (always positive); a negative entryOrGuid is a spawn guid and goes through the int overload.</summary>
    public static SmartScriptRow CreatureRow(uint entry, ushort id, SmartEvent ev, SmartAction action, SmartTarget target = SmartTarget.Self,
        uint e1 = 0, uint e2 = 0, uint e3 = 0, uint e4 = 0, uint a1 = 0, uint a2 = 0, uint a3 = 0,
        uint t1 = 0, uint t2 = 0, uint t3 = 0, uint condition = 0, ushort link = 0)
        => CreatureRow(checked((int)entry), id, ev, action, target, e1, e2, e3, e4, a1, a2, a3, t1, t2, t3, condition, link);

    public static SmartScriptRow ObjectRow(int entryOrGuid, ushort id, SmartEvent ev, SmartAction action, SmartTarget target = SmartTarget.Self,
        uint e1 = 0, uint e2 = 0, uint e3 = 0, uint e4 = 0, uint a1 = 0, uint a2 = 0, uint a3 = 0,
        uint t1 = 0, uint t2 = 0, uint t3 = 0, uint condition = 0, ushort link = 0)
        => Make(entryOrGuid, 1, id, (byte)ev, (byte)action, (byte)target, e1, e2, e3, e4, a1, a2, a3, t1: t1, t2: t2, t3: t3, condition: condition, link: link);

    public static SmartScriptRow TriggerRow(int triggerId, ushort id, SmartAction action, SmartTarget target, uint e1 = 0,
        uint a1 = 0, uint a2 = 0, uint a3 = 0, uint t1 = 0, uint t2 = 0, uint t3 = 0)
        => Make(triggerId, 2, id, (byte)SmartEvent.AreaTriggerOnTrigger, (byte)action, (byte)target, e1, a1: a1, a2: a2, a3: a3, t1: t1, t2: t2, t3: t3);

    /// <summary>A timed action list row: e1/e2 the delay range before it runs, e3/e4 the repeat range after it ran.</summary>
    public static SmartScriptRow ListRow(int listId, ushort id, SmartAction action, SmartTarget target = SmartTarget.Self,
        uint delay = 0, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint t1 = 0, uint t2 = 0, uint t3 = 0, uint condition = 0)
        => Make(listId, 9, id, 0, (byte)action, (byte)target, delay, delay, a1: a1, a2: a2, a3: a3, t1: t1, t2: t2, t3: t3, condition: condition);
}

/// <summary>The world's shape: ConditionFeature forwards to the table evaluator it holds, it is not one itself (as TerminateCondMapConditionTests).</summary>
internal sealed class ForwardingConditions(ConditionEvaluator inner) : IConditionEvaluator, IConditionTableEvaluator
{
    public ConditionEvaluator Current => inner;

    public bool IsSatisfied(uint conditionId, Player player, NpcInfo? source) => inner.IsSatisfied(conditionId, player, source);
}

/// <summary>
/// A map with the smart wolf (299, spawn 1 at 5,0), a player at the origin and, on request, more smart creatures, a speaker and game objects, wired
/// to a recording spell seam. The cmangos <c>conditions</c> rows below are written by hand from the type numbers of Conditions.cpp (299-308, 424-466,
/// 484-489), the same rows TerminateCondMapConditionTests uses for the DB-script path.
/// </summary>
internal sealed class SmartRig : IDisposable
{
    public const uint Grark = 8400;
    public const uint Demetria = 12339;
    public const uint Other = 301;
    public const uint Speaker = 302;
    public const uint ObjectEntry = 50000;
    public const uint ObjectEntry2 = 50001;

    /// <summary>A goober whose noDamageImmune column (data11) is set, so a spawned-by-default spawn of it can despawn and respawn (GameObject.cpp:985-991).</summary>
    public const uint ObjectEntryDespawning = 50002;
    public const uint CountSpawnsFlag = 0x00200000;

    /// <summary>317 player dead or beyond 60 of the source; 318 the creature source dead; 606 Demetria spawned; 1380 Grark within 80 of the target; NOT rows 1383, 1317, 1318.</summary>
    public static readonly ConditionRecord[] Conditions =
    [
        new(317, 36, 0, 60, 0, 0, 0),
        new(318, 36, 3, 0, 0, 0, 0),
        new(606, 39, Demetria, 1, 0, 0, 0),
        new(1380, 37, Grark, 80, 0, 0, 0),
        new(1383, -3, 1380, 0, 0, 0, 0),
        new(1317, -3, 317, 0, 0, 0, 0),
        new(1318, -3, 318, 0, 0, 0, 0),
    ];

    private SmartRig(WorldRuntime world, Map map, CreatureMapSystem creatures, GameObjectMapSystem? objects, FakeCaster spells, FakeObjectSpells objectSpells,
        Player player, FakeSession session, SmartScriptCatalog catalog)
    {
        World = world;
        Map = map;
        Creatures = creatures;
        Objects = objects;
        Spells = spells;
        ObjectSpells = objectSpells;
        Player = player;
        Session = session;
        Catalog = catalog;
    }

    public WorldRuntime World { get; }

    public Map Map { get; }

    public CreatureMapSystem Creatures { get; }

    public GameObjectMapSystem? Objects { get; }

    public FakeCaster Spells { get; }

    public FakeObjectSpells ObjectSpells { get; }

    public Player Player { get; }

    public FakeSession Session { get; }

    public SmartScriptCatalog Catalog { get; }

    public Creature Wolf => Creatures.Creatures.Single(c => c.Entry == WolfEntry);

    public CreatureSmartAI WolfAi => AiOf(Wolf);

    public SmartScript Script => WolfAi.Script;

    public static CreatureSmartAI AiOf(Creature creature) => Assert.IsType<CreatureSmartAI>(creature.AI);

    /// <param name="rows">The smart_scripts rows; the catalog is built without references.</param>
    /// <param name="conditions">Wire the table evaluator over <see cref="Conditions"/>; false leaves the creature AI services without one.</param>
    /// <param name="creatureSpawns">Further creature spawns (entries <see cref="Other"/> smart and friendly, <see cref="Speaker"/> plain and friendly, <see cref="Grark"/>).</param>
    /// <param name="objectSpawns">Game object spawns of the goober templates <see cref="ObjectEntry"/> and <see cref="ObjectEntry2"/>; null builds no object system.</param>
    public static SmartRig Start(IEnumerable<SmartScriptRow> rows, bool conditions = false, IEnumerable<CreatureSpawn>? creatureSpawns = null,
        IEnumerable<GameObjectSpawn>? objectSpawns = null, float playerX = 0)
    {
        var catalog = new SmartScriptCatalog(rows);
        var content = new CreatureContent(
            [
                Template(configure: t => t.AIName = CreatureAiFactory.SmartAIName),
                Template(Other, t => { t.AIName = CreatureAiFactory.SmartAIName; t.Faction = 35; }),
                Template(Speaker, t => { t.Faction = 35; t.Name = "Speaker"; }),
                Template(Grark, t => { t.Faction = 35; t.Name = "Grark"; }),
                Template(Demetria, t => { t.Faction = 35; t.ExtraFlags = CountSpawnsFlag; }),
            ],
            [Spawn(1, WolfEntry, 5, 0), .. creatureSpawns ?? []], [], [], [],
            new CreatureAiContent([], [], new BroadcastTextCatalog([new BroadcastText(9001, "Grr, $N!", "", 0, 0, 0, [0, 0, 0], [0, 0, 0])]),
                [], EventAiDialect.CMangos, [])
            { SmartScripts = catalog });
        var spells = new FakeCaster();
        var services = new CreatureAiServices { Spells = spells };
        if (conditions)
        {
            services = new CreatureAiServices
            {
                Spells = spells,
                Conditions = new ForwardingConditions(new ConditionEvaluator(ConditionTable.Build(Conditions), new ConditionContext())),
            };
        }

        (WorldRuntime world, Map map, CreatureMapSystem creatures) = CreateAiSystem(content, services);
        var objectSpells = new FakeObjectSpells();
        GameObjectMapSystem? objects = null;
        if (objectSpawns is not null)
        {
            objects = new GameObjectMapSystem(map, new GameObjectContent(
                [GameObjectTestKit.GoTemplate(ObjectEntry, GameObjectType.Goober), GameObjectTestKit.GoTemplate(ObjectEntry2, GameObjectType.Goober),
                    GameObjectTestKit.GoTemplate(ObjectEntryDespawning, GameObjectType.Goober, (11, 1u))],
                objectSpawns, [], [], []))
            {
                Spells = objectSpells,
                Random = new Random(1),
                FallbackAi = new SmartGameObjectAi(() => catalog),
            };
            map.AddUpdater(objects);
        }

        (Player player, FakeSession session) = AddPlayer(world, 1, playerX, 0);

        // AddPlayer runs a zero-length tick, on which an UPDATE event with no delay already fires; start every test from a clean record.
        objectSpells.Casts.Clear();
        return new SmartRig(world, map, creatures, objects, spells, objectSpells, player, session, catalog);
    }

    public GameObject ObjectBySpawn(uint spawnGuid) => Objects!.GameObjects.Single(g => g.Spawn?.Guid == spawnGuid);

    public SmartScript ObjectScript(GameObject go)
        => Assert.IsType<SmartGameObjectAi>(Objects!.AiFor(go)).ScriptFor(Objects, go);

    public void Dispose() => World.Dispose();
}
