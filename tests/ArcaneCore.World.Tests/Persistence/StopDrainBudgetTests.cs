using ArcaneCore.World.Persistence;
using Xunit;

namespace ArcaneCore.World.Tests.Persistence;

/// <summary>
/// The stall-only accounting of the shutdown drain lane, without any queue. The lanes run on a clock the test steps
/// (<see cref="StopDrainBudget.Time"/>): with Thread.Sleep on the wall clock a loaded runner overslept, so two 60 ms failures read as one
/// exhausted 100 ms budget after the first, and 150 ms of overlap read as 245 ms (hosted CI). Stepped, every stalled time is exact.
/// </summary>
public sealed class StopDrainBudgetTests
{
    private readonly SteppedClock _clock = new();

    [Fact]
    public void NotStarted_NeverRefuses()
    {
        var lane = new StopDrainBudget { Time = _clock, StallBudget = TimeSpan.FromMilliseconds(50) };
        using (StopDrainBudget.Attempt attempt = lane.BeginAttempt())
        {
            _clock.Advance(120);
            attempt.Failed();
        }

        Assert.False(lane.Started);
        Assert.False(lane.Exhausted);
        Assert.Equal(0, lane.StalledMs);
        Assert.Null(lane.Refuse());

        lane.Begin(); // failures before Begin never pre-exhaust the lane
        Assert.Null(lane.Refuse());
        Assert.Equal(0, lane.Failed);
    }

    [Fact]
    public void IdleTimeDoesNotCount()
    {
        var lane = new StopDrainBudget { Time = _clock, StallBudget = TimeSpan.FromMilliseconds(100) };
        lane.Begin();
        _clock.Advance(250); // two and a half budgets with no attempt in flight
        Assert.Null(lane.Refuse());
        Assert.False(lane.Exhausted);
        Assert.Equal(0, lane.Refused);
    }

    [Fact]
    public void FailedInFlightTimeCounts_AndASuccessResets()
    {
        var lane = new StopDrainBudget { Time = _clock, StallBudget = TimeSpan.FromMilliseconds(100) };
        lane.Begin();

        using (StopDrainBudget.Attempt first = lane.BeginAttempt())
        {
            _clock.Advance(60);
            first.Failed();
        }

        Assert.False(lane.Exhausted);
        Assert.Null(lane.Refuse());

        using (StopDrainBudget.Attempt second = lane.BeginAttempt())
        {
            _clock.Advance(60);
            second.Failed();
        }

        Assert.True(lane.Exhausted);
        StopDrainExhaustedException? refusal = lane.Refuse();
        Assert.NotNull(refusal);
        Assert.Same(refusal, lane.LastRefusal);
        Assert.Equal(1, lane.Refused);

        using (StopDrainBudget.Attempt third = lane.BeginAttempt())
        {
            third.Succeeded();
        }

        Assert.False(lane.Exhausted);
        Assert.Null(lane.Refuse());
        Assert.Equal(1, lane.Succeeded);
        Assert.Equal(2, lane.Failed);
    }

    [Fact]
    public void InFlightAttemptDoesNotCountUntilItFails()
    {
        var lane = new StopDrainBudget { Time = _clock, StallBudget = TimeSpan.FromMilliseconds(80) };
        lane.Begin();
        using StopDrainBudget.Attempt attempt = lane.BeginAttempt();
        _clock.Advance(160);
        Assert.False(lane.Exhausted); // a running attempt is unknown until its verdict: it may be a slow write that lands
        Assert.Null(lane.Refuse());
        attempt.Failed();
        Assert.True(lane.Exhausted); // the verdict charges the wall time it covered
        Assert.NotNull(lane.Refuse());
    }

    [Fact]
    public void InFlightAttemptThatLands_NeverCounted()
    {
        var lane = new StopDrainBudget { Time = _clock, StallBudget = TimeSpan.FromMilliseconds(80) };
        lane.Begin();
        using StopDrainBudget.Attempt attempt = lane.BeginAttempt();
        _clock.Advance(160);
        Assert.Null(lane.Refuse()); // asked after the budget has passed, before the verdict
        attempt.Succeeded();
        Assert.Equal(0, lane.StalledMs);
        Assert.Null(lane.Refuse());
        Assert.Equal(0, lane.Refused);
    }

    [Fact]
    public void OverlappingFailedAttempts_CountTheirWallTimeOnce()
    {
        var lane = new StopDrainBudget { Time = _clock, StallBudget = TimeSpan.FromMilliseconds(250) };
        lane.Begin();
        StopDrainBudget.Attempt a = lane.BeginAttempt();
        StopDrainBudget.Attempt b = lane.BeginAttempt();
        _clock.Advance(150);
        a.Failed();
        b.Failed();

        Assert.Equal(150, lane.StalledMs); // 150 ms of wall time, not 2 x 150
        Assert.False(lane.Exhausted);
        Assert.Equal(2, lane.Failed);
    }

    [Fact]
    public void AFailureAfterALandingCountsOnlyTheTimeAfterIt()
    {
        var lane = new StopDrainBudget { Time = _clock, StallBudget = TimeSpan.FromMilliseconds(150) };
        lane.Begin();
        StopDrainBudget.Attempt a = lane.BeginAttempt();
        _clock.Advance(100);
        StopDrainBudget.Attempt b = lane.BeginAttempt();
        b.Succeeded();
        _clock.Advance(100);
        a.Failed();

        Assert.Equal(100, lane.StalledMs); // the 100 ms before the landing are reset by it
        Assert.False(lane.Exhausted);
    }

    [Fact]
    public void Describe_ReportsInFlight()
    {
        var lane = new StopDrainBudget { Time = _clock };
        lane.Begin();
        using StopDrainBudget.Attempt attempt = lane.BeginAttempt();
        Assert.Contains("1 in flight", lane.Describe(), StringComparison.Ordinal);
        attempt.Succeeded();
        Assert.Contains("0 in flight", lane.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void DisposeWithoutAVerdict_CountsAsFailed()
    {
        var lane = new StopDrainBudget { Time = _clock };
        lane.Begin();
        using (lane.BeginAttempt())
        {
        }

        Assert.Equal(1, lane.Failed);
        Assert.Equal(0, lane.Succeeded);
    }

    [Fact]
    public void ZeroBudget_IsExhaustedAtOnce_OnceStarted()
    {
        var lane = new StopDrainBudget { Time = _clock, StallBudget = TimeSpan.Zero };
        Assert.Null(lane.Refuse());
        lane.Begin();
        Assert.NotNull(lane.Refuse());
    }

    [Fact]
    public void AStopDrainRefusalIsNeverAVerdict()
    {
        Assert.True(StopDrainBudget.IsNoVerdict(new StopDrainExhaustedException("refused")));
        Assert.True(StopDrainBudget.IsNoVerdict(new Microsoft.Data.Sqlite.SqliteException("SQLite Error 5: 'database is locked'.", 5)));
        Assert.False(StopDrainBudget.IsNoVerdict(new IOException("store down")));
    }

    /// <summary>A Stopwatch-frequency clock that moves only when the test steps it.</summary>
    private sealed class SteppedClock : TimeProvider
    {
        private long _now = 1_000_000;

        public override long TimestampFrequency => global::System.Diagnostics.Stopwatch.Frequency;

        public override long GetTimestamp() => Interlocked.Read(ref _now);

        public void Advance(int ms) => Interlocked.Add(ref _now, ms * TimestampFrequency / 1000);
    }
}
