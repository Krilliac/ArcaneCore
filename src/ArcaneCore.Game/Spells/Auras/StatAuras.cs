using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Progression;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The flat stat auras: SPELL_AURA_MOD_STAT (29), MOD_RESISTANCE (22), MOD_ATTACK_POWER (99) and
/// MOD_RANGED_ATTACK_POWER (124), after vmangos <c>Aura::HandleAuraModStat</c> (SpellAuras.cpp:4641),
/// <c>HandleAuraModResistance</c> (:4551), <c>HandleAuraModAttackPower</c> (:5169) and
/// <c>HandleAuraModRangedAttackPower</c> (:5181).
/// <para>
/// vmangos accumulates every contribution in <c>m_auraModifiersGroup[unitMod][TOTAL_VALUE]</c>
/// (Unit::HandleStatModifier, Unit.cpp:7794) and rewrites the update field from the sum. The repo has no
/// such ledger: items and level-ups already write the same fields as deltas (ItemSeams.cs
/// EquipmentStatsApplier, PlayerProgression), so a flat TOTAL_VALUE contribution is the same delta here.
/// The amount that was applied is remembered per aura so removal subtracts exactly that amount.
/// </para>
/// <para>
/// Limits (documented in docs/areas/spells.md): percent modifiers (MOD_PERCENT_STAT, MOD_RESISTANCE_PCT,
/// MOD_BASE_RESISTANCE, MOD_ATTACK_POWER_PCT ...) are other aura types and are not handled, so the flat sum is
/// exact only while none of them is active; the derived values vmangos recomputes besides max health and max
/// mana (armor from agility, attack power from strength and agility, crit and dodge, spell power, mana
/// regeneration) are not recomputed for any source of stats in the repo; the Improved Scorpid Sting stamina
/// reduction (:4650-4677), the Faerie Fire dispel immunities (:4574-4581) and the SPELLMOD_ATTACK_POWER talent
/// modifier need systems that do not exist yet; attack power polarity is the spell's, not per effect.
/// </para>
/// </summary>
public sealed class StatAuras : ISpellHandlerModule
{
    /// <summary>vmangos CLASSMASK_WAND_USERS (SharedDefines.h:112): priest, mage and warlock take no ranged attack power.</summary>
    private static readonly Class[] s_wandUsers = [Class.Priest, Class.Mage, Class.Warlock];

    /// <summary>What an aura contributed when it was applied, so removal subtracts the same amount.</summary>
    private static readonly ConditionalWeakTable<SpellAura, Applied> s_applied = new();

