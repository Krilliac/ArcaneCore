using System.Diagnostics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;

namespace ArcaneCore.World.Playerbots.Chat.Inference;

/// <summary>How a generation ended.</summary>
internal enum ChatGenerationStatus
{
    /// <summary>A stop token, a line break or the token limit: <see cref="ChatGenerationResult.Text"/> holds the reply.</summary>
    Completed,

    /// <summary>The hard time budget ran out first: nothing is said.</summary>
    Overrun,

    /// <summary>The caller gave up (its own deadline, or shutdown).</summary>
    Cancelled,

    /// <summary>The prompt and the reply do not fit the context window.</summary>
    ContextFull,
}

/// <summary>A generation's outcome and its timings (the counters and the probe).</summary>
internal sealed record ChatGenerationResult(
    ChatGenerationStatus Status,
    string Text,
    int PromptTokens,
    int ReusedTokens,
    int GeneratedTokens,
    double PrefillMs,
    double DecodeMs,
    string StopReason,
    IReadOnlyList<int> Tokens);

/// <summary>
/// One generation slot: the key/value cache, the activation buffers and the worker team, all allocated once. A generation runs
/// synchronously on the caller's thread (the engine's own BelowNormal generation thread) and fans each matrix product out to the
/// team. The cache remembers the tokens it holds, so the next prompt only computes what differs from the last one: the fixed
/// system rules at the front of every bot prompt are computed once and reused by every reply after.
/// <para>
/// Matrix products dequantize one weight row at a time into a per-worker buffer (<see cref="GgmlQuant.DequantizeRow"/>) and dot it
/// with every token of the batch (<see cref="TensorPrimitives.Dot(ReadOnlySpan{float}, ReadOnlySpan{float})"/>), so a prompt
/// chunk of <see cref="MaxBatch"/> tokens dequantizes each row once. Decoding a reply is one token per pass.
/// </para>
/// </summary>
internal sealed class ChatInferenceContext : IDisposable
{
    /// <summary>Prompt tokens computed per pass.</summary>
    public const int MaxBatch = 32;

    private readonly ChatModel _model;
    private readonly ChatModelConfig _c;
    private readonly InferenceTeam _team;
    private readonly float[][] _keyCache;
    private readonly float[][] _valueCache;
    private readonly float[] _x, _xb, _attended, _q, _k, _v, _hb, _hb2, _logits, _scores, _ropeScratch;
    private readonly float[] _cos, _sin;
    private readonly float[][] _rowScratch;
    private readonly MatMulJob _matMul;
    private readonly AttentionJob _attention;
    private readonly ChatSampler _sampler = new();
    private readonly List<int> _cached = [];
    private readonly List<byte> _bytes = [];
    private readonly int[] _recent;

    public ChatInferenceContext(ChatModel model, int contextTokens, int threads, string name = "bot-chat")
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _c = model.Config;
        ContextTokens = Math.Clamp(Math.Min(contextTokens, _c.ContextLength), 16, 1 << 16);
        _team = new InferenceTeam(Math.Clamp(threads, 1, 64), name);
        int maxColumns = Math.Max(Math.Max(_c.Dim, _c.FfnDim), _c.QueryDim);
        _keyCache = new float[_c.Layers][];
        _valueCache = new float[_c.Layers][];
        for (int l = 0; l < _c.Layers; l++)
        {
            _keyCache[l] = new float[ContextTokens * _c.KvDim];
            _valueCache[l] = new float[ContextTokens * _c.KvDim];
        }

