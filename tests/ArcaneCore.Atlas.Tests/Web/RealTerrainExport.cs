using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Game.Maps.Terrain;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Web;

/// <summary>
/// What <see cref="RealTerrainExport.Write"/> put in the real-data temp directory: one real server terrain tile sampled
/// through the server's own height query into an ATG1 chunk, plus the probe positions and file counts the JS test checks.
/// Everything it writes is derived from client data, so it goes only to a temp directory the fact deletes.
/// </summary>
/// <param name="FileName">The .map file sampled.</param>
/// <param name="Samples">Vertices in the chunk (one per sample with a valid height).</param>
/// <param name="Triangles">Triangles in the chunk.</param>
/// <param name="MinZ">Lowest sampled height.</param>
/// <param name="MaxZ">Highest sampled height.</param>
/// <param name="MapFiles">Number of <c>*.map</c> files under maps/.</param>
/// <param name="VMapTiles">Number of <c>*.vmtile</c> files under vmaps/.</param>
/// <param name="NavTiles">Number of <c>*.mmtile</c> files under mmaps/.</param>
internal sealed record RealTerrainExport(string FileName, int Samples, int Triangles, double MinZ, double MaxZ, int MapFiles, int VMapTiles, int NavTiles)
{
    /// <summary>Samples per tile side (a 64 x 64 lattice at half-square insets, so no sample sits on a tile or square edge).</summary>
    private const int Lattice = 64;

    private const uint EasternKingdoms = 0;

    /// <summary>Farley, innkeeper inside the Lion's Pride Inn, Goldshire (guid 80346).</summary>
    // ref: tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:57
    private static readonly (double X, double Y) GoldshireInn = (-9462.66, 16.1915);

