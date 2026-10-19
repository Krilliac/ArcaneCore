using System.Buffers.Binary;
using System.Numerics;
using ArcaneCore.Atlas.Reference.Navmesh;
using ArcaneCore.Game.Maps.Collision.MMaps;
using DotRecast.Detour;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Navmesh;

public sealed class MmapTileReaderTests
{
    private sealed record Refusal(string Name, Action<byte[], BuiltTile> Corrupt, string Expect);

    private static TileSpec Spec(bool bv = true, int linkSize = 16, IReadOnlyList<OffMeshSpec>? offMesh = null) => new()
    {
        TerrainX = 32,
        TerrainY = 32,
        Cells = 3,
        Flags = (i, j) => (ushort)(1 << ((i + j) % 4)),
        BvTree = bv,
        LinkSize = linkSize,
        OffMesh = offMesh ?? [],
        UsesLiquids = true,
    };

    private static readonly OffMeshSpec Connection = new(
        SyntheticNav.TileMinRecast(32, 32) + new Vector3(20, 10, 20), SyntheticNav.TileMinRecast(32, 32) + new Vector3(400, 10, 400), 3f, Bidirectional: true, UserId: 77);

    private static readonly Refusal[] Refusals =
    [
        new("prefix magic", (b, _) => Put32(b, 0, 0x50414d4d), "bad mmap magic"),
        new("cMaNGOS mmap version 8", (b, _) => Put32(b, 8, 8), "mmap tile version 8"),
        new("mmap version 5", (b, _) => Put32(b, 8, 5), "mmap tile version 5"),
        new("prefix Detour version", (b, _) => Put32(b, 4, 6), "Detour version 6 in the prefix"),
        new("size past the file", (b, _) => Put32(b, 12, (uint)b.Length), "exceeds"),
        new("Detour magic", (b, t) => Put32(b, t.Layout.Header, 0x12345678), "not a Detour tile"),
        new("Detour header version", (b, t) => Put32(b, t.Layout.Header + 4, 6), "Detour tile version 6"),
        new("negative poly count", (b, t) => Put32(b, t.Layout.Header + 24, 0xffffffff), "out of range"),
        new("poly count not matching the size", (b, t) => Put32(b, t.Layout.Header + 24, (uint)t.TotalPolys + 1), "does not match its header"),
        new("empty link table", (b, t) => Put32(b, t.Layout.Header + 32, 0), "link table of 0 links"),
        new("ground polygon with two vertices", (b, t) => b[t.Layout.Polys + 30] = 2, "polygon 0 has 2 vertices"),
        new("polygon vertex past the vertex table", (b, t) => Put16(b, t.Layout.Polys + 4, 0xffff), "names vertex"),
        new("polygon neighbour past the polygon table", (b, t) => Put16(b, t.Layout.Polys + 16, (ushort)(t.TotalPolys + 5)), "names neighbour"),
        new("detail triangles past their table", (b, t) => b[t.Layout.DetailMeshes + 9] = 200, "reaches past"),
        new("detail triangle outside its sub-mesh", (b, t) => b[t.Layout.DetailTris] = 9, "outside its sub-mesh"),
        new("BV escape past the tree", (b, t) => Put32(b, t.Layout.Bv + 12, unchecked((uint)-100000)), "BV node 0"),
        new("BV leaf past the polygons", (b, t) => Put32(b, t.Layout.Bv + 16 + 12, 4000), "BV node 1 names polygon 4000"),
        new("off-mesh connection naming a ground polygon", (b, t) => Put16(b, t.Layout.OffMesh + 28, 0), "not an off-mesh polygon"),
        new("off-mesh connection past the polygons", (b, t) => Put16(b, t.Layout.OffMesh + 28, 500), "not an off-mesh polygon"),
    ];

    public static TheoryData<string> RefusalNames { get; } = [.. Refusals.Select(r => r.Name)];

