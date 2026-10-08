using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi.EventAi;

/// <summary>
/// The cmangos EventAI engine semantics (CreatureEventAI.cpp) through the running AI only: flags above one byte,
/// the combat-action contract, repeat timers, the KILL parameter layout, phases and the 500 ms batch. Every
/// expected value is derived from the cited cmangos code, not from the previous implementation.
/// </summary>
public sealed class EngineSemanticsTests
{
    private static CreatureAiEvent Row(uint id, EventAiEventType type, uint flags = 0, byte chance = 100, int p1 = 0, int p2 = 0, int p3 = 0, int p4 = 0, int p5 = 0,
        uint phaseMask = 0, CreatureAiAction a1 = default, CreatureAiAction a2 = default, CreatureAiAction a3 = default, uint creatureId = WolfEntry, uint guid = 0)
        => new()
        {
            Id = id,
            CreatureId = guid == 0 ? creatureId : 0,
            CreatureGuid = guid,
            EventType = (byte)type,
            Flags = flags,
            Chance = chance,
            Param1 = p1,
            Param2 = p2,
            Param3 = p3,
            Param4 = p4,
            Param5 = p5,
            InversePhaseMask = phaseMask,
            Action1 = a1,
            Action2 = a2,
            Action3 = a3,
        };

    private static CreatureAiAction Act(EventAiActionType type, int p1 = 0, int p2 = 0, int p3 = 0) => new((byte)type, p1, p2, p3);

    private static CreatureAiAction Cast(int spell, EventAiTarget target = EventAiTarget.Self, int flags = 0) => Act(EventAiActionType.Cast, spell, (int)target, flags);

    private static CreatureContent EventContent(IEnumerable<CreatureAiEvent> events, params CreatureSpawn[] extraSpawns)
        => new(
            [Template(configure: t => t.AIName = CreatureAiFactory.EventAIName)],
            [Spawn(1, WolfEntry, 5, 0), .. extraSpawns], [], [], [],
            new CreatureAiContent(events, []));

    private sealed record Fight(WorldRuntime World, Map Map, CreatureMapSystem System, FakeCaster Spells, Player Player, Creature Wolf, CreatureEventAI Ai) : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    private static Fight Start(IEnumerable<CreatureAiEvent> events, ICreatureSpellCaster? caster = null, CreatureOptions? options = null)
    {
        var fake = caster as FakeCaster ?? new FakeCaster();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(EventContent(events), new CreatureAiServices { Spells = caster ?? fake }, options);
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        return new Fight(world, map, system, fake, player, wolf, (CreatureEventAI)wolf.AI!);
    }

    private static void Pull(Fight f, uint damage = 1) => f.Map.Combat.DealDamage(f.Player, f.Wolf, damage, direct: false);

    // --- flags ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1024u, 1)]  // EFLAG_COMBAT_ACTION without EFLAG_REPEATABLE: once
    [InlineData(1025u, 4)]  // with EFLAG_REPEATABLE: repeats on its timers
    public void HealthEvent_WithCombatActionFlags_FiresOnceOrRepeatsByTheRepeatFlag(uint flags, int expectedAtLeast)
    {
        // HP <= 50% (param 1), >= 0% (param 2), repeat every 1000 ms (params 3 and 4).
        using Fight f = Start([Row(1, EventAiEventType.HealthPercent, flags, p1: 50, p3: 1000, p4: 1000, a1: Cast(900))]);
        Pull(f, 30); // 25 of 55 health left: 45 percent

        Run(f.World, 6000);

        int casts = f.Spells.Casts.Count(c => c.Spell == 900);
        if (flags == 1024)
        {
            Assert.Equal(expectedAtLeast, casts);
        }
        else
        {
            Assert.InRange(casts, expectedAtLeast, 6);
        }
    }

    [Fact]
    public void AMultiActionRowWithCombatActionFlags_RunsEveryAction_AndRandomActionRunsExactlyOne()
    {
        CreatureAiAction[] actions = [Cast(901), Cast(902), Cast(903)];
        using Fight all = Start([Row(1, EventAiEventType.Aggro, 1025, a1: actions[0], a2: actions[1], a3: actions[2])]);
        Pull(all);
        Assert.Equal([901u, 902u, 903u], all.Spells.Casts.Select(c => c.Spell));

        // 0x21 = repeatable | random action: one of the three.
        using Fight one = Start([Row(1, EventAiEventType.Aggro, 0x21, a1: actions[0], a2: actions[1], a3: actions[2])]);
        Pull(one);
        Assert.Single(one.Spells.Casts);
    }

