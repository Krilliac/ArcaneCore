using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// vmangos MovementGeneratorType values (Movement/MotionMaster.h:36-59) for the generators ArcaneCore runs. Cyclic (3),
/// Flight (8) and the rest of that list are not run here. The numbers are internal: they are neither persisted nor sent.
/// </summary>
public enum MovementGeneratorType : byte
{
    Idle = 0,
    Random = 1,
    Waypoint = 2,
    Confused = 5,
    Chase = 6,
    Home = 7,
    Point = 9,
    Fleeing = 10,

    /// <summary>vmangos DISTRACT_MOTION_TYPE (MotionMaster.h:50): stand facing a spot for a while.</summary>
    Distract = 11,
    Follow = 15,

    /// <summary>cmangos FORMATION_MOTION_TYPE (MotionGenerators/MotionMaster.h:78): a spawn group formation follower holding its slot.</summary>
    Formation = 21,
}

/// <summary>What a movement generator may ask of its owner (implemented by <see cref="CreatureMapSystem"/>).</summary>
internal interface ICreatureMover
{
    /// <summary>Launch a straight spline to (x, y, z) and tell the observers (SMSG_MONSTER_MOVE).</summary>
    void MoveTo(Creature creature, float x, float y, float z, bool run, float? finalOrientation);

    /// <summary>Launch a linear spline through <paramref name="path"/> (destination last) and tell the observers.</summary>
    void MovePath(Creature creature, IReadOnlyList<Vector3> path, bool run, SplineFacing facing);

    /// <summary>Stop a moving creature where it is (stop packet).</summary>
    void StopMoving(Creature creature);

    /// <summary>
    /// The path of an authored route (waypoint, point, formation, scripted move) from the creature to <paramref name="destination"/>:
    /// the mesh path, or the straight line when the mesh has no path (vmangos moves such a spline along the shortcut,
    /// <c>MoveSplineInit::Move(PathFinder*)</c>). EMPTY when the map's search budget is spent: the caller asks again on a later update,
    /// it never walks straight for want of budget.
    /// </summary>
    IReadOnlyList<Vector3> FindPath(Creature creature, Vector3 destination);

    /// <summary>
    /// Search a path for a motion that decides itself what no path means (<see cref="PathOutcome"/>). The default, for a mover without
    /// a pathfinder, is <see cref="FindPath"/> labelled <see cref="PathOutcome.Direct"/>. <paramref name="toSpawn"/> (a return home) uses the
    /// vmangos end tolerance (<see cref="Maps.Collision.PathOptions.EndTolerance"/>): a spawn point a few yards off the mesh still has a whole path.
    /// </summary>
    PlannedPath PlanPath(Creature creature, Vector3 destination, bool toSpawn = false) => new(PathOutcome.Direct, FindPath(creature, destination));

    /// <summary>
    /// Put the creature at the spot at once (vmangos <c>Unit::NearTeleportTo</c>), as an evade return does when no whole path leads home.
    /// The default only relocates.
    /// </summary>
    void TeleportHome(Creature creature, float x, float y, float z, float orientation)
        => creature.Relocate(x, y, z, orientation, creature.Movement.Time);

    /// <summary>vmangos Creature::UpdateLeashExtensionTime: the creature cannot leash for now. The default (a mover without a leash) does nothing.</summary>
    void ExtendLeash(Creature creature)
    {
    }

    /// <summary>A uniform random number in [0, 1).</summary>
    double NextDouble();

    /// <summary>A uniform random integer in [min, max] (vmangos urand).</summary>
    int URand(int min, int max);

    /// <summary>
    /// A random point on the navmesh within <paramref name="radius"/> of <paramref name="center"/> that the creature can walk to
    /// (steep slopes excluded, vmangos <c>MOVE_EXCLUDE_STEEP_SLOPES</c>). False when there is no mesh data for the spot: the caller keeps its
    /// own point choice. Nothing is drawn from the random source in that case.
    /// </summary>
    bool TryGetRandomMeshPoint(Creature creature, Vector3 center, float radius, out Vector3 point)
    {
        point = center;
        return false;
    }

