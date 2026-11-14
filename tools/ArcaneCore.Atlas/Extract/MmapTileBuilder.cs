using System.Buffers.Binary;
using ArcaneCore.Game.Maps.Collision.MMaps;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Recast;

namespace ArcaneCore.Atlas.Extract;

/// <summary>
/// Builds the navigation mesh of one map tile from its triangle soup, with the parameters and the steps of the reference
/// generator: triangles are marked ground, steep or unwalkable by their slope (terrain and models have different
/// limits), rasterized highest first into 25 x 25 sub-tiles, filtered (obstacles, ledges with a lower step limit where
/// terrain meets a model, low ceilings, water depth), meshed by Recast, merged, flagged by area and written as one Detour
/// tile in an <c>.mmtile</c> file. The Recast and Detour steps are DotRecast's; everything between them is re-expressed
/// here. Stateless and thread-safe.
/// </summary>
/// <remarks>
/// Not done: the per-map and per-tile parameter overrides of the reference's <c>config.json</c> (it ships none), and
/// off-mesh connections (the server does not read them).
/// </remarks>
// ref: vmangos@0e3ff01 contrib/mmap/src/TileWorker.cpp:430-944 (buildMoveMapTile)
internal static class MmapTileBuilder
{
    // ref: vmangos@0e3ff01 contrib/mmap/src/MapBuilder.h:37-46 (the cell size divides the tile: 2000 cells per map tile, in 25 sub-tiles of 80)
    public const float BaseUnitDim = 0.2666666f;
    private const int VertexPerMap = 2000;
    private const int VertexPerTile = 80;
    private const int TilesPerMap = VertexPerMap / VertexPerTile;

    // ref: vmangos@0e3ff01 contrib/mmap/src/TileWorker.cpp:992-1011 (getDefaultConfig)
    private const float WalkableSlopeAngleTerrain = 75.0f;
    private const float WalkableSlopeAngleModels = 61.0f;
    private const int MaxEdgeLen = 45;
    private const float MaxSimplificationError = 1.8f;
    private const int MinRegionArea = 30 * 30;
    private const int MergeRegionArea = 10 * 10;
    private const float DetailSampleDist = 2.0f;
    private const float DetailSampleMaxError = 0.5f;

    // ref: vmangos@0e3ff01 contrib/mmap/src/TileWorker.cpp:463-470 (agent: 1.5 yards tall; steps of 1.2 yards where terrain meets a model, 1.8 on terrain)
    private const float AgentHeight = 1.5f;
    private const float AgentMaxClimbModelTerrainTransition = 1.2f;
    private const float AgentMaxClimbTerrain = 1.8f;

    // ref: vmangos@0e3ff01 src/game/Maps/MoveMapSharedDefines.h:40-52 (NavTerrain)
    private const int NavGround = 0x01;
    private const int NavMagma = 0x02;
    private const int NavSlime = 0x04;
    private const int NavWater = 0x08;
    private const int NavSteepSlopes = 0x10;

    private const float RecastPi = 3.14159265f;
    private const int MaxSpanHeight = 0xffff;

    /// <summary>What a tile build produced.</summary>
    /// <param name="File">The <c>.mmtile</c> bytes, or null when the tile has nothing walkable.</param>
    /// <param name="Polygons">Polygons in the tile.</param>
    /// <param name="FailedSubTiles">Sub-tiles a Recast step failed on (they are left out of the tile).</param>
    /// <param name="Problem">Why no file was produced although there was geometry, or null.</param>
    public readonly record struct Result(byte[]? File, int Polygons, int FailedSubTiles, string? Problem);

