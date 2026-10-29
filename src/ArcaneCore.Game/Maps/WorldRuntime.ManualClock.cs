using System.Diagnostics;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// The manual clock (test seam, see <see cref="UseManualClock"/>): game time that advances only through <see cref="AdvanceClockAsync"/>.
/// </summary>
public sealed partial class WorldRuntime
{
    // Manual clock (test seam, see UseManualClock): game time advances only through AdvanceClockAsync.
    private bool _manualClock;
    private uint _manualStepMs;
    private long _manualTick; // world thread only
    private readonly List<ManualAdvance> _manualAdvances = []; // world thread only

    /// <summary>
    /// Milliseconds since the world started, wrapping like vmangos WorldTimer::getMSTime.
    /// This is the clock movement timestamps and create blocks carry.
    /// </summary>
    public uint NowMs => Clock.NowMs;

    /// <summary>Time since the world was created (does not wrap, unlike <see cref="NowMs"/>).</summary>
    public TimeSpan Uptime => TimeSpan.FromMilliseconds(Clock.ElapsedMs);

    /// <summary>
    /// The world's game clock: tick number, tick-start milliseconds, game UTC. <see cref="NowMs"/> and <see cref="Uptime"/> forward to it.
    /// </summary>
    public WorldClock Clock { get; }

    /// <summary>Whether <see cref="UseManualClock"/> froze the simulation clock.</summary>
    public bool IsManualClock => _manualClock;

    /// <summary>
    /// Test seam (never configuration-bound): from now on game time — <see cref="NowMs"/>, <see cref="Uptime"/> and
    /// every tick's diff — advances only through <see cref="AdvanceClockAsync"/>, in steps of at most
    /// <paramref name="stepMs"/> (default 50, vmangos WORLD_SLEEP_CONST). Queued work runs at once: <see cref="Post"/>,
    /// <see cref="InvokeAsync{T}"/>, <see cref="AdvanceClockAsync"/> and a session's packet queue wake the world thread (real clock: they
    /// never do). Otherwise the world thread ticks every <see cref="WorldRuntimeOptions.TickIntervalMs"/> of wall time with a zero diff,
    /// so sessions and <see cref="InvokeAsync{T}"/> keep working while the simulation stands still. Call before <see cref="Start"/>.
    /// </summary>
    public void UseManualClock(uint stepMs = 50) => UseManualClockCore(stepMs, startAtMs: null, startUtc: null);

    /// <summary>
    /// <see cref="UseManualClock"/> starting at <paramref name="startAtMs"/> instead of the wall-clock reading so far, for tests
    /// that compare whole runs (the reading so far depends on how long the set-up took, so every timestamp of a run would shift).
    /// </summary>
    public void UseManualClockFrom(long startAtMs, uint stepMs = 50) => UseManualClockCore(stepMs, startAtMs, startUtc: null);

    /// <summary>
    /// <see cref="UseManualClockFrom(long, uint)"/> that also pins game UTC (<see cref="WorldClock.UtcNow"/>) to
    /// <paramref name="startUtc"/> at <paramref name="startAtMs"/>, so wall-date logic is reproducible.
    /// </summary>
    public void UseManualClockFrom(long startAtMs, DateTimeOffset? startUtc, uint stepMs = 50) => UseManualClockCore(stepMs, startAtMs, startUtc);

    /// <summary>
    /// Opt-in deterministic clock for callers that drive <see cref="RunTick(uint)"/> directly (no world thread, so <see cref="Start"/> refuses):
    /// every tick advances <see cref="NowMs"/>, <see cref="Uptime"/> and game UTC by exactly its diff, from <paramref name="startAtMs"/>.
    /// Call before the first tick.
    /// </summary>
    public void UseSteppedClock(long startAtMs = 0, DateTimeOffset? startUtc = null)
    {
        if (_thread is not null || _manualClock)
        {
            throw new InvalidOperationException("the stepped clock must be chosen before the world starts and excludes the manual clock");
        }

        if (Clock.TickNumber != 0)
        {
            throw new InvalidOperationException("the stepped clock must be chosen before the first tick");
        }

        Clock.UseGameDriven(WorldClockMode.Stepped, startAtMs, startUtc);
    }

