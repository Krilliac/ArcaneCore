// Ported from Krilliac/Sonder-Inference src/cache/include/sonder/inference/cache/kv_cache_manager.hpp @1c4c152 (Apache-2.0)
// Ported from Krilliac/Sonder-Inference src/cache/kv_cache_manager.cpp @1c4c152 (Apache-2.0)
//
// Logical block/page KV-cache manager. Pure policy and bookkeeping: the manager owns *logical*
// fixed-size blocks and per-sequence block tables and never touches tensor memory. The engine maps
// a block id onto its physical KV storage and applies the copy operations the manager reports.
//
// Why ArcaneCore wants it: every bot's prompt starts with the same system/grounding text and only
// the persona and conversation differ after it. Full blocks get a chained hash of (parent hash,
// fingerprint, tokens); a new request whose leading blocks match is served those blocks by
// reference, so the engine skips that much prefill (CachedPrefixTokens / AppendResult.TokensReused).
//
// - append is all-or-nothing: when the worst-case block need exceeds free plus evictable blocks it
//   returns Unavailable and changes nothing.
// - fork shares every block by reference count; writing into a shared partial tail allocates a
//   private copy and queues a BlockCopy for the engine (TakePendingCopies). Full blocks are immutable.
// - truncate rolls a sequence back; a rewritten tail is copied if shared, or removed from the prefix
//   index if exclusively owned.
// - a block whose reference count reaches zero stays in the prefix index as cached/evictable when
//   full, else returns to the free list. Cached blocks are evicted only when the free list is empty.
// - hybrid/recurrent architectures reuse only up to an exact saved recurrent-state checkpoint.
//
// Not thread-safe; the owning engine loop serialises access. Decode appends one token at a time
// without allocating except when a new block slot is first used.

using System.Runtime.InteropServices;

namespace ArcaneCore.World.Playerbots.Chat.Inference.Scheduling.Cache;

public sealed class KvCacheConfig
{
    /// <summary>Tokens per logical block (vLLM-style page size).</summary>
    public uint BlockSizeTokens { get; set; } = 16;

    /// <summary>Size of the block pool.</summary>
    public uint NumBlocks { get; set; }

    /// <summary>Engine-reported KV bytes per token (all layers). Accounting only.</summary>
    public ulong BytesPerToken { get; set; }

    /// <summary>Keep full blocks addressable by prefix hash after their sequences end.</summary>
    public bool EnablePrefixCaching { get; set; } = true;

    /// <summary>Pinned-utilisation threshold for <see cref="PressureLevel.High"/>.</summary>
    public double HighWatermark { get; set; } = 0.80;

    /// <summary>Pinned-utilisation threshold for <see cref="PressureLevel.Critical"/>.</summary>
    public double CriticalWatermark { get; set; } = 0.95;

    /// <summary>Maximum engine state bytes retained by checkpoints. Zero disables saving.</summary>
    public ulong MaxCheckpointBytes { get; set; } = 64UL * 1024UL * 1024UL;

    /// <summary>Bound on checkpoint bookkeeping independent of state sizes. Zero disables saving.</summary>
    public int MaxCheckpoints { get; set; } = 1024;

    public KvCacheConfig Clone() => (KvCacheConfig)MemberwiseClone();
}

public readonly record struct SequenceOptions(
    CacheFingerprint Fingerprint = default,
    byte Priority = 0,
    ModelArchitecture Architecture = ModelArchitecture.AttentionOnly);

/// <summary>
/// A restorable recurrent state associated with an exact prefix lineage. Position is exact,
/// including inside a block; it is never rounded down.
/// </summary>
public readonly record struct RecurrentStateCheckpoint(int Position, ulong StateSize, ulong Owner);

/// <summary>Result of <see cref="KvCacheManager.AppendTokens(ulong, ReadOnlySpan{int}, out AppendResult)"/>.</summary>
public struct AppendResult
{
    public int TokensAppended;

    /// <summary>Tokens satisfied by prefix-cache hits in this call (prefill can skip them).</summary>
    public int TokensReused;

    public int BlocksAllocated;
    public int BlocksReused;
    public int CopyOnWrite;
    public int TokensCheckpointLimited;
    public RecurrentStateCheckpoint? Checkpoint;
}

/// <summary>
/// A logical copy the engine must perform before writing to <see cref="Dst"/> (copy-on-write of a
/// shared, partially filled block).
/// </summary>
public readonly record struct BlockCopy(uint Src, uint Dst, uint NumTokens);

public enum CacheEventKind : byte
{
    Allocated,
    Reused,
    Evicted,
    CopyOnWrite,
    PressureChanged,
    CheckpointEvicted,
}

/// <summary>Telemetry event, emitted synchronously to the listener.</summary>
public readonly record struct CacheEvent(
    CacheEventKind Kind,
    ulong Sequence = 0,
    uint Block = CacheNames.InvalidBlock,
    uint SourceBlock = CacheNames.InvalidBlock,
    PressureLevel Pressure = PressureLevel.Normal,
    RecurrentStateCheckpoint? Checkpoint = null);

public struct KvCacheStats
{
    // Gauges
    public int TotalBlocks;

    /// <summary>Never used / fully released.</summary>
    public int FreeBlocks;

    /// <summary>Reference count 0, reusable via the prefix index, evictable.</summary>
    public int CachedBlocks;

    /// <summary>Reference count &gt; 0.</summary>
    public int PinnedBlocks;

