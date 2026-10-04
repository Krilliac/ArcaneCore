using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using ArcaneCore.Kernel.Resilience;
using Xunit;

namespace ArcaneCore.Realm.Tests.Resilience;

/// <summary>
/// The deadline callback versus disposal. <c>ITimer.Dispose()</c> does not wait for a callback the timer queue has
/// already dequeued, so an operation that completes at the deadline lets the callback run after
/// <see cref="TimeoutPolicy.ExecuteAsync{TState,TResult}"/> has disposed its token source. That callback must never
/// throw: it runs on a timer thread where an exception aborts the process.
/// </summary>
public sealed class TimeoutPolicyDeadlineRaceTests
{
    [Fact]
    public async Task DeadlineCallback_RunningAfterTheCallCompleted_DoesNotThrow()
    {
        var clock = new HandFiredTimeProvider();
        var policy = new TimeoutPolicy(TimeSpan.FromMilliseconds(5), clock);
        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(7, await policy.ExecuteAsync(static (x, _) => new ValueTask<int>(x), 7));

            // The call has returned and disposed its deadline; now the callback the timer queue had already dequeued runs.
            Assert.True(clock.TryTake(out HandFiredTimeProvider.HandTimer? timer), "the policy armed no timer");
            Assert.True(timer.Disposed, "the policy did not dispose its timer");
            Exception? thrown = await Task.Run(timer.Fire);
            Assert.Null(thrown);
        }
    }

    [Fact]
    public async Task DeadlineCallback_RunningAfterAPessimisticCallCompleted_DoesNotThrow()
    {
        var clock = new HandFiredTimeProvider();
        var policy = new TimeoutPolicy(TimeSpan.FromMilliseconds(5), clock, TimeoutStrategy.Pessimistic);
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(3, await policy.ExecuteAsync(static (x, _) => new ValueTask<int>(x), 3));
            while (clock.TryTake(out HandFiredTimeProvider.HandTimer? timer))
            {
                Assert.Null(await Task.Run(timer.Fire));
            }
        }
    }

    [Fact]
    public async Task Stress_CompletionsRacingTheDeadline_RaiseNoExceptionOnTheTimerThread()
    {
        var firstChance = new ConcurrentQueue<string>();
        var unobserved = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> onFirstChance = (_, e) =>
        {
            if (e.Exception is ObjectDisposedException)
            {
                string stack = Environment.StackTrace;
                if (stack.Contains(nameof(TimeoutPolicy), StringComparison.Ordinal))
                {
                    firstChance.Enqueue(e.Exception.Message + Environment.NewLine + stack);
                }
            }
        };
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, e) =>
        {
            if (e.Exception.ToString().Contains(nameof(TimeoutPolicy), StringComparison.Ordinal))
            {
                unobserved.Enqueue(e.Exception);
            }
        };

        AppDomain.CurrentDomain.FirstChanceException += onFirstChance;
        TaskScheduler.UnobservedTaskException += onUnobserved;
        try
        {
            TimeSpan deadline = TimeSpan.FromMilliseconds(2);
            var cooperative = new TimeoutPolicy(deadline);
            var pessimistic = new TimeoutPolicy(deadline, strategy: TimeoutStrategy.Pessimistic);
            int completed = 0, timedOut = 0;
            Task[] workers = new Task[64];
            for (int w = 0; w < workers.Length; w++)
            {
                int seed = w;
                workers[w] = Task.Run(async () =>
                {
                    var random = new Random(seed);
                    for (int i = 0; i < 300; i++)
                    {
                        TimeoutPolicy policy = (i & 1) == 0 ? cooperative : pessimistic;
                        int delay = random.Next(0, 4); // completes around the 2 ms deadline, on either side of it
                        try
                        {
                            await policy.ExecuteAsync(
                                static async (ms, ct) =>
                                {
                                    await Task.Delay(ms, CancellationToken.None).ConfigureAwait(false); // ignores its token on purpose
                                    return ms;
                                },
                                delay);
                            Interlocked.Increment(ref completed);
                        }
                        catch (TimeoutRejectedException)
                        {
                            Interlocked.Increment(ref timedOut);
                        }
                    }
                });
            }

            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromMinutes(2));
            Assert.Equal(64 * 300, completed + timedOut);
            Assert.True(completed > 0, "no call completed before its deadline; the race was not exercised");
            Assert.True(timedOut > 0, "no call outlived its deadline; the race was not exercised");

            // Let every late callback run, then surface any faulted task nobody awaited.
            await Task.Delay(50);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= onFirstChance;
            TaskScheduler.UnobservedTaskException -= onUnobserved;
        }

        Assert.True(firstChance.IsEmpty, "ObjectDisposedException raised inside TimeoutPolicy:" + Environment.NewLine + string.Join(Environment.NewLine, firstChance.Take(3)));
        Assert.True(unobserved.IsEmpty, "unobserved exception from TimeoutPolicy:" + Environment.NewLine + string.Join(Environment.NewLine, unobserved.Take(3)));
    }

    /// <summary>
    /// A clock whose timers never fire on their own: the test fires a callback by hand, on a pool thread, after the
    /// policy has disposed the timer, which is exactly the state a system timer is in when its callback was dequeued
    /// just before <c>Dispose()</c>. The callback's exception, if any, is returned instead of crashing the test host.
    /// </summary>
    private sealed class HandFiredTimeProvider : TimeProvider
    {
        private readonly ConcurrentQueue<HandTimer> _timers = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new HandTimer(callback, state);
            _timers.Enqueue(timer);
            return timer;
        }

        public bool TryTake(out HandTimer timer) => _timers.TryDequeue(out timer!);

        public sealed class HandTimer(TimerCallback callback, object? state) : ITimer
        {
            public volatile bool Disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public Exception? Fire()
            {
                try
                {
                    callback(state);
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }

            public void Dispose() => Disposed = true; // returns at once, like TimerQueueTimer.Dispose with a callback in flight

            public ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }
        }
    }
}
