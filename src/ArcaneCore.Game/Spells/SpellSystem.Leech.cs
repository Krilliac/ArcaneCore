using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// vmangos Aura::PeriodicTick SPELL_AURA_PERIODIC_LEECH (SpellAuras.cpp:5927-6014): the tick needs a living
    /// target and a living caster that is still in the target's world; the amount (never negative) less a
    /// partial resist is logged with SMSG_SPELLNONMELEEDAMAGELOG (periodic flag set, there is no periodic aura
    /// log for a leech), limited to the target's health and dealt; the caster is then healed by the damage
    /// dealt times EffectMultipleValue (1 when it is not positive) and, when the target died of it, stops
    /// channelling this spell. A heal log is sent when a player is involved (SpellCaster::DealHeal :1041).
    /// Limits: spell power, absorbs, immunities, procs and the SPELLMOD_MULTIPLE_VALUE talent modifier have no
    /// systems yet.
    /// </summary>
    internal void TickPeriodicLeech(SpellAuraHolder holder, SpellAura aura)
    {
        Unit target = holder.Target;
        if (!target.IsInWorld || !target.IsAlive || ResolveAuraCaster(holder) is not { IsAlive: true } caster)
        {
            return;
        }

        SpellInfo spell = holder.Spell;
        uint amount = (uint)Math.Max(0, aura.Amount);
        uint resisted = Math.Min(amount, CombatRules.RollPartialResist(this, caster, target, spell, amount));
        SendToSet(caster, WorldOpcode.SmsgSpellnonmeleedamagelog, SpellPackets.BuildSpellNonMeleeDamageLog(
            target.Guid, caster.Guid, spell.Id, amount, spell.School, resisted: resisted, periodic: true), includeSelf: true);

        uint net = Math.Min(amount - resisted, target.Health);
        uint dealt = Damage.DealSpellDamage(caster, target, spell, net, periodic: true);
        OnDamageTaken(target, caster, dealt, periodic: true);

        if (!target.IsAlive && GetState(caster.Guid)?.CurrentCast is { State: SpellCastState.Casting } channel && channel.Spell.Id == spell.Id)
        {
            Cancel(channel);
        }

        float multiplier = spell.Effects[aura.EffectIndex].MultipleValue > 0 ? spell.Effects[aura.EffectIndex].MultipleValue : 1.0f;
        uint heal = (uint)(dealt * multiplier);
        if (caster.IsAlive)
        {
            Damage.Heal(caster, caster, spell, heal);
            if (caster is Player)
            {
                SendToSet(caster, WorldOpcode.SmsgSpellheallog,
                    SpellPackets.BuildSpellHealLog(caster.Guid, caster.Guid, spell.Id, heal), includeSelf: true);
            }
        }
    }

    /// <summary>
    /// vmangos Aura::PeriodicTick SPELL_AURA_PERIODIC_MANA_LEECH (SpellAuras.cpp:6116-6190): the misc value is
    /// the power; the target must currently use that power and the caster must be alive. Up to the amount
    /// (never negative) of the target's power is drained, SMSG_PERIODICAURALOG reports power, amount drained
    /// and the gain multiplier (EffectMultipleValue when the caster has a pool of that power, else 0), the
    /// caster gains drained * multiplier and the target's creature threat list gets half of the gain as threat;
    /// auras that break on damage are removed from the target.
    /// Limits: the Mark of Kazzak explosion (:6162-6169), the Improved Drain Mana talents (:6172-6180) and
    /// SPELLMOD_MULTIPLE_VALUE are not implemented.
    /// </summary>
    internal void TickPeriodicManaLeech(SpellAuraHolder holder, SpellAura aura)
    {
        Unit target = holder.Target;
        if (!target.IsAlive || aura.MiscValue is < 0 or > (int)PowerType.Happiness)
        {
            return;
        }

        var power = (PowerType)aura.MiscValue;
        if (target.PowerType != power || ResolveAuraCaster(holder) is not { IsAlive: true } caster)
        {
            return;
        }

        SpellInfo spell = holder.Spell;
        uint wanted = (uint)Math.Max(0, aura.Amount);
        uint drained = Math.Min(GetPower(target, power), wanted);
        SetPower(target, power, GetPower(target, power) - drained);

        float multiplier = caster.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)power) > 0
            ? spell.Effects[aura.EffectIndex].MultipleValue
            : 0f;
        var log = new PacketWriter(40);
        log.WritePackedGuid(target.Guid.Value);
        log.WritePackedGuid(holder.CasterGuid.Value);
        log.WriteUInt32(spell.Id);
        log.WriteUInt32(1);
        log.WriteUInt32((uint)AuraType.PeriodicManaLeech);
        log.WriteUInt32((uint)aura.MiscValue);
        log.WriteUInt32(drained);
        log.WriteSingle(multiplier);
        SendToSet(target, WorldOpcode.SmsgPeriodicauralog, log.ToArray(), includeSelf: true);

        uint gain = (uint)(drained * multiplier);
        if (gain != 0)
        {
            uint before = GetPower(caster, power);
            SetPower(caster, power, before + gain);
            SpellThreat.Add(this, caster, target, spell, (GetPower(caster, power) - before) * 0.5f);
        }

        if (GetState(target.Guid) is { } state)
        {
            foreach (SpellAuraHolder breaking in state.Auras.Where(h => (h.Spell.AuraInterruptFlags & SpellAuraInterruptFlags.Damage) != 0).ToArray())
            {
                RemoveHolder(state, breaking);
            }
        }
    }
}
