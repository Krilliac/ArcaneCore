using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Terrain;
using GridCoord = ArcaneCore.Game.Maps.Grid.GridCoord;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// The answer to one <c>query</c>: plain values, produced on the world thread and written to the client off it. <see cref="Ok"/> false
/// means a refusal (<see cref="Code"/>, <see cref="Message"/>); no other field is then meaningful.
/// </summary>
internal sealed class QueryAnswer
{
    public long Id;
    public bool Ok;
    public QueryKind Kind;
    public string Code = string.Empty;
    public string Message = string.Empty;

    /// <summary>los: no static model blocks the segment (doodads ignored, as the server's own line of sight is).</summary>
    public bool Clear;

    /// <summary>los: no static model blocks the segment, doodads included.</summary>
    public bool ClearWithDoodads;

    /// <summary>los: the first model hit on the segment (doodads included), when any model blocks it.</summary>
    public bool HasHit;

    public Vector3 Hit;

    /// <summary>los, height, path: real collision or navigation data backs the answer (false: the open default answered).</summary>
    public bool DataBacked;

    /// <summary>height: the terrain surface under the point (null: the tile holds no terrain there).</summary>
    public float? Terrain;

    /// <summary>height: the nearest model surface below the point (null: none).</summary>
    public float? Model;

    /// <summary>height: the floor the server would put a unit on (terrain or model, <c>MapCollision.GetHeight</c>; null: none).</summary>
    public float? Floor;

    /// <summary>height: outside every enclosing model, or inside one flagged exterior (null: no model data).</summary>
    public bool? Outdoors;

    /// <summary>path: the classification flags of the answer.</summary>
    public PathType PathType;

    public bool HasPath;

    public float Length;

    public Vector3[] Points = [];

    /// <summary>path: the points were cut to <see cref="MapFeedProtocol.MaxResultPoints"/>.</summary>
    public bool Cut;

    /// <summary>undermap: the verdict, and for <see cref="UnderMapKind.UnderMap"/> the depth below the terrain in yards.</summary>
    public UnderMapKind Verdict;

    public float Depth;

    public static QueryAnswer Refused(in FeedRequest request, string code, string message)
        => new() { Id = request.Id, Ok = false, Kind = request.Kind, Code = code, Message = message };

    public static QueryAnswer Refused(long id, string code, string message)
        => new() { Id = id, Ok = false, Code = code, Message = message };
}

/// <summary>
/// The questions a client may ask (<c>query</c>): line of sight, ground height, a navigation path and an under-map verdict, answered by the
/// map's own <see cref="MapCollision"/>. World thread only (the connection hands them to <c>WorldRuntime.InvokeAsync</c>).
/// <para>
/// A question never loads anything: every grid the question touches must be loaded already (<c>Grids.IsGridLoaded</c>) and so must the
/// terrain tile the terrain code would read there (<c>Terrain.IsTileLoaded</c>), otherwise it is refused with
/// <see cref="MapFeedProtocol.NotLoaded"/> and the grid named. The collision API cannot be trusted to refuse by itself:
/// <c>TerrainInfo.GetHeight</c>, <c>GetZoneAndAreaId</c> and <c>MapCollision.ClassifyUnderMap</c> read a tile from disk on first use, and a
/// line of sight through an unloaded tile would answer "clear" (no data is open). The model tree of a map is read from disk the first time
/// something asks for it; for a map with loaded grids that has happened already. Coordinates arrive validated (finite, inside the map).
/// </para>
/// </summary>
internal static class MapFeedQueries
{
    /// <summary>The probe height <c>MapCollision.GetHeight</c> adds when it looks for a model floor (vmangos <c>GetHeightStatic</c>: z + 2).</summary>
    private const float ModelProbeOffset = 2.0f;

    private const float EdgeSlack = 0.01f;

    /// <summary>The running maps with their player and creature counts, ordered by map and instance (the <c>maps</c> of <c>hello</c>).</summary>
    public static MapSummary[] Summarize(WorldRuntime world)
    {
        var maps = new List<MapSummary>();
        foreach (Map map in world.Maps)
        {
            maps.Add(new MapSummary(map.MapId, map.InstanceId, map.PlayerCount, map.FindUpdater<CreatureMapSystem>()?.Creatures.Count ?? 0));
        }

        maps.Sort(static (a, b) => a.MapId != b.MapId ? a.MapId.CompareTo(b.MapId) : a.InstanceId.CompareTo(b.InstanceId));
        return [.. maps];
    }

