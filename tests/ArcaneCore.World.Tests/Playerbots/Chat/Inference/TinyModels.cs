using System.Buffers.Binary;
using ArcaneCore.World.Playerbots.Chat.Inference;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference;

/// <summary>A tiny synthetic decoder (random weights, the test vocabulary) and its weights as plain floats for the reference forward pass.</summary>
internal sealed class TinyModel
{
    public required string Architecture { get; init; }
    public required int Dim { get; init; }
    public required int Heads { get; init; }
    public required int KvHeads { get; init; }
    public required int Ffn { get; init; }
    public required int Layers { get; init; }
    public int HeadDim => Dim / Heads;
    public int Vocabulary { get; init; }
    public float RopeBase { get; init; } = 10000f;
    public float Epsilon { get; init; } = 1e-5f;

    /// <summary>Every tensor as the floats the engine will see (after its quantization round trip).</summary>
    public Dictionary<string, float[]> Weights { get; } = new(StringComparer.Ordinal);

    public GgufBuilder Builder { get; } = new();

    /// <summary>
    /// Build a model: <paramref name="matrixType"/> F32 or Q8_0 (then every row length must be a multiple of 32). The norms are near 1,
    /// the rest uniform in +-<paramref name="scale"/>.
    /// </summary>
    public static TinyModel Create(string arch, int dim, int heads, int kvHeads, int ffn, int layers = 1, GgmlType matrixType = GgmlType.F32, int seed = 7, float scale = 0.4f, bool tied = false, uint context = 64)
    {
        (string[] tokens, int[] types, string[] merges) = BpeTokenizerTests.Vocabulary();
        var model = new TinyModel { Architecture = arch, Dim = dim, Heads = heads, KvHeads = kvHeads, Ffn = ffn, Layers = layers, Vocabulary = tokens.Length };
        var random = new Random(seed);
        GgufBuilder b = model.Builder;
        b.String("general.architecture", arch).String("general.name", "Tiny " + arch).U32("general.file_type", matrixType == GgmlType.Q8_0 ? 7u : 0u)
            .U32($"{arch}.context_length", context).U32($"{arch}.embedding_length", (uint)dim).U32($"{arch}.block_count", (uint)layers)
            .U32($"{arch}.feed_forward_length", (uint)ffn).U32($"{arch}.attention.head_count", (uint)heads)
            .U32($"{arch}.attention.head_count_kv", (uint)kvHeads).F32($"{arch}.rope.freq_base", model.RopeBase)
            .F32($"{arch}.attention.layer_norm_rms_epsilon", model.Epsilon)
            .String("tokenizer.ggml.model", "gpt2").String("tokenizer.ggml.pre", "gpt2")
            .Strings("tokenizer.ggml.tokens", tokens).Ints("tokenizer.ggml.token_type", types).Strings("tokenizer.ggml.merges", merges)
            .U32("tokenizer.ggml.eos_token_id", (uint)(tokens.Length - 1));

        int hd = dim / heads, qDim = heads * hd, kvDim = kvHeads * hd, vocab = tokens.Length;
        model.Matrix("token_embd.weight", vocab, dim, matrixType, random, scale);
        for (int l = 0; l < layers; l++)
        {
            string p = $"blk.{l}.";
            model.Norm(p + "attn_norm.weight", dim, random);
            model.Matrix(p + "attn_q.weight", qDim, dim, matrixType, random, scale);
            model.Matrix(p + "attn_k.weight", kvDim, dim, matrixType, random, scale);
            model.Matrix(p + "attn_v.weight", kvDim, dim, matrixType, random, scale);
            model.Matrix(p + "attn_output.weight", dim, qDim, matrixType, random, scale);
            if (arch == "qwen2")
            {
                model.Vector(p + "attn_q.bias", qDim, random, scale);
                model.Vector(p + "attn_k.bias", kvDim, random, scale);
                model.Vector(p + "attn_v.bias", kvDim, random, scale);
            }

            model.Norm(p + "ffn_norm.weight", dim, random);
            model.Matrix(p + "ffn_gate.weight", ffn, dim, matrixType, random, scale);
            model.Matrix(p + "ffn_up.weight", ffn, dim, matrixType, random, scale);
            model.Matrix(p + "ffn_down.weight", dim, ffn, matrixType, random, scale);
        }

        model.Norm("output_norm.weight", dim, random);
        if (!tied) model.Matrix("output.weight", vocab, dim, matrixType, random, scale);
        else model.Weights["output.weight"] = model.Weights["token_embd.weight"];
        return model;
    }

