using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>What stops a creature that is under a fear or confuse aura from moving at all.</summary>
internal static class CrowdControlGates
{
    /// <summary>
    /// Stunned or rooted (UNIT_FLAG_STUNNED, MOVEFLAG_ROOT): the fear and confuse generators hold still while it lasts
    /// (mangosserver ConfusedMovementGenerator::Intent ignores any other no-reaction state). The root flag of a creature is
    /// set by <c>CcState.RefreshRoot</c> from its root and stun auras.
    /// </summary>
    public static bool IsHeldInPlace(Creature creature)
        => (creature.UnitFlags & UnitFlags.Stunned) != 0 || creature.Movement.HasFlag(MovementFlags.Root);
}

/// <summary>
/// The flight of a creature under a fear aura (ModFear). It runs the same leg logic as the timed flight of
/// <see cref="FleeingMovementGenerator"/> (vmangos FleeingMovementGenerator: legs away from the fear source, a
/// 0.5-1 s pause between legs) with no duration, but the unit flag is not its own: <c>UnitFlags.Fleeing</c> is
/// derived from the live auras by <c>CcState.RefreshFear</c>, and the movement hook of the map tick starts and ends this
/// generator from that flag. So interrupting or finishing it (a pushed point, an evade clearing the stack) leaves the flag
/// as the auras set it, where the plain generator would clear it.
/// <para>
/// A creature that is stunned or rooted does not move: its spline is stopped, the leg timers stand still, and it runs
/// again when the state lifts.
/// </para>
/// Thread affinity: world thread (the creature's map). Allocation: one object pair per fear application, none per tick.
/// </summary>
internal sealed class CrowdControlFleeingMovementGenerator(Unit? source) : ICreatureMovementGenerator
{
    private readonly FleeingMovementGenerator _flight = new(source, durationMs: 0);

    public MovementGeneratorType Type => MovementGeneratorType.Fleeing;

    public void Initialize(Creature creature, ICreatureMover mover)
    {
        if (CrowdControlGates.IsHeldInPlace(creature))
        {
            return; // the first Update after the hold lifts starts the first leg (the pause timer is 0)
        }

        _flight.Initialize(creature, mover);
    }

    public void Interrupt(Creature creature, ICreatureMover mover)
    {
        _flight.Interrupt(creature, mover);
        creature.UnitFlags |= UnitFlags.Fleeing; // the flag follows the auras, not this generator
    }

    public void Finish(Creature creature, ICreatureMover mover, bool completed)
    {
        bool aurasHoldFlag = (creature.UnitFlags & UnitFlags.Fleeing) != 0;
        _flight.Finish(creature, mover, completed);
        if (aurasHoldFlag)
        {
            creature.UnitFlags |= UnitFlags.Fleeing;
        }
    }

    public bool Update(Creature creature, ICreatureMover mover, uint diffMs)
    {
        if (CrowdControlGates.IsHeldInPlace(creature))
        {
            if (creature.IsMoving)
            {
                mover.StopMoving(creature);
            }

            return true;
        }

        return _flight.Update(creature, mover, diffMs);
    }
}
