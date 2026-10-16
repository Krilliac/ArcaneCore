// Ported from Krilliac/Sonder-Inference src/cache/include/sonder/inference/cache/eviction_policy.hpp @1c4c152 (Apache-2.0)
// Ported from Krilliac/Sonder-Inference src/cache/eviction_policy.cpp @1c4c152 (Apache-2.0)
//
// KV-cache module: pluggable eviction policies (LRU and priority-aware).

namespace ArcaneCore.World.Playerbots.Chat.Inference.Scheduling.Cache;

/// <summary>
/// Metadata the manager hands to an eviction policy when a block becomes evictable (reference
/// count dropped to zero while it is still reusable through the prefix index).
/// </summary>
/// <param name="Block">The evictable block.</param>
/// <param name="LastUse">Monotonic logical clock value of the last use.</param>
/// <param name="Priority">Highest priority of any sequence that referenced the block.</param>
/// <param name="Depth">
/// Position of the block in its prefix chain (0 = first block). Deeper blocks are cheaper to lose:
/// evicting a shallow block orphans the usefulness of every descendant.
/// </param>
/// <param name="RecomputeTokens">Estimated cost (tokens) to recompute this block and its missing ancestors.</param>
public readonly record struct EvictionCandidate(
    uint Block,
    ulong LastUse,
    byte Priority = 0,
    uint Depth = 0,
    ulong RecomputeTokens = 0);

/// <summary>
/// Pluggable eviction policy. Only blocks with reference count zero are ever offered; the manager
/// never asks a policy to evict a pinned block.
/// </summary>
public abstract class EvictionPolicy
{
    public abstract string Name { get; }

    public abstract int Count { get; }

    public bool IsEmpty => Count == 0;

    /// <summary>Block became evictable.</summary>
    public abstract void Insert(EvictionCandidate candidate);

    /// <summary>Block is no longer evictable (reused, or removed by the manager).</summary>
    public abstract void Erase(uint block);

    /// <summary>Choose (and forget) the next victim, or null when empty.</summary>
    public abstract uint? PopVictim();
}

/// <summary>Shared ordered-set mechanics; subclasses define the victim key.</summary>
public abstract class OrderedEvictionPolicy<TKey> : EvictionPolicy
    where TKey : IComparable<TKey>
{
    private readonly SortedSet<Entry> _order = new();
    private readonly Dictionary<uint, TKey> _index = [];
    private ulong _seq;

    public override int Count => _index.Count;

    protected abstract TKey MakeKey(EvictionCandidate candidate, ulong sequence);

    public override void Insert(EvictionCandidate candidate)
    {
        Erase(candidate.Block);
        TKey key = MakeKey(candidate, _seq++);
        _order.Add(new Entry(key, candidate.Block));
        _index.Add(candidate.Block, key);
    }

    public override void Erase(uint block)
    {
        if (_index.Remove(block, out TKey? key))
        {
            _order.Remove(new Entry(key!, block));
        }
    }

    public override uint? PopVictim()
    {
        if (_order.Count == 0)
        {
            return null;
        }

        Entry min = _order.Min;
        _order.Remove(min);
        _index.Remove(min.Block);
        return min.Block;
    }

    private readonly record struct Entry(TKey Key, uint Block) : IComparable<Entry>
    {
        public int CompareTo(Entry other)
        {
            int c = Key.CompareTo(other.Key);
            return c != 0 ? c : Block.CompareTo(other.Block);
        }
    }
}

/// <summary>LRU victim key: last use, then insertion order.</summary>
public readonly record struct LruEvictionKey(ulong LastUse, ulong Sequence) : IComparable<LruEvictionKey>
{
    public int CompareTo(LruEvictionKey other)
    {
        int c = LastUse.CompareTo(other.LastUse);
        return c != 0 ? c : Sequence.CompareTo(other.Sequence);
    }
}

/// <summary>Priority-aware victim key; smaller is evicted first (depth is inverted).</summary>
public readonly record struct PriorityEvictionKey(byte Priority, uint InverseDepth, ulong RecomputeTokens, ulong LastUse, ulong Sequence)
    : IComparable<PriorityEvictionKey>
{
    public int CompareTo(PriorityEvictionKey other)
    {
        int c = Priority.CompareTo(other.Priority);
        if (c != 0)
        {
            return c;
        }

        c = InverseDepth.CompareTo(other.InverseDepth);
        if (c != 0)
        {
            return c;
        }

        c = RecomputeTokens.CompareTo(other.RecomputeTokens);
        if (c != 0)
        {
            return c;
        }

        c = LastUse.CompareTo(other.LastUse);
        return c != 0 ? c : Sequence.CompareTo(other.Sequence);
    }
}

/// <summary>Classic least-recently-used eviction (ties broken by insertion order).</summary>
public sealed class LruEvictionPolicy : OrderedEvictionPolicy<LruEvictionKey>
{
    public override string Name => "lru";

    protected override LruEvictionKey MakeKey(EvictionCandidate candidate, ulong sequence) => new(candidate.LastUse, sequence);
}

/// <summary>
/// Priority-aware eviction. Victim order: lowest priority first, then deepest block in its prefix
/// chain (leaves before roots), then cheapest recompute, then least recently used. For bot chat
/// this keeps the shared system/grounding prompt (the chain roots) resident longest.
/// </summary>
public sealed class PriorityAwareEvictionPolicy : OrderedEvictionPolicy<PriorityEvictionKey>
{
    public override string Name => "priority_lru";

    protected override PriorityEvictionKey MakeKey(EvictionCandidate candidate, ulong sequence) =>
        new(candidate.Priority, uint.MaxValue - candidate.Depth, candidate.RecomputeTokens, candidate.LastUse, sequence);
}

public enum EvictionPolicyKind : byte
{
    Lru,
    PriorityAware,
}

public static class EvictionPolicies
{
    public static EvictionPolicy Create(EvictionPolicyKind kind) => kind switch
    {
        EvictionPolicyKind.Lru => new LruEvictionPolicy(),
        _ => new PriorityAwareEvictionPolicy(),
    };
}
