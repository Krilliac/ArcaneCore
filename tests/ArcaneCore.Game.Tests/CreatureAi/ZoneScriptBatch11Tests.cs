using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts.World;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// Zone script batch 11: Maws and the Arcanite Buoy (azshara.cpp) and the Blackwood stores (darkshore.cpp go_furbolg_food), against a real map with a
/// recording caster and quest service. Random draws come from the map's seeded Random(1).
/// </summary>
public sealed class ZoneScriptBatch11Tests
{
    private const float Z = 83.5f;
    private const uint HugeHealth = 1_000_000;

    private sealed class RecordingCaster : ICreatureSpellCaster
    {
        public List<(Creature Caster, uint Spell, Unit? Target)> Casts { get; } = [];

        public event Action<Unit, Unit, SpellInfo>? SpellHit
        {
            add { }
            remove { }
        }

        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
        {
            Casts.Add((caster, spellId, target));
            return CreatureCastResult.Ok;
        }

        public bool IsCasting(Creature caster) => false;

        public HashSet<(Unit Unit, uint Spell)> Auras { get; } = [];

        public List<(Unit Unit, uint Spell)> Removed { get; } = [];

        public bool HasAura(Unit unit, uint spellId) => Auras.Contains((unit, spellId));

        public void RemoveAuras(Unit unit, uint spellId)
        {
            Removed.Add((unit, spellId));
            Auras.Remove((unit, spellId));
        }

        public void Interrupt(Creature caster)
        {
        }

        public void OnCreatureRemoved(Creature creature)
        {
        }
    }

    private sealed class Quests : IScriptQuestEvents, IEventAiQuestEvents
    {
        public HashSet<uint> Incomplete { get; } = [];

        public List<(Player Player, uint QuestId)> Completed { get; } = [];

        public bool IsQuestIncomplete(Player player, uint questId) => Incomplete.Contains(questId);

        public void AreaExploredOrEventHappens(Player player, uint questId) => Completed.Add((player, questId));

        public void FailQuest(Player player, uint questId)
        {
        }

        public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source)
        {
        }

        public void GroupEventFailHappens(Player player, uint questId)
        {
        }

        public IReadOnlyList<Player> GroupMembersOf(Player player) => [];

        public void EventHappened(Player player, uint questId, Creature source, bool rewardGroup)
        {
            Assert.True(rewardGroup);
            Completed.Add((player, questId));
        }

