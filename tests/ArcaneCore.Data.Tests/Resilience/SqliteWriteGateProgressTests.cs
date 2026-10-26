using System.Diagnostics;
using ArcaneCore.Data.Resilience;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArcaneCore.Data.Tests.Resilience;

/// <summary>
/// How long a <see cref="SqliteWriteGate"/> waiter is willing to wait: by lack of progress (a hand-off to anyone restarts the countdown) with a hard cap, never counting
/// process-wide suspensions (GC pauses), without a thrown exception per slice, and with a busy message that names the real holder and the real wait. The clock-driven tests
/// run the gate on a <see cref="FakeClock"/> (only the hand-offs and the test's own advances move time); waiters still poll it every few real milliseconds, and a test
/// that moves the clock waits until the waiter has demonstrably read it again (no sleeps), so a wrong decision cannot slip by unobserved.
/// </summary>
public sealed class SqliteWriteGateProgressTests
{
    private const int BoundMs = 1000;

    private static readonly AsyncLocal<bool> CountingFlow = new();

    private sealed class FakeClock : IWriteGateClock
    {
        private readonly object _lock = new();
        private double _now;
        private double _paused;

        /// <summary>Set (per async flow) on the waiter that must park when it next reads the clock while <see cref="Armed"/>.</summary>
        public static readonly AsyncLocal<bool> ParkThisFlow = new();

        /// <summary>Set (per async flow) on the waiter whose clock reads are counted in <see cref="Reads"/>.</summary>
        public static readonly AsyncLocal<bool> WatchThisFlow = new();

        private long _reads; // guarded by _lock
        private int _suspendBetweenReads;
        private double _pauseBetweenReadsMs;

        /// <summary>How many times a watched flow has read <see cref="NowMs"/> (counted under the same lock that reads the time, so a read counted after a change saw it).</summary>
        public long Reads
        {
            get
            {
                lock (_lock) return _reads;
            }
        }

        /// <summary>The next <see cref="PausedMs"/> read of a watched flow first suspends the process for <paramref name="ms"/>: a pause landing between the waiter's two reads.</summary>
        public void SuspendBetweenNextReads(double ms)
        {
            _pauseBetweenReadsMs = ms;
            Volatile.Write(ref _suspendBetweenReads, 1);
        }

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

