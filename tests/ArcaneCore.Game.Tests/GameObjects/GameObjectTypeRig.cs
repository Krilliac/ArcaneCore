using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>A recording <see cref="IGameObjectSpells"/>: every object spell, ritual spell and cooldown it was asked for.</summary>
internal sealed class FakeObjectSpells : IGameObjectSpells
{
    public List<(GameObject Source, uint Spell, Unit Target, Unit? Caster)> Casts { get; } = [];

    public List<(GameObject Ritual, uint Spell, Unit Caster, ObjectGuid Target)> RitualCasts { get; } = [];

    public List<(Player Helper, uint Spell, GameObject Ritual)> Animations { get; } = [];

    public List<(Player Owner, uint Spell)> Cooldowns { get; } = [];

    public HashSet<ObjectGuid> Channeling { get; } = [];

    public Dictionary<uint, float> Ranges { get; } = [];

    public bool RitualSpellSucceeds { get; set; } = true;

    public bool Cast(GameObject source, uint spellId, Unit target, Unit? unitCaster)
    {
        Casts.Add((source, spellId, target, unitCaster));
        return true;
    }

    public float? MaxRange(uint spellId) => Ranges.TryGetValue(spellId, out float range) ? range : null;

    public bool IsChanneling(Unit unit) => Channeling.Contains(unit.Guid);

    public void StartRitualAnimation(Player helper, uint animSpellId, GameObject ritual) => Animations.Add((helper, animSpellId, ritual));

    public bool CastRitualSpell(GameObject ritual, uint spellId, Unit caster, ObjectGuid summonTarget)
    {
        RitualCasts.Add((ritual, spellId, caster, summonTarget));
        return RitualSpellSucceeds;
    }

    public void StartCreatingSpellCooldown(Player owner, uint spellId) => Cooldowns.Add((owner, spellId));
}

internal sealed class FakeObjectGossip : IGameObjectGossip
{
    public List<(Player Player, GameObject Go, uint Menu)> Opened { get; } = [];

    public bool OpenGossip(Player player, GameObject go, uint menuId)
    {
        Opened.Add((player, go, menuId));
        return true;
    }
}

internal sealed class FakeFlagStands : IGameObjectFlagStands
{
    public bool InBattleground { get; set; } = true;

    public List<(Player Player, GameObject Flag)> Clicks { get; } = [];

    public List<Player> StealthBroken { get; } = [];

    public bool CanUseBattlegroundObject(Player player) => InBattleground;

    public bool OnFlagClicked(Player player, GameObject flag)
    {
        Clicks.Add((player, flag));
        return true;
    }

    public void BreakStealthAndInvisibility(Player player) => StealthBroken.Add(player);
}

/// <summary>A <see cref="Random"/> whose doubles are fixed (the vein's next-open roll).</summary>
internal sealed class FixedRandom(double value) : Random
{
    public override double NextDouble() => value;

    protected override double Sample() => value;
}

/// <summary>
/// A map for the type behaviours (gameobject types lane): chests with a level, a restock time, a key lock and a linked trap; a mineral vein; goobers
/// with gossip, a spell and questId -1; spell casters; an environmental trap; an area damage object; a flag stand; rituals. Wired to fakes for
/// spells, gossip and battlegrounds.
/// </summary>
internal sealed class GameObjectTypeRig
{
    public const uint LevelChest = 301;
    public const uint RestockChest = 302;
    public const uint KeyChest = 303;
    public const uint DoorKeyChest = 304;
    public const uint Vein = 305;
    public const uint TrappedChest = 306;
    public const uint ChestTrap = 307;
    public const uint GossipGoober = 308;
    public const uint SpellGoober = 309;
    public const uint EveryoneGoober = 310;
    public const uint Portal = 311;
    public const uint PartyLightwell = 312;
    public const uint FireTrap = 313;
    public const uint FirePit = 314;
    public const uint FlagStand = 315;
    public const uint Ritual = GameObjectMapSystem.PlayerSummoningRitualEntry; // 36727, the warlock Summoning Portal
    public const uint WildRitual = 317;
    public const uint GroupedRitual = 318;
    public const uint TrappedButton = 319;
    public const uint CooldownTrap = 320;
    public const uint CooldownTrapButton = 321;
    public const uint EventChest = 322;      // a key-locked chest with a chest.eventId (data6), as Trelane's and Benedict's chests
    public const uint QuestEventChest = 323; // a lockless chest with an event and a chest.questId (data8)
    public const uint EventGoober = 324;      // a goober with a goober.eventId (data2) and no auto-close time
    public const uint InUseEventGoober = 325; // the same with a 5 s auto-close time (data3), in use until it runs out
    public const uint ChestEvent = 9043;
    public const uint ChestQuest = 696;

