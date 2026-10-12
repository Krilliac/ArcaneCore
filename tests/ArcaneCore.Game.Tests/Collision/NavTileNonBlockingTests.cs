using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
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

    private readonly NavMeshFixture _fixture = new();
    private readonly ManualResetEventSlim _gate = new();
    private int _reads;
    private int _running;
    private int _peak;

    public void Dispose()
    {
        _gate.Set();
        _gate.Dispose();
        _fixture.Dispose();
    }

    private NavMeshPathfinder Gated(int tiles = 1, ILogger? logger = null, bool failing = false)
    {
        _fixture.WriteParams(0);
        for (int x = 0; x < tiles; x++)
        {
            _fixture.WriteTile(0, x == 0 && tiles == 1 ? 31 : x, x == 0 && tiles == 1 ? 31 : TileRow, new CellTile(x, 0, 0, 0, 10, 10));
        }

        var nav = new NavMeshPathfinder(_fixture.Directory, logger)
        {
            FileReader = path =>
            {
                if (!path.EndsWith(".mmtile", StringComparison.Ordinal))
                {
                    return File.Exists(path) ? File.ReadAllBytes(path) : null;
                }

                Interlocked.Increment(ref _reads);
                int now = Interlocked.Increment(ref _running);
                int seen;
                while ((seen = Volatile.Read(ref _peak)) < now && Interlocked.CompareExchange(ref _peak, now, seen) != seen)
                {
                }

                try
                {
                    _gate.Wait();
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
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));
        return await task;
    }

    [Fact]
    public async Task NotResidentTile_AnswersPendingWithoutBlocking_ThenCompletesOnceTheReadIsInstalled()
    {
        NavMeshPathfinder nav = Gated();

        PathResult pending = await Prompt(() => nav.FindPath(0, From, To, NonBlocking));
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
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref _running) == nav.MaxConcurrentTileReads, TimeSpan.FromSeconds(10)));
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
    public void WithoutTheOption_TheReadStaysOnTheCallingThread()
    {
        NavMeshPathfinder nav = Gated();
        _gate.Set();

        PathResult path = nav.FindPath(0, From, To, new PathOptions { LoadTiles = true });

        Assert.Equal(PathType.Normal, path.Type);
        Assert.Equal(0, nav.TileReadsStarted);
        Assert.Equal(1, _reads);
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
