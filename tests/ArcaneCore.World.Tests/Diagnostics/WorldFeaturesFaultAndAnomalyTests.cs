using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Loot;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Economy;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.OutdoorPvP;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Diagnostics;

/// <summary>
/// Verifies that faults and outcome anomalies in <see cref="EconomySettlements"/>, <see cref="LootSettlements"/>,
/// <see cref="CharacterDeletion"/> (<see cref="ICharacterDeleteHook"/>), <see cref="BattlegroundFeature"/>,
/// <see cref="OutdoorPvPFeature"/>, and <c>WorldState</c> features are recorded in <see cref="FaultLedger.Shared"/>
/// and <see cref="AnomalyLedger.Shared"/>.
/// </summary>
public sealed class WorldFeaturesFaultAndAnomalyTests
{
    private sealed class EconomyCommitProbeException(string message) : Exception(message);
    private sealed class EconomyReconcileProbeException(string message) : Exception(message);
    private sealed class LootCommitProbeException(string message) : Exception(message);
    private sealed class LootReconcileProbeException(string message) : Exception(message);
    private sealed class CharacterDeleteHookProbeException(string message) : Exception(message);
    private sealed class BattlegroundProbeException(string message) : Exception(message);
    private sealed class OutdoorPvPProbeException(string message) : Exception(message);
    private sealed class WorldEventProbeException(string message) : Exception(message);