        _x = new float[MaxBatch * _c.Dim];
        _xb = new float[MaxBatch * Math.Max(_c.Dim, _c.QueryDim)];
        _attended = new float[MaxBatch * _c.QueryDim];
        _q = new float[MaxBatch * _c.QueryDim];
        _k = new float[MaxBatch * _c.KvDim];
        _v = new float[MaxBatch * _c.KvDim];
        _hb = new float[MaxBatch * _c.FfnDim];
        _hb2 = new float[MaxBatch * _c.FfnDim];
        _logits = new float[_c.Vocabulary];
        _scores = new float[_c.Heads * ContextTokens];
        _ropeScratch = new float[_c.HeadDim];
        (_cos, _sin) = TransformerMath.RopeTables(ContextTokens, _c.RopeDims, _c.RopeBase);
        _rowScratch = new float[_team.Size][];
        for (int i = 0; i < _rowScratch.Length; i++) _rowScratch[i] = new float[maxColumns];
        _matMul = new MatMulJob(model.File, _rowScratch);
        _attention = new AttentionJob(this);
        _recent = new int[ContextTokens];
    }

    /// <summary>Positions the cache holds (the prompt plus the reply must fit).</summary>
    public int ContextTokens { get; }

    public int Threads => _team.Size;

    /// <summary>Bytes this slot allocated (the cache and buffers), for the startup line.</summary>
    public long AllocatedBytes => 4L * ((2L * _c.Layers * ContextTokens * _c.KvDim) + _x.Length + _xb.Length + _attended.Length + _q.Length
        + _k.Length + _v.Length + _hb.Length + _hb2.Length + _logits.Length + _scores.Length + _cos.Length + _sin.Length
        + _rowScratch.Sum(r => (long)r.Length));

    /// <summary>The logits of the last forward pass (tests and the probe).</summary>
    internal ReadOnlySpan<float> Logits => _logits;

    /// <summary>Forget the cached prompt (the next generation computes everything).</summary>
    public void Reset() => _cached.Clear();

    /// <summary>
    /// Generate a reply to <paramref name="prompt"/> (already templated token ids). Runs on the calling thread; returns
    /// <see cref="ChatGenerationStatus.Overrun"/> once <see cref="ChatGenerationSettings.BudgetMs"/> has passed (checked between
    /// passes) and <see cref="ChatGenerationStatus.Cancelled"/> when <paramref name="cancellationToken"/> fires.
    /// </summary>
    public ChatGenerationResult Generate(IReadOnlyList<int> prompt, ChatGenerationSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(settings);
        long started = Stopwatch.GetTimestamp();
        if (prompt.Count == 0 || prompt.Count + settings.MaxNewTokens > ContextTokens)
            return new ChatGenerationResult(ChatGenerationStatus.ContextFull, string.Empty, prompt.Count, 0, 0, 0, 0, "context", []);
        foreach (int id in prompt)
            if ((uint)id >= (uint)_c.Vocabulary) throw new ArgumentOutOfRangeException(nameof(prompt), $"token {id} is outside the vocabulary");

        // Reuse the cached prefix; at least the last prompt token is computed again, for its logits.
        int reuse = 0;
        int limit = Math.Min(_cached.Count, prompt.Count - 1);
        while (reuse < limit && _cached[reuse] == prompt[reuse]) reuse++;
        if (_cached.Count > reuse) _cached.RemoveRange(reuse, _cached.Count - reuse);

        Span<int> chunk = stackalloc int[MaxBatch];
        int position = reuse;
        while (position < prompt.Count)
        {
            if (Over(started, settings, cancellationToken) is { } stop)
                return new ChatGenerationResult(stop, string.Empty, prompt.Count, reuse, 0, Ms(started), 0, "budget", []);
            int count = Math.Min(MaxBatch, prompt.Count - position);
            for (int i = 0; i < count; i++) chunk[i] = prompt[position + i];
            Forward(chunk[..count], position, wantLogits: position + count == prompt.Count);
            for (int i = 0; i < count; i++) _cached.Add(chunk[i]);
            position += count;
        }

        double prefillMs = Ms(started);
        long decodeStarted = Stopwatch.GetTimestamp();
        Random random = settings.Seed is { } seed ? new Random(seed) : Random.Shared;
        int recentCount = 0;
        int penaltyFrom = Math.Max(0, prompt.Count - settings.PenaltyWindow);
        for (int i = penaltyFrom; i < prompt.Count; i++) _recent[recentCount++] = prompt[i];
        var generated = new List<int>(settings.MaxNewTokens);
        _bytes.Clear();
        string reason = "length";
        BpeTokenizer tokenizer = _model.Tokenizer;
        while (generated.Count < settings.MaxNewTokens)
        {
            int window = Math.Min(recentCount, settings.PenaltyWindow);
            int next = _sampler.Sample(_logits, _recent.AsSpan(recentCount - window, window), settings, random);
            if (next == tokenizer.Eos || next == tokenizer.ImEnd || next == tokenizer.EndOfText || tokenizer.IsControl(next))
            {
                reason = "stop";
                break;
            }

            if (settings.StopAtNewline && tokenizer.HasNewline(next))
            {
                // Keep what came before the break on this token, then stop.
                var piece = new List<byte>();
                tokenizer.AppendBytes(next, piece);
                int cut = piece.FindIndex(b => b is (byte)'\n' or (byte)'\r');
                _bytes.AddRange(piece.Take(cut < 0 ? piece.Count : cut));
                generated.Add(next);
                reason = "newline";
                break;
            }

            generated.Add(next);
            tokenizer.AppendBytes(next, _bytes);
            if (recentCount < _recent.Length) _recent[recentCount++] = next;
            if (generated.Count >= settings.MaxNewTokens) break;
            if (Over(started, settings, cancellationToken) is { } stop)
                return new ChatGenerationResult(stop, string.Empty, prompt.Count, reuse, generated.Count, prefillMs, Ms(decodeStarted), "budget", generated);
            int at = prompt.Count + generated.Count - 1;
            chunk[0] = next;
            Forward(chunk[..1], at, wantLogits: true);
            _cached.Add(next);
        }

        string text = System.Text.Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(_bytes));
        return new ChatGenerationResult(ChatGenerationStatus.Completed, text, prompt.Count, reuse, generated.Count, prefillMs, Ms(decodeStarted), reason, generated);
    }

    private static ChatGenerationStatus? Over(long started, ChatGenerationSettings settings, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return ChatGenerationStatus.Cancelled;
        return Ms(started) > settings.BudgetMs ? ChatGenerationStatus.Overrun : null;
    }

    private static double Ms(long started) => Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    /// <summary>
    /// One pass over <paramref name="tokens"/> at positions <paramref name="start"/>.. (their keys and values land in the cache);
    /// with <paramref name="wantLogits"/>, the last token's logits land in <see cref="Logits"/>.
    /// </summary>
    internal void Forward(ReadOnlySpan<int> tokens, int start, bool wantLogits)
    {
        int n = tokens.Length, dim = _c.Dim, qDim = _c.QueryDim, kvDim = _c.KvDim, ffn = _c.FfnDim;
        if (n is < 1 or > MaxBatch) throw new ArgumentOutOfRangeException(nameof(tokens), $"1..{MaxBatch} tokens per pass");
        if (start < 0 || start + n > ContextTokens) throw new ArgumentOutOfRangeException(nameof(start), "past the context window");
        GgufFile file = _model.File;
        for (int t = 0; t < n; t++)
            GgmlQuant.DequantizeRow(_model.Embedding.Type, file.Row(_model.Embedding.Tensor, tokens[t]), _x.AsSpan(t * dim, dim));

        int pairs = _c.RopeDims / 2;
        for (int l = 0; l < _c.Layers; l++)
        {
            ChatModelLayer layer = _model.Layers[l];
            for (int t = 0; t < n; t++) TransformerMath.RmsNorm(_x.AsSpan(t * dim, dim), layer.AttentionNorm, _xb.AsSpan(t * dim, dim), _c.RmsEpsilon);
            MatMul(layer.Query, _xb, n, _q, layer.QueryBias);
            MatMul(layer.Key, _xb, n, _k, layer.KeyBias);
            MatMul(layer.Value, _xb, n, _v, layer.ValueBias);
            for (int t = 0; t < n; t++)
            {
                int pos = start + t;
                ReadOnlySpan<float> cos = _cos.AsSpan(pos * pairs, pairs), sin = _sin.AsSpan(pos * pairs, pairs);
                for (int h = 0; h < _c.Heads; h++)
                    TransformerMath.Rope(_q.AsSpan((t * qDim) + (h * _c.HeadDim), _c.HeadDim), _c.RopeDims, cos, sin, _c.RopeNeox, _ropeScratch);
                for (int h = 0; h < _c.KvHeads; h++)
                    TransformerMath.Rope(_k.AsSpan((t * kvDim) + (h * _c.HeadDim), _c.HeadDim), _c.RopeDims, cos, sin, _c.RopeNeox, _ropeScratch);
                _k.AsSpan(t * kvDim, kvDim).CopyTo(_keyCache[l].AsSpan(pos * kvDim, kvDim));
                _v.AsSpan(t * kvDim, kvDim).CopyTo(_valueCache[l].AsSpan(pos * kvDim, kvDim));
            }

            _attention.Set(l, start, n);
            _team.Run(_attention, _c.Heads);
            MatMul(layer.Output, _attended, n, _xb, null);
            TensorPrimitives.Add(_x.AsSpan(0, n * dim), _xb.AsSpan(0, n * dim), _x.AsSpan(0, n * dim));

            for (int t = 0; t < n; t++) TransformerMath.RmsNorm(_x.AsSpan(t * dim, dim), layer.FfnNorm, _xb.AsSpan(t * dim, dim), _c.RmsEpsilon);
            MatMul(layer.Gate, _xb, n, _hb, null);
            MatMul(layer.Up, _xb, n, _hb2, null);
            TransformerMath.SwiGlu(_hb.AsSpan(0, n * ffn), _hb2.AsSpan(0, n * ffn));
            MatMul(layer.Down, _hb, n, _xb, null);
            TensorPrimitives.Add(_x.AsSpan(0, n * dim), _xb.AsSpan(0, n * dim), _x.AsSpan(0, n * dim));
        }

        if (!wantLogits) return;
        TransformerMath.RmsNorm(_x.AsSpan((n - 1) * dim, dim), _model.OutputNorm, _xb.AsSpan(0, dim), _c.RmsEpsilon);
        MatMul(_model.Output, _xb, 1, _logits, null);
    }

    /// <summary><paramref name="output"/>[t*rows + r] = W[r] . <paramref name="input"/>[t*cols..] (+ bias[r]) for the first <paramref name="batch"/> tokens.</summary>
    private void MatMul(WeightMatrix weight, float[] input, int batch, float[] output, float[]? bias)
    {
        _matMul.Set(weight, input, batch, output);
        _team.Run(_matMul, weight.Rows);
        if (bias is null) return;
        for (int t = 0; t < batch; t++) TensorPrimitives.Add(output.AsSpan(t * weight.Rows, weight.Rows), bias, output.AsSpan(t * weight.Rows, weight.Rows));
    }

    private sealed class MatMulJob(GgufFile file, float[][] scratch) : IInferenceJob
    {
        private WeightMatrix? _weight;
        private float[] _input = [];
        private float[] _output = [];
        private int _batch;

        public void Set(WeightMatrix weight, float[] input, int batch, float[] output)
        {
            _weight = weight;
            _input = input;
            _batch = batch;
            _output = output;
        }

        public void Execute(int worker, int start, int end)
        {
            WeightMatrix weight = _weight!;
            int rows = weight.Rows, cols = weight.Columns, batch = _batch;
            float[] input = _input, output = _output;
            Span<float> buffer = scratch[worker].AsSpan(0, cols);
            bool plain = weight.Type == GgmlType.F32;
            for (int r = start; r < end; r++)
            {
                ReadOnlySpan<byte> raw = file.Row(weight.Tensor, r);
                ReadOnlySpan<float> row;
                if (plain)
                {
                    row = MemoryMarshal.Cast<byte, float>(raw);
                }
                else
                {
                    GgmlQuant.DequantizeRow(weight.Type, raw, buffer);
                    row = buffer;
                }

                for (int t = 0; t < batch; t++) output[(t * rows) + r] = TensorPrimitives.Dot(row, input.AsSpan(t * cols, cols));
            }
        }
    }

    /// <summary>Causal grouped-query attention, one item per query head: each batch token attends to every cached position up to its own.</summary>
    private sealed class AttentionJob(ChatInferenceContext owner) : IInferenceJob
    {
        private int _layer, _start, _count;

        public void Set(int layer, int start, int count)
        {
            _layer = layer;
            _start = start;
            _count = count;
        }

        public void Execute(int worker, int start, int end)
        {
            ChatModelConfig c = owner._c;
            int hd = c.HeadDim, qDim = c.QueryDim, kvDim = c.KvDim, group = c.Heads / c.KvHeads;
            float scale = 1f / MathF.Sqrt(hd);
            float[] keys = owner._keyCache[_layer], values = owner._valueCache[_layer];
            for (int h = start; h < end; h++)
            {
                int kvOffset = (h / group) * hd;
                Span<float> scores = owner._scores.AsSpan(h * owner.ContextTokens, owner.ContextTokens);
                for (int t = 0; t < _count; t++)
                {
                    int pos = _start + t;
                    ReadOnlySpan<float> query = owner._q.AsSpan((t * qDim) + (h * hd), hd);
                    for (int p = 0; p <= pos; p++) scores[p] = TensorPrimitives.Dot(query, keys.AsSpan((p * kvDim) + kvOffset, hd)) * scale;
                    Span<float> weights = scores[..(pos + 1)];
                    TransformerMath.Softmax(weights);
                    Span<float> result = owner._attended.AsSpan((t * qDim) + (h * hd), hd);
                    result.Clear();
                    for (int p = 0; p <= pos; p++) TensorPrimitives.MultiplyAdd(values.AsSpan((p * kvDim) + kvOffset, hd), weights[p], result, result);
                }
            }
        }
    }

    public void Dispose() => _team.Dispose();
}
