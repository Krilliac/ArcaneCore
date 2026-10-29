using ArcaneCore.Game.Spells;
using ArcaneCore.World.Tests.ClientData;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Spells.ContractSweep;

/// <summary>
/// A scoped sweep of the cast-check classes (e cooldowns, f costs, g range): runs a fixed set of spells that the baseline once listed
/// for those classes, with only that contract, and fails while any of them violates or while the check stops running for them. A fast
/// RED/GREEN proof for the lane that owns the classes; the full ratchet in <see cref="SpellContractSweepTests"/> stays the gate.
/// </summary>
[Collection(SpellContractSweepCollection.Name)]
public sealed class SpellContractSweepCastChecksTests(ITestOutputHelper output)
{
    // Cost: per-level and scaled (0x80000) mana costs, rage and energy next-swing abilities, a percentage cost, a self power drain.
    private static readonly uint[] s_costSpells = [14, 1609, 1770, 2497, 6154, 17289, 17682, 18267, 19463, 20701, 20754, 22187, 28447];

    // Cooldown: next-swing abilities, channels with a short category cooldown, an aura that triggers a spell of its own category.
    private static readonly uint[] s_cooldownSpells = [1635, 1794, 1770, 2497, 2677, 3150, 5165, 6595, 7290, 9373, 11427, 13013, 25698];

    // Range: next-swing abilities (the range check does not apply to them) and two ordinary ranged spells that must still be checked.
    private static readonly uint[] s_rangeSpells = [10851, 11427, 12097, 12166, 12538, 13446, 14112, 133, 116];

    private static SweepResult Run(string contracts, uint[] ids)
    {
        SpellSweepCatalog catalog = SpellSweepCatalog.Shared;
        SpellInfo[] selection = [.. catalog.Spells.Where(s => ids.Contains(s.Id))];
        Assert.Equal(ids.Length, selection.Length);
        using var rig = new SpellSweepRig(new SpellStore([.. catalog.Spells, SweepHelperSpells.SelfDummySpell()], [], []), RealClientDbcFactAttribute.DbcDirectory);
        rig.TakeFaults();
        return new SpellSweepRunner(rig, catalog) { Contracts = contracts }.Run(selection, catalog.Spells.Count, full: false);
    }

    /// <summary>Fails on a violation of the class, and when too few of the spells ran the check (a check that did not run is not a pass).</summary>
    private void AssertNone(string contracts, string prefix, uint[] ids, int minimumRan)
    {
        SweepResult result = Run(contracts, ids);
        string[] found = [.. result.Violations.Where(v => v.Category.StartsWith(prefix, StringComparison.Ordinal)).Select(v => v.Key).Order(StringComparer.Ordinal)];
        int ran = result.Outcomes.Count(o => o.Coverage().Split('\t').ElementAtOrDefault(2)?.Split(' ').Contains(contracts) == true);
        output.WriteLine($"{found.Length} violations in {result.Outcomes.Count} spells; the check ran for {ran}");
        Assert.True(found.Length == 0, $"{found.Length} {prefix}* violations:\n" + string.Join('\n', found.Take(30)));
        Assert.True(ran >= minimumRan, $"the {prefix}* check ran for only {ran} of {ids.Length} spells (expected at least {minimumRan}):\n"
            + string.Join('\n', result.Outcomes.Select(o => o.Coverage())));
    }

    [RealClientDbcFact]
    public void CooldownClasses_NoViolations() => AssertNone("e", "e.", s_cooldownSpells, 10);

    [RealClientDbcFact]
    public void CostClasses_NoViolations() => AssertNone("f", "f.", s_costSpells, 10);

    [RealClientDbcFact]
    public void RangeClass_NoViolations() => AssertNone("g", "g.", s_rangeSpells, 2);
}
