using ArcaneCore.Kernel.Resilience;
using Xunit;

namespace ArcaneCore.Realm.Tests.Resilience;

/// <summary>The breaker's state machine under a fake clock: trips, open period, half-open probe, recovery, concurrency, allocation.</summary>
public sealed class CircuitBreakerTests
{
    private static readonly TimeSpan Open = TimeSpan.FromSeconds(10);

    private static CircuitBreakerOptions Consecutive(int threshold = 3) => new()
    {
        Name = "test",
        FailureThreshold = threshold,
        FailureRateThreshold = 0, // rate trip off: these tests pin the consecutive trip
        OpenDuration = Open,
    };

    private static readonly Func<int, int> Succeed = static x => x;
    private static readonly Func<int, int> Fail = static _ => throw new TimeoutException("boom");

    [Fact]
    public void ConsecutiveFailures_OpenTheCircuit_AndRefuseWithRetryAfter()
    {
        var clock = new FakeTimeProvider();
        var changes = new List<CircuitStateChange>();
        var breaker = new CircuitBreaker(Consecutive(3), clock, onStateChange: changes.Add);

        Assert.Throws<TimeoutException>(() => breaker.Execute(Fail, 0));
        Assert.Throws<TimeoutException>(() => breaker.Execute(Fail, 0));
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Throws<TimeoutException>(() => breaker.Execute(Fail, 0));
        Assert.Equal(CircuitState.Open, breaker.State);

        clock.Advance(TimeSpan.FromSeconds(4));
        CircuitOpenException refused = Assert.Throws<CircuitOpenException>(() => breaker.Execute(Succeed, 1));
        Assert.Equal("test", refused.CircuitName);
        Assert.Equal(TimeSpan.FromSeconds(6), refused.RetryAfter);
        Assert.Equal(1, breaker.Rejected);

        CircuitStateChange change = Assert.Single(changes);
        Assert.Equal((CircuitState.Closed, CircuitState.Open), (change.From, change.To));
        Assert.IsType<TimeoutException>(change.Cause);
    }

    [Fact]
    public void SuccessResetsTheConsecutiveCount()
    {
        var breaker = new CircuitBreaker(Consecutive(3), new FakeTimeProvider());
        for (int round = 0; round < 5; round++)
        {
            Assert.Throws<TimeoutException>(() => breaker.Execute(Fail, 0));
            Assert.Throws<TimeoutException>(() => breaker.Execute(Fail, 0));
            Assert.Equal(7, breaker.Execute(Succeed, 7));
        }

        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(0, breaker.ConsecutiveFailures);
    }

    [Fact]
    public void HalfOpen_AdmitsOneProbe_SuccessCloses()
    {
        var clock = new FakeTimeProvider();
        var changes = new List<CircuitStateChange>();
        var breaker = new CircuitBreaker(Consecutive(1), clock, onStateChange: changes.Add);
        Assert.Throws<TimeoutException>(() => breaker.Execute(Fail, 0));
        Assert.Equal(CircuitState.Open, breaker.State);

        clock.Advance(Open - TimeSpan.FromTicks(1));
        Assert.False(breaker.TryEnter(out TimeSpan wait));
        Assert.Equal(TimeSpan.FromTicks(1), wait);

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(breaker.TryEnter(out _)); // the probe
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        Assert.False(breaker.TryEnter(out wait)); // a second caller while the probe is in flight
        Assert.Equal(TimeSpan.Zero, wait);

        breaker.OnSuccess();
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(0, breaker.ConsecutiveFailures);
        Assert.Equal([CircuitState.Open, CircuitState.HalfOpen, CircuitState.Closed], changes.Select(c => c.To));
    }

