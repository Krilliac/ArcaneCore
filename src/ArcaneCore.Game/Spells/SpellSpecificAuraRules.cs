namespace ArcaneCore.Game.Spells;

/// <summary>
/// The spell specific stacking rules of vmangos <c>Unit::RemoveNoStackAurasDueToAuraHolder</c> (Unit.cpp:3355-3573) for every
/// <see cref="SpellSpecific"/>: a hunter has one aspect and one sting per target, a mage one armor, a warlock one curse per target per
/// caster, a shaman one elemental shield, and so on (the kinds and which are per target, per caster or per rank chain are
/// <see cref="SpellSpecifics"/>). The classes of the new holder and of each holder already on the target are compared:
/// <list type="bullet">
/// <item>per target, or per target and the same caster (Unit.cpp:3494-3502): the other holder is removed. A snare or an attack speed
/// reduction that is weaker than the one it meets is refused instead (:3504-3507, Spells::CompareSpellSpecificAuras); a lower rank of the same
/// chain is refused (:3509-3512, "cannot remove higher rank").</item>
/// <item>one rank per target, from another caster (:3525-3546): a rank of the same chain replaces a weaker rank and is refused by a stronger.</item>
/// </list>
/// A refused holder is turned away before it is added (<see cref="SpellSystem.HolderAddRefusals"/>, vmangos returns false before anything is
/// removed), so it applies, sends and removes nothing. Everything else gives way on <see cref="SpellSystem.HolderAdded"/>. Aura load at login
/// adds the saved holders through the same path (<c>SpellSystem.RestoreAuras</c>), so the rules also settle saved auras.
/// LIMITS: the elixir specifics (the <c>spell_elixir</c> table), the database <c>spell_group</c> stack rules, the heal-over-time first-in-chain
/// rule (:3498-3500) and the generic <c>IsNoStackSpellDueToSpell</c> fallback (:3554-3572) are not modelled; passive spells are skipped (vmangos
/// lets them stack with other ranks only, :3366-3371 and :3433-3453); the rank chain is the spell family and name
/// (<see cref="SpellSpecifics.IsSameChain"/>). Only the passive attribute (0x40) is tested here; vmangos also skips SPELL_ATTR_DO_NOT_DISPLAY (0x80),
/// Unit.cpp:3367 and :3434, which is not modelled. Thread affinity: the map thread that owns the target, like every holder event.
/// </summary>
public sealed class SpellSpecificAuraRules
{
    private readonly SpellSystem _spells;

    private SpellSpecificAuraRules(SpellSystem spells) => _spells = spells;

    /// <summary>Subscribe the rules to <paramref name="spells"/>' holder add refusals and events.</summary>
    public static SpellSpecificAuraRules Install(SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        var rules = new SpellSpecificAuraRules(spells);
        spells.HolderAddRefusals.Add(rules.Refuses);
        spells.HolderAdded += rules.OnHolderAdded;
        return rules;
    }

    private bool Refuses(SpellAuraHolder holder)
    {
        List<SpellAuraHolder>? none = null;
        return Evaluate(holder, ref none, collect: false);
    }

    private void OnHolderAdded(SpellAuraHolder holder)
    {
        List<SpellAuraHolder>? losers = null;
        if (Evaluate(holder, ref losers))
        {
            return; // refused holders never get here; a later change of the target's auras is not this rule's to undo
        }

        if (losers is null)
        {
            return;
        }

        foreach (SpellAuraHolder loser in losers)
        {
            _spells.RemoveAuraHolder(loser);
        }
    }

    /// <summary>
    /// Whether <paramref name="holder"/> is refused by a holder it would have to replace. When it is not, the holders it replaces are added to
    /// <paramref name="losers"/> (created on the first one, when <paramref name="collect"/>).
    /// </summary>
    private bool Evaluate(SpellAuraHolder holder, ref List<SpellAuraHolder>? losers, bool collect = true)
    {
        SpellInfo spell = holder.Spell;
        if (spell.IsPassive)
        {
            return false;
        }

        SpellSpecific specific = SpellSpecifics.Of(spell);
        if (specific == SpellSpecific.Normal)
        {
            return false; // no predicate holds for a spell without a specific (SpellEntry.h:109-180 default: false)
        }

        foreach (SpellAuraHolder other in _spells.GetAuras(holder.Target))
        {
            SpellInfo otherSpell = other.Spell;
            if (ReferenceEquals(other, holder) || other.IsRemoved || otherSpell.Id == spell.Id || otherSpell.IsPassive
                || ReferenceEquals(other.AreaParent, holder) || ReferenceEquals(holder.AreaParent, other)
                || Triggers(otherSpell, spell.Id) || Triggers(spell, otherSpell.Id))
            {
                continue; // an aura never removes the aura that triggered it, nor the one it triggers (Unit.cpp:3419-3431)
            }

            SpellSpecific otherSpecific = SpellSpecifics.Of(otherSpell);
            bool sameCaster = holder.CasterGuid == other.CasterGuid;
            bool sameChain = SpellSpecifics.IsSameChain(spell, otherSpell);
            if (SpellSpecifics.IsSinglePerTarget(specific, otherSpecific)
                || (SpellSpecifics.IsSinglePerTargetPerCaster(specific, otherSpecific) && sameCaster))
            {
                if (specific is SpellSpecific.Snare or SpellSpecific.NegativeHaste && !SpellSpecifics.IsAtLeastAsStrong(spell, otherSpell))
                {
                    return true; // cannot remove a stronger snare or haste debuff
                }

                if (sameChain && SpellSpecifics.CompareAuraRanks(spell, otherSpell) < 0)
                {
                    return true; // cannot remove higher rank
                }

                AddLoser(ref losers, other, collect);
                continue;
            }

            if (SpellSpecifics.IsSingleRanksPerTarget(specific, otherSpecific) && !sameCaster && sameChain)
            {
                if (SpellSpecifics.CompareAuraRanks(spell, otherSpell) < 0)
                {
                    return true;
                }

                AddLoser(ref losers, other, collect);
            }
        }

        return false;
    }

    private static void AddLoser(ref List<SpellAuraHolder>? losers, SpellAuraHolder other, bool collect)
    {
        if (collect)
        {
            (losers ??= []).Add(other);
        }
    }

    /// <summary>Whether an effect of <paramref name="spell"/> triggers <paramref name="spellId"/> (vmangos EffectTriggerSpell).</summary>
    private static bool Triggers(SpellInfo spell, uint spellId)
    {
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.TriggerSpell == spellId)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Installs <see cref="SpellSpecificAuraRules"/> on every spell system (discovered <see cref="ISpellHandlerModule"/>).</summary>
public sealed class SpellSpecificAuraRulesModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        SpellSpecificAuraRules.Install(system);
    }
}