    /// <summary>Reference count &gt; 1.</summary>
    public int SharedBlocks;

    public int Sequences;
    public uint BlockSizeTokens;
    public ulong BytesPerBlock;
    public ulong PinnedBytes;
    public ulong CachedBytes;

    /// <summary>Pinned / total.</summary>
    public double Utilization;

    public PressureLevel Pressure;

    // Counters (monotonic)
    public ulong BlocksAllocated;
    public ulong PrefixHitBlocks;
    public ulong PrefixMissLookups;
    public ulong AvoidedPrefillTokens;
    public ulong CheckpointLimitedTokens;
    public ulong Evictions;
    public ulong CheckpointEvictions;
    public int Checkpoints;
    public ulong CheckpointBytes;
    public ulong CopyOnWrite;
    public ulong AllocationFailures;
    public ulong CheckpointReusedTokens;
}

public sealed class KvCacheManager
{
    private readonly KvCacheConfig _config;
    private readonly EvictionPolicy _policy;
    private readonly Block[] _blocks;
    private readonly List<uint> _freeList;
    private readonly Dictionary<ulong, uint> _prefixIndex = [];
    private readonly List<Checkpoint> _checkpoints = [];
    private readonly Dictionary<ulong, Sequence> _sequences = [];
    private List<BlockCopy> _pendingCopies = [];
    private Action<CacheEvent>? _listener;
    private ulong _clock;
    private PressureLevel _lastPressure = PressureLevel.Normal;
    private KvCacheStats _counters;

    /// <summary>A null policy selects <see cref="PriorityAwareEvictionPolicy"/>.</summary>
    public KvCacheManager(KvCacheConfig config, EvictionPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config.Clone();
        if (_config.BlockSizeTokens == 0)
        {
            throw new ArgumentException("KvCacheConfig.BlockSizeTokens must be > 0", nameof(config));
        }

        if (!(_config.HighWatermark > 0.0 && _config.HighWatermark <= 1.0) ||
            !(_config.CriticalWatermark > 0.0 && _config.CriticalWatermark <= 1.0) ||
            _config.HighWatermark > _config.CriticalWatermark)
        {
            throw new ArgumentException("KvCacheConfig watermarks must satisfy 0 < high <= critical <= 1", nameof(config));
        }

        _policy = policy ?? new PriorityAwareEvictionPolicy();
        _blocks = new Block[_config.NumBlocks];
        for (int i = 0; i < _blocks.Length; i++)
        {
            _blocks[i] = new Block();
        }

        _freeList = new List<uint>(_blocks.Length);
        for (uint i = _config.NumBlocks; i > 0; --i)
        {
            _freeList.Add(i - 1);
        }
    }

    public KvCacheConfig Config => _config;

    public EvictionPolicy EvictionPolicy => _policy;

    private int BlockSize => (int)_config.BlockSizeTokens;

    // --- hashing / index ---------------------------------------------------------

    private static ulong Mix64(ulong x)
    {
        // splitmix64 finaliser
        x += 0x9e3779b97f4a7c15UL;
        x = (x ^ (x >> 30)) * 0xbf58476d1ce4e5b9UL;
        x = (x ^ (x >> 27)) * 0x94d049bb133111ebUL;
        return x ^ (x >> 31);
    }

    private static ulong ChainHash(ulong parent, CacheFingerprint fp, ReadOnlySpan<int> tokens, ModelArchitecture architecture)
    {
        ulong h = Mix64(parent ^ Mix64(fp.Value ^ 0x5bd1e9955bd1e995UL));
        if (architecture != ModelArchitecture.AttentionOnly)
        {
            h = Mix64(h ^ Mix64((byte)architecture));
        }

        foreach (int t in tokens)
        {
            h = Mix64(h ^ (uint)t);
        }

        return h;
    }

    private ulong PrevHash(Sequence seq, int blockIndex) =>
        blockIndex == 0 ? 0 : _blocks[seq.Blocks[blockIndex - 1]].Hash;

    private uint? Lookup(ulong hash, ulong parent, CacheFingerprint fp, ReadOnlySpan<int> tokens, ModelArchitecture architecture)
    {
        if (!_prefixIndex.TryGetValue(hash, out uint b))
        {
            return null;
        }

        Block blk = _blocks[b];
        // Correctness beats hit rate: verify everything we can, not just the hash.
        if (blk.ParentHash != parent || blk.Fingerprint != fp || blk.Architecture != architecture ||
            !CollectionsMarshal.AsSpan(blk.Tokens).SequenceEqual(tokens))
        {
            return null;
        }

        return b;
    }

    private void Unregister(uint b)
    {
        Block blk = _blocks[b];
        if (!blk.Registered)
        {
            return;
        }

        if (_prefixIndex.TryGetValue(blk.Hash, out uint indexed) && indexed == b)
        {
            _prefixIndex.Remove(blk.Hash);
        }

        blk.Registered = false;
    }

    private void FinalizeBlock(Sequence seq, int blockIndex)
    {
        uint b = seq.Blocks[blockIndex];
        Block blk = _blocks[b];
        blk.ParentHash = PrevHash(seq, blockIndex);
        blk.Fingerprint = seq.Options.Fingerprint;
        blk.Architecture = seq.Options.Architecture;
        blk.Hash = ChainHash(blk.ParentHash, blk.Fingerprint, CollectionsMarshal.AsSpan(blk.Tokens), blk.Architecture);
        blk.HasHash = true;
        blk.Depth = (uint)blockIndex;
        if (_config.EnablePrefixCaching && _prefixIndex.TryAdd(blk.Hash, b))
        {
            blk.Registered = true;
        }
    }

