using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Casters.Drain;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Immunity;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Mana burn and the health funnel (docs/areas/unit-control.md): SPELL_EFFECT_POWER_BURN (62, the priest's Mana Burn), SPELL_AURA_POWER_BURN_MANA
/// (162, Ignite Mana, Soul Tap, Brood Affliction: Blue) and SPELL_AURA_PERIODIC_HEALTH_FUNNEL (62, Blood Siphon and Blood Funnel, which vmangos
/// ticks with the PERIODIC_LEECH code). SPELL_AURA_PERIODIC_MANA_FUNNEL (63) stays unhandled as in vmangos (HandleUnused; the only 1.12 row is
/// "zzOLDMana Funnel").
/// </summary>
public sealed class PowerBurnModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.PowerBurn, system.EffectPowerBurn);
        system.RegisterAura(AuraType.PowerBurnMana, new AuraHandler(null, static (s, h, a) => s.TickPowerBurn(h, a)));
        system.RegisterAura(AuraType.PeriodicHealthFunnel, new AuraHandler(null, DrainAuras.TickLeech));
    }
}

public sealed partial class SpellSystem
{
    /// <summary>
    /// vmangos Spell::EffectPowerBurn (SpellEffects.cpp:1778-1809): a target of the effect's power type loses up to the effect value of it;
    /// the burned amount times EffectMultipleValue (through the caster's SPELLMOD_MULTIPLE_VALUE) is the spell's damage, which then goes
    /// the school-damage way (vmangos m_damage → CalculateSpellDamage → DealSpellDamage: bonus, crit, resist, absorb, log).
    /// </summary>
    internal void EffectPowerBurn(SpellEffectContext context)
    {
        int misc = context.Effect.MiscValue;
        if (misc is < 0 or > (int)PowerType.Happiness || !context.Target.IsAlive || context.Value < 0)
        {
            return;
        }

        var power = (PowerType)misc;
        Unit target = context.Target;
        if (target.PowerType != power)
        {
            return;
        }

        uint current = GetPower(target, power);
        uint burned = Math.Min(current, (uint)context.Value);
        SetPower(target, power, current - burned);
        float multiplier = ModFloat(context.Caster, context.Spell, SpellModOp.MultipleValue, context.Effect.MultipleValue);
        uint damage = (uint)Math.Max(0f, burned * multiplier);
        if (damage == 0)
        {
            return;
        }

        DealDirectDamage(context.Caster, target, context.Spell, ModifyDirect(SpellAmountStage.DirectDamage, context, damage), allowCrit: true);
    }

    /// <summary>
    /// vmangos Aura::PeriodicTick SPELL_AURA_POWER_BURN_MANA (SpellAuras.cpp:6300-6352): nothing on a dead target or without the caster; an
    /// immune target is reported; a target of the aura's power type loses rand_dither(amount) of it, and the burned amount times
    /// EffectMultipleValue (dithered) is spell damage: the caster's bonus and crit (vmangos CalculateSpellDamage), resist and absorb, the
    /// periodic SMSG_SPELLNONMELEEDAMAGELOG, DEAL/TAKE_HARMFUL_PERIODIC procs (with TAKEN_ANY_DAMAGE when damage gets through), then the damage.
    /// </summary>
    internal void TickPowerBurn(SpellAuraHolder holder, SpellAura aura)
    {
        Unit target = holder.Target;
        if (!target.IsAlive || ResolveAuraCaster(holder) is not { } caster)
        {
            return;
        }

        SpellInfo spell = holder.Spell;
        if (ImmunityRules.IsImmuneToDamage(this, target, spell.SchoolMask(), spell))
        {
            SendToSet(caster, WorldOpcode.SmsgSpellordamageImmune, SpellRulePackets.BuildSpellOrDamageImmune(caster.Guid, target.Guid, spell.Id), includeSelf: true);
            return;
        }

        if (aura.MiscValue is < 0 or > (int)PowerType.Happiness || target.PowerType != (PowerType)aura.MiscValue)
        {
            return;
        }

        var power = (PowerType)aura.MiscValue;
        uint current = GetPower(target, power);
        uint burned = Math.Min(current, Dither(Math.Max(aura.Amount, 0)));
        SetPower(target, power, current - burned);
        uint gain = Dither(burned * spell.Effects[aura.EffectIndex].MultipleValue);

        // vmangos rolls IsSpellCrit on every tick, whatever was burned (SpellAuras.cpp:6335), then SpellCaster::CalculateSpellDamage
        // (SpellCaster.cpp:1229-1277): the done/taken bonus, then the critical bonus; a crit marks the log even at zero damage.
        bool crit = CombatRules.RollCrit(this, caster, target, spell);
        float bonused = AmountModifier is { } modifier && gain > 0
            ? modifier.Modify(SpellAmountStage.DirectDamage, caster, target, spell, aura.EffectIndex, gain, 1)
            : gain;
        uint amount = (uint)Math.Max(0f, bonused);
        if (crit)
        {
            amount = CombatRules is ISpellCritAmounts exact
                ? exact.CriticalDamage(this, caster, target, spell, amount)
                : (uint)(amount * CombatRules.CritMultiplier(spell));
        }

        uint original = amount;
        uint resisted = ApplyResist(caster, target, spell, ref amount, periodic: false);
        uint absorbed = AbsorbDamage(caster, target, spell.SchoolMask(), amount, spell);
        amount -= absorbed;
        SendToSet(caster, WorldOpcode.SmsgSpellnonmeleedamagelog, SpellPackets.BuildSpellNonMeleeDamageLog(
            target.Guid, caster.Guid, spell.Id, amount, spell.School, absorbed, resisted, hitInfo: crit ? SpellHitTypeCrit : 0, periodic: true), includeSelf: true);

        FirePeriodicDamageProcs(caster, target, spell, amount, original);
        uint dealt = Damage.DealSpellDamage(caster, target, spell, amount, periodic: true, startsCombat: true, critical: crit, durabilityLoss: true,
            reflected: holder.IsReflected && ReferenceEquals(caster, target));
        OnDamageTaken(target, caster, dealt, periodic: true, absorbed, spell.Id);
    }

    /// <summary>vmangos rand_dither / rand_ditheru: the whole part plus one with the probability of the fraction.</summary>
    private uint Dither(float value) => value <= 0f ? 0u : (uint)Math.Floor(value + Random.NextSingle());
}
