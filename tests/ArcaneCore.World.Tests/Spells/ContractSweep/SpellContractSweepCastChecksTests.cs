using ArcaneCore.Game.Spells;
using ArcaneCore.World.Tests.ClientData;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Spells.ContractSweep;

/// <summary>
/// A scoped sweep of the cast-check classes (e cooldowns, f costs, g range): runs only the spells the baseline lists for those
/// categories, with only those contracts, and fails while any of them still violates. A fast RED/GREEN proof for the lane that
/// owns the classes; the full ratchet in <see cref="SpellContractSweepTests"/> stays the gate.
/// </summary>
[Collection(SpellContractSweepCollection.Name)]
public sealed class SpellContractSweepCastChecksTests(ITestOutputHelper output)
{
    private static readonly string[] s_prefixes = ["e.", "f.", "g."];

    private static SweepResult Run(string contracts, string prefix)
    {
        SpellSweepCatalog catalog = SpellSweepCatalog.Shared;
        HashSet<uint> ids = [.. File.ReadAllLines(SpellSweepBaseline.SourcePath)
            .Where(l => l.StartsWith(prefix, StringComparison.Ordinal))
            .Select(l => uint.Parse(l.Split('\t')[1], System.Globalization.CultureInfo.InvariantCulture))];
        SpellInfo[] selection = [.. catalog.Spells.Where(s => ids.Contains(s.Id))];
        using var rig = new SpellSweepRig(new SpellStore([.. catalog.Spells, SweepHelperSpells.SelfDummySpell()], [], []), RealClientDbcFactAttribute.DbcDirectory);
        rig.TakeFaults();
        return new SpellSweepRunner(rig, catalog) { Contracts = contracts }.Run(selection, catalog.Spells.Count, full: false);
    }

    private void AssertNone(string contracts, string prefix)
    {
        SweepResult result = Run(contracts, prefix);
        string[] found = [.. result.Violations.Where(v => v.Category.StartsWith(prefix, StringComparison.Ordinal)).Select(v => v.Key).Order(StringComparer.Ordinal)];
        output.WriteLine($"{found.Length} violations in {result.Outcomes.Count} spells");
        Assert.True(found.Length == 0, $"{found.Length} {prefix}* violations:\n" + string.Join('\n', found.Take(30)));
    }

    [RealClientDbcFact]
    public void CooldownClasses_NoViolations() => AssertNone("e", "e.");

    [RealClientDbcFact]
    public void CostClasses_NoViolations() => AssertNone("f", "f.");

    [RealClientDbcFact]
    public void RangeClass_NoViolations() => AssertNone("g", "g.");
}
