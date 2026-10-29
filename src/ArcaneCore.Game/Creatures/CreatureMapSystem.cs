using System.Diagnostics;
using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells.Rules.CrowdControl;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using GridCoord = ArcaneCore.Game.Maps.Grid.GridCoord;
using GridDefines = ArcaneCore.Game.Maps.Grid.GridDefines;
using MapGrid = ArcaneCore.Game.Maps.Grid.Grid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The creatures of one map: database spawns bucketed by grid, grids loaded around players and
/// unloaded after they leave, per-player visibility, death/corpse/respawn timers and idle
/// movement. Attached to its <see cref="Map"/> as an <see cref="IMapUpdater"/>.
/// <para>
/// Each update advances catch-up splines, creature timers and idle movement. The map owns
/// grid lifecycle, visibility and values/flush; observer snapshots after its flush schedule
/// catch-up moves for the next tick, after a creature's create block reached the client.
/// </para>
/// <para>
/// Grids follow vmangos GridDefines.h (64×64 grids of 533.33333 yd, ComputeGridPair). Within a
/// grid spawn bookkeeping is a plain list; live objects use the map's shared cell index.
/// The map's grid load/unload events and World:Maps configuration own their lifecycle.
/// </para>
/// Thread affinity: world thread only.
/// </summary>
public sealed partial class CreatureMapSystem : IMapUpdater, IMapUpdateBreakdown, ICreatureMover
{
    private readonly CreatureContent _content;
    private readonly CreatureOptions _options;
    private readonly ICreatureHeightProvider _height;
    private readonly IUnderMapClassifier? _underMap;
    private readonly Random _random;
    private readonly Func<uint> _serverTime;
    private readonly ILogger _logger;
    private readonly Func<uint, CreatureDisplayModelMetadata?>? _displayModelResolver;

    private readonly Dictionary<GridCoord, List<CreatureSpawn>> _spawnsByGrid = [];
    private readonly Dictionary<GridCoord, LoadedGrid> _grids = [];
    private readonly Dictionary<uint, long> _respawnAt = [];
    private readonly Dictionary<ObjectGuid, Creature> _creatures = [];
    private readonly Dictionary<Player, SeenCreatures> _seen = [];
    private readonly List<Creature> _newCreatures = [];
    private readonly List<Creature> _seenScratch = [];
    private Action? _captureNewObservers; // one delegate for every tick's deferred capture
    private readonly List<(Player Viewer, Creature Creature, uint SplineId)> _catchUp = [];
    private readonly HashSet<uint> _warnedMissingTemplates = [];
    private readonly List<Creature> _scratchCreatures = [];
    private readonly List<Creature> _randomMotionScratch = [];
    private int _creaturesVersion = 1;
    private int _scratchCreaturesVersion;

    private long _clockMs;
    private uint _splineCounter;

    /// <summary>MoveTo's one-point path: read by StartSpline and the packet builder and not kept (a one-point spline stores no Path).</summary>
    private readonly Vector3[] _onePoint = new Vector3[1];
    private uint _nextTemporaryCounter;

    public CreatureMapSystem(
        Map map, CreatureContent content, CreatureOptions? options = null, ICreatureHeightProvider? height = null,
        Random? random = null, Func<uint>? serverTime = null, ILogger? logger = null, CreatureAiServices? aiServices = null,
        ICreatureRespawnPersistence? respawnPersistence = null, IRespawnClock? respawnClock = null,
        Func<uint, CreatureDisplayModelMetadata?>? displayModelResolver = null, IUnderMapClassifier? underMap = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(content);
        Map = map;
        _content = content;
        _options = options ?? new CreatureOptions();
        _height = height ?? new MapCreatureHeightProvider(map);
        _underMap = underMap;
        _random = random ?? new Random();
        _serverTime = serverTime ?? (() => unchecked((uint)_clockMs));
        _logger = logger ?? NullLogger.Instance;
        _displayModelResolver = displayModelResolver;
        _ai = aiServices ?? CreatureAiServices.Default;
        _persistence = respawnPersistence;
        _respawnClock = respawnClock ?? SystemRespawnClock.Instance;
        _executeRelayStep = ExecuteRelayStepSafely;
        SubscribeAi();

        uint maxGuid = 0;
        foreach (CreatureSpawn spawn in content.GetSpawns(map.MapId))
        {
            GridCoord grid = GridDefines.ComputeGridCoord(spawn.X, spawn.Y);
            if (!_spawnsByGrid.TryGetValue(grid, out List<CreatureSpawn>? list))
            {
                _spawnsByGrid[grid] = list = [];
            }

            list.Add(spawn);
            maxGuid = Math.Max(maxGuid, spawn.Guid);
        }

        // Runtime spawns (GM .npc add, summons) take counters above the database spawns.
        _nextTemporaryCounter = maxGuid + 1;
        LoadPersistedRespawns();
        InitializePools();
        InitializeSpawnGroups();

        Map.Grids.GridLoaded += grid => LoadGrid(new GridCoord(grid.Coord.X, grid.Coord.Y));
        Map.Grids.GridUnloading += OnMapGridUnloading;
        foreach (MapGrid grid in Map.Grids.LoadedGrids.ToArray())
        {
            if (grid.ObjectDataLoaded)
            {
                LoadGrid(new GridCoord(grid.Coord.X, grid.Coord.Y));
            }
        }
    }

    public Map Map { get; }

    /// <summary>This system's clock: the sum of every update's diff (ms).</summary>
    public long ClockMs => _clockMs;

    public int LoadedGridCount => _grids.Count;

    public IReadOnlyCollection<Creature> Creatures => _creatures.Values;

    public Creature? FindCreature(ObjectGuid guid) => _creatures.GetValueOrDefault(guid);

    public bool IsGridLoaded(float x, float y) => _grids.ContainsKey(GridDefines.ComputeGridCoord(x, y));

    /// <summary>The respawn time (on <see cref="ClockMs"/>) kept for an unloaded dead spawn.</summary>
    public long? PendingRespawnAt(uint spawnGuid) => _respawnAt.TryGetValue(spawnGuid, out long at) ? at : null;

    // --- IMapUpdater ------------------------------------------------------------------------

    public void Update(Map map, uint diffMs)
    {
        if (!ReferenceEquals(map, Map))
        {
            throw new InvalidOperationException($"creature system of map {Map.MapId} updated by map {map.MapId}");
        }

        _clockMs += diffMs;
        _pathSearchesThisTick = 0;
        _pathNodesThisTick = 0;
        _pathTicksThisTick = 0;
        _timing = default;
        // World:Parallel:CreatureSplinePass: the creatures' splines advance in a pass of their own, which is the only part of this update
        // that runs outside the world's serial gate when the map updater runs ungated (World:Parallel:Ungated=CreatureMapSystem). Every
        // other stage reaches shared services (spells, scripts, the pathfinder, persistence) and holds the gate; outside the parallel
        // maps phase, or when the map gates this updater, Serial() is a no-op or a reentrant entry.
        bool splinePass = Map.Runtime.Options.Parallel.CreatureSplinePass;
        long t = Stopwatch.GetTimestamp();
        using (Serial())
        {
            SweepWanderJobs();
            SweepPathJobs();
            RunDeferredMoves();
            SendCatchUpMoves();
            t = Lap(t, out _timing.CatchUp);
            UpdateLinkedFollowers();
            t = Lap(t, out _timing.Linked);
            if (splinePass)
            {
                ApplyPendingActivations();
                if (_creatures.Count == 0)
                {
                    t = Lap(t, out _timing.Creatures);
                    UpdateTail(diffMs, t);
                    return;
                }
            }
            else
            {
                UpdateCreatures(diffMs);
            }
        }

        if (splinePass)
        {
            UpdateCreaturesInPasses(diffMs, ref t);
            return;
        }

        t = Lap(t, out _timing.Creatures);
        using (Serial())
        {
            UpdateTail(diffMs, t);
        }
    }

    private void UpdateTail(uint diffMs, long t)
    {
        UpdateSpawnGroups();
        CheckDormantRespawnStuck(diffMs);
        t = Lap(t, out _timing.SpawnGroups);
        UpdatePendingAi();
        t = Lap(t, out _timing.PendingAi);
        UpdateRelayScripts();
        t = Lap(t, out _timing.Relay);
        UpdateForcedDespawns();
        t = Lap(t, out _timing.Despawns);
        Map.RunAfterUpdate(_captureNewObservers ??= CaptureNewObservers);
        Lap(t, out _timing.AfterUpdate);
    }

    /// <summary>The world's serial gate (a no-op outside the parallel maps phase, reentrant when the map already gates this updater).</summary>
    private WorldRuntime.SerialScope Serial() => Map.EnterSerialAndFlush();

