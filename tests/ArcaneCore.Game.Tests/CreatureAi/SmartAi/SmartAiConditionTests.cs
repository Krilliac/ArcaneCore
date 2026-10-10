using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.SmartAi.SmartRows;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi.SmartAi;

/// <summary>
/// <c>smart_scripts.ConditionId</c> (docs/integration/smartai-slice2-20261010.md): the event is gated by a cmangos <c>conditions</c> row through the
/// relay-condition path, target the event's invoker, source the script's base object. The check sits where AzerothCore checks smart-event conditions:
/// in ProcessEventsFor for non-LINK rows (SmartScript.cpp:157-180) and in ProcessTimedAction, where a failed check re-arms the timer at 5000/5000
/// (:4316-4318). The condition rows are those of <see cref="SmartRig.Conditions"/> (types 36, 37, 39 and -3 NOT of cmangos Conditions.cpp).
/// </summary>
public sealed class SmartAiConditionTests
{
    private const uint CastSpell = 77;

    private static Creature SpawnNear(SmartRig rig, uint entry, float x, float y = 0, float z = 83.5f)
        => rig.Creatures.SpawnTemporary(rig.Creatures.Content.FindTemplate(entry)!, x, y, z, 0)!;

    [Theory]
    [InlineData(1380u, 10f, true)]    // 37 CREATURE_IN_RANGE: Grark 10 yards from the player holds
    [InlineData(1380u, 200f, false)]  // ... 200 yards away does not (range 80)
    [InlineData(1380u, -1f, false)]   // ... nobody there does not
    [InlineData(1383u, 10f, false)]   // -3 NOT over it: Grark near, so it fails
    [InlineData(1383u, 200f, true)]   // ... Grark far, so it holds
    [InlineData(1383u, -1f, true)]    // ... nobody there, so it holds
    public void ConditionedEvent_RunsOnlyWhenItsConditionHolds(uint conditionId, float grarkX, bool runs)
    {
        using SmartRig rig = SmartRig.Start(
            [CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.Cast, SmartTarget.ActionInvoker, a1: CastSpell, condition: conditionId)],
            conditions: true);
        if (grarkX >= 0)
        {
            SpawnNear(rig, SmartRig.Grark, grarkX);
        }

        rig.WolfAi.OnAggro(rig.Player);

