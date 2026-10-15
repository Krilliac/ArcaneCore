using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// Off-world-thread writer for state a feature keeps authoritatively in memory (a saved variable, a stage, a counter): the
/// world thread records the latest value per key and returns; one background drain writes the values in arrival order of
/// their keys, the newest value of a key replacing an older one still waiting (each write stores the whole value). vmangos
/// does the same with its saved variables: memory is the truth and <c>ObjectMgr::_SaveVariable</c> queues the row through
/// the asynchronous <c>WorldDatabase.PExecute</c>. A failed write is logged and the value stays the in-memory truth; the
/// key's next change writes again. <see cref="FlushAsync"/> waits for every queued value (the feature's StopAsync).
/// </summary>
internal sealed class StateSaveQueue<TKey, TState>(Func<TState, Task> save, ILogger? logger, string what)
    where TKey : notnull
{
    private readonly Lock _gate = new();
    private readonly Dictionary<TKey, TState> _pending = [];
    private readonly List<TKey> _order = [];
    private Task _drain = Task.CompletedTask;
    private bool _draining;

    /// <summary>Writes completed (diagnostics, tests).</summary>
    public long Written => Interlocked.Read(ref _written);

    private long _written;

    /// <summary>Queue <paramref name="state"/> as the value to store for <paramref name="key"/>; never blocks on storage.</summary>
    public void Save(TKey key, TState state)
    {
        lock (_gate)
        {
            if (!_pending.ContainsKey(key)) _order.Add(key);
            _pending[key] = state;
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

    private async Task DrainAsync()
    {
        while (true)
        {
            TState[] batch;
            lock (_gate)
            {
                if (_order.Count == 0)
                {
                    _draining = false;
                    return;
                }

                batch = new TState[_order.Count];
                for (int i = 0; i < _order.Count; i++) batch[i] = _pending[_order[i]];
                _order.Clear();
                _pending.Clear();
            }

            foreach (TState state in batch) await Write(state).ConfigureAwait(false);
        }
    }

    private async Task Write(TState state)
    {
        try
        {
            await save(state).ConfigureAwait(false);
            Interlocked.Increment(ref _written);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Record($"state-save-queue:{what}", ex);
            logger?.LogError(ex, "could not save the {What} state", what);
        }
    }
}
