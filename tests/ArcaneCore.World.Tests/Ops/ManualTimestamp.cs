namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// A <see cref="TimeProvider"/> whose monotonic clock (<see cref="GetTimestamp"/>, 1 tick = 1 ms) moves only when a test says so.
/// Wall-clock time (<see cref="TimeProvider.GetUtcNow"/>) is the system's, so features that read dates are unaffected.
/// </summary>
internal sealed class ManualTimestamp : TimeProvider
{
    private long _ms = 1_000_000;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Interlocked.Read(ref _ms);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ms, (long)by.TotalMilliseconds);
}
