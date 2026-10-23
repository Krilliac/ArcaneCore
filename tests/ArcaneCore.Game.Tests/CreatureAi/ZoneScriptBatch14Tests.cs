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
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// Zone script batch 14: Winna's kitten and the Corrupted Saber (felwood.cpp) and the Rallying Cry heralds' gossip (orgrimmar.cpp, stormwind_city.cpp), against a real map with a
/// recording caster and quest service. Random draws come from the map's seeded Random(1).
/// </summary>
public sealed class ZoneScriptBatch14Tests
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

    private static readonly int[] KittenTexts = [FelwoodKittenAI.EmoteSabJump, FelwoodKittenAI.EmoteSabFollow];

    private static (Rig Rig, FelwoodKittenAI Ai, Creature Winna) Kitten(float winnaX = 60f)
    {
        var rig = new Rig(FelwoodKittenAI.Entry, "Kitten", KittenTexts,
            extraTemplates: [Template(FelwoodKittenAI.NpcCorruptSaber, Tpl("Corrupted Saber")), Template(FelwoodKittenAI.NpcWinna, Tpl("Winna Hazzard"))],
            extraSpawns: [Spawn(2, FelwoodKittenAI.NpcWinna, winnaX, 0f, Z)]);
        KalimdorWorldMapData data = rig.AddKalimdorData();
        data.OnCreatureCreate(rig.Npc);
        rig.Npc.SetOwnerGuid(rig.Player.Guid);
        return (rig, Assert.IsType<FelwoodKittenAI>(rig.Npc.AI), rig.One(FelwoodKittenAI.NpcWinna));
    }

    [Fact]
    public void Kitten_WithAPlayerOwnerItJumpsInAndWaitsPaused()
    {
        (Rig rig, FelwoodKittenAI ai, _) = Kitten();
        using (rig)
        {
            Tick(rig, 300);

            Assert.True(ai.HasFollowState(FollowerAI.FollowState.InProgress));
            Assert.True(ai.Paused);
            Assert.NotEqual(0u, (uint)(rig.Npc.UnitFlags & UnitFlags.Spawning));
            Assert.Single(Ids(rig), FelwoodKittenAI.EmoteSabJump);
            Assert.False(ai.Transformed);
        }
    }

    [Fact]
    public void Kitten_WithoutAnOwnerNothingStarts()
    {
        using var rig = new Rig(FelwoodKittenAI.Entry, "Kitten", KittenTexts);
        KalimdorWorldMapData data = rig.AddKalimdorData();
        data.OnCreatureCreate(rig.Npc);
        FelwoodKittenAI ai = Assert.IsType<FelwoodKittenAI>(rig.Npc.AI);
        Tick(rig, 9000);

        Assert.False(ai.HasFollowState(FollowerAI.FollowState.InProgress));
        Assert.DoesNotContain(rig.Caster.Casts, c => c.Spell == FelwoodKittenAI.SpellCorruptSaberVisual);
    }

    [Fact]
    public void Kitten_AfterSevenAndAHalfSecondsItBecomesTheCorruptedSaberAndFollows()
    {
        (Rig rig, FelwoodKittenAI ai, _) = Kitten();
        using (rig)
        {
            Tick(rig, 7_200);
            Assert.False(ai.Transformed);
            rig.RunUntil(() => ai.Transformed, 1_500);

            Assert.Single(rig.Caster.Casts, c => c.Spell == FelwoodKittenAI.SpellCorruptSaberVisual && ReferenceEquals(c.Target, rig.Npc));
            Assert.Equal(FelwoodKittenAI.NpcCorruptSaber, rig.Npc.Entry);
            Assert.Equal(FelwoodKittenAI.Entry, rig.Npc.Template.Entry);
            Assert.False(ai.Paused);
            Assert.Equal(0u, (uint)(rig.Npc.UnitFlags & UnitFlags.Spawning));
            Assert.Contains(FelwoodKittenAI.EmoteSabFollow, Ids(rig));
        }
    }

    [Fact]
    public void Kitten_NearWinnaWhileFollowingItGainsTheGossipFlag()
    {
        (Rig rig, FelwoodKittenAI ai, Creature winna) = Kitten();
        using (rig)
        {
            Tick(rig, 300);
            ai.MoveInLineOfSight(winna);
            Assert.Equal(0u, rig.Npc.NpcFlags & (uint)NpcFlags.Gossip);

            winna.Relocate(rig.Npc.X + 2f, rig.Npc.Y, rig.Npc.Z);
            ai.MoveInLineOfSight(winna);
            Assert.NotEqual(0u, rig.Npc.NpcFlags & (uint)NpcFlags.Gossip);
        }
    }

    [Fact]
    public void CorruptSaber_TheReleaseLineNeedsTheQuestAndWinnaClose_ChoosingItCompletesTheQuest()
    {
        (Rig rig, FelwoodKittenAI ai, Creature winna) = Kitten();
        using (rig)
        {
            Assert.Null(ai.Hello(rig.Player, InfoOf(rig.Npc))); // still a kitten
            rig.RunUntil(() => ai.Transformed, 9_000);

            Assert.Empty(ai.Hello(rig.Player, InfoOf(rig.Npc))!.Items);
            rig.Quest.Incomplete.Add(FelwoodKittenAI.QuestCorruptSaber);
            Assert.Empty(ai.Hello(rig.Player, InfoOf(rig.Npc))!.Items); // Winna too far

            winna.Relocate(rig.Npc.X + 2f, rig.Npc.Y, rig.Npc.Z);
            ScriptedGossipItem item = Assert.Single(ai.Hello(rig.Player, InfoOf(rig.Npc))!.Items);
            Assert.Equal(FelwoodKittenAI.GossipItemRelease, item.Text);

            ScriptedGossipReply reply = ai.SelectReply(rig.Player, InfoOf(rig.Npc), item.Sender, item.Action);
            Assert.True(reply.Close);
            Assert.Contains((rig.Player, FelwoodKittenAI.QuestCorruptSaber), rig.Quest.Completed);
            Assert.True(ai.HasFollowState(FollowerAI.FollowState.Complete));
        }
    }

    // --- Rallying Cry heralds ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(RallyingCryGossip.NpcRunthalak, RallyingCryGossip.GoHeadOfNefarianOg, RallyingCryGossip.GossipHeadOg)]
    [InlineData(RallyingCryGossip.NpcSaurfang, RallyingCryGossip.GoHeadOfOnyxiaOg, RallyingCryGossip.GossipHeadOg)]
    [InlineData(RallyingCryGossip.NpcMattingly, RallyingCryGossip.GoHeadOfOnyxiaSw, RallyingCryGossip.GossipHeadMattingly)]
    [InlineData(RallyingCryGossip.NpcAfrasiabi, RallyingCryGossip.GoHeadOfNefarianSw, RallyingCryGossip.GossipHeadAfrasiabi)]
    public void Herald_WithItsHeadUpWithinAHundredYardsTheHeadMenuShows(uint herald, uint head, uint menu)
    {
        using var rig = new Rig(herald, "herald");
        var gossip = new RallyingCryGossip();
        Assert.Null(gossip.Hello(rig.Player, InfoOf(rig.Npc)));

        GameObjectMapSystem objects = rig.AddObjects((head, GameObjectType.Generic));
        GameObject go = objects.Summon(head, 90f, 0f, Z, 0f)!;
        Assert.Equal(menu, gossip.Hello(rig.Player, InfoOf(rig.Npc))!.DatabaseMenuId);

        go.Relocate(110f, 0f, Z);
        Assert.Null(gossip.Hello(rig.Player, InfoOf(rig.Npc)));
    }

    [Fact]
    public void Herald_TheOtherDragonsHeadDoesNotCount_NorDoesAnotherNpc()
    {
        using var rig = new Rig(RallyingCryGossip.NpcSaurfang, "Saurfang");
        GameObjectMapSystem objects = rig.AddObjects((RallyingCryGossip.GoHeadOfNefarianOg, GameObjectType.Generic));
        objects.Summon(RallyingCryGossip.GoHeadOfNefarianOg, 5f, 0f, Z, 0f);
        var gossip = new RallyingCryGossip();

        Assert.Null(gossip.Hello(rig.Player, InfoOf(rig.Npc)));
        Assert.Null(gossip.Hello(rig.Player, InfoOf(rig.Npc) with { Entry = 1 }));
    }
}