    private sealed record Applied(int Amount, bool Positive);

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterAura(AuraType.ModStat, new AuraHandler(ApplyStat, null));
        system.RegisterAura(AuraType.ModResistance, new AuraHandler(ApplyResistance, null));
        system.RegisterAura(AuraType.ModAttackPower, new AuraHandler((_, h, a, apply) => ApplyAttackPower(h, a, apply, ranged: false), null));
        system.RegisterAura(AuraType.ModRangedAttackPower, new AuraHandler((_, h, a, apply) => ApplyAttackPower(h, a, apply, ranged: true), null));
    }

    /// <summary>Record on apply (returns the amount) or take back on removal (returns the negated amount); null when nothing was applied.</summary>
    private static Applied? Take(SpellAura aura, bool apply, bool positive)
    {
        if (apply)
        {
            var record = new Applied(aura.Amount, positive);
            s_applied.AddOrUpdate(aura, record);
            return record;
        }

        if (!s_applied.TryGetValue(aura, out Applied? previous))
        {
            return null;
        }

        s_applied.Remove(aura);
        return previous;
    }

    // --- SPELL_AURA_MOD_STAT -------------------------------------------------------------------

    /// <summary>
    /// Misc value -1/-2 = all five stats, 0-4 = one stat (strength, agility, stamina, intellect, spirit);
    /// anything else is ignored (vmangos logs and returns). Each stat moves UNIT_FIELD_STATn; a player also
    /// moves PLAYER_FIELD_POSSTATn (amount &gt; 0) or NEGSTATn (Player::ApplyStatBuffMod, Player.h:1506).
    /// Stamina and intellect then move the maximum health and mana by the difference of the stat bonus
    /// (Player::UpdateMaxHealth / UpdateMaxPower, StatSystem.cpp:165-190, bonus curves :134-150; creatures
    /// 10 health per stamina and 15 mana per intellect, :775-806).
    /// </summary>
    private static void ApplyStat(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        int misc = aura.MiscValue;
        if (misc is < -2 or > 4)
        {
            return;
        }

        if (Take(aura, apply, holder.IsPositive) is not { } applied)
        {
            return;
        }

        int delta = apply ? applied.Amount : -applied.Amount;
        Unit target = holder.Target;
        for (int stat = 0; stat < 5; stat++)
        {
            if (misc >= 0 && misc != stat)
            {
                continue;
            }

            int field = UpdateFields.UnitFieldStat0 + stat;
            int before = target.GetInt32(field);
            target.SetInt32(field, before + delta);
            if (target is Player)
            {
                int buffField = (applied.Amount > 0 ? UpdateFields.PlayerFieldPosstat0 : UpdateFields.PlayerFieldNegstat0) + stat;
                target.SetInt32(buffField, target.GetInt32(buffField) + delta);
            }

            if (stat == 2)
            {
                ChangeMaxHealth(target, target is Player
                    ? (int)ExperienceFormulas.HealthBonusFromStamina((uint)Math.Max(0, before + delta))
                        - (int)ExperienceFormulas.HealthBonusFromStamina((uint)Math.Max(0, before))
                    : delta * 10);
            }
            else if (stat == 3)
            {
                if (target is Player player)
                {
                    if (player.PowerType == PowerType.Mana)
                    {
                        ChangeMaxMana(target, (int)ExperienceFormulas.ManaBonusFromIntellect((uint)Math.Max(0, before + delta))
                            - (int)ExperienceFormulas.ManaBonusFromIntellect((uint)Math.Max(0, before)));
                    }
                }
                else
                {
                    ChangeMaxMana(target, delta * 15);
                }
            }
        }
    }

    /// <summary>vmangos Unit::SetMaxHealth: the maximum is at least 1 and drags the current health down with it.</summary>
    private static void ChangeMaxHealth(Unit target, int delta)
    {
        if (delta == 0)
        {
            return;
        }

        uint max = (uint)Math.Max(1L, (long)target.MaxHealth + delta);
        target.MaxHealth = max;
        if (target.Health > max)
        {
            target.Health = max;
        }
    }

    /// <summary>vmangos Unit::SetMaxPower (mana): never negative, and the current mana follows it down.</summary>
    private static void ChangeMaxMana(Unit target, int delta)
    {
        if (delta == 0)
        {
            return;
        }

        uint max = (uint)Math.Max(0L, (long)target.GetUInt32(UpdateFields.UnitFieldMaxpower1) + delta);
        target.SetUInt32(UpdateFields.UnitFieldMaxpower1, max);
        if (target.GetUInt32(UpdateFields.UnitFieldPower1) > max)
        {
            target.SetUInt32(UpdateFields.UnitFieldPower1, max);
        }
    }

    // --- SPELL_AURA_MOD_RESISTANCE -------------------------------------------------------------

    /// <summary>
    /// The misc value is a school MASK (bit 0 = physical, which is armor). Each school moves
    /// UNIT_FIELD_RESISTANCES + school, except holy, which has no resistance in 1.12 (Player::UpdateResistances,
    /// StatSystem.cpp:117-126 writes 0); a player also moves the UI buff fields
    /// PLAYER_FIELD_RESISTANCEBUFFMODSPOSITIVE / NEGATIVE (Player::ApplyResistanceBuffModsMod, Player.h:1516) by
    /// the same change. A zero amount does nothing.
    /// </summary>
    private static void ApplyResistance(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (aura.Amount == 0 || Take(aura, apply, holder.IsPositive) is not { } applied)
        {
            return;
        }

        int delta = apply ? applied.Amount : -applied.Amount;
        Unit target = holder.Target;
        for (int school = 0; school < 7; school++)
        {
            if (((uint)aura.MiscValue & (1u << school)) == 0)
            {
                continue;
            }

            if (school != (int)SpellSchool.Holy)
            {
                int field = UpdateFields.UnitFieldResistances + school;
                target.SetInt32(field, target.GetInt32(field) + delta);
            }

            if (target is Player)
            {
                int buffField = (applied.Amount > 0
                    ? UpdateFields.PlayerFieldResistancebuffmodspositive
                    : UpdateFields.PlayerFieldResistancebuffmodsnegative) + school;
                target.SetInt32(buffField, target.GetInt32(buffField) + delta);
            }
        }
    }

    // --- SPELL_AURA_MOD_ATTACK_POWER / MOD_RANGED_ATTACK_POWER -----------------------------------

    /// <summary>
    /// Unit::HandleAttackPowerModifier (Unit.cpp:7745): the positive flat mods and the negative flat mods live
    /// in the two int16 halves of UNIT_FIELD_(RANGED_)ATTACK_POWER_MODS (low = positive, high = negative; see
    /// Unit::GetTotalAttackPowerValue, Unit.cpp:8037). Which half is the aura's polarity (Aura::IsPositive).
    /// Priests, mages and warlocks ignore ranged attack power (SpellAuras.cpp:5183).
    /// </summary>
    private static void ApplyAttackPower(SpellAuraHolder holder, SpellAura aura, bool apply, bool ranged)
    {
        Unit target = holder.Target;
        if (ranged && Array.IndexOf(s_wandUsers, target.Class) >= 0)
        {
            return;
        }

        if (Take(aura, apply, holder.IsPositive) is not { } applied)
        {
            return;
        }

        int delta = apply ? applied.Amount : -applied.Amount;
        int field = ranged ? UpdateFields.UnitFieldRangedAttackPowerMods : UpdateFields.UnitFieldAttackPowerMods;
        uint packed = target.GetUInt32(field);
        short positive = unchecked((short)(packed & 0xFFFF));
        short negative = unchecked((short)(packed >> 16));
        if (applied.Positive)
        {
            positive = unchecked((short)(positive + delta));
        }
        else
        {
            negative = unchecked((short)(negative + delta));
        }

        target.SetUInt32(field, (ushort)positive | ((uint)(ushort)negative << 16));
    }
}