    /// <summary>Builds the tile.</summary>
    /// <param name="mapId">The map (maps 0 and 1 use the continent parameters).</param>
    /// <param name="mesh">The tile's triangles.</param>
    /// <param name="bmin">The tile's bounds in Recast space (y from the triangles).</param>
    /// <param name="bmax">The tile's upper bounds.</param>
    /// <param name="origin">The map's navigation mesh origin (from its <c>.mmap</c> file).</param>
    /// <param name="isUnderMap">Whether a Recast-space point is below the terrain and under a model; null skips the test.</param>
    /// <param name="usesLiquids">Stored in the file header.</param>
    public static Result Build(uint mapId, MmapMeshData mesh, RcVec3f bmin, RcVec3f bmax, RcVec3f origin, Func<float, float, float, bool>? isUnderMap, bool usesLiquids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        float[] tVerts = [.. mesh.SolidVerts];
        int[] tTris = [.. mesh.SolidTris];
        int tTriCount = tTris.Length / 3;
        float[] lVerts = [.. mesh.LiquidVerts];
        int[] lTris = [.. mesh.LiquidTris];
        int lTriCount = lTris.Length / 3;

        // ref: vmangos@0e3ff01 contrib/mmap/src/TileWorker.cpp:453-498
        bool continent = mapId <= 1;
        float agentRadius = continent ? 0.2f : 0.3f;
        float cs = BaseUnitDim;
        float ch = continent ? 0.25f : 0.1f;
        int walkableHeight = (int)MathF.Ceiling(AgentHeight / ch);
        int walkableClimb = (int)MathF.Floor(AgentMaxClimbModelTerrainTransition / ch);
        int walkableClimbTerrain = (int)MathF.Floor(AgentMaxClimbTerrain / ch);
        int walkableRadius = (int)MathF.Ceiling(agentRadius / cs);
        int borderSize = walkableRadius + 3;
        int size = VertexPerTile + (borderSize * 2);
        int inWaterGround = walkableHeight / 2;
        int stepForGroundInheritWater = (int)MathF.Ceiling(30.0f / ch);

        int[] areas = TriangleAreas(mesh, tVerts, tTris, isUnderMap);

        // Triangles are rasterized highest first (by their first corner), so each sub-tile gets them in that order.
        // ref: vmangos@0e3ff01 dep/recastnavigation/Recast/Source/RecastRasterization.cpp:457-514 (SortAndRasterizeTriangles)
        int[] order = [.. Enumerable.Range(0, tTriCount).OrderByDescending(i => tVerts[(3 * tTris[3 * i]) + 1])];
        float subTileSize = VertexPerTile * cs;
        float pad = (borderSize + 1) * cs;
        List<int>[] solidBins = Bin(order, tVerts, tTris, bmin, subTileSize, pad);
        List<int>[] liquidBins = Bin([.. Enumerable.Range(0, lTriCount)], lVerts, lTris, bmin, subTileSize, pad);

        var context = new RcContext();
        var polyMeshes = new List<RcPolyMesh>(TilesPerMap * TilesPerMap);
        var detailMeshes = new List<RcPolyMeshDetail>(TilesPerMap * TilesPerMap);
        int failed = 0;
        for (int y = 0; y < TilesPerMap; y++)
        {
            for (int x = 0; x < TilesPerMap; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // ref: vmangos@0e3ff01 contrib/mmap/src/TileWorker.cpp:516-524 (the sub-tile's box, grown by the border)
                var subMin = new RcVec3f(bmin.X + (x * subTileSize), bmin.Y, bmin.Z + (y * subTileSize));
                var subMax = new RcVec3f(bmin.X + ((x + 1) * subTileSize), bmax.Y, bmin.Z + ((y + 1) * subTileSize));
                var fieldMin = new RcVec3f(subMin.X - (borderSize * cs), subMin.Y, subMin.Z - (borderSize * cs));
                var fieldMax = new RcVec3f(subMax.X + (borderSize * cs), subMax.Y, subMax.Z + (borderSize * cs));
                List<int> solid = solidBins[x + (y * TilesPerMap)];
                if (solid.Count == 0)
                {
                    // Nothing to stand on here; the empty mesh keeps the merged mesh's bounds those of the whole tile.
                    polyMeshes.Add(EmptyMesh(subMin, subMax, cs, ch, borderSize));
                    continue;
                }

                try
                {
                    var field = new RcHeightfield(size, size, fieldMin, fieldMax, cs, ch, borderSize);
                    var liquids = new RcHeightfield(size, size, fieldMin, fieldMax, cs, ch, borderSize);
                    Rasterize(context, lVerts, lTris, mesh.LiquidType, liquidBins[x + (y * TilesPerMap)], liquids);
                    Rasterize(context, tVerts, tTris, areas, solid, field);

                    // ref: vmangos@0e3ff01 contrib/mmap/src/TileWorker.cpp:614-637
                    RcFilters.FilterLowHangingWalkableObstacles(context, walkableClimbTerrain, field);
                    FilterLedgeSpans(walkableHeight, walkableClimb, walkableClimbTerrain, field);
                    RemoveModelAreas(field);
                    RcFilters.FilterWalkableLowHeightSpans(context, walkableHeight, field);
                    ApplyLiquid(liquids, field, inWaterGround, stepForGroundInheritWater);

                    // ref: vmangos@0e3ff01 contrib/mmap/src/TileWorker.cpp:637-694
                    RcCompactHeightfield compact = RcCompacts.BuildCompactHeightfield(context, walkableHeight, walkableClimbTerrain, field);
                    RcAreas.ErodeWalkableArea(context, walkableRadius, compact);
                    RcAreas.MedianFilterWalkableArea(context, compact);
                    RcRegions.BuildDistanceField(context, compact);
                    RcRegions.BuildRegions(context, compact, MinRegionArea, MergeRegionArea);
                    RcContourSet contours = RcContours.BuildContours(context, compact, MaxSimplificationError, MaxEdgeLen, RcBuildContoursFlags.RC_CONTOUR_TESS_WALL_EDGES);
                    RcPolyMesh polyMesh = RcMeshs.BuildPolyMesh(context, contours, NavMeshFormat.MaxVertsPerPoly);
                    if (polyMesh is null || polyMesh.npolys == 0)
                    {
                        polyMeshes.Add(EmptyMesh(subMin, subMax, cs, ch, borderSize));
                        continue;
                    }

                    RcPolyMeshDetail detail = RcMeshDetails.BuildPolyMeshDetail(context, polyMesh, compact, DetailSampleDist, DetailSampleMaxError);
                    if (detail is null)
                    {
                        failed++;
                        polyMeshes.Add(EmptyMesh(subMin, subMax, cs, ch, borderSize));
                        continue;
                    }

                    polyMeshes.Add(polyMesh);
                    detailMeshes.Add(detail);
                }
                catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
                {
                    // The reference prints the failed step and goes on without this sub-tile.
                    failed++;
                    polyMeshes.Add(EmptyMesh(subMin, subMax, cs, ch, borderSize));
                }
            }
        }

        if (detailMeshes.Count == 0)
        {
            return new Result(null, 0, failed, null);
        }

        // ref: vmangos@0e3ff01 contrib/mmap/src/TileWorker.cpp:705-751
        RcPolyMesh merged = RcMeshs.MergePolyMeshes(context, [.. polyMeshes], polyMeshes.Count);
        RcPolyMeshDetail mergedDetail = RcMeshDetails.MergePolyMeshDetails(context, [.. detailMeshes], detailMeshes.Count);
        if (merged is null || mergedDetail is null || merged.npolys == 0)
        {
            return new Result(null, 0, failed, null);
        }

        // ref: vmangos@0e3ff01 contrib/mmap/src/TileWorker.cpp:753-785 (polygon flags from the area)
        for (int i = 0; i < merged.npolys; i++)
        {
            if (merged.areas[i] == RcRecast.RC_NULL_AREA)
            {
                continue;
            }

            merged.flags[i] |= (merged.areas[i] & 0xF) switch
            {
                MmapArea.None => 0,
                MmapArea.Ground => NavGround,
                MmapArea.SteepSlope => NavGround | NavSteepSlopes,
                MmapArea.WaterTransition => NavGround | NavWater,
                MmapArea.Water => NavWater,
                MmapArea.Magma => NavMagma,
                MmapArea.Slime => NavSlime,
                _ => 0x1,
            };
        }

        if (merged.nverts >= 0xffff)
        {
            return new Result(null, 0, failed, $"too many vertices ({merged.nverts}).");
        }

        // ref: vmangos@0e3ff01 contrib/mmap/src/TileWorker.cpp:787-822
        var option = new DtNavMeshCreateParams
        {
            verts = merged.verts,
            vertCount = merged.nverts,
            polys = merged.polys,
            polyAreas = merged.areas,
            polyFlags = merged.flags,
            polyCount = merged.npolys,
            nvp = merged.nvp,
            detailMeshes = mergedDetail.meshes,
            detailVerts = mergedDetail.verts,
            detailVertsCount = mergedDetail.nverts,
            detailTris = mergedDetail.tris,
            detailTriCount = mergedDetail.ntris,
            walkableHeight = AgentHeight,
            walkableRadius = agentRadius,
            walkableClimb = AgentMaxClimbTerrain,
            tileX = (int)((((bmin.X + bmax.X) / 2) - origin.X) / MmapTerrain.GridSize),
            tileZ = (int)((((bmin.Z + bmax.Z) / 2) - origin.Z) / MmapTerrain.GridSize),
            tileLayer = 0,
            buildBvTree = true,
            bmin = bmin,
            bmax = bmax,
            cs = cs,
            ch = ch,
        };
        DtMeshData data = DtNavMeshBuilder.CreateNavMeshData(option);
        if (data is null)
        {
            return new Result(null, 0, failed, "the Detour tile could not be created.");
        }

        return new Result(Serialize(data, usesLiquids), data.header.polyCount, failed, null);
    }

