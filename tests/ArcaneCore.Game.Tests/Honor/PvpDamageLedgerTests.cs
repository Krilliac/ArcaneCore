using ArcaneCore.Game.Honor;
using Xunit;

namespace ArcaneCore.Game.Tests.Honor;

/// <summary>The damage-taken history (vmangos Unit::UnitDamaged, Unit.cpp:342-344): per attacker, cleared after 60 s idle.</summary>
public sealed class PvpDamageLedgerTests
{
    private const long T0 = 1_000_000;

    [Fact]
    public void Damage_adds_up_per_attacker()
    {
        var ledger = new PvpDamageLedger();
        ledger.Record(5, 100, T0);
        ledger.Record(5, 50, T0 + 10);
        ledger.Record(0, 7, T0 + 20);
        Assert.Equal(new Dictionary<ulong, uint> { [5] = 150, [0] = 7 }, ledger.Snapshot(T0 + 30));
    }

    [Fact]
    public void An_idle_minute_clears_the_history_but_exactly_a_minute_does_not()
    {
        var ledger = new PvpDamageLedger();
        ledger.Record(5, 100, T0);
        Assert.Single(ledger.Snapshot(T0 + 60_000));
        Assert.Empty(ledger.Snapshot(T0 + 60_001));
    }

    [Fact]
    public void A_hit_after_the_expiry_starts_a_fresh_history()
    {
        var ledger = new PvpDamageLedger();
        ledger.Record(5, 100, T0);
        ledger.Record(6, 40, T0 + 70_000);
        Assert.Equal(new Dictionary<ulong, uint> { [6] = 40 }, ledger.Snapshot(T0 + 70_001));
    }

    [Fact]
    public void A_hit_inside_the_minute_keeps_the_history_and_extends_it()
    {
        var ledger = new PvpDamageLedger();
        ledger.Record(5, 100, T0);
        ledger.Record(6, 40, T0 + 59_000);
        Assert.Equal(new Dictionary<ulong, uint> { [5] = 100, [6] = 40 }, ledger.Snapshot(T0 + 59_000));
        // The minute now runs from the last hit, not the first.
        Assert.Equal(2, ledger.Snapshot(T0 + 100_000).Count);
        Assert.Empty(ledger.Snapshot(T0 + 119_001));
    }

    [Fact]
    public void Clear_forgets_everything_and_the_snapshot_is_a_copy()
    {
        var ledger = new PvpDamageLedger();
        ledger.Record(5, 100, T0);
        IReadOnlyDictionary<ulong, uint> copy = ledger.Snapshot(T0);
        ledger.Clear();
        Assert.Single(copy);
        Assert.Empty(ledger.Snapshot(T0));
    }
}
