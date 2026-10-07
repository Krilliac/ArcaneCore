using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>Attack start, victim selection, the leash, the unreachable-target rule and combat movement (vmangos CreatureAI::AttackStart, Creature::SelectHostileTarget, IsOutOfThreatArea).</summary>
public sealed partial class CreatureMapSystem : ICreaturePathQuery
{
    // --- combat --------------------------------------------------------------------------------

    /// <summary>
    /// vmangos CreatureAI::AttackStart: melee (unless the AI turned it off), zero threat so the
    /// target is on the threat list, both sides in combat, then on the first entry the combat
    /// start point, the AI's aggro hook and the assistance call; chase when combat movement is on.
    /// </summary>
    public bool AttackStart(Creature creature, Unit target)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(target);
        if (!_creatures.ContainsKey(creature.Guid) || !creature.IsAlive || creature.IsEvading
            || !target.IsAlive || !ReferenceEquals(target.Map, Map) || !Map.Combat.Hooks.CanAttack(creature, target)
            || creature.ReactState == CreatureReactState.Passive) // vmangos CreatureAI::AttackStart (AI/CreatureAI.cpp:67-70)
        {
            return false;
        }

        bool melee = creature.AI?.MeleeEnabled ?? creature.MeleeAllowedByTemplate;
        if (!ReferenceEquals(creature.Combat.Victim, target))
        {
            if (!Map.Combat.Attack(creature, target, melee))
            {
                return false;
            }

            SendAiReaction(creature);
        }

        creature.Combat.Threat.AddThreat(target, 0f);
        Map.Combat.SetInCombatState(creature, 0);
        Map.Combat.SetInCombatState(target, 0);
        if (!creature.HasAggroed)
        {
            creature.HasAggroed = true;
            creature.PacifiedMs = 0; // vmangos Creature::SetInCombatWith... enter combat clears the temporary pacify (Creature.cpp:3665)
            creature.CombatStart = new CreatureHome(creature.X, creature.Y, creature.Z, creature.Orientation);
            creature.AI?.OnAggro(target);
            if (!creature.IsAlive || creature.IsEvading)
            {
                return true; // the aggro script killed or reset it
            }

            CallAssistance(creature, target);
        }

