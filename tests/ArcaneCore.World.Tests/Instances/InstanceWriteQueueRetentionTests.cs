using System.Collections.Concurrent;
using ArcaneCore.Game.Instances;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.World.Instances;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Instances;

/// <summary>
/// Retention, retry sweep, per-character barrier, shutdown failure, and terminal ownership fence
/// tests for <see cref="InstanceWriteQueue"/> (M15.0 B1).
/// </summary>
public sealed class InstanceWriteQueueRetentionTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task FailedWrite_IsRetainedAfterThreeAttempts_AndRecoveredBySweep()
    {
        var store = new FaultableInstanceStore { FailuresRemaining = 4 };
        await using ServiceProvider provider = CreateProvider(store);
        var queue = new InstanceWriteQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
        {
            RetainedRetryInterval = Timeout.InfiniteTimeSpan,
        };
        queue.Start();
        IInstancePersistence persistence = queue;

        persistence.PlayerBound(42, 700, permanent: true);
        await queue.FlushAsync().WaitAsync(Budget);

        // 3 inline attempts failed -> write is retained, not dropped.
        Assert.Equal(3, store.Attempts);
        Assert.True(queue.HasRetainedFailure(42));
        Assert.Equal([42], queue.RetainedCharacters);
        Assert.Empty(store.Writes);

        // First sweep fails (4th attempt), write remains retained.
        Assert.True(queue.PostRetainedSweepForTest());
        await queue.FlushAsync().WaitAsync(Budget);
        Assert.Equal(4, store.Attempts);
        Assert.True(queue.HasRetainedFailure(42));

        // Second sweep succeeds (5th attempt), write lands and retention clears.
        Assert.True(queue.PostRetainedSweepForTest());
        await queue.FlushAsync().WaitAsync(Budget);
        Assert.Equal(5, store.Attempts);
        Assert.False(queue.HasRetainedFailure(42));
        Assert.Empty(queue.RetainedCharacters);
        Assert.Equal(["bind 42 700 True"], store.Writes);

        await queue.StopAsync();
    }

    [Fact]
    public async Task FlushCharacterAsync_FaultsWhileRetained_AndSucceedsOnceStoreRecovers()
    {
        var store = new FaultableInstanceStore { AlwaysFail = true };
        await using ServiceProvider provider = CreateProvider(store);
        var queue = new InstanceWriteQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
        {
            RetainedRetryInterval = Timeout.InfiniteTimeSpan,
        };
        queue.Start();

        queue.PlayerBound(11, 300, permanent: true);
        queue.PlayerEnteredInstance(11, 33, 300);
        await queue.FlushAsync().WaitAsync(Budget);

        Assert.True(queue.HasRetainedFailure(11));

        // Login/handoff barrier for character 11 must fault while writes remain retained.
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => queue.FlushCharacterAsync(11).WaitAsync(Budget));
        Assert.Contains("11", ex.Message);

        // Unrelated character 99 is unaffected.
        await queue.FlushCharacterAsync(99).WaitAsync(Budget);

        // Storage recovers -> FlushCharacterAsync(11) retries and lands both writes.
        store.AlwaysFail = false;
        await queue.FlushCharacterAsync(11).WaitAsync(Budget);
        Assert.False(queue.HasRetainedFailure(11));
        Assert.Contains("bind 11 300 True", store.Writes);
        Assert.Contains("last 11 33 300", store.Writes);

        await queue.StopAsync();
    }

    [Fact]
    public async Task CharacterDeleted_PurgesRetainedBindsAndLastInstance_BeforeWritingDelete()
    {
        var store = new FaultableInstanceStore { AlwaysFail = true };
        await using ServiceProvider provider = CreateProvider(store);
        var queue = new InstanceWriteQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
        {
            RetainedRetryInterval = Timeout.InfiniteTimeSpan,
        };
        queue.Start();

        queue.PlayerBound(21, 500, permanent: true);
        queue.PlayerEnteredInstance(21, 33, 500);
        await queue.FlushAsync().WaitAsync(Budget);
        Assert.True(queue.HasRetainedFailure(21));

        store.AlwaysFail = false;
        queue.CharacterDeleted(21);
        await queue.FlushCharacterAsync(21).WaitAsync(Budget);

        Assert.False(queue.HasRetainedFailure(21));
        Assert.Equal(["delete character 21"], store.Writes);
        await queue.StopAsync();
    }

    [Fact]
    public async Task InstanceDeleted_PurgesRetainedInstanceSaveAndData()
    {
        var store = new FaultableInstanceStore { AlwaysFail = true };
        await using ServiceProvider provider = CreateProvider(store);
        var queue = new InstanceWriteQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
        {
            RetainedRetryInterval = Timeout.InfiniteTimeSpan,
        };
        queue.Start();

        queue.EnqueueInstanceSaveForTest(888, 33, 1000, "boss=1");
        await queue.FlushAsync().WaitAsync(Budget);

        store.AlwaysFail = false;
        queue.InstanceDeleted(888);
        await queue.FlushAsync().WaitAsync(Budget);

        // StopAsync drains any remaining retained keys; since InstanceDeleted purged them, only delete 888 was written.
        await queue.StopAsync();
        Assert.Equal(["delete 888"], store.Writes);
    }

    [Fact]
    public async Task StopAsync_ThrowsInvalidOperationException_WhenRetainedWritesRemainUndrained()
    {
        var store = new FaultableInstanceStore { AlwaysFail = true };
        await using ServiceProvider provider = CreateProvider(store);
        var queue = new InstanceWriteQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
        {
            RetainedRetryInterval = Timeout.InfiniteTimeSpan,
        };
        queue.Start();

        queue.PlayerBound(55, 900, permanent: true);
        await queue.FlushAsync().WaitAsync(Budget);
        Assert.True(queue.HasRetainedFailure(55));

        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync().WaitAsync(Budget));
    }

    [Fact]
    public async Task OwnershipRejected_FailsTerminallyOnAttemptOne_WithoutRetrying()
    {
        var store = new FaultableInstanceStore { RejectOwnership = true };
        await using ServiceProvider provider = CreateProvider(store);
        var queue = new InstanceWriteQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
        {
            RetainedRetryInterval = Timeout.InfiniteTimeSpan,
        };
        var fatalTcs = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        int fatalCount = 0;
        queue.FatalFailure += ex =>
        {
            Interlocked.Increment(ref fatalCount);
            fatalTcs.TrySetResult(ex);
        };
        queue.Start();

        queue.PlayerBound(77, 400, permanent: true);
        Exception fatal = await fatalTcs.Task.WaitAsync(Budget);

        Assert.IsType<CharacterOwnershipRejectedException>(fatal);
        Assert.Same(fatal, queue.TerminalFailure);
        Assert.Equal(1, Volatile.Read(ref fatalCount));
        Assert.Equal(1, store.Attempts); // No 3-attempt retry loop on stale-owner rejection.

        InvalidOperationException flushEx = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.FlushCharacterAsync(77).WaitAsync(Budget));
        Assert.Same(fatal, flushEx.InnerException);
        InvalidOperationException stopEx = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync().WaitAsync(Budget));
        Assert.Same(fatal, stopEx.InnerException);
    }

    private static ServiceProvider CreateProvider(IInstanceStore store) =>
        new ServiceCollection().AddSingleton(store).BuildServiceProvider();

    private sealed class FaultableInstanceStore : IInstanceStore
    {
        public int FailuresRemaining;
        public volatile bool AlwaysFail;
        public volatile bool RejectOwnership;
        public int Attempts;
        public ConcurrentQueue<string> Writes { get; } = new();

        public Task<InstanceStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(InstanceStoreSnapshot.Empty);

        public Task SaveInstanceAsync(InstanceRecord instance, CancellationToken cancellationToken = default)
            => RecordOrThrow($"instance {instance.Id}");

        public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default)
            => RecordOrThrow($"delete {instanceId}");

        public Task SaveInstanceDataAsync(uint instanceId, string data, CancellationToken cancellationToken = default)
            => RecordOrThrow($"data {instanceId} {data}");

        public Task SaveBindAsync(CharacterInstanceBindRecord bind, CancellationToken cancellationToken = default)
            => RecordOrThrow($"bind {bind.CharacterId} {bind.InstanceId} {bind.Permanent}");

        public Task DeleteBindAsync(int characterId, uint instanceId, CancellationToken cancellationToken = default)
            => RecordOrThrow($"unbind {characterId} {instanceId}");

        public Task SaveResetTimeAsync(InstanceResetRecord reset, CancellationToken cancellationToken = default)
            => RecordOrThrow($"reset {reset.MapId}");

        public Task SaveLastInstanceAsync(CharacterLastInstanceRecord last, CancellationToken cancellationToken = default)
            => RecordOrThrow($"last {last.CharacterId} {last.MapId} {last.InstanceId}");

        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
            => RecordOrThrow($"delete character {characterId}");

        public Task SaveGroupBindAsync(GroupInstanceBindRecord bind, CancellationToken cancellationToken = default)
            => RecordOrThrow($"group bind {bind.LeaderCharacterId} {bind.InstanceId} {bind.Permanent}");

        public Task DeleteGroupBindAsync(int leaderCharacterId, uint instanceId, CancellationToken cancellationToken = default)
            => RecordOrThrow($"group unbind {leaderCharacterId} {instanceId}");

        private Task RecordOrThrow(string entry)
        {
            Interlocked.Increment(ref Attempts);
            if (RejectOwnership)
            {
                throw new CharacterOwnershipRejectedException("world", 1);
            }

            if (AlwaysFail)
            {
                throw new IOException("simulated instance store outage");
            }

            while (true)
            {
                int remaining = Volatile.Read(ref FailuresRemaining);
                if (remaining <= 0)
                {
                    break;
                }

                if (Interlocked.CompareExchange(ref FailuresRemaining, remaining - 1, remaining) == remaining)
                {
                    throw new IOException("simulated transient instance store error");
                }
            }

            Writes.Enqueue(entry);
            return Task.CompletedTask;
        }
    }
}
