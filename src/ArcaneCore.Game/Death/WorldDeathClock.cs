using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Death;

/// <summary>
/// The death clock of a <see cref="WorldRuntime"/>: the machine clock, except on the manual test clock, where Unix seconds
/// follow world time (<see cref="WorldRuntime.Uptime"/>) from the reading when the manual clock was first asked. Without this a
/// test that steps the world in virtual time waits wall-clock seconds for the corpse reclaim delay, resurrection sickness and
/// the like. On the real clock it returns exactly what <see cref="DeathClock.System"/> returns. Thread-safe: the anchor is
/// published once, immutable.
/// </summary>
public sealed class WorldDeathClock(WorldRuntime world) : DeathClock
{
    private readonly WorldRuntime _world = world ?? throw new ArgumentNullException(nameof(world));
    private Anchor? _anchor;

    public override long UnixSeconds
    {
        get
        {
            if (!_world.IsManualClock)
            {
                return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }

            Anchor anchor = _anchor ?? InitAnchor();
            return anchor.UnixSeconds + (long)((_world.Uptime.TotalMilliseconds - anchor.UptimeMs) / 1000);
        }
    }

    private Anchor InitAnchor()
    {
        var fresh = new Anchor(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), _world.Uptime.TotalMilliseconds);
        return Interlocked.CompareExchange(ref _anchor, fresh, null) ?? fresh;
    }

    private sealed record Anchor(long UnixSeconds, double UptimeMs);
}
