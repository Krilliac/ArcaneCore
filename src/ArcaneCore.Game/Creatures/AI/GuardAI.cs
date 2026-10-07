using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// mangos GuardAI (Object/GuardAI.cpp, re-implemented): the AI of a creature with the GUARD extra flag (vmangos
/// <c>CREATURE_FLAG_EXTRA_GUARD</c>, 0x400 in both references). It differs from <see cref="AggressorAI"/> only in whom it attacks on
/// sight (<see cref="CreatureMapSystem.CanGuardAggroOnSight"/>): a unit whose faction is hostile to players, a unit the guard's own
/// hostility (faction template or reputation) calls an enemy, and, with <c>Creatures:GuardsDefendFriendlies</c>, the attacker of a
/// creature the guard is friendly to. Fighting, victim selection, evade and the return home are the shared host rules; the
/// reference's own EnterEvadeMode and UpdateAI do the same work (GuardAI.cpp:97-173).
/// <para>
/// Not delivered: SMSG_ZONE_UNDER_ATTACK on death (GuardAI::JustDied sends the zone id to the whole opposing team through
/// World::SendGlobalMessage; this build has no world-wide team broadcast and no verified layout of that message in its message tables),
/// the reference's CONFIG_FLOAT_SIGHT_GUARDER visibility (the aggro radius is the template's detection range, as for every creature).
/// </para>
/// Thread affinity: world thread, like every <see cref="CreatureAI"/>.
/// </summary>
public sealed class GuardAI(Creature creature) : CreatureAI(creature)
{
    public override bool AggroesOnSight => true;

    /// <summary>mangos GuardAI::MoveInLineOfSight (GuardAI.cpp:65-85): attack when the host's guard rule allows it.</summary>
    public override void MoveInLineOfSight(Unit who)
    {
        if (System is { } system && system.CanGuardAggroOnSight(Me, who))
        {
            system.EnterCombatWithTarget(Me, who);
        }
    }
}
