using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The heal over time first-in-chain rule of vmangos Unit::RemoveNoStackAurasDueToAuraHolder (Unit.cpp:3373-3401 finds the chain of a holder
/// with a PERIODIC_HEAL or OBS_MOD_HEALTH aura, :3498-3500 makes two holders of that chain single per target, whatever the caster, and
/// :3509-3512 refuses a weaker rank). Periodic damage of one chain from two casters still stacks (:3548-3552).
/// </summary>
public sealed class HotFirstInChainStackingTests : IDisposable
{
    private const uint Renew1 = 139;
    private const uint Renew2 = 6074;
    private const uint Dot1 = 589;
    private const uint Dot2 = 594;
    private const uint FamilyPriest = 6;
    private const uint VestmentsBonus = 28807;

    private static SpellInfo Periodic(uint id, string name, AuraType aura, int amount, SpellImplicitTarget target) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, amount, target, aura, amplitude: 3000)) with
        {
            Name = name,
            SpellFamilyName = FamilyPriest,
            SpellVisual = 1,
            Duration = new SpellDuration(15_000, 0, 15_000),
        };

    private readonly SpellTestKit _kit;
    private readonly Player _caster;
    private readonly Player _otherCaster;
    private readonly Player _target;

    public HotFirstInChainStackingTests()
    {
        _kit = new SpellTestKit(
            Periodic(Renew1, "Renew", AuraType.PeriodicHeal, 10, SpellImplicitTarget.UnitCaster),
            Periodic(Renew2, "Renew", AuraType.PeriodicHeal, 20, SpellImplicitTarget.UnitCaster),
            Periodic(Dot1, "Shadow Word: Pain", AuraType.PeriodicDamage, 10, SpellImplicitTarget.UnitEnemy) with { Attributes = SpellAttributes.AuraIsDebuff },
            Periodic(Dot2, "Shadow Word: Pain", AuraType.PeriodicDamage, 20, SpellImplicitTarget.UnitEnemy) with { Attributes = SpellAttributes.AuraIsDebuff },
            // The passive set bonus named like the heal over time, in the same family but another icon, visual and aura: no rank of it.
            Periodic(VestmentsBonus, "Renew", AuraType.ModHealingPct, 5, SpellImplicitTarget.UnitCaster) with
            {
                Attributes = SpellAttributes.Passive | SpellAttributes.DoNotDisplay,
                SpellVisual = 0,
                SpellIconId = 1,
                Duration = new SpellDuration(-1, 0, -1),
            });
        (_caster, _) = _kit.AddPlayer(1, 0, 0);
        (_target, _) = _kit.AddPlayer(2, 3, 0);
        (_otherCaster, _) = _kit.AddPlayer(3, -2, 0);
        _target.MaxHealth = 1_000_000;
        _target.Health = 500_000;
    }

    public void Dispose() => _kit.Dispose();

    [Fact]
    public void AWeakerHotRankFromAnotherCaster_IsRefused()
    {
        Assert.True(_kit.System.AddAura(_target, Renew2, caster: _otherCaster));

        Assert.False(_kit.System.AddAura(_target, Renew1, caster: _caster));

        Assert.True(_kit.System.HasAura(_target, Renew2));
        Assert.False(_kit.System.HasAura(_target, Renew1));
    }

    [Fact]
    public void AStrongerHotRankFromAnotherCaster_ReplacesTheWeaker()
    {
        Assert.True(_kit.System.AddAura(_target, Renew1, caster: _otherCaster));

        Assert.True(_kit.System.AddAura(_target, Renew2, caster: _caster));

        Assert.True(_kit.System.HasAura(_target, Renew2));
        Assert.False(_kit.System.HasAura(_target, Renew1));
    }

    [Fact]
    public void DotRanksFromTwoCasters_StillStack()
    {
        Assert.True(_kit.System.AddAura(_target, Dot2, caster: _otherCaster));
        Assert.True(_kit.System.AddAura(_target, Dot1, caster: _caster));

        Assert.True(_kit.System.HasAura(_target, Dot2));
        Assert.True(_kit.System.HasAura(_target, Dot1));
    }

    [Fact]
    public void ASelfCastHot_DoesNotRemoveASameNamedPassiveSetBonus()
    {
        Assert.True(_kit.System.AddAura(_caster, VestmentsBonus, permanent: true, caster: _caster));

        Assert.True(_kit.System.AddAura(_caster, Renew1, caster: _caster));

        Assert.True(_kit.System.HasAura(_caster, VestmentsBonus));
        Assert.True(_kit.System.HasAura(_caster, Renew1));
    }
}
