using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The six playerbot features detach and attach again in a running world with bots online (docs/design/hot-reload-tiers.md,
/// section 4.4 2a): the bots stay logged in (their rows, scopes and sessions are the core's <see cref="ManagedBotRegistry"/>), the
/// brains are rebuilt, no subscription or timer is doubled, and the checkpoint keeps writing.
/// </summary>
public sealed class PlayerbotFeatureDetachTests
{
    /// <summary>The bot features, by the names the feature host gives them.</summary>
    private static readonly string[] BotFeatures =
    [
        nameof(ManagedPlayerbotFeature),
        nameof(PlayerbotPopulationFeature),
        nameof(PlayerbotQaFeature),
        "PlayerbotSocialFeature",
        "PlayerbotGroupCoordinator",
        "PlayerbotBattlegroundCoordinator",
    ];

    private sealed class IdleController : IPlayerbotController
    {
        public int Detaches;

        public void Tick(PlayerbotControllerContext context, uint elapsedMs) { }

        public void Detached(Guid botId) => Detaches++;
    }

    private static async Task<(Guid[] Ids, IdleController[] Controllers)> StartBotsAsync(ManagedPlayerbotFeature feature, int count)
    {
        var ids = new Guid[count];
        var controllers = new IdleController[count];
        for (int i = 0; i < count; i++)
        {
            PlayerbotOperationResult created = await feature.CreateAsync("Cycle" + (char)('a' + i), 1, 1);
            Assert.True(created.Success, created.Code);
            ids[i] = created.BotId!.Value;
            controllers[i] = new IdleController();
            PlayerbotOperationResult started = await feature.StartScriptedAsync(ids[i].ToString(), controllers[i]);
            Assert.True(started.Success, started.Code);
        }

        return (ids, controllers);
    }

    private static async Task CheckpointAsync(WorldTestHost host, ManagedPlayerbotFeature feature)
    {
        Task before = await host.World.InvokeAsync(() => feature.LastCheckpoint);
        await before.WaitAsync(TimeSpan.FromSeconds(30));
        for (int tick = 0; tick < 110; tick++) await host.World.AdvanceClockAsync(50); // a checkpoint starts at 5000 ms
        Task started = await host.World.InvokeAsync(() => feature.LastCheckpoint);
        Assert.NotSame(before, started);
        await started.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static WorldTestHost Start(ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore owners, Action<IServiceCollection>? extra = null)
        => ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners, manualClock: true, extraServices: extra);

    [Fact]
    public async Task CyclingEveryBotFeature_KeepsTheBotsOnline_RebuildsTheirBrains_DoublesNoSubscription_AndTheCheckpointStillWrites()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = Start(owners);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        ManagedBotRegistry registry = host.WorldServices.GetRequiredService<ManagedBotRegistry>();
        FeatureHost features = host.WorldServices.GetRequiredService<FeatureHost>();
        (Guid[] ids, IdleController[] controllers) = await StartBotsAsync(feature, 3);
        await CheckpointAsync(host, feature);

        ManagedBot[] entries = [.. ids.Select(id => registry.TryGet(id, out ManagedBot bot) ? bot : throw new InvalidOperationException("not registered"))];
        PlayerbotBrain[] brains = await host.World.InvokeAsync(() => ids.Select(id => feature.FindBrain(id)!).ToArray());
        Dictionary<string, int> subscriptions = BotFeatures.ToDictionary(name => name, name => features.Find(name)!.Subscriptions);
        // Bot QA (off here) and the battleground coordinator (driven by the managed feature's update) subscribe nothing of their own.
        Assert.All(BotFeatures.Except([nameof(PlayerbotQaFeature), "PlayerbotBattlegroundCoordinator"]),
            name => Assert.True(subscriptions[name] > 0, name + " subscribed nothing through its bag"));
        Assert.All(BotFeatures, name => Assert.NotEqual(DetachKind.Pinned, features.Find(name)!.Kind));

        foreach (string name in BotFeatures)
        {
            FeatureResult cycled = await features.CycleAsync(name);
            Assert.True(cycled.Ok, name + ": " + cycled.Detail);
            await host.World.AdvanceClockAsync(50);
        }

        // Same sessions, still in the world; the registry holds the same entries; nothing logged out.
        Assert.Equal(3, registry.Count);
        for (int i = 0; i < ids.Length; i++)
        {
            Assert.True(registry.TryGet(ids[i], out ManagedBot after));
            Assert.Same(entries[i], after);
            Assert.Equal(SessionState.InWorld, after.Session.State);
            Assert.Equal(ManagedPlayerbotState.Running, owners.Peek(ids[i])!.State);
            Assert.Equal(0, controllers[i].Detaches); // a scripted bot keeps its controller across the cycle
        }

        // New brains around the same sessions.
        PlayerbotBrain[] rebuilt = await host.World.InvokeAsync(() => ids.Select(id => feature.FindBrain(id)!).ToArray());
        for (int i = 0; i < ids.Length; i++)
        {
            Assert.NotNull(rebuilt[i]);
            Assert.NotSame(brains[i], rebuilt[i]);
        }

        // One update handler per feature: the population's clock moves exactly with the world's.
        PlayerbotPopulationFeature population = host.WorldServices.GetRequiredService<PlayerbotPopulationFeature>();
        long clock = population.ClockMs;
        for (int tick = 0; tick < 10; tick++) await host.World.AdvanceClockAsync(50);
        Assert.Equal(clock + 500, population.ClockMs);

        // The checkpoint writes a changed goal, as before the cycle.
        int rows = owners.RowsWritten;
        long revision = owners.Peek(ids[1])!.Revision;
        await host.World.InvokeAsync(() => { feature.FindBrain(ids[1])!.HoldGoalForTests(PlayerbotGoalKind.Quest, 4321, 88); return true; });
        await CheckpointAsync(host, feature);
        Assert.Equal(rows + 1, owners.RowsWritten);
        Assert.Equal(revision + 1, owners.Peek(ids[1])!.Revision);
        Assert.Equal((PlayerbotGoalKind.Quest, 4321u, 88u), (owners.Peek(ids[1])!.Goal, owners.Peek(ids[1])!.TargetEntry, owners.Peek(ids[1])!.QuestId));

        // As many subscriptions as before, once the lazy ones (the social pass's guild and level events, the coordinator's leader
        // changes) were made again by the passes of the 5.5 s just run: none doubled, none lost.
        foreach (string name in BotFeatures)
        {
            FeatureStatus status = features.Find(name)!;
            Assert.Equal(FeatureState.Attached, status.State);
            Assert.True(subscriptions[name] == status.Subscriptions, $"{name}: {status.Subscriptions} subscriptions after the cycle, {subscriptions[name]} before");
        }

        // And the operations still work: a stop goes through the rebuilt feature and leaves the registry.
        Assert.True((await feature.StopAsync(ids[0].ToString())).Success);
        Assert.False(registry.TryGet(ids[0], out _));
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public async Task WhileTheBotFeatureIsDetached_TheBotsStayOnline_OperationsRefuse_AndTheSessionsDoFullClientWork()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = Start(owners);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        ManagedBotRegistry registry = host.WorldServices.GetRequiredService<ManagedBotRegistry>();
        FeatureHost features = host.WorldServices.GetRequiredService<FeatureHost>();
        (Guid[] ids, _) = await StartBotsAsync(feature, 1);
        Assert.NotNull(registry.SessionPolicy);
        Assert.True(registry.SessionPolicy!.Lean); // the default fidelity

        FeatureResult detached = await features.DetachAsync(nameof(ManagedPlayerbotFeature));
        Assert.True(detached.Ok, detached.Detail);
        Assert.Null(registry.SessionPolicy);
        Assert.True(feature.IsDetaching);
        for (int tick = 0; tick < 20; tick++) await host.World.AdvanceClockAsync(50);

        Assert.True(registry.TryGet(ids[0], out ManagedBot bot));
        Assert.Equal(SessionState.InWorld, bot.Session.State);
        Assert.True(((IPlayerSession)bot.Session).WantsUpdateObjects); // no pack policy: the full client path
        Assert.Equal(ManagedPlayerbotFeature.PlayerbotsDetaching, (await feature.CreateAsync("Refused", 1, 1)).Code);
        Assert.Null(await host.World.InvokeAsync(() => feature.FindBrain(ids[0])));

        FeatureResult attached = await features.AttachAsync(nameof(ManagedPlayerbotFeature));
        Assert.True(attached.Ok, attached.Detail);
        Assert.False(feature.IsDetaching);
        Assert.NotNull(registry.SessionPolicy);
        Assert.False(((IPlayerSession)bot.Session).WantsUpdateObjects); // lean again
        Assert.NotNull(await host.World.InvokeAsync(() => feature.FindBrain(ids[0])));
        Assert.True((await feature.CreateAsync("Allowed", 1, 1)).Success);
    }

    [Fact]
    public async Task AShutdownWithTheBotFeatureDetached_ClosesTheSessions_AndLeavesTheRowsForTheNextStart()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        WorldTestHost host = Start(owners);
        ManagedBot bot;
        Guid id;
        try
        {
            ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
            ManagedBotRegistry registry = host.WorldServices.GetRequiredService<ManagedBotRegistry>();
            (Guid[] ids, _) = await StartBotsAsync(feature, 1);
            id = ids[0];
            Assert.True(registry.TryGet(id, out bot));
            FeatureResult detached = await host.WorldServices.GetRequiredService<FeatureHost>().DetachAsync(nameof(ManagedPlayerbotFeature));
            Assert.True(detached.Ok, detached.Detail);
        }
        finally
        {
            await host.DisposeAsync();
        }

        Assert.Equal(SessionState.Closed, bot.Session.State);
        ManagedPlayerbot row = owners.Peek(id)!;
        Assert.Equal(ManagedPlayerbotState.Running, row.State); // restored by the next start
        Assert.True(row.DesiredEnabled);
    }

    [Fact]
    public async Task CyclingBotQa_KeepsExactlyOneHangSink_AndDetachingItRemovesTheSink()
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var bus = new PlayerbotQaSignalBus(64);
        await using WorldTestHost host = Start(owners, services =>
        {
            services.AddSingleton<IPlayerbotQaSink>(new PlayerbotQaSink(bus));
            services.AddSingleton(Options.Create(new PlayerbotQaOptions { TickHangWarmupMinutes = 7 }));
        });
        ManagedBotRegistry registry = host.WorldServices.GetRequiredService<ManagedBotRegistry>();
        FeatureHost features = host.WorldServices.GetRequiredService<FeatureHost>();
        Assert.Equal(TimeSpan.FromMinutes(7), registry.HangSink!.Warmup);

        FeatureResult cycled = await features.CycleAsync(nameof(PlayerbotQaFeature)); // a second registration would throw: the cycle would fail
        Assert.True(cycled.Ok, cycled.Detail);
        Assert.NotNull(registry.HangSink);

        FeatureResult detached = await features.DetachAsync(nameof(PlayerbotQaFeature));
        Assert.True(detached.Ok, detached.Detail);
        Assert.Null(registry.HangSink);

        FeatureResult attached = await features.AttachAsync(nameof(PlayerbotQaFeature));
        Assert.True(attached.Ok, attached.Detail);
        registry.HangSink!.ReportHang("Maps", "map", "The world tick is hung: test", 1);
        var signals = new List<QaSignal>();
        bus.Drain(signals, 8);
        QaSignal signal = Assert.Single(signals);
        Assert.Equal((QaSignalKind.TickHang, "Maps", "map"), (signal.Kind, signal.Stage, signal.Reason));
    }
}

