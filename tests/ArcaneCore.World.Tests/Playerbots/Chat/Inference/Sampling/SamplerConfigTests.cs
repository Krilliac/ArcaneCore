// Ported from Krilliac/Sonder-Inference src/sampling/tests/test_config.cpp @1c4c152 (Apache-2.0)

using ArcaneCore.World.Playerbots.Chat.Inference.Sampling;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference.Sampling;

/// <summary>Config defaults (llama.cpp), validation of every field, the error string, and stage names.</summary>
public sealed class SamplerConfigTests
{
    private static void Near(double expected, double actual, double eps) => Assert.True(Math.Abs(expected - actual) <= eps, $"{actual} not within {eps} of {expected}");

    [Fact]
    public void DefaultConfigIsValidAndMatchesLlamaCppDefaults()
    {
        var c = new SamplerConfig();
        Assert.True(c.Validate().Ok);
        Assert.True(c.Validate(32000).Ok);
        Near(0.8, c.Temperature, 1e-6);
        Assert.Equal(40, c.TopK);
        Near(0.95, c.TopP, 1e-6);
        Near(0.05, c.MinP, 1e-6);
        Near(1.0, c.TypicalP, 1e-6);
        Assert.Equal(64, c.PenaltyLastN);
        Near(1.0, c.RepeatPenalty, 1e-6);
        Assert.Equal(StageKinds.DefaultOrder(), c.StageOrder);
    }

    [Fact]
    public void TemperatureMustBeFiniteAndNonNegative()
    {
        var c = new SamplerConfig { Temperature = -0.1f };
        Assert.True(c.Validate().HasError("temperature"));
        c.Temperature = float.NaN;
        Assert.True(c.Validate().HasError("temperature"));
        c.Temperature = float.PositiveInfinity;
        Assert.True(c.Validate().HasError("temperature"));
        c.Temperature = 0f;
        Assert.True(c.Validate().Ok);
    }

    [Fact]
    public void TopKMustBeNonNegativeLargeValuesAllowed()
    {
        var c = new SamplerConfig { TopK = -1 };
        Assert.True(c.Validate().HasError("top_k"));
        c.TopK = 0;
        Assert.True(c.Validate().Ok);
        c.TopK = 1 << 30;
        Assert.True(c.Validate(10).Ok);
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void ProbabilityParametersMustBeWithinZeroAndOne(float bad)
    {
        var c = new SamplerConfig { TopP = bad, MinP = bad, TypicalP = bad };
        ValidationResult r = c.Validate();
        Assert.True(r.HasError("top_p"));
        Assert.True(r.HasError("min_p"));
        Assert.True(r.HasError("typical_p"));
    }

    [Fact]
    public void ProbabilityParameterEdgesAreValid()
    {
        var edge = new SamplerConfig { TopP = 0f, MinP = 1f, TypicalP = 0f };
        Assert.True(edge.Validate().Ok);
    }

    [Fact]
    public void PenaltyFieldsAreValidated()
    {
        var c = new SamplerConfig
        {
            PenaltyLastN = -2,
            RepeatPenalty = 0f,
            FrequencyPenalty = float.NaN,
            PresencePenalty = float.PositiveInfinity,
            MinKeep = -1,
        };
        ValidationResult r = c.Validate();
        Assert.True(r.HasError("penalty_last_n"));
        Assert.True(r.HasError("repeat_penalty"));
        Assert.True(r.HasError("frequency_penalty"));
        Assert.True(r.HasError("presence_penalty"));
        Assert.True(r.HasError("min_keep"));
        Assert.Equal(5, r.Errors.Count);

        // Negative penalties encourage repetition; allowed.
        var ok = new SamplerConfig { PenaltyLastN = -1, FrequencyPenalty = -2f, PresencePenalty = 2f };
        Assert.True(ok.Validate().Ok);
    }

    [Fact]
    public void LogitBiasTokenIdsAndValuesAreValidated()
    {
        var c = new SamplerConfig
        {
            LogitBiases =
            [
                new LogitBias(-1, 1f),
                new LogitBias(5, float.NaN),
                new LogitBias(6, float.PositiveInfinity),
                new LogitBias(7, float.NegativeInfinity),
                new LogitBias(100, 1f),
            ],
        };
        ValidationResult noVocab = c.Validate();
        Assert.True(noVocab.HasError("logit_bias[0]"));
        Assert.True(noVocab.HasError("logit_bias[1]"));
        Assert.True(noVocab.HasError("logit_bias[2]"));
        Assert.False(noVocab.HasError("logit_bias[3]")); // -inf bans the token: allowed
        Assert.False(noVocab.HasError("logit_bias[4]")); // vocab unknown
        Assert.True(c.Validate(50).HasError("logit_bias[4]"));
    }

    [Fact]
    public void StopSequencesAndStopTokensAreValidated()
    {
        var c = new SamplerConfig { StopSequences = ["ok", ""], StopTokens = [2, -5, 99] };
        ValidationResult r = c.Validate(10);
        Assert.False(r.HasError("stop_sequences[0]"));
        Assert.True(r.HasError("stop_sequences[1]"));
        Assert.False(r.HasError("stop_tokens[0]"));
        Assert.True(r.HasError("stop_tokens[1]"));
        Assert.True(r.HasError("stop_tokens[2]"));
    }

    [Fact]
    public void StageOrderRejectsDuplicatesAcceptsSubsetsAndEmpty()
    {
        var c = new SamplerConfig { StageOrder = [StageKind.TopK, StageKind.Temperature, StageKind.TopK] };
        Assert.True(c.Validate().HasError("stage_order[2]"));
        c.StageOrder = [StageKind.Temperature, StageKind.MinP];
        Assert.True(c.Validate().Ok);
        c.StageOrder = [];
        Assert.True(c.Validate().Ok);
    }

    [Fact]
    public void ZeroVocabSizeIsRejected() => Assert.True(new SamplerConfig().Validate(0).HasError("vocab_size"));

    [Fact]
    public void ErrorStringNamesEveryFailingField()
    {
        var c = new SamplerConfig { TopP = 2f, TopK = -3 };
        string s = c.Validate().ToString();
        Assert.Contains("sampling config invalid", s, StringComparison.Ordinal);
        Assert.Contains("top_p", s, StringComparison.Ordinal);
        Assert.Contains("top_k", s, StringComparison.Ordinal);
        Assert.Contains("got 2", s, StringComparison.Ordinal);
        Assert.Equal("sampling config valid", new SamplerConfig().Validate().ToString());
    }

    [Fact]
    public void BuildThrowsWithTheValidationMessage()
    {
        var c = new SamplerConfig { MinP = -1f };
        ArgumentException e = Assert.Throws<ArgumentException>(() => SamplerChain.Build(c));
        Assert.Contains("min_p", e.Message, StringComparison.Ordinal);

        var v = new SamplerConfig { LogitBiases = [new LogitBias(20, 1f)] };
        Assert.Throws<ArgumentException>(() => SamplerChain.Build(v, null, 10));
    }

    [Fact]
    public void StageNamesRoundTripThroughParse()
    {
        foreach (StageKind kind in StageKinds.DefaultOrder())
        {
            Assert.Equal(kind, StageKinds.Parse(StageKinds.ToName(kind)));
        }

        Assert.Equal(StageKind.TypicalP, StageKinds.Parse("typical_p"));
        Assert.Equal(StageKind.Temperature, StageKinds.Parse("temp"));
        Assert.False(StageKinds.Parse("xtc").HasValue);
        Assert.False(StageKinds.Parse(string.Empty).HasValue);
    }
}