    // --- block pool --------------------------------------------------------------

    private void ResetBlock(uint b)
    {
        RemoveCheckpointsForBlock(b);
        Block blk = _blocks[b];
        blk.Tokens.Clear();
        blk.HasHash = false;
        blk.Registered = false;
        blk.Evictable = false;
        blk.Hash = 0;
        blk.ParentHash = 0;
        blk.Depth = 0;
        blk.Priority = 0;
        blk.Fingerprint = default;
        blk.Architecture = ModelArchitecture.AttentionOnly;
    }

    private uint AllocateBlock(ulong owner)
    {
        uint b;
        if (_freeList.Count != 0)
        {
            b = _freeList[^1];
            _freeList.RemoveAt(_freeList.Count - 1);
        }
        else if (_policy.PopVictim() is uint victim)
        {
            b = victim;
            _blocks[b].Evictable = false;
            Unregister(b);
            ResetBlock(b);
            _counters.Evictions++;
            Emit(new CacheEvent(CacheEventKind.Evicted, 0, b));
        }
        else
        {
            return CacheNames.InvalidBlock;
        }

        _blocks[b].Tokens.EnsureCapacity(BlockSize);
        _counters.BlocksAllocated++;
        Emit(new CacheEvent(CacheEventKind.Allocated, owner, b));
        return b;
    }

    private void Acquire(uint b, Sequence seq)
    {
        Block blk = _blocks[b];
        if (blk.Evictable)
        {
            _policy.Erase(b);
            blk.Evictable = false;
        }

        ++blk.RefCount;
        blk.Priority = Math.Max(blk.Priority, seq.Options.Priority);
        blk.LastUse = ++_clock;
    }

    private void Release(uint b, byte priority)
    {
        Block blk = _blocks[b];
        --blk.RefCount;
        blk.Priority = Math.Max(blk.Priority, priority);
        blk.LastUse = ++_clock;
        if (blk.RefCount != 0)
        {
            return;
        }

        if (blk.Registered && _config.EnablePrefixCaching)
        {
            blk.Evictable = true;
            _policy.Insert(new EvictionCandidate(
                b,
                blk.LastUse,
                blk.Priority,
                blk.Depth,
                (ulong)(blk.Depth + 1) * _config.BlockSizeTokens));
        }
        else
        {
            Unregister(b);
            ResetBlock(b);
            _freeList.Add(b);
        }
    }

    private void EnsureTailWritable(ulong id, Sequence seq, ref AppendResult result)
    {
        int k = seq.NumTokens % BlockSize;
        uint b = seq.Blocks[^1];
        Block blk = _blocks[b];
        if (blk.RefCount > 1 || (seq.Options.Architecture != ModelArchitecture.AttentionOnly && CheckpointUsesBlock(b)))
        {
            uint nb = AllocateBlock(id); // availability pre-checked by caller
            Block nblk = _blocks[nb];
            nblk.Tokens.Clear();
            nblk.Tokens.AddRange(CollectionsMarshal.AsSpan(blk.Tokens)[..k]);
            nblk.Fingerprint = seq.Options.Fingerprint;
            Acquire(nb, seq);
            // A non-indexed tail will be released below. Preserve checkpoints whose entire prefix
            // survives the copy by moving their lineage reference.
            if (!blk.Registered && blk.RefCount == 1)
            {
                foreach (Checkpoint cp in _checkpoints)
                {
                    for (int index = 0; index < cp.Blocks.Count; ++index)
                    {
                        if (cp.Blocks[index] == b && cp.State.Position - (index * BlockSize) <= k)
                        {
                            cp.Blocks[index] = nb;
                        }
                    }
                }
            }

            Release(b, seq.Options.Priority);
            seq.Blocks[^1] = nb;
            _pendingCopies.Add(new BlockCopy(b, nb, (uint)k));
            _counters.CopyOnWrite++;
            result.CopyOnWrite++;
            result.BlocksAllocated++;
            Emit(new CacheEvent(CacheEventKind.CopyOnWrite, id, nb, b));
        }
        else
        {
            // Exclusively owned: mutate in place, but it can no longer serve as a cached prefix
            // block (e.g. after truncate into a full block).
            Unregister(b);
            blk.HasHash = false;
            if (blk.Tokens.Count > k)
            {
                blk.Tokens.RemoveRange(k, blk.Tokens.Count - k);
            }
        }
    }

    // --- sequence lifecycle ------------------------------------------------------

    public KvCacheStatus AddSequence(ulong id, SequenceOptions options = default)
    {
        if (_sequences.ContainsKey(id))
        {
            return KvCacheStatus.Fail(KvCacheError.InvalidState, $"sequence {id} already exists");
        }

        // Owner ids may be recycled, but old engine state must not be attributed to the new owner.
        for (int i = _checkpoints.Count; i > 0; --i)
        {
            if (_checkpoints[i - 1].State.Owner == id)
            {
                EraseCheckpoint(i - 1);
            }
        }

        _sequences.Add(id, new Sequence { Options = options });
        return KvCacheStatus.Success;
    }

