using ArcaneCore.Game.Spells;
using ArcaneCore.World.Tests.ClientData;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Spells.ContractSweep;

/// <summary>
/// The interrupt slice of the sweep (contracts c and d) over spells whose interrupt lines were artefacts of the check, not server faults:
/// food stood up by the struck creature's answering hit (melee-swing), a stun broken by the melee the cast itself started (Charge), absorb
/// shields used up by the hit (melee-hit-taken), DAMAGE auras whose spell has procFlags (vmangos spares those from the damage break), and
/// channels still on their cast bar when the caster moved (vmangos cancels a cast bar on movement only with SPELL_INTERRUPT_FLAG_MOVEMENT).
/// A focused, fast proof that needs no full sweep.
/// </summary>
[Collection(SpellContractSweepCollection.Name)]
public sealed class SpellSweepInterruptSliceTests(ITestOutputHelper output)
{
    /// <summary>The spells of the slice.</summary>
    private static readonly uint[] Slice =
    [
        5388, 7331, 7967,
        10250, 10257, 1131, 1137, 11657, 13711, 18124, 18140, 18234, 22731, 22734, 23698, 24005, 24355, 24360, 24707, 24800, 25660,
        25696, 25697, 25990, 26030, 26260, 26261, 26263, 26401, 26402, 26470, 26473, 26475, 27759, 28616, 29007, 29055, 29073, 3288,
        6118, 7423, 7447, 7848,
        16053, 6115,
    ];

    [RealClientDbcFact]
    public void Slice_InterruptChecksMeasureOnlyTheAction()
    {
        SpellSweepCatalog catalog = SpellSweepCatalog.Shared;
        SpellInfo[] spells = [.. Slice.Select(id => catalog.Store.Get(id) ?? throw new InvalidOperationException($"Spell.dbc has no {id}"))];
        using var rig = new SpellSweepRig(new SpellStore([.. catalog.Spells, SweepHelperSpells.SelfDummySpell()], [], []), RealClientDbcFactAttribute.DbcDirectory);
        rig.TakeFaults();
        SweepResult result = new SpellSweepRunner(rig, catalog) { Contracts = "cd" }.Run(spells, spells.Length, full: false);
        string[] violations =
        [
            .. result.Violations
                .Where(v => v.Category is SweepCategory.InterruptNotRemoved or SweepCategory.InterruptRemovedWithoutFlag or SweepCategory.CastNotInterrupted)
                .Select(v => v.Key)
                .Order(StringComparer.Ordinal),
        ];
        output.WriteLine(string.Join('\n', violations));
        Assert.True(violations.Length == 0, $"{violations.Length} interrupt violations:\n" + string.Join('\n', violations));

        // A check that did not run is not a pass: every spell of the slice ran its interrupt check (c) or its cast check (d).
        string[] notRun =
        [
            .. result.Outcomes
                .Where(o => !o.Checks.GetValueOrDefault("c", "").Contains("=ran", StringComparison.Ordinal) && o.Checks.GetValueOrDefault("d") != "ran")
                .Select(o => $"{o.SpellId} c={o.Checks.GetValueOrDefault("c")} d={o.Checks.GetValueOrDefault("d")}"),
        ];
        Assert.True(notRun.Length == 0, "checks that did not run:\n" + string.Join('\n', notRun));
        Assert.Equal(Slice.Length, result.Outcomes.Count);
    }
}
