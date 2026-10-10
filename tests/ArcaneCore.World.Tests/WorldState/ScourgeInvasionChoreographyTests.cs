using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
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
        public List<uint> CityDefeats { get; } = [];
        public Task<bool> CityAttackDefeatedAsync(uint zoneId, long nowUnix, int nextAttackSeconds,
            CancellationToken cancellationToken = default)
        {
            Assert.InRange(nextAttackSeconds, ScourgeInvasionCatalog.CityAttackTimerMinSeconds, ScourgeInvasionCatalog.CityAttackTimerMaxSeconds);
            if (State.State != ScourgeInvasionState.Enabled) return Task.FromResult(false);
            CityDefeats.Add(zoneId);
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
                    Template(ScourgeInvasionCatalog.Flameshocker, "Flameshocker"),
                ];
                int[] textIds = [.. ScourgeInvasionCatalog.PallidYells, .. ScourgeInvasionCatalog.MouthZoneStartYells,
                    .. ScourgeInvasionCatalog.MouthZoneEndYells, .. ScourgeInvasionCatalog.MouthRandomYells,
                    ScourgeInvasionCatalog.BolvarCastleDefended, ScourgeInvasionCatalog.SylvanasCourtDefended];
                var ai = new CreatureAiContent([], [], new BroadcastTextCatalog(textIds.Select(id =>
                    new BroadcastText((uint)id, $"text {id}", "", 1, 0, 0, [], []))));
                (uint, uint, CreatureWaypoint)[] paths =
                [
                    .. new[] { ScourgeInvasionCatalog.PallidHorror, ScourgeInvasionCatalog.PatchworkTerror }
                        .SelectMany(entry => Enumerable.Range(0, 4).SelectMany(path => Enumerable.Range(1, 3).Select(point =>
                            (entry, (uint)path, new CreatureWaypoint((uint)point, -8578f + point * 10, 886f, 87.3f, 0, 0))))),
                ];
                foreach (uint mapId in new uint[] { 0, 1 })
                {
                    Map map = World.GetMap(mapId);
                    map.AddUpdater(new CreatureMapSystem(map, new CreatureContent(templates, [], [], [], [], ai, paths)));
                }
            }
            _provider.GetRequiredService<GameEventFeature>().Attach(World);
            Invasion = _provider.GetRequiredService<ScourgeInvasionFeature>();
            Invasion.Random = new Random(20261010);
            Invasion.NowUnix = () => Now;
            Invasion.Attach(World);
        }

        public RecordingSession AddPlayer(uint mapId, float x, float y, float z)
        {
            var session = new RecordingSession();
            var player = new Player(new CharacterRecord
            {
                Id = 7, AccountId = 1, Name = "Watcher", Race = (byte)Race.Human, Class = (byte)Class.Warrior,
                Gender = (byte)Gender.Male, Level = 60, MapId = mapId, X = x, Y = y, Z = z,
            }, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), session);
            World.AddPlayer(player);
            return session;
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

    internal sealed class RecordingSession : IPlayerSession
    {
        public int AccountId => 1;
        public AccountSecurity Security => AccountSecurity.Player;
        public int Chat { get; private set; }
        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
            if (opcode == WorldOpcode.SmsgMessagechat) Chat++;
        }
        public void ProcessWorldPackets(Player player) { }
        public void Kick() { }
        public void OnLoggedOut() { }
    }

    [Fact]
    public void CityAttackerWalksItsEntryPathWithFlameshockersAndItsDeathSavesTheNextAttack()
    {
        var store = new StateStore();
        using var rig = new Rig(store);
        Assert.True(rig.Events.Service!.StartEvent(17));
        rig.World.RunTick(5_000);
        rig.World.RunTick(1_000);
        Creature attacker = rig.Invasion.CityAttackers[1519];
        Assert.Equal(MovementGeneratorType.Waypoint, attacker.Motion.CurrentType);
        var ai = Assert.IsType<PallidHorrorAi>(attacker.AI);
        Assert.Equal(1519u, ai.ZoneId);
        Assert.InRange(ai.Flameshockers.Count, 5, 9);
        Assert.All(ai.Flameshockers, f =>
        {
            Assert.True(f.IsAlive);
            Assert.Equal(MovementGeneratorType.Follow, f.Motion.CurrentType);
            Assert.IsType<FlameshockerAi>(f.AI);
        });
        Creature[] shockers = [.. ai.Flameshockers];

        rig.Now += 60;
        attacker.Map!.FindUpdater<CreatureMapSystem>()!.KillCreature(attacker);
        Assert.All(shockers, f => Assert.False(f.IsAlive));
        Assert.Equal([1519u], store.CityDefeats);
        Assert.InRange(store.State.NextCityAttack(1519), rig.Now + 2700, rig.Now + 3600);
        Assert.False(rig.Invasion.CityAttackers.ContainsKey(1519));
        int claims = store.Claims;
        rig.World.RunTick(5_000); // the defended capital waits for its saved timer
        Assert.Equal(claims, store.Claims);
        Assert.False(rig.Invasion.CityAttackers.ContainsKey(1519));
    }

    [Fact]
    public void MouthsYellTheirZoneStartAndEndToPlayersOfTheirZone()
    {
        var store = new StateStore();
        using var rig = new Rig(store);
        ScourgeInvasionPosition azshara = ScourgeInvasionCatalog.MouthPositions[16];
        RecordingSession watcher = rig.AddPlayer(1, azshara.X + 5, azshara.Y, azshara.Z);
        Assert.True(rig.Events.Service!.StartEvent(17));
        rig.World.RunTick(5_000);
        rig.World.RunTick(1_000);
        Assert.All(rig.Invasion.Mouths.Values, m => Assert.IsType<ScourgeMouthAi>(m.AI));
        // Without terrain every map-1 point reads zone 0, so the three Kalimdor Mouths all reach the watcher once.
        Assert.Equal(3, watcher.Chat);
        Creature mouth = rig.Invasion.Mouths[16];
        var mouthAi = Assert.IsType<ScourgeMouthAi>(mouth.AI);
        CreatureMapSystem kalimdor = mouth.Map!.FindUpdater<CreatureMapSystem>()!;
        MovementGeneratorType before = mouth.Motion.CurrentType;
        Assert.False(kalimdor.StartEntryWaypointPath(mouth, 0)); // the Mouth's entry has no path: nothing changes
        Assert.Equal(before, mouth.Motion.CurrentType);
        Assert.Equal(16u, mouthAi.ZoneId);

        store.ZoneDefeated(16);
        rig.World.RunTick(5_000);
        Assert.Equal(4, watcher.Chat); // one end yell
        Assert.True(mouthAi.Ended);
        Assert.False(mouth.IsInWorld && mouth.IsAlive);
        rig.World.RunTick(5_000);
        Assert.Equal(4, watcher.Chat);
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
