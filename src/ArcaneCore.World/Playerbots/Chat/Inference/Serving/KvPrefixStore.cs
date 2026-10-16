using System.Runtime.InteropServices;
using ArcaneCore.World.Playerbots.Chat.Inference.Scheduling.Cache;

namespace ArcaneCore.World.Playerbots.Chat.Inference.Serving;

/// <summary>
/// The shared prompt-prefix KV cache across generation slots. Every bot prompt starts with the same system rules (and a bot's own
/// facts and conversation repeat from one reply to the next), so the keys and values of those leading tokens are kept in a block
/// pool and copied into a slot instead of being computed again. The ported <see cref="KvCacheManager"/> does the bookkeeping: blocks
/// of <see cref="BlockSizeTokens"/> tokens addressed by a chained hash of their tokens and the model's fingerprint, reference counts
/// and LRU eviction of unreferenced blocks. This class owns the physical storage (per layer, the same row layout as a slot's cache)
/// and the copies between it and a <see cref="ChatInferenceContext"/>.
/// <para>
/// No sequence stays in the manager between calls: <see cref="Restore"/> and <see cref="Register"/> add a short-lived sequence, copy,
/// and free it, so every stored block is evictable and the pool can never fill with pinned blocks. Engine loop only (not thread-safe).
/// </para>
/// </summary>
internal sealed class KvPrefixStore
{
    public const uint BlockSizeTokens = 16;

    private readonly KvCacheManager _manager;
    private readonly CacheFingerprint _fingerprint;
    private readonly float[][] _keys;
    private readonly float[][] _values;
    private readonly int _kvDim;
    private ulong _nextId = 1;
    private long _restoredTokens, _restores, _registeredTokens;

    public KvPrefixStore(ChatModel model, int tokens)
    {
        ArgumentNullException.ThrowIfNull(model);
        ChatModelConfig c = model.Config;
        int blocks = Math.Max(1, tokens / (int)BlockSizeTokens);
        _kvDim = c.KvDim;
        _keys = new float[c.Layers][];
        _values = new float[c.Layers][];
        int floats = blocks * (int)BlockSizeTokens * _kvDim;
        for (int l = 0; l < c.Layers; l++)
        {
            _keys[l] = new float[floats];
            _values[l] = new float[floats];
        }

        _manager = new KvCacheManager(new KvCacheConfig
        {
            BlockSizeTokens = BlockSizeTokens,
            NumBlocks = (uint)blocks,
            BytesPerToken = (ulong)(2L * c.Layers * _kvDim * sizeof(float)),
            EnablePrefixCaching = true,
        });
        // Only KV computed by this very model file may be reused: name, architecture, shape and size of the file.
        _fingerprint = new FingerprintBuilder()
            .Add(model.Name).Add(c.Architecture).Add((ulong)c.Layers).Add((ulong)c.Dim).Add((ulong)_kvDim)
            .Add((ulong)model.File.FileLength).Build();
        Tokens = blocks * (int)BlockSizeTokens;
        Bytes = 2L * c.Layers * floats * sizeof(float);
    }

    /// <summary>Positions the pool holds.</summary>
    public int Tokens { get; }

    /// <summary>Bytes of the pool (keys and values of every layer).</summary>
    public long Bytes { get; }

    /// <summary>Prompt tokens served from the pool instead of computed.</summary>
    public long RestoredTokens => Interlocked.Read(ref _restoredTokens);

    /// <summary>Times a slot was filled from the pool.</summary>
    public long Restores => Interlocked.Read(ref _restores);

    /// <summary>Tokens written into the pool.</summary>
    public long RegisteredTokens => Interlocked.Read(ref _registeredTokens);

    /// <summary>The manager's counters and gauges.</summary>
    public KvCacheStats Stats() => _manager.Stats();

    /// <summary>
    /// Fill <paramref name="context"/> with the longest stored prefix of <paramref name="tokens"/> (whole blocks, never the last token,
    /// whose logits are needed) when it is longer than the <paramref name="have"/> tokens the slot already holds. Returns the tokens
    /// the slot now holds valid KV for.
    /// </summary>
    public int Restore(ChatInferenceContext context, List<int> tokens, int have)
    {
        ReadOnlySpan<int> all = CollectionsMarshal.AsSpan(tokens);
        if (all.Length < 2) return have;
        int found = _manager.MatchPrefix(_fingerprint, all[..^1]);
        if (found <= have) return have;
        ulong id = _nextId++;
        if (!_manager.AddSequence(id, new SequenceOptions(_fingerprint)).IsOk) return have;
        try
        {
            if (!_manager.AppendTokens(id, all[..found], out AppendResult appended).IsOk) return have;
            int reused = appended.TokensReused; // a block evicted between the lookup and the append is a miss from there on
            if (reused <= have) return have;
            ReadOnlySpan<uint> table = _manager.BlockTable(id);
            int bs = (int)BlockSizeTokens;
            int first = have / bs; // the slot's own blocks before it are the same tokens: only the rest is copied
            for (int b = first; b < reused / bs; b++)
                context.ImportKv(_keys, _values, (int)table[b] * bs, b * bs, bs);
            context.SetCachedTokens(all[..reused]);
            Interlocked.Add(ref _restoredTokens, reused - have);
            Interlocked.Increment(ref _restores);
            return reused;
        }
        finally
        {
            _manager.FreeSequence(id);
        }
    }

    /// <summary>
    /// Store the whole blocks of the first <paramref name="count"/> tokens of <paramref name="tokens"/>, whose KV
    /// <paramref name="context"/> holds now (right after their prefill), so later prompts that start the same way reuse them. Blocks
    /// already stored are not copied again; when the pool cannot take them, nothing is stored.
    /// </summary>
    public void Register(ChatInferenceContext context, List<int> tokens, int count)
    {
        int bs = (int)BlockSizeTokens;
        // At most half the pool per prompt, so one long conversation cannot evict the shared system prefix of everyone else.
        int full = Math.Min(Math.Min(count, context.CachedCount), Tokens / 2) / bs * bs;
        if (full == 0) return;
        ReadOnlySpan<int> span = CollectionsMarshal.AsSpan(tokens)[..full];
        if (_manager.MatchPrefix(_fingerprint, span) >= full) return; // all stored already
        ulong id = _nextId++;
        if (!_manager.AddSequence(id, new SequenceOptions(_fingerprint)).IsOk) return;
        try
        {
            if (!_manager.AppendTokens(id, span, out AppendResult appended).IsOk) return;
            ReadOnlySpan<uint> table = _manager.BlockTable(id);
            for (int b = appended.TokensReused / bs; b < full / bs; b++)
                context.ExportKv(b * bs, bs, _keys, _values, (int)table[b] * bs);
            Interlocked.Add(ref _registeredTokens, full - appended.TokensReused);
        }
        finally
        {
            _manager.FreeSequence(id);
        }
    }
}
