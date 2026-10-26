using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using DotRecast.Detour;

namespace ArcaneCore.Atlas.Reference.Navmesh;

/// <summary>
/// Builds <c>ref.nav.links</c> from the link table DotRecast built when it added the tile (<c>DtNavMesh.AddTile</c>):
/// <list type="bullet">
/// <item><description><b>internal</b>: one line between the centres of two polygons sharing an edge (shared attribute id 1).</description></item>
/// <item><description><b>external</b>: the portal sub-edge the link covers, plus lines from the owning polygon's centre through the portal middle to the
/// neighbouring tile's polygon centre; one attribute per link (from/to polygon, side, edge, sub-edge bytes).</description></item>
/// <item><description><b>offmesh</b>: the connection's two endpoints as points and a line, and a line from each endpoint (as DotRecast snapped it
/// onto the mesh) to the polygon it landed on; one attribute per connection.</description></item>
/// </list>
/// Links to polygons whose tile is not resident are counted but not drawn.
/// </summary>
internal static class RefNavLinkGeometry
{
    private const uint InternalAttribute = 1;
    private const uint FirstDetailedAttribute = 16;

    public static IReadOnlyList<GeometryChunk> Build(
        string layerId,
        TileKey key,
        ResidentNavTile resident,
        IReadOnlyList<NavLink> links,
        NavLinkCounts counts,
        long maxChunkBytes,
        CancellationToken cancellationToken)
    {
        DtMeshTile tile = resident.Tile;
        DtMeshData data = tile.data;
        DtMeshHeader header = data.header;
        var state = new State();
        var parts = new PartedBuilder(layerId, key, WorldGrid.Origin(key), maxChunkBytes, (builder, _) =>
        {
            builder.Stats["detour"] = new JsonArray(header.x, header.y, header.layer);
            builder.Stats["internalLinks"] = counts.Internal;
            builder.Stats["externalLinks"] = counts.External;
            builder.Stats["offMeshLinks"] = counts.OffMesh;
            builder.Stats["staleLinks"] = counts.Stale;
            builder.Stats["maxLinkCount"] = counts.MaxLinkCount;
            builder.Stats["offMeshConnections"] = header.offMeshConCount;
        });

        // Each connection this tile owns is drawn whether or not DotRecast could link it.
        foreach (DtOffMeshConnection con in data.offMeshCons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DrawConnection(parts.Current, state, tile, con, links);
        }

        int lastSource = -1;
        foreach (NavLink link in links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (link.SourcePoly != lastSource)
            {
                lastSource = link.SourcePoly;
                if (parts.RollIfFull())
                {
                    state.ResetVertices();
                }
            }

            if (link.TargetPoly is null || link.TargetTile is null)
            {
                continue;
            }

            switch (link.Kind)
            {
                case NavLinkKind.Internal:
                    DrawInternal(parts.Current, state, tile, link);
                    break;
                case NavLinkKind.External:
                    DrawExternal(parts.Current, state, tile, link);
                    break;
                default:
                    DrawOffMeshLink(parts.Current, state, tile, link);
                    break;
            }
        }

        return parts.Complete();
    }

    private static void DrawInternal(GeometryBuilder builder, State state, DtMeshTile tile, NavLink link)
    {
        // A pair of internal links (A to B and B to A) is one line.
        (int, int) pair = (Math.Min(link.SourcePoly, link.TargetPolyIndex), Math.Max(link.SourcePoly, link.TargetPolyIndex));
        if (!state.InternalPairs.Add(pair))
        {
            return;
        }

        builder.DescribeAttribute(InternalAttribute, () => new JsonObject { ["colorKey"] = "internal", ["kind"] = "internal" });
        uint from = state.Center(builder, tile, tile.data.polys[link.SourcePoly]);
        uint to = state.Center(builder, link.TargetTile!, link.TargetPoly!);
        builder.AddLine(from, to, InternalAttribute);
    }

