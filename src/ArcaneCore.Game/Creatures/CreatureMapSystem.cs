using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells.Rules.CrowdControl;
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
public sealed partial class CreatureMapSystem : IMapUpdater, ICreatureMover
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

    private long _clockMs;
    private uint _splineCounter;
    private uint _nextTemporaryCounter;

    public CreatureMapSystem(
        Map map, CreatureContent content, CreatureOptions? options = null, ICreatureHeightProvider? height = null,
        Random? random = null, Func<uint>? serverTime = null, ILogger? logger = null, CreatureAiServices? aiServices = null,
        ICreatureRespawnPersistence? respawnPersistence = null, IRespawnClock? respawnClock = null,
        Func<uint, CreatureDisplayModelMetadata?>? displayModelResolver = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(content);
        Map = map;
        _content = content;
        _options = options ?? new CreatureOptions();
        _height = height ?? new MapCreatureHeightProvider(map);
        _random = random ?? new Random();
        _serverTime = serverTime ?? (() => unchecked((uint)_clockMs));
        _logger = logger ?? NullLogger.Instance;
        _displayModelResolver = displayModelResolver;
        _ai = aiServices ?? CreatureAiServices.Default;
        _persistence = respawnPersistence;
        _respawnClock = respawnClock ?? SystemRespawnClock.Instance;
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
        SendCatchUpMoves();
        UpdateLinkedFollowers();
        UpdateCreatures(diffMs);
        UpdateSpawnGroups();
        UpdatePendingAi();
        UpdateRelayScripts();
        UpdateForcedDespawns();
        Map.RunAfterUpdate(_captureNewObservers ??= CaptureNewObservers);
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
        _seen.Remove(player);
        _catchUp.RemoveAll(c => ReferenceEquals(c.Viewer, player));
    }

    // --- ICreatureMover -----------------------------------------------------------------------

    void ICreatureMover.MoveTo(Creature creature, float x, float y, float z, bool run, float? finalOrientation)
        => MoveTo(creature, x, y, z, run, finalOrientation);

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

    /// <summary>Launch a spline from the creature's current position and tell its observers.</summary>
    public void MoveTo(Creature creature, float x, float y, float z, bool run, float? finalOrientation)
        => MovePath(creature, [new Vector3(x, y, z)], run, finalOrientation is { } angle ? SplineFacing.ToAngle(angle) : SplineFacing.None);

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

        uint id = ++_splineCounter;
        var start = new Vector3(creature.X, creature.Y, creature.Z);
        CreatureSpline spline = creature.StartSpline(path, run, facing, id, _clockMs);
        byte[] packet = CreatureMovePackets.BuildPath(creature.Guid, start, id, facing, run, spline.DurationMs, spline.Points, _options.Movement.MonsterMoveOffsetBase);
        SyncWalkMode(creature, run);
        Map.BroadcastToObservers(creature, WorldOpcode.SmsgMonsterMove, packet);
    }

    /// <summary>
    /// The path from the creature to <paramref name="destination"/>: every corner after the start,
    /// the end last. It asks the map's <see cref="IPathfinder"/> (<c>map.Collision</c>,
    /// feat/vmap-los: the navmesh when mmaps are installed, else a straight line). When there is
    /// no usable path (<see cref="PathType.NoPath"/>) the creature goes straight, as vmangos chase
    /// does with <c>PATHFIND_NOPATH</c> outside instances.
    /// </summary>
    public IReadOnlyList<Vector3> FindPath(Creature creature, Vector3 destination)
    {
        ArgumentNullException.ThrowIfNull(creature);
        var start = new Vector3(creature.X, creature.Y, creature.Z);
        PathResult path = Map.Collision.FindPath(start, destination);
        if (!path.HasPath)
        {
            return [destination];
        }

        // The whole path goes into one multi-point spline (vmangos MoveSplineInit::MovebyPath),
        // so chase, flee, home and point moves do not re-launch at every corner.
        var corners = new List<Vector3>(path.Points.Count - 1);
        for (int i = 1; i < path.Points.Count; i++)
        {
            corners.Add(path.Points[i]);
        }

        return corners;
    }

    /// <summary>Stop a moving creature where it is (vmangos Unit::StopMoving → MoveSplineInit::Stop).</summary>
    public void StopMoving(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
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

    private void UpdateCreatures(uint diffMs)
    {
        uint now = _serverTime();
        // Live position may be outside the spawn's original grid. Snapshot ownership rather
        // than spawn buckets so a moved creature is updated once and survives home-grid unload.
        _scratchCreatures.Clear();
        _scratchCreatures.AddRange(_creatures.Values);
        foreach (Creature creature in _scratchCreatures)
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
                        UpdateAi(creature, diffMs);
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
                        UpdateAi(creature, diffMs);
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
                        creature.Motion.Update(diffMs);
                    }

                    CheckNoMeleePanicEnded(creature);
                    CheckCalledGuard(creature);

                    break;

                case CreatureDeathState.Corpse:
                    // vmangos Creature::Update CORPSE: decay over, or a DB spawn's respawn time reached.
                    bool respawnDue = creature.Spawn is not null && creature.RespawnAtMs <= _clockMs;
                    creature.SkinningForOthersMs = creature.SkinningForOthersMs <= diffMs ? 0 : creature.SkinningForOthersMs - diffMs; // Creature.cpp:911-914
                    if (creature.CorpseDecayMs <= diffMs || respawnDue)
                    {
                        RemoveCorpse(creature);
                    }
                    else
                    {
                        creature.CorpseDecayMs -= diffMs;
                    }

                    break;

                case CreatureDeathState.Dead:
                    if (creature.Spawn is not null && creature.RespawnAtMs <= _clockMs && PoolKeepsOnRespawn(creature))
                    {
                        Respawn(creature);
                    }

                    break;
            }
        }
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
        foreach (Player player in Map.Players)
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
