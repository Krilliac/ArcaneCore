// Ported from Krilliac/Sonder-Inference src/scheduler/include/sonder/inference/scheduler/clock.hpp @1c4c152 (Apache-2.0)
//
// Clock abstraction so scheduling is deterministic and testable without a real backend.

using System.Diagnostics;

namespace ArcaneCore.World.Playerbots.Chat.Inference.Scheduling;

public interface ISchedulerClock
{
    /// <summary>Current time in microseconds since an arbitrary epoch.</summary>
    long NowUs { get; }
}

/// <summary>
/// Manually advanced clock. Time only moves when the owner calls <see cref="Advance"/>, so
/// repeated simulations produce bit-identical results.
/// </summary>
public sealed class SimClock(long startUs = 0) : ISchedulerClock
{
    private long _now = startUs;

    public long NowUs => _now;

    public void Advance(long deltaUs)
    {
        if (deltaUs > 0)
        {
            _now += deltaUs;
        }
    }

    public void AdvanceTo(long timeUs)
    {
        if (timeUs > _now)
        {
            _now = timeUs;
        }
    }
}

/// <summary>Monotonic wall clock for the live engine (Stopwatch ticks, no allocation).</summary>
public sealed class SteadyClock : ISchedulerClock
{
    private readonly long _origin = Stopwatch.GetTimestamp();

    public long NowUs => Stopwatch.GetElapsedTime(_origin).Ticks / TimeSpan.TicksPerMicrosecond;
}
