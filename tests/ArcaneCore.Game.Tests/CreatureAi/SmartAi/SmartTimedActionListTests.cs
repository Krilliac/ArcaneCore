using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.SmartAi.SmartRows;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi.SmartAi;

/// <summary>
/// SMART_ACTION_CALL_TIMED_ACTIONLIST (80), CALL_RANDOM_TIMED_ACTIONLIST (87) and CALL_RANDOM_RANGE_TIMED_ACTIONLIST (88) with source type 9 rows, by
/// AzerothCore SmartScript::SetScript9 (SmartScript.cpp:5595-5622) and the OnUpdate / UpdateTimer handling of the list (:5219-5232, :5295-5311):
/// rows run one after another in id order, each delay counted from the row before, the stored event type replaced by the timer type, allowOverride
/// deciding whether a running list may be replaced, a list called from inside a list refused (docs/integration/smartai-slice2-20261010.md).
/// </summary>
public sealed class SmartTimedActionListTests
{
    private static void Pull(SmartRig rig) => rig.Map.Combat.DealDamage(rig.Player, rig.Wolf, 1, direct: false);

    private static SmartScriptRow Call(ushort id, SmartEvent on, uint list, uint timerType = 0, uint allowOverride = 0, SmartTarget target = SmartTarget.Self,
        uint targetEntry = 0)
        => CreatureRow(WolfEntry, id, on, SmartAction.CallTimedActionList, target, a1: list, a2: timerType, a3: allowOverride, t1: targetEntry);

    private static IEnumerable<uint> Spells(SmartRig rig) => rig.Spells.Casts.Select(c => c.Spell);

    [Fact]
    public void CallTimedActionList_RunsRowsInIdOrder_EachDelayCountedFromThePreviousRow()
    {
        // Given out of order on purpose: the list runs in id order.
        using SmartRig rig = SmartRig.Start(
        [
            Call(0, SmartEvent.Aggro, 50, timerType: 0),
            ListRow(50, 2, SmartAction.Cast, delay: 300, a1: 3),
            ListRow(50, 0, SmartAction.Cast, delay: 1000, a1: 1),
            ListRow(50, 1, SmartAction.Cast, delay: 500, a1: 2),
        ]);
        rig.WolfAi.OnAggro(rig.Player);
        Assert.Equal([0, 1, 2], rig.Script.TimedActionList.Select(h => (int)h.Row.Id));
        Assert.All(rig.Script.TimedActionList, h => Assert.True(h.InTimedList));
        Assert.Equal([true, false, false], rig.Script.TimedActionList.Select(h => h.TimedEnabled)); // only the first row counts down

        rig.WolfAi.OnUpdate(999);
        Assert.Empty(rig.Spells.Casts);
        rig.WolfAi.OnUpdate(2);                       // 1001 ms: row 0
        Assert.Equal([1u], Spells(rig));
        rig.WolfAi.OnUpdate(400);                     // row 1 counts from row 0, not from the call: 500 ms after row 0 is not yet
        Assert.Equal([1u], Spells(rig));
        rig.WolfAi.OnUpdate(99);
        Assert.Equal([1u, 2u], Spells(rig));
        rig.WolfAi.OnUpdate(200);
        Assert.Equal([1u, 2u], Spells(rig));
        rig.WolfAi.OnUpdate(2);
        Assert.Equal([1u, 2u, 3u], Spells(rig));

        rig.WolfAi.OnUpdate(1);                       // no row is enabled any more: the list is cleared
        Assert.Empty(rig.Script.TimedActionList);
        rig.WolfAi.OnUpdate(5000);
        Assert.Equal(3, rig.Spells.Casts.Count);      // and nothing runs again on its own
    }

    [Fact]
    public void CallTimedActionList_AfterTheListIsDone_ANewCallRunsAgain()
    {
        using SmartRig rig = SmartRig.Start(
        [
            Call(0, SmartEvent.Aggro, 50),
            ListRow(50, 0, SmartAction.Cast, delay: 100, a1: 1),
        ]);
        rig.WolfAi.OnAggro(rig.Player);
        rig.WolfAi.OnUpdate(101);
        rig.WolfAi.OnUpdate(1);
        Assert.Empty(rig.Script.TimedActionList);
        rig.WolfAi.OnAggro(rig.Player);
        Assert.Single(rig.Script.TimedActionList);
        rig.WolfAi.OnUpdate(101);
        Assert.Equal([1u, 1u], Spells(rig));
    }

