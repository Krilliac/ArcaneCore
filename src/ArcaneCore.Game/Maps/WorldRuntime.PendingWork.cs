using System.Diagnostics;

namespace ArcaneCore.Game.Maps;

/// <summary>Thrown from a manual-clock advance when pending work did not finish within <see cref="WorldRuntime.PendingWorkHangGuard"/> of wall time.</summary>
public sealed class PendingWorkHangException(string message) : InvalidOperationException(message);

/// <summary>
/// Background work the simulation must wait for before game time moves on (a save, a load, a path job). On the manual clock the world
/// thread holds every advancing tick while <see cref="Count"/> is not zero (it keeps draining commands and session queues as zero-diff
/// ticks); on the real clock this only counts. Thread-safe.
/// </summary>
public sealed class WorldPendingWork
{
    private readonly object _gate = new();
    private readonly List<string> _names = [];
    private readonly Action _onIdle;
    private int _count;

    internal WorldPendingWork(Action onIdle) => _onIdle = onIdle;

    /// <summary>Work registered and not yet disposed.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Register <paramref name="name"/> as pending until the returned handle is disposed (idempotent, any thread).</summary>
    public IDisposable Begin(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate)
        {
            _names.Add(name);
            _count++;
        }

        return new Handle(this, name);
    }

    /// <summary>The names of the work still pending (a snapshot).</summary>
    public string[] Names()
    {
        lock (_gate)
        {
            return [.. _names];
        }
    }

    private void End(string name)
    {
        bool idle;
        lock (_gate)
        {
            _names.Remove(name);
            idle = --_count == 0;
        }

        if (idle)
        {
            _onIdle();
        }
    }

    private sealed class Handle(WorldPendingWork owner, string name) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.End(name);
            }
        }
    }
}

public sealed partial class WorldRuntime
{
    /// <summary>Background work the manual clock waits for (see <see cref="WorldPendingWork"/>).</summary>
    public WorldPendingWork PendingWork { get; }

    /// <summary>
    /// Manual clock only: how long, in wall time, the world thread holds an advancing tick for pending work before it fails the
    /// advances with a <see cref="PendingWorkHangException"/> naming the work. Default 60 s.
    /// </summary>
    public TimeSpan PendingWorkHangGuard { get; set; } = TimeSpan.FromSeconds(60);

    private long _heldSinceTimestamp; // world thread only; 0 = not holding
    private bool _manualHeld;         // world thread only: the tick just chosen was held for pending work

    /// <summary>Manual clock (world thread): if pending work holds an advancing tick, return 0 (a zero-diff tick) and arm or trip the hang guard.</summary>
    private uint HoldForPendingWork(uint diff)
    {
        _manualHeld = false;
        if (diff == 0 || PendingWork.Count == 0)
        {
            _heldSinceTimestamp = 0;
            return diff;
        }

        long now = Stopwatch.GetTimestamp();
        if (_heldSinceTimestamp == 0)
        {
            _heldSinceTimestamp = now;
        }
        else if (now - _heldSinceTimestamp >= PendingWorkHangGuard.TotalSeconds * Stopwatch.Frequency)
        {
            var failure = new PendingWorkHangException(
                $"pending work did not finish within {PendingWorkHangGuard.TotalSeconds:F1} s of wall time while the manual clock waited at {Clock.ElapsedMs} ms (tick {Clock.TickNumber}): {string.Join(", ", PendingWork.Names())}");
            foreach (ManualAdvance advance in _manualAdvances)
            {
                advance.Done.TrySetException(failure);
            }

            _manualAdvances.Clear();
            _heldSinceTimestamp = 0;
            return 0;
        }

        _manualHeld = true;
        return 0;
    }
}
