using ArcaneCore.Kernel.Realms;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Tests.RealmList;

/// <summary>A clock that moves only when the test calls <see cref="Advance"/>, firing every timer that falls due inside the step.</summary>
internal sealed class HandClock : TimeProvider
{
    public static readonly DateTimeOffset Epoch = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly Lock _gate = new();
    private readonly List<HandTimer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    public override DateTimeOffset GetUtcNow() => Epoch + TimeSpan.FromTicks(GetTimestamp());

    public DateTime UtcNow => GetUtcNow().UtcDateTime;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new HandTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        long target;
        lock (_gate)
        {
            target = _ticks + by.Ticks;
        }

        while (true)
        {
            HandTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is not null && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _ticks = target;
                    return;
                }

                _ticks = Math.Max(_ticks, next.Due!.Value);
                next.Due = next.PeriodTicks > 0 ? next.Due + next.PeriodTicks : null;
            }

            next.Fire();
        }
    }

    private sealed class HandTimer(HandClock owner, TimerCallback callback, object? state) : ITimer
    {
        public long? Due;
        public long PeriodTicks;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (!owner._timers.Contains(this))
                {
                    owner._timers.Add(this);
                }

                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._ticks + Math.Max(0, dueTime.Ticks);
                PeriodTicks = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                Due = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}

/// <summary>One realmlist row with the EF store's semantics (the EF store itself is covered in ArcaneCore.Data.Tests), recording every call.</summary>
internal sealed class RealmRowStore(RealmEntry row) : IRealmStatusStore
{
    private readonly Lock _gate = new();
    private readonly List<string> _calls = [];

    public RealmEntry Row { get; } = row;

    /// <summary>Make the named call throw this many more times (<c>"beat"</c>, <c>"state"</c>, ...).</summary>
    public Dictionary<string, int> FailNext { get; } = [];

    /// <summary>Runs inside every call, on the calling thread (to observe which thread writes).</summary>
    public Action<string>? OnCall { get; set; }

    public int Writes
    {
        get
        {
            lock (_gate)
            {
                return _calls.Count;
            }
        }
    }

    public string[] Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public Task<bool> SetStartingAsync(int realmId, DateTime heartbeatUtc, CancellationToken cancellationToken = default)
        => Change("starting", realmId, r => { r.Flags |= RealmFlags.Offline; r.State = RealmRuntimeState.Starting; r.HeartbeatUtc = heartbeatUtc; });

    public Task<bool> SetOnlineAsync(int realmId, DateTime heartbeatUtc, CancellationToken cancellationToken = default)
        => Change("online", realmId, r => { r.Flags &= ~RealmFlags.Offline; r.Population = 0f; r.State = RealmRuntimeState.Online; r.HeartbeatUtc = heartbeatUtc; });

    public Task<bool> SetStateAsync(int realmId, RealmRuntimeState state, DateTime heartbeatUtc, CancellationToken cancellationToken = default)
        => Change("state", realmId, r => { r.State = state; r.HeartbeatUtc = heartbeatUtc; });

    public Task<bool> BeatAsync(int realmId, DateTime heartbeatUtc, CancellationToken cancellationToken = default)
        => Change("beat", realmId, r => r.HeartbeatUtc = heartbeatUtc);

    public Task<bool> SetStoppedAsync(int realmId, CancellationToken cancellationToken = default)
        => Change("stopped", realmId, r => { r.Flags |= RealmFlags.Offline; r.State = RealmRuntimeState.Stopped; });

    public Task<bool> SetPopulationAsync(int realmId, float population, CancellationToken cancellationToken = default)
        => Change("population", realmId, r => r.Population = population);

    private Task<bool> Change(string call, int realmId, Action<RealmEntry> change)
    {
        OnCall?.Invoke(call);
        lock (_gate)
        {
            _calls.Add(call);
            if (FailNext.TryGetValue(call, out int remaining) && remaining > 0)
            {
                FailNext[call] = remaining - 1;
                throw new InvalidOperationException("the auth database is unavailable (test)");
            }

            if (realmId != Row.Id)
            {
                return Task.FromResult(false);
            }

            change(Row);
            return Task.FromResult(true);
        }
    }
}

/// <summary>An <see cref="ILogger"/> that keeps its lines.</summary>
internal sealed class LineLogger : ILogger
{
    private readonly Lock _gate = new();
    private readonly List<(LogLevel Level, string Text)> _lines = [];

    public (LogLevel Level, string Text)[] Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _lines.Add((logLevel, formatter(state, exception) + (exception is null ? string.Empty : " | " + exception.Message)));
        }
    }
}
