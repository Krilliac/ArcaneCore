// Ported from Krilliac/Sonder-Inference src/cache/tests/test_checkpoints.cpp @1c4c152 (Apache-2.0)
// Ported from Krilliac/Sonder-Inference src/cache/tests/test_kv_cache_manager.cpp @1c4c152 (Apache-2.0) (hybrid cases)
//
// Recurrent-state checkpoints for hybrid/recurrent architectures: prefix reuse stops at an exact
// saved checkpoint. ArcaneCore's first models are attention-only, so these guard the general case.

using ArcaneCore.World.Playerbots.Chat.Inference.Scheduling.Cache;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference.Scheduling;

public sealed class KvCacheCheckpointTests
{
    private static readonly CacheFingerprint Fp = new FingerprintBuilder().Add("checkpoint-model").Add("rev1").Build();
    private static readonly CacheFingerprint KFp = new FingerprintBuilder().Add("model-a").Add("rev1").Add(16UL).Build();

    private static int[] Tokens(int first, int count) => KvCacheManagerTests.Toks(first, count);

    private static int[] Contents(KvCacheManager m, ulong id) => KvCacheManagerTests.Contents(m, id);

    private static KvCacheConfig Config(uint blocks = 16, uint blockSize = 4) => new()
    {
        NumBlocks = blocks,
        BlockSizeTokens = blockSize,
        BytesPerToken = 16,
    };

    private static SequenceOptions Recurrent() => new(Fp, 0, ModelArchitecture.Hybrid);

    private static void Ok(KvCacheStatus status) => Assert.True(status.IsOk, status.Message);

    // ---- hybrid cases from test_kv_cache_manager.cpp

    [Fact]
    public void HybridPrefixReuseStopsAtTheNearestRecurrentCheckpoint()
    {
        var m = new KvCacheManager(KvCacheManagerTests.Cfg(8));
        var hybrid = new SequenceOptions(KFp, 0, ModelArchitecture.Hybrid);
        Ok(m.AddSequence(1, hybrid));
        Ok(m.AppendTokens(1, Tokens(0, 8)));
        Ok(m.SaveCheckpoint(1, 4, 128));
        Ok(m.FreeSequence(1));

        Assert.Equal(4, m.MatchPrefix(KFp, Tokens(0, 8), ModelArchitecture.Hybrid));
        Ok(m.AddSequence(2, hybrid));
        Ok(m.AppendTokens(2, Tokens(0, 8), out AppendResult r));
        Assert.Equal(4, r.TokensReused);
        Assert.Equal(1, r.BlocksReused);
        Assert.Equal(1, r.BlocksAllocated);
        Assert.Equal(4UL, m.Stats().CheckpointLimitedTokens);
        Assert.True(m.Validate());
    }

    [Fact]
    public void HybridLaterCheckpointAuthorizesTheCompleteEarlierPrefix()
    {
        var m = new KvCacheManager(KvCacheManagerTests.Cfg(8));
        var hybrid = new SequenceOptions(KFp, 0, ModelArchitecture.Hybrid);
        Ok(m.AddSequence(1, hybrid));
        Ok(m.AppendTokens(1, Tokens(0, 8)));
        Ok(m.SaveCheckpoint(1, 8, 128));
        Ok(m.FreeSequence(1));
        Assert.Equal(8, m.MatchPrefix(KFp, Tokens(0, 8), ModelArchitecture.Hybrid));
        Assert.Equal(8, m.MatchPrefix(KFp, Tokens(0, 12), ModelArchitecture.Hybrid));
        Ok(m.AddSequence(2, hybrid));
        Ok(m.AppendTokens(2, Tokens(0, 8), out AppendResult r));
        Assert.Equal(8, r.TokensReused);
        Assert.True(r.Checkpoint.HasValue);
    }

    [Fact]
    public void HybridUnalignedCheckpointNeverAuthorizesAnEarlierPosition()
    {
        var m = new KvCacheManager(KvCacheManagerTests.Cfg(8));
        var hybrid = new SequenceOptions(KFp, 0, ModelArchitecture.Hybrid);
        Ok(m.AddSequence(1, hybrid));
        Ok(m.AppendTokens(1, Tokens(0, 8)));
        Ok(m.SaveCheckpoint(1, 6, 128));
        Assert.Equal(0, m.MatchPrefix(KFp, Tokens(0, 4), ModelArchitecture.Hybrid));
        Assert.Equal(6, m.MatchPrefix(KFp, Tokens(0, 8), ModelArchitecture.Hybrid));
        Assert.Equal(1, m.Stats().Checkpoints);
    }

