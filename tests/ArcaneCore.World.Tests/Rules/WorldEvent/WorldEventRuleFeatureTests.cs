using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Rules;
using ArcaneCore.Game.Rules.WorldEvent;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Rules;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Rules;
using ArcaneCore.World.Rules.WorldEvent;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Rules.WorldEvent;

/// <summary>The world-event rule host in a world: game-event starts and stops and the world tick reach the shipped rows' vocabulary.</summary>
public sealed class WorldEventRuleFeatureTests : IDisposable
{
    private const ushort Watched = 13, Other = 14;

    private sealed class Events : IGameEventDataStore
    {
        public Task<GameEventContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GameEventContent(
                [new GameEventRecord(Watched, 0, 525600, 1, 0, 0, "Watched"), new GameEventRecord(Other, 0, 525600, 1, 0, 0, "Other")], [], [], [], [], [], []));

        public Task SetDisabledAsync(uint entry, bool disabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RuleStore(RuleContent content) : IRuleContentStore
    {
        public Task<RuleContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }

    private sealed class NoConditions : IConditionContentStore
    {
        public Task<IReadOnlyList<ConditionRecord>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConditionRecord>>([]);
    }

    private readonly ServiceProvider _provider;
    private readonly WorldRuntime _world;
    private readonly GameEventFeature _events;

    private static RuleRecord Rule(int id, short eventType, int ownerId) => new()
    {
        Id = id, Host = (byte)RuleHostKind.WorldEvent, OwnerKind = (byte)RuleOwnerKind.GameEvent, OwnerId = ownerId, EventType = eventType,
    };

    private static RuleActionRecord Act(int rule, short type, int eventId)
        => new() { OwnerId = rule, ActionType = type, Param1 = eventId, TargetType = 1 };

    public WorldEventRuleFeatureTests()
        : this(new RuleContent
        {
            Rules =
            [
                Rule(1, WorldEventRuleIds.Started, Watched),
                Rule(2, WorldEventRuleIds.Stopped, Watched),
            ],
            Actions =
            [
                Act(1, WorldEventRuleIds.StartEvent, Other),
                Act(2, WorldEventRuleIds.StopEvent, Other),
            ],
        })
    {
    }

    private WorldEventRuleFeatureTests(RuleContent content)
    {
        var services = new ServiceCollection();
        services.AddScoped<IGameEventDataStore, Events>();
        services.AddSingleton<IRuleContentStore>(new RuleStore(content));
        services.AddSingleton<IConditionContentStore>(new NoConditions());
        _provider = services.BuildServiceProvider();
        IServiceScopeFactory scopes = _provider.GetRequiredService<IServiceScopeFactory>();
        _world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance), NullLogger<WorldRuntime>.Instance);
        _events = new GameEventFeature(_provider, NullLogger<GameEventFeature>.Instance);
        _events.Attach(_world);
        var conditions = new ConditionFeature(_provider, scopes, NullLogger<ConditionFeature>.Instance);
        conditions.Attach(_world);
        var rules = new RuleFeature(_provider, scopes, NullLogger<RuleFeature>.Instance);
        rules.Attach(_world);
        Assert.Empty(rules.Catalog.Rejected);
        new WorldEventRuleFeature(rules, _events, conditions, NullLogger<WorldEventRuleFeature>.Instance).Attach(_world);
        _world.RunTick(5_000); // the game-event service initialises
    }

    public void Dispose()
    {
        _world.Dispose();
        _provider.Dispose();
    }

    [Fact]
    public void AStartRule_StartsAnotherEvent()
    {
        Assert.False(_events.IsActiveEvent(Other));

        Assert.True(_events.Service!.StartEvent(Watched));

        Assert.True(_events.IsActiveEvent(Other));
    }

    [Fact]
    public void AStopRule_StopsAnotherEvent()
    {
        _events.Service!.StartEvent(Watched);
        Assert.True(_events.IsActiveEvent(Other));

        _events.Service.StopEvent(Watched);

        Assert.False(_events.IsActiveEvent(Other));
    }

    [Fact]
    public void AnEventWithoutRules_ChangesNothing()
    {
        _events.Service!.StartEvent(Other);

        Assert.False(_events.IsActiveEvent(Watched));
        _world.RunTick(100);
    }
}
