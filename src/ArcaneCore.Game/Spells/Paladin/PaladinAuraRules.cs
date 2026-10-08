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
/// The weaker holder is refused before it is added (<see cref="SpellSystem.HolderAddRefusals"/>), as vmangos does, so a weaker party aura pulsing
/// onto a member who holds a stronger rank applies, sends and removes nothing. The state goes through <see cref="SpellSystem.ModifyAuraState"/>
/// (vmangos Unit::ModifyAuraState, with its side effects when the world installed the aura states).
/// LIMITS: the database <c>spell_group</c> stack rules (Greater Blessing versus Blessing) are not modelled; the rank chain is the spell family and
/// name (<see cref="PaladinSpells.IsSameChain"/>).
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
        spells.HolderAddRefusals.Add(rules.RefusesWeakerRank);
        spells.HolderAdded += rules.OnHolderAdded;
        spells.HolderRemoved += rules.OnHolderRemoved;
        return rules;
    }

    /// <summary>UNIT_FIELD_AURASTATE bit of AURA_STATE_JUDGEMENT (bit state - 1, vmangos Unit::ModifyAuraState).</summary>
    public static uint JudgementStateBit => 1u << ((int)AuraState.Judgement - 1);

    /// <summary>The holders on the target that <paramref name="holder"/> competes with under the spell specific rules.</summary>
    private List<(SpellAuraHolder Other, bool SameChain)> Rivals(SpellAuraHolder holder, PaladinSpellSpecific specific)
    {
        var rivals = new List<(SpellAuraHolder Other, bool SameChain)>();
        foreach (SpellAuraHolder other in _spells.GetAuras(holder.Target))
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

            rivals.Add((other, sameChain));
        }

        return rivals;
    }

    /// <summary>"cannot remove higher rank": vmangos refuses the new holder and removes none of what it collected (aurasToRemove).</summary>
    private bool RefusesWeakerRank(SpellAuraHolder holder)
    {
        PaladinSpellSpecific specific = PaladinSpells.Specific(holder.Spell);
        return specific != PaladinSpellSpecific.None
            && Rivals(holder, specific).Exists(r => r.SameChain && PaladinSpells.CompareAuraRanks(holder.Spell, r.Other.Spell) < 0);
    }

    private void OnHolderAdded(SpellAuraHolder holder)
    {
        PaladinSpellSpecific specific = PaladinSpells.Specific(holder.Spell);
        if (specific == PaladinSpellSpecific.None)
        {
            return;
        }

        // A weaker rank never gets here (RefusesWeakerRank); everything else of the kind gives way.
        foreach ((SpellAuraHolder other, _) in Rivals(holder, specific))
        {
            _spells.RemoveAuraHolder(other);
        }

        if (specific == PaladinSpellSpecific.Seal && !holder.IsRemoved)
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
