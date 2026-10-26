using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;

namespace ArcaneCore.Atlas.Server;

/// <summary>
/// The navmesh layers of the server view, built from the <c>.mmtile</c> files with the server's own reader
/// (<see cref="NavMeshTile.ParseFile"/>): <c>nav.poly</c>, <c>nav.detail</c>, <c>nav.portals</c> and <c>nav.bv</c>.
/// <para>
/// Format facts, each with two sources. A tile file is a 20-byte generator header (magic, Detour version, mmap version, data size, uses-liquids)
/// and a Detour tile: a 100-byte <c>dtMeshHeader</c> (polyCount at tile offset 24, bvNodeCount at 48, offMeshConCount at 52, so at file offsets 44, 68
/// and 72), vertices (Recast space, y up), polygons of up to six vertices with one neighbour entry per edge, links, detail meshes, detail
/// vertices, detail triangles and BV nodes (recastnavigation Detour/Include/DetourNavMesh.h:261-289 and 80-120;
/// vmangos contrib/mmap/src/MapBuilder.cpp tile writer). The polygon flags are the generator's terrain classes
/// (<see cref="NavTerrain"/>; vmangos contrib/mmap/src/TileWorker.cpp:757-780: ground, ground + steep, ground + water, water, magma, slime), the
/// area is the low six bits of the area-and-type byte and the type the top two (0 ground, 1 off-mesh connection). Recast space is
/// (world Y, world Z, world X) (<see cref="NavMeshFormat.ToWorld"/>). A polygon edge whose neighbour entry has the external-link bit 0x8000
/// crosses into the Detour tile at <see cref="NavMeshFormat.SideOffset"/> of its low byte (DetourNavMesh.h <c>DT_EXT_LINK</c>), and the server joins it to
/// the opposite border edges of that tile where they overlap (<see cref="NavMeshTile.GetCrossPortals"/>).
/// </para>
/// </summary>
public sealed class NavLayerSource : ServerLayerSource
{
    /// <summary>Creates the source over <paramref name="data"/>.</summary>
    public NavLayerSource(ServerData data)
        : base(data, [LayerIds.NavPoly, LayerIds.NavDetail, LayerIds.NavPortals, LayerIds.NavBvRejected])
    {
    }

    /// <summary>The colour key of a polygon's flags (precedence magma, slime, water, steep slopes, ground), or null for flags with none of them.</summary>
    // ref: src/ArcaneCore.Game/Maps/Collision/CollisionContracts.cs:205-214 NavTerrain ground 0x01, magma 0x02, slime 0x04, water 0x08, steep 0x10
    // ref: vmangos@0e3ff01 src/game/Maps/MoveMapSharedDefines.h:43-47 NAV_GROUND/NAV_MAGMA/NAV_SLIME/NAV_WATER/NAV_STEEP_SLOPES
    public static string? NavColorKey(ushort flags)
    {
        var terrain = (NavTerrain)flags;
        if ((terrain & NavTerrain.Magma) != 0)
        {
            return ColorKeys.Magma;
        }

        if ((terrain & NavTerrain.Slime) != 0)
        {
            return ColorKeys.Slime;
        }

        if ((terrain & NavTerrain.Water) != 0)
        {
            return ColorKeys.Water;
        }

        if ((terrain & NavTerrain.SteepSlopes) != 0)
        {
            return ColorKeys.Steep;
        }

        return (terrain & NavTerrain.Ground) != 0 ? ColorKeys.Ground : null;
    }

    /// <summary>The names of the set flags of a polygon, joined with <c>|</c> (unnamed bits as hex).</summary>
    public static string NavFlagNames(ushort flags)
    {
        var names = new List<string>();
        foreach (NavTerrain flag in new[] { NavTerrain.Ground, NavTerrain.Magma, NavTerrain.Slime, NavTerrain.Water, NavTerrain.SteepSlopes })
        {
            if (((NavTerrain)flags & flag) != 0)
            {
                names.Add(flag.ToString());
            }
        }

        ushort known = (ushort)(NavTerrain.Ground | NavTerrain.Magma | NavTerrain.Slime | NavTerrain.Water | NavTerrain.SteepSlopes);
        if ((flags & ~known) != 0)
        {
            names.Add(Hex((uint)(flags & ~known)));
        }

        return names.Count == 0 ? "none" : string.Join('|', names);
    }

