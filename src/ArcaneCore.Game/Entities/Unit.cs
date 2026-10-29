using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Entities;

/// <summary>
/// A living object (creature or player): unit update fields plus a movement state and the
/// six movement speeds sent in its create block.
/// </summary>
public abstract partial class Unit : WorldObject
{
    /// <summary>Vanilla base speeds, yards/second (vmangos Unit.cpp baseMoveSpeed).</summary>
    public const float BaseWalkSpeed = 2.5f;
    public const float BaseRunSpeed = 7.0f;
    public const float BaseRunBackSpeed = 4.5f;
    public const float BaseSwimSpeed = 4.722222f;
    public const float BaseSwimBackSpeed = 2.5f;
    public const float BaseTurnRate = 3.141594f;

    private MovementInfo _movement;
    internal Pets.Control.UnitControlState? ControlState;

    /// <summary>vmangos Object.h: BATCHING_INTERVAL (400 ms) × 13.</summary>
    public const uint HeartbeatIntervalMs = 5200;

    private long _heartbeatTimer = HeartbeatIntervalMs;

    public event Action<Unit>? Heartbeat;

    internal void UpdateHeartbeat(uint diffMs)
    {
        _heartbeatTimer -= diffMs;
        while (_heartbeatTimer <= 0)
        {
            _heartbeatTimer += HeartbeatIntervalMs;
            Heartbeat?.Invoke(this);
        }
    }

    protected Unit(ObjectGuid guid, byte typeId, uint typeMask, int valuesCount)
        : base(guid, typeId, typeMask, valuesCount)
    {
    }

    public override ReadOnlySpan<ushort> FieldFlags => UpdateFieldTables.UnitVisibility;

    public override ReadOnlySpan<bool> GuidFieldStarts => UpdateFieldTables.UnitGuidStarts;

    /// <summary>vmangos Unit constructor: UPDATEFLAG_ALL | UPDATEFLAG_LIVING | UPDATEFLAG_HAS_POSITION.</summary>
    public override ObjectUpdateFlags CreateUpdateFlags =>
        ObjectUpdateFlags.All | ObjectUpdateFlags.Living | ObjectUpdateFlags.HasPosition;

    /// <summary>
    /// The last accepted movement state. Its position and orientation always equal <see cref="WorldObject.X"/> etc.: every
    /// position write copies itself here (<see cref="OnPositionWritten"/>), as vmangos <c>WorldObject::Relocate</c> does with
    /// <c>m_movementInfo.ChangePosition</c>. Unlike vmangos, a position write does not restamp <c>MovementInfo.Time</c>
    /// (docs/areas/grid-terrain.md, Reference discrepancies). Use <see cref="ApplyMovement"/> to change the rest of it.
    /// </summary>
    public ref readonly MovementInfo Movement => ref _movement;

    /// <summary>Keep the movement block's position equal to the unit's (docs/areas/grid-terrain.md "Position and movement block").</summary>
    private protected sealed override void OnPositionWritten()
    {
        _movement.X = X;
        _movement.Y = Y;
        _movement.Z = Z;
        _movement.Orientation = Orientation;
    }

    /// <summary>
    /// The unit's <see cref="Spells.SpellSystem"/> state while it has one (set by the spell system when it creates the state, cleared when
    /// it forgets it). Lets world-object writes that the spell tick depends on (the map) wake the state (active set, SpellSystem.ActiveSet.cs).
    /// </summary>
    internal Spells.UnitSpellState? SpellState { get; set; }

    private protected sealed override void OnMapWritten() => SpellState?.Wake(Spells.SpellWakeReason.MapChanged);

    public float WalkSpeed
    {
        get;
        set
        {
            if (Invariant.Check(float.IsFinite(value) && value >= 0f, $"Unit {Guid} assigned invalid WalkSpeed {value}"))
            {
                field = value;
            }
        }
    } = BaseWalkSpeed;

