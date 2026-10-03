using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Dispel;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// The auras on <paramref name="target"/> a dispel of <paramref name="miscValue"/> (negative = all types) by
    /// <paramref name="caster"/> may remove, each with the stacks it can lose (vmangos Spell::EffectDispel,
    /// SpellEffects.cpp:2466-2510): the holder's dispel type is in the mask; magic and poison auras are filtered
    /// by the target's friendliness (a friend loses negative ones, an enemy positive ones) unless the dispel
    /// spell is the warlock spellstone; a charm aura on a non-friend is taken regardless of polarity.
    /// Passive auras are never dispelled and dispel type 0 never matches (deliberate, see docs/areas/spell-rules.md).
    /// </summary>
    internal List<(SpellAuraHolder Holder, int Stacks)> DispelCandidates(Unit caster, Unit target, SpellInfo? dispelSpell, int miscValue)
    {
        bool checkFaction = dispelSpell is null || !DispelRules.IgnoresFaction(dispelSpell);
        bool friendly = checkFaction && Relations.IsFriendly(caster, target);
        uint mask = DispelRules.MaskFor(miscValue);
        var candidates = new List<(SpellAuraHolder, int)>();
        foreach (SpellAuraHolder holder in GetAuras(target))
        {
            uint type = holder.Spell.Dispel;
            if (holder.IsRemoved || holder.Spell.IsPassive || type == 0 || type > 31 || (mask & (1u << (int)type)) == 0)
            {
                continue;
            }

            if (checkFaction && DispelRules.UsesPolarity(type))
            {
                bool charm = holder.Spell.HasAura(AuraType.ModCharm) || holder.Spell.HasAura(AuraType.ModPossess);
                if (!friendly && charm)
                {
                    // a charm aura on a non-friend is dispelled whatever its polarity
                }
                else if (holder.IsPositive == friendly)
                {
                    continue;
                }
            }

            candidates.Add((holder, Math.Max((int)holder.StackAmount, 1)));
        }

        return candidates;
    }

    /// <summary>
    /// SPELL_EFFECT_DISPEL (vmangos Spell::EffectDispel, SpellEffects.cpp:2456-2610): Shield Slam only
    /// dispels half of the time; up to the effect value (0 counts as 1) random picks each take ONE stack of a
    /// candidate holder; every pick may fail through the talent dispel-resistance spell mod of the aura's
    /// caster; successes are logged once (SMSG_SPELLDISPELLOG, one entry per spell) and their stacks removed,
    /// failures are reported with SMSG_DISPEL_FAILED.
    /// </summary>
    private void EffectDispel(SpellEffectContext context)
    {
        Unit caster = context.Caster;
        Unit target = context.Target;
        if (DispelRules.IsShieldSlam(context.Spell) && Random.Next(0, 100) >= 50)
        {
            return;
        }

        List<(SpellAuraHolder Holder, int Stacks)> candidates = DispelCandidates(caster, target, context.Spell, context.Effect.MiscValue);
        if (candidates.Count == 0)
        {
            return;
        }

        int count = context.Value == 0 ? 1 : context.Value;
        var succeeded = new List<(SpellAuraHolder Holder, int Stacks)>();
        var failed = new List<uint>();
        for (int pick = 0; pick < count && candidates.Count > 0; pick++)
        {
            int index = Random.Next(0, candidates.Count);
            (SpellAuraHolder holder, int stacks) = candidates[index];
            if (stacks <= 1)
            {
                candidates.RemoveAt(index);
            }
            else
            {
                candidates[index] = (holder, stacks - 1);
            }

            float missChance = 0f;
            if (ResolveAuraCaster(holder) is { } auraCaster)
            {
                missChance = SpellModifiers.Apply(auraCaster, holder.Spell, SpellModOp.ResistDispelChance, missChance);
            }

            if (missChance > 0f && Random.Next(0, 100) < missChance)
            {
                failed.Add(holder.Spell.Id);
                continue;
            }

            int found = succeeded.FindIndex(s => s.Holder.Spell.Id == holder.Spell.Id && s.Holder.CasterGuid == holder.CasterGuid);
            if (found >= 0)
            {
                succeeded[found] = (succeeded[found].Holder, succeeded[found].Stacks + 1);
            }
            else
            {
                succeeded.Add((holder, 1));
            }
        }

        if (succeeded.Count > 0)
        {
            SendToSet(caster, WorldOpcode.SmsgSpelldispellog,
                SpellRulePackets.BuildSpellDispelLog(target.Guid, caster.Guid, [.. succeeded.Select(s => s.Holder.Spell.Id)]), includeSelf: true);
            foreach ((SpellAuraHolder holder, int stacks) in succeeded)
            {
                RemoveStacks(holder, stacks);
            }
        }

        if (failed.Count > 0)
        {
            SendToSet(caster, WorldOpcode.SmsgDispelFailed, SpellRulePackets.BuildDispelFailed(caster.Guid, target.Guid, failed), includeSelf: true);
        }
    }

    /// <summary>
    /// The holders of <paramref name="target"/> a dispel of <paramref name="dispelType"/> by <paramref name="caster"/>
    /// may remove (also what decides NOTHING_TO_DISPEL). A value above 31, such as a negative misc value
    /// cast to unsigned, means every type.
    /// </summary>
    public List<SpellAuraHolder> DispellableAuras(Unit caster, Unit target, uint dispelType)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        int misc = dispelType > 31 ? -1 : (int)dispelType;
        return [.. DispelCandidates(caster, target, null, misc).Select(c => c.Holder)];
    }

    /// <summary>vmangos SpellAuraHolder::ModStackAmount(-n): the holder goes when its stacks run out, otherwise the aura amounts follow the stack count.</summary>
    private void RemoveStacks(SpellAuraHolder holder, int stacks)
    {
        if (holder.IsRemoved || GetState(holder.Target.Guid) is not { } state)
        {
            return;
        }

        if (ModStackAmount(holder, -stacks))
        {
            RemoveHolder(state, holder, AuraRemoveMode.Dispel);
        }
    }
}
