using ArcaneCore.Game.Spells;
using ArcaneCore.World.Tests.ClientData;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Spells.ContractSweep;

/// <summary>
/// A scoped sweep of the aura-state slice (aura lifetime (b), stat restore after expiry, dispel (i) and unhandled auras (a)):
/// ARCANECORE_SPELL_SWEEP_IDS (comma-separated spell ids) narrows it for diagnosis; the default ids are the slice's representatives.
/// </summary>
[Collection(SpellContractSweepCollection.Name)]
public sealed class AuraStateSliceSweepTests(ITestOutputHelper output)
{
    private static SpellStore SweepStore(IEnumerable<SpellInfo> spells) => new([.. spells, SweepHelperSpells.SelfDummySpell()], [], []);

    [RealClientDbcFact]
    public void Slice_Diagnose()
    {
        string? ids = Environment.GetEnvironmentVariable("ARCANECORE_SPELL_SWEEP_IDS");
        if (string.IsNullOrWhiteSpace(ids))
        {
            return;
        }

        SpellSweepCatalog catalog = SpellSweepCatalog.Shared;
        SpellInfo[] spells = [.. ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(uint.Parse).Select(id => catalog.Store.Get(id)).OfType<SpellInfo>()];
        using var rig = new SpellSweepRig(SweepStore(catalog.Spells), RealClientDbcFactAttribute.DbcDirectory);
        rig.TakeFaults();
        SweepResult result = new SpellSweepRunner(rig, catalog) { Contracts = Environment.GetEnvironmentVariable("ARCANECORE_SPELL_SWEEP_CONTRACTS") ?? "abij" }
            .Run(spells, spells.Length, full: false);
        foreach (SpellInfo spell in spells)
        {
            string effects = string.Join(" | ", spell.Effects.Where(e => e.Effect != SpellEffectName.None)
                .Select(e => $"{e.Effect}/{e.AuraType} bp={e.BaseDice}+{e.BasePoints}:{e.DieSides} misc={e.MiscValue} tA={e.TargetA} amp={e.Amplitude} trig={e.TriggerSpell}"));
            output.WriteLine($"SPELL {spell.Id} '{spell.Name}' dur={spell.Duration} dispel={spell.Dispel} attr=0x{spell.Attributes:X} {effects}");
        }

        foreach (SweepSpellOutcome outcome in result.Outcomes)
        {
            output.WriteLine($"OUT {outcome.SpellId} cast={outcome.CastResult} applied={outcome.AppliedAura} checks={string.Join(",", outcome.Checks.Select(kv => kv.Key + "=" + kv.Value))}");
        }

        foreach (SweepViolation violation in result.Violations)
        {
            output.WriteLine("VIOL " + violation.Key);
        }
    }
}
