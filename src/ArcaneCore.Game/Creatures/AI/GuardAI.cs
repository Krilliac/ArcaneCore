using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// vmangos GuardAI (AI/GuardAI.cpp, re-implemented): the AI of a creature with the GUARD extra flag (vmangos
/// <c>CREATURE_FLAG_EXTRA_GUARD</c>, 0x400 in both references). It differs from <see cref="AggressorAI"/> only in whom it attacks on
/// sight (<see cref="CreatureMapSystem.CanGuardAggroOnSight"/>): a unit whose faction is hostile to players, a unit the guard's own
/// hostility (faction template or reputation) calls an enemy, a player the guard is not friendly to who is contested-PvP or attacking
/// one of the guard's friends or someone on a taxi (watched from up to 30 yd), and, with <c>Creatures:GuardsDefendFriendlies</c>, a
/// creature fighting a creature the guard is friendly to. Fighting, victim selection, evade and the return home are the shared host
/// rules (vmangos GuardAI::UpdateAI is SelectHostileTarget plus the spell list and melee, GuardAI.cpp:79-88). A GUARD-flagged template
/// whose AIName is 'EventAI', or one named 'GuardEventAI', runs <see cref="CreatureEventAI"/> with the same sight rule (GuardEventAI).
/// <para>
/// Not delivered: SMSG_ZONE_UNDER_ATTACK on a guard's death (mangos GuardAI::JustDied sends it through a world-wide team broadcast;
/// neither vmangos GuardAI nor GuardEventAI does, and this build has no verified layout of the message).
/// </para>
/// Thread affinity: world thread, like every <see cref="CreatureAI"/>.
/// </summary>
public sealed class GuardAI(Creature creature) : CreatureAI(creature)
{
    public override bool AggroesOnSight => true;

    /// <summary>vmangos GuardAI::MoveInLineOfSight (GuardAI.cpp:50-77): attack when the host's guard rule allows it.</summary>
    public override void MoveInLineOfSight(Unit who)
    {
        if (System is { } system && system.CanGuardAggroOnSight(Me, who))
        {
            system.EnterCombatWithTarget(Me, who);
        }
    }
}
