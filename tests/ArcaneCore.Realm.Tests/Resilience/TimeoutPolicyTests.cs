using ArcaneCore.Kernel.Resilience;
using Xunit;

namespace ArcaneCore.Realm.Tests.Resilience;

public sealed class TimeoutPolicyTests
{
    [Fact]
    public async Task Cooperative_CancelsTheOperationAtTheDeadline_AndReportsTimeout()
    {
        var clock = new FakeTimeProvider();
        var policy = new TimeoutPolicy(TimeSpan.FromSeconds(2), clock);
        CancellationToken seen = default;
        Task<int> run = policy.ExecuteAsync<object?, int>(
            (_, ct) =>
            {
                seen = ct;
                var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(() => tcs.TrySetCanceled(ct));
                return new ValueTask<int>(tcs.Task);
            },
            null).AsTask();

        Assert.False(run.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1.999));
        Assert.False(seen.IsCancellationRequested);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(seen.IsCancellationRequested);
        TimeoutRejectedException ex = await Assert.ThrowsAsync<TimeoutRejectedException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(2), ex.Timeout);
    }

    [Fact]
    public async Task Cooperative_CallerCancellation_IsNotATimeout()
    {
        var policy = new TimeoutPolicy(TimeSpan.FromSeconds(30), new FakeTimeProvider());
        using var cts = new CancellationTokenSource();
        Task<int> run = policy.ExecuteAsync<object?, int>(
            (_, ct) =>
            {
                var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(() => tcs.TrySetCanceled(ct));
                return new ValueTask<int>(tcs.Task);
            },
            null, cts.Token).AsTask();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task FastOperation_PassesThrough()
    {
        var policy = new TimeoutPolicy(TimeSpan.FromSeconds(1), new FakeTimeProvider());
        Assert.Equal(5, await policy.ExecuteAsync(static (x, _) => new ValueTask<int>(x), 5));
        await policy.ExecuteAsync(static (_, _) => ValueTask.CompletedTask, 0);
    }

    [Fact]
    public async Task Pessimistic_AbandonsAnOperationThatIgnoresItsToken()
    {
        var clock = new FakeTimeProvider();
        var policy = new TimeoutPolicy(TimeSpan.FromMilliseconds(500), clock, TimeoutStrategy.Pessimistic);
        using var release = new ManualResetEventSlim();
        Task<int> run = policy.ExecuteAsync<ManualResetEventSlim, int>(
            static (gate, _) =>
            {
                gate.Wait(); // blocks and never looks at the token, like a synchronous provider
                return new ValueTask<int>(1);
            },
            release).AsTask();

        for (int i = 0; i < 100 && clock.PendingTimers < 2; i++)
        {
            await Task.Delay(1); // the deadline timer and the Task.Delay(infinite) expiry must both be armed
        }

        clock.Advance(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAsync<TimeoutRejectedException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        release.Set(); // the abandoned work finishes on its own; nothing observes it
    }

    [Fact]
    public void InvalidTimeout_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeoutPolicy(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeoutPolicy(TimeSpan.FromDays(2)));
    }
}
