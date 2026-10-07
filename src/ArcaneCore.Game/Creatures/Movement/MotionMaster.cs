using System.Numerics;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// A creature's movement generator stack (vmangos MotionMaster). The bottom entry is the
/// default generator from the spawn (idle, random or waypoint) and is never removed; combat and
/// scripts push chase, follow, flee, home and point generators on top. Only the top generator is
/// updated. A generator whose update returns false is removed and the one beneath resumes.
/// Thread affinity: world thread (the creature's map).
/// </summary>
public sealed class MotionMaster
{
    private readonly Creature _owner;
    private readonly List<ICreatureMovementGenerator> _stack = [];
    private ICreatureMovementGenerator _default = IdleMovementGenerator.Instance;
    private ICreatureMover? _mover;

    internal MotionMaster(Creature owner) => _owner = owner;

    /// <summary>The type of the generator that currently drives the creature.</summary>
    public MovementGeneratorType CurrentType => Top.Type;

    /// <summary>The default (bottom) generator's type.</summary>
    public MovementGeneratorType DefaultType => _default.Type;

    /// <summary>Generators above the default, bottom first.</summary>
    public IReadOnlyList<MovementGeneratorType> ActiveTypes => [.. _stack.Select(g => g.Type)];

    /// <summary>The unit a chase or follow generator on top is after.</summary>
    public Unit? TargetedUnit => Top is TargetedMovementGenerator targeted ? targeted.Target : null;

    internal ICreatureMovementGenerator Top => _stack.Count > 0 ? _stack[^1] : _default;

    internal ICreatureMovementGenerator Default => _default;

    /// <summary>False when the generator on top has found its target unreachable (always true for generators that never do).</summary>
    public bool IsReachable => Top.IsReachable;

    /// <summary>Install the default generator and start it (spawn, respawn).</summary>
    internal void Initialize(ICreatureMovementGenerator defaultGenerator, ICreatureMover mover, bool start)
    {
        _mover = mover;
        ClearStack(complete: false);
        _default = defaultGenerator;
        if (start)
        {
            _default.Initialize(_owner, mover);
        }
    }

    /// <summary>Drop every pushed generator and resume the default (vmangos Clear(true)).</summary>
    public void Clear()
    {
        if (_mover is null || _stack.Count == 0)
        {
            return;
        }

        ClearStack(complete: false);
        ResumeTop(_mover);
    }

    /// <summary>Forget everything without moving (death, removal from the map).</summary>
    internal void Reset()
    {
        ClearStack(complete: false);
    }

    /// <summary>vmangos MoveChase: run after <paramref name="target"/>; replaces a chase of another target.</summary>
    public void MoveChase(Unit target)
        => MoveChase(target, null);

    /// <summary>Move to melee reach or hold a requested caster distance from <paramref name="target"/>.</summary>
    internal void MoveChase(Unit target, float? distance)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Top is TargetedMovementGenerator chase && ReferenceEquals(chase.Target, target)
            && ((distance is null && chase is ChaseMovementGenerator) || (distance is not null && chase is RangedMovementGenerator ranged && ranged.Distance == distance.Value)))
        {
            return;
        }

        RemoveTop(MovementGeneratorType.Chase);
        Push(distance is { } yards ? new RangedMovementGenerator(target, yards) : new ChaseMovementGenerator(target));
    }

    /// <summary>vmangos MoveFollow: keep <paramref name="distance"/> yd at <paramref name="angle"/> from the target's facing.</summary>
    public void MoveFollow(Unit target, float distance, float angle)
    {
        ArgumentNullException.ThrowIfNull(target);
        RemoveTop(MovementGeneratorType.Follow);
        Push(new FollowMovementGenerator(target, distance, angle));
    }

    /// <summary>vmangos MoveFleeing / MoveTimedFleeing: run from <paramref name="source"/> (0 ms = until removed).</summary>
    public void MoveFleeing(Unit? source, uint durationMs)
    {
        RemoveTop(MovementGeneratorType.Fleeing);
        Push(new FleeingMovementGenerator(source, durationMs));
    }

    /// <summary>vmangos MoveTargetedHome: clear the stack and run back to <paramref name="home"/>.</summary>
    public void MoveTargetedHome(CreatureHome home)
    {
        ClearStack(complete: false);
        Push(new HomeMovementGenerator(home));
    }

    /// <summary>vmangos MovePoint: go to a point; the AI's movement-inform gets <paramref name="id"/>.</summary>
    public void MovePoint(uint id, float x, float y, float z, bool run)
        => Push(new PointMovementGenerator(id, new Vector3(x, y, z), run));

    /// <summary>Remove the top generator if it is of <paramref name="type"/> (the one beneath resumes).</summary>
    public bool Remove(MovementGeneratorType type)
    {
        if (_stack.Count == 0 || _stack[^1].Type != type || _mover is null)
        {
            return false;
        }

        Pop(completed: false);
        return true;
    }

    /// <summary>One tick of the top generator (the owner advanced the spline first).</summary>
    internal void Update(uint diffMs)
    {
        if (_mover is null)
        {
            return;
        }

        ICreatureMovementGenerator top = Top;
        if (!top.Update(_owner, _mover, diffMs) && _stack.Count > 0 && ReferenceEquals(top, _stack[^1]))
        {
            Pop(completed: true);
        }
    }

    private void Push(ICreatureMovementGenerator generator)
    {
        ICreatureMover mover = _mover ?? throw new InvalidOperationException($"{_owner.Guid} has no movement owner");
        Top.Interrupt(_owner, mover);
        _stack.Add(generator);
        generator.Initialize(_owner, mover);
    }

    private void RemoveTop(MovementGeneratorType type)
    {
        if (_stack.Count > 0 && _stack[^1].Type == type && _mover is not null)
        {
            ICreatureMovementGenerator top = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            top.Finish(_owner, _mover, completed: false);
        }
    }

    private void Pop(bool completed)
    {
        ICreatureMover mover = _mover!;
        ICreatureMovementGenerator top = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        int depth = _stack.Count;
        top.Finish(_owner, mover, completed);

        // A finish callback (home reached) may already have pushed or cleared generators.
        if (_stack.Count == depth)
        {
            ResumeTop(mover);
        }
    }

    /// <summary>
    /// Resume the generator now on top. The default (idle/random/waypoint) stays interrupted
    /// while the creature is in combat: it stands until the AI chases again or evades.
    /// </summary>
    private void ResumeTop(ICreatureMover mover)
    {
        if (_stack.Count == 0 && _owner.Combat.IsInCombat)
        {
            if (_owner.IsMoving)
            {
                mover.StopMoving(_owner);
            }

            return;
        }

        Top.Resume(_owner, mover);
    }

    private void ClearStack(bool complete)
    {
        while (_stack.Count > 0)
        {
            ICreatureMovementGenerator top = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            if (_mover is not null)
            {
                top.Finish(_owner, _mover, complete);
            }
        }
    }
}
