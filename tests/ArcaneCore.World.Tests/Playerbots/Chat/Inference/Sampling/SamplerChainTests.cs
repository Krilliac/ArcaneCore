// Ported from Krilliac/Sonder-Inference src/sampling/tests/test_chain.cpp @1c4c152 (Apache-2.0)

using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots.Chat.Inference.Sampling;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference.Sampling;

/// <summary>
/// The whole chain: greedy and t = 0, seed reproducibility (the source's golden sequence for seed 42), empirical frequencies,
/// error statuses, NaN/+inf, penalties through accept, accept_prompt, stage order, stop tokens.
/// </summary>
public sealed class SamplerChainTests
{
    private const float Inf = float.PositiveInfinity;

    private static readonly float[] Logits = [1.0f, 2.0f, 0.5f, 1.5f, -1.0f, 0.0f, 2.2f, 1.1f];

    private static void Near(double expected, double actual, double eps) => Assert.True(Math.Abs(expected - actual) <= eps, $"{actual} not within {eps} of {expected}");

    private static SamplerConfig Plain(ulong seed) => new()
    {
        Seed = seed,
        Temperature = 1f,
        TopK = 0,
        TopP = 1f,
        MinP = 0f,
    };

    private static int[] Draw(SamplerChain chain, float[] logits, int n)
    {
        var tokens = new int[n];
        for (int i = 0; i < n; i++)
        {
            tokens[i] = chain.Sample(logits).Token;
        }

        return tokens;
    }

    private sealed class CountingConstraint : IConstraint
    {
        public int Applied;
        public int Accepted;
        public int Resets;

        public string Name => "counting";

        public bool IsComplete => false;

        public void Apply(Candidates candidates) => Applied++;

        public void Accept(int token) => Accepted++;

        public void Reset() => Resets++;
    }

    [Fact]
    public void GreedyPicksArgmaxWithLowestIdTieBreak()
    {
        SamplerConfig c = Plain(1);
        c.Greedy = true;
        SamplerChain chain = SamplerChain.Build(c);
        Assert.Equal(6, chain.Sample(Logits).Token);
        SampleResult r = chain.Sample([-Inf, 3f, 3f]);
        Assert.Equal(1, r.Token);
        Near(0.5, r.Probability, 1e-6);
    }

