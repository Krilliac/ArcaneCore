using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Atlas.Server;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Server;

/// <summary>
/// Pins the counters, eviction rule and failure handling of <see cref="ParseCache{TKey, TValue}"/> and the eviction timing of
/// <see cref="LoadCache{TKey, TValue}"/>, which a shared cost-bounded cache must keep.
/// </summary>
public sealed class ParseCacheCharacterisationTests
{
    private static (ParsedFile<string> File, long Cost) Load(string value, long cost) => (ParsedFile<string>.Ok(value, value), cost);

    [Fact]
    public void Stats_CountEntriesCostHitsAndLoads()
    {
        var cache = new ParseCache<string, string>(maxCost: 100);
        Assert.Equal(new ParseCacheStats(0, 0, 0, 0), cache.Stats);

        cache.GetOrLoad("a", () => Load("A", 10));
        Assert.Equal(new ParseCacheStats(1, 10, 0, 1), cache.Stats);

        // A hit does not run the loader, does not add cost, and counts one hit.
        ParsedFile<string> again = cache.GetOrLoad("a", () => throw new InvalidOperationException("must not load"));
        Assert.Equal("A", again.Value);
        Assert.Equal(new ParseCacheStats(1, 10, 1, 1), cache.Stats);

        cache.GetOrLoad("b", () => Load("B", 7));
        Assert.Equal(new ParseCacheStats(2, 17, 1, 2), cache.Stats);
    }

    [Fact]
    public void Eviction_IsOnlyOverBudget_AndTheBoundaryIsInclusive()
    {
        var cache = new ParseCache<string, string>(maxCost: 20);
        cache.GetOrLoad("a", () => Load("A", 10));
        cache.GetOrLoad("b", () => Load("B", 10));

        // Cost equal to the budget keeps everything.
        Assert.Equal(20, cache.Stats.Cost);
        Assert.Equal(2, cache.Stats.Entries);

        // One unit over evicts the coldest entry only.
        cache.GetOrLoad("c", () => Load("C", 1));
        Assert.False(cache.TryPeek("a", out _));
        Assert.True(cache.TryPeek("b", out _));
        Assert.True(cache.TryPeek("c", out _));
        Assert.Equal(new ParseCacheStats(2, 11, 0, 3), cache.Stats);
    }

    [Fact]
    public void Eviction_NeverDropsTheMostRecentEntry_EvenWhenItAloneExceedsTheBudget()
    {
        var cache = new ParseCache<string, string>(maxCost: 5);
        cache.GetOrLoad("small", () => Load("s", 3));
        cache.GetOrLoad("huge", () => Load("H", 50));

        Assert.False(cache.TryPeek("small", out _));
        Assert.True(cache.TryPeek("huge", out ParsedFile<string> huge));
        Assert.Equal("H", huge.Value);
        Assert.Equal(new ParseCacheStats(1, 50, 0, 2), cache.Stats);

        // A hit on an over-budget sole entry keeps it too.
        cache.GetOrLoad("huge", () => throw new InvalidOperationException("must not load"));
        Assert.Equal(new ParseCacheStats(1, 50, 1, 2), cache.Stats);
    }

    [Fact]
    public void EvictedKey_LoadsAgain_AndCountsAsALoadNotAHit()
    {
        var cache = new ParseCache<string, string>(maxCost: 10);
        cache.GetOrLoad("a", () => Load("A", 10));
        cache.GetOrLoad("b", () => Load("B", 10));
        Assert.False(cache.TryPeek("a", out _));

        int loads = 0;
        cache.GetOrLoad("a", () =>
        {
            loads++;
            return Load("A2", 10);
        });

        Assert.Equal(1, loads);
        Assert.Equal(new ParseCacheStats(1, 10, 0, 3), cache.Stats);
    }

    [Fact]
    public void ThrowingLoad_IsNotCached_NotCounted_AndTheNextCallerLoadsAgain()
    {
        var cache = new ParseCache<string, string>(maxCost: 100);

        Assert.Throws<InvalidDataException>(() => cache.GetOrLoad("a", () => throw new InvalidDataException("bad")));
        Assert.Equal(new ParseCacheStats(0, 0, 0, 0), cache.Stats);
        Assert.False(cache.TryPeek("a", out _));

        ParsedFile<string> ok = cache.GetOrLoad("a", () => Load("A", 4));
        Assert.True(ok.IsOk);
        Assert.Equal(new ParseCacheStats(1, 4, 0, 1), cache.Stats);
    }

    [Fact]
    public void MissingAndRejectedResults_AreCachedWithTheirCost()
    {
        var cache = new ParseCache<string, string>(maxCost: 100);
        int loads = 0;

        for (int i = 0; i < 3; i++)
        {
            ParsedFile<string> missing = cache.GetOrLoad("m", () =>
            {
                loads++;
                return (ParsedFile<string>.Missing("no file"), 2);
            });
            Assert.Equal(TileState.Missing, missing.State);
            Assert.Equal("no file", missing.Detail);
            Assert.Null(missing.Value);
        }

        ParsedFile<string> rejected = cache.GetOrLoad("r", () => (ParsedFile<string>.Rejected("corrupt"), 3));
        Assert.Equal(TileState.Rejected, rejected.State);
        Assert.False(rejected.IsOk);

        Assert.Equal(1, loads);
        Assert.Equal(new ParseCacheStats(2, 5, 2, 2), cache.Stats);
    }

