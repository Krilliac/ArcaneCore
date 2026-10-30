namespace ArcaneCore.Game.Maps;

/// <summary>
/// A <see cref="TimeProvider"/> over a <see cref="WorldClock"/>: <see cref="GetUtcNow"/> is game UTC, timestamps are game milliseconds
/// (frequency 1000), so <see cref="TimeProvider.GetElapsedTime(long)"/> measures game time. Timers (<see cref="TimeProvider.CreateTimer"/>) are the base ones: they run on wall time, because game time moves only with
/// ticks; game logic schedules on the tick instead. Ops keep the system provider; registering this one is a per-host choice.
/// </summary>
public sealed class GameTimeProvider(WorldClock clock) : TimeProvider
{
    private readonly WorldClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public override DateTimeOffset GetUtcNow() => _clock.UtcNow;

    public override long GetTimestamp() => _clock.ElapsedMs;

    public override long TimestampFrequency => 1000;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
