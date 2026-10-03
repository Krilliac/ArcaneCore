using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Stats;
using Xunit;

namespace ArcaneCore.Game.Tests.Stats;

/// <summary>
/// Expected values are worked by hand from vmangos 4b3d241 (D:\refs\vmangos\src\game): StatSystem.cpp,
/// Objects/Player.cpp, Objects/Unit.cpp, Objects/SpellCaster.cpp and ObjectMgr.cpp. The line numbers are in the
/// production comments next to each formula.
/// </summary>
public sealed class StatFormulaTests
{
    private const float Tol = 0.001f;

    // ---- stamina / intellect / armor -------------------------------------------------

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(15f, 15f)]
    [InlineData(20f, 20f)]
    [InlineData(22f, 40f)]
    [InlineData(100f, 820f)]
    public void HealthBonusFromStamina_IsOnePerPointToTwentyThenTen(float stamina, float expected)
        => Assert.Equal(expected, StatFormulas.HealthBonusFromStamina(stamina), Tol);

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(10f, 10f)]
    [InlineData(20f, 20f)]
    [InlineData(22f, 50f)]
    [InlineData(50f, 470f)]
    public void ManaBonusFromIntellect_IsOnePerPointToTwentyThenFifteen(float intellect, float expected)
        => Assert.Equal(expected, StatFormulas.ManaBonusFromIntellect(intellect), Tol);

    [Fact]
    public void Armor_GetsTwoPerAgilityAndAnIntellectPercentAura()
    {
        Assert.Equal(160f, StatFormulas.ArmorFromAgility(80f), Tol);
        Assert.Equal(50f, StatFormulas.ArmorFromIntellectPercent(100f, 50f), Tol);
    }

    [Fact]
    public void MaxHealth_ComposesTheGroupAndStaminaBonus()
    {
        // (create 60 + base 0) * 1 + total 0 + stamina bonus 40 = 100
        Assert.Equal(100u, StatFormulas.MaxHealth(0, 60, 1, 0, 1, 22));
        // ((10 + 50) * 1.5) + 20 + (20 + 10 * 10) = 230
        Assert.Equal(230u, StatFormulas.MaxHealth(10, 50, 1.5f, 20, 1, 30));
        // total pct multiplies the stamina bonus too: 100 * 1.1
        Assert.Equal(110u, StatFormulas.MaxHealth(0, 60, 1, 0, 1.1f, 22));
    }

    [Fact]
    public void MaxHealth_NeverDropsBelowOne()
        => Assert.Equal(1u, StatFormulas.MaxHealth(0, 0, 1, 0, 1, 0));

    [Fact]
    public void MaxPower_AddsTheIntellectBonusOnlyToManaWithACreatePool()
    {
        Assert.Equal(150u, StatFormulas.MaxPower(PowerType.Mana, 100, 22, 0, 1, 0, 1));
        Assert.Equal(0u, StatFormulas.MaxPower(PowerType.Mana, 0, 22, 0, 1, 0, 1));
        Assert.Equal(1000u, StatFormulas.MaxPower(PowerType.Rage, 1000, 22, 0, 1, 0, 1));
        // ((100 + 20) * 2 + 10 + 50) * 1.5
        Assert.Equal(450u, StatFormulas.MaxPower(PowerType.Mana, 100, 22, 20, 2, 10, 1.5f));
    }

    // ---- attack power ----------------------------------------------------------------

    [Theory]
    [InlineData(Class.Warrior, 60u, 120f, 80f, 400f)]       // 3*60 + 2*120 - 20
    [InlineData(Class.Paladin, 60u, 120f, 80f, 400f)]
    [InlineData(Class.Rogue, 30u, 60f, 100f, 200f)]         // 2*30 + 60 + 100 - 20
    [InlineData(Class.Hunter, 40u, 50f, 70f, 180f)]         // 2*40 + 50 + 70 - 20
    [InlineData(Class.Shaman, 20u, 50f, 10f, 120f)]         // 2*20 + 2*50 - 20
    [InlineData(Class.Mage, 10u, 30f, 99f, 20f)]            // str - 10
    [InlineData(Class.Priest, 10u, 30f, 99f, 20f)]
    [InlineData(Class.Warlock, 10u, 30f, 99f, 20f)]
    [InlineData(Class.Druid, 60u, 50f, 99f, 80f)]           // caster form: 2*str - 20
    public void MeleeAttackPower_FollowsTheClassFormula(Class cls, uint level, float str, float agi, float expected)
        => Assert.Equal(expected, StatFormulas.AttackPowerFromStrengthAndAgility(false, cls, level, str, agi), Tol);

    [Theory]
    [InlineData(ShapeshiftForm.Cat, 0f, 260f)]             // 0 + 2*100 + 80 - 20
    [InlineData(ShapeshiftForm.Cat, 50f, 290f)]            // + 60 * 0.5 (Predatory Strikes)
    [InlineData(ShapeshiftForm.Bear, 0f, 180f)]            // 0 + 2*100 - 20
    [InlineData(ShapeshiftForm.DireBear, 50f, 210f)]       // 30 + 200 - 20
    public void DruidMeleeAttackPower_UsesTheFormAndPredatoryStrikes(ShapeshiftForm form, float predatoryPct, float expected)
        => Assert.Equal(expected, StatFormulas.AttackPowerFromStrengthAndAgility(false, Class.Druid, 60, 100f, 80f, form, predatoryPct), Tol);

    [Theory]
    [InlineData(Class.Hunter, 60u, 100f, 310f)]            // 2*60 + 2*100 - 10
    [InlineData(Class.Rogue, 30u, 100f, 120f)]             // 30 + 100 - 10
    [InlineData(Class.Warrior, 60u, 50f, 100f)]            // 60 + 50 - 10
    [InlineData(Class.Mage, 30u, 30f, 20f)]                // agi - 10
    [InlineData(Class.Druid, 30u, 40f, 30f)]
    public void RangedAttackPower_FollowsTheClassFormula(Class cls, uint level, float agi, float expected)
        => Assert.Equal(expected, StatFormulas.AttackPowerFromStrengthAndAgility(true, cls, level, 99f, agi), Tol);

    [Fact]
    public void DruidRangedAttackPower_IsZeroInCatAndBearForm()
    {
        Assert.Equal(0f, StatFormulas.AttackPowerFromStrengthAndAgility(true, Class.Druid, 60, 99, 80, ShapeshiftForm.Cat), Tol);
        Assert.Equal(0f, StatFormulas.AttackPowerFromStrengthAndAgility(true, Class.Druid, 60, 99, 80, ShapeshiftForm.Bear), Tol);
        Assert.Equal(0f, StatFormulas.AttackPowerFromStrengthAndAgility(true, Class.Druid, 60, 99, 80, ShapeshiftForm.DireBear), Tol);
    }

    [Fact]
    public void TotalAttackPower_AddsBothModsScalesByTheMultiplierAndNeverGoesNegative()
    {
        Assert.Equal(484f, StatFormulas.TotalAttackPower(400, 50, -10, 0.1f), Tol);
        Assert.Equal(0f, StatFormulas.TotalAttackPower(10, 0, -50, 0.5f), Tol);
    }

    [Theory]
    [InlineData(2600u, false, true, null, 0u, 2.6f)]                                    // not normalized: delay / 1000
    [InlineData(2600u, true, false, null, 0u, 2.6f)]                                    // normalized only for players
    [InlineData(2600u, true, true, null, 0u, 2.4f)]                                     // fist
    [InlineData(2600u, true, true, InventoryType.TwoHandWeapon, 8u, 3.3f)]
    [InlineData(2600u, true, true, InventoryType.Ranged, 2u, 2.8f)]
    [InlineData(2600u, true, true, InventoryType.RangedRight, 3u, 2.8f)]
    [InlineData(2600u, true, true, InventoryType.Thrown, 16u, 2.8f)]
    [InlineData(2600u, true, true, InventoryType.Weapon, 7u, 2.4f)]
    [InlineData(2600u, true, true, InventoryType.Weapon, 15u, 1.7f)]                    // dagger
    [InlineData(2600u, true, true, InventoryType.WeaponMainHand, 15u, 1.7f)]
    [InlineData(2600u, true, true, InventoryType.WeaponOffHand, 0u, 2.4f)]
    public void ApMultiplier_FollowsTheWeaponKind(uint delay, bool normalized, bool isPlayer, InventoryType? weapon, uint subClass, float expected)
        => Assert.Equal(expected, StatFormulas.ApMultiplier(delay, normalized, isPlayer, weapon, subClass), Tol);

    // ---- crit / dodge / parry / block ------------------------------------------------

    [Theory]
    [InlineData(Class.Warrior, 0f)]
    [InlineData(Class.Paladin, 0.7f)]
    [InlineData(Class.Hunter, 0f)]
    [InlineData(Class.Rogue, 0f)]
    [InlineData(Class.Priest, 3.0f)]
    [InlineData(Class.Shaman, 1.7f)]
    [InlineData(Class.Mage, 3.2f)]
    [InlineData(Class.Warlock, 2.0f)]
    [InlineData(Class.Druid, 0.9f)]
    public void ClassBaseCritAndDodge_AreTheNostalriusClassBases(Class cls, float expected)
        => Assert.Equal(expected, StatFormulas.ClassBaseCritDodge(cls), Tol);

    [Fact]
    public void CritPercentage_AddsClassBaseAgilityCritAndTheSkillTerm()
    {
        Assert.Equal(5.2f, StatFormulas.CritPercentage(Class.Mage, 0, 2, 300, 300), Tol);
        // weapon skill 290 of 300: -10 * 0.04
        Assert.Equal(4.8f, StatFormulas.CritPercentage(Class.Mage, 0, 2, 290, 300), Tol);
        // aura flat crit adds
        Assert.Equal(7.2f, StatFormulas.CritPercentage(Class.Mage, 2, 2, 300, 300), Tol);
        // skill above the level maximum raises it
        Assert.Equal(1.4f, StatFormulas.CritPercentage(Class.Warrior, 0, 1, 310, 300), Tol);
    }

    [Fact]
    public void CritPercentage_ClampsAtZero()
        => Assert.Equal(0f, StatFormulas.CritPercentage(Class.Warrior, 0, 0, 100, 300), Tol);

    [Fact]
    public void BlockPercentage_IsFivePlusDefenseTermPlusAuraAndZeroWithoutAShield()
    {
        Assert.Equal(0f, StatFormulas.BlockPercentage(false, 300, 300, 10), Tol);
        Assert.Equal(5f, StatFormulas.BlockPercentage(true, 300, 300, 0), Tol);
        Assert.Equal(3f, StatFormulas.BlockPercentage(true, 250, 300, 0), Tol);
        Assert.Equal(13f, StatFormulas.BlockPercentage(true, 300, 300, 8), Tol);
        Assert.Equal(0f, StatFormulas.BlockPercentage(true, 1, 300, 0), Tol);
    }

    [Fact]
    public void ParryPercentage_IsFivePlusDefenseTermPlusWeaponAuraAndZeroWithoutParry()
    {
        Assert.Equal(0f, StatFormulas.ParryPercentage(false, 300, 300, 10), Tol);
        Assert.Equal(5f, StatFormulas.ParryPercentage(true, 300, 300, 0), Tol);
        Assert.Equal(3f, StatFormulas.ParryPercentage(true, 250, 300, 0), Tol);
        Assert.Equal(7f, StatFormulas.ParryPercentage(true, 300, 300, 2), Tol);
        Assert.Equal(0f, StatFormulas.ParryPercentage(true, 1, 300, 0), Tol);
    }

    [Fact]
    public void DodgePercentage_IsClassBasePlusAgilityPlusDefenseTermPlusAura()
    {
        Assert.Equal(8.7f, StatFormulas.DodgePercentage(Class.Mage, 4.5f, 300, 300, 1), Tol);
        Assert.Equal(4.1f, StatFormulas.DodgePercentage(Class.Rogue, 4.5f, 290, 300, 0), Tol);
        Assert.Equal(0f, StatFormulas.DodgePercentage(Class.Warrior, 0, 1, 300, 0), Tol);
    }

    [Fact]
    public void ShieldBlockValue_IsFlatPlusStrengthOverTwentyMinusOneScaledAndFloored()
    {
        Assert.Equal(34u, StatFormulas.ShieldBlockValue(30, 100, 1));
        Assert.Equal(0u, StatFormulas.ShieldBlockValue(0, 10, 1));
        Assert.Equal(37u, StatFormulas.ShieldBlockValue(30, 100, 1.1f));   // 34 * 1.1 = 37.4
    }

    // ---- weapon damage composition ---------------------------------------------------

    private static DamageInputs Warrior60(WeaponAttackType type = WeaponAttackType.BaseAttack, int index = 0) => new(
        type, index, AttSpeed: 2.0f, TotalAttackPower: 400f, BaseValue: 0, BasePct: 1, TotalValue: 0, TotalPct: 1,
        TotalPhysical: 0, WeaponMin: 2, WeaponMax: 4, WeaponDamageMode.Weapon, Level: 60, AmmoDps: 0);

    [Fact]
    public void MinMaxDamage_AddsApOverFourteenTimesSpeedToTheWeaponRange()
    {
        DamageRange r = StatFormulas.CalculateMinMaxDamage(Warrior60());
        Assert.Equal(59.142857f, r.Min, Tol);   // 2 + 400 / 14 * 2
        Assert.Equal(61.142857f, r.Max, Tol);
    }

    [Fact]
    public void MinMaxDamage_AppliesTheGroupsInVmangosOrder()
    {
        DamageInputs i = Warrior60() with { BaseValue = 3, BasePct = 1.1f, TotalValue = 5, TotalPct = 1.2f, TotalPhysical = 2 };
        DamageRange r = StatFormulas.CalculateMinMaxDamage(i);
        float ap = 400f / 14f * 2f;
        Assert.Equal((((3 + ap) + 2) * 1.1f + 5 + 2) * 1.2f, r.Min, Tol);
        Assert.Equal((((3 + ap) + 4) * 1.1f + 5 + 2) * 1.2f, r.Max, Tol);
    }

    [Fact]
    public void MinMaxDamage_OffHandUsesTheHalfDamageTotalPct()
    {
        DamageInputs i = Warrior60(WeaponAttackType.OffAttack) with { TotalPct = UnitModConstants.OffHandDamageTotalPct };
        DamageRange r = StatFormulas.CalculateMinMaxDamage(i);
        Assert.Equal((57.142857f + 2) * 0.5f, r.Min, Tol);
        Assert.Equal((57.142857f + 4) * 0.5f, r.Max, Tol);
    }

    [Fact]
    public void MinMaxDamage_AnUnusableWeaponFallsBackToTheFistRangeAndDropsTotalValue()
    {
        DamageInputs i = Warrior60() with { Mode = WeaponDamageMode.CannotUseWeapon, TotalValue = 9 };
        DamageRange r = StatFormulas.CalculateMinMaxDamage(i);
        Assert.Equal(58.142857f, r.Min, Tol);   // 1 + 400 / 14 * 2
        Assert.Equal(59.142857f, r.Max, Tol);   // 2 + 400 / 14 * 2
    }

    [Fact]
    public void MinMaxDamage_ShapeshiftFormUsesTheLevelRangeCappedAtSixty()
    {
        DamageInputs i = Warrior60() with { Mode = WeaponDamageMode.ShapeshiftForm, Level = 70, TotalValue = 9 };
        DamageRange r = StatFormulas.CalculateMinMaxDamage(i);
        Assert.Equal(57.142857f + (60 * 0.85f * 2f), r.Min, Tol);
        Assert.Equal(57.142857f + (60 * 1.25f * 2f), r.Max, Tol);
    }

    [Fact]
    public void MinMaxDamage_ShapeshiftFormIgnoresExtraWeaponDamageEntries()
    {
        DamageInputs i = Warrior60(index: 1) with { Mode = WeaponDamageMode.ShapeshiftForm, WeaponMin = 5, WeaponMax = 6, TotalValue = 9, TotalPhysical = 3 };
        DamageRange r = StatFormulas.CalculateMinMaxDamage(i);
        Assert.Equal(0f, r.Min, Tol);
        Assert.Equal(0f, r.Max, Tol);
    }

    [Fact]
    public void MinMaxDamage_ExtraEntriesDropApAndFlatTotals()
    {
        DamageInputs i = Warrior60(index: 1) with { WeaponMin = 3, WeaponMax = 5, BaseValue = 7, TotalValue = 9, TotalPhysical = 4, BasePct = 2, TotalPct = 1.5f };
        DamageRange r = StatFormulas.CalculateMinMaxDamage(i);
        Assert.Equal(3 * 2 * 1.5f, r.Min, Tol);
        Assert.Equal(5 * 2 * 1.5f, r.Max, Tol);
    }

    [Fact]
    public void MinMaxDamage_RangedAddsAmmoDpsTimesSpeedToTheFirstEntryOnly()
    {
        DamageInputs i = Warrior60(WeaponAttackType.RangedAttack) with { AttSpeed = 2.8f, TotalAttackPower = 140f, AmmoDps = 20f, WeaponMin = 10, WeaponMax = 20 };
        DamageRange r = StatFormulas.CalculateMinMaxDamage(i);
        Assert.Equal(10 + (140f / 14f * 2.8f) + (20f * 2.8f), r.Min, Tol);
        Assert.Equal(20 + (140f / 14f * 2.8f) + (20f * 2.8f), r.Max, Tol);

        DamageRange extra = StatFormulas.CalculateMinMaxDamage(i with { Index = 1 });
        Assert.Equal(10f, extra.Min, Tol);
        Assert.Equal(20f, extra.Max, Tol);
    }

    // ---- skill gain ------------------------------------------------------------------

    [Theory]
    [InlineData(50u, 0f, 90f)]       // 90% of max 100 = 90 > 50: min(100, 90 * 50 / 50)
    [InlineData(50u, 100f, 92f)]     // + min(10, 0.02 * 100)
    [InlineData(50u, 1000f, 100f)]   // intellect bonus capped at 10, total capped at 100
    [InlineData(20u, 0f, 100f)]      // 225 -> 100
    [InlineData(0u, 0f, 100f)]
    [InlineData(95u, 0f, 24.654f)]   // (0.5 - 0.0168966*95*3 + 0.0152069*100*3) * 100
    [InlineData(98u, 0f, 2.3618f)]   // 9.447 * 0.5 / (4 - 2)
    [InlineData(99u, 0f, 0.7297f)]   // 4.378 * 0.5 / (4 - 1)
    [InlineData(100u, 0f, 0f)]
    [InlineData(120u, 50f, 0f)]
    public void WeaponSkillGainChance_FollowsTheTwoSegmentCurve(uint value, float intellect, float expected)
        => Assert.Equal(expected, StatFormulas.CombatSkillGainChance(false, 20, value, 25, intellect), 0.01f);

    [Fact]
    public void WeaponSkillGainChance_UsesFiveTimesLevelAsTheMaximumNotTheSkillsOwn()
    {
        // level 60 -> max 300; value 280 is above 270 (= 90%): (0.5 - 0.0168966*280 + 0.0152069*300) * 100
        Assert.Equal(33.102f, StatFormulas.CombatSkillGainChance(false, 60, 280, 63, 0), 0.01f);
    }

    [Theory]
    [InlineData(50u, 22u, 67.5f)]    // gray(20) = 13, 3 * (22 - 13) * 50 / 20
    [InlineData(50u, 40u, 90f)]      // mob level capped at player + 5 = 25: 3 * 12 * 50 / 20
    [InlineData(50u, 10u, 22.5f)]    // level difference floored at 3
    [InlineData(10u, 25u, 100f)]     // 162 -> 100
    [InlineData(100u, 25u, 0f)]      // at the maximum: no gain
    public void DefenseSkillGainChance_FollowsTheOldFormula(uint value, uint mobLevel, float expected)
        => Assert.Equal(expected, StatFormulas.CombatSkillGainChance(true, 20, value, mobLevel, 500f), 0.01f);
}