    /// <summary>The attribute id of a polygon in <c>nav.poly</c>: flags in the low 16 bits, area above.</summary>
    public static uint PolyAttribute(ushort flags, byte area) => ((uint)area << 16) | flags;

    /// <summary>
    /// The terrain tile on the far side of a polygon edge that crosses a tile border: Detour X follows world Y and Detour Y follows world X (both
    /// grow while the terrain tile index falls), so Detour step (dx, dy) is terrain step (-dy, -dx). The caller verifies the answer against
    /// the neighbour file's header.
    /// </summary>
    public static TileKey NeighbourTerrainTile(TileKey tile, int detourDx, int detourDy) => new(tile.MapId, tile.X - detourDy, tile.Y - detourDx);

    /// <inheritdoc />
    protected override IReadOnlyCollection<TileKey> ListTiles(string layerId, uint mapId) => Data.NavTiles(mapId);

    /// <inheritdoc />
    protected override TileStatus Peek(string layerId, TileKey tile)
    {
        if (!tile.IsInGrid)
        {
            return new TileStatus(layerId, tile, TileState.NotApplicable, "navmesh tiles are per grid tile");
        }

        bool cached = Data.TryPeekNav(tile, out ParsedFile<NavTileData> parsed);
        return StatusOf(layerId, tile, Data.NavFileExists(tile), cached, parsed, ".mmtile file");
    }

    /// <inheritdoc />
    protected override LayerBuild BuildTile(string layerId, TileKey tile, CancellationToken cancellationToken)
    {
        if (!tile.IsInGrid)
        {
            return NotApplicable(layerId, tile, "navmesh tiles are per grid tile; the map's global tile has none");
        }

        ParsedFile<NavTileData> file = Data.NavFile(tile);
        if (!file.IsOk)
        {
            return FromFailure(layerId, tile, file);
        }

        NavTileData data = file.Value;
        string name = file.Detail ?? NavMeshFormat.TileFileName(tile.MapId, tile.X, tile.Y);
        return layerId switch
        {
            LayerIds.NavPoly => BuildPolygons(tile, data, name, cancellationToken),
            LayerIds.NavDetail => BuildDetail(tile, data, name, cancellationToken),
            LayerIds.NavPortals => BuildPortals(tile, data, name, cancellationToken),
            _ => BuildBvBox(tile, data, name),
        };
    }

    // ref: src/ArcaneCore.Game/Maps/Collision/MMaps/NavMeshFormat.cs:47-50 Recast = (y, z, x); vmangos@0e3ff01 src/game/Maps/PathFinder.cpp:164-165 {y, z, x}
    private static Vector3 World(Vector3 recast) => NavMeshFormat.ToWorld(recast);

    /// <summary>Twice the signed area of a polygon seen from above in world X/Y (positive: counter-clockwise, facing up).</summary>
    private static double SignedArea(IReadOnlyList<Vector3> worldPoints)
    {
        double sum = 0;
        for (int i = 0, j = worldPoints.Count - 1; i < worldPoints.Count; j = i++)
        {
            sum += ((double)worldPoints[j].X * worldPoints[i].Y) - ((double)worldPoints[i].X * worldPoints[j].Y);
        }

        return sum;
    }

