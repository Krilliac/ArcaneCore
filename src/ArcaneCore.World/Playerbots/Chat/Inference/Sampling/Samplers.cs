// Ported from Krilliac/Sonder-Inference src/sampling/include/sonder/sampling/sampler.hpp, samplers.hpp and src/sampling/src/samplers.cpp @1c4c152 (Apache-2.0)
// Stage semantics follow llama.cpp's documented sampler behaviour (tools/completion/README.md, MIT); the source is an independent
// implementation of that documentation and so is this port.

namespace ArcaneCore.World.Playerbots.Chat.Inference.Sampling;

/// <summary>
/// One composable stage of a sampler chain. Stages transform or truncate the candidate set in place; they must be deterministic given
/// their inputs and internal state.
/// </summary>
internal abstract class Sampler
{
    public abstract string Name { get; }

    /// <summary>Transform <paramref name="candidates"/> in place.</summary>
    public abstract void Apply(Candidates candidates);

    /// <summary>Observe a committed token (penalty history). Default: stateless.</summary>
    public virtual void Accept(int token)
    {
    }

    /// <summary>Clear per-sequence state. Default: stateless.</summary>
    public virtual void Reset()
    {
    }

    /// <summary>Deep copy including state (forks a session).</summary>
    public abstract Sampler Clone();

    /// <summary>min_keep 0 is treated as 1.</summary>
    protected static int EffectiveMinKeep(int minKeep) => Math.Max(minKeep, 1);

    /// <summary>Build the stage for <paramref name="kind"/> from <paramref name="config"/>.</summary>
    public static Sampler Create(StageKind kind, SamplerConfig config)
    {
        int minKeep = Math.Max(config.MinKeep, 0);
        return kind switch
        {
            StageKind.Penalties => new PenaltiesSampler(config.PenaltyLastN, config.RepeatPenalty, config.FrequencyPenalty, config.PresencePenalty),
            StageKind.TopK => new TopKSampler(config.TopK),
            StageKind.TypicalP => new TypicalPSampler(config.TypicalP, minKeep),
            StageKind.TopP => new TopPSampler(config.TopP, minKeep),
            StageKind.MinP => new MinPSampler(config.MinP, minKeep),
            StageKind.Temperature => new TemperatureSampler(config.Temperature),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown sampling stage kind"),
        };
    }
}

/// <summary>Adds a fixed bias to specific token logits. Repeated entries accumulate; ids not in the candidate set are ignored.</summary>
internal sealed class LogitBiasSampler : Sampler
{
    private readonly int[] _ids;
    private readonly float[] _bias;
    private readonly Dictionary<int, float> _byId;

    public LogitBiasSampler(IEnumerable<LogitBias> biases)
    {
        _byId = [];
        foreach (LogitBias b in biases)
        {
            _byId[b.Token] = _byId.GetValueOrDefault(b.Token) + b.Bias;
        }

        _ids = [.. _byId.Keys];
        _bias = new float[_ids.Length];
        for (int i = 0; i < _ids.Length; i++)
        {
            _bias[i] = _byId[_ids[i]];
        }
    }

    private LogitBiasSampler(LogitBiasSampler other)
    {
        _ids = other._ids;
        _bias = other._bias;
        _byId = other._byId;
    }

    public override string Name => "logit_bias";

    public override void Apply(Candidates candidates)
    {
        if (_ids.Length == 0)
        {
            return;
        }

        if (candidates.IdOrdered)
        {
            // Sparse: touch only the biased ids instead of scanning the vocabulary.
            Span<TokenData> span = candidates.Span;
            for (int i = 0; i < _ids.Length; i++)
            {
                int id = _ids[i];
                if ((uint)id < (uint)span.Length)
                {
                    span[id].Logit += _bias[i];
                }
            }
        }
        else
        {
            foreach (ref TokenData t in candidates.Span)
            {
                if (_byId.TryGetValue(t.Id, out float bias))
                {
                    t.Logit += bias;
                }
            }
        }

        candidates.Sorted = false;
    }

    // Immutable after construction, so the arrays are shared.
    public override Sampler Clone() => new LogitBiasSampler(this);
}

/// <summary>
/// Repetition, frequency and presence penalties over the last <c>lastN</c> accepted tokens (llama.cpp semantics): for a token seen
/// <c>count</c> &gt; 0 times, <c>logit = logit &lt;= 0 ? logit * repeat : logit / repeat</c>, then <c>logit -= count * frequency + presence</c>.
/// </summary>
internal sealed class PenaltiesSampler : Sampler
{
    private readonly int _lastN;
    private readonly float _repeat;
    private readonly float _frequency;
    private readonly float _presence;
    private readonly Queue<int> _history;
    private readonly Dictionary<int, int> _counts;

