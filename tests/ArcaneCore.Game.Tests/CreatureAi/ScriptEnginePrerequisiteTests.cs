using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// The engine prerequisites of the dungeon script lanes: a ScriptName AI that hosts the entry's EventAI rows, the dormant Stoned state
/// (aura 10255) and the gossip/quest-only script coverage.
/// </summary>
public sealed class ScriptEnginePrerequisiteTests
{
    private const string ScriptName = "test_stoned_add";
    private const uint Stoned = StonedDormancy.StonedAura;
    private const uint Whirlwind = 17207;
    private const uint GuardsDestroyed = 10666;

    /// <summary>A spell caster whose Stoned self-cast lands as an aura, like the real spell system's.</summary>
    private sealed class AuraCaster : ICreatureSpellCaster
    {
        public List<(uint Spell, Unit? Target, bool Triggered)> Casts { get; } = [];

        public HashSet<(Unit, uint)> Auras { get; } = [];

        public event Action<Unit, Unit, SpellInfo>? SpellHit
        {
            add { }
            remove { }
        }

        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
        {
            Casts.Add((spellId, target, triggered));
            if (spellId == Stoned && ReferenceEquals(target, caster))
            {
                Auras.Add((caster, spellId));
            }

            return CreatureCastResult.Ok;
        }

        public bool IsCasting(Creature caster) => false;

        public bool HasAura(Unit unit, uint spellId) => Auras.Contains((unit, spellId));

        public void RemoveAuras(Unit unit, uint spellId) => Auras.Remove((unit, spellId));

        public void Interrupt(Creature caster)
        {
        }

