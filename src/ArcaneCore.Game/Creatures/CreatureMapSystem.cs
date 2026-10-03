using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The creatures of one map: database spawns bucketed by grid, grids loaded around players and
/// unloaded after they leave, per-player visibility, death/corpse/respawn timers and idle
/// movement. Attached to its <see cref="Map"/> as an <see cref="IMapUpdater"/>.
/// <para>
/// Each update runs (0) catch-up moves for clients that received a moving creature's create
/// block last tick, (1) grid load/unload, (2) creature timers and movement, (3) visibility —
/// all before the map's values/flush phases, so the blocks queued here go out this tick.
/// </para>
/// <para>
/// Grids follow vmangos GridDefines.h (64×64 grids of 533.33333 yd, ComputeGridPair). Within a
/// grid the visit is a plain list (vmangos further splits a grid into 16×16 cells, cmangos into
/// 8×8; the cell index belongs to the grid/map area). Visibility uses
/// <see cref="Map.IsWithinVisibilityDistance"/>, the same rule players use.
/// </para>
/// Thread affinity: world thread only.
/// </summary>
public sealed class CreatureMapSystem : IMapUpdater, ICreatureMover
{
    /// <summary>vmangos MAX_NUMBER_OF_GRIDS.</summary>
    public const int MaxNumberOfGrids = 64;

    /// <summary>vmangos SIZE_OF_GRIDS.</summary>
    public const float SizeOfGrids = 533.33333f;

    /// <summary>vmangos CENTER_GRID_ID.</summary>
    public const int CenterGridId = MaxNumberOfGrids / 2;

    /// <summary>vmangos CENTER_GRID_OFFSET.</summary>
    public const float CenterGridOffset = SizeOfGrids / 2;

    /// <summary>
    /// Extra distance around a player's visibility range within which grids are kept loaded and
    /// searched, so creatures that wander out of their home grid are still found.
    /// </summary>
    public const float GridSearchMargin = 50f;

    private readonly CreatureContent _content;
    private readonly CreatureOptions _options;
    private readonly ICreatureHeightProvider _height;
    private readonly Random _random;
    private readonly Func<uint> _serverTime;
    private readonly ILogger _logger;

    private readonly Dictionary<GridCoord, List<CreatureSpawn>> _spawnsByGrid = [];
    private readonly Dictionary<GridCoord, LoadedGrid> _grids = [];
    private readonly Dictionary<uint, long> _respawnAt = [];
    private readonly Dictionary<ObjectGuid, Creature> _creatures = [];
    private readonly Dictionary<Player, HashSet<Creature>> _seen = [];
    private readonly List<(Player Viewer, Creature Creature, uint SplineId)> _catchUp = [];
    private readonly HashSet<uint> _warnedMissingTemplates = [];
    private readonly List<GridCoord> _scratchGrids = [];
    private readonly List<Creature> _scratchCreatures = [];

    private long _clockMs;
    private uint _splineCounter;
    private uint _nextTemporaryCounter;

    public CreatureMapSystem(
        Map map, CreatureContent content, CreatureOptions? options = null, ICreatureHeightProvider? height = null,
        Random? random = null, Func<uint>? serverTime = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(content);
        Map = map;
        _content = content;
        _options = options ?? new CreatureOptions();
        _height = height ?? NoTerrainHeight.Instance;
        _random = random ?? new Random();
        _serverTime = serverTime ?? (() => unchecked((uint)_clockMs));
        _logger = logger ?? NullLogger.Instance;

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
        UpdateGrids();
        UpdateCreatures(diffMs);
        UpdateVisibility();
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
        _seen.Remove(player);
        _catchUp.RemoveAll(c => ReferenceEquals(c.Viewer, player));
    }

    // --- API for other systems (combat, GM commands, scripts) ---------------------------------

    /// <summary>
    /// Kill a creature (vmangos Creature::SetDeathState(JUST_DIED) → CORPSE): health 0, NPC flags
    /// cleared, target cleared, movement stopped. The corpse decays after the rank's corpse delay
    /// (or the template's) and the respawn time is death + urand(spawntimesecsmin, max).
    /// </summary>
    public void KillCreature(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (creature.DeathState != CreatureDeathState.Alive || !_creatures.ContainsKey(creature.Guid))
        {
            return;
        }

        StopMoving(creature);
        creature.Health = 0;
        creature.NpcFlags = 0;
        creature.Target = default;
        creature.DeathState = CreatureDeathState.Corpse;
        creature.CorpseDecayMs = creature.CorpseDecaySeconds(_options) * 1000;
        creature.RespawnAtMs = _clockMs + (creature.NextRespawnDelaySeconds() * 1000L);
    }