    [Fact]
    public void GreedyChainContainsOnlyLogitBiasAndPenalties()
    {
        var c = new SamplerConfig { Greedy = true, LogitBiases = [new LogitBias(0, 1f)] };
        SamplerChain chain = SamplerChain.Build(c);
        Assert.Equal(new[] { "logit_bias", "penalties" }, chain.StageNames());
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(999UL)]
    public void TemperatureZeroIsDeterministicRegardlessOfSeed(ulong seed)
    {
        SamplerChain chain = SamplerChain.Build(new SamplerConfig { Seed = seed, Temperature = 0f });
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(6, chain.Sample(Logits).Token);
        }
    }

    [Fact]
    public void FixedSeedReproducesTheExactTokenSequence()
    {
        SamplerChain a = SamplerChain.Build(Plain(1234));
        SamplerChain b = SamplerChain.Build(Plain(1234));
        Assert.Equal(Draw(a, Logits, 500), Draw(b, Logits, 500));
    }

    [Fact]
    public void DifferentSeedsProduceDifferentSequences()
    {
        SamplerChain a = SamplerChain.Build(Plain(1));
        SamplerChain b = SamplerChain.Build(Plain(2));
        Assert.NotEqual(Draw(a, Logits, 200), Draw(b, Logits, 200));
    }

    [Fact]
    public void ReseedAndCloneContinueIdentically()
    {
        SamplerChain a = SamplerChain.Build(Plain(77));
        int[] first = Draw(a, Logits, 50);
        a.Reseed(77);
        Assert.Equal(first, Draw(a, Logits, 50));

        SamplerChain b = SamplerChain.Build(Plain(5));
        Draw(b, Logits, 10);
        SamplerChain fork = b.Clone();
        Assert.Equal(Draw(b, Logits, 100), Draw(fork, Logits, 100));
    }

    [Fact]
    public void GoldenSequenceForSeed42WithDefaultConfig()
    {
        // The source's golden sequence (cross-checked there against an independent Python reference). Pinned so any change to the
        // RNG, ordering or stage semantics is noticed, and so this port is held to the same tokens as the source.
        SamplerChain chain = SamplerChain.Build(new SamplerConfig { Seed = 42 });
        int[] golden = [6, 1, 3, 0, 2, 3, 3, 7, 3, 1, 3, 6, 7, 6, 3, 0];
        Assert.Equal(golden, Draw(chain, Logits, 16));
    }

    [Fact]
    public void EmpiricalFrequenciesMatchSoftmaxWithPOneAndKAboveVocab()
    {
        SamplerConfig c = Plain(2024);
        c.TopK = 1000; // larger than the vocabulary
        c.TopP = 1f;
        SamplerChain chain = SamplerChain.Build(c, null, Logits.Length);
        var counts = new int[Logits.Length];
        const int n = 40000;
        for (int i = 0; i < n; i++)
        {
            counts[chain.Sample(Logits).Token]++;
        }

        double z = 0.0;
        foreach (float l in Logits)
        {
            z += Math.Exp(l);
        }

        for (int i = 0; i < Logits.Length; i++)
        {
            Near(Math.Exp(Logits[i]) / z, (double)counts[i] / n, 0.01);
        }
    }

    [Fact]
    public void TopKOneAlwaysYieldsArgmax()
    {
        SamplerConfig c = Plain(3);
        c.TopK = 1;
        SamplerChain chain = SamplerChain.Build(c);
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(6, chain.Sample(Logits).Token);
        }
    }

    [Fact]
    public void EmptyLogitsAndAllNegativeInfinityReportErrorsInsteadOfAToken()
    {
        SamplerChain chain = SamplerChain.Build(Plain(1));
        SampleResult e = chain.Sample([]);
        Assert.Equal(SampleStatus.EmptyLogits, e.Status);
        Assert.Equal(SamplingTokens.Invalid, e.Token);
        SampleResult n = chain.Sample([-Inf, -Inf, -Inf]);
        Assert.Equal(SampleStatus.NoViableCandidates, n.Status);
        Assert.False(n.Ok);
        Assert.NotEqual(SampleResult.Describe(SampleStatus.Ok), SampleResult.Describe(n.Status));
    }

    [Fact]
    public void BanningEveryTokenThroughLogitBiasYieldsNoViableCandidates()
    {
        SamplerConfig c = Plain(1);
        c.LogitBiases = [new LogitBias(0, float.NegativeInfinity), new LogitBias(1, float.NegativeInfinity)];
        SamplerChain chain = SamplerChain.Build(c);
        Assert.Equal(SampleStatus.NoViableCandidates, chain.Sample([1f, 2f]).Status);
    }

    [Fact]
    public void NaNLogitsAreTreatedAsNegativeInfinity()
    {
        SamplerChain chain = SamplerChain.Build(Plain(9));
        for (int i = 0; i < 20; i++)
        {
            SampleResult r = chain.Sample([float.NaN, 0f, float.NaN]);
            Assert.True(r.Ok);
            Assert.Equal(1, r.Token);
            Near(1.0, r.Probability, 1e-6);
        }
    }

    [Fact]
    public void PositiveInfinityLogitAlwaysWins()
    {
        SamplerChain chain = SamplerChain.Build(Plain(11));
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(1, chain.Sample([100f, Inf, 5f]).Token);
        }
    }

    [Fact]
    public void SingleTokenVocabAlwaysReturnsIt()
    {
        SamplerChain chain = SamplerChain.Build(new SamplerConfig { Seed = 1 });
        SampleResult r = chain.Sample([-3f]);
        Assert.True(r.Ok);
        Assert.Equal(0, r.Token);
        Near(1.0, r.Probability, 1e-6);
    }

    [Fact]
    public void AcceptedTokensArePenalisedInLaterSteps()
    {
        SamplerConfig c = Plain(1);
        c.Greedy = true;
        c.RepeatPenalty = 1.5f;
        SamplerChain chain = SamplerChain.Build(c);
        float[] logits = [5f, 4.9f];
        Assert.Equal(0, chain.Sample(logits).Token);
        chain.Accept(0);
        Assert.Equal(1, chain.Sample(logits).Token);
        chain.Reset();
        Assert.Equal(0, chain.Sample(logits).Token);
    }

    [Fact]
    public void AcceptPromptFeedsPenaltiesButNotTheConstraint()
    {
        SamplerConfig c = Plain(1);
        c.Greedy = true;
        c.PresencePenalty = 10f;
        var constraint = new CountingConstraint();
        SamplerChain chain = SamplerChain.Build(c, constraint);
        chain.AcceptPrompt([0, 0]);
        Assert.Equal(0, constraint.Accepted);
        Assert.Equal(1, chain.Sample([5f, 4f]).Token);
        chain.Accept(1);
        Assert.Equal(1, constraint.Accepted);
        Assert.True(constraint.Applied >= 1);
        chain.Reset();
        Assert.Equal(1, constraint.Resets);
    }

    [Fact]
    public void CustomStageOrderIsHonoured()
    {
        SamplerConfig c = Plain(1);
        c.StageOrder = [StageKind.Temperature, StageKind.MinP, StageKind.TopK];
        Assert.Equal(new[] { "temperature", "min_p", "top_k" }, SamplerChain.Build(c).StageNames());
    }

    [Fact]
    public void DefaultOrderFollowsLlamaCpp()
    {
        SamplerChain chain = SamplerChain.Build(new SamplerConfig { Seed = 1 });
        Assert.Equal(new[] { "penalties", "top_k", "typical_p", "top_p", "min_p", "temperature" }, chain.StageNames());
    }

    [Fact]
    public void OrderMattersTemperatureBeforeMinPChangesTheKeptSet()
    {
        // Logits {2, 0}: p = {0.881, 0.119}; min_p 0.2 -> threshold 0.176 drops token 1.
        // At temperature 4 first: p = {0.622, 0.378} -> token 1 survives.
        float[] logits = [2f, 0f];
        SamplerConfig c = Plain(1);
        c.MinP = 0.2f;
        c.Temperature = 4f;
        c.StageOrder = [StageKind.MinP, StageKind.Temperature];
        SamplerChain late = SamplerChain.Build(c);
        late.Sample(logits);
        Assert.Equal(1, late.LastCandidates.Count);
        c.StageOrder = [StageKind.Temperature, StageKind.MinP];
        SamplerChain early = SamplerChain.Build(c);
        early.Sample(logits);
        Assert.Equal(2, early.LastCandidates.Count);
    }

    [Fact]
    public void StopTokensAreReported()
    {
        SamplerChain chain = SamplerChain.Build(new SamplerConfig { StopTokens = [2, 7] });
        Assert.True(chain.IsStopToken(2));
        Assert.True(chain.IsStopToken(7));
        Assert.False(chain.IsStopToken(3));
    }

    [Fact]
    public void ManuallyComposedChainWorksAndRejectsNullStages()
    {
        var chain = new SamplerChain(5, SamplerSelector.Greedy);
        chain.Add(new LogitBiasSampler([new LogitBias(0, 10f)])).Add(new TopKSampler(2));
        Assert.Equal(2, chain.StageCount);
        Assert.Equal(0, chain.Sample(Logits).Token);
        Assert.Throws<ArgumentNullException>(() => chain.Add(null!));
    }

    [Fact]
    public void UnseededConfigStillSamplesValidTokens()
    {
        SamplerChain chain = SamplerChain.Build(new SamplerConfig()); // no seed: entropy seed
        for (int i = 0; i < 20; i++)
        {
            SampleResult r = chain.Sample(Logits);
            Assert.True(r.Ok);
            Assert.InRange(r.Token, 0, Logits.Length - 1);
        }
    }

    [Fact]
    public void VocabularySizedStepsReuseTheCandidateBuffer()
    {
        // ArcaneCore: a Qwen-sized vocabulary (~152k) with the default chain; after warm-up a step allocates nothing.
        // Measured through AllocationProbe, not a raw counter: other threads' gen0 collections and replacements of the runtime's
        // process-wide caches (cast cache, GVM cache) land on the measuring thread under parallel load.
        var logits = new float[151_936];
        var rng = new SamplingRng(3);
        for (int i = 0; i < logits.Length; i++)
        {
            logits[i] = (float)(rng.Uniform() * 20.0) - 10f;
        }

        SamplerChain chain = SamplerChain.Build(new SamplerConfig { Seed = 42, RepeatPenalty = 1.1f }, null, logits.Length);
        for (int i = 0; i < 3; i++)
        {
            chain.Accept(chain.Sample(logits).Token);
        }

        SampleResult last = default;
        Action window = () =>
        {
            for (int i = 0; i < 2; i++)
            {
                last = chain.Sample(logits);
            }
        };
        AllocationProbe.AssertEveryCleanWindowAtMost(window, 0, "SamplerChain.Sample (152k vocab) x2");
        Assert.True(last.Ok);
    }
}