        public void OnCreatureRemoved(Creature creature)
        {
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig(float playerX = 60)
        {
            CreatureAiAction Cast(uint spell, int flags = 0) => new((byte)EventAiActionType.Cast, (int)spell, (int)EventAiTarget.Self, flags);
            CreatureAiEvent Row(uint id, EventAiEventType type, byte flags, CreatureAiAction action, int p1 = 0, int p2 = 0, int p3 = 0, int p4 = 0)
                => new() { Id = id, CreatureId = WolfEntry, EventType = (byte)type, Param1 = p1, Param2 = p2, Param3 = p3, Param4 = p4, Chance = 100, Flags = flags, Action1 = action };

            // The shape of the fixture rows of the Archaedas minions: Stoned on spawn and on reaching home, a combat ability, a death cast.
            CreatureAiEvent[] rows =
            [
                Row(1, EventAiEventType.Spawned, 0, Cast(Stoned)),
                Row(2, EventAiEventType.ReachedHome, 0, Cast(Stoned)),
                Row(3, EventAiEventType.Death, 0, Cast(GuardsDestroyed, CreatureEventAI.CastTriggered)),
                Row(4, EventAiEventType.TimerInCombat, CreatureEventAI.FlagRepeatable, Cast(Whirlwind), 500, 500, 1000, 1000),
            ];
            var content = new CreatureContent(
                [Template(configure: t => t.AIName = CreatureAiFactory.EventAIName) with { ScriptName = ScriptName }],
                [Spawn(1, WolfEntry, 0, 0)], [], [], [], new CreatureAiContent(rows, []));
            var factory = new CreatureAiFactory();
            factory.RegisterEventAiScript(ScriptName, (c, ai) => new StonedEventAI(c, ai));
            (World, Map, System) = CreateAiSystem(content, new CreatureAiServices { Factory = factory, Spells = Spells, Hostility = new AlwaysHostile() });
            (Player, _) = AddPlayer(World, 1, playerX, 0);
            Wolf = Assert.Single(System.Creatures);
            Ai = Assert.IsAssignableFrom<StonedEventAI>(Wolf.AI);
            Ai.OnRespawn(); // the spawn row casts Stoned
        }

        public AuraCaster Spells { get; } = new();

        public WorldRuntime World { get; }

        public Map Map { get; }

        public CreatureMapSystem System { get; }

        public Player Player { get; }

        public Creature Wolf { get; }

        public StonedEventAI Ai { get; }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void AScriptNameAiThatHostsEventAi_StillFiresTheEntrysRows()
    {
        using var rig = new Rig();

        // The ScriptName AI is a CreatureEventAI: the engine, the row count and the supported-row report are the plain EventAI's.
        Assert.IsAssignableFrom<CreatureEventAI>(rig.Ai);
        Assert.Equal(4, rig.Ai.EventCount);
        Assert.Empty(rig.Ai.Unsupported);
        Assert.Contains(rig.Spells.Casts, c => c.Spell == Stoned && ReferenceEquals(c.Target, rig.Wolf)); // SPAWNED row

        rig.Spells.RemoveAuras(rig.Wolf, Stoned);
        rig.Map.Combat.DealDamage(rig.Player, rig.Wolf, 1, direct: false);
        Run(rig.World, 2500);
        Assert.Contains(rig.Spells.Casts, c => c.Spell == Whirlwind && ReferenceEquals(c.Target, rig.Wolf)); // in-combat timer row

        rig.Spells.Casts.Clear();
        rig.Ai.OnReachedHome();
        Assert.Contains(rig.Spells.Casts, c => c.Spell == Stoned); // REACHED_HOME row

        rig.Map.Combat.DealDamage(rig.Player, rig.Wolf, rig.Wolf.Health, direct: false);
        Assert.Contains(rig.Spells.Casts, c => c.Spell == GuardsDestroyed && c.Triggered); // DEATH row
    }

    [Fact]
    public void TheScriptNameAi_IsWhatTheFactoryBuilds_AndANameCannotBeRegisteredTwice()
    {
        using var rig = new Rig();
        Assert.Equal(typeof(StonedEventAI), rig.Wolf.AI!.GetType());
        Assert.Throws<InvalidOperationException>(() => new CreatureAiFactory().RegisterEventAiScript("EventAI", (c, ai) => new CreatureEventAI(c, ai)));
    }

    [Fact]
    public void AStonedCreature_IsInert_NoAggro_NoMovement_NotAttackable()
    {
        using var rig = new Rig(playerX: 60);
        rig.Ai.OnUpdate(100);
        Assert.True(rig.Ai.IsDormant);
        Assert.Equal(StonedDormancy.DormantFlags, rig.Wolf.UnitFlags & StonedDormancy.DormantFlags);
        Assert.True(rig.Wolf.AiImmobilized);

        // A hostile player 3 yd away for 10 s: no aggro, no chase, and the player cannot attack it.
        (Player near, _) = AddPlayer(rig.World, 2, 3, 0);
        float x = rig.Wolf.X, y = rig.Wolf.Y;
        Run(rig.World, 10_000);
        Assert.Null(rig.Wolf.Combat.Victim);
        Assert.False(rig.Wolf.Combat.IsInCombat);
        Assert.Equal((x, y), (rig.Wolf.X, rig.Wolf.Y));
        Assert.False(rig.Map.Combat.Hooks.CanAttack(near, rig.Wolf));
        Assert.False(rig.Ai.AttackStart(near)); // even a script order is refused
        Assert.Null(rig.Wolf.Combat.Victim);
        rig.Ai.OnAttackedBy(near);
        Assert.Null(rig.Wolf.Combat.Victim);
    }

    [Fact]
    public void RemovingTheAura_WakesTheCreature_AndAnAttackOrderAtOnceTakes()
    {
        using var rig = new Rig();
        rig.Ai.OnUpdate(100);
        Assert.True(rig.Ai.IsDormant);

        // The Archaedas awakening: remove the aura, then order an attack in the same call, before any tick.
        rig.Spells.RemoveAuras(rig.Wolf, Stoned);
        Assert.True(rig.Ai.AttackStart(rig.Player));
        Assert.False(rig.Ai.IsDormant);
        Assert.Same(rig.Player, rig.Wolf.Combat.Victim);
        Assert.Equal(UnitFlags.None, rig.Wolf.UnitFlags & StonedDormancy.DormantFlags);
        Assert.False(rig.Wolf.AiImmobilized);
        Assert.True(rig.Map.Combat.Hooks.CanAttack(rig.Player, rig.Wolf));
    }

    [Fact]
    public void Waking_RaisesWokeOnce()
    {
        using var rig = new Rig();
        int woke = 0;
        var dormancy = new StonedDormancy(rig.Wolf);
        dormancy.Woke += () => woke++;
        Assert.True(dormancy.Sync());
        rig.Spells.RemoveAuras(rig.Wolf, Stoned);
        Assert.False(dormancy.Sync());
        Assert.False(dormancy.Sync());
        Assert.Equal(1, woke);
    }

    [Fact]
    public void ReachingHome_RecastsStoned_AndTheCreatureIsDormantAgain()
    {
        using var rig = new Rig();
        rig.Ai.OnUpdate(100);
        rig.Spells.RemoveAuras(rig.Wolf, Stoned);
        rig.Ai.OnUpdate(100);
        Assert.False(rig.Ai.IsDormant);

        rig.Ai.OnReachedHome(); // the REACHED_HOME row casts Stoned, which lands as the aura
        Assert.True(rig.Ai.IsDormant);
        Assert.True((rig.Wolf.UnitFlags & UnitFlags.NotSelectable) != 0);
    }

    [Fact]
    public void ADormantCreatureThatDies_LeavesNoDormantFlagsBehind()
    {
        using var rig = new Rig();
        rig.Ai.OnUpdate(100);
        Assert.True(rig.Ai.IsDormant);
        rig.Ai.OnDeath(null);
        Assert.Equal(UnitFlags.None, rig.Wolf.UnitFlags & StonedDormancy.DormantFlags);
        Assert.False(rig.Wolf.AiImmobilized);
    }

    [Fact]
    public void Dormancy_KeepsATemplateFlagItDidNotAdd()
    {
        using var rig = new Rig();
        rig.Spells.RemoveAuras(rig.Wolf, Stoned);
        rig.Ai.OnUpdate(100);
        Assert.False(rig.Ai.IsDormant);
        rig.Wolf.UnitFlags |= UnitFlags.NotSelectable; // already set before the aura lands
        rig.Spells.Auras.Add((rig.Wolf, Stoned));
        rig.Ai.OnUpdate(100);
        Assert.True(rig.Ai.IsDormant);
        rig.Spells.RemoveAuras(rig.Wolf, Stoned);
        rig.Ai.OnUpdate(100);
        Assert.True((rig.Wolf.UnitFlags & UnitFlags.NotSelectable) != 0);
        Assert.Equal(UnitFlags.None, rig.Wolf.UnitFlags & (UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc));
    }

    [Fact]
    public void ARespawnWhileDormant_ReEntersDormancyOnTheReinitializedCreature()
    {
        using var rig = new Rig();
        rig.Ai.OnUpdate(100);
        Assert.True(rig.Ai.IsDormant);

        // What a respawn does to the creature before the AI hears of it: flags and immobilization back to the template's, the auras gone.
        rig.Wolf.UnitFlags &= ~StonedDormancy.DormantFlags;
        rig.System.SetAiImmobilized(rig.Wolf, false, combatOnly: false);
        rig.Spells.Auras.Clear();

        rig.Ai.OnRespawn(); // the SPAWNED row casts Stoned again

        // Without Forget() the helper still believes it is dormant, never re-applies the flags, and the creature stands there attackable.
        Assert.True(rig.Ai.IsDormant);
        Assert.Equal(StonedDormancy.DormantFlags, rig.Wolf.UnitFlags & StonedDormancy.DormantFlags);
        Assert.True(rig.Wolf.AiImmobilized);

        rig.Spells.RemoveAuras(rig.Wolf, Stoned);
        Assert.False(rig.Ai.IsDormant);
        Assert.Equal(UnitFlags.None, rig.Wolf.UnitFlags & StonedDormancy.DormantFlags);
        Assert.False(rig.Wolf.AiImmobilized);
    }

    // --- script catalogue: gossip and quest scripts ---------------------------------------------------------

    private static CreatureTemplate T(uint entry, string script) => new() { Entry = entry, Name = "T" + entry, ScriptName = script, AIName = "EventAI" };

    private sealed class StubGossip : ArcaneCore.Game.Npc.INpcGossipScript
    {
        public ArcaneCore.Game.Npc.ScriptedGossipMenu? Hello(Player player, ArcaneCore.Game.Npc.NpcInfo npc) => null;

        public uint Select(Player player, ArcaneCore.Game.Npc.NpcInfo npc, uint sender, uint action) => 0;
    }

    private sealed class StubQuest : INpcQuestScript
    {
    }

    [Theory]
    [InlineData(ScriptKind.GossipScript)]
    [InlineData(ScriptKind.QuestScript)]
    public void AGossipOrQuestOnlyScript_CountsAsImplemented_OnlyBecauseAHandlerIsRegistered(ScriptKind kind)
    {
        var factory = new CreatureAiFactory();
        CreatureTemplate template = T(990_999, "npc_test_only"); // an entry with no entry script (9999, used before, is Ringo since zone scripts batch 6)
        var catalog = new ScriptCatalog(factory, new CreatureContent([template], [Spawn(1, 990_999, 1, 1, mapId: 70)], [], [], []));

        Assert.True(catalog.Resolve(template, 70).ScriptNameMissing); // unknown to every layer

        if (kind == ScriptKind.GossipScript)
        {
            factory.RegisterNpcScript("npc_test_only", "TestImplementation", gossip: new StubGossip());
        }
        else
        {
            factory.RegisterNpcScript("npc_test_only", "TestImplementation", quest: new StubQuest());
        }

        ScriptResolution resolved = catalog.Resolve(template, 70);
        Assert.Equal(kind, resolved.Kind);
        Assert.Equal("TestImplementation", resolved.Implementation);
        Assert.True(resolved.ScriptNameResolved);

        ScriptCoverageSummary summary = ScriptCoverageSummary.Of(catalog.Coverage(70));
        Assert.Equal((1, 1, 0), (summary.ScriptNames, summary.ScriptNamesResolved, summary.ScriptNamesMissing));
    }

    [Fact]
    public void ANpcScriptName_ClashesFailClosed_InEveryDirection()
    {
        var factory = new CreatureAiFactory();
        factory.Register("npc_has_ai", c => new AggressorAI(c));
        factory.RegisterEventAiScript("npc_has_event_ai", (c, ai) => new CreatureEventAI(c, ai));
        Assert.Throws<InvalidOperationException>(() => factory.RegisterNpcScript("npc_has_ai", "X", gossip: new StubGossip()));
        Assert.Throws<InvalidOperationException>(() => factory.RegisterNpcScript("npc_has_event_ai", "X", gossip: new StubGossip()));
        factory.RegisterNpcScript("npc_once", "X", gossip: new StubGossip());
        Assert.Throws<InvalidOperationException>(() => factory.RegisterNpcScript("npc_once", "Y", quest: new StubQuest()));
        Assert.Throws<ArgumentException>(() => factory.RegisterNpcScript("npc_no_handler", "Z")); // a string alone is no implementation

        // an AI registered under a name an npc script already owns
        Assert.Throws<InvalidOperationException>(() => factory.Register("npc_once", c => new AggressorAI(c)));
        Assert.Throws<InvalidOperationException>(() => factory.RegisterEventAiScript("NPC_ONCE", (c, ai) => new CreatureEventAI(c, ai)));
    }

    [Fact]
    public void AnEventAiScript_KeepsTheGuardSightRulesOfAGuardTemplate()
    {
        CreatureContent Content(uint extraFlags) => new(
            [Template(configure: t => t.AIName = CreatureAiFactory.EventAIName) with { ScriptName = ScriptName, ExtraFlags = extraFlags }],
            [Spawn(1, WolfEntry, 0, 0)], [], [], [], new CreatureAiContent([], []));

        foreach ((uint extraFlags, bool guardRules) in new[] { (0x400u, true), (0u, false) }) // 0x400: the GUARD extra flag
        {
            var factory = new CreatureAiFactory();
            factory.RegisterEventAiScript(ScriptName, (c, ai) => new StonedEventAI(c, ai));
            (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(Content(extraFlags), new CreatureAiServices { Factory = factory });
            using (world)
            {
                AddPlayer(world, 1, 60, 0); // brings the spawn in range
                var ai = Assert.IsAssignableFrom<CreatureEventAI>(Assert.Single(system.Creatures).AI);
                Assert.Equal(guardRules, ai.UsesGuardSightRules); // as the plain EventAI entry sets it (vmangos GuardEventAI::Permissible)
            }
        }
    }
}