    /// <summary>One stage's duration in <see cref="Stopwatch"/> ticks, and the timestamp the next stage starts from.</summary>
    private static long Lap(long since, out long ticks)
    {
        long now = Stopwatch.GetTimestamp();
        ticks = now - since;
        return now;
    }

    /// <summary>Where the last <see cref="Update"/> spent its time (ticks of <see cref="Stopwatch"/>); filled on every update, read only to log a slow one.</summary>
    private struct UpdateTiming
    {
        public long CatchUp, Linked, Creatures, SpawnGroups, PendingAi, Relay, Despawns, AfterUpdate;

        /// <summary>Inside <see cref="Creatures"/>: AI updates, motion generator updates, and (inside either) path searches.</summary>
        public long Ai, Motion, Path;

        /// <summary>The spline pass of World:Parallel:CreatureSplinePass (inside <see cref="Creatures"/>; 0 when the pass is off).</summary>
        public long SplinePass;
        public int UngatedWanderers;

        public int Walked, Updated, Searches;
        public long SlowestCreatureTicks;
        public uint SlowestEntry;
        public Type? SlowestGenerator;

        /// <summary>The update's slowest A* search (<see cref="SearchPath"/>): its time, who asked, the endpoints and the work it did.</summary>
        public long SlowestSearchTicks;
        public SlowSearch SlowestSearch;

        /// <summary>Path-job answers applied this update (<see cref="TryPlanPathByJob"/>): count, worker time, slowest (not on this thread).</summary>
        public int JobSearches;
        public long JobSearchTicks, SlowestJobSearchTicks;
    }

    /// <summary>
    /// One search's detail for the slow-map-update line. Few nodes and expansions with a long time mean the search didn't get the
    /// thread (a GC, paging, the host). Many nodes mean the search did the work. LS-008: single creature searches of 120-560 ms with
    /// no GC on the live 2026-10-17 run, under node caps of 512-2048.
    /// </summary>
    private readonly record struct SlowSearch(
        uint Entry, MovementGeneratorType Motion, Vector3 From, Vector3 To, long Nodes, long Expansions, PathType Result, bool GcDuring);

    private UpdateTiming _timing;

    private static string TickMs(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The last update's stages for the slow-map-update line (<see cref="IMapUpdateBreakdown"/>). World thread; allocates.</summary>
    string IMapUpdateBreakdown.DescribeLastUpdate()
    {
        UpdateTiming t = _timing;
        string text = $"creatures {TickMs(t.Creatures)} ms over {t.Updated}/{t.Walked} active (ai {TickMs(t.Ai)}, motion {TickMs(t.Motion)}{(t.SplinePass > 0 ? $", spline pass {TickMs(t.SplinePass)}" : "")}{(t.UngatedWanderers > 0 ? $", {t.UngatedWanderers} wanderers outside the gate" : "")}), path searches {TickMs(t.Path)} ms in {t.Searches} searches from any stage, "
            + $"catch-up moves {TickMs(t.CatchUp)}, linked followers {TickMs(t.Linked)}, spawn groups {TickMs(t.SpawnGroups)}, pending ai {TickMs(t.PendingAi)}, "
            + $"relay scripts {TickMs(t.Relay)}, forced despawns {TickMs(t.Despawns)}, observers {TickMs(t.AfterUpdate)}, "
            + $"path outcomes so far: {NoPathPaths} no-path, {DirectFallbackPaths} straight authored routes, {DeferredPathSearches} deferred, {HomePathTeleports} home teleports";
        if (t.SlowestCreatureTicks > 0)
        {
            text += $"; slowest creature entry {t.SlowestEntry} {TickMs(t.SlowestCreatureTicks)} ms, top generator {t.SlowestGenerator?.Name ?? "none"}";
        }

        if (t.JobSearches > 0 || _creaturePathJobs.Count > 0)
        {
            text += $"; path jobs applied {t.JobSearches} (worker time {TickMs(t.JobSearchTicks)} ms, slowest {TickMs(t.SlowestJobSearchTicks)} ms), in flight {_creaturePathJobs.Count}";
        }

        if (t.SlowestSearchTicks > 0)
        {
            SlowSearch s = t.SlowestSearch;
            text += string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"; slowest path search {TickMs(t.SlowestSearchTicks)} ms: entry {s.Entry} ({s.Motion}) from {s.From.X:F1},{s.From.Y:F1},{s.From.Z:F1} to {s.To.X:F1},{s.To.Y:F1},{s.To.Z:F1}, {s.Nodes} nodes, {s.Expansions} expansions, {s.Result}{(s.GcDuring ? ", a GC ran during it" : "")}");
        }

        return text;
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
        _seen.Remove(player);
        _catchUp.RemoveAll(c => ReferenceEquals(c.Viewer, player));
    }

    // --- ICreatureMover -----------------------------------------------------------------------

    void ICreatureMover.MoveTo(Creature creature, float x, float y, float z, bool run, float? finalOrientation)
        => MoveStraight(creature, x, y, z, run, finalOrientation);

    PlannedPath ICreatureMover.PlanPath(Creature creature, Vector3 destination, bool toSpawn) => PlanPath(creature, destination, toSpawn);

    void ICreatureMover.TeleportHome(Creature creature, float x, float y, float z, float orientation)
    {
        HomePathTeleports++;
        CreaturePathStats.AddHomeTeleport();
        if (!creature.IsEvading && creature.Motion.CurrentType != MovementGeneratorType.Home)
        {
            CreatureAnomalies.ReportHomeUnreachable(creature, new Vector3(x, y, z), "home return fell back to teleport");
        }
        else
        {
            creature.ClearHomeUnreachableEpisode();
        }

        NearTeleport(creature, x, y, z, orientation);
    }

    void ICreatureMover.MovePath(Creature creature, IReadOnlyList<Vector3> path, bool run, SplineFacing facing)
        => MovePath(creature, path, run, facing);

    IReadOnlyList<Vector3> ICreatureMover.FindPath(Creature creature, Vector3 destination) => FindPath(creature, destination);

    bool ICreatureMover.IsCasting(Creature creature)
    {
        if (_ungatedMotion)
        {
            if (ReferenceEquals(creature, _ungatedSubject))
            {
                return _ungatedSubjectCasting;
            }

            using (Serial())
            {
                return _ai.Spells?.IsCasting(creature) ?? false;
            }
        }

        return _ai.Spells?.IsCasting(creature) ?? false;
    }

    CreatureMovementOptions ICreatureMover.MovementOptions => _options.Movement;

    void ICreatureMover.OnMovementFinished(Creature creature, MovementGeneratorType type, uint pointId)
        => OnMovementFinished(creature, type, pointId);

    void ICreatureMover.OnWaypointScript(Creature creature, uint scriptId, ObjectGuid targetGuid)
        => StartDbScript(DbScriptKind.CreatureMovement, scriptId, creature,
            targetGuid.IsEmpty ? creature : Map.FindObject(targetGuid));

    double ICreatureMover.NextDouble() => _random.NextDouble();

    int ICreatureMover.URand(int min, int max) => min >= max ? min : _random.Next(min, max + 1);

    float? ICreatureMover.GetHeight(uint mapId, float x, float y, float z)
    {
        if (_ungatedMotion)
        {
            // Without model data MapCreatureHeightProvider's answer is the terrain height, read here from a tile in memory. With models it
            // needs the world's vmap trees (world-thread state): in the gate.
            if (_ungatedTerrainFloor && _height is MapCreatureHeightProvider && mapId == Map.MapId && Map.TryGetLoadedHeight(x, y, out float height))
            {
                return MapCreatureHeightProvider.Known(height); // MapCreatureHeightProvider's answer
            }

            using (Serial())
            {
                return _height.GetHeight(mapId, x, y, z);
            }
        }

        return _height.GetHeight(mapId, x, y, z);
    }

    /// <summary>
    /// Wander, flee and fear choose their points with steep slopes excluded (vmangos Map::GetWalkRandomPosition, Map.cpp:3351-3353, and
    /// Map::GetWalkHitPosition, Map.cpp:3212-3224); their path searches exclude them too (<see cref="AvoidsSteepSlopes"/>).
    /// </summary>
    private static readonly PathOptions MeshPointOptions = new() { ExcludeFlags = NavTerrain.SteepSlopes };

    bool ICreatureMover.TryGetRandomMeshPoint(Creature creature, Vector3 center, float radius, out Vector3 point)
    {
        point = center;
        if (_ungatedMotion)
        {
            if (_ungatedNoMesh)
            {
                return false;
            }

            if (_ungatedMeshPoints?.TryGetRandomPointAroundShared(creature.MapId, center, radius, _random, MeshPointOptions, out point) is { } answered)
            {
                return answered;
            }

            using (Serial())
            {
                return Map.Collision.MeshPoints is { } owned && owned.TryGetRandomPointAround(creature.MapId, center, radius, _random, MeshPointOptions, out point);
            }
        }

        return Map.Collision.MeshPoints is { } mesh && mesh.TryGetRandomPointAround(creature.MapId, center, radius, _random, MeshPointOptions, out point);
    }

