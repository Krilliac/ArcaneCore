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
    /// <summary>Creature movement fidelity switches (<c>Creatures:Movement</c>). Every default is retail.</summary>
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
}
