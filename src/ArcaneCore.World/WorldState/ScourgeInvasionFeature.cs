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
    private readonly HashSet<(uint ZoneId, uint SpawnGuid)> _pendingDeaths = [];
    private uint _reloadMs;

    public ScourgeInvasionSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void Attach(WorldRuntime world)
    {
        _world = world;
        using (IServiceScope scope = scopes.CreateScope())
        {
            if (scope.ServiceProvider.GetService<IScourgeInvasionStateStore>() is { } store)
            {
                _hasStore = true;
                Volatile.Write(ref _snapshot, store.LoadAsync().GetAwaiter().GetResult());
            }
        }
        if (_hasStore) WorldStateHooks.For(world).WorldStates.Add(this);
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
        if (active && Snapshot.State == ScourgeInvasionState.Disabled)
        {
            using IServiceScope scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<IScourgeInvasionStateStore>()
                .StartAsync().GetAwaiter().GetResult();
            Reload();
        }
        else if (!active && Snapshot.State == ScourgeInvasionState.Enabled && Snapshot.BattlesWon < 150)
        {
            using IServiceScope scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<IScourgeInvasionStateStore>()
                .StopAsync().GetAwaiter().GetResult();
            Reload();
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
            }
            if (map.FindUpdater<GameObjectMapSystem>() is { } objects && !_circleAis.ContainsKey(objects))
            {
                var ai = new InvasionCircleAi(this);
                _circleAis.Add(objects, ai);
                objects.RegisterAi(ScourgeInvasionCatalog.SummonCircle, ai);
            }
        }
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
        _pendingDeaths.Add((zone, known.Guid));
        FlushDeaths();
    }

    private void FlushDeaths()
    {
        if (_pendingDeaths.Count == 0) return;
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            IScourgeInvasionStateStore store = scope.ServiceProvider.GetRequiredService<IScourgeInvasionStateStore>();
            foreach ((uint zone, uint guid) in _pendingDeaths.ToArray())
            {
                store.NecropolisDestroyedAsync(zone, guid, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Random.Shared.Next(2700, 3601)).GetAwaiter().GetResult();
                _pendingDeaths.Remove((zone, guid));
            }
            Reload();
            SyncEvents();
        }
        catch (Exception ex)
        {
            // Complete CreatureMapSystem's death transition even if the realm store is unavailable.
            logger.LogError(ex, "could not persist Scourge Necropolis death; {Count} pending retry", _pendingDeaths.Count);
        }
    }

    private void OnTick(uint diffMs)
    {
        if (!_hasStore) return;
        _reloadMs = _reloadMs > diffMs ? _reloadMs - diffMs : 0;
        if (_reloadMs != 0) return;
        try
        {
            FlushDeaths();
            Reload();
            if (Snapshot.State == ScourgeInvasionState.Enabled && Snapshot.BattlesWon < 150)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (ScourgeInvasionZoneProgress zone in Snapshot.Zones.OrderBy(z => z.NextAttackUnix))
                {
                    if (zone.Remaining != 0 || zone.NextAttackUnix == 0 || zone.NextAttackUnix > now) continue;
                    using IServiceScope scope = scopes.CreateScope();
                    if (scope.ServiceProvider.GetRequiredService<IScourgeInvasionStateStore>()
                        .RestartZoneAsync(zone.ZoneId, now).GetAwaiter().GetResult()) Reload();
                    if (Snapshot.Zones.Count(z => z.Remaining > 0) > 1) break;
                }
            }
            SyncEvents();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "could not refresh Scourge invasion state");
        }
        _reloadMs = 5_000;
    }

    private void Reload()
    {
        using IServiceScope scope = scopes.CreateScope();
        ScourgeInvasionSnapshot previous = Snapshot;
        ScourgeInvasionSnapshot current = scope.ServiceProvider.GetRequiredService<IScourgeInvasionStateStore>()
            .LoadAsync().GetAwaiter().GetResult();
        Volatile.Write(ref _snapshot, current);
        PublishWorldStates(previous, current);
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