    [Theory]
    [InlineData(0u, false, true)]   // 0: out of combat only
    [InlineData(0u, true, false)]
    [InlineData(1u, false, false)]  // 1: in combat only
    [InlineData(1u, true, true)]
    [InlineData(2u, false, true)]   // above 1: always
    [InlineData(2u, true, true)]
    [InlineData(7u, false, true)]
    [InlineData(7u, true, true)]
    public void CallTimedActionList_TimerTypeSelectsOocIcOrAlways(uint timerType, bool inCombat, bool runs)
    {
        using SmartRig rig = SmartRig.Start(
        [
            Call(0, SmartEvent.Evade, 50, timerType),
            ListRow(50, 0, SmartAction.Cast, delay: 100, a1: 9),
        ]);
        if (inCombat)
        {
            Pull(rig);
        }

        rig.WolfAi.OnEvade();
        SmartHolder row = Assert.Single(rig.Script.TimedActionList);
        Assert.Equal(timerType switch { 0 => SmartEvent.UpdateOutOfCombat, 1 => SmartEvent.UpdateInCombat, _ => SmartEvent.Update }, row.Event);

        rig.WolfAi.OnUpdate(150);
        Assert.Equal(runs ? new[] { 9u } : [], Spells(rig));
        if (!runs)
        {
            Assert.Single(rig.Script.TimedActionList); // a list that cannot run yet waits, it is not dropped
        }
    }

    [Theory]
    [InlineData(0u, 1u)]  // allowOverride 0: the running list (spell 1) stays
    [InlineData(1u, 2u)]  // allowOverride 1: the new list (spell 2) replaces it
    public void CallTimedActionList_KeepsARunningListUnlessAllowOverride(uint allowOverride, uint listThatRuns)
    {
        using SmartRig rig = SmartRig.Start(
        [
            Call(0, SmartEvent.Aggro, 50),
            Call(1, SmartEvent.Evade, 51, allowOverride: allowOverride),
            ListRow(50, 0, SmartAction.Cast, delay: 10000, a1: 1),
            ListRow(51, 0, SmartAction.Cast, delay: 100, a1: 2),
        ]);
        rig.WolfAi.OnAggro(rig.Player);
        Assert.Equal(50, Assert.Single(rig.Script.TimedActionList).Row.EntryOrGuid);
        rig.WolfAi.OnEvade();
        Assert.Equal(listThatRuns, Assert.Single(rig.Script.TimedActionList).Row.ActionParam1);

        rig.WolfAi.OnUpdate(150);
        Assert.Equal(allowOverride == 0 ? [] : new[] { 2u }, Spells(rig));
    }

    [Fact]
    public void CallTimedActionList_FromInsideAListIsRefused()
    {
        using SmartRig rig = SmartRig.Start(
        [
            Call(0, SmartEvent.Aggro, 50),
            ListRow(50, 0, SmartAction.CallTimedActionList, delay: 100, a1: 51),
            ListRow(50, 1, SmartAction.Cast, delay: 100, a1: 1),
            ListRow(51, 0, SmartAction.Cast, delay: 0, a1: 9),
        ]);
        rig.WolfAi.OnAggro(rig.Player);
        rig.WolfAi.OnUpdate(101);

        string refused = Assert.Single(rig.Script.Unsupported);
        Assert.Contains("timed action list 51 called from a timed action", refused, StringComparison.Ordinal);
        Assert.All(rig.Script.TimedActionList, h => Assert.Equal(50, h.Row.EntryOrGuid)); // list 50 was not replaced by list 51

        rig.WolfAi.OnUpdate(500);
        Assert.Equal([1u], Spells(rig)); // list 50 went on to its next row; list 51's cast never ran
    }

    [Theory]
    [InlineData(false)] // 87: lists in param1..6
    [InlineData(true)]  // 88: lists in the range param1..param2
    public void CallRandomAndRandomRange_PickAListAndReadParam2And3AsTimerTypeAndOverride(bool range)
    {
        // AzerothCore reads the timer type from param2 and allowOverride from param3 for 87 and 88 as well (the same union members the 80 reads),
        // so 87's second and third list ids are also its timer type and override flag, and 88's max id its timer type.
        uint[] pool = range ? [50, 51] : [50, 51, 52];
        SmartScriptRow call = range
            ? CreatureRow(WolfEntry, 0, SmartEvent.Evade, SmartAction.CallRandomRangeTimedActionList, SmartTarget.Self, a1: 50, a2: 51)
            : CreatureRow(WolfEntry, 0, SmartEvent.Evade, SmartAction.CallRandomTimedActionList, SmartTarget.Self, a1: 50, a2: 51, a3: 52);
        SmartScriptRow[] rows = [call, .. pool.Select(id => ListRow((int)id, 0, SmartAction.Cast, delay: 100, a1: id))];

        // Timer type 51 is above 1: the list runs in combat as well (it would not if the type were read as 0).
        using (SmartRig inCombat = SmartRig.Start(rows))
        {
            Pull(inCombat);
            inCombat.WolfAi.OnEvade();
            inCombat.WolfAi.OnUpdate(101);
            Assert.Contains(Assert.Single(Spells(inCombat)), pool);
        }

        using SmartRig rig = SmartRig.Start(rows);

        // Each call rolls its list: over many calls every list of the pool is chosen, and none outside it.
        for (int i = 0; i < 60; i++)
        {
            rig.WolfAi.OnEvade();
            rig.WolfAi.OnUpdate(101);
            rig.WolfAi.OnUpdate(1);
        }

        Assert.Equal(pool.Order(), Spells(rig).Distinct().Order());
        int before = rig.Spells.Casts.Count;

        // allowOverride is param3: nonzero for 87 (52), zero for 88. With it a second call restarts the list (its timer starts over), without
        // it the running list is kept and so runs out on the original timer.
        rig.WolfAi.OnEvade();
        rig.WolfAi.OnUpdate(60);
        rig.WolfAi.OnEvade();
        rig.WolfAi.OnUpdate(60);
        Assert.Equal(range ? 1 : 0, rig.Spells.Casts.Count - before);
    }

