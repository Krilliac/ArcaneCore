using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.Atlas.Tests.Web;

/// <summary>
/// Writes the "golden" directory the JavaScript tests read: ATG1 chunks produced by <see cref="GeometryWire.Write"/>, and
/// JSON holding what <see cref="CameraMath"/>, <see cref="WorldGrid"/>, <see cref="LayerCatalog"/> and
/// <see cref="ViewState"/> answer, so the browser code is held to the C# core's numbers (within 1e-9) instead of to a
/// restatement of them. Everything is synthetic and deterministic (no random numbers, no client data).
/// </summary>
internal static partial class WebGolden
{
    /// <summary>The environment variable the JS tests read for the golden directory.</summary>
    public const string Variable = "ARCANECORE_ATLAS_GOLDEN";

    /// <summary>Names of the corrupt-chunk variants <see cref="Export"/> writes (each must be refused by both decoders).</summary>
    public static readonly string[] CorruptNames =
    [
        "truncated", "truncated-header", "magic", "version", "json-length", "index-past-vertices", "nan-position",
        "infinite-position", "trailer-not-object", "trailer-no-tile", "trailer-not-json",
    ];

    /// <summary>Writes every golden file into <paramref name="directory"/> (created if missing) and returns the parsed manifest.</summary>
    public static JsonObject Export(string directory)
    {
        Directory.CreateDirectory(directory);
        var chunks = new JsonArray();
        var parts = new List<byte[]>();
        foreach ((string name, GeometryChunk chunk, bool full, bool byteEqual) in Chunks())
        {
            byte[] bytes = GeometryWire.Write(chunk);
            File.WriteAllBytes(Path.Combine(directory, name + ".bin"), bytes);
            chunks.Add(new JsonObject
            {
                ["name"] = name,
                ["file"] = name + ".bin",
                ["byteEqual"] = byteEqual,
                ["expect"] = full ? Expect(chunk, bytes) : Summary(chunk, bytes),
            });
            if (name != "large")
            {
                parts.Add(bytes);
            }
        }

        var envelope = new List<byte>();
        envelope.AddRange(BitConverter.GetBytes((uint)parts.Count));
        foreach (byte[] part in parts)
        {
            envelope.AddRange(BitConverter.GetBytes((uint)part.Length));
            envelope.AddRange(part);
        }

        File.WriteAllBytes(Path.Combine(directory, "envelope.bin"), [.. envelope]);
        var envelopeNames = new JsonArray();
        foreach (JsonNode? chunk in chunks)
        {
            if (chunk!["name"]!.GetValue<string>() != "large")
            {
                envelopeNames.Add(chunk["name"]!.GetValue<string>());
            }
        }

        var corrupt = new JsonArray();
        byte[] terrain = File.ReadAllBytes(Path.Combine(directory, "terrain.bin"));
        foreach ((string name, byte[] bytes) in CorruptVariants(terrain, Terrain()))
        {
            File.WriteAllBytes(Path.Combine(directory, "corrupt-" + name + ".bin"), bytes);
            corrupt.Add(new JsonObject { ["name"] = name, ["file"] = "corrupt-" + name + ".bin" });
        }

        JsonArray v2Chunks = ExportV2(directory);
        JsonObject textures = ExportTextures(directory);

        var root = new JsonObject
        {
            ["v2"] = v2Chunks,
            ["textures"] = textures,
            ["chunks"] = chunks,
            ["envelope"] = new JsonObject { ["file"] = "envelope.bin", ["parts"] = envelopeNames },
            ["corrupt"] = corrupt,
            ["cameras"] = Cameras(),
            ["tiles"] = Tiles(),
            ["catalog"] = Catalog(),
            ["view"] = View(),
            ["mergePatch"] = MergePatchVectors(),
        };
        File.WriteAllText(Path.Combine(directory, "golden.json"), root.ToJsonString(), new UTF8Encoding(false));
        return root;
    }

