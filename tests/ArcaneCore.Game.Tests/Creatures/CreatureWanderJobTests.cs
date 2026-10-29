using System.Collections.Concurrent;
using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.TestSupport;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// A wander leg's random-point pick and path search as ONE job on the path-job pool (<c>Creatures:Movement:PathJobsWander</c>). The
/// scenes run over the real navmesh code on a synthetic 40 x 40 yd tile (<see cref="NavMeshFixture"/>) behind a wrapper that can hold
/// every shared (worker-side) query closed, so a test decides when a pending job finishes.
/// </summary>
public sealed class CreatureWanderJobTests
{
    private const uint MapId = 0;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// What changed while the job was pending. There is no real map-change case: a creature belongs to its map's <see cref="CreatureMapSystem"/>
    /// and this kit has no map-transfer API for creatures (only despawn and a new spawn), which the Despawned case already covers; the
    /// relocation case moves the start 3 yd, past the 0.5 yd start tolerance, which is the other half of "the creature is not where the job started".
    /// </summary>
    public enum StaleCase
    {
        Killed,
        Despawned,
        InCombat,
        Evading,
        RelocatedThreeYards, // a real map change is too heavy in this kit: the start moves 3 yd (more than the 0.5 yd start tolerance)
    }

    /// <summary>The real navmesh behind every service interface the map and the pool use; shared queries wait for <see cref="Gate"/> and are recorded.</summary>
    private sealed class GatedMesh(NavMeshPathfinder inner) : IMapAwarePathfinder, ISharedPathSearch, IMeshPointQuery, ISharedMeshPointQuery, IMeshFloorQuery, ISharedMeshFloorQuery
    {
        private int _syncPointCalls;
        private int _syncPathCalls;
        private int _pickEntered;
        private int _pickReturned;
        private int _pathReturned;

        public ManualResetEventSlim Gate { get; } = new(true);

        public ConcurrentQueue<int> SharedPointThreads { get; } = [];

        public ConcurrentQueue<int> SharedPathThreads { get; } = [];

        /// <summary>The end point of every shared path search, in the order they were answered.</summary>
        public ConcurrentQueue<Vector3> SharedPathEnds { get; } = [];

        public int SyncPointCalls => Volatile.Read(ref _syncPointCalls);

        public int SyncPathCalls => Volatile.Read(ref _syncPathCalls);

        /// <summary>Shared point picks a worker has entered (they may still be waiting at the gate).</summary>
        public int PickEntered => Volatile.Read(ref _pickEntered);

        public int PickReturned => Volatile.Read(ref _pickReturned);

        /// <summary>The point each shared pick returned, keyed by the order the picks were entered (0 = the first job a worker took).</summary>
        public ConcurrentDictionary<int, Vector3> Picks { get; } = new();

        public int PathReturned => Volatile.Read(ref _pathReturned);

        public bool Enabled => inner.Enabled;

        public bool HasNavigationData(uint mapId) => inner.HasNavigationData(mapId);

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Interlocked.Increment(ref _syncPathCalls);
            return inner.FindPath(mapId, start, end, options);
        }

        public bool CanSearchShared(uint mapId) => inner.CanSearchShared(mapId);

        public PathResult FindPathShared(uint mapId, Vector3 start, Vector3 end, PathOptions options)
        {
            Gate.Wait(Wait);
            SharedPathThreads.Enqueue(Environment.CurrentManagedThreadId);
            PathResult result = inner.FindPathShared(mapId, start, end, options);
            SharedPathEnds.Enqueue(end);
            Interlocked.Increment(ref _pathReturned);
            return result;
        }

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        {
            Interlocked.Increment(ref _syncPointCalls);
            return inner.TryGetRandomPointAround(mapId, center, radius, random, options, out point);
        }

        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => inner.Raycast(mapId, start, end, options);

