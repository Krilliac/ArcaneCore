using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Game.WorldState.States;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>classic-db 4498 resource piles (vmangos HandleWarEffortGameObject tiers) and the capital-city counters.</summary>
public sealed class WarEffortPileTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);

    private static WarEffortSnapshot Gathering(Func<WarEffortResource, long>? count = null)
        => new(WarEffortPhase.Gathering, 0,
            WarEffortCatalog.Resources.Select(r => count?.Invoke(r) ?? 0).ToArray());

    private static long[] Fraction(WarEffortPileGroup group, WarEffortTeam team, double fraction)
        => WarEffortCatalog.Resources.Select(r =>
            WarEffortPileCatalog.GroupOf(r.Id) == (group, team) ? (long)Math.Ceiling(r.Goal * fraction) : 0).ToArray();

    [Fact]
    public void CatalogHasEveryClassicDb4498RowOnceWithItsFactionMap()
    {
        Assert.Equal(59, WarEffortPileCatalog.Piles.Count);
        Assert.Equal(WarEffortPileCatalog.Piles.Count, WarEffortPileCatalog.Piles.Select(p => p.Guid).Distinct().Count());
        Assert.All(WarEffortPileCatalog.Piles, p => Assert.Equal(p.Team == WarEffortTeam.Alliance ? 0u : 1u, p.MapId));
        Assert.All(WarEffortPileCatalog.Piles, p => Assert.InRange(p.Guid, p.Team == WarEffortTeam.Alliance ? 155000u : 155500u,
            p.Team == WarEffortTeam.Alliance ? 155054u : 155554u));
        // Five groups, five tiers each, per faction; four Alliance and five Horde initial piles.
        foreach (WarEffortTeam team in Enum.GetValues<WarEffortTeam>())
            for (int tier = 1; tier <= 5; tier++)
                Assert.Equal(5, WarEffortPileCatalog.Piles.Count(p => p.Team == team && p.Tier == tier));
        Assert.Equal(4, WarEffortPileCatalog.Piles.Count(p => p.Team == WarEffortTeam.Alliance && p.Tier == 0));
        Assert.Equal(5, WarEffortPileCatalog.Piles.Count(p => p.Team == WarEffortTeam.Horde && p.Tier == 0));
        // Each faction has three resources per group.
        foreach (WarEffortTeam team in Enum.GetValues<WarEffortTeam>())
            foreach (WarEffortPileGroup group in Enum.GetValues<WarEffortPileGroup>())
                Assert.Equal(3, WarEffortCatalog.Resources.Count(r => WarEffortPileCatalog.GroupOf(r.Id) == (group, team)));
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.19, 0)]
    [InlineData(0.21, 1)]
    [InlineData(0.41, 2)]
    [InlineData(0.61, 3)]
    [InlineData(0.81, 4)]
    [InlineData(0.99, 4)]
    [InlineData(1.0, 5)]
    public void GatheringTierCountsDownFromTheGroupObjectiveInFifths(double fraction, int tier)
    {
        var state = new WarEffortSnapshot(WarEffortPhase.Gathering, 0,
            Fraction(WarEffortPileGroup.Bars, WarEffortTeam.Horde, fraction));
        Assert.Equal(tier, WarEffortPileCatalog.TierOf(state, WarEffortPileGroup.Bars, WarEffortTeam.Horde, Now));
        // Other groups and the other faction are unaffected.
        Assert.Equal(0, WarEffortPileCatalog.TierOf(state, WarEffortPileGroup.Bars, WarEffortTeam.Alliance, Now));
        Assert.Equal(0, WarEffortPileCatalog.TierOf(state, WarEffortPileGroup.Herbs, WarEffortTeam.Horde, Now));
    }

    [Fact]
    public void TransportTierShrinksOneTierPerDayAndLaterPhasesKeepTierOne()
    {
        var none = new long[WarEffortCatalog.ResourceCount];
        for (int daysLeft = 5; daysLeft >= 1; daysLeft--)
        {
            var moving = new WarEffortSnapshot(WarEffortPhase.Transporting,
                Now.ToUnixTimeSeconds() + (daysLeft - 1) * 86_400 + 60, none);
            Assert.Equal(daysLeft, WarEffortPileCatalog.TierOf(moving, WarEffortPileGroup.Cooking, WarEffortTeam.Alliance, Now));
        }

        foreach (WarEffortPhase phase in new[] { WarEffortPhase.Gong, WarEffortPhase.TenHourWar, WarEffortPhase.Done })
            Assert.Equal(1, WarEffortPileCatalog.TierOf(new WarEffortSnapshot(phase, 0, none), WarEffortPileGroup.Skinning, WarEffortTeam.Horde, Now));
        Assert.Equal(0, WarEffortPileCatalog.TierOf(WarEffortSnapshot.Disabled, WarEffortPileGroup.Skinning, WarEffortTeam.Horde, Now));
        Assert.Empty(WarEffortPileCatalog.Visible(WarEffortSnapshot.Disabled, Now));
    }

    [Fact]
    public void InitialPilesShowOnlyWhileGathering()
    {
        Assert.Equal(9, WarEffortPileCatalog.Visible(Gathering(), Now).Count(p => p.Tier == 0));
        var moving = new WarEffortSnapshot(WarEffortPhase.Transporting, Now.ToUnixTimeSeconds() + 4 * 86_400 + 1,
            new long[WarEffortCatalog.ResourceCount]);
        Assert.DoesNotContain(WarEffortPileCatalog.Visible(moving, Now), p => p.Tier == 0);
        Assert.Equal(50, WarEffortPileCatalog.Visible(moving, Now).Count());
    }

    [Fact]
    public void CapitalCountersFollowThePhase()
    {
        WarEffortSnapshot gathering = Gathering(r => r.Id == 0 ? 1234 : 0);
        IReadOnlyList<(uint Field, uint Value)> states = WarEffortPileCatalog.CapitalStates(gathering, Now);
        Assert.Equal(25 + 30, states.Count);
        Assert.Contains((2020u, 96000u), states); // Peacebloom total
        Assert.Contains((2021u, 1234u), states); // Peacebloom now
        Assert.Contains((1998u, 45000u), states); // shared Copper Bar total, once
        Assert.Single(states, s => s.Field == 1998);
        Assert.Equal(2018u, WarEffortCatalog.Resources[25].WorldStateField);
        Assert.Equal(1998u, WarEffortPileCatalog.TotalField(25));

        var moving = new WarEffortSnapshot(WarEffortPhase.Transporting, Now.ToUnixTimeSeconds() + 2 * 86_400 + 5,
            new long[WarEffortCatalog.ResourceCount]);
        Assert.Equal([(WarEffortCatalog.DaysLeftCondition, 3u)], WarEffortPileCatalog.CapitalStates(moving, Now));
        Assert.Empty(WarEffortPileCatalog.CapitalStates(moving with { Phase = WarEffortPhase.Gong }, Now));
    }

    [Fact]
    public void FeatureFillsCapitalZonesOnly()
    {
        using Rig rig = new(Gathering(r => r.Id == 3 ? 77 : 0));
        var orgrimmar = new List<WorldStatePair>();
        rig.War.Fill(null!, 1637, orgrimmar);
        Assert.Contains(new WorldStatePair(2079, 77), orgrimmar);
        var barrens = new List<WorldStatePair>();
        rig.War.Fill(null!, 17, barrens);
        Assert.Empty(barrens);
    }

    [Fact]
    public void PilesAreSummonedFromSavedStateAfterRestartAndFollowLaterTurnIns()
    {
        using Rig rig = new(new WarEffortSnapshot(WarEffortPhase.Gathering, 0,
            Fraction(WarEffortPileGroup.Bars, WarEffortTeam.Horde, 0.45)));
        rig.World.RunTick(5_000);
        HashSet<uint> horde = rig.War.SpawnedPileGuids(rig.Kalimdor).ToHashSet();
        // Five initial Horde piles plus Bars tiers 1-2 (155514, 155524).
        Assert.Equal(new HashSet<uint> { 155500, 155501, 155502, 155503, 155504, 155514, 155524 }, horde);
        Assert.Equal(4, rig.War.SpawnedPileGuids(rig.EasternKingdoms).Count());
        Assert.Contains(rig.KalimdorObjects.GameObjects, go => go.Entry == 180840 && go.IsSpawned); // Bars tier 2

        rig.Store.State = new WarEffortSnapshot(WarEffortPhase.Gathering, 0,
            Fraction(WarEffortPileGroup.Bars, WarEffortTeam.Horde, 1.0));
        rig.World.RunTick(5_000);
        Assert.Equal(10, rig.War.SpawnedPileGuids(rig.Kalimdor).Count());

        // Transport day 4 of 5 (two days left): tier 2; the initial piles go away; every group shows tiers 1-2.
        rig.Store.State = rig.Store.State with
        {
            Phase = WarEffortPhase.Transporting,
            PhaseEndsAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86_400 + 60,
        };
        rig.World.RunTick(5_000);
        Assert.Equal(10, rig.War.SpawnedPileGuids(rig.Kalimdor).Count());
        Assert.DoesNotContain(155500u, rig.War.SpawnedPileGuids(rig.Kalimdor));
        Assert.DoesNotContain(155534u, rig.War.SpawnedPileGuids(rig.Kalimdor));
        Assert.Equal(20, rig.KalimdorObjects.GameObjects.Count(go => go.IsSpawned) + rig.EkObjects.GameObjects.Count(go => go.IsSpawned));
    }

    private sealed class MemoryStore : IWarEffortStateStore
    {
        public WarEffortSnapshot State { get; set; } = WarEffortSnapshot.Disabled;
        public Task<WarEffortSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);

        public Task SetPhaseAsync(WarEffortPhase phase, long phaseEndsAtUnix, CancellationToken cancellationToken = default)
        {
            State = State with { Phase = phase, PhaseEndsAtUnix = phaseEndsAtUnix };
            return Task.CompletedTask;
        }

        public Task<bool> MarkBossKilledAsync(int bossIndex, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _provider;
        public WorldRuntime World { get; }
        public WarEffortFeature War { get; }
        public MemoryStore Store { get; }
        public Map Kalimdor { get; }
        public Map EasternKingdoms { get; }
        public GameObjectMapSystem KalimdorObjects { get; }
        public GameObjectMapSystem EkObjects { get; }

        public Rig(WarEffortSnapshot saved)
        {
            var services = new ServiceCollection();
            services.AddSingleton(new MemoryStore { State = saved });
            services.AddScoped<IWarEffortStateStore>(sp => sp.GetRequiredService<MemoryStore>());
            services.AddScoped<IGameEventDataStore, NoEvents>();
            services.AddSingleton(sp => new GameEventFeature(sp, NullLogger<GameEventFeature>.Instance));
            services.AddSingleton(sp => new WarEffortFeature(sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<GameEventFeature>(), NullLogger<WarEffortFeature>.Instance));
            _provider = services.BuildServiceProvider();
            Store = _provider.GetRequiredService<MemoryStore>();
            World = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
                new CharacterSaveQueue(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
                NullLogger<WorldRuntime>.Instance);
            var content = new GameObjectContent(
                [.. WarEffortPileCatalog.Piles.Select(p => p.Entry).Distinct().Select(Template)], [], [], [], []);
            Kalimdor = World.GetMap(1);
            KalimdorObjects = new GameObjectMapSystem(Kalimdor, content);
            Kalimdor.AddUpdater(KalimdorObjects);
            EasternKingdoms = World.GetMap(0);
            EkObjects = new GameObjectMapSystem(EasternKingdoms, content);
            EasternKingdoms.AddUpdater(EkObjects);
            _provider.GetRequiredService<GameEventFeature>().Attach(World);
            War = _provider.GetRequiredService<WarEffortFeature>();
            War.Attach(World);
        }

        private static GameObjectTemplate Template(uint entry) => new()
        {
            Entry = entry, Type = (uint)GameObjectType.Generic, DisplayId = 6500, Name = $"AQWar - Resource {entry}", Size = 1.0f,
            Data = new uint[GameObjectTemplate.DataCount],
        };

        public void Dispose()
        {
            World.Dispose();
            _provider.Dispose();
        }
    }

    private sealed class NoEvents : IGameEventDataStore
    {
        public Task<GameEventContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GameEventContent([], [], [], [], [], [], []));

        public Task SetDisabledAsync(uint entry, bool disabled, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
