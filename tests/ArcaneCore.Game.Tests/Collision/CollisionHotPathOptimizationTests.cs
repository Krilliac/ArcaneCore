using System.Collections.Concurrent;
using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// Regression, zero-allocation, and concurrent thread-safety tests for the collision hot paths in
/// <c>BihTree</c>, <c>VMapTree</c>, and <c>NavMeshQuery</c>.
/// </summary>
public sealed class CollisionHotPathOptimizationTests
{
    private const uint MapId = 0;

    private struct CountingPointCallback : IPointPrimitiveCallback
    {
        public int Count;
        public int LastPrimitive;

        public void Invoke(int primitive)
        {
            Count++;
            LastPrimitive = primitive;
        }
    }

    private struct NearestBoxRayCallback(IReadOnlyList<(Vector3 Low, Vector3 High)> boxes, Vector3 origin, Vector3 direction) : IRayPrimitiveCallback
    {
        public int HitIndex = -1;

        public bool Invoke(int primitive, ref float maxDistance)
        {
            (Vector3 low, Vector3 high) = boxes[primitive];
            if (!BihTree.ClipToBox(origin, direction, low, high, 0f, maxDistance, out float tMin, out _))
            {
                return false;
            }

            if (tMin < maxDistance)
            {
                maxDistance = tMin;
                HitIndex = primitive;
                return true;
            }

            return false;
        }
    }

    [Fact]
    public void BihTree_StructCallbacks_MatchDelegateOverloads_AndAllocateZeroBytes()
    {
        var rng = new Random(1337);
        var boxes = new List<(Vector3 Low, Vector3 High)>(64);
        for (int i = 0; i < 64; i++)
        {
            var low = new Vector3(rng.NextSingle() * 80f, rng.NextSingle() * 80f, rng.NextSingle() * 80f);
            boxes.Add((low, low + new Vector3(2f + (rng.NextSingle() * 6f), 2f + (rng.NextSingle() * 6f), 2f + (rng.NextSingle() * 6f))));
        }

        BihTree tree = BihTree.Build(boxes, leafSize: 2);
        Vector3 point = boxes[17].Low + new Vector3(0.5f, 0.5f, 0.5f);
        var delegateVisited = new List<int>();
        tree.IntersectPoint(point, delegateVisited.Add);

        var structPoint = new CountingPointCallback();
        tree.IntersectPoint(point, ref structPoint);
        Assert.Equal(delegateVisited.Count, structPoint.Count);
        Assert.True(structPoint.Count >= 1);

        Vector3 rayOrigin = new(-10f, 40f, 40f);
        Vector3 rayDir = Vector3.Normalize(boxes[17].Low - rayOrigin);

        Action window = () =>
        {
            for (int i = 0; i < 200; i++)
            {
                var ptCb = new CountingPointCallback();
                tree.IntersectPoint(point, ref ptCb);
                float maxDist = 200f;
                var rayCb = new NearestBoxRayCallback(boxes, rayOrigin, rayDir);
                tree.IntersectRay(rayOrigin, rayDir, ref maxDist, ref rayCb, stopAtFirstHit: false);
            }
        };

        AllocationProbe.AssertEveryCleanWindowAtMost(window, maxBytes: 0, "BihTree struct ray and point callbacks");
    }