    /// <summary>Respawn a dead creature now (GM command / script).</summary>
    public void ForceRespawn(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (creature.DeathState == CreatureDeathState.Alive || !_creatures.ContainsKey(creature.Guid))
        {
            return;
        }

        if (creature.DeathState == CreatureDeathState.Corpse)
        {
            RemoveCorpse(creature);
        }

        Respawn(creature);
    }

    /// <summary>
    /// Put a creature that is not in the database into the map at a position (GM .npc add,
    /// summons). It is announced with UPDATETYPE_CREATE_OBJECT2, as vmangos Map::Add does, and
    /// does not respawn after death; it disappears when its grid unloads.
    /// </summary>
    public Creature SpawnTemporary(CreatureTemplate template, float x, float y, float z, float orientation)
    {
        ArgumentNullException.ThrowIfNull(template);
        var creature = new Creature(_nextTemporaryCounter++ & 0x00FFFFFF, template, spawn: null, _content, _random);
        creature.MapId = Map.MapId;
        creature.SetHome(new CreatureHome(x, y, z, orientation));
        creature.ResetToHome(_serverTime());
        creature.IsNewObject = true;

        GridCoord grid = ComputeGrid(x, y);
        if (!_grids.TryGetValue(grid, out LoadedGrid? loaded))
        {
            loaded = LoadGrid(grid);
        }

        AddToWorld(creature, loaded);
        return creature;
    }

    /// <summary>Remove a creature from the map at once (destroyed for every client that sees it).</summary>
    public void Despawn(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!_creatures.ContainsKey(creature.Guid))
        {
            return;
        }

        HideFromAll(creature);
        foreach (LoadedGrid grid in _grids.Values)
        {
            grid.Creatures.Remove(creature);
        }

