using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Core;

public sealed class GeometryWireTests
{
    private static readonly TileKey Tile = new(0, 49, 31);

    private static GeometryChunk TwoTriangles()
    {
        WorldPoint origin = WorldGrid.Origin(Tile);
        var builder = new GeometryBuilder(LayerIds.Terrain, Tile, origin, part: 2);
        uint a = builder.AddVertex(origin.X - 1, origin.Y - 1, 50.25);
        uint b = builder.AddVertex(origin.X - 2, origin.Y - 1, 51.5);
        uint c = builder.AddVertex(origin.X - 1, origin.Y - 2, 52.75);
        uint d = builder.AddVertex(origin.X - 2, origin.Y - 2, 53);
        builder.AddTriangle(a, b, c, attribute: 7);
        builder.AddTriangle(b, d, c, attribute: 7);
        builder.AddPoint(d, attribute: 9);
        builder.DescribeAttribute(7, () => new JsonObject { ["colorKey"] = ColorKeys.Ground, ["square"] = 3 });
        builder.DescribeAttribute(9, () => new JsonObject { ["note"] = "corner" });
        builder.Stats["samples"] = 4;
        return builder.Build();
    }

    [Fact]
    public void SharedEdges_AreEmittedOnce()
    {
        GeometryChunk chunk = TwoTriangles();

        // Two triangles sharing edge b-c: 3 + 3 - 1 distinct edges.
        Assert.Equal(2, chunk.TriangleCount);
        Assert.Equal(5, chunk.LineCount);
        Assert.Equal(new uint[] { 7, 7, 7, 7, 7 }, chunk.LineAttributes);
    }

    [Fact]
    public void Header_SpellsAtg1_AndCarriesCountsAndOrigin()
    {
        GeometryChunk chunk = TwoTriangles();
        byte[] wire = GeometryWire.Write(chunk);

        Assert.Equal("ATG1"u8.ToArray(), wire[..4]);
        Assert.Equal(1, BitConverter.ToUInt16(wire, 4));
        Assert.Equal(1 | 2 | 4, BitConverter.ToUInt16(wire, 6));
        Assert.Equal(chunk.Origin.X, BitConverter.ToDouble(wire, 8));
        Assert.Equal(chunk.Origin.Y, BitConverter.ToDouble(wire, 16));
        Assert.Equal(0.0, BitConverter.ToDouble(wire, 24));
        Assert.Equal(4u, BitConverter.ToUInt32(wire, 32));
        Assert.Equal(2u, BitConverter.ToUInt32(wire, 36));
        Assert.Equal(5u, BitConverter.ToUInt32(wire, 40));
        Assert.Equal(1u, BitConverter.ToUInt32(wire, 44));

        // Positions start right after the 48-byte header: vertex 0 is (-1, -1, 50.25) from the origin.
        Assert.Equal(-1f, BitConverter.ToSingle(wire, 48));
        Assert.Equal(-1f, BitConverter.ToSingle(wire, 52));
        Assert.Equal(50.25f, BitConverter.ToSingle(wire, 56));

        // The JSON length word sits after every array: 48 + 4*(12 + 6 + 2 + 10 + 5 + 1 + 1) bytes.
        int jsonOffset = GeometryWire.HeaderBytes + (4 * (12 + 6 + 2 + 10 + 5 + 1 + 1));
        Assert.Equal((uint)(wire.Length - jsonOffset - 4), BitConverter.ToUInt32(wire, jsonOffset));
    }

    [Fact]
    public void RoundTrip_PreservesEverything()
    {
        GeometryChunk chunk = TwoTriangles();
        GeometryChunk read = GeometryWire.Read(GeometryWire.Write(chunk));

        Assert.Equal(chunk.LayerId, read.LayerId);
        Assert.Equal(chunk.Tile, read.Tile);
        Assert.Equal(2, read.Part);
        Assert.Equal(chunk.Origin, read.Origin);
        Assert.Equal(chunk.Positions, read.Positions);
        Assert.Equal(chunk.Triangles, read.Triangles);
        Assert.Equal(chunk.TriangleAttributes, read.TriangleAttributes);
        Assert.Equal(chunk.Lines, read.Lines);
        Assert.Equal(chunk.LineAttributes, read.LineAttributes);
        Assert.Equal(chunk.Points, read.Points);
        Assert.Equal(chunk.PointAttributes, read.PointAttributes);
        Assert.Equal(ColorKeys.Ground, read.Attributes[7]["colorKey"]!.GetValue<string>());
        Assert.Equal("corner", read.Attributes[9]["note"]!.GetValue<string>());
        Assert.Equal(4, read.Stats["samples"]!.GetValue<int>());
        Assert.Equal(new WorldPoint(chunk.Origin.X - 2, chunk.Origin.Y - 2, 53), read.Vertex(3));
    }

    [Fact]
    public void GlobalTile_RoundTrips()
    {
        var builder = new GeometryBuilder(LayerIds.VMapWmo, TileKey.Global(329), WorldGrid.Origin(TileKey.Global(329)));
        builder.AddLine(builder.AddVertex(1, 2, 3), builder.AddVertex(4, 5, 6));
        GeometryChunk read = GeometryWire.Read(GeometryWire.Write(builder.Build()));

        Assert.True(read.Tile.IsGlobal);
        Assert.Equal(329u, read.Tile.MapId);
        Assert.Equal(1, read.LineCount);
    }

    [Fact]
    public void Read_RejectsCorruptInput()
    {
        byte[] wire = GeometryWire.Write(TwoTriangles());

        Assert.Throws<InvalidDataException>(() => GeometryWire.Read(wire.AsSpan(0, 20)));
        Assert.Throws<InvalidDataException>(() => GeometryWire.Read(wire.AsSpan(0, wire.Length - 1)));

        byte[] badMagic = (byte[])wire.Clone();
        badMagic[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => GeometryWire.Read(badMagic));

        // A triangle index past the vertex count is refused, not drawn.
        byte[] badIndex = (byte[])wire.Clone();
        int firstTriangleIndex = GeometryWire.HeaderBytes + (4 * 3 * 4);
        BitConverter.GetBytes(99u).CopyTo(badIndex, firstTriangleIndex);
        Assert.Throws<InvalidDataException>(() => GeometryWire.Read(badIndex));
    }

    [Fact]
    public void Builder_RefusesNonFiniteVertices()
    {
        var builder = new GeometryBuilder(LayerIds.Terrain, Tile, WorldGrid.Origin(Tile));

        Assert.Throws<ArgumentException>(() => builder.AddVertex(double.NaN, 0, 0));
        Assert.Throws<ArgumentException>(() => builder.AddVertex(0, double.PositiveInfinity, 0));
    }

    [Fact]
    public void LayerBuild_Of_ReportsEmptyWhenNothingWasBuilt()
    {
        LayerBuild empty = LayerBuild.Of(LayerIds.Liquid, Tile, []);
        LayerBuild ok = LayerBuild.Of(LayerIds.Terrain, Tile, [TwoTriangles()]);

        Assert.Equal(TileState.Empty, empty.Status.State);
        Assert.Equal(TileState.Ok, ok.Status.State);
        Assert.Equal(2 + 5 + 1, ok.Status.Primitives);
    }
}
