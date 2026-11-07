using System.Diagnostics;
using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// The synchronous tile read counter (<see cref="SyncTileReadCounters"/>, design-terrain-io section 3.1): a terrain tile the loader reads
/// itself because no prefetch finished is counted, with its time; a prefetch hit, a missing file and an unconfigured manager are not; a read
/// over <see cref="SyncTileReadCounters.AnomalyMs"/> raises <c>sync-tile-read</c>. Synthetic <c>.map</c> files (<see cref="MapFileBuilder"/>).
/// The counters are thread-static, so each test compares this thread's totals before and after.
/// </summary>
public sealed class SyncTileReadCounterTests : IDisposable
{
    private static readonly TimeSpan PrefetchTimeout = TimeSpan.FromSeconds(30);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "arcanecore-sync-reads-" + Guid.NewGuid().ToString("N"));

    private const uint VMapMapId = 0;
    private const int VMapRow = 30;
    private VMapFixture? _models;

    public void Dispose()
    {
        _models?.Dispose();
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private TerrainManager ManagerWithTile(int tileX, int tileY)
    {
        string maps = Directory.CreateDirectory(Path.Combine(_dataDir, "maps")).FullName;
        File.WriteAllBytes(Path.Combine(maps, TerrainTile.FileName(0, tileX, tileY)), new MapFileBuilder { GridArea = 41 }.Build());
        return new TerrainManager(_dataDir);
    }

    [Fact]
    public void ATileTheLoaderReadsItself_IsCountedWithItsTime()
    {
        TerrainManager manager = ManagerWithTile(32, 32);
        SyncFileReadTotals before = SyncTileReadCounters.Current;

        Assert.True(manager.LoadTile(0, 32, 32).HasData);

        SyncFileReadTotals delta = SyncTileReadCounters.Current - before;
        Assert.Equal(1, delta.Reads);
        Assert.True(delta.Ticks > 0, "the read took measurable time");
        Assert.Equal(0, delta.ReadsHoldingGate);
        Assert.Equal(0, manager.PrefetchHits);
    }

    [Fact]
    public void ATileTheLoaderTakesFromAFinishedPrefetch_IsNotCounted()
    {
        TerrainManager manager = ManagerWithTile(32, 32);
        manager.Prefetch(0, 32, 32);
        var clock = Stopwatch.StartNew();
        bool finished;
        while (!(manager.IsPrefetched(0, 32, 32, out finished) && finished) && clock.Elapsed < PrefetchTimeout)
        {
            Thread.Sleep(5);
        }

        Assert.True(finished, "the background read finished");
        SyncFileReadTotals before = SyncTileReadCounters.Current;

        Assert.True(manager.LoadTile(0, 32, 32).HasData);

        Assert.Equal(0, (SyncTileReadCounters.Current - before).Reads);
        Assert.Equal(1, manager.PrefetchHits);
    }

    [Fact]
    public void AMissingFile_IsNotCountedAsARead()
    {
        TerrainManager manager = ManagerWithTile(32, 32);
        SyncFileReadTotals before = SyncTileReadCounters.Current;

        Assert.False(manager.LoadTile(0, 40, 40).HasData);

        Assert.Equal(0, (SyncTileReadCounters.Current - before).Reads);
    }

    [Fact]
    public void WithoutTerrainData_NothingIsCounted()
    {
        var manager = new TerrainManager(null);
        SyncFileReadTotals before = SyncTileReadCounters.Current;

        Assert.False(manager.LoadTile(0, 32, 32).HasData);

        Assert.Equal(0, (SyncTileReadCounters.Current - before).Reads);
    }

    [Fact]
    public void ARead_AddsToTheCallerKindsProcessTotalsAndTheGateCount()
    {
        (long reads, long ticks) before = SyncTileReadCounters.Process(SyncReadCaller.MapWorkerHoldingGate, SyncReadFile.VMap);
        SyncFileReadTotals mine = SyncTileReadCounters.Current;

        SyncTileReadCounters.Record(SyncReadCaller.MapWorkerHoldingGate, SyncReadFile.VMap, 1_000, 0, 1, 2);

        (long reads, long ticks) after = SyncTileReadCounters.Process(SyncReadCaller.MapWorkerHoldingGate, SyncReadFile.VMap);
        Assert.True(after.reads >= before.reads + 1);
        Assert.True(after.ticks >= before.ticks + 1_000);
        SyncFileReadTotals delta = SyncTileReadCounters.Current - mine;
        Assert.Equal(1, delta.Reads);
        Assert.Equal(1, delta.ReadsHoldingGate);
        Assert.Equal(1_000, delta.Ticks);
    }

    private VMapManager VMapsWithTiles(params int[] tilesX)
    {
        _models = new VMapFixture();
        _models.Place("wall", VMapFixture.Box(new Vector3(-1, -10, 0), new Vector3(1, 10, 10)), new Vector3(0, 0, 0));
        _models.Write(VMapMapId);
        foreach (int x in tilesX)
        {
            _models.WriteTile(VMapMapId, x, VMapRow, []);
        }

        return new VMapManager(_models.Directory);
    }

    [Fact]
    public void AVMapTileTheLoaderReadsItself_IsCountedWithItsTime_AsAVMapRead()
    {
        VMapManager vmaps = VMapsWithTiles(1);
        (long reads, long ticks) process = SyncTileReadCounters.Process(SyncReadCaller.OutsideMapUpdate, SyncReadFile.VMap);
        SyncFileReadTotals before = SyncTileReadCounters.Current;

        Assert.True(vmaps.LoadTile(VMapMapId, 1, VMapRow));

        SyncFileReadTotals delta = SyncTileReadCounters.Current - before;
        Assert.Equal(1, delta.Reads);
        Assert.True(delta.Ticks > 0, "the read took measurable time");
        Assert.True(SyncTileReadCounters.Process(SyncReadCaller.OutsideMapUpdate, SyncReadFile.VMap).Reads >= process.reads + 1);
    }

    [Fact]
    public void AVMapTileTakenFromAFinishedPrefetch_IsNotCounted()
    {
        VMapManager vmaps = VMapsWithTiles(2);
        vmaps.Prefetch(VMapMapId, 2, VMapRow);
        var clock = Stopwatch.StartNew();
        while (!vmaps.IsPrefetchFinished(VMapMapId, 2, VMapRow) && clock.Elapsed < PrefetchTimeout)
        {
            Thread.Sleep(5);
        }

        Assert.True(vmaps.IsPrefetchFinished(VMapMapId, 2, VMapRow), "the background read finished");
        SyncFileReadTotals before = SyncTileReadCounters.Current;

        Assert.True(vmaps.LoadTile(VMapMapId, 2, VMapRow));

        Assert.Equal(0, (SyncTileReadCounters.Current - before).Reads);
        Assert.Equal(1, vmaps.PrefetchHits);
    }

    [Fact]
    public void AMissingVMapTileFile_IsNotCountedAsARead()
    {
        VMapManager vmaps = VMapsWithTiles(3);
        File.Delete(_models!.PathOf(VMapFormat.TileFileName(VMapMapId, 3, VMapRow)));
        SyncFileReadTotals before = SyncTileReadCounters.Current;

        Assert.False(vmaps.LoadTile(VMapMapId, 3, VMapRow));

        Assert.Equal(0, (SyncTileReadCounters.Current - before).Reads);
    }

    [Fact]
    public void Describe_NamesTheFileReads_AndTheOnesMadeHoldingTheSerialGate()
    {
        var diagnostics = new MapUpdateDiagnostics();
        diagnostics.Reset();
        diagnostics.BeginTiles();
        diagnostics.Begin();
        long oneMs = Stopwatch.Frequency / 1000;

        SyncTileReadCounters.Record(SyncReadCaller.MapWorker, SyncReadFile.Terrain, oneMs, 7, 1, 1);
        SyncTileReadCounters.Record(SyncReadCaller.MapWorkerHoldingGate, SyncReadFile.VMap, oneMs, 7, 2, 2);
        diagnostics.Complete();

        string text = diagnostics.Describe();
        Assert.Contains("terrain/vmap tile file reads (prefetch misses): 2 (2.0 ms), holding the serial gate 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_HasNoFileReadSegment_WhenNoneWereMade()
    {
        var diagnostics = new MapUpdateDiagnostics();
        diagnostics.Reset();
        diagnostics.BeginTiles();
        diagnostics.Begin();
        diagnostics.Complete();

        Assert.DoesNotContain("tile file reads", diagnostics.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeProcess_NamesEachCallerKindThatReadAFile()
    {
        long oneMs = Stopwatch.Frequency / 1000;
        SyncTileReadCounters.Record(SyncReadCaller.MapWorkerHoldingGate, SyncReadFile.Terrain, oneMs, 7, 1, 1);

        string text = SyncTileReadCounters.DescribeProcess();

        Assert.StartsWith("terrain/vmap tile file reads since start (prefetch misses): ", text, StringComparison.Ordinal);
        Assert.Contains("MapWorkerHoldingGate ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeProcess_NamesThePrefetchesDroppedUntaken_AfterTheReads()
    {
        SyncTileReadCounters.Record(SyncReadCaller.WorldThread, SyncReadFile.Terrain, 1_000, 7, 1, 1);
        SyncTileReadCounters.RecordPrefetchDrop(SyncReadFile.VMap, PrefetchDrop.Expired);

        string text = SyncTileReadCounters.DescribeProcess();

        int drops = text.IndexOf("; terrain/vmap tile prefetches dropped untaken since start: ", StringComparison.Ordinal);
        Assert.True(drops > 0, text);
        Assert.Matches(@"VMap \d+ evicted, \d+ arrival evicted, [1-9]\d* expired", text[drops..]);
    }

    private static TilePrefetchCache<object> CacheOfHeldWork() => new() { Start = _ => new TaskCompletionSource<object?>().Task };

    [Fact]
    public void AFullApproachBudget_CountsOneEviction_PerPrefetchItDrops()
    {
        TilePrefetchCache<object> cache = CacheOfHeldWork();
        for (int i = 0; i < TilePrefetchCache<object>.MaxEntries; i++)
        {
            cache.Request(0, i, 0, () => new object());
        }

        Assert.Equal(0, cache.Evictions);

        cache.Request(0, 100, 0, () => new object());
        cache.Request(0, 101, 0, () => new object());

        Assert.Equal(2, cache.Evictions);
        Assert.Equal(0, cache.ArrivalEvictions);
        Assert.Equal(0, cache.Expiries);
        Assert.Equal(TilePrefetchCache<object>.MaxEntries, cache.Pending);
    }

    [Fact]
    public void AFullArrivalBudget_CountsArrivalEvictions_NotApproachEvictions()
    {
        TilePrefetchCache<object> cache = CacheOfHeldWork();
        for (int i = 0; i <= TilePrefetchCache<object>.MaxArrivalEntries; i++)
        {
            cache.Request(0, i, 0, () => new object(), arrival: true);
        }

        Assert.Equal(1, cache.ArrivalEvictions);
        Assert.Equal(0, cache.Evictions);
    }

    [Fact]
    public void PrefetchesPastTheirExpiry_CountAsExpired_AndEvictNothing()
    {
        TilePrefetchCache<object> cache = CacheOfHeldWork();
        long now = 0;
        cache.NowMs = () => now;
        for (int i = 0; i < TilePrefetchCache<object>.MaxEntries; i++)
        {
            cache.Request(0, i, 0, () => new object());
        }

        now = TilePrefetchCache<object>.ExpiryMs + 1;
        cache.Request(0, 100, 0, () => new object());

        Assert.Equal(TilePrefetchCache<object>.MaxEntries, cache.Expiries);
        Assert.Equal(0, cache.Evictions);
        Assert.Equal(1, cache.Pending);
    }

    [Fact]
    public void APrefetchTakenClaimedOrForgotten_IsNoDrop()
    {
        var cache = new TilePrefetchCache<object> { Start = work => Task.FromResult(work()) };
        cache.Request(0, 1, 0, () => new object());
        Assert.True(cache.TryTake(0, 1, 0, out object _));
        cache.Request(0, 2, 0, () => new object());
        cache.Forget(0, 2, 0);
        TilePrefetchCache<object> held = CacheOfHeldWork();
        held.Request(0, 3, 0, () => new object());
        Assert.True(held.TryClaimQueued(0, 3, 0));

        foreach (TilePrefetchCache<object> each in new[] { cache, held })
        {
            Assert.Equal(0, each.Evictions + each.ArrivalEvictions + each.Expiries);
        }
    }

    [Fact]
    public void TheTerrainLoader_ExposesItsEvictions_AndAddsThemToTheProcessTotals()
    {
        TerrainManager manager = ManagerWithTile(32, 32);
        long approach = SyncTileReadCounters.PrefetchDrops(SyncReadFile.Terrain, PrefetchDrop.Evicted);
        long arrival = SyncTileReadCounters.PrefetchDrops(SyncReadFile.Terrain, PrefetchDrop.ArrivalEvicted);

        // Tiles with no file: the background reads open nothing that Dispose then has to delete.
        for (int i = 0; i <= TilePrefetchCache<TerrainTile>.MaxEntries; i++)
        {
            manager.Prefetch(0, i, 0);
        }

        for (int i = 0; i <= TilePrefetchCache<TerrainTile>.MaxArrivalEntries; i++)
        {
            manager.PrefetchArrival(0, i, 1);
        }

        Assert.Equal(1, manager.PrefetchEvictions);
        Assert.Equal(1, manager.PrefetchArrivalEvictions);
        Assert.Equal(0, manager.PrefetchExpiries);
        Assert.True(SyncTileReadCounters.PrefetchDrops(SyncReadFile.Terrain, PrefetchDrop.Evicted) >= approach + 1);
        Assert.True(SyncTileReadCounters.PrefetchDrops(SyncReadFile.Terrain, PrefetchDrop.ArrivalEvicted) >= arrival + 1);
    }

    [Fact]
    public void TheVMapLoader_ExposesItsEvictions_AndAddsThemToTheProcessTotals()
    {
        VMapManager vmaps = VMapsWithTiles(4);
        long approach = SyncTileReadCounters.PrefetchDrops(SyncReadFile.VMap, PrefetchDrop.Evicted);
        long arrival = SyncTileReadCounters.PrefetchDrops(SyncReadFile.VMap, PrefetchDrop.ArrivalEvicted);

        // Rows with no tile file: the background reads open nothing that Dispose then has to delete.
        for (int i = 0; i <= TilePrefetchCache<object>.MaxEntries; i++)
        {
            vmaps.Prefetch(VMapMapId, i, VMapRow + 1);
        }

        for (int i = 0; i <= TilePrefetchCache<object>.MaxArrivalEntries; i++)
        {
            vmaps.PrefetchArrival(VMapMapId, i, VMapRow + 2);
        }

        Assert.Equal(1, vmaps.PrefetchEvictions);
        Assert.Equal(1, vmaps.PrefetchArrivalEvictions);
        Assert.Equal(0, vmaps.PrefetchExpiries);
        Assert.True(SyncTileReadCounters.PrefetchDrops(SyncReadFile.VMap, PrefetchDrop.Evicted) >= approach + 1);
        Assert.True(SyncTileReadCounters.PrefetchDrops(SyncReadFile.VMap, PrefetchDrop.ArrivalEvicted) >= arrival + 1);
    }

    [Fact]
    public void ARead_OutsideAnyMapUpdate_IsOfTheOutsideKind()
    {
        Assert.Equal(SyncReadCaller.OutsideMapUpdate, SyncTileReadCounters.CallerKind());
    }

    [Fact]
    public void AReadOver100Ms_RaisesSyncTileRead_AndAFasterOneDoesNot()
    {
        long slow = (long)((SyncTileReadCounters.AnomalyMs + 50) * Stopwatch.Frequency / 1000.0);
        long fast = (long)((SyncTileReadCounters.AnomalyMs - 50) * Stopwatch.Frequency / 1000.0);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "terrain/sync-tile-read" && n.Subject.Contains("map 4242 ", StringComparison.Ordinal));
        using AnomalyCapture vmap = AnomalyLedger.Shared.Capture(n => n.Key == "collision/sync-tile-read" && n.Subject.Contains("map 4243 ", StringComparison.Ordinal));

        SyncTileReadCounters.Record(SyncReadCaller.WorldThread, SyncReadFile.Terrain, fast, 4242, 1, 1);
        Assert.Equal(0, capture.Count("terrain/sync-tile-read"));

        SyncTileReadCounters.Record(SyncReadCaller.MapWorkerHoldingGate, SyncReadFile.Terrain, slow, 4242, 3, 4);
        SyncTileReadCounters.Record(SyncReadCaller.WorldThread, SyncReadFile.VMap, slow, 4243, 5, 6);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains("(3, 4)", notice.Subject, StringComparison.Ordinal);
        Assert.Contains("MapWorkerHoldingGate", notice.Subject, StringComparison.Ordinal);
        Assert.Equal(1, vmap.Count("collision/sync-tile-read"));
    }
}