    /// <summary>The corrupt variants of an ATG1 chunk, by name. Both <c>GeometryWire.Read</c> and the JS decoder must refuse each.</summary>
    public static IReadOnlyList<(string Name, byte[] Bytes)> CorruptVariants(byte[] good, GeometryChunk source)
    {
        int jsonLength = JsonSerializer.SerializeToUtf8Bytes(GeometryWire.Metadata(source)).Length;
        int trailerStart = good.Length - jsonLength - 4;
        var list = new List<(string, byte[])>
        {
            ("truncated", good[..^5]),
            ("truncated-header", good[..40]),
        };

        byte[] magic = (byte[])good.Clone();
        magic[0] = (byte)'X';
        list.Add(("magic", magic));

        byte[] version = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(version.AsSpan(4), 3);
        list.Add(("version", version));

        byte[] length = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(length.AsSpan(trailerStart), (uint)jsonLength + 1);
        list.Add(("json-length", length));

        byte[] index = (byte[])good.Clone();
        int trianglesAt = GeometryWire.HeaderBytes + (source.Positions.Length * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(trianglesAt), (uint)source.VertexCount);
        list.Add(("index-past-vertices", index));

        byte[] nan = (byte[])good.Clone();
        BinaryPrimitives.WriteSingleLittleEndian(nan.AsSpan(GeometryWire.HeaderBytes), float.NaN);
        list.Add(("nan-position", nan));

        byte[] infinite = (byte[])good.Clone();
        BinaryPrimitives.WriteSingleLittleEndian(infinite.AsSpan(GeometryWire.HeaderBytes), float.PositiveInfinity);
        list.Add(("infinite-position", infinite));

        list.Add(("trailer-not-object", WithTrailer(good, trailerStart, "[1,2]")));
        list.Add(("trailer-no-tile", WithTrailer(good, trailerStart, "{\"layer\":\"x\",\"part\":0,\"attributes\":{},\"stats\":{}}")));
        list.Add(("trailer-not-json", WithTrailer(good, trailerStart, "{{{")));
        return list;
    }

