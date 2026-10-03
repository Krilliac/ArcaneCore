using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// Which AI a creature gets. vmangos selects EventAI only for <c>ai_name = 'EventAI'</c> (AI/CreatureAISelector.cpp:37-100); cmangos-classic
/// selects it for every creature by default (AI/EventAI/CreatureEventAI.cpp:51-63), and classic-db has no AIName column at all, so
/// <c>Creatures:ImplicitEventAi</c> bridges the data: rows for the entry (or a spawn) mean EventAI unless an AIName says otherwise.
/// </summary>
public sealed class AiSelectionTests
{
    // classic-db's most common combat script: "flee at 15 %" (HEALTH_PCT 15..0 -> FLEE_FOR_ASSIST), 1,253 rows on 1,284 templates
    private static CreatureAiEvent FleeAtFifteenPercent(uint entry, uint guid = 0)
        => new()
        {
            Id = 1,
            CreatureId = entry,
            CreatureGuid = guid,
            EventType = (byte)EventAiEventType.HealthPercent,
            Param1 = 15,
            Param2 = 0,
            Param3 = 0,
            Param4 = 0,
            Chance = 100,
            Action1 = new CreatureAiAction((byte)EventAiActionType.FleeForAssist, 0, 0, 0),
        };

    private static (WorldRuntime World, Map Map, CreatureMapSystem System, Player Player) Start(
        CreatureTemplate template, IEnumerable<CreatureSpawn> spawns, IEnumerable<CreatureAiEvent> rows, CreatureOptions? options = null)
    {
        var content = new CreatureContent([template], spawns, [], [], [], new CreatureAiContent(rows, []));
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() }, options);
        (Player player, _) = AddPlayer(runtime, 1, 0, 0);
        return (runtime, map, system, player);
    }

    [Fact]
    public void ATemplateWithoutAnAiName_ButWithRows_RunsEventAi_AndTheFleeRowFires()
    {
        (WorldRuntime runtime, Map map, CreatureMapSystem system, Player player) = Start(Template(), [Spawn(1, WolfEntry, 30, 0)], [FleeAtFifteenPercent(WolfEntry)]);
        using WorldRuntime world = runtime;
        Creature wolf = Assert.Single(system.Creatures);
        var ai = Assert.IsType<CreatureEventAI>(wolf.AI);
        Assert.Equal(1, ai.EventCount);
        Assert.Empty(ai.Unsupported);
        map.Combat.DealDamage(player, wolf, 1, direct: false);
        Assert.NotEqual(MovementGeneratorType.Fleeing, wolf.Motion.CurrentType);

        map.Combat.DealDamage(player, wolf, wolf.Health - (wolf.MaxHealth / 10), direct: false); // down to 10 %
        Run(world, 1500);

        Assert.True(wolf.Health * 100 / wolf.MaxHealth <= 15);
        Assert.Equal(MovementGeneratorType.Fleeing, wolf.Motion.CurrentType);
    }

    [Fact]
    public void WithTheSwitchOff_AnEmptyAiNameKeepsTheDefaultAi()
    {
        (WorldRuntime runtime, _, CreatureMapSystem system, _) = Start(Template(), [Spawn(1, WolfEntry, 30, 0)], [FleeAtFifteenPercent(WolfEntry)],
            new CreatureOptions { ImplicitEventAi = false });
        using WorldRuntime world = runtime;

        Assert.IsType<AggressorAI>(Assert.Single(system.Creatures).AI);
    }

    [Fact]
    public void AnExplicitAiName_AlwaysWins_OverTheRows()
    {
        (WorldRuntime runtime, _, CreatureMapSystem system, _) = Start(Template(configure: t => t.AIName = "ReactorAI"), [Spawn(1, WolfEntry, 30, 0)], [FleeAtFifteenPercent(WolfEntry)]);
        using WorldRuntime world = runtime;

        Assert.IsType<ReactorAI>(Assert.Single(system.Creatures).AI);
    }

    [Fact]
    public void WithoutRows_TheDefaultsStay_ReactorForACivilianAggressorForTheRest()
    {
        (WorldRuntime runtime, _, CreatureMapSystem system, _) = Start(Template(), [Spawn(1, WolfEntry, 30, 0)], []);
        using WorldRuntime world = runtime;
        Assert.IsType<AggressorAI>(Assert.Single(system.Creatures).AI);

        (WorldRuntime runtime2, _, CreatureMapSystem system2, _) = Start(Template(configure: t => t.Civilian = true), [Spawn(1, WolfEntry, 30, 0)], []);
        using WorldRuntime world2 = runtime2;
        Assert.IsType<ReactorAI>(Assert.Single(system2.Creatures).AI);
    }

    [Fact]
    public void ARowKeyedToOneSpawn_GivesEventAiToThatSpawnOnly()
    {
        (WorldRuntime runtime, _, CreatureMapSystem system, _) = Start(Template(), [Spawn(1, WolfEntry, 30, 0), Spawn(2, WolfEntry, 40, 0)], [FleeAtFifteenPercent(WolfEntry, guid: 2)]);
        using WorldRuntime world = runtime;

        Creature first = system.Creatures.Single(c => c.Spawn!.Guid == 1);
        Creature second = system.Creatures.Single(c => c.Spawn!.Guid == 2);
        Assert.IsType<AggressorAI>(first.AI);
        Assert.IsType<CreatureEventAI>(second.AI);
    }

    [Fact]
    public void RowsOfAnotherEntry_DoNotAttachEventAi()
    {
        (WorldRuntime runtime, _, CreatureMapSystem system, _) = Start(Template(), [Spawn(1, WolfEntry, 30, 0)], [FleeAtFifteenPercent(WolfEntry + 1)]);
        using WorldRuntime world = runtime;

        Assert.IsType<AggressorAI>(Assert.Single(system.Creatures).AI);
    }
}
