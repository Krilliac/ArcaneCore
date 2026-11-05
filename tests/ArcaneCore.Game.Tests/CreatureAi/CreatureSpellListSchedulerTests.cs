using ArcaneCore.Game.Creatures.AI;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>The creature spell-list scheduler (cmangos UnitAI::UpdateSpellLists semantics) against a fake host and a scripted Random.</summary>
public sealed class CreatureSpellListSchedulerTests
{
    private sealed class Marker;

    /// <summary>A Random whose draws are scripted per call kind, recording the bounds each draw asked for.</summary>
    private sealed class ScriptedRandom : Random
    {
        /// <summary>Answers of Next(min, max): the 0..100 rolls (availability, support gate, ranged gate), in call order. Empty: the minimum.</summary>
        public Queue<int> Rolls { get; } = new();

        /// <summary>Answers of Next(max): the shuffle picks. Empty: 0.</summary>
        public Queue<int> Picks { get; } = new();

        /// <summary>Answers of NextInt64(min, max): the weight roll and the cooldown ranges. Empty: the minimum.</summary>
        public Queue<long> Longs { get; } = new();

        public List<(int Min, int Max)> RollRanges { get; } = [];

        public List<(long Min, long Max)> LongRanges { get; } = [];

        public override int Next(int minValue, int maxValue)
        {
            RollRanges.Add((minValue, maxValue));
            return Rolls.Count > 0 ? Rolls.Dequeue() : minValue;
        }

        public override int Next(int maxValue) => Picks.Count > 0 ? Picks.Dequeue() : 0;

        public override long NextInt64(long minValue, long maxValue)
        {
            LongRanges.Add((minValue, maxValue));
            return Longs.Count > 0 ? Longs.Dequeue() : minValue;
        }
    }

    private sealed class FakeHost : ICreatureSpellListHost<Marker>
    {
        public bool IsInCombat { get; set; } = true;
        public bool CanCastSpell { get; set; } = true;
        public bool InRangedMode { get; set; }
        public HashSet<uint> Refused { get; } = [];
        public HashSet<uint> NoTarget { get; } = [];
        public HashSet<uint> NotReady { get; } = [];
        public HashSet<uint> NoAutocast { get; } = [];
        public HashSet<int> ConditionFalse { get; } = [];
        public List<uint> Casts { get; } = [];
        public List<Marker?> CastTargets { get; } = [];
        public List<uint> Scripts { get; } = [];
        public List<Marker?> ScriptTargets { get; } = [];
        public List<int> ConditionsAsked { get; } = [];
        public List<uint> TargetIdsAsked { get; } = [];
        public Marker Victim { get; } = new();

        /// <summary>Runs inside TryCast, as the spell system's cooldown step does for an instant spell.</summary>
        public Action<uint>? DuringCast { get; set; }

        public bool IsSpellReady(uint spellId) => !NotReady.Contains(spellId);

        public bool CanAutocast(uint spellId) => !NoAutocast.Contains(spellId);

        public bool IsCombatConditionSatisfied(int combatCondition, uint spellId)
        {
            ConditionsAsked.Add(combatCondition);
            return !ConditionFalse.Contains(combatCondition);
        }

        public bool TryChooseTarget(uint targetId, uint spellId, out Marker? target)
        {
            TargetIdsAsked.Add(targetId);
            target = targetId == CreatureSpellListTargets.Current ? Victim : null;
            return !NoTarget.Contains(spellId);
        }

        public bool TryCast(uint spellId, Marker? target)
        {
            if (Refused.Contains(spellId)) return false;
            DuringCast?.Invoke(spellId);
            Casts.Add(spellId);
            CastTargets.Add(target);
            return true;
        }

        public void OnCast(CreatureSpellListEntry entry, Marker? target)
        {
            Scripts.Add(entry.ScriptId);
            ScriptTargets.Add(target);
        }
    }

