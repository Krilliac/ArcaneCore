using System.Numerics.Tensors;
using ArcaneCore.World.Playerbots.Chat.Inference;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference;

/// <summary>
/// The engine's arithmetic against plain reference arithmetic: RMSNorm, softmax, SwiGLU and both RoPE layouts, then whole forward
/// passes of tiny synthetic llama and qwen2 models against <see cref="TinyModel.ReferenceLogits"/> (double precision, nested loops).
/// </summary>
public sealed class TransformerTests
{
    [Fact]
    public void RmsNorm_matches_the_formula()
    {
        float[] x = [1f, -2f, 3f, 0.5f];
        float[] w = [1f, 0.5f, 2f, -1f];
        var y = new float[4];
        TransformerMath.RmsNorm(x, w, y, 1e-5f);
        double rms = Math.Sqrt(((1 + 4 + 9 + 0.25) / 4) + 1e-5);
        for (int i = 0; i < 4; i++) Assert.Equal(x[i] / rms * w[i], y[i], 5);
    }

    [Fact]
    public void Softmax_matches_the_formula_and_survives_large_values()
    {
        float[] v = [1f, 2f, 3f];
        TransformerMath.Softmax(v);
        double sum = Math.Exp(1) + Math.Exp(2) + Math.Exp(3);
        Assert.Equal(Math.Exp(1) / sum, v[0], 5);
        Assert.Equal(Math.Exp(3) / sum, v[2], 5);

        float[] big = [1000f, 1001f];
        TransformerMath.Softmax(big);
        Assert.Equal(1 / (1 + Math.E), big[0], 5);
        Assert.Equal(1f, big[0] + big[1], 5);
    }