    bool ICreatureMover.TryRaycastMesh(Creature creature, Vector3 from, Vector3 to, out Vector3 reached)
    {
        reached = to;
        if (_ungatedMotion)
        {
            if (_ungatedNoMesh)
            {
                return false;
            }

            if (_ungatedMeshPoints?.RaycastShared(creature.MapId, from, to, MeshPointOptions) is { } sharedRay)
            {
                if (!sharedRay.Available)
                {
                    return false;
                }

                reached = sharedRay.Point;
                return true;
            }

            using (Serial())
            {
                if (Map.Collision.MeshPoints is not { } owned)
                {
                    return false;
                }

                MeshRaycastResult ownedRay = owned.Raycast(creature.MapId, from, to, MeshPointOptions);
                if (!ownedRay.Available)
                {
                    return false;
                }

                reached = ownedRay.Point;
                return true;
            }
        }

        if (Map.Collision.MeshPoints is not { } mesh)
        {
            return false;
        }

        MeshRaycastResult ray = mesh.Raycast(creature.MapId, from, to, MeshPointOptions);
        if (!ray.Available)
        {
            return false;
        }

        reached = ray.Point;
        return true;
    }

    /// <summary>
    /// Move the creature to (x, y, z) the way a scripted move should: along the map's navmesh (<see cref="FindPath"/>), the straight line only where
    /// the mesh has no data or no path (an authored destination, as vmangos). When this update's search budget is spent the move waits for a later
    /// update (<see cref="DeferredMoveCount"/>); a newer move or spline of the creature replaces the waiting one. Tell the observers.
    /// </summary>
    public void MoveTo(Creature creature, float x, float y, float z, bool run, float? finalOrientation)
    {
        ArgumentNullException.ThrowIfNull(creature);
        IReadOnlyList<Vector3> path = FindPath(creature, new Vector3(x, y, z));
        if (path.Count == 0)
        {
            _deferredMoves[creature] = new DeferredMove(x, y, z, run, finalOrientation);
            return;
        }

        MovePath(creature, path, run, finalOrientation is { } angle ? SplineFacing.ToAngle(angle) : SplineFacing.None);
    }

    /// <summary>Launch a straight spline from the creature's current position to (x, y, z) and tell its observers. No pathfinding.</summary>
    public void MoveStraight(Creature creature, float x, float y, float z, bool run, float? finalOrientation)
    {
        _onePoint[0] = new Vector3(x, y, z); // no list per move (wave 17 allocation leftover)
        MovePath(creature, _onePoint, run, finalOrientation is { } angle ? SplineFacing.ToAngle(angle) : SplineFacing.None);
    }

    private readonly record struct DeferredMove(float X, float Y, float Z, bool Run, float? FinalOrientation);

    private readonly Dictionary<Creature, DeferredMove> _deferredMoves = [];
    private readonly List<KeyValuePair<Creature, DeferredMove>> _deferredScratch = [];

    /// <summary>Scripted moves waiting for path budget.</summary>
    public int DeferredMoveCount => _deferredMoves.Count;

    /// <summary>
    /// Re-issue the scripted moves that waited for budget, first in the update so they are served before the creatures' own searches.
    /// A creature that died or left the map drops its move.
    /// </summary>
    private void RunDeferredMoves()
    {
        if (_deferredMoves.Count == 0)
        {
            return;
        }

        _deferredScratch.Clear();
        _deferredScratch.AddRange(_deferredMoves);
        _deferredMoves.Clear();
        foreach ((Creature creature, DeferredMove move) in _deferredScratch)
        {
            if (creature.IsAlive && creature.IsInWorld && _creatures.ContainsKey(creature.Guid))
            {
                MoveTo(creature, move.X, move.Y, move.Z, move.Run, move.FinalOrientation);
            }
        }

        _deferredScratch.Clear();
    }

