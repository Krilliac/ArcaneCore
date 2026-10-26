using ArcaneCore.Atlas.Core;
using DotRecast.Detour;

namespace ArcaneCore.Atlas.Reference.Navmesh;

/// <summary>Which kind of connection a Detour link is.</summary>
public enum NavLinkKind
{
    /// <summary>Between two polygons of the same tile, across a shared edge (<c>link.side</c> is 0xff).</summary>
    Internal,

    /// <summary>Across a tile border into a polygon of the neighbouring tile.</summary>
    External,

    /// <summary>To or from an off-mesh connection polygon.</summary>
    OffMesh,
}

/// <summary>One link of a tile's link table, with the polygon it leads to resolved.</summary>
/// <param name="SourcePoly">Index of the polygon that owns the link.</param>
/// <param name="Link">DotRecast's link (reference, edge, side, sub-edge bytes).</param>
/// <param name="TargetTile">The tile holding the target polygon, or null when the reference is stale.</param>
/// <param name="TargetPoly">The target polygon, or null when the reference is stale.</param>
/// <param name="TargetKey">The terrain tile of <paramref name="TargetTile"/>, or null when it is not resident.</param>
/// <param name="Kind">Internal, external or off-mesh.</param>
public sealed record NavLink(int SourcePoly, DtLink Link, DtMeshTile? TargetTile, DtPoly? TargetPoly, TileKey? TargetKey, NavLinkKind Kind)
{
    /// <summary>Index of the target polygon in its tile, or -1.</summary>
    public int TargetPolyIndex => TargetPoly?.index ?? -1;

    /// <summary>True when the target polygon is an off-mesh connection.</summary>
    public bool TargetIsOffMesh => TargetPoly is { } p && p.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION;
}

/// <summary>How many links of each kind a tile holds, and how many lead nowhere.</summary>
public sealed record NavLinkCounts(int Internal, int External, int OffMesh, int Stale, int Allocated, int MaxLinkCount)
{
    /// <summary>Every link in the tile's polygon chains.</summary>
    public int Total => Internal + External + OffMesh + Stale;
}

/// <summary>Reads a resident tile's link table the way Detour walks it: each polygon's chain from <c>firstLink</c> through <c>next</c>.</summary>
public static class NavLinkInventory
{
    /// <summary>
    /// All links of the tile in a canonical order (source polygon, kind, target tile, target polygon, edge), independent of the
    /// order tiles were added in (DotRecast pushes each new link at the front of its chain, so the chain order is not stable).
    /// Call inside a <see cref="ReferenceNavMap.Read{T}"/> callback.
    /// </summary>
    public static List<NavLink> Collect(NavMapView view, ResidentNavTile resident)
    {
        DtMeshTile tile = resident.Tile;
        DtMeshData data = tile.data;
        var links = new List<NavLink>();
        for (int p = 0; p < data.polys.Length; p++)
        {
            DtPoly poly = data.polys[p];
            bool sourceOffMesh = poly.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION;
            int steps = 0;
            for (int index = poly.firstLink; index >= 0 && index < tile.links.Length && steps <= tile.links.Length; index = tile.links[index].next)
            {
                steps++;
                DtLink link = tile.links[index];
                DtMeshTile? targetTile = null;
                DtPoly? targetPoly = null;
                TileKey? targetKey = null;
                if (view.Mesh.GetTileAndPolyByRef(link.refs, out DtMeshTile tt, out DtPoly tp).Succeeded())
                {
                    targetTile = tt;
                    targetPoly = tp;
                    targetKey = view.TerrainTileOf(tt);
                }

                bool targetOffMesh = targetPoly is not null && targetPoly.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION;

                // ref: vmangos@0e3ff01 dep/recastnavigation/Detour/Source/DetourNavMesh.cpp:551 (connectIntLinks: side = 0xff) and :424
                // (connectExtLinks: side = the border direction 0-7); tests/ThirdParty/DotRecast/DotRecast.Detour/DtNavMesh.cs:631 and :728.
                NavLinkKind kind = sourceOffMesh || targetOffMesh ? NavLinkKind.OffMesh
                    : link.side == 0xff ? NavLinkKind.Internal : NavLinkKind.External;
                links.Add(new NavLink(p, link, targetTile, targetPoly, targetKey, kind));
            }
        }

        links.Sort(Compare);
        return links;
    }

    /// <summary>Counts <paramref name="links"/> by kind; stale links (target no longer resolvable) are counted apart.</summary>
    public static NavLinkCounts Count(IReadOnlyList<NavLink> links, DtMeshTile tile)
    {
        int internalLinks = 0, external = 0, offMesh = 0, stale = 0;
        foreach (NavLink link in links)
        {
            if (link.TargetPoly is null)
            {
                stale++;
                continue;
            }

            switch (link.Kind)
            {
                case NavLinkKind.Internal:
                    internalLinks++;
                    break;
                case NavLinkKind.External:
                    external++;
                    break;
                default:
                    offMesh++;
                    break;
            }
        }

        int free = 0;
        for (int i = tile.linksFreeList; i >= 0 && i < tile.links.Length && free <= tile.links.Length; i = tile.links[i].next)
        {
            free++;
        }

        return new NavLinkCounts(internalLinks, external, offMesh, stale, tile.links.Length - free, tile.links.Length);
    }

    private static int Compare(NavLink a, NavLink b)
    {
        int c = a.SourcePoly.CompareTo(b.SourcePoly);
        c = c != 0 ? c : a.Kind.CompareTo(b.Kind);
        c = c != 0 ? c : (a.TargetKey?.X ?? -1).CompareTo(b.TargetKey?.X ?? -1);
        c = c != 0 ? c : (a.TargetKey?.Y ?? -1).CompareTo(b.TargetKey?.Y ?? -1);
        c = c != 0 ? c : a.TargetPolyIndex.CompareTo(b.TargetPolyIndex);
        return c != 0 ? c : a.Link.edge.CompareTo(b.Link.edge);
    }
}