    /// <summary>
    /// Walk the navmesh from <paramref name="from"/> toward <paramref name="to"/> (steep slopes count as walls): <paramref name="reached"/>
    /// is where the creature may go, on the mesh; it is <paramref name="to"/> projected onto the mesh when nothing is in the way. False when
    /// there is no mesh data for the spot (<paramref name="reached"/> is then <paramref name="to"/>).
    /// </summary>
    bool TryRaycastMesh(Creature creature, Vector3 from, Vector3 to, out Vector3 reached)
    {
        reached = to;
        return false;
    }

    /// <summary>Ground height near (x, y, z), or null when unknown.</summary>
    float? GetHeight(uint mapId, float x, float y, float z);

    /// <summary>Whether the creature is casting a spell (it stands still while it does).</summary>
    bool IsCasting(Creature creature);

    /// <summary>The <c>Creatures:Movement</c> switches.</summary>
    CreatureMovementOptions MovementOptions { get; }

    /// <summary>A generator that ends by itself finished (home reached, point reached).</summary>
    void OnMovementFinished(Creature creature, MovementGeneratorType type, uint pointId);

    /// <summary>Run a DB script attached to a reached waypoint.</summary>
    void OnWaypointScript(Creature creature, uint scriptId, ObjectGuid targetGuid) { }

    /// <summary>
    /// Turn the creature to <paramref name="angle"/> where it stands (vmangos Unit::SetFacingTo, Objects/Unit.cpp:2785-2794: a facing spline
    /// with no path). The default only sets the orientation; <see cref="CreatureMapSystem"/> also tells the observers.
    /// </summary>
    void SetFacingTo(Creature creature, float angle) => creature.Orientation = Creature.NormalizeOrientation(angle);
}

/// <summary>
/// One entry of a creature's <see cref="MotionMaster"/> stack (vmangos MovementGenerator). The
/// owner advances the spline, then calls <see cref="Update"/> on the top generator every map
/// tick while the creature is alive.
/// </summary>
internal interface ICreatureMovementGenerator
{
    MovementGeneratorType Type { get; }

    /// <summary>Start (vmangos Initialize): at spawn, at respawn, or when pushed.</summary>
    void Initialize(Creature creature, ICreatureMover mover);

    /// <summary>Another generator was pushed on top (vmangos Interrupt).</summary>
    void Interrupt(Creature creature, ICreatureMover mover)
    {
    }

    /// <summary>The generator above was removed and this one runs again (vmangos Reset).</summary>
    void Resume(Creature creature, ICreatureMover mover) => Initialize(creature, mover);

    /// <summary>Removed from the stack (vmangos Finalize); <paramref name="completed"/> when its own update ended it.</summary>
    void Finish(Creature creature, ICreatureMover mover, bool completed)
    {
    }

    /// <summary>One tick; false when the generator is done and should be removed.</summary>
    bool Update(Creature creature, ICreatureMover mover, uint diffMs);

    /// <summary>
    /// Where evade sends the creature when this generator is the default one beneath the home move,
    /// or null for the built-in rule (vmangos MovementGenerator::GetResetPosition, MovementGenerator.h:64;
    /// random and waypoint movement override it, RandomMovementGenerator.cpp:131 and
    /// WaypointMovementGenerator.cpp:292; HomeMovementGenerator.cpp:52 consumes it).
    /// </summary>
    CreatureHome? GetResetPosition(Creature creature) => null;

    /// <summary>
    /// False once the generator has found its target unreachable (vmangos MovementGenerator::IsReachable,
    /// MovementGenerator.h:61, overridden by TargetedMovementGenerator.h:53; Creature.cpp:1016 uses it for
    /// the unreachable-target evade).
    /// </summary>
    bool IsReachable => true;
}

/// <summary>vmangos IdleMovementGenerator: stays put.</summary>
internal sealed class IdleMovementGenerator : ICreatureMovementGenerator
{
    public static readonly IdleMovementGenerator Instance = new();

    public MovementGeneratorType Type => MovementGeneratorType.Idle;

    public void Initialize(Creature creature, ICreatureMover mover)
    {
    }

    public bool Update(Creature creature, ICreatureMover mover, uint diffMs) => true;
}
