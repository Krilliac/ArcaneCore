using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>Mouth of Kel'Thuzad per attacked zone and the persisted Stormwind/Undercity city attacks, including a restart.</summary>
public sealed class ScourgeInvasionChoreographyTests
{
    private sealed class StateStore : IScourgeInvasionStateStore
    {
        public ScourgeInvasionSnapshot State { get; set; } = ScourgeInvasionSnapshot.Disabled;
        public int Claims { get; private set; }
        public Task<ScourgeInvasionSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
        public Task<bool> StartAsync(CancellationToken cancellationToken = default)
        {
            if (State.State == ScourgeInvasionState.Enabled) return Task.FromResult(false);
            State = new ScourgeInvasionSnapshot(ScourgeInvasionState.Enabled, 0, 0,
                ScourgeInvasionCatalog.Zones.Select(z => new ScourgeInvasionZoneProgress(z.ZoneId, z.Necropolises, 0)).ToArray());
            return Task.FromResult(true);
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            State = ScourgeInvasionSnapshot.Disabled;
            return Task.CompletedTask;
        }
        public Task<bool> NecropolisDestroyedAsync(uint zoneId, uint spawnGuid, long nowUnix, int nextAttackSeconds,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> RestartZoneAsync(uint zoneId, long nowUnix, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public Task<bool> ClaimCityAttackAsync(uint zoneId, long nowUnix, int nextAttackSeconds,
            CancellationToken cancellationToken = default)
        {
            Assert.InRange(nextAttackSeconds, ScourgeInvasionCatalog.CityAttackTimerMinSeconds, ScourgeInvasionCatalog.CityAttackTimerMaxSeconds);
            if (!State.IsCityAttackDue(zoneId, nowUnix)) return Task.FromResult(false);
            Claims++;
            State = State with
            {
                Cities = State.Cities.Select(c => c.ZoneId == zoneId ? c with { NextAttackUnix = nowUnix + nextAttackSeconds } : c).ToArray(),
            };
            return Task.FromResult(true);
        }
        public void ZoneDefeated(uint zoneId) => State = State with
        {
            Zones = State.Zones.Select(z => z.ZoneId == zoneId ? z with { Remaining = 0 } : z).ToArray(),
        };
    }

    private sealed class Events : IGameEventDataStore
    {
        public Task<GameEventContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GameEventContent(
                [new GameEventRecord(17, 0, 525600, 1, 0, 0, "Scourge Invasion"),
                 .. Enumerable.Range(90, 10).Select(id => new GameEventRecord((uint)id, 0, 525600, 1, 0, 0, $"Scourge {id}"))],
                [], [], [], [], [], []));
        public Task SetDisabledAsync(uint entry, bool disabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _provider;
        public WorldRuntime World { get; }
        public ScourgeInvasionFeature Invasion { get; }
        public long Now { get; set; } = 1_800_000_000;

        public Rig(StateStore store, bool withMaps = true)
        {
            var services = new ServiceCollection();
            services.AddSingleton(store);
            services.AddScoped<IScourgeInvasionStateStore>(sp => sp.GetRequiredService<StateStore>());
            services.AddScoped<IGameEventDataStore, Events>();
            services.AddSingleton(sp => new GameEventFeature(sp, NullLogger<GameEventFeature>.Instance));
            services.AddSingleton(sp => new ScourgeInvasionFeature(sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<GameEventFeature>(), NullLogger<ScourgeInvasionFeature>.Instance));
            _provider = services.BuildServiceProvider();
            World = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
                new CharacterSaveQueue(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
                NullLogger<WorldRuntime>.Instance);
            if (withMaps)
            {
                CreatureTemplate[] templates =
                [
                    Template(ScourgeInvasionCatalog.MouthOfKelThuzad, "Mouth of Kel'Thuzad"),
                    Template(ScourgeInvasionCatalog.PallidHorror, "Pallid Horror"),
                    Template(ScourgeInvasionCatalog.PatchworkTerror, "Patchwork Terror"),
                ];
                foreach (uint mapId in new uint[] { 0, 1 })
                {
                    Map map = World.GetMap(mapId);
                    map.AddUpdater(new CreatureMapSystem(map, new CreatureContent(templates, [], [], [], [])));
                }
            }
            _provider.GetRequiredService<GameEventFeature>().Attach(World);
            Invasion = _provider.GetRequiredService<ScourgeInvasionFeature>();
            Invasion.Random = new Random(20261010);
            Invasion.NowUnix = () => Now;
            Invasion.Attach(World);
        }

        public GameEventFeature Events => _provider.GetRequiredService<GameEventFeature>();

        public IEnumerable<Creature> Living(uint entry) => World.Maps
            .Select(m => m.FindUpdater<CreatureMapSystem>()).OfType<CreatureMapSystem>()
            .SelectMany(s => s.Creatures).Where(c => c.Entry == entry && c.IsAlive && c.IsInWorld);

        public void Dispose()
        {
            World.Dispose();
            _provider.Dispose();
        }

        private static CreatureTemplate Template(uint entry, string name) => new()
        {
            Entry = entry, Name = name, MinLevel = 60, MaxLevel = 60, DisplayIds = [16000], Faction = 21,
            MinLevelHealth = 1000, MaxLevelHealth = 1000,
        };
    }

    [Fact]
    public void MouthsStandInAttackedZonesAndCitiesAreAttackedOnTheirSavedTimerAcrossARestart()
    {
        var store = new StateStore();
        using (var rig = new Rig(store))
        {
            rig.World.RunTick(5_000);
            Assert.Empty(rig.Invasion.Mouths);
            Assert.Empty(rig.Invasion.CityAttackers);

            Assert.True(rig.Events.Service!.StartEvent(17));
            rig.World.RunTick(5_000);
            Assert.Equal(ScourgeInvasionCatalog.Zones.Select(z => z.ZoneId).Order(), rig.Invasion.Mouths.Keys.Order());
            Assert.Equal(6, rig.Living(ScourgeInvasionCatalog.MouthOfKelThuzad).Count());
            Creature azsharaMouth = rig.Invasion.Mouths[16];
            Assert.Equal(1u, azsharaMouth.Map!.MapId);
            Assert.Equal(3273.75f, azsharaMouth.X, 0.01f);

            Assert.Equal([1497u, 1519u], rig.Invasion.CityAttackers.Keys.Order());
            Assert.All(rig.Invasion.CityAttackers.Values, c => Assert.Contains(c.Entry,
                new[] { ScourgeInvasionCatalog.PallidHorror, ScourgeInvasionCatalog.PatchworkTerror }));
            Assert.Equal(2, store.Claims);
            Creature stormwind = rig.Invasion.CityAttackers[1519];
            long next = store.State.NextCityAttack(1519);
            Assert.InRange(next, rig.Now + 2700, rig.Now + 3600);

            rig.World.RunTick(5_000); // not due again: no second summon, mouths not duplicated
            Assert.Equal(2, store.Claims);
            Assert.Same(stormwind, rig.Invasion.CityAttackers[1519]);
            Assert.Equal(6, rig.Living(ScourgeInvasionCatalog.MouthOfKelThuzad).Count());

            store.ZoneDefeated(16);
            rig.World.RunTick(5_000);
            rig.World.RunTick(5_000);
            Assert.False(rig.Invasion.Mouths.ContainsKey(16));
            Assert.DoesNotContain(rig.Living(ScourgeInvasionCatalog.MouthOfKelThuzad), c => c.Map!.MapId == 1 && c.X > 3000 && c.X < 3500);
            Assert.Equal(5, rig.Living(ScourgeInvasionCatalog.MouthOfKelThuzad).Count());

            rig.Now = next;
            int due = store.State.Cities.Count(c => c.NextAttackUnix <= next);
            rig.World.RunTick(5_000); // due: the old attacker is replaced, not doubled
            Assert.Equal(2 + due, store.Claims);
            Assert.NotSame(stormwind, rig.Invasion.CityAttackers[1519]);
            Assert.False(stormwind.IsInWorld && stormwind.IsAlive);
            Assert.Equal(2, rig.Living(ScourgeInvasionCatalog.PallidHorror).Count() + rig.Living(ScourgeInvasionCatalog.PatchworkTerror).Count());
        }

        // Restart: the saved state comes back, the five still-attacked zones get their Mouth again, and the capitals wait for their timers.
        int claims = store.Claims;
        using (var restarted = new Rig(store))
        {
            restarted.Now = 1_800_000_000 + 1;
            restarted.World.RunTick(5_000);
            Assert.Equal(5, restarted.Invasion.Mouths.Count);
            Assert.False(restarted.Invasion.Mouths.ContainsKey(16));
            Assert.Equal(claims, store.Claims);
            Assert.Empty(restarted.Invasion.CityAttackers);

            restarted.Now = store.State.Cities.Min(c => c.NextAttackUnix);
            restarted.World.RunTick(5_000);
            Assert.InRange(store.Claims, claims + 1, claims + 2);
            Assert.Equal(store.Claims - claims, restarted.Invasion.CityAttackers.Count);

            Assert.True(restarted.Events.Service!.StopEvent(17));
            restarted.World.RunTick(5_000);
            restarted.World.RunTick(5_000);
            Assert.Equal(ScourgeInvasionState.Disabled, store.State.State);
            Assert.Empty(restarted.Invasion.Mouths);
            Assert.Empty(restarted.Invasion.CityAttackers);
            Assert.Empty(restarted.Living(ScourgeInvasionCatalog.MouthOfKelThuzad));
            Assert.Empty(restarted.Living(ScourgeInvasionCatalog.PallidHorror));
            Assert.Empty(restarted.Living(ScourgeInvasionCatalog.PatchworkTerror));
        }
    }

    [Fact]
    public void AnUnloadedCapitalDoesNotSpendItsTimer()
    {
        var store = new StateStore();
        using var rig = new Rig(store, withMaps: false);
        Assert.True(rig.Events.Service!.StartEvent(17));
        rig.World.RunTick(5_000);
        rig.World.RunTick(5_000);
        Assert.Equal(0, store.Claims);
        Assert.Empty(rig.Invasion.Mouths);
        Assert.All(store.State.Cities, c => Assert.Equal(0, c.NextAttackUnix));
    }

    [Fact]
    public void MilestoneOneHundredFiftyStopsTheMouthsButCitiesKeepTheirTimer()
    {
        var store = new StateStore();
        using var rig = new Rig(store);
        Assert.True(rig.Events.Service!.StartEvent(17));
        rig.World.RunTick(5_000);
        Assert.Equal(6, rig.Invasion.Mouths.Count);
        store.State = store.State with { BattlesWon = 150 };
        rig.World.RunTick(5_000);
        rig.World.RunTick(5_000);
        Assert.Empty(rig.Invasion.Mouths);
        Assert.Equal(2, rig.Invasion.CityAttackers.Count); // mangos-classic city attacks run while the state is enabled
    }
}
