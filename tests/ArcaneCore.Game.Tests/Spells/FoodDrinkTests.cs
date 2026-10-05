using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class FoodDrinkTests
{
    [Fact]
    public void FoodAuraAddsScaledHealthWhileSitting()
    {
        using var kit = new SpellTestKit(SpellTestKit.Spell(992001,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModRegen, amplitude: 1000)));
        (Player player, _) = kit.AddPlayer(1);
        BindCombat(kit, player);
        player.Health = 100;
        player.SetStandState(StandState.Sit);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 992001, SpellCastTargets.ForSelf(), true));
        Tick(kit, player);
        Assert.Equal(200u, player.Health);
    }

    [Fact]
    public void DrinkAuraMatchesManaAndDoesNotAddRage()
    {
        using var kit = new SpellTestKit(SpellTestKit.Spell(992002,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 25, aura: AuraType.ModPowerRegen, misc: (int)PowerType.Mana, amplitude: 1000)));
        (Player player, _) = kit.AddPlayer(1);
        BindCombat(kit, player);
        player.SetUInt32(UpdateFields.UnitFieldPower1, 0);
        player.SetUInt32(UpdateFields.UnitFieldPower2, 0);
        uint rageBefore = player.GetUInt32(UpdateFields.UnitFieldPower2);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 992002, SpellCastTargets.ForSelf(), true));
        Tick(kit, player);
        Assert.Equal(10u, player.GetUInt32(UpdateFields.UnitFieldPower1));
        Assert.Equal(rageBefore, player.GetUInt32(UpdateFields.UnitFieldPower2));
    }

    [Fact]
    public void FoodZeroAmplitudeUsesTheVanillaFiveSecondDefault()
    {
        using var kit = new SpellTestKit(SpellTestKit.Spell(992004,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 100, aura: AuraType.ModRegen)));
        (Player player, _) = kit.AddPlayer(1);
        BindCombat(kit, player);
        player.Health = 100;
        player.SetStandState(StandState.Sit);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 992004, SpellCastTargets.ForSelf(), true));
        Tick(kit, player);
        Assert.Equal(140u, player.Health);
    }

    [Fact]
    public void StandingInterruptRemovesFoodDrinkAura()
    {
        using var kit = new SpellTestKit(SpellTestKit.Spell(992003,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 20, aura: AuraType.ModRegen, amplitude: 1000)) with
        { AuraInterruptFlags = SpellAuraInterruptFlags.StandingCancels });
        (Player player, _) = kit.AddPlayer(1);
        player.SetStandState(StandState.Sit);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 992003, SpellCastTargets.ForSelf(), true));
        Assert.True(kit.System.HasAura(player, 992003));
        kit.System.RemoveAurasWithInterruptFlags(player, (uint)SpellAuraInterruptFlags.StandingCancels);
        Assert.False(kit.System.HasAura(player, 992003));
    }

    [Theory]
    [InlineData(0u, false, 1f, 10u)]
    [InlineData(1000u, true, 1f, 10u)]
    [InlineData(2000u, true, 2f, 20u)]
    public void DrinkIgnoresAmplitudeAndSurvivesFiveSecondRuleAndCombat(uint amplitude, bool inCombat, float rate, uint expected)
    {
        using var kit = new SpellTestKit(SpellTestKit.Spell(992005,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 25, aura: AuraType.ModPowerRegen,
                misc: (int)PowerType.Mana, amplitude: amplitude)));
        (Player player, _) = kit.AddPlayer(1);
        BindCombat(kit, player, rate);
        player.SetUInt32(UpdateFields.UnitFieldPower1, 0);
        player.Combat.LastManaUseTimer = 5000;
        if (inCombat) player.Map!.Combat.SetInCombatState(player, 60000);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 992005, SpellCastTargets.ForSelf(), true));
        Tick(kit, player);
        Assert.Equal(expected, player.GetUInt32(UpdateFields.UnitFieldPower1));
    }

    [Fact]
    public void FoodStopsInCombat_AndDrinkDoesNotRestoreAnotherPower()
    {
        using var kit = new SpellTestKit(SpellTestKit.Spell(992006,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 100, aura: AuraType.ModRegen),
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 100, aura: AuraType.ModPowerRegen, misc: (int)PowerType.Energy)));
        (Player player, _) = kit.AddPlayer(1);
        BindCombat(kit, player);
        player.Health = 100;
        player.SetUInt32(UpdateFields.UnitFieldPower1, 0);
        player.Map!.Combat.SetInCombatState(player, 60000);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 992006, SpellCastTargets.ForSelf(), true));
        Tick(kit, player);
        Assert.Equal(100u, player.Health);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldPower1));
    }

    private static void BindCombat(SpellTestKit kit, Player player, float manaRate = 1f)
    {
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions { RateMana = manaRate },
            new SpellSystemPowerAuras(kit.System)));
        player.MaxHealth = 1000;
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 0);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000);
    }

    private static void Tick(SpellTestKit kit, Player player)
    {
        player.Combat.RegenTimer = 0;
        kit.World.RunTick(50);
    }
}
