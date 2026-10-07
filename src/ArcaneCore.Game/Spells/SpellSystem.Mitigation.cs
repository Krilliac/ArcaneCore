using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Immunity;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// The damage a unit's absorbing auras take away from a hit (vmangos Unit::CalculateDamageAbsorbAndResist,
    /// Unit.cpp:1920-2200, the part after the partial resist, 1.12 order): a school-immune unit absorbs all of it;
    /// otherwise school absorb auras (aura order, each up to its remaining amount, the shield breaks at 0 or when its
    /// last charge drops), then mana shields (limited by the mana that can pay for it), then damage splitting
    /// (flat amounts, then percents) to the living casters of the splitting auras. Returns how much of
    /// <paramref name="damage"/> was absorbed. Melee and ranged white damage call this too (the combat lanes).
    /// </summary>
    /// <param name="attacker">Whoever deals the damage (the split damage is dealt by them to the split target).</param>
    /// <param name="target">The unit taking the damage.</param>
    /// <param name="schoolMask">The damage school mask.</param>
    /// <param name="damage">The damage after armor, crit and partial resist.</param>
    /// <param name="spell">The damaging spell, null for white damage.</param>
    public uint AbsorbDamage(Unit? attacker, Unit target, uint schoolMask, uint damage, SpellInfo? spell) =>
        AbsorbDamage(attacker, target, schoolMask, damage, spell, allowSplit: true);

    private uint AbsorbDamage(Unit? attacker, Unit target, uint schoolMask, uint damage, SpellInfo? spell, bool allowSplit)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (damage == 0 || !target.IsAlive)
        {
            return 0;
        }

        if (ImmunityRules.IsImmuneToSchoolMask(this, target, schoolMask) && (spell is null || !spell.IgnoresImmunities()))
        {
            return damage;
        }

        int remaining = (int)damage;
        AbsorbBySchoolShields(target, schoolMask, ref remaining);
        AbsorbByManaShields(target, schoolMask, ref remaining);
        if (allowSplit)
        {
            SplitDamage(attacker, target, schoolMask, spell, ref remaining);
        }

        return damage - (uint)remaining;
    }

    private void AbsorbBySchoolShields(Unit target, uint schoolMask, ref int remaining)
    {
        List<uint>? broken = null;
        foreach (SpellAuraHolder holder in GetAuras(target).ToArray())
        {
            if (remaining <= 0)
            {
                break;
            }

            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is null || aura.Type != AuraType.SchoolAbsorb || ((uint)aura.MiscValue & schoolMask) == 0 || remaining <= 0)
                {
                    continue;
                }

                int absorb = aura.Amount;
                if (absorb <= 0)
                {
                    (broken ??= []).Add(holder.Spell.Id);
                    continue;
                }

                absorb = Math.Min(absorb, remaining);
                remaining -= absorb;
                aura.Amount -= absorb;

                // vmangos SpellAuraHolder::DropAuraCharge: the last charge dropping breaks the shield.
                if (holder.Charges > 0 && --holder.Charges == 0)
                {
                    aura.Amount = 0;
                }

                if (aura.Amount <= 0)
                {
                    (broken ??= []).Add(holder.Spell.Id);
                }
            }
        }

        if (broken is not null)
        {
            foreach (uint spellId in broken.Distinct())
            {
                RemoveAuras(target, spellId); // vmangos RemoveAurasDueToSpell(.., AURA_REMOVE_BY_SHIELD_BREAK)
            }
        }
    }

    private void AbsorbByManaShields(Unit target, uint schoolMask, ref int remaining)
    {
        foreach (SpellAuraHolder holder in GetAuras(target).ToArray())
        {
            if (remaining <= 0)
            {
                break;
            }

            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is null || aura.Type != AuraType.ManaShield || ((uint)aura.MiscValue & schoolMask) == 0 || remaining <= 0)
                {
                    continue;
                }

                int absorb = Math.Min(remaining, aura.Amount);
                float manaPerDamage = holder.Spell.Effects[aura.EffectIndex].MultipleValue;
                if (manaPerDamage != 0f)
                {
                    manaPerDamage = SpellModifiers.Apply(target, holder.Spell, SpellModOp.MultipleValue, manaPerDamage);
                    int maxAbsorb = SpellRounding.Dither(GetPower(target, PowerType.Mana) / manaPerDamage, Random);
                    absorb = Math.Min(absorb, maxAbsorb);
                    int manaCost = SpellRounding.Dither(absorb * manaPerDamage, Random);
                    SetPower(target, PowerType.Mana, (uint)Math.Max(0, (int)GetPower(target, PowerType.Mana) - manaCost));
                }

                aura.Amount -= absorb;
                remaining -= absorb;
                if (aura.Amount <= 0)
                {
                    RemoveAuras(target, holder.Spell.Id);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// vmangos HandleSplitDamage (Unit.cpp:2086-2177): flat split auras send up to their amount of what remains to
    /// the living caster of the aura (not the target itself, not a dead one), after that caster's own absorbs when it
    /// does not split itself; percent split auras send that share of what remains. The split damage is dealt as damage over time.
    /// </summary>
    private void SplitDamage(Unit? attacker, Unit target, uint schoolMask, SpellInfo? spell, ref int remaining)
    {
        foreach (SpellAuraHolder holder in GetAuras(target).ToArray())
        {
            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is null || holder.IsRemoved || remaining < 0 || ((uint)aura.MiscValue & schoolMask) == 0
                    || aura.Type is not (AuraType.SplitDamageFlat or AuraType.SplitDamagePct)
                    || ResolveAuraCaster(holder) is not { } splitTo || ReferenceEquals(splitTo, target) || !splitTo.IsAlive)
                {
                    continue;
                }

                uint splitted;
                uint splitAbsorbed = 0;
                if (aura.Type == AuraType.SplitDamageFlat)
                {
                    int part = Math.Min(remaining, aura.Amount);
                    remaining -= part;
                    splitted = (uint)Math.Max(part, 0);
                    if (!HasLiveAura(splitTo, AuraType.SplitDamageFlat))
                    {
                        splitAbsorbed = AbsorbDamage(attacker, splitTo, schoolMask, splitted, spell, allowSplit: false);
                        splitted -= splitAbsorbed;
                    }
                }
                else
                {
                    splitted = (uint)(remaining * aura.Amount / 100.0f);
                    remaining -= (int)splitted;
                }

                Unit source = attacker ?? target;
                uint dealt = Damage.DealSpellDamage(source, splitTo, holder.Spell, splitted,
                    periodic: true, startsCombat: true, critical: false, durabilityLoss: false);
                OnDamageTaken(splitTo, source, dealt, periodic: true, splitAbsorbed);
                SendToSet(source, WorldOpcode.SmsgSpellnonmeleedamagelog,
                    SpellPackets.BuildSpellNonMeleeDamageLog(splitTo.Guid, source.Guid, holder.Spell.Id, dealt, SpellSchoolMasks.FirstSchoolIn(schoolMask)), includeSelf: true);
            }
        }
    }
}
