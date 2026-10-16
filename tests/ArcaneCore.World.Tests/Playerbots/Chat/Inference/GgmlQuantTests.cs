using System.Buffers.Binary;
using System.Runtime.Intrinsics;
using ArcaneCore.World.Playerbots.Chat.Inference;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference;

/// <summary>
/// The block decoders against hand-computed values (ggml-quants.c's block layouts written out by hand here), and the vector decoders
/// against the scalar reference on random blocks.
/// </summary>
public sealed class GgmlQuantTests
{
    private static void Half(byte[] block, int at, float value)
        => BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(at, 2), BitConverter.HalfToUInt16Bits((Half)value));

    private static float[] Both(GgmlType type, byte[] block, int n, out float[] vector)
    {
        var scalar = new float[n];
        GgmlQuant.DequantizeRowScalar(type, block, scalar);
        vector = new float[n];
        if (Vector256.IsHardwareAccelerated) GgmlQuant.DequantizeRowVector(type, block, vector);
        else scalar.CopyTo(vector, 0);
        return scalar;
    }

    [Fact]
    public void Q8_0_is_scale_times_signed_byte()
    {
        // Block: d (f16) = 0.25, q[j] = j - 16  ->  w[j] = (j - 16) / 4.
        var block = new byte[34];
        Half(block, 0, 0.25f);
        for (int j = 0; j < 32; j++) block[2 + j] = unchecked((byte)(sbyte)(j - 16));
        float[] scalar = Both(GgmlType.Q8_0, block, 32, out float[] vector);
        for (int j = 0; j < 32; j++)
        {
            Assert.Equal((j - 16) * 0.25f, scalar[j]);
            Assert.Equal(scalar[j], vector[j]);
        }

        Assert.Equal(-4f, scalar[0]);
        Assert.Equal(3.75f, scalar[31]);
    }

    [Fact]
    public void Q4_0_puts_low_nibbles_first_and_high_nibbles_second()
    {
        // d = 2; byte j = (15 - j) << 4 | j  ->  w[j] = (j - 8) * 2, w[j + 16] = (15 - j - 8) * 2.
        var block = new byte[18];
        Half(block, 0, 2f);
        for (int j = 0; j < 16; j++) block[2 + j] = (byte)(((15 - j) << 4) | j);
        float[] scalar = Both(GgmlType.Q4_0, block, 32, out float[] vector);
        for (int j = 0; j < 16; j++)
        {
            Assert.Equal((j - 8) * 2f, scalar[j]);
            Assert.Equal((7 - j) * 2f, scalar[j + 16]);
        }

        Assert.Equal(scalar, vector);
    }

    [Fact]
    public void Q5_0_takes_the_fifth_bit_of_weight_i_from_bit_i_of_qh()
    {
        // d = 1; nibbles all 0x3 (low and high); qh = 0xA5A5_0F0F: weight i is (3 | bit_i(qh) << 4) - 16.
        var block = new byte[22];
        Half(block, 0, 1f);
        uint qh = 0xA5A50F0F;
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(2, 4), qh);
        for (int j = 0; j < 16; j++) block[6 + j] = 0x33;
        float[] scalar = Both(GgmlType.Q5_0, block, 32, out float[] vector);
        for (int i = 0; i < 32; i++)
        {
            int expected = (3 | ((int)((qh >> i) & 1) << 4)) - 16;
            Assert.Equal(expected, scalar[i]);
        }

        Assert.Equal(-13f, scalar[4]);  // bit 4 of 0x0F0F is 0
        Assert.Equal(3f, scalar[0]);    // bit 0 is 1: 19 - 16
        Assert.Equal(scalar, vector);
    }

    [Fact]
    public void Q4_K_uses_the_packed_six_bit_scales_and_mins()
    {
        // d = 1, dmin = 0.5. Scales: sub-blocks 0..3 take sc = q[j] & 63, m = q[j+4] & 63; 4..7 take the low/high nibbles of
        // q[j+4] plus two high bits from q[j-4] (scale) and q[j] (min).
        int[] sc = [1, 2, 3, 4, 5 + 16, 6, 7, 8 + 32];
        int[] m = [0, 1, 2, 3, 4, 5 + 48, 6, 7];
        var scales = new byte[12];
        for (int j = 0; j < 4; j++)
        {
            scales[j] = (byte)(sc[j] | ((sc[j + 4] >> 4) << 6));
            scales[j + 4] = (byte)(m[j] | ((m[j + 4] >> 4) << 6));
            scales[j + 8] = (byte)((sc[j + 4] & 0x0F) | ((m[j + 4] & 0x0F) << 4));
        }

        for (int j = 0; j < 8; j++) Assert.Equal((sc[j], m[j]), GgmlQuant.ScaleMinK4(j, scales));

        var block = new byte[144];
        Half(block, 0, 1f);
        Half(block, 2, 0.5f);
        scales.CopyTo(block, 4);
        for (int l = 0; l < 128; l++) block[16 + l] = (byte)(((l + 3) % 16 << 4) | (l % 16));
        float[] scalar = Both(GgmlType.Q4_K, block, 256, out float[] vector);
        for (int chunk = 0; chunk < 4; chunk++)
        {
            for (int l = 0; l < 32; l++)
            {
                int q = 32 * chunk + l;
                Assert.Equal((sc[2 * chunk] * (q % 16)) - (0.5f * m[2 * chunk]), scalar[(64 * chunk) + l]);
                Assert.Equal((sc[(2 * chunk) + 1] * ((q + 3) % 16)) - (0.5f * m[(2 * chunk) + 1]), scalar[(64 * chunk) + 32 + l]);
            }
        }

        Assert.Equal(scalar, vector);
    }

    [Fact]
    public void Q6_K_combines_four_low_and_two_high_bits_with_sixteen_weight_scales()
    {
        var block = new byte[210];
        var random = new Random(6);
        random.NextBytes(block.AsSpan(0, 192)); // ql[128], qh[64]
        for (int s = 0; s < 16; s++) block[192 + s] = unchecked((byte)(sbyte)(s - 8));
        Half(block, 208, 0.5f);
        float[] scalar = Both(GgmlType.Q6_K, block, 256, out float[] vector);

        // Weight y of the super-block: half h = y / 128, then within it r = y % 128, quarter k = r / 32, l = r % 32.
        for (int y = 0; y < 256; y++)
        {
            int h = y / 128, r = y % 128, k = r / 32, l = r % 32;
            byte ql = block[(64 * h) + l + (k % 2 == 1 ? 32 : 0)];
            int low = k < 2 ? ql & 0x0F : ql >> 4;
            int high = (block[128 + (32 * h) + l] >> (2 * k)) & 3;
            int q = (low | (high << 4)) - 32;
            int scale = (sbyte)block[192 + (8 * h) + (l / 16) + (2 * k)];
            Assert.Equal(0.5f * scale * q, scalar[y]);
        }

        Assert.Equal(scalar, vector);
    }

    [Fact]
    public void F16_rows_convert_and_vector_matches_scalar_on_random_rows()
    {
        var f16 = new byte[8];
        Half(f16, 0, 1.5f);
        Half(f16, 2, -2f);
        Half(f16, 4, 0.125f);
        Half(f16, 6, 65504f);
        float[] values = Both(GgmlType.F16, f16, 4, out float[] vector);
        Assert.Equal([1.5f, -2f, 0.125f, 65504f], values);
        Assert.Equal(values, vector);

        var random = new Random(42);
        foreach ((GgmlType type, int n) in new[] { (GgmlType.Q8_0, 128), (GgmlType.Q4_0, 96), (GgmlType.Q5_0, 64), (GgmlType.Q4_K, 512), (GgmlType.Q6_K, 512) })
        {
            int blockBytes = GgmlQuant.BlockBytes(type), blockLength = GgmlQuant.BlockLength(type);
            var row = new byte[n / blockLength * blockBytes];
            random.NextBytes(row);
            // Keep the f16 scales finite and small.
            for (int b = 0; b < n / blockLength; b++)
            {
                int at = b * blockBytes;
                switch (type)
                {
                    case GgmlType.Q4_K:
                        Half(row, at, 0.01f * (b + 1));
                        Half(row, at + 2, 0.02f);
                        break;
                    case GgmlType.Q6_K:
                        Half(row, at + 208, 0.03f);
                        break;
                    default:
                        Half(row, at, 0.05f * (b + 1));
                        break;
                }
            }

            float[] scalar = Both(type, row, n, out float[] fast);
            Assert.Equal(scalar, fast);
        }
    }

    [Fact]
    public void Refuses_partial_blocks_and_unsupported_types()
    {
        Assert.Throws<NotSupportedException>(() => GgmlQuant.DequantizeRowScalar(GgmlType.Q8_0, new byte[34], new float[31]));
        Assert.Throws<NotSupportedException>(() => GgmlQuant.DequantizeRowScalar(GgmlType.Q5_K, new byte[176], new float[256]));
        Assert.Throws<ArgumentException>(() => GgmlQuant.DequantizeRowScalar(GgmlType.Q8_0, new byte[33], new float[32]));
    }
}
