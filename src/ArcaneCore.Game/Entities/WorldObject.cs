using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Game.Entities;

/// <summary>
/// Base of every networked object: the update-field values plus the set of fields changed
/// since the last values update. Field indices come from <see cref="UpdateFields"/>.
/// <para>
/// Thread affinity: an object in a map is owned by the world thread. Its fields and
/// position may only be read or written there.
/// </para>
/// </summary>
public abstract class WorldObject
{
    private readonly uint[] _values;
    private readonly UpdateMask _changed;
    private bool _batchingMove;
    private bool _rejectedNonFiniteRelocation;
    internal Player? SightWatcher;

    /// <summary>
    /// True if a caller attempted to relocate or move this object to a non-finite position
    /// since the last check, and the invariant guard refused the write. Clears the flag.
    /// </summary>
    public bool ConsumeRejectedNonFiniteRelocation()
    {
        bool rejected = _rejectedNonFiniteRelocation;
        _rejectedNonFiniteRelocation = false;
        return rejected;
    }

    protected void MarkRejectedNonFiniteRelocation() => _rejectedNonFiniteRelocation = true;

    protected WorldObject(ObjectGuid guid, byte typeId, uint typeMask, int valuesCount)
    {
        Guid = guid;
        TypeId = typeId;
        _values = new uint[valuesCount];
        _changed = new UpdateMask(valuesCount);

        SetUInt64(UpdateFields.ObjectFieldGuid, guid.Value);
        SetUInt32(UpdateFields.ObjectFieldType, typeMask);
        SetFloat(UpdateFields.ObjectFieldScaleX, 1.0f);
    }

    public ObjectGuid Guid { get; }

    /// <summary>The <see cref="Game.TypeId"/> written in create blocks.</summary>
    public byte TypeId { get; }

    public int ValuesCount => _values.Length;

    public ReadOnlySpan<uint> Values => _values;

    /// <summary>Per-field visibility flags for this object type (<see cref="UpdateFieldTables"/>).</summary>
    public abstract ReadOnlySpan<ushort> FieldFlags { get; }

    /// <summary>True at the low half of each GUID field for this object type.</summary>
    public abstract ReadOnlySpan<bool> GuidFieldStarts { get; }

    /// <summary>Movement-block flags this object always sends in create blocks.</summary>
    public abstract ObjectUpdateFlags CreateUpdateFlags { get; }

    /// <summary>
    /// Radius used by distance checks (vmangos SizeFactor::BoundingRadius). Units read
    /// UNIT_FIELD_BOUNDINGRADIUS; other objects use DEFAULT_WORLD_OBJECT_SIZE (ObjectDefines.h).
    /// </summary>
    public virtual float BoundingRadius => DefaultBoundingRadius;

    /// <summary>vmangos ObjectDefines.h DEFAULT_WORLD_OBJECT_SIZE (used until model data is imported).</summary>
    public const float DefaultBoundingRadius = 0.388999998569489f;

    public uint MapId { get; set; }

