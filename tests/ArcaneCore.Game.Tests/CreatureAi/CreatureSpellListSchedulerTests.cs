using ArcaneCore.Game.Creatures.AI;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>The creature spell-list scheduler (cmangos UnitAI::UpdateSpellLists semantics) against a fake host.</summary>
public sealed class CreatureSpellListSchedulerTests
{
    private sealed class Marker;

    private sealed class FakeHost : ICreatureSpellListHost<Marker>
    {
        public bool IsInCombat { get; set; } = true;
        public bool CanCastSpell { get; set; } = true;
        public bool InRangedMode { get; set; }
        public HashSet<uint> Refused { get; } = [];
        public HashSet<uint> NoTarget { get; } = [];
        public List<uint> Casts { get; } = [];
        public List<uint> Scripts { get; } = [];
        public Marker Victim { get; } = new();

        public bool IsSpellReady(uint spellId) => true;

        public bool TryChooseTarget(uint targetId, uint spellId, out Marker? target)
        {
            target = targetId == CreatureSpellListTargets.Current ? Victim : null;
            return !NoTarget.Contains(spellId);
        }

        public bool TryCast(uint spellId, Marker? target)
        {
            if (Refused.Contains(spellId)) return false;
            Casts.Add(spellId);
            return true;
        }

        public void OnCast(CreatureSpellListEntry entry, Marker? target) => Scripts.Add(entry.ScriptId);
    }

    private static CreatureSpellListEntry Entry(uint pos, uint spell, uint probability = 0, uint initial = 0, uint repeat = 0,
        CreatureSpellListFlags flags = CreatureSpellListFlags.None, uint availability = 100, uint script = 0)
        => new(pos, spell, flags, CreatureSpellListTargets.Current, script, availability, probability, initial, initial, repeat, repeat);

    private static CreatureSpellList List(uint chanceSupport, uint chanceRanged, params CreatureSpellListEntry[] entries)
        => new(1, "test", chanceSupport, chanceRanged, entries);

    private static CreatureSpellListScheduler<Marker> Make(FakeHost host, CreatureSpellList list, int seed = 1)
    {
        CreatureSpellListScheduler<Marker> s = new(host, new Random(seed));
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
    public void Tick_AllProbabilitiesZero_CastsLowestPositionFirst()
    {
        FakeHost host = new();
        CreatureSpellListScheduler<Marker> s = Make(host, List(0, 0, Entry(2, 22), Entry(1, 11)));
        Assert.True(s.Update(0));
        Assert.Equal([11u], host.Casts);
    }

    [Fact]
    public void Tick_InitialCooldownDelaysFirstCast_RepeatCooldownDelaysNext()
    {
        FakeHost host = new();
        CreatureSpellListScheduler<Marker> s = Make(host, List(0, 0, Entry(0, 11, initial: 3000, repeat: 6000)));
        s.Update(1200);
        s.Update(1200);
        Assert.Empty(host.Casts);
        s.Update(1200); // 3600 ms: past the 3000 ms initial cooldown
        Assert.Single(host.Casts);
        for (int i = 0; i < 4; i++) s.Update(1200); // 4800 ms after the cast, still inside 6000 ms
        Assert.Single(host.Casts);
        s.Update(1200);
        s.Update(1200);
        Assert.Equal(2, host.Casts.Count);
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

    [Fact]
    public void Tick_OnlyOneBlockingSpellPerTick_ByProbabilityWeight()
    {
        FakeHost host = new();
        CreatureSpellListScheduler<Marker> s = Make(host, List(0, 0, Entry(1, 11, probability: 50), Entry(2, 22, probability: 50)));
        s.Update(0);
        Assert.Single(host.Casts);
    }

    [Fact]
    public void Tick_RangedGate_ChanceDropsByFiftyInMeleeMode()
    {
        // Ranged chance 50: always passes in ranged mode only when the roll is <= 50, never gated at 0 in melee mode beyond roll 0.
        // With chance 100 in ranged mode every roll passes; in melee mode the chance is 50 so some of 200 ticks must fail.
        FakeHost ranged = new() { InRangedMode = true };
        FakeHost melee = new() { InRangedMode = false };
        CreatureSpellList list = List(0, 100, Entry(1, 11, flags: CreatureSpellListFlags.RangedAction));
        CreatureSpellListScheduler<Marker> a = Make(ranged, list, 7);
        CreatureSpellListScheduler<Marker> b = Make(melee, list, 7);
        for (int i = 0; i < 200; i++)
        {
            a.Update(1200);
            b.Update(1200);
        }

        Assert.Equal(200, ranged.Casts.Count);
        Assert.InRange(melee.Casts.Count, 60, 140);
    }

    [Fact]
    public void Bind_AvailabilityZeroRollsOutAnEntryAndNullClears()
    {
        FakeHost host = new();
        CreatureSpellListScheduler<Marker> s = new(host, new Random(3));
        s.Bind(List(0, 0, Entry(1, 11), Entry(2, 22, availability: 0)));
        Assert.True(s.ActiveEntries.Count is 1 or 2);
        Assert.Contains(s.ActiveEntries, e => e.SpellId == 11);
        s.Bind(null);
        Assert.False(s.HasSpells);
        Assert.False(s.Update(5000));
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
