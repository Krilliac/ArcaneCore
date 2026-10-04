using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Casters.Drain;

/// <summary>
/// The drain auras: PERIODIC_LEECH (Drain Life, Siphon Life, Devouring Plague) and PERIODIC_MANA_LEECH (Drain Mana).
/// vmangos Aura::PeriodicTick, SpellAuras.cpp:5927-6000 (leech) and :6116-6175 (mana leech).
/// <para>
/// Not modelled (documented limits): damage absorption of the leech (the absorb framework is another lane), spell
/// immunities, the PERIODIC_HEALTH_FUNNEL aura that shares vmangos' leech block (Health Funnel targets the caster's pet:
/// pets lane), threat of the heal and of the drained mana, spell mods of the multiplier, and Mark of Kazzak.
/// </para>
/// </summary>
public static class DrainAuras
{
    /// <summary>Improved Drain Mana ranks (vmangos SpellAuras.cpp:6192-6200): 15 and 30 percent of the drained mana as shadow damage.</summary>
    private const uint ImprovedDrainManaRank1 = 17864;

    private const uint ImprovedDrainManaRank2 = 18393;

    public static void Register(SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        spells.RegisterAura(AuraType.PeriodicLeech, new AuraHandler(null, TickLeech));
        spells.RegisterAura(AuraType.PeriodicManaLeech, new AuraHandler(null, TickManaLeech));
    }

    /// <summary>
    /// Health leech tick: the aura's (snapshotted) amount, target side modifiers, partial resist, capped at the
    /// target's health; the caster heals the damage dealt times EffectMultipleValue (1 when unset). Nothing happens
    /// while either side is dead; a target that dies ends the caster's channel of this spell.
    /// </summary>
    private static void TickLeech(SpellSystem spells, SpellAuraHolder holder, SpellAura aura)
    {
        Unit target = holder.Target;
        if (!target.IsInWorld || !target.IsAlive || spells.AuraCaster(holder) is not { IsAlive: true } caster)
        {
            return;
        }

        SpellInfo spell = holder.Spell;
        uint amount = (uint)Math.Max(aura.Amount, 0);
        uint damage = spells.ModifyTick(SpellAmountStage.DamageOverTimeTick, holder, aura, caster, amount);
        uint resisted = Math.Min(damage, spells.CombatRules.RollPartialResist(spells, caster, target, spell, damage));
        SpellSystem.SendToSet(caster, WorldOpcode.SmsgSpellnonmeleedamagelog,
            CasterPeriodicPackets.BuildPeriodicSpellDamageLog(target.Guid, caster.Guid, spell.Id, damage, spell.School, absorbed: 0, resisted), includeSelf: true);

        damage = Math.Min(damage - resisted, target.Health);
        uint dealt = spells.Damage.DealSpellDamage(caster, target, spell, damage, periodic: true);
        spells.OnDamageTaken(target, caster, dealt, periodic: true);

        if (!target.IsAlive
            && spells.GetState(caster.Guid)?.CurrentCast is { State: SpellCastState.Casting } cast && cast.Spell.Id == spell.Id)
        {
            spells.CancelChannel(caster);
        }

        // SPELLMOD_MULTIPLE_VALUE on the leech multiple (vmangos Aura::PeriodicTick, SpellAuras.cpp:6008).
        float multiplier = spells.ModFloat(caster, spell, SpellModOp.MultipleValue,
            spell.Effects[aura.EffectIndex].MultipleValue > 0 ? spell.Effects[aura.EffectIndex].MultipleValue : 1.0f);
        uint gain = (uint)(dealt * multiplier);
        if (gain > 0 && caster.IsAlive)
        {
            uint healed = spells.Damage.Heal(caster, caster, spell, gain);
            SpellSystem.SendToSet(caster, WorldOpcode.SmsgSpellheallog,
                SpellPackets.BuildSpellHealLog(caster.Guid, caster.Guid, spell.Id, healed), includeSelf: true);
        }
    }

    /// <summary>
    /// Mana leech tick: drains min(power, amount) when the target uses the aura's power type, the living caster gains the
    /// drained amount times EffectMultipleValue (only if it has any of that power at all), Improved Drain Mana adds shadow
    /// damage, and the drain breaks damage-cancelled auras on the target.
    /// </summary>
    private static void TickManaLeech(SpellSystem spells, SpellAuraHolder holder, SpellAura aura)
    {
        Unit target = holder.Target;
        if (!target.IsAlive || aura.MiscValue is < 0 or > (int)PowerType.Happiness)
        {
            return;
        }

        var power = (PowerType)aura.MiscValue;
        if (target.PowerType != power || spells.AuraCaster(holder) is not { IsAlive: true } caster)
        {
            return;
        }

        uint available = SpellSystem.GetPower(target, power);
        uint drained = Math.Min(available, (uint)Math.Max(aura.Amount, 0));
        SpellSystem.SetPower(target, power, available - drained);

        // SPELLMOD_MULTIPLE_VALUE on the gain multiplier, only when the caster has the power at all (SpellAuras.cpp:6171-6175).
        float multiplier = caster.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)power) > 0
            ? spells.ModFloat(caster, holder.Spell, SpellModOp.MultipleValue, holder.Spell.Effects[aura.EffectIndex].MultipleValue)
            : 0f;
        SpellSystem.SendToSet(target, WorldOpcode.SmsgPeriodicauralog,
            CasterPeriodicPackets.BuildManaLeechLog(target.Guid, caster.Guid, holder.Spell.Id, (uint)power, drained, multiplier), includeSelf: true);

        uint gain = (uint)(drained * multiplier);
        if (gain > 0)
        {
            uint max = caster.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)power);
            SpellSystem.SetPower(caster, power, Math.Min(max, SpellSystem.GetPower(caster, power) + gain));
        }

        ImprovedDrainMana(spells, caster, target, drained);
        spells.BreakDamageCancelledAuras(target);
    }

    /// <summary>The talent's shadow damage tick (vmangos PeriodicTick(talent spell, PERIODIC_DAMAGE, drained * 0.15 / 0.3)).</summary>
    private static void ImprovedDrainMana(SpellSystem spells, Unit caster, Unit target, uint drained)
    {
        (uint talentId, float fraction) = spells.HasAura(caster, ImprovedDrainManaRank2) ? (ImprovedDrainManaRank2, 0.3f)
            : spells.HasAura(caster, ImprovedDrainManaRank1) ? (ImprovedDrainManaRank1, 0.15f)
            : (0u, 0f);
        if (talentId == 0 || !target.IsAlive || spells.Store.Get(talentId) is not { } talent)
        {
            return;
        }

        float amount = (uint)(drained * fraction); // the vmangos PeriodicTick takes a uint32
        if (spells.AmountModifier is { } modifier)
        {
            amount = modifier.Modify(SpellAmountStage.DamageOverTimeTick, caster, target, talent, 0, amount, 1);
        }

        uint damage = (uint)Math.Floor(Math.Max(amount, 0f) + spells.Random.NextSingle());
        uint resisted = Math.Min(damage, spells.CombatRules.RollPartialResist(spells, caster, target, talent, damage));
        SpellSystem.SendToSet(caster, WorldOpcode.SmsgSpellnonmeleedamagelog,
            CasterPeriodicPackets.BuildPeriodicSpellDamageLog(target.Guid, caster.Guid, talent.Id, damage, talent.School, absorbed: 0, resisted), includeSelf: true);
        uint dealt = spells.Damage.DealSpellDamage(caster, target, talent, damage - resisted, periodic: true);
        spells.OnDamageTaken(target, caster, dealt, periodic: true);
    }
}
