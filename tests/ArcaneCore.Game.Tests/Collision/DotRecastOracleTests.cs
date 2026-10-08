using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Terrain;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// Diagnostic comparison against the vmangos Detour-v7 tiles. The source format and search
/// contract are from vmangos contrib/mmap/src/MapBuilder.cpp:462, src/game/Maps/MoveMap.cpp:86-92,
/// and src/game/Maps/PathFinder.cpp (PathInfo::createFilter, PathInfo::BuildPolyPath).
/// DotRecast is a test-only independent implementation, not the production pathfinder.
/// </summary>
public sealed class DotRecastOracleTests(ITestOutputHelper output)
{
    private static readonly (string Name, uint Map, Vector3[] Anchors)[] Regions =
    [
        ("Coldridge", 0, [new(-6240.32f, 331.033f, 382.758f)]),
        ("Kharanos-switchback", 0, [new(-5165, -876, 507), new(-5384, -716, 397)]),
        ("Goldshire-inn", 0, [new(-9462.66f, 16.1915f, 57.0459f)]),
        ("Deathknell-crypt", 0, [new(1676.35f, 1677.45f, 121.67f)]),
        ("Valley-of-Trials", 1, [new(-618.518f, -4251.67f, 38.718f)]),
        ("Shadowglen", 1, [new(10311.3f, 831.463f, 1326.41f)]),
    ];

    [Fact]
    public void SyntheticWall_ExercisesBothReadersAndQueriesWithoutClientData()
    {
        byte[] tileBytes = new CellTile(0, 0, 0, 0, 4, 4, Size: 5)
        {
            Walkable = (i, j) => !(i == 2 && j < 3),
        }.Build();
        var arc = new NavMesh(0, new NavMeshParams(Vector3.Zero, 533.3333f, 533.3333f, 1, 0));
        Assert.True(arc.AddTile(31, 31, NavMeshTile.Parse(tileBytes)));
        var dot = new DtNavMesh();
        Assert.True(dot.Init(new DtNavMeshParams
        {
            orig = RcVec3f.Zero, tileWidth = 533.3333f, tileHeight = 533.3333f,
            maxTiles = 1, maxPolys = 1 << 16,
        }, 6).Succeeded());
        Assert.True(dot.AddTile(new DtMeshDataReader().Read(new RcByteBuffer(tileBytes), 6, is32Bit: false),
            0, 0, out _).Succeeded());

        Vector3 start = NavMeshFormat.ToRecast(new Vector3(7, 5, 0));
        Vector3 end = NavMeshFormat.ToRecast(new Vector3(17, 5, 0));
        Assert.True(arc.TryFindNearestPoly(start, NavMeshPathfinder.NearExtents, PathOptions.Default,
            out NavPolyRef arcStart, out Vector3 arcStartOn));
        Assert.True(arc.TryFindNearestPoly(end, NavMeshPathfinder.NearExtents, PathOptions.Default,
            out NavPolyRef arcEnd, out Vector3 arcEndOn));
        (var corridor, bool complete) = NavMeshQuery.FindCorridor(arc, arcStart, arcStartOn, arcEnd, arcEndOn,
            PathOptions.Default);
        Assert.True(complete);
        List<Vector3> arcCorners = NavMeshQuery.StringPull(arcStartOn,
            [.. corridor.Skip(1).Select(p => (p.Left, p.Right))], arcEndOn);

        var dotQuery = new DtNavMeshQuery(dot);
        var filter = new DtQueryDefaultFilter();
        filter.SetIncludeFlags((int)PathOptions.Default.EffectiveIncludeFlags);
        (long dotStart, RcVec3f dotStartOn) = Locate(dotQuery, start, filter);
        (long dotEnd, RcVec3f dotEndOn) = Locate(dotQuery, end, filter);
        Assert.NotEqual(0, dotStart);
        Assert.NotEqual(0, dotEnd);
        long[] path = new long[32];
        Assert.True(dotQuery.FindPath(dotStart, dotEnd, dotStartOn, dotEndOn, filter,
            path, out int pathCount, path.Length).Succeeded());
        Assert.Equal(dotEnd, path[pathCount - 1]);
        DtStraightPath[] dotCorners = new DtStraightPath[32];
        Assert.True(dotQuery.FindStraightPath(dotStartOn, dotEndOn, path, pathCount,
            dotCorners, out int cornerCount, dotCorners.Length, 0).Succeeded());
        // From the polygons DotRecast picked (the start sits on a cell edge, a nearest-polygon tie),
        // both searches must choose the same corridor: Detour's node rules decide between the
        // equal-length routes around the wall. One tile, so the polygon indices name the corridor.
        NavMeshTile arcTile = arcStart.Tile;
        (var sameStartCorridor, bool sameStartComplete) = NavMeshQuery.FindCorridor(arc,
            new NavPolyRef(arcTile, PolyIndex(dot, dotStart)), V3(dotStartOn),
            new NavPolyRef(arcTile, PolyIndex(dot, dotEnd)), V3(dotEndOn), PathOptions.Default);
        Assert.True(sameStartComplete);
        Assert.Equal(path.Take(pathCount).Select(r => PolyIndex(dot, r)), sameStartCorridor.Select(p => p.Poly.Poly));
        Assert.True(arcCorners.Count > 2);
        Assert.True(cornerCount > 2);
        Assert.True(Vector3.Distance(arcCorners[^1], V3(dotCorners[cornerCount - 1].pos)) < 0.01f);
    }