    public const uint ChestLoot = 700;
    public const uint ExpendableKey = 3467;   // Dull Iron Key: spell 3366, charges -1 (classic-db item_template)
    public const uint PlainKey = 4103;        // Shackle Key: no spell
    public const uint ExpendableKeyLock = 40;
    public const uint PlainKeyLock = 41;
    public const uint VeinLock = 42;
    public const uint TrapSpell = 7001;
    public const uint GooberSpell = 7002;
    public const uint PortalSpell = 17334;
    public const uint LightwellSpell = 7003;
    public const uint RitualSpell = 7720;
    public const uint RitualAnim = 7004;
    public const uint SacrificeSpell = 20625;
    public const uint GossipMenu = 5001;

    private static readonly LockEntry[] Locks =
    [
        new(ExpendableKeyLock, [1, 0, 0, 0, 0, 0, 0, 0], [ExpendableKey, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0]),
        new(PlainKeyLock, [1, 0, 0, 0, 0, 0, 0, 0], [PlainKey, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0]),
        new(VeinLock, [2, 0, 0, 0, 0, 0, 0, 0], [(uint)LockType.Mining, 0, 0, 0, 0, 0, 0, 0], [175, 0, 0, 0, 0, 0, 0, 0]),
    ];

    public static ItemTemplateStore Items { get; } = new(
    [
        .. ItemStore.All,
        new ItemTemplate { Entry = ExpendableKey, Class = 13, Name = "Dull Iron Key", DisplayId = 6714, Spells = [new ItemSpell(3366, 0, -1, 0, -1, 0, -1)] },
        new ItemTemplate { Entry = PlainKey, Class = 13, Name = "Shackle Key", DisplayId = 6708 },
    ], ItemTestData.StartingItems);

    private GameObjectTypeRig(WorldRuntime world, Map map, GameObjectMapSystem system, FakeQuestJournal quests)
    {
        World = world;
        Map = map;
        System = system;
        Quests = quests;
    }

    public WorldRuntime World { get; }

    public Map Map { get; }

    public GameObjectMapSystem System { get; }

    public FakeQuestJournal Quests { get; }

    public FakeObjectSpells Spells { get; } = new();

    public FakeObjectGossip Gossip { get; } = new();

    public FakeFlagStands FlagStands { get; } = new();

    /// <summary>Raid membership the system asks for (unordered pairs).</summary>
    public HashSet<(ObjectGuid, ObjectGuid)> Raids { get; } = [];

    public Dictionary<ObjectGuid, uint> GroupIds { get; } = [];