        public void KillCredit(Player player, uint creatureEntry, Creature source)
        {
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig(uint entry, string name, int[]? textIds = null, CreatureTemplate[]? extraTemplates = null, CreatureSpawn[]? extraSpawns = null,
            IEnumerable<(uint Entry, uint PathId, CreatureWaypoint Point)>? scriptWaypoints = null, uint faction = 35, float x = 0f, float y = 0f, float z = Z,
            IEnumerable<(uint Entry, uint PathId, CreatureWaypoint Point)>? entryWaypoints = null)
        {
            var aiContent = new CreatureAiContent([], [.. (textIds ?? []).Where(id => id < 0).Select(id => new CreatureAiText(id, $"text{id}", 0, 0, 0))],
                new BroadcastTextCatalog((textIds ?? []).Where(id => id > 0).Select(id =>
                    new BroadcastText((uint)id, $"text{id}", string.Empty, 0, 0, 0, [0, 0, 0], [0, 0, 0]))));
            CreatureContent content = new([Template(entry, Tpl(name, faction)), .. extraTemplates ?? []],
                [Spawn(1, entry, x, y, z), .. extraSpawns ?? []], [], [], [], aiContent, entryWaypoints: entryWaypoints, scriptWaypoints: scriptWaypoints);
            var services = new CreatureAiServices { ScriptQuests = Quest, QuestEvents = Quest, Spells = Caster };
            (World, Map, System) = CreateAiSystem(content, services);
            (Player, Session) = AddPlayer(World, 1, x + 1f, y);
            Player.Relocate(x + 1f, y, z, 0, 0);
            Player.MaxHealth = HugeHealth;
            Player.Health = HugeHealth;
            Npc = Assert.Single(System.Creatures, c => c.Template.Entry == entry);
        }

        public RecordingCaster Caster { get; } = new();

        public Quests Quest { get; } = new();

        public WorldRuntime World { get; }

        public Map Map { get; }

        public CreatureMapSystem System { get; }

        public Player Player { get; }

        public FakeSession Session { get; }

        public Creature Npc { get; }

        public T Ai<T>() where T : CreatureAI => Assert.IsType<T>(Npc.AI);

        public List<(string Name, string Message)> Chat() => [.. Packets(Session, WorldOpcode.SmsgMessagechat).Select(p => ParseMonsterChat(p)).Select(c => (c.Name, c.Message))];

        public List<string> Heard() => [.. Chat().Select(c => c.Message)];

        public Creature One(uint entry) => Assert.Single(System.Creatures, c => c.Template.Entry == entry && !ReferenceEquals(c, Npc));

        public KalimdorWorldMapData AddKalimdorData()
        {
            var data = new KalimdorWorldMapData(Map);
            data.Initialize();
            Map.AddUpdater(data);
            return data;
        }

        public GameObjectMapSystem AddObjects(params (uint Entry, GameObjectType Type)[] templates)
            => AddObjects(templates, []);

        public GameObjectMapSystem AddObjects((uint Entry, GameObjectType Type)[] templates, GameObjectSpawn[] spawns)
        {
            var objects = new GameObjectMapSystem(Map, new GameObjectContent([.. templates.Select(t => GameObjectTestKit.GoTemplate(t.Entry, t.Type))], spawns, [], [], []));
            Map.AddUpdater(objects);
            return objects;
        }

        public void RunUntil(Func<bool> done, int limitMs = 60_000)
        {
            for (int elapsed = 0; elapsed < limitMs && !done(); elapsed += 100)
            {
                World.RunTick(100);
            }

            Assert.True(done(), $"not done within {limitMs} ms");
        }

        public void Dispose() => World.Dispose();
    }

    private static Action<CreatureTemplateBuilder> Tpl(string name, uint faction = 35, uint health = HugeHealth) => b =>
    {
        b.Name = name;
        b.Faction = faction;
        b.MinLevelHealth = health;
        b.MaxLevelHealth = health;
    };


    private static List<int> Ids(Rig rig) => [.. rig.Heard().Select(m => int.Parse(m.AsSpan(4), System.Globalization.CultureInfo.InvariantCulture))];

    private static NpcInfo InfoOf(Creature c)
        => new(c.Guid, c.Entry, c.Spawn?.Guid ?? 0, NpcFlags.Gossip, 0, c.X, c.Y, c.Z, c.BoundingRadius, c.IsAlive, false, c.Combat.IsInCombat, false, 0);

