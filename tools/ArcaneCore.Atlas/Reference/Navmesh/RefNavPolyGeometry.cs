using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Game.Maps.Collision.MMaps;
using DotRecast.Detour;

namespace ArcaneCore.Atlas.Reference.Navmesh;

/// <summary>
/// Builds <c>ref.nav.poly</c>: the polygons exactly as DotRecast parsed them (vertex indices into the tile's vertices, the file's
/// flags and area), each as a triangle fan for picking and depth plus its outline as lines. Off-mesh connection polygons have two
/// vertices and are drawn by <c>ref.nav.links</c> instead. The attribute id of a primitive is its polygon's index in the tile.
/// </summary>
internal static class RefNavPolyGeometry
{
    public static IReadOnlyList<GeometryChunk> Build(string layerId, TileKey key, ResidentNavTile resident, long maxChunkBytes, CancellationToken cancellationToken)
    {
        DtMeshData data = resident.File.Data;
        DtMeshHeader header = data.header;
        int offMesh = data.polys.Count(p => p.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION);
        var perPart = new List<int>();
        var parts = new PartedBuilder(layerId, key, WorldGrid.Origin(key), maxChunkBytes, (builder, part) =>
        {
            builder.Stats["polys"] = part < perPart.Count ? perPart[part] : 0;
            builder.Stats["tilePolys"] = header.polyCount;
            builder.Stats["offMeshPolysSkipped"] = offMesh;
            builder.Stats["detour"] = new JsonArray(header.x, header.y, header.layer);
            builder.Stats["usesLiquids"] = resident.File.Prefix.UsesLiquids;
        });

        int[] vertexMap = new int[header.vertCount];
        Array.Fill(vertexMap, -1);
        uint[] indices = new uint[NavMeshFormat.MaxVertsPerPoly];
        foreach (DtPoly poly in data.polys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (poly.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION)
            {
                continue;
            }

            GeometryBuilder builder = parts.Current;
            ushort flags = (ushort)poly.flags;
            int area = poly.GetArea();
            uint attribute = (uint)poly.index;
            builder.DescribeAttribute(attribute, () => new JsonObject
            {
                ["colorKey"] = NavTerrainNames.ColorKey(flags),
                ["poly"] = poly.index,
                ["flags"] = (int)flags,
                ["flagNames"] = NavTerrainNames.Describe(flags),
                ["area"] = area,
                ["vertCount"] = poly.vertCount,
            });

            for (int i = 0; i < poly.vertCount; i++)
            {
                int tileVertex = poly.verts[i];
                if (vertexMap[tileVertex] < 0)
                {
                    vertexMap[tileVertex] = (int)builder.AddVertex(NavGeometry.ToWorld(data.verts, tileVertex));
                }

                indices[i] = (uint)vertexMap[tileVertex];
            }

            for (int i = 1; i + 1 < poly.vertCount; i++)
            {
                builder.AddTriangle(indices[0], indices[i], indices[i + 1], attribute, edges: false);
            }

            for (int i = 0; i < poly.vertCount; i++)
            {
                builder.AddEdge(indices[i], indices[(i + 1) % poly.vertCount], attribute);
            }

            while (perPart.Count <= parts.Part)
            {
                perPart.Add(0);
            }

            perPart[parts.Part]++;
            if (parts.RollIfFull())
            {
                Array.Fill(vertexMap, -1);
            }
        }

        return parts.Complete();
    }
}
