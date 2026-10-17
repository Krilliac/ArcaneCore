// Ported from Krilliac/Sonder-Inference src/engine/kv_capacity_adapter.hpp @1c4c152 (Apache-2.0)
//
// Adapter: KvCacheManager -> IKvCapacity.
//
// The scheduler admits work by reserving block *counts*; the cache allocates concrete blocks only
// when the engine appends tokens. This adapter keeps a reservation ledger per request and reports
// as free only the blocks the cache can still hand out (free + evictable) minus reservations not
// yet materialised as blocks in the request's sequence.
//
// Conventions:
// - The scheduler request id and the cache sequence id are the same value.
// - Release drops the reservation only. Sequence lifetime (AddSequence, FreeSequence, fork) stays
//   with the engine, so KV can outlive a reservation, e.g. for prefix reuse after completion.
// - An optional release hook lets the engine free the request's sequence synchronously inside
//   Release. The scheduler preempts victims one at a time and re-reads FreeBlocks after each
//   release, so blocks must become reusable immediately or it would preempt every running request.
// - Prefix-cache hits are not discounted; reservations are worst case.

using ArcaneCore.World.Playerbots.Chat.Inference.Scheduling.Cache;

namespace ArcaneCore.World.Playerbots.Chat.Inference.Scheduling;

public sealed class KvCacheCapacityAdapter(KvCacheManager manager) : IKvCapacity
{
    private readonly KvCacheManager _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    private readonly Dictionary<ulong, ulong> _held = [];

    /// <summary>
    /// Called after a reservation is dropped (preemption, completion, failure, cancellation). Runs
    /// on the scheduler's caller thread.
    /// </summary>
    public Action<ulong>? ReleaseHook { get; set; }

    public uint BlockSizeTokens => _manager.Config.BlockSizeTokens;

    public ulong TotalBlocks => _manager.Config.NumBlocks;

    public ulong FreeBlocks
    {
        get
        {
            ulong available = (ulong)_manager.AvailableBlocks;
            ulong pending = Outstanding;
            return available > pending ? available - pending : 0;
        }
    }

    /// <summary>Reserved blocks not yet backed by blocks in the request's sequence.</summary>
    public ulong Outstanding
    {
        get
        {
            ulong total = 0;
            foreach ((ulong id, ulong held) in _held)
            {
                ulong used = (ulong)_manager.BlockTable(id).Length;
                total += held > used ? held - used : 0;
            }

            return total;
        }
    }

    public bool TryReserve(ulong id, ulong blocks)
    {
        if (blocks > FreeBlocks)
        {
            return false;
        }

        _held[id] = _held.GetValueOrDefault(id) + blocks;
        return true;
    }

    public void Release(ulong id)
    {
        _held.Remove(id);
        ReleaseHook?.Invoke(id);
    }

    public ulong BlocksHeld(ulong id) => _held.GetValueOrDefault(id);
}