    [Fact]
    public void HalfOpen_ProbeFailure_ReopensForAFullPeriod()
    {
        var clock = new FakeTimeProvider();
        var breaker = new CircuitBreaker(Consecutive(1), clock);
        Assert.Throws<TimeoutException>(() => breaker.Execute(Fail, 0));

        clock.Advance(Open);
        Assert.Throws<TimeoutException>(() => breaker.Execute(Fail, 0)); // the probe fails
        Assert.Equal(CircuitState.Open, breaker.State);

        clock.Advance(Open - TimeSpan.FromSeconds(1));
        CircuitOpenException refused = Assert.Throws<CircuitOpenException>(() => breaker.Execute(Succeed, 0));
        Assert.Equal(TimeSpan.FromSeconds(1), refused.RetryAfter);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(5, breaker.Execute(Succeed, 5));
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public void HalfOpen_NeutralOutcome_ReleasesTheProbeSlot()
    {
        var clock = new FakeTimeProvider();
        var breaker = new CircuitBreaker(Consecutive(1), clock);
        Assert.Throws<TimeoutException>(() => breaker.Execute(Fail, 0));
        clock.Advance(Open);

        Assert.True(breaker.TryEnter());
        Assert.False(breaker.TryEnter());
        breaker.OnNeutral(); // the probe was cancelled by its caller: nothing learned
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        Assert.True(breaker.TryEnter()); // the slot is free again
    }

    [Fact]
    public void FailureRate_TripsOnlyAfterMinimumThroughput_AndForgetsOldWindow()
    {
        var clock = new FakeTimeProvider();
        var options = new CircuitBreakerOptions
        {
            Name = "rate",
            FailureThreshold = 0,
            FailureRateThreshold = 0.5,
            MinimumThroughput = 4,
            SamplingWindow = TimeSpan.FromSeconds(10),
            SamplingBuckets = 10,
            OpenDuration = Open,
        };
        var breaker = new CircuitBreaker(options, clock);

        // 1 success, 2 failures: 2/3 failed but only 3 calls, below the minimum throughput.
        breaker.Execute(Succeed, 0);
        Assert.Throws<TimeoutException>(() => breaker.Execute(Fail, 0));
        Assert.Throws<TimeoutException>(() => breaker.Execute(Fail, 0));
        Assert.Equal(CircuitState.Closed, breaker.State);

        // The window slides past those three before the next failure: 1 of 1 in the window, still below the minimum.
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Throws<TimeoutException>(() => breaker.Execute(Fail, 0));
        Assert.Equal(CircuitState.Closed, breaker.State);

        // Within one window: F S S = 3 calls (1 failed, still below the minimum), then F makes it 2 of 4 = 50 %: trips.
        breaker.Execute(Succeed, 0);
        breaker.Execute(Succeed, 0);
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Throws<TimeoutException>(() => breaker.Execute(Fail, 0));
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public void NonFailureExceptions_PassThroughWithoutCounting()
    {
        var breaker = new CircuitBreaker(Consecutive(1), new FakeTimeProvider(), isFailure: static ex => ex is TimeoutException);
        Assert.Throws<InvalidOperationException>(() => breaker.Execute<int, int>(static _ => throw new InvalidOperationException("business rule"), 0));
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(0, breaker.ConsecutiveFailures);
    }

    [Fact]
    public async Task CallerCancellation_IsNeutral_AndBreakerCancellationCountsAsFailureOnlyWhenNotRequested()
    {
        var breaker = new CircuitBreaker(Consecutive(1), new FakeTimeProvider());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await breaker.ExecuteAsync<int, int>(static (_, ct) => ValueTask.FromCanceled<int>(ct), 0, cts.Token));
        Assert.Equal(CircuitState.Closed, breaker.State);

        // An OperationCanceledException nobody asked for (a provider's internal timeout) is a failure under the default classifier.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await breaker.ExecuteAsync<int, int>(static (_, _) => ValueTask.FromException<int>(new TaskCanceledException()), 0, CancellationToken.None));
        Assert.Equal(CircuitState.Closed, breaker.State); // default classifier: OperationCanceledException is never a failure
        Assert.Throws<TimeoutException>(() => breaker.Execute(Fail, 0));
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public async Task ExecuteAsync_RefusalIsAFaultedValueTask_NotASynchronousThrow()
    {
        var breaker = new CircuitBreaker(Consecutive(1), new FakeTimeProvider());
        Assert.Throws<TimeoutException>(() => breaker.Execute(Fail, 0));

        ValueTask<int> refused = breaker.ExecuteAsync<int, int>(static (x, _) => new ValueTask<int>(x), 1);
        Assert.True(refused.IsFaulted);
        await Assert.ThrowsAsync<CircuitOpenException>(async () => await refused);
    }

    [Fact]
    public async Task ExecuteAsync_AsynchronousOutcomes_AreCounted()
    {
        var breaker = new CircuitBreaker(Consecutive(2), new FakeTimeProvider());
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask<int> pending = breaker.ExecuteAsync(static (g, _) => new ValueTask<int>(g.Task), gate, CancellationToken.None);
        Assert.False(pending.IsCompleted);
        gate.SetException(new IOException("reset"));
        await Assert.ThrowsAsync<IOException>(async () => await pending);
        Assert.Equal(1, breaker.ConsecutiveFailures);

        var ok = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask<int> pendingOk = breaker.ExecuteAsync(static (g, _) => new ValueTask<int>(g.Task), ok, CancellationToken.None);
        ok.SetResult(9);
        Assert.Equal(9, await pendingOk);
        Assert.Equal(0, breaker.ConsecutiveFailures);
    }

    [Fact]
    public void IsolateAndReset_AreManualOverrides()
    {
        var breaker = new CircuitBreaker(Consecutive(3), new FakeTimeProvider());
        breaker.Isolate();
        Assert.Equal(CircuitState.Isolated, breaker.State);
        Assert.False(breaker.TryEnter(out TimeSpan wait));
        Assert.Equal(Timeout.InfiniteTimeSpan, wait);
        breaker.Reset();
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(3, breaker.Execute(Succeed, 3));
    }

    [Fact]
    public void ConcurrentCallers_NeverCorruptTheStateMachine()
    {
        var clock = new FakeTimeProvider();
        var breaker = new CircuitBreaker(Consecutive(50), clock);
        int executed = 0;
        int refused = 0;
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            for (int i = 0; i < 5000; i++)
            {
                try
                {
                    breaker.Execute(static x => x % 3 == 0 ? throw new TimeoutException() : x, i);
                    Interlocked.Increment(ref executed);
                }
                catch (TimeoutException)
                {
                    Interlocked.Increment(ref executed);
                }
                catch (CircuitOpenException)
                {
                    Interlocked.Increment(ref refused);
                }
            }
        });

        Assert.Equal(8 * 5000, executed + refused);
        Assert.Equal(refused, breaker.Rejected);
        Assert.True(breaker.State is CircuitState.Closed or CircuitState.Open);
    }

