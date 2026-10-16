// Ported from Krilliac/Sonder-Inference src/cache/tests/test_eviction_policy.cpp @1c4c152 (Apache-2.0)

using ArcaneCore.World.Playerbots.Chat.Inference.Scheduling.Cache;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference.Scheduling;

public sealed class EvictionPolicyTests
{
    private static EvictionCandidate Cand(uint b, ulong lastUse, byte prio = 0, uint depth = 0, ulong recompute = 0) =>
        new(b, lastUse, prio, depth, recompute);

    [Fact]
    public void LruEvictsLeastRecentlyUsedFirst()
    {
        var p = new LruEvictionPolicy();
        Assert.True(p.IsEmpty);
        p.Insert(Cand(1, 30));
        p.Insert(Cand(2, 10));
        p.Insert(Cand(3, 20));
        Assert.Equal(3, p.Count);
        Assert.Equal(2u, p.PopVictim());
        Assert.Equal(3u, p.PopVictim());
        Assert.Equal(1u, p.PopVictim());
        Assert.Null(p.PopVictim());
    }

    [Fact]
    public void LruEraseAndReinsertUpdatesPosition()
    {
        var p = new LruEvictionPolicy();
        p.Insert(Cand(1, 1));
        p.Insert(Cand(2, 2));
        p.Erase(1);
        p.Erase(99); // unknown is a no-op
        Assert.Equal(1, p.Count);
        p.Insert(Cand(1, 3));
        p.Insert(Cand(2, 5)); // reinsert replaces the old entry
        Assert.Equal(2, p.Count);
        Assert.Equal(1u, p.PopVictim());
        Assert.Equal(2u, p.PopVictim());
    }

    [Fact]
    public void LruTiesBrokenByInsertionOrder()
    {
        var p = new LruEvictionPolicy();
        p.Insert(Cand(7, 5));
        p.Insert(Cand(3, 5));
        Assert.Equal(7u, p.PopVictim());
        Assert.Equal(3u, p.PopVictim());
    }

    [Fact]
    public void PriorityLowestPriorityEvictedFirstRegardlessOfRecency()
    {
        var p = new PriorityAwareEvictionPolicy();
        p.Insert(Cand(1, 1, prio: 5));
        p.Insert(Cand(2, 100, prio: 1));
        p.Insert(Cand(3, 50, prio: 9));
        Assert.Equal(2u, p.PopVictim());
        Assert.Equal(1u, p.PopVictim());
        Assert.Equal(3u, p.PopVictim());
    }

    [Fact]
    public void PriorityDeeperLeafBlocksBeforeRootsAtEqualPriority()
    {
        var p = new PriorityAwareEvictionPolicy();
        p.Insert(Cand(10, 1, 0, depth: 0));
        p.Insert(Cand(11, 2, 0, depth: 1));
        p.Insert(Cand(12, 3, 0, depth: 2));
        Assert.Equal(12u, p.PopVictim());
        Assert.Equal(11u, p.PopVictim());
        Assert.Equal(10u, p.PopVictim());
    }

    [Fact]
    public void PriorityCheaperRecomputeThenLruAsTieBreakers()
    {
        var p = new PriorityAwareEvictionPolicy();
        p.Insert(Cand(1, 1, 0, 3, recompute: 64));
        p.Insert(Cand(2, 9, 0, 3, recompute: 16));
        p.Insert(Cand(3, 5, 0, 3, recompute: 16));
        Assert.Equal(3u, p.PopVictim());
        Assert.Equal(2u, p.PopVictim());
        Assert.Equal(1u, p.PopVictim());
    }

    [Fact]
    public void PriorityEraseRemovesCandidate()
    {
        var p = new PriorityAwareEvictionPolicy();
        p.Insert(Cand(1, 1));
        p.Insert(Cand(2, 2));
        p.Erase(1);
        Assert.Equal(1, p.Count);
        Assert.Equal(2u, p.PopVictim());
        Assert.True(p.IsEmpty);
    }

    [Fact]
    public void FactoryNames()
    {
        Assert.Equal("lru", EvictionPolicies.Create(EvictionPolicyKind.Lru).Name);
        Assert.Equal("priority_lru", EvictionPolicies.Create(EvictionPolicyKind.PriorityAware).Name);
    }
}
