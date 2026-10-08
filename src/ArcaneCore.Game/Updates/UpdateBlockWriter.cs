using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Updates;

/// <summary>
/// Serializes SMSG_UPDATE_OBJECT blocks for one viewer. Layouts follow vmangos
/// Object::BuildCreateUpdateBlockForPlayer, Object::BuildMovementUpdate and
/// Object::BuildValuesUpdate for client builds &gt; 1.8.4.
/// </summary>
public static class UpdateBlockWriter
{
    /// <summary>
    /// Create block: update type, packed GUID, type id, movement block, then every non-zero
    /// field the viewer may see. <paramref name="isNewObject"/> selects
    /// UPDATETYPE_CREATE_OBJECT2 (an object just spawned into the map, vmangos Map::Add) over
    /// UPDATETYPE_CREATE_OBJECT (an existing object coming into view).
    /// </summary>
    public static void WriteCreateBlock(PacketWriter writer, WorldObject obj, Player viewer, bool isNewObject, uint serverTimeMs)
    {
        writer.WriteByte((byte)(isNewObject ? ObjectUpdateType.CreateObject2 : ObjectUpdateType.CreateObject));
        writer.WritePackedGuid(obj.Guid.Value);
        writer.WriteByte(obj.TypeId);

        ObjectUpdateFlags flags = obj.CreateUpdateFlags;
        if (ReferenceEquals(obj, viewer))
        {
            flags |= ObjectUpdateFlags.Self;
        }

        WriteMovementBlock(writer, obj, flags, serverTimeMs);

        UpdateFieldFlags visible = VisibleFieldsFor(obj, viewer);
        var mask = new UpdateMask(obj.ValuesCount);
        ReadOnlySpan<uint> values = obj.Values;
        ReadOnlySpan<ushort> fieldFlags = obj.FieldFlags;
        ReadOnlySpan<bool> guidStarts = obj.GuidFieldStarts;
        for (int index = 0; index < values.Length; index++)
        {
            if (((UpdateFieldFlags)fieldFlags[index] & visible) == 0)
            {
                continue;
            }

            // GUID fields are sent with both halves or not at all: the client ignores a
            // partially present 64-bit value in a create block (vmangos Object::_SetCreateBits).
            if (guidStarts[index])
            {
                if (values[index] != 0 || values[index + 1] != 0)
                {
                    mask.SetBit(index);
                    mask.SetBit(index + 1);
                }

                index++;
            }
            else if (obj.GetValueFor(index, viewer) != 0)
            {
                mask.SetBit(index);
            }
        }

        WriteValues(writer, obj, viewer, mask);
    }

    /// <summary>
    /// Values block for the fields that changed since the last flush and that the viewer may
    /// see (vmangos Object::_SetUpdateBits). Returns false (writing nothing) when none apply.
    /// </summary>
    public static bool TryWriteValuesBlock(PacketWriter writer, WorldObject obj, Player viewer)
    {
        UpdateFieldFlags visible = VisibleFieldsFor(obj, viewer);
        UpdateMask changed = obj.ChangedFields;
        ReadOnlySpan<ushort> fieldFlags = obj.FieldFlags;

        var mask = new UpdateMask(obj.ValuesCount);
        bool any = false;
        for (int index = changed.NextSetBit(0); index >= 0; index = changed.NextSetBit(index + 1))
        {
            if (((UpdateFieldFlags)fieldFlags[index] & visible) != 0)
            {
                mask.SetBit(index);
                any = true;
            }
        }

        if (!any)
        {
            return false;
        }

        writer.WriteByte((byte)ObjectUpdateType.Values);
        writer.WritePackedGuid(obj.Guid.Value);
        WriteValues(writer, obj, viewer, mask);
        return true;
    }