    /// <summary>
    /// Launch a linear spline through <paramref name="path"/> (every point after the current
    /// position, destination last) and send SMSG_MONSTER_MOVE to the observers.
    /// </summary>
    public void MovePath(Creature creature, IReadOnlyList<Vector3> path, bool run, SplineFacing facing)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0)
        {
            return;
        }

        if (_deferredMoves.Count > 0)
        {
            _deferredMoves.Remove(creature); // a newer move replaces one that was waiting for budget
        }

        uint id = ++_splineCounter;
        var start = new Vector3(creature.X, creature.Y, creature.Z);
        CreatureSpline spline = creature.StartSpline(path, run, facing, id, _clockMs);
        byte[] packet = CreatureMovePackets.BuildPath(creature.Guid, start, id, facing, run, spline.DurationMs, path, _options.Movement.MonsterMoveOffsetBase); // path == spline.Points, without its one-point list
        SyncWalkMode(creature, run);
        Map.BroadcastToObservers(creature, WorldOpcode.SmsgMonsterMove, packet);
    }

    /// <summary>
    /// The path of an AUTHORED route (waypoint, point, formation, scripted move) from the creature to <paramref name="destination"/>: every
    /// corner after the start, the end last. It asks the map's <see cref="IPathfinder"/> (<c>map.Collision</c>: the navmesh when mmaps are
    /// installed, else a straight line). When the mesh has no path (<see cref="PathType.NoPath"/>) the route is walked straight: vmangos
    /// does the same for these moves (<c>MoveSplineInit::Move(PathFinder*)</c> takes the shortcut of a NOPATH finder), because the author
    /// chose the destination; each such answer counts in <see cref="DirectFallbackPaths"/>. When the update's search budget is spent the
    /// result is EMPTY and nothing walks: the caller asks again on a later update. The motions that decide for themselves what no path means
    /// (chase, flee, confused, wander, home) use <see cref="PlanPath"/>.
    /// </summary>
    public IReadOnlyList<Vector3> FindPath(Creature creature, Vector3 destination)
    {
        PlannedPath plan = PlanPath(creature, destination);
        switch (plan.Outcome)
        {
            case PathOutcome.Deferred:
                return plan.Corners;
            case PathOutcome.NoPath:
                DirectFallbackPaths++;
                CreaturePathStats.AddDirectFallback();
                return DrapeOnGround(creature, new Vector3(creature.X, creature.Y, creature.Z), [destination]);
            default:
                return plan.Corners;
        }
    }

    /// <summary>
    /// Search a path and report how it ended (<see cref="PathOutcome"/>) without substituting a straight line for a missing one. The corners
    /// are the whole path in one multi-point spline (vmangos MoveSplineInit::MovebyPath) so a move does not re-launch at every corner.
    /// World thread. Allocates the corner list.
    /// </summary>
    internal PlannedPath PlanPath(Creature creature, Vector3 destination, bool toSpawn = false, bool chase = false)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!float.IsFinite(destination.X) || !float.IsFinite(destination.Y) || !float.IsFinite(destination.Z))
        {
            CreatureAnomalies.ReportNonFinite(
                creature,
                $"Creature {creature.Guid} (entry {creature.Entry}) requested path to non-finite destination ({destination.X}, {destination.Y}, {destination.Z}) on map {Map.MapId}");
        }

        _lastPlanQueued = false;
        if (_options.Movement.PathJobs || _pathJobsResolved)
        {
            if ((!_ungatedMotion || _ungatedCanSearch)
                && TryPlanPathByJob(creature, destination, toSpawn, chase, out PlannedPath queued, _ungatedMotion ? _ungatedPathSearch : null))
            {
                return queued;
            }
        }

        if (_ungatedMotion)
        {
            // A search on this thread uses the pathfinder's owner path (it may read tiles): in the gate, as the owner.
            using (Serial())
            {
                _ungatedMotion = false;
                try
                {
                    return PlanPathOnThisThread(creature, destination, toSpawn, chase);
                }
                finally
                {
                    _ungatedMotion = true;
                }
            }
        }

        return PlanPathOnThisThread(creature, destination, toSpawn, chase);
    }

    /// <summary><see cref="PlanPath"/>'s synchronous search on the calling (owner) thread, within the update's search budget.</summary>
    private PlannedPath PlanPathOnThisThread(Creature creature, Vector3 destination, bool toSpawn, bool chase)
    {
        if (!HasPathBudget)
        {
            NoteDeferredPath(creature);
            return PlannedPath.Of(PathOutcome.Deferred);
        }

        var start = new Vector3(creature.X, creature.Y, creature.Z);
        PathOptions pathOptions = PathOptionsFor(creature, toSpawn, chase);
        PathResult path = SearchPath(creature, start, destination, pathOptions);
        return FinishPlannedPath(creature, start, destination, toSpawn, pathOptions, path);
    }

    /// <summary>
    /// The outcome of a finished search (this thread's or a path job's, <see cref="TryPlanPathByJob"/>): pending tiles defer, no path
    /// is reported, and a found path becomes the corner list (smoothed and varied as before).
    /// </summary>
    private PlannedPath FinishPlannedPath(Creature creature, Vector3 start, Vector3 destination, bool toSpawn, PathOptions pathOptions, PathResult path)
    {
        if ((path.Type & PathType.TilesPending) != 0)
        {
            NoteDeferredPath(creature);
            return PlannedPath.Of(PathOutcome.Deferred);
        }

        if (!path.HasPath && toSpawn && Vector3.DistanceSquared(start, destination) <= AtHomeYards * AtHomeYards)
        {
            // Already standing on its spawn point: a spawn off the mesh (a mesh hole, a doodad top) has no path from itself to
            // itself, and that is no unreachable home (live: entry 1224 reported "no mesh path to home" 0.0 yd from home). The walk
            // home is the zero-length straight move, which still ends in the usual reached-home.
            creature.ClearHomeUnreachableEpisode();
            return new PlannedPath(PathOutcome.Direct, DrapeOnGround(creature, start, [destination], window: AtHomeSnapYards));
        }

        if (!path.HasPath)
        {
            NoPathPaths++;
            CreaturePathStats.AddNoPath();
            bool evadingHome = toSpawn && (creature.IsEvading || creature.Motion.CurrentType == MovementGeneratorType.Home);
            if (toSpawn && !evadingHome)
            {
                CreatureAnomalies.ReportHomeUnreachable(creature, destination, "no mesh path to home");
            }

            bool chasingOrFollowing = creature.Motion.CurrentType is MovementGeneratorType.Chase or MovementGeneratorType.Follow;
            if (!evadingHome
                && !chasingOrFollowing
                && (toSpawn || creature.Combat.IsInCombat || creature.Motion.CurrentType is not (MovementGeneratorType.Random or MovementGeneratorType.Fleeing or MovementGeneratorType.Confused))
                && (toSpawn || !InAMediumItCannotEnter(creature, destination)))
            {
                CreatureAnomalies.ReportPathFailed(creature, start, destination, toSpawn ? "home" : creature.Motion.CurrentType.ToString());
            }

            return PlannedPath.Of(PathOutcome.NoPath);
        }

        var corners = new List<Vector3>(path.Points.Count - 1);
        for (int i = 1; i < path.Points.Count; i++)
        {
            corners.Add(path.Points[i]);
        }

        PathOutcome outcome = (path.Type & PathType.Incomplete) != 0 ? PathOutcome.Partial
            : (path.Type & (PathType.NotUsingPath | PathType.Shortcut)) != 0 ? PathOutcome.Direct
            : PathOutcome.Mesh;
        if (outcome is PathOutcome.Mesh or PathOutcome.Direct)
        {
            creature.ClearPathFailedEpisode();
            if (toSpawn)
            {
                creature.ClearHomeUnreachableEpisode();
            }
        }

        if (outcome == PathOutcome.Mesh && corners.Count >= 2
            && (toSpawn || creature.Combat.IsInCombat || creature.Motion.CurrentType != MovementGeneratorType.Random)
            && Map.Collision.MeshPoints is { } mesh)
        {
            corners = CreatureSplineSmoothing.VaryAndSmooth(
                creature, start, corners, mesh, _height.GetHeight, pathOptions, allowVariation: !toSpawn && !creature.Combat.IsInCombat);
        }

        // Every outcome (a mesh path, a partial one, a straight shortcut): the mesh's own heights sit +0.2 yd (p95 +0.55) over the floor
        // and a chord between its corners misses the ground by p95 1.4 yd, so the leg is put on the ground and split where it misses.
        return new PlannedPath(outcome, DrapeOnGround(creature, start, corners, cornersOnMesh: outcome is PathOutcome.Mesh or PathOutcome.Partial));
    }

    // --- per-tick creature path-search budget ------------------------------------------------------

    private int _pathSearchesThisTick;
    private long _pathNodesThisTick;
    private long _pathTicksThisTick;
    private CreaturePathBudget _pathBudget = CreaturePathBudget.Default;
    private long _pathTickLimit = CreaturePathBudget.Default.MaxStopwatchTicks;

    /// <summary>
    /// The most path searches and A* nodes creature movement may spend in one map update (reset at the start of <see cref="Update"/>).
    /// A request past it is deferred to a later update (<see cref="DeferredPathSearches"/>); no motion walks straight for want of budget.
    /// </summary>
    public CreaturePathBudget PathBudget
    {
        get => _pathBudget;
        set
        {
            _pathBudget = value;
            _pathTickLimit = value.MaxStopwatchTicks;
        }
    }

    /// <summary>Searches creature movement has run since the current update began.</summary>
    public int PathSearchesThisTick => _pathSearchesThisTick;

    /// <summary>A* nodes those searches created.</summary>
    public long PathNodesThisTick => _pathNodesThisTick;

    /// <summary>Path requests (any motion) put off to a later update because the budget was spent (diagnostics, tests).</summary>
    public long DeferredPathSearches { get; private set; }

    /// <summary>
    /// Authored routes (waypoint, point, formation, scripted move) walked along the straight line because the mesh had no path for them
    /// (<see cref="FindPath"/>; diagnostics, tests). A spent budget never counts here: it defers.
    /// </summary>
    public long DirectFallbackPaths { get; private set; }

    /// <summary>Searches that ended <see cref="PathType.NoPath"/>, for any motion (diagnostics, tests).</summary>
    public long NoPathPaths { get; private set; }

    /// <summary>Evade/home returns done by teleport because no whole mesh path led home (diagnostics, tests).</summary>
    public long HomePathTeleports { get; private set; }

    /// <summary>Wall time (<see cref="Stopwatch"/> ticks) creature path searches have spent since the current update began.</summary>
    public long PathTicksThisTick => _pathTicksThisTick;

    /// <summary>
    /// Whether another search fits the tick's budget. The last search may overshoot the node budget by one search's cap and the time
    /// budget (<see cref="CreaturePathBudget.MaxMilliseconds"/>) by one search's time.
    /// </summary>
    public bool HasPathBudget => _pathSearchesThisTick < (_inCrowdedRandomPass ? _randomPassSearchLimit : PathBudget.MaxSearches)
        && _pathNodesThisTick < PathBudget.MaxNodes
        && _pathTicksThisTick < _pathTickLimit;

    private bool _inCrowdedRandomPass;
    private int _randomPassSearchLimit;
    private Creature? _pathFirstDeferred;

    /// <summary>
    /// Count a chase/follow request put off because <see cref="HasPathBudget"/> was false. The first creature deferred in an update is
    /// where the next update's creature walk starts, so no chaser waits behind the same early creatures forever.
    /// </summary>
    internal void NoteDeferredPath(Creature creature)
    {
        DeferredPathSearches++;
        CreaturePathStats.AddDeferred();
        _pathFirstDeferred ??= creature;
    }

    private static readonly PathOptions[] s_moverOptions = BuildMoverOptions(0f);
    private static readonly PathOptions[] s_spawnOptions = BuildMoverOptions(PathOptions.VmangosEndTolerance);
    private static readonly PathOptions[] s_wanderOptions = BuildMoverOptions(0f, WanderMaxSearchNodes, excludeSteepSlopes: true);
    private static readonly PathOptions[] s_offSlopeOptions = BuildMoverOptions(0f, excludeSteepSlopes: true);

    /// <summary>
    /// The A* node cap of an out-of-combat random-movement leg. The leg goes to a point within the wander distance (vmangos
    /// <c>GetRandomPoint</c>), so a reachable one is a few dozen polygons away; a point that is not reachable (another floor, an island
    /// the circle overlaps) made the search exhaust the full <see cref="PathOptions.DefaultMaxSearchNodes"/> pool before it answered a
    /// partial path, once per wanderer every few seconds (live 2026-10-16, map 0: single random-movement updates of 50-425 ms, most of
    /// it in path search). A capped leg still answers the partial path toward the point (<see cref="PathOptions.AllowPartial"/>), as the
    /// full search did.
    /// </summary>
    public const int WanderMaxSearchNodes = 512;

    private static readonly PathOptions[] s_chaseOptions = BuildMoverOptions(0f, wideEndSearch: true);

    private static PathOptions[] BuildMoverOptions(
        float tolerance, int maxSearchNodes = PathOptions.DefaultMaxSearchNodes, bool wideEndSearch = false, bool excludeSteepSlopes = false)
    {
        var all = new PathOptions[16]; // InhabitType bits 0-2, plus 8 for a pet
        for (uint inhabit = 0; inhabit < all.Length; inhabit++)
        {
            all[inhabit] = new PathOptions
            {
                Mover = PathMover.FromInhabit(inhabit & 7, isPet: (inhabit & 8) != 0),
                EndTolerance = tolerance,
                MaxSearchNodes = maxSearchNodes,
                WideEndSearch = wideEndSearch,
                ExcludeFlags = excludeSteepSlopes ? NavTerrain.SteepSlopes : NavTerrain.Empty,
                ExcludeOnlyInSearch = excludeSteepSlopes,
            };
        }

        return all;
    }

    /// <summary>
    /// The query options of a creature: its own locomotion (<c>InhabitType</c>, vmangos PathInfo::createFilter), so a flier gets the flier
    /// exception (a hole in the mesh is a flying shortcut, not NoPath) and a walker keeps off water it cannot swim. A return home
    /// (<paramref name="toSpawn"/>) also takes vmangos' 7 yd end tolerance. A chase or follow (<paramref name="chase"/>) looks for its
    /// destination's polygon in cmangos' wider box too (<see cref="PathOptions.WideEndSearch"/>): a target off the mover's mesh (swimming,
    /// for a creature that cannot swim) is approached to the nearest usable point, unreachable, instead of not at all. A random, confused
    /// or fleeing leg keeps off steep slopes (<see cref="AvoidsSteepSlopes"/>).
    /// </summary>
    private static PathOptions PathOptionsFor(Creature creature, bool toSpawn, bool chase = false)
        => (toSpawn ? s_spawnOptions
            : chase ? s_chaseOptions
            : IsWanderLeg(creature) ? s_wanderOptions
            : AvoidsSteepSlopes(creature) ? s_offSlopeOptions
            : s_moverOptions)[(creature.Template.InhabitType & 7) | (creature.IsPet ? 8 : 0)];

    /// <summary>
    /// A leg of the motions whose vmangos path search excludes steep slopes: random (<c>MOVE_EXCLUDE_STEEP_SLOPES</c>,
    /// RandomMovementGenerator.cpp:52, read by MoveSplineInit.h:129-130), confused (ConfusedMovementGenerator.cpp:79), and fear and flee
    /// (FearMovementGenerator.cpp:38, FleeingMovementGenerator.cpp:56), each through <c>PathFinder::ExcludeSteepSlopes</c>
    /// (PathFinder.h:88). Every other creature path (chase, home, waypoint, point, follow) excludes nothing
    /// (PathInfo::createFilter, PathFinder.cpp:657-677). The exclusion is the search's only (<see cref="PathOptions.ExcludeOnlyInSearch"/>):
    /// a creature standing on a slope is located there and paths off it. Keyed on the current motion, as <see cref="IsWanderLeg"/> is.
    /// </summary>
    private static bool AvoidsSteepSlopes(Creature creature)
        => creature.Motion.CurrentType is MovementGeneratorType.Random or MovementGeneratorType.Confused or MovementGeneratorType.Fleeing;

    /// <summary>How near its spawn point a creature counts as home already, so a failed search there is no unreachable home.</summary>
    internal const float AtHomeYards = 0.5f;

    /// <summary>
    /// Whether <paramref name="destination"/> lies in a medium the creature cannot move in: in or under liquid for one that cannot swim, out of
    /// it for one that only swims (<c>InhabitType</c>; a pet goes anywhere, a flier is never in this case). Its NoPath is then the expected
    /// answer (vmangos PathInfo::BuildPolyPath: no polygon of the mover's filter at the end, PATHFIND_NOPATH) and the unreachable-target
    /// timer ends the chase; it is not reported as <c>creature/path-failed</c>. Live 2026-10-18: most of the remaining reports were Durotar
    /// Tigers and Bloodtalons whose target swam more than the wide end search's 10 yards off the coast.
    /// </summary>
    private bool InAMediumItCannotEnter(Creature creature, Vector3 destination)
    {
        uint inhabit = creature.Template.InhabitType;
        if (creature.IsPet || (inhabit & 0x04) != 0 || (inhabit & 0x03) is 0 or 0x03)
        {
            return false; // a pet, a flier, or a creature that walks and swims (or has no InhabitType)
        }

        bool inLiquid = (Map.GetLiquidStatus(destination.X, destination.Y, destination.Z, LiquidTypeFlags.AllLiquids, out _)
            & (LiquidStatus.InWater | LiquidStatus.UnderWater)) != 0;
        return (inhabit & 0x02) == 0 ? inLiquid : !inLiquid;
    }

    /// <summary>An out-of-combat random-movement leg (<see cref="WanderMaxSearchNodes"/>).</summary>
    private static bool IsWanderLeg(Creature creature)
        => !creature.Combat.IsInCombat && creature.Motion.CurrentType == MovementGeneratorType.Random;

    /// <summary>One counted search through the map's pathfinder. World thread.</summary>
    private PathResult SearchPath(Creature creature, Vector3 start, Vector3 destination, PathOptions? options = null)
    {
        PathSearchTotals before = PathSearchCounters.Current;
        int gen0Before = GC.CollectionCount(0);
        long started = Stopwatch.GetTimestamp();
        PathResult path = Map.Collision.FindPath(start, destination, options);
        long elapsed = Stopwatch.GetTimestamp() - started;
        _timing.Path += elapsed;
        PathSearchTotals spent = PathSearchCounters.Current - before;
        if (spent.Searches > 0)
        {
            _pathTicksThisTick += elapsed; // as with the counts, a straight line without navigation data costs nothing
            if (elapsed > _timing.SlowestSearchTicks)
            {
                _timing.SlowestSearchTicks = elapsed;
                _timing.SlowestSearch = new SlowSearch(creature.Entry, creature.Motion.CurrentType, start, destination, spent.Nodes, spent.Expansions,
                    path.Type, GC.CollectionCount(0) != gen0Before);
            }
        }

        _timing.Searches += (int)spent.Searches;

        // Only searches that ran A* count: without navigation data the "search" is a straight line and costs nothing.
        _pathSearchesThisTick += (int)spent.Searches;
        _pathNodesThisTick += spent.Nodes;
        return path;
    }

    /// <summary>Stop a moving creature where it is (vmangos Unit::StopMoving → MoveSplineInit::Stop).</summary>
    public void StopMoving(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (_deferredMoves.Count > 0)
        {
            _deferredMoves.Remove(creature); // a queued scripted move is stale once the creature is stopped or teleported
        }

        if (!creature.IsMoving)
        {
            return;
        }

        creature.StopSpline(_clockMs, _serverTime());
        uint id = ++_splineCounter;
        byte[] packet = CreatureMovePackets.BuildStop(creature.Guid, creature.X, creature.Y, creature.Z, id);
        Map.BroadcastToObservers(creature, WorldOpcode.SmsgMonsterMove, packet);
    }

    // --- phases -------------------------------------------------------------------------------

    /// <summary>
    /// A client that got a moving creature's create block last tick now gets the rest of the
    /// path (vmangos writes the live spline into the create block's movement part instead,
    /// PacketBuilder::WriteCreate; the M5 create block never advertises a spline).
    /// </summary>
    private void SendCatchUpMoves()
    {
        if (_catchUp.Count == 0)
        {
            return;
        }

        foreach ((Player viewer, Creature creature, uint splineId) in _catchUp)
        {
            if (viewer.Map != Map || !viewer.VisibleObjects.Contains(creature.Guid)
                || creature.Spline is not { } spline || spline.Id != splineId || spline.IsFinished(_clockMs))
            {
                continue;
            }

            (float x, float y, float z) = spline.PositionAt(_clockMs);
            uint remaining = spline.DurationMs - spline.ElapsedMs(_clockMs);
            byte[] packet = CreatureMovePackets.BuildPath(
                creature.Guid, new Vector3(x, y, z), spline.Id, spline.Facing, spline.Run, remaining, spline.RemainingPoints(_clockMs), _options.Movement.MonsterMoveOffsetBase);
            viewer.Session.Send(WorldOpcode.SmsgMonsterMove, packet);
        }

        _catchUp.Clear();
    }

    /// <summary>Creature updates skipped because the creature stood outside every active cell (diagnostics, tests).</summary>
    public long SkippedInactiveCreatures { get; private set; }

    /// <summary>
    /// A creature updated wherever it stands (vmangos WorldObject::IsActiveObject, Objects/Object.h:780: m_isActiveObject or a
    /// viewpoint with viewers): an active object of the map (<see cref="Map.SetActive"/>, vmangos SetActiveObjectState / AddToActive:
    /// script command 21, the AOI and ACTIVE template flags and formation members, <see cref="QueueActivationIfFlagged"/>), and a
    /// creature a player looks through (possession, Eyes of the Beast: Camera viewers; it is updated but does not activate cells).
    /// A formation member also counts from the moment it enters, before its queued activation applies.
    /// </summary>
    private bool IsAlwaysUpdated(Creature creature)
        => creature.IsFormationMember || Map.Grids.IsActive(creature) || UnitControl.ViewerOf(creature) is not null;

    private readonly List<Creature> _pendingActivation = [];

    /// <summary>The creature behaviour bits that make a creature an active object when it enters the map.</summary>
    private const CreatureBehaviourFlags ActiveOnSpawn = CreatureBehaviourFlags.Active | CreatureBehaviourFlags.LargeAoi
        | CreatureBehaviourFlags.GiganticAoi | CreatureBehaviourFlags.InfiniteAoi;

    /// <summary>
    /// A creature entering the map that is an active object from its template or group (Map::AddToActive): vmangos Creature.cpp:683-699
    /// (CREATURE_STATIC_FLAG_LARGE_AOI, CREATURE_FLAG_EXTRA_GIGANTIC_AOI / INFINITE_AOI, with Visibility.ForceActiveObjects, default
    /// true, World.cpp:828), mangos-classic Creature.cpp:205 (CREATURE_EXTRA_FLAG_ACTIVE, classic-db's dialect) and vmangos
    /// Creature.cpp:289-292 (a formation member). Queued and applied before the next creature update: activating loads and pins the
    /// creature's own grid, which must not run inside the grid load that placed the creature.
    /// </summary>
    private void QueueActivationIfFlagged(Creature creature)
    {
        if (creature.ScriptActiveObject || (creature.Template.Behaviour & ActiveOnSpawn) != 0 || creature.IsFormationMember)
        {
            _pendingActivation.Add(creature);
        }
    }

    private void ApplyPendingActivations()
    {
        if (_pendingActivation.Count == 0)
        {
            return;
        }

        Creature[] pending = [.. _pendingActivation];
        _pendingActivation.Clear();
        foreach (Creature creature in pending)
        {
            if (ReferenceEquals(creature.Map, Map) && Map.Grids.Contains(creature) && !Map.Grids.IsActive(creature))
            {
                Map.SetActive(creature, true);
            }
        }
    }

    /// <summary>The walk's per-creature verdict of the spline pass (<see cref="UpdateCreaturesInPasses"/>), by index in <see cref="_scratchCreatures"/>.</summary>
    private enum SplinePassMark : byte
    {
        Inactive,
        Active,
        Advanced,
    }

    private SplinePassMark[] _splineMarks = [];
    private int[] _activeScratchIndices = [];

    private int PrepareScratchCreaturesAndConsumeStart()
    {
        if (_scratchCreaturesVersion != _creaturesVersion)
        {
            _scratchCreatures.Clear();
            foreach (Creature creature in _creatures.Values)
            {
                creature.ScratchIndex = _scratchCreatures.Count;
                _scratchCreatures.Add(creature);
            }

            _scratchCreaturesVersion = _creaturesVersion;
        }

        int start = 0;
        if (_pathFirstDeferred is { } resume)
        {
            int idx = resume.ScratchIndex;
            if ((uint)idx < (uint)_scratchCreatures.Count && ReferenceEquals(_scratchCreatures[idx], resume))
            {
                start = idx;
            }

            _pathFirstDeferred = null;
        }

        return start;
    }

    /// <summary>
    /// <see cref="UpdateCreatures"/> in two passes (World:Parallel:CreatureSplinePass): first every creature of the walk is judged
    /// active or not and an active living one has its spline advanced (map-local: position, grid cell, visibility queue; a change of cell
    /// takes the gate in <see cref="Map.OnObjectMoved"/>), outside the serial gate; then the rest of the walk runs inside it with those
    /// verdicts. Same walk, same order of creatures, at any thread count.
    /// </summary>
    private void UpdateCreaturesInPasses(uint diffMs, ref long t)
    {
        uint now = _serverTime();
        int start = PrepareScratchCreaturesAndConsumeStart();
        int count = _scratchCreatures.Count;
        if (_splineMarks.Length < count)
        {
            _splineMarks = new SplinePassMark[Math.Max(count, _splineMarks.Length * 2)];
        }

        if (_activeScratchIndices.Length < count)
        {
            _activeScratchIndices = new int[Math.Max(count, _activeScratchIndices.Length * 2)];
        }

        bool activeCellsOnly = Map.UpdatesActiveCellsOnly;
        int activeCount = 0;
        long splineStart = Stopwatch.GetTimestamp();
        for (int walked = 0; walked < count; walked++)
        {
            int index = (start + walked) % count;
            Creature creature = _scratchCreatures[index];
            if (activeCellsOnly && !Map.Grids.IsInActiveCell(creature) && !IsAlwaysUpdated(creature))
            {
                _splineMarks[index] = SplinePassMark.Inactive;
                creature.Spline?.Pause(_clockMs, diffMs);
                creature.IsFrozenOutsideActiveCells = true;
                SkippedInactiveCreatures++;
                continue;
            }

            if (creature.DeathState == CreatureDeathState.Alive)
            {
                try
                {
                    creature.AdvanceSpline(_clockMs, now);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("creature.update", ex);
                    _logger.LogFault("creature.spline-pass", ex, "Creature spline pass fault for {Guid} (entry={Entry}, map={MapId})",
                        creature.Guid, creature.Entry, Map.MapId);
                }

                _splineMarks[index] = SplinePassMark.Advanced;
            }
            else
            {
                _splineMarks[index] = SplinePassMark.Active;
            }

            _activeScratchIndices[activeCount++] = index;
        }

        _timing.SplinePass = Stopwatch.GetTimestamp() - splineStart;
        using (Serial())
        {
            UpdateCreatures(diffMs, _splineMarks, _activeScratchIndices, activeCount, splitRandomMotion: true);
            PrepareUngatedMotion();
            if (_ungatedRandom.Count == 0)
            {
                try
                {
                    foreach (Creature creature in _randomMotionScratch)
                    {
                        UpdateRandomMotion(creature, diffMs);
                    }
                }
                finally
                {
                    _inCrowdedRandomPass = false;
                    _randomMotionScratch.Clear();
                }

                t = Lap(t, out _timing.Creatures);
                UpdateTail(diffMs, t);
                return;
            }
        }

        // The wander legs of plain spawn wanderers run outside the gate (map-local: this map's RNG, path budget and job queue, the
        // creature's spline; shared reads go through ICreatureMover with the rare owner-only cases in the gate, and packets wait in the
        // map's outbox), then every other Random motion of the walk inside it. Same order at any thread count.
        _ungatedMotion = true;
        try
        {
            for (int i = 0; i < _ungatedRandom.Count; i++)
            {
                _ungatedSubject = _ungatedRandom[i];
                _ungatedSubjectCasting = _ungatedRandomCasting[i];
                UpdateRandomMotion(_ungatedSubject, diffMs);
            }
        }
        finally
        {
            _ungatedMotion = false;
            _ungatedSubject = null;
            _timing.UngatedWanderers += _ungatedRandom.Count;
            _ungatedRandom.Clear();
            _ungatedRandomCasting.Clear();
        }

        using (Serial())
        {
            try
            {
                foreach (Creature creature in _randomMotionScratch)
                {
                    UpdateRandomMotion(creature, diffMs);
                }
            }
            finally
            {
                _inCrowdedRandomPass = false;
                _randomMotionScratch.Clear();
            }

            t = Lap(t, out _timing.Creatures);
            UpdateTail(diffMs, t);
        }
    }

    // --- Random motion outside the serial gate (World:Parallel:CreatureSplinePass with Ungated=CreatureMapSystem) ------------------

    private readonly List<Creature> _ungatedRandom = [];
    private readonly List<bool> _ungatedRandomCasting = [];
    private bool _ungatedMotion;
    private Creature? _ungatedSubject;
    private bool _ungatedSubjectCasting;
    private ISharedMeshPointQuery? _ungatedMeshPoints;
    private bool _ungatedNoMesh;
    private ISharedPathSearch? _ungatedPathSearch;
    private bool _ungatedCanSearch;
    private bool _ungatedTerrainFloor;

    /// <summary>
    /// Whether the creature's Random motion may run outside the gate: a plain wander (no expiry, so its update never pops the generator
    /// and runs no finish hook).
    /// </summary>
    private static bool CanMoveUngated(Creature creature)
        => creature.Motion.Top is RandomMovementGenerator { Expires: false };

    /// <summary>
    /// In the gate, before the ungated wander pass: the owner-only lookups it needs are made here (the map's pathfinder, whether its
    /// navmesh is known, the path-job pool, whether model floors exist), so the pass itself never reads the navmesh table or a terrain file.
    /// </summary>
    private void PrepareUngatedMotion()
    {
        _ungatedTerrainFloor = !Map.Collision.LineOfSight.Enabled; // MapCollision.GetHeight asks no model then
        IMeshPointQuery? meshPoints = Map.Collision.MeshPoints;
        _ungatedNoMesh = meshPoints is null;
        _ungatedMeshPoints = meshPoints as ISharedMeshPointQuery;
        _ungatedPathSearch = Map.Collision.PathfinderForMap as ISharedPathSearch;
        _ungatedCanSearch = (_options.Movement.PathJobs || _pathJobsResolved) && PathJobs is not null
            && _ungatedPathSearch is { } shared && shared.CanSearchShared(Map.MapId);
    }

    /// <summary>One Random motion update of the walk's second pass (a creature that died or left meanwhile is skipped).</summary>
    private void UpdateRandomMotion(Creature creature, uint diffMs)
    {
        if (creature.DeathState != CreatureDeathState.Alive
            || !_creatures.ContainsKey(creature.Guid)
            || (creature.Combat.IsInCombat && ReferenceEquals(creature.Motion.Top, creature.Motion.Default)))
        {
            return;
        }

        bool timePerCreature = Map.IsCollectingDiagnostics || TimeRandomMotion;
        long motionStart = timePerCreature ? Stopwatch.GetTimestamp() : 0;
        try
        {
            creature.Motion.Update(diffMs);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("creature.update", ex);
            _logger.LogFault(
                "CreatureMapSystem.UpdateCreatureMotion",
                ex,
                "Creature random motion update fault for {Guid} (entry={Entry}, name={Name}, map={MapId}, pos=({X:F2},{Y:F2},{Z:F2}))",
                creature.Guid,
                creature.Entry,
                creature.Template.Name,
                Map.MapId,
                creature.X,
                creature.Y,
                creature.Z);
        }

        if (timePerCreature)
        {
            long motionTicks = Math.Max(1L, Stopwatch.GetTimestamp() - motionStart);
            RandomMotionTicksTotal += motionTicks;
            RandomMotionCalls++;
            if (Map.IsCollectingDiagnostics)
            {
                _timing.Motion += motionTicks;
                if (motionTicks > _timing.SlowestCreatureTicks)
                {
                    _timing.SlowestCreatureTicks = motionTicks;
                    _timing.SlowestEntry = creature.Entry;
                    _timing.SlowestGenerator = creature.Motion.Top?.GetType();
                }
            }
        }
    }

    private void UpdateCreatures(uint diffMs) => UpdateCreatures(diffMs, null, null, 0, splitRandomMotion: false);

    /// <summary>
    /// The creature walk. With <paramref name="marks"/> (the spline pass ran), the scratch list is the pass's, each creature's active
    /// verdict is the pass's and a creature the pass advanced is not advanced again.
    /// </summary>
    private void UpdateCreatures(uint diffMs, SplinePassMark[]? marks, int[]? activeIndices, int activeCount, bool splitRandomMotion)
    {
        uint now = _serverTime();
        int start = 0;
        if (marks is null)
        {
            // Live position may be outside the spawn's original grid. Snapshot ownership rather
            // than spawn buckets so a moved creature is updated once and survives home-grid unload.
            ApplyPendingActivations();
            start = PrepareScratchCreaturesAndConsumeStart();
        }

        bool activeCellsOnly = Map.UpdatesActiveCellsOnly;
        bool timePerCreature = Map.IsCollectingDiagnostics;

        // Fair deferral: the walk starts at the creature whose path search was deferred first last update, so deferred chasers are
        // served before the ones that were served (a fixed order starves the late creatures while demand stays above the budget).
        // Non-Random movement (chase, home, flee, confused, waypoint, point, follow, formation) runs in pass 1 so idle Random wanderers
        // never starve combat or evade path searches; Random movement runs in pass 2 with the remaining budget.
        _randomMotionScratch.Clear();
        int count = _scratchCreatures.Count;
        _timing.Walked = count;
        int loopCount = activeIndices is not null ? activeCount : count;
        long lastTimestamp = timePerCreature ? Stopwatch.GetTimestamp() : 0;
        bool skippedSinceLastTimestamp = false;
        for (int walked = 0; walked < loopCount; walked++)
        {
            int index = activeIndices is not null ? activeIndices[walked] : (start + walked) % count;
            Creature creature = _scratchCreatures[index];
            // vmangos Map::UpdateActiveCellsSynch: only the cells around players and active objects are visited.
            if (activeIndices is null && activeCellsOnly && !Map.Grids.IsInActiveCell(creature) && !IsAlwaysUpdated(creature))
            {
                // vmangos Unit::Update -> UpdateSplineMovement(p_time), Unit.cpp:322,10632:
                // splines advance only by an active update's delta, never by time spent in an inactive cell.
                // Keep all map-clock readers (including stop and observer catch-up) on the same frozen position.
                creature.Spline?.Pause(_clockMs, diffMs);
                creature.IsFrozenOutsideActiveCells = true;
                SkippedInactiveCreatures++;
                skippedSinceLastTimestamp = true;
                continue;
            }

            creature.IsFrozenOutsideActiveCells = false;
            _timing.Updated++;
            if (timePerCreature && skippedSinceLastTimestamp)
            {
                lastTimestamp = Stopwatch.GetTimestamp();
                skippedSinceLastTimestamp = false;
            }

            long creatureStart = lastTimestamp;
            // WorldObject::UpdateHelper::UpdateRealTime: the update after a pause gets the time since the last one (update_diff),
            // which the corpse timers count down (Creature::Update CORPSE, Creature.cpp:901-914); motion and AI get the tick.
            uint sinceLast = creature.LastUpdateClockMs < 0 ? diffMs : (uint)Math.Min(_clockMs - creature.LastUpdateClockMs, uint.MaxValue);
            creature.LastUpdateClockMs = _clockMs;
            try
            {
                switch (creature.DeathState)
                {
                    case CreatureDeathState.Alive:
                        if (marks is null || marks[index] != SplinePassMark.Advanced)
                        {
                            creature.AdvanceSpline(_clockMs, now);
                        }

                        if (creature.PacifiedMs > 0)
                        {
                            creature.PacifiedMs = creature.PacifiedMs <= diffMs ? 0 : creature.PacifiedMs - diffMs; // vmangos Creature::Update
                        }

                        // A possessed creature is moved by its possessor's client (vmangos HandleMoverRelocation): its AI still runs (PetAI's
                        // possessed branch keeps the melee victim) but no leash or generator of its own moves it. Fear and confuse do: vmangos
                        // ModConfuseSpell starts their generator whoever controls the unit, UpdateMotion runs it (UNIT_STATE_POSSESSED is not in
                        // UNIT_STATE_CAN_NOT_MOVE) and the client gets the control back when it ends (UpdateControl).
                        if ((creature.UnitFlags & UnitFlags.Possessed) != 0)
                        {
                            TimedUpdateAi(creature, diffMs, timePerCreature, ref lastTimestamp);
                            if (creature.DeathState == CreatureDeathState.Alive && _creatures.ContainsKey(creature.Guid))
                            {
                                UpdatePossessedCrowdControl(creature, diffMs);
                            }

                            break;
                        }

                        // vmangos Creature::Update (Creature.cpp:1037-1043): the leash or the 24 s unreachable count evades, and a creature
                        // that has not reached its victim for 3 s does not run its AI.
                        if (!CheckHardLeash(creature, diffMs) && !UpdateUnreachableTarget(creature, diffMs))
                        {
                            TimedUpdateAi(creature, diffMs, timePerCreature, ref lastTimestamp);
                        }

                        if (creature.DeathState != CreatureDeathState.Alive || !_creatures.ContainsKey(creature.Guid))
                        {
                            break; // the script killed or despawned it
                        }

                        SyncCrowdControlMovement(creature);

                        // The default (idle/random/waypoint) generator does not run in combat;
                        // chase, flee, home and point generators on top of it always do.
                        if (!creature.Combat.IsInCombat || !ReferenceEquals(creature.Motion.Top, creature.Motion.Default))
                        {
                            if (creature.Motion.CurrentType == MovementGeneratorType.Random)
                            {
                                _randomMotionScratch.Add(creature);
                            }
                            else if (timePerCreature)
                            {
                                long motionStart = lastTimestamp;
                                creature.Motion.Update(diffMs);
                                lastTimestamp = Stopwatch.GetTimestamp();
                                _timing.Motion += lastTimestamp - motionStart;
                            }
                            else
                            {
                                creature.Motion.Update(diffMs);
                            }
                        }

                        CheckNoMeleePanicEnded(creature);
                        CheckCalledGuard(creature);
                        CheckAliveCreatureAnomalies(creature, diffMs);

                        break;

                    case CreatureDeathState.Corpse:
                        // vmangos Creature::Update CORPSE: decay over, or a DB spawn's respawn time reached.
                        bool respawnDue = creature.Spawn is not null && creature.RespawnAtMs <= _clockMs;
                        creature.SkinningForOthersMs = creature.SkinningForOthersMs <= sinceLast ? 0 : creature.SkinningForOthersMs - sinceLast; // Creature.cpp:911-914
                        if (creature.CorpseDecayMs <= sinceLast || respawnDue)
                        {
                            RemoveCorpse(creature);
                        }
                        else
                        {
                            creature.CorpseDecayMs -= sinceLast;
                        }

                        break;

                    case CreatureDeathState.Dead:
                        if (creature.Spawn is not null && creature.RespawnAtMs <= _clockMs && PoolKeepsOnRespawn(creature) && LinkAllowsSpawn(creature))
                        {
                            Respawn(creature);
                        }
                        else
                        {
                            CheckRespawnStuck(creature, diffMs, progressedThisTick: false);
                        }

                        break;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("creature.update", ex);
                _logger.LogFault(
                    "CreatureMapSystem.UpdateCreature",
                    ex,
                    "Creature update fault for {Guid} (entry={Entry}, name={Name}, map={MapId}, pos=({X:F2},{Y:F2},{Z:F2}))",
                    creature.Guid,
                    creature.Entry,
                    creature.Template.Name,
                    Map.MapId,
                    creature.X,
                    creature.Y,
                    creature.Z);
            }

            if (timePerCreature)
            {
                if (lastTimestamp == creatureStart)
                {
                    lastTimestamp = Stopwatch.GetTimestamp();
                }

                long creatureTicks = Math.Max(1L, lastTimestamp - creatureStart);
                if (creatureTicks > _timing.SlowestCreatureTicks)
                {
                    _timing.SlowestCreatureTicks = creatureTicks;
                    _timing.SlowestEntry = creature.Entry;
                    _timing.SlowestGenerator = creature.Motion.Top?.GetType();
                }
            }
        }

        if (_randomMotionScratch.Count > 256 && PathBudget.MaxSearches > 8)
        {
            _inCrowdedRandomPass = true;
            _randomPassSearchLimit = Math.Min(PathBudget.MaxSearches, _pathSearchesThisTick + 8);
        }

        if (splitRandomMotion)
        {
            // The caller runs the Random motions in two passes (UpdateCreaturesInPasses): the plain wanderers, with their casting state
            // read now (the spell system's state table is world-wide), and the rest left in _randomMotionScratch.
            int kept = 0;
            for (int i = 0; i < _randomMotionScratch.Count; i++)
            {
                Creature creature = _randomMotionScratch[i];
                if (CanMoveUngated(creature))
                {
                    _ungatedRandom.Add(creature);
                    _ungatedRandomCasting.Add(_ai.Spells?.IsCasting(creature) ?? false);
                }
                else
                {
                    _randomMotionScratch[kept++] = creature;
                }
            }

            _randomMotionScratch.RemoveRange(kept, _randomMotionScratch.Count - kept);
            return;
        }

        try
        {
            foreach (Creature creature in _randomMotionScratch)
            {
                UpdateRandomMotion(creature, diffMs);
            }
        }
        finally
        {
            _inCrowdedRandomPass = false;
            _randomMotionScratch.Clear();
        }
    }

    /// <summary><see cref="UpdateAi"/>, its time added to the update's AI stage.</summary>
    private void TimedUpdateAi(Creature creature, uint diffMs, bool timePerCreature, ref long lastTimestamp)
    {
        if (!timePerCreature)
        {
            UpdateAi(creature, diffMs);
            return;
        }

        long started = Stopwatch.GetTimestamp();
        UpdateAi(creature, diffMs);
        lastTimestamp = Stopwatch.GetTimestamp();
        _timing.Ai += lastTimestamp - started;
    }

    /// <summary>
    /// The crowd-control movement hook (vmangos Unit::SetFeared / SetConfused start MoveFleeing / MoveConfused when the aura
    /// lands and drop the generator when it ends): the unit flags the auras own (<c>CcState.RefreshFear</c>) say what the
    /// creature's movement stack must run, so a fear or confuse needs no call from the spell system into the map.
    /// A creature without either flag and without such a generator costs two flag tests. World thread.
    /// </summary>
    private static void SyncCrowdControlMovement(Creature creature)
    {
        UnitFlags flags = creature.UnitFlags;
        CrowdControlMovement wanted = (flags & UnitFlags.Fleeing) != 0 ? CrowdControlMovement.Fear
            : (flags & UnitFlags.Confused) != 0 ? CrowdControlMovement.Confuse
            : CrowdControlMovement.None;
        MotionMaster motion = creature.Motion;
        if (wanted == motion.ActiveCrowdControl)
        {
            return;
        }

        motion.SyncCrowdControl(wanted, wanted == CrowdControlMovement.Fear ? CcState.FearSource(creature) : null);
    }

    /// <summary>
    /// The movement a possessed creature still gets from the server: the fear or confuse generator while the aura flag holds. Nothing
    /// else on its stack is updated (vmangos MotionMaster::Initialize installs no default generator for a possessed unit, and the chase
    /// and follow generators stop on UNIT_STATE_POSSESSED), and ending the crowd control does not resume the default
    /// (<see cref="MotionMaster"/> keeps it interrupted while possessed).
    /// </summary>
    private static void UpdatePossessedCrowdControl(Creature creature, uint diffMs)
    {
        SyncCrowdControlMovement(creature);
        MotionMaster motion = creature.Motion;
        if (motion.ActiveCrowdControl != CrowdControlMovement.None && motion.IsCrowdControlOnTop)
        {
            motion.Update(diffMs);
        }
    }

    /// <summary>
    /// After the visibility pass: every creature a player started to observe since the last call is remembered, and one that is
    /// moving gets a catch-up SMSG_MONSTER_MOVE next tick (<see cref="SendCatchUpMoves"/>), because the create block does not
    /// carry the spline. A player observes a creature exactly when the creature's GUID is in its visible set (the map keeps
    /// <see cref="Map.ObserversOf"/> as the inverse of <see cref="Player.VisibleObjects"/>), so only the visible sets are walked,
    /// and only for players whose set changed (<see cref="Player.VisibilityVersion"/>) or who forgot a creature
    /// (<see cref="ForgetObservers"/>) since they were last looked at; a player with neither has nothing new to find. New catch-ups
    /// of one player are queued in map-join order.
    /// </summary>
    private void CaptureNewObservers()
    {
        foreach (Player player in Map.PlayerValues)
        {
            if (!_seen.TryGetValue(player, out SeenCreatures? seen))
            {
                _seen[player] = seen = new SeenCreatures();
            }
            else if (!seen.Dirty && seen.Version == player.VisibilityVersion)
            {
                continue;
            }

            seen.Dirty = false;
            seen.Version = player.VisibilityVersion;
            ObserverCaptureVisits += seen.Creatures.Count + player.VisibleObjects.Count;
            foreach (Creature creature in seen.Creatures)
            {
                if (!ReferenceEquals(creature.Map, Map) || !player.VisibleObjects.Contains(creature.Guid))
                {
                    _seenScratch.Add(creature);
                }
            }

            foreach (Creature gone in _seenScratch)
            {
                seen.Creatures.Remove(gone);
            }

            _seenScratch.Clear();
            foreach (ObjectGuid guid in player.VisibleObjects)
            {
                if (guid.IsPlayer)
                {
                    continue;
                }

                if (_creatures.TryGetValue(guid, out Creature? creature) && seen.Creatures.Add(creature) && creature.Spline is not null)
                {
                    _seenScratch.Add(creature);
                }
            }

            if (_seenScratch.Count > 1)
            {
                _seenScratch.Sort(static (a, b) => a.MapSequence.CompareTo(b.MapSequence));
            }

            foreach (Creature creature in _seenScratch)
            {
                _catchUp.Add((player, creature, creature.Spline!.Id));
            }

            _seenScratch.Clear();
        }

        // IsNewObject is only true from AddToWorld to here (creatures marked new are listed there).
        foreach (Creature creature in _newCreatures)
        {
            if (_creatures.TryGetValue(creature.Guid, out Creature? current) && ReferenceEquals(current, creature))
            {
                creature.IsNewObject = false;
            }
        }

        _newCreatures.Clear();
    }

    /// <summary>The creatures <paramref name="player"/> was last seen observing (tests; world thread).</summary>
    internal IReadOnlyCollection<Creature> SeenBy(Player player)
        => _seen.TryGetValue(player, out SeenCreatures? seen) ? seen.Creatures : [];

    /// <summary>The catch-up moves queued for the next tick (tests; world thread).</summary>
    internal IReadOnlyList<(Player Viewer, Creature Creature, uint SplineId)> QueuedCatchUps => _catchUp;

    /// <summary>Remembered creatures and visible GUIDs <see cref="CaptureNewObservers"/> looked at (world-thread work counter for tests).</summary>
    internal long ObserverCaptureVisits { get; private set; }

    /// <summary>The creatures a player was seen observing (<see cref="CaptureNewObservers"/>) and when it was last looked at.</summary>
    private sealed class SeenCreatures
    {
        public HashSet<Creature> Creatures { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>The player's <see cref="Player.VisibilityVersion"/> when it was last looked at.</summary>
        public int Version { get; set; }

        /// <summary>A creature was forgotten since (<see cref="ForgetObservers"/>): look again even if the visible set is unchanged.</summary>
        public bool Dirty { get; set; }
    }

}
