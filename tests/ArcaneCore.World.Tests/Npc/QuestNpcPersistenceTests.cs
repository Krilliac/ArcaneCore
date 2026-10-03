using System.Collections.Concurrent;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

public sealed class QuestNpcPersistenceTests
{
    [Fact]
    public async Task DeltasAndTaxiMasks_AreCopiedOrderedAndSavedWithFreshScopes()
    {
        var storage = new Storage { BlockFirstQuestSave = true };
        await using ServiceProvider provider = Provider(storage);
        await using var queue = Queue(provider);
        queue.Start();
        var first = new List<CharacterQuestStatus> { Row(7, 101, status: 3, timer: 123) };
        queue.SaveQuests(7, first);
        first[0] = Row(7, 999, status: 5);
        await storage.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.SaveQuests(7, [Row(7, 101, status: 5)]);
        uint[] mask = [7, 8];
        queue.SaveTaxiMask(7, mask);
        mask[0] = 999;
        Task barrier = queue.FlushCharacterAsync(7);
        Assert.False(barrier.IsCompleted);

        storage.ReleaseFirst.TrySetResult();
        await barrier.WaitAsync(TimeSpan.FromSeconds(5));

        CharacterQuestData saved = storage.Snapshot(7);
        Assert.Equal((byte)5, Assert.Single(saved.Quests).Status);
        Assert.Equal(new uint[] { 7, 8 }, saved.TaxiMask);
        Assert.Equal(new[] { "quests:7:101", "quests:7:101", "taxi:7" }, storage.Attempts);
        Assert.Equal(3, storage.ScopesCreated);
        Assert.Equal(storage.ScopesCreated, storage.ScopesDisposed);
    }

    [Fact]
    public async Task AFailedDelta_IsRecoveredFromTheCompleteAuthoritativeJournalAndTaxiMask()
    {
        var storage = new Storage { FailuresRemaining = 1 };
        await using ServiceProvider provider = Provider(storage);
        await using var queue = Queue(provider);
        queue.LoadCharacter(7, new CharacterQuestData([Row(7, 101, 3, 123), Row(7, 202, 1)], [1]), queue.CaptureLoadRevision(7));

        queue.SaveQuests(7, [Row(7, 101, 5)]);
        queue.SaveTaxiMask(7, [3, 4]);
        await queue.FlushCharacterAsync(7).WaitAsync(TimeSpan.FromSeconds(5));

        CharacterQuestData saved = storage.Snapshot(7);
        Assert.Equal(new uint[] { 101, 202 }, saved.Quests.Select(q => q.Quest));
        Assert.Equal((byte)5, saved.Quests[0].Status);
        Assert.Equal(0, saved.Quests[0].Timer);
        Assert.Equal((byte)1, saved.Quests[1].Status);
        Assert.Equal(new uint[] { 3, 4 }, saved.TaxiMask);
        Assert.Equal(new[] { "quests:7:101", "taxi:7", "quests:7:101,202", "taxi:7" }, storage.Attempts);
    }