    public PenaltiesSampler(int lastN, float repeat, float frequency, float presence)
    {
        _lastN = lastN;
        _repeat = repeat;
        _frequency = frequency;
        _presence = presence;
        _history = new Queue<int>(lastN > 0 ? lastN + 1 : 0);
        _counts = new Dictionary<int, int>(lastN > 0 ? lastN + 1 : 0);
    }

    private PenaltiesSampler(PenaltiesSampler other)
    {
        _lastN = other._lastN;
        _repeat = other._repeat;
        _frequency = other._frequency;
        _presence = other._presence;
        _history = new Queue<int>(other._history);
        _counts = new Dictionary<int, int>(other._counts);
    }

    public override string Name => "penalties";

    public override void Apply(Candidates candidates)
    {
        if (_lastN == 0 || _counts.Count == 0 || (_repeat == 1f && _frequency == 0f && _presence == 0f))
        {
            return;
        }

        if (candidates.IdOrdered)
        {
            // Sparse: the window holds at most lastN distinct ids, so index them instead of scanning the vocabulary.
            Span<TokenData> span = candidates.Span;
            foreach (KeyValuePair<int, int> entry in _counts)
            {
                if ((uint)entry.Key < (uint)span.Length && entry.Value > 0)
                {
                    Penalise(ref span[entry.Key], entry.Value);
                }
            }
        }
        else
        {
            foreach (ref TokenData t in candidates.Span)
            {
                if (_counts.TryGetValue(t.Id, out int count) && count > 0)
                {
                    Penalise(ref t, count);
                }
            }
        }

        candidates.Sorted = false;
    }

    private void Penalise(ref TokenData t, int count)
    {
        if (t.Logit <= 0f)
        {
            t.Logit *= _repeat;
        }
        else
        {
            t.Logit /= _repeat;
        }

        t.Logit -= (count * _frequency) + _presence;
    }

    public override void Accept(int token)
    {
        if (_lastN == 0)
        {
            return;
        }

        _history.Enqueue(token);
        _counts[token] = _counts.GetValueOrDefault(token) + 1;
        if (_lastN > 0 && _history.Count > _lastN)
        {
            int old = _history.Dequeue();
            if (_counts.TryGetValue(old, out int count))
            {
                if (count <= 1)
                {
                    _counts.Remove(old);
                }
                else
                {
                    _counts[old] = count - 1;
                }
            }
        }
    }

    public override void Reset()
    {
        _history.Clear();
        _counts.Clear();
    }

    /// <summary>How many times <paramref name="token"/> is in the window.</summary>
    public int Count(int token) => _counts.GetValueOrDefault(token);

    public override Sampler Clone() => new PenaltiesSampler(this);
}

/// <summary>Keep the k highest-logit candidates. k &lt;= 0 disables; k &gt;= size keeps all. Partial sort, O(n log k).</summary>
internal sealed class TopKSampler(int k) : Sampler
{
    public override string Name => "top_k";

    public override void Apply(Candidates candidates)
    {
        if (k <= 0 || k >= candidates.Count)
        {
            return;
        }

        if (!candidates.Sorted)
        {
            SamplingMath.PartialSortCanonical(candidates.Span, k);
        }

        candidates.Truncate(k);
        candidates.Sorted = true;
        candidates.IdOrdered = false;
    }

    public override Sampler Clone() => new TopKSampler(k);
}

/// <summary>Nucleus: keep the smallest canonical prefix whose mass is &gt;= p, but at least min_keep. p &gt;= 1 disables.</summary>
internal sealed class TopPSampler(float p, int minKeep) : Sampler
{
    public override string Name => "top_p";

    public override void Apply(Candidates candidates)
    {
        if (p >= 1f || candidates.IsEmpty)
        {
            return;
        }

        SamplingMath.SortCanonical(candidates);
        if (!SamplingMath.Softmax(candidates))
        {
            return;
        }

        int min = EffectiveMinKeep(minKeep);
        Span<TokenData> span = candidates.Span;
        double cum = 0.0;
        int keep = span.Length;
        for (int i = 0; i < span.Length; i++)
        {
            cum += span[i].P;
            if (cum >= p && i + 1 >= min)
            {
                keep = i + 1;
                break;
            }
        }

        candidates.Truncate(keep);
    }

    public override Sampler Clone() => new TopPSampler(p, minKeep);
}

/// <summary>Keep candidates with p &gt;= min_p * p_max, but at least min_keep. p &lt;= 0 disables.</summary>
internal sealed class MinPSampler(float p, int minKeep) : Sampler
{
    public override string Name => "min_p";

