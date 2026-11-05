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
/// Spell lists on the map: which creatures get a scheduler, the combat arming, the tick, a script's list change, the reset and the row-driven target rules
/// (cmangos Creature::SetSpellList, UnitAI::EnterCombat, UnitAI::SpellListChanged, UnitAI::ChooseTarget).
/// </summary>
public sealed class CreatureSpellListWiringTests
{
    private const uint ListId = 1000;
    private const uint OtherListId = 1001;
    private const uint Bolt = 7001;
    private const uint Nova = 7002;
    private const uint NoAutocast = 7003;
    private const uint Area = 7004;

    /// <summary>A caster that knows a few spells and records what the list casts.</summary>
    private sealed class ListCaster : ICreatureSpellCaster
    {
        public List<(uint Spell, Unit? Target)> Casts { get; } = [];

        public CreatureCastResult Result { get; set; } = CreatureCastResult.Ok;

        public event Action<Unit, Unit, SpellInfo>? SpellHit
        {
            add { }
            remove { }
        }

        public SpellInfo? FindSpell(uint spellId) => spellId switch
        {
            Bolt or Nova => new SpellInfo { Id = spellId, Range = new SpellRange(0f, 100f) },
            NoAutocast => new SpellInfo { Id = spellId, Range = new SpellRange(0f, 100f), AttributesEx = SpellAttributesEx.NoAutocastAi },
            Area => new SpellInfo { Id = spellId, Range = new SpellRange(0f, 100f), Effects = [new SpellEffectInfo { Effect = SpellEffectName.Dummy, Radius = 100f }] },
            _ => null,
        };

        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
        {
            Casts.Add((spellId, target));
            return Result;
        }

        public bool IsCasting(Creature caster) => false;

        public bool HasAura(Unit unit, uint spellId) => false;

        public void Interrupt(Creature caster)
        {
        }

        public void OnCreatureRemoved(Creature creature)
        {
        }
    }

    private static CreatureSpellListEntry Entry(uint position, uint spell, uint initial = 0, uint repeat = 5000, uint target = CreatureSpellListTargets.Current)
        => new(position, spell, CreatureSpellListFlags.None, target, 0, 100, 0, initial, initial, repeat, repeat);

    private static CreatureContent Content(string aiName, uint templateList, CreatureSpellListCatalog catalog, IEnumerable<CreatureAiEvent>? events = null)
        => new(
            [Template(configure: t => t.AIName = aiName) with { SpellList = templateList }],
            [Spawn(1, WolfEntry, 5, 0)], [], [], [],
            new CreatureAiContent(events ?? [], []) { SpellLists = catalog });

    private static CreatureSpellListCatalog Catalog(params CreatureSpellList[] lists) => new(lists);