    /// <summary>Samples the Goldshire tile of <paramref name="root"/> into <paramref name="directory"/> and returns what was written.</summary>
    public static RealTerrainExport Write(string root, string directory)
    {
        TileKey tile = WorldGrid.TileOf(EasternKingdoms, GoldshireInn.X, GoldshireInn.Y)
            ?? throw new InvalidOperationException("the Goldshire inn position is outside the tile grid");
        string fileName = WorldGrid.MapFileName(tile);
        string path = Path.Combine(root, "maps", fileName);
        Assert.True(File.Exists(path), $"{path} is missing; a 1.12.1 extraction holds the Goldshire tile");
        TerrainTile terrain = TerrainTile.Parse(File.ReadAllBytes(path));
        Assert.True(terrain.HasData, $"{fileName} parsed as an empty tile");

        WorldPoint origin = WorldGrid.Origin(tile);
        double step = WorldGrid.TileSize / Lattice;
        var builder = new GeometryBuilder(LayerIds.Terrain, tile, origin);
        var index = new uint?[Lattice, Lattice];
        var serverZ = new JsonArray();
        double minZ = double.MaxValue;
        double maxZ = double.MinValue;
        for (int row = 0; row < Lattice; row++)
        {
            for (int col = 0; col < Lattice; col++)
            {
                double x = origin.X - ((row + 0.5) * step);
                double y = origin.Y - ((col + 0.5) * step);
                float z = terrain.GetHeight((float)x, (float)y);
                if (z <= TerrainTile.InvalidHeight)
                {
                    continue;
                }

                index[row, col] = builder.AddVertex(x, y, z);
                builder.AddPoint(index[row, col]!.Value);
                serverZ.Add((double)z);
                minZ = Math.Min(minZ, z);
                maxZ = Math.Max(maxZ, z);
            }
        }

        for (int row = 0; row + 1 < Lattice; row++)
        {
            for (int col = 0; col + 1 < Lattice; col++)
            {
                if (index[row, col] is { } a && index[row, col + 1] is { } b && index[row + 1, col] is { } c && index[row + 1, col + 1] is { } d)
                {
                    builder.AddTriangle(a, b, d);
                    builder.AddTriangle(a, d, c);
                }
            }
        }

        builder.Stats["source"] = fileName;
        GeometryChunk chunk = builder.Build();
        Assert.True(chunk.VertexCount > Lattice * Lattice / 2, $"only {chunk.VertexCount} of {Lattice * Lattice} samples had a height");
        File.WriteAllBytes(Path.Combine(directory, "chunk.bin"), GeometryWire.Write(chunk));

        var vertices = new JsonArray();
        for (uint i = 0; i < chunk.VertexCount; i++)
        {
            WorldPoint p = chunk.Vertex(i);
            vertices.Add(new JsonArray(p.X, p.Y, p.Z));
        }

        string[] mapFiles = Directory.GetFiles(Path.Combine(root, "maps"), "*.map");
        string[] vmapTiles = Directory.Exists(Path.Combine(root, "vmaps")) ? Directory.GetFiles(Path.Combine(root, "vmaps"), "*.vmtile") : [];
        string[] navTiles = Directory.Exists(Path.Combine(root, "mmaps")) ? Directory.GetFiles(Path.Combine(root, "mmaps"), "*.mmtile") : [];

        var probes = new JsonArray { Probe(root, "Goldshire inn", EasternKingdoms, GoldshireInn.X, GoldshireInn.Y) };
        string[] ordered = [.. mapFiles.Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];
        foreach (string name in new[] { ordered[0], ordered[ordered.Length / 2], ordered[^1] })
        {
            // {map:D3}{x:D2}{y:D2}.map  ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:215
            var key = new TileKey(uint.Parse(name[..3], System.Globalization.CultureInfo.InvariantCulture), int.Parse(name[3..5], System.Globalization.CultureInfo.InvariantCulture), int.Parse(name[5..7], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(name, WorldGrid.MapFileName(key));
            (double cx, double cy) = WorldGrid.Centre(key);
            probes.Add(Probe(root, $"centre of {name}", key.MapId, cx, cy));
        }

        var json = new JsonObject
        {
            ["file"] = fileName,
            ["tile"] = new JsonArray(tile.MapId, tile.X, tile.Y),
            ["origin"] = new JsonArray(origin.X, origin.Y, origin.Z),
            ["vertices"] = vertices,
            ["serverZ"] = serverZ,
            ["triangleCount"] = chunk.TriangleCount,
            ["pointCount"] = chunk.PointCount,
            ["probes"] = probes,
            ["counts"] = new JsonObject
            {
                ["map"] = mapFiles.Length,
                ["vmtile"] = vmapTiles.Length,
                ["mmtile"] = navTiles.Length,

                // go{displayId:D4}.mmtile  ref: vmangos@0e3ff01 contrib/mmap/src/MapBuilder.cpp:791
                ["transport"] = navTiles.Count(f => Path.GetFileName(f).StartsWith("go", StringComparison.Ordinal)),
            },
        };
        File.WriteAllText(Path.Combine(directory, "real.json"), json.ToJsonString(), new UTF8Encoding(false));
        return new RealTerrainExport(fileName, chunk.VertexCount, chunk.TriangleCount, minZ, maxZ, mapFiles.Length, vmapTiles.Length, navTiles.Length);
    }

    /// <summary>A world position with the tile and the three file names the server derives for it, and whether each file exists.</summary>
    private static JsonObject Probe(string root, string name, uint mapId, double x, double y)
    {
        TileKey tile = WorldGrid.TileOf(mapId, x, y) ?? throw new InvalidOperationException($"{name} is outside the tile grid");
        string map = WorldGrid.MapFileName(tile);
        string vmap = WorldGrid.VMapTileFileName(tile);
        string mmap = WorldGrid.NavTileFileName(tile);
        return new JsonObject
        {
            ["name"] = name,
            ["map"] = mapId,
            ["at"] = new JsonArray(x, y),
            ["tile"] = new JsonArray(tile.X, tile.Y),
            ["files"] = new JsonObject { ["map"] = map, ["vmap"] = vmap, ["mmap"] = mmap },
            ["exists"] = new JsonObject
            {
                ["map"] = File.Exists(Path.Combine(root, "maps", map)),
                ["vmap"] = File.Exists(Path.Combine(root, "vmaps", vmap)),
                ["mmap"] = File.Exists(Path.Combine(root, "mmaps", mmap)),
            },
        };
    }
}
