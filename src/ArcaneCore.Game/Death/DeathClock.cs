namespace ArcaneCore.Game.Death;

/// <summary>
/// The wall clock death state is measured with: Unix seconds, like vmangos <c>time(nullptr)</c>
/// (Player::m_deathExpireTime, Corpse::m_time; Player.cpp:20190-20193, MiscHandler.cpp:588).
/// One clock serves the whole world, so a ghost that crosses maps keeps one consistent time base
/// (a per-map uptime counter does not). Tests substitute a fixed or stepping clock.
/// </summary>
public class DeathClock
{
    /// <summary>The real clock.</summary>
    public static DeathClock System { get; } = new();

    /// <summary>Seconds since 1970-01-01T00:00:00Z.</summary>
    public virtual long UnixSeconds => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
