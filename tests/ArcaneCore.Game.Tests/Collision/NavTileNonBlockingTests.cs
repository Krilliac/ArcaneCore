using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// <see cref="PathOptions.NonBlockingTiles"/>: a search that needs a navmesh tile that is not resident never reads the file on the
/// calling thread. The tile source is a reader the test gates, so nothing here sleeps or depends on disk speed.
/// </summary>
public sealed class NavTileNonBlockingTests : IDisposable
{
    private static readonly PathOptions NonBlocking = new() { LoadTiles = true, NonBlockingTiles = true };
    private static readonly Vector3 From = new(2, 3, 0);
    private static readonly Vector3 To = new(18, 3, 0);
    private const int TileRow = 30;

    // Guards against a hang, not behaviour: the gated reads never finish on their own, so a call that blocked would wait forever.
    // The test thread and any pool continuations can be starved for many seconds by a busy full-suite run (other tests blocking pool threads).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(120);

    private readonly NavMeshFixture _fixture = new();
    private readonly ManualResetEventSlim _gate = new();
    private int _reads;
    private int _running;
    private int _peak;
    private int _lastReadThread;
    private int _readsOnPoolThread;
    private Func<string, bool> _gated = _ => true;

    public void Dispose()
    {
        _gate.Set();
        _gate.Dispose();
        _fixture.Dispose();
    }

    private NavMeshPathfinder Gated(int tiles = 1, ILogger? logger = null, bool failing = false, bool outOfMemory = false, int concurrency = NavMeshPathfinder.DefaultConcurrentTileReads)
    {
        _fixture.WriteParams(0);
        for (int x = 0; x < tiles; x++)
        {
            _fixture.WriteTile(0, x == 0 && tiles == 1 ? 31 : x, x == 0 && tiles == 1 ? 31 : TileRow, new CellTile(x, 0, 0, 0, 10, 10));
        }

        var nav = new NavMeshPathfinder(_fixture.Directory, logger, concurrency)
        {
            FileReader = path =>
            {
                if (!path.EndsWith(".mmtile", StringComparison.Ordinal))
                {
                    return File.Exists(path) ? File.ReadAllBytes(path) : null;
                }

                Interlocked.Increment(ref _reads);
                Volatile.Write(ref _lastReadThread, Environment.CurrentManagedThreadId);
                if (Thread.CurrentThread.IsThreadPoolThread) Interlocked.Increment(ref _readsOnPoolThread);
                int now = Interlocked.Increment(ref _running);
                int seen;
                while ((seen = Volatile.Read(ref _peak)) < now && Interlocked.CompareExchange(ref _peak, now, seen) != seen)
                {
                }

                try
                {
                    if (_gated(path)) _gate.Wait();
                    if (failing && outOfMemory) throw new OutOfMemoryException("buffer");
                    return failing ? throw new IOException("disk gone") : File.ReadAllBytes(path);
                }
                finally
                {
                    Interlocked.Decrement(ref _running);
                }
            },
        };
        Assert.True(nav.HasNavigationData(0));
        return nav;
    }