    /// <summary>
    /// The area of every solid triangle from its slope: ground up to the slope a player can walk (52 degrees), steep up to
    /// the limit for creatures (75 degrees on terrain, 61 on models), unwalkable beyond. Model triangles get the model
    /// variants, and one whose three (slightly shrunk) corners are all below the terrain and under a model is unwalkable.
    /// </summary>
    // ref: vmangos@0e3ff01 contrib/mmap/src/TileWorker.cpp:546-608
    private static int[] TriangleAreas(MmapMeshData mesh, float[] verts, int[] tris, Func<float, float, float, bool>? isUnderMap)
    {
        float playerClimbLimit = MathF.Cos(52.0f / 180.0f * RecastPi);
        float maxClimbLimitTerrain = MathF.Cos(WalkableSlopeAngleTerrain / 180.0f * RecastPi);
        float maxClimbLimitModels = MathF.Cos(WalkableSlopeAngleModels / 180.0f * RecastPi);
        var areas = new int[tris.Length / 3];
        Span<float> corners = stackalloc float[9];
        for (int i = 0; i < areas.Length; i++)
        {
            int a = tris[i * 3] * 3;
            int b = tris[(i * 3) + 1] * 3;
            int c = tris[(i * 3) + 2] * 3;

            // The y of the unit normal of (b - a) x (c - a).
            float e0x = verts[b] - verts[a];
            float e0y = verts[b + 1] - verts[a + 1];
            float e0z = verts[b + 2] - verts[a + 2];
            float e1x = verts[c] - verts[a];
            float e1y = verts[c + 1] - verts[a + 1];
            float e1z = verts[c + 2] - verts[a + 2];
            float nx = (e0y * e1z) - (e0z * e1y);
            float ny = (e0z * e1x) - (e0x * e1z);
            float nz = (e0x * e1y) - (e0y * e1x);
            float normalY = ny * (1.0f / MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz)));

