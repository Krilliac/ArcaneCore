using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Paladin;

/// <summary>
/// The paladin aura bookkeeping of vmangos that the generic aura engine does not do:
/// <list type="bullet">
/// <item>AURA_STATE_JUDGEMENT (SpellAuras.cpp:6815-6817 and :6866-6893): a seal on a unit sets it (Judgement, 20271, needs it on its caster:
/// CasterAuraState 5); the last seal leaving clears it.</item>
/// <item>The spell specific rules of Unit::RemoveNoStackAurasDueToAuraHolder (Unit.cpp:3355-3560) for the paladin specifics: a seal replaces
/// any other seal on the unit (IsSingleFromSpellSpecificPerTarget); a blessing, aura or judgement replaces the same caster's other one of the same
/// kind (IsSingleFromSpellSpecificPerTargetPerCaster); from another caster, a rank of the same chain replaces a weaker rank
/// (IsSingleFromSpellSpecificSpellRanksPerTarget). A weaker rank never replaces a stronger one: the new holder goes instead
/// (Spells::CompareAuraRanks).</item>
/// </list>
/// LIMITS: vmangos refuses the weaker holder before adding it; here it is added and taken off again in the same call. The database
/// <c>spell_group</c> stack rules (Greater Blessing versus Blessing) are not modelled; the rank chain is the spell family and name
/// (<see cref="PaladinSpells.IsSameChain"/>).
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
        PaladinSpellSpecific specific = PaladinSpells.Specific(holder.Spell);
        if (specific == PaladinSpellSpecific.None)
        {
            return;
        }

        Unit target = holder.Target;
        var replaced = new List<SpellAuraHolder>();
        foreach (SpellAuraHolder other in _spells.GetAuras(target))
        {
            if (ReferenceEquals(other, holder) || other.IsRemoved || other.Spell.Id == holder.Spell.Id
                || PaladinSpells.Specific(other.Spell) != specific || ReferenceEquals(other.AreaParent, holder) || ReferenceEquals(holder.AreaParent, other))
            {
                continue;
            }

            bool sameChain = PaladinSpells.IsSameChain(holder.Spell, other.Spell);
            if (specific != PaladinSpellSpecific.Seal && other.CasterGuid != holder.CasterGuid && !sameChain)
            {
                continue;
            }

            // "cannot remove higher rank": vmangos returns before it removes anything it collected (aurasToRemove).
            if (sameChain && PaladinSpells.CompareAuraRanks(holder.Spell, other.Spell) < 0)
            {
                _spells.RemoveAuraHolder(holder);
                return;
            }

            replaced.Add(other);
        }

        foreach (SpellAuraHolder other in replaced)
        {
            _spells.RemoveAuraHolder(other);
        }

        if (specific == PaladinSpellSpecific.Seal && !holder.IsRemoved)
        {
            target.SetUInt32(UpdateFields.UnitFieldAurastate, target.GetUInt32(UpdateFields.UnitFieldAurastate) | JudgementStateBit);
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
            target.SetUInt32(UpdateFields.UnitFieldAurastate, target.GetUInt32(UpdateFields.UnitFieldAurastate) & ~JudgementStateBit);
        }
    }
}
