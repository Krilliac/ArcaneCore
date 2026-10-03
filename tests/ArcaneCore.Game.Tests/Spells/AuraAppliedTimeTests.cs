using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The time a holder was applied (vmangos SpellAuraHolder::m_applyTime = time(nullptr) at construction,
/// SpellAuras.cpp:6665; getter SpellAuras.h:473), in whole Unix seconds on an injectable clock. Duel completion removes the
/// opposing negative auras "applied since the duel started" through it (Player.cpp:6757-6770).
/// </summary>
public sealed class AuraAppliedTimeTests
{
    private const uint Stacking = 930201;
    private const uint Plain = 930202;

    private static SpellTestKit Kit(out long[] clock)
    {
        var box = new long[1] { 1_800_000_000 };
        clock = box;
        var kit = new SpellTestKit(
            Spell(Stacking, Effect(SpellEffectName.ApplyAura, 4, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
            {
                StackAmount = 3,
                Duration = new SpellDuration(30000, 0, 30000),
                Attributes = SpellAttributes.AuraIsDebuff,
                SpellVisual = 1,
            },
            Spell(Plain, Effect(SpellEffectName.ApplyAura, 4, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
            {
                Duration = new SpellDuration(30000, 0, 30000),
                Attributes = SpellAttributes.AuraIsDebuff,
                SpellVisual = 1,
            });
        kit.System.UnixSecondsClock = () => box[0];
        return kit;
    }

    [Fact]
    public void ANewHolder_IsStampedWithTheClockAtApplication()
    {
        using SpellTestKit kit = Kit(out long[] clock);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);

        clock[0] = 1_800_000_042;
        kit.System.CastSpell(caster, Plain, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(1_800_000_042, kit.System.GetAuras(target).Single(h => h.Spell.Id == Plain).AppliedAtUnixSeconds);
    }

    [Fact]
    public void RestackingTheSameSpell_KeepsTheFirstStamp()
    {
        using SpellTestKit kit = Kit(out long[] clock);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(caster, Stacking, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        clock[0] += 30;
        kit.System.CastSpell(caster, Stacking, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        SpellAuraHolder holder = kit.System.GetAuras(target).Single(h => h.Spell.Id == Stacking);
        Assert.Equal(2, holder.StackAmount);
        Assert.Equal(1_800_000_000, holder.AppliedAtUnixSeconds); // vmangos refresh does not touch m_applyTime
    }

    [Fact]
    public void ReapplyingANonStackingSpell_ReplacesTheHolderAndRestamps()
    {
        using SpellTestKit kit = Kit(out long[] clock);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(caster, Plain, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        clock[0] += 30;
        kit.System.CastSpell(caster, Plain, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        SpellAuraHolder holder = kit.System.GetAuras(target).Single(h => h.Spell.Id == Plain);
        Assert.Equal(1_800_000_030, holder.AppliedAtUnixSeconds);
    }
}
