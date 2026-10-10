using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi.SmartAi;

/// <summary>AzerothCore SmartScript.cpp semantics for the slice-1 events, actions and targets (docs/integration/smartai-20261010.md).</summary>
public sealed class SmartAiTests
{
    private const uint SummonEntry = 300;

    private static SmartScriptRow Row(ushort id, SmartEvent ev, SmartAction action, SmartTarget target = SmartTarget.Self,
        uint e1 = 0, uint e2 = 0, uint e3 = 0, uint e4 = 0, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint a4 = 0, uint a5 = 0,
        uint phaseMask = 0, uint flags = 0, byte chance = 100, ushort link = 0, uint t1 = 0, uint t2 = 0, uint t3 = 0, uint t4 = 0,
        float x = 0, float y = 0, float z = 0, int entryOrGuid = (int)WolfEntry)
        => new()
        {
            EntryOrGuid = entryOrGuid, Id = id, Link = link, EventType = (byte)ev, EventPhaseMask = phaseMask, EventFlags = flags,
            EventChance = chance, EventParam1 = e1, EventParam2 = e2, EventParam3 = e3, EventParam4 = e4,
            ActionType = (byte)action, ActionParam1 = a1, ActionParam2 = a2, ActionParam3 = a3, ActionParam4 = a4, ActionParam5 = a5,
            TargetType = (byte)target, TargetParam1 = t1, TargetParam2 = t2, TargetParam3 = t3, TargetParam4 = t4, TargetX = x, TargetY = y, TargetZ = z,
        };

    private sealed record Fight(WorldRuntime World, Map Map, CreatureMapSystem System, FakeCaster Spells, Player Player, FakeSession Session,
        Creature Wolf, CreatureSmartAI Ai) : IDisposable
    {
        public SmartScript Script => Ai.Script;
        public void Dispose() => World.Dispose();
    }

