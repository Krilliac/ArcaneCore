using System.Numerics.Tensors;

namespace ArcaneCore.World.Playerbots.Chat.Inference;

/// <summary>The transformer's elementwise pieces, on <see cref="TensorPrimitives"/> (SIMD; every call takes a whole vector).</summary>
internal static class TransformerMath
{
    /// <summary>RMSNorm: <c>y = x / sqrt(mean(x^2) + eps) * w</c>.</summary>
    public static void RmsNorm(ReadOnlySpan<float> x, ReadOnlySpan<float> weight, Span<float> destination, float epsilon)
    {
        float mean = TensorPrimitives.SumOfSquares(x) / x.Length;
        float scale = 1f / MathF.Sqrt(mean + epsilon);
        TensorPrimitives.Multiply(x, scale, destination);
        TensorPrimitives.Multiply(destination, weight, destination);
    }

    /// <summary>In-place softmax (max-subtracted, so large logits do not overflow).</summary>
    public static void Softmax(Span<float> values)
    {
        if (values.IsEmpty) return;
        float max = TensorPrimitives.Max(values);
        TensorPrimitives.Subtract(values, max, values);
        TensorPrimitives.Exp(values, values);
        float sum = TensorPrimitives.Sum(values);
        TensorPrimitives.Divide(values, sum, values);
    }

    /// <summary>SwiGLU: <c>gate = silu(gate) * up</c> with <c>silu(g) = g * sigmoid(g)</c>. <paramref name="up"/> is overwritten.</summary>
    public static void SwiGlu(Span<float> gate, Span<float> up)
    {
        TensorPrimitives.Multiply(gate, up, up);   // up = g * u
        TensorPrimitives.Sigmoid(gate, gate);      // gate = sigmoid(g)
        TensorPrimitives.Multiply(gate, up, gate); // gate = sigmoid(g) * g * u
    }

    /// <summary>
    /// The RoPE tables: for position p and pair i, angle = p * base^(-2i/dims). Laid out [position][pair] for <paramref name="positions"/>
    /// positions and dims/2 pairs.
    /// </summary>
    public static (float[] Cos, float[] Sin) RopeTables(int positions, int dims, float @base)
    {
        int pairs = dims / 2;
        var cos = new float[positions * pairs];
        var sin = new float[positions * pairs];
        for (int i = 0; i < pairs; i++)
        {
            double frequency = Math.Pow(@base, -2.0 * i / dims);
            for (int p = 0; p < positions; p++)
            {
                double angle = p * frequency;
                cos[(p * pairs) + i] = (float)Math.Cos(angle);
                sin[(p * pairs) + i] = (float)Math.Sin(angle);
            }
        }

        return (cos, sin);
    }

    /// <summary>
    /// Rotate one head's first <paramref name="dims"/> values by position (<paramref name="cos"/>/<paramref name="sin"/>: that
    /// position's dims/2 table entries). Llama files pair neighbours (2i, 2i+1) (llama.cpp <c>LLAMA_ROPE_TYPE_NORM</c>: the converter
    /// permuted Q and K for it); qwen2 pairs halves (i, i+dims/2) (<c>NEOX</c>). <paramref name="scratch"/> holds at least dims floats.
    /// </summary>
    public static void Rope(Span<float> head, int dims, ReadOnlySpan<float> cos, ReadOnlySpan<float> sin, bool neox, Span<float> scratch)
    {
        int pairs = dims / 2;
        if (neox)
        {
            Span<float> a = head[..pairs];
            Span<float> b = head.Slice(pairs, pairs);
            Span<float> oldA = scratch[..pairs];
            Span<float> product = scratch.Slice(pairs, pairs);
            a.CopyTo(oldA);
            TensorPrimitives.Multiply(a, cos, a);            // a = a cos
            TensorPrimitives.Multiply(b, sin, product);      // product = b sin
            TensorPrimitives.Subtract(a, product, a);        // a' = a cos - b sin
            TensorPrimitives.Multiply(b, cos, b);            // b = b cos
            TensorPrimitives.MultiplyAdd(oldA, sin, b, b);   // b' = b cos + a sin
            return;
        }

        for (int i = 0; i < pairs; i++)
        {
            float x0 = head[2 * i], x1 = head[(2 * i) + 1];
            head[2 * i] = (x0 * cos[i]) - (x1 * sin[i]);
            head[(2 * i) + 1] = (x0 * sin[i]) + (x1 * cos[i]);
        }
    }
}