    private static void DrawExternal(GeometryBuilder builder, State state, DtMeshTile tile, NavLink link)
    {
        DtPoly source = tile.data.polys[link.SourcePoly];
        int edge = link.Link.edge;
        if (edge >= source.vertCount)
        {
            return;
        }

        // The portal is the part of the source polygon's edge the link covers: bmin and bmax are its ends as fractions of the edge from
        // vertex `edge` to the next, scaled to 0-255 (DotRecast writes them in ConnectExtLinks, DtNavMeshQuery.GetPortalPoints reads them).
        // ref: vmangos@0e3ff01 dep/recastnavigation/Detour/Source/DetourNavMesh.cpp:424-445 (connectExtLinks: side = dir, bmin/bmax =
        // clamp(t, 0, 1) * 255) and tests/ThirdParty/DotRecast/DotRecast.Detour/DtNavMesh.cs:728-763 (the same in DotRecast).
        WorldPoint a = NavGeometry.ToWorld(tile.data.verts, source.verts[edge]);
        WorldPoint b = NavGeometry.ToWorld(tile.data.verts, source.verts[(edge + 1) % source.vertCount]);
        WorldPoint start = Lerp(a, b, link.Link.bmin / 255.0);
        WorldPoint end = Lerp(a, b, link.Link.bmax / 255.0);
        WorldPoint middle = Lerp(start, end, 0.5);
        uint id = state.NextAttribute++;
        builder.DescribeAttribute(id, () => new JsonObject
        {
            ["colorKey"] = "external",
            ["kind"] = "external",
            ["fromPoly"] = link.SourcePoly,
            ["toTile"] = link.TargetKey is { } t ? new JsonArray(t.X, t.Y) : null,
            ["toPoly"] = link.TargetPolyIndex,
            ["side"] = (int)link.Link.side,
            ["edge"] = edge,
            ["bmin"] = (int)link.Link.bmin,
            ["bmax"] = (int)link.Link.bmax,
        });

        uint portalStart = builder.AddVertex(start);
        uint portalEnd = builder.AddVertex(end);
        uint portalMiddle = builder.AddVertex(middle);
        uint from = state.Center(builder, tile, source);
        uint to = state.Center(builder, link.TargetTile!, link.TargetPoly!);
        builder.AddLine(portalStart, portalEnd, id);
        builder.AddLine(from, portalMiddle, id);
        builder.AddLine(portalMiddle, to, id);
    }

    private static void DrawConnection(GeometryBuilder builder, State state, DtMeshTile tile, DtOffMeshConnection con, IReadOnlyList<NavLink> links)
    {
        uint id = state.ConnectionAttribute(tile, con.poly);
        bool startLinked = links.Any(l => l.SourcePoly == con.poly && l.Link.edge == 0 && l.TargetPoly is not null);
        bool endLinked = links.Any(l => l.SourcePoly == con.poly && l.Link.edge == 1 && l.TargetPoly is not null);
        WorldPoint from = NavGeometry.ToWorld(con.pos[0]);
        WorldPoint to = NavGeometry.ToWorld(con.pos[1]);
        state.ConnectionJson[id] = Connection(con, from, to, startLinked, endLinked);
        state.Describe(builder, id);
        uint a = builder.AddVertex(from);
        uint b = builder.AddVertex(to);
        builder.AddLine(a, b, id);
        builder.AddPoint(a, id);
        builder.AddPoint(b, id);
    }

    private static JsonObject Connection(DtOffMeshConnection con, WorldPoint from, WorldPoint to, bool? startLinked, bool? endLinked) => new()
    {
        ["colorKey"] = "offmesh",
        ["kind"] = "offmesh",
        ["userId"] = con.userId,
        ["poly"] = con.poly,
        ["radius"] = con.rad,
        ["bidirectional"] = (con.flags & DtDetour.DT_OFFMESH_CON_BIDIR) != 0,
        ["flags"] = con.flags,
        ["side"] = con.side,
        ["from"] = new JsonArray(from.X, from.Y, from.Z),
        ["to"] = new JsonArray(to.X, to.Y, to.Z),
        ["startLinked"] = startLinked,
        ["endLinked"] = endLinked,
    };