    /// <summary>
    /// Save a recurrent state checkpoint for the sequence's current lineage. The caller owns the
    /// state and must retain it until the CheckpointEvicted notification.
    /// </summary>
    public KvCacheStatus SaveCheckpoint(ulong id, int position, ulong stateSize)
    {
        if (!_sequences.TryGetValue(id, out Sequence? seq))
        {
            return KvCacheStatus.Fail(KvCacheError.NotFound, $"unknown sequence {id}");
        }

        if (seq.Options.Architecture == ModelArchitecture.AttentionOnly || position <= 0 || position > seq.NumTokens)
        {
            return KvCacheStatus.Fail(KvCacheError.InvalidArgument, "invalid recurrent checkpoint position or architecture");
        }

        if (stateSize == 0 || _config.MaxCheckpoints <= 0 || stateSize > _config.MaxCheckpointBytes)
        {
            return KvCacheStatus.Fail(KvCacheError.InvalidArgument, "checkpoint exceeds configured capacity");
        }

        var saved = new Checkpoint
        {
            State = new RecurrentStateCheckpoint(position, stateSize, id),
            Fingerprint = seq.Options.Fingerprint,
            Architecture = seq.Options.Architecture,
        };
        saved.Blocks.AddRange(CollectionsMarshal.AsSpan(seq.Blocks)[..BlocksForTokens(position)]);
        // Validate before mutating accounting. Subtraction keeps ulong.MaxValue budgets safe.
        for (int i = _checkpoints.Count; i > 0; --i)
        {
            if (_checkpoints[i - 1].State.Owner == id && _checkpoints[i - 1].State.Position == position)
            {
                EraseCheckpoint(i - 1);
            }
        }

        ulong total = 0;
        foreach (Checkpoint cp in _checkpoints)
        {
            total += cp.State.StateSize;
        }

        while (_checkpoints.Count != 0 &&
               (total > _config.MaxCheckpointBytes - stateSize || _checkpoints.Count >= _config.MaxCheckpoints))
        {
            int victim = 0;
            for (int i = 1; i < _checkpoints.Count; i++)
            {
                if (_checkpoints[i].LastUse < _checkpoints[victim].LastUse)
                {
                    victim = i;
                }
            }

            total -= _checkpoints[victim].State.StateSize;
            EraseCheckpoint(victim);
        }

        saved.LastUse = ++_clock;
        _checkpoints.Add(saved);
        return KvCacheStatus.Success;
    }

    /// <summary>Remove a previously saved checkpoint at <paramref name="position"/>.</summary>
    public KvCacheStatus RemoveCheckpoint(ulong id, int position)
    {
        for (int i = 0; i < _checkpoints.Count; ++i)
        {
            if (_checkpoints[i].State.Owner == id && _checkpoints[i].State.Position == position)
            {
                EraseCheckpoint(i);
                return KvCacheStatus.Success;
            }
        }

        return KvCacheStatus.Fail(KvCacheError.NotFound, "checkpoint not found");
    }

    private int[] SequenceTokens(Sequence seq)
    {
        int[] tokens = new int[seq.NumTokens];
        int filled = 0;
        foreach (uint b in seq.Blocks)
        {
            ReadOnlySpan<int> block = CollectionsMarshal.AsSpan(_blocks[b].Tokens);
            int take = Math.Min(seq.NumTokens - filled, block.Length);
            block[..take].CopyTo(tokens.AsSpan(filled));
            filled += take;
        }

        return tokens;
    }

    /// <summary>Exact saved state for this sequence's lineage; does not touch LRU state.</summary>
    public RecurrentStateCheckpoint? CheckpointAt(ulong id, int position)
    {
        if (!_sequences.TryGetValue(id, out Sequence? seq) || position > seq.NumTokens)
        {
            return null;
        }

        int[] tokens = SequenceTokens(seq);
        int? cp = FindCheckpoint(seq.Options.Fingerprint, seq.Options.Architecture, tokens, position);
        if (cp is not int index || _checkpoints[index].State.Position != position)
        {
            return null;
        }

        return _checkpoints[index].State;
    }

    /// <summary>
    /// Session fork: <paramref name="child"/> references every block of <paramref name="parent"/>
    /// (no copy). A shared partial tail block is copied lazily on first write.
    /// </summary>
    public KvCacheStatus ForkSequence(ulong parent, ulong child, byte? childPriority = null)
    {
        if (!_sequences.TryGetValue(parent, out Sequence? p))
        {
            return KvCacheStatus.Fail(KvCacheError.NotFound, $"unknown parent sequence {parent}");
        }

        if (_sequences.ContainsKey(child))
        {
            return KvCacheStatus.Fail(KvCacheError.InvalidState, $"sequence {child} already exists");
        }

        if (p.Options.Architecture != ModelArchitecture.AttentionOnly && p.NumTokens != 0)
        {
            RecurrentStateCheckpoint? state = CheckpointAt(parent, p.NumTokens);
            if (state is null || state.Value.Owner == child)
            {
                return KvCacheStatus.Fail(KvCacheError.InvalidState, "recurrent fork requires an exact saved checkpoint");
            }
        }

        // Discard state left by an earlier incarnation of the child id.
        for (int i = _checkpoints.Count; i > 0; --i)
        {
            if (_checkpoints[i - 1].State.Owner == child)
            {
                EraseCheckpoint(i - 1);
            }
        }

        Sequence copy = p.Clone();
        if (childPriority is byte priority)
        {
            copy.Options = copy.Options with { Priority = priority };
        }

        foreach (uint b in copy.Blocks)
        {
            Acquire(b, copy);
        }

        _sequences.Add(child, copy);
        UpdatePressure();
        return KvCacheStatus.Success;
    }

