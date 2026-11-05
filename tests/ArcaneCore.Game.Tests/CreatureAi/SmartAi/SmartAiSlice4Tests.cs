using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureAi.SmartAi.SmartRows;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi.SmartAi;

/// <summary>
/// SmartAI slice 4: actions SET_DATA (45) and CLOSE_GOSSIP (72) (AzerothCore SmartScript.cpp:1469-1495 and :2010-2016). Rows use raw AzerothCore ids.
/// Written in a lane that does not build or run tests; unproven until the integration gate.
/// </summary>
public sealed class SmartAiSlice4Tests
{
    private const int Wolf = (int)WolfEntry;

    [Fact]
    public void SetData_OnSelf_RaisesDataSetWithTheMatchingIdAndValueOnly_AndTheSenderIsTheInvoker()
    {
        using var rig = SmartRig.Start(
        [
            Make(Wolf, 0, 0, 4, 45, 1, a1: 3, a2: 7),
            Make(Wolf, 0, 1, 38, 22, 1, e1: 3, e2: 7, a1: 4),
            Make(Wolf, 0, 2, 38, 22, 1, e1: 3, e2: 8, a1: 9),
            Make(Wolf, 0, 3, 38, 22, 1, e1: 4, e2: 7, a1: 10),
        ]);

        rig.WolfAi.OnAggro(rig.Player);

        Assert.Equal(4u, rig.Script.Phase);
        Assert.Same(rig.Wolf, rig.Script.LastInvoker);
        Assert.Empty(rig.Script.Unsupported);
    }

    [Fact]
    public void SetData_OnAnotherSmartCreature_RunsItsDataSetRow()
    {
        using var rig = SmartRig.Start(
        [
            // The wolf sends data 1 = 2 to the closest creature of entry Other, whose own row sets its phase.
            Make(Wolf, 0, 0, 4, 45, 19, a1: 1, a2: 2, t1: SmartRig.Other, t2: 50),
            Make((int)SmartRig.Other, 0, 0, 38, 22, 1, e1: 1, e2: 2, a1: 6),
        ], creatureSpawns: [Spawn(2, SmartRig.Other, 5, 0)]);
        Creature other = rig.Creatures.Creatures.Single(c => c.Entry == SmartRig.Other);

        rig.WolfAi.OnAggro(rig.Player);

        Assert.Equal(6u, SmartRig.AiOf(other).Script.Phase);
        Assert.Equal(0u, rig.Script.Phase);
    }

    [Fact]
    public void SetData_ActionOnAGameObjectScript_TargetingItself_RaisesItsOwnDataSetRow_WithANullInvoker()
    {
        // Source type 1: JUST_CREATED -> action 45 (target 1 = self) -> event 38 on the same object -> action 22 sets phase 5.
        using var rig = SmartRig.Start(
            [
                Make((int)SmartRig.ObjectEntry, 1, 0, 63, 45, 1, a1: 3, a2: 7),
                Make((int)SmartRig.ObjectEntry, 1, 1, 38, 22, 1, e1: 3, e2: 7, a1: 5),
                Make((int)SmartRig.ObjectEntry, 1, 2, 38, 22, 1, e1: 3, e2: 8, a1: 9),
            ],
            creatureSpawns: [], objectSpawns: [GameObjectTestKit.GoSpawn(1, SmartRig.ObjectEntry, 0, 2)]);
        GameObject go = rig.ObjectBySpawn(1);
        Assert.Empty(rig.Catalog.Rejected);

        SmartScript script = rig.ObjectScript(go);

        Assert.Equal(5u, script.Phase);
        Assert.Null(script.LastInvoker);
    }

    [Fact]
    public void SetData_DirectCallOnAGameObjectAi_RaisesItsDataSetEvent()
    {
        using var rig = SmartRig.Start(
            [Make((int)SmartRig.ObjectEntry, 1, 0, 38, 22, 1, e1: 3, e2: 7, a1: 5)],
            creatureSpawns: [], objectSpawns: [GameObjectTestKit.GoSpawn(1, SmartRig.ObjectEntry, 0, 2)]);
        GameObject go = rig.ObjectBySpawn(1);
        Assert.Empty(rig.Catalog.Rejected);

        var ai = Assert.IsType<SmartGameObjectAi>(rig.Objects!.AiFor(go));
        ai.SetData(rig.Objects, go, 3, 8);
        Assert.Equal(0u, rig.ObjectScript(go).Phase);
        ai.SetData(rig.Objects, go, 3, 7);
        Assert.Equal(5u, rig.ObjectScript(go).Phase);
    }

    [Fact]
    public void CloseGossip_SendsGossipCompleteToEachPlayerTarget_AndToNoCreature()
    {
        using var rig = SmartRig.Start(
        [
            Make(Wolf, 0, 0, 4, 72, 7),
            Make(Wolf, 0, 1, 4, 72, 1),
        ]);
        Assert.Empty(Packets(rig.Session, WorldOpcode.SmsgGossipComplete));

        rig.WolfAi.OnAggro(rig.Player);

        byte[] packet = Assert.Single(Packets(rig.Session, WorldOpcode.SmsgGossipComplete));
        Assert.Empty(packet);
    }

    [Fact]
    public void Catalog_Slice4_AcceptsActions45And72ForCreaturesAndObjects_AndEvent38ForObjects_RefusesAreaTriggers()
    {
        var catalog = new SmartScriptCatalog(
        [
            Make(Wolf, 0, 0, 4, 45, 1, a1: 1, a2: 1),
            Make(Wolf, 0, 1, 4, 72, 7),
            Make((int)SmartRig.ObjectEntry, 1, 0, 38, 45, 1, e1: 1, e2: 1),
            Make((int)SmartRig.ObjectEntry, 1, 1, 64, 72, 7),
            Make(101, 2, 0, 46, 45, 7),
            Make(101, 2, 1, 46, 72, 7),
        ]);

        Assert.Single(catalog.For(WolfEntry, 0), r => r.ActionType == 45);
        Assert.Equal(2, catalog.For(WolfEntry, 0).Count);
        Assert.Equal(2, catalog.ForGameObject(SmartRig.ObjectEntry, 0).Count);
        string[] reasons = [.. catalog.Rejected.OrderBy(r => r.Id).Select(r => r.Reason)];
        Assert.Equal(2, reasons.Length);
        Assert.Contains("action 45 is not supported for source type 2", reasons[0], StringComparison.Ordinal);
        Assert.Contains("action 72 is not supported for source type 2", reasons[1], StringComparison.Ordinal);
    }
}