        if (runs)
        {
            Assert.Single(rig.Spells.Casts, c => c.Spell == CastSpell && ReferenceEquals(c.Target, rig.Player));
        }
        else
        {
            Assert.Empty(rig.Spells.Casts);
        }
    }

    [Fact]
    public void ConditionedEvent_IsCheckedPerRow_AndOnlyTheFailingRowIsHeldBack()
    {
        using SmartRig rig = SmartRig.Start(
        [
            CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.Cast, SmartTarget.ActionInvoker, a1: 1, condition: 1380),
            CreatureRow(WolfEntry, 1, SmartEvent.Aggro, SmartAction.Cast, SmartTarget.ActionInvoker, a1: 2),
            CreatureRow(WolfEntry, 2, SmartEvent.Aggro, SmartAction.Cast, SmartTarget.ActionInvoker, a1: 3, condition: 1383),
        ], conditions: true);
        rig.WolfAi.OnAggro(rig.Player);
        Assert.Equal([2u, 3u], rig.Spells.Casts.Select(c => c.Spell)); // Grark is not there: row 0 fails, 1 has none, 2 (NOT) holds
    }

    [Fact]
    public void ConditionedTimedEvent_FailingCheckRearmsTheTimerAtFiveSeconds()
    {
        // UPDATE_OOC: first run after 100 ms, then every 1000 ms. 606 SPAWN_COUNT (Demetria spawned) needs no invoker, which a timer does not have.
        using SmartRig rig = SmartRig.Start(
            [CreatureRow(WolfEntry, 0, SmartEvent.UpdateOutOfCombat, SmartAction.Cast, SmartTarget.Self, 100, 100, 1000, 1000, a1: CastSpell, condition: 606)],
            conditions: true);
        SmartHolder timed = rig.Script.Events.Single();

        rig.WolfAi.OnUpdate(101);
        Assert.Empty(rig.Spells.Casts);
        Assert.Equal(5000u, timed.TimerMs); // not the 1000 ms repeat: a failed check is retried in five seconds

        SpawnNear(rig, SmartRig.Demetria, 30, 30);
        rig.WolfAi.OnUpdate(4000);          // a 1000 ms re-arm would have fired by now
        rig.WolfAi.OnUpdate(999);
        Assert.Empty(rig.Spells.Casts);
        rig.WolfAi.OnUpdate(2);             // 5001 ms after the failed check
        Assert.Single(rig.Spells.Casts, c => c.Spell == CastSpell);
        Assert.Equal(1000u, timed.TimerMs); // a passed check re-arms with the repeat range
    }

    [Fact]
    public void ConditionedTimedEvent_StillFailing_KeepsRearmingAtFiveSeconds()
    {
        using SmartRig rig = SmartRig.Start(
            [CreatureRow(WolfEntry, 0, SmartEvent.UpdateOutOfCombat, SmartAction.Cast, SmartTarget.Self, 100, 100, 1000, 1000, a1: CastSpell, condition: 606)],
            conditions: true);
        rig.WolfAi.OnUpdate(101);
        rig.WolfAi.OnUpdate(5001);
        Assert.Equal(5000u, rig.Script.Events.Single().TimerMs);
        Assert.Empty(rig.Spells.Casts);
    }

    [Fact]
    public void ConditionIdNotInTheTable_FailsClosed()
    {
        // Conditions.cpp:1023-1028: a condition that is not loaded is not satisfied. Checked for an event with an invoker and for one without.
        using SmartRig rig = SmartRig.Start(
        [
            CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.Cast, SmartTarget.ActionInvoker, a1: 1, condition: 999),
            CreatureRow(WolfEntry, 1, SmartEvent.Evade, SmartAction.Cast, SmartTarget.Self, a1: 2, condition: 999),
            CreatureRow(WolfEntry, 2, SmartEvent.Evade, SmartAction.Cast, SmartTarget.Self, a1: 3),
        ], conditions: true);
        rig.WolfAi.OnAggro(rig.Player);
        rig.WolfAi.OnEvade();
        Assert.Equal([3u], rig.Spells.Casts.Select(c => c.Spell)); // only the unconditioned row ran
    }

    [Fact]
    public void NoConditionEvaluator_FailsClosed()
    {
        // The same row as the passing case of ConditionedEvent_RunsOnlyWhenItsConditionHolds (1383, nobody near), but no evaluator is wired.
        using SmartRig rig = SmartRig.Start(
        [
            CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.Cast, SmartTarget.ActionInvoker, a1: CastSpell, condition: 1383),
            CreatureRow(WolfEntry, 1, SmartEvent.Aggro, SmartAction.Cast, SmartTarget.ActionInvoker, a1: CastSpell + 1),
        ], conditions: false);
        rig.WolfAi.OnAggro(rig.Player);
        Assert.Equal([CastSpell + 1], rig.Spells.Casts.Select(c => c.Spell)); // only the unconditioned row ran
    }

    [Fact]
    public void LinkedRow_IsNotConditionChecked()
    {
        // AzerothCore skips SMART_EVENT_LINK rows in ProcessEventsFor and runs them from the row that links to them, with no condition check of their own.
        // 318 (the creature source dead) is false for the living wolf, so the linked row would be held back if it were checked.
        using SmartRig rig = SmartRig.Start(
        [
            CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.SetEventPhase, a1: 3, link: 1),
            CreatureRow(WolfEntry, 1, SmartEvent.Link, SmartAction.Cast, SmartTarget.ActionInvoker, a1: CastSpell, condition: 318),
        ], conditions: true);
        rig.WolfAi.OnAggro(rig.Player);
        Assert.Equal(3u, rig.Script.Phase);
        Assert.Single(rig.Spells.Casts, c => c.Spell == CastSpell && ReferenceEquals(c.Target, rig.Player));

        // The head of the chain, when its own condition fails, runs nothing - and so never reaches the link.
        using SmartRig held = SmartRig.Start(
        [
            CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.SetEventPhase, a1: 3, link: 1, condition: 318),
            CreatureRow(WolfEntry, 1, SmartEvent.Link, SmartAction.Cast, SmartTarget.ActionInvoker, a1: CastSpell),
        ], conditions: true);
        held.WolfAi.OnAggro(held.Player);
        Assert.Equal(0u, held.Script.Phase);
        Assert.Empty(held.Spells.Casts);
    }

    [Fact]
    public void ConditionSource_IsTheScriptsBaseObject_AndTheTargetIsTheInvoker()
    {
        // 317: the target (player) dead or beyond 60 yards of the source (the wolf, 5 yards off). With the invoker as target and the wolf as source
        // the living near player does not satisfy it; NOT 317 does.
        using SmartRig rig = SmartRig.Start(
        [
            CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.Cast, SmartTarget.ActionInvoker, a1: 1, condition: 317),
            CreatureRow(WolfEntry, 1, SmartEvent.Aggro, SmartAction.Cast, SmartTarget.ActionInvoker, a1: 2, condition: 1317),
        ], conditions: true);
        rig.WolfAi.OnAggro(rig.Player);
        Assert.Equal([2u], rig.Spells.Casts.Select(c => c.Spell));
    }
}
