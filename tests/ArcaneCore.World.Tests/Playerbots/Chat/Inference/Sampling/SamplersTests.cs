// Ported from Krilliac/Sonder-Inference src/sampling/tests/test_samplers.cpp @1c4c152 (Apache-2.0)

using ArcaneCore.World.Playerbots.Chat.Inference.Sampling;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference.Sampling;

/// <summary>Each stage on hand-built candidate sets: softmax and canonical order, top-k, top-p, min-p, typical-p, temperature, penalties, logit bias.</summary>
public sealed class SamplersTests
{
    private const float Inf = float.PositiveInfinity;

    private static void Near(double expected, double actual, double eps) => Assert.True(Math.Abs(expected - actual) <= eps, $"{actual} not within {eps} of {expected}");

    private static Candidates Make(params float[] logits) => Candidates.FromLogits(logits);

    // Logits whose softmax is exactly the given probabilities.
    private static Candidates FromProbs(params double[] probs)
    {
        var logits = new float[probs.Length];
        for (int i = 0; i < probs.Length; i++)
        {
            logits[i] = (float)Math.Log(probs[i]);
        }

        return Make(logits);
    }

    private static int[] Ids(Candidates c)
    {
        var ids = new int[c.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = c[i].Id;
        }

        Array.Sort(ids);
        return ids;
    }

    private static float LogitOf(Candidates c, int id)
    {
        for (int i = 0; i < c.Count; i++)
        {
            if (c[i].Id == id)
            {
                return c[i].Logit;
            }
        }

        return float.NaN;
    }

    // ------------------------------------------------------------------ helpers

    [Fact]
    public void SoftmaxNormalisesAndOrders()
    {
        Candidates c = Make(1f, 2f, 3f);
        Assert.True(SamplingMath.Softmax(c));
        double sum = 0.0;
        for (int i = 0; i < c.Count; i++)
        {
            sum += c[i].P;
        }

        Near(1.0, sum, 1e-6);
        Assert.True(c[2].P > c[1].P && c[1].P > c[0].P);
    }

    [Fact]
    public void SoftmaxIsStableForHugeLogits()
    {
        Candidates c = Make(10000f, 10000f, -10000f);
        Assert.True(SamplingMath.Softmax(c));
        Near(0.5, c[0].P, 1e-6);
        Near(0.0, c[2].P, 1e-12);
    }

    [Fact]
    public void SoftmaxWithAllNegativeInfinityReportsNoViableCandidate()
    {
        Candidates c = Make(-Inf, -Inf);
        Assert.False(SamplingMath.Softmax(c));
        Assert.True(c[0].P == 0f && c[1].P == 0f);
        Assert.Equal(0, SamplingMath.ViableCount(c));
        Assert.False(SamplingMath.Softmax(new Candidates()));
    }

    [Fact]
    public void SoftmaxWithPositiveInfinityIsUniformOverTheInfiniteTokens()
    {
        Candidates c = Make(Inf, 3f, Inf, -Inf);
        Assert.True(SamplingMath.Softmax(c));
        Near(0.5, c[0].P, 1e-7);
        Near(0.5, c[2].P, 1e-7);
        Assert.Equal(0f, c[1].P);
    }

    [Fact]
    public void CanonicalSortBreaksTiesByLowerTokenId()
    {
        Candidates c = Make(1f, 5f, 5f, 2f);
        SamplingMath.SortCanonical(c);
        Assert.Equal(1, c[0].Id);
        Assert.Equal(2, c[1].Id);
        Assert.Equal(3, c[2].Id);
        Assert.Equal(0, c[3].Id);
    }

    [Fact]
    public void PartialSortMatchesAFullSortPrefix()
    {
        // ArcaneCore: the heap partial sort must agree with a full canonical sort on a vocabulary-sized set with many ties.
        var rng = new SamplingRng(5);
        var logits = new float[4096];
        for (int i = 0; i < logits.Length; i++)
        {
            logits[i] = (float)Math.Round(rng.Uniform() * 50.0); // coarse values force ties
        }

        logits[17] = float.NegativeInfinity;
        Candidates full = Make(logits);
        SamplingMath.SortCanonical(full);
        foreach (int k in new[] { 1, 2, 40, 1000, 4095 })
        {
            Candidates partial = Make(logits);
            SamplingMath.PartialSortCanonical(partial.Span, k);
            for (int i = 0; i < k; i++)
            {
                Assert.Equal(full[i].Id, partial[i].Id);
            }
        }
    }