    public static QueryAnswer Run(WorldRuntime world, in FeedRequest request)
    {
        Map? map = world.FindMap(request.MapId, request.InstanceId);
        if (map is null || map.IsUnloaded)
        {
            return QueryAnswer.Refused(request, MapFeedProtocol.MapNotLoaded, $"map {request.MapId} instance {request.InstanceId} is not running");
        }

        return request.Kind switch
        {
            QueryKind.LineOfSight => AnswerLineOfSight(map, request),
            QueryKind.Height => AnswerHeight(map, request),
            QueryKind.Path => AnswerPath(map, request),
            QueryKind.UnderMap => AnswerUnderMap(map, request),
            QueryKind.Inspect or QueryKind.Anomalies => QueryAnswer.Refused(request, MapFeedProtocol.BadArgs, "inspect and anomalies are answered by the insight path, not here"),
            _ => QueryAnswer.Refused(request, MapFeedProtocol.BadArgs, "unknown query kind"),
        };
    }

    private static QueryAnswer AnswerLineOfSight(Map map, in FeedRequest request)
    {
        if (!SegmentLoaded(map, request.A, request.B, out (int X, int Y) missing))
        {
            return NotLoaded(request, missing);
        }

        MapCollision collision = map.Collision;
        ILineOfSight models = collision.LineOfSight;
        Vector3 a = request.A;
        Vector3 b = request.B;
        bool clear = collision.IsInLineOfSight(a.X, a.Y, a.Z, b.X, b.Y, b.Z);
        bool clearWithDoodads = models.IsInLineOfSight(map.MapId, a, b, ignoreM2: false);
        var answer = new QueryAnswer
        {
            Id = request.Id,
            Ok = true,
            Kind = QueryKind.LineOfSight,
            Clear = clear,
            ClearWithDoodads = clearWithDoodads,
            DataBacked = models.Enabled,
        };

        if (!clear || !clearWithDoodads)
        {
            answer.HasHit = models.TryGetObjectHit(map.MapId, a, b, 0f, out Vector3 hit);
            answer.Hit = hit;
        }

        return answer;
    }

    private static QueryAnswer AnswerHeight(Map map, in FeedRequest request)
    {
        Vector3 a = request.A;
        if (!PointLoaded(map, a.X, a.Y, out (int X, int Y) missing))
        {
            return NotLoaded(request, missing);
        }

        MapCollision collision = map.Collision;
        ILineOfSight models = collision.LineOfSight;
        float terrain = map.Terrain.GetHeight(a.X, a.Y, a.Z);
        float floor = collision.GetHeight(a.X, a.Y, a.Z);
        float? model = models.Enabled ? models.GetModelHeight(map.MapId, a.X, a.Y, a.Z + ModelProbeOffset, MapCollision.DefaultHeightSearch) : null;
        return new QueryAnswer
        {
            Id = request.Id,
            Ok = true,
            Kind = QueryKind.Height,
            DataBacked = models.Enabled,
            Terrain = terrain > TerrainTile.InvalidHeight ? terrain : null,
            Model = model is { } found && found > TerrainTile.InvalidHeight ? found : null,
            Floor = floor > TerrainTile.InvalidHeight ? floor : null,
            Outdoors = models.Enabled ? collision.IsOutdoors(a.X, a.Y, a.Z) : null,
        };
    }

    /// <summary>
    /// A navigation path from <c>a</c> to <c>b</c> without loading any tile (<see cref="PathOptions.LoadTiles"/> defaults to false). The
    /// start and end grids must be loaded; an unloaded tile along the search stops the detour search at the loaded boundary and reports the
    /// partial answer through <see cref="PathResult.Type"/> (<see cref="PathType.Incomplete"/>).
    /// </summary>
    private static QueryAnswer AnswerPath(Map map, in FeedRequest request)
    {
        if (!PointLoaded(map, request.A.X, request.A.Y, out (int X, int Y) missing)
            || !PointLoaded(map, request.B.X, request.B.Y, out missing))
        {
            return NotLoaded(request, missing);
        }

        MapCollision collision = map.Collision;
        IPathfinder finder = collision.PathfinderForMap;
        PathResult path = collision.FindPath(request.A, request.B);
        int count = Math.Min(path.Points.Count, MapFeedProtocol.MaxResultPoints);
        var points = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            points[i] = path.Points[i];
        }

