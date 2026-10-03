using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells.Casters;

/// <summary>
/// Spell cost modifiers: the school flat modifier (aura 73) is applied first, then the school percent
/// multiplier (aura 72, Clearcasting-style), floored at 0. vmangos Spell.cpp:6955-7040 (CalculatePowerCost),
/// SpellAuras.cpp:5391-5412 (HandleModPowerCostPCT, HandleModPowerCost).
/// </summary>
public sealed class PowerCostModifierTests
{
    private const uint ArcaneBolt = 2101;
    private const uint ClearcastLike = 2102;
    private const uint CostUpLike = 2103;
    private const uint FlatUpLike = 2104;
    private const uint ScaledCreatureSpell = 2105;
    private const int ArcaneMask = 0x40;
    private const int FireMask = 0x04;

    private static SpellInfo Aura(uint id, AuraType type, int amount, int mask) => SpellTestKit.Spell(
        id, SpellTestKit.Effect(SpellEffectName.ApplyAura, amount, aura: type, misc: mask)) with
    {
        Duration = new SpellDuration(30000, 0, 30000),
        SpellVisual = 1,
    };

    private static SpellTestKit NewKit()
    {
        var kit = new SpellTestKit(
            SpellTestKit.Spell(ArcaneBolt, SpellTestKit.Effect(SpellEffectName.Heal, 1)) with
            {
                PowerType = (int)PowerType.Mana,
                ManaCost = 100,
                School = SpellSchool.Arcane,
            },
            Aura(ClearcastLike, AuraType.ModPowerCostSchoolPct, -100, ArcaneMask),
            Aura(CostUpLike, AuraType.ModPowerCostSchoolPct, 10, ArcaneMask),
            Aura(FlatUpLike, AuraType.ModPowerCostSchool, 10, ArcaneMask | FireMask),
            SpellTestKit.Spell(ScaledCreatureSpell, SpellTestKit.Effect(SpellEffectName.Heal, 1)) with
            {
                PowerType = (int)PowerType.Mana,
                ManaCost = 200,
                SpellLevel = 40,
                Attributes = (SpellAttributes)CasterAttributes.ScalesWithCreatureLevel,
            });
        CasterSpellModules.Register(kit.System);
        return kit;
    }

    private uint Cost(SpellTestKit kit, Player player) => SpellSystem.CalculatePowerCost(player, kit.Store.Get(ArcaneBolt)!);

    [Fact]
    public void PercentCostAura_WritesTheSchoolMultiplierField_AndRestoresItOnRemoval()
    {
        using SpellTestKit kit = NewKit();
        (Player player, _) = kit.AddPlayer(1);
        Assert.Equal(100u, Cost(kit, player));

        kit.System.CastSpell(player, ClearcastLike, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(-1.0f, player.GetFloat(UpdateFields.UnitFieldPowerCostMultiplier + (int)SpellSchool.Arcane));
        Assert.Equal(0f, player.GetFloat(UpdateFields.UnitFieldPowerCostMultiplier + (int)SpellSchool.Fire));
        Assert.Equal(0u, Cost(kit, player));

        kit.System.RemoveAuras(player, ClearcastLike);

        Assert.Equal(0f, player.GetFloat(UpdateFields.UnitFieldPowerCostMultiplier + (int)SpellSchool.Arcane));
        Assert.Equal(100u, Cost(kit, player));
    }

    [Fact]
    public void PercentCostAuras_OfOneSchool_AddUp()
    {
        using SpellTestKit kit = NewKit();
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, CostUpLike, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(0.1f, player.GetFloat(UpdateFields.UnitFieldPowerCostMultiplier + (int)SpellSchool.Arcane), 0.0001f);
        Assert.Equal(110u, Cost(kit, player));
    }

    [Fact]
    public void FlatCostAura_AddsToTheModifierOfEverySchoolInItsMask_AndIsAppliedBeforeThePercent()
    {
        using SpellTestKit kit = NewKit();
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, FlatUpLike, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(10, player.GetInt32(UpdateFields.UnitFieldPowerCostModifier + (int)SpellSchool.Arcane));
        Assert.Equal(10, player.GetInt32(UpdateFields.UnitFieldPowerCostModifier + (int)SpellSchool.Fire));
        Assert.Equal(0, player.GetInt32(UpdateFields.UnitFieldPowerCostModifier + (int)SpellSchool.Frost));
        Assert.Equal(110u, Cost(kit, player));

        kit.System.CastSpell(player, CostUpLike, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(121u, Cost(kit, player));

        kit.System.RemoveAuras(player, FlatUpLike);
        Assert.Equal(0, player.GetInt32(UpdateFields.UnitFieldPowerCostModifier + (int)SpellSchool.Arcane));
        Assert.Equal(110u, Cost(kit, player));
    }

    [Fact]
    public void ScalesWithCreatureLevel_DividesByTheLevelRatio()
    {
        // vmangos: cost = int32(cost / (1.117f * spellLevel / casterLevel - 0.1327f)); level 40 spell, level 1 caster.
        using SpellTestKit kit = NewKit();
        (Player player, _) = kit.AddPlayer(1);
        float expected = 200 / ((1.117f * 40 / 1) - 0.1327f);

        Assert.Equal((uint)expected, SpellSystem.CalculatePowerCost(player, kit.Store.Get(ScaledCreatureSpell)!));
    }

    [Fact]
    public void HandlersAreRegistered_ForBothCostAuraTypes()
    {
        using SpellTestKit kit = NewKit();
        Assert.True(kit.System.HasAuraHandler(AuraType.ModPowerCostSchoolPct));
        Assert.True(kit.System.HasAuraHandler(AuraType.ModPowerCostSchool));
    }
}