    [Fact]
    public async Task ConcurrentLookupsOfOneKey_ShareOneLoad()
    {
        var cache = new ParseCache<string, string>(maxCost: 100);
        int loads = 0;
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var results = new ParsedFile<string>[8];

        Task first = Task.Run(() => results[0] = cache.GetOrLoad("k", () =>
        {
            Interlocked.Increment(ref loads);
            started.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return Load("K", 6);
        }));
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

        Task[] others = [.. Enumerable.Range(1, 7).Select(i => Task.Run(() => results[i] = cache.GetOrLoad("k", () =>
        {
            Interlocked.Increment(ref loads);
            return Load("other", 6);
        })))];
        release.Set();
        await Task.WhenAll([first, .. others]).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, loads);
        Assert.All(results, r => Assert.Equal("K", r.Value));
        Assert.Equal(1, cache.Stats.Entries);
        Assert.Equal(6, cache.Stats.Cost);
        Assert.Equal(1, cache.Stats.Loads);
    }

    [Fact]
    public void Clear_ResetsEntriesAndCost_ButKeepsTheHitAndLoadCounters()
    {
        var cache = new ParseCache<string, string>(maxCost: 100);
        cache.GetOrLoad("a", () => Load("A", 10));
        cache.GetOrLoad("a", () => Load("A", 10));

        cache.Clear();

        Assert.Equal(new ParseCacheStats(0, 0, 1, 1), cache.Stats);
        Assert.False(cache.TryPeek("a", out _));
    }

    [Fact]
    public void GetOrLoad_RejectsANullLoader_AndTryPeekNeverLoads()
    {
        var cache = new ParseCache<string, string>(maxCost: 100);

        Assert.Throws<ArgumentNullException>(() => cache.GetOrLoad("a", null!));
        Assert.False(cache.TryPeek("a", out ParsedFile<string> file));
        Assert.Equal(default(ParsedFile<string>), file);
        Assert.Equal(new ParseCacheStats(0, 0, 0, 0), cache.Stats);
    }

    [Fact]
    public void TryPeek_DoesNotRefreshRecencyOrCountAHit()
    {
        var cache = new ParseCache<string, string>(maxCost: 20);
        cache.GetOrLoad("a", () => Load("A", 10));
        cache.GetOrLoad("b", () => Load("B", 10));

        Assert.True(cache.TryPeek("a", out _));
        cache.GetOrLoad("c", () => Load("C", 10));

        // "a" was only peeked, so it is still the coldest and goes first.
        Assert.False(cache.TryPeek("a", out _));
        Assert.True(cache.TryPeek("b", out _));
        Assert.Equal(0, cache.Stats.Hits);
    }

    [Fact]
    public void StatusJson_RendersCacheCountersWithTheBytesKey_InNameOrder()
    {
        var caches = new SortedDictionary<string, ParseCacheStats>(StringComparer.Ordinal)
        {
            ["terrain"] = new ParseCacheStats(3, 1234, 5, 7),
            ["nav"] = new ParseCacheStats(0, 0, 0, 0),
        };
        var status = new ServerStatus(
            EngineStarted: true,
            DataDirectory: "data",
            TerrainAvailable: true,
            ModelsAvailable: false,
            MeshAvailable: false,
            Maps: [0u, 1u],
            ResidentTerrainTiles: 3,
            ResidentModelTiles: 0,
            ResidentNavTiles: 0,
            ModelFilesLoaded: 0,
            ModelCacheResets: 0,
            QueriesRun: 0,
            QueriesRefused: 0,
            QueriesFailed: 0,
            LastQueryMs: 0,
            PendingJobs: 0,
            CompletedJobs: 0,
            Caches: caches);

        JsonObject json = status.ToJson()["parsedFileCaches"]!.AsObject();

        Assert.Equal(new[] { "nav", "terrain" }, json.Select(p => p.Key).ToArray());
        Assert.Equal(3, (int)json["terrain"]!["entries"]!);
        Assert.Equal(1234L, (long)json["terrain"]!["bytes"]!);
        Assert.Equal(5L, (long)json["terrain"]!["hits"]!);
        Assert.Equal(7L, (long)json["terrain"]!["loads"]!);
        Assert.Equal(new[] { "entries", "bytes", "hits", "loads" }, json["terrain"]!.AsObject().Select(p => p.Key).ToArray());
    }

    [Fact]
    public async Task LoadCache_EvictsOnInsert_BeforeTheLoadRuns_SoAnInFlightLoadCanBeDropped()
    {
        var cache = new LoadCache<int, string>(capacity: 1);
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        Task<string> slow = Task.Run(() => cache.GetOrLoad(1, () =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return "one";
        }));
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, cache.Count);

        // Inserting key 2 evicts key 1 immediately, while key 1's loader is still running.
        Assert.Equal("two", cache.GetOrLoad(2, () => "two"));
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryPeek(2, out _));

        release.Set();
        Assert.Equal("one", await slow);
        Assert.False(cache.TryPeek(1, out _));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void LoadCache_Peek_DoesNotRefreshRecency_AndRejectsANullLoader()
    {
        var cache = new LoadCache<int, string>(capacity: 2);
        cache.GetOrLoad(1, () => "a");
        cache.GetOrLoad(2, () => "b");

        Assert.True(cache.TryPeek(1, out string? peeked));
        Assert.Equal("a", peeked);
        cache.GetOrLoad(3, () => "c");

        Assert.False(cache.TryPeek(1, out _));
        Assert.True(cache.TryPeek(2, out _));
        Assert.Throws<ArgumentNullException>(() => cache.GetOrLoad(9, null!));
    }
}
