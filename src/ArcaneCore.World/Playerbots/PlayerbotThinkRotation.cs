namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The fair rotation of the bots' thinks over ticks. A tick thinks for as many running bots as its budget lets it (the shared
/// action budget, World:Playerbots:MaxActionsPerTick, and the 8 ms cap of <see cref="ManagedPlayerbotFeature"/>), starting at the
/// cursor; the next tick starts right after the last bot this one visited.
/// <para>
/// Before (scale campaign 2026-10-08), the cursor moved one bot per tick whatever the tick visited. A tick that visits K of N bots
/// then visits bot j in K consecutive ticks and skips it for the next N - K: at 300 bots and about 30 thinks per tick, every bot
/// went about 13.5 s without a think. A ghost on its way to a spirit healer measures "not closing" in world time
/// (<see cref="PlayerbotRecovery.StuckMs"/>, 10 s), so after such a gap it faulted with
/// <see cref="PlayerbotRecovery.SpiritHealerFailed"/> and was quarantined, then disabled: 117 faults in 4 minutes at 300 bots
/// live, none at 200. Advancing by the visited count bounds the gap at ceil(N / K) ticks.
/// </para>
/// </summary>
internal sealed class PlayerbotThinkRotation
{
    private int _cursor;

    /// <summary>The first bot the next tick visits (tests).</summary>
    internal int Cursor => _cursor;

    /// <summary>
    /// One tick over <paramref name="count"/> bots: <paramref name="visit"/> is called with each index from the cursor on, wrapping,
    /// until it returns false (the budget or the time cap ran out before that bot: it is not counted as visited) or every bot was
    /// visited once. Returns the number visited.
    /// </summary>
    internal int Tick(int count, Func<int, bool> visit)
    {
        if (count <= 0)
        {
            _cursor = 0;
            return 0;
        }

        if (_cursor >= count) _cursor %= count;
        int visited = 0;
        while (visited < count && visit((_cursor + visited) % count)) visited++;
        // A tick that could visit nobody still moves on by one, so no bot can hold the head of the rotation for good.
        _cursor = (_cursor + Math.Max(1, visited)) % count;
        return visited;
    }
}