    public KvCacheStatus AppendTokens(ulong id, ReadOnlySpan<int> tokens) => AppendTokens(id, tokens, out _);

    /// <summary>
    /// Append tokens, allocating blocks as needed. While a sequence's content so far has been
    /// fully served from cache, whole blocks are looked up in the prefix index first.
    /// All-or-nothing: on <see cref="KvCacheError.Unavailable"/> nothing changes.
    /// </summary>
    public KvCacheStatus AppendTokens(ulong id, ReadOnlySpan<int> tokens, out AppendResult result)
    {
        result = default;
        if (!_sequences.TryGetValue(id, out Sequence? seq))
        {
            return KvCacheStatus.Fail(KvCacheError.NotFound, $"unknown sequence {id}");
        }

        int n = tokens.Length;
        if (n == 0)
        {
            return KvCacheStatus.Success;
        }

        int need = BlocksNeeded(id, n);
        if (need > AvailableBlocks)
        {
            _counters.AllocationFailures++;
            UpdatePressure();
            return KvCacheStatus.Fail(KvCacheError.Unavailable, $"need {need} blocks, {AvailableBlocks} available");
        }

        int bs = BlockSize;
        int recurrentLimit = 0;
        RecurrentStateCheckpoint? selected = null;
        // No checkpoint scan or growing prefix copy on attention/decode paths.
        if (seq.Options.Architecture != ModelArchitecture.AttentionOnly &&
            _config.EnablePrefixCaching && seq.PrefixStreak && n >= bs)
        {
            int[] combined = new int[seq.NumTokens + n];
            SequenceTokens(seq).CopyTo(combined, 0);
            tokens.CopyTo(combined.AsSpan(seq.NumTokens));
            int raw = MatchBlocks(seq.Options.Fingerprint, combined, seq.Options.Architecture);
            int? cp = FindCheckpoint(seq.Options.Fingerprint, seq.Options.Architecture, combined, raw);
            if (cp is int cpIndex && _checkpoints[cpIndex].State.Position > seq.NumTokens)
            {
                recurrentLimit = _checkpoints[cpIndex].State.Position;
                selected = _checkpoints[cpIndex].State;
                // Restoring inside a block may need an additional COW block to preserve the saved
                // lineage. Fall back to recompute if the worst-case reservation has no room for it.
                if (recurrentLimit % bs != 0 && need == AvailableBlocks)
                {
                    recurrentLimit = 0;
                    selected = null;
                }
                else
                {
                    _checkpoints[cpIndex].LastUse = ++_clock;
                }
            }

            int usable = Math.Max(seq.NumTokens, recurrentLimit);
            result.TokensCheckpointLimited = raw > usable ? raw - usable : 0;
            _counters.CheckpointLimitedTokens += (ulong)result.TokensCheckpointLimited;
        }

        int i = 0;
        while (i < n)
        {
            int k = seq.NumTokens % bs;
            if (k != 0)
            {
                EnsureTailWritable(id, seq, ref result);
                Block blk = _blocks[seq.Blocks[^1]];
                int take = Math.Min(bs - k, n - i);
                blk.Tokens.AddRange(tokens.Slice(i, take));
                blk.LastUse = ++_clock;
                seq.NumTokens += take;
                i += take;
                seq.PrefixStreak = false;
                if (k + take == bs)
                {
                    FinalizeBlock(seq, seq.Blocks.Count - 1);
                }

                continue;
            }

            int remaining = n - i;
            if (_config.EnablePrefixCaching && seq.PrefixStreak && remaining >= bs)
            {
                ReadOnlySpan<int> chunk = tokens.Slice(i, bs);
                ulong parent = PrevHash(seq, seq.Blocks.Count);
                ulong h = ChainHash(parent, seq.Options.Fingerprint, chunk, seq.Options.Architecture);
                if (seq.Options.Architecture == ModelArchitecture.AttentionOnly || recurrentLimit > seq.NumTokens)
                {
                    if (Lookup(h, parent, seq.Options.Fingerprint, chunk, seq.Options.Architecture) is uint hit)
                    {
                        int take = seq.Options.Architecture == ModelArchitecture.AttentionOnly
                            ? bs
                            : Math.Min(bs, recurrentLimit - seq.NumTokens);
                        Acquire(hit, seq);
                        seq.Blocks.Add(hit);
                        seq.NumTokens += take;
                        seq.CachedPrefix += take;
                        i += take;
                        result.BlocksReused++;
                        result.TokensReused += take;
                        _counters.PrefixHitBlocks++;
                        _counters.AvoidedPrefillTokens += (ulong)take;
                        Emit(new CacheEvent(CacheEventKind.Reused, id, hit));
                        continue;
                    }
                }

                _counters.PrefixMissLookups++;
            }

            seq.PrefixStreak = false;

            uint nb = AllocateBlock(id); // pre-checked; cannot fail
            Acquire(nb, seq);
            seq.Blocks.Add(nb);
            int takeNew = Math.Min(bs, remaining);
            Block nblk = _blocks[nb];
            nblk.Fingerprint = seq.Options.Fingerprint;
            nblk.Tokens.Clear();
            nblk.Tokens.AddRange(tokens.Slice(i, takeNew));
            seq.NumTokens += takeNew;
            i += takeNew;
            result.BlocksAllocated++;
            if (takeNew == bs)
            {
                FinalizeBlock(seq, seq.Blocks.Count - 1);
            }
        }

        if (selected is not null && result.TokensReused > 0)
        {
            result.Checkpoint = selected;
            _counters.CheckpointReusedTokens += (ulong)result.TokensReused;
        }

        result.TokensAppended = n;
        UpdatePressure();
        return KvCacheStatus.Success;
    }

