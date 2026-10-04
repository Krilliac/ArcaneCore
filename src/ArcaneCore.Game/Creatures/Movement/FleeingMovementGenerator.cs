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
    private bool _interrupted;

    public MovementGeneratorType Type => MovementGeneratorType.Fleeing;

    public void Initialize(Creature creature, ICreatureMover mover)
    {
        _interrupted = false;
        if (CrowdControlGates.IsHeldInPlace(creature))
        {
            return; // the first Update after the hold lifts starts the first leg (the pause timer is 0)
        }

        // The plain flight sets the flag when it starts; here it follows the auras. Resume (a pushed point ended) comes
        // through here too, and the aura may have ended under the point, before the hook sees it.
        bool aurasHoldFlag = (creature.UnitFlags & UnitFlags.Fleeing) != 0;
        _flight.Initialize(creature, mover);
        RestoreFlag(creature, aurasHoldFlag);
    }

    public void Interrupt(Creature creature, ICreatureMover mover)
    {
        // The plain flight clears the flag; here it follows the auras. Setting it back unconditionally would stick it on a
        // creature whose fear ended before the push, since no aura is left to clear it.
        bool aurasHoldFlag = (creature.UnitFlags & UnitFlags.Fleeing) != 0;
        _flight.Interrupt(creature, mover);
        RestoreFlag(creature, aurasHoldFlag);
        _interrupted = true;
    }

    public void Finish(Creature creature, ICreatureMover mover, bool completed)
    {
        if (_interrupted)
        {
            return; // buried under a pushed generator: the running spline is that generator's, and the flag already follows the auras
        }

        bool aurasHoldFlag = (creature.UnitFlags & UnitFlags.Fleeing) != 0;
        _flight.Finish(creature, mover, completed);
        RestoreFlag(creature, aurasHoldFlag);
    }

    /// <summary>Leave <c>UnitFlags.Fleeing</c> as the auras had it before the wrapped flight touched it.</summary>
    private static void RestoreFlag(Creature creature, bool aurasHoldFlag)
    {
        if (aurasHoldFlag != ((creature.UnitFlags & UnitFlags.Fleeing) != 0))
        {
            creature.UnitFlags = aurasHoldFlag ? creature.UnitFlags | UnitFlags.Fleeing : creature.UnitFlags & ~UnitFlags.Fleeing;
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