    [Fact]
    public void VMapTree_RayHeightObjectHitAndAreaQueries_MatchExpectedAndAllocateZeroBytesOnHotPaths()
    {
        using var fixture = new VMapFixture();
        WorldModel wall = VMapFixture.Box(new Vector3(-1f, -10f, 0f), new Vector3(1f, 10f, 10f), mogpFlags: ModelAreaInfo.MogpInterior, rootId: 77, groupId: 12);
        WorldModel slab = VMapFixture.Box(new Vector3(-5f, -5f, -0.5f), new Vector3(5f, 5f, 0.5f), mogpFlags: ModelAreaInfo.MogpExterior, rootId: 88, groupId: 15);
        fixture.Place("wall", wall, new Vector3(100f, 100f, 0f));
        fixture.Place("slab", slab, new Vector3(130f, 100f, 10f));
        fixture.Write(MapId);

        var manager = new VMapManager(fixture.Directory);
        Assert.True(manager.LoadTile(MapId, 31, 31));
        VMapTree tree = manager.GetTree(MapId)!;
        Assert.Equal(2, tree.LoadedInstanceCount);

        Vector3 losFrom = VMapFormat.ToInternal(new Vector3(95f, 100f, 2f));
        Vector3 losTo = VMapFormat.ToInternal(new Vector3(105f, 100f, 2f));
        Vector3 clearFrom = VMapFormat.ToInternal(new Vector3(95f, 120f, 2f));
        Vector3 clearTo = VMapFormat.ToInternal(new Vector3(105f, 120f, 2f));
        Vector3 heightQuery = VMapFormat.ToInternal(new Vector3(130f, 100f, 20f));
        Vector3 insideWall = VMapFormat.ToInternal(new Vector3(100f, 100f, 5f));
        Vector3 outsideAll = VMapFormat.ToInternal(new Vector3(160f, 100f, 5f));

        Assert.False(tree.IsInLineOfSight(losFrom, losTo, ignoreM2: true));
        Assert.True(tree.IsInLineOfSight(clearFrom, clearTo, ignoreM2: true));
        Assert.True(tree.TryGetObjectHit(losFrom, losTo, -0.5f, out Vector3 hitPos));
        Assert.True(Vector3.Distance(losFrom, hitPos) < Vector3.Distance(losFrom, losTo));
        Assert.Equal(10.5f, tree.GetHeight(heightQuery, 50f)!.Value, 3);

        Assert.True(tree.TryGetAreaInfo(insideWall, out ModelAreaInfo area));
        Assert.Equal(77, area.RootId);
        Assert.Equal(12, area.GroupId);
        Assert.Equal(0f, area.GroundZ, 3);
        Assert.False(tree.TryGetAreaInfo(outsideAll, out _));

        Action window = () =>
        {
            for (int i = 0; i < 200; i++)
            {
                _ = tree.LoadedInstanceCount;
                tree.IsInLineOfSight(losFrom, losTo, ignoreM2: true);
                tree.TryGetObjectHit(losFrom, losTo, -0.5f, out _);
                tree.GetHeight(heightQuery, 50f);
                tree.TryGetAreaInfo(outsideAll, out _);
            }
        };

        AllocationProbe.AssertEveryCleanWindowAtMost(window, maxBytes: 0, "VMapTree raycast, height, object hit, and area miss hot paths");
    }

    [Fact]
    public void NavMeshQuery_CorridorRaycastAndNearestPoly_ProduceDeterministicResultsAcrossReopenGraphs()
    {
        using var fixture = new NavMeshFixture();
        var west = new CellTile(0, 0, 0, 0, 8, 8, Size: 4)
        {
            LinkedSides = [2],
            Walkable = (i, j) => !(i == 4 && j is >= 1 and <= 5),
        };
        var east = new CellTile(0, 1, 32, 0, 8, 8, Size: 4)
        {
            LinkedSides = [6],
            Walkable = (i, j) => !(i == 3 && j is >= 2 and <= 6),
        };
        fixture.WriteParams(MapId);
        fixture.WriteTile(MapId, 31, 31, west);
        fixture.WriteTile(MapId, 30, 31, east);

        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(MapId, 31, 31));
        Assert.True(nav.LoadTile(MapId, 30, 31));
        NavMesh mesh = nav.GetNavMesh(MapId)!;

        Vector3 startWorld = new(4f, 12f, 0f);
        Vector3 endWorld = new(56f, 20f, 0f);
        Vector3 startRc = NavMeshFormat.ToRecast(startWorld);
        Vector3 endRc = NavMeshFormat.ToRecast(endWorld);

        Assert.True(mesh.TryFindNearestPoly(startRc, NavMeshPathfinder.NearExtents, PathOptions.Default, out NavPolyRef startRef, out Vector3 startOnMesh));
        Assert.True(mesh.TryFindNearestPoly(endRc, NavMeshPathfinder.NearExtents, PathOptions.Default, out NavPolyRef endRef, out Vector3 endOnMesh));

        (var corridor, bool complete) = NavMeshQuery.FindCorridor(mesh, startRef, startOnMesh, endRef, endOnMesh, PathOptions.Default);
        Assert.True(complete);
        Assert.True(corridor.Count > 4);
        Assert.Equal(startRef, corridor[0].Poly);
        Assert.Equal(endRef, corridor[^1].Poly);

        List<Vector3>? worldPath = NavMeshQuery.FindWorldPath(mesh, startWorld, startRef, startOnMesh, endRef, endOnMesh, endRc, PathOptions.Default, out bool worldComplete);
        Assert.True(worldComplete);
        Assert.NotNull(worldPath);
        Assert.True(worldPath.Count >= 3);
        Assert.Equal(startWorld, worldPath[0]);
        Assert.True(Vector3.Distance(endWorld, worldPath[^1]) < 0.05f);

