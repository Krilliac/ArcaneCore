using System.Diagnostics;
using ArcaneCore.Data.Resilience;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArcaneCore.Data.Tests.Resilience;

/// <summary>
/// How long a <see cref="SqliteWriteGate"/> waiter is willing to wait: by lack of progress (a hand-off to anyone restarts the countdown) with a hard cap, never counting
/// process-wide suspensions (GC pauses), without a thrown exception per slice, and with a busy message that names the real holder and the real wait. The clock-driven tests
/// run the gate on a <see cref="FakeClock"/> (only the hand-offs and the test's own advances move time); waiters still poll it every few real milliseconds.
/// </summary>
public sealed class SqliteWriteGateProgressTests
{
    private const int BoundMs = 1000;

    private sealed class FakeClock : IWriteGateClock
    {
        private readonly object _lock = new();
        private double _now;
        private double _paused;

        /// <summary>Set (per async flow) on the waiter that must park when it next reads the clock while <see cref="Armed"/>.</summary>
        public static readonly AsyncLocal<bool> ParkThisFlow = new();

        public volatile bool Armed;
        public readonly ManualResetEventSlim Parked = new(false);
        public readonly ManualResetEventSlim Resume = new(false);

        public double NowMs
        {
            get
            {
                if (Armed && ParkThisFlow.Value)
                {
                    Parked.Set();
                    Resume.Wait(TimeSpan.FromSeconds(10));
                }

                lock (_lock) return _now;
            }
        }

        public double PausedMs
        {
            get
            {
                lock (_lock) return _paused;
            }
        }

        public int PollMs => 5;

        /// <summary>Time passes and the process keeps running.</summary>
        public void Advance(double ms)
        {
            lock (_lock) _now += ms;
        }

        /// <summary>Time passes while every thread of the process is suspended (a GC pause).</summary>
        public void Suspend(double ms)
        {
            lock (_lock)
            {
                _now += ms;
                _paused += ms;
            }
        }
    }

    private static SqliteWriteGate NewGate(FakeClock clock)
        => SqliteWriteGate.CreateIsolated(Path.Combine(Path.GetTempPath(), "gate-progress-" + Guid.NewGuid().ToString("N") + ".db"), clock);

