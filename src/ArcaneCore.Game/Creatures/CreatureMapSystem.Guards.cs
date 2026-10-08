using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
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
    /// modelled) appears 5 yd east of it (<see cref="GuardSummonPoint"/>), attacks the enemy and despawns after 2 minutes alive and out of combat
    /// (TEMPSUMMON_TIMED_OR_DEAD_DESPAWN: the timer starts again while it fights). Returns whether the call was made.
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
            (float x, float y, float z) = GuardSummonPoint(civilian);
            Creature guard = SpawnTemporary(template, x, y, z, 0);
            AddTimedSummon(guard, GuardPostTable.GuardDespawnMs, SummonTimer.OutOfCombat); // TEMPSUMMON_TIMED_OR_DEAD_DESPAWN, 2 minutes
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
    /// vmangos <c>GetNearPoint(civilian, x, y, z, 0, 5, 0)</c> (GuardMgr.cpp:450) with DetectPosCollision on, its default (World.cpp:751;
    /// Objects/Object.cpp:2726-2825): the civilian's bounding radius + 5 yd at the absolute angle 0, on the ground under it. When the
    /// civilian cannot see that point, the first point at the same distance it can see, going round both sides of the angle in 45 degree
    /// steps; when it sees none, the first point. The ObjectPosSelector's avoidance of spots other objects already take is not ported.
    /// </summary>
    private (float X, float Y, float Z) GuardSummonPoint(Creature civilian)
    {
        float range = civilian.BoundingRadius + GuardPostTable.SummonDistance;
        (float X, float Y, float Z) first = default;
        for (int step = 0; step < 8; step++)
        {
            int quarter = (step + 1) / 2; // 0, +45, -45, +90, -90, +135, -135, 180 degrees
            float angle = (step % 2 == 1 ? 1 : -1) * quarter * MathF.PI / 4f;
            float x = civilian.X + (range * MathF.Cos(angle));
            float y = civilian.Y + (range * MathF.Sin(angle));
            float z = _height.GetHeight(Map.MapId, x, y, civilian.Z) ?? civilian.Z;
            if (step == 0)
            {
                first = (x, y, z);
            }

            if (Map.Collision.IsWithinLineOfSight(civilian, x, y, z))
            {
                return (x, y, z);
            }
        }

        return first;
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
