using System.Data.Common;
using System.Net.Sockets;
using ArcaneCore.Kernel.Resilience;
using Xunit;

namespace ArcaneCore.Realm.Tests.Resilience;

public sealed class PolicyPipelineTests
{
    [Fact]
    public async Task RetryGoesThroughTheBreaker_AndAnOpenCircuitEndsTheRetries()
    {
        var clock = new FakeTimeProvider();
        var breaker = new CircuitBreaker(new CircuitBreakerOptions { Name = "p", FailureThreshold = 2, FailureRateThreshold = 0, OpenDuration = TimeSpan.FromSeconds(5) }, clock);
        var retry = new RetryPolicy(new RetryOptions { MaxAttempts = 10, BaseDelay = TimeSpan.FromMilliseconds(10), MaxDelay = TimeSpan.FromMilliseconds(10), Jitter = RetryJitter.None }, clock);
        ResiliencePipeline pipeline = Policy.Builder("p").WithRetry(retry).WithCircuitBreaker(breaker).Build();

        int calls = 0;
        Task<int> run = pipeline.ExecuteAsync<object?, int>((_, _) => { calls++; return ValueTask.FromException<int>(new TimeoutException()); }, null).AsTask();
        await FakeClockRunner.RunUntilCompleteAsync(clock, run.ContinueWith(_ => { }, TaskScheduler.Default));

        // Two attempts trip the breaker; the third attempt is refused by it and that refusal is final.
        await Assert.ThrowsAsync<CircuitOpenException>(() => run);
        Assert.Equal(2, calls);
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public async Task TimeoutInsideTheBreaker_CountsAsAFailure()
    {
        var clock = new FakeTimeProvider();
        var breaker = new CircuitBreaker(new CircuitBreakerOptions { Name = "t", FailureThreshold = 1, FailureRateThreshold = 0 }, clock);
        ResiliencePipeline pipeline = Policy.Builder("t").WithCircuitBreaker(breaker).WithTimeout(new TimeoutPolicy(TimeSpan.FromSeconds(1), clock)).Build();

        Task<int> run = pipeline.ExecuteAsync<object?, int>(
            (_, ct) =>
            {
                var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(() => tcs.TrySetCanceled(ct));
                return new ValueTask<int>(tcs.Task);
            },
            null).AsTask();
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutRejectedException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public async Task BulkheadIsOutermost_RejectionBypassesRetryAndBreaker()
    {
        using var bulkhead = new Bulkhead(new BulkheadOptions { MaxConcurrency = 1, MaxQueue = 0 });
        var breaker = new CircuitBreaker(new CircuitBreakerOptions { Name = "b", FailureThreshold = 1, FailureRateThreshold = 0 });
        ResiliencePipeline pipeline = Policy.Builder("b").WithBulkhead(bulkhead).WithCircuitBreaker(breaker).Build();
        Assert.True(bulkhead.TryEnter());
        await Assert.ThrowsAsync<BulkheadRejectedException>(async () => await pipeline.ExecuteAsync(static (x, _) => new ValueTask<int>(x), 1));
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(0, breaker.ConsecutiveFailures);
    }

    [Fact]
    public async Task EmptyPipeline_JustRunsTheOperation()
    {
        ResiliencePipeline pipeline = Policy.Builder("none").Build();
        Assert.Equal(3, await pipeline.ExecuteAsync(static (x, _) => new ValueTask<int>(x), 3));
        await pipeline.ExecuteAsync(static (_, _) => ValueTask.CompletedTask, 0);
    }

    [Fact]
    public void FullPipelineWithoutTimeout_FastPathAllocatesNothing()
    {
        using var bulkhead = new Bulkhead(new BulkheadOptions { MaxConcurrency = 8, MaxQueue = 8 });
        var breaker = new CircuitBreaker(new CircuitBreakerOptions { Name = "a" });
        var retry = new RetryPolicy(new RetryOptions());
        ResiliencePipeline pipeline = Policy.Builder("a").WithBulkhead(bulkhead).WithRetry(retry).WithCircuitBreaker(breaker).Build();
        static ValueTask<int> Op(int x, CancellationToken _) => new(x);
        Func<int, CancellationToken, ValueTask<int>> op = Op;
        for (int i = 0; i < 2000; i++)
        {
            _ = pipeline.ExecuteAsync(op, i);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            ValueTask<int> vt = pipeline.ExecuteAsync(op, i);
            Assert.True(vt.IsCompletedSuccessfully);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void TransientClassifier_KnowsTheUsualSuspects()
    {
        Assert.True(TransientFailure.IsTransient(new TimeoutException()));
        Assert.True(TransientFailure.IsTransient(new SocketException(10061)));
        Assert.True(TransientFailure.IsTransient(new IOException("reset")));
        Assert.True(TransientFailure.IsTransient(new InvalidOperationException("wrapped", new SocketException(10061))));
        Assert.True(TransientFailure.IsTransient(new AggregateException(new IOException())));
        Assert.True(TransientFailure.IsTransient(new TimeoutRejectedException(TimeSpan.FromSeconds(1))));
        Assert.True(TransientFailure.IsTransient(new DependencyUnavailableException("Auth database", new IOException())));
        Assert.True(TransientFailure.IsTransient(new FakeDbException(transient: true)));

        Assert.False(TransientFailure.IsTransient(new FakeDbException(transient: false)));
        Assert.False(TransientFailure.IsTransient(new InvalidOperationException("account does not exist")));
        Assert.False(TransientFailure.IsTransient(new OperationCanceledException()));
        Assert.False(TransientFailure.IsTransient(new CircuitOpenException("x", TimeSpan.Zero)));
        Assert.False(TransientFailure.IsTransient(new BulkheadRejectedException("x", 1, 0)));
        Assert.False(TransientFailure.IsTransient(new ArgumentException()));
    }

    private sealed class FakeDbException(bool transient) : DbException("db")
    {
        public override bool IsTransient => transient;
    }
}