    private static void SetClass(Player player, Class cls) => player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)cls);

    private static void Tick(Rig rig, int ms)
    {
        for (int elapsed = 0; elapsed < ms; elapsed += 100)
        {
            rig.World.RunTick(100);
        }
    }

    private static readonly int[] MawsTexts = [MawsAI.EmoteFrenzy, MawsAI.EmoteMawsKill];

    private static bool Gone(Rig rig) => !rig.Npc.IsAlive || !rig.System.Creatures.Contains(rig.Npc);

    // --- Maws -------------------------------------------------------------------------------------------------

    [Fact]
    public void Maws_TheEntryRunsItsScript_ThrashOnResetUnlessAlreadyOn()
    {
        using var rig = new Rig(MawsAI.Entry, "Maws", MawsTexts, faction: 14);
        Assert.IsType<MawsAI>(rig.Npc.AI);
        Assert.Single(rig.Caster.Casts, c => c.Spell == MawsAI.SpellThrash && ReferenceEquals(c.Target, rig.Npc));

        rig.Caster.Auras.Add((rig.Npc, MawsAI.SpellThrash));
        rig.Ai<MawsAI>().OnEvade();

        Assert.Single(rig.Caster.Casts, c => c.Spell == MawsAI.SpellThrash);
    }

    [Fact]
    public void Maws_FrenzyWithItsEmoteAfter25Seconds_DarkWaterOnlyBelowTwentyPercent()
    {
        using var rig = new Rig(MawsAI.Entry, "Maws", MawsTexts, faction: 14);
        rig.Ai<MawsAI>().AttackStart(rig.Player);
        rig.Caster.Casts.Clear();
        rig.Session.Clear();

        Tick(rig, 24_800);
        Assert.DoesNotContain(rig.Caster.Casts, c => c.Spell == MawsAI.SpellFrenzy);
        Tick(rig, 400);
        Assert.Single(rig.Caster.Casts, c => c.Spell == MawsAI.SpellFrenzy && ReferenceEquals(c.Target, rig.Npc));
        Assert.Contains(MawsAI.EmoteFrenzy, Ids(rig));
        Assert.DoesNotContain(rig.Caster.Casts, c => c.Spell == MawsAI.SpellDarkWater);

        rig.Npc.Health = rig.Npc.MaxHealth / 10;
        Tick(rig, 15_200);

        Assert.Contains(rig.Caster.Casts, c => c.Spell == MawsAI.SpellDarkWater);
    }

    [Fact]
    public void Maws_RampageOnTheVictimWithinTwoMinutes()
    {
        using var rig = new Rig(MawsAI.Entry, "Maws", MawsTexts, faction: 14);
        rig.Ai<MawsAI>().AttackStart(rig.Player);

        rig.RunUntil(() => rig.Caster.Casts.Any(c => c.Spell == MawsAI.SpellRampage), 121_000);

        Assert.Contains(rig.Caster.Casts, c => c.Spell == MawsAI.SpellRampage && ReferenceEquals(c.Target, rig.Player));
    }

    [Fact]
    public void Maws_HisDeathTakesTheLightningAwayWithTheZoneEmote()
    {
        using var rig = new Rig(MawsAI.Entry, "Maws", MawsTexts, faction: 14);
        GameObjectMapSystem objects = rig.AddObjects([(MawsAI.GoTheatricLightning, GameObjectType.Generic)],
            [GameObjectTestKit.GoSpawn(1, MawsAI.GoTheatricLightning, 5f, 0f)]);
        GameObject lightning = Assert.Single(objects.GameObjects, g => g.Entry == MawsAI.GoTheatricLightning);
        Assert.True(lightning.IsSpawned);
        rig.Session.Clear();

        rig.Map.Combat.Kill(rig.Player, rig.Npc);
        Tick(rig, 200);

        Assert.False(lightning.IsSpawned);
        Assert.Contains(MawsAI.EmoteMawsKill, Ids(rig));
    }

    [Fact]
    public void Maws_After150MinutesHeGoes()
    {
        using var rig = new Rig(MawsAI.Entry, "Maws", MawsTexts, faction: 14);
        var ai = rig.Ai<MawsAI>();
        rig.Player.Relocate(rig.Npc.X + 60f, rig.Npc.Y, rig.Npc.Z, 0, 0);
        ai.OnUpdate(MawsAI.LifetimeMs - 1000);
        Assert.False(Gone(rig));
        rig.Session.Clear();

        rig.RunUntil(() => Gone(rig), 3000);
    }

    // --- the Arcanite Buoy ------------------------------------------------------------------------------------

    private static Rig BuoyRig()
    {
        (float x, float y, float z, _) = KalimdorWorldMapData.MawsSpawn;
        return new Rig(SimoneTheInconspicuousAI.NpcPrecious, "bystander", MawsTexts,
            extraTemplates: [Template(MawsAI.Entry, Tpl("Maws", 14))],
            entryWaypoints: [(MawsAI.Entry, 0u, new CreatureWaypoint(1, x + 30f, y, z, 0f, 0)), (MawsAI.Entry, 0u, new CreatureWaypoint(2, x + 60f, y, z, 0f, 0))],
            x: x - 100f, y: y, z: z);
    }

    [Fact]
    public void Buoy_APlayersEventBringsMawsAtHisSpot_RunningHisPath_OnlyOnce()
    {
        using Rig rig = BuoyRig();
        KalimdorWorldMapData data = rig.AddKalimdorData();

        Assert.True(data.OnSpellEvent(rig.Player, KalimdorWorldMapData.EventArcaniteBuoy));

        Creature maws = rig.One(MawsAI.Entry);
        Assert.Equal(KalimdorWorldMapData.MawsSpawn.X, maws.X, 1f);
        Assert.Equal(KalimdorWorldMapData.MawsSpawn.Y, maws.Y, 1f);
        Assert.False(data.OnSpellEvent(rig.Player, KalimdorWorldMapData.EventArcaniteBuoy)); // one within 200 yd
        Assert.Single(rig.System.Creatures, c => c.Entry == MawsAI.Entry);
        Tick(rig, 2000);
        Assert.True(maws.X > KalimdorWorldMapData.MawsSpawn.X + 2f, $"Maws did not take his path (x {maws.X})");
    }

    [Fact]
    public void Buoy_AnotherEventOrANonPlayerSourceSummonsNothing()
    {
        using Rig rig = BuoyRig();
        KalimdorWorldMapData data = rig.AddKalimdorData();

        Assert.False(data.OnSpellEvent(rig.Player, 1));
        Assert.True(data.OnSpellEvent(rig.Npc, KalimdorWorldMapData.EventArcaniteBuoy));

        Assert.DoesNotContain(rig.System.Creatures, c => c.Entry == MawsAI.Entry);
    }

    // --- the Blackwood stores ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(KalimdorWorldMapData.GoBlackwoodGrainStores)]
    [InlineData(KalimdorWorldMapData.GoBlackwoodNutStores)]
    [InlineData(KalimdorWorldMapData.GoBlackwoodFruitStores)]
    public void FurbolgFood_ANearbyWarriorAttacksThePlayer(uint store)
    {
        using var rig = new Rig(KalimdorWorldMapData.NpcBlackwoodWarrior, "Blackwood Warrior", faction: 14, x: 10f);
        rig.Player.Relocate(-20f, 0f, Z, 0, 0);
        KalimdorWorldMapData data = rig.AddKalimdorData();
        GameObjectMapSystem objects = rig.AddObjects((store, GameObjectType.Chest));
        GameObject go = objects.Summon(store, 0f, 0f, Z, 0f)!;

        Assert.True(data.OnGameObjectUse(rig.Player, go));

        Assert.Same(rig.Player, rig.Npc.Combat.Victim);
        Assert.Single(rig.System.Creatures, c => c.Entry == KalimdorWorldMapData.NpcBlackwoodWarrior);
    }

    [Fact]
    public void FurbolgFood_WithNoWarriorInRangeANewOneAppearsWithinTwentyYardsAndAttacks()
    {
        using var rig = new Rig(SimoneTheInconspicuousAI.NpcPrecious, "bystander",
            extraTemplates: [Template(KalimdorWorldMapData.NpcBlackwoodWarrior, Tpl("Blackwood Warrior", 14))],
            extraSpawns: [Spawn(2, KalimdorWorldMapData.NpcBlackwoodWarrior, 80f, 0f, Z)]);
        KalimdorWorldMapData data = rig.AddKalimdorData();
        GameObjectMapSystem objects = rig.AddObjects((KalimdorWorldMapData.GoBlackwoodGrainStores, GameObjectType.Chest));
        GameObject go = objects.Summon(KalimdorWorldMapData.GoBlackwoodGrainStores, 0f, 0f, Z, 0f)!;

        Assert.True(data.OnGameObjectUse(rig.Player, go));

        Creature summoned = Assert.Single(rig.System.Creatures, c => c.Entry == KalimdorWorldMapData.NpcBlackwoodWarrior && c.Spawn is null);
        Assert.True(MathF.Sqrt((summoned.X * summoned.X) + (summoned.Y * summoned.Y)) <= KalimdorWorldMapData.FurbolgFoodRange + 0.01f);
        Assert.Same(rig.Player, summoned.Combat.Victim);
    }
}
