using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Casters.Dispel;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_EFFECT_DISPEL as vmangos implements it (Spell::EffectDispel, SpellEffects.cpp:2456-2600): the dispel type comes
/// from EffectMiscValue (negative = every type), the friendly / hostile polarity filter applies only to Magic and Poison
/// auras, the effect value is the number of removals (at least one) and each stack of an aura is one removal, an aura can
/// resist the dispel (SPELLMOD_RESIST_DISPEL_CHANCE), and the outcome is sent as SMSG_SPELLDISPELLOG / SMSG_DISPEL_FAILED.
/// <para>
/// Not modelled (documented limits): the Shield Slam 50 percent gate (warrior lane), the Warlock Spellstone that dispels
/// both polarities (needs the spell family fields), charm auras on a charmed friendly target being removed first, and
/// reflected dispels.
/// </para>
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>vmangos DISPEL_ALL_MASK (SpellDefines.h:732): magic, curse, disease and poison.</summary>
    private const uint DispelAllMask = (1u << 1) | (1u << 2) | (1u << 3) | (1u << 4);

    private const int DispelMagic = 1;
    private const int DispelPoison = 4;

    /// <summary>
    /// The chance in percent (0-100) that an aura resists a dispel, for the aura's caster and the aura's spell
    /// (vmangos SPELLMOD_RESIST_DISPEL_CHANCE, a talent spell mod; the base chance is 0). Null means no spell mods.
    /// </summary>
    public Func<Unit?, SpellInfo, int>? DispelResistChance { get; set; }

    /// <summary>Auras on <paramref name="target"/> that a dispel of <paramref name="dispelType"/> by <paramref name="caster"/> may remove (negative type = all).</summary>
    public List<SpellAuraHolder> DispellableAuras(Unit caster, Unit target, uint dispelType)
        => DispellableAuras(caster, target, unchecked((int)dispelType));

    /// <summary>The candidate list of vmangos Spell::EffectDispel for a dispel effect with EffectMiscValue <paramref name="dispelType"/>.</summary>
    public List<SpellAuraHolder> DispellableAuras(Unit caster, Unit target, int dispelType)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        uint mask = dispelType < 0 ? DispelAllMask : dispelType < 32 ? 1u << dispelType : 0;
        bool friendly = Relations.IsFriendly(caster, target);
        List<SpellAuraHolder> candidates = [];
        foreach (SpellAuraHolder holder in GetAuras(target))
        {
            if (holder.IsRemoved || holder.Spell.Dispel >= 32 || ((1u << (int)holder.Spell.Dispel) & mask) == 0)
            {
                continue;
            }

            // Only magic and poison respect the polarity: a friendly target loses harmful auras, an enemy loses beneficial ones.
            if ((int)holder.Spell.Dispel is DispelMagic or DispelPoison && holder.IsPositive == friendly)
            {
                continue;
            }

            candidates.Add(holder);
        }

        return candidates;
    }

    private void EffectDispel(SpellEffectContext context)
    {
        Unit caster = context.Caster;
        Unit target = context.Target;
        List<(SpellAuraHolder Holder, int StacksLeft)> pool = [.. DispellableAuras(caster, target, context.Effect.MiscValue).Select(h => (h, (int)h.StackAmount))];
        if (pool.Count == 0)
        {
            return;
        }

        // Some spells have effect value 0 and mean one removal.
        int removals = context.Value == 0 ? 1 : context.Value;
        List<(SpellAuraHolder Holder, int Count)> succeeded = [];
        List<uint> failed = [];
        for (int i = 0; i < removals && pool.Count > 0; i++)
        {
            int index = Random.Next(pool.Count);
            (SpellAuraHolder holder, int stacksLeft) = pool[index];
            if (--stacksLeft == 0)
            {
                pool.RemoveAt(index);
            }
            else
            {
                pool[index] = (holder, stacksLeft);
            }

            int resistChance = DispelResistChance?.Invoke(holder.CasterOwner.Caster, holder.Spell) ?? 0;
            if (resistChance > 0 && Random.Next(100) < resistChance)
            {
                failed.Add(holder.Spell.Id);
                continue;
            }

            int found = succeeded.FindIndex(s => s.Holder.Spell.Id == holder.Spell.Id && s.Holder.CasterGuid == holder.CasterGuid);
            if (found >= 0)
            {
                succeeded[found] = (succeeded[found].Holder, succeeded[found].Count + 1);
            }
            else
            {
                succeeded.Add((holder, 1));
            }
        }

        if (succeeded.Count > 0)
        {
            foreach ((SpellAuraHolder holder, int count) in succeeded)
            {
                RemoveStacks(holder, count);
            }

            SendToSet(caster, WorldOpcode.SmsgSpelldispellog,
                DispelPackets.BuildDispelLog(target.Guid, caster.Guid, [.. succeeded.Select(s => s.Holder.Spell.Id)]), includeSelf: true);
        }

        if (failed.Count > 0)
        {
            SendToSet(caster, WorldOpcode.SmsgDispelFailed, DispelPackets.BuildDispelFailed(caster.Guid, target.Guid, failed), includeSelf: true);
        }
    }

    /// <summary>vmangos Unit::RemoveAuraHolderFromStack: lose <paramref name="count"/> stacks, the holder goes when none are left.</summary>
    private void RemoveStacks(SpellAuraHolder holder, int count)
    {
        if (holder.IsRemoved || GetState(holder.Target.Guid) is not { } state)
        {
            return;
        }

        int left = holder.StackAmount - count;
        if (left <= 0)
        {
            RemoveHolder(state, holder);
            return;
        }

        int previous = Math.Max((int)holder.StackAmount, 1);
        foreach (SpellAura? aura in holder.Auras)
        {
            if (aura is not null)
            {
                aura.Amount = aura.Amount / previous * left;
            }
        }

        holder.StackAmount = (byte)left;
        WriteAuraApplications(holder);
    }
}
