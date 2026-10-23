using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
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
/// Zone script batch 9: the demons of The Stave of the Ancients (Klinfran, Solenor, Artorius) and The Cleaner, against a real map with a
/// recording caster and quest service. Random draws come from the map's seeded Random(1).
/// </summary>
public sealed class ZoneScriptBatch9Tests
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

    private static readonly int[] Texts =
    [
        KlinfranAI.EmoteFrenzy, SolenorAI.EmoteImmobilized, ArtoriusAI.EmotePoison, TheCleanerAI.SayAggro,
    ];

    private static Rig DemonRig(uint friendly, uint demon, Class cls = Class.Hunter)
    {
        var rig = new Rig(friendly, "friendly", Texts,
            extraTemplates:
            [
                Template(demon, Tpl("demon", 14)), Template(StaveDemonAI.NpcTheCleaner, Tpl("The Cleaner", 14)),
                Template(SolenorAI.NpcCreepingDoom, Tpl("Creeping Doom", 14)),
            ]);
        SetClass(rig.Player, cls);
        return rig;
    }

    private static void Tick(Rig rig, int ms)
    {
        for (int elapsed = 0; elapsed < ms; elapsed += 100)
        {
            rig.World.RunTick(100);
        }
    }

    /// <summary>Asks the friendly form for its real face and waits out the 10 s change.</summary>
    private static void Transform(Rig rig, uint demon)
    {
        var gossip = new StaveDemonGossip((_, _) => true);
        Assert.True(gossip.SelectReply(rig.Player, InfoOf(rig.Npc), StaveDemonGossip.SenderMain, StaveDemonGossip.ActionRealFace).Close);
        rig.RunUntil(() => rig.Npc.Entry == demon, 11_000);
    }

    private static bool Gone(Rig rig) => !rig.Npc.IsAlive || !rig.System.Creatures.Contains(rig.Npc);

    // --- registration -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(KlinfranAI.NpcFranklin, typeof(KlinfranAI))]
    [InlineData(KlinfranAI.NpcKlinfran, typeof(KlinfranAI))]
    [InlineData(SolenorAI.NpcNelson, typeof(SolenorAI))]
    [InlineData(SolenorAI.NpcSolenor, typeof(SolenorAI))]
    [InlineData(ArtoriusAI.NpcAmiable, typeof(ArtoriusAI))]
    [InlineData(ArtoriusAI.NpcDoombringer, typeof(ArtoriusAI))]
    [InlineData(TheCleanerAI.Entry, typeof(TheCleanerAI))]
    public void TheEntryRunsItsScript(uint entry, Type ai)
    {
        using var rig = new Rig(entry, "npc");
        Assert.IsType(ai, rig.Npc.AI);
    }

    // --- gossip and the change --------------------------------------------------------------------------------

    [Fact]
    public void Gossip_TheRealFaceOptionOnlyWhileTheStaveIsIncomplete()
    {
        var incomplete = new HashSet<uint>();
        var gossip = new StaveDemonGossip((_, q) => incomplete.Contains(q));
        using Rig rig = DemonRig(KlinfranAI.NpcFranklin, KlinfranAI.NpcKlinfran);
        NpcInfo npc = InfoOf(rig.Npc);

        ScriptedGossipMenu without = Assert.IsType<ScriptedGossipMenu>(gossip.Hello(rig.Player, npc));
        Assert.Empty(without.Items);
        Assert.Equal(0u, without.NpcTextId);

        incomplete.Add(StaveDemonAI.QuestStaveOfTheAncients);
        ScriptedGossipItem item = Assert.Single(Assert.IsType<ScriptedGossipMenu>(gossip.Hello(rig.Player, npc)).Items);
        Assert.Equal(StaveDemonGossip.OptionRealFace, item.Text);
        Assert.Equal(StaveDemonGossip.ActionRealFace, item.Action);
    }

    [Fact]
    public void Gossip_TheChoiceTakesTheGossipAwayAndTheDemonAppearsTenSecondsLater_WithTwentyMinutesToLive()
    {
        using Rig rig = DemonRig(KlinfranAI.NpcFranklin, KlinfranAI.NpcKlinfran);
        var ai = rig.Ai<KlinfranAI>();
        var gossip = new StaveDemonGossip((_, _) => true);
        Assert.Equal((uint)NpcFlags.Gossip, rig.Npc.NpcFlags);

        Assert.Equal(default, gossip.SelectReply(rig.Player, InfoOf(rig.Npc), StaveDemonGossip.SenderMain, 1));
        Assert.False(ai.IsTransforming);

        Assert.True(gossip.SelectReply(rig.Player, InfoOf(rig.Npc), StaveDemonGossip.SenderMain, StaveDemonGossip.ActionRealFace).Close);
        Assert.True(ai.IsTransforming);
        Assert.Equal(0u, rig.Npc.NpcFlags);
        Assert.Equal(rig.Player.Guid, ai.Hunter);
        Tick(rig, 9900);
        Assert.Equal(KlinfranAI.NpcFranklin, rig.Npc.Entry);

        rig.RunUntil(() => rig.Npc.Entry == KlinfranAI.NpcKlinfran, 1000);

        Assert.Same(ai, rig.Npc.AI);
        Assert.False(ai.IsTransforming);
        Assert.True(ai.IsDemon);
        Assert.InRange(ai.DespawnMs, StaveDemonAI.DemonLifetimeMs - 1000, StaveDemonAI.DemonLifetimeMs);
    }

    // --- the shared demon rules -------------------------------------------------------------------------------

    [Fact]
    public void Demon_TheHunterFightsIt_ItsDeathSetsATwoHourRespawn()
    {
        using Rig rig = DemonRig(KlinfranAI.NpcFranklin, KlinfranAI.NpcKlinfran);
        Transform(rig, KlinfranAI.NpcKlinfran);
        rig.Ai<KlinfranAI>().AttackStart(rig.Player);
        Tick(rig, 500);
        Assert.True(rig.Npc.IsAlive);
        Assert.DoesNotContain(rig.System.Creatures, c => c.Entry == StaveDemonAI.NpcTheCleaner);

        rig.Map.Combat.Kill(rig.Player, rig.Npc);

        Assert.InRange(rig.Npc.RespawnAtMs - rig.System.ClockMs, (StaveDemonAI.RespawnAfterDeathSeconds * 1000L) - 1000, StaveDemonAI.RespawnAfterDeathSeconds * 1000L);
    }

    [Fact]
    public void Demon_ANonHunterAttackerMakesItVanishForFifteenMinutes_AndTheCleanerAppears()
    {
        using Rig rig = DemonRig(KlinfranAI.NpcFranklin, KlinfranAI.NpcKlinfran, Class.Warrior);
        Transform(rig, KlinfranAI.NpcKlinfran);
        rig.Ai<KlinfranAI>().AttackStart(rig.Player);

        rig.RunUntil(() => Gone(rig), 2000);

        Assert.InRange(rig.Npc.RespawnAtMs - rig.System.ClockMs, (StaveDemonAI.RespawnAfterVanishSeconds * 1000L) - 1000, StaveDemonAI.RespawnAfterVanishSeconds * 1000L);
        Assert.Contains(rig.System.Creatures, c => c.Entry == StaveDemonAI.NpcTheCleaner);
    }

    [Fact]
    public void Demon_ASecondUnitOnItsThreatListMakesItVanish_TheCleanerTakesThemBothOn()
    {
        using Rig rig = DemonRig(KlinfranAI.NpcFranklin, KlinfranAI.NpcKlinfran);
        Transform(rig, KlinfranAI.NpcKlinfran);
        rig.Ai<KlinfranAI>().AttackStart(rig.Player);
        Tick(rig, 300);
        Assert.True(rig.Npc.IsAlive);
        (Player helper, _) = AddPlayer(rig.World, 2, rig.Npc.X - 1f, rig.Npc.Y);
        helper.MaxHealth = HugeHealth;
        helper.Health = HugeHealth;
        rig.Npc.Combat.Threat.AddThreat(helper, 1f);

        rig.RunUntil(() => Gone(rig), 2000);

        Creature cleaner = Assert.Single(rig.System.Creatures, c => c.Entry == StaveDemonAI.NpcTheCleaner);
        Assert.True(cleaner.Combat.Victim is not null || cleaner.Combat.Threat.Contains(rig.Player) || cleaner.Combat.Threat.Contains(helper));
        Assert.InRange(rig.Npc.RespawnAtMs - rig.System.ClockMs, (StaveDemonAI.RespawnAfterVanishSeconds * 1000L) - 1000, StaveDemonAI.RespawnAfterVanishSeconds * 1000L);
    }

    [Fact]
    public void Demon_LeftAloneTwentyMinutesItVanishesWithoutTheCleaner()
    {
        using Rig rig = DemonRig(KlinfranAI.NpcFranklin, KlinfranAI.NpcKlinfran);
        rig.Player.Relocate(rig.Npc.X + 60f, rig.Npc.Y, rig.Npc.Z, 0, 0);
        Transform(rig, KlinfranAI.NpcKlinfran);
        var ai = rig.Ai<KlinfranAI>();
        ai.OnUpdate(StaveDemonAI.DemonLifetimeMs - 2000);
        Assert.False(Gone(rig));

        rig.RunUntil(() => Gone(rig), 5000);

        Assert.DoesNotContain(rig.System.Creatures, c => c.Entry == StaveDemonAI.NpcTheCleaner);
        Assert.InRange(rig.Npc.RespawnAtMs - rig.System.ClockMs, (StaveDemonAI.RespawnAfterVanishSeconds * 1000L) - 1000, StaveDemonAI.RespawnAfterVanishSeconds * 1000L);
    }

    // --- Klinfran ---------------------------------------------------------------------------------------------

    [Fact]
    public void Klinfran_DemonicFrenzyFiveSecondsIn_ThenEveryFifteen_WithItsEmote()
    {
        using Rig rig = DemonRig(KlinfranAI.NpcFranklin, KlinfranAI.NpcKlinfran);
        Transform(rig, KlinfranAI.NpcKlinfran);
        rig.Ai<KlinfranAI>().AttackStart(rig.Player);
        rig.Caster.Casts.Clear();
        rig.Session.Clear();

        Tick(rig, 4800);
        Assert.DoesNotContain(rig.Caster.Casts, c => c.Spell == KlinfranAI.SpellDemonicFrenzy);
        Tick(rig, 400);
        Assert.Single(rig.Caster.Casts, c => c.Spell == KlinfranAI.SpellDemonicFrenzy && ReferenceEquals(c.Target, rig.Npc));
        Assert.Equal([KlinfranAI.EmoteFrenzy], Ids(rig));
        Tick(rig, 15_000);
        Assert.Equal(2, rig.Caster.Casts.Count(c => c.Spell == KlinfranAI.SpellDemonicFrenzy));
    }

    [Fact]
    public void Klinfran_ScorpidStingTakesTheFrenzyOffAndGivesEntropicSting()
    {
        using Rig rig = DemonRig(KlinfranAI.NpcFranklin, KlinfranAI.NpcKlinfran);
        Transform(rig, KlinfranAI.NpcKlinfran);
        var ai = rig.Ai<KlinfranAI>();
        rig.Caster.Casts.Clear();

        ai.OnSpellHit(rig.Player, new SpellInfo { Id = 3043 }); // Scorpid Sting rank 1: nothing
        Assert.Empty(rig.Caster.Casts);
        ai.OnSpellHit(rig.Player, new SpellInfo { Id = KlinfranAI.SpellScorpidStingRank4 });

        Assert.Contains(rig.Caster.Removed, r => ReferenceEquals(r.Unit, rig.Npc) && r.Spell == KlinfranAI.SpellDemonicFrenzy);
        Assert.Single(rig.Caster.Casts, c => c.Spell == KlinfranAI.SpellEntropicSting);
    }

    // --- Solenor ----------------------------------------------------------------------------------------------

    [Fact]
    public void Solenor_SoulFlameAsHeAppearsAndAgain150MsLater()
    {
        using Rig rig = DemonRig(SolenorAI.NpcNelson, SolenorAI.NpcSolenor);
        var ai = rig.Ai<SolenorAI>();
        new StaveDemonGossip((_, _) => true).SelectReply(rig.Player, InfoOf(rig.Npc), StaveDemonGossip.SenderMain, StaveDemonGossip.ActionRealFace);
        rig.Player.Relocate(rig.Npc.X + 100f, rig.Npc.Y, rig.Npc.Z, 0, 0);
        rig.RunUntil(() => rig.Npc.Entry == SolenorAI.NpcSolenor, 11_000);

        Assert.Single(rig.Caster.Casts, c => c.Spell == SolenorAI.SpellSoulFlame);
        Assert.InRange(ai.SoulFlameMs, 1u, SolenorAI.SoulFlameRecastMs);
        Tick(rig, 200);
        Assert.Equal(2, rig.Caster.Casts.Count(c => c.Spell == SolenorAI.SpellSoulFlame && ReferenceEquals(c.Target, rig.Npc)));
        Assert.Equal(0u, ai.SoulFlameMs);
    }

    [Fact]
    public void Solenor_WingClipGivesCripplingClipWithItsEmote_FrostTrapPutsOutTheSoulFlame_CreepingDoomInTheFight()
    {
        using Rig rig = DemonRig(SolenorAI.NpcNelson, SolenorAI.NpcSolenor);
        Transform(rig, SolenorAI.NpcSolenor);
        var ai = rig.Ai<SolenorAI>();
        ai.AttackStart(rig.Player);
        rig.Caster.Casts.Clear();
        rig.Session.Clear();

        ai.OnSpellHit(rig.Player, new SpellInfo { Id = SolenorAI.SpellWingClipRank3 });
        Assert.Single(rig.Caster.Casts, c => c.Spell == SolenorAI.SpellCripplingClip);
        Assert.Equal([SolenorAI.EmoteImmobilized], Ids(rig));

        rig.Caster.Auras.Add((rig.Npc, SolenorAI.SpellSoulFlame));
        rig.Caster.Auras.Add((rig.Npc, SolenorAI.SpellFrostTrap));
        Tick(rig, 6100);

        Assert.Contains(rig.Caster.Removed, r => ReferenceEquals(r.Unit, rig.Npc) && r.Spell == SolenorAI.SpellSoulFlame);
        Assert.Contains(rig.Caster.Casts, c => c.Spell == SolenorAI.SpellCreepingDoom);
    }

    [Fact]
    public void Solenor_DreadfulFrightOnlyAtAVictimBeyondFiveYards()
    {
        using Rig rig = DemonRig(SolenorAI.NpcNelson, SolenorAI.NpcSolenor);
        Transform(rig, SolenorAI.NpcSolenor);
        var ai = rig.Ai<SolenorAI>();
        ai.AttackStart(rig.Player);
        rig.Caster.Casts.Clear();
        for (int i = 0; i < 160; i++)
        {
            rig.Player.Relocate(rig.Npc.X + 1f, rig.Npc.Y, rig.Npc.Z, 0, 0);
            rig.World.RunTick(100);
        }

        Assert.DoesNotContain(rig.Caster.Casts, c => c.Spell == SolenorAI.SpellDreadfulFright);
        rig.Player.Relocate(rig.Npc.X + 10f, rig.Npc.Y, rig.Npc.Z, 0, 0);
        rig.Npc.Combat.Threat.AddThreat(rig.Player, 0f);
        ai.OnUpdate(100);

        Assert.Contains(rig.Caster.Casts, c => c.Spell == SolenorAI.SpellDreadfulFright && ReferenceEquals(c.Target, rig.Player));
    }

    // --- Artorius ---------------------------------------------------------------------------------------------

    [Fact]
    public void Artorius_SerpentStingRankEightOrNineGivesStingingTraumaWithItsEmote()
    {
        using Rig rig = DemonRig(ArtoriusAI.NpcAmiable, ArtoriusAI.NpcDoombringer);
        Transform(rig, ArtoriusAI.NpcDoombringer);
        var ai = rig.Ai<ArtoriusAI>();
        rig.Caster.Casts.Clear();
        rig.Session.Clear();

        ai.OnSpellHit(rig.Player, new SpellInfo { Id = 1978 });
        Assert.Empty(rig.Caster.Casts);
        ai.OnSpellHit(rig.Player, new SpellInfo { Id = ArtoriusAI.SpellSerpentStingRank8 });
        ai.OnSpellHit(rig.Player, new SpellInfo { Id = ArtoriusAI.SpellSerpentStingRank9 });

        Assert.Equal(2, rig.Caster.Casts.Count(c => c.Spell == ArtoriusAI.SpellStingingTrauma));
        Assert.Equal([ArtoriusAI.EmotePoison, ArtoriusAI.EmotePoison], Ids(rig));
    }

    [Fact]
    public void Artorius_DemonicDoomEverySevenAndAHalfSecondsAtAVictimInRange_FrenzyFiveToEightSecondsIn()
    {
        using Rig rig = DemonRig(ArtoriusAI.NpcAmiable, ArtoriusAI.NpcDoombringer);
        Transform(rig, ArtoriusAI.NpcDoombringer);
        var ai = rig.Ai<ArtoriusAI>();
        ai.AttackStart(rig.Player);
        rig.Caster.Casts.Clear();

        Tick(rig, 7400);
        Assert.DoesNotContain(rig.Caster.Casts, c => c.Spell == ArtoriusAI.SpellDemonicDoom);
        Tick(rig, 800);
        Assert.Single(rig.Caster.Casts, c => c.Spell == ArtoriusAI.SpellDemonicFrenzy);
        Assert.Single(rig.Caster.Casts, c => c.Spell == ArtoriusAI.SpellDemonicDoom && ReferenceEquals(c.Target, rig.Player));
    }

    // --- The Cleaner ------------------------------------------------------------------------------------------

    [Fact]
    public void Cleaner_ImmuneFromItsResetAndGoneThreeSecondsLaterWithNobodyToFight()
    {
        using var rig = new Rig(TheCleanerAI.Entry, "The Cleaner", Texts, faction: 14);
        rig.Player.Relocate(rig.Npc.X + 60f, rig.Npc.Y, rig.Npc.Z, 0, 0);

        Assert.Contains(rig.Caster.Casts, c => c.Spell == TheCleanerAI.SpellImmunity && ReferenceEquals(c.Target, rig.Npc));
        Tick(rig, 2500);
        Assert.False(Gone(rig));
        rig.RunUntil(() => Gone(rig), 2000);
    }

    [Fact]
    public void Cleaner_CallsOutOnAggro()
    {
        using var rig = new Rig(TheCleanerAI.Entry, "The Cleaner", Texts, faction: 14);
        rig.Session.Clear();

        rig.Ai<TheCleanerAI>().OnAggro(rig.Player);

        Assert.Equal([TheCleanerAI.SayAggro], Ids(rig));
    }
}