    [Fact]
    public void ListRowWhoseConditionFails_IsSkippedAndTheNextRowIsEnabled()
    {
        // 318: the creature source dead, false for the living wolf. SmartScript.cpp:5219-5232: a list row that was processed once, whether or
        // not its condition held, is disabled and the next one enabled.
        using SmartRig rig = SmartRig.Start(
        [
            Call(0, SmartEvent.Aggro, 50),
            ListRow(50, 0, SmartAction.Cast, delay: 100, a1: 1, condition: 318),
            ListRow(50, 1, SmartAction.Cast, delay: 100, a1: 2),
        ], conditions: true);
        rig.WolfAi.OnAggro(rig.Player);
        rig.WolfAi.OnUpdate(101);
        Assert.Equal([2u], Spells(rig));
        SmartHolder skipped = rig.Script.TimedActionList.First();
        Assert.Equal((uint)1, skipped.Row.ActionParam1);
        Assert.False(skipped.TimedEnabled); // and it does not come back on its 5000 ms re-check: only an enabled row counts down

        rig.WolfAi.OnUpdate(6000);
        Assert.Equal([2u], Spells(rig));
    }

    [Fact]
    public void CallTimedActionList_OnAnotherSmartCreature_RunsInItsScriptWithTheCallersInvoker()
    {
        using SmartRig rig = SmartRig.Start(
        [
            Call(0, SmartEvent.Aggro, 50, timerType: 2, target: SmartTarget.ClosestCreature, targetEntry: SmartRig.Other),
            ListRow(50, 0, SmartAction.Cast, SmartTarget.ActionInvoker, delay: 100, a1: 5),
        ], creatureSpawns: [Spawn(2, SmartRig.Other, 8, 0)]);
        Creature otherCreature = rig.Creatures.Creatures.Single(c => c.Entry == SmartRig.Other);
        CreatureSmartAI other = SmartRig.AiOf(otherCreature);

        rig.WolfAi.OnAggro(rig.Player);
        Assert.Empty(rig.Script.TimedActionList);            // the list is the other creature's, not the caller's
        Assert.Single(other.Script.TimedActionList);
        Assert.Same(rig.Player, other.Script.LastInvoker);   // SmartAI::SetScript9 stores the caller's invoker first (SmartAI.cpp:1343-1348)

        rig.WolfAi.OnUpdate(500);
        Assert.Empty(rig.Spells.Casts);
        other.OnUpdate(101);
        (uint spell, Unit? target, _) = Assert.Single(rig.Spells.Casts);
        Assert.Equal(5u, spell);
        Assert.Same(rig.Player, target);
    }

    [Fact]
    public void CallTimedActionList_OnATargetThatIsNotAScriptedObject_DoesNothing()
    {
        // A plain creature (no smart AI) and a player are not script targets; nothing is started and nothing throws.
        using SmartRig rig = SmartRig.Start(
        [
            Call(0, SmartEvent.Aggro, 50, timerType: 2, target: SmartTarget.ClosestCreature, targetEntry: SmartRig.Speaker),
            Call(1, SmartEvent.Aggro, 50, timerType: 2, target: SmartTarget.ActionInvoker),
            ListRow(50, 0, SmartAction.Cast, delay: 100, a1: 5),
        ], creatureSpawns: [Spawn(2, SmartRig.Speaker, 8, 0)]);
        rig.WolfAi.OnAggro(rig.Player);
        Assert.Empty(rig.Script.TimedActionList);
        rig.WolfAi.OnUpdate(500);
        Assert.Empty(rig.Spells.Casts);
    }
}
