using System.Buffers.Binary;
using System.Numerics;
using ArcaneCore.Game.DebugDraw;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.DebugDraw;

/// <summary>
/// The client-only marker packets (docs/areas/debug-draw.md): reserved GUIDs, the synthetic goober templates, and a create block that
/// carries the marker's entry, model, scale and position without the object ever entering a map.
/// </summary>
public sealed class DebugMarkerPacketTests
{
    [Fact]
    public void EveryKind_HasItsOwnReservedEntry_AndAGooberTemplate()
    {
        var entries = new HashSet<uint>();
        foreach (DebugMarkerKind kind in Enum.GetValues<DebugMarkerKind>())
        {
            DebugMarkerStyle style = DebugMarkerStyles.Of(kind);
            Assert.Equal(kind, style.Kind);
            Assert.True(entries.Add(style.Entry));
            Assert.True(DebugMarkerStyles.IsMarkerEntry(style.Entry));
            Assert.True(style.Entry <= 0x00FFFFFF); // fits the 24 entry bits of a GUID

            GameObjectTemplate template = Assert.IsType<GameObjectTemplate>(DebugMarkerStyles.FindTemplate(style.Entry));
            Assert.Equal((uint)GameObjectType.Goober, template.Type); // the 1.12 client only shows a hover name on an interactive object
            Assert.Equal(style.DisplayId, template.DisplayId);
            Assert.StartsWith("DebugDraw: ", template.Name, StringComparison.Ordinal);
            Assert.All(template.Data, d => Assert.Equal(0u, d)); // no lock, quest or spell: a use does nothing client-side
        }

        Assert.Equal(DebugMarkerStyles.All.Count, entries.Count);
        Assert.Null(DebugMarkerStyles.FindTemplate(DebugMarkerStyles.EntryBase - 1));
        Assert.Null(DebugMarkerStyles.FindTemplate(DebugMarkerStyles.EntryBase + 0xFF)); // reserved, unused
        Assert.Null(DebugMarkerStyles.FindTemplate(20001));
    }

    [Fact]
    public void MarkerGuids_AreGameObjectGuidsOfTheKindsEntry_AndNeverZero()
    {
        ObjectGuid guid = DebugMarkerPackets.MarkerGuid(DebugMarkerKind.PathCorner, 42);
        Assert.Equal(HighGuid.GameObject, guid.High);
        Assert.Equal(DebugMarkerStyles.Of(DebugMarkerKind.PathCorner).Entry, guid.Entry);
        Assert.Equal(42u, guid.Counter);
        Assert.True(DebugMarkerStyles.IsMarkerGuid(guid));
        Assert.Equal(1u, DebugMarkerPackets.MarkerGuid(DebugMarkerKind.Cell, 0).Counter);

        // A real game object of a content entry never qualifies, even with the same counter.
        Assert.False(DebugMarkerStyles.IsMarkerGuid(ObjectGuid.WithEntry(HighGuid.GameObject, 20001, 42)));
        Assert.False(DebugMarkerStyles.IsMarkerGuid(ObjectGuid.WithEntry(HighGuid.Unit, DebugMarkerStyles.EntryBase, 42)));
    }

    [Fact]
    public void SendCreates_WritesOneCreateBlockPerMarker_WithEntryModelScaleAndPosition()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var session = new FakeSession();
        Player viewer = TestWorld.CreatePlayer(1, 0, 0, session);

        var a = new DebugMarker(DebugMarkerPackets.MarkerGuid(DebugMarkerKind.LosBlocked, 7), DebugMarkerKind.LosBlocked, new Vector3(10, 20, 30), 2973);
        var glow = new DebugMarker(DebugMarkerPackets.MarkerGuid(DebugMarkerKind.LosBlocked, 8), DebugMarkerKind.LosBlocked, new Vector3(10, 20, 30), 1308);
        List<(WorldOpcode Opcode, byte[] Payload)> sent = [];
        DebugMarkerPackets.SendCreates(viewer, [a, glow], (op, p) => sent.Add((op, p)), compressionThreshold: 0, serverTimeMs: 1000);

