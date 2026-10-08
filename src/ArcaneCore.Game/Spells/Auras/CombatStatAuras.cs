using System.Runtime.CompilerServices;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Stats;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The combat stat auras of the vmangos stat system, and the recomputes the derived values need when an aura that feeds them comes or goes:
/// <list type="table">
/// <item><term>MOD_CRIT_PERCENT (52)</term><description>Aura::HandleAuraModCritPercent (SpellAuras.cpp:5021-5049): players only; the crit fields
/// take the generic amount and, for a spell that names an item class, the amount while the hand's weapon fits.</description></item>
/// <item><term>MOD_DAMAGE_DONE (13)</term><description>Aura::HandleModDamageDone (:5230-5316): the physical part is UNIT_MOD_DAMAGE_PHYSICAL
/// (generic, or any aura on a creature) or the fitting hand's TOTAL_VALUE; the client's damage done fields.</description></item>
/// <item><term>MOD_DAMAGE_PERCENT_DONE (79)</term><description>Aura::HandleModDamagePercentDone (:5318-5374): a generic physical aura (any physical
/// aura on a creature) is the TOTAL_PCT of the main-hand, off-hand and ranged groups; a weapon-restricted one the fitting hand's; the client's percent
/// field.</description></item>
/// <item><term>MOD_OFFHAND_DAMAGE_PCT (122)</term><description>Aura::HandleModOffhandDamagePercent (:5376-5385): TOTAL_PCT of the off-hand group
/// (Dual Wield Specialization).</description></item>
/// <item><term>MOD_SPELL_DAMAGE_OF_STAT_PERCENT (174)</term><description>Aura::HandleModSpellDamagePercentFromStat (:4712-4721): the client's spell
/// damage fields (the spell damage itself reads the aura at the cast).</description></item>
/// </list>
/// The player terms live in <see cref="PlayerStatState.Auras"/> and <see cref="PlayerStatState.Mods"/> and are folded in by every
/// <see cref="PlayerStatSystem"/> update, so equipment, disarm and stat changes keep them right. A creature's damage fields follow from
/// <see cref="CreatureDamageStats"/>.
/// <para>
/// Recomputes other handlers' auras need (holder added or removed, <see cref="SpellSystem.HolderAdded"/>): a flat attack power aura (MOD_ATTACK_POWER,
/// MOD_RANGED_ATTACK_POWER: vmangos HandleStatModifier(UNIT_MOD_ATTACK_POWER) recomputes the damage, Unit.cpp:7745-7790) and a disarm
/// (HandleAuraModDisarm: swing time and damage of the main hand, <see cref="PlayerStatSystem.OnDisarmChanged"/>); on a creature also the attack power
/// percent. A stack refresh of such an aura that changes its amount without a new holder is not seen (no 1.12 attack power aura stacks).
/// </para>
/// World thread only.
/// </summary>
public sealed class CombatStatAuras : ISpellHandlerModule
{
    /// <summary>What an aura applied to a non-player's ledger, so its removal takes back the same amount.</summary>
    private static readonly ConditionalWeakTable<SpellAura, StrongBox<int>> s_ledgerAmounts = new();

