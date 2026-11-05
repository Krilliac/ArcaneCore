using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;

namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureMapSystem
{
    /// <summary>CreatureTypeFlags::CAN_ASSIST (cmangos Entities/CreatureDefines.h): can aid any player (and group) in combat, typically an escort.</summary>
    public const uint TypeFlagCanAssist = 0x00001000;

    /// <summary>MAX_PLAYER_DISTANCE of the escort and follower AIs: how far away the enemy may be.</summary>
    public const float PlayerAssistRange = 100f;

    /// <summary>
    /// mangos-classic <c>npc_escortAI::AssistPlayerInCombat</c> and <c>FollowerAI::AssistPlayerInCombat</c> (AI/ScriptDevAI/base/escort_ai.cpp:55-90,
    /// follower_ai.cpp:30-64), called from <c>UnitAI::MoveInLineOfSight</c> (AI/BaseAI/UnitAI.cpp:70-95) once the creature may initiate an attack:
    /// <paramref name="who"/>, which is fighting a player (or a unit a player owns or charms), is taken on although it is far outside the aggro
    /// radius. The caller has checked that the escort or follow is in progress. Needs the template's CAN_ASSIST type flag; refused while the
    /// creature is stunned, confused or fleeing (the reference's feign-death check is not modelled), when the enemy is not one it may attack (the
    /// hooks' CanAttack, which for two NPCs is hostile in either direction as mangos-classic Relations.cpp CanAttack does; no forward-only
    /// hostility test is added), farther than <see cref="PlayerAssistRange"/> or out
    /// of line of sight. A creature without a victim attacks <paramref name="who"/> through its AI; one already fighting only adds it to its
    /// threat list (<see cref="EnterCombatWithTarget"/>). Like the reference the gate before it holds: a creature that already has a victim assists
    /// only in an instanceable map. Returns true when it did.
    /// </summary>
    public bool AssistPlayerInCombat(Creature creature, Unit who)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(who);
        if ((creature.Template.TypeFlags & TypeFlagCanAssist) == 0
            || who.Combat.Victim is not { } victim || !victim.IsCharmerOrOwnerPlayerOrPlayerItself
            || !_creatures.ContainsKey(creature.Guid) || creature.IsEvading || ReferenceEquals(creature, who)
            || (creature.UnitFlags & LostControl) != 0
            || !CanInitiateAttack(creature)
            || (creature.Combat.Victim is not null && !Map.Combat.Hooks.IsInstanceable(Map.MapId))
            || !who.IsAlive || !ReferenceEquals(who.Map, Map)
            || !Map.Combat.Hooks.CanAttack(creature, who)
            || WorldGeometry.DistanceSq3D(creature, who) > PlayerAssistRange * PlayerAssistRange
            || !InLineOfSight(creature, who))
        {
            return false;
        }

        EnterCombatWithTarget(creature, who);
        return true;
    }

    /// <summary>
    /// vmangos <c>npc_escortAI::UpdateEscortAI</c> (AI/ScriptedEscortAI.cpp:301-308): "make nearby enemies aggro passive escort npcs". The
    /// nearest unit the escort is hostile to inside its own aggro radius (<c>SelectNearestHostileUnitInAggroRange(true)</c>: within
    /// <see cref="GetAttackDistance"/>, attackable, hostile, seen and in line of sight; players count when it is the nearest) is made to attack the
    /// escort when it is a creature that is no pet or charm and not yet in combat. Returns that creature, or null when nothing was started.
    /// </summary>
    public Creature? AggroNearestHostileCreature(Creature escort)
    {
        ArgumentNullException.ThrowIfNull(escort);
        if (!_creatures.ContainsKey(escort.Guid) || !escort.IsAlive || escort.IsEvading)
        {
            return null;
        }

        Unit? nearest = null;
        float nearestSq = float.MaxValue;
        foreach (Unit candidate in Map.Players.Cast<Unit>().Concat(_creatures.Values))
        {
            if (ReferenceEquals(candidate, escort) || !candidate.IsAlive || !ReferenceEquals(candidate.Map, Map) || candidate is Player { IsGameMaster: true }
                || (candidate is Creature other && other.IsInEvadeMode))
            {
                continue;
            }

            float distSq = WorldGeometry.DistanceSq3D(escort, candidate);
            float range = MathF.Min(GetAttackDistance(escort, candidate), MathF.Sqrt(nearestSq));
            if (distSq > range * range || distSq >= nearestSq
                || !Map.Combat.Hooks.CanAttack(escort, candidate) || !_ai.Hostility.IsHostile(escort, candidate) || !CanSeeForAggro(escort, candidate))
            {
                continue;
            }

            nearest = candidate;
            nearestSq = distSq;
        }

        if (nearest is Creature { AI: { } ai } target && target.CharmerOrOwnerGuid.IsEmpty && !target.Combat.IsInCombat)
        {
            ai.AttackStart(escort);
            return target;
        }

        return null;
    }
}
