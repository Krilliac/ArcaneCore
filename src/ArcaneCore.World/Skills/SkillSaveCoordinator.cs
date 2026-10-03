using System.Collections.Concurrent;
using ArcaneCore.Kernel.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Skills;

/// <summary>
/// Writes character skill snapshots to the store without ever waiting on the world thread, with
/// latest-snapshot-wins semantics per character: the world thread hands over a complete snapshot (a later
/// one replaces an earlier one that is still waiting), a single worker writes them with
/// <see cref="ICharacterSkillStore.ReplaceSnapshotAsync"/>, and a failed write is RETAINED and retried (it is
/// never dropped after the in-memory dirty state was cleared, and only a newer snapshot supersedes it).
/// <see cref="FlushCharacterAsync"/> and <see cref="FlushAllAsync"/> wait until nothing is pending.
/// </summary>
public sealed class SkillSaveCoordinator : IAsyncDisposable
{
    private sealed class Entry(CharacterSkillSnapshot snapshot, long version)
    {
        public CharacterSkillSnapshot Snapshot { get; } = snapshot;

        public long Version { get; } = version;
    }

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _logger;
    private readonly TimeSpan _retryDelay;
    private readonly ConcurrentDictionary<int, Entry> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _waitersLock = new();
    private readonly List<(int? CharacterId, TaskCompletionSource Done)> _waiters = [];
    private readonly Dictionary<int, Exception> _lastFailure = [];
    private Task? _worker;
    private long _version;
    private bool _warnedNoStore;
    private int _disposed;

    public SkillSaveCoordinator(IServiceScopeFactory scopes, ILogger logger, TimeSpan? retryDelay = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>Characters with a snapshot waiting or being retried.</summary>
    public int Pending => _pending.Count;

    public void Start() => _worker ??= Task.Run(RunAsync);

    /// <summary>Hand over a snapshot (any thread). A snapshot already waiting for this character is replaced.</summary>
    public void Enqueue(int characterId, CharacterSkillSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _pending[characterId] = new Entry(snapshot, Interlocked.Increment(ref _version));
        _signal.Release();
    }

    /// <summary>
    /// Drop whatever is waiting for a deleted character (its rows are gone, a late write would be refused by the
    /// store anyway) and release anyone waiting on it.
    /// </summary>
    public void Forget(int characterId)
    {
        _pending.TryRemove(characterId, out _);
        lock (_waitersLock)
        {
            _lastFailure.Remove(characterId);
        }

        CompleteWaiters();
    }

    /// <summary>
    /// Wait until nothing is pending for the character. Throws <see cref="TimeoutException"/> when the pending
    /// snapshot could not be written within <paramref name="timeout"/> (the snapshot stays queued and keeps retrying).
    /// </summary>
    public Task FlushCharacterAsync(int characterId, TimeSpan timeout) => WaitAsync(characterId, timeout);

    /// <summary>Wait until nothing is pending at all (shutdown), with the same timeout rule.</summary>
    public Task FlushAllAsync(TimeSpan timeout) => WaitAsync(null, timeout);

    /// <summary>The last write failure for a character still pending, for diagnostics and tests.</summary>
    public Exception? LastFailure(int characterId)
    {
        lock (_waitersLock)
        {
            return _lastFailure.GetValueOrDefault(characterId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return; // StopAsync disposes, and the container disposes the feature again
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        _signal.Release();
        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
        _signal.Dispose();
    }

    private async Task WaitAsync(int? characterId, TimeSpan timeout)
    {
        TaskCompletionSource done;
        lock (_waitersLock)
        {
            if (characterId is { } id ? !_pending.ContainsKey(id) : _pending.IsEmpty)
            {
                return;
            }

            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((characterId, done));
        }

        if (_worker is null)
        {
            throw new InvalidOperationException("the skill save coordinator has not been started");
        }

        try
        {
            await done.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (_waitersLock)
            {
                _waiters.RemoveAll(w => ReferenceEquals(w.Done, done));
            }

            throw;
        }
    }

    private async Task RunAsync()
    {
        CancellationToken token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Drain the semaphore: one pass handles everything queued so far.
            while (_signal.CurrentCount > 0 && _signal.Wait(0))
            {
            }

            bool anyFailed = false;
            foreach (int characterId in _pending.Keys.ToArray())
            {
                if (!_pending.TryGetValue(characterId, out Entry? entry))
                {
                    continue;
                }

                if (!await TryWriteAsync(characterId, entry).ConfigureAwait(false))
                {
                    anyFailed = true;
                }
            }

            CompleteWaiters();
            if (anyFailed && !token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_retryDelay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _signal.Release();
            }
        }

        CompleteWaiters();
    }

    private async Task<bool> TryWriteAsync(int characterId, Entry entry)
    {
        try
        {
            await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
            ICharacterSkillStore? store = scope.ServiceProvider.GetService<ICharacterSkillStore>();
            if (store is null)
            {
                if (!_warnedNoStore)
                {
                    _warnedNoStore = true;
                    _logger.LogWarning("No ICharacterSkillStore is registered; character skills are not persisted");
                }
            }
            else
            {
                await store.ReplaceSnapshotAsync(characterId, entry.Snapshot, _stop.Token).ConfigureAwait(false);
            }

            // Remove only the snapshot that was written: a newer one stays queued.
            _pending.TryRemove(new KeyValuePair<int, Entry>(characterId, entry));
            lock (_waitersLock)
            {
                _lastFailure.Remove(characterId);
            }

            return true;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            lock (_waitersLock)
            {
                _lastFailure[characterId] = ex;
            }

            _logger.LogError(ex, "Saving the skills of character {Character} failed; it is retried", characterId);
            return false;
        }
    }

    private void CompleteWaiters()
    {
        lock (_waitersLock)
        {
            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                (int? characterId, TaskCompletionSource done) = _waiters[i];
                if (characterId is { } id ? !_pending.ContainsKey(id) : _pending.IsEmpty)
                {
                    done.TrySetResult();
                    _waiters.RemoveAt(i);
                }
            }
        }
    }
}
