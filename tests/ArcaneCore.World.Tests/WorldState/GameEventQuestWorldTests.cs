using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Time;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>
/// Event quests in the daemon: the quest feature builds its store at its own pace, the event feature drives the schedule, and
/// <see cref="GameEventQuestFeature"/> joins them: a quest listed in <c>game_event_quest</c> is inactive from load and active while its event runs.
/// </summary>
public sealed class GameEventQuestWorldTests
{
    private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    private sealed class FakeQuests : IQuestContentStore
    {
        public Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            QuestTemplate Task(uint id) => new() { Entry = id, Method = 2, QuestLevel = 1, Title = $"Quest {id}" };
            return System.Threading.Tasks.Task.FromResult(new QuestContent([Task(10), Task(11)], [], []));
        }
    }

    private sealed class FakeEvents : IGameEventDataStore
    {
        public Task<GameEventContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GameEventContent(
                [new GameEventRecord(2, 1, 1440, 120, 0, 0, "Winter Veil")],
                [new GameEventTimeRecord(2, "2026-10-03 12:00:00", "2030-12-31 22:59:59")],
                [], [], [],
                [new GameEventQuestRecord(10, 2), new GameEventQuestRecord(777, 2)],
                []));

        public Task SetDisabledAsync(uint entry, bool disabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task AnEventQuest_IsInactiveUntilItsEventRuns_AndInactiveAgainAfterwards_WhateverTheAttachOrder()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        collection.AddScoped<IQuestContentStore, FakeQuests>();
        collection.AddScoped<IGameEventDataStore, FakeEvents>();
        collection.AddSingleton(sp => new QuestNpcFeature(sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<QuestNpcFeature>.Instance));
        collection.AddSingleton(sp => new GameEventFeature(sp, NullLogger<GameEventFeature>.Instance));
        collection.AddSingleton(sp => new GameEventQuestFeature(sp, NullLogger<GameEventQuestFeature>.Instance));
        await using ServiceProvider services = collection.BuildServiceProvider();
        using var world = new WorldRuntime(
            new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
            new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
            NullLogger<WorldRuntime>.Instance);
        var clock = new FixedGameTime(Utc(2026, 10, 3, 10));
        WorldStateHooks.For(world).Time = clock;
        QuestNpcFeature quests = services.GetRequiredService<QuestNpcFeature>();
        // The event feature attaches BEFORE the quest feature has built its store: the join must still happen.
        services.GetRequiredService<GameEventFeature>().Attach(world);
        services.GetRequiredService<GameEventQuestFeature>().Attach(world);
        quests.Attach(world);

        world.RunTick(10);
        Quest listed = quests.Services.Quests.Get(10)!;
        Assert.False(listed.IsActive);                           // listed: inactive from load
        Assert.True(quests.Services.Quests.Get(11)!.IsActive);   // not listed: untouched

        clock.UtcNow = Utc(2026, 10, 3, 12, 30);
        world.RunTick(8_000_000); // past the 7201 s delay: the update runs and starts event 2
        Assert.True(listed.IsActive);

        clock.UtcNow = Utc(2026, 10, 3, 15);
        world.RunTick(uint.MaxValue / 2);
        Assert.False(listed.IsActive);

        await quests.DisposeAsync();
    }
}