    /// <summary>Roll back to <paramref name="newLength"/> tokens (speculative rejection, edits).</summary>
    public KvCacheStatus Truncate(ulong id, int newLength)
    {
        if (!_sequences.TryGetValue(id, out Sequence? seq))
        {
            return KvCacheStatus.Fail(KvCacheError.NotFound, $"unknown sequence {id}");
        }

        if (newLength < 0 || newLength > seq.NumTokens)
        {
            return KvCacheStatus.Fail(KvCacheError.InvalidArgument, "truncate beyond sequence length");
        }

        if (newLength == seq.NumTokens)
        {
            return KvCacheStatus.Success;
        }

        if (seq.Options.Architecture != ModelArchitecture.AttentionOnly && newLength != 0 &&
            CheckpointAt(id, newLength) is null)
        {
            return KvCacheStatus.Fail(KvCacheError.InvalidArgument, "recurrent truncate requires an exact saved checkpoint");
        }

        int keep = BlocksForTokens(newLength);
        while (seq.Blocks.Count > keep)
        {
            Release(seq.Blocks[^1], seq.Options.Priority);
            seq.Blocks.RemoveAt(seq.Blocks.Count - 1);
        }

        for (int i = _checkpoints.Count; i > 0; --i)
        {
            if (_checkpoints[i - 1].State.Owner == id && _checkpoints[i - 1].State.Position > newLength)
            {
                EraseCheckpoint(i - 1);
            }
        }

        seq.NumTokens = newLength;
        seq.CachedPrefix = Math.Min(seq.CachedPrefix, newLength);
        seq.PrefixStreak = seq.CachedPrefix == seq.NumTokens && seq.NumTokens % BlockSize == 0;
        UpdatePressure();
        return KvCacheStatus.Success;
    }

    public KvCacheStatus FreeSequence(ulong id)
    {
        if (!_sequences.Remove(id, out Sequence? seq))
        {
            return KvCacheStatus.Fail(KvCacheError.NotFound, $"unknown sequence {id}");
        }

        // Release tail-first so roots of a prefix chain end up most recently used.
        for (int b = seq.Blocks.Count - 1; b >= 0; b--)
        {
            Release(seq.Blocks[b], seq.Options.Priority);
        }

        UpdatePressure();
        return KvCacheStatus.Success;
    }

    public KvCacheStatus SetPriority(ulong id, byte priority)
    {
        if (!_sequences.TryGetValue(id, out Sequence? seq))
        {
            return KvCacheStatus.Fail(KvCacheError.NotFound, $"unknown sequence {id}");
        }

        seq.Options = seq.Options with { Priority = priority };
        foreach (uint b in seq.Blocks)
        {
            _blocks[b].Priority = Math.Max(_blocks[b].Priority, priority);
        }

        return KvCacheStatus.Success;
    }

    // --- queries -----------------------------------------------------------------

    public bool HasSequence(ulong id) => _sequences.ContainsKey(id);

    public int NumTokens(ulong id) => _sequences.TryGetValue(id, out Sequence? seq) ? seq.NumTokens : 0;

    /// <summary>Leading tokens of the sequence that were served from the prefix cache.</summary>
    public int CachedPrefixTokens(ulong id) => _sequences.TryGetValue(id, out Sequence? seq) ? seq.CachedPrefix : 0;

    public ReadOnlySpan<uint> BlockTable(ulong id) =>
        _sequences.TryGetValue(id, out Sequence? seq) ? CollectionsMarshal.AsSpan(seq.Blocks) : [];

    public uint RefCount(uint block) => block < _blocks.Length ? _blocks[block].RefCount : 0;

    /// <summary>
    /// Logical token contents recorded for a block (verification/debugging; a shared partial block
    /// may hold more tokens than a given sequence uses).
    /// </summary>
    public ReadOnlySpan<int> BlockTokens(uint block) =>
        block < _blocks.Length ? CollectionsMarshal.AsSpan(_blocks[block].Tokens) : [];

    private int MatchBlocks(CacheFingerprint fingerprint, ReadOnlySpan<int> tokens, ModelArchitecture architecture)
    {
        if (!_config.EnablePrefixCaching)
        {
            return 0;
        }

        int bs = BlockSize;
        ulong parent = 0;
        int matched = 0;
        while (tokens.Length - matched >= bs)
        {
            ReadOnlySpan<int> chunk = tokens.Slice(matched, bs);
            ulong h = ChainHash(parent, fingerprint, chunk, architecture);
            if (Lookup(h, parent, fingerprint, chunk, architecture) is null)
            {
                break;
            }

            parent = h;
            matched += bs;
        }

        return matched;
    }

    /// <summary>
    /// Matches complete KV blocks, then caps recurrent reuse at an exact saved checkpoint (possibly
    /// inside a block). A pure query: does not touch LRU state or counters. The engine uses it to
    /// decide how much of a bot's prompt (the shared system/grounding prefix) is already cached.
    /// </summary>
    public int MatchPrefix(CacheFingerprint fingerprint, ReadOnlySpan<int> tokens, ModelArchitecture architecture = ModelArchitecture.AttentionOnly)
    {
        int raw = MatchBlocks(fingerprint, tokens, architecture);
        if (architecture == ModelArchitecture.AttentionOnly)
        {
            return raw;
        }

        int? cp = FindCheckpoint(fingerprint, architecture, tokens, raw);
        return cp is int index ? _checkpoints[index].State.Position : 0;
    }