                lock (_lock)
                {
                    if (WatchThisFlow.Value) _reads++;
                    return _now;
                }
            }
        }

        public double PausedMs
        {
            get
            {
                if (WatchThisFlow.Value && Interlocked.CompareExchange(ref _suspendBetweenReads, 0, 1) == 1) Suspend(_pauseBetweenReadsMs);
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

    /// <summary>
    /// Runs <paramref name="change"/> on the clock, then waits until the watched writer has read the clock twice more (or finished): the first read saw the change and the
    /// second proves its loop went round again, i.e. it looked at the new time and kept waiting. No sleeps: the wait ends on the writer's own progress.
    /// </summary>
    private static void Observe(FakeClock clock, Task writer, Action change)
    {
        change();
        long after = clock.Reads;
        WaitUntil(() => clock.Reads >= after + 2 || writer.IsCompleted, "the writer to read the clock after the change");
    }

    private static Task<SqliteWriteGate.GateEntry> StartWriter(SqliteWriteGate gate, bool park = false)
        => Task.Run(async () =>
        {
            FakeClock.WatchThisFlow.Value = true;
            FakeClock.ParkThisFlow.Value = park;
            return await gate.TryEnterAsync(BoundMs, "writer", 0, CancellationToken.None);
        });

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
    private static (SqliteWriteGate.Hold First, Link[] Links, Task<SqliteWriteGate.GateEntry> Writer) QueueBehind(SqliteWriteGate gate, int links, bool parkWriter = false)
    {
        SqliteWriteGate.Hold first = gate.Enter(0, "H", 0)!;
        var chain = new Link[links];
        for (int i = 0; i < links; i++)
        {
            chain[i] = new Link(gate, i);
            WaitUntilWaiting(gate, i + 1);
        }

        Task<SqliteWriteGate.GateEntry> writer = StartWriter(gate, parkWriter);
        WaitUntilWaiting(gate, links + 1);
        return (first, chain, writer);
    }

    /// <summary>Moves the queue one step: the holder lets go, the next link takes the gate, then <paramref name="advanceMs"/> pass with that link holding it.</summary>
    private static async Task StepAsync(FakeClock clock, Task writer, Action releaseHolder, Link next, double advanceMs)
    {
        releaseHolder();
        await next.Acquired;
        Observe(clock, writer, () => clock.Advance(advanceMs));
    }

    private static async Task<SqliteWriteGate.GateEntry> DriveMovingQueueAsync(int links, double stepMs)
    {
        var clock = new FakeClock();
        SqliteWriteGate gate = NewGate(clock);
        (SqliteWriteGate.Hold first, Link[] chain, Task<SqliteWriteGate.GateEntry> writer) = QueueBehind(gate, links);

        Observe(clock, writer, () => clock.Advance(stepMs)); // the first holder held it for a step; the writer looked at that
        Action releaseHolder = first.Release;
        for (int i = 0; i < links; i++)
        {
            await StepAsync(clock, writer, releaseHolder, chain[i], stepMs); // returns once the writer has looked at the moved clock
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
        SqliteWriteGate.Hold holder = gate.Enter(0, "never-releases", 0)!;
        using ReleaseAtEnd releaseHolder = new(holder);
        Task<SqliteWriteGate.GateEntry> writer = StartWriter(gate);
        WaitUntilWaiting(gate, 1);

        Observe(clock, writer, () => clock.Advance(900));
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
        Task<SqliteWriteGate.GateEntry> writer = StartWriter(gate);
        WaitUntilWaiting(gate, 1);

        Observe(clock, writer, () => clock.Advance(500));
        Observe(clock, writer, () => clock.Suspend(6000)); // a GC freeze six times the bound: the holder could not run either
        Observe(clock, writer, () => clock.Advance(400)); // 900 ms of real, unsuspended waiting so far
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
        SqliteWriteGate.Hold holder = gate.Enter(0, "H", 0)!;
        using ReleaseAtEnd releaseHolder = new(holder);
        Task<SqliteWriteGate.GateEntry> writer = StartWriter(gate);
        WaitUntilWaiting(gate, 1);

        Observe(clock, writer, () => clock.Advance(600));
        Observe(clock, writer, () => clock.Suspend(6000));
        clock.Advance(500); // 1100 ms active
        SqliteWriteGate.GateEntry entry = await writer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(entry.Hold);
        Assert.Equal(7100, entry.WaitedMs);
    }

    [Fact]
    public async Task APauseLandingBetweenTheWaitersTwoClockReads_IsNotLost()
    {
        // The waiter reads the wall clock, then the suspension total. A GC pause that lands in between is in the total but not in the clock it just read; accounting per
        // interval clamped that negative difference to zero and then counted the pause's six seconds as active time on the next look.
        var clock = new FakeClock();
        SqliteWriteGate gate = NewGate(clock);
        SqliteWriteGate.Hold holder = gate.Enter(0, "H", 0)!;
        Task<SqliteWriteGate.GateEntry> writer = StartWriter(gate);
        WaitUntilWaiting(gate, 1);
        WaitUntil(() => clock.Reads >= 2, "the writer to be polling");

        Observe(clock, writer, () => clock.Advance(100));
        clock.SuspendBetweenNextReads(6000);
        long armedAt = clock.Reads;
        WaitUntil(() => clock.Reads >= armedAt + 3 || writer.IsCompleted, "the writer to read through the split pause");
        Observe(clock, writer, () => clock.Advance(100));
        Assert.False(writer.IsCompleted, "a pause that landed between the two reads was counted against the wait");

        holder.Release();
        SqliteWriteGate.GateEntry entry = await writer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(entry.Hold);
        Assert.Equal(6200, entry.WaitedMs);
        entry.Hold.Release();
    }

    [Fact]
    public async Task TheIdleCountdown_RunsFromTheHandOff_NotFromWhenTheWaiterNoticedIt()
    {
        var clock = new FakeClock();
        SqliteWriteGate gate = NewGate(clock);
        (SqliteWriteGate.Hold first, Link[] chain, Task<SqliteWriteGate.GateEntry> writer) = QueueBehind(gate, links: 1, parkWriter: true);
        using ReleaseAtEnd releaseFirst = new(first);

        clock.Armed = true; // the writer's next look at the clock parks it
        Assert.True(clock.Parked.Wait(TimeSpan.FromSeconds(10)), "the writer never reached the clock");
        first.Release(); // the gate moves to the link at clock time 0 ...
        await chain[0].Acquired;
        clock.Advance(1100); // ... and nothing moves for longer than the bound before the writer wakes up and first sees that hand-off
        clock.Armed = false;
        clock.Resume.Set();

        SqliteWriteGate.GateEntry entry = await writer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(entry.Hold); // measured from the hand-off: 1100 ms idle. Measured from noticing it, the writer would have restarted the countdown and kept waiting
        chain[0].Release();
        await chain[0].Done;
    }

    [Fact]
    public async Task ABlockingWaiter_KeepsTheSingleBound_EvenWhileTheQueueMoves()
    {
        var clock = new FakeClock();
        SqliteWriteGate gate = NewGate(clock);
        SqliteWriteGate.Hold first = gate.Enter(0, "H", 0)!;
        var link = new Link(gate, 0);
        WaitUntilWaiting(gate, 1);
        Task<SqliteWriteGate.GateEntry> writer = Task.Run(() =>
        {
            FakeClock.WatchThisFlow.Value = true;
            return gate.TryEnter(BoundMs, "sync-writer", 0);
        });
        WaitUntilWaiting(gate, 2);

        Observe(clock, writer, () => clock.Advance(700));
        first.Release(); // a hand-off: an async waiter would restart its countdown ...
        await link.Acquired;
        Observe(clock, writer, () => clock.Advance(700)); // ... but a blocked thread has used 1400 ms of its single 1000 ms bound
        SqliteWriteGate.GateEntry entry = await writer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(entry.Hold);
        Assert.True(entry.WaitedMs >= 1400, $"waited {entry.WaitedMs}");
        link.Release();
        await link.Done;
    }

    [Theory]
    [InlineData(1000, 4000)]
    [InlineData(5000, 20_000)]
    [InlineData(15_000, 30_000)]
    [InlineData(30_000, 30_000)]
    [InlineData(120_000, 120_000)]
    public void TheHardCap_IsFourBounds_NeverBeyondThirtySecondsUnlessTheBoundItselfIs(int boundMs, int expectedCapMs)
        => Assert.Equal(expectedCapMs, SqliteWriteGate.HardCapMs(boundMs));

    [Fact]
    public async Task TheBusyMessage_NamesTheCurrentHolder_AndTheRealWait()
    {
        var clock = new FakeClock();
        SqliteWriteGate gate = NewGate(clock);
        SqliteWriteGate.Hold holder = gate.Enter(0, "CharacterDbContext.SaveRest", 0)!;
        using ReleaseAtEnd releaseHolder = new(holder);
        Task<SqliteWriteGate.GateEntry> writer = StartWriter(gate);
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
        SqliteWriteGate.Hold holder = gate.Enter(0, "H", 0)!;
        using ReleaseAtEnd releaseHolder = new(holder);
        int timeoutExceptions = 0;
        void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            // First-chance events are process-wide and test classes run in parallel: count only this test's own async flow.
            if (e.Exception is TimeoutException && CountingFlow.Value) Interlocked.Increment(ref timeoutExceptions);
        }

        CountingFlow.Value = true;
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
        // Public API, real time: 8 writers hold the gate ~120 ms each in turn (~1 s in all) against a 2 s bound (hard cap 8 s). The bound is wide enough that building the
        // queue on a loaded pool cannot use it up; what is under test is that nothing gives up while the queue keeps moving.
        SqliteWriteGate gate = NewRealGate();
        SqliteWriteGate.Hold first = gate.Enter(0, "H", 0)!;
        var order = new List<int>();
        var tasks = new List<Task>();
        for (int i = 0; i < 8; i++)
        {
            int id = i;
            tasks.Add(Task.Run(async () =>
            {
                SqliteWriteGate.Hold? hold = await gate.EnterAsync(2000, "w" + id, 0, CancellationToken.None);
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

    /// <summary>Releases a hold when the test scope ends (<see cref="SqliteWriteGate.Hold"/> is not disposable; its release is idempotent).</summary>
    private sealed class ReleaseAtEnd(SqliteWriteGate.Hold hold) : IDisposable
    {
        public void Dispose() => hold.Release();
    }
}
