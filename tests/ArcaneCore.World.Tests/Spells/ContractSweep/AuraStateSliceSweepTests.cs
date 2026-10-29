using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Tests.ClientData;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Spells.ContractSweep;

/// <summary>
/// A scoped sweep of the aura-state classes (aura lifetime (b) and the stat block after expiry) over representative Spell.dbc rows:
/// Slice and Dice 6434 and Bloodlust 379 (attack time), Stoneform 7020 and Burning Spirit 14744 (scale), Food 10256 and Blessed
/// Sunfruit 18124 (a triggered Well Fed outlives the food), Polymorph Backfire 28406 (a triggered Polymorph outlives it), Minor Tremor
/// 10455 and Immune Root/Snare/Stun 5579 (the vmangos short-aura floor). None of them may report b.state-not-restored or
/// b.duration-mismatch. ARCANECORE_SPELL_SWEEP_IDS (comma-separated) sweeps other ids instead, and ARCANECORE_SPELL_SWEEP_TRACE=1
/// prints every holder removal of a swept spell with its cause (stack) and the unit's stat block, for diagnosis.
/// </summary>
[Collection(SpellContractSweepCollection.Name)]
public sealed class AuraStateSliceSweepTests(ITestOutputHelper output)
{
    private static readonly uint[] Representatives = [6434, 379, 7020, 14744, 10256, 18124, 28406, 10455, 5579];

    private static SpellStore SweepStore(IEnumerable<SpellInfo> spells) => new([.. spells, SweepHelperSpells.SelfDummySpell()], [], []);

    [RealClientDbcFact]
    public void Representatives_RestoreTheirStatBlock_AndMatchTheirDuration()
    {
        string? ids = Environment.GetEnvironmentVariable("ARCANECORE_SPELL_SWEEP_IDS");
        uint[] chosen = string.IsNullOrWhiteSpace(ids)
            ? Representatives
            : [.. ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(uint.Parse)];
        SpellSweepCatalog catalog = SpellSweepCatalog.Shared;
        SpellInfo[] spells = [.. chosen.Select(id => catalog.Store.Get(id) ?? throw new InvalidOperationException($"Spell.dbc has no {id}"))];
        using var rig = new SpellSweepRig(SweepStore(catalog.Spells), RealClientDbcFactAttribute.DbcDirectory);
        rig.TakeFaults();
        if (Environment.GetEnvironmentVariable("ARCANECORE_SPELL_SWEEP_TRACE") == "1")
        {
            Trace(rig, [.. chosen]);
        }

        SweepResult result = new SpellSweepRunner(rig, catalog) { Contracts = "bj" }.Run(spells, spells.Length, full: false);
        foreach (SweepViolation violation in result.Violations)
        {
            output.WriteLine("violation " + violation.Key);
        }

        Assert.Equal(spells.Length, result.Outcomes.Count);
        Assert.All(result.Outcomes, o => Assert.Equal("ran", o.Checks["b"]));
        Assert.DoesNotContain(result.Violations, v => v.Category is SweepCategory.NotRestored or SweepCategory.DurationMismatch
            || SweepCategory.IsGating(v.Category));
    }

    private void Trace(SpellSweepRig rig, HashSet<uint> wanted)
    {
        rig.System.HolderRemoved += h =>
        {
            if (!wanted.Contains(h.Spell.Id))
            {
                return;
            }

            string stack = string.Join(" <- ", new System.Diagnostics.StackTrace().GetFrames().Skip(1).Take(14)
                .Select(f => f.GetMethod()?.DeclaringType?.Name + "." + f.GetMethod()?.Name));
            Unit unit = h.Target;
            output.WriteLine($"removed {h.Spell.Id} mode={h.RemoveMode} dur={h.Duration}/{h.MaxDuration} target={unit.GetType().Name}"
                + $" scale={unit.GetFloat(UpdateFields.ObjectFieldScaleX):G9} attack-time={unit.GetUInt32(UpdateFields.UnitFieldBaseattacktime)}"
                + $" stats={string.Join('/', Enumerable.Range(0, 5).Select(i => unit.GetUInt32(UpdateFields.UnitFieldStat0 + i)))}"
                + $" max-power={string.Join('/', Enumerable.Range(0, 5).Select(i => unit.GetUInt32(UpdateFields.UnitFieldMaxpower1 + i)))}"
                + $" others={string.Join(',', rig.System.GetAuras(unit).Where(o => !o.IsRemoved).Select(o => o.Spell.Id + ":" + o.Duration))} :: {stack}");
        };
    }
}