    private static Fight Start(params SmartScriptRow[] rows)
    {
        var content = new CreatureContent(
            [Template(configure: t => t.AIName = CreatureAiFactory.SmartAIName), Template(SummonEntry)],
            [Spawn(1, WolfEntry, 5, 0)], [], [], [],
            new CreatureAiContent([], [], new BroadcastTextCatalog([new BroadcastText(9001, "Grr, $N!", "", 0, 0, 0, [0, 0, 0], [0, 0, 0])]),
                [], EventAiDialect.CMangos, [])
            { SmartScripts = new SmartScriptCatalog(rows) });
        var fake = new FakeCaster();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = fake });
        (Player player, FakeSession session) = AddPlayer(world, 1, 0, 0);
        Creature wolf = system.Creatures.Single(c => c.Entry == WolfEntry);
        var ai = Assert.IsType<CreatureSmartAI>(wolf.AI);
        return new Fight(world, map, system, fake, player, session, wolf, ai);
    }

    private static void Pull(Fight f) => f.Map.Combat.DealDamage(f.Player, f.Wolf, 1, direct: false);

    [Fact]
    public void Catalog_SpawnRowsReplaceEntryRows_AndOtherSourceTypesAreNotCreatureRows()
    {
        var catalog = new SmartScriptCatalog(
        [
            Row(0, SmartEvent.Aggro, SmartAction.SetEventPhase, a1: 1),
            Row(1, SmartEvent.Aggro, SmartAction.SetEventPhase, a1: 1),
            Row(0, SmartEvent.Aggro, SmartAction.SetEventPhase, a1: 2, entryOrGuid: -7),
            Row(0, SmartEvent.Aggro, SmartAction.SetEventPhase) with { SourceType = 1, EntryOrGuid = 5 },
        ]);
        Assert.Equal(3, catalog.Count);
        Assert.Equal(2, catalog.For(WolfEntry, 0).Count);
        Assert.Equal(2u, Assert.Single(catalog.For(WolfEntry, 7)).ActionParam1);
        Assert.Empty(catalog.For(5, 0));
    }

    [Fact]
    public void SmartAIName_SelectsSmartAI_AndUnsupportedRowsAreReportedNotRun()
    {
        using Fight f = Start(
            Row(0, SmartEvent.Aggro, SmartAction.SetEventPhase, a1: 1),
            Row(1, (SmartEvent)3, SmartAction.SetEventPhase, a1: 2),
            Row(2, SmartEvent.Aggro, (SmartAction)41),
            Row(3, SmartEvent.Aggro, SmartAction.Cast, (SmartTarget)13));
        Assert.Single(f.Script.Events);
        Assert.Equal(3, f.Script.Unsupported.Count);
    }

    [Fact]
    public void UpdateInAndOutOfCombat_RunOnTheirInitialThenRepeatTimers_OnlyInTheirCombatState()
    {
        using Fight f = Start(
            Row(0, SmartEvent.UpdateOutOfCombat, SmartAction.Cast, SmartTarget.Self, 1000, 1000, 3000, 3000, a1: 100),
            Row(1, SmartEvent.UpdateInCombat, SmartAction.Cast, SmartTarget.Victim, 2000, 2000, 4000, 4000, a1: 200));
        f.Ai.OnUpdate(999);
        Assert.Empty(f.Spells.Casts);
        f.Ai.OnUpdate(2);
        Assert.Single(f.Spells.Casts, c => c.Spell == 100);
        Assert.Equal(3000u, f.Script.Events[0].TimerMs);
        Pull(f);
        f.Ai.OnUpdate(5000);
        Assert.Single(f.Spells.Casts, c => c.Spell == 100); // the OOC row waits in combat
        Assert.Single(f.Spells.Casts, c => c.Spell == 200 && ReferenceEquals(c.Target, f.Player));
        f.Ai.OnUpdate(3999);
        Assert.Single(f.Spells.Casts, c => c.Spell == 200);
        f.Ai.OnUpdate(2);
        Assert.Equal(2, f.Spells.Casts.Count(c => c.Spell == 200));
    }

    [Fact]
    public void HealthPct_FiresInsideItsBand_AndNotRepeatableFiresOnceUntilReset()
    {
        using Fight f = Start(
            Row(0, SmartEvent.HealthPct, SmartAction.Cast, SmartTarget.Self, 0, 50, 0, 0, a1: 300, flags: (uint)SmartEventFlags.NotRepeatable));
        Pull(f);
        f.Ai.OnUpdate(100);
        Assert.Empty(f.Spells.Casts);
        f.Wolf.Health = f.Wolf.MaxHealth / 2;
        f.Ai.OnUpdate(100);
        f.Ai.OnUpdate(100);
        Assert.Single(f.Spells.Casts, c => c.Spell == 300);
        f.Script.OnReset();
        f.Ai.OnUpdate(100);
        Assert.Equal(2, f.Spells.Casts.Count(c => c.Spell == 300));
    }

    [Fact]
    public void AggroDeathEvadeAndSpellHit_RunWithTheirInvoker_AndSpellHitHonoursSpellAndCooldown()
    {
        using Fight f = Start(
            Row(0, SmartEvent.Aggro, SmartAction.Cast, SmartTarget.ActionInvoker, a1: 1),
            Row(1, SmartEvent.Evade, SmartAction.Cast, SmartTarget.Self, a1: 2),
            Row(2, SmartEvent.Death, SmartAction.Cast, SmartTarget.ActionInvoker, a1: 3),
            Row(3, SmartEvent.SpellHit, SmartAction.Cast, SmartTarget.ActionInvoker, 77, 0, 5000, 5000, a1: 4));
        f.Ai.OnAggro(f.Player);
        Assert.Contains(f.Spells.Casts, c => c.Spell == 1 && ReferenceEquals(c.Target, f.Player));
        f.Ai.OnEvade();
        Assert.Contains(f.Spells.Casts, c => c.Spell == 2 && ReferenceEquals(c.Target, f.Wolf));
        f.Ai.OnSpellHit(f.Player, SpellTestKit.Spell(76));
        Assert.DoesNotContain(f.Spells.Casts, c => c.Spell == 4);
        f.Ai.OnSpellHit(f.Player, SpellTestKit.Spell(77));
        f.Ai.OnSpellHit(f.Player, SpellTestKit.Spell(77));
        Assert.Single(f.Spells.Casts, c => c.Spell == 4);
        f.Ai.OnUpdate(5001);
        f.Ai.OnSpellHit(f.Player, SpellTestKit.Spell(77));
        Assert.Equal(2, f.Spells.Casts.Count(c => c.Spell == 4));
        f.Ai.OnDeath(f.Player);
        Assert.Contains(f.Spells.Casts, c => c.Spell == 3 && ReferenceEquals(c.Target, f.Player));
    }

    [Fact]
    public void Phases_GateEvents_SetIncDecAndCapAtTwelve_AndResetReturnsToZero()
    {
        using Fight f = Start(
            Row(0, SmartEvent.Aggro, SmartAction.SetEventPhase, a1: 1),
            Row(1, SmartEvent.Aggro, SmartAction.Cast, SmartTarget.Self, a1: 10, phaseMask: 0b10), // phase 2 only
            Row(2, SmartEvent.Evade, SmartAction.IncEventPhase, a1: 1),
            Row(3, SmartEvent.Death, SmartAction.IncEventPhase, a1: 0, a2: 5));
        f.Ai.OnAggro(f.Player);
        Assert.Equal(1u, f.Script.Phase);
        Assert.DoesNotContain(f.Spells.Casts, c => c.Spell == 10);
        f.Ai.OnEvade();
        Assert.Equal(2u, f.Script.Phase);
        f.Ai.OnAggro(f.Player); // row 0 runs first and puts phase 1 back, so row 1 is skipped again (rows run in id order)
        Assert.Equal(1u, f.Script.Phase);
        f.Script.SetPhase(40);
        Assert.Equal(SmartScript.MaxPhase, f.Script.Phase);
        f.Ai.OnDeath(null);
        Assert.Equal(7u, f.Script.Phase);
        f.Script.OnReset();
        Assert.Equal(0u, f.Script.Phase);
        Assert.False(f.Script.IsInPhase(1));
    }

    [Fact]
    public void TimedEvents_CreateTriggerAndRemove()
    {
        using Fight f = Start(
            Row(0, SmartEvent.Aggro, SmartAction.CreateTimedEvent, a1: 5, a2: 1000, a3: 1000),
            Row(1, SmartEvent.TimedEventTriggered, SmartAction.Cast, SmartTarget.Self, 5, a1: 55),
            Row(2, SmartEvent.Evade, SmartAction.CreateTimedEvent, a1: 6, a2: 500, a3: 500, a4: 500, a5: 500),
            Row(3, SmartEvent.TimedEventTriggered, SmartAction.Cast, SmartTarget.Self, 6, a1: 66),
            Row(4, SmartEvent.Death, SmartAction.RemoveTimedEvent, a1: 6));
        f.Ai.OnAggro(f.Player);
        f.Ai.OnUpdate(999);
        Assert.Empty(f.Spells.Casts);
        f.Ai.OnUpdate(2);
        Assert.Single(f.Spells.Casts, c => c.Spell == 55);
        Assert.Empty(f.Script.StoredEvents); // not repeatable: gone after it ran
        f.Ai.OnEvade();
        f.Ai.OnUpdate(501);
        f.Ai.OnUpdate(501);
        Assert.Equal(2, f.Spells.Casts.Count(c => c.Spell == 66));
        f.Ai.OnDeath(null);
        Assert.Empty(f.Script.StoredEvents);
    }

    [Fact]
    public void Link_RunsTheLinkedRowWithTheSameInvoker_AndChanceZeroIsAlwaysAndAFailedRollSkipsTheLink()
    {
        using Fight f = Start(
            Row(0, SmartEvent.Aggro, SmartAction.SetEventPhase, a1: 3, link: 1),
            Row(1, SmartEvent.Link, SmartAction.Cast, SmartTarget.ActionInvoker, a1: 9));
        f.Ai.OnAggro(f.Player);
        Assert.Single(f.Spells.Casts, c => c.Spell == 9 && ReferenceEquals(c.Target, f.Player));
    }

    [Fact]
    public void Cast_AuraNotPresentSkips_InterruptPreviousInterrupts_AndABusyCasterDelaysTheTimer()
    {
        using Fight f = Start(
            Row(0, SmartEvent.UpdateOutOfCombat, SmartAction.Cast, SmartTarget.Self, 100, 100, 100, 100, a1: 1,
                a2: (uint)(SmartCastFlags.AuraNotPresent | SmartCastFlags.Triggered)),
            Row(1, SmartEvent.Aggro, SmartAction.Cast, SmartTarget.Self, a1: 2, a2: (uint)SmartCastFlags.InterruptPrevious));
        f.Spells.Auras.Add((f.Wolf, 1u));
        f.Ai.OnUpdate(101);
        Assert.Empty(f.Spells.Casts);
        f.Spells.Auras.Clear();
        f.Spells.Casting = true;
        f.Ai.OnUpdate(101);
        Assert.Empty(f.Spells.Casts);
        f.Spells.Casting = false;
        f.Ai.OnUpdate(1);
        Assert.Single(f.Spells.Casts, c => c.Spell == 1 && c.Triggered);
        f.Ai.OnAggro(f.Player);
        Assert.Equal(1, f.Spells.Interrupts);
    }

    [Fact]
    public void Talk_SaysTheBroadcastTextToTheTarget()
    {
        using Fight f = Start(Row(0, SmartEvent.Aggro, SmartAction.Talk, SmartTarget.ActionInvoker, a1: 9001));
        Packets(f.Session, WorldOpcode.SmsgMessagechat);
        f.Ai.OnAggro(f.Player);
        MonsterChat say = ParseMonsterChat(Assert.Single(Packets(f.Session, WorldOpcode.SmsgMessagechat)));
        Assert.Contains(f.Player.Name, say.Message);
    }

    [Fact]
    public void Summon_AtAPositionOrOnEachTarget_AndMove_ToAPosition()
    {
        using Fight f = Start(
            Row(0, SmartEvent.Aggro, SmartAction.SummonCreature, SmartTarget.Position, a1: SummonEntry, a3: 10000, x: 20, y: 0, z: 0),
            Row(1, SmartEvent.Evade, SmartAction.SummonCreature, SmartTarget.Self, a1: SummonEntry, a3: 10000, x: 2),
            Row(2, SmartEvent.Death, SmartAction.MoveToPos, SmartTarget.Position, a1: 4, x: 30, y: 5, z: 0));
        f.Ai.OnAggro(f.Player);
        Creature atPoint = Assert.Single(f.System.Creatures, c => c.Entry == SummonEntry);
        Assert.Equal(20f, atPoint.X);
        f.Ai.OnEvade();
        Assert.Contains(f.System.Creatures, c => c.Entry == SummonEntry && c.X == f.Wolf.X + 2);
        f.Ai.OnDeath(null);
        Assert.Equal(MovementGeneratorType.Point, f.Wolf.Motion.CurrentType);
    }

    [Fact]
    public void Targets_ThreatAndRangeTypes()
    {
        using Fight f = Start();
        Pull(f);
        Assert.Same(f.Player, Assert.Single(f.Script.GetTargets(Row(0, SmartEvent.Aggro, SmartAction.None, SmartTarget.Victim), null)));
        Assert.Same(f.Player, Assert.Single(f.Script.GetTargets(Row(0, SmartEvent.Aggro, SmartAction.None, SmartTarget.ThreatList), null)));
        Assert.Empty(f.Script.GetTargets(Row(0, SmartEvent.Aggro, SmartAction.None, SmartTarget.HostileSecondAggro), null));
        Assert.Same(f.Player, Assert.Single(f.Script.GetTargets(Row(0, SmartEvent.Aggro, SmartAction.None, SmartTarget.ClosestPlayer), null)));
        Assert.Empty(f.Script.GetTargets(Row(0, SmartEvent.Aggro, SmartAction.None, SmartTarget.PlayerRange, t1: 10, t2: 20), null));
        Assert.Single(f.Script.GetTargets(Row(0, SmartEvent.Aggro, SmartAction.None, SmartTarget.PlayerDistance, t1: 10), null));
        Creature other = f.System.SpawnTemporary(f.System.Content.FindTemplate(SummonEntry)!, f.Wolf.X + 3, f.Wolf.Y, f.Wolf.Z, 0);
        Assert.Same(other, Assert.Single(f.Script.GetTargets(Row(0, SmartEvent.Aggro, SmartAction.None, SmartTarget.ClosestCreature, t1: SummonEntry), null)));
        Assert.Single(f.Script.GetTargets(Row(0, SmartEvent.Aggro, SmartAction.None, SmartTarget.CreatureDistance, t1: SummonEntry, t2: 5, t3: 1), null));
        Assert.Empty(f.Script.GetTargets(Row(0, SmartEvent.Aggro, SmartAction.None, SmartTarget.CreatureRange, t1: SummonEntry, t2: 4, t3: 10, t4: 2), null));
        Creature summoned = f.System.SummonAt(f.Wolf, SummonEntry, f.Wolf.X + 20, f.Wolf.Y, f.Wolf.Z, 0, null, 0)!;
        var summonedAi = new CreatureSmartAI(summoned, f.System.Content.Ai);
        Assert.Same(f.Wolf, Assert.Single(summonedAi.Script.GetTargets(Row(0, SmartEvent.Aggro, SmartAction.None, SmartTarget.OwnerOrSummoner), null)));
    }
}
