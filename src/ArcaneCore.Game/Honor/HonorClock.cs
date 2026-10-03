namespace ArcaneCore.Game.Honor;

/// <summary>
/// The wall clock honor is dated with. vmangos counts honor in game days (World::m_gameDay: Unix time plus the
/// configured offset, divided by a day); tests substitute a fixed clock so no test waits on real time.
/// </summary>
public class HonorClock
{
    /// <summary>The real clock.</summary>
    public static HonorClock System { get; } = new();

    /// <summary>Seconds since 1970-01-01T00:00:00Z.</summary>
    public virtual long UnixSeconds => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>The game day for the given offset in seconds.</summary>
    public uint GameDay(int timeZoneOffsetSeconds) => HonorMaintenancePlanner.GameDay(UnixSeconds, timeZoneOffsetSeconds);
}