    // --- the combat-action contract ---------------------------------------------------------------

    /// <summary>A spell caster that refuses a number of casts before it accepts (cmangos DoCastSpellIfCan failing).</summary>
    private sealed class RefusingCaster(int refusals) : ICreatureSpellCaster
    {
        private int _remaining = refusals;

        public List<uint> Attempts { get; } = [];

        public event Action<Unit, Unit, SpellInfo>? SpellHit
        {
            add { }
            remove { }
        }

        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
        {
            Attempts.Add(spellId);
            return _remaining-- > 0 ? CreatureCastResult.Failed : CreatureCastResult.Ok;
        }

        public bool IsCasting(Creature caster) => false;

        public bool HasAura(Unit unit, uint spellId) => false;

        public void Interrupt(Creature caster)
        {
        }

        public void OnCreatureRemoved(Creature creature)
        {
        }
    }

    [Fact]
    public void CombatActionRow_KeepsItsTimerWhenTheFirstActionFails_AndRetriesAtTheNextBatch()
    {
        // Repeating every 3000 ms; combat-action rows retry at once when the cast fails (cmangos ProcessEvent :649-652).
        var caster = new RefusingCaster(refusals: 2);
        using Fight f = Start([Row(1, EventAiEventType.TimerInCombat, 1025, p1: 0, p2: 0, p3: 3000, p4: 3000, a1: Cast(910), a2: Cast(911))], caster);
        Pull(f);

        Run(f.World, 2400); // four batches (every 600 ms): refused, refused, accepted (then the 3000 ms repeat timer runs)

        Assert.Equal(new uint[] { 910, 910, 910, 911 }, caster.Attempts);
    }

    [Fact]
    public void ARowWithoutTheCombatActionFlag_ResetsItsTimerEvenWhenTheCastFails()
    {
        var caster = new RefusingCaster(refusals: 100);
        using Fight f = Start([Row(1, EventAiEventType.TimerInCombat, 1, p1: 0, p2: 0, p3: 3000, p4: 3000, a1: Cast(910), a2: Cast(911))], caster);
        Pull(f);

        Run(f.World, 2400);

        // One attempt, the second action still runs, and the 3000 ms timer then silences the row.
        Assert.Equal(new uint[] { 910, 911 }, caster.Attempts);
    }

    [Fact]
    public void ACastWhileCasting_FailsUnlessTriggeredOrInterrupting()
    {
        var spells = new FakeCaster { Casting = true };
        using Fight f = Start(
        [
            Row(1, EventAiEventType.Aggro, 0, a1: Cast(920)),
            Row(2, EventAiEventType.Aggro, 0, a1: Cast(921, flags: CreatureEventAI.CastTriggered)),
            Row(3, EventAiEventType.Aggro, 0, a1: Cast(922, flags: CreatureEventAI.CastInterruptPrevious)),
        ], spells);

        Pull(f);

        Assert.Equal([921u, 922u], f.Spells.Casts.Select(c => c.Spell));
        Assert.Equal(1, f.Spells.Interrupts);
    }

    // --- chance ----------------------------------------------------------------------------------

    [Fact]
    public void AZeroChanceRow_NeverCasts_EvenWithoutTheRepeatFlag()
    {
        // Chance 0 never passes (cmangos: chance <= rnd % 100). The failed roll still runs ResetEvent, which sets the
        // repeat timer and (no repeatable flag) disables the row; the engine-internals tests inspect that state.
        using Fight f = Start([Row(1, EventAiEventType.TimerInCombat, 0, chance: 0, p3: 100, p4: 100, a1: Cast(930))]);
        Pull(f);

        Run(f.World, 3000);

        Assert.Empty(f.Spells.Casts);
    }

    // --- KILL ------------------------------------------------------------------------------------

