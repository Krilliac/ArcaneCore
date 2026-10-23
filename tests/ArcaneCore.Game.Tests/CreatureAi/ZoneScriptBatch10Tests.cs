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
/// Zone script batch 10: Simone the Inconspicuous, Simone the Seductress and Precious the Devourer (ungoro_crater.cpp), against a real map with a
/// recording caster and quest service. Random draws come from the map's seeded Random(1).
/// </summary>
public sealed class ZoneScriptBatch10Tests
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

    private static void Tick(Rig rig, int ms)
    {
        for (int elapsed = 0; elapsed < ms; elapsed += 100)
        {
            rig.World.RunTick(100);
        }
    }

    private static readonly int[] Texts = [SimoneTheSeductressAI.EmoteSilence, TheCleanerAI.SayAggro];

    private static Rig SimoneRig(Class cls = Class.Hunter, bool spawnPrecious = false)
    {
        var rig = new Rig(SimoneTheInconspicuousAI.Entry, "Simone the Inconspicuous", Texts,
            extraTemplates:
            [
                Template(SimoneTheInconspicuousAI.NpcPrecious, Tpl("Precious")),
                Template(SimoneTheInconspicuousAI.NpcSimoneTheSeductress, Tpl("Simone the Seductress", 14)),
                Template(SimoneTheInconspicuousAI.NpcPreciousTheDevourer, Tpl("Precious the Devourer", 14)),
                Template(StaveDemonAI.NpcTheCleaner, Tpl("The Cleaner", 14)),
            ],
            extraSpawns: spawnPrecious ? [Spawn(2, SimoneTheInconspicuousAI.NpcPrecious, 3f, 0f, Z)] : null);
        SetClass(rig.Player, cls);
        return rig;
    }

    private static Creature[] All(Rig rig, uint entry) => [.. rig.System.Creatures.Where(c => c.Entry == entry && c.IsAlive)];

    /// <summary>Asks Simone for her real face and waits out the 10 s; returns the Seductress.</summary>
    private static Creature Transform(Rig rig)
    {
        Assert.True(new StaveDemonGossip((_, _) => true)
            .SelectReply(rig.Player, InfoOf(rig.Npc), StaveDemonGossip.SenderMain, StaveDemonGossip.ActionRealFace).Close);
        rig.RunUntil(() => All(rig, SimoneTheInconspicuousAI.NpcSimoneTheSeductress).Length == 1, 11_000);
        return All(rig, SimoneTheInconspicuousAI.NpcSimoneTheSeductress)[0];
    }

    private static Creature Devourer(Rig rig) => Assert.Single(All(rig, SimoneTheInconspicuousAI.NpcPreciousTheDevourer));

    private static long RespawnIn(Rig rig) => rig.Npc.RespawnAtMs - rig.System.ClockMs;

    [Theory]
    [InlineData(SimoneTheInconspicuousAI.Entry, typeof(SimoneTheInconspicuousAI))]
    [InlineData(SimoneTheSeductressAI.Entry, typeof(SimoneTheSeductressAI))]
    [InlineData(PreciousTheDevourerAI.Entry, typeof(PreciousTheDevourerAI))]
    public void TheEntryRunsItsScript(uint entry, Type ai)
    {
        using var rig = new Rig(entry, "npc");
        Assert.IsType(ai, rig.Npc.AI);
    }

    [Fact]
    public void Simone_WithNoPreciousNearbySheSummonsOneThatFollowsHer_WithHerGossip()
    {
        using Rig rig = SimoneRig();
        Tick(rig, 100);

        Creature precious = Assert.Single(All(rig, SimoneTheInconspicuousAI.NpcPrecious));
        Assert.True(precious.Motion.HasActive(MovementGeneratorType.Follow));
        Assert.Equal((uint)NpcFlags.Gossip, rig.Npc.NpcFlags);
    }

    [Fact]
    public void Simone_APreciousWithinAHundredYardsIsTakenInsteadOfANewOne()
    {
        using Rig rig = SimoneRig(spawnPrecious: true);
        Tick(rig, 100);

        Creature precious = Assert.Single(All(rig, SimoneTheInconspicuousAI.NpcPrecious));
        Assert.NotNull(precious.Spawn);
    }

    [Fact]
    public void Simone_TheChoiceStopsHer_TenSecondsLaterTheSeductressAndTheDevourerReplaceHerAndPrecious_SheRespawnsIn35Minutes()
    {
        using Rig rig = SimoneRig();
        var ai = rig.Ai<SimoneTheInconspicuousAI>();
        Tick(rig, 100);
        Creature precious = Assert.Single(All(rig, SimoneTheInconspicuousAI.NpcPrecious));

        ai.BeginEvent(rig.Player);
        Assert.True(ai.IsTransforming);
        Assert.Equal(0u, rig.Npc.NpcFlags);
        Tick(rig, 9900);
        Assert.Empty(All(rig, SimoneTheInconspicuousAI.NpcSimoneTheSeductress));

        rig.RunUntil(() => All(rig, SimoneTheInconspicuousAI.NpcSimoneTheSeductress).Length == 1, 1000);

        Creature demon = All(rig, SimoneTheInconspicuousAI.NpcSimoneTheSeductress)[0];
        Creature devourer = Devourer(rig);
        var seductress = Assert.IsType<SimoneTheSeductressAI>(demon.AI);
        Assert.Equal(rig.Npc.Guid, seductress.SimoneGuid);
        Assert.Equal(devourer.Guid, seductress.PreciousGuid);
        Assert.Equal(demon.Guid, Assert.IsType<PreciousTheDevourerAI>(devourer.AI).SimoneGuid);
        Assert.False(rig.Npc.IsAlive);
        Assert.False(precious.IsAlive && rig.System.Creatures.Contains(precious));
        Assert.InRange(RespawnIn(rig), (SimoneTheInconspicuousAI.RespawnWhileDemonSeconds * 1000L) - 1000, SimoneTheInconspicuousAI.RespawnWhileDemonSeconds * 1000L);
    }

    [Fact]
    public void Seductress_HerDeathMovesSimonesRespawnToTwoHours()
    {
        using Rig rig = SimoneRig();
        Creature demon = Transform(rig);
        demon.AI!.AttackStart(rig.Player);
        Tick(rig, 300);
        Assert.True(demon.IsAlive);

        rig.Map.Combat.Kill(rig.Player, demon);

        Assert.InRange(RespawnIn(rig), (StaveDemonAI.RespawnAfterDeathSeconds * 1000L) - 1000, StaveDemonAI.RespawnAfterDeathSeconds * 1000L);
    }

    [Fact]
    public void Seductress_ANonHunterMakesHerAndTheDevourerVanish_TheCleanerAppears_SimoneIn15Minutes()
    {
        using Rig rig = SimoneRig(Class.Warrior);
        Creature demon = Transform(rig);
        Creature devourer = Devourer(rig);
        demon.AI!.AttackStart(rig.Player);

        rig.RunUntil(() => !demon.IsAlive || !rig.System.Creatures.Contains(demon), 2000);

        Assert.False(devourer.IsAlive && rig.System.Creatures.Contains(devourer));
        Assert.NotEmpty(All(rig, StaveDemonAI.NpcTheCleaner));
        Assert.InRange(RespawnIn(rig), (StaveDemonAI.RespawnAfterVanishSeconds * 1000L) - 1000, StaveDemonAI.RespawnAfterVanishSeconds * 1000L);
    }

    [Fact]
    public void Seductress_ASecondUnitOnPreciousThreatListMakesThemVanishWithinTheTwoSecondCheck()
    {
        using Rig rig = SimoneRig();
        Creature demon = Transform(rig);
        Creature devourer = Devourer(rig);
        demon.AI!.AttackStart(rig.Player);
        Tick(rig, 300);
        Assert.True(demon.IsAlive);
        (Player helper, _) = AddPlayer(rig.World, 2, devourer.X - 1f, devourer.Y);
        helper.MaxHealth = HugeHealth;
        helper.Health = HugeHealth;
        devourer.Combat.Threat.AddThreat(rig.Player, 1f);
        devourer.Combat.Threat.AddThreat(helper, 1f);

        rig.RunUntil(() => !demon.IsAlive || !rig.System.Creatures.Contains(demon), 6000);

        Assert.NotEmpty(All(rig, StaveDemonAI.NpcTheCleaner));
    }

    [Fact]
    public void Seductress_TemptressKissAndChainLightningThreeToSixSecondsIn_ViperStingSilencesHerWithTheEmote()
    {
        using Rig rig = SimoneRig();
        Creature demon = Transform(rig);
        demon.AI!.AttackStart(rig.Player);
        rig.Caster.Casts.Clear();
        rig.Session.Clear();

        Tick(rig, 6300);
        Assert.Single(rig.Caster.Casts, c => c.Spell == SimoneTheSeductressAI.SpellTemptressKiss && ReferenceEquals(c.Target, rig.Player));
        Assert.Single(rig.Caster.Casts, c => c.Spell == SimoneTheSeductressAI.SpellChainLightning && ReferenceEquals(c.Target, rig.Player));

        demon.AI!.OnSpellHit(rig.Player, new SpellInfo { Id = 3034 }); // Viper Sting rank 1: nothing
        Assert.DoesNotContain(rig.Caster.Casts, c => c.Spell == SimoneTheSeductressAI.SpellSilence);
        demon.AI!.OnSpellHit(rig.Player, new SpellInfo { Id = SimoneTheSeductressAI.SpellViperStingRank3 });
        Assert.Single(rig.Caster.Casts, c => c.Spell == SimoneTheSeductressAI.SpellSilence);
        Assert.Contains(SimoneTheSeductressAI.EmoteSilence, Ids(rig));
    }

    [Fact]
    public void Seductress_BackHomeWithoutALivingPreciousSheBringsANewDevourer()
    {
        using Rig rig = SimoneRig();
        Creature demon = Transform(rig);
        Creature devourer = Devourer(rig);
        rig.Map.Combat.Kill(rig.Player, devourer);
        var ai = Assert.IsType<SimoneTheSeductressAI>(demon.AI);

        ai.OnReachedHome();

        Creature fresh = Devourer(rig);
        Assert.NotSame(devourer, fresh);
        Assert.Equal(fresh.Guid, ai.PreciousGuid);
        Assert.Equal(demon.Guid, Assert.IsType<PreciousTheDevourerAI>(fresh.AI).SimoneGuid);
    }

    [Fact]
    public void Devourer_EvadingWithItsSimoneDeadItGoes()
    {
        using Rig rig = SimoneRig();
        Creature demon = Transform(rig);
        Creature devourer = Devourer(rig);
        rig.Map.Combat.Kill(rig.Player, demon);

        devourer.AI!.OnEvade();

        Assert.False(devourer.IsAlive && rig.System.Creatures.Contains(devourer));
    }

    [Fact]
    public void SetRespawnIn_OnlyMovesADeadDatabaseCreature()
    {
        using Rig rig = SimoneRig();
        Assert.False(rig.System.SetRespawnIn(rig.Npc, 60));

        rig.Map.Combat.Kill(rig.Player, rig.Npc);
        Assert.True(rig.System.SetRespawnIn(rig.Npc, 60));

        Assert.Equal(60_000L, RespawnIn(rig));
    }
}