    /// <summary>
    /// World X. Changing it re-files the object in its map's grid (docs/areas/grid-terrain.md). A unit's movement block follows
    /// (<see cref="OnPositionWritten"/>).
    /// </summary>
    public float X
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                if (!_batchingMove)
                {
                    Map?.OnObjectMoved(this);
                }
            }

            OnPositionWritten();
        }
    }

    /// <summary>World Y. Changing it re-files the object in its map's grid. A unit's movement block follows.</summary>
    public float Y
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                if (!_batchingMove)
                {
                    Map?.OnObjectMoved(this);
                }
            }

            OnPositionWritten();
        }
    }

    /// <summary>World Z. A unit's movement block follows.</summary>
    public float Z
    {
        get;
        set
        {
            field = value;
            OnPositionWritten();
        }
    }

    /// <summary>Facing. A unit's movement block follows (vmangos <c>WorldObject::SetOrientation</c>, Object.cpp:1592-1598).</summary>
    public float Orientation
    {
        get;
        set
        {
            field = value;
            OnPositionWritten();
        }
    }

    /// <summary>
    /// Called after every write of <see cref="X"/>, <see cref="Y"/>, <see cref="Z"/> or <see cref="Orientation"/>. A unit copies
    /// the position into its movement block, so the two never disagree (vmangos <c>WorldObject::Relocate</c> always calls
    /// <c>m_movementInfo.ChangePosition</c>, Object.cpp:1567-1576).
    /// </summary>
    private protected virtual void OnPositionWritten()
    {
    }

    /// <summary>
    /// Called after every write of <see cref="Map"/> (entering, leaving or changing maps). A unit wakes its spell state so the spell tick
    /// re-checks <see cref="IsInWorld"/> (SpellSystem.UpdateState forgets a unit that left the world).
    /// </summary>
    private protected virtual void OnMapWritten()
    {
    }

    /// <summary>The map this object is in, or null when not in the world.</summary>
    public Map? Map
    {
        get;
        internal set
        {
            field = value;
            _hasNotifiedPosition = false; // a new map computes visibility from scratch
            OnMapWritten();
        }
    }

    private float _notifiedX;
    private float _notifiedY;
    private float _notifiedZ;
    private bool _hasNotifiedPosition;

    /// <summary>
    /// vmangos Unit::OnRelocated (Unit.cpp:10151-10167): a move schedules a visibility pass only once the object is more than
    /// the relocation lower limit (<c>Visibility.RelocationLowerLimit</c>, 10 yards; <c>World::m_relocation_lower_limit_sq</c>)
    /// from where the last pass was scheduled (<c>m_lastNotifiedPosition</c>, 3D). True when this move passes, and then the
    /// position is recorded. A limit of 0 passes every move.
    /// </summary>
    internal bool TakeRelocationNotify(float lowerLimitSq)
    {
        if (_hasNotifiedPosition)
        {
            float dx = _notifiedX - X;
            float dy = _notifiedY - Y;
            float dz = _notifiedZ - Z;
            if (dx * dx + dy * dy + dz * dz <= lowerLimitSq)
            {
                return false;
            }
        }

        _notifiedX = X;
        _notifiedY = Y;
        _notifiedZ = Z;
        _hasNotifiedPosition = true;
        return true;
    }

    public bool IsInWorld => Map is not null;

    /// <summary>Order in which the object joined its map; visibility passes visit objects in this order.</summary>
    internal long MapSequence { get; set; }

    /// <summary>Cached cell index (<c>cell.Y * TotalNumberOfCellsPerMap + cell.X</c>) while indexed in <see cref="Maps.Grid.GridContainer"/>; -1 when not indexed.</summary>
    internal int GridCellId { get; set; } = -1;

    /// <summary>Whether this object is currently in <see cref="Maps.Grid.GridContainer"/>'s active object set.</summary>
    internal bool IsGridActiveObject { get; set; }

    /// <summary>
    /// Move the object in one step, so its map re-files it once (setting <see cref="X"/> and
    /// <see cref="Y"/> separately re-files it after each).
    /// </summary>
    public void SetPosition(float x, float y, float z, float orientation)
    {
        if (!Invariant.Check(float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z),
                $"WorldObject {Guid} relocated to non-finite position ({x}, {y}, {z}) on map {MapId}"))
        {
            MarkRejectedNonFiniteRelocation();
            if (float.IsFinite(orientation))
            {
                Orientation = orientation;
            }
            else if (!float.IsFinite(Orientation))
            {
                Orientation = 0f;
            }

            return;
        }

        float safeOrientation = float.IsFinite(orientation)
            ? orientation
            : (float.IsFinite(Orientation) ? Orientation : 0f);
        bool moved = x != X || y != Y;
        _batchingMove = true;
        try
        {
            X = x;
            Y = y;
        }
        finally
        {
            _batchingMove = false;
        }

        Z = z;
        Orientation = safeOrientation;
        if (moved)
        {
            Map?.OnObjectMoved(this);
        }
    }

    /// <summary>Relocate the object to finite world coordinates and orientation.</summary>
    public void Relocate(float x, float y, float z, float orientation) => SetPosition(x, y, z, orientation);

    /// <summary>Relocate the object to finite world coordinates while preserving its current orientation.</summary>
    public void Relocate(float x, float y, float z) => SetPosition(x, y, z, Orientation);

    /// <summary>Fields changed since the last values flush.</summary>
    internal UpdateMask ChangedFields => _changed;

    /// <summary>Set while this object sits in its map's values-update queue.</summary>
    internal bool IsQueuedForUpdate { get; set; }

    // --- field access -------------------------------------------------------------

    public uint GetUInt32(int index) => _values[index];

    public int GetInt32(int index) => unchecked((int)_values[index]);

    public float GetFloat(int index) => BitConverter.UInt32BitsToSingle(_values[index]);

    public ulong GetUInt64(int index) => _values[index] | ((ulong)_values[index + 1] << 32);

    public byte GetByte(int index, int byteOffset) => (byte)(_values[index] >> (byteOffset * 8));

    public ushort GetUInt16(int index, int shortOffset) => (ushort)(_values[index] >> (shortOffset * 16));

    public bool HasFlag(int index, uint flag) => (_values[index] & flag) != 0;

    public void SetUInt32(int index, uint value)
    {
        if (_values[index] != value)
        {
            _values[index] = value;
            MarkChanged(index);
        }
    }

    public void SetInt32(int index, int value) => SetUInt32(index, unchecked((uint)value));

    public void SetFloat(int index, float value)
    {
        if (!Invariant.Check(float.IsFinite(value),
                $"WorldObject {Guid} field {index} assigned non-finite float {value}"))
        {
            return;
        }

        SetUInt32(index, BitConverter.SingleToUInt32Bits(value));
    }

    /// <summary>
    /// Set a 64-bit (GUID) field. Both halves are marked changed together: the client ignores
    /// a 64-bit value whose halves arrive separately (vmangos Object::SetUInt64Value).
    /// </summary>
    public void SetUInt64(int index, ulong value)
    {
        uint low = (uint)value;
        uint high = (uint)(value >> 32);
        if (_values[index] != low || _values[index + 1] != high)
        {
            _values[index] = low;
            _values[index + 1] = high;
            MarkChanged(index);
            MarkChanged(index + 1);
        }
    }

    public void SetByte(int index, int byteOffset, byte value)
    {
        int shift = byteOffset * 8;
        uint updated = (_values[index] & ~(0xFFu << shift)) | ((uint)value << shift);
        SetUInt32(index, updated);
    }

    public void SetUInt16(int index, int shortOffset, ushort value)
    {
        int shift = shortOffset * 16;
        uint updated = (_values[index] & ~(0xFFFFu << shift)) | ((uint)value << shift);
        SetUInt32(index, updated);
    }

    public void SetFlag(int index, uint flag) => SetUInt32(index, _values[index] | flag);

    public void RemoveFlag(int index, uint flag) => SetUInt32(index, _values[index] & ~flag);

    /// <summary>
    /// Optional per-viewer field values (vmangos Object::BuildValuesUpdate special cases such as
    /// UNIT_DYNFLAG_LOOTABLE for allowed looters and GAMEOBJECT_DYN_FLAGS for quest objects).
    /// Null sends the stored values to everyone. World thread.
    /// </summary>
    public Updates.IViewerFieldFilter? ViewerFieldFilter { get; set; }

    /// <summary>The value <paramref name="viewer"/> receives for field <paramref name="index"/>.</summary>
    public uint GetValueFor(int index, Player viewer)
        => ViewerFieldFilter is { } filter ? filter.Filter(this, index, _values[index], viewer) : _values[index];

    /// <summary>
    /// Queue a values update of <paramref name="index"/> although its stored value did not change,
    /// because a <see cref="ViewerFieldFilter"/> answer changed (vmangos ForceValuesUpdateAtIndex).
    /// </summary>
    public void ForceFieldUpdate(int index) => MarkChanged(index);

    /// <summary>Forget pending changes (after a values flush, or for values a create block already carried).</summary>
    internal void ClearChangedFields() => _changed.Clear();

    /// <summary>
    /// The map whose values queue carries this object's field changes: its own map, or for an
    /// object that lives outside maps (an item) the map of the player whose client has it.
    /// </summary>
    internal virtual Map? ValuesUpdateMap => Map;

    private void MarkChanged(int index)
    {
        _changed.SetBit(index);
        if (!IsQueuedForUpdate && ValuesUpdateMap is { } map)
        {
            map.QueueValuesUpdate(this);
        }
    }
}
