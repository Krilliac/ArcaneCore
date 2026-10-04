using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Stats;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The percent stat auras and the flat ones the flat sum was missing, after mangos zero <c>Aura::HandleModPercentStat</c>,
/// <c>HandleModTotalPercentStat</c>, <c>HandleAuraModIncreaseHealth</c> / <c>Percent</c>, <c>HandleAuraModIncreaseEnergy</c> / <c>Percent</c>,
/// <c>HandleModBaseResistance</c>, <c>HandleAuraModBaseResistancePCT</c>, <c>HandleModResistancePercent</c>,
/// <c>HandleAuraModAttackPowerPercent</c>, <c>HandleAuraModRangedAttackPowerPercent</c>, <c>HandleAuraModParryPercent</c> /
/// <c>DodgePercent</c> / <c>BlockPercent</c> and <c>HandleShieldBlockValue</c> (SpellAuraPeriodic.cpp, SpellAuras.cpp:1454-1530):
/// MOD_PERCENT_STAT, MOD_TOTAL_STAT_PERCENTAGE, MOD_INCREASE_HEALTH, MOD_INCREASE_HEALTH_PERCENT, MOD_INCREASE_ENERGY,
/// MOD_INCREASE_ENERGY_PERCENT, MOD_BASE_RESISTANCE, MOD_BASE_RESISTANCE_PCT, MOD_RESISTANCE_PCT, MOD_ATTACK_POWER_PCT,
/// MOD_RANGED_ATTACK_POWER_PCT, MOD_PARRY_PERCENT, MOD_DODGE_PERCENT, MOD_BLOCK_PERCENT, MOD_SHIELD_BLOCKVALUE and
/// MOD_SHIELD_BLOCKVALUE_PCT.
/// <para>
/// State and threads. Every handler runs on the world thread. A percent contribution is multiplied into the target's
/// <see cref="UnitModLedger"/> (a player's lives on <see cref="PlayerStatState.Mods"/>, any other unit's is created by its first
/// percent aura, so no allocation happens on a unit that never gets one) and the group's field is then recomputed from the flat sum
/// that items, level-ups and the flat auras of <see cref="StatAuras"/> keep in it: <c>field = flat + Applied</c> and
/// <c>Applied</c> is rederived on every refresh, which is the reference's <c>((BASE * BASE_PCT) + TOTAL_VALUE) * TOTAL_PCT</c> order, flat
/// first. Nothing here runs per tick; the cost is one aura apply or removal, or one <see cref="PlayerStatSystem.UpdateAll"/> (equipment
/// and level changes). The amount an aura applied is remembered per aura so a removal takes back the same amount.
/// </para>
/// <para>
/// Groups. Stats (BASE_PCT for players only, TOTAL_PCT for every unit), maximum health and the unit's own power type (TOTAL_PCT), armor
/// and the five resistances (BASE_PCT for players: the base is the worn items' resistance, as items are BASE_VALUE in the reference;
/// TOTAL_PCT for every unit) and the two attack power groups (TOTAL_PCT, written to the multiplier field as <c>TotalPct - 1</c>,
/// StatSystem.cpp:786-790). The stat base of BASE_PCT is the stat without the player's buff counters (items and flat auras write
/// PLAYER_FIELD_POSSTAT / NEGSTAT with their stat deltas), i.e. the level base value.
/// </para>
/// <para>
/// Limits (docs/areas/stats.md): the buff counters PLAYER_FIELD_POS/NEGSTAT and the resistance buff mods are not scaled by the percent
/// auras (the reference does for the client UI, Player::ApplyStatPercentBuffMod, ApplyResistanceBuffModsPercentMod), pets get no base armor
/// modifiers, the SPELLMOD_ATTACK_POWER caster modifier of the attack power percent auras does not exist, and the dependent mana
/// regeneration and spell power are not recomputed.
/// </para>
/// </summary>
public sealed class PercentStatAuras : ISpellHandlerModule
{
    /// <summary>mangos CLASSMASK_WAND_USERS: priest, mage and warlock take no ranged attack power.</summary>
    private static readonly Class[] s_wandUsers = [Class.Priest, Class.Mage, Class.Warlock];

