using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

public sealed class ScourgeInvasionConditionTests
{
    private sealed class StateStore : IScourgeInvasionStateStore
    {
        public ScourgeInvasionSnapshot State { get; private set; } = ScourgeInvasionSnapshot.Disabled;
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
            CancellationToken cancellationToken = default)
        {
            ScourgeInvasionZoneProgress? zone = State.Zones.FirstOrDefault(z => z.ZoneId == zoneId);
            if (zone is null || zone.Remaining == 0) return Task.FromResult(false);
            State = State with { Zones = State.Zones.Select(z => z.ZoneId == zoneId
                ? z with { Remaining = z.Remaining - 1, NextAttackUnix = z.Remaining == 1 ? nowUnix + nextAttackSeconds : 0 }
                : z).ToArray(), BattlesWon = State.BattlesWon + (zone.Remaining == 1 ? 1 : 0) };
            return Task.FromResult(true);
        }
        public Task<bool> RestartZoneAsync(uint zoneId, long nowUnix, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public void Set(ScourgeInvasionSnapshot snapshot) => State = snapshot;
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

    private sealed class Conditions : IConditionContentStore
    {
        public Task<IReadOnlyList<ConditionRecord>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConditionRecord>>(
            [new(2148, (int)ConditionType.WorldScript, 2259, 0, 0, 0, 0),
             new(2149, (int)ConditionType.WorldScript, 2260, 0, 0, 0, 0)]);
    }

    [Fact]
    public async Task MainEventStartsTheSixZoneConditionsAndADefeatStopsItsZoneEvent()
    {
        var services = new ServiceCollection();
        services.AddSingleton<StateStore>();
        services.AddScoped<IScourgeInvasionStateStore>(sp => sp.GetRequiredService<StateStore>());
        services.AddScoped<IGameEventDataStore, Events>();
        services.AddSingleton<IConditionContentStore, Conditions>();
        services.AddSingleton(sp => new GameEventFeature(sp, NullLogger<GameEventFeature>.Instance));
        services.AddSingleton(sp => new ScourgeInvasionFeature(sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<GameEventFeature>(), NullLogger<ScourgeInvasionFeature>.Instance));
        services.AddSingleton(sp => new ConditionFeature(sp, sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ConditionFeature>.Instance));
        using ServiceProvider provider = services.BuildServiceProvider();
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
            NullLogger<WorldRuntime>.Instance);
        GameEventFeature events = provider.GetRequiredService<GameEventFeature>();
        events.Attach(world);
        ScourgeInvasionFeature invasion = provider.GetRequiredService<ScourgeInvasionFeature>();
        invasion.Attach(world);
        ConditionFeature conditions = provider.GetRequiredService<ConditionFeature>();
        conditions.Attach(world);
        world.RunTick(5_000);
        Assert.Equal(false, conditions.Current.EvaluateWithoutSubjects(2149));

        Assert.True(events.Service!.StartEvent(17));
        world.RunTick(5_000);
        Assert.Equal(true, conditions.Current.EvaluateWithoutSubjects(2148));
        Assert.Equal(true, conditions.Current.EvaluateWithoutSubjects(2149));
        Assert.All(ScourgeInvasionCatalog.Zones, zone => Assert.True(events.IsActiveEvent(zone.EventId)));

        StateStore store = provider.GetRequiredService<StateStore>();
        await store.NecropolisDestroyedAsync(16, 97592, 1_800_000_000, 3000);
        await store.NecropolisDestroyedAsync(16, 97593, 1_800_000_000, 3000);
        world.RunTick(5_000);
        Assert.Equal(false, conditions.Current.EvaluateWithoutSubjects(2149));
        Assert.False(events.IsActiveEvent(92));
        Assert.True(events.IsActiveEvent(90));

        Assert.True(events.Service.StopEvent(17));
        world.RunTick(5_000);
        Assert.Equal(ScourgeInvasionState.Disabled, store.State.State);
        Assert.All(ScourgeInvasionCatalog.Zones, zone => Assert.False(events.IsActiveEvent(zone.EventId)));

        store.Set(new ScourgeInvasionSnapshot(ScourgeInvasionState.Enabled, 50, 0,
            ScourgeInvasionSnapshot.Disabled.Zones));
        world.RunTick(5_000);
        Assert.True(events.IsActiveEvent(96));
        store.Set(store.State with { BattlesWon = 100 });
        world.RunTick(5_000);
        Assert.False(events.IsActiveEvent(96));
        Assert.True(events.IsActiveEvent(97));
        store.Set(store.State with { BattlesWon = 150 });
        world.RunTick(5_000);
        Assert.False(events.IsActiveEvent(17));
        Assert.True(events.IsActiveEvent(98));
        Assert.True(events.IsActiveEvent(99));
    }
}
