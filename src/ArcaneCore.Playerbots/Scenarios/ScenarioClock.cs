using ArcaneCore.Game.Maps;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>
/// A <see cref="TimeProvider"/> for scenario hosts. Registered as the host's <see cref="TimeProvider"/> it drives every
/// feature that reads wall time through it (economy mail delay and trade anti-scam window, duel countdown, unix-time
/// cooldowns ...). Once it <see cref="Follow"/>s a manual world clock it moves exactly with game time, tick by tick;
/// <see cref="Advance"/> adds a jump on top (e.g. the one-hour mail delivery delay, without simulating an hour).
/// </summary>
public sealed class ScenarioTimeProvider(DateTimeOffset start) : TimeProvider
{
    private long _extraTicks;
    private WorldRuntime? _world;
    private TimeSpan _followedFrom;

    public override DateTimeOffset GetUtcNow()
    {
        TimeSpan game = Volatile.Read(ref _world) is { } world ? world.Uptime - _followedFrom : TimeSpan.Zero;
        return start + game + TimeSpan.FromTicks(Interlocked.Read(ref _extraTicks));
    }

    /// <summary>From now on also advance with <paramref name="world"/>'s game time.</summary>
    public void Follow(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _followedFrom = world.Uptime;
        Volatile.Write(ref _world, world);
    }

    /// <summary>Jump forward without running the world.</summary>
    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(by), "time only moves forward");
        Interlocked.Add(ref _extraTicks, by.Ticks);
    }
}

/// <summary>How a scenario lets time pass while it waits.</summary>
public interface IScenarioClock
{
    /// <summary>True when game time only moves through this clock (deterministic test runs).</summary>
    bool IsManual { get; }

    /// <summary>Game time a manual clock has advanced since it was created (zero for the real clock).</summary>
    TimeSpan Advanced { get; }

    /// <summary>Let <paramref name="by"/> pass: a manual clock runs the world forward; the real clock sleeps.</summary>
    Task AdvanceAsync(TimeSpan by, CancellationToken cancellationToken = default);

    /// <summary>
    /// Manual clock: run the world tick by tick until <paramref name="condition"/> (world thread, after every tick) holds
    /// (true) or <paramref name="max"/> has passed (false). The real clock just sleeps <paramref name="max"/> and returns false.
    /// </summary>
    Task<bool> AdvanceUntilAsync(TimeSpan max, Func<bool> condition, CancellationToken cancellationToken = default);
}

/// <summary>The scenario clocks: real time (a live world) and the deterministic manual world clock (tests).</summary>
public sealed class ScenarioClock : IScenarioClock
{
    private readonly WorldRuntime? _world;
    private readonly TimeSpan _createdAt;

    private ScenarioClock(WorldRuntime? world)
    {
        _world = world;
        _createdAt = world?.Uptime ?? TimeSpan.Zero;
    }

    /// <summary>Wall time: waiting sleeps (the live server).</summary>
    public static ScenarioClock Real { get; } = new(null);

    /// <summary>
    /// The world's manual clock (<see cref="WorldRuntime.UseManualClock"/>); a given <see cref="ScenarioTimeProvider"/> is
    /// made to <see cref="ScenarioTimeProvider.Follow"/> it.
    /// </summary>
    public static ScenarioClock Manual(WorldRuntime world, ScenarioTimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.IsManualClock) throw new InvalidOperationException("the world clock is not manual (WorldRuntime.UseManualClock)");
        time?.Follow(world);
        return new ScenarioClock(world);
    }

    public bool IsManual => _world is not null;

    public TimeSpan Advanced => _world is { } world ? world.Uptime - _createdAt : TimeSpan.Zero;

    public async Task AdvanceAsync(TimeSpan by, CancellationToken cancellationToken = default)
    {
        if (by <= TimeSpan.Zero) return;
        if (_world is null)
        {
            await Task.Delay(by, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _world.AdvanceClockAsync(Milliseconds(by)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> AdvanceUntilAsync(TimeSpan max, Func<bool> condition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (_world is null)
        {
            await Task.Delay(max, cancellationToken).ConfigureAwait(false);
            return false;
        }

        return await _world.AdvanceClockUntilAsync(Milliseconds(max), condition).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static uint Milliseconds(TimeSpan span) => (uint)Math.Clamp(span.TotalMilliseconds, 0, uint.MaxValue);
}