    /// <summary>Warrior Last Stand's triggered spell (HandleAuraModIncreaseHealth: the amount is also added to the current health).</summary>
    private const uint LastStandTriggered = 12976;

    /// <summary>Bear Form (Passive): the health percentage is kept across the change.</summary>
    private const uint BearFormPassive = 1178;

    /// <summary>Dire Bear Form (Passive): as <see cref="BearFormPassive"/>.</summary>
    private const uint DireBearFormPassive = 9635;

    private static readonly ConditionalWeakTable<SpellAura, Applied> s_applied = new();

    /// <summary>What an aura contributed on apply: its amount and, for the pool auras, the group it went to (a druid's power type can change later).</summary>
    private sealed record Applied(int Amount, UnitMods Group);

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterAura(AuraType.ModPercentStat, new AuraHandler((_, h, a, apply) => ApplyStatPercent(h, a, apply, UnitModifierType.BasePct), null));
        system.RegisterAura(AuraType.ModTotalStatPercentage, new AuraHandler((_, h, a, apply) => ApplyStatPercent(h, a, apply, UnitModifierType.TotalPct), null));
        system.RegisterAura(AuraType.ModIncreaseHealth, new AuraHandler((_, h, a, apply) => ApplyIncreaseHealth(h, a, apply), null));
        system.RegisterAura(AuraType.ModIncreaseHealthPercent, new AuraHandler((_, h, a, apply) => ApplyPoolPercent(h, a, apply, health: true), null));
        system.RegisterAura(AuraType.ModIncreaseEnergy, new AuraHandler((_, h, a, apply) => ApplyIncreaseEnergy(h, a, apply), null));
        system.RegisterAura(AuraType.ModIncreaseEnergyPercent, new AuraHandler((_, h, a, apply) => ApplyPoolPercent(h, a, apply, health: false), null));
        system.RegisterAura(AuraType.ModBaseResistance, new AuraHandler((_, h, a, apply) => ApplyBaseResistance(h, a, apply), null));
        system.RegisterAura(AuraType.ModBaseResistancePct, new AuraHandler((_, h, a, apply) => ApplyResistancePercent(h, a, apply, UnitModifierType.BasePct), null));
        system.RegisterAura(AuraType.ModResistancePct, new AuraHandler((_, h, a, apply) => ApplyResistancePercent(h, a, apply, UnitModifierType.TotalPct), null));
        system.RegisterAura(AuraType.ModAttackPowerPct, new AuraHandler((_, h, a, apply) => ApplyAttackPowerPercent(h, a, apply, ranged: false), null));
        system.RegisterAura(AuraType.ModRangedAttackPowerPct, new AuraHandler((_, h, a, apply) => ApplyAttackPowerPercent(h, a, apply, ranged: true), null));
        system.RegisterAura(AuraType.ModDodgePercent, new AuraHandler((_, h, a, apply) => ApplyDefense(h, a, apply, static (s, d) => s.AddDodgeBonus(d)), null));
        system.RegisterAura(AuraType.ModParryPercent, new AuraHandler((_, h, a, apply) => ApplyDefense(h, a, apply, static (s, d) => s.AddParryBonus(d)), null));
        system.RegisterAura(AuraType.ModBlockPercent, new AuraHandler((_, h, a, apply) => ApplyDefense(h, a, apply, static (s, d) => s.AddBlockBonus(d)), null));
        system.RegisterAura(AuraType.ModShieldBlockvalue, new AuraHandler((_, h, a, apply) => ApplyShieldBlock(h, a, apply, percent: false), null));
        system.RegisterAura(AuraType.ModShieldBlockvaluePct, new AuraHandler((_, h, a, apply) => ApplyShieldBlock(h, a, apply, percent: true), null));
    }

    /// <summary>Record on apply or take back on removal; null when a removal finds nothing recorded.</summary>
    private static Applied? Take(SpellAura aura, bool apply, UnitMods group = UnitMods.End)
    {
        if (apply)
        {
            var record = new Applied(aura.Amount, group);
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

    // --- refresh: recompute a group's field from the flat sum and the ledger ---------------------

    /// <summary>
    /// The value of a group after its percentages: <c>((baseValue * BasePct) + (pre - baseValue)) * TotalPct</c> (UnitStatModifier.cpp:117-125,
    /// with <paramref name="pre"/> = BASE_VALUE + TOTAL_VALUE) truncated to an integer as SetStat and the other field writers do; a
    /// TotalPct at or below 0 reads 0 (Unit::GetTotalStatValue).
    /// </summary>
    internal static int Modified(UnitModLedger ledger, UnitMods group, int pre, int baseValue)
    {
        float total = ledger.TotalPct(group);
        if (total <= 0)
        {
            return 0;
        }

        float value = ((baseValue * ledger.BasePct(group)) + (pre - baseValue)) * total;
        return (int)Math.Clamp(value, -2e9f, 2e9f);
    }

    /// <summary>Recompute one stat of a unit from its flat sum and percentages (no health or mana follow-up; see <see cref="StatAuras.OnStatMoved"/>).</summary>
    internal static void RefreshStat(Unit unit, int stat)
    {
        var group = (UnitMods)((int)UnitMods.StatStrength + stat);
        if (UnitModLedger.Find(unit) is not { } ledger || ledger.IsNeutral(group))
        {
            return;
        }

        int field = UpdateFields.UnitFieldStat0 + stat;
        int current = unit.GetInt32(field);
        int pre = current - ledger.Applied(group);

        // BASE_PCT scales the level base only: the buff counters hold what items and flat auras added (BASE_PCT is a player-only aura).
        int baseValue = 0;
        if (unit is Player player && ledger.BasePct(group) != 1.0f)
        {
            baseValue = Math.Max(0, pre - player.GetInt32(UpdateFields.PlayerFieldPosstat0 + stat) - player.GetInt32(UpdateFields.PlayerFieldNegstat0 + stat));
        }

        int value = Math.Max(0, Modified(ledger, group, pre, baseValue));
        ledger.SetApplied(group, value - pre);
        if (value != current)
        {
            unit.SetInt32(field, value);
        }
    }

    /// <summary>
    /// The five stat groups of a unit, for <see cref="PlayerStatSystem.UpdateAll"/> (a changed flat sum, such as an item, is scaled again).
    /// The health and mana bonuses are the caller's (<see cref="StatBonuses.Update"/>).
    /// </summary>
    internal static void RefreshStats(Unit unit)
    {
        for (int stat = 0; stat < 5; stat++)
        {
            RefreshStat(unit, stat);
        }
    }

    /// <summary>The maximum health and the five maximum powers of a unit; a lowered maximum drags the current value down with it.</summary>
    internal static void RefreshPools(Unit unit)
    {
        if (UnitModLedger.Find(unit) is not { } ledger)
        {
            return;
        }

        if (!ledger.IsNeutral(UnitMods.Health))
        {
            int current = (int)Math.Min(unit.MaxHealth, int.MaxValue);
            int pre = current - ledger.Applied(UnitMods.Health);
            int value = Math.Max(1, Modified(ledger, UnitMods.Health, pre, 0));
            ledger.SetApplied(UnitMods.Health, value - pre);
            if (value != current)
            {
                unit.MaxHealth = (uint)value;
                if (unit.Health > (uint)value)
                {
                    unit.Health = (uint)value;
                }
            }
        }

        for (int power = (int)PowerType.Mana; power <= (int)PowerType.Happiness; power++)
        {
            var group = (UnitMods)((int)UnitMods.Mana + power);
            if (ledger.IsNeutral(group))
            {
                continue;
            }

            int maxField = UpdateFields.UnitFieldMaxpower1 + power;
            int current = (int)Math.Min(unit.GetUInt32(maxField), int.MaxValue);
            int pre = current - ledger.Applied(group);
            int value = Math.Max(0, Modified(ledger, group, pre, 0));
            ledger.SetApplied(group, value - pre);
            if (value != current)
            {
                unit.SetUInt32(maxField, (uint)value);
                int powerField = UpdateFields.UnitFieldPower1 + power;
                if (unit.GetUInt32(powerField) > (uint)value)
                {
                    unit.SetUInt32(powerField, (uint)value);
                }
            }
        }
    }

    /// <summary>Armor and the resistances of a unit, for <see cref="PlayerStatSystem.UpdateAll"/> (after the agility armor was brought in line).</summary>
    internal static void RefreshResistances(Unit unit)
    {
        for (int school = 0; school < 7; school++)
        {
            RefreshResistance(unit, school);
        }
    }

    /// <summary>Recompute the field of one school (0 = armor) from the flat sum, the worn items' base and the percentages; holy has no field in 1.12.</summary>
    internal static void RefreshResistance(Unit unit, int school)
    {
        var group = (UnitMods)((int)UnitMods.Armor + school);
        if (school == (int)SpellSchool.Holy || UnitModLedger.Find(unit) is not { } ledger || ledger.IsNeutral(group))
        {
            return;
        }

        int field = UpdateFields.UnitFieldResistances + school;
        int current = unit.GetInt32(field);
        int pre = current - ledger.Applied(group);
        int baseValue = unit is Player player && ledger.BasePct(group) != 1.0f ? player.StatState.ItemResistance(school) : 0;
        int value = Math.Max(0, Modified(ledger, group, pre, baseValue));
        ledger.SetApplied(group, value - pre);
        if (value != current)
        {
            unit.SetInt32(field, value);
        }
    }

    /// <summary>The derived combat values of a player attached to a stat system, after a stat group changed (no-op for any other unit).</summary>
    internal static void UpdateDerived(Unit unit)
    {
        if (unit is Player { StatState.Maintainer: { } maintainer } player)
        {
            maintainer.UpdateAll(player);
        }
    }

    // --- MOD_PERCENT_STAT / MOD_TOTAL_STAT_PERCENTAGE --------------------------------------------

    /// <summary>
    /// Misc value -1 = all five stats, 0-4 = one (anything else is ignored). BASE_PCT (80) exists for players only, TOTAL_PCT (137) for
    /// every unit. The stat's field is recomputed, then the health and mana bonuses follow it. For TOTAL_PCT on stamina of a spell with
    /// the ability attribute the health percentage is kept across the change (HandleModTotalPercentStat).
    /// </summary>
    private static void ApplyStatPercent(SpellAuraHolder holder, SpellAura aura, bool apply, UnitModifierType slot)
    {
        int misc = aura.MiscValue;
        Unit target = holder.Target;
        if (misc is < -1 or > 4 || (slot == UnitModifierType.BasePct && target is not Player) || Take(aura, apply) is not { } applied)
        {
            return;
        }

        UnitModLedger ledger = UnitModLedger.For(target);
        uint healthBefore = target.Health;
        uint maxBefore = target.MaxHealth;
        for (int stat = 0; stat < 5; stat++)
        {
            if (misc >= 0 && misc != stat)
            {
                continue;
            }

            int field = UpdateFields.UnitFieldStat0 + stat;
            int before = target.GetInt32(field);
            ledger.Apply((UnitMods)((int)UnitMods.StatStrength + stat), slot, applied.Amount, apply);
            RefreshStat(target, stat);
            StatAuras.OnStatMoved(target, stat, before, target.GetInt32(field));
        }

        RefreshPools(target);
        if (slot == UnitModifierType.TotalPct && misc == 2 && maxBefore > 0 && (holder.Spell.Attributes & SpellAttributes.IsAbility) != 0)
        {
            // newHP = (curHP * newMaxHP) / maxHP, in integers (HandleModTotalPercentStat).
            target.Health = KeepRatio(target.MaxHealth, healthBefore, maxBefore);
        }

        UpdateDerived(target);
    }

    /// <summary><c>newMax * health / oldMax</c> in integers, never above the new maximum.</summary>
    private static uint KeepRatio(uint newMax, uint health, uint oldMax) => (uint)Math.Min((ulong)newMax * health / oldMax, newMax);

    // --- MOD_INCREASE_HEALTH / MOD_INCREASE_HEALTH_PERCENT ---------------------------------------

    /// <summary>
    /// A flat amount of maximum health (TOTAL_VALUE of the health group). Last Stand's triggered spell also adds the amount to the current health
    /// and takes it back on removal (never below 1 while alive); the Bear and Dire Bear Form passives keep the health percentage across the change.
    /// </summary>
    private static void ApplyIncreaseHealth(SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (Take(aura, apply) is not { } applied)
        {
            return;
        }

        Unit target = holder.Target;
        int amount = applied.Amount;
        switch (holder.Spell.Id)
        {
            case LastStandTriggered:
                if (apply)
                {
                    AddMaxHealth(target, amount);
                    target.Health = (uint)Math.Clamp((long)target.Health + amount, 0L, target.MaxHealth);
                }
                else
                {
                    if (target.Health > amount)
                    {
                        target.Health -= (uint)amount;
                    }
                    else if (target.Health > 0)
                    {
                        target.Health = 1;
                    }

                    AddMaxHealth(target, -amount);
                }

                break;
            case BearFormPassive:
            case DireBearFormPassive:
            {
                uint maxBefore = target.MaxHealth;
                uint healthBefore = target.Health;
                AddMaxHealth(target, apply ? amount : -amount);
                if (maxBefore > 0)
                {
                    target.Health = KeepRatio(target.MaxHealth, healthBefore, maxBefore);
                }

                break;
            }

            default:
                AddMaxHealth(target, apply ? amount : -amount);
                break;
        }
    }

    /// <summary>Move the flat maximum health, then put the percentages back on top.</summary>
    private static void AddMaxHealth(Unit target, int delta)
    {
        StatAuras.ChangeMaxHealth(target, delta);
        RefreshPools(target);
    }

    /// <summary>MOD_INCREASE_HEALTH_PERCENT and MOD_INCREASE_ENERGY_PERCENT: TOTAL_PCT of the health group, or of the unit's own power type.</summary>
    private static void ApplyPoolPercent(SpellAuraHolder holder, SpellAura aura, bool apply, bool health)
    {
        Unit target = holder.Target;
        UnitMods group = UnitMods.Health;
        if (!health && !TryPowerGroup(target, aura, apply, out group))
        {
            return;
        }

        if (Take(aura, apply, group) is not { } applied)
        {
            return;
        }

        UnitModLedger.For(target).Apply(applied.Group, UnitModifierType.TotalPct, applied.Amount, apply);
        RefreshPools(target);
    }

    // --- MOD_INCREASE_ENERGY ---------------------------------------------------------------------

    /// <summary>A flat amount of the maximum of the unit's current power type (misc value = power type, else ignored; TOTAL_VALUE of the power group).</summary>
    private static void ApplyIncreaseEnergy(SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        Unit target = holder.Target;
        if (!TryPowerGroup(target, aura, apply, out UnitMods group) || Take(aura, apply, group) is not { } applied)
        {
            return;
        }

        // The recorded group is used on removal: a druid may have left the power type the aura was applied to.
        int power = (int)(applied.Group - UnitMods.Mana);
        int maxField = UpdateFields.UnitFieldMaxpower1 + power;
        long delta = apply ? applied.Amount : -applied.Amount;
        target.SetUInt32(maxField, (uint)Math.Clamp(target.GetUInt32(maxField) + delta, 0L, uint.MaxValue));
        RefreshPools(target);
        int powerField = UpdateFields.UnitFieldPower1 + power;
        if (target.GetUInt32(powerField) > target.GetUInt32(maxField))
        {
            target.SetUInt32(powerField, target.GetUInt32(maxField));
        }
    }

    /// <summary>
    /// The power group an energy or energy percent aura goes to: the unit's power type when it is the aura's misc value. A removal needs no check,
    /// it takes the group the apply recorded.
    /// </summary>
    private static bool TryPowerGroup(Unit target, SpellAura aura, bool apply, out UnitMods group)
    {
        group = UnitMods.End;
        if (!apply)
        {
            if (!s_applied.TryGetValue(aura, out Applied? recorded) || recorded.Group == UnitMods.End)
            {
                return false;
            }

            group = recorded.Group;
            return true;
        }

        if (aura.MiscValue != (int)target.PowerType)
        {
            return false;
        }

        group = (UnitMods)((int)UnitMods.Mana + aura.MiscValue);
        return true;
    }

    // --- resistances -----------------------------------------------------------------------------

    /// <summary>
    /// MOD_BASE_RESISTANCE: a flat amount to each school of the misc mask, players only (TOTAL_VALUE of the resistance group; unlike
    /// MOD_RESISTANCE it moves no buff counter). Holy has no field.
    /// </summary>
    private static void ApplyBaseResistance(SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        Unit target = holder.Target;
        if (target is not Player || Take(aura, apply) is not { } applied)
        {
            return;
        }

        int delta = apply ? applied.Amount : -applied.Amount;
        for (int school = 0; school < 7; school++)
        {
            if (((uint)aura.MiscValue & (1u << school)) == 0 || school == (int)SpellSchool.Holy)
            {
                continue;
            }

            int field = UpdateFields.UnitFieldResistances + school;
            target.SetInt32(field, target.GetInt32(field) + delta);
            RefreshResistance(target, school);
        }
    }

    /// <summary>MOD_BASE_RESISTANCE_PCT (BASE_PCT, players only) and MOD_RESISTANCE_PCT (TOTAL_PCT, every unit): each school of the misc mask.</summary>
    private static void ApplyResistancePercent(SpellAuraHolder holder, SpellAura aura, bool apply, UnitModifierType slot)
    {
        Unit target = holder.Target;
        if ((slot == UnitModifierType.BasePct && target is not Player) || Take(aura, apply) is not { } applied)
        {
            return;
        }

        UnitModLedger ledger = UnitModLedger.For(target);
        for (int school = 0; school < 7; school++)
        {
            if (((uint)aura.MiscValue & (1u << school)) == 0)
            {
                continue;
            }

            ledger.Apply((UnitMods)((int)UnitMods.Armor + school), slot, applied.Amount, apply);
            RefreshResistance(target, school);
        }
    }

    // --- attack power ----------------------------------------------------------------------------

    /// <summary>
    /// MOD_ATTACK_POWER_PCT / MOD_RANGED_ATTACK_POWER_PCT: TOTAL_PCT of the attack power group, written as <c>TotalPct - 1</c> to the multiplier
    /// field (UNIT_FIELD_(RANGED_)ATTACK_POWER_MULTIPLIER, StatSystem.cpp:786-790) that <see cref="StatFormulas.TotalAttackPower"/> reads.
    /// Priests, mages and warlocks take no ranged attack power (HandleAuraModRangedAttackPowerPercent).
    /// </summary>
    private static void ApplyAttackPowerPercent(SpellAuraHolder holder, SpellAura aura, bool apply, bool ranged)
    {
        Unit target = holder.Target;
        if ((ranged && Array.IndexOf(s_wandUsers, target.Class) >= 0) || Take(aura, apply) is not { } applied)
        {
            return;
        }

        UnitMods group = ranged ? UnitMods.AttackPowerRanged : UnitMods.AttackPower;
        UnitModLedger ledger = UnitModLedger.For(target);
        ledger.Apply(group, UnitModifierType.TotalPct, applied.Amount, apply);
        float total = ledger.TotalPct(group);
        target.SetFloat(ranged ? UpdateFields.UnitFieldRangedAttackPowerMultiplier : UpdateFields.UnitFieldAttackPowerMultiplier, (total <= 0 ? 0.0f : total) - 1.0f);
        if (target is Player { StatState.Maintainer: { } maintainer } player)
        {
            maintainer.UpdateAttackPowerAndDamage(player, ranged);
        }
    }

    // --- defense and shield ----------------------------------------------------------------------

    /// <summary>MOD_DODGE_PERCENT / MOD_PARRY_PERCENT / MOD_BLOCK_PERCENT: players only; the sum of the amounts feeds the percentage formula.</summary>
    private static void ApplyDefense(SpellAuraHolder holder, SpellAura aura, bool apply, Action<PlayerStatState, float> add)
    {
        if (holder.Target is not Player player || Take(aura, apply) is not { } applied)
        {
            return;
        }

        add(player.StatState, apply ? applied.Amount : -applied.Amount);
    }

    /// <summary>MOD_SHIELD_BLOCKVALUE (flat) and MOD_SHIELD_BLOCKVALUE_PCT (multiplier): players only (Player::HandleBaseModValue).</summary>
    private static void ApplyShieldBlock(SpellAuraHolder holder, SpellAura aura, bool apply, bool percent)
    {
        if (holder.Target is not Player player || Take(aura, apply) is not { } applied)
        {
            return;
        }

        if (percent)
        {
            player.StatState.ApplyShieldBlockPct(applied.Amount, apply);
        }
        else
        {
            player.StatState.AddShieldBlockFlat(apply ? applied.Amount : -applied.Amount);
        }
    }
}
