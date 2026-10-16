// Ported from Krilliac/Sonder-Inference src/sampling/include/sonder/sampling/chain.hpp and src/sampling/src/chain.cpp @1c4c152 (Apache-2.0)

namespace ArcaneCore.World.Playerbots.Chat.Inference.Sampling;

internal enum SampleStatus : byte
{
    /// <summary>A token was chosen.</summary>
    Ok,

    /// <summary>The logits span was empty.</summary>
    EmptyLogits,

    /// <summary>Every candidate ended at -inf (over-constrained or fully banned).</summary>
    NoViableCandidates,
}

/// <summary>One step's outcome. <see cref="Token"/> is <see cref="SamplingTokens.Invalid"/> unless <see cref="Ok"/>.</summary>
internal readonly record struct SampleResult(SampleStatus Status, int Token, float Probability)
{
    public bool Ok => Status == SampleStatus.Ok;

    public static string Describe(SampleStatus status) => status switch
    {
        SampleStatus.Ok => "ok",
        SampleStatus.EmptyLogits => "empty logits",
        SampleStatus.NoViableCandidates => "no viable candidates (all logits -inf)",
        _ => "unknown",
    };
}

internal enum SamplerSelector : byte
{
    /// <summary>argmax (ties: lowest token id).</summary>
    Greedy,

    /// <summary>Seeded draw from the softmax of the surviving candidates, walked in canonical order.</summary>
    Distribution,
}

/// <summary>
/// Ordered pipeline: NaN to -inf, [constraint], stages (<see cref="Build"/> puts logit bias first), selector.
/// Not thread-safe: one chain per sequence. The candidate buffer is reused, so after warm-up <see cref="Sample"/> does not allocate
/// unless the vocabulary grows. <see cref="Clone"/> copies stages and state to fork a session; the constraint is shared by reference,
/// so install a fresh one on each fork.
/// </summary>
internal sealed class SamplerChain
{
    private readonly List<Sampler> _stages = [];
    private readonly HashSet<int> _stopTokens = [];
    private readonly Candidates _cur;
    private IConstraint? _constraint;
    private SamplingRng _rng;

    public SamplerChain(ulong seed = 0, SamplerSelector selector = SamplerSelector.Distribution, int vocabCapacity = 0)
    {
        Selector = selector;
        _rng = new SamplingRng(seed);
        _cur = new Candidates(vocabCapacity);
    }

    private SamplerChain(SamplerChain other)
    {
        foreach (Sampler s in other._stages)
        {
            _stages.Add(s.Clone());
        }

        _stopTokens = [.. other._stopTokens];
        _constraint = other._constraint;
        Selector = other.Selector;
        _rng = other._rng;
        _cur = other._cur.Clone();
    }

    public SamplerSelector Selector { get; set; }

    public int StageCount => _stages.Count;

    /// <summary>The candidate set the last <see cref="Sample"/> chose from (canonical order for the distribution selector).</summary>
    public Candidates LastCandidates => _cur;

    public IConstraint? Constraint => _constraint;

    public SamplerChain Add(Sampler stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        _stages.Add(stage);
        return this;
    }

    /// <summary>Install (or clear with null) the constraint. It runs before every stage.</summary>
    public void SetConstraint(IConstraint? constraint) => _constraint = constraint;

    public void SetStopTokens(IEnumerable<int> tokens)
    {
        _stopTokens.Clear();
        _stopTokens.UnionWith(tokens);
    }

    public bool IsStopToken(int token) => _stopTokens.Contains(token);

    public void Reseed(ulong seed) => _rng.Reseed(seed);

    public IReadOnlyList<string> StageNames()
    {
        var names = new string[_stages.Count];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = _stages[i].Name;
        }