    private static (WorldRuntime World, Map Map, CreatureMapSystem System, Creature Wolf, Player Player, ListCaster Caster) Start(CreatureContent content, CreatureOptions? options = null)
    {
        var caster = new ListCaster();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = caster }, options);
        (Player player, _) = AddPlayer(world, 1, 50, 0);
        return (world, map, system, Assert.Single(system.Creatures), player, caster);
    }

    [Fact]
    public void Spawn_BindsTheTemplateList_AndACreatureWithoutOneHasNoScheduler()
    {
        CreatureSpellListCatalog catalog = Catalog(new CreatureSpellList(ListId, "test", 0, 0, [Entry(1, Bolt)]));

        (WorldRuntime listed, _, _, Creature wolf, _, _) = Start(Content(CreatureAiFactory.EventAIName, ListId, catalog));
        using (listed)
        {
            Assert.Equal(ListId, wolf.SpellLists?.List?.Id);
        }

        (WorldRuntime plain, _, _, Creature other, _, _) = Start(Content(CreatureAiFactory.EventAIName, 0, catalog));
        using (plain)
        {
            Assert.Null(other.SpellLists);
        }
    }

    [Fact]
    public void Combat_ArmsInitialCooldowns_ThenTheListCastsAtTheVictim()
    {
        CreatureSpellListCatalog catalog = Catalog(new CreatureSpellList(ListId, "test", 0, 0, [Entry(1, Bolt, initial: 3000)]));
        (WorldRuntime world, Map map, _, Creature wolf, Player player, ListCaster caster) = Start(Content(CreatureAiFactory.EventAIName, ListId, catalog));
        using (world)
        {
            Run(world, 5000);
            Assert.Empty(caster.Casts); // out of combat nothing runs

            map.Combat.DealDamage(player, wolf, 1, direct: false);
            Assert.Same(player, wolf.Combat.Victim);
            Run(world, 2000);
            Assert.Empty(caster.Casts); // the initial cooldown holds it
            Run(world, 2500);
            Assert.Equal([(Bolt, (Unit?)player)], caster.Casts);
        }
    }

    [Fact]
    public void SpellTheAiMayNotAutocast_AndUnknownSpells_AreNotCast()
    {
        CreatureSpellListCatalog catalog = Catalog(new CreatureSpellList(ListId, "test", 0, 0, [Entry(1, NoAutocast), Entry(2, 9999), Entry(3, Nova)]));
        (WorldRuntime world, Map map, _, Creature wolf, Player player, ListCaster caster) = Start(Content(CreatureAiFactory.EventAIName, ListId, catalog));
        using (world)
        {
            map.Combat.DealDamage(player, wolf, 1, direct: false);
            Run(world, 1500);
            Assert.Equal([Nova], caster.Casts.Select(c => c.Spell));
        }
    }

    [Fact]
    public void SetSpellList_InCombat_ArmsTheNewList_AnUnknownIdKeepsIt_AndZeroClears()
    {
        CreatureSpellListCatalog catalog = Catalog(
            new CreatureSpellList(ListId, "first", 0, 0, [Entry(1, Bolt, initial: 60000)]),
            new CreatureSpellList(OtherListId, "second", 0, 0, [Entry(1, Nova, initial: 2000)]));
        (WorldRuntime world, Map map, CreatureMapSystem system, Creature wolf, Player player, ListCaster caster) = Start(Content(CreatureAiFactory.EventAIName, ListId, catalog));
        using (world)
        {
            map.Combat.DealDamage(player, wolf, 1, direct: false);

            Assert.True(system.SetSpellList(wolf, OtherListId));
            Assert.Equal(OtherListId, wolf.SpellLists?.List?.Id);
            Run(world, 1500);
            Assert.Empty(caster.Casts); // armed at the switch, not ready at once
            Run(world, 2000);
            Assert.Equal([Nova], caster.Casts.Select(c => c.Spell));

            Assert.False(system.SetSpellList(wolf, 424242));
            Assert.Equal(OtherListId, wolf.SpellLists?.List?.Id);

            Assert.True(system.SetSpellList(wolf, 0));
            Assert.Null(wolf.SpellLists);
        }
    }

    [Fact]
    public void EventAiAction60_SwitchesTheList()
    {
        CreatureSpellListCatalog catalog = Catalog(new CreatureSpellList(OtherListId, "second", 0, 0, [Entry(1, Nova)]));
        CreatureAiEvent row = new()
        {
            Id = 1,
            CreatureId = WolfEntry,
            EventType = (byte)EventAiEventType.Spawned,
            Chance = 100,
            Action1 = new CreatureAiAction((byte)EventAiActionType.SetSpellSet, (int)OtherListId, 0, 0),
        };
        (WorldRuntime world, _, _, Creature wolf, _, _) = Start(Content(CreatureAiFactory.EventAIName, 0, catalog, [row]));
        using (world)
        {
            Assert.IsType<CreatureEventAI>(wolf.AI).OnRespawn();
            Assert.Equal(OtherListId, wolf.SpellLists?.List?.Id);
        }
    }

    [Fact]
    public void RangedEntries_BecomeMainSpellsOfTheEventAi()
    {
        CreatureSpellListEntry ranged = new(1, Bolt, CreatureSpellListFlags.RangedAction, CreatureSpellListTargets.Current, 0, 100, 0, 0, 0, 0, 0);
        CreatureSpellListCatalog catalog = Catalog(new CreatureSpellList(ListId, "test", 0, 100, [ranged, Entry(2, Nova)]));
        (WorldRuntime world, _, _, Creature wolf, _, _) = Start(Content(CreatureAiFactory.EventAIName, ListId, catalog));
        using (world)
        {
            var ai = Assert.IsType<CreatureEventAI>(wolf.AI);
            Assert.True(ai.IsMainSpell(Bolt));
            Assert.False(ai.IsMainSpell(Nova));
        }
    }

    [Fact]
    public void Reload_RebindsOutOfCombat()
    {
        CreatureContent content = Content(CreatureAiFactory.EventAIName, ListId, Catalog(new CreatureSpellList(ListId, "old", 0, 0, [Entry(1, Bolt)])));
        (WorldRuntime world, _, _, Creature wolf, _, _) = Start(content);
        using (world)
        {
            content.SwapDefinitions(Content(CreatureAiFactory.EventAIName, ListId, Catalog(new CreatureSpellList(ListId, "new", 0, 0, [Entry(1, Nova)]))));
            Run(world, 200);
            Assert.Equal("new", wolf.SpellLists?.List?.Name);
        }
    }

    [Fact]
    public void AttackRow_WithTheEffectRadiusFlag_NeedsAUnitInsideTheRadius_AndCastsWithoutATarget()
    {
        // Row shaped like the reference's USE_EFFECT_RADIUS row: the check needs a unit inside the radius, the cast goes out with no target.
        var row = new CreatureSpellListTargeting(103, CreatureSpellListTargetingType.Attack, 0, 0, 0x2000, -1);
        CreatureSpellListCatalog catalog = new(
            [new CreatureSpellList(ListId, "test", 0, 0, [Entry(1, Bolt, target: 103), Entry(2, Area, target: 103)])],
            [row]);
        (WorldRuntime world, Map map, _, Creature wolf, Player player, ListCaster caster) = Start(Content(CreatureAiFactory.EventAIName, ListId, catalog));
        using (world)
        {
            map.Combat.DealDamage(player, wolf, 1, direct: false);
            Run(world, 1500);
            Assert.Equal([(Area, (Unit?)null)], caster.Casts); // Bolt has no effect radius, so no unit is inside it
        }
    }

    [Fact]
    public void ScriptAi_DoesNotRunATemplateList()
    {
        var factory = RecorderFactory();
        CreatureSpellListCatalog catalog = Catalog(new CreatureSpellList(ListId, "test", 0, 0, [Entry(1, Bolt)]));
        var caster = new ListCaster();
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(Content(RecorderName, ListId, catalog), new CreatureAiServices { Spells = caster, Factory = factory });
        using (world)
        {
            AddPlayer(world, 1, 50, 0);
            Creature wolf = Assert.Single(system.Creatures);
            Assert.Null(wolf.SpellLists);
            Assert.False(system.SetSpellList(wolf, ListId));
        }
    }

    [Fact]
    public void Evade_ForgetsCooldowns_SoTheNextFightArmsAgain()
    {
        CreatureSpellListCatalog catalog = Catalog(new CreatureSpellList(ListId, "test", 0, 0, [Entry(1, Bolt, initial: 0, repeat: 600000)]));
        (WorldRuntime world, Map map, CreatureMapSystem system, Creature wolf, Player player, ListCaster caster) = Start(Content(CreatureAiFactory.EventAIName, ListId, catalog));
        using (world)
        {
            map.Combat.DealDamage(player, wolf, 1, direct: false);
            Run(world, 1500);
            Assert.Single(caster.Casts);

            system.EnterEvadeMode(wolf);
            Run(world, 20000);
            Assert.False(wolf.IsEvading);
            Assert.Single(caster.Casts);

            map.Combat.DealDamage(player, wolf, 1, direct: false);
            Run(world, 1500);
            Assert.Equal(2, caster.Casts.Count); // the ten minute repeat is gone
        }
    }
}
