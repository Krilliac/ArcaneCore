namespace ArcaneCore.Game.Maps;

/// <summary>
/// A <see cref="TimeProvider"/> over a <see cref="WorldClock"/>: <see cref="GetUtcNow"/> is game UTC, timestamps are game milliseconds
/// (frequency 1000), so <see cref="TimeProvider.GetElapsedTime(long)"/> measures game time. It has no timers: game time moves only with
/// ticks, so schedule on the tick instead. Not registered as the host's <see cref="TimeProvider"/> by default (ops keep the system one).
/// </summary>
public sealed class GameTimeProvider(WorldClock clock) : TimeProvider
{
    private readonly WorldClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public override DateTimeOffset GetUtcNow() => _clock.UtcNow;

    public override long GetTimestamp() => _clock.ElapsedMs;

    public override long TimestampFrequency => 1000;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => throw new NotSupportedException("game time has no timers; schedule the work on the world tick");
}
