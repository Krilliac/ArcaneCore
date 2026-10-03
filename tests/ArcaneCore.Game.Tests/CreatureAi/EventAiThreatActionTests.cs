using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>EventAI actions 13 (THREAT_SINGLE) and 14 (THREAT_ALL_PCT), cmangos-classic CreatureEventAI.cpp ProcessAction.</summary>
public sealed class EventAiThreatActionTests
{
    private static CreatureAiEvent AggroRow(CreatureAiAction a1, CreatureAiAction a2 = default)
        => new()
        {
            Id = 1,
            CreatureId = WolfEntry,
            EventType = (byte)EventAiEventType.Aggro,
            Chance = 100,
            Action1 = a1,
            Action2 = a2,
        };

    private static CreatureAiAction Act(EventAiActionType type, int p1 = 0, int p2 = 0, int p3 = 0) => new((byte)type, p1, p2, p3);

    private static (WorldRuntime World, Map Map, Creature Wolf, Player Player) Start(CreatureAiEvent row)
    {
        CreatureContent content = new(
            [Template(configure: t => t.AIName = CreatureAiFactory.EventAIName)],
            [Spawn(1, WolfEntry, 5, 0)], [], [], [],
            new CreatureAiContent([row], []));
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() });
        (Player player, _) = AddPlayer(runtime, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        var ai = Assert.IsType<CreatureEventAI>(wolf.AI);
        Assert.Empty(ai.Unsupported);
        return (runtime, map, wolf, player);
    }

    [Fact]
    public void ThreatSingle_Direct_AddsTheValue_AndNonDirectChangesByPercent()
    {
        (WorldRuntime runtime, Map map, Creature wolf, Player player) = Start(AggroRow(Act(EventAiActionType.ThreatSingle, 100, (int)EventAiTarget.Victim, 1)));
        using WorldRuntime world = runtime;
        map.Combat.DealDamage(player, wolf, 40, direct: false); // the aggro event runs when the creature starts to attack

        Assert.Equal(140f, wolf.Combat.Threat.GetThreat(player));

        (WorldRuntime runtime2, Map map2, Creature wolf2, Player player2) = Start(AggroRow(Act(EventAiActionType.ThreatSingle, 50, (int)EventAiTarget.Victim, 0)));
        using WorldRuntime world2 = runtime2;
        map2.Combat.DealDamage(player2, wolf2, 40, direct: false);

        Assert.Equal(60f, wolf2.Combat.Threat.GetThreat(player2)); // +50 %
    }

    [Fact]
    public void ThreatAllPercent_ChangesEveryEntry_AndBelowMinus100RemovesIt()
    {
        (WorldRuntime runtime, Map map, Creature wolf, Player player) = Start(AggroRow(Act(EventAiActionType.ThreatAllPercent, -50)));
        using WorldRuntime world = runtime;
        map.Combat.DealDamage(player, wolf, 40, direct: false);

        Assert.Equal(20f, wolf.Combat.Threat.GetThreat(player));

        (WorldRuntime runtime2, Map map2, Creature wolf2, Player player2) = Start(AggroRow(Act(EventAiActionType.ThreatAllPercent, -150)));
        using WorldRuntime world2 = runtime2;
        map2.Combat.DealDamage(player2, wolf2, 40, direct: false);

        Assert.False(wolf2.Combat.Threat.Contains(player2));
    }
}
