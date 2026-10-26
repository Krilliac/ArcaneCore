using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Paladin;

/// <summary>
/// The paladin aura bookkeeping of vmangos that the generic aura engine does not do: AURA_STATE_JUDGEMENT (SpellAuras.cpp:6815-6817 and
/// :6866-6893). A seal on a unit sets it (Judgement, 20271, needs it on its caster: CasterAuraState 5); the last seal leaving clears it. The
/// state goes through <see cref="SpellSystem.ModifyAuraState"/> (vmangos Unit::ModifyAuraState, with its side effects when the world installed the
/// aura states). The stacking rules of the paladin spell specifics (seal, blessing, aura, judgement) are the general
/// <see cref="SpellSpecificAuraRules"/>, shared with every other class.
/// </summary>
public sealed class PaladinAuraRules
{
    private readonly SpellSystem _spells;

    private PaladinAuraRules(SpellSystem spells) => _spells = spells;

    /// <summary>Subscribe the rules to <paramref name="spells"/>' holder events.</summary>
    public static PaladinAuraRules Install(SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        var rules = new PaladinAuraRules(spells);
        spells.HolderAdded += rules.OnHolderAdded;
        spells.HolderRemoved += rules.OnHolderRemoved;
        return rules;
    }

    /// <summary>UNIT_FIELD_AURASTATE bit of AURA_STATE_JUDGEMENT (bit state - 1, vmangos Unit::ModifyAuraState).</summary>
    public static uint JudgementStateBit => 1u << ((int)AuraState.Judgement - 1);

    private void OnHolderAdded(SpellAuraHolder holder)
    {
        if (PaladinSpells.IsSeal(holder.Spell) && !holder.IsRemoved)
        {
            _spells.ModifyAuraState(holder.Target, AuraState.Judgement, true);
        }
    }

    private void OnHolderRemoved(SpellAuraHolder holder)
    {
        if (!PaladinSpells.IsSeal(holder.Spell))
        {
            return;
        }

        Unit target = holder.Target;
        if (!_spells.GetAuras(target).Any(h => !h.IsRemoved && !ReferenceEquals(h, holder) && PaladinSpells.IsSeal(h.Spell)))
        {
            _spells.ModifyAuraState(target, AuraState.Judgement, false);
        }
    }
}
