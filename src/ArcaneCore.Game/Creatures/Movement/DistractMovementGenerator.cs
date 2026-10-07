using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// vmangos DistractMovementGenerator (Movement/IdleMovementGenerator.cpp:33-75): the creature stands where it is for
/// <c>durationMs</c> (a sitting creature stands up first) while the generators beneath it are interrupted, then turns back to its
/// spawn facing (vmangos <c>SetFacingTo(GetHomePositionO())</c> in Finalize) and the generator beneath resumes. The update ends it once a
/// tick is longer than what is left (vmangos <c>time_diff &gt; m_timer</c>). <see cref="MotionMaster"/> expires it whenever another
/// generator is pushed (MotionMaster::Mutate, MotionMaster.cpp:699-702). Started by the stealth alert and by SPELL_EFFECT_DISTRACT
/// (<see cref="CreatureMapSystem.Distract"/>). World thread.
/// </summary>
internal sealed class DistractMovementGenerator(uint durationMs) : ICreatureMovementGenerator
{
    private uint _remainingMs = durationMs;

    public MovementGeneratorType Type => MovementGeneratorType.Distract;

    public void Initialize(Creature creature, ICreatureMover mover)
    {
        if (creature.StandState != StandState.Stand)
        {
            creature.StandState = StandState.Stand;
        }
    }

    /// <summary>vmangos Reset = Initialize: the timer is not restarted.</summary>
    public void Resume(Creature creature, ICreatureMover mover) => Initialize(creature, mover);

    public bool Update(Creature creature, ICreatureMover mover, uint diffMs)
    {
        if (diffMs > _remainingMs)
        {
            return false;
        }

        _remainingMs -= diffMs;
        return true;
    }

    public void Finish(Creature creature, ICreatureMover mover, bool completed)
    {
        if (creature.IsAlive)
        {
            mover.SetFacingTo(creature, creature.Home.Orientation);
        }
    }
}