    private static async Task<T> Prompt<T>(Func<T> call)
    {
        Task<T> task = Task.Run(call);
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(HangGuard)));
        return await task;
    }

    [Fact]
    public async Task NotResidentTile_AnswersPendingWithoutBlocking_ThenCompletesOnceTheReadIsInstalled()
    {
        NavMeshPathfinder nav = Gated();

        // A dedicated thread, never a pool thread, so the caller's thread id cannot be a reader's: the reader threads are dedicated
        // too, and a thread id is only reused after its thread has exited.
        int callThread = 0;
        var answer = new TaskCompletionSource<PathResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The thread stays alive until the test ends, pass or fail: an exited thread's ManagedThreadId can be reused by a new pool thread.
        using var pinned = new PinnedThread(() =>
        {
            callThread = Environment.CurrentManagedThreadId;
            try { answer.SetResult(nav.FindPath(0, From, To, NonBlocking)); }
            catch (Exception ex) { answer.SetException(ex); }
        });
        Assert.Same(answer.Task, await Task.WhenAny(answer.Task, Task.Delay(HangGuard)));
        PathResult pending = await answer.Task;
        Assert.Equal(PathType.TilesPending, pending.Type);
        Assert.False(pending.HasPath);
        Assert.False(nav.GetNavMesh(0)!.IsTerrainTileLoaded(31, 31));

        _gate.Set();
        await nav.PendingTileRead(0, 31, 31)!;

        PathResult done = nav.FindPath(0, From, To, NonBlocking);
        Assert.Equal(PathType.Normal, done.Type);
        Assert.Equal(2, done.Points.Count);
        Assert.True(Vector3.Distance(To, done.End) < 0.01f);
        Assert.True(nav.GetNavMesh(0)!.IsTerrainTileLoaded(31, 31));
        Assert.Equal(1, _reads);
        Assert.Equal(1, nav.TileReadsStarted);
        Assert.Equal(nav.MaxConcurrentTileReads, nav.FreeTileReadSlots);
        Assert.NotEqual(callThread, Volatile.Read(ref _lastReadThread)); // read on a reader thread, not here
    }

    [Fact]
    public async Task ABackgroundRead_NeverRunsOnAThreadPoolThread()
    {
        NavMeshPathfinder nav = Gated(); // the read is held at the gate until the pending answer is in: a read that finished between the two endpoint polls would install the tile mid-query

        Assert.Equal(PathType.TilesPending, nav.FindPath(0, From, To, NonBlocking).Type);
        Task read = nav.PendingTileRead(0, 31, 31)!;
        _gate.Set();
        Assert.Same(read, await Task.WhenAny(read, Task.Delay(HangGuard)));
        await read;

        Assert.Equal(1, _reads);
        Assert.Equal(0, Volatile.Read(ref _readsOnPoolThread));
    }

    [Fact]
    public void ABackgroundRead_FinishesWhileEveryThreadPoolWorkerIsBusy()
    {
        NavMeshPathfinder nav = Gated(); // the read is held at the gate until the pending answer is in: a read that finished between the two endpoint polls would install the tile mid-query
        // Not disposed: blockers still queued when the test ends must find it set, not disposed.
        var release = new ManualResetEventSlim();
        // More than every worker the pool has or starts with, by a margin it cannot inject in the time the read takes.
        ThreadPool.GetMinThreads(out int minWorkers, out _);
        int blockers = Math.Max(minWorkers, ThreadPool.ThreadCount) + Environment.ProcessorCount * 4 + 8;
        bool probeRan = false;
        try
        {
            for (int i = 0; i < blockers; i++)
            {
                ThreadPool.QueueUserWorkItem(_ => release.Wait());
            }

            // Queued behind every blocker and ahead of anything the read could queue: it runs only once all the blockers have a thread.
            ThreadPool.QueueUserWorkItem(_ => Volatile.Write(ref probeRan, true));

            Assert.Equal(PathType.TilesPending, nav.FindPath(0, From, To, NonBlocking).Type);
            Task read = nav.PendingTileRead(0, 31, 31)!;
            _gate.Set();
            Assert.True(SpinWait.SpinUntil(() => read.IsCompleted, HangGuard), "the read did not finish while the shared pool was held busy");
            Assert.False(Volatile.Read(ref probeRan), "the pool reached the probe before the read finished: it was not saturated, so this run proves nothing");
        }
        finally
        {
            release.Set();
        }

        Assert.Equal(1, _reads);
        Assert.Equal(0, Volatile.Read(ref _readsOnPoolThread));
    }

    [Fact]
    public async Task AnIdleReader_ExitsAfterItsWait_AndTheNextReadStartsANewOne()
    {
        const int tiles = 2;
        NavMeshPathfinder nav = Gated(tiles);
        nav.ReaderIdleMilliseconds = 20;
        int started = 0;
        nav.ReaderThreadStart = start =>
        {
            Interlocked.Increment(ref started);
            new Thread(start) { IsBackground = true }.Start();
        };

        for (int x = 0; x < tiles; x++)
        {
            Vector3 at = new((32f - x - 0.5f) * 533.33333f, (32f - TileRow - 0.5f) * 533.33333f, 0);
            _gate.Reset(); // held until the pending answer is in, as above
            Assert.Equal(PathType.TilesPending, nav.FindPath(0, at, at, NonBlocking).Type);
            Assert.Equal(x + 1, Volatile.Read(ref started)); // the first reader has exited (or none existed): the read has a thread of its own
            Task read = nav.PendingTileRead(0, x, TileRow)!;
            _gate.Set();
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(HangGuard)));
            Assert.True(SpinWait.SpinUntil(() => nav.TileReaderThreads == 0, HangGuard), "an idle reader thread did not exit");
        }

        Assert.Equal(tiles, _reads);
        Assert.Equal(nav.MaxConcurrentTileReads, nav.FreeTileReadSlots);
    }

    [Fact]
    public async Task AReaderThatCannotStart_WithNoOtherReader_FailsTheRead_AndLeavesNothingQueued_AndALaterReadWorks()
    {
        var logger = new CountingLogger();
        NavMeshPathfinder nav = Gated(tiles: 2, logger: logger);
        nav.ReaderThreadStart = _ => throw new OutOfMemoryException("no thread");
        Vector3 first = new((32f - 0 - 0.5f) * 533.33333f, (32f - TileRow - 0.5f) * 533.33333f, 0);
        Vector3 second = new((32f - 1 - 0.5f) * 533.33333f, (32f - TileRow - 0.5f) * 533.33333f, 0);

        // Answered at once, so the query's second endpoint already collects the failure: not pending, logged once, the tile counts as absent.
        Assert.NotEqual(PathType.TilesPending, nav.FindPath(0, first, first, NonBlocking).Type);
        Assert.Equal(1, nav.TileReadsStarted);
        Assert.Null(nav.PendingTileRead(0, 0, TileRow));
        Assert.Equal(1, logger.Errors);
        Assert.Equal(0, nav.TileReaderThreads);
        Assert.Equal(0, nav.QueuedTileReads);
        Assert.Equal(nav.MaxConcurrentTileReads, nav.FreeTileReadSlots);
        Assert.Equal(0, _reads);

        // The count was given back: a later read starts a thread as usual.
        nav.ReaderThreadStart = null;
        Assert.Equal(PathType.TilesPending, nav.FindPath(0, second, second, NonBlocking).Type);
        Task read = nav.PendingTileRead(0, 1, TileRow)!;
        _gate.Set();
        Assert.Same(read, await Task.WhenAny(read, Task.Delay(HangGuard)));
        Assert.Equal(1, _reads);
        Assert.Equal(1, nav.TileReaderThreads);
    }

    [Fact]
    public async Task AReaderThatCannotStart_WhileAnotherRuns_LeavesTheReadToTheOneRunning()
    {
        var logger = new CountingLogger();
        NavMeshPathfinder nav = Gated(tiles: 2, logger: logger);
        int attempts = 0;
        nav.ReaderThreadStart = start =>
        {
            if (Interlocked.Increment(ref attempts) > 1) throw new OutOfMemoryException("no thread");
            new Thread(start) { IsBackground = true }.Start();
        };
        Vector3 first = new((32f - 0 - 0.5f) * 533.33333f, (32f - TileRow - 0.5f) * 533.33333f, 0);
        Vector3 second = new((32f - 1 - 0.5f) * 533.33333f, (32f - TileRow - 0.5f) * 533.33333f, 0);

        Assert.Equal(PathType.TilesPending, nav.FindPath(0, first, first, NonBlocking).Type);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref _running) == 1, HangGuard)); // the one reader is held at the gate
        Assert.Equal(PathType.TilesPending, nav.FindPath(0, second, second, NonBlocking).Type);

        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Equal(1, nav.TileReaderThreads);
        Assert.Equal(1, nav.QueuedTileReads); // queued, not failed
        Assert.Equal(1, logger.Errors);
        Task read = nav.PendingTileRead(0, 1, TileRow)!;
        Assert.False(read.IsCompleted);

        _gate.Set();
        Assert.Same(read, await Task.WhenAny(read, Task.Delay(HangGuard)));
        nav.FindPath(0, second, second, NonBlocking);
        Assert.True(nav.GetNavMesh(0)!.IsTerrainTileLoaded(1, TileRow)); // read and installed: the thread that did not start cost this read nothing
        Assert.Equal(1, logger.Errors);
        Assert.Equal(2, _reads);
        Assert.Equal(0, nav.QueuedTileReads);
    }

    [Fact]
    public async Task APollForAPrefetchStillWaitingForThePool_TakesItBack_AndReadsOnAReaderThread()
    {
        NavMeshPathfinder nav = Gated();
        Func<NavMeshTile?>? queued = null;
        var neverStarted = new TaskCompletionSource<NavMeshTile?>();
        nav.PrefetchStart = work =>
        {
            queued = work;
            return neverStarted.Task; // a pool too busy to start it
        };
        nav.Prefetch(0, 31, 31);

        Assert.Equal(PathType.TilesPending, nav.FindPath(0, From, To, NonBlocking).Type);
        Assert.Equal(1, nav.TileReadsStarted);
        Task read = nav.PendingTileRead(0, 31, 31)!;
        _gate.Set();
        Assert.Same(read, await Task.WhenAny(read, Task.Delay(HangGuard)));
        Assert.Equal(PathType.Normal, nav.FindPath(0, From, To, NonBlocking).Type);
        Assert.Equal(1, _reads);
        Assert.Equal(0, Volatile.Read(ref _readsOnPoolThread));

        Assert.Null(queued!()); // when the pool does reach it, it reads nothing: the file exists, so a read would have returned the tile
        Assert.False(nav.IsPrefetchFinished(0, 31, 31));
    }

    [Fact]
    public void APollForAPrefetchAlreadyReading_WaitsForIt_AndTheFileIsNotReadTwice()
    {
        NavMeshPathfinder nav = Gated();
        NavMeshTile? tile = null;
        var finished = new TaskCompletionSource<NavMeshTile?>();
        nav.PrefetchStart = work =>
        {
            tile = work(); // it got its thread: the read has begun, and the task stands unfinished until the test says so
            return finished.Task;
        };
        nav.Prefetch(0, 31, 31);
        Assert.NotNull(tile);

        Assert.Equal(PathType.TilesPending, nav.FindPath(0, From, To, NonBlocking).Type);
        Assert.Equal(0, nav.TileReadsStarted);

        finished.SetResult(tile);
        Assert.Equal(PathType.Normal, nav.FindPath(0, From, To, NonBlocking).Type);
        Assert.Equal(0, nav.TileReadsStarted);
        Assert.Equal(1, nav.PrefetchHits);
    }

    [Fact]
    public void APrefetchCanBeClaimedOnlyWhileItWaitsForAThread()
    {
        var cache = new TilePrefetchCache<object>();
        var work = new List<Func<object?>>();
        var never = new TaskCompletionSource<object?>();
        cache.Start = item =>
        {
            work.Add(item);
            return never.Task;
        };
        int loads = 0;
        object Load()
        {
            loads++;
            return new object();
        }

        cache.Request(0, 1, 1, Load);
        cache.Request(0, 2, 2, Load);
        Assert.False(cache.TryClaimQueued(0, 3, 3)); // never asked for

        Assert.NotNull(work[0]());
        Assert.False(cache.TryClaimQueued(0, 1, 1)); // its read has begun: waited for, not read twice
        Assert.True(cache.Contains(0, 1, 1));

        Assert.True(cache.TryClaimQueued(0, 2, 2));
        Assert.False(cache.Contains(0, 2, 2));
        Assert.Null(work[1]());
        Assert.Equal(1, loads);
    }

    /// <summary>
    /// Runs <c>body</c> on a dedicated thread that then stays alive until <see cref="Dispose"/>, which releases it and joins it (bounded)
    /// before the release event is disposed, so the thread never touches a disposed event and a failing test cannot leak it.
    /// </summary>
    private sealed class PinnedThread : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;

        public PinnedThread(Action body)
        {
            _thread = new Thread(() =>
            {
                body();
                _release.Wait();
            }) { IsBackground = true };
            _thread.Start();
        }

        public void Dispose()
        {
            _release.Set();
            if (_thread.Join(TimeSpan.FromSeconds(10))) _release.Dispose(); // a thread that did not exit would still wait on it
        }
    }

    [Fact]
    public async Task RepeatedRequestsForOneTile_ShareOneRead()
    {
        NavMeshPathfinder nav = Gated();

        for (int i = 0; i < 25; i++)
        {
            PathResult pending = await Prompt(() => nav.FindPath(0, From + new Vector3(0, i * 0.1f, 0), To, NonBlocking));
            Assert.Equal(PathType.TilesPending, pending.Type);
        }

        Assert.Equal(1, nav.TileReadsStarted);
        _gate.Set();
        await nav.PendingTileRead(0, 31, 31)!;
        Assert.Equal(PathType.Normal, nav.FindPath(0, From, To, NonBlocking).Type);
        Assert.Equal(1, _reads);
    }

    [Fact]
    public async Task TileReads_AreBoundedInConcurrency_AndAllFinish()
    {
        const int tiles = 6;
        NavMeshPathfinder nav = Gated(tiles);
        for (int x = 0; x < tiles; x++)
        {
            Vector3 at = new((32f - x - 0.5f) * 533.33333f, (32f - TileRow - 0.5f) * 533.33333f, 0);
            Assert.Equal(PathType.TilesPending, (await Prompt(() => nav.FindPath(0, at, at, NonBlocking))).Type);
        }

        Assert.Equal(tiles, nav.TileReadsStarted);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref _running) == nav.MaxConcurrentTileReads, HangGuard));
        Assert.Equal(0, nav.FreeTileReadSlots);

        _gate.Set();
        for (int x = 0; x < tiles; x++)
        {
            await nav.PendingTileRead(0, x, TileRow)!;
        }

        Assert.Equal(tiles, _reads);
        Assert.Equal(nav.MaxConcurrentTileReads, _peak);
    }

    [Fact]
    public async Task AFailingRead_IsReportedOnce_AndTheQueryFallsBackToTheUnloadedTileAnswer()
    {
        var logger = new CountingLogger();
        NavMeshPathfinder nav = Gated(logger: logger, failing: true);

        Assert.Equal(PathType.TilesPending, (await Prompt(() => nav.FindPath(0, From, To, NonBlocking))).Type);
        _gate.Set();
        await nav.PendingTileRead(0, 31, 31)!;

        for (int i = 0; i < 10; i++)
        {
            // As a tile that cannot be read always was: the endpoint is on an unloaded tile, so the mover goes straight.
            PathResult fallback = nav.FindPath(0, From, To, NonBlocking);
            Assert.Equal(PathType.Normal | PathType.NotUsingPath, fallback.Type);
        }

        Assert.Equal(1, logger.Errors);
        Assert.Equal(1, _reads);
        Assert.Equal(1, nav.TileReadsStarted);
        Assert.False(nav.GetNavMesh(0)!.IsTerrainTileLoaded(31, 31));
    }

    [Fact]
    public async Task ARead_ThatThrowsOutOfMemory_EndsAsALoggedFailure_AndReturnsItsSlot()
    {
        var logger = new CountingLogger();
        NavMeshPathfinder nav = Gated(logger: logger, failing: true, outOfMemory: true);

        Assert.Equal(PathType.TilesPending, (await Prompt(() => nav.FindPath(0, From, To, NonBlocking))).Type);
        Task read = nav.PendingTileRead(0, 31, 31)!;
        _gate.Set();
        Assert.Same(read, await Task.WhenAny(read, Task.Delay(HangGuard)));
        await read; // the read completed with its error as the result: it did not fault, and the reader thread (and the process) survived

        Assert.Equal(nav.MaxConcurrentTileReads, nav.FreeTileReadSlots);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(PathType.Normal | PathType.NotUsingPath, nav.FindPath(0, From, To, NonBlocking).Type);
        }

        Assert.Equal(1, logger.Errors);
        Assert.Equal(1, _reads);
        Assert.False(nav.GetNavMesh(0)!.IsTerrainTileLoaded(31, 31));
        Assert.Null(nav.PendingTileRead(0, 31, 31));
    }

    [Fact]
    public async Task AMissingTile_IsNeverPendingWithNothingLeftToAwait()
    {
        // No .mmtile for the probe. The read can finish inside FindPath, between the two endpoint checks, and used to answer
        // TilesPending after that read had already left the table (the indoor-climb world setup then awaited null).
        using NavMeshFixture bare = new();
        bare.WriteParams(0);
        for (int i = 0; i < 40; i++)
        {
            var nav = new NavMeshPathfinder(bare.Directory);
            PathResult path = nav.FindPath(0, From, To, NonBlocking);
            if (path.Type == PathType.TilesPending)
            {
                Task? read = nav.PendingTileRead(0, 31, 31);
                Assert.NotNull(read);
                await read!;
                path = nav.FindPath(0, From, To, NonBlocking);
            }

            Assert.Equal(PathType.Normal | PathType.NotUsingPath, path.Type);
            Assert.Null(nav.PendingTileRead(0, 31, 31));
        }
    }

    [Fact]
    public void WithoutTheOption_TheReadStaysOnTheCallingThread()
    {
        NavMeshPathfinder nav = Gated();
        _gate.Set();

        PathResult path = nav.FindPath(0, From, To, new PathOptions { LoadTiles = true });

        Assert.Equal(PathType.Normal, path.Type);
        Assert.Equal(0, nav.TileReadsStarted);
        Assert.Equal(1, _reads);
        Assert.Equal(Environment.CurrentManagedThreadId, Volatile.Read(ref _lastReadThread)); // the read ran on the calling thread
    }

    [Fact]
    public void ReadConcurrency_IsConfigurable_DefaultsToTwo_AndStaysInRange()
    {
        Assert.Equal(2, NavMeshPathfinder.DefaultConcurrentTileReads);
        Assert.Equal(2, new NavMeshPathfinder(_fixture.Directory).MaxConcurrentTileReads);
        Assert.Equal(1, new NavMeshPathfinder(_fixture.Directory, null, 0).MaxConcurrentTileReads);
        Assert.Equal(5, new NavMeshPathfinder(_fixture.Directory, null, 5).FreeTileReadSlots);
        Assert.Equal(16, new NavMeshPathfinder(_fixture.Directory, null, 500).MaxConcurrentTileReads);
        Assert.Equal(2, new CollisionOptions().NavTileReadConcurrency);
    }

    [Fact]
    public async Task HeldReads_AreBounded_AndARequestOverTheBoundIsPendingWithoutStartingARead()
    {
        NavMeshPathfinder nav = Gated(0);
        int total = NavMeshPathfinder.MaxPendingTileReads + 4;
        for (int x = 0; x < total; x++)
        {
            Vector3 at = new((32f - x - 0.5f) * 533.33333f, (32f - TileRow - 0.5f) * 533.33333f, 0);
            Assert.Equal(PathType.TilesPending, (await Prompt(() => nav.FindPath(0, at, at, NonBlocking))).Type);
        }

        Assert.Equal(NavMeshPathfinder.MaxPendingTileReads, nav.TileReadsStarted);
    }

    [Fact]
    public async Task ACompletePathFoundWithoutThePendingTile_IsAccepted_ButAPartialOrMissingOneIsPending()
    {
        // One resident tile with every side linked to a neighbour that is still being read. The start sits on the border cells, so the
        // search wants the neighbour at once: it is pending. A whole path found inside the tile stands; no path (a wall) might only
        // be for want of the neighbour's way round it.
        _gated = path => !path.EndsWith(NavMeshFormat.TileFileName(0, 31, 31), StringComparison.Ordinal);
        _fixture.WriteParams(0);
        _fixture.WriteTile(0, 31, 31, new CellTile(0, 0, 0, 0, 10, 10) { LinkedSides = [0, 2, 4, 6] });
        var open = Resident();
        long before = PathSearchCounters.TilesPending;
        Vector3 a = new(1, 1, 0), b = new(19, 1, 0);
        PathResult whole = open.FindPath(0, a, b, NonBlocking);
        Assert.Equal(PathType.Normal, whole.Type);
        Assert.True(PathSearchCounters.TilesPending > before, "the search did ask for the neighbouring tile");

        _fixture.WriteTile(0, 31, 31, new CellTile(0, 0, 0, 0, 10, 10) { LinkedSides = [0, 2, 4, 6], Walkable = (i, _) => i != 5 });
        var walled = Resident();
        PathResult none = walled.FindPath(0, a, b, NonBlocking);
        Assert.Equal(PathType.TilesPending, none.Type);
        Assert.False(none.HasPath);

        NavMeshPathfinder Resident()
        {
            NavMeshPathfinder nav = Gated(0);
            Assert.True(nav.LoadTile(0, 31, 31));
            return nav;
        }
    }

    private sealed class CountingLogger : ILogger
    {
        public int Errors;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                Interlocked.Increment(ref Errors);
            }
        }
    }
}
