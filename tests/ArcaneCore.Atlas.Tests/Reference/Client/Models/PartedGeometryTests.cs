using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Models;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Models;

/// <summary>Edge emission of <see cref="PartedGeometry"/>: shared edges once per part, and a hash that spreads mesh edges.</summary>
public sealed class PartedGeometryTests
{
    private static readonly TileKey Tile = new(0, 49, 31);

    [Fact]
    public void SharedEdgesAreDrawnOncePerPart()
    {
        var geometry = new PartedGeometry(LayerIds.RefWmo, Tile, WorldGrid.Origin(Tile));
        uint attribute = geometry.Attribute("a", () => []);
        GeometryBuilder builder = geometry.Writable(1024);
        uint[] v = [.. Enumerable.Range(0, 4).Select(i => builder.AddVertex(-9000 + i, 100 + (i % 2), 50))];

        // Two triangles sharing the edge v1-v2 (in opposite directions), and a degenerate one that adds no edge.
        geometry.AddTriangle(builder, v[0], v[1], v[2], attribute);
        geometry.AddTriangle(builder, v[2], v[1], v[3], attribute);
        geometry.AddTriangle(builder, v[3], v[3], v[3], attribute);
        Assert.Equal(3, builder.TriangleCount);
        Assert.Equal(5, builder.LineCount);

        // A new part has its own vertex indices, so the same index pair is a new edge there.
        GeometryBuilder next = geometry.NextPart();
        uint[] w = [.. Enumerable.Range(0, 3).Select(i => next.AddVertex(-9000 + i, 100, 50))];
        geometry.AddTriangle(next, w[0], w[1], w[2], attribute);
        Assert.Equal(3, next.LineCount);
        Assert.Throws<InvalidOperationException>(() => geometry.AddTriangle(builder, v[0], v[1], v[3], attribute));

        IReadOnlyList<GeometryChunk> chunks = geometry.Finish(new JsonObject());
        Assert.Equal<int>([5, 3], chunks.Select(c => c.LineCount));
    }

    [Fact]
    public void EdgeKeysOfAMeshSpreadOverTheHashRange()
    {
        // The edges of a 1000 x 100 vertex grid: neighbours differ by 1 or by the row length, the pattern that makes the
        // default ulong hash (low ^ high) collapse into a few hundred values.
        const uint Row = 1000;
        var keys = new List<ulong>();
        for (uint y = 0; y + 1 < 100; y++)
        {
            for (uint x = 0; x + 1 < Row; x++)
            {
                uint i = (y * Row) + x;
                keys.Add(Key(i, i + 1));
                keys.Add(Key(i, i + Row));
                keys.Add(Key(i + 1, i + Row));
            }
        }

        int mixed = keys.Select(EdgeKeyComparer.Instance.GetHashCode).Distinct().Count();
        int plain = keys.Select(k => k.GetHashCode()).Distinct().Count();
        Assert.True(mixed >= keys.Count * 99L / 100, $"{mixed} distinct hashes for {keys.Count} edges");
        Assert.True(plain * 50L < keys.Count, $"the default hash spread {plain} values; the regression this guards against is gone");
    }

    private static ulong Key(uint a, uint b) => ((ulong)a << 32) | b;
}
