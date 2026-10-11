using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Game.WorldState.States;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.WorldState;

/// <summary>Owns the six persisted Necropolis counts and the matching ClassicDB zone events/conditions.</summary>
public sealed class ScourgeInvasionFeature(IServiceScopeFactory scopes, GameEventFeature events,
    ILogger<ScourgeInvasionFeature> logger) : IWorldFeature, IGameEventListener, IWorldStateProvider
{
    private WorldRuntime? _world;
    private ScourgeInvasionSnapshot _snapshot = ScourgeInvasionSnapshot.Disabled;
    private bool _hasStore;
    private GameEventService? _events;
    private readonly Dictionary<uint, uint> _spawnZoneByGuid = [];
    private readonly Dictionary<uint, uint> _circleZoneByGuid = [];
    private readonly HashSet<CreatureMapSystem> _healthSystems = [];
    private readonly Dictionary<GameObjectMapSystem, InvasionCircleAi> _circleAis = [];
    private readonly Dictionary<(uint ZoneId, uint SpawnGuid), int> _pendingDeaths = [];
    private readonly List<StoreOp> _ops = [];
    private readonly Dictionary<uint, (int SpawnIndex, uint Entry)> _unsummonedCityAttacks = [];
    private OffThreadStoreWork? _store;
    private Func<Func<Task>, Task>? _offload;
    private ScourgeInvasionState? _intendedState;
    private bool _refreshDue;
    private bool _wake;
    private uint _reloadMs;
    private readonly Dictionary<uint, Creature> _mouths = [];
    private readonly Dictionary<uint, Creature> _cityAttackers = [];

    /// <summary>The live Mouth of Kel'Thuzad per attacked zone (mangos-classic InvasionZone::mouthGuid).</summary>
    public IReadOnlyDictionary<uint, Creature> Mouths => _mouths;

    /// <summary>The live Pallid Horror or Patchwork Terror per capital (mangos-classic CityAttack::pallidGuid).</summary>
    public IReadOnlyDictionary<uint, Creature> CityAttackers => _cityAttackers;

    /// <summary>Test seam for the reference's urand/PickRandomValue picks.</summary>
    internal Random Random { get; set; } = Random.Shared;

    /// <summary>Wall clock for timers; tests move it.</summary>
    internal Func<long> NowUnix { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public ScourgeInvasionSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Test seam for where realm-store work runs (<see cref="OffThreadStoreWork.Offload"/>). Production leaves it null: the
    /// thread pool, never the world thread. Tests whose fake stores complete synchronously pass <c>work =&gt; work()</c>.
    /// </summary>
    internal Func<Func<Task>, Task>? StoreOffload
    {
        get => _offload;
        set
        {
            _offload = value;
            if (_store is not null && value is not null) _store.Offload = value;
        }
    }

    /// <summary>The single-flight store runner (null before <see cref="Attach"/>); tests read its job count.</summary>
    internal OffThreadStoreWork? StoreWork => _store;

    /// <summary>One queued store mutation and the error logged if it fails.</summary>
    private sealed record StoreOp(Func<IScourgeInvasionStateStore, Task> Run, string Failure, uint Zone);

    public void Attach(WorldRuntime world)
    {
        _world = world;
        _store = new OffThreadStoreWork(world);
        if (_offload is not null) _store.Offload = _offload;
        using (IServiceScope scope = scopes.CreateScope())
        {
            if (scope.ServiceProvider.GetService<IScourgeInvasionStateStore>() is { } store)
            {
                _hasStore = true;
                Volatile.Write(ref _snapshot, store.LoadAsync().GetAwaiter().GetResult());
            }
        }
        if (_hasStore) WorldStateHooks.For(world).WorldStates.Add(this);
        // QuestNpcFeature rebuilds its services when it attaches: install the cultist gossip on the first world command.
        world.Post(() =>
        {
            using IServiceScope scope = scopes.CreateScope();
            var creatureWorld = scope.ServiceProvider.GetService<ArcaneCore.World.Creatures.CreatureWorldFeature>();
            scope.ServiceProvider.GetService<ArcaneCore.World.Npc.QuestNpcFeature>()?.Services.AddGossipScript(new CultistEngineerGossip(
                id => creatureWorld?.Content.Ai.BroadcastTexts.Find((uint)id)?.Text, Random));
        });
        events.ServiceCreated += Wire;
        if (events.Service is { } current) Wire(current);
        world.WorldTick += diffMs =>
        {
            InstallInvasionAis(world);
            OnTick(diffMs);
        };
        world.MapUnloading += map =>
        {
            if (map.FindUpdater<CreatureMapSystem>() is { } system) _healthSystems.Remove(system);
            if (map.FindUpdater<GameObjectMapSystem>() is { } objects) _circleAis.Remove(objects);
            foreach (uint key in _mouths.Where(p => ReferenceEquals(p.Value.Map, map)).Select(p => p.Key).ToArray()) _mouths.Remove(key);
            foreach (uint key in _cityAttackers.Where(p => ReferenceEquals(p.Value.Map, map)).Select(p => p.Key).ToArray())
                _cityAttackers.Remove(key);
        };
    }

    public bool? WorldScriptCondition(uint field, uint state)
        => !_hasStore ? null : Snapshot.WorldScriptCondition(field);

    public void Fill(Player player, uint zoneId, List<WorldStatePair> states)
    {
        if (Snapshot.State == ScourgeInvasionState.Enabled)
            states.AddRange(WorldStatePairs(Snapshot));
    }

    private static IReadOnlyList<WorldStatePair> WorldStatePairs(ScourgeInvasionSnapshot snapshot)
    {
        List<WorldStatePair> pairs = [];
        foreach (ScourgeInvasionZone zone in ScourgeInvasionCatalog.Zones)
            pairs.Add(new WorldStatePair(zone.WorldStateField, snapshot.Remaining(zone.ZoneId) > 0 ? 1 : 0));
        pairs.Add(new WorldStatePair(ScourgeInvasionCatalog.BattlesWonField, snapshot.BattlesWon));
        foreach (ScourgeInvasionZone zone in ScourgeInvasionCatalog.Zones)
            pairs.Add(new WorldStatePair(zone.NecropolisCountField, snapshot.Remaining(zone.ZoneId)));
        return pairs;
    }

    private void PublishWorldStates(ScourgeInvasionSnapshot previous, ScourgeInvasionSnapshot current)
    {
        if (_world is null || previous.State == ScourgeInvasionState.Disabled
            && current.State == ScourgeInvasionState.Disabled) return;

        IReadOnlyList<WorldStatePair> before = WorldStatePairs(previous);
        IReadOnlyList<WorldStatePair> after = WorldStatePairs(current);
        for (int i = 0; i < after.Count; i++)
        {
            if (previous.State == ScourgeInvasionState.Enabled && before[i].Value == after[i].Value
                && current.State == ScourgeInvasionState.Enabled) continue;
            byte[] packet = WorldStatePackets.BuildUpdate(after[i].State, (uint)after[i].Value);
            foreach (Player player in _world.OnlinePlayers)
                player.Session.Send(ArcaneCore.Protocol.WorldOpcode.SmsgUpdateWorldState, packet);
        }
    }

    public void OnEventChanged(ushort eventId, bool active, bool resume)
    {
        if (!_hasStore || eventId != ScourgeInvasionCatalog.MainEvent) return;
        // A start or stop still queued for the store counts as the state, so a quick start/stop pair is not lost.
        ScourgeInvasionState state = _intendedState ?? Snapshot.State;
        if (active && state == ScourgeInvasionState.Disabled)
        {
            _intendedState = ScourgeInvasionState.Enabled;
            Enqueue(new StoreOp(store => store.StartAsync(), "could not start the Scourge invasion", 0));
        }
        else if (!active && state == ScourgeInvasionState.Enabled && Snapshot.BattlesWon < 150)
        {
            _intendedState = ScourgeInvasionState.Disabled;
            Enqueue(new StoreOp(store => store.StopAsync(), "could not stop the Scourge invasion", 0));
        }
    }

    private void Wire(GameEventService service)
    {
        _events = service;
        _spawnZoneByGuid.Clear();
        _circleZoneByGuid.Clear();
        foreach (ScourgeInvasionZone zone in ScourgeInvasionCatalog.Zones)
        {
            if (service.Rows.Creatures.TryGetValue(zone.EventId, out IReadOnlyList<uint>? guids))
                foreach (uint guid in guids) _spawnZoneByGuid[guid] = zone.ZoneId;
            if (service.Rows.GameObjects.TryGetValue(zone.EventId, out IReadOnlyList<uint>? objects))
                foreach (uint guid in objects) _circleZoneByGuid[guid] = zone.ZoneId;
        }
        service.AddListener(this);
    }

    private void InstallInvasionAis(WorldRuntime world)
    {
        if (!_hasStore) return;
        foreach (Map map in world.Maps.Where(m => m.MapId is 0 or 1))
        {
            if (map.FindUpdater<CreatureMapSystem>() is { } creatures && _healthSystems.Add(creatures))
            {
                creatures.RegisterEntryAi(ScourgeInvasionCatalog.NecropolisHealth,
                    creature => new NecropolisHealthAi(creature, this));
                creatures.RegisterEntryAi(ScourgeInvasionCatalog.Necropolis,
                    creature => new NecropolisAi(creature));
                creatures.RegisterEntryAi(ScourgeInvasionCatalog.NecropolisRelay,
                    creature => new NecropolisRelayAi(creature));
                creatures.RegisterEntryAi(ScourgeInvasionCatalog.NecropolisProxy,
                    creature => new NecropolisProxyAi(creature));
                creatures.RegisterEntryAi(ScourgeInvasionCatalog.NecroticShard,
                    creature => new NecroticShardAi(creature));
                creatures.RegisterEntryAi(ScourgeInvasionCatalog.DamagedNecroticShard,
                    creature => new NecroticShardAi(creature));
                creatures.RegisterEntryAi(ScourgeInvasionCatalog.GhostGhoulSpawner,
                    creature => new ScourgeCampSpawnerAi(creature));
                creatures.RegisterEntryAi(ScourgeInvasionCatalog.GhostSkeletonSpawner,
                    creature => new ScourgeCampSpawnerAi(creature));
                creatures.RegisterEntryAi(ScourgeInvasionCatalog.GhoulSkeletonSpawner,
                    creature => new ScourgeCampSpawnerAi(creature));
                creatures.RegisterEntryAi(ScourgeInvasionCatalog.MouthOfKelThuzad,
                    creature => new ScourgeMouthAi(creature, NearestMouthZone(creature), Random));
                creatures.RegisterEntryAi(ScourgeInvasionCatalog.PallidHorror,
                    creature => new PallidHorrorAi(creature, this, NearestCity(creature)));
                creatures.RegisterEntryAi(ScourgeInvasionCatalog.PatchworkTerror,
                    creature => new PallidHorrorAi(creature, this, NearestCity(creature)));
                creatures.RegisterEntryAi(ScourgeInvasionCatalog.CultistEngineer,
                    creature => new CultistEngineerAi(creature));
                creatures.RegisterEntryAi(ScourgeInvasionCatalog.ShadowOfDoom,
                    creature => new ShadowOfDoomAi(creature, Random));
                creatures.RegisterEntryAi(ScourgeInvasionCatalog.Flameshocker,
                    creature => new FlameshockerAi(creature, Random));
            }
            if (map.FindUpdater<GameObjectMapSystem>() is { } objects && !_circleAis.ContainsKey(objects))
            {
                var ai = new InvasionCircleAi(this);
                _circleAis.Add(objects, ai);
                objects.RegisterAi(ScourgeInvasionCatalog.SummonCircle, ai);
                var necropolisObject = new NecropolisObjectAi();
                foreach (uint entry in ScourgeInvasionCatalog.NecropolisObjects) objects.RegisterAi(entry, necropolisObject);
            }
        }
    }

    private static float Distance2(Creature c, ScourgeInvasionPosition p) => (c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y);

    private static uint NearestMouthZone(Creature creature)
        => ScourgeInvasionCatalog.Zones.Where(z => z.MapId == creature.Map?.MapId)
            .MinBy(z => Distance2(creature, ScourgeInvasionCatalog.MouthPositions[z.ZoneId]))?.ZoneId ?? 0;

    private static uint NearestCity(Creature creature)
        => ScourgeInvasionCatalog.Cities.MinBy(c => c.Spawns.Min(p => Distance2(creature, p)))!.ZoneId;

    /// <summary>PallidHorrorAI::JustDied: the capital's next attack is 45-60 minutes after this death.</summary>
    internal void OnCityAttackerDied(Creature creature, uint zoneId)
    {
        if (_cityAttackers.TryGetValue(zoneId, out Creature? tracked) && ReferenceEquals(tracked, creature))
            _cityAttackers.Remove(zoneId);
        if (!_hasStore) return;
        long now = NowUnix();
        int next = Random.Next(ScourgeInvasionCatalog.CityAttackTimerMinSeconds, ScourgeInvasionCatalog.CityAttackTimerMaxSeconds + 1);
        Enqueue(new StoreOp(store => store.CityAttackDefeatedAsync(zoneId, now, next),
            "could not save the Scourge city attack defeat in zone {Zone}", zoneId));
    }

    internal uint? CircleZone(GameObject go)
        => go.Spawn is { } spawn && _circleZoneByGuid.TryGetValue(spawn.Guid, out uint zone)
            ? zone : null;

    internal bool IsZoneEventObject(GameObject go, uint zoneId)
        => go.Spawn is { } spawn && _circleZoneByGuid.TryGetValue(spawn.Guid, out uint mapped)
            && mapped == zoneId;

    internal bool HasLivingCircleOwner(GameObject circle, uint zoneId, CreatureMapSystem creatures)
    {
        CreatureSpawn? nearest = creatures.Content.GetSpawns(creatures.Map.MapId, ScourgeInvasionCatalog.NecropolisHealth)
            .Where(spawn => _spawnZoneByGuid.TryGetValue(spawn.Guid, out uint mapped) && mapped == zoneId)
            .Where(spawn => DistanceSquared(circle.X, circle.Y, circle.Z, spawn.X, spawn.Y, spawn.Z) <= 350f * 350f)
            .MinBy(spawn => DistanceSquared(circle.X, circle.Y, circle.Z, spawn.X, spawn.Y, spawn.Z));
        return nearest is not null && !Snapshot.DestroyedSpawnGuids.Contains(nearest.Guid);
    }

    private static float DistanceSquared(float ax, float ay, float az, float bx, float by, float bz)
        => (ax - bx) * (ax - bx) + (ay - by) * (ay - by) + (az - bz) * (az - bz);

    internal bool IsCircleAttackActive(uint zoneId)
        => ScourgeInvasionCatalog.ForZone(zoneId) is { } zone
            && Snapshot.Remaining(zoneId) > 0
            && _events?.IsActiveEvent(zone.EventId) == true;

    internal void OnNecropolisDied(Creature creature)
    {
        if (!_hasStore || creature.Entry != ScourgeInvasionCatalog.NecropolisHealth) return;
        uint? zoneId = creature.Spawn is { } spawn && _spawnZoneByGuid.TryGetValue(spawn.Guid, out uint mapped)
            ? mapped : creature.Map is { } map ? map.GetZoneAndAreaId(creature.X, creature.Y, creature.Z).ZoneId : null;
        if (zoneId is not { } zone || ScourgeInvasionCatalog.ForZone(zone) is null || creature.Spawn is not { } known)
            return;
        // NecropolisDestroyedAsync rolls the zone's 45-60 minute reattack once, when the death is recorded.
        _pendingDeaths.TryAdd((zone, known.Guid), Random.Next(2700, 3601));
        _wake = true;
        TryLaunch();
    }

    /// <summary>
    /// After the world stopped: wait for the store job in flight, then write what is still queued (start/stop, city defeats,
    /// Necropolis deaths; each store call is idempotent for a repeated death). The world thread no longer runs, so its queues are
    /// read here directly.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_hasStore || _store is null) return;
        try
        {
            await _store.InFlight.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Logged by the job's failure path when it was applied; the queued work below is still written.
        }

        StoreOp[] ops = [.. _ops];
        _ops.Clear();
        var deaths = _pendingDeaths.Select(p => (p.Key.ZoneId, p.Key.SpawnGuid, NextAttackSeconds: p.Value)).ToArray();
        if (ops.Length == 0 && deaths.Length == 0) return;
        var result = new RefreshResult();
        try
        {
            await RunStoreAsync(ops, deaths, full: false, NowUnix(), [], result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "could not write the queued Scourge invasion state at shutdown");
        }

        if (result.DeathFailure is { } failure)
            logger.LogError(failure, "could not persist Scourge Necropolis death at shutdown");
    }

    private void Enqueue(StoreOp op)
    {
        _ops.Add(op);
        _wake = true;
        TryLaunch();
    }

    private void OnTick(uint diffMs)
    {
        if (!_hasStore) return;
        _reloadMs = _reloadMs > diffMs ? _reloadMs - diffMs : 0;
        if (_reloadMs == 0)
        {
            _refreshDue = true;
            _wake = true;
            _reloadMs = 5_000;
        }
        TryLaunch();
    }

    /// <summary>What one store job did, filled off the world thread and read by <see cref="ApplyRefresh"/> on it.</summary>
    private sealed class RefreshResult
    {
        public readonly List<(uint ZoneId, uint SpawnGuid)> FlushedDeaths = [];
        public Exception? DeathFailure;
        public ScourgeInvasionSnapshot? Snapshot;
        public readonly List<(ScourgeCityAttack City, int SpawnIndex, uint Entry)> Claims = [];
    }

    /// <summary>
    /// Starts the next store job when one is wanted and none is in flight. The world thread only copies its queues here;
    /// every realm-store call runs in <see cref="RunStoreAsync"/> off it, and the result is applied by a later world command.
    /// A busy runner leaves the request set for the next tick, so at most one job runs and no request is lost.
    /// </summary>
    private void TryLaunch()
    {
        if (!_wake || _store is null || _store.Busy) return;
        _wake = false;
        StoreOp[] ops = [.. _ops];
        _ops.Clear();
        var deaths = _pendingDeaths.Select(p => (p.Key.ZoneId, p.Key.SpawnGuid, NextAttackSeconds: p.Value)).ToArray();
        bool full = _refreshDue;
        _refreshDue = false;
        if (!full && ops.Length == 0 && deaths.Length == 0) return;
        long now = NowUnix();
        // mangos-classic StartNewCityAttackIfTime: only a capital whose map can take the summon is claimed.
        HashSet<uint> summonable = full
            ? ScourgeInvasionCatalog.Cities.Where(c => Creatures(c.MapId) is not null).Select(c => c.ZoneId).ToHashSet()
            : [];
        var result = new RefreshResult();
        _store.TryRun(() => RunStoreAsync(ops, deaths, full, now, summonable, result),
            () => ApplyRefresh(result, full, syncEvents: full || deaths.Length > 0),
            ex =>
            {
                foreach ((uint, uint) done in result.FlushedDeaths) _pendingDeaths.Remove(done);
                logger.LogError(ex, "could not refresh Scourge invasion state");
            });
    }

    /// <summary>Off the world thread: queued writes in order, pending deaths, then the refresh's due zone restarts and city claims.</summary>
    private async Task RunStoreAsync(StoreOp[] ops, (uint ZoneId, uint SpawnGuid, int NextAttackSeconds)[] deaths, bool full,
        long now, HashSet<uint> summonable, RefreshResult result)
    {
        using IServiceScope scope = scopes.CreateScope();
        IScourgeInvasionStateStore store = scope.ServiceProvider.GetRequiredService<IScourgeInvasionStateStore>();
        foreach (StoreOp op in ops)
        {
            try
            {
                await op.Run(store).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
#pragma warning disable CA2254 // each StoreOp carries a constant template
                logger.LogError(ex, op.Failure, op.Zone);
#pragma warning restore CA2254
            }
        }
        try
        {
            foreach ((uint zone, uint guid, int next) in deaths)
            {
                await store.NecropolisDestroyedAsync(zone, guid, now, next).ConfigureAwait(false);
                result.FlushedDeaths.Add((zone, guid));
            }
        }
        catch (Exception ex)
        {
            // CreatureMapSystem's death transition already completed; the unsaved deaths stay pending for the next refresh.
            result.DeathFailure = ex;
        }
        ScourgeInvasionSnapshot snapshot = await store.LoadAsync().ConfigureAwait(false);
        result.Snapshot = snapshot;
        if (!full) return;
        if (snapshot.State == ScourgeInvasionState.Enabled && snapshot.BattlesWon < 150)
        {
            foreach (ScourgeInvasionZoneProgress zone in snapshot.Zones.OrderBy(z => z.NextAttackUnix))
            {
                if (zone.Remaining != 0 || zone.NextAttackUnix == 0 || zone.NextAttackUnix > now) continue;
                if (await store.RestartZoneAsync(zone.ZoneId, now).ConfigureAwait(false))
                    result.Snapshot = snapshot = await store.LoadAsync().ConfigureAwait(false);
                if (snapshot.Zones.Count(z => z.Remaining > 0) > 1) break;
            }
        }
        if (snapshot.State != ScourgeInvasionState.Enabled) return;
        foreach (ScourgeCityAttack city in ScourgeInvasionCatalog.Cities)
        {
            if (!snapshot.IsCityAttackDue(city.ZoneId, now) || !summonable.Contains(city.ZoneId)) continue;
            if (!await store.ClaimCityAttackAsync(city.ZoneId, now,
                    Random.Next(ScourgeInvasionCatalog.CityAttackTimerMinSeconds, ScourgeInvasionCatalog.CityAttackTimerMaxSeconds + 1))
                .ConfigureAwait(false)) continue;
            int spawnIndex = Random.Next(city.Spawns.Count);
            uint entry = Random.Next(2) == 0 ? ScourgeInvasionCatalog.PallidHorror : ScourgeInvasionCatalog.PatchworkTerror;
            result.Claims.Add((city, spawnIndex, entry));
            result.Snapshot = snapshot = await store.LoadAsync().ConfigureAwait(false);
        }
    }

    /// <summary>On the world thread: publish the loaded state, then drive the game events and the summons from it.</summary>
    private void ApplyRefresh(RefreshResult result, bool full, bool syncEvents)
    {
        foreach ((uint, uint) done in result.FlushedDeaths) _pendingDeaths.Remove(done);
        if (result.DeathFailure is { } failure)
            logger.LogError(failure, "could not persist Scourge Necropolis death; {Count} pending retry", _pendingDeaths.Count);
        if (_ops.Count == 0) _intendedState = null;
        // A claim saved the capital's next timer: its summon is owed until a refresh finds the map (or the invasion stops).
        foreach ((ScourgeCityAttack city, int spawnIndex, uint entry) in result.Claims)
            _unsummonedCityAttacks[city.ZoneId] = (spawnIndex, entry);
        if (result.Snapshot is { } current)
        {
            ScourgeInvasionSnapshot previous = Snapshot;
            Volatile.Write(ref _snapshot, current);
            PublishWorldStates(previous, current);
        }
        // A start or stop queued after this job began is not in its snapshot: driving event 17 from it would undo the
        // operator's .event start/stop. The job that carries the queued change applies the events and summons.
        if (_intendedState is null)
        {
            try
            {
                if (syncEvents) SyncEvents();
                if (full) SyncChoreography();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "could not refresh Scourge invasion state");
            }
        }
        else if (full)
        {
            _refreshDue = true; // the choreography (and any city claim made here) is applied by the follow-up job
            _wake = true;
        }
        TryLaunch();
    }

    private void SyncEvents()
    {
        GameEventService? service = _events;
        if (service?.IsInitialised != true) return;
        bool attacking = Snapshot.State == ScourgeInvasionState.Enabled && Snapshot.BattlesWon < 150;
        SetEvent(service, 17, attacking);
        foreach (ScourgeInvasionZone zone in ScourgeInvasionCatalog.Zones)
        {
            bool zoneActive = attacking && Snapshot.Remaining(zone.ZoneId) > 0;
            if (!zoneActive && service.IsActiveEvent(zone.EventId))
                foreach (InvasionCircleAi ai in _circleAis.Values) ai.ForgetZone(zone.ZoneId);
            SetEvent(service, zone.EventId, zoneActive);
        }
        SetEvent(service, 96, attacking && Snapshot.BattlesWon is >= 50 and < 100);
        SetEvent(service, 97, attacking && Snapshot.BattlesWon is >= 100 and < 150);
        SetEvent(service, 98, Snapshot.State == ScourgeInvasionState.Enabled && Snapshot.BattlesWon >= 150);
        SetEvent(service, 99, Snapshot.State == ScourgeInvasionState.Enabled && Snapshot.BattlesWon >= 150);
    }

    private CreatureMapSystem? Creatures(uint mapId)
        => _world?.FindMap(mapId)?.FindUpdater<CreatureMapSystem>();

    private static void Despawn(Creature creature)
        => creature.Map?.FindUpdater<CreatureMapSystem>()?.ForcedDespawn(creature, 0);

    private static bool IsLive(Creature creature) => creature.IsInWorld && creature.IsAlive;

    /// <summary>
    /// mangos-classic SummonMouth/OnDisable and StartNewCityAttackIfTime/SummonPallid: one Mouth of Kel'Thuzad stands at each
    /// attacked zone's point, and each capital gets a Pallid Horror or Patchwork Terror whenever its saved 45-60 minute timer is
    /// due. An unavailable map is retried on the next refresh, and the timer is only advanced once the summon can happen.
    /// </summary>
    private void SyncChoreography()
    {
        bool enabled = Snapshot.State == ScourgeInvasionState.Enabled;
        bool attacking = enabled && Snapshot.BattlesWon < 150;
        foreach (ScourgeInvasionZone zone in ScourgeInvasionCatalog.Zones)
        {
            if (_mouths.TryGetValue(zone.ZoneId, out Creature? mouth) && !IsLive(mouth))
                _mouths.Remove(zone.ZoneId, out mouth);
            bool active = attacking && Snapshot.Remaining(zone.ZoneId) > 0;
            if (!active && mouth is not null)
            {
                if (mouth.AI is ScourgeMouthAi ai) ai.EndAttack();
                else Despawn(mouth);
                _mouths.Remove(zone.ZoneId);
            }
            else if (active && mouth is null && Creatures(zone.MapId) is { } creatures
                && ScourgeInvasionCatalog.MouthPositions.TryGetValue(zone.ZoneId, out ScourgeInvasionPosition at)
                && creatures.SummonInstanceCreature(ScourgeInvasionCatalog.MouthOfKelThuzad, at.X, at.Y, at.Z, at.O) is { } summoned)
                _mouths[zone.ZoneId] = summoned;
        }

        foreach (ScourgeCityAttack city in ScourgeInvasionCatalog.Cities)
        {
            if (_cityAttackers.TryGetValue(city.ZoneId, out Creature? attacker) && !IsLive(attacker))
                _cityAttackers.Remove(city.ZoneId, out attacker);
            if (!enabled)
            {
                if (attacker is not null) Despawn(attacker);
                _cityAttackers.Remove(city.ZoneId);
                _unsummonedCityAttacks.Remove(city.ZoneId);
                continue;
            }
            // A claim whose map unloaded before this apply keeps its summon for the next refresh that finds the map.
            if (!_unsummonedCityAttacks.TryGetValue(city.ZoneId, out (int SpawnIndex, uint Entry) pick)
                || Creatures(city.MapId) is not { } creatures) continue;
            _unsummonedCityAttacks.Remove(city.ZoneId);
            if (attacker is not null) Despawn(attacker);
            _cityAttackers.Remove(city.ZoneId);
            ScourgeInvasionPosition at = city.Spawns[pick.SpawnIndex];
            if (creatures.SummonInstanceCreature(pick.Entry, at.X, at.Y, at.Z, at.O) is { } summoned)
            {
                _cityAttackers[city.ZoneId] = summoned;
                if (!creatures.StartEntryWaypointPath(summoned, ScourgeInvasionCatalog.CityAttackPath(city.ZoneId, pick.SpawnIndex)))
                    logger.LogWarning("Scourge city attacker {Entry} in zone {Zone} has no entry path {Path}; it holds its spawn point",
                        pick.Entry, city.ZoneId, ScourgeInvasionCatalog.CityAttackPath(city.ZoneId, pick.SpawnIndex));
            }
            else
                logger.LogWarning("Scourge city attack in zone {Zone}: creature template {Entry} is missing", city.ZoneId, pick.Entry);
        }
    }

    private static void SetEvent(GameEventService service, ushort id, bool active)
    {
        if (!service.IsValidEvent(id)) return;
        if (active && !service.IsActiveEvent(id)) service.StartEvent(id);
        else if (!active && service.IsActiveEvent(id)) service.StopEvent(id);
    }

    private sealed class NecropolisHealthAi(Creature creature, ScourgeInvasionFeature feature) : CreatureAI(creature)
    {
        private int _zaps;
        public override void OnRespawn() => _zaps = 0;
        public override void OnSpellHit(Unit caster, SpellInfo spell)
        {
            if (spell.Id == ScourgeInvasionCatalog.CampDeathCommunique)
                DoCast(Me, ScourgeInvasionCatalog.ZapNecropolis, triggered: true);
            else if (spell.Id == ScourgeInvasionCatalog.ZapNecropolis && ++_zaps >= 3)
                System?.KillCreature(Me);
        }
        public override void OnDeath(Unit? killer) => feature.OnNecropolisDied(Me);
    }
}
