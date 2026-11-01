using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureAi.SmartAi.SmartRows;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi.SmartAi;

/// <summary>
/// SmartAI events DATA_SET (38), ESCORT_REACHED (40, refused), TEXT_OVER (52) and MOVEMENTINFORM (34). Rows use raw AzerothCore ids
/// (SmartScriptMgr.h); the observable effect is SET_EVENT_PHASE, read back from <see cref="SmartScript.Phase"/>.
/// </summary>
public sealed class SmartAiEventsTests
{
    private const int Wolf = (int)WolfEntry;

    [Fact]
    public void DataSet_RunsOnlyForTheMatchingIdAndValue_AndHonoursItsCooldown()
    {
        using var rig = SmartRig.Start([Make(Wolf, 0, 0, 38, 22, 1, e1: 3, e2: 7, e3: 1000, e4: 1000, a1: 4)]);

        rig.WolfAi.SetData(3, 8);
        rig.WolfAi.SetData(4, 7);
        Assert.Equal(0u, rig.Script.Phase);

        rig.WolfAi.SetData(3, 7, rig.Player);
        Assert.Equal(4u, rig.Script.Phase);
        Assert.Same(rig.Player, rig.Script.LastInvoker);

        // The cooldown (param3..param4) deactivates the row until the timer ran out.
        rig.Script.SetPhase(0);
        rig.WolfAi.SetData(3, 7);
        Assert.Equal(0u, rig.Script.Phase);
        rig.Script.OnUpdate(1000); // the countdown reaches 0
        rig.Script.OnUpdate(1);    // the next update reactivates the row
        rig.WolfAi.SetData(3, 7);
        Assert.Equal(4u, rig.Script.Phase);
    }

    [Fact]
    public void TextOver_FiresAfterTheTalkDurationWithTheGroupAndSpeaker()
    {
        using var rig = SmartRig.Start(
        [
            Make(Wolf, 0, 0, 4, 1, 7, a1: 9001, a2: 500),
            Make(Wolf, 0, 1, 52, 22, 1, e1: 9001, e2: (uint)WolfEntry, a1: 3),
            Make(Wolf, 0, 2, 52, 22, 1, e1: 9002, a1: 9),
            Make(Wolf, 0, 3, 52, 22, 1, e1: 9001, e2: 12345, a1: 10),
        ]);

        rig.WolfAi.OnAggro(rig.Player);
        rig.Script.SetPhase(0);

        // Not yet: the duration (500 ms) has not run out.
        rig.Script.OnUpdate(100);
        Assert.Equal(0u, rig.Script.Phase);

        // Over: only the row of this group and of this speaker (or any speaker) runs.
        rig.Script.OnUpdate(500);
        Assert.Equal(3u, rig.Script.Phase);

        // It fires once: a later update raises nothing.
        rig.Script.SetPhase(0);
        rig.Script.OnUpdate(1000);
        Assert.Equal(0u, rig.Script.Phase);
    }

    [Fact]
    public void MovementInform_MapsMotionTypesToTheAzerothCoreNumbers_AndMatchesTypeAndPoint()
    {
        using var rig = SmartRig.Start(
        [
            Make(Wolf, 0, 0, 34, 22, 1, e1: 8, e2: 4, a1: 2),
            Make(Wolf, 0, 1, 34, 22, 1, e1: 6, e2: 0, a1: 7),
            Make(Wolf, 0, 2, 34, 22, 1, e1: 0, e2: 9, a1: 11),
        ]);

        // Point (AC 8) at another point id: no row.
        rig.WolfAi.OnMovementInform(MovementGeneratorType.Point, 5);
        Assert.Equal(0u, rig.Script.Phase);

        // Waypoint motion is AC type 2: no row names it at node 4.
        rig.WolfAi.OnMovementInform(MovementGeneratorType.Waypoint, 4);
        Assert.Equal(0u, rig.Script.Phase);

        // Point 4 of type Point (vmangos 9, AC 8).
        rig.WolfAi.OnMovementInform(MovementGeneratorType.Point, 4);
        Assert.Equal(2u, rig.Script.Phase);

        // Home is AC type 6 whatever the point id; vmangos' own number for it is 7.
        rig.WolfAi.OnMovementInform(MovementGeneratorType.Home, 1);
        Assert.Equal(7u, rig.Script.Phase);

        // Any type at point 9.
        rig.WolfAi.OnMovementInform(MovementGeneratorType.Chase, 9);
        Assert.Equal(11u, rig.Script.Phase);

        Assert.Equal<uint>(
            [0u, 1u, 2u, 4u, 5u, 6u, 8u, 9u, 10u, 14u, 18u],
            new[]
            {
                MovementGeneratorType.Idle, MovementGeneratorType.Random, MovementGeneratorType.Waypoint, MovementGeneratorType.Confused,
                MovementGeneratorType.Chase, MovementGeneratorType.Home, MovementGeneratorType.Point, MovementGeneratorType.Fleeing,
                MovementGeneratorType.Distract, MovementGeneratorType.Follow, MovementGeneratorType.Formation,
            }.Select(t => SmartMotionTypes.ToSmart(t)!.Value));
        Assert.Null(SmartMotionTypes.ToSmart((MovementGeneratorType)200));
    }

    [Fact]
    public void Catalog_AcceptsTheNewEvents_AndRefusesEscortReachedPathIdsBadTypesAndABadCooldown()
    {
        var catalog = new SmartScriptCatalog(
        [
            Make(Wolf, 0, 0, 38, 22, 1, e1: 1, e2: 1, a1: 1),
            Make(Wolf, 0, 1, 40, 22, 1, e1: 2, a1: 1),
            Make(Wolf, 0, 2, 52, 22, 1, e1: 1, a1: 1),
            Make(Wolf, 0, 3, 34, 22, 1, e1: 8, e2: 1, a1: 1),
            Make(Wolf, 0, 4, 34, 22, 1, e1: 8, e2: 1, e3: 5, a1: 1),
            Make(Wolf, 0, 5, 34, 22, 1, e1: 20, e2: 1, a1: 1),
            Make(Wolf, 0, 6, 38, 22, 1, e1: 1, e2: 1, e3: 9, e4: 2, a1: 1),
        ]);

        Assert.Equal(3, catalog.For(WolfEntry, 0).Count);
        string[] reasons = [.. catalog.Rejected.OrderBy(r => r.Id).Select(r => r.Reason)];
        Assert.Equal(4, reasons.Length);
        Assert.Contains("ESCORT_REACHED needs a SMART_ACTION_WP_START escort", reasons[0], StringComparison.Ordinal);
        Assert.Contains("MOVEMENTINFORM path id 5", reasons[1], StringComparison.Ordinal);
        Assert.Contains("MOVEMENTINFORM movement type 20", reasons[2], StringComparison.Ordinal);
        Assert.Contains("DATA_SET cooldown 9..2", reasons[3], StringComparison.Ordinal);
    }
}
