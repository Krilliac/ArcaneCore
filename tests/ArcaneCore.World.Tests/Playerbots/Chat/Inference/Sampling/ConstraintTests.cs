// Ported from Krilliac/Sonder-Inference src/sampling/tests/test_constraint.cpp @1c4c152 (Apache-2.0)

using ArcaneCore.World.Playerbots.Chat.Inference.Sampling;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference.Sampling;

/// <summary>The constraint hook: the allow-list, ordering before truncation, over-constraint, a stateful constraint, replacement.</summary>
public sealed class ConstraintTests
{
    // Toy stateful constraint: forces a fixed token sequence, then completes.
    private sealed class SequenceConstraint(int[] sequence) : IConstraint
    {
        private int _pos;

        public string Name => "sequence";

        public bool IsComplete => _pos >= sequence.Length;

        public void Apply(Candidates candidates)
        {
            foreach (ref TokenData t in candidates.Span)
            {
                if (_pos >= sequence.Length || t.Id != sequence[_pos])
                {
                    t.Logit = float.NegativeInfinity;
                }
            }

            candidates.Sorted = false;
        }

        public void Accept(int token) => _pos++;

        public void Reset() => _pos = 0;
    }

    private static SamplerConfig RandomConfig(ulong seed) => new()
    {
        Seed = seed,
        Temperature = 1.5f,
        TopK = 0,
        TopP = 1f,
        MinP = 0f,
    };

    [Fact]
    public void AllowlistMasksEveryOtherToken()
    {
        var a = new TokenAllowlistConstraint([1, 3]);
        Candidates c = Candidates.FromLogits([1f, 1f, 1f, 1f, 1f]);
        a.Apply(c);
        Assert.Equal(float.NegativeInfinity, c[0].Logit);
        Assert.Equal(1f, c[1].Logit);
        Assert.Equal(float.NegativeInfinity, c[2].Logit);
        Assert.Equal(1f, c[3].Logit);
        Assert.Equal(float.NegativeInfinity, c[4].Logit);
        Assert.Equal("allowlist", a.Name);
        Assert.False(a.IsComplete);
    }

    [Fact]
    public void ChainWithAllowlistOnlySamplesAllowedTokens()
    {
        SamplerChain chain = SamplerChain.Build(RandomConfig(8), new TokenAllowlistConstraint([2, 5]));
        float[] logits = [9f, 8f, 0f, 7f, 6f, 0.1f];
        var seen = new SortedSet<int>();
        for (int i = 0; i < 500; i++)
        {
            SampleResult r = chain.Sample(logits);
            Assert.True(r.Ok);
            seen.Add(r.Token);
        }

        Assert.Equal(new[] { 2, 5 }, seen);
    }

    [Fact]
    public void ConstraintRunsBeforeTruncationStages()
    {
        SamplerConfig c = RandomConfig(1);
        c.TopK = 1; // would pick token 0 without the constraint
        SamplerChain chain = SamplerChain.Build(c, new TokenAllowlistConstraint([3]));
        Assert.Equal(3, chain.Sample([10f, 1f, 1f, -5f]).Token);
    }

    [Fact]
    public void OverConstrainedSamplingReportsNoViableCandidates()
    {
        SamplerChain chain = SamplerChain.Build(RandomConfig(1), new TokenAllowlistConstraint([42]));
        Assert.Equal(SampleStatus.NoViableCandidates, chain.Sample([1f, 2f]).Status);
    }

    [Fact]
    public void StatefulConstraintAdvancesOnAcceptAndResets()
    {
        var seq = new SequenceConstraint([4, 1, 3]);
        SamplerChain chain = SamplerChain.Build(RandomConfig(99), seq);
        var logits = new float[6];
        var output = new List<int>();
        while (!seq.IsComplete)
        {
            SampleResult r = chain.Sample(logits);
            Assert.True(r.Ok);
            output.Add(r.Token);
            chain.Accept(r.Token);
        }

        Assert.Equal(new[] { 4, 1, 3 }, output);
        Assert.Equal(SampleStatus.NoViableCandidates, chain.Sample(logits).Status);
        chain.Reset();
        Assert.False(seq.IsComplete);
        Assert.Equal(4, chain.Sample(logits).Token);
    }

    [Fact]
    public void ConstraintCanBeReplacedOrCleared()
    {
        SamplerChain chain = SamplerChain.Build(RandomConfig(3), new TokenAllowlistConstraint([0]));
        float[] logits = [0f, 50f];
        Assert.Equal(0, chain.Sample(logits).Token);
        chain.SetConstraint(null);
        Assert.Equal(1, chain.Sample(logits).Token);
    }
}