    // ------------------------------------------------------------------- top-k

    [Fact]
    public void TopKKeepsTheKHighestLogits()
    {
        Candidates c = Make(0.1f, 3f, 2f, -1f, 2.5f);
        new TopKSampler(2).Apply(c);
        Assert.Equal(new[] { 1, 4 }, Ids(c));
        Assert.True(c.Sorted);
    }

    [Fact]
    public void TopKZeroDisablesAndKAboveVocabKeepsEverything()
    {
        Candidates a = Make(1f, 2f, 3f);
        new TopKSampler(0).Apply(a);
        Assert.Equal(3, a.Count);
        Candidates b = Make(1f, 2f, 3f);
        new TopKSampler(1000).Apply(b);
        Assert.Equal(3, b.Count);
        Candidates d = Make(1f, 2f, 3f);
        new TopKSampler(3).Apply(d);
        Assert.Equal(3, d.Count);
    }

    [Fact]
    public void TopKTiesKeepTheLowerTokenId()
    {
        Candidates c = Make(1f, 1f, 1f);
        new TopKSampler(1).Apply(c);
        Assert.Equal(1, c.Count);
        Assert.Equal(0, c[0].Id);
    }

    // ------------------------------------------------------------------- top-p

    [Fact]
    public void TopPKeepsSmallestPrefixReachingPInclusive()
    {
        Candidates a = Make(0f, 0f); // exactly 0.5 / 0.5
        new TopPSampler(0.5f, 1).Apply(a);
        Assert.Equal(new[] { 0 }, Ids(a));
        Candidates b = FromProbs(0.5, 0.3, 0.2);
        new TopPSampler(0.6f, 1).Apply(b);
        Assert.Equal(new[] { 0, 1 }, Ids(b));
        Candidates d = FromProbs(0.2, 0.5, 0.3);
        new TopPSampler(0.75f, 1).Apply(d);
        Assert.Equal(new[] { 1, 2 }, Ids(d));
    }

    [Fact]
    public void TopPOneIsANoOp()
    {
        Candidates c = FromProbs(0.97, 0.01, 0.01, 0.01);
        new TopPSampler(1f, 1).Apply(c);
        Assert.Equal(4, c.Count);
    }

    [Fact]
    public void TopPZeroKeepsMinKeepAtLeastOne()
    {
        Candidates a = FromProbs(0.4, 0.35, 0.25);
        new TopPSampler(0f, 0).Apply(a);
        Assert.Equal(new[] { 0 }, Ids(a));
        Candidates b = FromProbs(0.4, 0.35, 0.25);
        new TopPSampler(0f, 2).Apply(b);
        Assert.Equal(new[] { 0, 1 }, Ids(b));
    }

    [Fact]
    public void TopPIgnoresNegativeInfinityTokensAndToleratesAllNegativeInfinity()
    {
        Candidates a = Make(0f, -Inf, 0f, -Inf);
        new TopPSampler(0.9f, 1).Apply(a);
        Assert.Equal(new[] { 0, 2 }, Ids(a));
        Candidates b = Make(-Inf, -Inf);
        new TopPSampler(0.5f, 1).Apply(b);
        Assert.Equal(2, b.Count);
    }

    // ------------------------------------------------------------------- min-p

    [Fact]
    public void MinPKeepsTokensAtLeastMinPTimesPMax()
    {
        Candidates c = FromProbs(0.5, 0.3, 0.2);
        new MinPSampler(0.5f, 1).Apply(c); // threshold 0.25
        Assert.Equal(new[] { 0, 1 }, Ids(c));
    }

    [Fact]
    public void MinPZeroDisablesAndOneKeepsOnlyTiesWithMax()
    {
        Candidates a = FromProbs(0.5, 0.3, 0.2);
        new MinPSampler(0f, 1).Apply(a);
        Assert.Equal(3, a.Count);
        Candidates b = Make(2f, 2f, 1f);
        new MinPSampler(1f, 1).Apply(b);
        Assert.Equal(new[] { 0, 1 }, Ids(b));
    }

    [Fact]
    public void MinPHonoursMinKeep()
    {
        Candidates c = FromProbs(0.9, 0.05, 0.05);
        new MinPSampler(0.5f, 2).Apply(c);
        Assert.Equal(2, c.Count);
        Assert.Equal(0, c[0].Id);
    }

