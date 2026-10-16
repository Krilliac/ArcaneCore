// Ported from Krilliac/Sonder-Inference src/scheduler/include/sonder/inference/scheduler/kv_capacity.hpp @1c4c152 (Apache-2.0)
// Ported from Krilliac/Sonder-Inference src/scheduler/src/kv_capacity.cpp @1c4c152 (Apache-2.0)
//
// Abstract KV-capacity interface used for admission control. The scheduler never touches the
// cache implementation; the engine adapts the real KV block manager to this interface
// (KvCacheCapacityAdapter), tests use FixedKvCapacity.

namespace ArcaneCore.World.Playerbots.Chat.Inference.Scheduling;

public interface IKvCapacity
{
    /// <summary>Tokens stored per logical KV block.</summary>
    uint BlockSizeTokens { get; }

    /// <summary>Total blocks the scheduler may ever use (for "never fits" checks).</summary>
    ulong TotalBlocks { get; }

    /// <summary>Blocks currently free for new reservations.</summary>
    ulong FreeBlocks { get; }

    /// <summary>Reserve <paramref name="blocks"/> additional blocks for <paramref name="id"/>. All-or-nothing.</summary>
    bool TryReserve(ulong id, ulong blocks);

    /// <summary>Release every block held by <paramref name="id"/> (no-op if none).</summary>
    void Release(ulong id);

    /// <summary>Blocks currently held by <paramref name="id"/>.</summary>
    ulong BlocksHeld(ulong id);
}

public static class KvBlocks
{
    /// <summary>Blocks needed to hold <paramref name="tokens"/> tokens.</summary>
    public static ulong ForTokens(uint tokens, uint blockSize) =>
        blockSize == 0 ? 0 : ((ulong)tokens + blockSize - 1) / blockSize;
}

/// <summary>Simple fixed-size pool; reference implementation for tests and simulation.</summary>
public sealed class FixedKvCapacity(ulong totalBlocks, uint blockSizeTokens) : IKvCapacity
{
    private readonly Dictionary<ulong, ulong> _held = [];

    public uint BlockSizeTokens { get; } = blockSizeTokens;

    public ulong TotalBlocks { get; } = totalBlocks;

    public ulong FreeBlocks => TotalBlocks - UsedBlocks;

    public ulong UsedBlocks { get; private set; }

    public ulong PeakUsedBlocks { get; private set; }

    public bool TryReserve(ulong id, ulong blocks)
    {
        if (blocks == 0)
        {
            return true;
        }

        if (blocks > TotalBlocks - UsedBlocks)
        {
            return false;
        }

        UsedBlocks += blocks;
        PeakUsedBlocks = Math.Max(PeakUsedBlocks, UsedBlocks);
        _held[id] = _held.GetValueOrDefault(id) + blocks;
        return true;
    }

    public void Release(ulong id)
    {
        if (_held.Remove(id, out ulong held))
        {
            UsedBlocks -= held;
        }
    }

    public ulong BlocksHeld(ulong id) => _held.GetValueOrDefault(id);
}
