using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>Hunter lane: tracker exclusivity and the Hunter's Mark target rule.</summary>
public sealed partial class SpellSystem
{
    /// <summary>
    /// Remove every other holder of <paramref name="target"/> that carries one of
    /// <paramref name="types"/> (vmangos Unit::RemoveNoStackAurasDueToAuraHolder for the SPELL_TRACKER
    /// class: only holders whose spell is a tracker, SpellEntry.cpp:153-157). The holder passed in is kept.
    /// </summary>
    internal void RemoveOtherHolders(Unit target, SpellAuraHolder keep, IReadOnlyCollection<AuraType> types)
    {
        if (GetState(target.Guid) is not { } state)
        {
            return;
        }

        foreach (SpellAuraHolder holder in state.Auras.Where(h => !ReferenceEquals(h, keep) && h.Spell.IsTracker && types.Any(h.HasAura)).ToArray())
        {
            RemoveHolder(state, holder);
        }
    }

    /// <summary>SPELLFAMILY_PRIEST (SpellDefines.h:39).</summary>
    private const uint SpellFamilyPriest = 6;

    /// <summary>CF_PRIEST_MIND_VISION (SpellClassMask.h:158).</summary>
    private const int PriestMindVisionFlagBit = 26;

    /// <summary>
    /// vmangos Spell::CheckCast SPELL_AURA_MOD_STALKED (Spell.cpp:6436-6447): the spell needs a unit
    /// target (BAD_IMPLICIT_TARGETS), and one the caster may attack when it names it explicitly
    /// (BAD_TARGETS), as Hunter's Mark does; Mind Vision is exempt.
    /// </summary>
    private SpellCastResult CheckStalkedTarget(Unit caster, SpellInfo spell, Unit? unitTarget)
    {
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.IsEmpty || effect.Effect != SpellEffectName.ApplyAura || effect.AuraType != AuraType.ModStalked)
            {
                continue;
            }

            // vmangos reads only the explicit unit target (m_targets.getUnitTarget()); a self-mask cast has none.
            if (unitTarget is not { } target)
            {
                return SpellCastResult.BadImplicitTargets;
            }

            // Spell.cpp:6444-6447: Priest Mind Vision (CF_PRIEST_MIND_VISION) is exempt from the attackable rule.
            if (effect.TargetA == SpellImplicitTarget.Unit && !Relations.IsHostile(caster, target)
                && !spell.IsFitToFamily(SpellFamilyPriest, PriestMindVisionFlagBit))
            {
                return SpellCastResult.BadTargets;
            }
        }

        return SpellCastResult.CastOk;
    }
}
