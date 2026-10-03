using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// vmangos SpellAuraHolder::SetStackAmount (SpellAuras.cpp:6965-6992): when a stack changes an aura's
/// amount, the aura is un-applied with the old amount and applied again with the new one, so a handler
/// that moves a unit field never leaves the old contribution behind.
/// </summary>
public sealed class AuraStackReapplyTests
{
    private const uint Stacking = 930101;
    private const AuraType Probe = SpellHandlerModuleTests.FixtureAura;

    [Fact]
    public void RestackingAnAura_UnappliesTheOldAmountAndAppliesTheNewOne()
    {
        using var kit = new SpellTestKit(
            Spell(Stacking, Effect(SpellEffectName.ApplyAura, -50, SpellImplicitTarget.UnitEnemy, Probe)) with
            {
                StackAmount = 5,
                Duration = new SpellDuration(30000, 0, 30000),
                Attributes = SpellAttributes.AuraIsDebuff,
                SpellVisual = 1,
            });
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        var calls = new List<(bool Apply, int Amount)>();
        kit.System.RegisterAura(Probe, new AuraHandler((_, _, aura, apply) => calls.Add((apply, aura.Amount)), null));

        for (int i = 0; i < 3; i++)
        {
            kit.System.CastSpell(caster, Stacking, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        }

        Assert.Equal(
            [(true, -50), (false, -50), (true, -100), (false, -100), (true, -150)],
            calls);

        // Removing the holder un-applies the amount that is currently applied.
        kit.System.RemoveAuras(target, Stacking);
        Assert.Equal((false, -150), calls[^1]);
    }

    [Fact]
    public void RefreshingAFullStack_ChangesNoAmount_SoNothingIsReapplied()
    {
        using var kit = new SpellTestKit(
            Spell(Stacking, Effect(SpellEffectName.ApplyAura, -50, SpellImplicitTarget.UnitEnemy, Probe)) with
            {
                StackAmount = 2,
                Duration = new SpellDuration(30000, 0, 30000),
                Attributes = SpellAttributes.AuraIsDebuff,
                SpellVisual = 1,
            });
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        var calls = new List<(bool Apply, int Amount)>();
        kit.System.RegisterAura(Probe, new AuraHandler((_, _, aura, apply) => calls.Add((apply, aura.Amount)), null));

        for (int i = 0; i < 4; i++)
        {
            kit.System.CastSpell(caster, Stacking, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        }

        Assert.Equal([(true, -50), (false, -50), (true, -100)], calls);
    }
}