        return new QueryAnswer
        {
            Id = request.Id,
            Ok = true,
            Kind = QueryKind.Path,
            PathType = path.Type,
            HasPath = path.HasPath,
            Length = path.Length,
            Points = points,
            Cut = count < path.Points.Count,
            DataBacked = finder is IMapAwarePathfinder aware ? aware.HasNavigationData(map.MapId) : finder.Enabled,
        };
    }

    private static QueryAnswer AnswerUnderMap(Map map, in FeedRequest request)
    {
        Vector3 a = request.A;
        if (!PointLoaded(map, a.X, a.Y, out (int X, int Y) missing))
        {
            return NotLoaded(request, missing);
        }

        UnderMapVerdict verdict = map.Collision.ClassifyUnderMap(a.X, a.Y, a.Z);
        return new QueryAnswer
        {
            Id = request.Id,
            Ok = true,
            Kind = QueryKind.UnderMap,
            Verdict = verdict.Kind,
            Depth = verdict.Kind == UnderMapKind.UnderMap ? verdict.DepthYards : 0f,
        };
    }

    private static QueryAnswer NotLoaded(in FeedRequest request, (int X, int Y) grid)
        => QueryAnswer.Refused(
            request,
            MapFeedProtocol.NotLoaded,
            $"grid [{grid.X},{grid.Y}] of map {request.MapId} instance {request.InstanceId} is not loaded; a query never loads one");

    /// <summary>Whether the grid under (x, y) is loaded and so is the terrain tile the terrain code reads there; else the grid to report.</summary>
    private static bool PointLoaded(Map map, float x, float y, out (int X, int Y) missing)
    {
        GridCoord grid = GridDefines.ComputeGridCoord(x, y);
        missing = (grid.X, grid.Y);
        return map.Grids.IsGridLoaded(grid)
               && TerrainTile.TileOf(x, y) is { } tile
               && map.Terrain.IsTileLoaded(tile.X, tile.Y);
    }

    /// <summary>
    /// Whether every grid the segment touches is loaded (with its terrain tile). The grids are found exactly, by clipping the segment
    /// against the rectangle of each grid inside the segment's bounding box (at most 64 x 64 of them), so a corner clipped by a few yards
    /// is not skipped.
    /// </summary>
    private static bool SegmentLoaded(Map map, Vector3 a, Vector3 b, out (int X, int Y) missing)
    {
        GridCoord from = GridDefines.ComputeGridCoord(a.X, a.Y);
        GridCoord to = GridDefines.ComputeGridCoord(b.X, b.Y);
        missing = (from.X, from.Y);
        for (int gx = Math.Min(from.X, to.X); gx <= Math.Max(from.X, to.X); gx++)
        {
            for (int gy = Math.Min(from.Y, to.Y); gy <= Math.Max(from.Y, to.Y); gy++)
            {
                if (!SegmentTouchesGrid(a, b, gx, gy))
                {
                    continue;
                }

                var grid = new GridCoord(gx, gy);
                (int X, int Y) tile = TerrainTile.TileOf(grid);
                if (!map.Grids.IsGridLoaded(grid) || !map.Terrain.IsTileLoaded(tile.X, tile.Y))
                {
                    missing = (gx, gy);
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the segment a-b meets grid (gx, gy). Grid g covers [(g - 32) * size, (g - 31) * size) on each axis
    /// (<see cref="GridDefines.ComputeGridCoord"/> inverted); the rectangle is widened by <see cref="EdgeSlack"/> so a segment along an edge
    /// asks for both grids.
    /// </summary>
    private static bool SegmentTouchesGrid(Vector3 a, Vector3 b, int gx, int gy)
    {
        float t0 = 0f;
        float t1 = 1f;
        return Clip(a.X, b.X - a.X, GridMin(gx), GridMax(gx), ref t0, ref t1)
               && Clip(a.Y, b.Y - a.Y, GridMin(gy), GridMax(gy), ref t0, ref t1);
    }

    private static float GridMin(int g) => ((g - GridDefines.CenterGridId) * GridDefines.SizeOfGrids) - EdgeSlack;

    private static float GridMax(int g) => ((g - GridDefines.CenterGridId + 1) * GridDefines.SizeOfGrids) + EdgeSlack;

    /// <summary>One slab of the Liang-Barsky clip: narrow the parameter interval [t0, t1] to where p + t * d is inside [min, max].</summary>
    private static bool Clip(float p, float d, float min, float max, ref float t0, ref float t1)
    {
        if (MathF.Abs(d) < 1e-6f)
        {
            return p >= min && p <= max;
        }

        float entry = (min - p) / d;
        float exit = (max - p) / d;
        if (entry > exit)
        {
            (entry, exit) = (exit, entry);
        }

        t0 = MathF.Max(t0, entry);
        t1 = MathF.Min(t1, exit);
        return t0 <= t1;
    }
}
