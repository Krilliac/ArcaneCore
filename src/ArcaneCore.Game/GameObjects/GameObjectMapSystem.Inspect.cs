namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// The internal use/respawn state of one game object as <c>.inspect gobject</c> shows it (docs/reference/gm-commands.md). Times are
/// milliseconds from now on the owning system's clock; null when the timer is not running.
/// </summary>
public sealed record GameObjectInspection(
    long? RespawnInMs,
    uint RolledRespawnSeconds,
    long? CooldownMs,
    long? AutoCloseInSeconds,
    uint UseCount,
    long AgeMs,
    bool Tracked);

public sealed partial class GameObjectMapSystem
{
    /// <summary>Read the internal timers and counters of <paramref name="go"/> (read-only; world thread).</summary>
    public GameObjectInspection Inspect(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        long now = _clockMs;
        return new GameObjectInspection(
            RespawnRemainingMs(go),
            go.RolledRespawnSeconds,
            go.CooldownUntilMs > now ? go.CooldownUntilMs - now : null,
            go.ResetAfterSecond is { } second ? Math.Max(0L, second + 1 - (now / 1000)) : null,
            go.UseCount,
            Math.Max(0L, now - go.CreatedAtMs),
            Tracks(go));
    }
}
