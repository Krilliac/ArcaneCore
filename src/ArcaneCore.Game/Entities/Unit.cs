using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Entities;

/// <summary>
/// A living object (creature or player): unit update fields plus a movement state and the
/// six movement speeds sent in its create block.
/// </summary>
public abstract class Unit : WorldObject
{
    /// <summary>Vanilla base speeds, yards/second (vmangos Unit.cpp baseMoveSpeed).</summary>
    public const float BaseWalkSpeed = 2.5f;
    public const float BaseRunSpeed = 7.0f;
    public const float BaseRunBackSpeed = 4.5f;
    public const float BaseSwimSpeed = 4.722222f;
    public const float BaseSwimBackSpeed = 2.5f;
    public const float BaseTurnRate = 3.141594f;

    private MovementInfo _movement;

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
    /// The last accepted movement state. Its position always mirrors <see cref="WorldObject.X"/>
    /// etc.; use <see cref="ApplyMovement"/> to change it.
    /// </summary>
    public ref readonly MovementInfo Movement => ref _movement;

    public float WalkSpeed { get; set; } = BaseWalkSpeed;
    public float RunSpeed { get; set; } = BaseRunSpeed;
    public float RunBackSpeed { get; set; } = BaseRunBackSpeed;
    public float SwimSpeed { get; set; } = BaseSwimSpeed;
    public float SwimBackSpeed { get; set; } = BaseSwimBackSpeed;
    public float TurnRate { get; set; } = BaseTurnRate;

    public override float BoundingRadius => GetFloat(UpdateFields.UnitFieldBoundingradius);

    // --- common unit fields -------------------------------------------------------

    public uint Health
    {
        get => GetUInt32(UpdateFields.UnitFieldHealth);
        set => SetUInt32(UpdateFields.UnitFieldHealth, value);
    }

    /// <summary>Alive while it has health (death states arrive with combat, M11).</summary>
    public bool IsAlive => Health > 0;

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
        _movement = movement;
        _movement.Time = serverTimeMs;
        SetPosition(movement.X, movement.Y, movement.Z, movement.Orientation);
    }

    /// <summary>Place the unit (teleport, spawn, login) with a fresh, stationary movement state.</summary>
    public void Relocate(float x, float y, float z, float orientation, uint serverTimeMs)
    {
        SetPosition(x, y, z, orientation);
        _movement = new MovementInfo
        {
            Flags = MovementFlags.None,
            Time = serverTimeMs,
            X = x,
            Y = y,
            Z = z,
            Orientation = orientation,
        };
    }
}
