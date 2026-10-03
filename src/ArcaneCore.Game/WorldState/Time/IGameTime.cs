namespace ArcaneCore.Game.WorldState.Time;

/// <summary>
/// The wall clock the world-state systems read (weather seasons, game events, the login game
/// time), injectable so tests do not depend on the machine clock. vmangos reads
/// <c>sWorld.GetGameTime()</c> and converts with <c>localtime</c>, i.e. the SERVER's local zone.
/// </summary>
public interface IGameTime
{
    DateTimeOffset UtcNow { get; }

    /// <summary>The zone "local time" means (vmangos localtime_r).</summary>
    TimeZoneInfo Zone { get; }
}

/// <summary>Extension helpers over <see cref="IGameTime"/>.</summary>
public static class GameTimeExtensions
{
    /// <summary>The current time in <see cref="IGameTime.Zone"/>.</summary>
    public static DateTimeOffset LocalNow(this IGameTime time) => TimeZoneInfo.ConvertTime(time.UtcNow, time.Zone);
}

/// <summary>The real clock in the server's local zone.</summary>
public sealed class SystemGameTime : IGameTime
{
    public static SystemGameTime Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public TimeZoneInfo Zone => TimeZoneInfo.Local;
}

/// <summary>A settable clock for tests and tools.</summary>
public sealed class FixedGameTime(DateTimeOffset utcNow, TimeZoneInfo? zone = null) : IGameTime
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public TimeZoneInfo Zone { get; set; } = zone ?? TimeZoneInfo.Utc;
}
