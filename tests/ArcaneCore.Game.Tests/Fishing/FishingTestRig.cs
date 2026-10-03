using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Fishing;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Game.Tests.Skills;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.Fishing;

/// <summary>Water that is exactly where the test says it is.</summary>
internal sealed class FakeTerrain : IFishingTerrain
{
    public bool Water { get; set; } = true;

    public float Level { get; set; } = 80.0f;

    public float Depth { get; set; } = -30.0f;

    public int Calls { get; private set; }

    public bool IsSwimmable(Map map, float x, float y, float z, float radius, out LiquidData data)
    {
        Calls++;
        data = Water ? new LiquidData(1, LiquidTypeFlags.Water, Level, Depth) : default;
        return Water && Level - Depth > radius;
    }
}

/// <summary>An index-picking random: <c>Next(n)</c> returns the configured index, everything else the lowest value.</summary>
internal sealed class PickRandom(int index) : Random
{
    public int Index { get; set; } = index;

    public override int Next(int maxValue) => Math.Min(Index, maxValue - 1);

    public override int Next(int minValue, int maxValue) => minValue;

    public override float NextSingle() => 0.0f;
}

/// <summary>A map, a spell system, a game object system, loot and fishing wired the way the world daemon wires them.</summary>
internal sealed class FishingRig : IDisposable
{
    public const uint BobberEntry = 35591;
    public const uint FishingSpell = 7620;
    public const uint HoleEntry = 9001;
    public const uint HoleLoot = 7001;
    public const uint SubZone = 10;
    public const uint Zone = 1;
    public const uint ZoneFish = Hide;
    public const uint SubZoneFish = ArcaneCore.Game.Tests.ItemTestData.ToughJerky;
    public const uint JunkFish = Lockbox;
    public const uint HoleFish = LockedBox;

    public FishingRig(
        IEnumerable<(LootTableKind, LootStoreRow)>? lootRows = null,
        IEnumerable<KeyValuePair<uint, int>>? baseSkills = null,
        IEnumerable<GameObjectSpawn>? spawns = null,
        FishingOptions? options = null,
        int skill = 150,
        float orientation = 0.0f,
        uint durationMs = 20000,
        SpellEffectInfo? effect = null)
    {
        SpellInfo spell = SpellTestKit.Spell(FishingSpell, effect ?? SpellTestKit.Effect(SpellEffectName.TransDoor, 0, SpellImplicitTarget.LocationCasterFishingSpot, misc: (int)BobberEntry) with { Radius = 15.0f }) with
        {
            AttributesEx = SpellAttributesEx.IsChanneled,
            Duration = new SpellDuration((int)durationMs, 0, (int)durationMs),
            ChannelInterruptFlags = SpellAuraInterruptFlags.Moving,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        Kit = new SpellTestKit(spell);
        Kit.System.Random = Pick;
        Map = Kit.World.GetMap(0);
        var content = new GameObjectContent(
            [
                GoTemplate(BobberEntry, GameObjectType.FishingNode),
                GoTemplate(HoleEntry, GameObjectType.FishingHole, (0, 12), (1, HoleLoot), (2, 2), (3, 2)),
            ],
            spawns ?? [], [], [], []);
        var loot = new LootContent(
            lootRows ??
            [
                (LootTableKind.Fishing, Row(SubZone, SubZoneFish, 100)),
                (LootTableKind.Fishing, Row(Zone, ZoneFish, 100)),
                (LootTableKind.Fishing, Row(0, JunkFish, 100)),
                (LootTableKind.GameObject, Row(HoleLoot, HoleFish, 100)),
            ],
            [],
            baseSkills ?? [new KeyValuePair<uint, int>(SubZone, 55), new KeyValuePair<uint, int>(Zone, 130)],
            []);
        Loot = new LootService(loot, random: new Random(11)) { Items = ItemStore };
        Objects = new GameObjectMapSystem(Map, content, Loot);
        Map.AddUpdater(Objects);
        Fishing = new FishingService(Map, Objects, options ?? new FishingOptions(), () => Kit.System, new Random(5), Terrain) { AreaOf = (_, _, _) => (Zone, SubZone) };
        Map.AddUpdater(Fishing);
        Objects.RegisterUseHandler(GameObjectType.FishingNode, Fishing.UseBobber);
        new FishingSpells(() => [Fishing]).Register(Kit.System);

        (Player, Session) = Kit.AddPlayer(1, 0, 0);
        Player.Orientation = orientation;
        Player.Inventory.Templates = ItemStore;
        Player.Inventory.GuidAllocator = new ItemGuidAllocator();
        Player.Inventory.Load([]);
        SkillRandom = new ScriptedSkillRandom();
        Skills = new PlayerSkills(Player, SkillTestKit.Catalog(), new SkillOptions(), new FakeSkillSpellHost { Cascade = Player }, SkillRandom);
        Player.AttachSkills(Skills);
        Skills.Set(SkillIds.Fishing, (ushort)skill, 300, 1);
        Objects.SkillValue = (player, id) => player.Skills!.GetValue(id);
        Kit.Spellbook.Teach(Player, FishingSpell);
        Kit.World.RunTick(50);
        Session.Clear();
    }

    public SpellTestKit Kit { get; }

    public Map Map { get; }

    public GameObjectMapSystem Objects { get; }

    public LootService Loot { get; }

    public FishingService Fishing { get; }

    public FakeTerrain Terrain { get; } = new();

    public PickRandom Pick { get; } = new(0);

    public Player Player { get; }

    public FakeSession Session { get; }

    public PlayerSkills Skills { get; }

    public ScriptedSkillRandom SkillRandom { get; }

    public GameObject? Bobber => Objects.GameObjects.SingleOrDefault(g => g.Type == GameObjectType.FishingNode);

    public SpellCastResult Cast() => Kit.System.HandleCastRequest(Player, FishingSpell, SpellCastTargets.ForSelf());

    /// <summary>Advance the spell clock and the map in 50 ms steps.</summary>
    public void Step(uint ms)
    {
        for (uint done = 0; done < ms; done += 50)
        {
            Kit.Advance(50, 50);
            Kit.World.RunTick(50);
        }
    }

    public void Dispose() => Kit.Dispose();
}