    private static byte[] WithTrailer(byte[] good, int trailerStart, string json)
    {
        byte[] text = Encoding.UTF8.GetBytes(json);
        byte[] result = new byte[trailerStart + 4 + text.Length];
        good.AsSpan(0, trailerStart).CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(trailerStart), (uint)text.Length);
        text.CopyTo(result.AsSpan(trailerStart + 4));
        return result;
    }

    private static IEnumerable<(string Name, GeometryChunk Chunk, bool Full, bool ByteEqual)> Chunks()
    {
        for (int k = 0; k < 4; k++)
        {
            yield return ($"pad{k}", Pad(k), true, true);
        }

        yield return ("terrain", Terrain(), true, true);
        yield return ("mixed", Mixed(), true, false);
        yield return ("empty", Empty(), true, true);
        yield return ("large", Large(), false, true);
    }

    // Three vertices and a one-character-longer JSON trailer per k, so the four chunks have four different sizes modulo 4
    // and the ones after the first start on every alignment inside a tile response.
    private static GeometryChunk Pad(int k)
    {
        var tile = new TileKey(0, 1 + k, 2);
        WorldPoint origin = WorldGrid.Origin(tile);
        var builder = new GeometryBuilder(LayerIds.Tiles, tile, origin);
        uint a = builder.AddVertex(origin.X - 1, origin.Y - 1, 10);
        uint b = builder.AddVertex(origin.X - 2, origin.Y - 1, 11);
        uint c = builder.AddVertex(origin.X - 1, origin.Y - 2, 12);
        builder.AddTriangle(a, b, c, 1);
        builder.Stats["pad"] = new string('p', k);
        return builder.Build();
    }

    private static GeometryChunk Terrain()
    {
        var tile = new TileKey(0, 49, 31);
        WorldPoint origin = WorldGrid.Origin(tile);
        var builder = new GeometryBuilder(LayerIds.Terrain, tile, origin);
        const int n = 16;
        double step = WorldGrid.TileSize / n;
        uint[,] vertex = new uint[n + 1, n + 1];
        for (int j = 0; j <= n; j++)
        {
            for (int i = 0; i <= n; i++)
            {
                double z = 40 + (12 * Math.Sin(i * 0.7) * Math.Cos(j * 0.5)) + (0.03125 * i);
                vertex[i, j] = builder.AddVertex(origin.X - (i * step), origin.Y - (j * step), z);
            }
        }

        string[] keys = [ColorKeys.Ground, ColorKeys.Steep, ColorKeys.Water, ColorKeys.Magma, ColorKeys.Slime];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                uint attribute = (uint)((i + j) % keys.Length);
                builder.DescribeAttribute(attribute, () => new JsonObject { ["colorKey"] = keys[attribute], ["index"] = (int)attribute });
                builder.AddTriangle(vertex[i, j], vertex[i + 1, j], vertex[i + 1, j + 1], attribute);
                builder.AddTriangle(vertex[i, j], vertex[i + 1, j + 1], vertex[i, j + 1], attribute);
            }
        }

        builder.Stats["squares"] = n * n;
        builder.Stats["source"] = "synthetic";
        return builder.Build();
    }

    // Triangles, explicit lines and points; a large attribute id; JSON the writer escapes (quotes, a backslash, non-ASCII).
    private static GeometryChunk Mixed()
    {
        var tile = new TileKey(1, 10, 20);
        WorldPoint origin = WorldGrid.Origin(tile);
        var builder = new GeometryBuilder(LayerIds.VMapWmo, tile, origin, part: 3);
        var corners = new List<uint>();
        foreach (double z in new[] { 100.0, 112.5 })
        {
            foreach (double y in new[] { -40.0, -10.0 })
            {
                foreach (double x in new[] { -30.0, -5.0 })
                {
                    corners.Add(builder.AddVertex(origin.X + x, origin.Y + y, z));
                }
            }
        }

        int[][] quads = [[0, 1, 3, 2], [4, 6, 7, 5], [0, 4, 5, 1], [2, 3, 7, 6], [0, 2, 6, 4], [1, 5, 7, 3]];
        foreach (int[] q in quads)
        {
            builder.AddTriangle(corners[q[0]], corners[q[1]], corners[q[2]], 100_000, edges: false);
            builder.AddTriangle(corners[q[0]], corners[q[2]], corners[q[3]], 7, edges: false);
        }

        builder.AddLine(corners[0], corners[1], 7);
        builder.AddLine(corners[1], corners[3], 100_000);
        builder.AddLine(corners[0], corners[7], 0);
        builder.AddPoint(corners[7], 9);
        builder.AddPoint(corners[0], 100_000);
        builder.DescribeAttribute(100_000, () => new JsonObject { ["colorKey"] = ColorKeys.Indoor, ["group"] = "Stormwind – Cathédral ✓" });
        builder.DescribeAttribute(7, () => new JsonObject { ["colorKey"] = ColorKeys.Outdoor, ["flags"] = new JsonArray(1, 2, 3) });
        builder.Stats["note"] = "quote \" backslash \\ tab \t umlaut ü";
        builder.Stats["nested"] = new JsonObject { ["count"] = 3, ["ok"] = true, ["none"] = null };
        return builder.Build();
    }

    private static GeometryChunk Empty()
    {
        var tile = new TileKey(0, 5, 6);
        return new GeometryBuilder(LayerIds.Tiles, tile, WorldGrid.Origin(tile)).Build();
    }

    private static GeometryChunk Large()
    {
        var tile = new TileKey(0, 32, 32);
        WorldPoint origin = WorldGrid.Origin(tile);
        var builder = new GeometryBuilder(LayerIds.RefTerrain, tile, origin, part: 1);
        const int n = 96;
        double step = WorldGrid.TileSize / n;
        uint[,] vertex = new uint[n + 1, n + 1];
        for (int j = 0; j <= n; j++)
        {
            for (int i = 0; i <= n; i++)
            {
                double z = 30 + (25 * Math.Sin(i * 0.11) * Math.Cos(j * 0.07));
                vertex[i, j] = builder.AddVertex(origin.X - (i * step), origin.Y - (j * step), z);
            }
        }

        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                uint attribute = (uint)((i / 8) + ((j / 8) * 12));
                builder.AddTriangle(vertex[i, j], vertex[i + 1, j], vertex[i + 1, j + 1], attribute);
                builder.AddTriangle(vertex[i, j], vertex[i + 1, j + 1], vertex[i, j + 1], attribute);
            }
        }

        return builder.Build();
    }

    private static JsonArray Doubles(IEnumerable<float> values)
    {
        var array = new JsonArray();
        foreach (float v in values)
        {
            array.Add((double)v);
        }

        return array;
    }

    private static JsonArray Uints(IEnumerable<uint> values)
    {
        var array = new JsonArray();
        foreach (uint v in values)
        {
            array.Add(v);
        }

        return array;
    }

    private static JsonArray Doubles(IEnumerable<double> values)
    {
        var array = new JsonArray();
        foreach (double v in values)
        {
            array.Add(v);
        }

        return array;
    }

    private static JsonArray Triple(WorldPoint p) => new(p.X, p.Y, p.Z);

    private static JsonNode? Bounds(GeometryChunk chunk)
    {
        if (chunk.VertexCount == 0)
        {
            return null;
        }

        double[] min = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
        double[] max = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
        for (uint i = 0; i < chunk.VertexCount; i++)
        {
            WorldPoint p = chunk.Vertex(i);
            double[] v = [p.X, p.Y, p.Z];
            for (int a = 0; a < 3; a++)
            {
                min[a] = Math.Min(min[a], v[a]);
                max[a] = Math.Max(max[a], v[a]);
            }
        }

        return new JsonObject { ["min"] = Doubles(min), ["max"] = Doubles(max) };
    }

    private static JsonObject Header(GeometryChunk chunk, byte[] bytes)
    {
        var attributes = new JsonObject();
        foreach ((uint id, JsonObject value) in chunk.Attributes.OrderBy(p => p.Key))
        {
            attributes[id.ToString(CultureInfo.InvariantCulture)] = value.DeepClone();
        }

        return new JsonObject
        {
            ["layer"] = chunk.LayerId,
            ["tile"] = new JsonArray(chunk.Tile.MapId, chunk.Tile.X, chunk.Tile.Y),
            ["part"] = chunk.Part,
            ["flags"] = (chunk.TriangleCount > 0 ? 1 : 0) | (chunk.LineCount > 0 ? 2 : 0) | (chunk.PointCount > 0 ? 4 : 0),
            ["origin"] = Triple(chunk.Origin),
            ["vertexCount"] = chunk.VertexCount,
            ["triangleCount"] = chunk.TriangleCount,
            ["lineCount"] = chunk.LineCount,
            ["pointCount"] = chunk.PointCount,
            ["attributes"] = attributes,
            ["stats"] = chunk.Stats.DeepClone(),
            ["byteLength"] = bytes.Length,
            ["bounds"] = Bounds(chunk),
        };
    }

    private static JsonObject Expect(GeometryChunk chunk, byte[] bytes)
    {
        JsonObject expect = Header(chunk, bytes);
        expect["positions"] = Doubles(chunk.Positions);
        expect["triangles"] = Uints(chunk.Triangles);
        expect["triangleAttributes"] = Uints(chunk.TriangleAttributes);
        expect["lines"] = Uints(chunk.Lines);
        expect["lineAttributes"] = Uints(chunk.LineAttributes);
        expect["points"] = Uints(chunk.Points);
        expect["pointAttributes"] = Uints(chunk.PointAttributes);
        return expect;
    }

    // Large chunks are described by sums taken in array order (so the JS sum is bit-identical) plus their ends.
    private static JsonObject Summary(GeometryChunk chunk, byte[] bytes)
    {
        JsonObject expect = Header(chunk, bytes);
        double positions = 0;
        foreach (float p in chunk.Positions)
        {
            positions += p;
        }

        static double Sum(uint[] values)
        {
            double total = 0;
            foreach (uint v in values)
            {
                total += v;
            }

            return total;
        }

        expect["sums"] = new JsonObject
        {
            ["positions"] = positions,
            ["triangles"] = Sum(chunk.Triangles),
            ["triangleAttributes"] = Sum(chunk.TriangleAttributes),
            ["lines"] = Sum(chunk.Lines),
            ["lineAttributes"] = Sum(chunk.LineAttributes),
        };
        expect["firstPositions"] = Doubles(chunk.Positions.Take(9));
        expect["lastPositions"] = Doubles(chunk.Positions.TakeLast(9));
        expect["firstTriangles"] = Uints(chunk.Triangles.Take(9));
        expect["lastTriangles"] = Uints(chunk.Triangles.TakeLast(9));
        return expect;
    }

    private static JsonObject CameraJson(CameraState camera) => new()
    {
        ["target"] = Triple(camera.Target),
        ["distance"] = camera.Distance,
        ["yaw"] = camera.Yaw,
        ["pitch"] = camera.Pitch,
        ["fov"] = camera.Fov,
        ["mode"] = ViewState.Name(camera.Mode),
    };

    private static IEnumerable<CameraState> CameraSet()
    {
        var inn = new WorldPoint(-9462.66, 16.1915, 57.0459);
        var far = new WorldPoint(10311.3, 831.463, 1326.41);
        foreach ((double yaw, double pitch) in new[] { (0.0, 0.0), (45.0, 45.0), (90.0, 30.0), (180.0, -20.0), (270.0, 60.0), (359.9, 89.0), (135.0, -89.0), (45.0, 90.0) })
        {
            yield return new CameraState(inn, 140, yaw, pitch, 55, CameraMode.Orbit);
        }

        yield return new CameraState(far, 5, 12.5, 33, 15, CameraMode.Orbit);
        yield return new CameraState(inn, 40_000, 200, 10, 110, CameraMode.Orbit);
        yield return new CameraState(inn, 0.5, 45, 45, 55, CameraMode.Orbit);
        yield return new CameraState(inn, 2500, 77, 25, 40, CameraMode.Orbit);
        foreach ((double yaw, double pitch, double fov) in new[] { (0.0, 0.0, 55.0), (120.0, 20.0, 70.0), (300.0, -60.0, 30.0), (10.0, 89.0, 90.0), (180.0, -89.0, 45.0) })
        {
            yield return new CameraState(far, 140, yaw, pitch, fov, CameraMode.Fly);
        }

        foreach ((double yaw, double distance) in new[] { (0.0, 140.0), (33.0, 140.0), (180.0, 140.0), (270.0, 140.0), (90.0, 0.5), (45.0, 5000.0) })
        {
            yield return new CameraState(inn, distance, yaw, 45, 55, CameraMode.Top);
        }
    }

    private static JsonArray Cameras()
    {
        var list = new JsonArray();
        foreach (CameraState camera in CameraSet())
        {
            foreach (double aspect in new[] { 16.0 / 9.0, 0.625 })
            {
                list.Add(CameraEntry(camera, aspect));
            }
        }

        return list;
    }

    private static JsonObject CameraEntry(CameraState camera, double aspect)
    {
        WorldPoint eye = CameraMath.Eye(camera);
        WorldPoint dir = CameraMath.Direction(camera);
        (double near, double far) = CameraMath.Planes(camera);
        var entry = new JsonObject
        {
            ["camera"] = CameraJson(camera),
            ["aspect"] = aspect,
            ["eye"] = Triple(eye),
            ["direction"] = Triple(dir),
            ["up"] = Triple(CameraMath.Up(camera)),
            ["planes"] = new JsonObject { ["near"] = near, ["far"] = far },
            ["view"] = Doubles(CameraMath.View(camera)),
            ["projection"] = Doubles(CameraMath.Projection(camera, aspect)),
        };

        WorldPoint[] points =
        [
            camera.Target,
            camera.Target + new WorldPoint(10, 5, -2),
            camera.Target + new WorldPoint(-30, 40, 15),
            eye + new WorldPoint(dir.X * 10, dir.Y * 10, dir.Z * 10),
            eye - new WorldPoint(dir.X * 10, dir.Y * 10, dir.Z * 10),
            camera.Target + new WorldPoint(2000, -3000, 500),
        ];
        var projected = new JsonArray();
        foreach (WorldPoint p in points)
        {
            (double X, double Y, double Depth)? result = CameraMath.Project(camera, p, aspect);
            projected.Add(new JsonObject
            {
                ["world"] = Triple(p),
                ["result"] = result is { } r ? new JsonObject { ["x"] = r.X, ["y"] = r.Y, ["depth"] = r.Depth } : null,
            });
        }

        entry["project"] = projected;

        var rays = new JsonArray();
        foreach ((double nx, double ny) in new[] { (-1.0, -1.0), (0.0, 0.0), (0.37, -0.62), (1.0, 1.0) })
        {
            foreach (double? maxDistance in new double?[] { null, 3500 })
            {
                PickRay ray = maxDistance is { } m
                    ? CameraMath.RayThrough(camera, 7, nx, ny, aspect, m)
                    : CameraMath.RayThrough(camera, 7, nx, ny, aspect);
                rays.Add(new JsonObject
                {
                    ["ndc"] = new JsonArray(nx, ny),
                    ["mapId"] = 7,
                    ["maxDistance"] = maxDistance,
                    ["origin"] = Triple(ray.Origin),
                    ["direction"] = Triple(ray.Direction),
                    ["limit"] = ray.MaxDistance,
                });
            }
        }

        entry["rays"] = rays;
        return entry;
    }

    private static JsonObject Tiles()
    {
        var points = new JsonArray();
        void AddPoint(double x, double y)
        {
            // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:244-248 (area cell) and :411-412 (height square), in float.
            float fx = (float)x;
            float fy = (float)y;
            int cellX = (int)(16 * (32 - (fx / GridDefines.SizeOfGrids))) & 15;
            int cellY = (int)(16 * (32 - (fy / GridDefines.SizeOfGrids))) & 15;
            int squareX = (int)(GridDefines.MapResolution * (32 - (fx / GridDefines.SizeOfGrids))) & (GridDefines.MapResolution - 1);
            int squareY = (int)(GridDefines.MapResolution * (32 - (fy / GridDefines.SizeOfGrids))) & (GridDefines.MapResolution - 1);
            TileKey? tile = WorldGrid.TileOf(0, x, y);
            points.Add(new JsonObject
            {
                ["x"] = x,
                ["y"] = y,
                ["tile"] = tile is { } t ? new JsonArray(t.X, t.Y) : null,
                ["cell"] = new JsonArray(cellX, cellY),
                ["square"] = new JsonArray(squareX, squareY),
            });
        }

        for (int i = 0; i < 3000; i++)
        {
            double x = (((i * 7919) % 36001) - 18000) + (((i * 37) % 100) / 100.0);
            double y = (((i * 104729) % 36007) - 18003) + (((i * 53) % 100) / 100.0);
            AddPoint(x, y);
        }

        // Tile borders: the exact float edge of every tile row and its two neighbouring floats, against a mid-tile other axis.
        for (int k = -2; k <= 66; k++)
        {
            float edge = (32 - k) * GridDefines.SizeOfGrids;
            foreach (float e in new[] { MathF.BitDecrement(edge), edge, MathF.BitIncrement(edge) })
            {
                AddPoint(e, -9462.66);
                AddPoint(16.1915, e);
            }
        }

        var tiles = new JsonArray();
        foreach ((int x, int y) in new[] { (0, 0), (63, 63), (32, 32), (49, 31), (10, 20), (0, 63), (63, 0), (7, 9) })
        {
            var key = new TileKey(0, x, y);
            WorldPoint origin = WorldGrid.Origin(key);
            (double minX, double maxX, double minY, double maxY) = WorldGrid.Bounds(key);
            (double cx, double cy) = WorldGrid.Centre(key);
            var files = new JsonArray();
            foreach (uint map in new uint[] { 0, 1, 30, 489 })
            {
                var mapped = new TileKey(map, x, y);
                files.Add(new JsonObject
                {
                    ["map"] = map,
                    ["terrain"] = WorldGrid.MapFileName(mapped),
                    ["nav"] = WorldGrid.NavTileFileName(mapped),
                    ["vmap"] = WorldGrid.VMapTileFileName(mapped),
                });
            }

            tiles.Add(new JsonObject
            {
                ["x"] = x,
                ["y"] = y,
                ["origin"] = Triple(origin),
                ["bounds"] = new JsonObject { ["minX"] = minX, ["maxX"] = maxX, ["minY"] = minY, ["maxY"] = maxY },
                ["centre"] = new JsonArray(cx, cy),
                ["files"] = files,
            });
        }

        var around = new JsonArray();
        foreach ((double x, double y, int radius) in new[] { (-9462.66, 16.1915, 0), (-9462.66, 16.1915, 1), (-9462.66, 16.1915, 2), (17000.5, 17000.5, 2), (-17000.0, -17000.0, 1), (100.0, 100.0, 4), (20000.0, 0.0, 1) })
        {
            var list = new JsonArray();
            foreach (TileKey t in WorldGrid.Around(0, x, y, radius))
            {
                list.Add(new JsonArray(t.X, t.Y));
            }

            around.Add(new JsonObject { ["x"] = x, ["y"] = y, ["radius"] = radius, ["tiles"] = list });
        }

        var grids = new JsonArray();
        foreach ((int gx, int gy) in new[] { (0, 0), (63, 63), (31, 32), (14, 17), (62, 1) })
        {
            (int tx, int ty) = TerrainTile.TileOf(new GridCoord(gx, gy));
            grids.Add(new JsonObject { ["gx"] = gx, ["gy"] = gy, ["tile"] = new JsonArray(tx, ty) });
        }

        return new JsonObject
        {
            ["constants"] = new JsonObject
            {
                ["tilesPerSide"] = WorldGrid.TilesPerSide,
                ["tileSize"] = WorldGrid.TileSize,
                ["mapResolution"] = WorldGrid.MapResolution,
                ["squareSize"] = WorldGrid.SquareSize,
            },
            ["points"] = points,
            ["tiles"] = tiles,
            ["around"] = around,
            ["grids"] = grids,
        };
    }

    private static JsonArray Catalog()
    {
        var list = new JsonArray();
        foreach (LayerInfo info in LayerCatalog.All)
        {
            var colors = new JsonObject();
            foreach ((string key, string color) in info.AttributeColors ?? new Dictionary<string, string>())
            {
                colors[key] = color;
            }

            list.Add(new JsonObject
            {
                ["id"] = info.Id,
                ["group"] = info.Group.ToString(),
                ["view"] = info.View.ToString(),
                ["title"] = info.Title,
                ["defaultColor"] = info.DefaultColor,
                ["defaultVisible"] = info.DefaultVisible,
                ["attributeColors"] = colors,
                ["tiled"] = info.Tiled,
            });
        }

        return list;
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject View()
    {
        string[] patches =
        [
            "{\"camera\":{\"target\":[1,2,3],\"yaw\":400,\"pitch\":-20}}",
            "{\"layers\":{\"terrain\":{\"visible\":false,\"color\":\"#abcdef\",\"mode\":\"fill\",\"opacity\":0.5}}}",
            "{\"compare\":\"split\",\"split\":0.25,\"loadRadius\":3,\"theme\":\"vellum\",\"hud\":false}",
            "{\"follow\":\"1A2B\"}",
            "{\"camera\":{\"mode\":\"top\",\"distance\":2000}}",
            "{\"map\":1,\"camera\":{\"fov\":110,\"yaw\":-90}}",
            "{\"layers\":{\"nav.poly\":{\"visible\":false},\"ref.terrain\":{\"visible\":true,\"mode\":\"xray\"}}}",
        ];
        var results = new JsonArray();
        foreach (string text in patches)
        {
            results.Add(new JsonObject { ["patch"] = Parse(text), ["result"] = ViewState.Default.Apply(Parse(text)).ToJson() });
        }

        var sequences = new JsonArray();
        string[][] chains =
        [
            [patches[0], patches[1], patches[2]],
            [patches[3], "{\"follow\":null}", patches[4]],
            [patches[6], patches[1], patches[5], patches[0]],
        ];
        foreach (string[] chain in chains)
        {
            ViewState state = ViewState.Default;
            var steps = new JsonArray();
            foreach (string text in chain)
            {
                state = state.Apply(Parse(text));
                steps.Add(Parse(text));
            }

            sequences.Add(new JsonObject { ["patches"] = steps, ["result"] = state.ToJson() });
        }

        return new JsonObject
        {
            ["default"] = ViewState.Default.ToJson(),
            ["patches"] = results,
            ["sequences"] = sequences,
            ["limits"] = new JsonObject
            {
                ["minDistance"] = CameraState.MinDistance,
                ["maxDistance"] = CameraState.MaxDistance,
                ["minFov"] = CameraState.MinFov,
                ["maxFov"] = CameraState.MaxFov,
                ["maxPitch"] = CameraState.MaxPitch,
                ["maxLoadRadius"] = ViewState.MaxLoadRadiusTiles,
                ["topEyeHeight"] = CameraMath.TopEyeHeight,
            },
        };
    }

    // RFC 7396 appendix A, with the results computed by ViewState.MergePatch.
    private static JsonArray MergePatchVectors()
    {
        (string Target, string Patch)[] cases =
        [
            ("{\"a\":\"b\"}", "{\"a\":\"c\"}"),
            ("{\"a\":\"b\"}", "{\"b\":\"c\"}"),
            ("{\"a\":\"b\"}", "{\"a\":null}"),
            ("{\"a\":\"b\",\"b\":\"c\"}", "{\"a\":null}"),
            ("{\"a\":[\"b\"]}", "{\"a\":\"c\"}"),
            ("{\"a\":\"c\"}", "{\"a\":[\"b\"]}"),
            ("{\"a\":{\"b\":\"c\"}}", "{\"a\":{\"b\":\"d\",\"c\":null}}"),
            ("{\"a\":[{\"b\":\"c\"}]}", "{\"a\":[1]}"),
            ("[\"a\",\"b\"]", "[\"c\",\"d\"]"),
            ("{\"a\":\"b\"}", "[\"c\"]"),
            ("{\"a\":\"foo\"}", "null"),
            ("{\"a\":\"foo\"}", "\"bar\""),
            ("{\"e\":null}", "{\"a\":1}"),
            ("[1,2]", "{\"a\":\"b\",\"c\":null}"),
            ("{}", "{\"a\":{\"bb\":{\"ccc\":null}}}"),
        ];
        var list = new JsonArray();
        foreach ((string target, string patch) in cases)
        {
            JsonNode? result = ViewState.MergePatch(JsonNode.Parse(target), JsonNode.Parse(patch));
            list.Add(new JsonObject
            {
                ["target"] = JsonNode.Parse(target),
                ["patch"] = JsonNode.Parse(patch),
                ["result"] = result,
            });
        }

        return list;
    }
}