    /// <summary>
    /// Logger that stalls the settlement worker inside the reconcile-failure log call, which runs after the
    /// reconcile anomaly is published but before the Finalize command is posted to the world. This makes the
    /// window in which a single world tick drains an empty queue deterministic instead of scheduler-dependent.
    /// </summary>
    private sealed class SlowReconcileLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("could not reconcile", StringComparison.Ordinal))
            {
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>
    /// Ticks the world in real time until <paramref name="task"/> completes. Settlement completion is posted from a
    /// worker thread after its anomaly notice, so a fixed number of ticks can drain an empty queue and never see it.
    /// </summary>
    private static async Task<T> TickUntilCompletedAsync<T>(WorldRuntime world, Task<T> task, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!task.IsCompleted)
        {
            world.RunTick(1);
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Task did not complete within {timeout} of world ticking.");
            }

            await Task.Delay(10);
        }

        return await task;
    }

    private sealed class ThrowingEconomyStore : IEconomyStore
    {
        public Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest request, CancellationToken cancellationToken = default)
            => Task.FromException<EconomyCommitResult>(new EconomyCommitProbeException("simulated economy commit failure"));

        public Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default)
            => Task.FromException<bool>(new EconomyReconcileProbeException("simulated economy reconcile failure"));

        public Task<IReadOnlyList<MailRecord>> GetMailsAsync(int receiverId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MailRecord>>([]);

        public Task<IReadOnlyList<MailRecord>> GetExpiredMailsAsync(long now, int max, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MailRecord>>([]);

        public Task<IReadOnlyList<MailRecord>> GetMailsInvolvingAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MailRecord>>([]);

        public Task<string?> GetItemTextAsync(uint itemTextId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<AuctionRecord>> GetAuctionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AuctionRecord>>([]);

        public Task<IReadOnlyDictionary<uint, ArcaneCore.Kernel.Items.ItemInstanceData>> GetEscrowItemsAsync(
            IReadOnlyCollection<uint> itemGuids, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<uint, ArcaneCore.Kernel.Items.ItemInstanceData>>(
                new Dictionary<uint, ArcaneCore.Kernel.Items.ItemInstanceData>());

        public Task<AuctionSnapshot> GetAuctionSnapshotAsync(AuctionSnapshotFilter filter, CancellationToken cancellationToken = default)
            => Task.FromResult(new AuctionSnapshot([], new Dictionary<uint, ArcaneCore.Kernel.Items.ItemInstanceData>()));

        public Task<EconomyIdSeed> GetIdSeedAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new EconomyIdSeed(0, 0, 0));
    }

    private sealed class ThrowingLootStateStore : ILootStateStore
    {
        public Task<IReadOnlyList<LootStateRecord>> LoadInstanceStatesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LootStateRecord>>([]);

        public Task<LootCommitResult> CommitAsync(LootCommitRequest request, CancellationToken cancellationToken = default)
            => Task.FromException<LootCommitResult>(new LootCommitProbeException("simulated loot commit failure"));

        public Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default)
            => Task.FromException<bool>(new LootReconcileProbeException("simulated loot reconcile failure"));
    }

    private sealed class ThrowingPostDeleteHook(int targetCharacterId) : ICharacterDeleteHook
    {
        public Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
        {
            if (character.Id == targetCharacterId)
            {
                throw new CharacterDeleteHookProbeException($"simulated delete hook failure for {character.Id}");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingCanDeleteHook(int targetCharacterId) : ICharacterDeleteHook
    {
        public Task<bool> CanDeleteCharacterAsync(WorldSession session, CharacterRecord character)
        {
            if (character.Id == targetCharacterId)
            {
                throw new CharacterDeleteHookProbeException($"simulated can-delete failure for {character.Id}");
            }

            return Task.FromResult(true);
        }
    }

    private sealed class ThrowingBattlegroundContentStore : IBattlegroundContentStore
    {
        public Task<BattlegroundContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromException<BattlegroundContent>(new BattlegroundProbeException("simulated bg content load failure"));
    }

    private sealed class ThrowingBattlegroundEntryPointStore : IBattlegroundEntryPointStore
    {
        public Task<BattlegroundEntryPointRecord?> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<BattlegroundEntryPointRecord?>(null);

        public Task SaveAsync(BattlegroundEntryPointRecord record, CancellationToken cancellationToken = default)
            => Task.FromException(new BattlegroundProbeException("simulated bg entry-point save failure"));

        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromException(new BattlegroundProbeException("simulated bg entry-point delete failure"));
    }

    private sealed class ThrowingSilithystStore : ISilithystStore
    {
        public Task<SilithystState?> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromException<SilithystState?>(new OutdoorPvPProbeException("simulated silithyst load failure"));

        public Task SaveAsync(SilithystState state, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ThrowingNightmareDragonsStore : INightmareDragonsStore
    {
        public Task<NightmareDragonsState?> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromException<NightmareDragonsState?>(new WorldEventProbeException("simulated nightmare dragons load failure"));

        public Task SaveAsync(NightmareDragonsState state, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ThrowingGameEventStatusStore : IGameEventStatusStore
    {
        public Task<IReadOnlyList<int>> LoadActiveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<int>>([]);

        public Task ReplaceActiveAsync(IReadOnlyCollection<int> eventIds, CancellationToken cancellationToken = default)
            => Task.FromException(new WorldEventProbeException("simulated game event status write failure"));
    }

    [Fact]
    public async Task EconomySettlements_RecordsFaultsAndAnomalies_OnCommitAndReconcileFailures()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IEconomyStore, ThrowingEconomyStore>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();

        var saves = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
        var settlements = new EconomySettlements(scopes, new SlowReconcileLogger());
        settlements.Attach(world, saves, null);

        var opId = Guid.NewGuid();
        var done = new TaskCompletionSource<EconomyOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(
            n => n.Subsystem == "economy" && n.Subject == $"operation {opId}");

        bool started = false;
        world.Post(() =>
        {
            started = settlements.TryStart([], [], (outcome, _) => done.TrySetResult(outcome), opId);
        });
        world.RunTick(1);
        Assert.True(started);

        await WorldTestHost.WaitForAsync(() => capture.Notices.Count >= 2, "economy settlement and reconcile anomalies");
        EconomyOutcome finalOutcome = await TickUntilCompletedAsync(world, done.Task, TimeSpan.FromSeconds(5));
        Assert.Equal(EconomyOutcome.Unknown, finalOutcome);

        Assert.Contains(FaultLedger.Shared.Snapshot(),
            s => s.Site == "economy.settlement" && s.ExceptionType == typeof(EconomyCommitProbeException).FullName);
        Assert.Contains(FaultLedger.Shared.Snapshot(),
            s => s.Site == "economy.settlement-reconcile" && s.ExceptionType == typeof(EconomyReconcileProbeException).FullName);

        Assert.Contains(capture.Notices,
            n => n.Key == "economy/settlement-failed" && n.Detail.Contains("EconomyCommitProbeException", StringComparison.Ordinal));
        Assert.Contains(capture.Notices,
            n => n.Key == "economy/settlement-reconcile-failed"
                && n.Severity == AnomalySeverity.Error
                && n.Detail.Contains("EconomyReconcileProbeException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LootSettlements_RecordsFaultsAndAnomalies_OnCommitAndReconcileFailures()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ILootStateStore, ThrowingLootStateStore>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();

        var saves = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
        var settlements = new LootSettlements(scopes, new SlowReconcileLogger());
        settlements.Attach(world, saves, null, static _ => true, static () => 0, static (_, _) => Task.CompletedTask);

        var key = new LootStateKey(777, 888);
        var updated = new LootStateRecord(key, 1001, 0, 1, false, 0, [], []);
        var done = new TaskCompletionSource<LootOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(
            n => n.Subsystem == "loot" && (n.Detail.Contains("LootCommitProbeException", StringComparison.Ordinal)
                || n.Detail.Contains("LootReconcileProbeException", StringComparison.Ordinal)));

        bool started = false;
        world.Post(() =>
        {
            var op = new LootOperation { Key = key, Expected = null, Updated = updated, Finished = (outcome, _, _) => done.TrySetResult(outcome) };
            started = settlements.TryStart(op);
        });
        world.RunTick(1);
        Assert.True(started);

        await WorldTestHost.WaitForAsync(() => capture.Notices.Count >= 2, "loot settlement and reconcile anomalies");
        LootOutcome finalOutcome = await TickUntilCompletedAsync(world, done.Task, TimeSpan.FromSeconds(5));
        Assert.Equal(LootOutcome.Unknown, finalOutcome);

        Assert.Contains(FaultLedger.Shared.Snapshot(),
            s => s.Site == "gameobject.loot" && s.ExceptionType == typeof(LootCommitProbeException).FullName);
        Assert.Contains(FaultLedger.Shared.Snapshot(),
            s => s.Site == "gameobject.loot" && s.ExceptionType == typeof(LootReconcileProbeException).FullName);

        Assert.Contains(capture.Notices,
            n => n.Key == "loot/settlement-failed" && n.Detail.Contains("LootCommitProbeException", StringComparison.Ordinal));
        Assert.Contains(capture.Notices,
            n => n.Key == "loot/settlement-reconcile-failed"
                && n.Severity == AnomalySeverity.Error
                && n.Detail.Contains("LootReconcileProbeException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CharacterDeleteHooks_RecordsFaultsAndAnomalies_WhenPreAndPostHooksThrow()
    {
        var characterStore = new InMemoryCharacterStore();
        int preFailId = (await characterStore.CreateAsync(new CharacterRecord { AccountId = 0, Name = "PreFailChar" })).Id;
        int postFailId = (await characterStore.CreateAsync(new CharacterRecord { AccountId = 0, Name = "PostFailChar" })).Id;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICharacterStore>(characterStore);
        services.AddSingleton<CharacterDirectory>();
        services.AddSingleton<ICharacterDeleteHook>(new ThrowingCanDeleteHook(preFailId));
        services.AddSingleton<ICharacterDeleteHook>(new ThrowingPostDeleteHook(postFailId));
        await using ServiceProvider provider = services.BuildServiceProvider();

        using var registry = new SessionRegistry();
        var saves = new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
        WorldSession session = WorldSession.CreateConsole(
            provider, new OpcodeTable(), world, registry, new WorldSessionOptions(), NullLogger.Instance);

        using AnomalyCapture preCapture = AnomalyLedger.Shared.Capture(
            n => n.Key == "character/delete-hook-failed" && n.Subject == $"character {preFailId}");
        Assert.False(await CharacterDeletion.TryDeleteAsync(session, (ulong)preFailId));
        Assert.Contains(FaultLedger.Shared.Snapshot(),
            s => s.Site == "character.delete" && s.ExceptionType == typeof(CharacterDeleteHookProbeException).FullName);
        Assert.Single(preCapture.Notices);

        using AnomalyCapture postCapture = AnomalyLedger.Shared.Capture(
            n => n.Key == "character/delete-hook-failed" && n.Subject == $"character {postFailId}");
        Assert.True(await CharacterDeletion.TryDeleteAsync(session, (ulong)postFailId));
        Assert.Contains(FaultLedger.Shared.Snapshot(),
            s => s.Site == "character.delete-hook" && s.ExceptionType == typeof(CharacterDeleteHookProbeException).FullName);
        Assert.Contains("ThrowingPostDeleteHook", Assert.Single(postCapture.Notices).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BattlegroundAndOutdoorPvPFeatures_RecordFaultsAndAnomalies_OnStoreAndHookFailures()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IBattlegroundContentStore, ThrowingBattlegroundContentStore>();
        services.AddScoped<IBattlegroundEntryPointStore, ThrowingBattlegroundEntryPointStore>();
        services.AddScoped<ISilithystStore, ThrowingSilithystStore>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();

        var saves = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);

        using AnomalyCapture bgCapture = AnomalyLedger.Shared.Capture(
            n => n.Key == "battleground/feature-fault" && n.Detail.Contains("BattlegroundProbeException", StringComparison.Ordinal));
        using AnomalyCapture opvpCapture = AnomalyLedger.Shared.Capture(
            n => n.Key == "outdoor-pvp/hook-fault" && n.Detail.Contains("OutdoorPvPProbeException", StringComparison.Ordinal));

        var bg = new BattlegroundFeature(provider, scopes, NullLoggerFactory.Instance);
        bg.Attach(world);
        bg.DeleteBinding(ObjectGuid.Player(501));
        await bg.FlushAsync();
        await bg.StopAsync();

        var opvp = new OutdoorPvPFeature(provider, NullLogger<OutdoorPvPFeature>.Instance);
        opvp.Attach(world);
        world.RunTick(1);
        world.GetMap(1); // triggers LoadSilithystMax on Kalimdor map creation

        Assert.Contains(FaultLedger.Shared.Snapshot(),
            s => s.Site == "battleground.feature" && s.ExceptionType == typeof(BattlegroundProbeException).FullName);
        Assert.Contains(bgCapture.Notices, n => n.Subject == "content-load");
        Assert.Contains(bgCapture.Notices, n => n.Subject == "write-loop");

        Assert.Contains(FaultLedger.Shared.Snapshot(),
            s => s.Site == "outdoor-pvp.feature" && s.ExceptionType == typeof(OutdoorPvPProbeException).FullName);
        Assert.Contains(opvpCapture.Notices, n => n.Subject == "silithyst-load");
    }

    [Fact]
    public async Task WorldStateFeatures_RecordFaultsAndAnomalies_OnLifecycleAndStoreFailures()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<INightmareDragonsStore, ThrowingNightmareDragonsStore>();
        services.AddScoped<IGameEventStatusStore, ThrowingGameEventStatusStore>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();

        var saves = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);

        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(
            n => n.Key == "world-event/lifecycle-fault" && n.Detail.Contains("WorldEventProbeException", StringComparison.Ordinal));

        // 1. OffThreadStoreWork synchronous offload failure
        var work = new OffThreadStoreWork(world)
        {
            Offload = _ => throw new WorldEventProbeException("simulated offload failure"),
        };
        Exception? observedFailure = null;
        Assert.True(work.TryRun(() => Task.CompletedTask, () => { }, ex => observedFailure = ex));
        Assert.IsType<WorldEventProbeException>(observedFailure);

        // 2. NightmareDragonsFeature startup load failure
        var dragons = new NightmareDragonsFeature(scopes, NullLogger<NightmareDragonsFeature>.Instance);
        dragons.Attach(world);

        // 3. GameEventStatusWriter status write failure
        var writer = new GameEventStatusWriter(provider, NullLogger.Instance);
        writer.Changed([1]);
        await writer.FlushAsync();

        Assert.Contains(FaultLedger.Shared.Snapshot(),
            s => s.Site == "world-event.offload" && s.ExceptionType == typeof(WorldEventProbeException).FullName);
        Assert.Contains(FaultLedger.Shared.Snapshot(),
            s => s.Site == "world-event.nightmare-dragons" && s.ExceptionType == typeof(WorldEventProbeException).FullName);
        Assert.Contains(FaultLedger.Shared.Snapshot(),
            s => s.Site == "world-event.game-events" && s.ExceptionType == typeof(WorldEventProbeException).FullName);

        Assert.Contains(capture.Notices, n => n.Subject == "offthread-store-work");
        Assert.Contains(capture.Notices, n => n.Subject == "nightmare-dragons");
        Assert.Contains(capture.Notices, n => n.Subject == "game-events");
    }
}
