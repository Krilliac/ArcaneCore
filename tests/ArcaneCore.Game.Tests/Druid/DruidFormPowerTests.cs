using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Druid;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Game.Stats;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Druid;

public sealed class DruidFormPowerTests
{
    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int minValue, int maxValue) => value;
    }

    [Fact]
    public void CatForm_ResetsEnergyAndFurorAddsTheRealProcAmount_ThenRemovalRestoresMana()
    {
        using var kit = new SpellTestKit(Dummy(), Cat(), Energize(FurorRules.CatEnergySpell, 40, PowerType.Energy));
        Player player = AddDruid(kit);
        kit.System.Random = new FixedRandom(100);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998202, SpellCastTargets.ForSelf(), true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998200, SpellCastTargets.ForSelf(), true));
        Assert.Equal(PowerType.Energy, player.PowerType);
        Assert.Equal(40u, MapCombat.GetPower(player, PowerType.Energy));
        kit.System.RemoveAuras(player, 998200);
        Assert.Equal(PowerType.Mana, player.PowerType);
        Assert.Equal(0u, MapCombat.GetPower(player, PowerType.Rage));
    }

    [Fact]
    public void BearForm_PreservesRageBeforeFurorProc_AndRemovalZerosRage()
    {
        using var kit = new SpellTestKit(Dummy(), Bear(), Energize(FurorRules.BearRageSpell, 100, PowerType.Rage));
        Player player = AddDruid(kit);
        MapCombat.SetPower(player, PowerType.Rage, 420);
        kit.System.Random = new FixedRandom(100);
        kit.System.CastSpell(player, 998202, SpellCastTargets.ForSelf(), true);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998201, SpellCastTargets.ForSelf(), true));
        Assert.Equal(PowerType.Rage, player.PowerType);
        Assert.Equal(520u, MapCombat.GetPower(player, PowerType.Rage));
        kit.System.RemoveAuras(player, 998201);
        Assert.Equal(PowerType.Mana, player.PowerType);
        Assert.Equal(0u, MapCombat.GetPower(player, PowerType.Rage));
    }

    [Fact]
    public void FurorThresholdIsInclusive_AndMissingProcRowsDoNotFabricatePower()
    {
        using var kit = new SpellTestKit(Dummy(), Cat());
        Player player = AddDruid(kit);
        kit.System.Random = new FixedRandom(100);
        kit.System.CastSpell(player, 998202, SpellCastTargets.ForSelf(), true);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998200, SpellCastTargets.ForSelf(), true));
        Assert.Equal(0u, MapCombat.GetPower(player, PowerType.Energy));
    }

    [Fact]
    public void FurorWrongIconAndZeroChanceDoNotProc()
    {
        using var kit = new SpellTestKit(Dummy(icon: 239, amount: 0), Cat(), Energize(FurorRules.CatEnergySpell, 40, PowerType.Energy));
        Player player = AddDruid(kit);
        kit.System.Random = new FixedRandom(1);
        kit.System.CastSpell(player, 998202, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, 998200, SpellCastTargets.ForSelf(), true);
        Assert.Equal(0u, MapCombat.GetPower(player, PowerType.Energy));
    }

    [Fact]
    public void CatToBearReplacementUsesBearRageAndDireBearUsesRage()
    {
        using var kit = new SpellTestKit(Cat(), Bear(), DireBear());
        Player player = AddDruid(kit);
        kit.System.CastSpell(player, 998200, SpellCastTargets.ForSelf(), true);
        Assert.Equal(PowerType.Energy, player.PowerType);
        kit.System.CastSpell(player, 998201, SpellCastTargets.ForSelf(), true);
        Assert.Equal(PowerType.Rage, player.PowerType);
        kit.System.RemoveAuras(player, 998201);
        kit.System.CastSpell(player, 998203, SpellCastTargets.ForSelf(), true);
        Assert.Equal(PowerType.Rage, player.PowerType);
    }

    [Fact]
    public void NonDruidCatFormStillUsesTheSourcePowerTransition()
    {
        using var kit = new SpellTestKit(Cat());
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warrior);
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        new ShapeshiftService(kit.System, new ShapeshiftFormCatalog([new((uint)DruidForms.Cat, 0, 0)]),
            new CombatOptions(), _ => []).Install();
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998200, SpellCastTargets.ForSelf(), true));
        Assert.Equal(PowerType.Energy, player.PowerType);
    }

    [Fact]
    public void DruidForms_ApplyAndRemoveCatalogLinkedBoosts_AndSkipMissingRows()
    {
        using var kit = new SpellTestKit(Cat(), Travel(), Link(3025), Link(5419));
        Player player = AddDruid(kit);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998200, SpellCastTargets.ForSelf(), true));
        Assert.True(kit.System.HasAura(player, 3025));
        kit.System.RemoveAuras(player, 998200);
        Assert.False(kit.System.HasAura(player, 3025));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998204, SpellCastTargets.ForSelf(), true));
        Assert.True(kit.System.HasAura(player, 5419));
        Assert.False(kit.System.HasAura(player, 21178));

        kit.System.RemoveAuras(player, 998204);
        Assert.False(kit.System.HasAura(player, 5419));
    }

    [Fact]
    public void CatForm_UsesHeartOfTheWildTalentAmount_AndRemovesTheExactStatDelta()
    {
        SpellInfo talent = Spell(990001, Effect(SpellEffectName.ApplyAura, 25, aura: AuraType.ModTotalStatPercentage,
            misc: FormBoostTable.HeartOfTheWildMiscValue)) with { SpellIconId = FormBoostTable.HeartOfTheWildIconId };
        SpellInfo heart = Spell(24900, Effect(SpellEffectName.ApplyAura, 2, aura: AuraType.ModTotalStatPercentage,
            misc: FormBoostTable.HeartOfTheWildMiscValue));
        using var kit = new SpellTestKit(Cat(), Link(3025), talent, heart);
        Player player = AddDruid(kit);
        player.SetInt32(UpdateFields.UnitFieldStat0 + FormBoostTable.HeartOfTheWildMiscValue, 10);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, talent.Id, SpellCastTargets.ForSelf(), true));
        Assert.Equal(12, player.GetInt32(UpdateFields.UnitFieldStat0 + FormBoostTable.HeartOfTheWildMiscValue));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998200, SpellCastTargets.ForSelf(), true));

        SpellAuraHolder applied = Assert.Single(kit.System.GetAuras(player), h => h.Spell.Id == 24900);
        Assert.Equal(25, applied.Auras[0]!.Amount); // differs from the DB default of 2
        Assert.Equal(15, player.GetInt32(UpdateFields.UnitFieldStat0 + FormBoostTable.HeartOfTheWildMiscValue));

        kit.System.RemoveAuras(player, 998200);
        Assert.Equal(12, player.GetInt32(UpdateFields.UnitFieldStat0 + FormBoostTable.HeartOfTheWildMiscValue));
        kit.System.RemoveAuras(player, talent.Id);
        Assert.Equal(10, player.GetInt32(UpdateFields.UnitFieldStat0 + FormBoostTable.HeartOfTheWildMiscValue));
    }

    [Fact]
    public void CatForm_HeartOfTheWildUpdatesLatentManaPoolAfterPowerSwitch_AndRemovalIsIdempotent()
    {
        SpellInfo talent = Spell(990041, Effect(SpellEffectName.ApplyAura, 25, aura: AuraType.ModTotalStatPercentage,
            misc: FormBoostTable.HeartOfTheWildMiscValue)) with { SpellIconId = FormBoostTable.HeartOfTheWildIconId };
        SpellInfo heart = Spell(24900, Effect(SpellEffectName.ApplyAura, 2, aura: AuraType.ModTotalStatPercentage,
            misc: FormBoostTable.HeartOfTheWildMiscValue));
        using var kit = new SpellTestKit(Cat(), Link(3025), talent, heart);
        Player player = AddDruid(kit);
        player.SetInt32(UpdateFields.UnitFieldStat0 + FormBoostTable.HeartOfTheWildMiscValue, 10);
        player.SetUInt32(UpdateFields.UnitFieldBaseMana, 1);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
        player.SetUInt32(UpdateFields.UnitFieldPower1, 100);
        var maintainer = new PlayerStatSystem();
        maintainer.Attach(player);
        maintainer.UpdateAll(player);
        Assert.Equal(110u, player.GetUInt32(UpdateFields.UnitFieldMaxpower1)); // initial intellect-10 bonus

        kit.System.CastSpell(player, talent.Id, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, 998200, SpellCastTargets.ForSelf(), true);
        Assert.Equal(PowerType.Energy, player.PowerType);
        Assert.Equal(115u, player.GetUInt32(UpdateFields.UnitFieldMaxpower1));
        maintainer.UpdateAll(player);
        Assert.Equal(115u, player.GetUInt32(UpdateFields.UnitFieldMaxpower1));

        kit.System.RemoveAuras(player, 998200);
        Assert.Equal(112u, player.GetUInt32(UpdateFields.UnitFieldMaxpower1));
        kit.System.RemoveAuras(player, talent.Id);
        Assert.Equal(110u, player.GetUInt32(UpdateFields.UnitFieldMaxpower1));
    }

    [Fact]
    public void TotalStatPercentage_StacksMultiplicatively_AndSupportsRemovalInEitherOrder()
    {
        SpellInfo ten = Spell(990010, Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.ModTotalStatPercentage, misc: 0));
        SpellInfo twenty = Spell(990011, Effect(SpellEffectName.ApplyAura, 20, aura: AuraType.ModTotalStatPercentage, misc: 0));
        using var kit = new SpellTestKit(ten, twenty);
        (Player player, _) = kit.AddPlayer(1);
        player.SetInt32(UpdateFields.UnitFieldStat0, 100);

        kit.System.CastSpell(player, ten.Id, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, twenty.Id, SpellCastTargets.ForSelf(), true);
        Assert.Equal(132, player.GetInt32(UpdateFields.UnitFieldStat0));
        kit.System.RemoveAuras(player, ten.Id);
        Assert.Equal(120, player.GetInt32(UpdateFields.UnitFieldStat0));
        kit.System.RemoveAuras(player, twenty.Id);
        Assert.Equal(100, player.GetInt32(UpdateFields.UnitFieldStat0));
    }

    [Fact]
    public void TotalStatPercentage_AllStatsAndExternalFlatChangesRecoverExactly()
    {
        SpellInfo all = Spell(990020, Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.ModTotalStatPercentage, misc: -1));
        SpellInfo pct = Spell(990021, Effect(SpellEffectName.ApplyAura, 20, aura: AuraType.ModTotalStatPercentage, misc: 0));
        SpellInfo flat = Spell(990022, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModStat, misc: 0));
        using var kit = new SpellTestKit(all, pct, flat);
        (Player player, _) = kit.AddPlayer(1);
        for (int stat = 0; stat < 5; stat++) player.SetInt32(UpdateFields.UnitFieldStat0 + stat, 100);

        kit.System.CastSpell(player, all.Id, SpellCastTargets.ForSelf(), true);
        Assert.All(Enumerable.Range(0, 5), stat => Assert.Equal(110, player.GetInt32(UpdateFields.UnitFieldStat0 + stat)));
        kit.System.RemoveAuras(player, all.Id);
        Assert.All(Enumerable.Range(0, 5), stat => Assert.Equal(100, player.GetInt32(UpdateFields.UnitFieldStat0 + stat)));

        kit.System.CastSpell(player, pct.Id, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, flat.Id, SpellCastTargets.ForSelf(), true);
        Assert.Equal(126, player.GetInt32(UpdateFields.UnitFieldStat0));
        kit.System.RemoveAuras(player, pct.Id);
        Assert.Equal(105, player.GetInt32(UpdateFields.UnitFieldStat0));
        kit.System.RemoveAuras(player, flat.Id);
        Assert.Equal(100, player.GetInt32(UpdateFields.UnitFieldStat0));
    }

    [Fact]
    public void TotalStatPercentage_EquipmentBeforeAndDuringModifier_AndZeroPercentKeepFlatLedger()
    {
        SpellInfo pct = Spell(990030, Effect(SpellEffectName.ApplyAura, 20, aura: AuraType.ModTotalStatPercentage, misc: 0));
        SpellInfo zero = Spell(990031, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModTotalStatPercentage, misc: 0));
        using var kit = new SpellTestKit(pct, zero);
        (Player player, _) = kit.AddPlayer(1);
        player.SetInt32(UpdateFields.UnitFieldStat0, 100);
        Item item = new(12345, new ItemTemplate { Entry = 12345, Stats = [new ItemStat((uint)ItemStatType.Strength, 10)] }, player.Guid);

        EquipmentStatsApplier.Instance.Apply(player, item, 0, apply: true);
        Assert.Equal(110, player.GetInt32(UpdateFields.UnitFieldStat0));
        kit.System.CastSpell(player, pct.Id, SpellCastTargets.ForSelf(), true);
        Assert.Equal(132, player.GetInt32(UpdateFields.UnitFieldStat0));
        EquipmentStatsApplier.Instance.Apply(player, item, 0, apply: false);
        Assert.Equal(120, player.GetInt32(UpdateFields.UnitFieldStat0));
        kit.System.RemoveAuras(player, pct.Id);
        Assert.Equal(100, player.GetInt32(UpdateFields.UnitFieldStat0));

        kit.System.CastSpell(player, zero.Id, SpellCastTargets.ForSelf(), true);
        EquipmentStatsApplier.Instance.Apply(player, item, 0, apply: true);
        kit.System.RemoveAuras(player, zero.Id);
        Assert.Equal(110, player.GetInt32(UpdateFields.UnitFieldStat0));
        EquipmentStatsApplier.Instance.Apply(player, item, 0, apply: false);
        Assert.Equal(100, player.GetInt32(UpdateFields.UnitFieldStat0));
    }

    [Fact]
    public void TotalStatPercentage_UsesSourceFloorForNonIntegralResult()
    {
        SpellInfo pct = Spell(990034, Effect(SpellEffectName.ApplyAura, 25, aura: AuraType.ModTotalStatPercentage, misc: 0));
        using var kit = new SpellTestKit(pct);
        (Player player, _) = kit.AddPlayer(1);
        player.SetInt32(UpdateFields.UnitFieldStat0, 103);
        kit.System.CastSpell(player, pct.Id, SpellCastTargets.ForSelf(), true);
        Assert.Equal(128, player.GetInt32(UpdateFields.UnitFieldStat0));
    }

    [Fact]
    public void TotalStatPercentage_UpdatesStaminaHealthAndIntellectManaUsingExistingDerivedCurves()
    {
        SpellInfo stamina = Spell(990032, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModTotalStatPercentage, misc: 2));
        SpellInfo intellect = Spell(990033, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModTotalStatPercentage, misc: 3));
        using var kit = new SpellTestKit(stamina, intellect);
        (Player player, _) = kit.AddPlayer(1);
        player.SetInt32(UpdateFields.UnitFieldStat0 + 2, 20);
        player.SetInt32(UpdateFields.UnitFieldStat0 + 3, 20);
        player.MaxHealth = 100;
        player.Health = 100;
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        player.SetUInt32(UpdateFields.UnitFieldBaseMana, 1);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
        player.SetUInt32(UpdateFields.UnitFieldPower1, 100);

        kit.System.CastSpell(player, stamina.Id, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, intellect.Id, SpellCastTargets.ForSelf(), true);
        Assert.Equal(30, player.GetInt32(UpdateFields.UnitFieldStat0 + 2));
        Assert.Equal(30, player.GetInt32(UpdateFields.UnitFieldStat0 + 3));
        Assert.Equal(100u + (uint)(StatFormulas.HealthBonusFromStamina(30) - StatFormulas.HealthBonusFromStamina(20)), player.MaxHealth);
        Assert.Equal(100u + (uint)(StatFormulas.ManaBonusFromIntellect(30) - StatFormulas.ManaBonusFromIntellect(20)), player.GetUInt32(UpdateFields.UnitFieldMaxpower1));

        kit.System.RemoveAuras(player, stamina.Id);
        kit.System.RemoveAuras(player, intellect.Id);
        Assert.Equal(20, player.GetInt32(UpdateFields.UnitFieldStat0 + 2));
        Assert.Equal(20, player.GetInt32(UpdateFields.UnitFieldStat0 + 3));
    }

    [Fact]
    public void StatAura_RefreshesAttachedCombatMaintainerAndTooltipPolarity()
    {
        SpellInfo strength = Spell(990035, Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.ModStat, misc: 0));
        SpellInfo agilityFlat = Spell(990039, Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.ModStat, misc: 1));
        SpellInfo agilityNegativeFlat = Spell(990040, Effect(SpellEffectName.ApplyAura, -5, aura: AuraType.ModStat, misc: 1));
        SpellInfo agilityPercent = Spell(990036, Effect(SpellEffectName.ApplyAura, 20, aura: AuraType.ModTotalStatPercentage, misc: 1));
        SpellInfo negative = Spell(990037, Effect(SpellEffectName.ApplyAura, -10, aura: AuraType.ModTotalStatPercentage, misc: 1));
        using var kit = new SpellTestKit(strength, agilityFlat, agilityNegativeFlat, agilityPercent, negative);
        (Player player, _) = kit.AddPlayer(1);
        player.SetInt32(UpdateFields.UnitFieldStat0, 100);
        player.SetInt32(UpdateFields.UnitFieldStat0 + 1, 100);
        var maintainer = new PlayerStatSystem();
        maintainer.Attach(player);
        maintainer.UpdateAll(player);
        int attackPower = player.GetInt32(UpdateFields.UnitFieldAttackPower);
        uint armor = player.GetUInt32(UpdateFields.UnitFieldResistances);

        kit.System.CastSpell(player, strength.Id, SpellCastTargets.ForSelf(), true);
        Assert.True(player.GetInt32(UpdateFields.UnitFieldAttackPower) > attackPower);
        kit.System.CastSpell(player, agilityFlat.Id, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, agilityNegativeFlat.Id, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, agilityPercent.Id, SpellCastTargets.ForSelf(), true);
        Assert.True(player.GetUInt32(UpdateFields.UnitFieldResistances) > armor);
        Assert.Equal(12f, player.GetFloat(UpdateFields.PlayerFieldPosstat0 + 1), 3);
        Assert.Equal(-6f, player.GetFloat(UpdateFields.PlayerFieldNegstat0 + 1), 3);
        kit.System.CastSpell(player, negative.Id, SpellCastTargets.ForSelf(), true);
        Assert.Equal(10.8f, player.GetFloat(UpdateFields.PlayerFieldPosstat0 + 1), 3);
        Assert.Equal(-5.4f, player.GetFloat(UpdateFields.PlayerFieldNegstat0 + 1), 3);
    }

    [Fact]
    public void StaminaPercentAbility_PreservesCurrentHealthRatioAcrossMaxHealthChange()
    {
        SpellInfo stamina = Spell(990038, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModTotalStatPercentage, misc: 2)) with
        {
            Attributes = SpellAttributes.IsAbility,
        };
        using var kit = new SpellTestKit(stamina);
        (Player player, _) = kit.AddPlayer(1);
        player.SetInt32(UpdateFields.UnitFieldStat0 + 2, 20);
        player.MaxHealth = 100;
        player.Health = 50;
        kit.System.CastSpell(player, stamina.Id, SpellCastTargets.ForSelf(), true);
        Assert.Equal(player.MaxHealth / 2, player.Health);
        kit.System.RemoveAuras(player, stamina.Id);
        Assert.Equal(50u, player.Health);
    }

    private static Player AddDruid(SpellTestKit kit)
    {
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Druid);
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        PowerTypeSwitch.EnsureFeralPowerCaps(player);
        new ShapeshiftService(kit.System, new ShapeshiftFormCatalog([
            new((uint)DruidForms.Cat, 0, 0), new((uint)DruidForms.Bear, 0, 0), new((uint)DruidForms.DireBear, 0, 0),
            new((uint)DruidForms.Travel, 0, 0)]),
            new CombatOptions(), _ => []).Install();
        return player;
    }

    private static SpellInfo Dummy(uint icon = FurorRules.DummyIconId, int amount = 100) => Spell(998202, Effect(SpellEffectName.ApplyAura, amount, aura: AuraType.Dummy)) with
    {
        SpellIconId = icon,
        Duration = new SpellDuration(60_000, 0, 60_000),
    };

    private static SpellInfo Cat() => Spell(998200, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModShapeshift, misc: DruidForms.Cat)) with
    {
        Duration = new SpellDuration(60_000, 0, 60_000), StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };

    private static SpellInfo Bear() => Spell(998201, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModShapeshift, misc: DruidForms.Bear)) with
    {
        Duration = new SpellDuration(60_000, 0, 60_000), StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };

    private static SpellInfo DireBear() => Spell(998203, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModShapeshift, misc: DruidForms.DireBear)) with
    {
        Duration = new SpellDuration(60_000, 0, 60_000), StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };

    private static SpellInfo Energize(uint id, int amount, PowerType power) => Spell(id, Effect(SpellEffectName.Energize, amount, misc: (int)power)) with
    {
        StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };

    private static SpellInfo Travel() => Spell(998204, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModShapeshift, misc: DruidForms.Travel)) with
    {
        Duration = new SpellDuration(60_000, 0, 60_000), StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };

    private static SpellInfo Link(uint id) => Spell(id, Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.Dummy)) with
    {
        Attributes = SpellAttributes.Passive, Duration = new SpellDuration(-1, 0, -1), StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };
}
