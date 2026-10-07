using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Calling the guards (static flag CALLS_GUARDS, 0x08000000): vmangos BasicAI::MoveInLineOfSight and SummonGuard (AI/BasicAI.cpp:49-105),
/// Creature::OnEnterCombat (Objects/Creature.cpp:3689-3690), GuardMgr::SummonGuard (GuardMgr.cpp:421-457) and Creature::CallNearestGuard
/// (Creature.cpp:3932-3949). The posts and their charges are <see cref="GuardPostTable"/>.
/// </summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>
    /// vmangos BasicAI::MoveInLineOfSight, the guard half (AI/BasicAI.cpp:49-77): a creature that may call the guards on sight
    /// (<see cref="Creature.CanCallGuardsOnSight"/>) and does not attack <paramref name="who"/> itself (it cannot initiate an attack, or
    /// <paramref name="who"/> is already its victim) calls them for a player within its detection range (not its aggro radius), 3 yd of
    /// height, attackable, hostile and in sight. The proximity rules of <see cref="IsProximityAggroAllowedFor"/> apply.
    /// </summary>
    public bool CanCallGuardsOnSight(Creature creature, Unit who)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(who);
        if (who is not Player { IsGameMaster: false } player || !player.IsAlive || !ReferenceEquals(player.Map, Map)
            || !creature.CanCallGuardsOnSight || !creature.IsAlive || creature.IsEvading || !_creatures.ContainsKey(creature.Guid)
            || (creature.UnitFlags & LostControl) != 0 || !IsProximityAggroAllowedFor(creature, who))
        {
            return false;
        }

        bool canInitiateAttack = !ReferenceEquals(creature.Combat.Victim, who) && CanInitiateAttack(creature) && (creature.AI?.AggroesOnSight ?? false);
        if (canInitiateAttack)
        {
            return false; // the attack path (CanAggroOnSight) handles it, with the aggro radius
        }

        bool canFly = (creature.Template.InhabitType & 0x04) != 0; // INHABIT_AIR
        if (!canFly && MathF.Max(0f, MathF.Abs(creature.Z - who.Z) - creature.BoundingRadius - who.BoundingRadius) > CreatureAggro.MaxZDistance)
        {
            return false;
        }

        float range = creature.Template.Detection;
        return DistanceSquared(creature, who) <= range * range
            && Map.Combat.Hooks.CanAttack(creature, who)
            && _ai.Hostility.IsHostile(creature, who)
            && CanSeeForAggro(creature, who);
    }

    /// <summary>
    /// vmangos GuardMgr::SummonGuard (GuardMgr.cpp:421-457). In an area without a guard post the nearest idle friendly guard attacks
    /// <paramref name="enemy"/> (<see cref="CallNearestGuard"/>) and the call counts as made. A post that is cooling down or out of charges
    /// refuses (false: the caller may try again). Otherwise the civilian speaks its call (<see cref="GuardPostTable.GetTextId"/>, as a say)
    /// and the post's guard for the team opposite the enemy's player (else the civilian's own team, which needs Faction.dbc and is not
    /// modelled) appears 5 yd east of it, attacks the enemy and despawns after 2 minutes. Returns whether the call was made.
    /// </summary>
    public bool SummonGuard(Creature civilian, Unit enemy)
    {
        ArgumentNullException.ThrowIfNull(civilian);
        ArgumentNullException.ThrowIfNull(enemy);
        if (!civilian.IsAlive || !enemy.IsAlive || !ReferenceEquals(enemy.Map, Map))
        {
            return false;
        }

        uint areaId = AreaOf(civilian);
        GuardPostCall call = _ai.GuardPosts.TryUse(areaId, GuardTeamAgainst(enemy), _serverTime());
        switch (call.Use)
        {
            case GuardPostUse.NoPost:
                CallNearestGuard(civilian, enemy);
                return true;
            case GuardPostUse.Unavailable:
                return false;
        }

        uint? modelId = _displayModelResolver?.Invoke(civilian.DisplayId)?.ModelId;
        uint textId = GuardPostTable.GetTextId(civilian.FactionTemplate, areaId, modelId);
        if (textId != 0 && _content.Ai.FindText((int)textId) is { } text)
        {
            Say(civilian, text with { Type = 0 }, enemy); // DoScriptText(..., CHAT_TYPE_SAY)
        }

        if (call.GuardEntry != 0 && _content.FindTemplate(call.GuardEntry) is { } template)
        {
            // GetNearPoint(civilian, x, y, z, 0, 5, 0): 5 yd at the absolute angle 0.
            float x = civilian.X + GuardPostTable.SummonDistance;
            float y = civilian.Y;
            float z = _height.GetHeight(Map.MapId, x, y, civilian.Z) ?? civilian.Z;
            Creature guard = SpawnTemporary(template, x, y, z, 0);
            _summons.Add((guard, _clockMs + GuardPostTable.GuardDespawnMs));
            civilian.CalledGuard = guard.Guid;
            if (guard.AI is { } ai)
            {
                ai.AttackStart(enemy);
            }
            else
            {
                AttackStart(guard, enemy);
            }
        }

        return true;
    }

    /// <summary>
    /// vmangos Creature::CallNearestGuard (Creature.cpp:3944-3949) over NearestFriendlyGuardInRangeCheck (Maps/GridNotifiers.h:1086-1116):
    /// the nearest creature within 50 yd that is a guard, alive, out of combat, friendly to the civilian and in its line of sight
    /// attacks <paramref name="enemy"/> when it may.
    /// </summary>
    public Creature? CallNearestGuard(Creature civilian, Unit enemy)
    {
        ArgumentNullException.ThrowIfNull(civilian);
        ArgumentNullException.ThrowIfNull(enemy);
        Creature? nearest = null;
        float best = GuardPostTable.NearestGuardRadius * GuardPostTable.NearestGuardRadius;
        foreach (Creature candidate in _creatures.Values)
        {
            if (ReferenceEquals(candidate, civilian) || !candidate.IsAlive || candidate.Combat.IsInCombat
                || (candidate.Template.Behaviour & CreatureBehaviourFlags.Guard) == 0 || !_ai.Hostility.IsFriendly(candidate, civilian))
            {
                continue;
            }

            float distance = DistanceSquared(civilian, candidate);
            if (distance <= best && InLineOfSight(civilian, candidate))
            {
                best = distance;
                nearest = candidate;
            }
        }

        if (nearest?.AI is { } ai && Map.Combat.Hooks.CanAttack(nearest, enemy))
        {
            ai.AttackStart(enemy);
        }

        return nearest;
    }

    /// <summary>
    /// vmangos BasicAI::SummonedCreatureDespawn (BasicAI.cpp:85-89): once the guard a civilian called is gone (despawned or dead), the
    /// civilian may call on sight again. Checked every update of a creature that has called one.
    /// </summary>
    private void CheckCalledGuard(Creature creature)
    {
        if (creature.CalledGuard is not { } guid)
        {
            return;
        }

        if (Map.FindObject(guid) is Creature { IsAlive: true })
        {
            return;
        }

        creature.CalledGuard = null;
        if ((creature.Template.Behaviour & CreatureBehaviourFlags.CallsGuards) != 0)
        {
            creature.CanCallGuardsOnSight = true;
        }
    }

    /// <summary>vmangos BasicAI constructor and JustRespawned: a CALLS_GUARDS creature may call on sight again.</summary>
    private static void ResetGuardCall(Creature creature)
    {
        creature.CanCallGuardsOnSight = (creature.Template.Behaviour & CreatureBehaviourFlags.CallsGuards) != 0;
        creature.CalledGuard = null;
    }

    /// <summary>vmangos GuardMgr::GetTeam (GuardMgr.cpp:406-419): the team opposite the enemy's player; null when no player controls it.</summary>
    private Team? GuardTeamAgainst(Unit enemy)
    {
        return enemy.GetCharmerOrOwnerPlayerOrSelf()?.Team switch
        {
            Team.Horde => Team.Alliance,
            Team.Alliance => Team.Horde,
            _ => null,
        };
    }

    /// <summary>The creature's area id (vmangos GetAreaId): the <see cref="CreatureAiServices.AreaOf"/> seam, else the map's terrain.</summary>
    private uint AreaOf(Creature creature) => _ai.AreaOf?.Invoke(creature) ?? Map.GetZoneAndAreaId(creature.X, creature.Y, creature.Z).AreaId;
}