    [Fact]
    public void Parse_ReadsTheTileAsWritten()
    {
        BuiltTile built = SyntheticNav.Build(Spec(offMesh: [Connection]));
        byte[] before = (byte[])built.File.Clone();

        ParsedNavTile tile = MmapTileReader.Parse(built.File);

        Assert.Equal(before, built.File);
        Assert.Equal(NavMeshFormat.MmapMagic, tile.Prefix.Magic);
        Assert.Equal(NavMeshFormat.DetourVersion, tile.Prefix.DetourVersion);
        Assert.Equal(NavMeshFormat.MmapVersion, tile.Prefix.MmapVersion);
        Assert.Equal((uint)(built.File.Length - MmapTileHeader.ByteSize), tile.Prefix.Size);
        Assert.True(tile.Prefix.UsesLiquids);
        Assert.Equal(16, tile.LinkSize);
        Assert.Equal(built.File.Length, tile.FileBytes);

        DtMeshHeader h = tile.Data.header;
        Assert.Equal((built.DetourX, built.DetourY, 0), (h.x, h.y, h.layer));
        Assert.Equal(9 + 1, h.polyCount);
        Assert.Equal(9, h.offMeshBase);
        Assert.Equal(16 + 2, h.vertCount);
        Assert.Equal(1, h.offMeshConCount);
        Assert.Equal(18, h.bvNodeCount);
        Assert.Equal(built.MaxLinkCount, h.maxLinkCount);
        Assert.Equal(9, h.detailMeshCount);
        Assert.Equal(18, h.detailTriCount);
        Assert.Equal(h.vertCount * 3, tile.RawVertices.Length);
        Assert.Equal(tile.Data.verts, tile.RawVertices);
        Assert.NotSame(tile.Data.verts, tile.RawVertices);

        DtPoly first = tile.Data.polys[0];
        Assert.Equal(4, first.vertCount);
        Assert.Equal(1, first.flags);
        Assert.Equal(1, first.GetArea());
        Assert.Equal(DtPolyTypes.DT_POLYTYPE_GROUND, first.GetPolyType());
        Assert.Equal(0x8000 | 4, first.neis[0]);

        DtPoly offMesh = tile.Data.polys[9];
        Assert.Equal(DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION, offMesh.GetPolyType());
        Assert.Equal(2, offMesh.vertCount);
        DtOffMeshConnection con = Assert.Single(tile.Data.offMeshCons);
        Assert.Equal(9, con.poly);
        Assert.Equal(77, con.userId);
        Assert.Equal(3f, con.rad);
        Assert.Equal(DtDetour.DT_OFFMESH_CON_BIDIR, con.flags);
        Assert.Equal(Connection.EndRecast.X, con.pos[1].X);
    }

    [Fact]
    public void Parse_AcceptsThe32BitLinkLayout()
    {
        ParsedNavTile tile = MmapTileReader.Parse(SyntheticNav.Build(Spec(linkSize: 12)).File);

        Assert.Equal(12, tile.LinkSize);
        Assert.Equal(9, tile.Data.header.polyCount);
    }

    [Fact]
    public void Parse_AcceptsATileWithoutABvTree()
    {
        ParsedNavTile tile = MmapTileReader.Parse(SyntheticNav.Build(Spec(bv: false)).File);

        Assert.Equal(0, tile.Data.header.bvNodeCount);
    }

    [Fact]
    public void Parse_AgreesWithTheServersReader_OnVerticesPolygonsAndDetail()
    {
        BuiltTile built = SyntheticNav.Build(Spec(offMesh: [Connection]));

        ParsedNavTile dot = MmapTileReader.Parse(built.File);
        NavMeshTile server = NavMeshTile.ParseFile(built.File);

        Assert.Equal((dot.Data.header.x, dot.Data.header.y, dot.Data.header.layer), (server.X, server.Y, server.Layer));
        Assert.Equal(server.Vertices.Length, dot.Data.header.vertCount);
        for (int i = 0; i < server.Vertices.Length; i++)
        {
            Assert.Equal(server.Vertices[i], new Vector3(dot.RawVertices[i * 3], dot.RawVertices[(i * 3) + 1], dot.RawVertices[(i * 3) + 2]));
        }

        Assert.Equal(server.Polys.Length, dot.Data.polys.Length);
        for (int p = 0; p < server.Polys.Length; p++)
        {
            NavPoly s = server.Polys[p];
            DtPoly d = dot.Data.polys[p];
            Assert.Equal(s.Flags, d.flags);
            Assert.Equal(s.Area, d.GetArea());
            Assert.Equal(s.Type, d.GetPolyType());
            Assert.Equal(s.VertexCount, d.vertCount);
            Assert.Equal(s.Vertices.Select(v => (int)v), d.verts.Take(d.vertCount));
            Assert.Equal(s.Neighbours.Select(v => (int)v), d.neis.Take(d.vertCount));
        }
    }