    private static CreatureSpellListEntry Entry(uint pos, uint spell, uint probability = 0, uint initial = 0, uint repeat = 0,
        CreatureSpellListFlags flags = CreatureSpellListFlags.None, uint availability = 100, uint script = 0,
        uint target = CreatureSpellListTargets.Current, bool disabledForAi = false, int condition = 0)
        => new(pos, spell, flags, target, script, availability, probability, initial, initial, repeat, repeat)
        {
            DisabledForAI = disabledForAi,
            CombatCondition = condition,
        };

    private static CreatureSpellList List(uint chanceSupport, uint chanceRanged, params CreatureSpellListEntry[] entries)
        => new(1, "test", chanceSupport, chanceRanged, entries);

    private static CreatureSpellListScheduler<Marker> Make(FakeHost host, CreatureSpellList list, ScriptedRandom? random = null)
    {
        CreatureSpellListScheduler<Marker> s = new(host, random ?? new ScriptedRandom());
        s.Bind(list);
        s.EnterCombat();
        return s;
    }

    [Fact]
    public void Catalog_RejectsDuplicatePositionsAndInvertedRanges()
    {
        Assert.Throws<ArgumentException>(() => List(0, 0, Entry(0, 10), Entry(0, 11)));
        Assert.Throws<ArgumentException>(() => List(0, 0, new CreatureSpellListEntry(0, 10, 0, 1, 0, 100, 0, 5, 1, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => List(101, 0, Entry(0, 10)));
        CreatureSpellListCatalog catalog = new([List(0, 0, Entry(0, 10))]);
        Assert.NotNull(catalog.Find(1));
        Assert.Null(catalog.Find(0));
        Assert.Throws<ArgumentException>(() => new CreatureSpellListCatalog([List(0, 0, Entry(0, 10)), List(0, 0, Entry(0, 11))]));
    }

    [Fact]
    public void Catalog_TargetingRows_AreIndexed_AndEntriesMustNameExistingRows()
    {
        // Shaped like the reference table: rows 0..7 of type Hardcoded, plus a row from 100 up.
        List<CreatureSpellListTargeting> rows = [];
        for (uint id = 0; id < CreatureSpellListTargets.HardcodedCount; id++)
            rows.Add(new CreatureSpellListTargeting(id, CreatureSpellListTargetingType.Hardcoded, 0, 0, 0, 0));
        CreatureSpellListTargeting row = new(150, CreatureSpellListTargetingType.Support, 1, 2, 3, -4);
        rows.Add(row);

        CreatureSpellList withTarget = List(0, 0, Entry(0, 10, target: 150));
        CreatureSpellList withHardcoded = new(2, "test", 0, 0, [Entry(0, 11, target: CreatureSpellListTargets.CurrentNotAlone)]);
        CreatureSpellListCatalog catalog = new([withTarget, withHardcoded], rows);
        Assert.Same(row, catalog.FindTargeting(150));
        Assert.Equal(CreatureSpellListTargetingType.Hardcoded, catalog.FindTargeting(CreatureSpellListTargets.CurrentNotAlone)?.Type);
        Assert.Null(catalog.FindTargeting(151));

        // Ids 8..99 and 100 up need a row when an entry names them.
        Assert.Throws<ArgumentException>(() => new CreatureSpellListCatalog([withTarget], rows.Where(r => r.Id != 150)));
        Assert.Throws<ArgumentException>(() => new CreatureSpellListCatalog([withTarget]));
        Assert.Throws<ArgumentException>(() => new CreatureSpellListCatalog([List(0, 0, Entry(0, 10, target: 50))], rows));
        Assert.Throws<ArgumentException>(() => new CreatureSpellListCatalog([], [row, row]));
        Assert.Throws<ArgumentException>(() => new CreatureSpellListCatalog([], [rows[0], rows[0]]));

        // Hardcoded ids resolve without a row; a row for one is accepted too.
        Assert.Equal(1, new CreatureSpellListCatalog([withHardcoded]).Count);
    }

    [Fact]
    public void Tick_AllProbabilitiesZero_CastsLowestPositionFirst()
    {
        FakeHost host = new();
        CreatureSpellListScheduler<Marker> s = Make(host, List(0, 0, Entry(2, 22), Entry(1, 11)));
        Assert.True(s.Update(0));
        Assert.Equal([11u], host.Casts);
    }

    [Fact]
    public void Tick_InitialAndRepeatCooldownsUseTheirRandomRanges()
    {
        FakeHost host = new();
        ScriptedRandom random = new();
        random.Longs.Enqueue(4000); // initial cooldown out of 1000..5000
        random.Longs.Enqueue(2500); // repeat cooldown out of 2000..3000
        CreatureSpellListScheduler<Marker> s = new(host, random);
        s.Bind(List(0, 0, new CreatureSpellListEntry(0, 11, CreatureSpellListFlags.None, 1, 0, 100, 0, 1000, 5000, 2000, 3000)));
        s.EnterCombat();
        Assert.Equal((1000L, 5001L), Assert.Single(random.LongRanges));

        s.Update(1200);
        s.Update(1200);
        s.Update(1200); // 3600 ms: inside the 4000 ms initial cooldown (a minimum-only roll would have cast at 1200)
        Assert.Empty(host.Casts);
        s.Update(1200); // 4800 ms
        Assert.Equal([11u], host.Casts);
        Assert.Equal((2000L, 3001L), random.LongRanges[1]);

        s.Update(1200);
        s.Update(1200); // 7200 ms, repeat ready at 7300
        Assert.Single(host.Casts);
        s.Update(1200); // 8400 ms
        Assert.Equal([11u, 11u], host.Casts);
    }

    [Fact]
    public void Bind_DoesNotArmInitialCooldowns_ButEnterCombatDoes()
    {
        FakeHost host = new();
        CreatureSpellListScheduler<Marker> s = new(host, new ScriptedRandom());
        s.Bind(List(0, 0, Entry(1, 11, initial: 60000)));
        s.Update(1200);
        Assert.Equal([11u], host.Casts); // a bind alone leaves the entry ready

        s.Bind(List(0, 0, Entry(1, 11, initial: 60000)));
        s.EnterCombat();
        s.Update(1200);
        Assert.Single(host.Casts);
    }

    [Fact]
    public void Tick_RefusedCast_FallsBackToNextEntryAndKeepsFirstReady()
    {
        FakeHost host = new();
        host.Refused.Add(11);
        CreatureSpellListScheduler<Marker> s = Make(host, List(0, 0, Entry(1, 11, repeat: 10000), Entry(2, 22)));
        s.Update(0);
        Assert.Equal([22u], host.Casts);
        host.Refused.Clear();
        s.Update(1200);
        Assert.Equal([22u, 11u], host.Casts); // the refused entry never started a repeat cooldown
    }

    [Fact]
    public void Tick_NoTargetOrOutOfCombatOrCannotCast_CastsNothing()
    {
        FakeHost host = new();
        host.NoTarget.Add(11);
        CreatureSpellListScheduler<Marker> s = Make(host, List(0, 0, Entry(1, 11)));
        s.Update(0);
        host.NoTarget.Clear();
        host.IsInCombat = false;
        s.Update(1200);
        host.IsInCombat = true;
        host.CanCastSpell = false;
        s.Update(1200);
        Assert.Empty(host.Casts);
        host.CanCastSpell = true;
        s.Update(1200);
        Assert.Single(host.Casts);
    }

    [Fact]
    public void Tick_SpellNotReady_IsSkipped_AndCastsOnceReady()
    {
        FakeHost host = new();
        host.NotReady.Add(11);
        CreatureSpellListScheduler<Marker> s = Make(host, List(0, 0, Entry(1, 11), Entry(2, 22)));
        s.Update(0);
        Assert.Equal([22u], host.Casts);
        host.NotReady.Clear();
        s.Update(1200);
        Assert.Equal([22u, 11u], host.Casts);

        FakeHost only = new();
        only.NotReady.Add(11);
        CreatureSpellListScheduler<Marker> lone = Make(only, List(0, 0, Entry(1, 11)));
        lone.Update(0);
        Assert.Empty(only.Casts);
    }

    [Fact]
    public void Tick_PassesTheChosenTargetToTheCastAndTheScript()
    {
        FakeHost host = new();
        CreatureSpellListScheduler<Marker> s = Make(host, List(0, 0,
            Entry(1, 11, target: CreatureSpellListTargets.Current, script: 5),
            Entry(2, 22, target: CreatureSpellListTargets.SelfTarget, flags: CreatureSpellListFlags.NonBlocking, script: 6)));
        s.Update(0);
        Assert.Equal([22u, 11u], host.Casts); // the non-blocking entry runs first
        Assert.Equal([CreatureSpellListTargets.Current, CreatureSpellListTargets.SelfTarget], host.TargetIdsAsked);
        Assert.Null(host.CastTargets[0]);
        Assert.Same(host.Victim, host.CastTargets[1]);
        Assert.Null(host.ScriptTargets[0]);
        Assert.Same(host.Victim, host.ScriptTargets[1]);
        Assert.Equal([6u, 5u], host.Scripts);
    }

    [Fact]
    public void Tick_NonBlockingSpellCastsAlongsideTheBlockingOne_AndRunsItsScript()
    {
        FakeHost host = new();
        CreatureSpellListScheduler<Marker> s = Make(host, List(0, 0,
            Entry(1, 11),
            Entry(2, 22, flags: CreatureSpellListFlags.NonBlocking, script: 77)));
        s.Update(0);
        Assert.Equal([22u, 11u], host.Casts);
        Assert.Equal([77u, 0u], host.Scripts);
    }

    [Theory]
    [InlineData(1, 29L, 11u)] // order A,B: a roll below A's weight picks A
    [InlineData(1, 30L, 22u)] // a roll at A's weight falls through to B
    [InlineData(0, 69L, 22u)] // shuffled to B,A: a roll below B's weight picks B
    [InlineData(0, 70L, 11u)] // shuffled to B,A: a roll at B's weight falls through to A
    public void Tick_OneBlockingSpellPerTick_ChosenByShuffleAndWeightRoll(int pick, long roll, uint expectedSpell)
    {
        FakeHost host = new();
        ScriptedRandom random = new();
        random.Picks.Enqueue(pick); // Fisher-Yates over two items: 0 swaps them, 1 keeps them
        random.Longs.Enqueue(roll);
        CreatureSpellListScheduler<Marker> s = Make(host, List(0, 0, Entry(1, 11, probability: 30), Entry(2, 22, probability: 70)), random);
        s.Update(0);
        Assert.Equal([expectedSpell], host.Casts);
        Assert.Equal((0L, 100L), random.LongRanges[0]);
    }

    [Fact]
    public void Tick_SupportGate_UsesTheSupportRollAgainstTheListChance()
    {
        FakeHost host = new();
        ScriptedRandom random = new();
        CreatureSpellListScheduler<Marker> s = Make(host, List(50, 0,
            Entry(1, 11, flags: CreatureSpellListFlags.SupportAction),
            Entry(2, 22, flags: CreatureSpellListFlags.NonBlocking)), random);
        random.Rolls.Enqueue(51); // support roll above the chance: the support entry is gated out
        random.Rolls.Enqueue(0);  // ranged roll
        s.Update(0);
        Assert.Equal([22u], host.Casts); // the entry without the support flag is not gated
        host.Casts.Clear();
        random.Rolls.Enqueue(50); // equal to the chance passes
        random.Rolls.Enqueue(0);
        s.Update(1200);
        Assert.Equal([22u, 11u], host.Casts);
        Assert.All(random.RollRanges, r => Assert.Equal((0, 101), r));
    }

    [Theory]
    [InlineData(true, 100, 100, true)]
    [InlineData(true, 100, 101, false)] // not reachable by a real roll; pins the comparison direction
    [InlineData(false, 100, 50, true)]  // melee mode: chance 100 - 50
    [InlineData(false, 100, 51, false)]
    [InlineData(false, 30, 0, true)]    // chance - 50 floors at 0, a roll of 0 still passes
    [InlineData(false, 30, 1, false)]
    [InlineData(true, 30, 30, true)]
    [InlineData(true, 30, 31, false)]
    public void Tick_RangedGate_ChanceDropsByFiftyInMeleeMode(bool rangedMode, uint chance, int roll, bool casts)
    {
        FakeHost host = new() { InRangedMode = rangedMode };
        ScriptedRandom random = new();
        CreatureSpellListScheduler<Marker> s = Make(host, List(0, chance, Entry(1, 11, flags: CreatureSpellListFlags.RangedAction)), random);
        random.Rolls.Enqueue(0);    // support roll
        random.Rolls.Enqueue(roll); // ranged roll
        s.Update(0);
        Assert.Equal(casts, host.Casts.Count == 1);
        Assert.All(host.Casts, c => Assert.Equal(11u, c));
    }

    [Fact]
    public void Tick_DisabledForAiEntry_IsSkipped()
    {
        FakeHost host = new();
        CreatureSpellListScheduler<Marker> s = Make(host, List(0, 0,
            Entry(1, 11, disabledForAi: true),
            Entry(2, 22, disabledForAi: true, flags: CreatureSpellListFlags.NonBlocking),
            Entry(3, 33)));
        s.Update(0);
        Assert.Equal([33u], host.Casts);

        FakeHost only = new();
        CreatureSpellListScheduler<Marker> lone = Make(only, List(0, 0, Entry(1, 11, disabledForAi: true)));
        lone.Update(0);
        Assert.Empty(only.Casts);
    }

    [Fact]
    public void Tick_CombatCondition_GatesTheEntry_AndZeroOrMinusOneAreNoCondition()
    {
        FakeHost host = new();
        host.ConditionFalse.Add(7);
        CreatureSpellListScheduler<Marker> s = Make(host, List(0, 0,
            Entry(1, 11, condition: 7),
            Entry(2, 22, condition: 0, flags: CreatureSpellListFlags.NonBlocking),
            Entry(3, 33, condition: -1, flags: CreatureSpellListFlags.NonBlocking)));
        s.Update(0);
        Assert.Equal([22u, 33u], host.Casts);
        Assert.Equal([7], host.ConditionsAsked); // 0 and -1 are never asked of the host

        host.ConditionFalse.Clear();
        s.Update(1200);
        Assert.Equal([22u, 33u, 22u, 33u, 11u], host.Casts);
    }

    [Fact]
    public void Bind_AvailabilityRoll_KeepsAtOrBelowTheAvailability_AndDropsAbove()
    {
        FakeHost host = new();
        ScriptedRandom random = new();
        CreatureSpellListScheduler<Marker> s = new(host, random);
        CreatureSpellList list = List(0, 0, Entry(1, 11), Entry(2, 22, availability: 40), Entry(3, 33, availability: 0));

        random.Rolls.Enqueue(41); // above 40: dropped
        random.Rolls.Enqueue(0);  // 0 does not exceed 0: kept
        s.Bind(list);
        Assert.Equal([11u, 33u], s.ActiveEntries.Select(e => e.SpellId));
        Assert.Equal([(0, 101), (0, 101)], random.RollRanges); // the availability-100 entry draws nothing

        random.Rolls.Enqueue(40); // equal to 40: kept
        random.Rolls.Enqueue(1);  // above 0: dropped
        s.Bind(list);
        Assert.Equal([11u, 22u], s.ActiveEntries.Select(e => e.SpellId));

        s.Update(1200);
        Assert.Equal([11u], host.Casts); // only kept entries are scheduled

        s.Bind(null);
        Assert.False(s.HasSpells);
        Assert.Null(s.List);
        Assert.False(s.Update(5000));
    }

    [Fact]
    public void Bind_DisabledList_SchedulesNothing_ButStaysBound()
    {
        FakeHost host = new();
        CreatureSpellListScheduler<Marker> s = new(host, new ScriptedRandom());
        CreatureSpellList disabled = new(1, "off", 0, 0, [Entry(1, 11)]) { Disabled = true };
        s.Bind(disabled);
        s.EnterCombat();
        Assert.Same(disabled, s.List);
        Assert.False(s.HasSpells);
        Assert.False(s.Update(1200));
        Assert.Empty(host.Casts);
    }

    [Fact]
    public void Tick_SpellTheAiMayNotAutocast_IsSkipped()
    {
        FakeHost host = new();
        host.NoAutocast.Add(11);
        CreatureSpellListScheduler<Marker> s = Make(host, List(0, 0, Entry(1, 11), Entry(2, 22)));
        s.Update(0);
        Assert.Equal([22u], host.Casts);
    }

    [Fact]
    public void Tick_ContinueAfterCast_WalksTheRemainingBlockingEntries()
    {
        FakeHost host = new();
        CreatureSpellListScheduler<Marker> s = new(host, new ScriptedRandom(), continueAfterCast: true);
        s.Bind(List(0, 0, Entry(1, 11), Entry(2, 22)));
        s.EnterCombat();
        s.Update(0);
        Assert.Equal([11u, 22u], host.Casts); // the roll stays below every later zero weight, as in the cmangos loop
    }

    [Fact]
    public void TryGetSpellCooldown_InsideTheCast_RollsOnce_ForTheSpellSystemAndTheEntryTimer()
    {
        FakeHost host = new();
        ScriptedRandom random = new();
        CreatureSpellListScheduler<Marker> s = new(host, random);
        s.Bind(List(0, 0, new CreatureSpellListEntry(0, 11, CreatureSpellListFlags.None, 1, 0, 100, 0, 0, 0, 2000, 3000)));
        s.EnterCombat();
        uint seen = 0;
        host.DuringCast = spell =>
        {
            Assert.True(s.TryGetSpellCooldown(spell, out seen, out bool category));
            Assert.False(category);
        };
        random.Longs.Enqueue(2500);
        s.Update(0);
        Assert.Equal(2500u, seen);
        Assert.Single(random.LongRanges); // one draw served both

        s.Update(1200);
        s.Update(1200); // 2400 ms: inside the 2500 ms repeat
        Assert.Single(host.Casts);
        s.Update(1200);
        Assert.Equal(2, host.Casts.Count);
    }

    [Fact]
    public void TryGetSpellCooldown_AfterACastTime_TakesTheRollOfTheCast_ThenRollsFresh()
    {
        FakeHost host = new();
        ScriptedRandom random = new();
        CreatureSpellListScheduler<Marker> s = new(host, random);
        s.Bind(List(0, 0, new CreatureSpellListEntry(0, 11, CreatureSpellListFlags.CategoryCooldown, 1, 0, 100, 0, 0, 0, 2000, 3000)));
        s.EnterCombat();
        random.Longs.Enqueue(2200);
        s.Update(0); // the spell has a cast time: nothing asked for the cooldown inside TryCast

        Assert.True(s.TryGetSpellCooldown(11, out uint first, out bool category));
        Assert.Equal(2200u, first);
        Assert.True(category);

        random.Longs.Enqueue(2900); // another cast of the spell, by a script say
        Assert.True(s.TryGetSpellCooldown(11, out uint second, out _));
        Assert.Equal(2900u, second);

        Assert.False(s.TryGetSpellCooldown(99, out uint none, out _));
        Assert.Equal(0u, none);
    }

    [Fact]
    public void Tick_AScriptBindsAnotherListInsideTheCast_TheOldTickStopsCleanly()
    {
        FakeHost host = new();
        CreatureSpellListScheduler<Marker> s = Make(host, List(0, 0,
            Entry(1, 11, flags: CreatureSpellListFlags.NonBlocking),
            Entry(2, 22, flags: CreatureSpellListFlags.NonBlocking),
            Entry(3, 33)));
        host.DuringCast = _ => s.Bind(List(0, 0, Entry(1, 44)));
        s.Update(0);
        Assert.Equal([11u], host.Casts); // the entries chosen from the replaced list do not fire

        host.DuringCast = null;
        s.Update(1200);
        Assert.Equal([11u, 44u], host.Casts);
    }

    [Fact]
    public void Reset_ForgetsCooldowns()
    {
        FakeHost host = new();
        CreatureSpellListScheduler<Marker> s = Make(host, List(0, 0, Entry(1, 11, initial: 60000)));
        s.Update(1200);
        Assert.Empty(host.Casts);
        s.Reset();
        s.Update(1200);
        Assert.Single(host.Casts);
    }
}