    public float RunSpeed
    {
        get;
        set
        {
            if (Invariant.Check(float.IsFinite(value) && value >= 0f, $"Unit {Guid} assigned invalid RunSpeed {value}"))
            {
                field = value;
            }
        }
    } = BaseRunSpeed;

    public float RunBackSpeed
    {
        get;
        set
        {
            if (Invariant.Check(float.IsFinite(value) && value >= 0f, $"Unit {Guid} assigned invalid RunBackSpeed {value}"))
            {
                field = value;
            }
        }
    } = BaseRunBackSpeed;

    public float SwimSpeed
    {
        get;
        set
        {
            if (Invariant.Check(float.IsFinite(value) && value >= 0f, $"Unit {Guid} assigned invalid SwimSpeed {value}"))
            {
                field = value;
            }
        }
    } = BaseSwimSpeed;

    public float SwimBackSpeed
    {
        get;
        set
        {
            if (Invariant.Check(float.IsFinite(value) && value >= 0f, $"Unit {Guid} assigned invalid SwimBackSpeed {value}"))
            {
                field = value;
            }
        }
    } = BaseSwimBackSpeed;

    public float TurnRate
    {
        get;
        set
        {
            if (Invariant.Check(float.IsFinite(value) && value >= 0f, $"Unit {Guid} assigned invalid TurnRate {value}"))
            {
                field = value;
            }
        }
    } = BaseTurnRate;

    public override float BoundingRadius
    {
        get
        {
            float radius = GetFloat(UpdateFields.UnitFieldBoundingradius);
            return float.IsFinite(radius) && radius >= 0f ? radius : base.BoundingRadius;
        }
    }

    // --- common unit fields -------------------------------------------------------

    public uint Health
    {
        get => GetUInt32(UpdateFields.UnitFieldHealth);
        set => SetUInt32(UpdateFields.UnitFieldHealth, value);
    }

    /// <summary>Health as a percentage in [0, 100], guarded against a zero <see cref="MaxHealth"/> denominator.</summary>
    public float HealthPercent => MaxHealth > 0 ? Math.Clamp((float)Health * 100f / MaxHealth, 0f, 100f) : 0f;

    /// <summary>
    /// Alive in vmangos' sense (Unit::IsAlive: death state ALIVE). A released ghost has 1 health
    /// but is not alive.
    /// </summary>
    public bool IsAlive => Health > 0 && Combat.DeathState == global::ArcaneCore.Game.Combat.DeathState.Alive;

    /// <summary>Combat state of this unit (src/ArcaneCore.Game/Combat; docs/integration/combat.md).</summary>
    public global::ArcaneCore.Game.Combat.UnitCombat Combat => field ??= new global::ArcaneCore.Game.Combat.UnitCombat(this);

    public uint MaxHealth
    {
        get => GetUInt32(UpdateFields.UnitFieldMaxhealth);
        set => SetUInt32(UpdateFields.UnitFieldMaxhealth, value);
    }

    public byte Level
    {
        get => (byte)GetUInt32(UpdateFields.UnitFieldLevel);
        set => SetUInt32(UpdateFields.UnitFieldLevel, value);
    }

    public uint FactionTemplate
    {
        get => GetUInt32(UpdateFields.UnitFieldFactiontemplate);
        set => SetUInt32(UpdateFields.UnitFieldFactiontemplate, value);
    }

    public uint DisplayId
    {
        get => GetUInt32(UpdateFields.UnitFieldDisplayid);
        set => SetUInt32(UpdateFields.UnitFieldDisplayid, value);
    }

    public uint NativeDisplayId
    {
        get => GetUInt32(UpdateFields.UnitFieldNativedisplayid);
        set => SetUInt32(UpdateFields.UnitFieldNativedisplayid, value);
    }

    /// <summary>DBC native scale for the current native display, retained for geometry restoration.</summary>
    public float NativeScale
    {
        get;
        internal set
        {
            if (Invariant.Check(float.IsFinite(value) && value > 0f, $"Unit {Guid} assigned invalid NativeScale {value}"))
            {
                field = value;
            }
        }
    } = 1.0f;