    public override void Apply(Candidates candidates)
    {
        if (p <= 0f || candidates.IsEmpty)
        {
            return;
        }

        SamplingMath.SortCanonical(candidates);
        if (!SamplingMath.Softmax(candidates))
        {
            return;
        }

        Span<TokenData> span = candidates.Span;
        float threshold = p * span[0].P; // the front is the max in canonical order
        int keep = 0;
        while (keep < span.Length && span[keep].P >= threshold)
        {
            keep++;
        }

        candidates.Truncate(Math.Min(span.Length, Math.Max(keep, EffectiveMinKeep(minKeep))));
    }

    public override Sampler Clone() => new MinPSampler(p, minKeep);
}

/// <summary>
/// Locally typical sampling (Meister et al. 2022): rank candidates by |-log p - H| ascending and keep the smallest set whose mass
/// exceeds p, but at least min_keep. Can drop the argmax. p &gt;= 1 disables. Scratch is kept between calls (the source allocated it per call).
/// </summary>
internal sealed class TypicalPSampler(float p, int minKeep) : Sampler
{
    private struct Ranked
    {
        public double Score;
        public int Index;
    }

    private static readonly Comparison<Ranked> ByScoreThenIndex = static (a, b) =>
    {
        int c = a.Score.CompareTo(b.Score);
        return c != 0 ? c : a.Index.CompareTo(b.Index); // index tie-break = stable over canonical order
    };

    private Ranked[] _ranked = [];
    private TokenData[] _kept = [];

    public override string Name => "typical_p";

    public override void Apply(Candidates candidates)
    {
        if (p >= 1f || candidates.IsEmpty)
        {
            return;
        }

        SamplingMath.SortCanonical(candidates);
        if (!SamplingMath.Softmax(candidates))
        {
            return;
        }

        Span<TokenData> span = candidates.Span;
        int n = span.Length;
        double entropy = 0.0;
        for (int i = 0; i < n; i++)
        {
            float pi = span[i].P;
            if (pi > 0f)
            {
                entropy -= pi * Math.Log(pi);
            }
        }

        if (_ranked.Length < n)
        {
            _ranked = new Ranked[n];
            _kept = new TokenData[n];
        }

        Span<Ranked> ranked = _ranked.AsSpan(0, n);
        for (int i = 0; i < n; i++)
        {
            double pi = span[i].P;
            ranked[i] = new Ranked
            {
                Score = pi > 0.0 ? Math.Abs(-Math.Log(pi) - entropy) : double.PositiveInfinity,
                Index = i,
            };
        }

        ranked.Sort(ByScoreThenIndex);

        int min = EffectiveMinKeep(minKeep);
        double cum = 0.0;
        int keep = n;
        for (int i = 0; i < n; i++)
        {
            cum += span[ranked[i].Index].P;
            if (cum > p && i + 1 >= min)
            {
                keep = i + 1;
                break;
            }
        }

        Span<TokenData> kept = _kept.AsSpan(0, keep);
        for (int i = 0; i < keep; i++)
        {
            kept[i] = span[ranked[i].Index];
        }

        kept.CopyTo(span);
        candidates.Truncate(keep);
        candidates.Sorted = false;
        candidates.IdOrdered = false;
        SamplingMath.SortCanonical(candidates);
    }

    public override Sampler Clone() => new TypicalPSampler(p, minKeep);
}

/// <summary>
/// Divide logits by t. t &lt;= 0 keeps only the argmax (lowest id on ties; llama.cpp <c>temp_ext</c>), which makes the following
/// distribution step deterministic. When every candidate is -inf the set is left whole (sorted).
/// </summary>
internal sealed class TemperatureSampler(float t) : Sampler
{
    public override string Name => "temperature";

    public override void Apply(Candidates candidates)
    {
        if (candidates.IsEmpty)
        {
            return;
        }

        Span<TokenData> span = candidates.Span;
        if (t <= 0f)
        {
            // Linear argmax in canonical order instead of the source's full sort; the kept candidate is identical.
            int best = 0;
            for (int i = 1; i < span.Length; i++)
            {
                if (SamplingMath.Precedes(span[i], span[best]))
                {
                    best = i;
                }
            }

            if (float.IsNegativeInfinity(span[best].Logit))
            {
                SamplingMath.SortCanonical(candidates);
                return;
            }

            span[0] = span[best];
            candidates.Truncate(1);
            candidates.Sorted = true;
            candidates.IdOrdered = span[0].Id == 0;
            return;
        }

        if (t == 1f)
        {
            return;
        }

        for (int i = 0; i < span.Length; i++)
        {
            span[i].Logit /= t;
        }

        candidates.Sorted = false;
    }

    public override Sampler Clone() => new TemperatureSampler(t);
}
