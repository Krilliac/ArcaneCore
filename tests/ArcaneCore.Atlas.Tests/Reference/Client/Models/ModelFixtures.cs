using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Models;

/// <summary>One triangle of a synthetic WMO group: three vertex indices and the MOPY flags byte.</summary>
internal readonly record struct SyntheticTriangle(ushort A, ushort B, ushort C, byte Flags = 0x20, byte Material = 0);

/// <summary>A synthetic WMO group file's content.</summary>
internal sealed class SyntheticWmoGroup
{
    public uint NameOffset { get; set; }

    public uint Flags { get; set; }

    public uint GroupId { get; set; } = 1;

    public List<Vector3> Vertices { get; } = [];

    public List<SyntheticTriangle> Triangles { get; } = [];

    public List<ushort> DoodadReferences { get; } = [];

    /// <summary>When set, MLIQ holds a 2x2-vertex, 1x1-tile liquid at these heights.</summary>
    public float[]? LiquidHeights { get; set; }

    /// <summary>Added to the MOGP chunk's size field (a negative value understates it, a positive one overruns the file).</summary>
    public int MogpSizeError { get; set; }

    /// <summary>When set, the MOVI chunk holds this many indices instead of three per triangle.</summary>
    public int? MoviIndexCount { get; set; }
}

/// <summary>A synthetic WMO root file's content.</summary>
internal sealed class SyntheticWmoRoot
{
    public uint Version { get; set; } = 17;

    public uint WmoId { get; set; } = 77;

    /// <summary>Group name strings; MOGN holds them back to back and each group's name offset points at its own.</summary>
    public List<string> GroupNames { get; } = [];

    public List<(string Name, uint First, uint Count)> DoodadSets { get; } = [];

    public List<(string Model, Vector3 Position, Quaternion Rotation, float Scale)> Doodads { get; } = [];

    public uint GroupCount { get; set; }

    public bool OmitMohd { get; set; }
}

/// <summary>Builds client-format files at runtime. Nothing here is a client file; tests never commit data.</summary>
internal static class ModelFixtures
{
    public static byte[] Chunk(string tag, byte[] payload)
    {
        var bytes = new byte[8 + payload.Length];
        for (int i = 0; i < 4; i++)
        {
            bytes[i] = (byte)tag[3 - i];
        }

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    public static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    public static byte[] WmoRoot(SyntheticWmoRoot root)
    {
        var mogn = new MemoryStream();
        var nameOffsets = new List<uint>();
        foreach (string name in root.GroupNames)
        {
            nameOffsets.Add((uint)mogn.Length);
            mogn.Write(Encoding.Latin1.GetBytes(name));
            mogn.WriteByte(0);
        }

        var modn = new MemoryStream();
        var modnOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach ((string model, _, _, _) in root.Doodads)
        {
            if (!modnOffsets.ContainsKey(model))
            {
                modnOffsets[model] = (uint)modn.Length;
                modn.Write(Encoding.Latin1.GetBytes(model));
                modn.WriteByte(0);
            }
        }

        var mohd = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(mohd.AsSpan(4), root.GroupCount);
        BinaryPrimitives.WriteUInt32LittleEndian(mohd.AsSpan(20), (uint)root.Doodads.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(mohd.AsSpan(24), (uint)root.DoodadSets.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(mohd.AsSpan(32), root.WmoId);
        WriteVector(mohd.AsSpan(36), new Vector3(-1, -2, -3));
        WriteVector(mohd.AsSpan(48), new Vector3(4, 5, 6));
        BinaryPrimitives.WriteUInt32LittleEndian(mohd.AsSpan(60), 0x0101);

        var mogi = new byte[32 * root.GroupCount];
        for (int i = 0; i < root.GroupCount; i++)
        {
            Span<byte> entry = mogi.AsSpan(32 * i, 32);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, 0x2000u + (uint)i);
            WriteVector(entry[4..], new Vector3(i, 0, 0));
            WriteVector(entry[16..], new Vector3(i + 1, 1, 1));
            BinaryPrimitives.WriteInt32LittleEndian(entry[28..], i < nameOffsets.Count ? (int)nameOffsets[i] : -1);
        }

        var mods = new byte[32 * root.DoodadSets.Count];
        for (int i = 0; i < root.DoodadSets.Count; i++)
        {
            Span<byte> entry = mods.AsSpan(32 * i, 32);
            Encoding.Latin1.GetBytes(root.DoodadSets[i].Name).CopyTo(entry);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[20..], root.DoodadSets[i].First);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], root.DoodadSets[i].Count);
        }

