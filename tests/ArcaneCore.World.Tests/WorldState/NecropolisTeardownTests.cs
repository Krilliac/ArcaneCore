using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>
/// Destroying a Scourge Necropolis (vmangos scourge_invasion.cpp NecropolisHealthAI::JustDied / SpellHitTarget, :402-457): the Necropolis
/// creature (16401), the necropolis objects within 5 yd of it and the Health unit itself leave the world and do not respawn, neither after
/// the Health unit's own 120 s respawn delay nor after a restart. The remaining Necropolises of the zone are untouched.
/// </summary>
public sealed class NecropolisTeardownTests
{
    private const float X = 3300f;
    private const float Y = -4300f;
    private const float Z = 100f;
    private const uint HealthGuid = 9100;
    private const uint NecropolisGuid = 9101;
    private const uint NearObjectGuid = 9500;
    private const uint FarObjectGuid = 9501;
    private const int TanarisEvent = 91;
    private const long StartUnix = 1_800_000_000;

    private sealed class StateStore : IScourgeInvasionStateStore
    {
        private readonly HashSet<uint> _dead = [];
        private ScourgeInvasionSnapshot _state = new(ScourgeInvasionState.Enabled, 0, 0,
            ScourgeInvasionCatalog.Zones.Select(z => new ScourgeInvasionZoneProgress(z.ZoneId, z.Necropolises, 0)).ToArray());