    private static LayerBuild BuildPolygons(TileKey tile, NavTileData data, string file, CancellationToken cancellationToken)
    {
        NavMeshTile mesh = data.Tile;
        var parts = new ChunkParts(LayerIds.NavPoly, tile, WorldGrid.Origin(tile));
        GeometryBuilder builder = parts.Current;
        var vertexOf = new int[mesh.Vertices.Length];
        Array.Fill(vertexOf, -1);
        var counts = new SortedDictionary<uint, int>();
        int offMesh = 0;
        int degenerate = 0;
        int flatPolygons = 0;
        int wallEdges = 0;
        int linkedEdges = 0;
        int externalEdges = 0;

        uint Vertex(int index)
        {
            if (vertexOf[index] < 0)
            {
                Vector3 w = World(mesh.Vertices[index]);
                vertexOf[index] = (int)builder.AddVertex(w.X, w.Y, w.Z);
            }

            return (uint)vertexOf[index];
        }

        for (int p = 0; p < mesh.Polys.Length; p++)
        {
            if ((p & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            NavPoly poly = mesh.Polys[p];
            if (poly.IsOffMeshConnection)
            {
                offMesh++;
                continue;
            }

            if (parts.RollIfFull())
            {
                builder = parts.Current;
                Array.Fill(vertexOf, -1);
            }

            int n = poly.VertexCount;
            var world = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                world[i] = World(mesh.Vertices[poly.Vertices[i]]);
            }

            double area2 = SignedArea(world);
            if (n < 2)
            {
                degenerate++;
                continue;
            }

            // A polygon with no area seen from above (vertical, or fewer than three corners) is still a polygon the server walks:
            // its outline is drawn, only the fill is left out.
            bool flat = n < 3 || area2 == 0;
            if (flat)
            {
                flatPolygons++;
            }

            // Recast's winding is clockwise in its own x/z plane, which is not a fixed handedness in world X/Y: order each polygon to face up.
            int step = area2 >= 0 ? 1 : -1;
            uint attribute = PolyAttribute(poly.Flags, poly.Area);
            ushort flags = poly.Flags;
            byte polyArea = poly.Area;
            builder.DescribeAttribute(attribute, () => new JsonObject
            {
                ["kind"] = "navmesh polygon",
                ["flags"] = Hex(flags),
                ["flagNames"] = NavFlagNames(flags),
                ["area"] = polyArea,
                ["colorKey"] = NavColorKey(flags),
            });
            counts[attribute] = counts.GetValueOrDefault(attribute) + 1;
            int first = step > 0 ? 0 : n - 1;
            uint v0 = Vertex(poly.Vertices[first]);
            for (int i = 1; !flat && i + 1 < n; i++)
            {
                uint v1 = Vertex(poly.Vertices[first + (step * i)]);
                uint v2 = Vertex(poly.Vertices[first + (step * (i + 1))]);
                builder.AddTriangle(v0, v1, v2, attribute, edges: false);
            }

            for (int e = 0; e < n; e++)
            {
                ushort nei = poly.Neighbours[e];
                if (nei == 0)
                {
                    wallEdges++;
                }
                else if ((nei & NavMeshFormat.ExternalLink) != 0)
                {
                    externalEdges++;
                }
                else
                {
                    linkedEdges++;
                }

                builder.AddEdge(Vertex(poly.Vertices[e]), Vertex(poly.Vertices[(e + 1) % n]), attribute);
            }
        }

        parts.Stats["file"] = file;
        parts.Stats["polygons"] = counts.Values.Sum();
        parts.Stats["offMeshPolygonsSkipped"] = offMesh;
        parts.Stats["degeneratePolygonsSkipped"] = degenerate;
        parts.Stats["flatPolygonsOutlinedOnly"] = flatPolygons;
        parts.Stats["headerPolyCount"] = data.HeaderPolyCount;
        parts.Stats["headerOffMeshConnections"] = data.HeaderOffMeshCount;
        parts.Stats["vertices"] = mesh.Vertices.Length;
        parts.Stats["wallEdges"] = wallEdges;
        parts.Stats["internalEdges"] = linkedEdges;
        parts.Stats["tileBorderEdges"] = externalEdges;
        parts.Stats["detourTile"] = new JsonArray(mesh.X, mesh.Y, mesh.Layer);
        parts.Stats["usesLiquids"] = data.UsesLiquids;
        foreach ((uint attribute, int count) in counts)
        {
            parts.Stats[$"polygons.{Hex(attribute)}"] = count;
        }

        IReadOnlyList<GeometryChunk> chunks = parts.Build();
        return LayerBuild.Of(LayerIds.NavPoly, tile, chunks, builder.IsEmpty ? $"{file} holds no ground polygons" : file);
    }

    private static LayerBuild BuildDetail(TileKey tile, NavTileData data, string file, CancellationToken cancellationToken)
    {
        NavMeshTile mesh = data.Tile;
        var parts = new ChunkParts(LayerIds.NavDetail, tile, WorldGrid.Origin(tile));
        GeometryBuilder builder = parts.Current;
        var polyVertex = new int[mesh.Vertices.Length];
        var detailVertex = new int[mesh.DetailVertices.Length];
        Array.Fill(polyVertex, -1);
        Array.Fill(detailVertex, -1);
        int triangles = 0;
        int badTriangles = 0;
        int polysWithDetail = 0;

        uint Add(Vector3 recast, ref int slot)
        {
            if (slot < 0)
            {
                Vector3 w = World(recast);
                slot = (int)builder.AddVertex(w.X, w.Y, w.Z);
            }

            return (uint)slot;
        }

        for (int p = 0; p < mesh.Polys.Length && p < mesh.Details.Length; p++)
        {
            if ((p & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            NavPoly poly = mesh.Polys[p];
            NavPolyDetail detail = mesh.Details[p];
            if (poly.IsOffMeshConnection || detail.TriangleCount == 0)
            {
                continue;
            }

            if (parts.RollIfFull())
            {
                builder = parts.Current;
                Array.Fill(polyVertex, -1);
                Array.Fill(detailVertex, -1);
            }

            uint attribute = (uint)p;
            int polyIndex = p;
            builder.DescribeAttribute(attribute, () => new JsonObject
            {
                ["kind"] = "navmesh detail mesh",
                ["polygon"] = polyIndex,
                ["flags"] = Hex(poly.Flags),
                ["flagNames"] = NavFlagNames(poly.Flags),
                ["area"] = poly.Area,
                ["detailVertices"] = detail.VertexCount,
                ["detailTriangles"] = detail.TriangleCount,
            });
            polysWithDetail++;
            for (int t = 0; t < detail.TriangleCount; t++)
            {
                long at = ((long)detail.TriangleBase + t) * 4;
                if (at + 3 > mesh.DetailTriangles.Length)
                {
                    badTriangles++;
                    continue;
                }

                var corner = new (Vector3 Recast, int Kind, int Index)[3];
                bool ok = true;
                for (int c = 0; c < 3 && ok; c++)
                {
                    int index = mesh.DetailTriangles[at + c];

                    // The Detour rule: an index below the polygon's vertex count names one of its own vertices, the rest name its detail vertices.
                    if (index < poly.VertexCount)
                    {
                        int tileVertex = poly.Vertices[index];
                        corner[c] = (mesh.Vertices[tileVertex], 0, tileVertex);
                    }
                    else
                    {
                        long i = (long)detail.VertexBase + index - poly.VertexCount;
                        if (i >= mesh.DetailVertices.Length)
                        {
                            ok = false;
                        }
                        else
                        {
                            corner[c] = (mesh.DetailVertices[i], 1, (int)i);
                        }
                    }
                }

                if (!ok)
                {
                    badTriangles++;
                    continue;
                }

                var world = new[] { World(corner[0].Recast), World(corner[1].Recast), World(corner[2].Recast) };
                double area2 = SignedArea(world);
                if (area2 == 0)
                {
                    badTriangles++;
                    continue;
                }

                var ids = new uint[3];
                for (int c = 0; c < 3; c++)
                {
                    ids[c] = corner[c].Kind == 0 ? Add(corner[c].Recast, ref polyVertex[corner[c].Index]) : Add(corner[c].Recast, ref detailVertex[corner[c].Index]);
                }

                if (area2 > 0)
                {
                    builder.AddTriangle(ids[0], ids[1], ids[2], attribute);
                }
                else
                {
                    builder.AddTriangle(ids[0], ids[2], ids[1], attribute);
                }

                triangles++;
            }
        }

        parts.Stats["file"] = file;
        parts.Stats["detailTriangles"] = triangles;
        parts.Stats["detailTrianglesSkipped"] = badTriangles;
        parts.Stats["polygonsWithDetail"] = polysWithDetail;
        parts.Stats["detailVertices"] = mesh.DetailVertices.Length;
        IReadOnlyList<GeometryChunk> chunks = parts.Build();
        return LayerBuild.Of(LayerIds.NavDetail, tile, chunks, builder.IsEmpty ? $"{file} holds no detail triangles" : file);
    }

    private LayerBuild BuildPortals(TileKey tile, NavTileData data, string file, CancellationToken cancellationToken)
    {
        NavMeshTile mesh = data.Tile;
        var parts = new ChunkParts(LayerIds.NavPortals, tile, WorldGrid.Origin(tile));
        GeometryBuilder builder = parts.Current;
        var neighbours = new Dictionary<(int Dx, int Dy), NavMeshTile?>();
        var neighbourNotes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        uint attribute = 0;
        int externalEdges = 0;
        int portals = 0;
        int edgesWithoutPortal = 0;
        int edgesWithoutNeighbour = 0;
        int diagonalSides = 0;

        for (int p = 0; p < mesh.Polys.Length; p++)
        {
            if ((p & 127) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            NavPoly poly = mesh.Polys[p];
            for (int e = 0; e < poly.VertexCount; e++)
            {
                ushort nei = poly.Neighbours[e];
                if ((nei & NavMeshFormat.ExternalLink) == 0)
                {
                    continue;
                }

                externalEdges++;
                int side = nei & 0xff;
                if (NavMeshFormat.SideOffset(side) is not { } offset)
                {
                    diagonalSides++;
                    continue;
                }

                if (!neighbours.TryGetValue(offset, out NavMeshTile? other))
                {
                    other = ResolveNeighbour(tile, mesh, offset, neighbourNotes);
                    neighbours[offset] = other;
                }

                if (other is null)
                {
                    edgesWithoutNeighbour++;
                    continue;
                }

                // ref: src/ArcaneCore.Game/Maps/Collision/MMaps/NavMeshTile.cs:85-88,686-703 GetCrossPortals (Volatile-published cache); vmangos@0e3ff01 dep/recastnavigation/Detour/Source/DetourNavMesh.cpp:300-415 findConnectingPolys (slab overlap)
                CrossPortal[] found = mesh.GetCrossPortals(p, e, other);
                if (found.Length == 0)
                {
                    edgesWithoutPortal++;
                    continue;
                }

                Vector3 from = World(mesh.Center(p));
                foreach (CrossPortal portal in found)
                {
                    parts.RollIfFull();
                    builder = parts.Current;
                    Vector3 a = World(portal.A);
                    Vector3 b = World(portal.B);
                    Vector3 mid = (a + b) * 0.5f;
                    Vector3 to = World(other.Center(portal.Poly));
                    uint id = ++attribute;
                    int fromPoly = p;
                    int edge = e;
                    int toPoly = portal.Poly;
                    int sideValue = side;
                    NavPoly target = other.Polys[portal.Poly];
                    int toTileX = other.X;
                    int toTileY = other.Y;
                    float length = Vector3.Distance(a, b);
                    float heightA = a.Z;
                    float heightB = b.Z;
                    builder.DescribeAttribute(id, () => new JsonObject
                    {
                        ["kind"] = "tile portal",
                        ["fromPolygon"] = fromPoly,
                        ["edge"] = edge,
                        ["side"] = sideValue,
                        ["toTile"] = new JsonArray(toTileX, toTileY),
                        ["toPolygon"] = toPoly,
                        ["toFlags"] = Hex(target.Flags),
                        ["length"] = length,
                        ["heightAtEnds"] = new JsonArray(heightA, heightB),
                    });
                    uint va = builder.AddVertex(a.X, a.Y, a.Z);
                    uint vb = builder.AddVertex(b.X, b.Y, b.Z);
                    uint vm = builder.AddVertex(mid.X, mid.Y, mid.Z);
                    uint vf = builder.AddVertex(from.X, from.Y, from.Z);
                    uint vt = builder.AddVertex(to.X, to.Y, to.Z);

                    // The shared border segment, then the link a search walks: this polygon's centre, the segment's middle, the neighbour's centre.
                    builder.AddLine(va, vb, id);
                    builder.AddLine(vf, vm, id);
                    builder.AddLine(vm, vt, id);
                    builder.AddPoint(vm, id);
                    portals++;
                }
            }
        }

        parts.Stats["file"] = file;
        parts.Stats["tileBorderEdges"] = externalEdges;
        parts.Stats["portals"] = portals;
        parts.Stats["borderEdgesWithoutPortal"] = edgesWithoutPortal;
        parts.Stats["borderEdgesWithoutNeighbourTile"] = edgesWithoutNeighbour;
        parts.Stats["borderEdgesOnDiagonalSides"] = diagonalSides;
        var notes = new JsonObject();
        foreach ((string key, string value) in neighbourNotes)
        {
            notes[key] = value;
        }

        parts.Stats["neighbourTiles"] = notes;
        IReadOnlyList<GeometryChunk> chunks = parts.Build();
        string detail = builder.IsEmpty
            ? externalEdges == 0 ? $"{file} has no polygon edge on a tile border" : $"{file} has {externalEdges} tile border edges and no portal the server would derive"
            : file;
        return LayerBuild.Of(LayerIds.NavPortals, tile, chunks, detail);
    }

    /// <summary>The parsed neighbour tile across <paramref name="offset"/>, or null with the reason noted when the server could not join to it.</summary>
    private NavMeshTile? ResolveNeighbour(TileKey tile, NavMeshTile mesh, (int Dx, int Dy) offset, SortedDictionary<string, string> notes)
    {
        string key = $"{offset.Dx},{offset.Dy}";
        TileKey neighbourKey = NeighbourTerrainTile(tile, offset.Dx, offset.Dy);
        if (!neighbourKey.IsInGrid)
        {
            notes[key] = $"{neighbourKey} is off the map";
            return null;
        }

        ParsedFile<NavTileData> file = Data.NavFile(neighbourKey);
        if (!file.IsOk)
        {
            notes[key] = file.State == TileState.Rejected ? $"{file.Detail}" : $"{neighbourKey}: no navmesh tile ({file.Detail})";
            return null;
        }

        NavMeshTile other = file.Value.Tile;
        if (other.X != mesh.X + offset.Dx || other.Y != mesh.Y + offset.Dy)
        {
            // The server finds the neighbour by the Detour coordinates in the headers, so a file with other coordinates is not the neighbour.
            notes[key] = $"{file.Detail} has Detour coordinates ({other.X}, {other.Y}), not ({mesh.X + offset.Dx}, {mesh.Y + offset.Dy})";
            return null;
        }

        notes[key] = $"{file.Detail}";
        return other;
    }

    private static LayerBuild BuildBvBox(TileKey tile, NavTileData data, string file)
    {
        NavMeshTile mesh = data.Tile;
        // ref: src/ArcaneCore.Game/Maps/Collision/MMaps/NavMeshTile.cs:125-139 the BV tree is used only when TryVerifyBvTree (:331) accepts it
        if (mesh.HasBvTree)
        {
            return LayerBuild.Empty(LayerIds.NavBvRejected, tile, $"{file}: the BV tree passed the server's verification ({data.HeaderBvNodeCount} header nodes)");
        }

        if (data.HeaderBvNodeCount == 0)
        {
            // Not a failed verification: the file simply stores no BV tree, so there is nothing for the server to reject.
            return LayerBuild.Empty(LayerIds.NavBvRejected, tile, $"{file}: the file stores no BV nodes (nothing to verify; not a rejected tree)");
        }

        string reason = $"the file's {data.HeaderBvNodeCount} BV nodes failed the server's verification (malformed, a polygon missing from the tree, or quantization slack over the limit)";
        WorldPoint origin = WorldGrid.Origin(tile);
        var builder = new GeometryBuilder(LayerIds.NavBvRejected, tile, origin);
        Vector3 a = World(mesh.BoundLow);
        Vector3 b = World(mesh.BoundHigh);
        Vector3 low = Vector3.Min(a, b);
        Vector3 high = Vector3.Max(a, b);
        var corner = new uint[8];
        for (int i = 0; i < 8; i++)
        {
            corner[i] = builder.AddVertex((i & 1) == 0 ? low.X : high.X, (i & 2) == 0 ? low.Y : high.Y, (i & 4) == 0 ? low.Z : high.Z);
        }

        for (int i = 0; i < 8; i++)
        {
            for (int bit = 1; bit <= 4; bit <<= 1)
            {
                if ((i & bit) == 0)
                {
                    builder.AddLine(corner[i], corner[i | bit]);
                }
            }
        }

        builder.DescribeAttribute(0, () => new JsonObject
        {
            ["kind"] = "tile without a usable BV tree",
            ["file"] = file,
            ["reason"] = reason,
            ["polygons"] = mesh.Polys.Length,
            ["headerBvNodes"] = data.HeaderBvNodeCount,
            ["effect"] = "nearest-polygon lookups on this tile scan every polygon instead of walking the tree",
        });
        builder.Stats["file"] = file;
        builder.Stats["reason"] = reason;
        builder.Stats["headerBvNodes"] = data.HeaderBvNodeCount;
        builder.Stats["polygons"] = mesh.Polys.Length;
        return LayerBuild.Of(LayerIds.NavBvRejected, tile, [builder.Build()], reason);
    }
}
