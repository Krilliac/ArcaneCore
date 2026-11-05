using System.Diagnostics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Diagnostics;
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

    public void Dispose()
    {
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
