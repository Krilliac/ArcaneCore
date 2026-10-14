using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Characters.Talents;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Talents;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.Social;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Skills;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Talents;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Persistence;

/// <summary>
/// Unified <see cref="CharacterPersistenceBarrier"/> and terminal <see cref="CharacterOwnershipRejectedException"/>
/// handling across per-character persistence queues (M15.0 B1).
/// </summary>
public sealed class CharacterPersistenceBarrierTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task SocialWriteQueue_OwnershipRejected_FailsTerminallyOnAttemptOne()
    {
        var store = new FaultableSocialStore { RejectOwnership = true };
        await using ServiceProvider provider = new ServiceCollection().AddSingleton<ISocialStore>(store).BuildServiceProvider();
        var queue = new SocialWriteQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
        {
            RetainedRetryInterval = Timeout.InfiniteTimeSpan,
        };
        var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.FatalFailure += ex => fatal.TrySetResult(ex);
        queue.Start();

        queue.SetSocial(10, 20, SocialFlags.Friend);
        Exception err = await fatal.Task.WaitAsync(Budget);

        Assert.IsType<CharacterOwnershipRejectedException>(err);
        Assert.Same(err, queue.TerminalFailure);
        Assert.Equal(1, store.Attempts);
        InvalidOperationException flushEx = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.FlushCharacterAsync(10).WaitAsync(Budget));
        Assert.Same(err, flushEx.InnerException);
        InvalidOperationException stopEx = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync().WaitAsync(Budget));
        Assert.Same(err, stopEx.InnerException);
    }

    [Fact]
    public async Task HonorWriteQueue_OwnershipRejected_FailsTerminallyOnAttemptOne()
    {
        var store = new FaultableHonorStore { RejectOwnership = true };
        await using ServiceProvider provider = new ServiceCollection().AddSingleton<IHonorStore>(store).BuildServiceProvider();
        var queue = new HonorWriteQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
        {
            RetainedRetryInterval = Timeout.InfiniteTimeSpan,
        };
        var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.FatalFailure += ex => fatal.TrySetResult(ex);
        queue.Start();

        queue.SaveState(10, CharacterHonorState.Empty with { RankPoints = 100f });
        Exception err = await fatal.Task.WaitAsync(Budget);

        Assert.IsType<CharacterOwnershipRejectedException>(err);
        Assert.Same(err, queue.TerminalFailure);
        Assert.Equal(1, store.Attempts);
        InvalidOperationException flushEx = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.FlushCharacterAsync(10).WaitAsync(Budget));
        Assert.Same(err, flushEx.InnerException);
        InvalidOperationException stopEx = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync().WaitAsync(Budget));
        Assert.Same(err, stopEx.InnerException);
    }

    [Fact]
    public async Task RestWriteQueue_OwnershipRejected_FailsTerminallyOnAttemptOne()
    {
        var store = new FaultableRestStore { RejectOwnership = true };
        await using ServiceProvider provider = new ServiceCollection().AddSingleton<ICharacterRestStore>(store).BuildServiceProvider();
        var queue = new RestWriteQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
        {
            RetainedRetryInterval = Timeout.InfiniteTimeSpan,
        };
        var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.FatalFailure += ex => fatal.TrySetResult(ex);
        queue.Start();

        queue.Save(10, new CharacterRestState(50f, 1000, true));
        Exception err = await fatal.Task.WaitAsync(Budget);

        Assert.IsType<CharacterOwnershipRejectedException>(err);
        Assert.Same(err, queue.TerminalFailure);
        Assert.Equal(1, store.Attempts);
        InvalidOperationException flushEx = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.FlushCharacterAsync(10).WaitAsync(Budget));
        Assert.Same(err, flushEx.InnerException);
        InvalidOperationException stopEx = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync().WaitAsync(Budget));
        Assert.Same(err, stopEx.InnerException);
    }

    [Fact]
    public async Task TaxiFlightWriteQueue_OwnershipRejected_FailsTerminallyOnAttemptOne_WithoutInvokingAbandon()
    {
        var store = new FaultableTaxiFlightStore { RejectOwnership = true };
        await using ServiceProvider provider = new ServiceCollection().AddSingleton<ICharacterTaxiFlightStore>(store).BuildServiceProvider();
        var queue = new TaxiFlightWriteQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
        {
            RetainedRetryInterval = Timeout.InfiniteTimeSpan,
        };
        var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.FatalFailure += ex => fatal.TrySetResult(ex);
        queue.Start();

        int abandonCalls = 0;
        queue.Save(10, new TaxiFlightRoute([1, 2], [10], [50]), () => Interlocked.Increment(ref abandonCalls));
        Exception err = await fatal.Task.WaitAsync(Budget);

        Assert.IsType<CharacterOwnershipRejectedException>(err);
        Assert.Same(err, queue.TerminalFailure);
        Assert.Equal(1, store.Attempts);
        Assert.Equal(0, Volatile.Read(ref abandonCalls));
        InvalidOperationException flushEx = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.FlushCharacterAsync(10).WaitAsync(Budget));
        Assert.Same(err, flushEx.InnerException);
        InvalidOperationException stopEx = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync().WaitAsync(Budget));
        Assert.Same(err, stopEx.InnerException);
    }

    [Fact]
    public async Task ExploredZonesWriteQueue_OwnershipRejected_FailsTerminallyOnAttemptOne()
    {
        var store = new FaultableExploredZonesStore { RejectOwnership = true };
        await using ServiceProvider provider = new ServiceCollection().AddSingleton<IExploredZonesStore>(store).BuildServiceProvider();
        var queue = new ExploredZonesWriteQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
        {
            RetainedRetryInterval = Timeout.InfiniteTimeSpan,
        };
        var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.FatalFailure += ex => fatal.TrySetResult(ex);
        queue.Start();

        queue.Save(10, new uint[ExploredZonesText.WordCount]);
        Exception err = await fatal.Task.WaitAsync(Budget);

        Assert.IsType<CharacterOwnershipRejectedException>(err);
        Assert.Same(err, queue.TerminalFailure);
        Assert.Equal(1, store.Attempts);
        InvalidOperationException flushEx = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.FlushCharacterAsync(10).WaitAsync(Budget));
        Assert.Same(err, flushEx.InnerException);
        InvalidOperationException stopEx = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync().WaitAsync(Budget));
        Assert.Same(err, stopEx.InnerException);
    }

    [Fact]
    public async Task SpellStatePersistence_OwnershipRejected_FailsTerminallyAndFaultsDurableFlush()
    {
        var store = new FaultableSpellStateStore { RejectOwnership = true };
        await using ServiceProvider provider = new ServiceCollection().AddSingleton<ICharacterSpellStateStore>(store).BuildServiceProvider();
        var persistence = new SpellStatePersistence(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        persistence.FatalFailure += ex => fatal.TrySetResult(ex);

        persistence.Save(10, SpellStateSnapshot.Empty);
        Exception err = await fatal.Task.WaitAsync(Budget);

        Assert.IsType<CharacterOwnershipRejectedException>(err);
        Assert.Same(err, persistence.TerminalFailure);
        Assert.Equal(1, store.Attempts);
        InvalidOperationException flushEx = await Assert.ThrowsAsync<InvalidOperationException>(() => persistence.FlushCharacterDurableAsync(10).WaitAsync(Budget));
        Assert.Same(err, flushEx.InnerException);
        InvalidOperationException loadEx = await Assert.ThrowsAsync<InvalidOperationException>(() => persistence.LoadAsync(10, store).WaitAsync(Budget));
        Assert.Same(err, loadEx.InnerException);
    }

    [Fact]
    public async Task SpellbookCache_OwnershipRejected_FailsTerminallyAndFaultsFlush()
    {
        var store = new FaultableSpellStore { RejectOwnership = true };
        await using ServiceProvider provider = new ServiceCollection().AddSingleton<ICharacterSpellStore>(store).BuildServiceProvider();
        var cache = new SpellbookCache(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.FatalFailure += ex => fatal.TrySetResult(ex);
        cache.Start();

        cache.DeleteCharacter(10);
        Exception err = await fatal.Task.WaitAsync(Budget);

        Assert.IsType<CharacterOwnershipRejectedException>(err);
        Assert.Same(err, cache.TerminalFailure);
        Assert.Equal(1, store.Attempts);
        InvalidOperationException flushEx = await Assert.ThrowsAsync<InvalidOperationException>(() => cache.FlushCharacterAsync(10).WaitAsync(Budget));
        Assert.Same(err, flushEx.InnerException);
        InvalidOperationException stopEx = await Assert.ThrowsAsync<InvalidOperationException>(() => cache.DisposeAsync().AsTask().WaitAsync(Budget));
        Assert.Same(err, stopEx.InnerException);
    }

    [Fact]
    public async Task TalentPersistence_OwnershipRejected_FailsTerminallyAndFaultsFlush()
    {
        var store = new FaultableTalentStore { RejectOwnership = true };
        await using ServiceProvider provider = new ServiceCollection().AddSingleton<ICharacterTalentStore>(store).BuildServiceProvider();
        var persistence = new TalentPersistence(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        persistence.FatalFailure += ex => fatal.TrySetResult(ex);
        persistence.Start();

        persistence.SaveRespec(10, new RespecState(2, 100));
        Exception err = await fatal.Task.WaitAsync(Budget);

        Assert.IsType<CharacterOwnershipRejectedException>(err);
        Assert.Same(err, persistence.TerminalFailure);
        Assert.Equal(1, store.Attempts);
        InvalidOperationException flushEx = await Assert.ThrowsAsync<InvalidOperationException>(() => persistence.FlushCharacterAsync(10).WaitAsync(Budget));
        Assert.Same(err, flushEx.InnerException);
        InvalidOperationException stopEx = await Assert.ThrowsAsync<InvalidOperationException>(() => persistence.DisposeAsync().AsTask().WaitAsync(Budget));
        Assert.Same(err, stopEx.InnerException);
    }

    [Fact]
    public async Task SkillSaveCoordinator_OwnershipRejected_FailsTerminallyOnAttemptOne()
    {
        var store = new FaultableSkillStore { RejectOwnership = true };
        await using ServiceProvider provider = new ServiceCollection().AddSingleton<ICharacterSkillStore>(store).BuildServiceProvider();
        var coordinator = new SkillSaveCoordinator(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, TimeSpan.FromMilliseconds(20));
        var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.FatalFailure += ex => fatal.TrySetResult(ex);
        coordinator.Start();

        coordinator.Enqueue(10, new CharacterSkillSnapshot([new((ushort)SkillIds.Swords, 5, 50)], []));
        Exception err = await fatal.Task.WaitAsync(Budget);

        Assert.IsType<CharacterOwnershipRejectedException>(err);
        Assert.Same(err, coordinator.TerminalFailure);
        Assert.Equal(1, store.Attempts);
        InvalidOperationException flushEx = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.FlushCharacterAsync(10, TimeSpan.FromSeconds(2)));
        Assert.Same(err, flushEx.InnerException);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task UnifiedBarrier_FlushesAllPerCharacterQueues_AndReportsDurableSuccess()
    {
        var socialStore = new FaultableSocialStore();
        var honorStore = new FaultableHonorStore();
        var restStore = new FaultableRestStore();
        var taxiStore = new FaultableTaxiFlightStore();
        var exploredStore = new FaultableExploredZonesStore();
        var instanceStore = new FaultableInstanceStore();
        var spellStore = new FaultableSpellStore();
        var spellStateStore = new FaultableSpellStateStore();
        var talentStore = new FaultableTalentStore();
        var skillStore = new FaultableSkillStore();
        var questStore = new FaultableQuestStore();

        var services = new ServiceCollection();
        services.AddSingleton<ISocialStore>(socialStore);
        services.AddSingleton<IHonorStore>(honorStore);
        services.AddSingleton<ICharacterRestStore>(restStore);
        services.AddSingleton<ICharacterTaxiFlightStore>(taxiStore);
        services.AddSingleton<IExploredZonesStore>(exploredStore);
        services.AddSingleton<IInstanceStore>(instanceStore);
        services.AddSingleton<ICharacterSpellStore>(spellStore);
        services.AddSingleton<ICharacterSpellStateStore>(spellStateStore);
        services.AddSingleton<ICharacterTalentStore>(talentStore);
        services.AddSingleton<ICharacterSkillStore>(skillStore);
        services.AddSingleton<ICharacterQuestStore>(questStore);

        services.AddSingleton(sp =>
        {
            var q = new QuestNpcPersistence(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
            q.Start();
            return q;
        });
        services.AddSingleton(sp =>
        {
            var q = new TaxiFlightWriteQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
            q.Start();
            return q;
        });
        services.AddSingleton(sp =>
        {
            var q = new SpellbookCache(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
            q.Start();
            return q;
        });
        services.AddSingleton(sp => new SpellStatePersistence(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance));
        services.AddSingleton(sp =>
        {
            var q = new TalentPersistence(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
            q.Start();
            return q;
        });
        services.AddSingleton(sp =>
        {
            var q = new SkillSaveCoordinator(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, TimeSpan.FromMilliseconds(20));
            q.Start();
            return q;
        });
        services.AddSingleton(sp =>
        {
            var q = new SocialWriteQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
            q.Start();
            return q;
        });
        services.AddSingleton(sp =>
        {
            var q = new HonorWriteQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
            q.Start();
            return q;
        });
        services.AddSingleton(sp =>
        {
            var q = new RestWriteQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
            q.Start();
            return q;
        });
        services.AddSingleton(sp =>
        {
            var q = new ExploredZonesWriteQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
            q.Start();
            return q;
        });
        services.AddSingleton(sp =>
        {
            var q = new InstanceWriteQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
            q.Start();
            return q;
        });
        services.AddSingleton<CharacterPersistenceBarrier>();

        await using ServiceProvider provider = services.BuildServiceProvider();
        const int characterId = 77;

        var quests = provider.GetRequiredService<QuestNpcPersistence>();
        quests.LoadCharacter(characterId, CharacterQuestData.Empty, quests.CaptureLoadRevision(characterId));
        quests.SaveTaxiMask(characterId, [1, 2]);

        provider.GetRequiredService<TaxiFlightWriteQueue>().Save(characterId, new TaxiFlightRoute([1, 2], [10], [50]));
        provider.GetRequiredService<SpellbookCache>().DeleteCharacter(characterId);
        provider.GetRequiredService<SpellStatePersistence>().Save(characterId, SpellStateSnapshot.Empty);
        provider.GetRequiredService<TalentPersistence>().SaveRespec(characterId, new RespecState(1, 500));
        provider.GetRequiredService<SkillSaveCoordinator>().Enqueue(characterId, new CharacterSkillSnapshot([new((ushort)SkillIds.Swords, 10, 50)], []));
        provider.GetRequiredService<SocialWriteQueue>().SetSocial(characterId, 88, SocialFlags.Friend);
        provider.GetRequiredService<HonorWriteQueue>().SaveState(characterId, CharacterHonorState.Empty with { RankPoints = 25f });
        provider.GetRequiredService<RestWriteQueue>().Save(characterId, new CharacterRestState(15f, 1234, true));
        provider.GetRequiredService<ExploredZonesWriteQueue>().Save(characterId, new uint[ExploredZonesText.WordCount]);
        provider.GetRequiredService<InstanceWriteQueue>().PlayerBound(characterId, 900, permanent: true);

        CharacterPersistenceBarrier barrier = provider.GetRequiredService<CharacterPersistenceBarrier>();
        CharacterPersistenceFlushResult result = await barrier.TryFlushCharacterAsync(characterId, Budget);

        Assert.True(result.IsDurable);
        Assert.Empty(result.Failures);
        Assert.True(questStore.Attempts >= 1);
        Assert.True(taxiStore.Attempts >= 1);
        Assert.True(spellStore.Attempts >= 1);
        Assert.True(spellStateStore.Attempts >= 1);
        Assert.True(talentStore.Attempts >= 1);
        Assert.True(skillStore.Attempts >= 1);
        Assert.True(socialStore.Attempts >= 1);
        Assert.True(honorStore.Attempts >= 1);
        Assert.True(restStore.Attempts >= 1);
        Assert.True(exploredStore.Attempts >= 1);
        Assert.True(instanceStore.Attempts >= 1);
    }

    [Fact]
    public async Task UnifiedBarrier_CollectsAllFailingQueues_AndThrowsOnFlushCharacterAsync()
    {
        var honorStore = new FaultableHonorStore { AlwaysFail = true };
        var instanceStore = new FaultableInstanceStore { AlwaysFail = true };
        var spellStateStore = new FaultableSpellStateStore { AlwaysFail = true };

        var services = new ServiceCollection();
        services.AddSingleton<IHonorStore>(honorStore);
        services.AddSingleton<IInstanceStore>(instanceStore);
        services.AddSingleton<ICharacterSpellStateStore>(spellStateStore);
        services.AddSingleton(sp =>
        {
            var q = new HonorWriteQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
            q.Start();
            return q;
        });
        services.AddSingleton(sp =>
        {
            var q = new InstanceWriteQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
            q.Start();
            return q;
        });
        services.AddSingleton(sp => new SpellStatePersistence(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance));
        services.AddSingleton<CharacterPersistenceBarrier>();

        await using ServiceProvider provider = services.BuildServiceProvider();
        const int characterId = 88;

        HonorWriteQueue honor = provider.GetRequiredService<HonorWriteQueue>();
        InstanceWriteQueue instances = provider.GetRequiredService<InstanceWriteQueue>();
        SpellStatePersistence spellState = provider.GetRequiredService<SpellStatePersistence>();

        honor.SaveState(characterId, CharacterHonorState.Empty with { RankPoints = 10f });
        instances.PlayerBound(characterId, 500, permanent: true);
        spellState.Save(characterId, SpellStateSnapshot.Empty);

        await honor.FlushAsync().WaitAsync(Budget);
        await instances.FlushAsync().WaitAsync(Budget);
        await spellState.FlushAsync().WaitAsync(Budget);

        CharacterPersistenceBarrier barrier = provider.GetRequiredService<CharacterPersistenceBarrier>();
        CharacterPersistenceFlushResult result = await barrier.TryFlushCharacterAsync(characterId, Budget);

        Assert.False(result.IsDurable);
        Assert.Contains(nameof(HonorWriteQueue), result.FailedQueues);
        Assert.Contains(nameof(InstanceWriteQueue), result.FailedQueues);
        Assert.Contains(nameof(SpellStatePersistence), result.FailedQueues);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => barrier.FlushCharacterAsync(characterId, Budget));
        Assert.Contains(nameof(HonorWriteQueue), ex.Message);
        Assert.Contains(nameof(InstanceWriteQueue), ex.Message);
        Assert.Contains(nameof(SpellStatePersistence), ex.Message);

        // Once the stores recover, the same barrier retries and drains all three cleanly.
        honorStore.AlwaysFail = false;
        instanceStore.AlwaysFail = false;
        spellStateStore.AlwaysFail = false;

        await barrier.FlushCharacterAsync(characterId, Budget);
        Assert.False(honor.HasRetainedFailure(characterId));
        Assert.False(instances.HasRetainedFailure(characterId));
        Assert.False(spellState.HasUnsaved(characterId));
    }

    private sealed class FaultableSocialStore : ISocialStore
    {
        public volatile bool RejectOwnership;
        public int Attempts;

        public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SocialEntry>>([]);

        public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default)
            => Step();

        public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GuildData>>([]);

        public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default) => Step();
        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default) => Step();
        public Task PurgeCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Step();

        private Task Step()
        {
            Interlocked.Increment(ref Attempts);
            if (RejectOwnership)
                throw new CharacterOwnershipRejectedException("world", 1);
            return Task.CompletedTask;
        }
    }

    private sealed class FaultableHonorStore : IHonorStore
    {
        public volatile bool RejectOwnership;
        public volatile bool AlwaysFail;
        public int Attempts;

        public Task<CharacterHonorData> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult(CharacterHonorData.Empty);

        public Task SaveStateAsync(int characterId, CharacterHonorState state, CancellationToken cancellationToken = default) => Step();
        public Task AppendCpAsync(int characterId, IReadOnlyList<HonorCpRecord> rows, CancellationToken cancellationToken = default) => Step();
        public Task ResetAsync(int characterId, CancellationToken cancellationToken = default) => Step();
        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Step();
        public Task DeleteDeletedCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Step();
        public Task<HonorMaintenanceState?> GetMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult<HonorMaintenanceState?>(null);
        public Task SaveMaintenanceAsync(HonorMaintenanceState state, CancellationToken cancellationToken = default) => Step();
        public Task<IReadOnlyList<HonorWeeklyScore>> ListWeeklyScoresAsync(uint weekBeginDay, uint weekEndDay, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<HonorWeeklyScore>>([]);
        public Task ApplyMaintenanceAsync(HonorMaintenanceBatch batch, CancellationToken cancellationToken = default) => Step();

        private Task Step()
        {
            Interlocked.Increment(ref Attempts);
            if (RejectOwnership)
                throw new CharacterOwnershipRejectedException("world", 1);
            if (AlwaysFail)
                throw new IOException("honor store down");
            return Task.CompletedTask;
        }
    }

    private sealed class FaultableRestStore : ICharacterRestStore
    {
        public volatile bool RejectOwnership;
        public int Attempts;

        public Task<CharacterRestState?> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<CharacterRestState?>(null);

        public Task SaveAsync(int characterId, CharacterRestState state, CancellationToken cancellationToken = default) => Step();
        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default) => Step();

        private Task Step()
        {
            Interlocked.Increment(ref Attempts);
            if (RejectOwnership)
                throw new CharacterOwnershipRejectedException("world", 1);
            return Task.CompletedTask;
        }
    }

    private sealed class FaultableTaxiFlightStore : ICharacterTaxiFlightStore
    {
        public volatile bool RejectOwnership;
        public int Attempts;

        public Task<TaxiFlightRoute?> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<TaxiFlightRoute?>(null);

        public Task SaveAsync(int characterId, TaxiFlightRoute route, CancellationToken cancellationToken = default) => Step();
        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default) => Step();

        private Task Step()
        {
            Interlocked.Increment(ref Attempts);
            if (RejectOwnership)
                throw new CharacterOwnershipRejectedException("world", 1);
            return Task.CompletedTask;
        }
    }

    private sealed class FaultableExploredZonesStore : IExploredZonesStore
    {
        public volatile bool RejectOwnership;
        public int Attempts;

        public Task<uint[]?> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<uint[]?>(null);

        public Task SaveAsync(int characterId, IReadOnlyList<uint> words, CancellationToken cancellationToken = default) => Step();
        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default) => Step();

        private Task Step()
        {
            Interlocked.Increment(ref Attempts);
            if (RejectOwnership)
                throw new CharacterOwnershipRejectedException("world", 1);
            return Task.CompletedTask;
        }
    }

    private sealed class FaultableSpellStateStore : ICharacterSpellStateStore
    {
        public volatile bool RejectOwnership;
        public volatile bool AlwaysFail;
        public int Attempts;

        public Task<CharacterSpellState> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CharacterSpellState([], []));

        public Task SaveAsync(int characterId, CharacterSpellState state, CancellationToken cancellationToken = default) => Step();
        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Step();

        private Task Step()
        {
            Interlocked.Increment(ref Attempts);
            if (RejectOwnership)
                throw new CharacterOwnershipRejectedException("world", 1);
            if (AlwaysFail)
                throw new IOException("spell state store down");
            return Task.CompletedTask;
        }
    }

    private sealed class FaultableSpellStore : ICharacterSpellStore
    {
        public volatile bool RejectOwnership;
        public int Attempts;

        public Task<IReadOnlyList<CharacterSpellRow>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CharacterSpellRow>>([]);

        public Task<IReadOnlyList<uint>> GetAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<uint>>([]);

        public Task AddAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default) => Step();
        public Task RemoveAsync(int characterId, uint spell, CancellationToken cancellationToken = default) => Step();
        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Step();

        private Task Step()
        {
            Interlocked.Increment(ref Attempts);
            if (RejectOwnership)
                throw new CharacterOwnershipRejectedException("world", 1);
            return Task.CompletedTask;
        }
    }

    private sealed class FaultableTalentStore : ICharacterTalentStore
    {
        public volatile bool RejectOwnership;
        public int Attempts;

        public Task<CharacterTalentState?> GetAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<CharacterTalentState?>(null);

        public Task SaveAsync(int characterId, CharacterTalentState state, CancellationToken cancellationToken = default) => Step();
        public Task<IReadOnlyList<uint>> GetDisabledAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<uint>>([]);
        public Task AddDisabledAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default) => Step();
        public Task RemoveDisabledAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default) => Step();
        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Step();

        private Task Step()
        {
            Interlocked.Increment(ref Attempts);
            if (RejectOwnership)
                throw new CharacterOwnershipRejectedException("world", 1);
            return Task.CompletedTask;
        }
    }

    private sealed class FaultableSkillStore : ICharacterSkillStore
    {
        public volatile bool RejectOwnership;
        public int Attempts;

        public Task<CharacterSkillSnapshot> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CharacterSkillSnapshot([], []));

        public Task<bool> ReplaceSnapshotAsync(int characterId, CharacterSkillSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Attempts);
            if (RejectOwnership)
                throw new CharacterOwnershipRejectedException("world", 1);
            return Task.FromResult(true);
        }
    }

    private sealed class FaultableQuestStore : ICharacterQuestStore
    {
        public int Attempts;

        public Task<CharacterQuestData> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult(CharacterQuestData.Empty);

        public Task SaveQuestsAsync(int characterId, IReadOnlyList<CharacterQuestStatus> upserts,
            CancellationToken cancellationToken = default, CharacterOwnershipToken? ownership = null)
        {
            Interlocked.Increment(ref Attempts);
            return Task.CompletedTask;
        }

        public Task SaveTaxiMaskAsync(int characterId, IReadOnlyList<uint> mask,
            CancellationToken cancellationToken = default, CharacterOwnershipToken? ownership = null)
        {
            Interlocked.Increment(ref Attempts);
            return Task.CompletedTask;
        }
    }

    private sealed class FaultableInstanceStore : IInstanceStore
    {
        public volatile bool AlwaysFail;
        public int Attempts;

        public Task<InstanceStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(InstanceStoreSnapshot.Empty);

        public Task SaveInstanceAsync(InstanceRecord instance, CancellationToken cancellationToken = default) => Step();
        public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default) => Step();
        public Task SaveInstanceDataAsync(uint instanceId, string data, CancellationToken cancellationToken = default) => Step();
        public Task SaveBindAsync(CharacterInstanceBindRecord bind, CancellationToken cancellationToken = default) => Step();
        public Task DeleteBindAsync(int characterId, uint instanceId, CancellationToken cancellationToken = default) => Step();
        public Task SaveResetTimeAsync(InstanceResetRecord reset, CancellationToken cancellationToken = default) => Step();
        public Task SaveLastInstanceAsync(CharacterLastInstanceRecord last, CancellationToken cancellationToken = default) => Step();
        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Step();
        public Task SaveGroupBindAsync(GroupInstanceBindRecord bind, CancellationToken cancellationToken = default) => Step();
        public Task DeleteGroupBindAsync(int leaderCharacterId, uint instanceId, CancellationToken cancellationToken = default) => Step();

        private Task Step()
        {
            Interlocked.Increment(ref Attempts);
            if (AlwaysFail)
                throw new IOException("instance store down");
            return Task.CompletedTask;
        }
    }
}
