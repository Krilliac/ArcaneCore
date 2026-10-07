namespace ArcaneCore.Game.Creatures;

/// <summary>Which point the intermediate offsets of an SMSG_MONSTER_MOVE spline are measured from.</summary>
public enum MonsterMoveOffsetBase
{
    /// <summary>
    /// Retail: each intermediate point is written as <c>destination - point</c> (vmangos Movement/spline/packet_builder.cpp:98;
    /// mangos-classic Movement/packet_builder.cpp:102).
    /// </summary>
    Destination,

    /// <summary>Legacy ArcaneCore layout (<c>middle - point</c>, middle = start/destination midpoint); rollback only.</summary>
    Midpoint,
}

public sealed partial class CreatureOptions
{
    /// <summary>Creature movement fidelity switches (<c>Creatures:Movement</c>). Every default is retail except <see cref="CreatureMovementOptions.MissingWaypointPathFallback"/>.</summary>
    public CreatureMovementOptions Movement { get; } = new();
}

/// <summary>
/// <c>Creatures:Movement:*</c>. A non-default value is a deliberate deviation from retail 1.12.1 and is documented in
/// docs/areas/creature-movement-spawns.md.
/// </summary>
public sealed class CreatureMovementOptions
{
    /// <summary>
    /// <c>Creatures:Movement:MonsterMoveOffsetBase</c>. <see cref="MonsterMoveOffsetBase.Destination"/> is retail; the
    /// midpoint layout is the pre-fidelity ArcaneCore behaviour, kept only as a rollback switch.
    /// </summary>
    public MonsterMoveOffsetBase MonsterMoveOffsetBase { get; set; } = MonsterMoveOffsetBase.Destination;

    /// <summary>
    /// <c>Creatures:Movement:RunDuringWanderChancePercent</c>: for a creature with the cmangos RUN_DURING_WANDER flag, the percent of
    /// random-movement legs that run (cmangos MotionGenerators/RandomMovementGenerator.cpp:135-136: <c>SetWalk(urand(0, 99) >= 15)</c>).
    /// vmangos has no such flag (its 0x20 is NO_MOVEMENT_PAUSE), so the setting only reaches creatures imported in the cmangos dialect.
    /// </summary>
    public uint RunDuringWanderChancePercent { get; set; } = 15;

    /// <summary>
    /// <c>Creatures:Movement:HonorWaypointRunColumn</c>: a waypoint node whose <c>creature_movement.Run</c> column is set is travelled at
    /// run speed. Not retail: the column exists in neither classic-db nor vmangos (an ArcaneCore addition of the creature-AI step);
    /// vmangos waypoint legs walk unless the creature runs by default (<c>SetWalk(!UNIT_STATE_RUNNING ...)</c>, Movement/WaypointMovementGenerator.cpp:240).
    /// </summary>
    public bool HonorWaypointRunColumn { get; set; }

    /// <summary>
    /// <c>Creatures:Movement:EvadeRestoresFullHealth</c>: a creature entering evade mode gets full health and mana at once. Retail (false):
    /// vmangos CreatureAI::EnterEvadeMode sets neither (AI/CreatureAI.cpp:323-346); health and mana return through the creature's own
    /// regeneration, a third of the maximum every 5 s (Objects/Creature.cpp:1087-1160).
    /// </summary>
    public bool EvadeRestoresFullHealth { get; set; }

    /// <summary>
    /// <c>Creatures:Movement:MissingWaypointPathFallback</c>: what a creature with waypoint movement but no path (neither its spawn's
    /// <c>creature_movement</c> rows nor its entry's <c>creature_movement_template</c> path) does. Both references leave it standing
    /// (vmangos WaypointMovementGenerator::LoadPath, Movement/WaypointMovementGenerator.cpp:47-52, logs and keeps an empty path);
    /// that is <see cref="MissingWaypointPathFallback.Idle"/>. The default, <see cref="MissingWaypointPathFallback.Random"/>, is a
    /// deliberate deviation: the creature wanders within its spawn's wander distance (5 yd when the spawn has none) so a content
    /// gap does not leave a patrol frozen in place. The content audit reports such spawns once at load either way.
    /// </summary>
    public MissingWaypointPathFallback MissingWaypointPathFallback { get; set; } = MissingWaypointPathFallback.Random;
}

/// <summary>The default movement of a waypoint creature without a path (<see cref="CreatureMovementOptions.MissingWaypointPathFallback"/>).</summary>
public enum MissingWaypointPathFallback
{
    /// <summary>Random movement within the spawn's wander distance (ArcaneCore default).</summary>
    Random,

    /// <summary>Stand at the spawn point (both references).</summary>
    Idle,
}
