using System.Buffers.Binary;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.DebugDraw;

/// <summary>One marker as the client knows it: a client-only game object.</summary>
/// <param name="Guid">Its GUID (a reserved marker entry, so it never equals a real object's).</param>
/// <param name="Kind">What it shows.</param>
/// <param name="Position">Where it stands.</param>
/// <param name="DisplayId">The model it is drawn with (the kind's model, or the kind's glow for a glow companion).</param>
public readonly record struct DebugMarker(ObjectGuid Guid, DebugMarkerKind Kind, Vector3 Position, uint DisplayId);

/// <summary>
/// The packets of a client-only marker. A marker is a game object that exists only in one client: its SMSG_UPDATE_OBJECT create block is
/// written for that viewer (the same layout <see cref="UpdateBlockWriter"/> writes for a real object) and sent to that session alone, and
/// SMSG_DESTROY_OBJECT removes it. The server never adds it to a map, so it is not in any visibility set, cannot be seen by anyone else,
/// takes no part in the simulation and is never saved.
/// </summary>
public static class DebugMarkerPackets
{
    /// <summary>The GUID of marker <paramref name="counter"/> of <paramref name="kind"/> (counter: 24 bits, never 0).</summary>
    public static ObjectGuid MarkerGuid(DebugMarkerKind kind, uint counter)
        => ObjectGuid.WithEntry(HighGuid.GameObject, DebugMarkerStyles.Of(kind).Entry, counter == 0 ? 1u : counter);

    /// <summary>
    /// The create blocks of <paramref name="markers"/> for <paramref name="viewer"/>, handed to <paramref name="send"/> as one or more
    /// SMSG_(COMPRESSED_)UPDATE_OBJECT packets (split at <see cref="UpdateData.MaxBodySize"/>).
    /// </summary>
    public static void SendCreates(Player viewer, IEnumerable<DebugMarker> markers, Action<WorldOpcode, byte[]> send, int compressionThreshold, uint serverTimeMs)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(send);
        var data = new UpdateData();
        foreach (DebugMarker marker in markers)
        {
            GameObject go = CreateDetached(marker, viewer.Orientation);
            PacketWriter writer = data.BeginBlock();
            UpdateBlockWriter.WriteCreateBlock(writer, go, viewer, isNewObject: true, serverTimeMs);
            data.EndBlock();
        }

        data.Flush(send, compressionThreshold);
    }

    /// <summary>SMSG_DESTROY_OBJECT: the full 8-byte GUID (vanilla).</summary>
    public static byte[] Destroy(ObjectGuid guid)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, guid.Value);
        return payload;
    }

    /// <summary>SMSG_PLAY_SPELL_VISUAL (vmangos WorldObject::PlaySpellVisual): u64 guid, u32 SpellVisualKit.dbc id.</summary>
    public static byte[] PlaySpellVisual(ObjectGuid guid, uint kitId)
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt64(guid.Value);
        writer.WriteUInt32(kitId);
        return writer.ToArray();
    }

    /// <summary>
    /// A game object for the create block only: the marker's synthetic template, position, facing and model. It is never added to a map.
    /// </summary>
    internal static GameObject CreateDetached(DebugMarker marker, float orientation)
    {
        GameObjectTemplate template = DebugMarkerStyles.FindTemplate(marker.Guid.Entry)
            ?? throw new ArgumentException("not a debug marker GUID", nameof(marker));
        var go = new GameObject(marker.Guid.Counter, template, spawn: null);
        go.SetPosition(marker.Position.X, marker.Position.Y, marker.Position.Z, orientation);
        go.InitializeFields();
        if (marker.DisplayId != 0)
        {
            go.SetUInt32(UpdateFields.GameobjectDisplayid, marker.DisplayId);
        }

        return go;
    }
}