    [Fact]
    public void SwiGlu_is_silu_of_the_gate_times_up()
    {
        float[] gate = [-2f, 0f, 1.5f];
        float[] up = [3f, 4f, -1f];
        float[] expected = [.. gate.Select((g, i) => (float)(g / (1 + Math.Exp(-g)) * up[i]))];
        TransformerMath.SwiGlu(gate, up);
        for (int i = 0; i < 3; i++) Assert.Equal(expected[i], gate[i], 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rope_rotates_the_right_pairs_by_position(bool neox)
    {
        const int dims = 8, pos = 5;
        const float theBase = 10000f;
        (float[] cos, float[] sin) = TransformerMath.RopeTables(16, dims, theBase);
        float[] head = [.. Enumerable.Range(1, dims).Select(i => (float)i)];
        float[] original = (float[])head.Clone();
        TransformerMath.Rope(head, dims, cos.AsSpan(pos * dims / 2, dims / 2), sin.AsSpan(pos * dims / 2, dims / 2), neox, new float[dims]);
        for (int i = 0; i < dims / 2; i++)
        {
            double angle = pos * Math.Pow(theBase, -2.0 * i / dims);
            int a = neox ? i : 2 * i, b = neox ? i + (dims / 2) : (2 * i) + 1;
            Assert.Equal((original[a] * Math.Cos(angle)) - (original[b] * Math.Sin(angle)), head[a], 4);
            Assert.Equal((original[a] * Math.Sin(angle)) + (original[b] * Math.Cos(angle)), head[b], 4);
        }

        // Position 0 is the identity.
        float[] same = (float[])original.Clone();
        TransformerMath.Rope(same, dims, cos.AsSpan(0, dims / 2), sin.AsSpan(0, dims / 2), neox, new float[dims]);
        Assert.Equal(original, same);
    }

    public static TheoryData<string, int, int, int, int, int, bool> Models => new()
    {
        { "llama", 8, 2, 1, 16, 1, false },
        { "qwen2", 8, 2, 1, 16, 1, false },
        { "llama", 12, 3, 3, 20, 2, true },
        { "qwen2", 16, 4, 2, 24, 2, true },
    };

    [Theory]
    [MemberData(nameof(Models))]
    public void Forward_pass_matches_the_reference(string arch, int dim, int heads, int kvHeads, int ffn, int layers, bool tied)
    {
        TinyModel tiny = TinyModel.Create(arch, dim, heads, kvHeads, ffn, layers, tied: tied);
        AssertMatchesReference(tiny, threads: 2);
    }

    [Fact]
    public void Q8_0_weights_match_the_reference_on_their_dequantized_values()
    {
        TinyModel tiny = TinyModel.Create("qwen2", 32, 4, 2, 64, layers: 1, matrixType: GgmlType.Q8_0);
        AssertMatchesReference(tiny, threads: 3);
    }

    private static void AssertMatchesReference(TinyModel tiny, int threads)
    {
        string path = tiny.Builder.WriteTemp();
        try
        {
            using ChatModel model = ChatModel.Load(path);
            Assert.Equal(tiny.Architecture, model.Config.Architecture);
            Assert.Equal(tiny.Architecture == "qwen2", model.Config.RopeNeox);
            int[] tokens = [104, 101, 108, 108, 111, 32, 33];
            double[] expected = tiny.ReferenceLogits(tokens);

            // The whole prompt in one pass.
            using (var batch = new ChatInferenceContext(model, 64, threads))
            {
                batch.Forward(tokens, 0, wantLogits: true);
                AssertClose(expected, batch.Logits);
            }

            // One token per pass (the decode path), on one thread.
            using (var single = new ChatInferenceContext(model, 64, 1))
            {
                for (int t = 0; t < tokens.Length; t++) single.Forward(tokens.AsSpan(t, 1), t, wantLogits: t == tokens.Length - 1);
                AssertClose(expected, single.Logits);
            }
        }
        finally
        {
            GgufBuilder.Delete(path);
        }
    }

    private static void AssertClose(double[] expected, ReadOnlySpan<float> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= 1e-3 * Math.Max(1, Math.Abs(expected[i])), $"logit {i}: expected {expected[i]}, got {actual[i]}");
    }

    [Fact]
    public void Greedy_generation_takes_the_reference_argmax_and_reuses_the_cached_prompt()
    {
        TinyModel tiny = TinyModel.Create("llama", 8, 2, 1, 16);
        string path = tiny.Builder.WriteTemp();
        try
        {
            using ChatModel model = ChatModel.Load(path);
            using var context = new ChatInferenceContext(model, 64, 2);
            int[] prompt = [104, 101, 108, 108, 111];
            var greedy = new ChatGenerationSettings(MaxNewTokens: 4, BudgetMs: 60_000, Temperature: 0f, TopK: 1, TopP: 1f, RepetitionPenalty: 1f, Seed: 1, StopAtNewline: false);
            ChatGenerationResult first = context.Generate(prompt, greedy, CancellationToken.None);
            Assert.Equal(ChatGenerationStatus.Completed, first.Status);
            Assert.Equal(0, first.ReusedTokens);
            double[] reference = tiny.ReferenceLogits(prompt);
            int argmax = Array.IndexOf(reference, reference.Max());
            if (model.Tokenizer.IsControl(argmax)) Assert.Empty(first.Tokens);
            else Assert.Equal(argmax, first.Tokens[0]);

            ChatGenerationResult second = context.Generate(prompt, greedy, CancellationToken.None);
            Assert.Equal(prompt.Length - 1, second.ReusedTokens); // only the last prompt token is computed again
            Assert.Equal(first.Tokens, second.Tokens);          // greedy is deterministic

            Assert.Equal(ChatGenerationStatus.Overrun, context.Generate(prompt, greedy with { BudgetMs = -1 }, CancellationToken.None).Status);
            Assert.Equal(ChatGenerationStatus.ContextFull, context.Generate([.. Enumerable.Repeat(104, 62)], greedy, CancellationToken.None).Status);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Equal(ChatGenerationStatus.Cancelled, context.Generate(prompt, greedy, cancelled.Token).Status);
        }
        finally
        {
            GgufBuilder.Delete(path);
        }
    }

    [Fact]
    public void Sampling_honours_greedy_top_k_and_the_repetition_penalty()
    {
        var sampler = new ChatSampler();
        var settings = new ChatGenerationSettings(8, 1000, Temperature: 0f, TopK: 40, TopP: 0.9f, RepetitionPenalty: 1f, Seed: 3);
        float[] logits = [0f, 5f, 1f, 4.9f];
        Assert.Equal(1, sampler.Sample((float[])logits.Clone(), [], settings, new Random(1)));

        // The penalty divides a positive logit: token 1 (5 / 2 = 2.5) falls below token 3 (4.9).
        Assert.Equal(3, sampler.Sample((float[])logits.Clone(), [1], settings with { RepetitionPenalty = 2f }, new Random(1)));

        // Top-k 1 at any temperature is greedy; top-k 2 never draws the third best.
        var hot = settings with { Temperature = 1.5f, TopK = 2, TopP = 1f };
        var random = new Random(9);
        for (int i = 0; i < 200; i++) Assert.Contains(sampler.Sample((float[])logits.Clone(), [], hot, random), new[] { 1, 3 });
        Assert.Equal(1, sampler.Sample((float[])logits.Clone(), [], hot with { TopK = 1 }, random));

        // The same seed draws the same tokens.
        var warm = settings with { Temperature = 1f, TopK = 4, TopP = 1f };
        var r1 = new Random(5);
        var r2 = new Random(5);
        for (int i = 0; i < 20; i++) Assert.Equal(sampler.Sample((float[])logits.Clone(), [], warm, r1), sampler.Sample((float[])logits.Clone(), [], warm, r2));
        Assert.Equal(1, TensorPrimitives.IndexOfMax(logits));
    }
}
