namespace ArcaneCore.Game.Spells;

/// <summary>
/// The spell specific stacking rules of vmangos <c>Unit::RemoveNoStackAurasDueToAuraHolder</c> (Unit.cpp:3355-3573) for every
/// <see cref="SpellSpecific"/>: a hunter has one aspect and one sting per target, a mage one armor, a warlock one curse per target per
/// caster, a shaman one elemental shield, and so on (the kinds and which are per target, per caster or per rank chain are
/// <see cref="SpellSpecifics"/>). The classes of the new holder and of each holder already on the target are compared:
/// <list type="bullet">
/// <item>per target, or per target and the same caster (Unit.cpp:3494-3502): the other holder is removed. A snare or an attack speed
/// reduction that is weaker than the one it meets is refused instead (:3504-3507, Spells::CompareSpellSpecificAuras); a lower rank of the same
/// chain is refused (:3509-3512, "cannot remove higher rank"). Two heals over time of one chain are per target too, whatever their casters
/// (:3373-3401 and :3498-3500).</item>
/// <item>one rank per target, from another caster (:3525-3546): a rank of the same chain replaces a weaker rank and is refused by a stronger.</item>
/// <item>a passive or hidden (SPELL_ATTR_DO_NOT_DISPLAY) holder already on the target (:3433-3453) only meets a holder of the same caster, or
/// when either is a party area aura, and then only a rank of its own chain: the weaker new rank is refused, a stronger one goes on to the
/// checks above. A passive or hidden new spell without an aura effect stacks with everything (:3366-3371, SpellMgr.cpp:3184-3195).</item>
/// </list>
/// A refused holder is turned away before it is added (<see cref="SpellSystem.HolderAddRefusals"/>, vmangos returns false before anything is
/// removed), so it applies, sends and removes nothing. Everything else gives way before the new holder takes a slot
/// (<see cref="SpellSystem.HolderAddDisplacements"/>): vmangos removes it at :3575-3576 and checks the visible slot limit only later
/// (:3282-3305), so a replaced debuff frees its own slot at the 16 debuff cap. Aura load at login adds the saved holders through the same path
/// (<c>SpellSystem.RestoreAuras</c>), so the rules also settle saved auras.
/// LIMITS: the elixir specifics (the <c>spell_elixir</c> table), the database <c>spell_group</c> stack rules and the generic
/// <c>IsNoStackSpellDueToSpell</c> fallback (:3554-3572) are not modelled; the rank chain is the spell family and name
/// (<see cref="SpellSpecifics.IsSameChain"/>), so a generic family heal over time is in no chain. Thread affinity: the map thread that owns the
/// target, like every holder event.
/// </summary>
public sealed class SpellSpecificAuraRules
{
    private readonly SpellSystem _spells;

    private SpellSpecificAuraRules(SpellSystem spells) => _spells = spells;

    /// <summary>Subscribe the rules to <paramref name="spells"/>' holder add refusals and displacements.</summary>
    public static SpellSpecificAuraRules Install(SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        var rules = new SpellSpecificAuraRules(spells);
        spells.HolderAddRefusals.Add(rules.Refuses);
        spells.HolderAddDisplacements.Add(rules.Displace);
        return rules;
    }

    private bool Refuses(SpellAuraHolder holder)
    {
        List<SpellAuraHolder>? none = null;
        return Evaluate(holder, ref none, collect: false);
    }