    private static void DrawOffMeshLink(GeometryBuilder builder, State state, DtMeshTile tile, NavLink link)
    {
        DtPoly source = tile.data.polys[link.SourcePoly];
        DtPoly target = link.TargetPoly!;
        DtMeshTile targetTile = link.TargetTile!;
        bool fromConnection = source.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION;
        DtMeshTile connectionTile = fromConnection ? tile : targetTile;
        DtPoly connectionPoly = fromConnection ? source : target;
        uint id = state.ConnectionAttribute(connectionTile, connectionPoly.index);
        if (!state.ConnectionJson.ContainsKey(id))
        {
            // A connection another tile owns, seen from the polygon it lands on: the link state of its other end is not known here.
            DtOffMeshConnection con = connectionTile.data.offMeshCons.First(c => c.poly == connectionPoly.index);
            state.ConnectionJson[id] = Connection(con, NavGeometry.ToWorld(con.pos[0]), NavGeometry.ToWorld(con.pos[1]), null, null);
        }

        state.Describe(builder, id);

        // The connection polygon's two vertices are the endpoints as DotRecast snapped them onto the mesh. A link from the
        // connection leaves from the vertex of its edge (0 start, 1 end); a link into it arrives at the nearer vertex.
        WorldPoint landingCentre = fromConnection
            ? NavGeometry.PolyCenter(targetTile.data.verts, target.verts, target.vertCount)
            : NavGeometry.PolyCenter(tile.data.verts, source.verts, source.vertCount);
        int vertex = fromConnection && link.Link.edge <= 1 ? link.Link.edge : Nearer(connectionTile.data.verts, connectionPoly, landingCentre);
        uint endpoint = builder.AddVertex(NavGeometry.ToWorld(connectionTile.data.verts, connectionPoly.verts[vertex]));
        uint landing = fromConnection ? state.Center(builder, targetTile, target) : state.Center(builder, tile, source);
        builder.AddLine(endpoint, landing, id);
    }

    private static int Nearer(float[] verts, DtPoly connection, WorldPoint to)
    {
        WorldPoint a = NavGeometry.ToWorld(verts, connection.verts[0]);
        WorldPoint b = NavGeometry.ToWorld(verts, connection.verts[1]);
        return Distance2(a, to) <= Distance2(b, to) ? 0 : 1;
    }

    private static double Distance2(WorldPoint a, WorldPoint b)
    {
        WorldPoint d = a - b;
        return (d.X * d.X) + (d.Y * d.Y) + (d.Z * d.Z);
    }

    private static WorldPoint Lerp(WorldPoint a, WorldPoint b, double t)
        => new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t));

    /// <summary>Per-build caches: polygon centre vertices, drawn internal pairs, attribute ids and connection descriptions.</summary>
    private sealed class State
    {
        private readonly Dictionary<(int X, int Y, int Layer, int Poly), uint> _centers = [];
        private readonly Dictionary<(int X, int Y, int Layer, int Poly), uint> _connections = [];

        public HashSet<(int, int)> InternalPairs { get; } = [];

        public uint NextAttribute { get; set; } = FirstDetailedAttribute;

        /// <summary>The attribute table entry of each connection attribute id (cloned into every part that uses it).</summary>
        public Dictionary<uint, JsonObject> ConnectionJson { get; } = [];

        /// <summary>The centre-of-polygon vertex of <paramref name="poly"/> in the current builder (added on first use).</summary>
        public uint Center(GeometryBuilder builder, DtMeshTile tile, DtPoly poly)
        {
            (int, int, int, int) id = (tile.data.header.x, tile.data.header.y, tile.data.header.layer, poly.index);
            if (!_centers.TryGetValue(id, out uint vertex))
            {
                vertex = builder.AddVertex(NavGeometry.PolyCenter(tile.data.verts, poly.verts, poly.vertCount));
                _centers[id] = vertex;
            }

            return vertex;
        }

        /// <summary>One attribute id per off-mesh connection polygon, shared by its line, its points and its links.</summary>
        public uint ConnectionAttribute(DtMeshTile tile, int poly)
        {
            (int, int, int, int) id = (tile.data.header.x, tile.data.header.y, tile.data.header.layer, poly);
            if (!_connections.TryGetValue(id, out uint attribute))
            {
                attribute = NextAttribute++;
                _connections[id] = attribute;
            }

            return attribute;
        }

        /// <summary>Adds the connection's attribute entry to <paramref name="builder"/> unless it already has it.</summary>
        public void Describe(GeometryBuilder builder, uint id)
            => builder.DescribeAttribute(id, () => ConnectionJson[id].DeepClone().AsObject());

        /// <summary>Forgets vertex indices when a new part starts (indices are per part).</summary>
        public void ResetVertices() => _centers.Clear();
    }
}
