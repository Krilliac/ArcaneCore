using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>Hunter lane: tracker exclusivity and the Hunter's Mark target rule.</summary>
public sealed partial class SpellSystem
{
    /// <summary>
    /// Remove every other holder of <paramref name="target"/> that carries one of
    /// <paramref name="types"/> (vmangos Unit::RemoveNoStackAurasDueToAuraHolder for the SPELL_TRACKER
    /// class). The holder passed in is kept.
    /// </summary>
    internal void RemoveOtherHolders(Unit target, SpellAuraHolder keep, IReadOnlyCollection<AuraType> types)
    {
        if (GetState(target.Guid) is not { } state)
        {
            return;
        }

        foreach (SpellAuraHolder holder in state.Auras.Where(h => !ReferenceEquals(h, keep) && types.Any(h.HasAura)).ToArray())
        {
            RemoveHolder(state, holder);
        }
    }

    /// <summary>
    /// vmangos Spell::CheckCast SPELL_AURA_MOD_STALKED (Spell.cpp:6436-6447): the spell needs a unit
    /// target (BAD_IMPLICIT_TARGETS), and one the caster may attack when it names it explicitly
    /// (BAD_TARGETS), as Hunter's Mark does.
    /// </summary>
    private SpellCastResult CheckStalkedTarget(Unit caster, SpellInfo spell, SpellCastTargets targets, Unit? unitTarget)
    {
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.IsEmpty || effect.Effect != SpellEffectName.ApplyAura || effect.AuraType != AuraType.ModStalked)
            {
                continue;
            }

            Unit? target = unitTarget ?? (targets.Mask == SpellCastTargetFlags.Self ? caster : null);
            if (target is null)
            {
                return SpellCastResult.BadImplicitTargets;
            }

            if (effect.TargetA == SpellImplicitTarget.Unit && !Relations.IsHostile(caster, target))
            {
                return SpellCastResult.BadTargets;
            }
        }

        return SpellCastResult.CastOk;
    }
}