    public static GameObjectTypeRig Create(IEnumerable<GameObjectSpawn> spawns)
    {
        GameObjectTemplate[] templates =
        [
            GoTemplate(LevelChest, GameObjectType.Chest, (1, ChestLoot), (9, 30)),
            GoTemplate(RestockChest, GameObjectType.Chest, (1, ChestLoot), (2, 30)),
            GoTemplate(KeyChest, GameObjectType.Chest, (0, ExpendableKeyLock), (1, ChestLoot)),
            GoTemplate(DoorKeyChest, GameObjectType.Chest, (0, PlainKeyLock), (1, ChestLoot)),
            GoTemplate(Vein, GameObjectType.Chest, (0, VeinLock), (1, ChestLoot), (3, 1), (4, 2), (5, 3)),
            GoTemplate(TrappedChest, GameObjectType.Chest, (1, ChestLoot), (7, ChestTrap)),
            GoTemplate(ChestTrap, GameObjectType.Trap, (3, TrapSpell), (4, 1)),
            GoTemplate(GossipGoober, GameObjectType.Goober, (19, GossipMenu)),
            GoTemplate(SpellGoober, GameObjectType.Goober, (10, GooberSpell), (7, 33)),
            GoTemplate(EveryoneGoober, GameObjectType.Goober, (1, uint.MaxValue)),
            GoTemplate(Portal, GameObjectType.SpellCaster, (0, PortalSpell)),
            GoTemplate(PartyLightwell, GameObjectType.SpellCaster, (0, LightwellSpell), (1, 2), (2, 1)),
            GoTemplate(FireTrap, GameObjectType.Trap, (2, 4), (3, TrapSpell), (5, 2), (7, 1)),
            GoTemplate(FirePit, GameObjectType.AreaDamage, (1, 5), (2, 10), (3, 10), (4, 2), (5, 2 * 65536)),
            GoTemplate(FlagStand, GameObjectType.FlagStand),
            GoTemplate(Ritual, GameObjectType.SummoningRitual, (0, 3), (1, RitualSpell), (6, 1)),
            GoTemplate(WildRitual, GameObjectType.SummoningRitual, (0, 2), (1, RitualSpell), (2, RitualAnim), (4, SacrificeSpell)),
            GoTemplate(GroupedRitual, GameObjectType.SummoningRitual, (0, 2), (1, RitualSpell), (6, 1)),
            GoTemplate(TrappedButton, GameObjectType.Button, (3, ChestTrap)),
            GoTemplate(CooldownTrap, GameObjectType.Trap, (3, TrapSpell), (5, 5)),
            GoTemplate(CooldownTrapButton, GameObjectType.Button, (3, CooldownTrap)),
            GoTemplate(EventChest, GameObjectType.Chest, (0, PlainKeyLock), (1, ChestLoot), (6, ChestEvent)),
            GoTemplate(QuestEventChest, GameObjectType.Chest, (1, ChestLoot), (6, ChestEvent), (8, ChestQuest)),
            GoTemplate(EventGoober, GameObjectType.Goober, (2, ChestEvent)),
            GoTemplate(InUseEventGoober, GameObjectType.Goober, (2, ChestEvent), (3, 5 * 0x10000)),
        ];
        var content = new GameObjectContent(templates, spawns, Locks, [], []);
        var lootContent = new LootContent([(LootTableKind.GameObject, Row(ChestLoot, Hide, 100))], []);
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var quests = new FakeQuestJournal();
        var loot = new LootService(lootContent, random: new Random(3)) { Items = Items, Quests = quests };
        var system = new GameObjectMapSystem(map, content, loot, quests);
        map.AddUpdater(system);
        var rig = new GameObjectTypeRig(world, map, system, quests);
        system.Spells = rig.Spells;
        system.Gossip = rig.Gossip;
        system.FlagStands = rig.FlagStands;
        system.SameRaid = (a, b) => rig.Raids.Contains((a.Guid, b.Guid)) || rig.Raids.Contains((b.Guid, a.Guid));
        system.GroupIdOf = player => rig.GroupIds.GetValueOrDefault(player.Guid);
        return rig;
    }

    public (Player Player, FakeSession Session) Join(uint guid, float x = 0, float y = 0, byte level = 10)
    {
        (Player player, FakeSession session) = Player(guid, x, y);
        player.Inventory.Templates = Items;
        player.Level = level;
        World.AddPlayer(player);
        World.RunTick(50);
        return (player, session);
    }

    public GameObject Single(uint entry) => System.GameObjects.Single(g => g.Entry == entry);

    /// <summary>Advance the map in one-second steps (the object timers read whole seconds).</summary>
    public void Seconds(int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            World.RunTick(1000);
        }
    }

    /// <summary>Open, take everything and close a chest: the release that settles it.</summary>
    public void LootOut(Player player, GameObject chest)
    {
        LootBag bag = chest.Loot ?? throw new InvalidOperationException("the chest has no loot window");
        foreach (LootItem item in bag.Items.ToArray())
        {
            System.Loot!.TakeItem(player, item.Slot);
        }

        System.Loot!.Release(player, chest.Guid);
    }
}