        var modd = new byte[40 * root.Doodads.Count];
        for (int i = 0; i < root.Doodads.Count; i++)
        {
            (string model, Vector3 position, Quaternion rotation, float scale) = root.Doodads[i];
            Span<byte> entry = modd.AsSpan(40 * i, 40);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, modnOffsets[model] | 0x01000000u);
            WriteVector(entry[4..], position);
            BinaryPrimitives.WriteSingleLittleEndian(entry[16..], rotation.X);
            BinaryPrimitives.WriteSingleLittleEndian(entry[20..], rotation.Y);
            BinaryPrimitives.WriteSingleLittleEndian(entry[24..], rotation.Z);
            BinaryPrimitives.WriteSingleLittleEndian(entry[28..], rotation.W);
            BinaryPrimitives.WriteSingleLittleEndian(entry[32..], scale);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[36..], 0xFF336699);
        }

        var version = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, root.Version);
        return Concat(
            Chunk("MVER", version),
            root.OmitMohd ? [] : Chunk("MOHD", mohd),
            Chunk("MOGN", mogn.ToArray()),
            Chunk("MOGI", mogi),
            Chunk("MODS", mods),
            Chunk("MODN", modn.ToArray()),
            Chunk("MODD", modd));
    }

    public static byte[] WmoGroup(SyntheticWmoGroup group)
    {
        var header = new byte[WmoGroupFileHeader];
        BinaryPrimitives.WriteUInt32LittleEndian(header, group.NameOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), group.Flags);
        WriteVector(header.AsSpan(12), new Vector3(-10, -10, -10));
        WriteVector(header.AsSpan(24), new Vector3(10, 10, 10));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(36), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(38), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(40), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(42), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(44), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48), 0x04030201);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52), 9);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56), group.GroupId);

        var mopy = new byte[2 * group.Triangles.Count];
        for (int i = 0; i < group.Triangles.Count; i++)
        {
            mopy[2 * i] = group.Triangles[i].Flags;
            mopy[(2 * i) + 1] = group.Triangles[i].Material;
        }

        int indexCount = group.MoviIndexCount ?? (3 * group.Triangles.Count);
        var movi = new byte[2 * indexCount];
        for (int i = 0; i < indexCount && i < 3 * group.Triangles.Count; i++)
        {
            SyntheticTriangle t = group.Triangles[i / 3];
            BinaryPrimitives.WriteUInt16LittleEndian(movi.AsSpan(2 * i), (i % 3) switch { 0 => t.A, 1 => t.B, _ => t.C });
        }

        var movt = new byte[12 * group.Vertices.Count];
        for (int i = 0; i < group.Vertices.Count; i++)
        {
            WriteVector(movt.AsSpan(12 * i), group.Vertices[i]);
        }

        var modr = new byte[2 * group.DoodadReferences.Count];
        for (int i = 0; i < group.DoodadReferences.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(modr.AsSpan(2 * i), group.DoodadReferences[i]);
        }

        var inner = new List<byte[]> { header, Chunk("MOPY", mopy), Chunk("MOVI", movi), Chunk("MOVT", movt) };
        if (group.DoodadReferences.Count > 0)
        {
            inner.Add(Chunk("MODR", modr));
        }

        if (group.LiquidHeights is { } heights)
        {
            var mliq = new byte[30 + (8 * 4) + 1];
            BinaryPrimitives.WriteInt32LittleEndian(mliq, 2);
            BinaryPrimitives.WriteInt32LittleEndian(mliq.AsSpan(4), 2);
            BinaryPrimitives.WriteInt32LittleEndian(mliq.AsSpan(8), 1);
            BinaryPrimitives.WriteInt32LittleEndian(mliq.AsSpan(12), 1);
            WriteVector(mliq.AsSpan(16), new Vector3(1, 2, 3));
            BinaryPrimitives.WriteUInt16LittleEndian(mliq.AsSpan(28), 5);
            for (int i = 0; i < 4; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(mliq.AsSpan(30 + (8 * i) + 4), heights[i]);
            }

            mliq[30 + 32] = 0x0F;
            inner.Add(Chunk("MLIQ", mliq));
        }

        byte[] body = Concat([.. inner]);
        var mogp = new byte[8 + body.Length];
        mogp[0] = (byte)'P';
        mogp[1] = (byte)'G';
        mogp[2] = (byte)'O';
        mogp[3] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(mogp.AsSpan(4), (uint)(body.Length + group.MogpSizeError));
        body.CopyTo(mogp, 8);
        var version = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, 17);
        return Concat(Chunk("MVER", version), mogp);
    }

    /// <summary>
    /// A 1.12.1 M2 (version 256): render vertices, one view, and an optional bounding hull. Offsets and counts sit at the
    /// header positions the reader uses.
    /// </summary>
    public static byte[] M2(
        IReadOnlyList<Vector3> vertices,
        IReadOnlyList<ushort> viewLookup,
        IReadOnlyList<ushort> viewTriangles,
        IReadOnlyList<Vector3> boundingVertices,
        IReadOnlyList<ushort> boundingIndices,
        uint version = 256,
        string name = "synthetic")
    {
        const int headerSize = 0x140;
        var buffer = new List<byte>(new byte[headerSize]);
        int Append(ReadOnlySpan<byte> bytes)
        {
            int at = buffer.Count;
            buffer.AddRange(bytes.ToArray());
            return at;
        }

        byte[] nameBytes = Encoding.Latin1.GetBytes(name + "\0");
        int nameAt = Append(nameBytes);
        var vertexBytes = new byte[48 * vertices.Count];
        for (int i = 0; i < vertices.Count; i++)
        {
            WriteVector(vertexBytes.AsSpan(48 * i), vertices[i]);
            vertexBytes[(48 * i) + 12] = 255;
        }

        int vertexAt = Append(vertexBytes);
        int lookupAt = Append(U16(viewLookup));
        int trianglesAt = Append(U16(viewTriangles));
        var view = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)viewLookup.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(4), (uint)lookupAt);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(8), (uint)viewTriangles.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(12), (uint)trianglesAt);
        BinaryPrimitives.WriteUInt32LittleEndian(view.AsSpan(40), 256);
        int viewAt = Append(view);
        int hullTrianglesAt = Append(U16(boundingIndices));
        var hullVertexBytes = new byte[12 * boundingVertices.Count];
        for (int i = 0; i < boundingVertices.Count; i++)
        {
            WriteVector(hullVertexBytes.AsSpan(12 * i), boundingVertices[i]);
        }

        int hullVerticesAt = Append(hullVertexBytes);

        byte[] file = [.. buffer];
        file[0] = (byte)'M';
        file[1] = (byte)'D';
        file[2] = (byte)'2';
        file[3] = (byte)'0';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x08), (uint)nameBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x0C), (uint)nameAt);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x44), (uint)vertices.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x48), (uint)vertexAt);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x4C), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x50), (uint)viewAt);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0xEC), (uint)boundingIndices.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0xF0), (uint)hullTrianglesAt);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0xF4), (uint)boundingVertices.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0xF8), (uint)hullVerticesAt);
        return file;
    }

    /// <summary>A unit-ish cube hull around the origin: 8 vertices, 12 triangles.</summary>
    public static byte[] CubeM2(float half = 1f, bool withHull = true)
    {
        Vector3[] corners =
        [
            new(-half, -half, -half), new(half, -half, -half), new(half, half, -half), new(-half, half, -half),
            new(-half, -half, half), new(half, -half, half), new(half, half, half), new(-half, half, half),
        ];
        ushort[] indices =
        [
            0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6, 0, 4, 5, 0, 5, 1,
            1, 5, 6, 1, 6, 2, 2, 6, 7, 2, 7, 3, 3, 7, 4, 3, 4, 0,
        ];
        ushort[] lookup = [0, 1, 2, 3, 4, 5, 6, 7];
        return M2(corners, lookup, indices, withHull ? corners : [], withHull ? indices : []);
    }

    /// <summary>A WMO group holding one big vertical wall in the plane x = <paramref name="x"/> (two triangles).</summary>
    public static SyntheticWmoGroup WallGroup(float x, byte flags = 0x20, uint groupFlags = 0)
    {
        var group = new SyntheticWmoGroup { Flags = groupFlags };
        group.Vertices.AddRange([new Vector3(x, -5, -5), new Vector3(x, 5, -5), new Vector3(x, 5, 5), new Vector3(x, -5, 5)]);
        group.Triangles.Add(new SyntheticTriangle(0, 1, 2, flags));
        group.Triangles.Add(new SyntheticTriangle(0, 2, 3, flags));
        return group;
    }

    private const int WmoGroupFileHeader = 68;

    private static byte[] U16(IReadOnlyList<ushort> values)
    {
        var bytes = new byte[2 * values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2 * i), values[i]);
        }

        return bytes;
    }

    private static void WriteVector(Span<byte> span, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(span, v.X);
        BinaryPrimitives.WriteSingleLittleEndian(span[4..], v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(span[8..], v.Z);
    }
}

