using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Locomotion;

/// <summary>
/// The server-ordered movement changes a player's client must acknowledge (vmangos MovementChangeType,
/// Unit.h; only the 1.12 changes this lane delivers).
/// </summary>
public enum MovementChangeType
{
    Root,
    WaterWalk,
    Hover,
    FeatherFall,

    /// <summary>A new walk speed (vmangos SPEED_CHANGE_WALK); the speed types carry <see cref="PendingMovementChange.NewValue"/>.</summary>
    SpeedWalk,
    SpeedRun,
    SpeedRunBack,
    SpeedSwim,
    SpeedSwimBack,

    /// <summary>A knock back order (vmangos KNOCK_BACK); it carries <see cref="PendingMovementChange.Knockback"/> and is never enforced.</summary>
    KnockBack,
}

/// <summary>The four numbers of SMSG_MOVE_KNOCK_BACK that the client must echo in its ack (vmangos knockbackInfo): direction cosine and sine, horizontal speed and vertical speed (sent negated).</summary>
public readonly record struct KnockbackInfo(float VCos, float VSin, float SpeedXY, float SpeedZ);

/// <summary>One sent-but-unacknowledged change (vmangos PlayerMovementPendingChange, Unit.h).</summary>
/// <param name="Counter">The movement counter sent to the client (the ack echoes it).</param>
/// <param name="Type">What changes.</param>
/// <param name="Apply">True to set the state, false to clear it.</param>
public sealed record PendingMovementChange(uint Counter, MovementChangeType Type, bool Apply)
{
    /// <summary>The speed in yards per second a speed change orders (vmangos pendingChange.newValue); 0 for the flag changes.</summary>
    public float NewValue { get; init; }

    /// <summary>The numbers of a knock back order; null for every other change.</summary>
    public KnockbackInfo? Knockback { get; init; }

    /// <summary>Milliseconds since the change was sent, advanced by the map tick (the ack timeout clock).</summary>
    public uint AgeMs { get; internal set; }
}

