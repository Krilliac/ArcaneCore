// Ported from Krilliac/Sonder-Inference src/cache/tests/test_kv_cache_manager.cpp @1c4c152 (Apache-2.0)

using ArcaneCore.World.Playerbots.Chat.Inference.Scheduling;
using ArcaneCore.World.Playerbots.Chat.Inference.Scheduling.Cache;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference.Scheduling;

public sealed class KvCacheManagerTests
{
    private static readonly CacheFingerprint KFp = new FingerprintBuilder().Add("model-a").Add("rev1").Add(16UL).Build();

    internal static KvCacheConfig Cfg(uint blocks, uint blockSize = 4, bool prefix = true) => new()
    {
        BlockSizeTokens = blockSize,
        NumBlocks = blocks,
        EnablePrefixCaching = prefix,
        BytesPerToken = 100,
    };

    internal static int[] Toks(int start, int n)
    {
        int[] v = new int[n];
        for (int i = 0; i < n; i++)
        {
            v[i] = start + i;
        }

        return v;
    }

    internal static int[] Cat(int[] a, int[] b) => [.. a, .. b];

    // Reconstruct a sequence's logical contents from its block table.
    internal static int[] Contents(KvCacheManager m, ulong id)
    {
        var output = new List<int>();
        int n = m.NumTokens(id);
        foreach (uint b in m.BlockTable(id))
        {
            foreach (int t in m.BlockTokens(b))
            {
                if (output.Count == n)
                {
                    break;
                }

                output.Add(t);
            }
        }

        return [.. output];
    }

    private static SequenceOptions Opts(byte priority = 0) => new(KFp, priority);

    private static void Ok(KvCacheStatus status) => Assert.True(status.IsOk, status.Message);

    [Fact]
    public void ConfigInvalidBlockSizeAndWatermarksThrow()
    {
        Assert.Throws<ArgumentException>(() => new KvCacheManager(Cfg(4, 0)));
        KvCacheConfig c = Cfg(4);
        c.HighWatermark = 0.9;
        c.CriticalWatermark = 0.5;
        Assert.Throws<ArgumentException>(() => new KvCacheManager(c));
    }

    [Fact]
    public void FingerprintOrderAndSeparationSensitive()
    {
        CacheFingerprint a = new FingerprintBuilder().Add("ab").Add("c").Build();
        CacheFingerprint b = new FingerprintBuilder().Add("a").Add("bc").Build();
        CacheFingerprint c = new FingerprintBuilder().Add("ab").Add("c").Build();
        Assert.NotEqual(a, b);
        Assert.Equal(a, c);
        Assert.Equal("critical", PressureLevel.Critical.ToName());
    }

