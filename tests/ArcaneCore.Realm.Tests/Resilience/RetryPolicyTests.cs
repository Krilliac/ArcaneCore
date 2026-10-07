using ArcaneCore.Kernel.Resilience;
using Xunit;

namespace ArcaneCore.Realm.Tests.Resilience;

public sealed class RetryPolicyTests
{
    private static RetryOptions Options(int attempts = 3, RetryJitter jitter = RetryJitter.None) => new()
    {
        MaxAttempts = attempts,
        BaseDelay = TimeSpan.FromMilliseconds(100),
        MaxDelay = TimeSpan.FromMilliseconds(400),
        Jitter = jitter,
    };

    [Theory]
    [InlineData(1, 100)]
    [InlineData(2, 200)]
    [InlineData(3, 400)]
    [InlineData(4, 400)]
    [InlineData(40, 400)]
    public void ComputeDelay_DoublesAndCaps(int attempt, int expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), RetryPolicy.ComputeDelay(Options(), attempt, 0.99));
    }

    [Fact]
    public void FullJitter_StaysWithinZeroAndTheExponentialDelay()
    {
        RetryOptions options = Options(jitter: RetryJitter.Full);
        var random = new Random(1234);
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            TimeSpan cap = RetryPolicy.ComputeDelay(Options(), attempt, 0);
            for (int i = 0; i < 1000; i++)
            {
                TimeSpan delay = RetryPolicy.ComputeDelay(options, attempt, random.NextDouble());
                Assert.InRange(delay, TimeSpan.Zero, cap);
            }
        }

        Assert.Equal(TimeSpan.Zero, RetryPolicy.ComputeDelay(options, 3, 0));
        Assert.Equal(TimeSpan.FromMilliseconds(400), RetryPolicy.ComputeDelay(options, 3, 1));
        Assert.Equal(TimeSpan.FromMilliseconds(200), RetryPolicy.ComputeDelay(options, 3, 0.5));
    }

    [Fact]
    public async Task TransientFailures_AreRetriedUpToMaxAttempts_ThenTheLastOnePropagates()
    {
        var clock = new FakeTimeProvider();
        var retries = new List<RetryAttempt>();
        var policy = new RetryPolicy(Options(3), clock, onRetry: retries.Add);
        int calls = 0;

        Task<int> run = policy.ExecuteAsync<object?, int>(
            (_, _) =>
            {
                calls++;
                return ValueTask.FromException<int>(new TimeoutException("attempt " + calls));
            },
            null).AsTask();

        await FakeClockRunner.RunUntilCompleteAsync(clock, run.ContinueWith(_ => { }, TaskScheduler.Default));
        TimeoutException last = await Assert.ThrowsAsync<TimeoutException>(() => run);
        Assert.Equal("attempt 3", last.Message);
        Assert.Equal(3, calls);
        Assert.Equal([100, 200], retries.Select(r => (int)r.Delay.TotalMilliseconds));
        Assert.Equal([1, 2], retries.Select(r => r.Attempt));
    }

    [Fact]
    public async Task EventualSuccess_ReturnsTheResult()
    {
        var clock = new FakeTimeProvider();
        var policy = new RetryPolicy(Options(5), clock);
        int calls = 0;
        Task<string> run = policy.ExecuteAsync<object?, string>(
            (_, _) => ++calls < 3 ? ValueTask.FromException<string>(new IOException()) : new ValueTask<string>("ok"),
            null).AsTask();
        await FakeClockRunner.RunUntilCompleteAsync(clock, run);
        Assert.Equal("ok", await run);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task NonTransientFailure_IsNotRetried()
    {
        var policy = new RetryPolicy(Options(5), new FakeTimeProvider());
        int calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await policy.ExecuteAsync<object?, int>((_, _) => { calls++; throw new InvalidOperationException(); }, null));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OpenCircuit_EndsTheRetriesAtOnce()
    {
        var policy = new RetryPolicy(Options(5), new FakeTimeProvider());
        int calls = 0;
        await Assert.ThrowsAsync<CircuitOpenException>(async () =>
            await policy.ExecuteAsync<object?, int>((_, _) => { calls++; return ValueTask.FromException<int>(new CircuitOpenException("x", TimeSpan.Zero)); }, null));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Cancellation_StopsTheWait()
    {
        var clock = new FakeTimeProvider();
        var policy = new RetryPolicy(Options(5), clock);
        using var cts = new CancellationTokenSource();
        Task<int> run = policy.ExecuteAsync<object?, int>((_, _) => ValueTask.FromException<int>(new TimeoutException()), null, cts.Token).AsTask();
        for (int i = 0; i < 100 && clock.PendingTimers == 0; i++)
        {
            await Task.Delay(1);
        }

        Assert.Equal(1, clock.PendingTimers); // waiting out the first delay
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TotalBudget_StopsARetryWhoseDelayWouldCrossIt()
    {
        var clock = new FakeTimeProvider();
        RetryOptions options = Options(10);
        options.MaxTotalDuration = TimeSpan.FromMilliseconds(250); // 100 + 200 > 250: only one retry fits
        var policy = new RetryPolicy(options, clock);
        int calls = 0;
        Task<int> run = policy.ExecuteAsync<object?, int>((_, _) => { calls++; return ValueTask.FromException<int>(new TimeoutException()); }, null).AsTask();
        await FakeClockRunner.RunUntilCompleteAsync(clock, run.ContinueWith(_ => { }, TaskScheduler.Default));
        await Assert.ThrowsAsync<TimeoutException>(() => run);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ExecuteUntil_PollsAtTheFixedInterval_AndMakesTheLastPollAtTheDeadline()
    {
        var clock = new FakeTimeProvider();
        var options = new RetryOptions
        {
            MaxAttempts = int.MaxValue,
            BaseDelay = TimeSpan.FromMilliseconds(250),
            MaxDelay = TimeSpan.FromMilliseconds(250),
            Jitter = RetryJitter.None,
            MaxTotalDuration = TimeSpan.FromMilliseconds(600),
        };
        var policy = new RetryPolicy(options, clock);
        var pollTimes = new List<long>();
        Task<bool> run = policy.ExecuteUntilAsync<object?>((_, _) => { pollTimes.Add(clock.GetTimestamp() / TimeSpan.TicksPerMillisecond); return new ValueTask<bool>(false); }, null).AsTask();
        await FakeClockRunner.RunUntilCompleteAsync(clock, run);
        Assert.False(await run);
        Assert.Equal([0, 250, 500, 600], pollTimes);
    }

    [Fact]
    public async Task ExecuteUntil_ReturnsTrueAsSoonAsThePollSucceeds()
    {
        var clock = new FakeTimeProvider();
        var policy = new RetryPolicy(new RetryOptions { MaxAttempts = 10, BaseDelay = TimeSpan.FromMilliseconds(10), MaxDelay = TimeSpan.FromMilliseconds(10), Jitter = RetryJitter.None }, clock);
        int polls = 0;
        Task<bool> run = policy.ExecuteUntilAsync<object?>((_, _) => new ValueTask<bool>(++polls == 3), null).AsTask();
        await FakeClockRunner.RunUntilCompleteAsync(clock, run);
        Assert.True(await run);
        Assert.Equal(3, polls);
    }

    [Fact]
    public void FirstAttemptSuccess_CompletesSynchronously_WithoutAllocation()
    {
        var policy = new RetryPolicy(Options(3), new FakeTimeProvider());
        static ValueTask<int> Op(int x, CancellationToken _) => new(x);
        Func<int, CancellationToken, ValueTask<int>> op = Op;
        for (int i = 0; i < 2000; i++)
        {
            _ = policy.ExecuteAsync(op, i);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            ValueTask<int> vt = policy.ExecuteAsync(op, i);
            Assert.True(vt.IsCompletedSuccessfully);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void InvalidOptions_AreRefused()
    {
        Assert.Throws<ArgumentException>(() => new RetryPolicy(new RetryOptions { MaxAttempts = 0 }));
        Assert.Throws<ArgumentException>(() => new RetryPolicy(new RetryOptions { BaseDelay = TimeSpan.FromSeconds(10), MaxDelay = TimeSpan.FromSeconds(1) }));
    }
}