        RemoveFromWorld(creature);
    }

    // --- ICreatureMover -----------------------------------------------------------------------

    void ICreatureMover.MoveTo(Creature creature, float x, float y, float z, bool run, float? finalOrientation)
        => MoveTo(creature, x, y, z, run, finalOrientation);

    double ICreatureMover.NextDouble() => _random.NextDouble();

    int ICreatureMover.URand(int min, int max) => min >= max ? min : _random.Next(min, max + 1);

    float? ICreatureMover.GetHeight(uint mapId, float x, float y, float z) => _height.GetHeight(mapId, x, y, z);

    /// <summary>Launch a spline from the creature's current position and tell its observers.</summary>
    public void MoveTo(Creature creature, float x, float y, float z, bool run, float? finalOrientation)
    {
        ArgumentNullException.ThrowIfNull(creature);
        uint id = ++_splineCounter;
        float sx = creature.X;
        float sy = creature.Y;
        float sz = creature.Z;
        CreatureSpline spline = creature.StartSpline(x, y, z, run, finalOrientation, id, _clockMs);
        byte[] packet = CreatureMovePackets.BuildMove(creature.Guid, sx, sy, sz, id, finalOrientation, run, spline.DurationMs, x, y, z);
        Map.BroadcastToObservers(creature, WorldOpcode.SmsgMonsterMove, packet);
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
            byte[] packet = CreatureMovePackets.BuildMove(
                creature.Guid, x, y, z, spline.Id, spline.FinalOrientation, spline.Run, remaining, spline.EndX, spline.EndY, spline.EndZ);
            viewer.Session.Send(WorldOpcode.SmsgMonsterMove, packet);
        }

        _catchUp.Clear();
    }

    /// <summary>Load the grids around every player; unload grids nobody has been near for the delay (vmangos Map::UnloadGrid).</summary>
    private void UpdateGrids()
    {
        foreach (Player player in Map.Players)
        {
            CollectGridsAround(player.X, player.Y);
            foreach (GridCoord coord in _scratchGrids)
            {
                if (!_grids.TryGetValue(coord, out LoadedGrid? grid))
                {
                    grid = LoadGrid(coord);
                }

                grid.LastActiveMs = _clockMs;
            }
        }

        if (!_options.GridUnload)
        {
            return;
        }

        List<GridCoord>? expired = null;
        foreach ((GridCoord coord, LoadedGrid grid) in _grids)
        {
            if (_clockMs - grid.LastActiveMs >= _options.GridUnloadDelayMs)
            {
                (expired ??= []).Add(coord);
            }
        }

        if (expired is not null)
        {
            foreach (GridCoord coord in expired)
            {
                UnloadGrid(coord);
            }
        }
    }

    private void UpdateCreatures(uint diffMs)
    {
        uint now = _serverTime();
        foreach (LoadedGrid grid in _grids.Values)
        {
            // A temporary creature's corpse removal takes it out of the list: iterate a copy.
            _scratchCreatures.Clear();
            _scratchCreatures.AddRange(grid.Creatures);
            foreach (Creature creature in _scratchCreatures)
            {
                switch (creature.DeathState)
                {
                    case CreatureDeathState.Alive:
                        creature.AdvanceSpline(_clockMs, now);
                        if (_options.MovementEnabled)
                        {
                            creature.MovementGenerator?.Update(creature, this, diffMs);
                        }

                        break;

                    case CreatureDeathState.Corpse:
                        // vmangos Creature::Update CORPSE: decay over, or a DB spawn's respawn time reached.
                        bool respawnDue = creature.Spawn is not null && creature.RespawnAtMs <= _clockMs;
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
                        if (creature.Spawn is not null && creature.RespawnAtMs <= _clockMs)
                        {
                            Respawn(creature);
                        }

                        break;
                }
            }
        }
    }

    /// <summary>
    /// vmangos Player::UpdateVisibilityOf&lt;Creature&gt; for every player and the creatures of the
    /// grids around it: create on entering range, out-of-range on leaving; dead creatures (corpse
    /// removed) are never visible (Creature::IsVisibleInGridForPlayer).
    /// </summary>
    private void UpdateVisibility()
    {
        uint now = _serverTime();
        foreach (Player player in Map.Players)
        {
            if (!_seen.TryGetValue(player, out HashSet<Creature>? seen))
            {
                _seen[player] = seen = [];
            }

            // Leaving range (or the world, or dying past the corpse).
            List<Creature>? gone = null;
            foreach (Creature creature in seen)
            {
                if (!_creatures.ContainsKey(creature.Guid) || creature.DeathState == CreatureDeathState.Dead
                    || !Map.IsWithinVisibilityDistance(player, creature, alreadyVisible: true))
                {
                    (gone ??= []).Add(creature);
                }
            }

            if (gone is not null)
            {
                foreach (Creature creature in gone)
                {
                    seen.Remove(creature);
                    if (player.VisibleObjects.Remove(creature.Guid))
                    {
                        player.PendingUpdates.AddOutOfRange(creature.Guid);
                    }
                }
            }

            // Entering range.
            CollectGridsAround(player.X, player.Y);
            foreach (GridCoord coord in _scratchGrids)
            {
                if (!_grids.TryGetValue(coord, out LoadedGrid? grid))
                {
                    continue;
                }

                foreach (Creature creature in grid.Creatures)
                {
                    if (creature.DeathState == CreatureDeathState.Dead || seen.Contains(creature)
                        || !Map.IsWithinVisibilityDistance(player, creature, alreadyVisible: false))
                    {
                        continue;
                    }

                    seen.Add(creature);
                    player.VisibleObjects.Add(creature.Guid);
                    PacketWriter block = player.PendingUpdates.BeginBlock();
                    UpdateBlockWriter.WriteCreateBlock(block, creature, player, creature.IsNewObject, now);
                    player.PendingUpdates.EndBlock();

                    if (creature.Spline is { } spline)
                    {
                        _catchUp.Add((player, creature, spline.Id));
                    }
                }
            }
        }

        // vmangos Map::Add: SetIsNewObject(true) only for the visibility pass of the add itself.
        foreach (Creature creature in _creatures.Values)
        {
            creature.IsNewObject = false;
        }
    }

    // --- grids & life cycle -------------------------------------------------------------------

    /// <summary>The grids overlapping the square of visibility range + margin around (x, y), into <see cref="_scratchGrids"/>.</summary>
    private void CollectGridsAround(float x, float y)
    {
        _scratchGrids.Clear();
        float reach = Map.VisibilityRange + GridSearchMargin;
        GridCoord min = ComputeGrid(x - reach, y - reach);
        GridCoord max = ComputeGrid(x + reach, y + reach);
        for (int gx = min.X; gx <= max.X; gx++)
        {
            for (int gy = min.Y; gy <= max.Y; gy++)
            {
                _scratchGrids.Add(new GridCoord(gx, gy));
            }
        }
    }

    /// <summary>vmangos ObjectGridLoader: create the grid's spawns (dead ones keep their respawn time).</summary>
    private LoadedGrid LoadGrid(GridCoord coord)
    {
        var grid = new LoadedGrid { LastActiveMs = _clockMs };
        _grids[coord] = grid;
        if (!_spawnsByGrid.TryGetValue(coord, out List<CreatureSpawn>? spawns))
        {
            return grid;
        }

        foreach (CreatureSpawn spawn in spawns)
        {
            CreatureTemplate? template = _content.FindTemplate(spawn.Entry);
            if (template is null)
            {
                if (_warnedMissingTemplates.Add(spawn.Entry))
                {
                    _logger.LogWarning("creature spawn {Guid} on map {MapId} uses missing creature_template {Entry}; skipped", spawn.Guid, Map.MapId, spawn.Entry);
                }

                continue;
            }

            var creature = new Creature(spawn.Guid, template, spawn, _content, _random);
            if (_respawnAt.Remove(spawn.Guid, out long respawnAt) && respawnAt > _clockMs)
            {
                creature.Health = 0;
                creature.NpcFlags = 0;
                creature.DeathState = CreatureDeathState.Dead;
                creature.RespawnAtMs = respawnAt;
            }

            AddToWorld(creature, grid);
        }

        return grid;
    }

    private void UnloadGrid(GridCoord coord)
    {
        if (!_grids.Remove(coord, out LoadedGrid? grid))
        {
            return;
        }

        foreach (Creature creature in grid.Creatures)
        {
            HideFromAll(creature);
            if (creature.Spawn is not null && creature.DeathState != CreatureDeathState.Alive)
            {
                _respawnAt[creature.Spawn.Guid] = creature.RespawnAtMs;
            }

            RemoveFromWorld(creature);
        }
    }

    private void AddToWorld(Creature creature, LoadedGrid grid)
    {
        creature.MapId = Map.MapId;
        creature.Map = Map;
        creature.WalkSpeed = creature.CreatureWalkSpeed;
        creature.RunSpeed = creature.CreatureRunSpeed;
        creature.ClearChangedFields();
        creature.MovementGenerator = CreateMovementGenerator(creature);
        _creatures[creature.Guid] = creature;
        grid.Creatures.Add(creature);
        if (creature.DeathState == CreatureDeathState.Alive && _options.MovementEnabled)
        {
            creature.MovementGenerator.Reset(creature, this);
        }
    }

    private void RemoveFromWorld(Creature creature)
    {
        _creatures.Remove(creature.Guid);
        creature.Map = null;
    }

    private ICreatureMovementGenerator CreateMovementGenerator(Creature creature)
    {
        switch (creature.MovementType)
        {
            case CreatureMovementType.Random:
                return new RandomMovementGenerator();

            case CreatureMovementType.Waypoint:
                IReadOnlyList<CreatureWaypoint> path = creature.Spawn is null ? [] : _content.GetWaypoints(creature.Spawn.Guid);
                if (path.Count == 0)
                {
                    _logger.LogWarning("{Creature} has waypoint movement but no creature_movement path; idling", creature.Guid);
                    return IdleMovementGenerator.Instance;
                }

                return new WaypointMovementGenerator(path);

            default:
                return IdleMovementGenerator.Instance;
        }
    }

    /// <summary>vmangos Creature::RemoveCorpse: the corpse disappears and the creature returns to its spawn point.</summary>
    private void RemoveCorpse(Creature creature)
    {
        creature.DeathState = CreatureDeathState.Dead;
        creature.CorpseDecayMs = 0;
        HideFromAll(creature);
        creature.ResetToHome(_serverTime());
        if (creature.Spawn is null)
        {
            // A temporary creature does not respawn (vmangos TemporarySummon despawns).
            foreach (LoadedGrid grid in _grids.Values)
            {
                grid.Creatures.Remove(creature);
            }

            RemoveFromWorld(creature);
        }
    }

    /// <summary>vmangos Creature::Update DEAD → respawn: fields re-initialized (level, display, health), ALIVE, movement restarted.</summary>
    private void Respawn(Creature creature)
    {
        creature.InitializeFields();
        creature.DeathState = CreatureDeathState.Alive;
        creature.RespawnAtMs = 0;
        creature.ResetToHome(_serverTime());

        // Invisible until now, so nobody needs a values update for the re-initialization.
        creature.ClearChangedFields();
        if (_options.MovementEnabled)
        {
            creature.MovementGenerator?.Reset(creature, this);
        }
    }

    /// <summary>Send out-of-range for <paramref name="creature"/> to everyone who sees it.</summary>
    private void HideFromAll(Creature creature)
    {
        foreach ((Player player, HashSet<Creature> seen) in _seen)
        {
            if (seen.Remove(creature) && player.VisibleObjects.Remove(creature.Guid))
            {
                player.PendingUpdates.AddOutOfRange(creature.Guid);
            }
        }

        _catchUp.RemoveAll(c => ReferenceEquals(c.Creature, creature));
    }

    private sealed class LoadedGrid
    {
        public List<Creature> Creatures { get; } = [];

        public long LastActiveMs { get; set; }
    }
}

/// <summary>A grid coordinate (vmangos GridPair).</summary>
public readonly record struct GridCoord(int X, int Y);