    [Fact]
    public void KillEvent_UsesParametersOneAndTwoAsTheRepeatTimer_AndThreeAsPlayerOnly()
    {
        // KILL = RepeatMin, RepeatMax, PlayerOnly (CreatureEventAI.h:648-654; GetRepeatTimers :559-566).
        using Fight f = Start(
            [Row(1, EventAiEventType.Kill, CreatureEventAI.FlagRepeatable, p1: 5000, p2: 5000, p3: 1, a1: Cast(940))],
            options: null);
        CreatureAI ai = f.Wolf.AI!;
        Creature otherWolf = f.System.SpawnTemporary(Template(), 20, 0, 83.5f, 0);

        ai.OnKilledUnit(otherWolf); // PlayerOnly = 1: a creature victim does not count
        Assert.Empty(f.Spells.Casts);

        ai.OnKilledUnit(f.Player);
        Assert.Single(f.Spells.Casts, c => c.Spell == 940);

        Run(f.World, 1000);
        ai.OnKilledUnit(f.Player); // inside the 5 s repeat timer
        Assert.Single(f.Spells.Casts, c => c.Spell == 940);

        Run(f.World, 5000);
        ai.OnKilledUnit(f.Player);
        Assert.Equal(2, f.Spells.Casts.Count(c => c.Spell == 940));
    }

    // --- phases and the batch ----------------------------------------------------------------------

    [Fact]
    public void TimersDoNotCountDown_WhileTheInversePhaseMaskHidesTheEvent()
    {
        // The row is hidden in phase 0 (bit 0 of the mask). cmangos UpdateEventTimers only decrements timers of events the
        // current phase allows (:1941-1948), so the 2000 ms timer is still whole when phase 1 begins.
        using Fight f = Start(
        [
            Row(1, EventAiEventType.TimerInCombat, 1, p1: 2000, p2: 2000, p3: 100000, p4: 100000, phaseMask: 0x1, a1: Cast(950)),
            Row(2, EventAiEventType.TimerInCombat, 0, p1: 4000, p2: 4000, a1: Act(EventAiActionType.SetPhase, 1)),
        ]);
        Pull(f);

        Run(f.World, 4300); // phase 1 starts at the 4200 ms batch; the hidden row's timer has not moved
        Assert.Equal(1, f.Ai.Phase);
        Assert.Empty(f.Spells.Casts);

        Run(f.World, 2500); // the timer drops by 600 ms per batch from the 4800 ms batch on and reaches 0 at 6600 ms
        Assert.Single(f.Spells.Casts, c => c.Spell == 950);
    }

    [Fact]
    public void TimerEvents_AreEvaluatedInBatches_AndTheIntervalIsConfigurable()
    {
        var options = new CreatureOptions();
        options.EventAi.UpdateIntervalMs = 1000;
        using Fight f = Start([Row(1, EventAiEventType.TimerInCombat, 0, p1: 100, p2: 100, a1: Cast(960))], options: options);
        Pull(f);

        Run(f.World, 1000); // a 1000 ms interval with 100 ms ticks puts the first batch at 1100 ms
        Assert.Empty(f.Spells.Casts);
        Run(f.World, 200);
        Assert.Single(f.Spells.Casts, c => c.Spell == 960);
    }

    [Fact]
    public void Death_RunsTheDeathEventsInThePhaseTheCreatureDiedIn_AndThenReturnsToPhaseZero()
    {
        // The death event summons a creature (a dead creature cannot cast); it is hidden in phase 0 and runs in phase 3.
        CreatureContent content = new(
            [Template(configure: t => t.AIName = CreatureAiFactory.EventAIName), Template(301, t => t.Name = "Spawn of Death")],
            [Spawn(1, WolfEntry, 5, 0)], [], [], [],
            new CreatureAiContent(
            [
                Row(1, EventAiEventType.Aggro, 0, a1: Act(EventAiActionType.SetPhase, 3)),
                Row(2, EventAiEventType.Death, 0, phaseMask: 0x1, a1: Act(EventAiActionType.Summon, 301, (int)EventAiTarget.Self, 0)),
            ], []));
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        var ai = (CreatureEventAI)wolf.AI!;
        map.Combat.DealDamage(player, wolf, 1, direct: false);
        Assert.Equal(3, ai.Phase);

        map.Combat.Kill(player, wolf);

        Assert.Single(system.Creatures, c => c.Entry == 301);
        Assert.Equal(0, ai.Phase);
    }