    private void Norm(string name, int length, Random random)
    {
        float[] values = [.. Enumerable.Range(0, length).Select(_ => 0.8f + (0.4f * (float)random.NextDouble()))];
        Weights[name] = values;
        Builder.TensorF32(name, values, length);
    }

    private void Vector(string name, int length, Random random, float scale)
    {
        float[] values = [.. Enumerable.Range(0, length).Select(_ => scale * ((2f * (float)random.NextDouble()) - 1f))];
        Weights[name] = values;
        Builder.TensorF32(name, values, length);
    }

    private void Matrix(string name, int rows, int cols, GgmlType type, Random random, float scale)
    {
        float[] values = [.. Enumerable.Range(0, rows * cols).Select(_ => scale * ((2f * (float)random.NextDouble()) - 1f))];
        if (type == GgmlType.Q8_0)
        {
            byte[] data = QuantizeQ8(values);
            // The reference sees what the engine sees: the dequantized values.
            var seen = new float[values.Length];
            for (int r = 0; r < rows; r++)
                GgmlQuant.DequantizeRowScalar(GgmlType.Q8_0, data.AsSpan(r * cols / 32 * 34, cols / 32 * 34), seen.AsSpan(r * cols, cols));
            Weights[name] = seen;
            Builder.Tensor(name, GgmlType.Q8_0, data, cols, rows);
            return;
        }

        Weights[name] = values;
        Builder.TensorF32(name, values, cols, rows);
    }

    /// <summary>ggml's <c>quantize_row_q8_0_ref</c>: per 32 weights, d = max|w| / 127 (as f16), q = round(w / d).</summary>
    internal static byte[] QuantizeQ8(float[] values)
    {
        var data = new byte[values.Length / 32 * 34];
        for (int b = 0; b < values.Length / 32; b++)
        {
            float amax = 0;
            for (int j = 0; j < 32; j++) amax = MathF.Max(amax, MathF.Abs(values[(b * 32) + j]));
            float d = amax / 127f;
            float id = d != 0 ? 1f / d : 0f;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(b * 34, 2), BitConverter.HalfToUInt16Bits((Half)d));
            for (int j = 0; j < 32; j++) data[(b * 34) + 2 + j] = unchecked((byte)(sbyte)MathF.Round(values[(b * 32) + j] * id));
        }