    [Fact]
    public void HybridNoCheckpointMeansNoPrefixReuseAndCheckpointsAreBounded()
    {
        KvCacheConfig c = KvCacheManagerTests.Cfg(8);
        c.MaxCheckpointBytes = 10;
        var m = new KvCacheManager(c);
        var recurrent = new SequenceOptions(KFp, 0, ModelArchitecture.Recurrent);
        Ok(m.AddSequence(1, recurrent));
        Ok(m.AppendTokens(1, Tokens(0, 8)));
        Assert.Equal(0, m.MatchPrefix(KFp, Tokens(0, 8), ModelArchitecture.Recurrent));
        Ok(m.SaveCheckpoint(1, 4, 8));
        Ok(m.SaveCheckpoint(1, 8, 8));
        Assert.True(m.Stats().CheckpointBytes <= 10);
        Assert.Equal(1UL, m.Stats().CheckpointEvictions);
        Assert.True(m.Validate());
    }

    // ---- test_checkpoints.cpp

    [Fact]
    public void Cp8IsSufficientForAnEightTokenPrefix()
    {
        var m = new KvCacheManager(Config());
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 8)));
        Ok(m.SaveCheckpoint(1, 8, 64));
        Ok(m.FreeSequence(1));
        Assert.Equal(8, m.MatchPrefix(Fp, Tokens(0, 8), ModelArchitecture.Hybrid));
    }

    [Fact]
    public void PartialPositionIsExactAndNeverRounded()
    {
        var m = new KvCacheManager(Config());
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 12)));
        Ok(m.SaveCheckpoint(1, 6, 64));
        Ok(m.FreeSequence(1));
        Assert.Equal(0, m.MatchPrefix(Fp, Tokens(0, 4), ModelArchitecture.Hybrid));
        Assert.Equal(6, m.MatchPrefix(Fp, Tokens(0, 12), ModelArchitecture.Hybrid));
    }

    [Fact]
    public void DivergentLineagesDoNotBorrowEqualLengthState()
    {
        var m = new KvCacheManager(Config());
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 8)));
        Ok(m.SaveCheckpoint(1, 8, 64));
        Ok(m.AddSequence(2, Recurrent()));
        Ok(m.AppendTokens(2, Tokens(100, 8)));
        Ok(m.SaveCheckpoint(2, 8, 64));
        Ok(m.FreeSequence(1));
        Ok(m.FreeSequence(2));

        Assert.Equal(8, m.MatchPrefix(Fp, Tokens(0, 8), ModelArchitecture.Hybrid));
        Assert.Equal(8, m.MatchPrefix(Fp, Tokens(100, 8), ModelArchitecture.Hybrid));
        Assert.Equal(0, m.MatchPrefix(Fp, Tokens(50, 8), ModelArchitecture.Hybrid));
        Ok(m.AddSequence(3, Recurrent()));
        Ok(m.AppendTokens(3, Tokens(100, 8), out AppendResult result));
        Assert.True(result.Checkpoint.HasValue);
        Assert.Equal(2UL, result.Checkpoint.GetValueOrDefault().Owner);
        Assert.Equal(8, result.Checkpoint.GetValueOrDefault().Position);
    }

    [Fact]
    public void ReplacementResizeParticipatesInByteEviction()
    {
        KvCacheConfig cfg = Config();
        cfg.MaxCheckpointBytes = 100;
        var m = new KvCacheManager(cfg);
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 8)));
        Ok(m.SaveCheckpoint(1, 4, 60));
        Ok(m.SaveCheckpoint(1, 4, 90));
        Assert.Equal(90UL, m.Stats().CheckpointBytes);
        Ok(m.SaveCheckpoint(1, 8, 40));
        Assert.True(m.Stats().CheckpointBytes <= 100);
        Assert.True(m.Stats().CheckpointEvictions >= 1);
    }

    [Fact]
    public void ZeroCapacityDisablesSaving()
    {
        KvCacheConfig cfg = Config();
        cfg.MaxCheckpointBytes = 0;
        var m = new KvCacheManager(cfg);
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 4)));
        Assert.Equal(KvCacheError.InvalidArgument, m.SaveCheckpoint(1, 4, 1).Code);
        Assert.Equal(0, m.Stats().Checkpoints);
        Assert.Equal(0UL, m.Stats().CheckpointBytes);
    }

    [Fact]
    public void FailedAppendLeavesSequenceAndCountersUnchanged()
    {
        var m = new KvCacheManager(Config(2));
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 4)));
        KvCacheStats before = m.Stats();
        int length = m.NumTokens(1);
        Assert.Equal(KvCacheError.Unavailable, m.AppendTokens(1, Tokens(4, 12)).Code);
        Assert.Equal(length, m.NumTokens(1));
        Assert.Equal(before.BlocksAllocated, m.Stats().BlocksAllocated);
        Assert.Equal(before.AllocationFailures + 1, m.Stats().AllocationFailures);
        Assert.True(m.Validate());
    }

    [Fact]
    public void ZeroTokenAppendDoesNotSelectACheckpoint()
    {
        var m = new KvCacheManager(Config());
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, ReadOnlySpan<int>.Empty, out AppendResult result));
        Assert.False(result.Checkpoint.HasValue);
        Assert.Equal(0, result.TokensCheckpointLimited);
    }

    [Fact]
    public void RemovalPreventsSubsequentReuse()
    {
        var m = new KvCacheManager(Config());
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 4)));
        Ok(m.SaveCheckpoint(1, 4, 32));
        Ok(m.RemoveCheckpoint(1, 4));
        Ok(m.FreeSequence(1));
        Assert.Equal(0, m.MatchPrefix(Fp, Tokens(0, 4), ModelArchitecture.Hybrid));
    }

    [Fact]
    public void OwnerReuseDoesNotInheritAPreviousOwnersState()
    {
        var m = new KvCacheManager(Config());
        Ok(m.AddSequence(7, Recurrent()));
        Ok(m.AppendTokens(7, Tokens(0, 4)));
        Ok(m.SaveCheckpoint(7, 4, 32));
        Ok(m.FreeSequence(7));
        Ok(m.AddSequence(7, Recurrent()));
        Assert.Equal(0, m.MatchPrefix(Fp, Tokens(0, 4), ModelArchitecture.Hybrid));
    }

    [Fact]
    public void TruncateRequiresCheckpointBoundaryOrZero()
    {
        var m = new KvCacheManager(Config());
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 8)));
        Ok(m.SaveCheckpoint(1, 4, 32));
        Assert.Equal(KvCacheError.InvalidArgument, m.Truncate(1, 2).Code);
        Ok(m.Truncate(1, 4));
        Ok(m.Truncate(1, 0));
    }

    [Fact]
    public void ForkRequiresASavedCurrentState()
    {
        var m = new KvCacheManager(Config());
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 4)));
        Assert.Equal(KvCacheError.InvalidState, m.ForkSequence(1, 2).Code);
        Ok(m.SaveCheckpoint(1, 4, 32));
        Ok(m.ForkSequence(1, 2));
    }

    [Fact]
    public void BlockEvictionInvalidatesDependentState()
    {
        var m = new KvCacheManager(Config(1));
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 4)));
        Ok(m.SaveCheckpoint(1, 4, 32));
        Ok(m.FreeSequence(1));
        Ok(m.AddSequence(2, Recurrent()));
        Ok(m.AppendTokens(2, Tokens(100, 4)));
        Assert.Equal(0, m.MatchPrefix(Fp, Tokens(0, 4), ModelArchitecture.Hybrid));
    }

    [Fact]
    public void AttentionOnlyDefaultPreservesCacheGaugesAndCounters()
    {
        var implicitArch = new KvCacheManager(Config());
        var explicitArch = new KvCacheManager(Config());
        Ok(implicitArch.AddSequence(1, new SequenceOptions(Fp, 0)));
        Ok(explicitArch.AddSequence(1, new SequenceOptions(Fp, 0, ModelArchitecture.AttentionOnly)));
        Ok(implicitArch.AppendTokens(1, Tokens(0, 8)));
        Ok(explicitArch.AppendTokens(1, Tokens(0, 8)));
        Ok(implicitArch.FreeSequence(1));
        Ok(explicitArch.FreeSequence(1));
        KvCacheStats a = implicitArch.Stats();
        KvCacheStats b = explicitArch.Stats();
        Assert.Equal(a.CachedBlocks, b.CachedBlocks);
        Assert.Equal(a.CachedBytes, b.CachedBytes);
        Assert.Equal(a.PrefixHitBlocks, b.PrefixHitBlocks);
        Assert.Equal(a.PrefixMissLookups, b.PrefixMissLookups);
        Assert.Equal(a.AvoidedPrefillTokens, b.AvoidedPrefillTokens);
        Assert.Equal(a.Evictions, b.Evictions);
        Assert.Equal(0UL, a.CheckpointBytes);
        Assert.Equal(0UL, b.CheckpointBytes);
    }

    [Fact]
    public void ChunkedPrefillDoesNotRetroactivelyClaimALaterCheckpoint()
    {
        var m = new KvCacheManager(Config());
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 8)));
        Ok(m.SaveCheckpoint(1, 8, 32));
        Ok(m.FreeSequence(1));
        Ok(m.AddSequence(2, Recurrent()));
        Ok(m.AppendTokens(2, Tokens(0, 4), out AppendResult first));
        Assert.Equal(0, first.TokensReused);
        Ok(m.AppendTokens(2, Tokens(4, 4), out AppendResult second));
        Assert.Equal(0, second.TokensReused);
    }

    [Fact]
    public void ChunkedPrefillCanReuseSuccessiveSavedBoundaries()
    {
        var m = new KvCacheManager(Config());
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 4)));
        Ok(m.SaveCheckpoint(1, 4, 32));
        Ok(m.AppendTokens(1, Tokens(4, 4)));
        Ok(m.SaveCheckpoint(1, 8, 32));
        Ok(m.FreeSequence(1));
        Ok(m.AddSequence(2, Recurrent()));
        Ok(m.AppendTokens(2, Tokens(0, 4), out AppendResult first));
        Assert.Equal(4, first.TokensReused);
        Ok(m.AppendTokens(2, Tokens(4, 4), out AppendResult second));
        Assert.Equal(4, second.TokensReused);
    }

    [Fact]
    public void PartialAppendReusesThroughCp6AndReportsItsOwner()
    {
        var m = new KvCacheManager(Config());
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 12)));
        Ok(m.SaveCheckpoint(1, 6, 32));
        Ok(m.FreeSequence(1));
        Ok(m.AddSequence(2, Recurrent()));
        Ok(m.AppendTokens(2, Tokens(0, 12), out AppendResult result));
        Assert.Equal(6, result.TokensReused);
        Assert.True(result.Checkpoint.HasValue);
        Assert.Equal(1UL, result.Checkpoint.GetValueOrDefault().Owner);
        Assert.Equal(6, result.Checkpoint.GetValueOrDefault().Position);
        Assert.Equal(12, m.NumTokens(2));
        Assert.Equal(Tokens(0, 12), Contents(m, 2));
        Assert.Equal(3, m.BlockTable(2).Length);
        Assert.Equal(0, m.BlockTokens(m.BlockTable(2)[0])[0]);
        Assert.Equal(4, m.BlockTokens(m.BlockTable(2)[1])[0]);
        Assert.Equal(8, m.BlockTokens(m.BlockTable(2)[2])[0]);
        Assert.True(m.Validate());
    }

    [Fact]
    public void InsufficientPoolRecomputesInsteadOfOverallocating()
    {
        var m = new KvCacheManager(Config(3));
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 12)));
        Ok(m.SaveCheckpoint(1, 6, 32));
        Ok(m.FreeSequence(1));
        Ok(m.AddSequence(2, Recurrent()));
        Ok(m.AppendTokens(2, Tokens(0, 12), out AppendResult result));
        Assert.Equal(0, result.TokensReused);
        Assert.True(m.Stats().PinnedBlocks <= 3);
        Assert.True(m.Validate());
    }

    [Fact]
    public void PartialCurrentStateRemainsTheMatchingLineage()
    {
        var m = new KvCacheManager(Config());
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 6)));
        Ok(m.SaveCheckpoint(1, 6, 32));
        Ok(m.AppendTokens(1, Tokens(6, 2)));
        Assert.Equal(6, m.MatchPrefix(Fp, Tokens(0, 8), ModelArchitecture.Hybrid));
        Assert.True(m.Validate());
    }

    [Fact]
    public void ReuseRefreshesTheSelectedCheckpointBeforeLruEviction()
    {
        KvCacheConfig cfg = Config();
        cfg.MaxCheckpointBytes = 100;
        var m = new KvCacheManager(cfg);
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 12)));
        Ok(m.SaveCheckpoint(1, 4, 40));
        Ok(m.SaveCheckpoint(1, 8, 40));
        Ok(m.AddSequence(2, Recurrent()));
        Ok(m.AppendTokens(2, Tokens(0, 4), out AppendResult _));
        Ok(m.SaveCheckpoint(1, 12, 40));
        Assert.True(m.Stats().CheckpointEvictions >= 1);
        Assert.True(m.CheckpointAt(1, 4).HasValue);
        Assert.False(m.CheckpointAt(1, 8).HasValue);
        Assert.True(m.Validate());
    }

    [Fact]
    public void ArchitectureIsPartOfPrefixIdentity()
    {
        var m = new KvCacheManager(Config());
        Ok(m.AddSequence(1, new SequenceOptions(Fp, 0, ModelArchitecture.AttentionOnly)));
        Ok(m.AppendTokens(1, Tokens(0, 8)));
        Ok(m.FreeSequence(1));
        Assert.Equal(0, m.MatchPrefix(Fp, Tokens(0, 8), ModelArchitecture.Hybrid));
    }

    [Fact]
    public void MaximumStateSizeAndUInt64BudgetAreSafe()
    {
        KvCacheConfig cfg = Config();
        cfg.MaxCheckpointBytes = ulong.MaxValue;
        var m = new KvCacheManager(cfg);
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 4)));
        Assert.Equal(KvCacheError.InvalidArgument, m.SaveCheckpoint(1, 4, 0).Code);
        Ok(m.SaveCheckpoint(1, 4, ulong.MaxValue));
        Assert.Equal(ulong.MaxValue, m.Stats().CheckpointBytes);
        Ok(m.SaveCheckpoint(1, 4, ulong.MaxValue));
        Assert.Equal(ulong.MaxValue, m.Stats().CheckpointBytes);
    }

    [Fact]
    public void EvictingTheParentSnapshotPreservesALiveForkButRequiresANewSnapshot()
    {
        var m = new KvCacheManager(Config());
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 4)));
        Ok(m.SaveCheckpoint(1, 4, 32));
        Ok(m.ForkSequence(1, 2));
        Ok(m.FreeSequence(1));
        Ok(m.RemoveCheckpoint(1, 4));
        Assert.Equal(Tokens(0, 4), Contents(m, 2));
        Assert.True(m.Validate());
        Assert.Equal(KvCacheError.InvalidState, m.ForkSequence(2, 3).Code);
        Ok(m.SaveCheckpoint(2, 4, 32));
        Ok(m.ForkSequence(2, 3));
        Assert.True(m.Validate());
    }

    [Fact]
    public void RecycledChildIdIsRejectedBeforeCheckpointPurging()
    {
        var m = new KvCacheManager(Config());
        Ok(m.AddSequence(2, Recurrent()));
        Ok(m.AppendTokens(2, Tokens(0, 4)));
        Ok(m.SaveCheckpoint(2, 4, 32));
        Ok(m.FreeSequence(2));
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 4)));
        Assert.Equal(KvCacheError.InvalidState, m.ForkSequence(1, 2).Code);
        RecurrentStateCheckpoint? cp = m.CheckpointAt(1, 4);
        Assert.True(cp.HasValue);
        Assert.Equal(2UL, cp.GetValueOrDefault().Owner);
        Assert.False(m.HasSequence(2));
        Assert.True(m.Validate());
    }

    [Fact]
    public void CountCapEmitsTheFullEvictedSnapshotAndClearResetsGauges()
    {
        KvCacheConfig cfg = Config();
        cfg.MaxCheckpoints = 1;
        var m = new KvCacheManager(cfg);
        var evicted = new List<RecurrentStateCheckpoint>();
        m.SetEventListener(e =>
        {
            if (e.Kind == CacheEventKind.CheckpointEvicted && e.Checkpoint is { } cp)
            {
                evicted.Add(cp);
            }
        });
        Ok(m.AddSequence(1, Recurrent()));
        Ok(m.AppendTokens(1, Tokens(0, 8)));
        Ok(m.SaveCheckpoint(1, 4, 32));
        Ok(m.SaveCheckpoint(1, 8, 48));
        RecurrentStateCheckpoint first = Assert.Single(evicted);
        Assert.Equal(1UL, first.Owner);
        Assert.Equal(4, first.Position);
        Assert.Equal(32UL, first.StateSize);
        Assert.Equal(1, m.Stats().Checkpoints);
        Ok(m.FreeSequence(1));
        Assert.True(m.ClearCached() >= 1);
        Assert.Equal(0, m.Stats().Checkpoints);
        Assert.Equal(0UL, m.Stats().CheckpointBytes);
        Assert.Equal(m.Stats().TotalBlocks, m.Stats().FreeBlocks);
        Assert.True(m.Validate());
    }
}