    [Fact]
    public void ConcurrentCallers_HalfOpen_AdmitsExactlyMaxProbes()
    {
        var clock = new FakeTimeProvider();
        var options = Consecutive(1);
        options.HalfOpenMaxProbes = 2;
        var breaker = new CircuitBreaker(options, clock);
        Assert.Throws<TimeoutException>(() => breaker.Execute(Fail, 0));
        clock.Advance(Open);

        int admitted = 0;
        using var start = new ManualResetEventSlim();
        Thread[] threads = [.. Enumerable.Range(0, 16).Select(_ => new Thread(() =>
        {
            start.Wait();
            if (breaker.TryEnter())
            {
                Interlocked.Increment(ref admitted);
            }
        }))];
        foreach (Thread t in threads)
        {
            t.Start();
        }

        start.Set();
        foreach (Thread t in threads)
        {
            t.Join();
        }

        Assert.Equal(2, admitted);
        Assert.Equal(14, breaker.Rejected);
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
    }

    [Fact]
    public void ClosedFastPath_AllocatesNothing()
    {
        var breaker = new CircuitBreaker(Consecutive(3), new FakeTimeProvider());
        static ValueTask<int> Op(int x, CancellationToken _) => new(x);
        Func<int, CancellationToken, ValueTask<int>> asyncOp = Op;

        // Warm up (JIT, static lambda caches, tiered compilation) before measuring.
        for (int i = 0; i < 2000; i++)
        {
            breaker.Execute(Succeed, i);
            _ = breaker.ExecuteAsync(asyncOp, i); // completes synchronously; nothing to await
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            breaker.Execute(Succeed, i);
            ValueTask<int> vt = breaker.ExecuteAsync(asyncOp, i);
            Assert.True(vt.IsCompletedSuccessfully);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void InvalidOptions_AreRefusedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new CircuitBreaker(new CircuitBreakerOptions { FailureThreshold = 0, FailureRateThreshold = 0 }));
        Assert.Throws<ArgumentException>(() => new CircuitBreaker(new CircuitBreakerOptions { OpenDuration = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new CircuitBreaker(new CircuitBreakerOptions { FailureRateThreshold = 1.5 }));
        Assert.Throws<ArgumentException>(() => new CircuitBreaker(new CircuitBreakerOptions { HalfOpenMaxProbes = 0 }));
    }
}
