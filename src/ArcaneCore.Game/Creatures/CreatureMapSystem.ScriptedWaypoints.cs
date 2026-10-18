using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// A script puts a creature back on a cmangos <c>waypoint_path</c> at a chosen point: <c>GetMotionMaster()->Clear(false, true);
/// MoveWaypoint(pathId, PATH_FROM_WAYPOINT_PATH); SetNextWaypoint(pointId)</c> (mangos-classic npc_cork_gizelton, kalimdor/desolace.cpp:516-519).
/// A formation leader's formation takes the path as its movement, as a dbscript MOVEMENT step does (FormationData::SetMovementInfo).
/// </summary>
public sealed partial class CreatureMapSystem
{
    /// <returns>Whether the path exists; a missing path leaves the creature's movement as it is.</returns>
    internal bool ResumeWaypointPath(Creature mover, uint pathId, uint pointId)
    {
        ArgumentNullException.ThrowIfNull(mover);
        IReadOnlyList<CreatureWaypoint> path = _content.GetWaypointPath(pathId);
        if (path.Count == 0)
        {
            ReportEscortWithoutPath(mover, $"waypoint_path {pathId} (resume at point {pointId})");
            return false;
        }

        int start = 0;
        for (int i = 0; i < path.Count; i++)
        {
            if (path[i].Point == pointId)
            {
                start = i;
                break;
            }
        }

        if (FormationOfMember(mover) is { } formation && formation.SlotOf(mover) is { SlotId: 0 })
        {
            formation.MovementType = 2;
            formation.PathId = pathId;
            formation.LastWaypointIndex = -1;
        }

        StopMoving(mover);
        mover.Motion.Initialize(new WaypointMovementGenerator(path, startIndex: start), this, start: true);
        return true;
    }
}
