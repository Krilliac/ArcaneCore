using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using MapGrid = ArcaneCore.Game.Maps.Grid.Grid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Creatures;

/// <summary>Creature death, corpse, respawn and grid load/unload (vmangos Creature::Update death states, ObjectGridLoader).</summary>
public sealed partial class CreatureMapSystem
{
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

        Map.Combat.Kill(null, creature);
    }

    /// <summary>Called by combat once death has stopped the unit's fights and cleared threat.</summary>
    internal void OnCreatureDied(Creature creature, Unit? killer)
    {
        if (creature.DeathState != CreatureDeathState.Alive || !_creatures.ContainsKey(creature.Guid))
        {
            return;
        }

        OnAiDeath(creature, killer);
        StopMoving(creature);
        creature.Health = 0;
        creature.NpcFlags = 0;
        creature.Target = default;
        creature.DeathState = CreatureDeathState.Corpse;
        creature.CorpseDecayMs = creature.CorpseDecaySeconds(_options) * 1000;
        creature.SkinningForOthersMs = Creature.SkinningForOthersDefaultMs; // Creature.cpp:822-825: a new life, a new corpse
        creature.LootedForSkin = false;
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

        foreach (LoadedGrid grid in _grids.Values)
        {
            grid.Creatures.Remove(creature);
        }

        RemoveFromWorld(creature);
    }

    // --- grids & life cycle -------------------------------------------------------------------

    /// <summary>vmangos ObjectGridLoader: create the grid's spawns (dead ones keep their respawn time).</summary>
    private LoadedGrid LoadGrid(GridCoord coord)
    {
        if (_grids.TryGetValue(coord, out LoadedGrid? existing))
        {
            return existing;
        }

        var grid = new LoadedGrid();
        _grids[coord] = grid;
        if (!_spawnsByGrid.TryGetValue(coord, out List<CreatureSpawn>? spawns))
        {
            return grid;
        }

        LoadSpawns(grid, spawns);
        return grid;
    }

    /// <summary>Create the creatures of <paramref name="spawns"/> in an already registered grid (a grid load, or one event spawn coming back: <see cref="RefreshSpawns"/>).</summary>
    private void LoadSpawns(LoadedGrid grid, IEnumerable<CreatureSpawn> spawns)
    {
        foreach (CreatureSpawn spawn in spawns)
        {
            if (_spawnGate is { } gate && !gate.AllowsCreature(spawn.Guid))
            {
                continue; // an event spawn whose event is not running (vmangos leaves game_event_creature guids out of the grid at load)
            }

            CreatureTemplate? template = _content.FindTemplate(spawn.Entry);
            if (template is null)
            {
                if (_warnedMissingTemplates.Add(spawn.Entry))
                {
                    _logger.LogWarning("creature spawn {Guid} on map {MapId} uses missing creature_template {Entry}; skipped", spawn.Guid, Map.MapId, spawn.Entry);
                }

                continue;
            }

            ObjectGuid guid = ObjectGuid.WithEntry(HighGuid.Unit, template.Entry, spawn.Guid);
            if (_creatures.TryGetValue(guid, out Creature? moved))
            {
                grid.Creatures.Add(moved);
                continue; // a live spawn walked away before its home grid unloaded
            }

            var creature = new Creature(spawn.Guid, template, spawn, _content, _random);
            if (_respawnAt.Remove(spawn.Guid, out long respawnAt) && respawnAt > _clockMs)
            {
                creature.Health = 0;
                creature.NpcFlags = 0;
                creature.DeathState = CreatureDeathState.Dead;
                creature.Combat.DeathState = DeathState.Dead;
                creature.RespawnAtMs = respawnAt;
            }

            AddToWorld(creature, grid);
        }
    }

    private void UnloadGrid(GridCoord coord)
    {
        if (!_grids.Remove(coord, out LoadedGrid? grid))
        {
            return;
        }

        foreach (Creature creature in grid.Creatures)
        {
            if (ReferenceEquals(creature.Map, Map) && Map.Grids.CellOf(creature) is { } cell
                && (cell.Grid.X != coord.X || cell.Grid.Y != coord.Y))
            {
                continue; // shared spatial ownership keeps a creature in another live grid
            }

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
        creature.System = this;
        creature.WalkSpeed = creature.CreatureWalkSpeed;
        creature.RunSpeed = creature.CreatureRunSpeed;
        creature.ClearChangedFields();
        _creatures[creature.Guid] = creature;
        grid.Creatures.Add(creature);
        if (creature.DeathState != CreatureDeathState.Dead)
        {
            Map.AddObject(creature, isNewObject: creature.IsNewObject);
        }

        creature.Motion.Initialize(CreateMovementGenerator(creature), this, start: creature.DeathState == CreatureDeathState.Alive);
        CreateAi(creature);
        if (creature.DeathState == CreatureDeathState.Alive)
        {
            creature.AI?.OnRespawn();
        }
    }

    private void RemoveFromWorld(Creature creature)
    {
        _creatures.Remove(creature.Guid);
        ForgetAi(creature);
        creature.Motion.Reset();
        Map.Combat.Untrack(creature);
        Map.RemoveObject(creature);
        creature.System = null;
        ForgetObservers(creature);
    }

    private ICreatureMovementGenerator CreateMovementGenerator(Creature creature)
    {
        if (!_options.MovementEnabled)
        {
            return IdleMovementGenerator.Instance;
        }

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
        creature.Combat.DeathState = DeathState.Dead;
        Map.Combat.Untrack(creature);
        _ai.Spells?.OnCreatureRemoved(creature);
        Map.RemoveObject(creature);
        ForgetObservers(creature);
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
        else if (!_grids.ContainsKey(ComputeGrid(creature.Home.X, creature.Home.Y)))
        {
            // The creature died after walking out of an unloaded home grid. Keep its deadline
            // as dormant spawn data; do not respawn into a grid no player has loaded.
            _respawnAt[creature.Spawn.Guid] = creature.RespawnAtMs;
            RemoveFromWorld(creature);
        }
    }

    /// <summary>vmangos Creature::Update DEAD → respawn: fields re-initialized (level, display, health), ALIVE, movement restarted.</summary>
    private void Respawn(Creature creature)
    {
        if (!_creatures.ContainsKey(creature.Guid))
        {
            return;
        }

        Map.Combat.Untrack(creature);
        creature.Combat.DeathState = DeathState.Alive;
        MapCombat.ClearInCombat(creature);
        creature.Combat.SetAttackTimer(WeaponAttackType.BaseAttack, 0);
        creature.InitializeFields();
        creature.DeathState = CreatureDeathState.Alive;
        creature.RespawnAtMs = 0;

        // vmangos Creature::Update DEAD -> respawn (Objects/Creature.cpp:877-878): 5 s before it may initiate an attack.
        creature.PacifiedMs = _options.RespawnPacifyMs;
        creature.ResetToHome(_serverTime());

        // Invisible until now, so nobody needs a values update for the re-initialization.
        creature.ClearChangedFields();
        Map.AddObject(creature);
        ResetAiState(creature);
        creature.Motion.Initialize(creature.Motion.Default, this, start: true);
        creature.AI?.OnRespawn();
    }

    private void ForgetObservers(Creature creature)
    {
        foreach (HashSet<Creature> seen in _seen.Values)
        {
            seen.Remove(creature);
        }

        _catchUp.RemoveAll(c => ReferenceEquals(c.Creature, creature));
    }

    private void OnMapGridUnloading(MapGrid grid)
    {
        UnloadGrid(new GridCoord(grid.Coord.X, grid.Coord.Y));
        // vmangos Map::CreatureRespawnRelocation returns a moved creature to its loaded
        // home grid before unloading its current grid. Otherwise drop the live ownership.
        foreach (Creature creature in grid.AllObjects().OfType<Creature>())
        {
            if (!_creatures.ContainsKey(creature.Guid))
            {
                continue;
            }

            GridCoord home = ComputeGrid(creature.Home.X, creature.Home.Y);
            if ((home.X != grid.Coord.X || home.Y != grid.Coord.Y)
                && _grids.ContainsKey(home) && creature.DeathState == CreatureDeathState.Alive)
            {
                Map.Combat.Untrack(creature);
                StopMoving(creature);
                ResetAiState(creature);
                MapCombat.ClearInCombat(creature);
                creature.ResetToHome(_serverTime());
                creature.Motion.Initialize(creature.Motion.Default, this, start: true);
                continue;
            }

            if (creature.Spawn is not null && creature.DeathState != CreatureDeathState.Alive)
            {
                _respawnAt[creature.Spawn.Guid] = creature.RespawnAtMs;
            }

            foreach (LoadedGrid loaded in _grids.Values)
            {
                loaded.Creatures.Remove(creature);
            }

            RemoveFromWorld(creature);
        }
    }

    private sealed class LoadedGrid
    {
        public List<Creature> Creatures { get; } = [];
    }
}