    // --------------------------------------------------------------- typical-p

    [Fact]
    public void TypicalPOneIsANoOp()
    {
        Candidates c = FromProbs(0.7, 0.2, 0.1);
        new TypicalPSampler(1f, 1).Apply(c);
        Assert.Equal(3, c.Count);
    }

    [Fact]
    public void TypicalPOnAUniformDistributionCutsByStrictCumulativeMass()
    {
        Candidates c = FromProbs(0.25, 0.25, 0.25, 0.25);
        new TypicalPSampler(0.5f, 1).Apply(c); // cum .25, .5, .75: the first > .5 is 3 tokens
        Assert.Equal(new[] { 0, 1, 2 }, Ids(c));
    }

    [Fact]
    public void TypicalPCanDropTheMostLikelyToken()
    {
        // H = 1.0889; |-log p - H|: 0.4 -> 0.173, 0.3 -> 0.115. Tokens 1 and 2 are more typical.
        Candidates c = FromProbs(0.4, 0.3, 0.3);
        new TypicalPSampler(0.5f, 1).Apply(c);
        Assert.Equal(new[] { 1, 2 }, Ids(c));
        Assert.True(c.Sorted);
    }

    [Fact]
    public void TypicalPKeepsThePeakOnSkewedDistributionsAndHandlesNegativeInfinity()
    {
        Candidates c = FromProbs(0.7, 0.2, 0.1);
        new TypicalPSampler(0.5f, 1).Apply(c);
        Assert.Equal(new[] { 0 }, Ids(c));
        Candidates d = Make(0f, -Inf, 0f);
        new TypicalPSampler(0.9f, 1).Apply(d);
        Assert.Equal(new[] { 0, 2 }, Ids(d));
        Candidates e = Make(-Inf, -Inf);
        new TypicalPSampler(0.5f, 1).Apply(e);
        Assert.Equal(2, e.Count);
    }

    // ------------------------------------------------------------- temperature

    [Fact]
    public void TemperatureDividesLogits()
    {
        Candidates c = Make(2f, -4f, -Inf);
        new TemperatureSampler(2f).Apply(c);
        Near(1.0, LogitOf(c, 0), 1e-6);
        Near(-2.0, LogitOf(c, 1), 1e-6);
        Assert.Equal(float.NegativeInfinity, LogitOf(c, 2));
        Candidates d = Make(2f);
        new TemperatureSampler(1f).Apply(d);
        Assert.Equal(2f, LogitOf(d, 0));
    }

    [Fact]
    public void TemperatureZeroKeepsOnlyTheArgmaxLowestIdOnTies()
    {
        Candidates c = Make(1f, 3f, 3f, 2f);
        new TemperatureSampler(0f).Apply(c);
        Assert.Equal(1, c.Count);
        Assert.Equal(1, c[0].Id);
        Candidates allNeg = Make(-Inf, -Inf);
        new TemperatureSampler(0f).Apply(allNeg);
        Assert.Equal(2, allNeg.Count);
    }

    // --------------------------------------------------------------- penalties

    [Fact]
    public void RepeatPenaltyDividesPositiveAndMultipliesNegativeLogits()
    {
        var s = new PenaltiesSampler(64, 2f, 0f, 0f);
        s.Accept(0);
        s.Accept(1);
        Candidates c = Make(4f, -2f, 4f);
        s.Apply(c);
        Near(2.0, LogitOf(c, 0), 1e-6);
        Near(-4.0, LogitOf(c, 1), 1e-6);
        Near(4.0, LogitOf(c, 2), 1e-6); // unseen token untouched
    }

    [Fact]
    public void RepeatPenaltyIsTheSameOnTheSparseAndScanningPaths()
    {
        // ArcaneCore: id-ordered sets are penalised by index; a reordered set takes the scan. Both must agree.
        var s = new PenaltiesSampler(64, 1.3f, 0.2f, 0.1f);
        s.Accept(3);
        s.Accept(3);
        s.Accept(0);
        Candidates ordered = Make(1f, -1f, 0.5f, 2f);
        Candidates shuffled = Make(1f, -1f, 0.5f, 2f);
        SamplingMath.SortCanonical(shuffled);
        Assert.False(shuffled.IdOrdered);
        s.Apply(ordered);
        s.Apply(shuffled);
        for (int id = 0; id < 4; id++)
        {
            Assert.Equal(LogitOf(ordered, id), LogitOf(shuffled, id));
        }
    }

