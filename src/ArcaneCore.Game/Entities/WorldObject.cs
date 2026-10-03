using ArcaneCore.Game.Maps;

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
    public virtual float BoundingRadius => 0.388999998569489f;

    public uint MapId { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    public float Orientation { get; set; }

    /// <summary>The map this object is in, or null when not in the world.</summary>
    public Map? Map { get; internal set; }

    public bool IsInWorld => Map is not null;

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

    public void SetFloat(int index, float value) => SetUInt32(index, BitConverter.SingleToUInt32Bits(value));

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
