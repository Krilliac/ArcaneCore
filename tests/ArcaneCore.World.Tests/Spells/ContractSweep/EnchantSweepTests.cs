using ArcaneCore.Game.Spells;
using ArcaneCore.World.Tests.ClientData;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Spells.ContractSweep;

/// <summary>
/// The enchant slice of the contract sweep: every Spell.dbc spell with an ENCHANT_ITEM, ENCHANT_ITEM_TEMPORARY or ENCHANT_HELD_ITEM
/// effect, cast in the composed sweep world. The sweep world must configure the SpellItemEnchantment catalog the way the daemon does,
/// otherwise the enchant handlers are never installed (EnchantingFeature falls back to the refusing guard) and the sweep reports
/// the effects as unhandled. Scoped to these spells, so it is the RED/GREEN proof for the slice.
/// </summary>
[Collection(SpellContractSweepCollection.Name)]
public sealed class EnchantSweepTests(ITestOutputHelper output)
{
    private static readonly SpellEffectName[] s_enchantEffects =
        [SpellEffectName.EnchantItem, SpellEffectName.EnchantItemTemporary, SpellEffectName.EnchantHeldItem];

    [RealClientDbcFact]
    public void EnchantSpells_HaveNoUnhandledEffectInTheSweepWorld()
    {
        SpellSweepCatalog catalog = SpellSweepCatalog.Shared;
        SpellInfo[] enchants = [.. catalog.Spells.Where(s => s.Effects.Any(e => !e.IsEmpty && s_enchantEffects.Contains(e.Effect)))];
        Assert.NotEmpty(enchants);

        using var rig = new SpellSweepRig(new SpellStore([.. catalog.Spells, SweepHelperSpells.SelfDummySpell()], [], []), RealClientDbcFactAttribute.DbcDirectory);
        rig.TakeFaults();
        SweepResult result = new SpellSweepRunner(rig, catalog) { Contracts = "a" }.Run(enchants, catalog.Spells.Count, full: false);

        string[] unhandled = [.. result.Violations.Where(v => v.Category == SweepCategory.UnhandledEffect).Select(v => $"{v.SpellId} {v.Detail}")];
        output.WriteLine($"{enchants.Length} enchant spells swept, {unhandled.Length} unhandled");
        Assert.Empty(unhandled);
        Assert.True(rig.System.HasEffectHandler(SpellEffectName.EnchantItem));
        Assert.True(rig.System.HasEffectHandler(SpellEffectName.EnchantItemTemporary));
        Assert.True(rig.System.HasEffectHandler(SpellEffectName.EnchantHeldItem));
    }
}
