using ArcaneCore.Data.Characters.Cluster;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// Off-world-thread writer for state a feature keeps authoritatively in memory (a saved variable, a stage, a counter): the
/// world thread records the latest value per key and returns; one background drain writes the values in arrival order of
/// their keys, the newest value of a key replacing an older one still waiting (each write stores the whole value). vmangos
/// does the same with its saved variables: memory is the truth and <c>ObjectMgr::_SaveVariable</c> queues the row through
/// the asynchronous <c>WorldDatabase.PExecute</c>. A failed write is logged and the value stays the in-memory truth; the
/// key's next change writes again. <see cref="FlushAsync"/> waits for every queued value (the feature's StopAsync).
/// A write that fails is also RETAINED (the newest failed value per key, until a later write of the key lands or is refused by the lease): <see cref="RetryRetainedAsync"/> is what a cluster
/// handoff calls, it writes the retained values again through the same ordered drain and faults, naming the keys, while one is still not durable.
/// A store that refuses the write because this node does not hold the realm lease for it (<see cref="RealmLeaseNotHeldException"/>, the worldstate lease of the
/// durable world state) is not a fault: the value stays the in-memory truth, the holder writes the stored one, and the refusal is only counted (<see cref="Skipped"/>).
/// </summary>
internal sealed class StateSaveQueue<TKey, TState>(Func<TState, Task> save, ILogger? logger, string what)
    where TKey : notnull
{
    private readonly Lock _gate = new();
    private readonly Dictionary<TKey, TState> _pending = [];
    private readonly List<TKey> _order = [];
    private Task _drain = Task.CompletedTask;
    private bool _draining;
    private readonly Dictionary<TKey, TState> _failed = [];

    /// <summary>Keys whose newest value failed to write and is still not durable, in no particular order.</summary>
    public IReadOnlyList<TKey> RetainedKeys
    {
        get
        {
            lock (_gate)
            {
                return [.. _failed.Keys];
            }
        }
    }

    /// <summary>Writes completed (diagnostics, tests).</summary>
    public long Written => Interlocked.Read(ref _written);

    private long _written;

    /// <summary>Writes the store refused because this node is not the lease holder (diagnostics, tests).</summary>
    public long Skipped => Interlocked.Read(ref _skipped);

    private long _skipped;

    /// <summary>Queue <paramref name="state"/> as the value to store for <paramref name="key"/>; never blocks on storage.</summary>
    public void Save(TKey key, TState state)
    {
        lock (_gate)
        {
            if (!_pending.ContainsKey(key)) _order.Add(key);
            _pending[key] = state;
            _failed.Remove(key); // the newer value is the one owed now; a retry must not write the older failed one over it
            if (_draining) return;
            _draining = true;
            _drain = Task.Run(DrainAsync);
        }
    }

    /// <summary>Completes when nothing is queued or being written.</summary>
    public async Task FlushAsync()
    {
        while (true)
        {
            Task drain;
            lock (_gate)
            {
                if (!_draining) return;
                drain = _drain;
            }

            await drain.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Write every value still queued, write the retained ones again (a value the key's newer one has not replaced), and complete when none is retained; faults with
    /// <see cref="InvalidOperationException"/> naming the keys while one still is. A <see cref="Save"/> that arrives meanwhile supersedes a retained value of its key.
    /// </summary>
    public async Task RetryRetainedAsync()
    {
        await FlushAsync().ConfigureAwait(false);
        lock (_gate)
        {
            foreach ((TKey key, TState state) in _failed)
            {
                if (_pending.TryAdd(key, state)) _order.Add(key);
            }

            if (_order.Count > 0 && !_draining)
            {
                _draining = true;
                _drain = Task.Run(DrainAsync);
            }
        }

        await FlushAsync().ConfigureAwait(false);
        TKey[] left;
        lock (_gate)
        {
            left = [.. _failed.Keys];
        }

        if (left.Length > 0)
        {
            throw new InvalidOperationException($"the {what} state is not durable: {string.Join(", ", left)}");
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            TKey[] keys;
            TState[] batch;
            lock (_gate)
            {
                if (_order.Count == 0)
                {
                    _draining = false;
                    return;
                }

                keys = [.. _order];
                batch = new TState[_order.Count];
                for (int i = 0; i < _order.Count; i++) batch[i] = _pending[_order[i]];
                _order.Clear();
                _pending.Clear();
            }

            for (int i = 0; i < batch.Length; i++) await Write(keys[i], batch[i]).ConfigureAwait(false);
        }
    }

    private async Task Write(TKey key, TState state)
    {
        try
        {
            await save(state).ConfigureAwait(false);
            Interlocked.Increment(ref _written);
            lock (_gate) _failed.Remove(key);
        }
        catch (RealmLeaseNotHeldException ex)
        {
            Interlocked.Increment(ref _skipped);
            lock (_gate) _failed.Remove(key); // the holder writes the stored value; nothing here is owed
            logger?.LogDebug("the {What} state was not stored: {Reason}", what, ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Record($"worldstate.save-queue:{what}", ex);
            logger?.LogError(ex, "could not save the {What} state", what);
            lock (_gate)
            {
                if (!_pending.ContainsKey(key)) _failed[key] = state; // a newer value already waiting supersedes this one and settles the key itself
            }
        }
    }
}