        public bool? TryGetRandomPointAroundShared(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        {
            int index = Interlocked.Increment(ref _pickEntered) - 1;
            Gate.Wait(Wait);
            SharedPointThreads.Enqueue(Environment.CurrentManagedThreadId);
            bool? answer = inner.TryGetRandomPointAroundShared(mapId, center, radius, random, options, out point);
            if (answer == true)
            {
                Picks[index] = point;
            }

            Interlocked.Increment(ref _pickReturned);
            return answer;
        }

        public MeshRaycastResult? RaycastShared(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => inner.RaycastShared(mapId, start, end, options);

        public bool TryGetMeshFloor(uint mapId, float x, float y, float z, float below, float above, out float height) => inner.TryGetMeshFloor(mapId, x, y, z, below, above, out height);

        public bool? TryGetMeshFloorShared(uint mapId, float x, float y, float z, float below, float above, out float height) => inner.TryGetMeshFloorShared(mapId, x, y, z, below, above, out height);
    }

    /// <summary>One 40 x 40 yd flat tile with N wolves wandering a 6 yd disc on it and a player in a corner.</summary>
    private sealed class Scene : IDisposable
    {
        private readonly NavMeshFixture _fixture = new();

        public Scene(int wanderers, int seed = 1, Action<CreatureOptions>? configure = null)
        {
            _fixture.WriteParams(MapId);
            _fixture.WriteTile(MapId, 31, 31, new CellTile(0, 0, 0, 0, 20, 20, Size: 2));
            Nav = new NavMeshPathfinder(_fixture.Directory);
            Assert.True(Nav.LoadTile(MapId, 31, 31));
            Mesh = new GatedMesh(Nav);

            var spawns = new List<CreatureSpawn>();
            for (int i = 0; i < wanderers; i++)
            {
                spawns.Add(Spawn((uint)(i + 1), WolfEntry, 10 + ((i % 3) * 10), 12 + ((i / 3) * 10), z: 0, movementType: 1, wander: 6));
            }

            World = TestWorld.CreateRuntime();
            Map = World.GetMap(MapId);
            var options = new CreatureOptions();
            options.Movement.PathJobs = true;
            configure?.Invoke(options);
            System = new CreatureMapSystem(Map, Content([Template()], spawns), options, random: new Random(seed), aiServices: new CreatureAiServices());
            Map.AddUpdater(System);
            WorldCollision.Of(World).Install(pathfinder: Mesh);
            Pool = new NavJobSystem(threads: 2);
            System.PathJobs = Pool;
            (Player, _) = AddPlayer(World, 1, 2, 2);
            Wolves = [.. System.Creatures.OrderBy(c => c.Guid.Counter)];
            Assert.Equal(wanderers, Wolves.Count);
        }

        public NavMeshPathfinder Nav { get; }

        public GatedMesh Mesh { get; }

        public WorldRuntime World { get; }

        public Map Map { get; }

        public CreatureMapSystem System { get; }

        public NavJobSystem Pool { get; }

        public Player Player { get; }

        public IReadOnlyList<Creature> Wolves { get; }

        public void Tick(int ticks, uint ms = 100)
        {
            for (int i = 0; i < ticks; i++)
            {
                World.RunTick(ms);
            }
        }

        /// <summary>Ticks of 100 ms until <paramref name="done"/> (at most <paramref name="max"/> of them); true when it became true.</summary>
        public bool TickUntil(Func<bool> done, int max)
        {
            for (int i = 0; i < max && !done(); i++)
            {
                World.RunTick(100);
            }

            return done();
        }

        public void Dispose()
        {
            Mesh.Gate.Set();
            Pool.Dispose();
            World.Dispose();
            _fixture.Dispose();
        }
    }

    private static bool Until(Func<bool> condition) => SpinWait.SpinUntil(condition, Wait);

    private static float DistanceXY(Vector3 a, Vector3 b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    [Fact]
    public void AWanderLeg_PicksItsPointAndSearchesItsPathOnAWorker_AndTheCreatureStandsStillUntilTheResultIsApplied()
    {
        using var s = new Scene(wanderers: 1);
        Creature wolf = s.Wolves[0];
        int testThread = Environment.CurrentManagedThreadId;
        s.Mesh.Gate.Reset();

        Assert.True(s.TickUntil(() => s.System.WanderJobsSubmitted >= 1, 300), "no wander job was submitted");
        Assert.True(Until(() => s.Mesh.PickEntered >= 1), "no worker took the job");
        var heldAt = new Vector3(wolf.X, wolf.Y, wolf.Z);

        // Held on the worker: several updates pass, the creature neither moves nor draws a point or a path on the map's thread.
        s.Tick(8);
        Assert.False(wolf.IsMoving, "the creature started a spline while its job was pending");
        Assert.Equal(heldAt, new Vector3(wolf.X, wolf.Y, wolf.Z));
        Assert.Equal(0, s.System.WanderJobsApplied);
        Assert.Equal(1, s.System.WanderJobsInFlight);

        s.Mesh.Gate.Set();
        Assert.True(Until(() => s.Mesh.PathReturned >= 1), "the job did not finish");
        Assert.True(Until(() => s.System.WanderJobsFinished >= 1 || s.System.WanderJobsApplied >= 1), "the job did not publish its answer");
        Assert.True(s.TickUntil(() => wolf.IsMoving, 10), "the finished leg was not started");

        Assert.NotEmpty(s.Mesh.SharedPointThreads);
        Assert.NotEmpty(s.Mesh.SharedPathThreads);
        Assert.DoesNotContain(testThread, s.Mesh.SharedPointThreads);
        Assert.DoesNotContain(testThread, s.Mesh.SharedPathThreads);
        Assert.Equal(0, s.Mesh.SyncPointCalls);
        Assert.Equal(0, s.Mesh.SyncPathCalls);
        Assert.True(s.System.WanderJobsSubmitted >= 1);
        Assert.True(s.System.WanderJobsApplied >= 1);
        Assert.Equal(0, s.System.PathJobsSubmitted); // one job, not a pick on the map plus an ordinary path job
    }

    [Theory]
    [InlineData(StaleCase.Killed)]
    [InlineData(StaleCase.Despawned)]
    [InlineData(StaleCase.InCombat)]
    [InlineData(StaleCase.Evading)]
    [InlineData(StaleCase.RelocatedThreeYards)]
    public void AJobWhoseCreatureChangedWhileItWasPending_IsDropped_AndItsLegIsNeverWalked(StaleCase staleCase)
    {
        using var s = new Scene(wanderers: 1);
        Creature wolf = s.Wolves[0];
        s.Mesh.Gate.Reset();
        Assert.True(s.TickUntil(() => s.System.WanderJobsSubmitted >= 1, 300), "no wander job was submitted");
        Assert.Equal(1, s.System.WanderJobsSubmitted);
        Assert.True(Until(() => s.Mesh.PickEntered >= 1), "no worker took the job");
        Assert.False(wolf.IsMoving);
        long appliedBefore = s.System.WanderJobsApplied;
        float newX = wolf.X + 3f;
        float newY = wolf.Y;

        switch (staleCase)
        {
            case StaleCase.Killed:
                s.Map.Combat.Kill(s.Player, wolf);
                break;
            case StaleCase.Despawned:
                s.System.Despawn(wolf);
                break;
            case StaleCase.InCombat:
                s.Map.Combat.DealDamage(s.Player, wolf, 1, direct: false);
                Assert.True(wolf.Combat.IsInCombat);
                break;
            case StaleCase.Evading:
                s.System.EnterEvadeMode(wolf);
                Assert.True(wolf.IsEvading);
                break;
            case StaleCase.RelocatedThreeYards:
                wolf.Relocate(newX, newY, wolf.Z, wolf.Orientation, 0);
                break;
        }

        // Still held: the next updates (or the sweep, 250 ms) notice the request no longer fits and drop the job.
        var splines = new List<CreatureSpline>();
        void Watch()
        {
            if (wolf.Spline is { } spline && !splines.Contains(spline))
            {
                splines.Add(spline);
            }
        }

        for (int i = 0; i < 6; i++)
        {
            s.World.RunTick(100);
            Watch();
        }

        Assert.True(s.System.WanderJobsStale >= 1, $"{staleCase}: the old job was not dropped as stale");

        // The worker finishes the old query now. Close the gate again at once, so a job started later (by an evaded or relocated creature
        // that wanders on) stays pending and cannot be applied inside this test.
        s.Mesh.Gate.Set();
        // The old job is the first pick a worker entered (confirmed before the stale action); a later pick or path search (the evade's
        // way home, a relocated creature's new request) belongs to another request.
        Assert.True(Until(() => s.Mesh.Picks.ContainsKey(0)), "the old job did not pick its point");
        Vector3 jobLegEnd = s.Mesh.Picks[0];
        Assert.True(Until(() => s.Mesh.SharedPathEnds.Any(e => DistanceXY(e, jobLegEnd) < 0.001f)), "the old job did not finish its path search");
        if (staleCase != StaleCase.RelocatedThreeYards)
        {
            s.Mesh.Gate.Reset();
        }

        for (int i = 0; i < 20; i++)
        {
            s.World.RunTick(100);
            Watch();
            Thread.Sleep(2);
        }

        foreach (CreatureSpline spline in splines)
        {
            Assert.True(DistanceXY(new Vector3(spline.EndX, spline.EndY, 0), jobLegEnd) > 0.05f, $"{staleCase}: the creature walked the dropped job's leg to ({jobLegEnd.X:F2}, {jobLegEnd.Y:F2})");
        }

        if (staleCase == StaleCase.RelocatedThreeYards)
        {
            // The request is made again from the new place; a leg it applies starts there, never at the old start.
            foreach (CreatureSpline spline in splines)
            {
                Assert.True(DistanceXY(new Vector3(spline.StartX, spline.StartY, 0), new Vector3(newX, newY, 0)) < 1f, $"a leg started at ({spline.StartX:F2}, {spline.StartY:F2}), not at the new place");
            }

            Assert.InRange(s.System.WanderJobsInFlight, 0, 1);
        }
        else
        {
            Assert.Equal(appliedBefore, s.System.WanderJobsApplied);
            if (staleCase == StaleCase.Evading)
            {
                Assert.InRange(s.System.WanderJobsInFlight, 0, 1); // home again, it may have asked for a new (held) leg
            }
            else
            {
                Assert.Equal(0, s.System.WanderJobsInFlight);
            }
        }
    }

    [Fact]
    public void WithMoreWanderersThanTheCap_TheExtraOnesWaitWithoutASynchronousSearch_AndAllWanderOnceTheJobsFinish()
    {
        using var s = new Scene(wanderers: 6);
        s.System.WanderJobsMaxInFlight = 2;
        s.Mesh.Gate.Reset();

        Assert.True(s.TickUntil(() => s.System.WanderJobsInFlight == 2 && s.System.WanderJobsThrottled > 0, 600), "the cap never filled and throttled");
        int maxInFlight = 0;
        for (int i = 0; i < 20; i++)
        {
            s.World.RunTick(100);
            maxInFlight = Math.Max(maxInFlight, s.System.WanderJobsInFlight);
            Assert.Equal(2, s.System.WanderJobsInFlight);
        }

        Assert.Equal(2, maxInFlight);
        Assert.Equal(2, s.System.WanderJobsSubmitted);
        Assert.True(s.System.WanderJobsThrottled > 0);
        Assert.All(s.Wolves, w => Assert.False(w.IsMoving));
        Assert.Equal(0, s.Mesh.SyncPointCalls);
        Assert.Equal(0, s.Mesh.SyncPathCalls);
        Assert.Equal(0, s.System.PathJobsSubmitted);

        s.Mesh.Gate.Set();
        var walked = new HashSet<Creature>();
        int peak = 0;
        for (int i = 0; i < 3000 && walked.Count < 6; i++)
        {
            s.World.RunTick(100);
            Thread.Sleep(1);
            peak = Math.Max(peak, s.System.WanderJobsInFlight);
            foreach (Creature wolf in s.Wolves)
            {
                if (wolf.IsMoving)
                {
                    walked.Add(wolf);
                }
            }
        }

        Assert.Equal(6, walked.Count);
        Assert.InRange(peak, 0, 2);
        Assert.Equal(0, s.Mesh.SyncPointCalls);
        Assert.Equal(0, s.Mesh.SyncPathCalls);
    }

    /// <summary>
    /// Characterization (passes before and after the change): the same map seed and the same tile give the same wander legs. Each tick
    /// waits for every outstanding job to publish so the pool's timing cannot change which tick a result is applied on.
    /// </summary>
    [Fact]
    public void TwoIdenticalWorlds_WithTheSameSeed_WalkTheSameWanderLegs()
    {
        const int Legs = 4;
        List<Vector3>[] first = RecordLegs(seed: 7, Legs);
        List<Vector3>[] second = RecordLegs(seed: 7, Legs);
        List<Vector3>[] other = RecordLegs(seed: 8, Legs);

        for (int i = 0; i < first.Length; i++)
        {
            Assert.Equal(Legs, first[i].Count);
            Assert.Equal(first[i], second[i]); // exact float equality (Vector3.Equals)
        }

        Assert.NotEqual(first.SelectMany(l => l), other.SelectMany(l => l)); // the legs depend on the seed (the comparison above is not vacuous)
    }

    private static List<Vector3>[] RecordLegs(int seed, int legs)
    {
        using var s = new Scene(wanderers: 3, seed);
        var seen = new CreatureSpline?[3];
        var ends = new[] { new List<Vector3>(), new List<Vector3>(), new List<Vector3>() };
        for (int tick = 0; tick < 4000 && ends.Any(e => e.Count < legs); tick++)
        {
            s.World.RunTick(100);
            Assert.True(
                Until(() => s.System.PathJobsFinished == s.System.PathJobsInFlight && s.System.WanderJobsFinished == s.System.WanderJobsInFlight),
                "a job did not publish its answer");
            for (int i = 0; i < 3; i++)
            {
                if (s.Wolves[i].Spline is { } spline && !ReferenceEquals(spline, seen[i]) && ends[i].Count < legs)
                {
                    seen[i] = spline;
                    ends[i].Add(new Vector3(spline.EndX, spline.EndY, spline.EndZ));
                }
            }
        }

        return ends;
    }

    [Fact]
    public void WithPathJobsWanderOff_NoWanderJobIsSubmitted_AndTheCreatureStillWandersTheOldWay()
    {
        using var s = new Scene(wanderers: 1, configure: o => o.Movement.PathJobsWander = false);
        Creature wolf = s.Wolves[0];

        for (int i = 0; i < 300 && !wolf.IsMoving; i++)
        {
            s.World.RunTick(100);
            Thread.Sleep(1);
        }

        Assert.True(wolf.IsMoving, "the wolf never started a wander leg");
        Assert.Equal(0, s.System.WanderJobsSubmitted);
        Assert.Equal(0, s.System.WanderJobsApplied);
        Assert.Equal(0, s.System.WanderJobsInFlight);
        Assert.True(s.System.PathJobsSubmitted >= 1, "the old route searches its path on the pool");
        Assert.True(s.Mesh.SyncPointCalls >= 1, "the old route picks its point on the map's thread");
    }

    // --- NavJob with a point pick (run directly, no creature) ---

    private sealed class NavScene : IDisposable
    {
        private readonly NavMeshFixture _fixture = new();

        public NavScene(bool loadTile = true)
        {
            _fixture.WriteParams(MapId);
            _fixture.WriteTile(MapId, 31, 31, new CellTile(0, 0, 0, 0, 20, 20, Size: 2));
            Nav = new NavMeshPathfinder(_fixture.Directory);
            if (loadTile)
            {
                Assert.True(Nav.LoadTile(MapId, 31, 31));
                Assert.True(Nav.CanSearchShared(MapId));
            }
        }

        public NavMeshPathfinder Nav { get; }

        public void Dispose() => _fixture.Dispose();
    }

    private static readonly PathOptions PointOptions = new() { ExcludeFlags = NavTerrain.SteepSlopes };

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(12345)]
    public void ANavJobWithAPick_PicksWhatTheSharedQueryPicksForTheSameSeed_AndSearchesThePathToIt(int seed)
    {
        using var scene = new NavScene();
        var center = new Vector3(20, 20, 0);
        const float Radius = 8f;
        bool? expectedAnswer = scene.Nav.TryGetRandomPointAroundShared(MapId, center, Radius, new Random(seed), PointOptions, out Vector3 expected);
        Assert.True(expectedAnswer);

        var job = new NavJob(scene.Nav, MapId, center, new NavJobPointPick(scene.Nav, center, Radius, seed, PointOptions), PathOptions.Default);
        using var pool = new NavJobSystem(threads: 1);
        pool.Run(job);

        Assert.True(job.IsDone);
        Assert.Equal(NavJobStatus.Completed, job.Status);
        Assert.Equal(NavJobPickOutcome.Picked, job.PickOutcome);
        Assert.Equal(expected, job.Destination); // exact
        Assert.NotNull(job.Result);
        Assert.True(job.Result!.HasPath);
        Assert.True(DistanceXY(job.Result.Points[^1], expected) < 0.05f, $"the path ends at {job.Result.Points[^1]}, not at the pick {expected}");
    }

    [Fact]
    public void ANavJobWithAPick_AtACentreOffEveryLoadedTile_ReportsNoPoint_AndSearchesNothing()
    {
        using var scene = new NavScene();
        var center = new Vector3(300, 300, 0);
        var job = new NavJob(scene.Nav, MapId, center, new NavJobPointPick(scene.Nav, center, 8f, 1, PointOptions), PathOptions.Default);
        using var pool = new NavJobSystem(threads: 1);
        pool.Run(job);

        Assert.Equal(NavJobStatus.Completed, job.Status);
        Assert.Equal(NavJobPickOutcome.NoPoint, job.PickOutcome);
        Assert.Null(job.Result);
    }

    [Fact]
    public void ANavJobWithAPick_BeforeTheOwnerLookedTheMapMeshUp_ReportsMeshUnknown()
    {
        using var scene = new NavScene(loadTile: false);
        var center = new Vector3(20, 20, 0);
        var job = new NavJob(scene.Nav, MapId, center, new NavJobPointPick(scene.Nav, center, 8f, 1, PointOptions), PathOptions.Default);
        using var pool = new NavJobSystem(threads: 1);
        pool.Run(job);

        Assert.Equal(NavJobStatus.Completed, job.Status);
        Assert.Equal(NavJobPickOutcome.MeshUnknown, job.PickOutcome);
        Assert.Null(job.Result);
    }

    [Fact]
    public void AnOrdinaryNavJob_HasNoPick_AndKeepsItsDestination()
    {
        using var scene = new NavScene();
        var start = new Vector3(10, 10, 0);
        var end = new Vector3(30, 30, 0);
        var job = new NavJob(scene.Nav, MapId, start, end, PathOptions.Default);
        using var pool = new NavJobSystem(threads: 1);
        pool.Run(job);

        Assert.Null(job.Pick);
        Assert.Equal(NavJobPickOutcome.None, job.PickOutcome);
        Assert.Equal(end, job.Destination);
        Assert.True(job.Result!.HasPath);
    }
}
