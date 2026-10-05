using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class AngerManagementTests
{
    [Fact]
    public void RageAura_FirstTickIsFiveSeconds_ThenThreeSecondCadence()
    {
        using var kit = new SpellTestKit(RageSpell(994001,
            Effect(SpellEffectName.ApplyAura, 17, aura: AuraType.ModPowerRegen, misc: (int)PowerType.Rage)));
        (Player player, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 994001, SpellCastTargets.ForSelf(), true));
        kit.Advance(4999);
        Assert.Equal(0u, SpellSystem.GetPower(player, PowerType.Rage));
        kit.Advance(1);
        Assert.Equal(10u, SpellSystem.GetPower(player, PowerType.Rage));
        kit.Advance(2999);
        Assert.Equal(10u, SpellSystem.GetPower(player, PowerType.Rage));
        kit.Advance(1);
        Assert.Equal(20u, SpellSystem.GetPower(player, PowerType.Rage));
    }

    [Fact]
    public void RageAura_DeadOrMismatchedPowerDoesNothing()
    {
        using var kit = new SpellTestKit(RageSpell(994002,
            Effect(SpellEffectName.ApplyAura, 17, aura: AuraType.ModPowerRegen, misc: (int)PowerType.Rage)));
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Energy);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 994002, SpellCastTargets.ForSelf(), true));
        kit.Advance(5000);
        Assert.Equal(0u, SpellSystem.GetPower(player, PowerType.Rage));
    }

    [Fact]
    public void RageAura_CatchupDeliversAllElapsedTicks()
    {
        using var kit = new SpellTestKit(RageSpell(994003, Effect(SpellEffectName.ApplyAura, 17, aura: AuraType.ModPowerRegen, misc: (int)PowerType.Rage)));
        (Player player, _) = kit.AddPlayer(1);
        kit.System.CastSpell(player, 994003, SpellCastTargets.ForSelf(), true);
        kit.Advance(11_000, step: 11_000);
        Assert.Equal(30u, SpellSystem.GetPower(player, PowerType.Rage));
    }

    [Fact]
    public void RageAura_DeadTargetDoesNotTick()
    {
        using var kit = new SpellTestKit(RageSpell(994004, Effect(SpellEffectName.ApplyAura, 17, aura: AuraType.ModPowerRegen, misc: (int)PowerType.Rage)));
        (Player player, _) = kit.AddPlayer(1);
        kit.System.CastSpell(player, 994004, SpellCastTargets.ForSelf(), true);
        player.Health = 0;
        kit.Advance(5000);
        Assert.Equal(0u, SpellSystem.GetPower(player, PowerType.Rage));
    }

    [Fact]
    public void RestoredRageAura_PreservesFirstTimerRemainderAbovePeriod()
    {
        using var kit = new SpellTestKit(Spell(994005,
            Effect(SpellEffectName.ApplyAura, 17, aura: AuraType.ModPowerRegen, misc: (int)PowerType.Rage)));
        (Player player, _) = kit.AddPlayer(1);
        PersistedAura saved = new()
        {
            SpellId = 994005, CasterGuid = player.Guid, CasterLevel = player.Level,
            MaxDurationMs = 20_000, RemainingMs = 20_000, EffectMask = 1,
            Amounts = [17, 0, 0], PeriodicTimers = [4000, 0, 0], SavedAtUnixMs = 0,
        };
        Assert.Single(kit.System.RestoreAuras(player, [saved], 0));
        kit.Advance(3999);
        Assert.Equal(0u, SpellSystem.GetPower(player, PowerType.Rage));
        kit.Advance(1);
        Assert.Equal(10u, SpellSystem.GetPower(player, PowerType.Rage));
        SpellStateSnapshot state = kit.System.CaptureState(player, 0);
        Assert.Equal(3000, state.Auras.Single().PeriodicTimers[0]);
    }

    private static SpellInfo RageSpell(uint id, params SpellEffectInfo[] effects)
        => Spell(id, effects) with { Duration = new SpellDuration(20_000, 0, 20_000) };
}
