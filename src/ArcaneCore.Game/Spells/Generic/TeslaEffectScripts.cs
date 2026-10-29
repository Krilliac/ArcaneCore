using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Generic;

/// <summary>
/// Stalagg Tesla Effect 28098 and Feugen Tesla Effect 28110 (vmangos SpellEffects.cpp:1266-1290, cast by the Tesla Coil in Thaddius' chamber at its
/// add). Only a creature target counts. If the coil is fighting and its add is further than 60 yards away, the add casts Shock 28099 on a random
/// attacker of the coil. Otherwise the add "evades": its Tesla Passive (28097 Stalagg, 28109 Feugen) is removed, its threat list is emptied, its
/// combat ends, and it casts its chain (28096 Stalagg, 28111 Feugen) on the coil again.
/// </summary>
[SpellScript(28098, 28110)]
public sealed class TeslaEffectScript : ISpellScript
{
    public const uint Shock = 28099;
    public const float ShockRange = 60f;

    internal static (uint Passive, uint Chain) For(uint spellId) => spellId == 28098 ? (28097u, 28096u) : (28109u, 28111u);

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Unit coil = context.Caster;
        if (context.Target is not Creature add)
        {
            return;
        }

        if (coil.Combat.Victim is not null && WorldDummyHelpers.Dist(coil, add) > ShockRange)
        {
            Unit[] attackers = coil.Combat.HasThreatList
                ? [.. coil.Combat.Threat.Entries.Where(e => e.IsOnline && e.Target.IsAlive).Select(e => e.Target)]
                : [];
            if (attackers.Length > 0)
            {
                Unit victim = attackers[GadgetCasts.Roll(context, attackers.Length)];
                context.System.CastSpell(add, Shock, SpellCastTargets.ForUnit(victim.Guid, context.Cast), triggered: false);
            }

            return;
        }

        (uint passive, uint chain) = For(context.Spell.Id);
        context.System.RemoveAuras(add, passive);
        if (add.Combat.HasThreatList)
        {
            add.Combat.Threat.Clear();
        }

        add.Map?.Combat.CombatStop(add);
        context.System.CastSpell(add, chain, SpellCastTargets.ForUnit(coil.Guid, context.Cast), triggered: false);
    }
}