    [Fact]
    public void ReachingHome_FiresTheHomeEvents_AndTheAggroEventsReArmOnTheNextFight()
    {
        using Fight f = Start(
        [
            Row(1, EventAiEventType.Aggro, 0, a1: Cast(980)),
            Row(2, EventAiEventType.ReachedHome, 0, a1: Cast(981)),
        ]);
        Pull(f);
        Assert.Single(f.Spells.Casts, c => c.Spell == 980);

        f.Wolf.AI!.EnterEvadeMode();
        Run(f.World, 3000);
        Assert.Single(f.Spells.Casts, c => c.Spell == 981);

        // A second fight triggers the (non-repeatable) aggro row again: each EnterCombat re-enables it (:1603-1607).
        f.Map.Combat.DealDamage(f.Player, f.Wolf, 1, direct: false);
        Assert.Equal(2, f.Spells.Casts.Count(c => c.Spell == 980));
    }

    // --- rows keyed by guid and debug rows -----------------------------------------------------------

    [Fact]
    public void GuidKeyedRows_RunOnlyOnThatSpawn()
    {
        CreatureContent content = new(
            [Template(configure: t => t.AIName = CreatureAiFactory.EventAIName)],
            [Spawn(1, WolfEntry, 5, 0), Spawn(2, WolfEntry, 6, 0)], [], [], [],
            new CreatureAiContent([Row(1, EventAiEventType.Aggro, 0, a1: Cast(990), guid: 2)], []));
        var spells = new FakeCaster();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = spells });
        using WorldRuntime w = world;
        (Player player, _) = AddPlayer(w, 1, 0, 0);
        Creature first = system.Creatures.Single(c => c.Spawn!.Guid == 1);
        Creature second = system.Creatures.Single(c => c.Spawn!.Guid == 2);

        map.Combat.DealDamage(player, first, 1, direct: false);
        Assert.Empty(spells.Casts);

        map.Combat.DealDamage(player, second, 1, direct: false);
        Assert.Single(spells.Casts, c => c.Spell == 990);
    }

    [Fact]
    public void DebugOnlyRows_AreSkippedUnlessEnabled()
    {
        CreatureAiEvent[] rows = [Row(1, EventAiEventType.Aggro, 0x80, a1: Cast(991))];
        using (Fight off = Start(rows))
        {
            Pull(off);
            Assert.Empty(off.Spells.Casts);
        }

        var options = new CreatureOptions();
        options.EventAi.DebugOnlyEvents = true;
        using Fight on = Start(rows, options: options);
        Pull(on);
        Assert.Single(on.Spells.Casts, c => c.Spell == 991);
    }

    // --- spawned and unsupported conditions ----------------------------------------------------------

    [Fact]
    public void SpawnedEvent_HonoursTheMapAndZoneConditions_AndOnlyAnUnknownConditionIsUnsupported()
    {
        // Condition 0 always, 1 the creature's map id, 2 its zone or area (cmangos SpawnedEventConditionsCheck, :1902-1927); any other
        // condition never fires. A death condition is a conditions-table id (CheckEvent :327-339), not an unsupported part.
        using Fight f = Start(
        [
            Row(1, EventAiEventType.Spawned, 0, p1: 0, a1: Cast(995)),
            Row(2, EventAiEventType.Spawned, 0, p1: 1, p2: 0, a1: Cast(996)),   // on map 0: fires
            Row(3, EventAiEventType.Spawned, 0, p1: 1, p2: 1, a1: Cast(997)),   // on map 1: does not
            Row(4, EventAiEventType.Spawned, 0, p1: 2, p2: 12, a1: Cast(998)),  // zone 12: the test terrain knows no zone, so it does not
            Row(5, EventAiEventType.Death, 0, p1: 77, a1: Cast(999)),            // death condition: not this event
            Row(6, EventAiEventType.Spawned, 0, p1: 3, a1: Cast(994)),          // no such condition
        ]);

        Assert.Equal([995u, 996u], f.Spells.Casts.Select(c => c.Spell).Order());
        Assert.Equal(["spawned condition 3 (row 6)"], f.Ai.Unsupported);
    }
}