/// <summary>The core-owned seams of <see cref="ManagedBotRegistry"/> and the encounter catalogue.</summary>
public sealed class ManagedBotRegistryTests
{
    private sealed class Sink : IBotTickHangSink
    {
        public TimeSpan Warmup => TimeSpan.Zero;

        public void ReportHang(string phase, string work, string message, long occurrences) { }
    }

    [Fact]
    public void ASeam_HoldsOneRegistration_AndOnlyItsOwnDisposalClearsIt()
    {
        var registry = new ManagedBotRegistry();
        var first = new Sink();
        var second = new Sink();
        IDisposable held = registry.UseHangSink(first);
        Assert.Same(first, registry.HangSink);
        Assert.Throws<InvalidOperationException>(() => registry.UseHangSink(second));

        held.Dispose();
        Assert.Null(registry.HangSink);
        IDisposable next = registry.UseHangSink(second);
        held.Dispose(); // a stale registration's second disposal must not clear the newer one
        Assert.Same(second, registry.HangSink);
        next.Dispose();
        Assert.Null(registry.HangSink);
    }

    [Fact]
    public void TheEncounterCatalog_KeepsWhatWasLoaded_AndAReplaceReturnsThePreviousList()
    {
        var catalog = new InstanceEncounterCatalog();
        Assert.False(catalog.IsLoaded);
        Assert.Empty(catalog.Current);
        IReadOnlyList<Kernel.WorldData.InstanceEncounter> previous = catalog.Replace([]);
        Assert.True(catalog.IsLoaded);
        Assert.Empty(previous);
    }
}
