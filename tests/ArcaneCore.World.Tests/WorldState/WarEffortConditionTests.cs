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

public sealed class WarEffortConditionTests
{
    private sealed class MemoryStore : IWarEffortStateStore
    {
        public WarEffortSnapshot State { get; private set; } = WarEffortSnapshot.Disabled;

        public Task<WarEffortSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);

        public Task SetPhaseAsync(WarEffortPhase phase, long phaseEndsAtUnix, CancellationToken cancellationToken = default)
        {
            State = State with { Phase = phase, PhaseEndsAtUnix = phaseEndsAtUnix };
            return Task.CompletedTask;
        }

        public void CompletePeacebloom()
        {
            long[] counters = [.. State.Counters];
            counters[0] = WarEffortCatalog.Resources[0].Goal;
            State = State with { Counters = counters };
        }

        public void SetState(WarEffortSnapshot state) => State = state;
    }

    private sealed class Events : IGameEventDataStore
    {
        public Task<GameEventContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GameEventContent(
                [new GameEventRecord(120, 0, 525600, 1, 0, 0, "AQ gathering"),
                 new GameEventRecord(121, 0, 525600, 1, 0, 0, "AQ transport")],
                [], [], [], [], [], []));

        public Task SetDisabledAsync(uint entry, bool disabled, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class Conditions : IConditionContentStore
    {
        public Task<IReadOnlyList<ConditionRecord>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConditionRecord>>(
            [
                new(2037, (int)ConditionType.WorldScript, 2021, 0, 0, 0, 0),
                new(2038, (int)ConditionType.WorldScript, 9999, 0, 0, 0, 0),
            ]);
    }

    [Fact]
    public void GatheringEventAndDurableCounterDriveTheLiveType40Condition()
    {
        var services = new ServiceCollection();
        services.AddSingleton<MemoryStore>();
        services.AddScoped<IWarEffortStateStore>(sp => sp.GetRequiredService<MemoryStore>());
        services.AddSingleton<IConditionContentStore, Conditions>();
        services.AddSingleton(sp => new GameEventFeature(sp, NullLogger<GameEventFeature>.Instance));
        services.AddSingleton(sp => new WarEffortFeature(sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<GameEventFeature>(), NullLogger<WarEffortFeature>.Instance));
        services.AddSingleton(sp => new ConditionFeature(sp, sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ConditionFeature>.Instance));
        using ServiceProvider provider = services.BuildServiceProvider();
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
            NullLogger<WorldRuntime>.Instance);
        WarEffortFeature war = provider.GetRequiredService<WarEffortFeature>();
        war.Attach(world);
        ConditionFeature conditions = provider.GetRequiredService<ConditionFeature>();
        conditions.Attach(world);

        Assert.Equal(false, conditions.Current.EvaluateWithoutSubjects(2037));
        Assert.Null(war.WorldScriptCondition(9999, 0));
        war.OnEventChanged(WarEffortCatalog.GatheringEvent, active: true, resume: false);
        Assert.Equal(WarEffortPhase.Gathering, provider.GetRequiredService<MemoryStore>().State.Phase);
        provider.GetRequiredService<MemoryStore>().CompletePeacebloom();
        world.RunTick(5_000);
        Assert.Equal(true, conditions.Current.EvaluateWithoutSubjects(2037));
        Assert.Null(conditions.Current.EvaluateWithoutSubjects(2038));
    }

    [Fact]
    public void DurablePhaseTransitionStopsGatheringEventAndStartsTransportEvent()
    {
        var services = new ServiceCollection();
        services.AddSingleton<MemoryStore>();
        services.AddScoped<IWarEffortStateStore>(sp => sp.GetRequiredService<MemoryStore>());
        services.AddScoped<IGameEventDataStore, Events>();
        services.AddSingleton(sp => new GameEventFeature(sp, NullLogger<GameEventFeature>.Instance));
        services.AddSingleton(sp => new WarEffortFeature(sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<GameEventFeature>(), NullLogger<WarEffortFeature>.Instance));
        using ServiceProvider provider = services.BuildServiceProvider();
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
            NullLogger<WorldRuntime>.Instance);
        GameEventFeature events = provider.GetRequiredService<GameEventFeature>();
        events.Attach(world);
        WarEffortFeature war = provider.GetRequiredService<WarEffortFeature>();
        war.Attach(world);
        provider.GetRequiredService<MemoryStore>().SetState(
            new WarEffortSnapshot(WarEffortPhase.Gathering, 0, new long[WarEffortCatalog.ResourceCount]));
        world.RunTick(5_000);
        Assert.True(events.IsActiveEvent(120));

        provider.GetRequiredService<MemoryStore>().SetState(
            new WarEffortSnapshot(WarEffortPhase.Transporting, DateTimeOffset.UtcNow.AddDays(5).ToUnixTimeSeconds(),
                new long[WarEffortCatalog.ResourceCount]));
        world.RunTick(5_000);
        Assert.False(events.IsActiveEvent(120));
        Assert.True(events.IsActiveEvent(121));
    }

    [Fact]
    public void DaysLeftConditionUsesTheReferenceFloorPlusOneComparison()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var state = new WarEffortSnapshot(WarEffortPhase.Transporting, now.AddDays(2).ToUnixTimeSeconds(),
            new long[WarEffortCatalog.ResourceCount]);
        Assert.Equal(true, state.WorldScriptCondition(WarEffortCatalog.DaysLeftCondition, 3, now));
        Assert.Equal(false, state.WorldScriptCondition(WarEffortCatalog.DaysLeftCondition, 2, now));
        Assert.Equal(true, state.WorldScriptCondition(WarEffortCatalog.DaysLeftCondition, 1, now.AddDays(2)));
    }
}