    [Fact]
    public void FrequencyScalesWithCountPresenceAppliesOnce()
    {
        var s = new PenaltiesSampler(64, 1f, 0.5f, 1f);
        s.Accept(0);
        s.Accept(0);
        s.Accept(0);
        s.Accept(1);
        Assert.Equal(3, s.Count(0));
        Candidates c = Make(10f, 10f, 10f);
        s.Apply(c);
        Near(10.0 - 1.5 - 1.0, LogitOf(c, 0), 1e-6);
        Near(10.0 - 0.5 - 1.0, LogitOf(c, 1), 1e-6);
        Near(10.0, LogitOf(c, 2), 1e-6);
    }

    [Fact]
    public void PenaltyWindowEvictsOldTokensZeroDisablesMinusOneIsUnbounded()
    {
        var w = new PenaltiesSampler(2, 2f, 0f, 0f);
        w.Accept(5);
        w.Accept(6);
        w.Accept(7);
        Assert.Equal(0, w.Count(5));
        Assert.Equal(1, w.Count(7));

        var off = new PenaltiesSampler(0, 2f, 1f, 1f);
        off.Accept(0);
        Candidates c = Make(4f);
        off.Apply(c);
        Assert.Equal(4f, LogitOf(c, 0));

        var all = new PenaltiesSampler(-1, 1f, 1f, 0f);
        for (int i = 0; i < 1000; i++)
        {
            all.Accept(3);
        }

        Assert.Equal(1000, all.Count(3));
    }

    [Fact]
    public void PenaltiesResetAndCloneAreIndependent()
    {
        var s = new PenaltiesSampler(8, 2f, 0f, 0f);
        s.Accept(1);
        Sampler copy = s.Clone();
        s.Reset();
        Assert.Equal(0, s.Count(1));
        Candidates c = Make(0f, 4f);
        copy.Apply(c);
        Near(2.0, LogitOf(c, 1), 1e-6);
    }

    // -------------------------------------------------------------- logit bias

    [Fact]
    public void LogitBiasAddsAccumulatesBansAndIgnoresUnknownIds()
    {
        var s = new LogitBiasSampler([new LogitBias(0, 1f), new LogitBias(0, 0.5f), new LogitBias(1, float.NegativeInfinity), new LogitBias(99, 3f)]);
        Candidates c = Make(1f, 1f, 1f);
        s.Apply(c);
        Near(2.5, LogitOf(c, 0), 1e-6);
        Assert.Equal(float.NegativeInfinity, LogitOf(c, 1));
        Near(1.0, LogitOf(c, 2), 1e-6);

        Candidates sorted = Make(1f, 1f, 1f);
        SamplingMath.SortCanonical(sorted);
        sorted.Sorted = false;
        sorted.IdOrdered = false;
        s.Apply(sorted);
        Near(2.5, LogitOf(sorted, 0), 1e-6);
        Assert.Equal(float.NegativeInfinity, LogitOf(sorted, 1));
    }

    [Fact]
    public void CreateBuildsTheRightStageForEachKind()
    {
        var cfg = new SamplerConfig();
        Assert.Equal("penalties", Sampler.Create(StageKind.Penalties, cfg).Name);
        Assert.Equal("top_k", Sampler.Create(StageKind.TopK, cfg).Name);
        Assert.Equal("typical_p", Sampler.Create(StageKind.TypicalP, cfg).Name);
        Assert.Equal("top_p", Sampler.Create(StageKind.TopP, cfg).Name);
        Assert.Equal("min_p", Sampler.Create(StageKind.MinP, cfg).Name);
        Assert.Equal("temperature", Sampler.Create(StageKind.Temperature, cfg).Name);
    }

    [Fact]
    public void StagesTolerateEmptyCandidateSets()
    {
        var c = new Candidates();
        new TopKSampler(3).Apply(c);
        new TopPSampler(0.5f, 1).Apply(c);
        new MinPSampler(0.5f, 1).Apply(c);
        new TypicalPSampler(0.5f, 1).Apply(c);
        new TemperatureSampler(0f).Apply(c);
        new TemperatureSampler(0.7f).Apply(c);
        Assert.True(c.IsEmpty);
    }
}