    /// <summary>Remove the holders <paramref name="holder"/> replaces, before it is given a slot (vmangos Unit.cpp:3575-3576).</summary>
    private void Displace(SpellAuraHolder holder)
    {
        List<SpellAuraHolder>? losers = null;
        if (Evaluate(holder, ref losers) || losers is null)
        {
            return; // a refused holder was turned away by Refuses before this runs
        }

        foreach (SpellAuraHolder loser in losers)
        {
            if (!loser.IsRemoved)
            {
                _spells.RemoveAuraHolder(loser); // an earlier loser's removal can take a later one with it
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="holder"/> is refused by a holder it would have to replace. When it is not, the holders it replaces are added to
    /// <paramref name="losers"/> (created on the first one, when <paramref name="collect"/>).
    /// </summary>
    private bool Evaluate(SpellAuraHolder holder, ref List<SpellAuraHolder>? losers, bool collect = true)
    {
        SpellInfo spell = holder.Spell;
        if (IsPassiveOrHidden(spell) && IsPassiveStackableWithRanks(spell))
        {
            return false; // Unit.cpp:3366-3371: a passive spell without an aura effect stacks with its own ranks
        }

        SpellSpecific specific = SpellSpecifics.Of(spell);
        bool healOverTime = IsHealOverTime(spell);
        bool newIsAreaAura = HasEffect(spell, SpellEffectName.ApplyAreaAuraParty); // Unit.cpp:3405
        foreach (SpellAuraHolder other in _spells.GetAuras(holder.Target))
        {
            SpellInfo otherSpell = other.Spell;
            if (ReferenceEquals(other, holder) || other.IsRemoved || otherSpell.Id == spell.Id
                || ReferenceEquals(other.AreaParent, holder) || ReferenceEquals(holder.AreaParent, other)
                || Triggers(otherSpell, spell.Id) || Triggers(spell, otherSpell.Id))
            {
                continue; // an aura never removes the aura that triggered it, nor the one it triggers (Unit.cpp:3419-3431)
            }

            bool sameCaster = holder.CasterGuid == other.CasterGuid;
            bool sameChain = SpellSpecifics.IsSameChain(spell, otherSpell);
            if (IsPassiveOrHidden(otherSpell))
            {
                // Unit.cpp:3433-3453: a passive or hidden holder only meets the same caster (or a party area aura on either side), and then only
                // its own rank chain; the weaker new rank is refused, a stronger one goes on to the checks below.
                if (!sameCaster && !newIsAreaAura && !HasEffect(otherSpell, SpellEffectName.ApplyAreaAuraParty))
                {
                    continue;
                }

                if (!sameChain)
                {
                    continue;
                }

                if (SpellSpecifics.CompareAuraRanks(spell, otherSpell) < 0)
                {
                    return true;
                }
            }

            SpellSpecific otherSpecific = SpellSpecifics.Of(otherSpell);

            // Unit.cpp:3498-3500: heals over time of one chain are single per target, whatever the caster.
            bool perTarget = SpellSpecifics.IsSinglePerTarget(specific, otherSpecific) || (healOverTime && sameChain);
            if (perTarget || (SpellSpecifics.IsSinglePerTargetPerCaster(specific, otherSpecific) && sameCaster))
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

    /// <summary>SPELL_ATTR_PASSIVE or SPELL_ATTR_DO_NOT_DISPLAY (vmangos Unit.cpp:3367 and :3434).</summary>
    private static bool IsPassiveOrHidden(SpellInfo spell) => spell.HasAttribute(SpellAttributes.Passive | SpellAttributes.DoNotDisplay);

    /// <summary>
    /// vmangos SpellInternal::IsPassiveSpellStackableWithRanks (SpellMgr.cpp:3184-3195): a passive spell with no APPLY_AURA and no
    /// APPLY_AREA_AURA_PARTY effect.
    /// </summary>
    private static bool IsPassiveStackableWithRanks(SpellInfo spell)
        => spell.IsPassive && !HasEffect(spell, SpellEffectName.ApplyAura) && !HasEffect(spell, SpellEffectName.ApplyAreaAuraParty);

    /// <summary>
    /// Whether the spell puts a PERIODIC_HEAL or OBS_MOD_HEALTH aura on its target (vmangos Unit.cpp:3373-3401 reads the holder's auras; a
    /// holder's auras are its spell's aura effects).
    /// </summary>
    private static bool IsHealOverTime(SpellInfo spell)
    {
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.Effect != SpellEffectName.None && effect.AuraType is AuraType.PeriodicHeal or AuraType.ObsModHealth)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasEffect(SpellInfo spell, SpellEffectName name)
    {
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.Effect == name)
            {
                return true;
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
