using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>The movement and place queries EventAI needs from the map (ACTION_T_CHANGE_MOVEMENT, the SPAWNED zone condition).</summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>cmangos CHANGE_MOVEMENT_FLAG_WAYPOINT_PATH: the path comes from <c>waypoint_path</c> (not supported here).</summary>
    public const uint ChangeMovementFlagWaypointPath = 0x2;

    /// <summary>
    /// cmangos ACTION_T_CHANGE_MOVEMENT (AI/EventAI/CreatureEventAI.cpp:1164-1206): 0 pushes the idle generator unless one is on top
    /// (MotionMaster::MoveIdle, MotionGenerators/MotionMaster.cpp:273-277); 1 mutates a walking wander of <paramref name="wanderOrPathId"/> yards
    /// around where the creature stands on top of the stack, the default staying beneath it (MoveRandomAroundPoint, :279-293); 2 stops the
    /// creature and makes waypoint path <paramref name="wanderOrPathId"/> its movement in place of everything (StopMoving, Clear(false, true),
    /// MoveWaypoint): 0 is the creature's default path (its spawn's <c>creature_movement</c>, else the entry's path 0; WaypointManager
    /// GetPathFromOrigin with PATH_NO_PATH), another id is that path of the entry's <c>creature_movement_template</c>; a path that does not
    /// exist leaves the creature standing (the waypoint generator of an empty path). Returns false, changing nothing, for what is not supported:
    /// a path from <c>waypoint_path</c> (flag 0x2), the path (3) and linear waypoint (4) movement types and any other type. The "as default"
    /// flag (0x1) only records the type in cmangos (m_defaultMovement, read by nothing in the EventAI of classic) and is ignored here.
    /// </summary>
    public bool ChangeMovement(Creature creature, uint movementType, uint wanderOrPathId, uint flags)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!_creatures.ContainsKey(creature.Guid))
        {
            return false;
        }

        switch (movementType)
        {
            case 0:
                creature.Motion.MoveIdle();
                return true;
            case 1:
                creature.Motion.MoveRandom(new RandomMovementGenerator(wanderOrPathId, new CreatureHome(creature.X, creature.Y, creature.Z, creature.Orientation),
                    run: false));
                return true;
            case 2:
            {
                IReadOnlyList<CreatureWaypoint> path = (flags & ChangeMovementFlagWaypointPath) != 0
                    ? _content.GetWaypointPath(wanderOrPathId)
                    : wanderOrPathId == 0
                        ? _content.ResolveWaypointPath(creature.Spawn?.Guid ?? 0, creature.Template.Entry).Points
                        : _content.GetEntryWaypoints(creature.Template.Entry, wanderOrPathId);
                StopMoving(creature);
                creature.Motion.Initialize(path.Count > 0 ? new WaypointMovementGenerator(path) : IdleMovementGenerator.Instance, this, start: true);
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// The zone and area the creature stands in (vmangos/cmangos <c>WorldObject::GetZoneAndAreaId</c>): the
    /// <see cref="CreatureAiServices.ZoneAndAreaOf"/> seam, else the map's terrain; (0, 0) where the terrain does not know.
    /// </summary>
    public (uint ZoneId, uint AreaId) ZoneAndAreaOf(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        return _ai.ZoneAndAreaOf?.Invoke(creature) ?? Map.GetZoneAndAreaId(creature.X, creature.Y, creature.Z);
    }
}