        return names;
    }

    /// <summary>
    /// Sample one token from raw logits (index = token id). NaN logits are treated as -inf. Does not call <see cref="Accept"/>; the
    /// caller commits the token.
    /// </summary>
    public SampleResult Sample(ReadOnlySpan<float> logits)
    {
        if (logits.IsEmpty)
        {
            _cur.Clear();
            return new SampleResult(SampleStatus.EmptyLogits, SamplingTokens.Invalid, 0f);
        }

        _cur.Load(logits);
        _constraint?.Apply(_cur);
        foreach (Sampler stage in _stages)
        {
            stage.Apply(_cur);
        }

        if (_cur.IsEmpty || SamplingMath.ViableCount(_cur) == 0)
        {
            return new SampleResult(SampleStatus.NoViableCandidates, SamplingTokens.Invalid, 0f);
        }

        Span<TokenData> span;
        int pick = 0;
        if (Selector == SamplerSelector.Greedy)
        {
            // The argmax in canonical order needs no sort: one linear pass (the source sorted the whole set).
            SamplingMath.Softmax(_cur);
            span = _cur.Span;
            for (int i = 1; i < span.Length; i++)
            {
                if (SamplingMath.Precedes(span[i], span[pick]))
                {
                    pick = i;
                }
            }
        }
        else
        {
            SamplingMath.SortCanonical(_cur);
            SamplingMath.Softmax(_cur);
            span = _cur.Span;
            double u = _rng.Uniform();
            double cum = 0.0;
            int lastViable = 0;
            pick = span.Length;
            for (int i = 0; i < span.Length; i++)
            {
                if (span[i].P <= 0f)
                {
                    continue;
                }

                lastViable = i;
                cum += span[i].P;
                if (u < cum)
                {
                    pick = i;
                    break;
                }
            }

            if (pick == span.Length)
            {
                pick = lastViable; // rounding: cum ended slightly below 1
            }
        }

        return new SampleResult(SampleStatus.Ok, span[pick].Id, span[pick].P);
    }

    /// <summary>Commit a generated token: updates penalty history and the constraint.</summary>
    public void Accept(int token)
    {
        foreach (Sampler stage in _stages)
        {
            stage.Accept(token);
        }

        _constraint?.Accept(token);
    }

    /// <summary>Feed prompt tokens into penalty history without advancing the constraint (llama.cpp accepts the prompt with grammar off).</summary>
    public void AcceptPrompt(ReadOnlySpan<int> tokens)
    {
        foreach (int t in tokens)
        {
            foreach (Sampler stage in _stages)
            {
                stage.Accept(t);
            }
        }
    }

    public void Reset()
    {
        foreach (Sampler stage in _stages)
        {
            stage.Reset();
        }

        _constraint?.Reset();
        _cur.Clear();
    }

    /// <summary>Deep copy of stages, RNG state and candidates; the constraint is shared.</summary>
    public SamplerChain Clone() => new(this);

    /// <summary>
    /// Build a chain from a config: logit bias (when any), then <see cref="SamplerConfig.StageOrder"/>; greedy keeps only penalties.
    /// Throws <see cref="ArgumentException"/> carrying <see cref="ValidationResult.ToString"/> when the config is invalid.
    /// </summary>
    public static SamplerChain Build(SamplerConfig config, IConstraint? constraint = null, int? vocabSize = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ValidationResult v = config.Validate(vocabSize);
        if (!v.Ok)
        {
            throw new ArgumentException(v.ToString(), nameof(config));
        }

        ulong seed = config.Seed ?? SamplingRng.EntropySeed();
        var chain = new SamplerChain(seed, config.Greedy ? SamplerSelector.Greedy : SamplerSelector.Distribution, vocabSize ?? 0);
        chain.SetConstraint(constraint);
        chain.SetStopTokens(config.StopTokens);

        if (config.LogitBiases.Count > 0)
        {
            chain.Add(new LogitBiasSampler(config.LogitBiases));
        }

        foreach (StageKind kind in config.StageOrder)
        {
            // Greedy keeps only stages that can change the argmax.
            if (config.Greedy && kind != StageKind.Penalties)
            {
                continue;
            }

            chain.Add(Sampler.Create(kind, config));
        }

        return chain;
    }
}