        (WorldOpcode opcode, byte[] body) = Assert.Single(sent);
        Assert.Equal(WorldOpcode.SmsgUpdateObject, opcode);
        var reader = new PacketReader(body);
        Assert.Equal(2u, reader.ReadUInt32()); // blocks
        Assert.Equal(0, reader.ReadByte());    // no transport

        foreach (DebugMarker marker in new[] { a, glow })
        {
            (ObjectGuid guid, Vector3 position, Dictionary<int, uint> values) = ReadGameObjectCreate(ref reader);
            Assert.Equal(marker.Guid, guid);
            Assert.Equal(marker.Position, position);
            Assert.Equal(marker.Guid.Entry, values[UpdateFields.ObjectFieldEntry]);
            Assert.Equal(marker.DisplayId, values[UpdateFields.GameobjectDisplayid]);
            Assert.Equal(DebugMarkerStyles.Of(DebugMarkerKind.LosBlocked).Scale, BitConverter.UInt32BitsToSingle(values[UpdateFields.ObjectFieldScaleX]));
            Assert.Equal((uint)GameObjectType.Goober, values[UpdateFields.GameobjectTypeId]);
            Assert.Equal(10f, BitConverter.UInt32BitsToSingle(values[UpdateFields.GameobjectPosX]));
            Assert.False(values.ContainsKey(UpdateFields.GameobjectFlags)); // no GO_FLAG_NODESPAWN or any other flag
        }

        Assert.Equal(body.Length, reader.Position);
        Assert.Empty(viewer.VisibleObjects); // the server-side visibility set never learns of a marker
    }

    [Fact]
    public void Destroy_And_SpellVisual_HaveTheVanillaLayouts()
    {
        ObjectGuid guid = DebugMarkerPackets.MarkerGuid(DebugMarkerKind.Height, 3);
        Assert.Equal(guid.Value, BinaryPrimitives.ReadUInt64LittleEndian(DebugMarkerPackets.Destroy(guid)));

        byte[] kit = DebugMarkerPackets.PlaySpellVisual(ObjectGuid.Player(5), 179);
        Assert.Equal(12, kit.Length);
        Assert.Equal(ObjectGuid.Player(5).Value, BinaryPrimitives.ReadUInt64LittleEndian(kit));
        Assert.Equal(179u, BinaryPrimitives.ReadUInt32LittleEndian(kit.AsSpan(8)));
    }

    /// <summary>A game object create block (vmangos Object::BuildMovementUpdate, HAS_POSITION | ALL): its GUID, position and set values.</summary>
    private static (ObjectGuid Guid, Vector3 Position, Dictionary<int, uint> Values) ReadGameObjectCreate(ref PacketReader reader)
    {
        Assert.Equal((byte)ObjectUpdateType.CreateObject2, reader.ReadByte());
        var guid = new ObjectGuid(reader.ReadPackedGuid());
        Assert.Equal((byte)TypeId.GameObject, reader.ReadByte());
        Assert.Equal((byte)(ObjectUpdateFlags.All | ObjectUpdateFlags.HasPosition), reader.ReadByte());
        var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        reader.ReadSingle();   // orientation
        reader.ReadUInt32();   // UPDATEFLAG_ALL
        int blocks = reader.ReadByte();
        uint[] mask = new uint[blocks];
        for (int i = 0; i < blocks; i++)
        {
            mask[i] = reader.ReadUInt32();
        }

        var values = new Dictionary<int, uint>();
        for (int index = 0; index < blocks * 32; index++)
        {
            if ((mask[index >> 5] & (1u << (index & 31))) != 0)
            {
                values[index] = reader.ReadUInt32();
            }
        }

        return (guid, position, values);
    }
}