/// <summary>Per-type facts shared by the packet builders and the resolver (vmangos MovementPacketSender.cpp:306-440).</summary>
public static class MovementChangeInfo
{
    /// <summary>The movement flag the change toggles.</summary>
    public static MovementFlags FlagOf(MovementChangeType type) => type switch
    {
        MovementChangeType.Root => MovementFlags.Root,
        MovementChangeType.WaterWalk => MovementFlags.WaterWalking,
        MovementChangeType.Hover => MovementFlags.Hover,
        MovementChangeType.FeatherFall => MovementFlags.SafeFall,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>The SMSG that orders the controlling client (SendMovementFlagChangeToController).</summary>
    public static WorldOpcode ControllerOpcode(MovementChangeType type, bool apply) => type switch
    {
        MovementChangeType.Root => apply ? WorldOpcode.SmsgForceMoveRoot : WorldOpcode.SmsgForceMoveUnroot,
        MovementChangeType.WaterWalk => apply ? WorldOpcode.SmsgMoveWaterWalk : WorldOpcode.SmsgMoveLandWalk,
        MovementChangeType.Hover => apply ? WorldOpcode.SmsgMoveSetHover : WorldOpcode.SmsgMoveUnsetHover,
        MovementChangeType.FeatherFall => apply ? WorldOpcode.SmsgMoveFeatherFall : WorldOpcode.SmsgMoveNormalFall,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>The MSG relayed to the other clients after the ack (SendMovementFlagChangeToObservers): packed GUID + movement block.</summary>
    public static WorldOpcode ObserverOpcode(MovementChangeType type, bool apply) => type switch
    {
        MovementChangeType.Root => apply ? WorldOpcode.MsgMoveRoot : WorldOpcode.MsgMoveUnroot,
        MovementChangeType.WaterWalk => WorldOpcode.MsgMoveWaterWalk,
        MovementChangeType.Hover => WorldOpcode.MsgMoveHover,
        MovementChangeType.FeatherFall => WorldOpcode.MsgMoveFeatherFall,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>The SMSG sent to everyone when the server enforces an unacknowledged change (SendMovementFlagChangeToAll, build above 1.9.4): packed GUID only.</summary>
    public static WorldOpcode EnforcedOpcode(MovementChangeType type, bool apply) => type switch
    {
        MovementChangeType.Root => apply ? WorldOpcode.SmsgSplineMoveRoot : WorldOpcode.SmsgSplineMoveUnroot,
        MovementChangeType.WaterWalk => apply ? WorldOpcode.SmsgSplineMoveWaterWalk : WorldOpcode.SmsgSplineMoveLandWalk,
        MovementChangeType.Hover => apply ? WorldOpcode.SmsgSplineMoveSetHover : WorldOpcode.SmsgSplineMoveUnsetHover,
        MovementChangeType.FeatherFall => apply ? WorldOpcode.SmsgSplineMoveFeatherFall : WorldOpcode.SmsgSplineMoveNormalFall,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}

/// <summary>
/// The pending-movement-change ledger of one unit (vmangos Unit::m_pendingMovementChanges and
/// m_lastMovementChangeCounterPerType, Unit.cpp:6619-6930). A change is pushed when its order is sent and
/// leaves the ledger when the matching ack arrives (<see cref="TryAcknowledge"/>) or when the ack times out
/// (<see cref="CheckTimeout"/>).
/// </summary>
public sealed class PendingMovementChanges
{
    private readonly List<PendingMovementChange> _changes = [];
    private readonly Dictionary<MovementChangeType, uint> _lastCounter = [];

    public int Count => _changes.Count;

    public bool HasPending => _changes.Count > 0;

    public IReadOnlyList<PendingMovementChange> Changes => _changes;

    /// <summary>The counter of the most recent change pushed for the type (vmangos GetLastCounterForMovementChangeType; 0 if none).</summary>
    public uint LastCounterOf(MovementChangeType type) => _lastCounter.GetValueOrDefault(type);

    public bool HasPendingOfType(MovementChangeType type) => _changes.Any(c => c.Type == type);

    /// <summary>Record a sent order (vmangos PushPendingMovementChange).</summary>
    public PendingMovementChange Push(uint counter, MovementChangeType type, bool apply, float newValue = 0.0f, KnockbackInfo? knockback = null)
    {
        var change = new PendingMovementChange(counter, type, apply) { NewValue = newValue, Knockback = knockback };
        _lastCounter[type] = counter;
        _changes.Add(change);
        return change;
    }

    /// <summary>
    /// Match an acknowledgement (vmangos FindPendingMovementFlagChange / FindPendingMovementRootChange): the
    /// first pending change with this counter, apply flag and type is removed. Older entries of the same type
    /// stay until they time out. Returns false when nothing matches (a wrong ack).
    /// </summary>
    public bool TryAcknowledge(uint counter, MovementChangeType type, bool apply)
    {
        for (int i = 0; i < _changes.Count; i++)
        {
            PendingMovementChange change = _changes[i];
            if (change.Counter == counter && change.Apply == apply && change.Type == type)
            {
                _changes.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Match a speed acknowledgement (vmangos FindPendingMovementSpeedChange, Unit.cpp:6893-6925): the same counter and
    /// type, and a speed within 0.01 of the one that was sent. The matching change is removed.
    /// </summary>
    public bool TryAcknowledgeSpeed(uint counter, MovementChangeType type, float speed)
    {
        for (int i = 0; i < _changes.Count; i++)
        {
            PendingMovementChange change = _changes[i];
            if (change.Counter == counter && change.Type == type && Math.Abs(change.NewValue - speed) <= 0.01f)
            {
                _changes.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Match a knock back acknowledgement (vmangos FindPendingMovementKnockbackChange, Unit.cpp:6867-6890): the same counter and
    /// the jump block of the client's movement block (cos, sin, xy speed, z speed) within 0.01 of what was sent.
    /// </summary>
    public bool TryAcknowledgeKnockBack(uint counter, in MovementInfo block)
    {
        for (int i = 0; i < _changes.Count; i++)
        {
            PendingMovementChange change = _changes[i];
            if (change.Counter != counter || change.Type != MovementChangeType.KnockBack || change.Knockback is not { } sent)
            {
                continue;
            }

            if (Math.Abs(sent.SpeedXY - block.JumpXySpeed) > 0.01f || Math.Abs(sent.SpeedZ - block.JumpZSpeed) > 0.01f
                || Math.Abs(sent.VCos - block.JumpCosAngle) > 0.01f || Math.Abs(sent.VSin - block.JumpSinAngle) > 0.01f)
            {
                continue;
            }

            _changes.RemoveAt(i);
            return true;
        }

        return false;
    }

    /// <summary>Age every pending change by one tick.</summary>
    public void Age(uint diffMs)
    {
        foreach (PendingMovementChange change in _changes)
        {
            change.AgeMs += diffMs;
        }
    }

    /// <summary>
    /// The oldest change's timeout (vmangos Unit::CheckPendingMovementChanges, Unit.cpp:6619-6665). Once it
    /// is older than <paramref name="ackTimeMs"/> (times five while teleporting) it is popped; a change that a
    /// newer change of the same type has superseded is only dropped, anything else is passed to
    /// <paramref name="enforce"/> (the server applies it). Returns the change that was enforced, if any.
    /// </summary>
    public PendingMovementChange? CheckTimeout(uint ackTimeMs, bool teleporting, out bool dropped)
    {
        dropped = false;
        if (_changes.Count == 0)
        {
            return null;
        }

        PendingMovementChange oldest = _changes[0];
        ulong limit = (ulong)ackTimeMs * (teleporting ? 5UL : 1UL);
        if (oldest.AgeMs <= limit)
        {
            return null;
        }

        _changes.RemoveAt(0);
        if (oldest.Counter < LastCounterOf(oldest.Type))
        {
            // "There is a new change for the same thing, don't resend old state."
            dropped = true;
            return null;
        }

        return oldest;
    }

    /// <summary>
    /// Empty the ledger (vmangos ResolvePendingMovementChanges): each change that is still the latest of its type
    /// is returned for the server to apply.
    /// </summary>
    public IReadOnlyList<PendingMovementChange> ResolveAll()
    {
        List<PendingMovementChange> resolved = [];
        foreach (PendingMovementChange change in _changes)
        {
            if (change.Counter == LastCounterOf(change.Type))
            {
                resolved.Add(change);
            }
        }

        _changes.Clear();
        return resolved;
    }
}
