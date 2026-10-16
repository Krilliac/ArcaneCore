using System.Diagnostics;
using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells.Rules.CrowdControl;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
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
    /// <summary>vmangos MAX_NUMBER_OF_GRIDS.</summary>
    public const int MaxNumberOfGrids = 64;

    /// <summary>vmangos SIZE_OF_GRIDS.</summary>
    public const float SizeOfGrids = 533.33333f;

    /// <summary>vmangos CENTER_GRID_ID.</summary>
    public const int CenterGridId = MaxNumberOfGrids / 2;

    /// <summary>vmangos CENTER_GRID_OFFSET.</summary>
    public const float CenterGridOffset = SizeOfGrids / 2;

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
            GridCoord grid = ComputeGrid(spawn.X, spawn.Y);
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

    public bool IsGridLoaded(float x, float y) => _grids.ContainsKey(ComputeGrid(x, y));

    /// <summary>The respawn time (on <see cref="ClockMs"/>) kept for an unloaded dead spawn.</summary>
    public long? PendingRespawnAt(uint spawnGuid) => _respawnAt.TryGetValue(spawnGuid, out long at) ? at : null;

    /// <summary>
    /// vmangos GridDefines.h ComputeGridPair: <c>int((x - CENTER_GRID_OFFSET) / SIZE_OF_GRIDS +
    /// CENTER_GRID_ID + 0.5)</c> per axis, computed in double "for the same result as MySQL".
    /// Clamped to the grid range.
    /// </summary>
    public static GridCoord ComputeGrid(float x, float y)
    {
        static int Axis(float v)
        {
            double offset = (v - (double)CenterGridOffset) / SizeOfGrids;
            int value = (int)(offset + CenterGridId + 0.5);
            return Math.Clamp(value, 0, MaxNumberOfGrids - 1);
        }

        return new GridCoord(Axis(x), Axis(y));
    }

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
        _timing = default;
        long t = Stopwatch.GetTimestamp();
        RunDeferredMoves();
        SendCatchUpMoves();
        t = Lap(t, out _timing.CatchUp);
        UpdateLinkedFollowers();
        t = Lap(t, out _timing.Linked);
        UpdateCreatures(diffMs);
        t = Lap(t, out _timing.Creatures);
        UpdateSpawnGroups();
        CheckDormantRespawnStuck(diffMs);
        t = Lap(t, out _timing.SpawnGroups);
        UpdatePendingAi();
        t = Lap(t, out _timing.PendingAi);
        UpdateStrippedFlagSafeguard(diffMs);
        UpdateRelayScripts();
        t = Lap(t, out _timing.Relay);
        UpdateForcedDespawns();
        t = Lap(t, out _timing.Despawns);
        Map.RunAfterUpdate(_captureNewObservers ??= CaptureNewObservers);
        Lap(t, out _timing.AfterUpdate);
    }

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

        public int Walked, Updated, Searches;
        public long SlowestCreatureTicks;
        public uint SlowestEntry;
        public Type? SlowestGenerator;
    }

    private UpdateTiming _timing;

    private static string TickMs(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The last update's stages for the slow-map-update line (<see cref="IMapUpdateBreakdown"/>). World thread; allocates.</summary>
    string IMapUpdateBreakdown.DescribeLastUpdate()
    {
        UpdateTiming t = _timing;
        string text = $"creatures {TickMs(t.Creatures)} ms over {t.Updated}/{t.Walked} active (ai {TickMs(t.Ai)}, motion {TickMs(t.Motion)}), path searches {TickMs(t.Path)} ms in {t.Searches} searches from any stage, "
            + $"catch-up moves {TickMs(t.CatchUp)}, linked followers {TickMs(t.Linked)}, spawn groups {TickMs(t.SpawnGroups)}, pending ai {TickMs(t.PendingAi)}, "
            + $"relay scripts {TickMs(t.Relay)}, forced despawns {TickMs(t.Despawns)}, observers {TickMs(t.AfterUpdate)}, "
            + $"path outcomes so far: {NoPathPaths} no-path, {DirectFallbackPaths} straight authored routes, {DeferredPathSearches} deferred, {HomePathTeleports} home teleports";
        if (t.SlowestCreatureTicks > 0)
        {
            text += $"; slowest creature entry {t.SlowestEntry} {TickMs(t.SlowestCreatureTicks)} ms, top generator {t.SlowestGenerator?.Name ?? "none"}";
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
        CreatureAnomalies.ReportHomeUnreachable(creature, new Vector3(x, y, z), "home return fell back to teleport");
        NearTeleport(creature, x, y, z, orientation);
    }

    void ICreatureMover.MovePath(Creature creature, IReadOnlyList<Vector3> path, bool run, SplineFacing facing)
        => MovePath(creature, path, run, facing);

    IReadOnlyList<Vector3> ICreatureMover.FindPath(Creature creature, Vector3 destination) => FindPath(creature, destination);

    bool ICreatureMover.IsCasting(Creature creature) => _ai.Spells?.IsCasting(creature) ?? false;

    CreatureMovementOptions ICreatureMover.MovementOptions => _options.Movement;

    void ICreatureMover.OnMovementFinished(Creature creature, MovementGeneratorType type, uint pointId)
        => OnMovementFinished(creature, type, pointId);

    void ICreatureMover.OnWaypointScript(Creature creature, uint scriptId, ObjectGuid targetGuid)
        => StartDbScript(DbScriptKind.CreatureMovement, scriptId, creature,
            targetGuid.IsEmpty ? creature : Map.FindObject(targetGuid));

    double ICreatureMover.NextDouble() => _random.NextDouble();

    int ICreatureMover.URand(int min, int max) => min >= max ? min : _random.Next(min, max + 1);

    float? ICreatureMover.GetHeight(uint mapId, float x, float y, float z) => _height.GetHeight(mapId, x, y, z);

    /// <summary>Wander, flee and fear choose their points with steep slopes excluded (vmangos RandomMovementGenerator.cpp:52, FleeingMovementGenerator.cpp:54).</summary>
    private static readonly PathOptions MeshPointOptions = new() { ExcludeFlags = NavTerrain.SteepSlopes };

    bool ICreatureMover.TryGetRandomMeshPoint(Creature creature, Vector3 center, float radius, out Vector3 point)
    {
        point = center;
        return Map.Collision.MeshPoints is { } mesh && mesh.TryGetRandomPointAround(creature.MapId, center, radius, _random, MeshPointOptions, out point);
    }

    bool ICreatureMover.TryRaycastMesh(Creature creature, Vector3 from, Vector3 to, out Vector3 reached)
    {
        reached = to;
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
                return [destination];
            default:
                return plan.Corners;
        }
    }

    /// <summary>
    /// Search a path and report how it ended (<see cref="PathOutcome"/>) without substituting a straight line for a missing one. The corners
    /// are the whole path in one multi-point spline (vmangos MoveSplineInit::MovebyPath) so a move does not re-launch at every corner.
    /// World thread. Allocates the corner list.
    /// </summary>
    internal PlannedPath PlanPath(Creature creature, Vector3 destination, bool toSpawn = false)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!float.IsFinite(destination.X) || !float.IsFinite(destination.Y) || !float.IsFinite(destination.Z))
        {
            CreatureAnomalies.ReportNonFinite(
                creature,
                $"Creature {creature.Guid} (entry {creature.Entry}) requested path to non-finite destination ({destination.X}, {destination.Y}, {destination.Z}) on map {Map.MapId}");
        }

        if (!HasPathBudget)
        {
            NoteDeferredPath(creature);
            return PlannedPath.Of(PathOutcome.Deferred);
        }

        var start = new Vector3(creature.X, creature.Y, creature.Z);
        PathOptions pathOptions = PathOptionsFor(creature, toSpawn);
        PathResult path = SearchPath(start, destination, pathOptions);
        if ((path.Type & PathType.TilesPending) != 0)
        {
            NoteDeferredPath(creature);
            return PlannedPath.Of(PathOutcome.Deferred);
        }

        if (!path.HasPath)
        {
            NoPathPaths++;
            CreaturePathStats.AddNoPath();
            if (toSpawn)
            {
                CreatureAnomalies.ReportHomeUnreachable(creature, destination, "no mesh path to home");
            }

            if (toSpawn || creature.Combat.IsInCombat || creature.Motion.CurrentType is not (MovementGeneratorType.Random or MovementGeneratorType.Fleeing or MovementGeneratorType.Confused))
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

        if (outcome == PathOutcome.Mesh && corners.Count >= 2 && Map.Collision.MeshPoints is { } mesh
            && (toSpawn || creature.Combat.IsInCombat || creature.Motion.CurrentType != MovementGeneratorType.Random))
        {
            corners = CreatureSplineSmoothing.VaryAndSmooth(
                creature, start, corners, mesh, _height.GetHeight, pathOptions, allowVariation: !toSpawn && !creature.Combat.IsInCombat);
        }

        return new PlannedPath(outcome, corners);
    }

    // --- per-tick creature path-search budget ------------------------------------------------------

    private int _pathSearchesThisTick;
    private long _pathNodesThisTick;

    /// <summary>
    /// The most path searches and A* nodes creature movement may spend in one map update (reset at the start of <see cref="Update"/>).
    /// A request past it is deferred to a later update (<see cref="DeferredPathSearches"/>); no motion walks straight for want of budget.
    /// </summary>
    public CreaturePathBudget PathBudget { get; set; } = CreaturePathBudget.Default;

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

    /// <summary>Whether another search fits the tick's budget. The last search may overshoot the node budget by one search's cap.</summary>
    public bool HasPathBudget => _pathSearchesThisTick < (_inCrowdedRandomPass ? _randomPassSearchLimit : PathBudget.MaxSearches) && _pathNodesThisTick < PathBudget.MaxNodes;

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

    private static PathOptions[] BuildMoverOptions(float tolerance)
    {
        var all = new PathOptions[16]; // InhabitType bits 0-2, plus 8 for a pet
        for (uint inhabit = 0; inhabit < all.Length; inhabit++)
        {
            all[inhabit] = new PathOptions { Mover = PathMover.FromInhabit(inhabit & 7, isPet: (inhabit & 8) != 0), EndTolerance = tolerance };
        }

        return all;
    }

    /// <summary>
    /// The query options of a creature: its own locomotion (<c>InhabitType</c>, vmangos PathInfo::createFilter), so a flier gets the flier
    /// exception (a hole in the mesh is a flying shortcut, not NoPath) and a walker keeps off water it cannot swim. A return home
    /// (<paramref name="toSpawn"/>) also takes vmangos' 7 yd end tolerance.
    /// </summary>
    private static PathOptions PathOptionsFor(Creature creature, bool toSpawn)
        => (toSpawn ? s_spawnOptions : s_moverOptions)[(creature.Template.InhabitType & 7) | (creature.IsPet ? 8 : 0)];

    /// <summary>One counted search through the map's pathfinder. World thread.</summary>
    private PathResult SearchPath(Vector3 start, Vector3 destination, PathOptions? options = null)
    {
        PathSearchTotals before = PathSearchCounters.Current;
        long started = Stopwatch.GetTimestamp();
        PathResult path = Map.Collision.FindPath(start, destination, options);
        _timing.Path += Stopwatch.GetTimestamp() - started;
        PathSearchTotals spent = PathSearchCounters.Current - before;
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
        => Map.Grids.IsActive(creature) || UnitControl.ViewerOf(creature) is not null || FormationOfMember(creature) is not null;

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
        if (creature.ScriptActiveObject || (creature.Template.Behaviour & ActiveOnSpawn) != 0 || FormationOfMember(creature) is not null)
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

    private void UpdateCreatures(uint diffMs)
    {
        uint now = _serverTime();
        // Live position may be outside the spawn's original grid. Snapshot ownership rather
        // than spawn buckets so a moved creature is updated once and survives home-grid unload.
        ApplyPendingActivations();
        _scratchCreatures.Clear();
        _scratchCreatures.AddRange(_creatures.Values);
        bool activeCellsOnly = Map.UpdatesActiveCellsOnly;

        // Fair deferral: the walk starts at the creature whose path search was deferred first last update, so deferred chasers are
        // served before the ones that were served (a fixed order starves the late creatures while demand stays above the budget).
        // Non-Random movement (chase, home, flee, confused, waypoint, point, follow, formation) runs in pass 1 so idle Random wanderers
        // never starve combat or evade path searches; Random movement runs in pass 2 with the remaining budget.
        _randomMotionScratch.Clear();
        int count = _scratchCreatures.Count;
        int start = _pathFirstDeferred is { } resume ? Math.Max(0, _scratchCreatures.IndexOf(resume)) : 0;
        _pathFirstDeferred = null; // deferrals from here on (this walk, or a pull before the next update) choose the next start
        for (int walked = 0; walked < count; walked++)
        {
            Creature creature = _scratchCreatures[(start + walked) % count];
            _timing.Walked++;
            // vmangos Map::UpdateActiveCellsSynch: only the cells around players and active objects are visited.
            if (activeCellsOnly && !Map.Grids.IsInActiveCell(creature) && !IsAlwaysUpdated(creature))
            {
                // vmangos Unit::Update -> UpdateSplineMovement(p_time), Unit.cpp:322,10632:
                // splines advance only by an active update's delta, never by time spent in an inactive cell.
                // Keep all map-clock readers (including stop and observer catch-up) on the same frozen position.
                creature.Spline?.Pause(_clockMs, diffMs);
                SkippedInactiveCreatures++;
                continue;
            }

            _timing.Updated++;
            long creatureStart = Stopwatch.GetTimestamp();
            // WorldObject::UpdateHelper::UpdateRealTime: the update after a pause gets the time since the last one (update_diff),
            // which the corpse timers count down (Creature::Update CORPSE, Creature.cpp:901-914); motion and AI get the tick.
            uint sinceLast = creature.LastUpdateClockMs < 0 ? diffMs : (uint)Math.Min(_clockMs - creature.LastUpdateClockMs, uint.MaxValue);
            creature.LastUpdateClockMs = _clockMs;
            try
            {
                switch (creature.DeathState)
                {
                    case CreatureDeathState.Alive:
                        creature.AdvanceSpline(_clockMs, now);
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
                            TimedUpdateAi(creature, diffMs);
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
                            TimedUpdateAi(creature, diffMs);
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
                            else
                            {
                                long motionStart = Stopwatch.GetTimestamp();
                                creature.Motion.Update(diffMs);
                                _timing.Motion += Stopwatch.GetTimestamp() - motionStart;
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

            long creatureTicks = Stopwatch.GetTimestamp() - creatureStart;
            if (creatureTicks > _timing.SlowestCreatureTicks)
            {
                _timing.SlowestCreatureTicks = creatureTicks;
                _timing.SlowestEntry = creature.Entry;
                _timing.SlowestGenerator = creature.Motion.Top?.GetType();
            }
        }

        if (_randomMotionScratch.Count > 256 && PathBudget.MaxSearches > 8)
        {
            _inCrowdedRandomPass = true;
            _randomPassSearchLimit = Math.Min(PathBudget.MaxSearches, _pathSearchesThisTick + 8);
        }

        try
        {
            foreach (Creature creature in _randomMotionScratch)
            {
                if (creature.DeathState == CreatureDeathState.Alive
                    && _creatures.ContainsKey(creature.Guid)
                    && (!creature.Combat.IsInCombat || !ReferenceEquals(creature.Motion.Top, creature.Motion.Default)))
                {
                    long motionStart = Stopwatch.GetTimestamp();
                    try
                    {
                        creature.Motion.Update(diffMs);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
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

                    long motionTicks = Stopwatch.GetTimestamp() - motionStart;
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
        finally
        {
            _inCrowdedRandomPass = false;
            _randomMotionScratch.Clear();
        }
    }

    /// <summary><see cref="UpdateAi"/>, its time added to the update's AI stage.</summary>
    private void TimedUpdateAi(Creature creature, uint diffMs)
    {
        long started = Stopwatch.GetTimestamp();
        UpdateAi(creature, diffMs);
        _timing.Ai += Stopwatch.GetTimestamp() - started;
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
    /// Snapshot the map's observer ownership after visibility and flush. A newly visible
    /// moving creature sends its remaining spline next tick, after its create block.
    /// </summary>
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

/// <summary>A grid coordinate (vmangos GridPair).</summary>
public readonly record struct GridCoord(int X, int Y);