        public IReadOnlyCollection<uint> Dead => _dead;
        public Task<ScourgeInvasionSnapshot> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_state with { DestroyedSpawnGuids = new HashSet<uint>(_dead) });
        public Task<bool> StartAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> NecropolisDestroyedAsync(uint zoneId, uint spawnGuid, long nowUnix, int nextAttackSeconds,
            CancellationToken cancellationToken = default)
        {
            if (!_dead.Add(spawnGuid)) return Task.FromResult(false);
            _state = _state with { Zones = _state.Zones.Select(z => z.ZoneId == zoneId
                ? z with { Remaining = z.Remaining - 1 } : z).ToArray() };
            return Task.FromResult(true);
        }
        public Task<bool> RestartZoneAsync(uint zoneId, long nowUnix, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    /// <summary>Tanaris (zone 440, map 1, event 91): the Health spawn is bound to the zone's event, which is how the feature resolves its zone.</summary>
    private sealed class ZoneEvents : IGameEventDataStore
    {
        public Task<GameEventContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GameEventContent(
                [new GameEventRecord(ScourgeInvasionCatalog.MainEvent, 0, 525600, 1, 0, 0, "Scourge Invasion"),
                 .. Enumerable.Range(90, 10).Select(id => new GameEventRecord((uint)id, 0, 525600, 1, 0, 0, $"Scourge {id}"))],
                [], [new GameEventSpawnRecord(HealthGuid, TanarisEvent)], [], [], [], []));
        public Task SetDisabledAsync(uint entry, bool disabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeClock : IRespawnClock
    {
        public long UnixSeconds { get; set; } = StartUnix;
    }

    private sealed class FakePersistence : ICreatureRespawnPersistence
    {
        public Dictionary<(uint Map, uint Instance, uint Guid), long> Stored { get; } = [];

        public IReadOnlyDictionary<uint, long> GetPending(uint mapId, uint instanceId)
            => Stored.Where(e => e.Key.Map == mapId && e.Key.Instance == instanceId).ToDictionary(e => e.Key.Guid, e => e.Value);

        public bool TryGetPending(uint mapId, uint instanceId, uint spawnGuid, out long respawnUnixSeconds)
            => Stored.TryGetValue((mapId, instanceId, spawnGuid), out respawnUnixSeconds);

        public void Save(uint mapId, uint instanceId, uint spawnGuid, long respawnUnixSeconds)
            => Stored[(mapId, instanceId, spawnGuid)] = respawnUnixSeconds;

        public void Delete(uint mapId, uint instanceId, uint spawnGuid) => Stored.Remove((mapId, instanceId, spawnGuid));
    }

    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly WorldRuntime _world;
        public CreatureMapSystem Creatures { get; }
        public GameObjectMapSystem Objects { get; }

        public Rig(StateStore store, FakePersistence persistence, FakeClock clock)
        {
            var services = new ServiceCollection();
            services.AddSingleton(store);
            services.AddScoped<IScourgeInvasionStateStore>(sp => sp.GetRequiredService<StateStore>());
            services.AddScoped<IGameEventDataStore, ZoneEvents>();
            services.AddSingleton(sp => new GameEventFeature(sp, NullLogger<GameEventFeature>.Instance));
            services.AddSingleton(sp => new ScourgeInvasionFeature(sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<GameEventFeature>(), NullLogger<ScourgeInvasionFeature>.Instance));
            _provider = services.BuildServiceProvider();
            _world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
                new CharacterSaveQueue(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
                NullLogger<WorldRuntime>.Instance);
            _world.Options.Maps.UpdateActiveCellsOnly = false;
            Map map = _world.GetMap(1);
            CreatureTemplate[] templates =
            [
                Template(ScourgeInvasionCatalog.NecropolisHealth, "Necropolis Health"),
                Template(ScourgeInvasionCatalog.Necropolis, "Necropolis"),
            ];
            CreatureSpawn[] spawns =
            [
                new() { Guid = HealthGuid, Entry = ScourgeInvasionCatalog.NecropolisHealth, MapId = 1, X = X, Y = Y, Z = Z },
                new() { Guid = NecropolisGuid, Entry = ScourgeInvasionCatalog.Necropolis, MapId = 1, X = X + 2f, Y = Y, Z = Z },
            ];
            Creatures = new CreatureMapSystem(map, new CreatureContent(templates, spawns, [], [], []),
                random: new Random(7), respawnPersistence: persistence, respawnClock: clock);
            map.AddUpdater(Creatures);
            GameObjectTemplate[] objectTemplates = [.. new uint[] { 181154, 181215 }.Select(entry => new GameObjectTemplate
            {
                Entry = entry, Type = 5, DisplayId = 1, Name = "Necropolis", Size = 1f, Data = new uint[GameObjectTemplate.DataCount],
            })];
            Objects = new GameObjectMapSystem(map, new GameObjectContent(objectTemplates,
            [
                new GameObjectSpawn { Guid = NearObjectGuid, Entry = 181154, MapId = 1, X = X + 4f, Y = Y, Z = Z },
                new GameObjectSpawn { Guid = FarObjectGuid, Entry = 181215, MapId = 1, X = X + 60f, Y = Y, Z = Z },
            ], [], [], []));
            map.AddUpdater(Objects);
            _provider.GetRequiredService<GameEventFeature>().Attach(_world);
            ScourgeInvasionFeature invasion = _provider.GetRequiredService<ScourgeInvasionFeature>();
            invasion.NowUnix = () => clock.UnixSeconds;
            invasion.StoreOffload = work => work(); // the fake store completes synchronously: apply in the same tick
            invasion.Attach(_world);
            Run(2); // the world tick installs the invasion AIs
            // Two long ticks (as in ScourgeInvasionChoreographyTests): the first full refresh loads the state, starts the invasion events
            // (the Tanaris event spawns the Health unit) and, after a restart, tears the saved destroyed Necropolises down again.
            _world.RunTick(5_000);
            _world.RunTick(5_000);
            Run(2);
            var player = new Player(new CharacterRecord
            {
                Id = 7, AccountId = 1, Name = "Watcher", Race = (byte)Race.Human, Class = (byte)Class.Warrior,
                Gender = (byte)Gender.Male, Level = 60, MapId = 1, X = X, Y = Y - 5f, Z = Z,
            }, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new ScourgeInvasionChoreographyTests.RecordingSession());
            _world.AddPlayer(player);
            Run(4);
        }

        public void Run(int ticks)
        {
            for (int i = 0; i < ticks; i++) _world.RunTick(500);
        }

        public Creature? Alive(uint entry) => Creatures.Creatures.FirstOrDefault(c => c.Entry == entry && c.IsAlive);

        public bool HasObject(uint entry) => Objects.GameObjects.Any(o => o.Entry == entry && o.IsSpawned);

        public void Dispose()
        {
            _world.Dispose();
            _provider.Dispose();
        }

        private static CreatureTemplate Template(uint entry, string name) => new()
        {
            Entry = entry, Name = name, MinLevel = 60, MaxLevel = 60, DisplayIds = [16000], Faction = 35,
            MinLevelHealth = 1000, MaxLevelHealth = 1000,
        };
    }

    [Fact]
    public void ADestroyedNecropolisTakesItsCreatureAndObjectsAndDoesNotComeBackAfterTheRespawnDelayOrARestart()
    {
        var store = new StateStore();
        var persistence = new FakePersistence();
        var clock = new FakeClock();
        using (var rig = new Rig(store, persistence, clock))
        {
            Creature health = Assert.IsType<Creature>(rig.Alive(ScourgeInvasionCatalog.NecropolisHealth));
            Assert.NotNull(rig.Alive(ScourgeInvasionCatalog.Necropolis));
            Assert.True(rig.HasObject(181154));
            Assert.True(rig.HasObject(181215));

            rig.Creatures.KillCreature(health);
            rig.Run(4);

            Assert.Null(rig.Alive(ScourgeInvasionCatalog.NecropolisHealth));
            Assert.Null(rig.Alive(ScourgeInvasionCatalog.Necropolis));
            Assert.False(rig.HasObject(181154));
            Assert.True(rig.HasObject(181215)); // another necropolis of the zone, out of the 5 yd range
            Assert.DoesNotContain(rig.Creatures.Creatures, c => c.DeathState == CreatureDeathState.Corpse);

            // The Health unit's own spawn time is 120 s: it must not come back, nor may the Necropolis or its object.
            clock.UnixSeconds += 130;
            rig.Run(260);
            Assert.Null(rig.Alive(ScourgeInvasionCatalog.NecropolisHealth));
            Assert.Null(rig.Alive(ScourgeInvasionCatalog.Necropolis));
            Assert.False(rig.HasObject(181154));
            Assert.True(rig.HasObject(181215));
            Assert.Contains(HealthGuid, store.Dead);
            Assert.True(persistence.Stored.ContainsKey((1, 0, HealthGuid)));
            Assert.True(persistence.Stored.ContainsKey((1, 0, NecropolisGuid)));
        }

        // Restart: the stored "never" keeps both creatures dead, and the saved destroyed state removes the object again.
        using var restarted = new Rig(store, persistence, clock);
        restarted.Run(20);
        Assert.Null(restarted.Alive(ScourgeInvasionCatalog.NecropolisHealth));
        Assert.Null(restarted.Alive(ScourgeInvasionCatalog.Necropolis));
        Assert.False(restarted.HasObject(181154));
        Assert.True(restarted.HasObject(181215));
    }

    [Fact]
    public void ADestroyedNecropolisKeepsItsHealthUnitOutWhenNoStoredRespawnRowSurvives()
    {
        // Creatures:Respawn:Persist=false (or a lost row): the event refresh spawns the Health unit again at start, although the saved state
        // counts its Necropolis as destroyed. EnforceTeardowns removes it, as it does the Necropolis and its objects.
        var store = new StateStore();
        Assert.True(store.NecropolisDestroyedAsync(440, HealthGuid, StartUnix, 3000).GetAwaiter().GetResult());
        using var rig = new Rig(store, new FakePersistence(), new FakeClock());
        rig.Run(40);
        Assert.Null(rig.Alive(ScourgeInvasionCatalog.NecropolisHealth));
        Assert.Null(rig.Alive(ScourgeInvasionCatalog.Necropolis));
        Assert.False(rig.HasObject(181154));
        Assert.True(rig.HasObject(181215));
    }

    [Fact]
    public void AnUntouchedNecropolisIsLeftAlone()
    {
        using var rig = new Rig(new StateStore(), new FakePersistence(), new FakeClock());
        rig.Run(20);
        Assert.NotNull(rig.Alive(ScourgeInvasionCatalog.NecropolisHealth));
        Assert.NotNull(rig.Alive(ScourgeInvasionCatalog.Necropolis));
        Assert.True(rig.HasObject(181154));
    }

    [Fact]
    public void RemoveWithoutRespawnStoresNeverAndRemovesOnlyOnce()
    {
        var persistence = new FakePersistence();
        var clock = new FakeClock();
        using var rig = new Rig(new StateStore(), persistence, clock);
        Creature necropolis = Assert.IsType<Creature>(rig.Alive(ScourgeInvasionCatalog.Necropolis));

        Assert.True(rig.Creatures.RemoveWithoutRespawn(necropolis));
        Assert.False(rig.Creatures.RemoveWithoutRespawn(necropolis));
        Assert.Equal(StartUnix + CreatureMapSystem.RemovedRespawnHorizonSeconds, persistence.Stored[(1, 0, NecropolisGuid)]);
        rig.Run(300);
        Assert.Null(rig.Alive(ScourgeInvasionCatalog.Necropolis));
    }
}
