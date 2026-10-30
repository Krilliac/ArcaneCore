namespace ArcaneCore.World.Tests;

/// <summary>
/// Waits for a game condition on either kind of host: a manual-clock host advances game time until it holds (bounded in game
/// milliseconds, <see cref="WorldTestHost.RunUntilAsync"/>); a real-clock host polls wall time as before
/// (<see cref="WorldTestHost.WaitForAsync"/>, 10 s).
/// </summary>
internal static class GameWait
{
    /// <summary>Default game-time budget on a manual host.</summary>
    public const uint DefaultMaxGameMs = 10_000;

    /// <summary>Wait until <paramref name="condition"/> holds. On a manual host it is evaluated on the world thread.</summary>
    public static Task UntilAsync(WorldTestHost host, Func<bool> condition, string what, uint maxGameMs = DefaultMaxGameMs)
        => host.Clock.IsManual ? host.RunUntilAsync(condition, what, maxGameMs) : WorldTestHost.WaitForAsync(condition, what);

    /// <summary>Wait until <paramref name="condition"/>, evaluated on the world thread, holds.</summary>
    public static Task UntilWorldAsync(WorldTestHost host, Func<bool> condition, string what, uint maxGameMs = DefaultMaxGameMs)
        => host.Clock.IsManual ? host.RunUntilAsync(condition, what, maxGameMs) : host.WaitForWorldAsync(condition, what);
}
