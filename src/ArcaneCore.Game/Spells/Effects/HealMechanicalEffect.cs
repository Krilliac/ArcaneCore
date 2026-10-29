using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_EFFECT_HEAL_MECHANICAL (75), the Mechanical Patch Kit family, after vmangos <c>Spell::EffectHealMechanical</c>
/// (SpellEffects.cpp:1835). The "target is a mechanical creature" rule is the spell's own target-creature-type field, checked before
/// effects run; the effect itself heals a living target with the caster's healing done and the target's healing taken.
/// </summary>
public sealed class HealMechanicalEffect : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.HealMechanical, static context => context.System.EffectHealMechanical(context));
    }
}

public sealed partial class SpellSystem
{
    /// <summary>
    /// vmangos EffectHealMechanical: SpellHealingBonusDone then SpellHealingBonusTaken (the DirectHeal amount stage), then
    /// <c>DealHeal(..., critical = false)</c>. Unlike EffectHeal it never rolls a crit and raises no heal procs (it does not go through
    /// m_healing). SMSG_SPELLHEALLOG carries the whole amount, non-critical, as SendHealSpellLog does in DealHeal.
    /// </summary>
    internal void EffectHealMechanical(SpellEffectContext context)
    {
        if (!context.Target.IsAlive || context.Value < 0)
        {
            return;
        }

        uint amount = ModifyDirect(SpellAmountStage.DirectHeal, context, (uint)context.Value);
        uint healed = Damage.Heal(context.Caster, context.Target, context.Spell, amount, periodic: false);
        RecordHealing(context.Caster, context.Target, context.Spell, healed, false);
        SendToSet(context.Caster, WorldOpcode.SmsgSpellheallog,
            SpellPackets.BuildSpellHealLog(context.Target.Guid, context.Caster.Guid, context.Spell.Id, amount, false), includeSelf: true);
    }
}