        NavMeshQuery.RayResult blockedRay = NavMeshQuery.Raycast(mesh, startRef, startOnMesh, endOnMesh, PathOptions.Default);
        Assert.True(blockedRay.Hit);
        Assert.InRange(blockedRay.Fraction, 0.01f, 0.99f);
    }

    [Fact]
    public void ConcurrentThreads_OnSharedBihVMapAndNavMesh_ProduceIdenticalResultsWithoutRaceOrCorruption()
    {
        using var vmapFixture = new VMapFixture();
        vmapFixture.Place("wall1", VMapFixture.Box(new Vector3(-1f, -10f, 0f), new Vector3(1f, 10f, 10f)), new Vector3(100f, 100f, 0f));
        vmapFixture.Place("slab1", VMapFixture.Box(new Vector3(-6f, -6f, -0.5f), new Vector3(6f, 6f, 0.5f)), new Vector3(120f, 100f, 8f));
        vmapFixture.Write(MapId);
        var vmaps = new VMapManager(vmapFixture.Directory);
        Assert.True(vmaps.LoadTile(MapId, 31, 31));
        VMapTree vmapTree = vmaps.GetTree(MapId)!;

        using var mmapFixture = new NavMeshFixture();
        mmapFixture.WriteParams(MapId);
        mmapFixture.WriteTile(MapId, 31, 31, new CellTile(0, 0, 0, 0, 6, 6, Size: 4) { LinkedSides = [2], Walkable = (i, j) => !(i == 3 && j < 4) });
        mmapFixture.WriteTile(MapId, 30, 31, new CellTile(0, 1, 24, 0, 6, 6, Size: 4) { LinkedSides = [6], Walkable = (i, j) => !(i == 2 && j > 1) });
        var nav = new NavMeshPathfinder(mmapFixture.Directory);
        Assert.True(nav.LoadTile(MapId, 31, 31));
        Assert.True(nav.LoadTile(MapId, 30, 31));
        NavMesh mesh = nav.GetNavMesh(MapId)!;

        Vector3 pathStart = new(4f, 8f, 0f);
        Vector3 pathEnd = new(44f, 16f, 0f);
        PathResult expectedPath = nav.FindPath(MapId, pathStart, pathEnd);
        Assert.Equal(PathType.Normal, expectedPath.Type);

        Vector3 startRc = NavMeshFormat.ToRecast(pathStart);
        Vector3 endRc = NavMeshFormat.ToRecast(pathEnd);
        Assert.True(mesh.TryFindNearestPoly(startRc, NavMeshPathfinder.NearExtents, PathOptions.Default, out NavPolyRef startPoly, out Vector3 startOn));

        Vector3 vmapFrom = VMapFormat.ToInternal(new Vector3(95f, 100f, 3f));
        Vector3 vmapTo = VMapFormat.ToInternal(new Vector3(105f, 100f, 3f));
        Vector3 vmapSlabPt = VMapFormat.ToInternal(new Vector3(120f, 100f, 20f));

        var errors = new ConcurrentQueue<string>();
        using var barrier = new Barrier(8);
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, worker =>
        {
            var rng = new Random(1000 + worker);
            barrier.SignalAndWait();
            for (int iter = 0; iter < 60; iter++)
            {
                if (vmapTree.IsInLineOfSight(vmapFrom, vmapTo, ignoreM2: true))
                {
                    errors.Enqueue("Expected wall to block LOS");
                }

                if (vmapTree.GetHeight(vmapSlabPt, 50f) is not { } h || MathF.Abs(h - 8.5f) > 1e-3f)
                {
                    errors.Enqueue("Unexpected slab height");
                }

                PathResult shared = nav.FindPathShared(MapId, pathStart, pathEnd, PathOptions.Default);
                if (shared.Type != expectedPath.Type || shared.Points.Count != expectedPath.Points.Count)
                {
                    errors.Enqueue($"Unexpected shared path: {shared.Type} ({shared.Points.Count} pts)");
                }

                NavMeshQuery.RayResult ray = NavMeshQuery.Raycast(mesh, startPoly, startOn, endRc, PathOptions.Default);
                if (!ray.Hit)
                {
                    errors.Enqueue("Expected raycast to hit obstacle");
                }

                if (!NavMeshQuery.TryRandomPointAround(mesh, startPoly, startOn, 5f, PathOptions.Default, rng, out _))
                {
                    errors.Enqueue("Expected random point query to succeed");
                }
            }
        });

        Assert.Empty(errors);
    }
}