        return data;
    }

    /// <summary>
    /// The plain reference: the whole forward pass over <paramref name="tokens"/> in double precision with nested loops, written from
    /// the architecture description (not from the engine), returning the last token's logits.
    /// </summary>
    public double[] ReferenceLogits(IReadOnlyList<int> tokens)
    {
        int hd = HeadDim, qDim = Heads * hd, kvDim = KvHeads * hd, group = Heads / KvHeads;
        bool neox = Architecture == "qwen2";
        var x = new double[tokens.Count][];
        for (int t = 0; t < tokens.Count; t++) x[t] = [.. Row("token_embd.weight", tokens[t], Dim)];
        for (int l = 0; l < Layers; l++)
        {
            string p = $"blk.{l}.";
            var keys = new double[tokens.Count][];
            var values = new double[tokens.Count][];
            var queries = new double[tokens.Count][];
            for (int t = 0; t < tokens.Count; t++)
            {
                double[] xn = RmsNorm(x[t], Weights[p + "attn_norm.weight"]);
                queries[t] = Rope(Add(MatVec(p + "attn_q.weight", qDim, Dim, xn), Bias(p + "attn_q.bias")), Heads, t, neox);
                keys[t] = Rope(Add(MatVec(p + "attn_k.weight", kvDim, Dim, xn), Bias(p + "attn_k.bias")), KvHeads, t, neox);
                values[t] = Add(MatVec(p + "attn_v.weight", kvDim, Dim, xn), Bias(p + "attn_v.bias"));
            }

            for (int t = 0; t < tokens.Count; t++)
            {
                var attended = new double[qDim];
                for (int h = 0; h < Heads; h++)
                {
                    int kv = h / group;
                    var scores = new double[t + 1];
                    for (int s = 0; s <= t; s++)
                    {
                        double dot = 0;
                        for (int i = 0; i < hd; i++) dot += queries[t][(h * hd) + i] * keys[s][(kv * hd) + i];
                        scores[s] = dot / Math.Sqrt(hd);
                    }

                    double max = scores.Max(), sum = 0;
                    for (int s = 0; s <= t; s++) sum += scores[s] = Math.Exp(scores[s] - max);
                    for (int s = 0; s <= t; s++)
                        for (int i = 0; i < hd; i++) attended[(h * hd) + i] += scores[s] / sum * values[s][(kv * hd) + i];
                }

                double[] o = MatVec(p + "attn_output.weight", Dim, qDim, attended);
                for (int i = 0; i < Dim; i++) x[t][i] += o[i];
                double[] xn = RmsNorm(x[t], Weights[p + "ffn_norm.weight"]);
                double[] gate = MatVec(p + "ffn_gate.weight", Ffn, Dim, xn);
                double[] up = MatVec(p + "ffn_up.weight", Ffn, Dim, xn);
                var hidden = new double[Ffn];
                for (int i = 0; i < Ffn; i++) hidden[i] = gate[i] / (1 + Math.Exp(-gate[i])) * up[i];
                double[] down = MatVec(p + "ffn_down.weight", Dim, Ffn, hidden);
                for (int i = 0; i < Dim; i++) x[t][i] += down[i];
            }
        }

        double[] last = RmsNorm(x[^1], Weights["output_norm.weight"]);
        return MatVec("output.weight", Vocabulary, Dim, last);
    }

    private IEnumerable<double> Row(string name, int row, int cols) => Weights[name].Skip(row * cols).Take(cols).Select(v => (double)v);

    private double[]? Bias(string name) => Weights.TryGetValue(name, out float[]? b) ? [.. b.Select(v => (double)v)] : null;

    private static double[] Add(double[] a, double[]? b)
    {
        if (b is null) return a;
        for (int i = 0; i < a.Length; i++) a[i] += b[i];
        return a;
    }

    private double[] MatVec(string name, int rows, int cols, double[] v)
    {
        float[] w = Weights[name];
        var y = new double[rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++) y[r] += w[(r * cols) + c] * v[c];
        return y;
    }

    private double[] RmsNorm(double[] v, float[] weight)
    {
        double mean = v.Sum(a => a * a) / v.Length;
        double scale = 1 / Math.Sqrt(mean + Epsilon);
        return [.. v.Select((a, i) => a * scale * weight[i])];
    }

    /// <summary>RoPE at position <paramref name="pos"/>: pairs (2i, 2i+1) for llama, (i, i + d/2) for qwen2; angle pos * base^(-2i/d).</summary>
    private double[] Rope(double[] v, int heads, int pos, bool neox)
    {
        int hd = HeadDim, half = hd / 2;
        var result = (double[])v.Clone();
        for (int h = 0; h < heads; h++)
        {
            for (int i = 0; i < half; i++)
            {
                double angle = pos * Math.Pow(RopeBase, -2.0 * i / hd);
                int a = (h * hd) + (neox ? i : 2 * i), b = (h * hd) + (neox ? i + half : (2 * i) + 1);
                result[a] = (v[a] * Math.Cos(angle)) - (v[b] * Math.Sin(angle));
                result[b] = (v[a] * Math.Sin(angle)) + (v[b] * Math.Cos(angle));
            }
        }

        return result;
    }
}
