using System.Numerics.Tensors;

namespace ArcaneCore.World.Playerbots.Chat.Inference;

/// <summary>
/// What a generation may do: its length, its hard time budget and its sampling. Tokens are chosen by the ported sampler chain
/// (<see cref="Serving.ChatReplyDecoder"/>: penalties over the last <paramref name="PenaltyWindow"/> tokens, top-k, top-p,
/// <paramref name="MinP"/>, temperature, a seeded draw or greedy at temperature 0); <paramref name="StopAtNewline"/> also installs the
/// one-chat-line constraint; <paramref name="StopSequences"/> end the reply at a text match (the match itself is not said).
/// </summary>
internal sealed record ChatGenerationSettings(
    int MaxNewTokens,
    double BudgetMs,
    float Temperature,
    int TopK,
    float TopP,
    float RepetitionPenalty,
    int? Seed,
    bool StopAtNewline = true,
    int PenaltyWindow = 64,
    float MinP = 0f,
    IReadOnlyList<string>? StopSequences = null);

/// <summary>
/// The engine's original compact sampler, kept as the reference the tests pin (generation itself now runs the ported
/// <see cref="Sampling.SamplerChain"/>). Picks the next token from the logits: the repetition penalty on the recent tokens (llama.cpp's rule: a positive logit is divided
/// by the penalty, a negative one multiplied), then greedy when the temperature is 0, else top-k, temperature, top-p and a draw.
/// The candidate scan only keeps logits within <see cref="Margin"/>/T of the best (a token further below has a probability under
/// e^-16 of the best one), so a 150k vocabulary costs one pass and a handful of heap inserts. Preallocated: no allocation per token.
/// </summary>
internal sealed class ChatSampler
{
    public const int MaxTopK = 256;
    private const float Margin = 16f;

    private readonly int[] _ids = new int[MaxTopK];
    private readonly float[] _values = new float[MaxTopK];
    private readonly float[] _probabilities = new float[MaxTopK];

    /// <summary>Choose a token. <paramref name="logits"/> is modified (penalties).</summary>
    public int Sample(Span<float> logits, ReadOnlySpan<int> recent, ChatGenerationSettings settings, Random random)
    {
        ApplyPenalty(logits, recent, settings.RepetitionPenalty);
        if (settings.Temperature <= 0f || settings.TopK == 1) return TensorPrimitives.IndexOfMax(logits);

        int k = Math.Clamp(settings.TopK <= 0 ? MaxTopK : settings.TopK, 1, MaxTopK);
        float best = TensorPrimitives.Max(logits);
        float floor = best - (Margin * settings.Temperature);
        int count = 0;
        for (int i = 0; i < logits.Length; i++)
        {
            float value = logits[i];
            if (value < floor || !float.IsFinite(value)) continue;
            if (count < k)
            {
                _ids[count] = i;
                _values[count] = value;
                count++;
                SiftUp(count - 1);
            }
            else if (value > _values[0])
            {
                _ids[0] = i;
                _values[0] = value;
                SiftDown(0, count);
            }
        }

        if (count == 0) return TensorPrimitives.IndexOfMax(logits);
        // Heap sort of the min-heap: each step moves the smallest left to the back, so the candidates end up in descending order.
        for (int end = count - 1; end > 0; end--)
        {
            Swap(0, end);
            SiftDown(0, end);
        }

        Span<float> p = _probabilities.AsSpan(0, count);
        TensorPrimitives.Divide(_values.AsSpan(0, count), settings.Temperature, p);
        TransformerMath.Softmax(p);
        float topP = Math.Clamp(settings.TopP, 0.01f, 1f);
        float cumulative = 0f;
        int keep = count;
        for (int i = 0; i < count; i++)
        {
            cumulative += p[i];
            if (cumulative >= topP)
            {
                keep = i + 1;
                break;
            }
        }

        float total = TensorPrimitives.Sum(p[..keep]);
        float draw = (float)random.NextDouble() * total;
        for (int i = 0; i < keep; i++)
        {
            draw -= p[i];
            if (draw <= 0f) return _ids[i];
        }

        return _ids[keep - 1];
    }

    /// <summary>Penalise each distinct token of <paramref name="recent"/> once.</summary>
    internal static void ApplyPenalty(Span<float> logits, ReadOnlySpan<int> recent, float penalty)
    {
        if (penalty <= 1f) return;
        for (int i = 0; i < recent.Length; i++)
        {
            int id = recent[i];
            if ((uint)id >= (uint)logits.Length || recent[..i].Contains(id)) continue;
            float value = logits[id];
            logits[id] = value > 0 ? value / penalty : value * penalty;
        }
    }

    private void SiftUp(int i)
    {
        while (i > 0)
        {
            int parent = (i - 1) / 2;
            if (_values[parent] <= _values[i]) return;
            Swap(parent, i);
            i = parent;
        }
    }

    private void SiftDown(int i, int count)
    {
        while (true)
        {
            int left = (2 * i) + 1, right = left + 1, smallest = i;
            if (left < count && _values[left] < _values[smallest]) smallest = left;
            if (right < count && _values[right] < _values[smallest]) smallest = right;
            if (smallest == i) return;
            Swap(i, smallest);
            i = smallest;
        }
    }

    private void Swap(int a, int b)
    {
        (_ids[a], _ids[b]) = (_ids[b], _ids[a]);
        (_values[a], _values[b]) = (_values[b], _values[a]);
    }
}
