using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class HealthRegenPercentTests
{
    [Fact]
    public void Aura88_StacksMultiplicativelyAndRemovalLeavesRemainingFactor()
    {
        using var kit = new SpellTestKit(Percent(997001, 50), Percent(997002, 50));
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        player.SetUInt32(UpdateFields.UnitFieldMaxhealth, 1000);
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 30);
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(kit.System)));

        kit.System.CastSpell(player, 997001, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, 997002, SpellCastTargets.ForSelf(), true);
        player.Health = 100;
        kit.World.RunTick(2000);
        Assert.Equal(109u, player.Health);

        player.Health = 100;
        kit.System.RemoveAuras(player, 997001);
        kit.World.RunTick(2000);
        // The first two-aura tick carried 0.675 from 9.675; the remaining
        // factor therefore contributes floor(6.45 + 0.675) = 7.
        Assert.Equal(107u, player.Health);

        player.Health = 100;
        kit.System.RemoveAuras(player, 997002);
        kit.World.RunTick(2000);
        Assert.Equal(104u, player.Health);
    }

    [Fact]
    public void Aura88_AloneDoesNotEnableCombatSpiritRegen_AndAura116StillWins()
    {
        using var kit = new SpellTestKit(Percent(997003, 50),
            Spell(997004, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModRegenDuringCombat))
                with { Duration = new SpellDuration(30_000, 0, 30_000) },
            Spell(997005, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModHealthRegenInCombat))
                with { Duration = new SpellDuration(30_000, 0, 30_000) });
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        player.SetUInt32(UpdateFields.UnitFieldMaxhealth, 1000);
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 30);
        player.Health = 100;
        player.Map!.Combat.SetInCombatState(player, 10_000);
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(kit.System)));

        kit.System.CastSpell(player, 997003, SpellCastTargets.ForSelf(), true);
        kit.World.RunTick(2000);
        Assert.Equal(100u, player.Health);

        kit.System.CastSpell(player, 997004, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, 997005, SpellCastTargets.ForSelf(), true);
        player.Health = 100;
        kit.World.RunTick(2000);
        Assert.Equal(104u, player.Health);
    }

    [Fact]
    public void Aura88_ScalesRateHealthButFoodRemainsUnscaled()
    {
        using var kit = new SpellTestKit(
            Percent(997006, 50),
            Spell(997007, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModRegen, amplitude: 5000))
                with { Duration = new SpellDuration(30_000, 0, 30_000) });
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        player.SetUInt32(UpdateFields.UnitFieldMaxhealth, 1000);
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 30);
        player.Health = 100;
        CombatEnvironment.Register(kit.World, new CombatEnvironment(
            new CombatOptions { RateHealth = 2.0f }, new SpellSystemPowerAuras(kit.System)));

        kit.System.CastSpell(player, 997006, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, 997007, SpellCastTargets.ForSelf(), true);
        kit.World.RunTick(2000);

        Assert.Equal(114u, player.Health);
    }

    [Fact]
    public void Aura88_NegativeFactorClampsAndHealthCaps()
    {
        using var kit = new SpellTestKit(Percent(997008, -150), Percent(997009, 50));
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        player.SetUInt32(UpdateFields.UnitFieldMaxhealth, 101);
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 30);
        player.Health = 100;
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(kit.System)));

        kit.System.CastSpell(player, 997008, SpellCastTargets.ForSelf(), true);
        kit.World.RunTick(2000);
        Assert.Equal(100u, player.Health);

        kit.System.RemoveAuras(player, 997008);
        kit.System.CastSpell(player, 997009, SpellCastTargets.ForSelf(), true);
        kit.World.RunTick(2000);
        Assert.Equal(101u, player.Health);
    }

    private static SpellInfo Percent(uint id, int amount)
        => Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura: AuraType.ModHealthRegenPercent))
            with { Duration = new SpellDuration(30_000, 0, 30_000) };
}
