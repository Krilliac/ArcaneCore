using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class CombatFlatHealthRegenTests
{
    [Fact]
    public void Aura161_CombatAddsFlatHealthWithoutEnablingSpiritOrRageDecay()
    {
        using var kit = new SpellTestKit(Flat(996001, 5));
        (Player player, _) = kit.AddPlayer(1);
        player.SetUInt32(UpdateFields.UnitFieldMaxhealth, 1000);
        player.Health = 100;
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 30);
        SpellSystem.SetPower(player, PowerType.Rage, 100);
        player.Map!.Combat.SetInCombatState(player, 10_000);
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(kit.System)));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 996001, SpellCastTargets.ForSelf(), true));
        kit.World.RunTick(2000);

        Assert.Equal(102u, player.Health);
        Assert.Equal(100u, SpellSystem.GetPower(player, PowerType.Rage));
    }

    [Fact]
    public void Aura161_SumsAndRemovalLeaveTheRemainingContribution()
    {
        using var kit = new SpellTestKit(Flat(996002, 5), Flat(996003, 10));
        (Player player, _) = kit.AddPlayer(1);
        player.SetUInt32(UpdateFields.UnitFieldMaxhealth, 1000);
        player.Health = 100;
        player.Map!.Combat.SetInCombatState(player, 10_000);
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(kit.System)));

        kit.System.CastSpell(player, 996002, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, 996003, SpellCastTargets.ForSelf(), true);
        kit.World.RunTick(2000);
        Assert.Equal(106u, player.Health);

        player.Health = 100;
        kit.System.RemoveAuras(player, 996002);
        kit.World.RunTick(2000);
        Assert.Equal(104u, player.Health);

        player.Health = 100;
        kit.System.RemoveAuras(player, 996003);
        kit.World.RunTick(2000);
        Assert.Equal(100u, player.Health);
    }

    [Fact]
    public void Aura161_RateHealthScalesSpiritAndFlatButNotFood()
    {
        using var kit = new SpellTestKit(
            Flat(996004, 5),
            Spell(996005, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModRegen, amplitude: 5000))
                with { Duration = new SpellDuration(30_000, 0, 30_000) });
        (Player player, _) = kit.AddPlayer(1);
        player.SetUInt32(UpdateFields.UnitFieldMaxhealth, 1000);
        player.Health = 100;
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 30);
        CombatEnvironment.Register(kit.World, new CombatEnvironment(
            new CombatOptions { RateHealth = 2.0f }, new SpellSystemPowerAuras(kit.System)));

        kit.System.CastSpell(player, 996004, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, 996005, SpellCastTargets.ForSelf(), true);
        kit.World.RunTick(2000);

        // Mage spirit regen is 4.3 per tick, doubled by Rate.Health; food is 2.0
        // per tick and is deliberately not rate-scaled; aura 161 contributes 4.
        // The fractional 0.6 is carried for a later tick, so this tick gains 14.
        Assert.Equal(114u, player.Health);
    }

    [Fact]
    public void Aura161_NegativeContributionCannotReduceHealth()
    {
        using var kit = new SpellTestKit(Flat(996006, -5));
        (Player player, _) = kit.AddPlayer(1);
        player.SetUInt32(UpdateFields.UnitFieldMaxhealth, 1000);
        player.Health = 100;
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(kit.System)));

        kit.System.CastSpell(player, 996006, SpellCastTargets.ForSelf(), true);
        kit.World.RunTick(2000);

        Assert.Equal(100u, player.Health);
    }

    [Fact]
    public void Aura161_FractionalContributionCarriesAndHealthCaps()
    {
        using var kit = new SpellTestKit(Flat(996007, 1), Flat(996008, 5));
        (Player player, _) = kit.AddPlayer(1);
        player.SetUInt32(UpdateFields.UnitFieldMaxhealth, 101);
        player.Health = 100;
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(kit.System)));

        kit.System.CastSpell(player, 996007, SpellCastTargets.ForSelf(), true);
        kit.World.RunTick(2000);
        Assert.Equal(100u, player.Health);
        kit.World.RunTick(2000);
        Assert.Equal(100u, player.Health);
        kit.World.RunTick(2000);
        Assert.Equal(101u, player.Health);

        player.Health = 100;
        kit.System.CastSpell(player, 996008, SpellCastTargets.ForSelf(), true);
        kit.World.RunTick(2000);
        Assert.Equal(101u, player.Health);
    }

    [Fact]
    public void RateHealth_NegativeValueNormalizesToRetailDefault()
    {
        var options = new CombatOptions { RateHealth = -1.0f };

        Assert.Contains("Rate.Health", options.Normalize());
        Assert.Equal(1.0f, options.RateHealth);
    }

    private static SpellInfo Flat(uint id, int amount)
        => Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura: AuraType.ModHealthRegenInCombat))
            with { Duration = new SpellDuration(30_000, 0, 30_000) };
}
