using ArcaneCore.World.Playerbots.Chat.Inference.Scheduling;

namespace ArcaneCore.World.Playerbots.Chat.Inference.Serving;

/// <summary>
/// The scheduler's KV capacity (<see cref="IKvCapacity"/>) as the engine really has it: <see cref="Slots"/> generation slots, each a
/// contiguous KV cache of <see cref="SlotTokens"/> positions owned by one sequence at a time. A reservation takes a whole free slot
/// (a sequence can never span two), so admission blocks exactly when every slot is busy, and the scheduler's priority preemption
/// frees a slot by preempting a running lower-class sequence. The slot a sequence holds is <see cref="SlotOf"/>. Engine loop only.
/// </summary>
internal sealed class SlotKvCapacity : IKvCapacity
{
    private readonly ulong[] _owner; // 0 = free (request ids start at 1)
    private readonly ulong _blocksPerSlot;

    public SlotKvCapacity(int slots, int slotTokens, uint blockSizeTokens = 16)
    {
        if (slots < 1) throw new ArgumentOutOfRangeException(nameof(slots));
        if (slotTokens < (int)blockSizeTokens) throw new ArgumentOutOfRangeException(nameof(slotTokens));
        _owner = new ulong[slots];
        BlockSizeTokens = blockSizeTokens;
        SlotTokens = slotTokens;
        _blocksPerSlot = (ulong)slotTokens / blockSizeTokens;
    }

    public int Slots => _owner.Length;

    public int SlotTokens { get; }

    public uint BlockSizeTokens { get; }

    /// <summary>One slot's worth: a request needing more can never run (the scheduler rejects it as never fitting).</summary>
    public ulong TotalBlocks => _blocksPerSlot;

    /// <summary>A whole slot's blocks when one is free, else none.</summary>
    public ulong FreeBlocks => FreeSlots > 0 ? _blocksPerSlot : 0;

    public int FreeSlots
    {
        get
        {
            int free = 0;
            foreach (ulong owner in _owner)
                if (owner == 0) free++;
            return free;
        }
    }

    public bool TryReserve(ulong id, ulong blocks)
    {
        if (id == 0) return false;
        if (SlotOf(id) >= 0) return blocks <= _blocksPerSlot; // the whole slot is already the sequence's
        if (blocks > _blocksPerSlot) return false;
        for (int i = 0; i < _owner.Length; i++)
        {
            if (_owner[i] != 0) continue;
            _owner[i] = id;
            return true;
        }

        return false;
    }

    public void Release(ulong id)
    {
        int slot = SlotOf(id);
        if (slot >= 0) _owner[slot] = 0;
    }

    public ulong BlocksHeld(ulong id) => SlotOf(id) >= 0 ? _blocksPerSlot : 0;

    /// <summary>Whether no sequence holds slot <paramref name="slot"/>.</summary>
    public bool IsFree(int slot) => _owner[slot] == 0;

    /// <summary>The slot <paramref name="id"/> holds, or -1.</summary>
    public int SlotOf(ulong id)
    {
        if (id == 0) return -1;
        for (int i = 0; i < _owner.Length; i++)
            if (_owner[i] == id) return i;
        return -1;
    }
}