    [Fact]
    public async Task AnUnrecoveredFailure_RefusesRelogAndStaleLoadsUntilAllLatestRowsRecover()
    {
        var storage = new Storage { FailWrites = true };
        await using ServiceProvider provider = Provider(storage);
        await using var queue = Queue(provider);
        var stale = new CharacterQuestData([Row(7, 101, 3, 123)], [1]);
        queue.LoadCharacter(7, stale, queue.CaptureLoadRevision(7));
        long staleRevision = queue.CaptureLoadRevision(7);
        queue.SaveQuests(7, [Row(7, 101, 5)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.FlushCharacterAsync(7));
        Assert.Throws<InvalidOperationException>(() => queue.LoadCharacter(7, stale, staleRevision));
        Assert.Throws<InvalidOperationException>(() => queue.CaptureLoadRevision(7));
        Assert.Empty(storage.Snapshot(7).Quests);

        queue.SaveQuests(7, [Row(7, 202, 1)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.FlushCharacterAsync(7));
        storage.FailWrites = false;
        await queue.FlushCharacterAsync(7).WaitAsync(TimeSpan.FromSeconds(5));

        CharacterQuestData recovered = storage.Snapshot(7);
        Assert.Equal((byte)5, recovered.Quests.Single(q => q.Quest == 101).Status);
        Assert.Equal(0, recovered.Quests.Single(q => q.Quest == 101).Timer);
        Assert.Equal((byte)1, recovered.Quests.Single(q => q.Quest == 202).Status);
        Assert.Equal(new uint[] { 1 }, recovered.TaxiMask);
        queue.LoadCharacter(7, recovered, queue.CaptureLoadRevision(7));
    }

    [Fact]
    public async Task OneCharactersFailure_DoesNotRefuseAnotherCharactersBarrier()
    {
        var storage = new Storage { FailCharacter = 7 };
        await using ServiceProvider provider = Provider(storage);
        await using var queue = Queue(provider);
        queue.SaveQuests(7, [Row(7, 101, 5)]);
        queue.SaveQuests(8, [Row(8, 202, 1)]);

        await queue.FlushCharacterAsync(8).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((uint)202, Assert.Single(storage.Snapshot(8).Quests).Quest);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.FlushCharacterAsync(7));
        storage.FailCharacter = 0;
        await queue.FlushCharacterAsync(7);
        Assert.Equal((byte)5, Assert.Single(storage.Snapshot(7).Quests).Status);
    }

    [Fact]
    public async Task StorageLoad_CannotOverwriteAStillPendingAuthoritativeChange()
    {
        var storage = new Storage { BlockFirstQuestSave = true };
        await using ServiceProvider provider = Provider(storage);
        await using var queue = Queue(provider);
        var stale = new CharacterQuestData([Row(7, 101, 3, 123)], []);
        queue.LoadCharacter(7, stale, queue.CaptureLoadRevision(7));
        long staleRevision = queue.CaptureLoadRevision(7);
        queue.SaveQuests(7, [Row(7, 101, 5)]);
        await storage.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Throws<InvalidOperationException>(() => queue.LoadCharacter(7, stale, staleRevision));
        storage.ReleaseFirst.TrySetResult();
        await queue.FlushCharacterAsync(7);
        Assert.Equal((byte)5, Assert.Single(storage.Snapshot(7).Quests).Status);
    }

    [Fact]
    public async Task RevisionCapture_RefusesWritesStartedAfterTheEarlierLoginBarrier()
    {
        var storage = new Storage { BlockFirstQuestSave = true };
        await using ServiceProvider provider = Provider(storage);
        await using var queue = Queue(provider);
        await queue.FlushCharacterAsync(7);
        queue.SaveQuests(7, [Row(7, 101, 5)]);
        await storage.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Throws<InvalidOperationException>(() => queue.CaptureLoadRevision(7));
        storage.ReleaseFirst.TrySetResult();
        await queue.FlushCharacterAsync(7);
        long revision = queue.CaptureLoadRevision(7);
        queue.LoadCharacter(7, storage.Snapshot(7), revision);
        Assert.Equal(revision + 1, queue.CaptureLoadRevision(7));
    }

    [Fact]
    public async Task StaleStorageLoad_AfterCompletedWritesIsRejectedAndCannotCorruptLaterRecovery()
    {
        var storage = new Storage();
        await using ServiceProvider provider = Provider(storage);
        await using var queue = Queue(provider);
        var stale = new CharacterQuestData([Row(7, 101, 3, 123)], [1]);
        queue.LoadCharacter(7, stale, queue.CaptureLoadRevision(7));
        await queue.FlushCharacterAsync(7);
        long oldLoadRevision = queue.CaptureLoadRevision(7);

        queue.SaveQuests(7, [Row(7, 101, 5)]);
        queue.SaveTaxiMask(7, [9]);
        await queue.FlushCharacterAsync(7);
        long latestRevision = queue.CaptureLoadRevision(7);
        Assert.Throws<InvalidOperationException>(() => queue.LoadCharacter(7, stale, oldLoadRevision));
        Assert.Equal(latestRevision, queue.CaptureLoadRevision(7));

        // A later failed delta forces replay of the cache. It must still contain the newer
        // completed row and taxi mask rather than the rejected storage read's old snapshot.
        storage.FailWrites = true;
        queue.SaveQuests(7, [Row(7, 202, 1)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.FlushCharacterAsync(7));
        storage.FailWrites = false;
        await queue.FlushCharacterAsync(7).WaitAsync(TimeSpan.FromSeconds(5));
        CharacterQuestData recovered = storage.Snapshot(7);
        Assert.Equal((byte)5, recovered.Quests.Single(q => q.Quest == 101).Status);
        Assert.Equal(0, recovered.Quests.Single(q => q.Quest == 101).Timer);
        Assert.Equal((byte)1, recovered.Quests.Single(q => q.Quest == 202).Status);
        Assert.Equal(new uint[] { 9 }, recovered.TaxiMask);
    }

    [Theory]
    [InlineData("quest")]
    [InlineData("taxi")]
    [InlineData("load")]
    public async Task EveryCacheMutation_InvalidatesAnEarlierStorageLoadEvenAfterItsBarrier(string mutation)
    {
        var storage = new Storage();
        await using ServiceProvider provider = Provider(storage);
        await using var queue = Queue(provider);
        long oldRevision = queue.CaptureLoadRevision(7);
        switch (mutation)
        {
            case "quest": queue.SaveQuests(7, [Row(7, 101, 5)]); break;
            case "taxi": queue.SaveTaxiMask(7, [9]); break;
            case "load": queue.LoadCharacter(7, new CharacterQuestData([Row(7, 101, 5)], [9]), oldRevision); break;
        }

        await queue.FlushCharacterAsync(7);
        Assert.Equal(oldRevision + 1, queue.CaptureLoadRevision(7));
        Assert.Throws<InvalidOperationException>(() => queue.LoadCharacter(7, CharacterQuestData.Empty, oldRevision));
        long nextLoadRevision = queue.CaptureLoadRevision(7);
        queue.LoadCharacter(7, storage.Snapshot(7), nextLoadRevision);
        Assert.Equal(nextLoadRevision + 1, queue.CaptureLoadRevision(7));
        Assert.Throws<InvalidOperationException>(() => queue.LoadCharacter(7, CharacterQuestData.Empty, nextLoadRevision));
    }

    [Fact]
    public async Task Shutdown_WaitsForQueuedWritesAndRetriesFailuresBeforeCompleting()
    {
        var storage = new Storage { BlockFirstQuestSave = true, FailuresRemaining = 1 };
        await using ServiceProvider provider = Provider(storage);
        var queue = Queue(provider);
        queue.LoadCharacter(7, new CharacterQuestData([Row(7, 101, 3, 123)], [1]), queue.CaptureLoadRevision(7));
        queue.SaveQuests(7, [Row(7, 101, 5)]);
        await storage.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task shutdown = queue.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        storage.ReleaseFirst.TrySetResult();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((byte)5, Assert.Single(storage.Snapshot(7).Quests).Status);
        Assert.Equal(new[] { "quests:7:101", "quests:7:101", "taxi:7" }, storage.Attempts);
        Assert.Throws<ObjectDisposedException>(() => queue.SaveQuests(7, [Row(7, 101, 3)]));
        await queue.DisposeAsync();
    }

    [Fact]
    public async Task Shutdown_ReportsUnrecoverableSavesInsteadOfClaimingADrain()
    {
        var storage = new Storage { FailWrites = true };
        await using ServiceProvider provider = Provider(storage);
        var queue = Queue(provider);
        queue.SaveQuests(7, [Row(7, 101, 5)]);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.DisposeAsync().AsTask());
        Assert.Contains("7", error.Message);
        Assert.Equal(2, storage.Attempts.Count);
        Assert.Equal(storage.ScopesCreated, storage.ScopesDisposed);
    }

    [Fact]
    public async Task AHostWithoutStorage_AllowsAnEmptyBarrierButRefusesQueuedWrites()
    {
        await using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        var queue = Queue(provider);
        await queue.FlushCharacterAsync(7);
        queue.SaveQuests(7, [Row(7, 101, 5)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.FlushCharacterAsync(7));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Quarantine_SuppressesQueuedAndNewDeltasAndShutdownSnapshots()
    {
        var storage = new Storage { BlockFirstQuestSave = true };
        await using ServiceProvider provider = Provider(storage);
        await using var queue = Queue(provider);
        queue.SaveQuests(8, [Row(8, 202, 1)]);
        await storage.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.SaveQuests(7, [Row(7, 101, 5)]);
        queue.SaveTaxiMask(7, [1]);
        queue.QuarantineCharacter(7);
        queue.SaveQuests(7, [Row(7, 303, 3)]);
        queue.SaveTaxiMask(7, [9]);
        Task drain = queue.FlushCharacterAsync(7);
        Assert.False(drain.IsCompleted);
        storage.ReleaseFirst.TrySetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(queue.IsQuarantined(7));
        Assert.Empty(storage.Snapshot(7).Quests);
        Assert.Empty(storage.Snapshot(7).TaxiMask);
        await queue.DisposeAsync();
        Assert.DoesNotContain(storage.Attempts, attempt => attempt.StartsWith("quests:7:", StringComparison.Ordinal)
            || attempt == "taxi:7");
        Assert.Equal(storage.ScopesCreated, storage.ScopesDisposed);
    }

    [Fact]
    public async Task QuarantinedFailure_FreshAuthoritativeLoadSupersedesRetainedCacheBeforeExplicitResume()
    {
        var storage = new Storage { FailWrites = true };
        await using ServiceProvider provider = Provider(storage);
        await using var queue = Queue(provider);
        queue.LoadCharacter(7, new CharacterQuestData([Row(7, 101, 3)], [1]), queue.CaptureLoadRevision(7));
        queue.SaveQuests(7, [Row(7, 101, 5)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.FlushCharacterAsync(7));
        queue.QuarantineCharacter(7);
        int failedAttempts = storage.Attempts.Count;
        await queue.FlushCharacterAsync(7);
        Assert.Equal(failedAttempts, storage.Attempts.Count);
        CharacterQuestStatus authoritative = Row(7, 101, 1) with { Rewarded = true, RewardChoice = 117 };
        queue.LoadCharacter(7, new CharacterQuestData([authoritative], [9]), queue.CaptureLoadRevision(7));
        queue.SaveQuests(7, [Row(7, 101, 3)]);
        queue.SaveTaxiMask(7, [1]);
        Assert.True(queue.IsQuarantined(7));
        storage.FailWrites = false;
        storage.FailuresRemaining = 1;
        queue.ResumeCharacter(7);
        queue.SaveQuests(7, [Row(7, 202, 1)]);
        await queue.FlushCharacterAsync(7).WaitAsync(TimeSpan.FromSeconds(5));
        CharacterQuestData recovered = storage.Snapshot(7);
        Assert.Equal(authoritative, Assert.Single(recovered.Quests, row => row.Quest == 101));
        Assert.Equal(new uint[] { 9 }, recovered.TaxiMask);
        Assert.False(queue.IsQuarantined(7));
    }

    [Fact]
    public async Task DurableReward_CanBeAdoptedWhileQuarantinedWithoutShutdownRewritingTheOldJournal()
    {
        var storage = new Storage();
        await using ServiceProvider provider = Provider(storage);
        await using var queue = Queue(provider);
        CharacterQuestStatus expected = Row(7, 101, 1);
        CharacterQuestStatus rewarded = expected with { Rewarded = true, RewardChoice = 117 };
        queue.LoadCharacter(7, new CharacterQuestData([expected], []), queue.CaptureLoadRevision(7));
        long previousRevision = queue.CaptureLoadRevision(7);
        await storage.SaveQuestsAsync(7, [rewarded]);
        queue.QuarantineCharacter(7);
        queue.AdoptRewarded(rewarded);
        Assert.Equal(previousRevision + 1, queue.CaptureLoadRevision(7));
        Assert.Throws<InvalidOperationException>(() => queue.LoadCharacter(7,
            new CharacterQuestData([expected], []), previousRevision));
        await queue.FlushCharacterAsync(7);
        await queue.DisposeAsync();
        Assert.True(queue.IsQuarantined(7));
        Assert.Equal(rewarded, Assert.Single(storage.Snapshot(7).Quests));
        Assert.Single(storage.Attempts);
    }

    private static QuestNpcPersistence Queue(IServiceProvider provider) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);

    private static ServiceProvider Provider(Storage storage) => new ServiceCollection()
        .AddScoped<ICharacterQuestStore>(_ => new ScopedStore(storage))
        .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    private static CharacterQuestStatus Row(int character, uint quest, byte status, long timer = 0) =>
        new(character, quest, status, false, false, timer, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private sealed class Storage
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<int, Dictionary<uint, CharacterQuestStatus>> _quests = [];
        private readonly Dictionary<int, uint[]> _taxi = [];
        private int _questAttempts;
        public readonly ConcurrentQueue<string> Attempts = new();
        public readonly TaskCompletionSource FirstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockFirstQuestSave;
        public volatile bool FailWrites;
        public volatile int FailCharacter;
        public int FailuresRemaining;
        public int ScopesCreated;
        public int ScopesDisposed;

        public CharacterQuestData Snapshot(int characterId)
        {
            lock (_gate)
            {
                return new CharacterQuestData(_quests.TryGetValue(characterId, out var quests)
                    ? quests.Values.OrderBy(q => q.Quest).ToArray() : [],
                    _taxi.TryGetValue(characterId, out var taxi) ? taxi.ToArray() : []);
            }
        }

        public async Task SaveQuestsAsync(int characterId, IReadOnlyList<CharacterQuestStatus> rows)
        {
            Attempts.Enqueue($"quests:{characterId}:{string.Join(",", rows.Select(q => q.Quest))}");
            if (Interlocked.Increment(ref _questAttempts) == 1 && BlockFirstQuestSave)
            {
                FirstEntered.TrySetResult();
                await ReleaseFirst.Task.ConfigureAwait(false);
            }

            ThrowIfFailing(characterId);
            lock (_gate)
            {
                if (!_quests.TryGetValue(characterId, out var quests))
                {
                    _quests[characterId] = quests = [];
                }

                foreach (CharacterQuestStatus row in rows)
                {
                    quests[row.Quest] = row;
                }
            }
        }

        public Task SaveTaxiMaskAsync(int characterId, IReadOnlyList<uint> mask)
        {
            Attempts.Enqueue($"taxi:{characterId}");
            ThrowIfFailing(characterId);
            lock (_gate)
            {
                _taxi[characterId] = mask.ToArray();
            }

            return Task.CompletedTask;
        }

        private void ThrowIfFailing(int characterId)
        {
            if (FailWrites || FailCharacter == characterId || FailuresRemaining > 0 && --FailuresRemaining >= 0)
            {
                throw new InvalidOperationException("quest storage unavailable");
            }
        }
    }

    private sealed class ScopedStore : ICharacterQuestStore, IDisposable
    {
        private readonly Storage _storage;
        public ScopedStore(Storage storage)
        {
            _storage = storage;
            Interlocked.Increment(ref storage.ScopesCreated);
        }

        public Task<CharacterQuestData> LoadAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_storage.Snapshot(characterId));

        public Task SaveQuestsAsync(int characterId, IReadOnlyList<CharacterQuestStatus> upserts, CancellationToken cancellationToken = default) =>
            _storage.SaveQuestsAsync(characterId, upserts);

        public Task SaveTaxiMaskAsync(int characterId, IReadOnlyList<uint> mask, CancellationToken cancellationToken = default) =>
            _storage.SaveTaxiMaskAsync(characterId, mask);

        public void Dispose() => Interlocked.Increment(ref _storage.ScopesDisposed);
    }
}