    /// <summary>
    /// Manual clock only: run exactly <paramref name="ticks"/> ticks of the configured step (a zero count is a barrier: it completes
    /// after one zero-diff tick, once everything queued before it has run). Completes on the world thread after the last tick.
    /// </summary>
    public Task AdvanceTicksAsync(int ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        if (!_manualClock)
        {
            throw new InvalidOperationException("the world clock is not manual");
        }

        long ms = (long)ticks * _manualStepMs;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ms, (long)uint.MaxValue, nameof(ticks));
        return AdvanceClockAsync((uint)ms);
    }

    private void UseManualClockCore(uint stepMs, long? startAtMs, DateTimeOffset? startUtc)
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("the manual clock must be chosen before the world starts");
        }

        if (Clock.Mode == WorldClockMode.Stepped)
        {
            throw new InvalidOperationException("the manual clock excludes the stepped clock");
        }

        if (stepMs == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stepMs), "the manual clock step must be positive");
        }

        _manualStepMs = stepMs;
        // Continue from the current reading, so values cached by features that attached earlier never run backwards.
        Clock.UseGameDriven(WorldClockMode.Manual, startAtMs ?? _clock.ElapsedMilliseconds, startUtc);
        _manualClock = true;
    }

    /// <summary>
    /// Manual clock only: run the simulation forward by <paramref name="milliseconds"/> of game time, in ticks of at
    /// most the configured step, back to back. Completes on the world thread after the last of those ticks.
    /// </summary>
    public Task AdvanceClockAsync(uint milliseconds) => AdvanceClockUntilAsync(milliseconds, null);

    /// <summary>
    /// Manual clock only: run the simulation forward tick by tick until <paramref name="condition"/> (evaluated on the
    /// world thread after every tick) holds — true — or <paramref name="maxMilliseconds"/> of game time have passed —
    /// false. Without a condition it simply advances and completes true. Concurrent advances share the ticks.
    /// </summary>
    public Task<bool> AdvanceClockUntilAsync(uint maxMilliseconds, Func<bool>? condition)
    {
        if (!_manualClock)
        {
            throw new InvalidOperationException("the world clock is not manual");
        }

        var advance = new ManualAdvance(maxMilliseconds, condition);
        Post(() =>
        {
            advance.AddedAtTick = _manualTick;
            _manualAdvances.Add(advance);
        });
        return advance.Done.Task;
    }

    private sealed class ManualAdvance(long remainingMs, Func<bool>? until)
    {
        public long Remaining = remainingMs;
        public long AddedAtTick = -1;
        public Func<bool>? Until { get; } = until;
        public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The next manual tick's diff: the configured step, cut to the largest remaining advance (0: commands only).</summary>
    private uint NextManualDiff()
    {
        _manualTick++;
        long wanted = 0;
        foreach (ManualAdvance advance in _manualAdvances)
        {
            wanted = Math.Max(wanted, advance.Remaining);
        }

        return (uint)Math.Min(wanted, _manualStepMs);
    }

    /// <summary>After a manual tick: charge it to the advances that existed before it and complete the finished ones.</summary>
    private void SettleManualAdvances(uint diff)
    {
        for (int i = _manualAdvances.Count - 1; i >= 0; i--)
        {
            ManualAdvance advance = _manualAdvances[i];
            if (advance.AddedAtTick == _manualTick)
            {
                continue; // added by a command of this very tick: it starts with the next one
            }

            advance.Remaining -= diff;
            bool reached;
            try
            {
                reached = advance.Until?.Invoke() ?? false;
            }
            catch (Exception ex)
            {
                FaultLedger.Shared.Record("world.manual-advance", ex);
                _manualAdvances.RemoveAt(i);
                advance.Done.TrySetException(ex);
                continue;
            }

            if (reached || advance.Remaining <= 0)
            {
                _manualAdvances.RemoveAt(i);
                advance.Done.TrySetResult(reached || advance.Until is null);
            }
        }
    }
}
