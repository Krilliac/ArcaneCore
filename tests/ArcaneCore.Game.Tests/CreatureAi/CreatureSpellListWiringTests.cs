using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Rules;
using ArcaneCore.Game.Rules.Creatures;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.Rules;
using ArcaneCore.Kernel.Rules.Creatures;
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
    private const uint OtherEntry = 9101;
    private const uint PlainEntry = 9102;

    /// <summary>A caster that knows a few spells and records what the list casts.</summary>
    internal sealed class ListCaster : ICreatureSpellCaster
    {
        public List<(uint Spell, Unit? Target)> Casts { get; } = [];

        public HashSet<(Unit Unit, uint Spell)> Auras { get; } = [];

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

        public bool HasAura(Unit unit, uint spellId) => Auras.Contains((unit, spellId));

        public void Interrupt(Creature caster)
        {
        }

        public void OnCreatureRemoved(Creature creature)
        {
        }
    }

    internal static CreatureSpellListEntry Entry(uint position, uint spell, uint initial = 0, uint repeat = 5000, uint target = CreatureSpellListTargets.Current)
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

    private static CreatureContent Entries(uint templateList, uint otherList, CreatureSpellListCatalog catalog, IEnumerable<CreatureAiEvent>? events = null)
        => new(
            [
                Template(configure: t => t.AIName = CreatureAiFactory.EventAIName) with { SpellList = templateList },
                Template(OtherEntry, t => t.AIName = CreatureAiFactory.EventAIName) with { SpellList = otherList },
                Template(PlainEntry, t => t.AIName = CreatureAiFactory.EventAIName),
            ],
            [Spawn(1, WolfEntry, 5, 0)], [], [], [],
            new CreatureAiContent(events ?? [], []) { SpellLists = catalog });

    [Fact]
    public void UpdateEntry_TakesTheNewTemplatesList_AndATemplateWithoutOneClearsIt()
    {
        CreatureSpellListCatalog catalog = Catalog(
            new CreatureSpellList(ListId, "first", 0, 0, [Entry(1, Bolt)]),
            new CreatureSpellList(OtherListId, "second", 0, 0, [Entry(1, Nova)]));
        (WorldRuntime world, _, CreatureMapSystem system, Creature wolf, _, _) = Start(Entries(ListId, OtherListId, catalog));
        using (world)
        {
            Assert.True(system.UpdateEntry(wolf, OtherEntry));
            Assert.Equal(OtherListId, wolf.SpellLists?.List?.Id);

            Assert.True(system.UpdateEntry(wolf, PlainEntry));
            Assert.Null(wolf.SpellLists);
        }
    }

    [Fact]
    public void UpdateEntry_InCombat_ArmsTheNewList_AndTheOldListCastsNoMore()
    {
        CreatureSpellListCatalog catalog = Catalog(
            new CreatureSpellList(ListId, "first", 0, 0, [Entry(1, Bolt)]),
            new CreatureSpellList(OtherListId, "second", 0, 0, [Entry(1, Nova, initial: 2000)]));
        (WorldRuntime world, Map map, CreatureMapSystem system, Creature wolf, Player player, ListCaster caster) = Start(Entries(ListId, OtherListId, catalog));
        using (world)
        {
            map.Combat.DealDamage(player, wolf, 1, direct: false);

            Assert.True(system.UpdateEntry(wolf, OtherEntry));
            Run(world, 1500);
            Assert.Empty(caster.Casts); // armed at the change, not ready at once
            Run(world, 2000);
            Assert.Equal([(Nova, (Unit?)player)], caster.Casts);
        }
    }

    [Fact]
    public void EventAiAction36_TakesTheListOfTheNewEntry()
    {
        CreatureSpellListCatalog catalog = Catalog(new CreatureSpellList(OtherListId, "second", 0, 0, [Entry(1, Nova)]));
        CreatureAiEvent row = new()
        {
            Id = 1,
            CreatureId = WolfEntry,
            EventType = (byte)EventAiEventType.Spawned,
            Chance = 100,
            Action1 = new CreatureAiAction(36, (int)OtherEntry, 0, 0),
        };
        (WorldRuntime world, _, _, Creature wolf, _, _) = Start(Entries(0, OtherListId, catalog, [row]));
        using (world)
        {
            Assert.IsType<CreatureEventAI>(wolf.AI).OnRespawn();
            Assert.Equal(OtherEntry, wolf.Template.Entry);
            Assert.Equal(OtherListId, wolf.SpellLists?.List?.Id);
        }
    }

    private sealed class OneEventOverride : ICreatureEventData
    {
        public GameEventCreatureOverride? Data { get; set; }

        public IEnumerable<uint> Listed => [1];

        public GameEventCreatureOverride? For(uint spawnGuid) => spawnGuid == 1 ? Data : null;
    }

    [Fact]
    public void AGameEventsEntry_BringsItsList_AndItsEndBringsTheOwnListBack()
    {
        CreatureSpellListCatalog catalog = Catalog(
            new CreatureSpellList(ListId, "first", 0, 0, [Entry(1, Bolt)]),
            new CreatureSpellList(OtherListId, "second", 0, 0, [Entry(1, Nova)]));
        (WorldRuntime world, _, CreatureMapSystem system, Creature wolf, _, _) = Start(Entries(ListId, OtherListId, catalog));
        using (world)
        {
            var data = new OneEventOverride();
            system.EventData = data;
            Assert.Equal(ListId, wolf.SpellLists?.List?.Id);

            data.Data = new GameEventCreatureOverride(OtherEntry, 0, 0, 0, 0);
            system.RefreshEventData([1]);
            Assert.Equal(OtherEntry, wolf.Template.Entry);
            Assert.Equal(OtherListId, wolf.SpellLists?.List?.Id);

            data.Data = null;
            system.RefreshEventData([1]);
            Assert.Equal(ListId, wolf.SpellLists?.List?.Id);
        }
    }

    [Fact]
    public void Respawn_GoesBackToTheTemplatesList()
    {
        CreatureSpellListCatalog catalog = Catalog(
            new CreatureSpellList(ListId, "first", 0, 0, [Entry(1, Bolt)]),
            new CreatureSpellList(OtherListId, "second", 0, 0, [Entry(1, Nova)]));
        (WorldRuntime world, _, CreatureMapSystem system, Creature wolf, _, _) = Start(Content(CreatureAiFactory.EventAIName, ListId, catalog));
        using (world)
        {
            Assert.True(system.SetSpellList(wolf, OtherListId));

            system.KillCreature(wolf);
            system.ForceRespawn(wolf);

            Assert.Equal(ListId, wolf.SpellLists?.List?.Id);
        }
    }

    [Fact]
    public void EveryRespawn_RollsTheAvailabilityAgain()
    {
        CreatureSpellListEntry[] entries = [.. Enumerable.Range(1, 12).Select(i => new CreatureSpellListEntry((uint)i, Bolt, CreatureSpellListFlags.None, CreatureSpellListTargets.Current, 0, 50, 0, 0, 0, 5000, 5000))];
        CreatureSpellListCatalog catalog = Catalog(new CreatureSpellList(ListId, "test", 0, 0, entries));
        (WorldRuntime world, _, CreatureMapSystem system, Creature wolf, _, _) = Start(Content(CreatureAiFactory.EventAIName, ListId, catalog));
        using (world)
        {
            HashSet<string> kept = [];
            for (int life = 0; life < 6; life++)
            {
                system.KillCreature(wolf);
                system.ForceRespawn(wolf);
                kept.Add(string.Join(',', wolf.SpellLists!.ActiveEntries.Select(e => e.Position)));
            }

            Assert.True(kept.Count > 1, "six lives kept the same entries of twelve at availability 50");
        }
    }

    [Fact]
    public void ATemplateWithoutAList_FallsBackToListEntryTimes100()
    {
        CreatureSpellListCatalog catalog = Catalog(new CreatureSpellList(WolfEntry * 100, "legacy", 0, 0, [Entry(1, Bolt)]));
        (WorldRuntime world, _, _, Creature wolf, _, _) = Start(Content(CreatureAiFactory.EventAIName, 0, catalog));
        using (world)
        {
            Assert.Equal(WolfEntry * 100, wolf.SpellLists?.List?.Id);
        }
    }

    private static RuleRecord Rule(int id, short eventType, int p1 = 0, int p2 = 0) => new()
    {
        Id = id,
        Host = CreatureRuleIds.Host,
        OwnerKind = CreatureRuleIds.OwnerEntry,
        OwnerId = (int)WolfEntry,
        EventType = eventType,
        EventParam1 = p1,
        EventParam2 = p2,
    };

    private static RuleActionRecord SetListAction(int rule, int listId) => new()
    {
        OwnerId = rule,
        Seq = 0,
        ActionType = CreatureRuleIds.ActionSetSpellList,
        Param1 = listId,
        TargetType = CreatureRuleIds.TargetSelf,
    };

    [Fact]
    public void TheRuleAction_SetsAList_ThatRunsArmed_AndZeroClearsIt()
    {
        RuleCatalog rules = RuleCatalog.Build(
            new RuleContent
            {
                Rules = [Rule(1_000_001, CreatureRuleIds.EventAggro), Rule(1_000_002, CreatureRuleIds.EventTimerInCombat, 3000, 3000)],
                Actions = [SetListAction(1_000_001, (int)OtherListId), SetListAction(1_000_002, 0)],
            },
            RuleVocabulary.Default);
        Assert.Empty(rules.Rejected.Select(r => $"{r.Table} {r.Id}: {r.Reason}"));
        var factory = new CreatureAiFactory();
        CreatureRuleAI.Register(factory, rules);
        var caster = new ListCaster();
        CreatureSpellListCatalog catalog = Catalog(new CreatureSpellList(OtherListId, "second", 0, 0, [Entry(1, Nova, initial: 1000)]));
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(Content(CreatureRuleAI.AiName, 0, catalog), new CreatureAiServices { Spells = caster, Factory = factory });
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 50, 0);
            Creature wolf = Assert.Single(system.Creatures);
            Assert.Null(wolf.SpellLists);

            map.Combat.DealDamage(player, wolf, 1, direct: false);
            Run(world, 100);
            Assert.Equal(OtherListId, wolf.SpellLists?.List?.Id);
            Assert.Empty(caster.Casts); // armed, so the initial cooldown holds it

            Run(world, 1700);
            Assert.Equal([Nova], caster.Casts.Select(c => c.Spell));

            Run(world, 2000);
            Assert.Null(wolf.SpellLists);
        }
    }

    /// <summary>The listed wolf and a plain creature of its faction beside it, both fighting a player.</summary>
    private static (WorldRuntime World, Map Map, Creature Wolf, Creature Friend, Player Player, ListCaster Caster) StartParty(CreatureSpellListCatalog catalog)
    {
        static void Stats(CreatureTemplateBuilder b)
        {
            b.MinLevelHealth = 1000;
            b.MaxLevelHealth = 1000;
        }

        CreatureContent content = new(
            [
                Template(configure: t =>
                {
                    Stats(t);
                    t.AIName = CreatureAiFactory.EventAIName;
                }) with { SpellList = ListId },
                Template(PlainEntry, Stats),
            ],
            [Spawn(1, WolfEntry, 5, 0), Spawn(2, PlainEntry, 7, 0)], [], [], [],
            new CreatureAiContent([], []) { SpellLists = catalog });
        var caster = new ListCaster();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = caster });
        (Player player, _) = AddPlayer(world, 1, 50, 0);
        Creature wolf = Assert.Single(system.Creatures, c => c.Template.Entry == WolfEntry);
        Creature friend = Assert.Single(system.Creatures, c => c.Template.Entry == PlainEntry);
        map.Combat.DealDamage(player, wolf, 1, direct: false);
        map.Combat.DealDamage(player, friend, 1, direct: false);
        return (world, map, wolf, friend, player, caster);
    }

    private static void AssertCastAt(ListCaster caster, int who, Creature wolf, Creature friend)
    {
        switch (who)
        {
            case 1:
                Assert.Equal([(Bolt, (Unit?)wolf)], caster.Casts);
                break;
            case 2:
                Assert.Equal([(Bolt, (Unit?)friend)], caster.Casts);
                break;
            default:
                Assert.Empty(caster.Casts);
                break;
        }
    }

    private static CreatureSpellListCatalog SupportCatalog(CreatureSpellListTargeting row, CreatureConditionCatalog? conditions = null)
    {
        CreatureSpellListEntry heal = Entry(1, Bolt, target: row.Id) with { Flags = CreatureSpellListFlags.SupportAction };
        return new CreatureSpellListCatalog([new CreatureSpellList(ListId, "test", 100, 0, [heal])], [row]) { Conditions = conditions ?? CreatureConditionCatalog.Empty };
    }

    [Theory]
    [InlineData(10u, 1u, 0u, 50u, 60u, 2)] // missing 10%, excluding self: the wolf misses more, but only its friend counts
    [InlineData(10u, 1u, 1u, 50u, 60u, 1)] // including self: the one missing the most
    [InlineData(50u, 1u, 0u, 100u, 60u, 0)] // 40% missing is under the row's 50%
    [InlineData(300u, 0u, 0u, 100u, 60u, 2)] // Param2 0: points, 400 missing
    [InlineData(500u, 0u, 0u, 100u, 60u, 0)]
    public void SupportRow_PicksTheFriendMissingTheMostHealth(uint param1, uint param2, uint param3, uint wolfPercent, uint friendPercent, int expected)
    {
        var row = new CreatureSpellListTargeting(200, CreatureSpellListTargetingType.Support, param1, param2, param3, -1);
        (WorldRuntime world, _, Creature wolf, Creature friend, _, ListCaster caster) = StartParty(SupportCatalog(row));
        using (world)
        {
            wolf.Health = wolf.MaxHealth * wolfPercent / 100;
            friend.Health = friend.MaxHealth * friendPercent / 100;

            Run(world, 1500);

            AssertCastAt(caster, expected, wolf, friend);
        }
    }

    [Fact]
    public void SupportRow_WithAUnitCondition_TakesTheFriendThatMeetsIt()
    {
        var below = new UnitConditionDefinition(9, false, [new UnitConditionTerm(12, CreatureConditionOperation.LessThan, 50)]); // health percent
        var row = new CreatureSpellListTargeting(208, CreatureSpellListTargetingType.Support, 0, 0, 0, 9);
        (WorldRuntime world, _, Creature wolf, Creature friend, _, ListCaster caster) = StartParty(SupportCatalog(row, new CreatureConditionCatalog([], [below])));
        using (world)
        {
            Run(world, 1500);
            Assert.Empty(caster.Casts);

            friend.Health = friend.MaxHealth / 4;
            Run(world, 1500);
            AssertCastAt(caster, 2, wolf, friend);
        }
    }

    [Theory]
    [InlineData(CreatureSpellListTargets.FriendlyMissingBuff, true, false, 2)]
    [InlineData(CreatureSpellListTargets.FriendlyMissingBuff, false, true, 1)]
    [InlineData(CreatureSpellListTargets.FriendlyMissingBuff, true, true, 0)]
    [InlineData(CreatureSpellListTargets.FriendlyMissingBuffNoSelf, true, false, 2)]
    [InlineData(CreatureSpellListTargets.FriendlyMissingBuffNoSelf, false, true, 0)]
    public void HardcodedMissingBuff_TakesTheFriendWithoutTheAura(uint targetId, bool wolfHasIt, bool friendHasIt, int expected)
    {
        CreatureSpellListCatalog catalog = Catalog(new CreatureSpellList(ListId, "test", 0, 0, [Entry(1, Bolt, target: targetId)]));
        (WorldRuntime world, _, Creature wolf, Creature friend, _, ListCaster caster) = StartParty(catalog);
        using (world)
        {
            if (wolfHasIt)
            {
                caster.Auras.Add((wolf, Bolt));
            }

            if (friendHasIt)
            {
                caster.Auras.Add((friend, Bolt));
            }

            Run(world, 1500);

            AssertCastAt(caster, expected, wolf, friend);
        }
    }

    [Fact]
    public void HardcodedCurrentNotAlone_WaitsForASecondEnemy()
    {
        CreatureSpellListCatalog catalog = Catalog(new CreatureSpellList(ListId, "test", 0, 0, [Entry(1, Bolt, target: CreatureSpellListTargets.CurrentNotAlone)]));
        (WorldRuntime world, Map map, _, Creature wolf, Player player, ListCaster caster) = Start(Content(CreatureAiFactory.EventAIName, ListId, catalog));
        using (world)
        {
            map.Combat.DealDamage(player, wolf, 1, direct: false);
            Run(world, 1500);
            Assert.Empty(caster.Casts);

            (Player second, _) = AddPlayer(world, 2, 50, 2);
            map.Combat.DealDamage(second, wolf, 1, direct: false);
            Run(world, 1500);
            Assert.Equal([(Bolt, wolf.Combat.Victim)], caster.Casts);
        }
    }
}
