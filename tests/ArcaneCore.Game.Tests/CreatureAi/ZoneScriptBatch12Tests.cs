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
/// Zone script batch 12: the Felhound Tracker (azshara.cpp npc_felhound_tracker) and Colonel Kurzen (stranglethorn_vale.cpp), against a real map with a
/// recording caster and quest service. Random draws come from the map's seeded Random(1).
/// </summary>
public sealed class ZoneScriptBatch12Tests
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

    // --- Felhound Tracker -------------------------------------------------------------------------------------

    private static (Rig Rig, FelhoundTrackerAi Ai) Felhound()
    {
        var rig = new Rig(FelhoundTrackerAi.Entry, "Felhound Tracker");
        KalimdorWorldMapData data = rig.AddKalimdorData();
        data.OnCreatureCreate(rig.Npc);
        return (rig, Assert.IsType<FelhoundTrackerAi>(rig.Npc.AI));
    }

    [Fact]
    public void Felhound_KalimdorRegistersTheEntryScriptOverTheGenericAi()
    {
        (Rig rig, _) = Felhound();
        using (rig)
        {
            Assert.IsType<FelhoundTrackerAi>(rig.Npc.AI);
        }
    }

    [Fact]
    public void Felhound_ARoarOffTheQuestDoesNothing_AnotherEmoteDoesNothing()
    {
        (Rig rig, FelhoundTrackerAi ai) = Felhound();
        using (rig)
        {
            GameObjectMapSystem objects = rig.AddObjects([(FelhoundTrackerAi.Crystals[0], GameObjectType.Goober)],
                [GameObjectTestKit.GoSpawn(1, FelhoundTrackerAi.Crystals[0], 10f, 0f)]);
            Assert.Single(objects.GameObjects).Flags |= GameObjectFlags.InteractCond;

            ai.OnReceiveEmote(rig.Player, FelhoundTrackerAi.TextEmoteRoar);
            Assert.False(ai.IsMovingToCrystal);

            rig.Quest.Incomplete.Add(FelhoundTrackerAi.QuestAzsharite);
            ai.OnReceiveEmote(rig.Player, 1);
            Assert.False(ai.IsMovingToCrystal);
        }
    }

    [Fact]
    public void Felhound_ARoarOnTheQuestRunsToTheNearestFlaggedCrystalOfTheFirstEntryInRange()
    {
        (Rig rig, FelhoundTrackerAi ai) = Felhound();
        using (rig)
        {
            uint first = FelhoundTrackerAi.Crystals[0], second = FelhoundTrackerAi.Crystals[1];
            GameObjectMapSystem objects = rig.AddObjects([(first, GameObjectType.Goober), (second, GameObjectType.Goober)],
            [
                GameObjectTestKit.GoSpawn(1, second, 5f, 0f),   // a later entry, nearer: not chosen
                GameObjectTestKit.GoSpawn(2, first, 15f, 0f),   // nearest of the first entry, but no longer flagged
                GameObjectTestKit.GoSpawn(3, first, 25f, 0f),   // the one it runs to
                GameObjectTestKit.GoSpawn(4, first, 0f, 35f),
            ]);
            foreach (GameObject go in objects.GameObjects.Where(g => g.Spawn?.Guid != 2))
            {
                go.Flags |= GameObjectFlags.InteractCond;
            }

            rig.Quest.Incomplete.Add(FelhoundTrackerAi.QuestAzsharite);
            ai.OnReceiveEmote(rig.Player, FelhoundTrackerAi.TextEmoteRoar);

            Assert.True(ai.IsMovingToCrystal);
            rig.RunUntil(() => !ai.IsMovingToCrystal, 20_000);
            Assert.InRange(rig.Npc.X, 25f - FelhoundTrackerAi.ContactDistance - 2f, 25f - FelhoundTrackerAi.ContactDistance + 0.5f);
            Assert.InRange(MathF.Abs(rig.Npc.Y), 0f, 1f);
            Assert.InRange(ai.WaitingMs, FelhoundTrackerAi.WaitMs - 1000, FelhoundTrackerAi.WaitMs);
        }
    }

    [Fact]
    public void Felhound_WithNoCrystalInFortyYardsItGrowlsAndStays()
    {
        (Rig rig, FelhoundTrackerAi ai) = Felhound();
        using (rig)
        {
            GameObjectMapSystem objects = rig.AddObjects([(FelhoundTrackerAi.Crystals[0], GameObjectType.Goober)],
                [GameObjectTestKit.GoSpawn(1, FelhoundTrackerAi.Crystals[0], 45f, 0f)]);
            Assert.Single(objects.GameObjects).Flags |= GameObjectFlags.InteractCond;
            rig.Quest.Incomplete.Add(FelhoundTrackerAi.QuestAzsharite);
            rig.Session.Clear();

            Assert.False(ai.FindNewCrystal());
            ai.OnReceiveEmote(rig.Player, FelhoundTrackerAi.TextEmoteRoar);

            Assert.False(ai.IsMovingToCrystal);
            Assert.NotEmpty(Packets(rig.Session, WorldOpcode.SmsgPlaySound));
        }
    }

    [Fact]
    public void Felhound_AtTheCrystalItWaitsTwentySecondsThenFollowsAgain()
    {
        (Rig rig, FelhoundTrackerAi ai) = Felhound();
        using (rig)
        {
            GameObjectMapSystem objects = rig.AddObjects([(FelhoundTrackerAi.Crystals[2], GameObjectType.Goober)],
                [GameObjectTestKit.GoSpawn(1, FelhoundTrackerAi.Crystals[2], 8f, 0f)]);
            Assert.Single(objects.GameObjects).Flags |= GameObjectFlags.InteractCond;
            rig.Quest.Incomplete.Add(FelhoundTrackerAi.QuestAzsharite);

            Assert.True(ai.FindNewCrystal());
            ai.OnMovementInform(MovementGeneratorType.Point, 1);
            Assert.False(ai.IsMovingToCrystal);
            Assert.Equal(FelhoundTrackerAi.WaitMs, ai.WaitingMs);

            ai.OnUpdate(19_900);
            Assert.Equal(100u, ai.WaitingMs);
            ai.OnUpdate(200);
            Assert.Equal(0u, ai.WaitingMs);
        }
    }

    // --- Colonel Kurzen ---------------------------------------------------------------------------------------

    [Fact]
    public void Kurzen_TheEntryRunsItsScript()
    {
        using var rig = new Rig(ColonelKurzenAI.Entry, "Colonel Kurzen", faction: 14);
        Assert.IsType<ColonelKurzenAI>(rig.Npc.AI);
    }

    [Fact]
    public void Kurzen_SmokeBombWithinTwelveSeconds_ThenGarroteWhileStealthed()
    {
        using var rig = new Rig(ColonelKurzenAI.Entry, "Colonel Kurzen", faction: 14);
        ColonelKurzenAI ai = rig.Ai<ColonelKurzenAI>();
        ai.AttackStart(rig.Player);
        rig.Caster.Casts.Clear();

        Tick(rig, 5_800);
        Assert.DoesNotContain(rig.Caster.Casts, c => c.Spell == ColonelKurzenAI.SpellSmokeBomb);
        rig.RunUntil(() => rig.Caster.Casts.Any(c => c.Spell == ColonelKurzenAI.SpellSmokeBomb), 7_000);
        Assert.Single(rig.Caster.Casts, c => c.Spell == ColonelKurzenAI.SpellSmokeBomb && ReferenceEquals(c.Target, rig.Npc));
        Assert.True(ai.GarrotePending);
        rig.Caster.Auras.Add((rig.Npc, ColonelKurzenAI.SpellStealth));

        rig.RunUntil(() => !ai.GarrotePending, 7_000);
        Assert.Single(rig.Caster.Casts, c => c.Spell == ColonelKurzenAI.SpellGarrote && ReferenceEquals(c.Target, rig.Player));
    }

    [Fact]
    public void Kurzen_NoGarroteOutOfStealth_TheBombComesAgain()
    {
        using var rig = new Rig(ColonelKurzenAI.Entry, "Colonel Kurzen", faction: 14);
        ColonelKurzenAI ai = rig.Ai<ColonelKurzenAI>();
        ai.AttackStart(rig.Player);
        rig.Caster.Casts.Clear();

        rig.RunUntil(() => ai.GarrotePending, 13_000);
        rig.RunUntil(() => !ai.GarrotePending, 7_000);
        Assert.DoesNotContain(rig.Caster.Casts, c => c.Spell == ColonelKurzenAI.SpellGarrote);

        rig.RunUntil(() => rig.Caster.Casts.Count(c => c.Spell == ColonelKurzenAI.SpellSmokeBomb) == 2, 13_000);
    }

    [Fact]
    public void Kurzen_NothingOutOfCombat()
    {
        using var rig = new Rig(ColonelKurzenAI.Entry, "Colonel Kurzen", faction: 35);
        rig.Caster.Casts.Clear();
        Tick(rig, 15_000);
        Assert.Empty(rig.Caster.Casts);
    }
}