    [Fact]
    public void AllocBlockTableGrowsPerBlockAndStatsAccountPinnedBlocks()
    {
        var m = new KvCacheManager(Cfg(8));
        Assert.Equal("priority_lru", m.EvictionPolicy.Name);
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Toks(0, 10), out AppendResult r));
        Assert.Equal(10, r.TokensAppended);
        Assert.Equal(3, r.BlocksAllocated);
        Assert.Equal(0, r.TokensReused);
        Assert.Equal(10, m.NumTokens(1));
        Assert.Equal(3, m.BlockTable(1).Length);
        Assert.Equal(Toks(0, 10), Contents(m, 1));
        // Decode one token at a time: fills the partial block, then allocates.
        Ok(m.AppendTokens(1, Toks(10, 1)));
        Ok(m.AppendTokens(1, Toks(11, 1)));
        Assert.Equal(3, m.BlockTable(1).Length);
        Ok(m.AppendTokens(1, Toks(12, 1)));
        Assert.Equal(4, m.BlockTable(1).Length);
        Assert.Equal(Toks(0, 13), Contents(m, 1));
        KvCacheStats s = m.Stats();
        Assert.Equal(8, s.TotalBlocks);
        Assert.Equal(4, s.PinnedBlocks);
        Assert.Equal(4, s.FreeBlocks);
        Assert.Equal(0, s.CachedBlocks);
        Assert.Equal(1, s.Sequences);
        Assert.Equal(400UL, s.BytesPerBlock);
        Assert.Equal(1600UL, s.PinnedBytes);
        Assert.Equal(4UL, s.BlocksAllocated);
        Assert.Equal(0.5, s.Utilization);
        Assert.True(m.Validate());
    }

    [Fact]
    public void ErrorsUnknownAndDuplicateSequences()
    {
        var m = new KvCacheManager(Cfg(4));
        Assert.Equal(KvCacheError.NotFound, m.AppendTokens(9, Toks(0, 1)).Code);
        Assert.Equal(KvCacheError.NotFound, m.FreeSequence(9).Code);
        Assert.Equal(KvCacheError.NotFound, m.Truncate(9, 0).Code);
        Assert.Equal(KvCacheError.NotFound, m.SetPriority(9, 1).Code);
        Assert.Equal(KvCacheError.NotFound, m.ForkSequence(9, 10).Code);
        Ok(m.AddSequence(1));
        Assert.Equal(KvCacheError.InvalidState, m.AddSequence(1).Code);
        Assert.Equal(KvCacheError.InvalidState, m.ForkSequence(1, 1).Code);
        Assert.False(string.IsNullOrEmpty(m.AddSequence(1).Message));
        Assert.Equal(0, m.BlockTable(9).Length);
        Assert.Equal(0, m.NumTokens(9));
        Assert.False(m.HasSequence(9));
        Ok(m.AppendTokens(1, ReadOnlySpan<int>.Empty));
        Assert.Equal(0u, m.RefCount(12345));
        Assert.Equal(0, m.BlockTokens(12345).Length);
        Assert.True(m.Validate());
    }

    [Fact]
    public void FreeFullBlocksStayCachedPartialBlocksReturnToFreeList()
    {
        var m = new KvCacheManager(Cfg(8));
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Toks(0, 10)));
        Ok(m.FreeSequence(1));
        KvCacheStats s = m.Stats();
        Assert.Equal(2, s.CachedBlocks);
        Assert.Equal(6, s.FreeBlocks);
        Assert.Equal(0, s.PinnedBlocks);
        Assert.Equal(800UL, s.CachedBytes);
        Assert.False(m.HasSequence(1));
        Assert.Equal(8, m.AvailableBlocks);
        Assert.True(m.Validate());
    }

    [Fact]
    public void FreeWithPrefixCachingDisabledReleasesEverything()
    {
        var m = new KvCacheManager(Cfg(8, 4, false));
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Toks(0, 10)));
        Ok(m.FreeSequence(1));
        Assert.Equal(8, m.Stats().FreeBlocks);
        Ok(m.AddSequence(2, Opts()));
        Ok(m.AppendTokens(2, Toks(0, 10), out AppendResult r));
        Assert.Equal(0, r.TokensReused);
        Assert.Equal(0, m.MatchPrefix(KFp, Toks(0, 10)));
        Assert.True(m.Validate());
    }

    [Fact]
    public void PrefixReuseAfterTheOwnerFinished()
    {
        var m = new KvCacheManager(Cfg(8));
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Toks(0, 10)));
        uint[] a = m.BlockTable(1).ToArray();
        Ok(m.FreeSequence(1));

        Ok(m.AddSequence(2, Opts()));
        int[] prompt = Cat(Toks(0, 8), Toks(500, 3));
        Ok(m.AppendTokens(2, prompt, out AppendResult r));
        Assert.Equal(8, r.TokensReused);
        Assert.Equal(2, r.BlocksReused);
        Assert.Equal(1, r.BlocksAllocated);
        Assert.Equal(8, m.CachedPrefixTokens(2));
        Assert.Equal(a[0], m.BlockTable(2)[0]);
        Assert.Equal(a[1], m.BlockTable(2)[1]);
        Assert.Equal(prompt, Contents(m, 2));
        KvCacheStats s = m.Stats();
        Assert.Equal(2UL, s.PrefixHitBlocks);
        Assert.Equal(8UL, s.AvoidedPrefillTokens);
        Assert.Equal(1UL, s.PrefixMissLookups); // seq 1's first block; seq 2's 3-token tail is not looked up
        Assert.Equal(0, s.CachedBlocks);
        Assert.True(m.Validate());
    }

    [Fact]
    public void SharedBotSystemPromptIsReusedAcrossPersonas()
    {
        // ArcaneCore scenario: every bot prompt starts with the same grounding text; only the
        // persona after it differs. The second and third bots skip the shared prefix's prefill.
        var m = new KvCacheManager(Cfg(64, 16));
        int[] system = Toks(1000, 48); // three whole blocks of shared grounding
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Cat(system, Toks(1, 20))));
        Ok(m.AddSequence(2, Opts()));
        Ok(m.AppendTokens(2, Cat(system, Toks(200, 20)), out AppendResult second));
        Ok(m.FreeSequence(1));
        Ok(m.AddSequence(3, Opts()));
        Assert.Equal(48, m.MatchPrefix(KFp, Cat(system, Toks(300, 5))));
        Ok(m.AppendTokens(3, Cat(system, Toks(300, 5)), out AppendResult third));
        Assert.Equal(48, second.TokensReused);
        Assert.Equal(48, third.TokensReused);
        Assert.Equal(48, m.CachedPrefixTokens(3));
        Assert.Equal(m.BlockTable(2)[0], m.BlockTable(3)[0]);
        Assert.True(m.Validate());
    }

    [Fact]
    public void PrefixConcurrentSharingBumpsRefcount()
    {
        var m = new KvCacheManager(Cfg(8));
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AddSequence(2, Opts()));
        Ok(m.AppendTokens(1, Toks(0, 8)));
        Ok(m.AppendTokens(2, Toks(0, 12), out AppendResult r));
        Assert.Equal(2, r.BlocksReused);
        Assert.Equal(1, r.BlocksAllocated);
        Assert.Equal(2u, m.RefCount(m.BlockTable(1)[0]));
        Assert.Equal(2, m.Stats().SharedBlocks);
        Assert.Equal(2UL, m.Stats().PrefixMissLookups); // seq 1 block 0, seq 2 block 2
        Ok(m.FreeSequence(1));
        Assert.Equal(1u, m.RefCount(m.BlockTable(2)[0]));
        Assert.True(m.Validate());
    }

    [Fact]
    public void PrefixFingerprintMismatchPreventsReuse()
    {
        var m = new KvCacheManager(Cfg(8));
        CacheFingerprint other = new FingerprintBuilder().Add("model-a").Add("rev2").Add(16UL).Build();
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Toks(0, 8)));
        Ok(m.AddSequence(2, new SequenceOptions(other, 0)));
        Ok(m.AppendTokens(2, Toks(0, 8), out AppendResult r));
        Assert.Equal(0, r.TokensReused);
        Assert.Equal(0, m.Stats().SharedBlocks);
        Assert.Equal(8, m.MatchPrefix(other, Toks(0, 8))); // its own blocks are now registered
        Assert.True(m.Validate());
    }

    [Fact]
    public void PrefixHashChainRequiresIdenticalAncestors()
    {
        var m = new KvCacheManager(Cfg(8));
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Cat(Toks(0, 4), Toks(100, 4))));
        // Same second block, different first block -> no hit anywhere.
        Assert.Equal(0, m.MatchPrefix(KFp, Cat(Toks(50, 4), Toks(100, 4))));
        Ok(m.AddSequence(2, Opts()));
        Ok(m.AppendTokens(2, Cat(Toks(50, 4), Toks(100, 4)), out AppendResult r));
        Assert.Equal(0, r.TokensReused);
        Assert.True(m.Validate());
    }

    [Fact]
    public void MatchPrefixIsAPureQuery()
    {
        var m = new KvCacheManager(Cfg(8));
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Toks(0, 10)));
        KvCacheStats before = m.Stats();
        Assert.Equal(8, m.MatchPrefix(KFp, Toks(0, 10)));
        Assert.Equal(4, m.MatchPrefix(KFp, Toks(0, 7)));
        Assert.Equal(4, m.MatchPrefix(KFp, Cat(Toks(0, 4), Toks(9, 4))));
        Assert.Equal(0, m.MatchPrefix(new CacheFingerprint(42), Toks(0, 10)));
        Assert.Equal(0, m.MatchPrefix(KFp, ReadOnlySpan<int>.Empty));
        Assert.Equal(before.PrefixHitBlocks, m.Stats().PrefixHitBlocks);
        Assert.Equal(before.PrefixMissLookups, m.Stats().PrefixMissLookups);
    }

    [Fact]
    public void PrefixOnlyTheLeadingRunIsLookedUp()
    {
        var m = new KvCacheManager(Cfg(16));
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Toks(0, 12)));
        Ok(m.AddSequence(2, Opts()));
        // hit, miss, then content identical to seq 1 block 2 but a different parent anyway
        Ok(m.AppendTokens(2, Cat(Cat(Toks(0, 4), Toks(77, 4)), Toks(8, 4)), out AppendResult r));
        Assert.Equal(1, r.BlocksReused);
        Assert.Equal(4, m.CachedPrefixTokens(2));
        Assert.Equal(2UL, m.Stats().PrefixMissLookups); // seq 1 block 0, seq 2 block 1 (no further lookups)
        // Chunked prefill across calls keeps the streak.
        Ok(m.AddSequence(3, Opts()));
        Ok(m.AppendTokens(3, Toks(0, 4), out r));
        Assert.Equal(1, r.BlocksReused);
        Ok(m.AppendTokens(3, Toks(4, 8), out r));
        Assert.Equal(2, r.BlocksReused);
        Assert.Equal(12, m.CachedPrefixTokens(3));
        Assert.True(m.Validate());
    }

    [Fact]
    public void ForkSharesEveryBlockWithoutAllocating()
    {
        var m = new KvCacheManager(Cfg(8));
        Ok(m.AddSequence(1, Opts(3)));
        Ok(m.AppendTokens(1, Toks(0, 8)));
        ulong before = m.Stats().BlocksAllocated;
        Ok(m.ForkSequence(1, 2));
        Ok(m.ForkSequence(1, 3, 7));
        Assert.Equal(before, m.Stats().BlocksAllocated);
        Assert.Equal(3u, m.RefCount(m.BlockTable(1)[0]));
        Assert.Equal(2, m.Stats().SharedBlocks);
        Assert.Equal(8, m.NumTokens(3));
        Assert.Equal(Toks(0, 8), Contents(m, 3));
        // Appending at a block boundary allocates a private block, no copy.
        Ok(m.AppendTokens(2, Toks(100, 2), out AppendResult r));
        Assert.Equal(0, r.CopyOnWrite);
        Assert.Equal(1, r.BlocksAllocated);
        Assert.Empty(m.TakePendingCopies());
        Assert.Equal(Toks(0, 8), Contents(m, 1));
        Assert.True(m.Validate());
    }

    [Fact]
    public void ForkCopyOnWriteForSharedPartialTail()
    {
        var m = new KvCacheManager(Cfg(8));
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Toks(0, 6)));
        Ok(m.ForkSequence(1, 2));
        uint sharedTail = m.BlockTable(1)[1];
        Assert.Equal(1, m.BlocksNeeded(2, 1)); // COW needs a block even though room remains
        Assert.Equal(2, m.BlocksNeeded(2, 3));
        Ok(m.AppendTokens(2, Toks(200, 1), out AppendResult r));
        Assert.Equal(1, r.CopyOnWrite);
        uint childTail = m.BlockTable(2)[1];
        Assert.NotEqual(sharedTail, childTail);
        Assert.Equal(m.BlockTable(1)[0], m.BlockTable(2)[0]);
        List<BlockCopy> copies = m.TakePendingCopies();
        Assert.Equal(new BlockCopy(sharedTail, childTail, 2), Assert.Single(copies));
        Assert.Empty(m.TakePendingCopies());
        Assert.Equal(Toks(0, 6), Contents(m, 1));
        Assert.Equal(Cat(Toks(0, 6), Toks(200, 1)), Contents(m, 2));
        // The parent now exclusively owns its tail: writes in place.
        Ok(m.AppendTokens(1, Toks(6, 1), out r));
        Assert.Equal(0, r.CopyOnWrite);
        Assert.Equal(sharedTail, m.BlockTable(1)[1]);
        Assert.Equal(Toks(0, 7), Contents(m, 1));
        Assert.Equal(1UL, m.Stats().CopyOnWrite);
        Assert.True(m.Validate());
    }

    [Fact]
    public void AllocOutOfBlocksIsAllOrNothing()
    {
        var m = new KvCacheManager(Cfg(3));
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Toks(0, 5)));
        Assert.False(m.CanAppend(1, 8));
        Assert.True(m.CanAppend(1, 7));
        Assert.False(m.CanAppend(99, 1));
        Assert.Equal(KvCacheError.Unavailable, m.AppendTokens(1, Toks(5, 8)).Code);
        Assert.Equal(5, m.NumTokens(1));
        Assert.Equal(2, m.BlockTable(1).Length);
        Assert.Equal(1UL, m.Stats().AllocationFailures);
        Assert.Equal(Toks(0, 5), Contents(m, 1));
        Ok(m.AppendTokens(1, Toks(5, 7)));
        Assert.Equal(0, m.Stats().FreeBlocks);
        Assert.True(m.Validate());
    }

    [Fact]
    public void EvictCachedBlocksAreReclaimedOnDemandLeavesFirst()
    {
        var m = new KvCacheManager(Cfg(4));
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Toks(0, 16)));
        Ok(m.FreeSequence(1));
        Assert.Equal(4, m.Stats().CachedBlocks);
        Ok(m.AddSequence(2, Opts()));
        Ok(m.AppendTokens(2, Toks(1000, 8)));
        KvCacheStats s = m.Stats();
        Assert.Equal(2UL, s.Evictions);
        Assert.Equal(2, s.CachedBlocks);
        // Roots survive so the leading prefix is still reusable.
        Assert.Equal(8, m.MatchPrefix(KFp, Toks(0, 16)));
        Assert.True(m.Validate());
    }

    [Fact]
    public void EvictLruPolicyEvictsOldestReleasedChainFirst()
    {
        var m = new KvCacheManager(Cfg(4), EvictionPolicies.Create(EvictionPolicyKind.Lru));
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Toks(0, 8)));
        Ok(m.AddSequence(2, Opts()));
        Ok(m.AppendTokens(2, Toks(100, 8)));
        Ok(m.FreeSequence(1));
        Ok(m.FreeSequence(2));
        Ok(m.AddSequence(3, Opts()));
        Ok(m.AppendTokens(3, Toks(500, 8)));
        Assert.Equal(0, m.MatchPrefix(KFp, Toks(0, 8)));
        Assert.Equal(8, m.MatchPrefix(KFp, Toks(100, 8)));
        Assert.True(m.Validate());
    }

    [Fact]
    public void EvictPriorityAwareKeepsHighPriorityPrefixes()
    {
        var m = new KvCacheManager(Cfg(4));
        Ok(m.AddSequence(1, Opts(9))); // player-facing
        Ok(m.AppendTokens(1, Toks(0, 8)));
        Ok(m.AddSequence(2, Opts(1))); // ambient
        Ok(m.AppendTokens(2, Toks(100, 8)));
        Ok(m.FreeSequence(2));
        Ok(m.FreeSequence(1)); // high priority released last... and more recent
        // Now make the low-priority chain more recent: reuse and release it again.
        Ok(m.AddSequence(4, Opts(1)));
        Ok(m.AppendTokens(4, Toks(100, 8)));
        Ok(m.FreeSequence(4));
        Ok(m.AddSequence(3, Opts(5)));
        Ok(m.AppendTokens(3, Toks(500, 8)));
        Assert.Equal(8, m.MatchPrefix(KFp, Toks(0, 8)));
        Assert.Equal(0, m.MatchPrefix(KFp, Toks(100, 8)));
        Assert.True(m.Validate());
    }

    [Fact]
    public void SetPriorityPropagatesToHeldBlocks()
    {
        var m = new KvCacheManager(Cfg(4));
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Toks(0, 8)));
        Ok(m.AddSequence(2, Opts(3)));
        Ok(m.AppendTokens(2, Toks(100, 8)));
        Ok(m.SetPriority(1, 9));
        Ok(m.FreeSequence(1));
        Ok(m.FreeSequence(2));
        Ok(m.AddSequence(3, Opts()));
        Ok(m.AppendTokens(3, Toks(500, 8)));
        Assert.Equal(8, m.MatchPrefix(KFp, Toks(0, 8)));
        Assert.Equal(0, m.MatchPrefix(KFp, Toks(100, 8)));
    }

    [Fact]
    public void TruncateReleasesBlocksAndRewritesTailSafely()
    {
        var m = new KvCacheManager(Cfg(8));
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Toks(0, 10)));
        Assert.Equal(KvCacheError.InvalidArgument, m.Truncate(1, 11).Code);
        Ok(m.Truncate(1, 6));
        Assert.Equal(6, m.NumTokens(1));
        Assert.Equal(2, m.BlockTable(1).Length);
        Assert.Equal(6, m.Stats().FreeBlocks);
        // Block 1 was full+registered with refcount 1: rewritten in place and no longer served
        // as a prefix block.
        uint b1 = m.BlockTable(1)[1];
        Ok(m.AppendTokens(1, Toks(900, 3)));
        Assert.Equal(b1, m.BlockTable(1)[1]);
        Assert.Equal(Cat(Toks(0, 6), Toks(900, 3)), Contents(m, 1));
        Assert.Equal(4, m.MatchPrefix(KFp, Toks(0, 8)));
        Assert.Equal(8, m.MatchPrefix(KFp, Cat(Toks(0, 6), Toks(900, 2))));
        Ok(m.Truncate(1, 0));
        Assert.Equal(0, m.BlockTable(1).Length);
        Assert.True(m.Validate());
    }

    [Fact]
    public void TruncateSharedFullBlockTriggersCopyOnWrite()
    {
        var m = new KvCacheManager(Cfg(8));
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Toks(0, 8)));
        Ok(m.ForkSequence(1, 2));
        Ok(m.Truncate(2, 6));
        Ok(m.AppendTokens(2, Toks(700, 1), out AppendResult r));
        Assert.Equal(1, r.CopyOnWrite);
        Assert.Equal(Toks(0, 8), Contents(m, 1));
        Assert.Equal(Cat(Toks(0, 6), Toks(700, 1)), Contents(m, 2));
        Assert.Equal(8, m.MatchPrefix(KFp, Toks(0, 8)));
        Assert.True(m.Validate());
    }

    [Fact]
    public void TruncateBackToACachedBoundaryRestoresPrefixLookup()
    {
        var m = new KvCacheManager(Cfg(8));
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Toks(0, 8)));
        Ok(m.AddSequence(2, Opts()));
        Ok(m.AppendTokens(2, Toks(0, 4)));
        Assert.Equal(4, m.CachedPrefixTokens(2));
        Ok(m.AppendTokens(2, Toks(300, 4))); // diverges
        Ok(m.Truncate(2, 4));
        Ok(m.AppendTokens(2, Toks(4, 4), out AppendResult r));
        Assert.Equal(1, r.BlocksReused);
        Assert.Equal(8, m.CachedPrefixTokens(2));
        Assert.True(m.Validate());
    }

    [Fact]
    public void TelemetryEventsAndPressureTransitions()
    {
        KvCacheConfig c = Cfg(10);
        c.HighWatermark = 0.5;
        c.CriticalWatermark = 0.9;
        var m = new KvCacheManager(c);
        var counts = new Dictionary<CacheEventKind, int>();
        var levels = new List<PressureLevel>();
        m.SetEventListener(e =>
        {
            counts[e.Kind] = counts.GetValueOrDefault(e.Kind) + 1;
            if (e.Kind == CacheEventKind.PressureChanged)
            {
                levels.Add(e.Pressure);
            }
        });
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Toks(0, 20))); // 5 blocks -> high
        Assert.Equal(PressureLevel.High, m.Pressure);
        Ok(m.AppendTokens(1, Toks(20, 16))); // 9 blocks -> critical
        Assert.Equal(PressureLevel.Critical, m.Pressure);
        Ok(m.FreeSequence(1));
        Assert.Equal(PressureLevel.Normal, m.Pressure);
        Ok(m.AddSequence(2, Opts()));
        Ok(m.AppendTokens(2, Toks(0, 8))); // 2 reused
        Ok(m.AddSequence(3, Opts()));
        Ok(m.AppendTokens(3, Toks(1000, 32))); // needs 8: 1 free + evict 7 cached
        Assert.Equal(9 + 8, counts.GetValueOrDefault(CacheEventKind.Allocated));
        Assert.Equal(2, counts.GetValueOrDefault(CacheEventKind.Reused));
        Assert.Equal(7, counts.GetValueOrDefault(CacheEventKind.Evicted));
        Assert.Equal(
            new[] { PressureLevel.High, PressureLevel.Critical, PressureLevel.Normal, PressureLevel.Critical },
            levels);
        Assert.Equal(PressureLevel.Critical, m.Stats().Pressure);
        Assert.True(m.Validate());
    }

    [Fact]
    public void ClearCachedReturnsCachedBlocksToTheFreeList()
    {
        var m = new KvCacheManager(Cfg(8));
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Toks(0, 12)));
        Ok(m.FreeSequence(1));
        Assert.Equal(3, m.ClearCached());
        Assert.Equal(8, m.Stats().FreeBlocks);
        Assert.Equal(0, m.MatchPrefix(KFp, Toks(0, 12)));
        Assert.True(m.Validate());
    }

    [Fact]
    public void ZeroCapacityPoolIsValidAndRejectsAppends()
    {
        var m = new KvCacheManager(Cfg(0));
        Ok(m.AddSequence(1));
        Assert.Equal(KvCacheError.Unavailable, m.AppendTokens(1, Toks(0, 1)).Code);
        Assert.Equal(PressureLevel.Normal, m.Pressure);
        Assert.Equal(0.0, m.Stats().Utilization);
        Assert.True(m.Validate());
    }

    [Theory]
    [InlineData(EvictionPolicyKind.PriorityAware)]
    [InlineData(EvictionPolicyKind.Lru)]
    public void StressRandomizedOpsKeepInvariantsAndContents(EvictionPolicyKind kind)
    {
        var rng = new Random(1234);
        var m = new KvCacheManager(Cfg(24, 4), EvictionPolicies.Create(kind));
        var shadow = new SortedDictionary<ulong, List<int>>();
        ulong next = 1;
        int okOps = 0;
        int rejected = 0;
        for (int step = 0; step < 4000; ++step)
        {
            int op = rng.Next(100);
            if (shadow.Count == 0 || op < 10)
            {
                ulong newId = next++;
                Ok(m.AddSequence(newId, Opts((byte)rng.Next(4))));
                shadow[newId] = [];
                // A shared system prompt drives prefix reuse.
                int[] p = Toks(0, 4 * rng.Next(4));
                if (m.AppendTokens(newId, p).IsOk)
                {
                    shadow[newId] = [.. p];
                    ++okOps;
                }
                else
                {
                    ++rejected;
                }

                continue;
            }

            KeyValuePair<ulong, List<int>> entry = shadow.ElementAt(rng.Next(shadow.Count));
            ulong id = entry.Key;
            List<int> expected = entry.Value;
            if (op < 55)
            {
                int[] t = new int[1 + rng.Next(7)];
                for (int i = 0; i < t.Length; i++)
                {
                    t[i] = rng.Next(3); // tiny vocabulary -> content collisions
                }

                int before = m.NumTokens(id);
                if (m.AppendTokens(id, t).IsOk)
                {
                    expected.AddRange(t);
                    ++okOps;
                }
                else
                {
                    ++rejected;
                    Assert.Equal(before, m.NumTokens(id));
                    Assert.Equal(expected.ToArray(), Contents(m, id));
                    // Scheduler-style preemption: drop the sequence to relieve pressure.
                    Ok(m.FreeSequence(id));
                    shadow.Remove(id);
                }
            }
            else if (op < 62)
            {
                ulong child = next++;
                Ok(m.ForkSequence(id, child));
                shadow[child] = [.. expected];
            }
            else if (op < 74)
            {
                int len = expected.Count == 0 ? 0 : rng.Next(expected.Count + 1);
                Ok(m.Truncate(id, len));
                expected.RemoveRange(len, expected.Count - len);
            }
            else
            {
                Ok(m.FreeSequence(id));
                shadow.Remove(id);
            }

            if (step % 7 == 0)
            {
                _ = m.TakePendingCopies();
            }

            Assert.True(m.Validate());
        }

        foreach ((ulong id, List<int> t) in shadow)
        {
            Assert.Equal(t.ToArray(), Contents(m, id));
        }

        Assert.True(okOps > 1000);
        Assert.True(rejected > 0); // pool pressure actually exercised
        KvCacheStats s = m.Stats();
        Assert.True(s.PrefixHitBlocks > 0);
        Assert.True(s.CopyOnWrite > 0);
        Assert.True(s.Evictions > 0);
        foreach (ulong id in shadow.Keys)
        {
            Ok(m.FreeSequence(id));
        }

        Assert.Equal(0, m.Stats().PinnedBlocks);
        Assert.True(m.Validate());
    }

    [Fact]
    public void CapacityAdapterReportsOnlyUnmaterialisedReservations()
    {
        // Port of src/engine/kv_capacity_adapter.hpp's contract (no upstream test file).
        var m = new KvCacheManager(Cfg(10));
        var adapter = new KvCacheCapacityAdapter(m);
        var released = new List<ulong>();
        adapter.ReleaseHook = id =>
        {
            released.Add(id);
            Ok(m.FreeSequence(id));
        };
        Assert.Equal(4u, adapter.BlockSizeTokens);
        Assert.Equal(10UL, adapter.TotalBlocks);
        Assert.True(adapter.TryReserve(1, 3));
        Assert.Equal(7UL, adapter.FreeBlocks);
        Ok(m.AddSequence(1, Opts()));
        Ok(m.AppendTokens(1, Toks(0, 8))); // two of the three reserved blocks materialised
        Assert.Equal(1UL, adapter.Outstanding);
        Assert.Equal(7UL, adapter.FreeBlocks);
        Assert.False(adapter.TryReserve(2, 8));
        Assert.True(adapter.TryReserve(2, 7));
        Assert.Equal(0UL, adapter.FreeBlocks);
        adapter.Release(1);
        Assert.Equal(1UL, Assert.Single(released));
        Assert.Equal(0UL, adapter.BlocksHeld(1));
        Assert.Equal(3UL, adapter.FreeBlocks); // 10 available (2 cached) minus 7 outstanding
    }
}
