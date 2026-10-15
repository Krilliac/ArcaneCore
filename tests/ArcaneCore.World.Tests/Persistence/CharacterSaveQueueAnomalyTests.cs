using System.Collections.Concurrent;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Stores;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Persistence;

/// <summary>The <c>persist/*</c> anomalies of <see cref="CharacterSaveQueue"/>: dropped on shutdown, retry exhausted, circuit open/recovered, slow save and backlog.</summary>
[Collection(PersistAnomalyCollection.Name)]
public sealed class CharacterSaveQueueAnomalyTests
{
    private const string Queue = nameof(CharacterSaveQueue);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static CharacterState State(int id, uint money) => new(id, 0, 12, 0, 0, 83.5f, 0, 1, 0, Money: money);

    private static SqliteException Busy() => new("SQLite Error 5: 'database is locked'.", 5);

    private static async Task<bool> BecomesTrueAsync(Func<bool> condition, int seconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        public Rig(WorldRuntimeOptions? options = null, TimeSpan? slowThreshold = null)
        {
            _services = new ServiceCollection().AddSingleton<ICharacterStore>(Store).BuildServiceProvider();
            Queue = new CharacterSaveQueue(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance, Options.Create(options ?? new WorldRuntimeOptions()))
            {
                SlowSaveThreshold = slowThreshold ?? TimeSpan.FromSeconds(1),
            };
        }

        public RecordingStore Store { get; } = new();

        public CharacterSaveQueue Queue { get; }

        /// <summary>Opened on dispose, so a failed assertion cannot leave a blocked write holding the stop forever.</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            Store.OnSave = null;
            try
            {
                await Queue.StopAsync().WaitAsync(Wait);
            }
            catch (InvalidOperationException)
            {
                // a scenario that ends with a retained or rejected snapshot
            }

            await _services.DisposeAsync();
        }
    }

    [Fact]
    public async Task WriteDroppedOnShutdown_FiresOnce_WhenASnapshotArrivesAfterTheQueueStopped()
    {
        await using var rig = new Rig();
        rig.Queue.Start();
        await rig.Queue.StopAsync().WaitAsync(Wait);
        using AnomalyCapture capture = PersistAnomalies.Capture();

        rig.Queue.Enqueue(State(41, 1));

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Equal("persist/write-dropped-on-shutdown", notice.Key);
        Assert.Equal(AnomalySeverity.Error, notice.Severity);
        Assert.Equal("character 41", notice.Subject);
        Assert.Contains("reason=queue-closed", notice.Detail, StringComparison.Ordinal);
        Assert.Empty(rig.Store.Saved);
    }

    [Fact]
    public async Task WriteDroppedOnShutdown_FiresOnce_WhenTheStopCannotDrainARetainedSnapshot()
    {
        await using var rig = new Rig();
        rig.Store.OnSave = (_, _) => Task.FromException(new IOException("store down"));
        rig.Queue.Enqueue(State(42, 1));
        rig.Queue.Start();
        Assert.True(await BecomesTrueAsync(() => rig.Queue.Pending == 0 && rig.Store.Attempts.Count >= 3, seconds: 15));
        using AnomalyCapture capture = PersistAnomalies.Capture();

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Queue.StopAsync().WaitAsync(Wait));

        AnomalyNotice dropped = Assert.Single(capture.Of("write-dropped-on-shutdown", Queue));
        Assert.Equal(AnomalySeverity.Error, dropped.Severity);
        Assert.Equal("queue CharacterSaveQueue", dropped.Subject);
        Assert.Contains("reason=undrained-on-stop", dropped.Detail, StringComparison.Ordinal);
        Assert.Contains("42", dropped.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteRetryExhausted_FiresOnce_WhenASnapshotFailsAllItsAttempts()
    {
        await using var rig = new Rig();
        rig.Store.OnSave = (_, _) => Task.FromException(new IOException("store down"));
        using AnomalyCapture capture = PersistAnomalies.Capture();

        rig.Queue.Enqueue(State(43, 1));
        Task flush = rig.Queue.FlushCharacterAsync(43); // starts the consumer; the barrier shares the one write of the waiting snapshot
        await Assert.ThrowsAsync<IOException>(() => flush.WaitAsync(Wait));

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Equal("persist/write-retry-exhausted", notice.Key);
        Assert.Equal(AnomalySeverity.Error, notice.Severity);
        Assert.Equal("character 43", notice.Subject);
        Assert.Contains("attempts=3", notice.Detail, StringComparison.Ordinal);
        Assert.Contains("error=IOException", notice.Detail, StringComparison.Ordinal);
        Assert.Equal(3, rig.Store.Attempts.Count); // the detail is true: three tries
    }

    [Fact]
    public async Task CircuitOpen_FiresOnce_WhenTheOwnershipFenceRejectsASnapshot_AndNotAgainForLaterCalls()
    {
        await using var rig = new Rig();
        var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Queue.FatalFailure += ex => fatal.TrySetResult(ex);
        rig.Store.OnSave = (_, _) => Task.FromException(new CharacterOwnershipRejectedException("world", 7));
        using AnomalyCapture capture = PersistAnomalies.Capture();

        rig.Queue.Enqueue(State(44, 1));
        rig.Queue.Start();
        await fatal.Task.WaitAsync(Wait);

        AnomalyNotice notice = Assert.Single(capture.Of("circuit-open", Queue));
        Assert.Equal("persist/circuit-open", notice.Key);
        Assert.Equal(AnomalySeverity.Warning, notice.Severity);
        Assert.Contains("reason=terminal-ownership-failure", notice.Detail, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => rig.Queue.Enqueue(State(45, 1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Queue.StopAsync().WaitAsync(Wait));
        Assert.Single(capture.Of("circuit-open", Queue));
        Assert.Empty(capture.Of("circuit-recovered", Queue));
    }

    [Fact]
    public async Task CircuitOpenAndRecovered_FireOncePerBusyEpisode_NotOncePerLockedFailure()
    {
        await using var rig = new Rig();
        int locked = 0;
        rig.Store.OnSave = (_, _) => Interlocked.Increment(ref locked) <= 2 ? Task.FromException(Busy()) : Task.CompletedTask; // two locked failures, then it works
        using AnomalyCapture capture = PersistAnomalies.Capture();

        rig.Queue.Enqueue(State(46, 1));
        rig.Queue.Start();
        Assert.True(await BecomesTrueAsync(() => !rig.Store.Saved.IsEmpty, seconds: 20), "the locked snapshot was never written after the lock cleared");

        AnomalyNotice open = Assert.Single(capture.Of("circuit-open", Queue));
        Assert.Equal(AnomalySeverity.Warning, open.Severity);
        Assert.Contains("reason=database-busy", open.Detail, StringComparison.Ordinal);
        Assert.Contains("character=46", open.Detail, StringComparison.Ordinal);
        Assert.Contains("streak=1", open.Detail, StringComparison.Ordinal); // the second locked failure (streak 2) added nothing

        AnomalyNotice recovered = Assert.Single(capture.Of("circuit-recovered", Queue));
        Assert.Equal("persist/circuit-recovered", recovered.Key);
        Assert.Equal(AnomalySeverity.Info, recovered.Severity);
        Assert.Contains("previousBusyStreak=2", recovered.Detail, StringComparison.Ordinal);
        Assert.True(Array.IndexOf(capture.Notices.ToArray(), open) < Array.IndexOf(capture.Notices.ToArray(), recovered), "recovered was reported before open");
    }

    [Fact]
    public async Task SaveSlow_FiresOnce_WhenAWriteTakesLongerThanTheThreshold()
    {
        await using var rig = new Rig(slowThreshold: TimeSpan.FromMilliseconds(50));
        rig.Store.OnSave = async (_, token) => await Task.Delay(150, token);
        using AnomalyCapture capture = PersistAnomalies.Capture();

        rig.Queue.Enqueue(State(47, 1));
        rig.Queue.Start();
        await rig.Queue.FlushCharacterAsync(47).WaitAsync(Wait);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Equal("persist/save-slow", notice.Key);
        Assert.Equal(AnomalySeverity.Info, notice.Severity);
        Assert.Equal("character 47", notice.Subject);
        Assert.Contains("thresholdMs=50", notice.Detail, StringComparison.Ordinal);
        long elapsed = long.Parse(notice.Detail.Split("elapsedMs=")[1].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(elapsed >= 100, $"elapsedMs={elapsed} for a 150 ms write");
    }

    [Fact]
    public async Task SaveSlow_IsSilent_ForAFastWrite_AndWhenTheThresholdIsZero()
    {
        await using var fast = new Rig(slowThreshold: TimeSpan.FromSeconds(30));
        await using var disabled = new Rig(slowThreshold: TimeSpan.Zero);
        disabled.Store.OnSave = async (_, token) => await Task.Delay(100, token);
        using AnomalyCapture capture = PersistAnomalies.Capture();

        fast.Queue.Enqueue(State(48, 1));
        fast.Queue.Start();
        await fast.Queue.FlushCharacterAsync(48).WaitAsync(Wait);
        disabled.Queue.Enqueue(State(49, 1));
        disabled.Queue.Start();
        await disabled.Queue.FlushCharacterAsync(49).WaitAsync(Wait);

        Assert.Single(fast.Store.Saved);
        Assert.Single(disabled.Store.Saved);
        Assert.Empty(capture.Notices);
    }

    [Fact]
    public async Task QueueBacklog_FiresOncePerEpisode_WhenTheDepthReachesTheWarnThreshold()
    {
        await using var rig = new Rig(new WorldRuntimeOptions { SaveQueueWarnDepth = 3, SaveQueueWarnAgeMs = 0 });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = rig.Release;
        rig.Store.OnSave = async (state, _) =>
        {
            if (state.Id == 99)
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        using AnomalyCapture capture = PersistAnomalies.Capture();

        rig.Queue.Enqueue(State(99, 1));
        rig.Queue.Start();
        await entered.Task.WaitAsync(Wait);
        for (int id = 1; id <= 5; id++)
        {
            rig.Queue.Enqueue(State(id, 5));
        }

        // The report is checked at most once a second, on an enqueue: keep enqueueing until the check has run over the backlog.
        Assert.True(await BecomesTrueAsync(() =>
        {
            rig.Queue.Enqueue(State(6, 5));
            return capture.Any("persist/queue-backlog");
        }, seconds: 10), "the backlog was never reported");
        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(1_100);
            rig.Queue.Enqueue(State(6, 5)); // more checks while the backlog persists: still the same episode
        }

        AnomalyNotice notice = Assert.Single(capture.Of("queue-backlog", Queue));
        Assert.Equal("persist/queue-backlog", notice.Key);
        Assert.Equal(AnomalySeverity.Warning, notice.Severity);
        Assert.Contains("warnDepth=3", notice.Detail, StringComparison.Ordinal);
        release.TrySetResult();
        await rig.Queue.FlushCharacterAsync(6).WaitAsync(Wait);
    }

    [Fact]
    public async Task QueueBacklog_IsSilent_WhileTheDepthStaysBelowTheWarnThreshold()
    {
        await using var rig = new Rig(new WorldRuntimeOptions { SaveQueueWarnDepth = 100, SaveQueueWarnAgeMs = 0 }, slowThreshold: TimeSpan.FromMinutes(5)); // the blocked write must not read as a slow save
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = rig.Release;
        rig.Store.OnSave = async (state, _) =>
        {
            if (state.Id == 99)
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        using AnomalyCapture capture = PersistAnomalies.Capture();

        rig.Queue.Enqueue(State(99, 1));
        rig.Queue.Start();
        await entered.Task.WaitAsync(Wait);
        for (int id = 1; id <= 5; id++)
        {
            rig.Queue.Enqueue(State(id, 5));
        }

        await Task.Delay(1_100);
        rig.Queue.Enqueue(State(6, 5)); // the once-a-second check runs now, over a backlog of seven characters
        Assert.True(rig.Queue.Pending >= 6);
        release.TrySetResult();
        await rig.Queue.FlushCharacterAsync(6).WaitAsync(Wait);

        Assert.Empty(capture.Notices);
    }

    [Fact]
    public async Task HealthyQueue_ReportsNothing_ThroughEnqueueFlushAndStop()
    {
        var rig = new Rig();
        using AnomalyCapture capture = PersistAnomalies.Capture();

        rig.Queue.Enqueue(State(50, 1));
        rig.Queue.Start();
        await rig.Queue.FlushCharacterAsync(50).WaitAsync(Wait);
        rig.Queue.Enqueue(State(50, 2));
        await rig.Queue.StopAsync().WaitAsync(Wait);

        Assert.Equal(2u, rig.Store.Saved.Last().Money);
        Assert.Empty(capture.Notices);
        await rig.DisposeAsync();
    }

    [Fact]
    public async Task ATransientNonBusyFailure_ThatTheInlineRetryAbsorbs_ReportsNothing()
    {
        await using var rig = new Rig();
        int calls = 0;
        rig.Store.OnSave = (_, _) => Interlocked.Increment(ref calls) == 1 ? Task.FromException(new IOException("blip")) : Task.CompletedTask;
        using AnomalyCapture capture = PersistAnomalies.Capture();

        rig.Queue.Enqueue(State(51, 1));
        rig.Queue.Start();
        await rig.Queue.FlushCharacterAsync(51).WaitAsync(Wait);

        Assert.Equal(2, rig.Store.Attempts.Count);
        Assert.Single(rig.Store.Saved);
        Assert.Empty(capture.Notices);
    }

    private sealed class RecordingStore : ICharacterStore
    {
        private readonly InMemoryCharacterStore _inner = new();
        private Func<CharacterState, CancellationToken, Task>? _onSave;

        public Func<CharacterState, CancellationToken, Task>? OnSave
        {
            get => Volatile.Read(ref _onSave);
            set => Volatile.Write(ref _onSave, value);
        }

        public ConcurrentQueue<CharacterState> Attempts { get; } = new();

        public ConcurrentQueue<CharacterState> Saved { get; } = new();

        public async Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts.Enqueue(state);
            if (OnSave is { } handler)
            {
                await handler(state, cancellationToken);
            }

            Saved.Enqueue(state);
        }

        public Task<IReadOnlyList<CharacterRecord>> GetByAccountAsync(int accountId, CancellationToken cancellationToken = default) => _inner.GetByAccountAsync(accountId, cancellationToken);
        public Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => _inner.GetByIdAsync(id, cancellationToken);
        public Task<bool> IsNameTakenAsync(string name, CancellationToken cancellationToken = default) => _inner.IsNameTakenAsync(name, cancellationToken);
        public Task<int> CountByAccountAsync(int accountId, CancellationToken cancellationToken = default) => _inner.CountByAccountAsync(accountId, cancellationToken);
        public Task<CharacterRecord> CreateAsync(CharacterRecord character, CancellationToken cancellationToken = default) => _inner.CreateAsync(character, cancellationToken);
        public Task<bool> DeleteAsync(int id, int accountId, CancellationToken cancellationToken = default) => _inner.DeleteAsync(id, accountId, cancellationToken);
        public Task<IReadOnlyList<ActionButton>> GetActionButtonsAsync(int characterId, CancellationToken cancellationToken = default) => _inner.GetActionButtonsAsync(characterId, cancellationToken);
        public Task<IReadOnlyList<CharacterIdentity>> GetAllIdentitiesAsync(CancellationToken cancellationToken = default) => _inner.GetAllIdentitiesAsync(cancellationToken);
    }
}
