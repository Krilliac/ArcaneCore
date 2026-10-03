using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Druid;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData;
using Xunit;

namespace ArcaneCore.Game.Tests.Druid;

/// <summary>
/// Registration guards of the form engine: aura 36 and effect 77 each have exactly one owner (RegisterAura and RegisterEffect
/// replace silently, so a second owner would hide the first), and every stance bit the 1.12.1 player spell data uses has a
/// form row and a handler.
/// </summary>
public sealed class FormRegistrationGuardTests
{
    /// <summary>
    /// The forms whose bit (form - 1) appears in the Stances / StancesNot masks of the classic-db spells and that this engine
    /// handles (Stances counts: Cat 96, Bear 75, Dire Bear 73, Moonkin 54, Shadowform 157, Stealth 21, Tree 4, Travel 1, Aquatic 1,
    /// Battle 52, Defensive 34, Berserker 20; StancesNot also names Ghost Wolf 8 times). Bits 14 and 15 (the creature bear and
    /// cat forms) and 32 (Spirit of Redemption, 39 spells) are used by creature and priest talent spells and are deliberately
    /// not handled (docs/areas/druid-forms.md, limits).
    /// </summary>
    public static readonly byte[] StanceBitForms = [1, 2, 3, 4, 5, 8, 16, 17, 18, 19, 28, 30, 31];

    [Fact]
    public void AuraType_ModShapeshift_HasNoBuiltInModuleOwner_AndTheStanceServiceInstallsTheOnlyHandler()
    {
        using var kit = new SpellTestKit();
        Assert.False(kit.System.HasAuraHandler(AuraType.ModShapeshift));          // no handler module may claim aura 36

        new ShapeshiftService(kit.System, ShapeshiftFormCatalog.Retail, new ArcaneCore.Game.Combat.CombatOptions(), _ => []).Install();

        Assert.True(kit.System.HasAuraHandler(AuraType.ModShapeshift));
    }

    [Fact]
    public void EffectName_ScriptEffect_HasExactlyOneModule_AndTheDruidScriptsOnlyUseTheRegistry()
    {
        using var kit = new SpellTestKit();

        Assert.True(kit.System.HasEffectHandler(SpellEffectName.ScriptEffect));
        Assert.Single(kit.System.Modules, type => type == typeof(ScriptEffectModule));
        Assert.Contains(ShapeshiftFormEffectRules.SpellId, ScriptEffectRegistry.For(kit.System).SpellIds);
        Assert.Throws<InvalidOperationException>(() => kit.System.RegisterModules([typeof(ScriptEffectModule)]));   // a second application is refused
    }

    [Fact]
    public void EveryStanceBitUsedByPlayerSpells_HasARetailCatalogRow_AndAHandler()
    {
        foreach (byte form in StanceBitForms)
        {
            Assert.True(ShapeshiftFormCatalog.Retail.TryGet(form, out _), $"form {form} has no SpellShapeshiftForm row");
            Assert.True(ShapeshiftService.HandlesForm((ShapeshiftForm)form), $"form {form} has no handler");
        }
    }

    [Fact]
    public void TheDruidFormTables_CoverTheFormsTheEngineGivesADisplayOrBoostTo()
    {
        foreach (byte form in new byte[] { 1, 2, 3, 4, 5, 8, 31 })
        {
            Assert.NotNull(FormDisplayTable.Get(form, alliance: true));
            Assert.NotEqual(default, FormBoostTable.Get(form));
        }

        Assert.NotNull(FormDisplayTable.Get(DruidForms.GhostWolf, alliance: true));
        foreach (byte form in new byte[] { 17, 28, 30 })
        {
            Assert.Null(FormDisplayTable.Get(form, alliance: true));               // no model change for stances, Shadowform, Stealth
        }
    }
}