/// <summary>An <see cref="IClientPlacementSource"/> that serves a fixed list per tile.</summary>
internal sealed class FakePlacements : IClientPlacementSource
{
    private readonly Dictionary<TileKey, List<ClientPlacement>> _tiles = [];

    public Dictionary<TileKey, string> Corrupt { get; } = [];

    public int Calls { get; private set; }

    public void Add(TileKey tile, params ClientPlacement[] placements)
    {
        if (!_tiles.TryGetValue(tile, out List<ClientPlacement>? list))
        {
            _tiles[tile] = list = [];
        }

        list.AddRange(placements);
    }

    public IReadOnlyList<ClientPlacement>? Placements(TileKey tile)
    {
        Calls++;
        if (Corrupt.TryGetValue(tile, out string? message))
        {
            throw new InvalidDataException(message);
        }

        return _tiles.TryGetValue(tile, out List<ClientPlacement>? list) ? list : null;
    }
}

/// <summary>An <see cref="IMapDirectory"/> over a fixed list.</summary>
internal sealed class FakeMapDirectory(params ClientMap[] maps) : IMapDirectory
{
    public IReadOnlyList<ClientMap> Maps { get; } = maps;

    public bool TryGetMap(uint mapId, out ClientMap map)
    {
        ClientMap? found = maps.FirstOrDefault(m => m.Id == mapId);
        map = found!;
        return found is not null;
    }

    public string? AreaName(uint areaId) => null;
}
