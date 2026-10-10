using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using Xunit;
using static ArcaneCore.Game.Tests.Conditions.ConditionTestSupport;

namespace ArcaneCore.Game.Tests.Conditions;

/// <summary>z2815's remaining leaf types, as read by mangos-classic ConditionEntry::Evaluate.</summary>
public sealed class ConditionContentGapTests
{
    [Fact]
    public void WorldStateUsesSignedValueAndTheSixComparisonOperators()
    {
        var context = new ConditionContext { WorldState = (_, id) => id == 4811 ? -2 : null };
        ConditionEvaluator e = Evaluator(context,
            Row(1, ConditionType.WorldState, 4811, 1, unchecked((uint)-2)),
            Row(2, ConditionType.WorldState, 4811, 2, unchecked((uint)-2)),
            Row(3, ConditionType.WorldState, 4811, 3, unchecked((uint)-1)),
            Row(4, ConditionType.WorldState, 4811, 4, unchecked((uint)-2)),
            Row(5, ConditionType.WorldState, 4811, 5, unchecked((uint)-3)),
            Row(6, ConditionType.WorldState, 4811, 6, unchecked((uint)-2)),
            Row(7, ConditionType.WorldState, 9, 1, 0));
        Player player = CreatePlayer();
        Assert.True(e.IsSatisfied(1, player, null));
        Assert.False(e.IsSatisfied(2, player, null));
        Assert.True(e.IsSatisfied(3, player, null));
        Assert.True(e.IsSatisfied(4, player, null));
        Assert.True(e.IsSatisfied(5, player, null));
        Assert.True(e.IsSatisfied(6, player, null));
        Assert.False(e.IsSatisfied(7, player, null));
        Assert.True(e.IsTypeAvailable(ConditionType.WorldState));
    }

    [Fact]
    public void EncounterWaypointRangeSpawnAndWorldScriptUseTheirLiveCollaborators()
    {
        var context = new ConditionContext
        {
            CompletedEncounter = (_, first, second) => first == 715 && second == 0,
            LastWaypoint = (_, _) => 8,
            CreatureInRange = (_, entry, range) => entry == 7172 && range == 80,
            SpawnCount = (_, entry) => entry == 412 ? 2u : 0u,
            WorldScript = (condition, state) => condition == 2113 && state == 0,
        };
        ConditionEvaluator e = Evaluator(context,
            Row(1, ConditionType.CompletedEncounter, 715),
            Row(2, ConditionType.LastWaypoint, 8),
            Row(3, ConditionType.LastWaypoint, 9, 2),
            Row(4, ConditionType.CreatureInRange, 7172, 80),
            Row(5, ConditionType.SpawnCount, 412, 1),
            Row(6, ConditionType.SpawnCount, 412, 3),
            Row(7, ConditionType.WorldScript, 2113),
            Row(8, ConditionType.LastWaypoint, 9, 1));
        Player player = CreatePlayer();
        NpcInfo npc = CreateNpc();
        Assert.True(e.IsSatisfied(1, player, npc));
        Assert.True(e.IsSatisfied(2, player, npc));
        Assert.True(e.IsSatisfied(3, player, npc));
        Assert.True(e.IsSatisfied(4, player, npc));
        Assert.True(e.IsSatisfied(5, player, npc));
        Assert.False(e.IsSatisfied(6, player, npc));
        Assert.True(e.IsSatisfied(7, player, npc));
        Assert.False(e.IsSatisfied(8, player, npc));
        Assert.False(e.IsSatisfied(2, player, null));
    }

    [Fact]
    public void DeadOrAwayChecksPlayerAndSourceCreature()
    {
        ConditionEvaluator e = Evaluator(null,
            Row(1, ConditionType.DeadOrAway, 0, 60),
            Row(2, ConditionType.DeadOrAway, 3));
        Player player = CreatePlayer();
        NpcInfo npc = CreateNpc();
        Assert.False(e.IsSatisfied(1, player, npc));
        Assert.False(e.IsSatisfied(2, player, npc));
        player.Health = 0;
        Assert.True(e.IsSatisfied(1, player, npc));
        Assert.True(e.IsSatisfied(2, player, null));
    }
}
