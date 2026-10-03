using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;

namespace ArcaneCore.Game.Combat.Threat;

/// <summary>
/// What a taunt does to the taunted creature (vmangos Unit::TauntApply and Unit::TauntFadeOut, Objects/Unit.cpp:7443-7509). The
/// taunt caster list itself is <see cref="ThreatList.AddTauntCaster"/>; this is the part that reacts.
/// </summary>
public static class Taunt
{
    /// <summary>
    /// vmangos Unit::TauntApply: a game master taunter, a target that cannot hold a threat list, and a taunter that already is
    /// the victim change nothing. Otherwise the target attacks the taunter (unless it is confused or fleeing, the sheep/fear fix,
    /// :7458-7466) and the threat list lifts the taunter's threat to the victim's (<see cref="ThreatList.TauntApply"/>).
    /// Limit: confuse and fear are read from the unit flags, not from aura holders; the creature is not turned to face the
    /// taunter (it faces its victim when it chases).
    /// </summary>
    public static void Apply(Unit target, Unit taunter)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(taunter);
        if (taunter is Player { IsGameMaster: true } || !ThreatRules.CanHaveThreatList(target) || ReferenceEquals(target.Combat.Victim, taunter))
        {
            return;
        }

        if ((target.UnitFlags & (UnitFlags.Confused | UnitFlags.Fleeing)) == 0 && target is Creature creature)
        {
            if (creature.AI is { } ai)
            {
                ai.AttackStart(taunter);
            }
            else
            {
                creature.System?.AttackStart(creature, taunter);
            }
        }

        target.Combat.Threat.TauntApply(taunter);
    }

    /// <summary>
    /// vmangos Unit::TauntFadeOut: only when the taunter is the current victim. An empty threat list ends the fight (the creature
    /// evades unless it is charmed, :7488-7498); otherwise the taunt's temporary threat is taken out and the next victim selection
    /// of the creature host chooses again (vmangos selects at once and turns to the result; the attack switch itself happens at the
    /// creature's next update either way). The taunter went back to the 110 % rule: it keeps the victim until someone exceeds
    /// 110 % of its threat.
    /// </summary>
    public static void FadeOut(Unit target, Unit taunter)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(taunter);
        if (taunter is Player { IsGameMaster: true } || !ThreatRules.CanHaveThreatList(target) || !ReferenceEquals(target.Combat.Victim, taunter))
        {
            return;
        }

        if (target.Combat.Threat.IsEmpty)
        {
            if (target.CharmerGuid.IsEmpty && target is Creature creature)
            {
                creature.System?.EnterEvadeMode(creature);
            }

            return;
        }

        target.Combat.Threat.TauntFadeOut(taunter);
    }
}
