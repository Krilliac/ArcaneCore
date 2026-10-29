using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_EFFECT_HEAL_MECHANICAL (75; vmangos Spell::EffectHealMechanical, SpellEffects.cpp:1835-1850; table row 75 "one spell: Mechanical Patch Kit").
/// The 1.12 Spell.dbc has one user, Mechanical Patch Kit (15057): effect 0 heals 700 on a friendly unit, and the spell's TargetCreatureType (mechanical)
/// is what keeps it off everything else, so the effect itself checks nothing but that the target is alive.
/// <para>
/// Unlike HEAL (<c>EffectHeal</c> adds to <c>m_healing</c>, which the spell hit then rolls a crit for, fires the heal procs and logs) this effect calls
/// <c>SpellCaster::DealHeal</c> at once: the caster's healing bonus and the target's healing-taken bonus apply (the <see cref="SpellAmountStage.DirectHeal"/>
/// stage), there is no crit and the heal is not recorded in the hit's outcome, so the hit fires as a plain proc with amount 0 (<c>m_healing</c> stays 0:
/// Spell.cpp:1294 heal branch not taken, the no-damage branch at :1454 instead). The heal adds no assist threat (<c>DealHeal</c> only restores health,
/// SpellCaster.cpp:1026-1044: <see cref="IDamageSink.HealingOrigin.NoThreat"/>); a cast that is not triggered still puts the caster in combat with an
/// in-combat ally, as any assist does (Spell.cpp:1713-1718). SMSG_SPELLHEALLOG is sent (with the whole amount, as <c>SendHealSpellLog(.., addhealth, critical)</c>)
/// only when the caster or the target is a player (SpellCaster.cpp:1041-1042).
/// </para>
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
    internal void EffectHealMechanical(SpellEffectContext context)
    {
        Unit target = context.Target;
        if (!target.IsAlive || context.Value < 0)
        {
            return; // vmangos: unitTarget && unitTarget->IsAlive() && damage >= 0
        }

        uint amount = ModifyDirect(SpellAmountStage.DirectHeal, context, (uint)context.Value);
        Damage.Heal(context.Caster, target, context.Spell, amount, IDamageSink.HealingOrigin.NoThreat);
        if (context.Caster is Player || target is Player)
        {
            SendToSet(context.Caster, WorldOpcode.SmsgSpellheallog,
                SpellPackets.BuildSpellHealLog(target.Guid, context.Caster.Guid, context.Spell.Id, amount, critical: false), includeSelf: true);
        }
    }
}