    [Theory]
    [MemberData(nameof(RefusalNames))]
    public void Parse_RefusesACorruptTile_NamingTheFault(string name)
    {
        Refusal refusal = Refusals.Single(r => r.Name == name);
        BuiltTile built = SyntheticNav.Build(Spec(offMesh: [Connection]));
        byte[] file = (byte[])built.File.Clone();
        refusal.Corrupt(file, built);

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => MmapTileReader.Parse(file));

        Assert.Contains(refusal.Expect, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesAFileShorterThanItsHeaders()
    {
        BuiltTile built = SyntheticNav.Build(Spec());

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => MmapTileReader.Parse(built.File.AsSpan(0, 100).ToArray()));

        Assert.Contains("shorter than", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesATruncatedTile()
    {
        BuiltTile built = SyntheticNav.Build(Spec());

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => MmapTileReader.Parse(built.File.AsSpan(0, built.File.Length - 8).ToArray()));

        Assert.Contains("exceeds", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsNull() => Assert.Throws<ArgumentNullException>(() => MmapTileReader.Parse(null!));

    [Fact]
    public void Params_ReadTheSevenFieldsAsTheServersReaderDoes()
    {
        byte[] file = SyntheticNav.BuildParams(maxTiles: 9);

        NavMeshParams parsed = MmapParamsReader.Parse(file);

        Assert.Equal(new Vector3(SyntheticNav.OriginXz, 0, SyntheticNav.OriginXz), parsed.Origin);
        Assert.Equal(SyntheticNav.TileSize, parsed.TileWidth);
        Assert.Equal(SyntheticNav.TileSize, parsed.TileHeight);
        Assert.Equal(9, parsed.MaxTiles);
        Assert.Equal(0, parsed.MaxPolys);
        Assert.Equal(NavMeshParams.Parse(file), parsed);
    }

    [Theory]
    [InlineData("short", "a dtNavMeshParams is 28")]
    [InlineData("long", "a dtNavMeshParams is 28")]
    [InlineData("origin", "origin is not finite")]
    [InlineData("width", "is not a positive finite size")]
    [InlineData("maxTiles0", "outside 1..4096")]
    [InlineData("maxTilesBig", "outside 1..4096")]
    [InlineData("maxPolys", "maxPolys -1 is negative")]
    public void Params_RefuseWhatIsNotAGeneratorParameterBlock(string corruption, string expect)
    {
        byte[] file = SyntheticNav.BuildParams(maxTiles: 9);
        file = corruption switch
        {
            "short" => file[..27],
            "long" => [.. file, 0],
            "origin" => Set(file, 4, BitConverter.SingleToUInt32Bits(float.NaN)),
            "width" => Set(file, 12, BitConverter.SingleToUInt32Bits(-1f)),
            "maxTiles0" => Set(file, 20, 0),
            "maxTilesBig" => Set(file, 20, 4097),
            "maxPolys" => Set(file, 24, uint.MaxValue),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
        };

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => MmapParamsReader.Parse(file));
        Assert.Contains(expect, e.Message, StringComparison.Ordinal);
    }

    private static byte[] Set(byte[] bytes, int at, uint value)
    {
        Put32(bytes, at, value);
        return bytes;
    }

    private static void Put32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);

    private static void Put16(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
}
