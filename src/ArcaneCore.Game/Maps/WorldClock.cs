using System.Diagnostics;

namespace ArcaneCore.Game.Maps;

/// <summary>How a <see cref="WorldClock"/> moves.</summary>
public enum WorldClockMode
{
    /// <summary>Production: the world's stopwatch, snapshotted once at the start of every tick.</summary>
    Realtime,

    /// <summary>Test seam (<see cref="WorldRuntime.UseManualClock"/>): game time advances only through <see cref="WorldRuntime.AdvanceClockAsync"/>.</summary>
    Manual,

    /// <summary>Opt-in (<see cref="WorldRuntime.UseSteppedClock"/>): game time advances by exactly the diff of each <see cref="WorldRuntime.RunTick(uint)"/>.</summary>
    Stepped,
}

/// <summary>
/// The one game clock of a <see cref="WorldRuntime"/>. Every reading is the value at the start of the current tick, so two reads
/// inside a tick agree. Written by the world thread only (<see cref="BeginTick"/>); readable from any thread.
/// <see cref="NowMs"/> wraps like vmangos WorldTimer::getMSTime; <see cref="ElapsedMs"/> does not.
/// </summary>
public sealed class WorldClock
{
    private const long TicksPerMs = TimeSpan.TicksPerMillisecond;
    private const long DriftToleranceTicks = TimeSpan.TicksPerSecond;
    private const long DriftCheckEveryMs = 1000;

    private readonly Stopwatch _stopwatch;
    private long _elapsedMs;          // tick-start game ms (Manual/Stepped: the running sum; Realtime: the stopwatch snapshot)
    private long _tickNumber;
    private long _utcOffsetTicks;     // UTC ticks at game time zero, so UtcNow = offset + ElapsedMs * TicksPerMs (one field: re-anchoring is one write)
    private long _lastDriftCheckMs;   // Realtime only; world thread
    private uint _diffMs;
    private int _mode;

    internal WorldClock(Stopwatch stopwatch)
    {
        _stopwatch = stopwatch;
        _utcOffsetTicks = DateTime.UtcNow.Ticks - stopwatch.ElapsedMilliseconds * TicksPerMs;
    }

    /// <summary>How this clock moves; fixed before the world starts.</summary>
    public WorldClockMode Mode => (WorldClockMode)Volatile.Read(ref _mode);

    /// <summary>Whether game time is a function of the ticks alone (every mode but <see cref="WorldClockMode.Realtime"/>).</summary>
    public bool IsDeterministic => Mode != WorldClockMode.Realtime;

    /// <summary>Ticks begun so far (every <see cref="WorldRuntime.RunTick(uint)"/>, zero-diff ones included).</summary>
    public long TickNumber => Volatile.Read(ref _tickNumber);

    /// <summary>
    /// Game milliseconds at the start of the current tick, not wrapping. On the real clock a live stopwatch read until the
    /// first tick has begun.
    /// </summary>
    public long ElapsedMs => Mode == WorldClockMode.Realtime && Volatile.Read(ref _tickNumber) == 0
        ? _stopwatch.ElapsedMilliseconds
        : Volatile.Read(ref _elapsedMs);

    /// <summary>The diff of the current tick (0 before the first).</summary>
    internal uint DiffMs => _diffMs;

    /// <summary><see cref="ElapsedMs"/> wrapped to 32 bits like vmangos WorldTimer::getMSTime (movement timestamps, create blocks).</summary>
    public uint NowMs => unchecked((uint)ElapsedMs);

    /// <summary>
    /// <see cref="NowMs"/> read live: on the real clock the stopwatch right now (it moves inside a tick, wrapping like <see cref="NowMs"/>), in the
    /// deterministic modes the tick-start game time. For measuring wall time spent inside one tick (think gaps and stalls), never for game logic.
    /// </summary>
    public uint LiveNowMs => Mode == WorldClockMode.Realtime ? unchecked((uint)_stopwatch.ElapsedMilliseconds) : NowMs;

    /// <summary>Game time as UTC: the anchor plus <see cref="ElapsedMs"/>.</summary>
    public DateTimeOffset UtcNow => new(Volatile.Read(ref _utcOffsetTicks) + ElapsedMs * TicksPerMs, TimeSpan.Zero);

    /// <summary><see cref="UtcNow"/> as Unix seconds.</summary>
    public long UnixSeconds => UtcNow.ToUnixTimeSeconds();

    /// <summary><see cref="UtcNow"/> as Unix milliseconds.</summary>
    public long UnixMilliseconds => UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Switch to a game-driven mode starting at <paramref name="startAtMs"/> (before the world starts).</summary>
    internal void UseGameDriven(WorldClockMode mode, long startAtMs, DateTimeOffset? startUtc)
    {
        long utcTicks = (startUtc ?? DateTimeOffset.UtcNow).UtcTicks;
        Volatile.Write(ref _utcOffsetTicks, utcTicks - startAtMs * TicksPerMs);
        Volatile.Write(ref _elapsedMs, startAtMs);
        Volatile.Write(ref _mode, (int)mode);
    }

    /// <summary>World thread only: the top of <see cref="WorldRuntime.RunTick(uint)"/>, before anything of the tick runs.</summary>
    internal void BeginTick(uint diffMs)
    {
        _diffMs = diffMs;
        if (Mode == WorldClockMode.Realtime)
        {
            long now = Math.Max(_stopwatch.ElapsedMilliseconds, Volatile.Read(ref _elapsedMs));
            Volatile.Write(ref _elapsedMs, now);
            CheckDrift(now);
        }
        else
        {
            Volatile.Write(ref _elapsedMs, Volatile.Read(ref _elapsedMs) + diffMs);
        }

        Volatile.Write(ref _tickNumber, _tickNumber + 1);
    }

    /// <summary>
    /// Real clock: once a second compare the anchored UTC with the system clock. The system clock jumping forward re-anchors at once;
    /// jumping backwards is slewed (at most a tenth of the elapsed time per check) so <see cref="UtcNow"/> never runs backwards.
    /// </summary>
    private void CheckDrift(long nowMs)
    {
        long sinceCheck = nowMs - _lastDriftCheckMs;
        if (sinceCheck < DriftCheckEveryMs)
        {
            return;
        }

        _lastDriftCheckMs = nowMs;
        long offset = Volatile.Read(ref _utcOffsetTicks);
        long drift = DateTime.UtcNow.Ticks - (offset + nowMs * TicksPerMs);
        if (drift > DriftToleranceTicks)
        {
            Volatile.Write(ref _utcOffsetTicks, offset + drift);
        }
        else if (drift < -DriftToleranceTicks)
        {
            long slew = Math.Min(-drift, sinceCheck * TicksPerMs / 10);
            Volatile.Write(ref _utcOffsetTicks, offset - slew);
        }
    }
}
