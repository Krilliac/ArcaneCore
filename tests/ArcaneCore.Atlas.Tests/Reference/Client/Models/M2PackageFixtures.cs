using System.Buffers.Binary;
using System.Text;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Models;

/// <summary>One vertex of a package fixture: position, normal and the first uv set.</summary>
internal readonly record struct PackageVertex(float X, float Y, float Z, float U, float V);

/// <summary>One submesh: a range of the view's triangle-index array (vertex indices, three per triangle).</summary>
internal readonly record struct PackageSubmesh(ushort Id, ushort StartIndex, ushort IndexCount, ushort Level = 0);

/// <summary>One texture unit (a batch): which submesh, which render-flags row, which texture-lookup row.</summary>
internal readonly record struct PackageUnit(ushort Submesh, ushort RenderFlags, ushort TextureLookup, sbyte Priority = 0, ushort ShaderId = 0);

/// <summary>One texture entry: type 0 is a file named <see cref="Name"/>, any other type is a replaceable texture (11 = creature skin 1).</summary>
internal readonly record struct PackageTexture(uint Type, string Name);

/// <summary>
/// Builds a 1.12.1 M2 (version 256) with the tables the package builder reads. Header offsets are the vanilla layout the existing
/// reader already uses for vertices (0x44) and views (0x4C), extended with the wowdev vanilla positions: textures 0x5C, render flags
/// 0x84, texture lookup 0x94 (the replaceable-texture lookup sits at 0x7C and the bone lookup at 0x8C). Per-record layouts: texture 16 bytes (type, flags, name length, name offset), render flags 4 bytes
/// (flags u16, blend u16), vertex 48 bytes (uv0 at +32), view 44 bytes (submeshes at +24, texture units at +32), submesh 32 bytes,
/// texture unit 24 bytes. A real file's own header proves the offsets in the env-gated tests; nothing here is a client file.
/// </summary>
internal static class M2PackageFixtures
{
    public static byte[] Build(
        IReadOnlyList<PackageVertex> vertices,
        IReadOnlyList<ushort> indices,
        IReadOnlyList<PackageSubmesh> submeshes,
        IReadOnlyList<PackageUnit> units,
        IReadOnlyList<PackageTexture> textures,
        IReadOnlyList<(ushort Flags, ushort Blend)> renderFlags,
        IReadOnlyList<ushort> textureLookup,
        string name = "package")
    {
        var buffer = new List<byte>(new byte[0x140]);
        int Append(byte[] bytes)
        {
            int at = buffer.Count;
            buffer.AddRange(bytes);
            return at;
        }

        byte[] nameBytes = Encoding.Latin1.GetBytes(name + "\0");
        int nameAt = Append(nameBytes);

        var vertexBytes = new byte[48 * vertices.Count];
        for (int i = 0; i < vertices.Count; i++)
        {
            Span<byte> v = vertexBytes.AsSpan(48 * i);
            BinaryPrimitives.WriteSingleLittleEndian(v, vertices[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(v[4..], vertices[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(v[8..], vertices[i].Z);
            v[12] = 255;
            BinaryPrimitives.WriteSingleLittleEndian(v[28..], 1f);
            BinaryPrimitives.WriteSingleLittleEndian(v[32..], vertices[i].U);
            BinaryPrimitives.WriteSingleLittleEndian(v[36..], vertices[i].V);
        }

        int vertexAt = Append(vertexBytes);
        ushort[] lookup = [.. Enumerable.Range(0, vertices.Count).Select(i => (ushort)i)];
        int lookupAt = Append(U16(lookup));
        int indicesAt = Append(U16(indices));

        var submeshBytes = new byte[32 * submeshes.Count];
        for (int i = 0; i < submeshes.Count; i++)
        {
            Span<byte> s = submeshBytes.AsSpan(32 * i);
            BinaryPrimitives.WriteUInt16LittleEndian(s, submeshes[i].Id);
            BinaryPrimitives.WriteUInt16LittleEndian(s[2..], submeshes[i].Level);
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(s[6..], (ushort)vertices.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(s[8..], submeshes[i].StartIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(s[10..], submeshes[i].IndexCount);
        }

        int submeshAt = Append(submeshBytes);

        var unitBytes = new byte[24 * units.Count];
        for (int i = 0; i < units.Count; i++)
        {
            Span<byte> u = unitBytes.AsSpan(24 * i);
            u[1] = (byte)units[i].Priority;
            BinaryPrimitives.WriteUInt16LittleEndian(u[2..], units[i].ShaderId);
            BinaryPrimitives.WriteUInt16LittleEndian(u[4..], units[i].Submesh);
            BinaryPrimitives.WriteUInt16LittleEndian(u[10..], units[i].RenderFlags);
            BinaryPrimitives.WriteUInt16LittleEndian(u[16..], units[i].TextureLookup);
        }

        int unitAt = Append(unitBytes);

        var view = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)lookup.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(4), (uint)lookupAt);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(8), (uint)indices.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(12), (uint)indicesAt);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(24), (uint)submeshes.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(28), (uint)submeshAt);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(32), (uint)units.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(36), (uint)unitAt);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(40), 256);
        int viewAt = Append(view);

        var textureBytes = new byte[16 * textures.Count];
        for (int i = 0; i < textures.Count; i++)
        {
            Span<byte> t = textureBytes.AsSpan(16 * i);
            BinaryPrimitives.WriteUInt32LittleEndian(t, textures[i].Type);
            if (textures[i].Type == 0)
            {
                byte[] file = Encoding.Latin1.GetBytes(textures[i].Name + "\0");
                int at = Append(file);
                BinaryPrimitives.WriteUInt32LittleEndian(t[8..], (uint)file.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(t[12..], (uint)at);
            }
        }

        int textureAt = Append(textureBytes);

        var flagBytes = new byte[4 * renderFlags.Count];
        for (int i = 0; i < renderFlags.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(flagBytes.AsSpan(4 * i), renderFlags[i].Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(flagBytes.AsSpan((4 * i) + 2), renderFlags[i].Blend);
        }

        int flagsAt = Append(flagBytes);
        int textureLookupAt = Append(U16(textureLookup));

        byte[] file2 = [.. buffer];
        "MD20"u8.CopyTo(file2);
        void Put(int offset, int value) => BinaryPrimitives.WriteUInt32LittleEndian(file2.AsSpan(offset), (uint)value);
        Put(0x04, 256);
        Put(0x08, nameBytes.Length);
        Put(0x0C, nameAt);
        Put(0x44, vertices.Count);
        Put(0x48, vertexAt);
        Put(0x4C, 1);
        Put(0x50, viewAt);
        Put(0x5C, textures.Count);
        Put(0x60, textureAt);
        Put(0x84, renderFlags.Count);
        Put(0x88, flagsAt);
        Put(0x94, textureLookup.Count);
        Put(0x98, textureLookupAt);
        return file2;
    }

    /// <summary>
    /// Four vertices in two quads' worth of triangles: submesh 0 is triangle (0,1,2) and submesh 1 is triangle (0,2,3). Unit 0 uses
    /// the file texture with opaque render flags 0; unit 1 uses the replaceable type 11 with render flags 1 (two-sided, alpha key).
    /// </summary>
    public static byte[] TwoBatches(string textureName = "Creature\\Test\\Skin.blp")
        => Build(
            [new(0, 0, 0, 0.25f, 0.75f), new(1, 0, 0, 1, 1), new(1, 1, 0, 1, 0), new(0, 1, 0, 0, 0)],
            [0, 1, 2, 0, 2, 3],
            [new(0, 0, 3), new(1, 3, 3)],
            [new(0, 0, 0), new(1, 1, 1)],
            [new(0, textureName), new(11, string.Empty)],
            [(0, 0), (0x04, 1)],
            [0, 1]);

    private static byte[] U16(IReadOnlyList<ushort> values)
    {
        var bytes = new byte[2 * values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2 * i), values[i]);
        }

        return bytes;
    }
}
