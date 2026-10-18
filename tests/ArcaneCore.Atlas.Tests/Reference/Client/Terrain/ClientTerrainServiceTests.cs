using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Terrain;

public sealed class ClientTerrainServiceTests
{
    private static void AssertNothingAboveWarning(ListLogger log)
        => Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Error);

    [Fact]
    public void LoadWdt_SaysWhyThereIsNoAnswer()
    {
        var fixture = new TerrainFixture();
        var noFiles = new ClientTerrainService(() => null, () => fixture.Maps, NullLogger.Instance);
        var noMaps = new ClientTerrainService(() => fixture.Files, () => null, NullLogger.Instance);
        ClientTerrainService service = fixture.Service();

        WdtLoad none = noFiles.LoadWdt(0);
        WdtLoad nodir = noMaps.LoadWdt(0);
        WdtLoad unknown = service.LoadWdt(999);

        Assert.Equal(TileState.Missing, none.State);
        Assert.Contains("No client files", none.Detail, StringComparison.Ordinal);
        Assert.Equal(TileState.Missing, nodir.State);
        Assert.Contains("map directory", nodir.Detail, StringComparison.Ordinal);
        Assert.Equal(TileState.Missing, unknown.State);
        Assert.Contains("999", unknown.Detail, StringComparison.Ordinal);
        Assert.Null(none.File);
    }

    [Fact]
    public void LoadWdt_ReportsAbsentEmptyAndRefusedFiles()
    {
        var fixture = new TerrainFixture();
        fixture.Files.Add(ClientMapPaths.WdtPath("Kalimdor"), []);
        ClientTerrainService service = fixture.Service();

        WdtLoad empty = service.LoadWdt(1);
        fixture.Files.Add(ClientMapPaths.WdtPath("Kalimdor"), [1, 2, 3, 4, 5, 6, 7]);
        WdtLoad garbage = service.LoadWdt(1);
        WdtLoad ok = service.LoadWdt(0);

        Assert.Equal(TileState.Missing, empty.State);
        Assert.Contains("zero bytes", empty.Detail, StringComparison.Ordinal);
        Assert.Equal(TileState.Rejected, garbage.State);
        Assert.Contains("Kalimdor.wdt", garbage.Detail, StringComparison.Ordinal);
        Assert.Equal(TileState.Ok, ok.State);
        Assert.Equal("World\\Maps\\Azeroth\\Azeroth.wdt", ok.Path);
        Assert.Equal("Azeroth", ok.Map!.Directory);
        Assert.Equal(3, ok.File!.TileCount);
        AssertNothingAboveWarning(fixture.Log);
    }

    [Fact]
    public void LoadWdt_ReportsAFileThatIsNotThere()
    {
        var fixture = new TerrainFixture();
        ClientTerrainService service = fixture.Service();

        WdtLoad absent = service.LoadWdt(1);

        Assert.Equal(TileState.Missing, absent.State);
        Assert.Contains("Kalimdor.wdt", absent.Detail, StringComparison.Ordinal);
        Assert.Contains("not in the client files", absent.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAdt_ReportsEachReasonATileHasNoTerrain()
    {
        var fixture = new TerrainFixture();
        fixture.Files.Add(ClientMapPaths.AdtPath("Azeroth", TerrainFixture.Flat), []);
        ClientTerrainService service = fixture.Service();

        AdtLoad notListed = service.LoadAdt(TerrainFixture.NotListed);
        AdtLoad absent = service.LoadAdt(TerrainFixture.ListedButAbsent);
        AdtLoad empty = service.LoadAdt(TerrainFixture.Flat);
        fixture.Files.Add(ClientMapPaths.AdtPath("Azeroth", TerrainFixture.Flat), new byte[100]);
        AdtLoad garbage = service.LoadAdt(TerrainFixture.Flat);
        AdtLoad ok = service.LoadAdt(TerrainFixture.Goldshire);

        Assert.Equal(TileState.Missing, notListed.State);
        Assert.Contains("does not list tile (10, 10)", notListed.Detail, StringComparison.Ordinal);
        Assert.Equal(TileState.Missing, absent.State);
        Assert.Contains("Azeroth_32_49.adt", absent.Detail, StringComparison.Ordinal);
        Assert.Equal(TileState.Missing, empty.State);
        Assert.Contains("zero bytes", empty.Detail, StringComparison.Ordinal);
        Assert.Equal(TileState.Rejected, garbage.State);
        Assert.Contains("Azeroth_33_48.adt", garbage.Detail, StringComparison.Ordinal);
        Assert.Equal(TileState.Ok, ok.State);
        Assert.Equal("World\\Maps\\Azeroth\\Azeroth_32_48.adt", ok.Path);
        Assert.Equal("memory", ok.Archive);
        Assert.Equal(256, ok.File!.Chunks.Count);
        AssertNothingAboveWarning(fixture.Log);
    }

    [Fact]
    public void LoadAdt_RefusesTilesOutsideTheGrid()
    {
        ClientTerrainService service = new TerrainFixture().Service();

        Assert.Throws<ArgumentException>(() => service.LoadAdt(TileKey.Global(0)));
        Assert.Throws<ArgumentException>(() => service.LoadAdt(new TileKey(0, 64, 0)));
    }

    [Fact]
    public void LoadAdt_ParsesOncePerFileSourceAndSharesTheResult()
    {
        var fixture = new TerrainFixture();
        var counting = new CountingFiles(fixture.Files);
        var service = new ClientTerrainService(() => counting, () => fixture.Maps, fixture.Log);

        AdtLoad first = service.LoadAdt(TerrainFixture.Goldshire);
        AdtLoad second = service.LoadAdt(TerrainFixture.Goldshire);

        Assert.Same(first, second);
        Assert.Same(first.File, second.File);

        // One WDT read and one ADT read.
        Assert.Equal(2, counting.Reads);
    }

    [Fact]
    public void LoadAdt_ParsesOnceWhenManyThreadsAskAtTheSameTime()
    {
        var fixture = new TerrainFixture();
        var counting = new CountingFiles(fixture.Files);
        var service = new ClientTerrainService(() => counting, () => fixture.Maps, fixture.Log);

        var results = new AdtLoad[64];
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i => results[i] = service.LoadAdt(TerrainFixture.Goldshire));

        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Equal(TileState.Ok, results[0].State);
        Assert.Equal(2, counting.Reads);
    }

    [Fact]
    public void LoadAdt_DoesNotRememberAbsentOrRefusedFiles()
    {
        var fixture = new TerrainFixture();
        ClientTerrainService service = fixture.Service();
        string path = ClientMapPaths.AdtPath("Azeroth", TerrainFixture.ListedButAbsent);

        AdtLoad absent = service.LoadAdt(TerrainFixture.ListedButAbsent);
        fixture.Files.Add(path, [9, 9, 9]);
        AdtLoad refused = service.LoadAdt(TerrainFixture.ListedButAbsent);
        fixture.Files.Add(path, SyntheticTerrain.Adt(new AdtSpec { Tile = TerrainFixture.ListedButAbsent }).Bytes);
        AdtLoad repaired = service.LoadAdt(TerrainFixture.ListedButAbsent);

        Assert.Equal(TileState.Missing, absent.State);
        Assert.Equal(TileState.Rejected, refused.State);
        Assert.Equal(TileState.Ok, repaired.State);
    }

    [Fact]
    public void LoadAdt_TurnsAnIoFailureIntoARejectionAndRetriesLater()
    {
        var fixture = new TerrainFixture();
        var counting = new CountingFiles(fixture.Files);
        var service = new ClientTerrainService(() => counting, () => fixture.Maps, fixture.Log);
        string path = ClientMapPaths.AdtPath("Azeroth", TerrainFixture.Goldshire);
        counting.Failing.Add(path);

        AdtLoad failed = service.LoadAdt(TerrainFixture.Goldshire);
        counting.Failing.Clear();
        AdtLoad retried = service.LoadAdt(TerrainFixture.Goldshire);

        Assert.Equal(TileState.Rejected, failed.State);
        Assert.Contains("locked", failed.Detail, StringComparison.Ordinal);
        Assert.Equal(TileState.Ok, retried.State);
        Assert.Contains(fixture.Log.Entries, e => e.Level == LogLevel.Warning);
        AssertNothingAboveWarning(fixture.Log);
    }

    [Fact]
    public void LoadAdt_KeepsAnotherFileSourcesResultsApart()
    {
        var fixture = new TerrainFixture();
        var first = new CountingFiles(fixture.Files);
        var second = new CountingFiles(fixture.Files);
        IClientFileSource current = first;
        var service = new ClientTerrainService(() => current, () => fixture.Maps, fixture.Log);

        AdtLoad a = service.LoadAdt(TerrainFixture.Goldshire);
        current = second;
        AdtLoad b = service.LoadAdt(TerrainFixture.Goldshire);
        current = first;
        AdtLoad again = service.LoadAdt(TerrainFixture.Goldshire);

        Assert.NotSame(a, b);
        Assert.NotSame(a.File, b.File);
        Assert.Same(a, again);
        Assert.Equal(2, first.Reads);
        Assert.Equal(2, second.Reads);
    }

    [Fact]
    public void Clear_MakesTheNextRequestReadAgain()
    {
        var fixture = new TerrainFixture();
        var counting = new CountingFiles(fixture.Files);
        var service = new ClientTerrainService(() => counting, () => fixture.Maps, fixture.Log);
        AdtLoad first = service.LoadAdt(TerrainFixture.Goldshire);

        service.Clear();
        AdtLoad second = service.LoadAdt(TerrainFixture.Goldshire);

        Assert.NotSame(first.File, second.File);
        Assert.Equal(4, counting.Reads);
    }

    [Fact]
    public void LoadAdt_EvictsTheLeastRecentlyUsedTile()
    {
        var fixture = new TerrainFixture();
        var counting = new CountingFiles(fixture.Files);
        var service = new ClientTerrainService(() => counting, () => fixture.Maps, fixture.Log, cacheCapacity: 1);

        service.LoadAdt(TerrainFixture.Goldshire);
        service.LoadAdt(TerrainFixture.Flat);
        int before = counting.Reads;
        service.LoadAdt(TerrainFixture.Goldshire);

        Assert.Equal(before + 1, counting.Reads);
    }

    [Fact]
    public void AvailableTiles_AreTheListedTilesThatHaveAFile_AndFollowTheFiles()
    {
        var fixture = new TerrainFixture();
        ClientTerrainService service = fixture.Service();

        IReadOnlyCollection<TileKey> before = service.AvailableTiles(0);
        fixture.Files.Add(ClientMapPaths.AdtPath("Azeroth", TerrainFixture.ListedButAbsent), [1]);
        IReadOnlyCollection<TileKey> after = service.AvailableTiles(0);

        Assert.Equal(new[] { TerrainFixture.Goldshire, TerrainFixture.Flat }, before);
        Assert.Equal(3, after.Count);
        Assert.Empty(service.AvailableTiles(1));
        Assert.Empty(service.AvailableTiles(999));
        Assert.Empty(service.AvailableTiles(TerrainFixture.DungeonMap));
        Assert.Empty(new ClientTerrainService(() => null, () => fixture.Maps, NullLogger.Instance).AvailableTiles(0));
    }

    [Fact]
    public void PeekAdt_AnswersFromTheWdtOrTheCacheAndNeverReadsAnAdt()
    {
        var fixture = new TerrainFixture();
        var counting = new CountingFiles(fixture.Files);
        var service = new ClientTerrainService(() => counting, () => fixture.Maps, fixture.Log);

        AdtLoad? unread = service.PeekAdt(TerrainFixture.Goldshire);
        int readsForWdt = counting.Reads;
        AdtLoad? notListed = service.PeekAdt(TerrainFixture.NotListed);
        AdtLoad loaded = service.LoadAdt(TerrainFixture.Goldshire);
        AdtLoad? afterLoad = service.PeekAdt(TerrainFixture.Goldshire);

        Assert.Null(unread);
        Assert.Equal(1, readsForWdt);
        Assert.Equal(TileState.Missing, notListed!.State);
        Assert.Same(loaded, afterLoad);
        Assert.Null(service.PeekAdt(TileKey.Global(0)));
        Assert.Equal(TileState.Missing, new ClientTerrainService(() => null, () => fixture.Maps, NullLogger.Instance).PeekAdt(TerrainFixture.Goldshire)!.State);
    }

    [Fact]
    public void AreaName_AsksTheMapDirectory()
    {
        var fixture = new TerrainFixture();

        Assert.Equal("Elwynn Forest", fixture.Service().AreaName(TerrainFixture.Area));
        Assert.Null(fixture.Service().AreaName(9999));
        Assert.Null(new ClientTerrainService(() => fixture.Files, () => null, NullLogger.Instance).AreaName(TerrainFixture.Area));
    }

    [Fact]
    public void Constructor_RefusesMissingDependenciesAndABadCapacity()
    {
        var fixture = new TerrainFixture();

        Assert.Throws<ArgumentNullException>(() => new ClientTerrainService(null!, () => fixture.Maps, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new ClientTerrainService(() => fixture.Files, null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => new ClientTerrainService(() => fixture.Files, () => fixture.Maps, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClientTerrainService(() => fixture.Files, () => fixture.Maps, NullLogger.Instance, 0));
    }

    [Fact]
    public void ReadsThroughADirectoryOfExtractedFilesWithAnyCase()
    {
        string root = Path.Combine(Path.GetTempPath(), "atlas-r2-service-" + Guid.NewGuid().ToString("N"));
        try
        {
            string maps = Path.Combine(root, "world", "MAPS", "azeroth");
            Directory.CreateDirectory(maps);
            File.WriteAllBytes(Path.Combine(maps, "AZEROTH.WDT"), SyntheticTerrain.Wdt([(TerrainFixture.Flat.X, TerrainFixture.Flat.Y)]));
            File.WriteAllBytes(Path.Combine(maps, "azeroth_33_48.ADT"), SyntheticTerrain.Adt(new AdtSpec { Tile = TerrainFixture.Flat }).Bytes);
            var files = new DirectoryClientFiles(root);
            var fixture = new TerrainFixture();
            var service = new ClientTerrainService(() => files, () => fixture.Maps, NullLogger.Instance);

            AdtLoad load = service.LoadAdt(TerrainFixture.Flat);

            Assert.Equal(TileState.Ok, load.State);
            Assert.Equal(Path.GetFullPath(root), load.Archive);
            Assert.Equal(new[] { TerrainFixture.Flat }, service.AvailableTiles(0));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class LoadCacheTests
{
    [Fact]
    public void GetOrLoad_LoadsOnceAndReturnsTheSameInstance()
    {
        var cache = new LoadCache<int, object>(4);
        int loads = 0;

        object a = cache.GetOrLoad(1, () =>
        {
            loads++;
            return new object();
        });
        object b = cache.GetOrLoad(1, () =>
        {
            loads++;
            return new object();
        });

        Assert.Same(a, b);
        Assert.Equal(1, loads);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void GetOrLoad_EvictsTheLeastRecentlyUsedKey()
    {
        var cache = new LoadCache<int, object>(2);
        object one = cache.GetOrLoad(1, () => new object());
        cache.GetOrLoad(2, () => new object());
        cache.GetOrLoad(1, () => new object());
        cache.GetOrLoad(3, () => new object());

        Assert.True(cache.TryPeek(1, out object? stillThere));
        Assert.Same(one, stillThere);
        Assert.False(cache.TryPeek(2, out _));
        Assert.True(cache.TryPeek(3, out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void GetOrLoad_DoesNotCacheAThrowOrAValueTheCallerRejects()
    {
        var cache = new LoadCache<int, string>(4);

        Assert.Throws<InvalidDataException>(() => cache.GetOrLoad(1, () => throw new InvalidDataException("no")));
        string first = cache.GetOrLoad(1, () => "x", _ => false);
        string second = cache.GetOrLoad(1, () => "y", _ => false);
        string kept = cache.GetOrLoad(2, () => "z", v => v == "z");

        Assert.Equal("x", first);
        Assert.Equal("y", second);
        Assert.Equal("z", cache.GetOrLoad(2, () => "other"));
        Assert.Equal("z", kept);
        Assert.False(cache.TryPeek(1, out _));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void GetOrLoad_SharesOneLoadBetweenConcurrentCallers()
    {
        var cache = new LoadCache<int, object>(4);
        int loads = 0;
        using var gate = new ManualResetEventSlim();
        var results = new object[16];

        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = results.Length }, i =>
        {
            results[i] = cache.GetOrLoad(5, () =>
            {
                Interlocked.Increment(ref loads);
                gate.Wait(TimeSpan.FromMilliseconds(50));
                return new object();
            });
        });

        Assert.Equal(1, loads);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public void Clear_DropsEverything()
    {
        var cache = new LoadCache<int, object>(4);
        cache.GetOrLoad(1, () => new object());

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryPeek(1, out _));
    }
}