    private static readonly UnitMods[] s_damageGroups = [UnitMods.DamageMainHand, UnitMods.DamageOffHand, UnitMods.DamageRanged];

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterAura(AuraType.ModCritPercent, new AuraHandler((_, h, a, apply) => ApplyCrit(h, a, apply), null));
        system.RegisterAura(AuraType.ModDamageDone, new AuraHandler((_, h, a, apply) => ApplyDamageDone(h, a, apply), null));
        system.RegisterAura(AuraType.ModDamagePercentDone, new AuraHandler((s, h, a, apply) => ApplyDamagePercentDone(s, h, a, apply), null));
        system.RegisterAura(AuraType.ModOffhandDamagePct, new AuraHandler((_, h, a, apply) => ApplyOffhandPercent(h, a, apply), null));
        system.RegisterAura(AuraType.ModSpellDamageOfStatPercent, new AuraHandler((_, h, a, apply) => ApplySpellDamageOfStat(h, a, apply), null));
        system.HolderAdded += holder => OnHolderChanged(system, holder);
        system.HolderRemoved += holder => OnHolderChanged(system, holder);
    }

    // --- handlers --------------------------------------------------------------------------------

    private static void ApplyCrit(SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (holder.Target is Player player && Track(player, holder, aura, apply) is not null)
        {
            player.StatState.Maintainer?.UpdateAll(player);
        }
    }

    /// <summary>A creature's physical part is recomputed by the holder event (<see cref="OnHolderChanged"/>), which also sees a removal.</summary>
    private static void ApplyDamageDone(SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (holder.Target is Player player && Track(player, holder, aura, apply) is not null)
        {
            Refresh(player, damage: PlayerStatAuras.HasPhysical(aura.MiscValue));
        }
    }

    private static void ApplyDamagePercentDone(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        Unit target = holder.Target;
        bool physical = PlayerStatAuras.HasPhysical(aura.MiscValue);
        if (target is Player player)
        {
            if (Track(player, holder, aura, apply) is not { } term)
            {
                return;
            }

            if (physical && term.IsGeneric)
            {
                ApplyToDamageGroups(player.StatState.Mods, term.Amount, apply);
            }

            Refresh(player, damage: physical || !term.IsGeneric);
            return;
        }

        if (physical && TakeLedgerAmount(aura, apply) is { } amount)
        {
            ApplyToDamageGroups(UnitModLedger.For(target), amount, apply);
            if (target is Creature creature)
            {
                CreatureDamageStats.UpdateMelee(creature, system);
            }
        }
    }

    private static void ApplyOffhandPercent(SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        Unit target = holder.Target;
        if (target is Player player)
        {
            if (Track(player, holder, aura, apply) is { } term)
            {
                player.StatState.Mods.Apply(UnitMods.DamageOffHand, UnitModifierType.TotalPct, term.Amount, apply);
                player.StatState.Maintainer?.UpdateAttackPowerAndDamage(player, ranged: false);
            }

            return;
        }

        if (TakeLedgerAmount(aura, apply) is { } amount)
        {
            UnitModLedger.For(target).Apply(UnitMods.DamageOffHand, UnitModifierType.TotalPct, amount, apply);
        }
    }

    private static void ApplySpellDamageOfStat(SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (holder.Target is Player player && Track(player, holder, aura, apply) is not null)
        {
            PlayerStatSystem.UpdateDamageDoneFields(player);
        }
    }

    // --- holder events ---------------------------------------------------------------------------

    private static void OnHolderChanged(SpellSystem system, SpellAuraHolder holder)
    {
        bool attackPower = false;
        bool disarm = false;
        bool creatureDamage = false;
        foreach (SpellAura? aura in holder.Auras)
        {
            switch (aura?.Type)
            {
                case AuraType.ModAttackPower:
                case AuraType.ModRangedAttackPower:
                    attackPower = true;
                    creatureDamage = true;
                    break;
                case AuraType.ModAttackPowerPct:
                case AuraType.ModDamageDone:
                    creatureDamage = true;
                    break;
                case AuraType.ModDisarm:
                    disarm = true;
                    creatureDamage = true;
                    break;
            }
        }

        switch (holder.Target)
        {
            case Player { StatState.Maintainer: { } maintainer } player:
                if (disarm)
                {
                    maintainer.OnDisarmChanged(player);
                }
                else if (attackPower)
                {
                    maintainer.UpdateAttackPowerAndDamage(player, ranged: false);
                    maintainer.UpdateAttackPowerAndDamage(player, ranged: true);
                }

                break;
            case Creature creature when creatureDamage:
                CreatureDamageStats.UpdateMelee(creature, system);
                break;
        }
    }

    // --- bookkeeping -----------------------------------------------------------------------------

    /// <summary>
    /// Add the aura's term to the player's stat auras (apply) or take it back (removal) and return it; null when nothing changed (a second apply
    /// of the same aura, a removal of one that was never applied). The term keeps the applied amount, so a removal takes back exactly that.
    /// </summary>
    private static StatAuraTerm? Track(Player player, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        PlayerStatAuras auras = player.StatState.Auras;
        if (!apply)
        {
            return auras.Remove(aura);
        }

        var term = new StatAuraTerm(aura.Type, aura.Amount, aura.MiscValue, aura.IsPositive, holder.Spell, holder.CastItemGuid);
        return auras.Add(aura, term) ? term : null;
    }

    /// <summary>The amount a non-player's ledger aura applied (remembered on apply, taken on removal; null when it never applied).</summary>
    private static int? TakeLedgerAmount(SpellAura aura, bool apply)
    {
        if (apply)
        {
            s_ledgerAmounts.AddOrUpdate(aura, new StrongBox<int>(aura.Amount));
            return aura.Amount;
        }

        if (!s_ledgerAmounts.TryGetValue(aura, out StrongBox<int>? box))
        {
            return null;
        }

        s_ledgerAmounts.Remove(aura);
        return box.Value;
    }

    private static void ApplyToDamageGroups(UnitModLedger ledger, int amount, bool apply)
    {
        foreach (UnitMods group in s_damageGroups)
        {
            ledger.Apply(group, UnitModifierType.TotalPct, amount, apply);
        }
    }

    /// <summary>Recompute what a damage done aura moves: the damage fields when it has a physical or weapon part, and always the display fields.</summary>
    private static void Refresh(Player player, bool damage)
    {
        if (damage && player.StatState.Maintainer is { } maintainer)
        {
            maintainer.UpdateAttackPowerAndDamage(player, ranged: false);
            maintainer.UpdateAttackPowerAndDamage(player, ranged: true);
        }

        PlayerStatSystem.UpdateDamageDoneFields(player);
    }
}
