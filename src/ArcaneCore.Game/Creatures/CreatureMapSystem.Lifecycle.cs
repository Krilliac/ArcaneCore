using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Pets.Control;
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

        if (creature.Spawn is { } diedSpawn)
        {
            _groupRespawnCleared.Remove(diedSpawn.Guid);
        }

        OnFormationMemberDied(creature); // cmangos Unit::Kill → FormationData::OnDeath
        OnAiDeath(creature, killer); // ends with InstanceData.OnCreatureDeath (sd2-low and sd2-mid both added the call; once is right)
        NotifySummonerOfDeath(creature);
        DespawnCorpseOfSummon(creature);
        StopMoving(creature);
        creature.Health = 0;
        creature.NpcFlags = 0;
        creature.Target = default;
        creature.DeathState = CreatureDeathState.Corpse;
        creature.CorpseDecayMs = creature.Summon is { Kind: SummonKind.Pet or SummonKind.Guardian or SummonKind.MiniPet } petLinks
            // vmangos Pet::SetDeathState(CORPSE) (Pet.cpp:649-653): every Pet object's corpse lasts 15 s, a hunter pet's an hour.
            ? petLinks.Kind == SummonKind.Pet && creature.GetOwner() is Player { Class: Class.Hunter }
                ? PetConstants.HunterPetCorpseDecayMs
                : PetConstants.CorpseDecayMs
            : creature.CorpseDecaySeconds(_options) * 1000;
        creature.SkinningForOthersMs = Creature.SkinningForOthersDefaultMs; // Creature.cpp:822-825: a new life, a new corpse
        creature.LootedForSkin = false;
        uint respawnDelay = creature.TakeRespawnDelaySeconds(); // a script's one-shot delay first (cmangos SetRespawnDelay(d, true))
        creature.RespawnAtMs = respawnDelay == Creature.RespawnNeverSeconds ? long.MaxValue : _clockMs + (respawnDelay * 1000L);
        SaveRespawnOnDeath(creature);
        // Capture the current pet while its corpse still belongs to the map.
        // After decay, the owner's pet GUID no longer resolves to the removed object.
        if (creature.Summon is { Kind: SummonKind.Pet } && creature.GetOwner() is Player owner)
        {
            Map.Pets?.SaveCurrentPet(owner);
        }
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

        // A scripted wipe can restore a boss before its delayed ForcedDespawn fires.
        // That old corpse timer must not remove the newly respawned creature.
        _forcedDespawns.RemoveAll(d => ReferenceEquals(d.Creature, creature));

        if (creature.Spawn is { } spawn && _groupOfSpawn.ContainsKey(spawn.Guid))
        {
            ForgetGroupRespawn(spawn.Guid); // its group brings it back at the next update
            return;
        }

        Respawn(creature);
    }

    /// <summary>
    /// Put a creature that is not in the database into the map at a position (GM .npc add,
    /// summons). It is announced with UPDATETYPE_CREATE_OBJECT2, as vmangos Map::Add does, and
    /// does not respawn after death; it disappears when its grid unloads.
    /// </summary>
    public Creature SpawnTemporary(CreatureTemplate template, float x, float y, float z, float orientation)
        => SpawnTemporary(template, x, y, z, orientation, summoner: null);

    /// <summary>
    /// <see cref="SpawnTemporary(CreatureTemplate, float, float, float, float)"/> by a summoner, known to the summon from its start. The
    /// summoner's AI hears of it at once unless <paramref name="notifySummoner"/> is false: a caller that still places or sets up the summon
    /// then calls <see cref="NotifyJustSummoned"/> itself.
    /// </summary>
    internal Creature SpawnTemporary(CreatureTemplate template, float x, float y, float z, float orientation, Creature? summoner,
        bool notifySummoner = true)
    {
        ArgumentNullException.ThrowIfNull(template);
        var creature = new Creature(_nextTemporaryCounter++ & 0x00FFFFFF, template, spawn: null, _content, _random, displayModelResolver: _displayModelResolver, statRates: _options.Rates);
        if (summoner is not null)
        {
            RecordSummoner(summoner, creature);
        }

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
        if (notifySummoner)
        {
            NotifyJustSummoned(creature);
        }

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

    /// <summary>The live creature of a database spawn: its GUID carries the entry chosen at creation, so every entry the spawn may take is tried.</summary>
    private Creature? FindLive(CreatureSpawn spawn, IReadOnlyList<uint> alternatives)
    {
        foreach (uint candidate in alternatives.Count > 0 ? alternatives : [spawn.Entry])
        {
            if (_creatures.TryGetValue(ObjectGuid.WithEntry(HighGuid.Unit, candidate, spawn.Guid), out Creature? live))
            {
                return live;
            }
        }

        return null;
    }

    /// <summary>Create the creatures of <paramref name="spawns"/> in an already registered grid (a grid load, or one event spawn coming back: <see cref="RefreshSpawns"/>).</summary>
    private void LoadSpawns(LoadedGrid grid, IEnumerable<CreatureSpawn> spawns)
    {
        foreach (CreatureSpawn spawn in spawns)
        {
            if (_scriptOnlySpawns.Contains(spawn.Guid) && !_activatingScriptSpawns.Contains(spawn.Guid))
            {
                continue;
            }

            if (!SpawnAllowed(spawn.Guid))
            {
                // an event spawn whose event is not running (vmangos leaves game_event_creature guids out of the grid at load), or an
                // instance spawn gated on a map variable (cmangos spawn_group WorldState)
                continue;
            }

            if (PoolRefusesAtLoad(spawn))
            {
                continue; // a pooled spawn exists only while its pool has it out (cmangos ObjectMgr::LoadCreatures, IsNotPartOfPoolOrEvent)
            }

            if (_groupOfSpawn.TryGetValue(spawn.Guid, out Maps.SpawnGroups.SpawnGroupState? group))
            {
                LoadGroupMember(group, spawn, grid); // its spawn group decides whether and as what it exists
                continue;
            }

            // A spawn with creature_spawn_entry rows becomes one of them; the entry part of its GUID is the one chosen when the object was
            // created, so a spawn that is already loaded is looked up under every entry it may carry.
            IReadOnlyList<uint> alternatives = _options.Respawn.AlternateEntries ? _content.GetSpawnEntries(spawn.Guid) : [];
            Creature? moved = FindLive(spawn, alternatives);
            if (moved is not null)
            {
                grid.Creatures.Add(moved);
                continue; // a live spawn walked away before its home grid unloaded
            }

            CreatureTemplate? template = alternatives.Count > 0 ? SpawnEntryChooser.Choose(_content, alternatives, _random) : _content.FindTemplate(spawn.Entry);
            if (template is null)
            {
                uint warnKey = alternatives.Count > 0 ? spawn.Guid | 0x8000_0000u : spawn.Entry;
                if (_warnedMissingTemplates.Add(warnKey))
                {
                    _logger.LogWarning(
                        alternatives.Count > 0
                            ? "creature spawn {Guid} on map {MapId} has no creature_template for any of its creature_spawn_entry rows; skipped"
                            : "creature spawn {Guid} on map {MapId} uses missing creature_template {Entry}; skipped",
                        spawn.Guid, Map.MapId, spawn.Entry);
                }

                continue;
            }

            var creature = new Creature(spawn.Guid, template, spawn, _content, _random, displayModelResolver: _displayModelResolver, statRates: _options.Rates);
            ApplyEventData(creature); // a running game event may change its entry or model (game_event_creature_data)
            if (_options.Respawn.DrawDelayAtLoad)
            {
                creature.DrawRespawnDelay(); // m_respawnDelay is drawn once per loaded object (Creature.cpp:1963)
            }

            if (_respawnAt.Remove(spawn.Guid, out long respawnAt))
            {
                if (respawnAt > _clockMs)
                {
                    creature.Health = 0;
                    creature.NpcFlags = 0;
                    creature.DeathState = CreatureDeathState.Dead;
                    creature.Combat.DeathState = DeathState.Dead;
                    creature.RespawnAtMs = respawnAt;
                }
                else
                {
                    DeletePersistedRespawn(creature); // "respawn time set but expired" (vmangos Creature.cpp:1984-1989)
                }
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
                SaveRespawnOnRemoval(creature);
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

        // vmangos Creature::AddToWorld -> ZoneScript::OnCreatureCreate: the instance script hears of every creature placed in its map.
        if (Map.FindUpdater<Instances.Scripts.InstanceData>() is { } instanceData)
        {
            instanceData.OnCreatureCreate(creature);
            if (creature.IsAlive)
            {
                instanceData.NotifyCreatureAlive(creature); // a member of a script's creature group
            }
        }
        CreateAi(creature);
        if (creature.DeathState == CreatureDeathState.Alive)
        {
            creature.AI?.OnRespawn();
            JoinFormation(creature); // cmangos Creature::AddToWorld → FormationData::SetFormationSlot
        }
    }

    private void RemoveFromWorld(Creature creature)
    {
        if (creature.IsAlive)
        {
            Map.FindUpdater<Instances.Scripts.InstanceData>()?.NotifyCreatureGone(creature); // cmangos ClearCreatureGroup at RemoveFromWorld
        }

        _creatures.Remove(creature.Guid);
        _corpseDespawns.Remove(creature);
        _forcedDespawns.RemoveAll(d => ReferenceEquals(d.Creature, creature));
        NotifySummonerOfRemoval(creature);
        ForgetAi(creature);
        creature.Motion.Reset();
        Map.Combat.Untrack(creature);
        Map.RemoveObject(creature);
        creature.System = null;
        ForgetObservers(creature);
        OnFormationMemberRemoved(creature);
        OnGroupMemberRemoved(creature);
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
                // The spawn's own creature_movement rows, else the entry's creature_movement_template path (a summon has no spawn row and
                // takes the entry path): mangos-classic WaypointManager::GetDefaultPath.
                CreatureWaypointPath path = _content.ResolveWaypointPath(creature.Spawn?.Guid ?? 0, creature.Template.Entry);
                if (path.Points.Count == 0)
                {
                    // Reported once in aggregate at content load (CreatureContent.FindWaypointSpawnsWithoutPath); per creature only at debug.
                    bool wander = _options.Movement.MissingWaypointPathFallback == MissingWaypointPathFallback.Random;
                    _logger.LogDebug("{Creature} has waypoint movement but no creature_movement or creature_movement_template path; {Fallback}",
                        creature.Guid, wander ? "wandering" : "idling");
                    return wander ? new RandomMovementGenerator() : IdleMovementGenerator.Instance;
                }

                return new WaypointMovementGenerator(path.Points);

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
        CorpseRemoving?.Invoke(creature);
        // mangos-classic Creature::RemoveCorpse -> InstanceData::OnCreatureDespawn (Creature.cpp:287-288): still on the map, so a triggered
        // cast at the corpse (The Beast's Finkle is Einhorn) works.
        Map.FindUpdater<Instances.Scripts.InstanceData>()?.OnCreatureDespawn(creature);
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
        else if (_groupOfSpawn.ContainsKey(creature.Spawn.Guid))
        {
            RemoveGroupMemberCorpse(creature, creature.Spawn);
        }
        else if (!_grids.ContainsKey(ComputeGrid(creature.Home.X, creature.Home.Y)))
        {
            // The creature died after walking out of an unloaded home grid. Keep its deadline
            // as dormant spawn data; do not respawn into a grid no player has loaded.
            _respawnAt[creature.Spawn.Guid] = creature.RespawnAtMs;
            SaveRespawnOnRemoval(creature);
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

        // An EventAI UPDATE_TEMPLATE lasts until the respawn (cmangos Creature::ResetEntry(respawn), Creature.cpp:636-655).
        if (creature.ScriptOriginalTemplate is { } original)
        {
            creature.ChangeTemplate(original);
            creature.ScriptOriginalTemplate = null;
        }

        // A spawn with several entries picks again at every respawn (vmangos Creature.cpp:830-841); the GUID stays, the AI follows the template.
        bool entryChanged = false;
        if (creature.Spawn is { } spawn && _options.Respawn.AlternateEntries && _content.GetSpawnEntries(spawn.Guid) is { Count: > 0 } alternatives
            && SpawnEntryChooser.Choose(_content, alternatives, _random) is { } chosen && chosen.Entry != creature.Template.Entry)
        {
            ForgetAi(creature);
            creature.ChangeTemplate(chosen);
            entryChanged = true;
        }

        Respawning?.Invoke(creature);
        Map.Combat.Untrack(creature);
        creature.Combat.DeathState = DeathState.Alive;
        MapCombat.ClearInCombat(creature);
        creature.Combat.SetAttackTimer(WeaponAttackType.BaseAttack, 0);
        creature.InitializeFields();
        creature.LootTapPlayerGuid = default;
        creature.LootTapGroup = null;
        creature.DeathState = CreatureDeathState.Alive;
        creature.RespawnAtMs = 0;
        DeletePersistedRespawn(creature);

        // vmangos Creature::Update DEAD -> respawn (Objects/Creature.cpp:877-878): 5 s before it may initiate an attack.
        creature.PacifiedMs = _options.RespawnPacifyMs;
        creature.ResetToHome(_serverTime());

        // Invisible until now, so nobody needs a values update for the re-initialization.
        creature.ClearChangedFields();
        Map.AddObject(creature);
        ResetAiState(creature);
        creature.Motion.Initialize(creature.Motion.Default, this, start: true);
        if (entryChanged)
        {
            CreateAi(creature);
        }

        ResetGuardCall(creature); // vmangos BasicAI::JustRespawned
        creature.WaypointsPaused = false; // a respawn starts with fresh unit state (relay scripts' pause and run mode)
        creature.ScriptRun = false;
        SetAiImmobilized(creature, false, combatOnly: false);
        creature.FollowMovementDisabled = false;
        creature.InvincibilityHpThreshold = 0; // an EventAI death prevention ends with the life it was set in
        creature.AI?.OnRespawn();
        JoinFormation(creature);
        if (Map.FindUpdater<Instances.Scripts.InstanceData>() is { } respawnData)
        {
            respawnData.OnCreatureRespawn(creature);
            respawnData.NotifyCreatureAlive(creature);
        }
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
                SaveRespawnOnRemoval(creature);
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