        ApplyCombatMovement(creature);
        return true;
    }

    /// <summary>
    /// vmangos Creature::EnterCombatWithTarget (Objects/Creature.cpp:4078-4087), what proximity aggro calls: a creature without a victim
    /// starts attacking <paramref name="target"/> through its AI; one that already fights somebody else only adds the target to its
    /// threat list.
    /// </summary>
    public void EnterCombatWithTarget(Creature creature, Unit target)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(target);
        if (creature.Combat.Victim is null)
        {
            if (creature.AI is { } ai)
            {
                ai.AttackStart(target);
            }
            else
            {
                AttackStart(creature, target);
            }
        }
        else if (!ReferenceEquals(creature.Combat.Victim, target) && target.IsAlive && ReferenceEquals(target.Map, Map))
        {
            creature.Combat.Threat.AddThreat(target, 0f);
            Map.Combat.SetInCombatState(target, 0);
        }
    }

    /// <summary>
    /// vmangos Creature::SendAIReaction(AI_REACTION_HOSTILE) from Unit::Attack (Objects/Unit.cpp:4535-4537): the creature tells everyone
    /// who sees it that it attacks, and the client plays its aggro sound. SMSG_AI_REACTION: guid, u32 reaction (gtker wow_messages
    /// smsg_ai_reaction.wowm).
    /// </summary>
    private void SendAiReaction(Creature creature) => SendAiReaction(creature, AiReaction.Hostile);

    /// <summary>SMSG_AI_REACTION with <paramref name="reaction"/> (alert for a noticed stealthed player, hostile for an attack).</summary>
    private void SendAiReaction(Creature creature, AiReaction reaction)
    {
        if (_options.SendAiReaction)
        {
            Map.BroadcastToObservers(creature, WorldOpcode.SmsgAiReaction, CreatureAiReactionPackets.Build(creature.Guid, reaction));
        }
    }

    /// <summary>
    /// vmangos Unit::SelectHostileTarget (Objects/Unit.cpp:7544-7612): the creature's victim for this update. In order: a
    /// creature still in its respawn pacify chooses nothing (no evade, :7559-7561); a taunter that is still a valid target wins
    /// (<see cref="ThreatList.GetTauntTarget"/>, :7563); otherwise the threat list picks (110 % in melee / 130 % at range, second-choice
    /// targets last); a NO_THREAT_LIST creature sticks to its current victim (:7569-7571). A chosen target is attacked unless the
    /// creature is stunned, confused or fleeing (:7573-7581, the sheep/fear fix). Without a target a NO_THREAT_LIST creature
    /// just returns false, a creature that is out of combat, taunted or charmed returns false, one that is not chasing and still
    /// has a targetable attacker (a pet sent at a far target) returns false, and anything else evades (:7584-7611).
    /// Returns whether the creature still has a victim.
    /// <para>
    /// Limits: stun / fear / confuse / feign death are read from the unit flags, not from aura holders; "prevents fleeing"
    /// and the pending-stun state are not modelled; second-choice targets are feared or confused units only (damage-immune,
    /// breakable-CC and totem rules need the aura engine and a spell catalog the host does not have).
    /// </para>
    /// </summary>
    public bool SelectHostileTarget(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!creature.IsAlive || creature.IsEvading || !_creatures.ContainsKey(creature.Guid) || creature.IsTempPacified)
        {
            return false;
        }

        UnitCombat combat = creature.Combat;
        bool noThreatList = (creature.Template.Behaviour & CreatureBehaviourFlags.NoThreatList) != 0;

        Unit? target = combat.Threat.GetTauntTarget(guid => Map.FindObject(guid) as Unit, u => IsValidHostileTarget(creature, u));
        if (target is null && combat.HasThreatList)
        {
            target = combat.Threat.SelectVictim(
                u => IsValidHostileTarget(creature, u),
                u => MapCombat.CanReachWithMeleeAutoAttack(creature, u),
                u => IsOutOfThreatArea(creature, u),
                static u => (u.UnitFlags & (UnitFlags.Confused | UnitFlags.Fleeing)) != 0);
        }

        if (target is null && noThreatList)
        {
            target = combat.Victim;
        }

        if (target is not null)
        {
            if ((creature.UnitFlags & LostControl) == 0)
            {
                if (!ReferenceEquals(combat.Victim, target) && Map.Combat.Attack(creature, target, creature.AI?.MeleeEnabled ?? creature.MeleeAllowedByTemplate))
                {
                    SendAiReaction(creature);
                }

                ApplyCombatMovement(creature);
                if (IsTargetUnreachableForTooLong(creature))
                {
                    GiveUpUnreachableTarget(creature, target);
                    return false;
                }
            }

            return true;
        }

        if (noThreatList)
        {
            return false; // like a player: the five second combat timer ends it
        }

        if (!combat.IsInCombat || combat.Threat.HasTauntCasters || !creature.CharmerGuid.IsEmpty)
        {
            return false;
        }

        if (creature.Motion.CurrentType != MovementGeneratorType.Chase)
        {
            foreach (Unit attacker in combat.Attackers)
            {
                if (ReferenceEquals(attacker.Map, Map) && IsValidHostileTarget(creature, attacker))
                {
                    return false;
                }
            }
        }

        EnterEvadeMode(creature);
        return false;
    }

    /// <summary>
    /// vmangos Creature::IsOutOfThreatArea (Objects/Creature.cpp:2796-2815), the soft leash. Never with NO_LEASH_EVADE or in an
    /// instanceable map; no target counts as out. Otherwise the threat area is a sphere around where the fight began with radius
    /// <c>max(1.5 x aggro radius, ThreatRadius)</c>; the target is outside when neither the creature nor the target is inside it
    /// and the leash extension clock is more than <see cref="CreatureOptions.LeashExtensionSeconds"/> whole seconds old. The clock
    /// starts at the first check of a fight (so a fight cannot leash in its first 12 s), is refreshed while the creature is
    /// crowd controlled, is shared with creatures that joined through its assistance call, and is cleared when combat stops.
    /// </summary>
    public bool IsOutOfThreatArea(Creature creature, Unit? target)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if ((creature.Template.Behaviour & CreatureBehaviourFlags.NoLeashEvade) != 0 || Map.Combat.Hooks.IsInstanceable(Map.MapId))
        {
            return false;
        }

        if (target is null)
        {
            return true;
        }

        if (!ReferenceEquals(target.Map, creature.Map))
        {
            return false;
        }

        CreatureHome anchor = creature.CombatStart ?? creature.Home;
        float radius = MathF.Max(GetAttackDistance(creature, target) * 1.5f, _options.ThreatRadius);
        bool inThreatArea = WithinDistance3d(creature, anchor, radius) || WithinDistance3d(target, anchor, radius);
        return !inThreatArea && LeashExtensionSeconds(creature) + _options.LeashExtensionSeconds < _clockMs / 1000;
    }

    private static bool WithinDistance3d(WorldObject obj, CreatureHome point, float distance)
    {
        float dx = obj.X - point.X;
        float dy = obj.Y - point.Y;
        float dz = obj.Z - point.Z;
        return (dx * dx) + (dy * dy) + (dz * dz) < distance * distance;
    }

    /// <summary>The leash extension clock in whole seconds, started now when this is the first look (vmangos GetLastLeashExtensionTime).</summary>
    private long LeashExtensionSeconds(Creature creature) => (creature.LeashClock ??= new LeashExtensionClock { Seconds = _clockMs / 1000 }).Seconds;

    /// <summary>vmangos Creature::UpdateLeashExtensionTime: a crowd-controlled creature cannot leash.</summary>
    private void RefreshLeashExtension(Creature creature) => (creature.LeashClock ??= new LeashExtensionClock()).Seconds = _clockMs / 1000;

    /// <summary>
    /// The creature's periodic combat checks (vmangos Creature::Update, Objects/Creature.cpp:976-993): every
    /// <see cref="CreatureOptions.LeashCheckIntervalMs"/> of world time, a crowd-controlled creature refreshes its leash extension
    /// clock and one with a template leash range farther than that from where the fight began evades (the hard leash). Returns
    /// whether it evaded (the AI then skips its update).
    /// </summary>
    private bool CheckHardLeash(Creature creature, uint diffMs)
    {
        if (!creature.Combat.IsInCombat || _options.LeashCheckIntervalMs == 0 || _clockMs % _options.LeashCheckIntervalMs > diffMs)
        {
            return false;
        }

        if ((creature.UnitFlags & LostControl) != 0)
        {
            RefreshLeashExtension(creature);
        }

        float leash = creature.Template.Leash;
        if (leash > 0 && !WithinDistance3d(creature, creature.CombatStart ?? creature.Home, leash))
        {
            EnterEvadeMode(creature);
            return true;
        }

        return false;
    }

    // --- unreachable target ------------------------------------------------------------------------

    /// <summary>
    /// <see cref="ICreaturePathQuery"/>: <see cref="FindPath"/> plus the pathfinder's verdict. No path and a partial path
    /// (<see cref="PathType.Incomplete"/>, the route ends at the point nearest the destination) are unreachable; a straight line
    /// without navigation data, a shortcut and a complete path are reachable. World thread; no allocation beyond the path itself.
    /// </summary>
    IReadOnlyList<Vector3> ICreaturePathQuery.FindPath(Creature creature, Vector3 destination, out bool reachable)
    {
        var start = new Vector3(creature.X, creature.Y, creature.Z);
        PathResult path = Map.Collision.FindPath(start, destination);
        if (!path.HasPath)
        {
            reachable = false;
            return [destination];
        }

        reachable = (path.Type & PathType.Incomplete) == 0;
        var corners = new List<Vector3>(path.Points.Count - 1);
        for (int i = 1; i < path.Points.Count; i++)
        {
            corners.Add(path.Points[i]);
        }

        return corners;
    }

    /// <summary>
    /// Whether the creature's chase has reported its victim unreachable for at least <see cref="CreatureOptions.UnreachableTargetEvadeMs"/>
    /// (0 turns the rule off). Only a chase on top of the stack counts: a creature that does not move in combat has nothing to be
    /// unreachable, as in vmangos (Creature.cpp:1017-1040 reads the current generator's IsReachable).
    /// </summary>
    public bool IsTargetUnreachableForTooLong(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        return _options.UnreachableTargetEvadeMs > 0
            && creature.Motion.Top is TargetedMovementGenerator { Type: MovementGeneratorType.Chase, IsReachable: false } chase
            && chase.UnreachableMs >= _options.UnreachableTargetEvadeMs;
    }

    /// <summary>
    /// mangos Unit::SelectHostileTarget on an unreachable victim (Object/UnitThreat.cpp:342-361): alone on the threat list the
    /// creature evades; otherwise the victim is dropped from the list (threat -101 %) and the attack stops without leaving combat,
    /// so the next selection picks another target. The reference also strips its taunt auras; this host has no aura removal seam
    /// for creatures (docs/areas/creature-ai.md), so a taunter that is unreachable is dropped from the list only.
    /// </summary>
    private void GiveUpUnreachableTarget(Creature creature, Unit target)
    {
        ThreatList threat = creature.Combat.Threat;
        if (threat.Entries.Count < 2) // the reference counts the online list (getThreatList().size())
        {
            EnterEvadeMode(creature);
            return;
        }

        threat.ModifyThreatPercent(target, -101);
        Map.Combat.AttackStop(creature, targetSwitch: true);
        creature.Motion.Remove(MovementGeneratorType.Chase);
    }

    /// <summary>
    /// Turn chasing on or off for the current victim (cmangos EventAI COMBAT_MOVEMENT): with it
    /// on the creature chases; off it stops where it is. Flight, point and home moves keep running.
    /// </summary>
    public void ApplyCombatMovement(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!creature.IsAlive || creature.IsEvading)
        {
            return;
        }

        MovementGeneratorType current = creature.Motion.CurrentType;
        if (current is MovementGeneratorType.Fleeing or MovementGeneratorType.Confused or MovementGeneratorType.Point or MovementGeneratorType.Home)
        {
            return;
        }

        if (creature.Combat.Victim is { } victim && (creature.AI?.CombatMovement ?? true))
        {
            if (creature.AI is CreatureEventAI { CurrentRangedMode: true, ChaseDistance: > 0 } ranged)
            {
                creature.Motion.MoveChase(victim, ranged.ChaseDistance);
            }
            else
            {
                creature.Motion.MoveChase(victim);
            }
        }
        else if (current == MovementGeneratorType.Chase)
        {
            creature.Motion.Remove(MovementGeneratorType.Chase);
            StopMoving(creature);
        }
    }

    /// <summary>Turn auto attack on or off against <paramref name="victim"/> (cmangos EventAI AUTO_ATTACK).</summary>
    public void SetMelee(Creature creature, Unit victim, bool melee)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(victim);
        if (melee)
        {
            Map.Combat.Attack(creature, victim, melee: true);
        }
        else if (creature.Combat.IsMeleeAttacking)
        {
            creature.Combat.IsMeleeAttacking = false;
            CombatPackets.SendToSet(creature, WorldOpcode.SmsgAttackstop, CombatPackets.AttackStop(creature.Guid, victim.Guid, false));
        }
    }

    private bool IsValidHostileTarget(Creature creature, Unit target)
        => target.IsAlive && ReferenceEquals(target.Map, Map)
            && Map.Combat.Hooks.CanAttack(creature, target);
}
