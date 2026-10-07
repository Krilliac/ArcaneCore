using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;
using static ArcaneCore.Game.Tests.SpellMods.ModTestSupport;

namespace ArcaneCore.Game.Tests.SpellMods;

/// <summary>
/// SPELLMOD_ACTIVATION_TIME on a periodic aura's tick interval: vmangos Aura::CalculatePeriodic (SpellAuras.cpp:8053-8085) applies it to
/// m_modifier.periodictime of the periodic aura types when the aura is created (SpellAuras.cpp:293) and again when it is refreshed
/// (:319). Synthetic spells only; each test pins the unmodified interval next to the modified one.
/// </summary>
public sealed class PeriodicActivationTimeModTests
{
    private const uint FamilyDot = 946001;
    private const uint FamilyHot = 946002;
    private const uint ActivationFlat = 946003;
    private const uint FamilyDummyAura = 946004;

    private static SpellTestKit Kit() => new(
        InFamily(Spell(FamilyDot, Effect(SpellEffectName.ApplyAura, 4, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
        {
            Duration = new SpellDuration(12000, 0, 12000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        }),
        InFamily(Spell(FamilyHot, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.PeriodicHeal, amplitude: 3000)) with
        {
            Duration = new SpellDuration(12000, 0, 12000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        }),
        // not one of the CalculatePeriodic types: its amplitude is left alone
        InFamily(Spell(FamilyDummyAura, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy, amplitude: 3000)) with
        {
            Duration = new SpellDuration(12000, 0, 12000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        }),
        Flat(ActivationFlat, SpellModOp.ActivationTime, -1000));

    private static SpellAura AuraOf(SpellTestKit kit, Unit unit, uint spell) =>
        kit.System.GetAuras(unit).Single(h => h.Spell.Id == spell && !h.IsRemoved).Auras[0]!;

    [Fact]
    public void ActivationTimeMod_ShortensTheTickIntervalOfADot()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 5, 0);
        kit.System.Relations = new FakeRelations { Hostile = { victim.Guid } };

        kit.System.CastSpell(caster, FamilyDot, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        Assert.Equal(3000u, AuraOf(kit, victim, FamilyDot).Period);
        kit.System.RemoveAuras(victim, FamilyDot);

        kit.System.LearnSpell(caster, ActivationFlat);
        kit.System.CastSpell(caster, FamilyDot, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        SpellAura aura = AuraOf(kit, victim, FamilyDot);
        Assert.Equal(2000u, aura.Period);

        kit.Advance(12000);
        Assert.Equal(6, aura.TickCount); // 12 s / 2 s (unmodified: 4)
    }

    [Fact]
    public void ActivationTimeMod_AppliesAgainWhenTheAuraIsRefreshed()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);

        kit.System.CastSpell(caster, FamilyHot, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(3000u, AuraOf(kit, caster, FamilyHot).Period);

        kit.System.LearnSpell(caster, ActivationFlat);
        kit.System.CastSpell(caster, FamilyHot, SpellCastTargets.ForSelf(), triggered: true); // same caster, same spell: refreshed in place

        Assert.Single(kit.System.GetAuras(caster), h => h.Spell.Id == FamilyHot);
        Assert.Equal(2000u, AuraOf(kit, caster, FamilyHot).Period);
    }

    [Fact]
    public void ActivationTimeMod_LeavesANonPeriodicAuraTypeAlone()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        kit.System.LearnSpell(caster, ActivationFlat);

        kit.System.CastSpell(caster, FamilyDummyAura, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(3000u, AuraOf(kit, caster, FamilyDummyAura).Amplitude);
    }
}