    private static SqliteWriteGate NewRealGate()
        => SqliteWriteGate.For(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Path.GetTempPath(), "gate-progress-" + Guid.NewGuid().ToString("N") + ".db"),
        }.ToString())!;

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.ElapsedMilliseconds < 10_000, "timed out waiting for " + what);
            Thread.Sleep(2);
        }
    }

    private static void WaitUntilWaiting(SqliteWriteGate gate, int count) => WaitUntil(() => gate.Waiting == count, $"Waiting == {count} (was {gate.Waiting})");

    /// <summary>Gives a waiter polling every few ms time to look at the clocks (and to wrongly give up, if it were going to).</summary>
    private static async Task LetWaitersPoll() => await Task.Delay(80);

    /// <summary>A queued writer ahead of the one under test: takes the gate when handed it, keeps it until told to release.</summary>
    private sealed class Link
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Link(SqliteWriteGate gate, int id)
        {
            Done = Task.Run(async () =>
            {
                SqliteWriteGate.Hold? hold = await gate.EnterAsync(10_000_000, "link" + id, 0, CancellationToken.None);
                Assert.NotNull(hold);
                _acquired.SetResult();
                await _release.Task;
                hold.Release();
            });
        }

        public Task Done { get; }

        public Task Acquired => _acquired.Task;

        public void Release() => _release.SetResult();
    }

    /// <summary>The gate held by "H", then <paramref name="links"/> queued writers, then the writer under test (async, bound <see cref="BoundMs"/>).</summary>
    private static (SqliteWriteGate.Hold First, Link[] Links, Task<SqliteWriteGate.GateEntry> Writer) QueueBehind(SqliteWriteGate gate, int links)
    {
        SqliteWriteGate.Hold first = gate.Enter(0, "H", 0)!;
        var chain = new Link[links];
        for (int i = 0; i < links; i++)
        {
            chain[i] = new Link(gate, i);
            WaitUntilWaiting(gate, i + 1);
        }

        Task<SqliteWriteGate.GateEntry> writer = Task.Run(async () => await gate.TryEnterAsync(BoundMs, "writer", 0, CancellationToken.None));
        WaitUntilWaiting(gate, links + 1);
        return (first, chain, writer);
    }

    /// <summary>Moves the queue one step: the holder lets go, the next link takes the gate, then <paramref name="advanceMs"/> pass with that link holding it.</summary>
    private static async Task StepAsync(FakeClock clock, Action releaseHolder, Link next, double advanceMs)
    {
        releaseHolder();
        await next.Acquired;
        clock.Advance(advanceMs);
    }

    private static async Task<SqliteWriteGate.GateEntry> DriveMovingQueueAsync(int links, double stepMs)
    {
        var clock = new FakeClock();
        SqliteWriteGate gate = NewGate(clock);
        (SqliteWriteGate.Hold first, Link[] chain, Task<SqliteWriteGate.GateEntry> writer) = QueueBehind(gate, links);

        clock.Advance(stepMs); // the first holder held it for a step
        Action releaseHolder = first.Release;
        for (int i = 0; i < links; i++)
        {
            await StepAsync(clock, releaseHolder, chain[i], stepMs);
            Link current = chain[i];
            releaseHolder = current.Release;
            if (writer.IsCompleted) break;
        }

        if (!writer.IsCompleted)
        {
            releaseHolder();
        }

        SqliteWriteGate.GateEntry entry = await writer.WaitAsync(TimeSpan.FromSeconds(10));
        entry.Hold?.Release();
        foreach (Link link in chain)
        {
            if (!link.Done.IsCompleted) link.Release();
        }

        return entry;
    }

    [Fact]
    public async Task AWaiterBehindAMovingQueue_DoesNotTimeOut_WhenTheTotalWaitExceedsTheBound()
    {
        // 5 hand-offs, each after 700 ms: 3500 ms of waiting against a 1000 ms bound, but never 1000 ms without a hand-off, and under the 4000 ms cap.
        SqliteWriteGate.GateEntry entry = await DriveMovingQueueAsync(links: 4, stepMs: 700);
        Assert.NotNull(entry.Hold);
        Assert.True(entry.WaitedMs >= 2800, $"waited {entry.WaitedMs}");
    }

    [Fact]
    public async Task AWaiterBehindAMovingQueue_StillGivesUpAtTheHardCap()
    {
        // The queue moves every 900 ms (never idle for the 1000 ms bound) but the writer is the 8th in line: the 4000 ms cap ends the wait.
        SqliteWriteGate.GateEntry entry = await DriveMovingQueueAsync(links: 7, stepMs: 900);
        Assert.Null(entry.Hold);
        Assert.True(entry.WaitedMs >= BoundMs * SqliteWriteGate.HardCapFactor, $"waited {entry.WaitedMs}");
    }

    [Fact]
    public async Task AWaiterBehindOneHolderThatNeverReleases_TimesOutAtTheBound()
    {
        var clock = new FakeClock();
        SqliteWriteGate gate = NewGate(clock);
        using SqliteWriteGate.Hold holder = gate.Enter(0, "never-releases", 0)!;
        Task<SqliteWriteGate.GateEntry> writer = Task.Run(async () => await gate.TryEnterAsync(BoundMs, "writer", 0, CancellationToken.None));
        WaitUntilWaiting(gate, 1);

        clock.Advance(900);
        await LetWaitersPoll();
        Assert.False(writer.IsCompleted, "gave up before the bound");

        clock.Advance(150);
        SqliteWriteGate.GateEntry entry = await writer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(entry.Hold);
        Assert.Equal(1050, entry.WaitedMs);
        Assert.Equal(0, gate.Waiting);
        Assert.Equal(1, gate.TimeoutsTotal);
    }

    [Fact]
    public async Task ASuspensionOfTheWholeProcess_DoesNotCountAgainstTheWait()
    {
        var clock = new FakeClock();
        SqliteWriteGate gate = NewGate(clock);
        SqliteWriteGate.Hold holder = gate.Enter(0, "H", 0)!;
        Task<SqliteWriteGate.GateEntry> writer = Task.Run(async () => await gate.TryEnterAsync(BoundMs, "writer", 0, CancellationToken.None));
        WaitUntilWaiting(gate, 1);

        clock.Advance(500);
        clock.Suspend(6000); // a GC freeze six times the bound: the holder could not run either
        clock.Advance(400); // 900 ms of real, unsuspended waiting so far
        await LetWaitersPoll();
        Assert.False(writer.IsCompleted, "the suspension was counted against the wait");

        holder.Release();
        SqliteWriteGate.GateEntry entry = await writer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(entry.Hold);
        Assert.Equal(6900, entry.WaitedMs); // the reported wait is wall time, pause included
        entry.Hold.Release();
    }

    [Fact]
    public async Task AWaiter_StillTimesOutOnceItHasWaitedTheBoundOfActiveTimeAroundASuspension()
    {
        var clock = new FakeClock();
        SqliteWriteGate gate = NewGate(clock);
        using SqliteWriteGate.Hold holder = gate.Enter(0, "H", 0)!;
        Task<SqliteWriteGate.GateEntry> writer = Task.Run(async () => await gate.TryEnterAsync(BoundMs, "writer", 0, CancellationToken.None));
        WaitUntilWaiting(gate, 1);

        clock.Advance(600);
        clock.Suspend(6000);
        clock.Advance(500); // 1100 ms active
        SqliteWriteGate.GateEntry entry = await writer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(entry.Hold);
        Assert.Equal(7100, entry.WaitedMs);
    }

    [Fact]
    public async Task TheBusyMessage_NamesTheCurrentHolder_AndTheRealWait()
    {
        var clock = new FakeClock();
        SqliteWriteGate gate = NewGate(clock);
        using SqliteWriteGate.Hold holder = gate.Enter(0, "CharacterDbContext.SaveRest", 0)!;
        Task<SqliteWriteGate.GateEntry> writer = Task.Run(async () => await gate.TryEnterAsync(BoundMs, "writer", 0, CancellationToken.None));
        WaitUntilWaiting(gate, 1);

        clock.Advance(7300); // despite the 1000 ms bound, nobody ever handed off, and the poll saw it late
        SqliteWriteGate.GateEntry entry = await writer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(entry.Hold);

        SqliteException busy = gate.Busy(entry.WaitedMs);
        Assert.Equal(5, busy.SqliteErrorCode);
        Assert.Contains("by CharacterDbContext.SaveRest", busy.Message, StringComparison.Ordinal);
        Assert.Contains("waited 7300 ms", busy.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("waited 1000 ms", busy.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no current holder", busy.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBusyMessage_DoesNotClaimThereIsNoHolder_WhileAGrantedWaiterHasNotResumed()
    {
        var clock = new FakeClock();
        SqliteWriteGate gate = NewGate(clock);
        SqliteWriteGate.Hold first = gate.Enter(0, "H", 0)!;
        Task<SqliteWriteGate.GateEntry> granted = Task.Run(async () =>
        {
            FakeClock.ParkThisFlow.Value = true;
            return await gate.TryEnterAsync(10_000_000, "GrantedWriter", 0, CancellationToken.None);
        });
        WaitUntilWaiting(gate, 1);

        clock.Armed = true; // the waiter's next look at the clock parks it, whether before or after it sees the hand-off
        Assert.True(clock.Parked.Wait(TimeSpan.FromSeconds(10)), "the waiter never reached the clock");
        first.Release(); // the gate is handed to the parked waiter: held, but Current is still null
        clock.Advance(250);

        Assert.Null(gate.Current);
        string stalled = gate.Busy(5000).Message;
        Assert.DoesNotContain("no current holder", stalled, StringComparison.Ordinal);
        Assert.Contains("handed to a waiter 250 ms ago, not yet resumed", stalled, StringComparison.Ordinal);

        clock.Armed = false;
        clock.Resume.Set();
        SqliteWriteGate.GateEntry entry = await granted.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(entry.Hold);
        string resumed = gate.Busy(5000).Message;
        Assert.Contains("by GrantedWriter", resumed, StringComparison.Ordinal);
        Assert.DoesNotContain("not yet resumed", resumed, StringComparison.Ordinal);
        entry.Hold.Release();
        Assert.Contains("no current holder", gate.Busy(5000).Message, StringComparison.Ordinal); // a free gate really has none
    }

    [Fact]
    public async Task AnAsyncWait_DoesNotThrowATimeoutExceptionWhenItGivesUp()
    {
        // Real clock, public API. The old loop awaited WaitAsync(timeout), which throws and catches a TimeoutException at the end of every slice.
        SqliteWriteGate gate = NewRealGate();
        using SqliteWriteGate.Hold holder = gate.Enter(0, "H", 0)!;
        int timeoutExceptions = 0;
        void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is TimeoutException) Interlocked.Increment(ref timeoutExceptions);
        }

        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        try
        {
            SqliteWriteGate.Hold? hold = await gate.EnterAsync(150, "waiter", 0, CancellationToken.None);
            Assert.Null(hold);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }

        Assert.Equal(0, Volatile.Read(ref timeoutExceptions));
    }

    [Fact]
    public async Task AWaiterBehindAMovingQueue_DoesNotTimeOut_OnTheRealClock()
    {
        // Public API, real time: 8 writers hold the gate ~120 ms each in turn (~1 s in all) against a 500 ms bound; no 500 ms stretch passes without a hand-off.
        SqliteWriteGate gate = NewRealGate();
        SqliteWriteGate.Hold first = gate.Enter(0, "H", 0)!;
        var order = new List<int>();
        var tasks = new List<Task>();
        for (int i = 0; i < 8; i++)
        {
            int id = i;
            tasks.Add(Task.Run(async () =>
            {
                SqliteWriteGate.Hold? hold = await gate.EnterAsync(500, "w" + id, 0, CancellationToken.None);
                Assert.NotNull(hold);
                lock (order) order.Add(id);
                await Task.Delay(120);
                hold.Release();
            }));
            WaitUntilWaiting(gate, i + 1);
        }

        await Task.Delay(120);
        first.Release();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Enumerable.Range(0, 8), order);
        Assert.Equal(0, gate.TimeoutsTotal);
    }
}
