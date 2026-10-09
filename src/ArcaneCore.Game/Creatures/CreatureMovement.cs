using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

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

    /// <summary>A path from the creature to <paramref name="destination"/> (pathfinder seam; straight line by default).</summary>
    IReadOnlyList<Vector3> FindPath(Creature creature, Vector3 destination);

    /// <summary>A uniform random number in [0, 1).</summary>
    double NextDouble();

    /// <summary>A uniform random integer in [min, max] (vmangos urand).</summary>
    int URand(int min, int max);

    /// <summary>Ground height near (x, y, z), or null when unknown.</summary>
    float? GetHeight(uint mapId, float x, float y, float z);

    /// <summary>Whether the creature is casting a spell (it stands still while it does).</summary>
    bool IsCasting(Creature creature);

    /// <summary>The <c>Creatures:Movement</c> switches.</summary>
    CreatureMovementOptions MovementOptions { get; }

    /// <summary>A generator that ends by itself finished (home reached, point reached).</summary>
    void OnMovementFinished(Creature creature, MovementGeneratorType type, uint pointId);

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