    /// <summary>
    /// Which field classes the viewer may receive (vmangos Object::GetUpdateFieldFlagsForTarget):
    /// everyone gets public and dynamic fields, an object's own player also gets private ones.
    /// Owner/group/special-info classes join with pets, items and groups.
    /// </summary>
    public static UpdateFieldFlags VisibleFieldsFor(WorldObject obj, Player viewer)
    {
        UpdateFieldFlags visible = UpdateFieldFlags.Public | UpdateFieldFlags.Dynamic;
        if (ReferenceEquals(obj, viewer))
        {
            // vmangos Object::GetUpdateFieldFlagsForTarget and Player::IsInSameRaidWith:
            // a player is in their own raid for field visibility, even while ungrouped.
            visible |= UpdateFieldFlags.Private | UpdateFieldFlags.GroupOnly;
        }

        // vmangos GetUpdateFieldFlagsForTarget: an item's owner also gets OWNER_ONLY | UNK2 (item owner).
        if (obj is Items.Item item && item.OwnerGuid == viewer.Guid)
        {
            visible |= UpdateFieldFlags.OwnerOnly | UpdateFieldFlags.ItemOwner;
        }

        // A unit's owner (its pet's stats) and its charmer get OWNER_ONLY (Object.cpp:1061-1063).
        if (obj is Unit unit && !ReferenceEquals(unit, viewer) && (unit.OwnerGuid == viewer.Guid || unit.CharmerGuid == viewer.Guid))
        {
            visible |= UpdateFieldFlags.OwnerOnly;
        }

        return visible;
    }

    /// <summary>The masked values, each as <paramref name="viewer"/> sees it (<see cref="WorldObject.GetValueFor"/>).</summary>
    private static void WriteValues(PacketWriter writer, WorldObject obj, Player viewer, UpdateMask mask)
    {
        mask.WriteTo(writer);
        for (int index = mask.NextSetBit(0); index >= 0; index = mask.NextSetBit(index + 1))
        {
            writer.WriteUInt32(obj.GetValueFor(index, viewer));
        }
    }

    /// <summary>vmangos Object::BuildMovementUpdate, build &gt; 1.8.4 branch.</summary>
    private static void WriteMovementBlock(PacketWriter writer, WorldObject obj, ObjectUpdateFlags flags, uint serverTimeMs)
    {
        writer.WriteByte((byte)flags);

        if ((flags & ObjectUpdateFlags.Living) != 0)
        {
            var unit = (Unit)obj;
            MovementInfo movement = unit.Movement;

            // Splines are not driven by ArcaneCore yet; never advertise one we cannot describe.
            movement.Flags &= ~MovementFlags.SplineEnabled;
            if (movement.Time == 0)
            {
                // vmangos: an object that never moved reports "now + 1000" at its current position.
                movement.Time = serverTimeMs + 1000;
                movement.X = unit.X;
                movement.Y = unit.Y;
                movement.Z = unit.Z;
                movement.Orientation = unit.Orientation;
            }

            movement.Write(writer);
            writer.WriteSingle(unit.WalkSpeed);
            writer.WriteSingle(unit.RunSpeed);
            writer.WriteSingle(unit.RunBackSpeed);
            writer.WriteSingle(unit.SwimSpeed);
            writer.WriteSingle(unit.SwimBackSpeed);
            writer.WriteSingle(unit.TurnRate);
        }
        else if ((flags & ObjectUpdateFlags.HasPosition) != 0)
        {
            writer.WriteSingle(obj.X);
            writer.WriteSingle(obj.Y);
            writer.WriteSingle(obj.Z);
            writer.WriteSingle(obj.Orientation);
        }

        if ((flags & ObjectUpdateFlags.HighGuid) != 0)
        {
            writer.WriteUInt32(0); // "unk uint32"
        }

        if ((flags & ObjectUpdateFlags.All) != 0)
        {
            writer.WriteUInt32(1); // "unk uint32"
        }

        if ((flags & ObjectUpdateFlags.MeleeAttacking) != 0)
        {
            writer.WriteByte(0); // empty packed GUID: no melee victim yet
        }

        if ((flags & ObjectUpdateFlags.Transport) != 0)
        {
            writer.WriteUInt32(serverTimeMs);
        }
    }
}