    /// <summary>Blocks needed for a fresh sequence of <paramref name="n"/> tokens.</summary>
    public int BlocksForTokens(int n) => (n + BlockSize - 1) / BlockSize;

    /// <summary>Worst-case new blocks needed to append <paramref name="n"/> tokens (ignores cache hits).</summary>
    public int BlocksNeeded(ulong id, int n)
    {
        if (!_sequences.TryGetValue(id, out Sequence? seq))
        {
            return BlocksForTokens(n);
        }

        if (n == 0)
        {
            return 0;
        }

        int bs = BlockSize;
        int k = seq.NumTokens % bs;
        int need = 0;
        int room = 0;
        if (k != 0)
        {
            room = bs - k;
            uint tail = seq.Blocks[^1];
            if (_blocks[tail].RefCount > 1 ||
                (seq.Options.Architecture != ModelArchitecture.AttentionOnly && CheckpointUsesBlock(tail)))
            {
                need += 1; // copy-on-write
            }
        }

        int rest = n > room ? n - room : 0;
        return need + BlocksForTokens(rest);
    }

    /// <summary>Blocks obtainable right now (free + evictable).</summary>
    public int AvailableBlocks => _freeList.Count + _policy.Count;

    public bool CanAppend(ulong id, int n) => HasSequence(id) && BlocksNeeded(id, n) <= AvailableBlocks;

    public PressureLevel Pressure
    {
        get
        {
            if (_blocks.Length == 0)
            {
                return PressureLevel.Normal;
            }

            int pinned = _blocks.Length - AvailableBlocks;
            double util = (double)pinned / _blocks.Length;
            if (util >= _config.CriticalWatermark)
            {
                return PressureLevel.Critical;
            }

            return util >= _config.HighWatermark ? PressureLevel.High : PressureLevel.Normal;
        }
    }

    public KvCacheStats Stats()
    {
        KvCacheStats s = _counters;
        s.TotalBlocks = _blocks.Length;
        s.FreeBlocks = _freeList.Count;
        s.CachedBlocks = _policy.Count;
        s.PinnedBlocks = s.TotalBlocks - s.FreeBlocks - s.CachedBlocks;
        int shared = 0;
        foreach (Block b in _blocks)
        {
            if (b.RefCount > 1)
            {
                shared++;
            }
        }

        s.SharedBlocks = shared;
        s.Sequences = _sequences.Count;
        s.BlockSizeTokens = _config.BlockSizeTokens;
        s.BytesPerBlock = _config.BytesPerToken * _config.BlockSizeTokens;
        s.PinnedBytes = s.BytesPerBlock * (ulong)s.PinnedBlocks;
        s.CachedBytes = s.BytesPerBlock * (ulong)s.CachedBlocks;
        s.Checkpoints = _checkpoints.Count;
        s.CheckpointBytes = 0;
        foreach (Checkpoint cp in _checkpoints)
        {
            s.CheckpointBytes += cp.State.StateSize;
        }

        s.Utilization = s.TotalBlocks == 0 ? 0.0 : (double)s.PinnedBlocks / s.TotalBlocks;
        s.Pressure = Pressure;
        return s;
    }

    /// <summary>
    /// Copy operations accumulated since the last call (the engine must apply them before running
    /// the step that writes the destination blocks).
    /// </summary>
    public List<BlockCopy> TakePendingCopies()
    {
        List<BlockCopy> output = _pendingCopies;
        _pendingCopies = [];
        return output;
    }

    public void SetEventListener(Action<CacheEvent>? listener) => _listener = listener;

    /// <summary>Drop every cached (reference count 0) block back to the free list.</summary>
    public int ClearCached()
    {
        int n = 0;
        while (_policy.PopVictim() is uint b)
        {
            _blocks[b].Evictable = false;
            Unregister(b);
            ResetBlock(b);
            _freeList.Add(b);
            _counters.Evictions++;
            Emit(new CacheEvent(CacheEventKind.Evicted, 0, b));
            ++n;
        }

        while (_checkpoints.Count != 0)
        {
            EraseCheckpoint(_checkpoints.Count - 1);
        }

        UpdatePressure();
        return n;
    }

    private void Emit(in CacheEvent e) => _listener?.Invoke(e);

    private void UpdatePressure()
    {
        PressureLevel now = Pressure;
        if (now != _lastPressure)
        {
            _lastPressure = now;
            Emit(new CacheEvent(CacheEventKind.PressureChanged, Pressure: now));
        }
    }

    private int? FindCheckpoint(CacheFingerprint fingerprint, ModelArchitecture architecture, ReadOnlySpan<int> tokens, int limit)
    {
        int? best = null;
        for (int i = 0; i < _checkpoints.Count; ++i)
        {
            Checkpoint cp = _checkpoints[i];
            if (cp.Fingerprint != fingerprint || cp.Architecture != architecture || cp.State.Position > limit ||
                cp.State.Position > tokens.Length ||
                (best is int b && cp.State.Position <= _checkpoints[b].State.Position))
            {
                continue;
            }

            int matched = 0;
            foreach (uint block in cp.Blocks)
            {
                ReadOnlySpan<int> values = CollectionsMarshal.AsSpan(_blocks[block].Tokens);
                int take = Math.Min(cp.State.Position - matched, values.Length);
                if (!values[..take].SequenceEqual(tokens.Slice(matched, take)))
                {
                    break;
                }

                matched += take;
            }

            if (matched == cp.State.Position)
            {
                best = i;
            }
        }

        return best;
    }