            bool terrain = mesh.IsTerrainTriangle(i);
            int area = MmapArea.None;
            if (normalY > playerClimbLimit)
            {
                area = terrain ? MmapArea.Ground : MmapArea.GroundModel;
            }
            else if (normalY > (terrain ? maxClimbLimitTerrain : maxClimbLimitModels))
            {
                area = terrain ? MmapArea.SteepSlope : MmapArea.SteepSlopeModel;
            }

            if (!terrain && area != MmapArea.None && isUnderMap is not null)
            {
                ReadOnlySpan<int> index = [a, b, c];
                for (int corner = 0; corner < 3; corner++)
                {
                    for (int v = 0; v < 3; v++)
                    {
                        corners[(3 * corner) + v] = ((5 * verts[index[corner] + v]) + verts[index[(corner + 1) % 3] + v] + verts[index[(corner + 2) % 3] + v]) / 7;
                    }
                }

                if (isUnderMap(corners[0], corners[1], corners[2]) && isUnderMap(corners[3], corners[4], corners[5]) && isUnderMap(corners[6], corners[7], corners[8]))
                {
                    area = MmapArea.None;
                }
            }

            areas[i] = area;
        }

        return areas;
    }

    /// <summary>Lists, per sub-tile, the triangles whose footprint may touch it, keeping the given order.</summary>
    private static List<int>[] Bin(int[] order, float[] verts, int[] tris, RcVec3f bmin, float subTileSize, float pad)
    {
        var bins = new List<int>[TilesPerMap * TilesPerMap];
        for (int i = 0; i < bins.Length; i++)
        {
            bins[i] = [];
        }

        foreach (int triangle in order)
        {
            int a = tris[triangle * 3] * 3;
            int b = tris[(triangle * 3) + 1] * 3;
            int c = tris[(triangle * 3) + 2] * 3;
            float minX = MathF.Min(verts[a], MathF.Min(verts[b], verts[c]));
            float maxX = MathF.Max(verts[a], MathF.Max(verts[b], verts[c]));
            float minZ = MathF.Min(verts[a + 2], MathF.Min(verts[b + 2], verts[c + 2]));
            float maxZ = MathF.Max(verts[a + 2], MathF.Max(verts[b + 2], verts[c + 2]));
            if (!float.IsFinite(minX + maxX + minZ + maxZ))
            {
                continue;
            }

            int x0 = Math.Max(0, (int)MathF.Floor((minX - pad - bmin.X) / subTileSize));
            int x1 = Math.Min(TilesPerMap - 1, (int)MathF.Floor((maxX + pad - bmin.X) / subTileSize));
            int z0 = Math.Max(0, (int)MathF.Floor((minZ - pad - bmin.Z) / subTileSize));
            int z1 = Math.Min(TilesPerMap - 1, (int)MathF.Floor((maxZ + pad - bmin.Z) / subTileSize));
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    bins[x + (z * TilesPerMap)].Add(triangle);
                }
            }
        }

        return bins;
    }

    private static void Rasterize(RcContext context, float[] verts, int[] tris, IReadOnlyList<int> areas, List<int> triangles, RcHeightfield field)
    {
        if (triangles.Count == 0)
        {
            return;
        }

        var subTris = new int[triangles.Count * 3];
        var subAreas = new int[triangles.Count];
        for (int i = 0; i < triangles.Count; i++)
        {
            int triangle = triangles[i];
            subTris[i * 3] = tris[triangle * 3];
            subTris[(i * 3) + 1] = tris[(triangle * 3) + 1];
            subTris[(i * 3) + 2] = tris[(triangle * 3) + 2];
            subAreas[i] = areas[triangle];
        }

        RcRasterizations.RasterizeTriangles(context, verts, subTris, subAreas, subAreas.Length, field, 0);
    }

    private static RcPolyMesh EmptyMesh(RcVec3f bmin, RcVec3f bmax, float cs, float ch, int borderSize) => new()
    {
        verts = [],
        polys = [],
        regs = [],
        areas = [],
        flags = [],
        nverts = 0,
        npolys = 0,
        maxpolys = 0,
        nvp = NavMeshFormat.MaxVertsPerPoly,
        bmin = bmin,
        bmax = bmax,
        cs = cs,
        ch = ch,
        borderSize = borderSize,
        maxEdgeError = MaxSimplificationError,
    };

    private static bool IsModelArea(int area) => area is MmapArea.GroundModel or MmapArea.SteepSlopeModel;

    /// <summary>
    /// The reference's ledge filter. A span next to a drop deeper than the step limit becomes unwalkable; one whose
    /// reachable neighbours differ by more than the limit becomes a steep slope. The limit is the terrain step, except
    /// where the span and its reachable neighbours are not all terrain or all model: there it is the lower transition
    /// step, and such a span becomes unwalkable instead of steep.
    /// </summary>
    // ref: vmangos@0e3ff01 contrib/mmap/src/TileWorker.cpp:108-206 (filterLedgeSpans)
    private static void FilterLedgeSpans(int walkableHeight, int walkableClimbTransition, int walkableClimbTerrain, RcHeightfield field)
    {
        int w = field.width;
        int h = field.height;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                for (RcSpan? span = field.spans[x + (y * w)]; span != null; span = span.next)
                {
                    if (span.area == RcRecast.RC_NULL_AREA)
                    {
                        continue;
                    }

                    int bot = span.smax;
                    int top = span.next != null ? span.next.smin : MaxSpanHeight;
                    int minNeighborHeight = MaxSpanHeight;
                    int accessibleMin = span.smax;
                    int accessibleMax = span.smax;
                    bool allTerrain = true;
                    bool allModel = true;
                    for (int dir = 0; dir < 4; dir++)
                    {
                        int dx = x + RcRecast.GetDirOffsetX(dir);
                        int dy = y + RcRecast.GetDirOffsetY(dir);
                        if (dx < 0 || dy < 0 || dx >= w || dy >= h)
                        {
                            minNeighborHeight = Math.Min(minNeighborHeight, -walkableClimbTerrain - bot);
                            continue;
                        }

                        // From below everything up to the first span.
                        RcSpan? neighbor = field.spans[dx + (dy * w)];
                        int nbot = -walkableClimbTerrain;
                        int ntop = neighbor != null ? neighbor.smin : MaxSpanHeight;
                        if (Math.Min(top, ntop) - Math.Max(bot, nbot) > walkableHeight)
                        {
                            minNeighborHeight = Math.Min(minNeighborHeight, nbot - bot);
                        }

                        for (; neighbor != null; neighbor = neighbor.next)
                        {
                            if (neighbor.area == RcRecast.RC_NULL_AREA)
                            {
                                continue;
                            }

                            nbot = neighbor.smax;
                            ntop = neighbor.next != null ? neighbor.next.smin : MaxSpanHeight;
                            if (Math.Min(top, ntop) - Math.Max(bot, nbot) > walkableHeight)
                            {
                                minNeighborHeight = Math.Min(minNeighborHeight, nbot - bot);
                                if (Math.Abs(nbot - bot) <= walkableClimbTerrain)
                                {
                                    accessibleMin = Math.Min(accessibleMin, nbot);
                                    accessibleMax = Math.Max(accessibleMax, nbot);
                                    if (IsModelArea(neighbor.area))
                                    {
                                        allTerrain = false;
                                    }
                                    else
                                    {
                                        allModel = false;
                                    }
                                }
                            }
                        }
                    }

                    bool transition = IsModelArea(span.area) ? !allModel : !allTerrain;
                    int maxClimb = transition ? walkableClimbTransition : walkableClimbTerrain;
                    if (minNeighborHeight < -maxClimb)
                    {
                        span.area = RcRecast.RC_NULL_AREA;
                    }
                    else if (accessibleMax - accessibleMin > maxClimb)
                    {
                        span.area = transition ? RcRecast.RC_NULL_AREA : MmapArea.SteepSlope;
                    }
                }
            }
        }
    }

    /// <summary>Folds the model variants of the areas back into ground and steep slope.</summary>
    // ref: vmangos@0e3ff01 contrib/mmap/src/TileWorker.cpp:39-55 (filterRemoveUselessAreas)
    private static void RemoveModelAreas(RcHeightfield field)
    {
        foreach (RcSpan first in field.spans)
        {
            for (RcSpan? span = first; span != null; span = span.next)
            {
                span.area = span.area switch
                {
                    MmapArea.GroundModel => MmapArea.Ground,
                    MmapArea.SteepSlopeModel => MmapArea.SteepSlope,
                    _ => span.area,
                };
            }
        }
    }

    /// <summary>
    /// Marks the ground under liquid: with at least <paramref name="min"/> cells of liquid over it (and at most
    /// <paramref name="max"/>) a ground span takes the liquid's area; shallower water makes it a transition area both
    /// walkers and swimmers use. A steep slope takes the liquid's area only when deep enough.
    /// </summary>
    // ref: vmangos@0e3ff01 contrib/mmap/src/TileWorker.cpp:57-95 (filterWalkableLowHeightSpansWith)
    private static void ApplyLiquid(RcHeightfield liquids, RcHeightfield field, int min, int max)
    {
        for (int i = 0; i < field.spans.Length; i++)
        {
            for (RcSpan? span = field.spans[i]; span != null; span = span.next)
            {
                for (RcSpan? liquid = liquids.spans[i]; liquid != null; liquid = liquid.next)
                {
                    int depth = liquid.smin - span.smax;
                    if (span.area == MmapArea.Ground)
                    {
                        if (depth <= max && depth >= 0)
                        {
                            if (depth >= min)
                            {
                                span.area = liquid.area;
                            }
                            else if (liquid.area == MmapArea.Water)
                            {
                                span.area = MmapArea.WaterTransition;
                            }
                        }
                    }
                    else if (span.area == MmapArea.SteepSlope && depth >= min && depth <= max)
                    {
                        span.area = liquid.area;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The tile file: the generator's header (magic, Detour version, generator version, data size, uses-liquids), then the
    /// Detour tile in its native layout with 16-byte link records, as the reference builds Detour with 64-bit polygon
    /// references. The link area is left empty; the server builds its own links.
    /// </summary>
    // ref: vmangos@0e3ff01 src/game/Maps/MoveMapSharedDefines.h:28-38 (MmapTileHeader) and dep/recastnavigation/Detour/Source/DetourNavMeshBuilder.cpp (dtCreateNavMeshData layout)
    private static byte[] Serialize(DtMeshData data, bool usesLiquids)
    {
        const int fileHeader = 20;
        const int tileHeader = 100;
        const int linkSize = 16;
        DtMeshHeader h = data.header;
        int vertsSize = Align4(12 * h.vertCount);
        int polysSize = Align4(32 * h.polyCount);
        int linksSize = Align4(linkSize * h.maxLinkCount);
        int detailMeshesSize = Align4(12 * h.detailMeshCount);
        int detailVertsSize = Align4(12 * h.detailVertCount);
        int detailTrisSize = Align4(4 * h.detailTriCount);
        int bvSize = Align4(16 * h.bvNodeCount);
        int dataSize = tileHeader + vertsSize + polysSize + linksSize + detailMeshesSize + detailVertsSize + detailTrisSize + bvSize;
        var file = new byte[fileHeader + dataSize];
        Span<byte> span = file;
        BinaryPrimitives.WriteUInt32LittleEndian(span, NavMeshFormat.MmapMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], NavMeshFormat.DetourVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], NavMeshFormat.MmapVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)dataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], usesLiquids ? 1u : 0u);

        int at = fileHeader;
        void I32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(at), value);
            at += 4;
        }

        void F32(float value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(at), value);
            at += 4;
        }

        void U16(int value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at), (ushort)value);
            at += 2;
        }

        I32(NavMeshFormat.DetourMagic);
        I32((int)NavMeshFormat.DetourVersion);
        I32(h.x);
        I32(h.y);
        I32(h.layer);
        I32(h.userId);
        I32(h.polyCount);
        I32(h.vertCount);
        I32(h.maxLinkCount);
        I32(h.detailMeshCount);
        I32(h.detailVertCount);
        I32(h.detailTriCount);
        I32(h.bvNodeCount);
        I32(0); // off-mesh connections
        I32(h.offMeshBase); // the first polygon past the ground polygons
        F32(h.walkableHeight);
        F32(h.walkableRadius);
        F32(h.walkableClimb);
        F32(h.bmin.X);
        F32(h.bmin.Y);
        F32(h.bmin.Z);
        F32(h.bmax.X);
        F32(h.bmax.Y);
        F32(h.bmax.Z);
        F32(h.bvQuantFactor);

        for (int i = 0; i < h.vertCount * 3; i++)
        {
            F32(data.verts[i]);
        }

        at = fileHeader + tileHeader + vertsSize;
        for (int i = 0; i < h.polyCount; i++)
        {
            DtPoly poly = data.polys[i];
            I32(0); // first link: filled in when the tile is added to a mesh
            for (int j = 0; j < NavMeshFormat.MaxVertsPerPoly; j++)
            {
                U16(j < poly.verts.Length ? poly.verts[j] : 0);
            }

            for (int j = 0; j < NavMeshFormat.MaxVertsPerPoly; j++)
            {
                U16(j < poly.neis.Length ? poly.neis[j] : 0);
            }

            U16(poly.flags);
            file[at++] = (byte)poly.vertCount;
            file[at++] = (byte)poly.areaAndtype;
        }

        at = fileHeader + tileHeader + vertsSize + polysSize + linksSize;
        for (int i = 0; i < h.detailMeshCount; i++)
        {
            DtPolyDetail detail = data.detailMeshes[i];
            I32(detail.vertBase);
            I32(detail.triBase);
            file[at++] = detail.vertCount;
            file[at++] = detail.triCount;
            at += 2;
        }

        at = fileHeader + tileHeader + vertsSize + polysSize + linksSize + detailMeshesSize;
        for (int i = 0; i < h.detailVertCount * 3; i++)
        {
            F32(data.detailVerts[i]);
        }

        at = fileHeader + tileHeader + vertsSize + polysSize + linksSize + detailMeshesSize + detailVertsSize;
        for (int i = 0; i < h.detailTriCount * 4; i++)
        {
            file[at++] = (byte)data.detailTris[i];
        }

        at = fileHeader + tileHeader + vertsSize + polysSize + linksSize + detailMeshesSize + detailVertsSize + detailTrisSize;
        for (int i = 0; i < h.bvNodeCount; i++)
        {
            DtBVNode node = data.bvTree[i];
            U16(node.bmin.X);
            U16(node.bmin.Y);
            U16(node.bmin.Z);
            U16(node.bmax.X);
            U16(node.bmax.Y);
            U16(node.bmax.Z);
            I32(node.i);
        }

        return file;
    }

    private static int Align4(int size) => (size + 3) & ~3;
}
