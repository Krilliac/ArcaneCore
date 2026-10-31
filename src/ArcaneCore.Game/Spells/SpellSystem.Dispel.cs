using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Reputation;
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
        => DispelCandidates(caster, target, dispelSpell, miscValue, out _);

    /// <inheritdoc cref="DispelCandidates(Unit, Unit, SpellInfo?, int)"/>
    /// <param name="priorityIndex">
    /// The index of the charm candidate the first pick must take, or -1 (vmangos priorityDispel, SpellEffects.cpp:2492-2499): the target is not
    /// friendly, the aura is a charm and the charmed unit's original faction template is friendly to the caster's template. The last such
    /// charm aura wins.
    /// </param>
    internal List<(SpellAuraHolder Holder, int Stacks)> DispelCandidates(Unit caster, Unit target, SpellInfo? dispelSpell, int miscValue, out int priorityIndex)
    {
        priorityIndex = -1;
        bool checkFaction = dispelSpell is null || !DispelRules.IgnoresFaction(dispelSpell);
        bool friendly = checkFaction && Relations.IsFriendly(caster, target);
        uint mask = DispelRules.MaskFor(miscValue);
        uint maxLevel = MaxDispelSpellLevel(dispelSpell);
        var candidates = new List<(SpellAuraHolder, int)>();
        foreach (SpellAuraHolder holder in GetAuras(target))
        {
            uint type = holder.Spell.Dispel;
            if (holder.IsRemoved || holder.Spell.IsPassive || type == 0 || type > 31 || (mask & (1u << (int)type)) == 0)
            {
                continue;
            }

            if (maxLevel > 0 && holder.Spell.SpellLevel > maxLevel)
            {
                continue;
            }

            if (checkFaction && DispelRules.UsesPolarity(type))
            {
                bool charm = holder.Spell.HasAura(AuraType.ModCharm) || holder.Spell.HasAura(AuraType.ModPossess);
                if (!friendly && charm)
                {
                    // a charm aura on a non-friend is dispelled whatever its polarity, and first when the charmed unit is a friend by origin
                    if (IsOriginalFactionFriendly(caster, target))
                    {
                        priorityIndex = candidates.Count;
                    }
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
    /// vmangos <c>charm->GetOriginalFactionTemplate()->IsFriendlyTo(*caster faction)</c>: a charmed unit's own template (a creature's template
    /// faction, a player's race faction) is friendly to the caster's current template. False without a charm info or a faction catalog.
    /// </summary>
    private static bool IsOriginalFactionFriendly(Unit caster, Unit target)
    {
        if (UnitControl.Find(target)?.Charm is null)
        {
            return false;
        }

        CombatHooks? hooks = caster.Map?.FindUpdater<MapCombat>()?.Hooks;
        while (hooks is ReputationCombatHooks wrapped)
        {
            hooks = wrapped.Fallback;
        }

        if (hooks is not FactionCombatHooks factions)
        {
            return false;
        }

        uint original = target switch
        {
            Creature creature => creature.Template.Faction,
            Player player => player.RaceFactionTemplate,
            _ => 0,
        };
        return factions.Factions.Find(original) is { } own && factions.Factions.Find(caster.FactionTemplate) is { } other && own.IsFriendlyTo(other);
    }

    /// <summary>vmangos Spell::CheckCast (Spell.cpp:6256-6273): maximum poison spellLevel dispellable by Anti-Venom (7932), Strong Anti-Venom (7933) and Powerful Anti-Venom (23786).</summary>
    internal static uint MaxDispelSpellLevel(SpellInfo? dispelSpell) => dispelSpell?.Id switch
    {
        7932 => 25,
        7933 => 35,
        23786 => 60,
        _ => 0,
    };

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

        List<(SpellAuraHolder Holder, int Stacks)> candidates = DispelCandidates(caster, target, context.Spell, context.Effect.MiscValue, out int priorityIndex);
        if (candidates.Count == 0)
        {
            return;
        }

        int count = context.Value == 0 ? 1 : context.Value;
        var succeeded = new List<(SpellAuraHolder Holder, int Stacks)>();
        var failed = new List<uint>();
        for (int pick = 0; pick < count && candidates.Count > 0; pick++)
        {
            // The first pick takes the priority charm without an RNG draw, every other pick draws as before (SpellEffects.cpp:2519-2523).
            int index = priorityIndex >= 0 ? priorityIndex : Random.Next(0, candidates.Count);
            priorityIndex = -1;
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
