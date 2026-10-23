using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
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
/// Zone script batch 13: the Blackwood furbolgs and the purified food (darkshore.cpp npc_corrupted_furbolg, event_purify_food), against a real map with a
/// recording caster and quest service. Random draws come from the map's seeded Random(1).
/// </summary>
public sealed class ZoneScriptBatch13Tests
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

    private const uint Warrior = CorruptedFurbolgAI.NpcBlackwoodWarrior, Totemic = CorruptedFurbolgAI.NpcBlackwoodTotemic;
    private const uint Bystander = 60_001;
    private static readonly int[] FurbolgTexts = [CorruptedFurbolgAI.EmoteLured, CorruptedFurbolgAI.EmotePurified, CorruptedFurbolgAI.EmoteGenericFlee];

    private static List<Creature> Furbolgs(Rig rig)
        => [.. rig.System.Creatures.Where(c => c.Template.Entry is Warrior or Totemic && c.IsAlive)];

    private static CorruptedFurbolgAI AiOf(Creature c) => Assert.IsType<CorruptedFurbolgAI>(c.AI);

    // --- npc_corrupted_furbolg in a fight ---------------------------------------------------------------------

    [Theory]
    [InlineData(Warrior)]
    [InlineData(Totemic)]
    public void Furbolg_BothEntriesRunTheScript_OnlyTheWarriorTakesBattleStance(uint entry)
    {
        using var rig = new Rig(entry, "Blackwood", FurbolgTexts, faction: 14);
        Assert.IsType<CorruptedFurbolgAI>(rig.Npc.AI);
        Assert.Equal(entry == Warrior ? 1 : 0, rig.Caster.Casts.Count(c => c.Spell == CorruptedFurbolgAI.SpellBattleStance && ReferenceEquals(c.Target, rig.Npc)));
    }

    [Fact]
    public void Furbolg_TheWarriorThunderclapsWithinTwentyOneSecondsInMelee()
    {
        using var rig = new Rig(Warrior, "Blackwood Warrior", FurbolgTexts, faction: 14);
        AiOf(rig.Npc).AttackStart(rig.Player);
        rig.Caster.Casts.Clear();

        Tick(rig, 15_800);
        Assert.DoesNotContain(rig.Caster.Casts, c => c.Spell == CorruptedFurbolgAI.SpellThunderclap);
        rig.RunUntil(() => rig.Caster.Casts.Any(c => c.Spell == CorruptedFurbolgAI.SpellThunderclap), 6_000);
        Assert.DoesNotContain(rig.Caster.Casts, c => c.Spell == CorruptedFurbolgAI.SpellHealingWard);
    }

    [Fact]
    public void Furbolg_TheTotemicDropsAHealingWardWithinTwelveSeconds_ThenNotForThirty()
    {
        using var rig = new Rig(Totemic, "Blackwood Totemic", FurbolgTexts, faction: 14);
        AiOf(rig.Npc).AttackStart(rig.Player);
        rig.Caster.Casts.Clear();

        rig.RunUntil(() => rig.Caster.Casts.Any(c => c.Spell == CorruptedFurbolgAI.SpellHealingWard), 12_200);
        Tick(rig, 29_500);
        Assert.Single(rig.Caster.Casts, c => c.Spell == CorruptedFurbolgAI.SpellHealingWard);
        Assert.DoesNotContain(rig.Caster.Casts, c => c.Spell == CorruptedFurbolgAI.SpellThunderclap);
    }

    [Fact]
    public void Furbolg_FleesOnceAtFifteenPercentWithTheEmote()
    {
        using var rig = new Rig(Warrior, "Blackwood Warrior", FurbolgTexts, faction: 14);
        AiOf(rig.Npc).AttackStart(rig.Player);
        Tick(rig, 500);
        rig.Session.Clear();

        rig.Npc.Health = rig.Npc.MaxHealth / 10;
        Tick(rig, 300);

        Assert.True(rig.Npc.Motion.HasActive(MovementGeneratorType.Fleeing));
        Assert.Single(Ids(rig), CorruptedFurbolgAI.EmoteGenericFlee);
        Tick(rig, 2000);
        Assert.Single(Ids(rig), CorruptedFurbolgAI.EmoteGenericFlee);
    }

    // --- event_purify_food ------------------------------------------------------------------------------------

    private static (Rig Rig, GameObject Bonfire) PurifyRig()
    {
        var rig = new Rig(Bystander, "bystander", FurbolgTexts,
            extraTemplates: [Template(Warrior, Tpl("Blackwood Warrior", 14)), Template(Totemic, Tpl("Blackwood Totemic", 14))],
            extraSpawns: [Spawn(2, Totemic, 45f, 0f, Z), Spawn(3, Totemic, 120f, 0f, Z)]);
        rig.AddKalimdorData();
        GameObjectMapSystem objects = rig.AddObjects((60_002, GameObjectType.Goober));
        return (rig, objects.Summon(60_002, 30f, 0f, Z, 0f)!);
    }

    [Fact]
    public void PurifyFood_FromAPlayerAtTheBonfire_TwoWarriorsComeAndEveryFurbolgInFortyYardsSeeksTheFood_TheDbScriptStillRuns()
    {
        (Rig rig, GameObject bonfire) = PurifyRig();
        using (rig)
        {
            Assert.NotEqual(ScriptedEventResult.Handled, ScriptedEvents.Start(rig.Map, ScriptedEvents.PurifyFood, rig.Player, bonfire));

            List<Creature> furbolgs = Furbolgs(rig);
            Assert.Equal(2, furbolgs.Count(c => c.Template.Entry == Warrior && c.Spawn is null));
            List<Creature> drawn = [.. furbolgs.Where(c => c.Spawn?.Guid != 3)];
            Assert.Equal(3, drawn.Count);

            rig.RunUntil(() => drawn.All(c => AiOf(c).IsPassive), 6_000);
            Assert.All(drawn, c => Assert.True((c.UnitFlags & UnitFlags.ImmuneToPlayer) != 0));
            Assert.False(AiOf(Assert.Single(furbolgs, c => c.Spawn?.Guid == 3)).IsPassive);

            rig.RunUntil(() => drawn.All(c => AiOf(c).IsPurified), 30_000);
            Assert.All(drawn, c => Assert.Equal(CorruptedFurbolgAI.FactionBlackwood, c.FactionTemplate));
            Assert.All(drawn, c => Assert.InRange(MathF.Sqrt(((c.X - 6f) * (c.X - 6f)) + (c.Y * c.Y)), 0f, CorruptedFurbolgAI.WanderRadius + 1f));

            rig.RunUntil(() => drawn.All(c => !c.IsAlive || !AiOf(c).IsPassive), 100_000);
            Assert.All(drawn.Where(c => c.IsAlive), c =>
            {
                Assert.False(c.HasTemporaryFaction);
                Assert.Equal(0u, (uint)(c.UnitFlags & UnitFlags.ImmuneToPlayer));
            });
        }
    }

    [Fact]
    public void PurifyFood_TheCombatScriptRunsFromTheLureUntilReset()
    {
        // darkshore.cpp: EVENT_START_PURIFICATION SetCombatScriptStatus(true) (:717); Reset sets it false (:752).
        (Rig rig, GameObject bonfire) = PurifyRig();
        using (rig)
        {
            List<Creature> drawn = [.. Furbolgs(rig).Where(c => c.Spawn?.Guid != 3)];
            Assert.All(drawn, c => Assert.False(AiOf(c).CombatScriptRunning));
            ScriptedEvents.Start(rig.Map, ScriptedEvents.PurifyFood, rig.Player, bonfire);
            rig.RunUntil(() => drawn.All(c => AiOf(c).IsPassive), 6_000);
            Assert.All(drawn, c => Assert.True(AiOf(c).CombatScriptRunning));

            AiOf(drawn[0]).EnterEvadeMode();
            Assert.False(AiOf(drawn[0]).IsPassive);
            Assert.False(AiOf(drawn[0]).CombatScriptRunning);
        }
    }

    [Fact]
    public void PurifyFood_OnlyTheLastFurbolgDrawnEmotes()
    {
        (Rig rig, GameObject bonfire) = PurifyRig();
        using (rig)
        {
            rig.Session.Clear();
            ScriptedEvents.Start(rig.Map, ScriptedEvents.PurifyFood, rig.Player, bonfire);
            List<Creature> drawn = [.. Furbolgs(rig).Where(c => c.Spawn?.Guid != 3)];
            rig.RunUntil(() => drawn.All(c => AiOf(c).IsPurified), 30_000);

            Assert.Single(Ids(rig), CorruptedFurbolgAI.EmotePurified);
        }
    }

    [Fact]
    public void PurifyFood_AFurbolgAlreadyFightingIsLeftAlone()
    {
        (Rig rig, GameObject bonfire) = PurifyRig();
        using (rig)
        {
            Creature busy = Assert.Single(rig.System.Creatures, c => c.Spawn?.Guid == 2);
            AiOf(busy).AttackStart(rig.Player);
            Tick(rig, 200);
            Assert.True(busy.Combat.IsInCombat);

            ScriptedEvents.Start(rig.Map, ScriptedEvents.PurifyFood, rig.Player, bonfire);
            Tick(rig, 6_000);

            Assert.False(AiOf(busy).IsPassive);
        }
    }

    [Fact]
    public void PurifyFood_WithoutAPlayerAtAnObjectNothingHappensAndTheDbScriptIsHeldBack()
    {
        (Rig rig, _) = PurifyRig();
        using (rig)
        {
            Assert.Equal(ScriptedEventResult.Handled, ScriptedEvents.Start(rig.Map, ScriptedEvents.PurifyFood, rig.Player, null));
            Assert.DoesNotContain(rig.System.Creatures, c => c.Template.Entry == Warrior);
        }
    }
}
