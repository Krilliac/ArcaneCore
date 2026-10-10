using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;

namespace ArcaneCore.Game.Spells;

/// <summary>SPELL_EFFECT_SANCTUARY (79), vmangos Spell::EffectSanctuary.</summary>
public sealed class SanctuaryEffect : ISpellHandlerModule
{
    public void Register(SpellSystem system) => system.RegisterEffect(SpellEffectName.Sanctuary, Apply);

    private static void Apply(SpellEffectContext context)
    {
        Unit target = context.Target;
        SpellSystem system = context.System;
        bool vanish = context.Spell.IsFitToFamily(8, 11); // SPELLFAMILY_ROGUE, CF_ROGUE_VANISH

        // vmangos passes killDelayed=true; delayed projectiles are not represented by this spell system.
        system.InterruptSpellsCastedOnMe(target, onlyIfNotStalked: false);
        if (target.Map is { } map)
        {
            foreach (Unit attacker in target.Combat.Attackers.ToArray())
            {
                if (!vanish || !system.IsContestedGuard(attacker.GetOwner() ?? attacker))
                    map.Combat.AttackStop(attacker);
            }
        }

        target.Combat.LastSanctuaryMs = system.NowMs;
        if (vanish)
        {
            // Vanish ends the rogue's combat before the triggered stealth spell takes effect.
            target.Map?.Combat.CombatStop(target);
            bool hasGuard = false;
            foreach (Unit holder in target.Combat.ThreatenedBy.ToArray())
            {
                if (system.IsContestedGuard(holder.GetOwner() ?? holder))
                    hasGuard = true;
                else
                    holder.Combat.Threat.Remove(target);
            }

            if (!hasGuard && target is Player player)
                system.SuppressCreatureDetection(player, 1000);
        }
        else
        {
            // DoResetThreat retains each reference, including a zero-threat current victim.
            foreach (ThreatEntry entry in target.Combat.Threat.Entries.ToArray())
                target.Combat.Threat.ModifyThreatPercent(entry.Target, -100);
        }
    }
}