    /// <summary>Explicit native object-scale override selected from creature data, if any.</summary>
    public float NativeScaleOverride
    {
        get;
        internal set
        {
            if (Invariant.Check(float.IsFinite(value) && value >= 0f, $"Unit {Guid} assigned invalid NativeScaleOverride {value}"))
            {
                field = value;
            }
        }
    }

    /// <summary>
    /// vmangos <c>Unit::GetCollisionHeight</c> (Unit.h:542): the model collision height (<c>m_modelCollisionHeight</c>,
    /// <see cref="global::ArcaneCore.Game.Locomotion.LocomotionState.CollisionHeight"/>) times <c>m_nativeScaleOverride</c>, which is
    /// the scale of the transformation applied (<c>SetTransformScale</c>, Unit.cpp:10967-10976;
    /// <see cref="global::ArcaneCore.Game.Spells.TransformScale"/>) or else <see cref="NativeCollisionScale"/>. The height a unit
    /// looks from in line of sight (Object.cpp:1770).
    /// </summary>
    public float CollisionHeight
    {
        get
        {
            float model = global::ArcaneCore.Game.Locomotion.LocomotionStates.TryGet(this, out global::ArcaneCore.Game.Locomotion.LocomotionState state)
                ? state.CollisionHeight
                : global::ArcaneCore.Game.Locomotion.LocomotionState.DefaultCollisionHeight;
            float scale = global::ArcaneCore.Game.Spells.TransformScale.TryGetTransformation(this, out float transform) ? transform : NativeCollisionScale;
            return model * scale;
        }
    }

    /// <summary>
    /// <c>m_nativeScaleOverride</c> while no transformation is applied: 1 (Unit.h:486; a player keeps it), which a creature
    /// replaces with the scale it was created with.
    /// </summary>
    private protected virtual float NativeCollisionScale => 1.0f;

    /// <summary>UNIT_FIELD_BYTES_0: race, class, gender, power type (vmangos Unit::GetRace etc.).</summary>
    public Race Race => (Race)GetByte(UpdateFields.UnitFieldBytes0, 0);

    public Class Class => (Class)GetByte(UpdateFields.UnitFieldBytes0, 1);

    public Gender Gender => (Gender)GetByte(UpdateFields.UnitFieldBytes0, 2);

    public PowerType PowerType => (PowerType)GetByte(UpdateFields.UnitFieldBytes0, 3);

    /// <summary>UNIT_FIELD_BYTES_1 byte 0 (vmangos Unit::SetStandState).</summary>
    public StandState StandState
    {
        get => (StandState)GetByte(UpdateFields.UnitFieldBytes1, 0);
        set => SetByte(UpdateFields.UnitFieldBytes1, 0, (byte)value);
    }

    /// <summary>UNIT_FIELD_TARGET — the unit's current target GUID.</summary>
    public ObjectGuid Target
    {
        get => new(GetUInt64(UpdateFields.UnitFieldTarget));
        set => SetUInt64(UpdateFields.UnitFieldTarget, value.Value);
    }

    public UnitFlags UnitFlags
    {
        get => (UnitFlags)GetUInt32(UpdateFields.UnitFieldFlags);
        set => SetUInt32(UpdateFields.UnitFieldFlags, (uint)value);
    }

