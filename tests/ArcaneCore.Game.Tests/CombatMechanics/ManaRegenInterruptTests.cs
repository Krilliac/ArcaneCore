using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

public sealed class ManaRegenInterruptTests
{
    [Fact]
    public void ManaModifierLifecycle_StacksSpiritFactors_RemovesThem_AndHonorsRateAndCap()
    {
        using var kit = new SpellTestKit(
            Spell(998150, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModManaRegenInterrupt)),
            Spell(998151, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModPowerRegenPercent, misc: (int)PowerType.Mana)),
            Spell(998152, Effect(SpellEffectName.ApplyAura, 100, aura: AuraType.ModPowerRegenPercent, misc: (int)PowerType.Mana)),
            Spell(998153, Effect(SpellEffectName.ApplyAura, 25, aura: AuraType.ModPowerRegen, misc: (int)PowerType.Mana)));
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 100);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000);
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions { RateMana = 2 }, new SpellSystemPowerAuras(kit.System)));
        foreach (uint id in new uint[] { 998150, 998151, 998152, 998153 })
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, id, SpellCastTargets.ForSelf(), true));
        player.Combat.NoteManaUsed();
        player.Combat.RegenTimer = 0;
        kit.World.RunTick(50);
        Assert.Equal(132u, player.GetUInt32(UpdateFields.UnitFieldPower1));
        kit.System.RemoveAuras(player, 998152);
        player.SetUInt32(UpdateFields.UnitFieldPower1, 0);
        player.Combat.RegenTimer = 0;
        kit.World.RunTick(50);
        Assert.Equal(76u, player.GetUInt32(UpdateFields.UnitFieldPower1));
        kit.System.RemoveAuras(player, 998150);
        player.SetUInt32(UpdateFields.UnitFieldPower1, 0);
        player.Combat.RegenTimer = 0;
        kit.World.RunTick(50);
        Assert.Equal(20u, player.GetUInt32(UpdateFields.UnitFieldPower1));
        player.Combat.LastManaUseTimer = 0;
        player.SetUInt32(UpdateFields.UnitFieldPower1, 97);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 99);
        player.Combat.RegenTimer = 0;
        kit.World.RunTick(50);
        Assert.Equal(99u, player.GetUInt32(UpdateFields.UnitFieldPower1));
    }

    [Theory]
    [InlineData(50, 50f)]
    [InlineData(100, 100f)]
    [InlineData(150, 100f)]
    public void Aura134_UsesTheCappedSpiritPercentageAfterManaSpend(int amount, float expected)
    {
        using var kit = new SpellTestKit(
            Spell(998101, Effect(SpellEffectName.ApplyAura, amount, aura: AuraType.ModManaRegenInterrupt)),
            Spell(998102, Effect(SpellEffectName.ApplyAura, 25, aura: AuraType.ModPowerRegen,
                misc: (int)PowerType.Mana)));
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(kit.System)));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998101, SpellCastTargets.ForSelf(), true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998102, SpellCastTargets.ForSelf(), true));
        player.Combat.LastManaUseTimer = CombatConstants.ManaRegenInterruptMs;

        SpellSystemPowerAuras auras = new(kit.System);
        Assert.Equal(expected, auras.GetManaRegenInterruptPercent(player));
        Assert.Equal(25f * (CombatConstants.PlayerRegenIntervalMs / 5000f), auras.GetDrinkPowerRegen(player, PowerType.Mana, CombatConstants.PlayerRegenIntervalMs));
    }

    [Fact]
    public void Aura134_IsRemovedAndNormalManaRegenReturns()
    {
        using var kit = new SpellTestKit(Spell(998103,
            Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModManaRegenInterrupt)));
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        SpellSystemPowerAuras auras = new(kit.System);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998103, SpellCastTargets.ForSelf(), true));
        Assert.Equal(50f, auras.GetManaRegenInterruptPercent(player));
        kit.System.RemoveAuras(player, 998103);
        Assert.Equal(0f, auras.GetManaRegenInterruptPercent(player));
    }

    [Fact]
    public void Aura134_IsAdditiveAcrossHoldersButCappedAtOneHundred()
    {
        using var kit = new SpellTestKit(
            Spell(998104, Effect(SpellEffectName.ApplyAura, 60, aura: AuraType.ModManaRegenInterrupt)),
            Spell(998105, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModManaRegenInterrupt)));
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        SpellSystemPowerAuras auras = new(kit.System);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998104, SpellCastTargets.ForSelf(), true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998105, SpellCastTargets.ForSelf(), true));
        Assert.Equal(100f, auras.GetManaRegenInterruptPercent(player));
        kit.System.RemoveAuras(player, 998104);
        Assert.Equal(50f, auras.GetManaRegenInterruptPercent(player));
    }

    [Fact]
    public void RecentManaSpend_RetainsSpiritPercentageAndFlatDrinkContribution()
    {
        using var kit = new SpellTestKit(
            Spell(998107, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModManaRegenInterrupt)),
            Spell(998108, Effect(SpellEffectName.ApplyAura, 25, aura: AuraType.ModPowerRegen, misc: (int)PowerType.Mana)));
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(kit.System)));
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 100);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000);
        player.SetUInt32(UpdateFields.UnitFieldPower1, 0);
        player.Combat.LastManaUseTimer = CombatConstants.ManaRegenInterruptMs;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998107, SpellCastTargets.ForSelf(), true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998108, SpellCastTargets.ForSelf(), true));

        player.Combat.RegenTimer = 0;
        kit.World.RunTick(50);

        Assert.Equal(28u, player.GetUInt32(UpdateFields.UnitFieldPower1));
    }

    [Fact]
    public void WithoutRecentManaSpend_Aura134DoesNotChangeNormalSpiritRate()
    {
        using var kit = new SpellTestKit(Spell(998109,
            Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModManaRegenInterrupt)));
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions { RateMana = 2.0f }, new SpellSystemPowerAuras(kit.System)));
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 100);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000);
        player.SetUInt32(UpdateFields.UnitFieldPower1, 0);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998109, SpellCastTargets.ForSelf(), true));

        player.Combat.RegenTimer = 0;
        kit.World.RunTick(50);

        Assert.Equal(75u, player.GetUInt32(UpdateFields.UnitFieldPower1));
    }

    [Theory]
    [InlineData(50, 18u)]
    [InlineData(100, 37u)]
    [InlineData(150, 37u)]
    public void RecentManaSpend_UsesAura134PercentageAndCap(int amount, uint expected)
    {
        using var kit = new SpellTestKit(Spell(998110,
            Effect(SpellEffectName.ApplyAura, amount, aura: AuraType.ModManaRegenInterrupt)));
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(kit.System)));
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 100);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000);
        player.Combat.LastManaUseTimer = CombatConstants.ManaRegenInterruptMs;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998110, SpellCastTargets.ForSelf(), true));
        player.Combat.RegenTimer = 0;
        kit.World.RunTick(50);
        Assert.Equal(expected, player.GetUInt32(UpdateFields.UnitFieldPower1));
    }

    [Fact]
    public void RemovingAura134_LeavesRecentSpendSuppressedUntilTheTimerExpires()
    {
        using var kit = new SpellTestKit(Spell(998111,
            Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModManaRegenInterrupt)));
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(kit.System)));
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 100);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998111, SpellCastTargets.ForSelf(), true));
        player.Combat.LastManaUseTimer = CombatConstants.ManaRegenInterruptMs;
        kit.System.RemoveAuras(player, 998111);
        player.Combat.RegenTimer = 0;
        kit.World.RunTick(50);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldPower1));
        player.Combat.LastManaUseTimer = 0;
        player.Combat.RegenTimer = 0;
        kit.World.RunTick(50);
        Assert.Equal(37u, player.GetUInt32(UpdateFields.UnitFieldPower1));
    }

    [Fact]
    public void Aura110MultipliesSpiritBeforeAura134_AndFlatDrinkIsOutsideBoth()
    {
        using var kit = new SpellTestKit(
            Spell(998112, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModManaRegenInterrupt)),
            Spell(998113, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModPowerRegenPercent, misc: (int)PowerType.Mana)),
            Spell(998114, Effect(SpellEffectName.ApplyAura, 25, aura: AuraType.ModPowerRegen, misc: (int)PowerType.Mana)));
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(kit.System)));
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 100);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000);
        player.Combat.LastManaUseTimer = CombatConstants.ManaRegenInterruptMs;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998112, SpellCastTargets.ForSelf(), true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998113, SpellCastTargets.ForSelf(), true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 998114, SpellCastTargets.ForSelf(), true));
        player.Combat.RegenTimer = 0;
        kit.World.RunTick(50);
        Assert.Equal(38u, player.GetUInt32(UpdateFields.UnitFieldPower1));
    }
}