    /// <summary>
    /// Seeded random 6x6 cell mazes: from the same start and end polygons, NavMeshQuery must pick
    /// DotRecast's corridor. Before NavMeshQuery kept Detour's node rules (per-side nodes, first-visit
    /// positions, reopening closed nodes) 53 of the 472 connected mazes here chose a different corridor.
    /// </summary>
    [Fact]
    public void SyntheticMazes_CorridorMatchesDetourNodeRules()
    {
        const int cells = 6;
        const float size = 5;
        var rng = new Random(1234);
        var mismatches = new List<string>();
        int compared = 0;
        for (int trial = 0; trial < 600; trial++)
        {
            bool[,] walkable = new bool[cells, cells];
            var open = new List<(int I, int J)>();
            for (int i = 0; i < cells; i++)
            for (int j = 0; j < cells; j++)
            {
                walkable[i, j] = rng.NextDouble() > 0.3;
                if (walkable[i, j]) open.Add((i, j));
            }

            if (open.Count < 4) continue;
            (int I, int J) a = open[rng.Next(open.Count)], b = open[rng.Next(open.Count)];
            byte[] bytes = new CellTile(0, 0, 0, 0, cells, cells, Size: size) { Walkable = (i, j) => walkable[i, j] }.Build();
            var arc = new NavMesh(0, new NavMeshParams(Vector3.Zero, 533.3333f, 533.3333f, 1, 0));
            Assert.True(arc.AddTile(31, 31, NavMeshTile.Parse(bytes)));
            var dot = new DtNavMesh();
            Assert.True(dot.Init(new DtNavMeshParams
            {
                orig = RcVec3f.Zero, tileWidth = 533.3333f, tileHeight = 533.3333f, maxTiles = 1, maxPolys = 1 << 16,
            }, NavMeshFormat.MaxVertsPerPoly).Succeeded());
            Assert.True(dot.AddTile(new DtMeshDataReader().Read(new RcByteBuffer(bytes), 6, is32Bit: false), 0, 0, out _).Succeeded());
            var query = new DtNavMeshQuery(dot);
            var filter = new DtQueryDefaultFilter();
            filter.SetIncludeFlags((int)PathOptions.Default.EffectiveIncludeFlags);
            Vector3 start = NavMeshFormat.ToRecast(new Vector3((a.I * size) + 1.3f, (a.J * size) + 2.1f, 0));
            Vector3 end = NavMeshFormat.ToRecast(new Vector3((b.I * size) + 3.7f, (b.J * size) + 1.4f, 0));
            query.FindNearestPoly(Rc(start), new RcVec3f(1, 2, 1), filter, out long dotStart, out RcVec3f dotStartOn, out _);
            query.FindNearestPoly(Rc(end), new RcVec3f(1, 2, 1), filter, out long dotEnd, out RcVec3f dotEndOn, out _);
            Assert.NotEqual(0, dotStart);
            Assert.NotEqual(0, dotEnd);
            long[] path = new long[64];
            Assert.True(query.FindPath(dotStart, dotEnd, dotStartOn, dotEndOn, filter, path, out int pathCount, path.Length).Succeeded());
            if (path[pathCount - 1] != dotEnd) continue; // the two cells are not connected

            compared++;
            NavMeshTile tile = Assert.IsType<NavMeshTile>(arc.GetTile(0, 0));
            (var corridor, bool complete) = NavMeshQuery.FindCorridor(arc,
                new NavPolyRef(tile, PolyIndex(dot, dotStart)), V3(dotStartOn),
                new NavPolyRef(tile, PolyIndex(dot, dotEnd)), V3(dotEndOn), PathOptions.Default);
            int[] expected = [.. path.Take(pathCount).Select(r => PolyIndex(dot, r))];
            int[] actual = [.. corridor.Select(p => p.Poly.Poly)];
            if (!complete || !expected.SequenceEqual(actual))
                mismatches.Add($"trial {trial}: DotRecast [{string.Join(",", expected)}], NavMeshQuery [{string.Join(",", actual)}]");
        }

        Assert.True(compared >= 400, $"only {compared} connected mazes");
        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} of {compared} corridors differ: {string.Join("; ", mismatches.Take(10))}");
    }

    [RealTerrainFact]
    public void RealVmangosTiles_CompareCorridorsAndStraightPaths()
    {
        string root = Environment.GetEnvironmentVariable(RealTerrainFactAttribute.Variable)!;
        string directory = Path.Combine(root, "mmaps");
        var results = new List<CaseResult>();
        foreach (var region in Regions)
        {
            CompareRegion(directory, region.Name, region.Map, region.Anchors, results);
        }

        string? reportDir = Environment.GetEnvironmentVariable("ARCANECORE_ORACLE_REPORT_DIR");
        if (!string.IsNullOrWhiteSpace(reportDir))
        {
            Directory.CreateDirectory(reportDir);
            var jsonOptions = new JsonSerializerOptions { IncludeFields = true };
            File.WriteAllLines(Path.Combine(reportDir, "cases.jsonl"), results.Select(r => JsonSerializer.Serialize(r, jsonOptions)));
            File.WriteAllLines(Path.Combine(reportDir, "disagreements.jsonl"), results.Where(r => r.Category != "Agree").Select(r => JsonSerializer.Serialize(r, jsonOptions)));
        }

        foreach (var group in results.GroupBy(r => r.Category).OrderBy(g => g.Key))
        {
            output.WriteLine($"{group.Key}: {group.Count()}");
        }

        foreach (var region in results.GroupBy(r => r.Region))
        {
            output.WriteLine($"{region.Key}: {region.Count()} pairs; ArcaneCore query {region.Average(r => r.ArcMicroseconds):F1} us; NavMeshPathfinder {region.Average(r => r.PathfinderMicroseconds):F1} us; DotRecast query {region.Average(r => r.DotMicroseconds):F1} us");
        }

        // Timing on equal work: DotRecast cannot be capped at 2048 nodes, so compare only the
        // queries it finished within that budget.
        var sameBudget = results.Where(r => r.DotNodes <= PathOptions.DefaultMaxSearchNodes).ToList();
        output.WriteLine($"Within the 2048-node budget ({sameBudget.Count} pairs): ArcaneCore {Stats(sameBudget.Select(r => r.ArcMicroseconds))}; DotRecast {Stats(sameBudget.Select(r => r.DotMicroseconds))}");
        output.WriteLine($"All pairs ({results.Count}): ArcaneCore {Stats(results.Select(r => r.ArcMicroseconds))}; NavMeshPathfinder {Stats(results.Select(r => r.PathfinderMicroseconds))}; DotRecast {Stats(results.Select(r => r.DotMicroseconds))}");

        Assert.True(results.Count >= 300, $"Expected at least 300 comparable pairs, got {results.Count}");
        Assert.All(Regions, region => Assert.Contains(results, r => r.Region == region.Name));
        // NavMeshQuery follows Detour's findPath node rules, so both engines should pick the same
        // corridor almost always (254 of 256 complete pairs on 2026-10-08; 53 of 256 before the fix).
        var complete = results.Where(r => r.ArcComplete && r.DotComplete).ToList();
        int sameCorridor = complete.Count(r => !r.CorridorDiffers);
        Assert.True(sameCorridor >= complete.Count * 0.95,
            $"Only {sameCorridor} of {complete.Count} complete corridors match DotRecast");
        Assert.DoesNotContain(results, r => r.Category is "OneSidePartial" or "OneSideNoPath");
    }

    private static void CompareRegion(string directory, string name, uint mapId, Vector3[] anchors, List<CaseResult> results)
    {
        NavMeshParams arcParams = NavMeshParams.Parse(File.ReadAllBytes(Path.Combine(directory, NavMeshFormat.ParamsFileName(mapId))));
        var dotParams = new DtNavMeshParams
        {
            orig = Rc(arcParams.Origin), tileWidth = arcParams.TileWidth, tileHeight = arcParams.TileHeight,
            maxTiles = arcParams.MaxTiles,
            // vmangos writes zero with DT_POLYREF64; DotRecast requires a real budget.
            maxPolys = 1 << 16,
        };
        var dot = new DtNavMesh();
        Assert.True(dot.Init(dotParams, NavMeshFormat.MaxVertsPerPoly).Succeeded());
        var dotQuery = new CountingDotQuery(dot);
        var arcPathfinder = new NavMeshPathfinder(directory);
        NavMesh arc = Assert.IsType<NavMesh>(arcPathfinder.GetNavMesh(mapId));
        var loaded = new HashSet<(int X, int Y)>();
        foreach (Vector3 anchor in anchors)
        {
            (int cx, int cy) = TerrainTile.TileOf(anchor.X, anchor.Y)!.Value;
            for (int x = cx - 1; x <= cx + 1; x++)
            for (int y = cy - 1; y <= cy + 1; y++)
            {
                if (!loaded.Add((x, y))) continue;
                string tilePath = Path.Combine(directory, NavMeshFormat.TileFileName(mapId, x, y));
                if (!File.Exists(tilePath)) continue;
                byte[] file = File.ReadAllBytes(tilePath);
                Assert.True(arcPathfinder.LoadTile(mapId, x, y), tilePath);
                byte[] data = file.AsSpan(MmapTileHeader.ByteSize).ToArray();
                var tile = new DtMeshDataReader().Read(new RcByteBuffer(data), 6, is32Bit: false);
                Assert.True(dot.AddTile(tile, 0, 0, out _).Succeeded(), tilePath);
            }
        }

        // Pick actual polygon centres rather than inventing content positions. Both engines get
        // precisely the same world points and the same loaded tile set.
        var candidates = new List<Vector3>();
        foreach ((int x, int y) in loaded.OrderBy(t => t.X).ThenBy(t => t.Y))
        {
            // A terrain tile name has the complementary Detour coordinate; look up via the
            // anchor's region by the polygon centres instead of assuming name == tile header.
            string tilePath = Path.Combine(directory, NavMeshFormat.TileFileName(mapId, x, y));
            if (!File.Exists(tilePath)) continue;
            NavMeshTile tile = NavMeshTile.ParseFile(File.ReadAllBytes(tilePath));
            for (int p = 0; p < tile.Polys.Length; p++)
            {
                if (!tile.Passes(p, PathOptions.Default.EffectiveIncludeFlags, PathOptions.Default.ExcludeFlags)) continue;
                Vector3 world = NavMeshFormat.ToWorld(tile.Center(p));
                if (anchors.Any(a => Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(world.X, world.Y)) < 250))
                    candidates.Add(world);
            }
        }

        Assert.True(candidates.Count >= 20, $"{name}: only {candidates.Count} nearby polygons");
        // Spread positions across the region, while keeping a reproducible fixed count.
        Vector3[] sampled = Enumerable.Range(0, 20).Select(i => candidates[(int)((long)i * (candidates.Count - 1) / 19)]).ToArray();
        var pairs = new List<(Vector3 Start, Vector3 End)>();
        foreach (Vector3 anchor in anchors)
            pairs.Add((anchor, sampled.OrderBy(p => Vector3.DistanceSquared(p, anchor)).First()));
        for (int i = 0; i < sampled.Length; i++)
        for (int offset = 1; offset <= 3; offset++)
            pairs.Add((sampled[i], sampled[(i + offset * 5) % sampled.Length]));
        if (name == "Kharanos-switchback") pairs.Add((anchors[0], anchors[1]));

        var filter = new DtQueryDefaultFilter();
        filter.SetIncludeFlags((int)PathOptions.Default.EffectiveIncludeFlags);
        filter.SetExcludeFlags((int)PathOptions.Default.ExcludeFlags);
        // One untimed query first, so neither engine's first timed call pays for JIT compilation.
        Compare(name, mapId, -1, pairs[0].Start, pairs[0].End, arc, arcPathfinder, dot, dotQuery, filter);
        foreach ((Vector3 start, Vector3 end) in pairs)
        {
            results.Add(Compare(name, mapId, results.Count(r => r.Region == name), start, end,
                arc, arcPathfinder, dot, dotQuery, filter));
        }
    }

    private static CaseResult Compare(string region, uint mapId, int caseIndex, Vector3 start, Vector3 end,
        NavMesh arc, NavMeshPathfinder pathfinder, DtNavMesh dot, CountingDotQuery dotQuery, DtQueryDefaultFilter filter)
    {
        Vector3 sr = NavMeshFormat.ToRecast(start), er = NavMeshFormat.ToRecast(end);
        var options = PathOptions.Default;
        var arcClock = Stopwatch.StartNew();
        bool arcStart = arc.TryFindNearestPoly(sr, NavMeshPathfinder.NearExtents, options, out NavPolyRef asp, out Vector3 asOn);
        bool arcEnd = arc.TryFindNearestPoly(er, NavMeshPathfinder.NearExtents, options, out NavPolyRef aep, out Vector3 aeOn);
        if (!arcStart) arcStart = arc.TryFindNearestPoly(sr, NavMeshPathfinder.FarExtents, options, out asp, out asOn);
        if (!arcEnd) arcEnd = arc.TryFindNearestPoly(er, NavMeshPathfinder.FarExtents, options, out aep, out aeOn);
        List<(NavPolyRef Poly, Vector3 Left, Vector3 Right)>? corridor = null;
        bool arcComplete = false;
        List<Vector3>? arcStraight = null;
        if (arcStart && arcEnd)
        {
            (corridor, arcComplete) = NavMeshQuery.FindCorridor(arc, asp, asOn, aep, aeOn, options);
            Vector3 goal = arcComplete ? aeOn : corridor[^1].Poly.Tile.ClosestPointOnPoly(corridor[^1].Poly.Poly, er);
            arcStraight = NavMeshQuery.StringPull(asOn, [.. corridor.Skip(1).Select(c => (c.Left, c.Right))], goal);
        }
        arcClock.Stop();

        // Diagnose whether a partial answer is caused by ArcaneCore's vmangos-sized 2048-node
        // search budget. Keep this separate from the production-budget timing above.
        bool? arcExpandedComplete = null;
        if (arcStart && arcEnd && !arcComplete)
        {
            (_, bool expanded) = NavMeshQuery.FindCorridor(arc, asp, asOn, aep, aeOn,
                options with { MaxSearchNodes = 16384 });
            arcExpandedComplete = expanded;
        }

        var dotClock = Stopwatch.StartNew();
        (long ds, RcVec3f dsp) = Locate(dotQuery, sr, filter);
        (long de, RcVec3f dep) = Locate(dotQuery, er, filter);
        long[] dotPath = new long[512];
        int pathCount = 0, straightCount = 0;
        DtStatus status = default;
        DtStraightPath[] straight = new DtStraightPath[256];
        if (ds != 0 && de != 0)
        {
            status = dotQuery.FindPath(ds, de, dsp, dep, filter, dotPath, out pathCount, dotPath.Length);
            if (status.Succeeded() && pathCount > 0)
            {
                RcVec3f goal = dep;
                if (dotPath[pathCount - 1] != de)
                    dotQuery.ClosestPointOnPoly(dotPath[pathCount - 1], Rc(er), out goal, out _);
                dotQuery.FindStraightPath(dsp, goal, dotPath, pathCount, straight, out straightCount, straight.Length, 0);
            }
        }
        dotClock.Stop();

        var pathfinderClock = Stopwatch.StartNew();
        PathResult production = pathfinder.FindPath(mapId, start, end);
        pathfinderClock.Stop();

        string[] arcKeys = corridor?.Select(p => $"{p.Poly.Tile.X},{p.Poly.Tile.Y},{p.Poly.Poly}").ToArray() ?? [];
        string[] dotKeys = dotPath.Take(pathCount).Select(r => Key(dot, r)).ToArray();
        Vector3[] arcPoints = arcStraight?.Select(NavMeshFormat.ToWorld).ToArray() ?? [];
        Vector3[] dotPoints = straight.Take(straightCount).Select(p => NavMeshFormat.ToWorld(V3(p.pos))).ToArray();
        bool dotComplete = pathCount > 0 && dotPath[pathCount - 1] == de;
        bool corridorDiffers = !arcKeys.SequenceEqual(dotKeys);
        bool straightDiffers = !SamePath(arcPoints, dotPoints);
        // DotRecast's node pool is unbounded while ArcaneCore (and vmangos, MoveMap.cpp:350) stop
        // allocating at 2048 nodes, so a partial ArcaneCore answer where DotRecast needed more
        // nodes is the shared budget, not a disagreement. Two partial answers mean the goal is not
        // reachable on the loaded tiles; their best-so-far corridors are not comparable.
        string category = !arcStart || !arcEnd || ds == 0 || de == 0 || pathCount == 0
            ? "OneSideNoPath"
            : !arcComplete && !dotComplete ? "BothPartial"
            : !arcComplete && dotQuery.NodeCount > options.MaxSearchNodes ? "NodeBudgetPartial"
            : arcComplete != dotComplete ? "OneSidePartial"
            : corridorDiffers ? "PolygonCorridorDiffers"
            : straightDiffers ? "StraightPathDiffers"
            : "Agree";
        return new CaseResult(region, caseIndex, mapId, start, end, category, arcComplete, arcExpandedComplete, dotComplete,
            corridorDiffers, straightDiffers, arcKeys, dotKeys, arcPoints, dotPoints, status.Value,
            dotQuery.NodeCount, production.Type.ToString(), production.Points.Count,
            arcClock.Elapsed.TotalMicroseconds, dotClock.Elapsed.TotalMicroseconds,
            pathfinderClock.Elapsed.TotalMicroseconds);
    }

    private static (long Ref, RcVec3f Point) Locate(DtNavMeshQuery query, Vector3 point, DtQueryDefaultFilter filter)
    {
        query.FindNearestPoly(Rc(point), Rc(NavMeshPathfinder.NearExtents), filter, out long reference, out RcVec3f nearest, out _);
        if (reference == 0)
            query.FindNearestPoly(Rc(point), Rc(NavMeshPathfinder.FarExtents), filter, out reference, out nearest, out _);
        return (reference, nearest);
    }

    private static int PolyIndex(DtNavMesh mesh, long reference)
    {
        Assert.True(mesh.GetTileAndPolyByRef(reference, out _, out DtPoly poly).Succeeded());
        return poly.index;
    }

    private static string Key(DtNavMesh mesh, long reference)
    {
        DtStatus status = mesh.GetTileAndPolyByRef(reference, out DtMeshTile tile, out DtPoly poly);
        return status.Succeeded() ? $"{tile.data.header.x},{tile.data.header.y},{poly.index}" : $"invalid:{reference}";
    }

    private static string Stats(IEnumerable<double> values)
    {
        double[] sorted = [.. values.Order()];
        return sorted.Length == 0
            ? "n/a"
            : $"mean {sorted.Average():F0} us, median {sorted[sorted.Length / 2]:F0} us, p95 {sorted[(int)(sorted.Length * 0.95)]:F0} us";
    }

    private static bool SamePath(Vector3[] a, Vector3[] b)
        => a.Length == b.Length && a.Zip(b).All(pair => Vector3.Distance(pair.First, pair.Second) <= 0.5f);

    private static RcVec3f Rc(Vector3 v) => new(v.X, v.Y, v.Z);
    private static Vector3 V3(RcVec3f v) => new(v.X, v.Y, v.Z);

    private sealed record CaseResult(string Region, int CaseIndex, uint Map, Vector3 Start, Vector3 End, string Category,
        bool ArcComplete, bool? ArcExpandedComplete, bool DotComplete, bool CorridorDiffers, bool StraightDiffers,
        string[] ArcCorridor, string[] DotCorridor,
        Vector3[] ArcStraight, Vector3[] DotStraight, uint DotStatus, int DotNodes,
        string PathfinderType, int PathfinderPoints,
        double ArcMicroseconds, double DotMicroseconds, double PathfinderMicroseconds);

    private sealed class CountingDotQuery(DtNavMesh mesh) : DtNavMeshQuery(mesh)
    {
        public int NodeCount => m_nodePool.GetNodeCount();
    }
}