    /// <summary>
    /// Accept a movement update: store it and move the object to its position. The time is
    /// replaced by the server time of receipt, which is what other clients are sent
    /// (vmangos/cmangos-classic MovementInfo::Read sets stime and ::Write sends it).
    /// </summary>
    public void ApplyMovement(in MovementInfo movement, uint serverTimeMs)
    {
        if (!Invariant.Check(float.IsFinite(movement.X) && float.IsFinite(movement.Y) && float.IsFinite(movement.Z),
                $"WorldObject {Guid} relocated to non-finite position ({movement.X}, {movement.Y}, {movement.Z}) on map {MapId}"))
        {
            MarkRejectedNonFiniteRelocation();
            return;
        }

        float safeOrientation = float.IsFinite(movement.Orientation)
            ? movement.Orientation
            : (float.IsFinite(Orientation) ? Orientation : 0f);
        _movement = movement;
        _movement.Time = serverTimeMs;
        _movement.Orientation = safeOrientation;
        SetPosition(movement.X, movement.Y, movement.Z, safeOrientation);
    }

    /// <summary>
    /// End a state emote (vmangos Unit::HandleEmoteState(0), Unit.cpp:1856-1859: UNIT_NPC_EMOTESTATE = 0). The field write is a
    /// no-op, and sends no update, when no state is set.
    /// </summary>
    public void ClearEmoteState()
    {
        if (GetUInt32(UpdateFields.UnitNpcEmotestate) != 0)
        {
            SetUInt32(UpdateFields.UnitNpcEmotestate, 0);
        }
    }

    /// <summary>
    /// Set movement flags the server decided (vmangos Unit::AddUnitMovementFlag). Locomotion uses it to apply an
    /// acknowledged root, water walk, hover or feather fall (src/ArcaneCore.Game/Locomotion).
    /// </summary>
    public void AddMovementFlags(MovementFlags flags) => _movement.Flags |= flags;

    /// <summary>Clear movement flags the server decided (vmangos Unit::RemoveUnitMovementFlag).</summary>
    public void RemoveMovementFlags(MovementFlags flags) => _movement.Flags &= ~flags;

    /// <summary>
    /// Flags that are server state, not client motion: a teleport or relocation does not clear them
    /// (vmangos only rewrites the position of m_movementInfo on a teleport). Walk mode is kept too: a creature's spline
    /// launch decides it (MoveSplineInit.cpp:109-112) and every spline step relocates the creature.
    /// </summary>
    private const MovementFlags ServerOwnedFlags = MovementFlags.Root | MovementFlags.WaterWalking | MovementFlags.Hover | MovementFlags.SafeFall | MovementFlags.WalkMode;

    /// <summary>The movement flags a relocation keeps (<see cref="ServerOwnedFlags"/>; a creature adds the flight a script gave it).</summary>
    private protected virtual MovementFlags RelocationKeptFlags => ServerOwnedFlags;

    /// <summary>Place the unit (teleport, spawn, login) with a fresh, stationary movement state.</summary>
    public void Relocate(float x, float y, float z, float orientation, uint serverTimeMs)
    {
        if (!Invariant.Check(float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z),
                $"WorldObject {Guid} relocated to non-finite position ({x}, {y}, {z}) on map {MapId}"))
        {
            MarkRejectedNonFiniteRelocation();
            if (float.IsFinite(orientation))
            {
                Orientation = orientation;
                _movement.Orientation = orientation;
            }
            else if (!float.IsFinite(Orientation))
            {
                Orientation = 0f;
                _movement.Orientation = 0f;
            }

            return;
        }

        float safeOrientation = float.IsFinite(orientation)
            ? orientation
            : (float.IsFinite(Orientation) ? Orientation : 0f);
        MovementFlags kept = _movement.Flags & RelocationKeptFlags;

        // A teleport, spell relocation, taxi stop or login ends any fall in progress (vmangos SetFallInformation(0),
        // Player.cpp:1932,2082,15051).
        if (global::ArcaneCore.Game.Locomotion.LocomotionStates.TryGet(this, out global::ArcaneCore.Game.Locomotion.LocomotionState state))
        {
            state.ResetFall();
        }

        SetPosition(x, y, z, safeOrientation);
        _movement = new MovementInfo
        {
            Flags = kept,
            Time = serverTimeMs,
            X = x,
            Y = y,
            Z = z,
            Orientation = safeOrientation,
        };
    }
}
