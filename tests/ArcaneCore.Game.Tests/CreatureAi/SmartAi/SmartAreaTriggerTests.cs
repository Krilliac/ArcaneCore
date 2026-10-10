using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureAi.SmartAi.SmartRows;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi.SmartAi;

/// <summary>
/// Source type 2 (area trigger): SMART_EVENT_AREATRIGGER_ONTRIGGER (46) with its trigger id filter, run in a script made for that one trigger with the
/// invoking player as invoker and as the reference point of the range targets (AzerothCore SmartTrigger, SmartAI.cpp:1586-1601; the event at
/// SmartScript.cpp:4743-4749). Dead players and game masters run nothing (docs/integration/smartai-slice2-20261010.md).
/// </summary>
public sealed class SmartAreaTriggerTests
{
    private const int Trigger = 100;

    private static int Chats(SmartRig rig) => Packets(rig.Session, WorldOpcode.SmsgMessagechat).Count;

    private static SmartScriptRow Say(int trigger, ushort id = 0, uint filter = 0)
        => TriggerRow(trigger, id, SmartAction.Talk, SmartTarget.ClosestCreature, e1: filter, a1: 9001, t1: SmartRig.Speaker);

    [Fact]
    public void AreaTrigger_RunsItsRowsForAnAlivePlayer_WithThePlayerAsInvoker()
    {
        using SmartRig rig = SmartRig.Start([Say(Trigger)], creatureSpawns: [Spawn(2, SmartRig.Speaker, 3, 0)]);
        Creature speaker = rig.Creatures.Creatures.Single(c => c.Entry == SmartRig.Speaker);
        rig.Session.Clear();

        Assert.True(SmartAreaTrigger.Run(rig.Catalog, rig.Player, Trigger));

        MonsterChat say = ParseMonsterChat(Assert.Single(Packets(rig.Session, WorldOpcode.SmsgMessagechat)));
        Assert.Equal(speaker.Template.Name, say.Name);                                 // the closest creature of the entry speaks
        Assert.Contains(rig.Player.Name, say.Message, StringComparison.Ordinal);       // "$N" is the invoking player
    }

    [Fact]
    public void AreaTrigger_WithNoRowsForTheId_RunsNothing()
    {
        using SmartRig rig = SmartRig.Start([Say(Trigger)], creatureSpawns: [Spawn(2, SmartRig.Speaker, 3, 0)]);
        rig.Session.Clear();
        Assert.False(SmartAreaTrigger.Run(rig.Catalog, rig.Player, Trigger + 1));
        Assert.False(SmartAreaTrigger.Run(SmartScriptCatalog.Empty, rig.Player, Trigger));
        Assert.Equal(0, Chats(rig));
        Assert.Throws<ArgumentNullException>(() => SmartAreaTrigger.Run(null!, rig.Player, Trigger));
        Assert.Throws<ArgumentNullException>(() => SmartAreaTrigger.Run(rig.Catalog, null!, Trigger));
    }

    [Fact]
    public void AreaTrigger_DeadPlayerOrGameMaster_RunsNothing()
    {
        using SmartRig rig = SmartRig.Start([Say(Trigger)], creatureSpawns: [Spawn(2, SmartRig.Speaker, 3, 0)]);
        rig.Session.Clear();

        Assert.True(SmartAreaTrigger.Run(rig.Catalog, rig.Player, Trigger)); // the control: the same player, alive and not a game master
        Assert.Equal(1, Chats(rig));

        rig.Player.Flags |= PlayerFlags.Gm;
        Assert.False(SmartAreaTrigger.Run(rig.Catalog, rig.Player, Trigger));
        Assert.Equal(1, Chats(rig));

        rig.Player.Flags &= ~PlayerFlags.Gm;
        rig.Map.Combat.Kill(rig.Wolf, rig.Player);
        Assert.False(rig.Player.IsAlive);
        Assert.False(SmartAreaTrigger.Run(rig.Catalog, rig.Player, Trigger));
        Assert.Equal(1, Chats(rig));
    }

    [Fact]
    public void AreaTrigger_Param1FiltersTheTriggerId()
    {
        // The rows of an id are found by entryorguid; the event's param1 then names the trigger it fires for (0 = any), SmartScript.cpp:4743-4749.
        using SmartRig rig = SmartRig.Start(
        [
            Say(100, filter: 0),     // any trigger
            Say(101, filter: 102),   // filter names another trigger: never fires for 101
            Say(103, filter: 103),   // filter names this trigger
        ], creatureSpawns: [Spawn(2, SmartRig.Speaker, 3, 0)]);
        rig.Session.Clear();

        Assert.True(SmartAreaTrigger.Run(rig.Catalog, rig.Player, 100));
        Assert.Equal(1, Chats(rig));
        Assert.True(SmartAreaTrigger.Run(rig.Catalog, rig.Player, 101)); // a script ran, its row did not match
        Assert.Equal(1, Chats(rig));
        Assert.True(SmartAreaTrigger.Run(rig.Catalog, rig.Player, 103));
        Assert.Equal(2, Chats(rig));
        Assert.False(SmartAreaTrigger.Run(rig.Catalog, rig.Player, 102)); // no rows under 102 at all
        Assert.Equal(2, Chats(rig));
    }

    [Fact]
    public void AreaTrigger_ClosestCreatureMeasuresFromThePlayer_AndCanCallATimedListOnIt()
    {
        // The player is at the origin. Of the two smart creatures of the entry the one at -3 is closest to the player; seen from the wolf (5,0) the one
        // at 10 would be, so the reference point is the invoker, not another creature.
        using SmartRig rig = SmartRig.Start(
        [
            TriggerRow(Trigger, 0, SmartAction.CallTimedActionList, SmartTarget.ClosestCreature, a1: 50, a2: 2, t1: SmartRig.Other),
            ListRow(50, 0, SmartAction.Cast, SmartTarget.ActionInvoker, delay: 100, a1: 5),
        ], creatureSpawns: [Spawn(2, SmartRig.Other, 10, 0), Spawn(3, SmartRig.Other, -3, 0)]);
        CreatureSmartAI far = SmartRig.AiOf(rig.Creatures.Creatures.Single(c => c.Spawn?.Guid == 2));
        CreatureSmartAI near = SmartRig.AiOf(rig.Creatures.Creatures.Single(c => c.Spawn?.Guid == 3));

        Assert.True(SmartAreaTrigger.Run(rig.Catalog, rig.Player, Trigger));
        Assert.Single(near.Script.TimedActionList);
        Assert.Empty(far.Script.TimedActionList);
        Assert.Empty(rig.Script.TimedActionList);
        Assert.Same(rig.Player, near.Script.LastInvoker);

        far.OnUpdate(500);
        Assert.Empty(rig.Spells.Casts);
        near.OnUpdate(101);
        (uint spell, Unit? target, _) = Assert.Single(rig.Spells.Casts);
        Assert.Equal(5u, spell);
        Assert.Same(rig.Player, target);
    }

    [Fact]
    public void AreaTrigger_ClosestCreatureOutOfReach_FindsNobody()
    {
        // The closest-creature search is 100 yards when no maximum is given: a speaker 200 yards off is not found.
        using SmartRig rig = SmartRig.Start([Say(Trigger)], creatureSpawns: [Spawn(2, SmartRig.Speaker, 200, 0)]);
        rig.Session.Clear();
        Assert.True(SmartAreaTrigger.Run(rig.Catalog, rig.Player, Trigger));
        Assert.Equal(0, Chats(rig));
    }
}
