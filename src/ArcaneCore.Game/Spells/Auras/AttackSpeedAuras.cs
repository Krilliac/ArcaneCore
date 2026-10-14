using System.Runtime.CompilerServices;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Ranged;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The attack speed auras (ranged (autorepeat lane)), after vmangos SpellAuras.cpp:5092-5166:
/// MOD_ATTACKSPEED (9, all three slots), MOD_MELEE_HASTE (138, main and off hand), MOD_RANGED_HASTE (140) and
/// MOD_RANGED_AMMO_HASTE (141, quivers and ammo pouches). Each moves the unit's attack speed multiplier and the
/// ATTACKTIME update field through <see cref="UnitCombat.ApplyAttackTimePercentMod"/> (vmangos Unit.cpp:9678-9697).
/// <list type="bullet">
/// <item>On apply the amount goes through the SPELLMOD_HASTE talent modifier of the caster's mod owner
/// (<see cref="SpellModOp.Haste"/>, the existing <see cref="SpellSystem.SpellModifiers"/> seam; the identity until a
/// modifier engine is installed); removal uses the modified amount, as vmangos does (the modifier is stored in m_amount).</item>
/// <item>Aura 141 only applies to a player whose ranged slot holds a weapon with AmmoType != 0 (thrown weapons carry 4, wands and
/// no weapon do not). vmangos reverts m_applied for a refused apply; here a per-aura record decides whether removal takes
/// anything back. It is evaluated once, at apply: swapping the weapon later does not re-evaluate it (vmangos).</item>
/// </list>
/// Limits: the Seal of the Crusader damage reduction (SpellAuras.cpp:5106-5111) needs the unit-mod TOTAL_PCT ledger of the
/// main hand and is not applied; the druid shapeshift attack speed override (IsAttackSpeedOverridenShapeShift) belongs to the
/// druid-forms lane.
/// </summary>
public sealed class AttackSpeedAuras : ISpellHandlerModule
{
    /// <summary>The percent each applied aura contributed (after the haste spell modifier); presence means "applied".</summary>
    private static readonly ConditionalWeakTable<SpellAura, StrongBox<float>> s_applied = new();
    private static readonly ConditionalWeakTable<Unit, List<SpellAura>> s_castSpeedAuras = new();

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterAura(AuraType.ModAttackspeed, new AuraHandler((s, h, a, apply) => Apply(s, h, a, apply, WeaponAttackType.BaseAttack, WeaponAttackType.OffAttack, WeaponAttackType.RangedAttack), null));
        system.RegisterAura(AuraType.ModCastingSpeedNotStack, new AuraHandler(ApplyCastingSpeed, null));
        system.RegisterAura(AuraType.ModMeleeHaste, new AuraHandler((s, h, a, apply) => Apply(s, h, a, apply, WeaponAttackType.BaseAttack, WeaponAttackType.OffAttack), null));
        system.RegisterAura(AuraType.ModRangedHaste, new AuraHandler((s, h, a, apply) => Apply(s, h, a, apply, WeaponAttackType.RangedAttack), null));
        system.RegisterAura(AuraType.ModRangedAmmoHaste, new AuraHandler(ApplyAmmoHaste, null));
    }

    /// <summary>
    /// vmangos <c>Aura::HandleModCastingSpeed</c> (SpellAuras.cpp:5080-5091) and <c>Unit::UpdateCastSpeed</c> (Unit.cpp:9702-9733):
    /// applies <see cref="SpellModOp.Haste"/> on apply, then recomputes <see cref="UpdateFields.UnitModCastSpeed"/> across all active
    /// <see cref="AuraType.ModCastingSpeedNotStack"/> auras on the target.
    /// </summary>
    private static void ApplyCastingSpeed(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        Unit target = holder.Target;
        List<SpellAura> active = s_castSpeedAuras.GetOrCreateValue(target);
        if (apply)
        {
            float percent = aura.Amount;
            if (holder.CasterOwner.Caster is Player modOwner)
            {
                percent = system.SpellModifiers.Apply(modOwner, holder.Spell, SpellModOp.Haste, percent);
                aura.Amount = (int)percent;
            }

            s_applied.AddOrUpdate(aura, new StrongBox<float>(percent));
            if (!active.Contains(aura))
            {
                active.Add(aura);
            }
        }
        else
        {
            if (!s_applied.TryGetValue(aura, out _))
            {
                return;
            }

            s_applied.Remove(aura);
            active.Remove(aura);
        }

        float total = 1.0f;
        for (int i = 0; i < active.Count; i++)
        {
            if (!s_applied.TryGetValue(active[i], out StrongBox<float>? box))
            {
                continue;
            }

            float val = box.Value;
            if (val == -100.0f)
            {
                val = -99.99f;
            }

            if (val > 0.0f)
            {
                total *= 100.0f / (100.0f + val);
            }
            else
            {
                total *= (100.0f - val) / 100.0f;
            }
        }

        target.SetFloat(UpdateFields.UnitModCastSpeed, total);
    }

    private static void ApplyAmmoHaste(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (holder.Target is not Player player)
        {
            return;
        }

        if (apply)
        {
            // HandleRangedAmmoHaste (SpellAuras.cpp:5141-5150): no weapon, or a weapon that takes no ammunition: nothing applied.
            Item? weapon = PlayerAmmo.RangedWeapon(player, nonBroken: false);
            if (weapon is null || weapon.Template.AmmoType == 0)
            {
                return;
            }
        }

        Apply(system, holder, aura, apply, WeaponAttackType.RangedAttack);
    }

    private static void Apply(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply, params WeaponAttackType[] slots)
    {
        float percent;
        if (apply)
        {
            percent = aura.Amount;
            if (holder.CasterOwner.Caster is Player modOwner)
            {
                percent = system.SpellModifiers.Apply(modOwner, holder.Spell, SpellModOp.Haste, percent);
                aura.Amount = (int)percent; // m_amount is what removal reverses
            }

            s_applied.AddOrUpdate(aura, new StrongBox<float>(percent));
        }
        else
        {
            if (!s_applied.TryGetValue(aura, out StrongBox<float>? box))
            {
                return; // never applied (a refused quiver aura, or already removed)
            }

            s_applied.Remove(aura);
            percent = box.Value;
        }

        foreach (WeaponAttackType slot in slots)
        {
            holder.Target.Combat.ApplyAttackTimePercentMod(slot, percent, apply);
        }
    }
}