    private bool CheckpointUsesBlock(uint block)
    {
        foreach (Checkpoint cp in _checkpoints)
        {
            if (cp.Blocks.Contains(block))
            {
                return true;
            }
        }

        return false;
    }

    private void EraseCheckpoint(int index)
    {
        RecurrentStateCheckpoint state = _checkpoints[index].State;
        _checkpoints.RemoveAt(index);
        _counters.CheckpointEvictions++;
        Emit(new CacheEvent(CacheEventKind.CheckpointEvicted, state.Owner, Checkpoint: state));
    }

    private void RemoveCheckpointsForBlock(uint block)
    {
        for (int i = _checkpoints.Count; i > 0; --i)
        {
            if (_checkpoints[i - 1].Blocks.Contains(block))
            {
                EraseCheckpoint(i - 1);
            }
        }
    }

    /// <summary>Full invariant check (refcounts, pool partition, prefix index). Tests/debug.</summary>
    public bool Validate()
    {
        int bs = BlockSize;
        if (_checkpoints.Count > _config.MaxCheckpoints)
        {
            return false;
        }

        ulong bytes = 0;
        foreach (Checkpoint cp in _checkpoints)
        {
            if (cp.State.StateSize == 0 || cp.State.StateSize > _config.MaxCheckpointBytes - bytes ||
                cp.State.Position == 0 || cp.Architecture == ModelArchitecture.AttentionOnly ||
                cp.Blocks.Count != BlocksForTokens(cp.State.Position))
            {
                return false;
            }

            bytes += cp.State.StateSize;
            int remaining = cp.State.Position;
            foreach (uint b in cp.Blocks)
            {
                if (b >= _blocks.Length || _blocks[b].Tokens.Count < Math.Min(remaining, bs))
                {
                    return false;
                }

                remaining -= Math.Min(remaining, bs);
            }
        }

        uint[] refs = new uint[_blocks.Length];
        foreach (Sequence seq in _sequences.Values)
        {
            if (seq.Blocks.Count != BlocksForTokens(seq.NumTokens) || seq.CachedPrefix > seq.NumTokens)
            {
                return false;
            }

            int full = seq.NumTokens / bs;
            for (int i = 0; i < seq.Blocks.Count; ++i)
            {
                uint b = seq.Blocks[i];
                if (b >= _blocks.Length)
                {
                    return false;
                }

                ++refs[b];
                Block blk = _blocks[b];
                int expect = i < full ? bs : seq.NumTokens % bs;
                if (blk.Tokens.Count < expect || (i < full && !blk.HasHash))
                {
                    return false;
                }
            }
        }

        bool[] inFree = new bool[_blocks.Length];
        foreach (uint b in _freeList)
        {
            if (b >= _blocks.Length || inFree[b])
            {
                return false;
            }

            inFree[b] = true;
        }

        int evictable = 0;
        int registered = 0;
        for (int b = 0; b < _blocks.Length; ++b)
        {
            Block blk = _blocks[b];
            if (blk.RefCount != refs[b])
            {
                return false;
            }

            if (blk.Registered)
            {
                ++registered;
            }

            if (inFree[b] && (blk.RefCount != 0 || blk.Registered || blk.Evictable))
            {
                return false;
            }

            if (blk.Evictable)
            {
                if (blk.RefCount != 0 || !blk.Registered)
                {
                    return false;
                }

                ++evictable;
            }

            if (blk.RefCount == 0 && !blk.Evictable && !inFree[b])
            {
                return false; // leaked
            }
        }

        if (evictable != _policy.Count || registered != _prefixIndex.Count)
        {
            return false;
        }

        foreach ((ulong hash, uint b) in _prefixIndex)
        {
            Block blk = _blocks[b];
            if (!blk.Registered || !blk.HasHash || blk.Hash != hash || blk.Tokens.Count != bs)
            {
                return false;
            }
        }

        return true;
    }

    private sealed class Block
    {
        public uint RefCount;
        public bool HasHash;
        public bool Registered;
        public bool Evictable;
        public ulong Hash;
        public ulong ParentHash;
        public uint Depth;
        public byte Priority;
        public ulong LastUse;
        public CacheFingerprint Fingerprint;
        public ModelArchitecture Architecture = ModelArchitecture.AttentionOnly;
        public readonly List<int> Tokens = [];
    }

    private sealed class Checkpoint
    {
        public RecurrentStateCheckpoint State;
        public CacheFingerprint Fingerprint;
        public ModelArchitecture Architecture = ModelArchitecture.AttentionOnly;
        public ulong LastUse;
        public readonly List<uint> Blocks = [];
    }

    private sealed class Sequence
    {
        public SequenceOptions Options;
        public List<uint> Blocks = [];
        public int NumTokens;
        public int CachedPrefix;
        public bool PrefixStreak = true;

        public Sequence Clone() => new()
        {
            Options = Options,
            Blocks = [.. Blocks],
            NumTokens = NumTokens,
            CachedPrefix = CachedPrefix,
            PrefixStreak = PrefixStreak,
        };
    }
}
