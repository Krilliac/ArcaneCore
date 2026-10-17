using System.Buffers.Binary;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ArcaneCore.World.Playerbots.Chat.Inference;

/// <summary>
/// The GGML block formats the in-process chat engine reads (ggml-quants.c): F32, F16, Q4_0, Q5_0, Q8_0, Q4_K and Q6_K. A row is
/// dequantized on the fly into a caller's float buffer (one row at a time, never the whole tensor) and then dotted with
/// <see cref="TensorPrimitives"/>. The block decoders use <see cref="Vector256"/> (32 weights per step: the size of a Q8_0, Q4_0
/// and Q5_0 block and of a Q4_K/Q6_K sub-block run) where the hardware has it; the scalar decoders are the reference the vector
/// ones are tested against, and run where it does not.
/// </summary>
internal static class GgmlQuant
{
    public const int QK = 32;     // weights per Q4_0 / Q5_0 / Q8_0 block
    public const int QKK = 256;   // weights per K-quant super-block

    /// <summary>Weights per block of <paramref name="type"/> (0 when unsupported).</summary>
    public static int BlockLength(GgmlType type) => type switch
    {
        GgmlType.F32 or GgmlType.F16 => 1,
        GgmlType.Q4_0 or GgmlType.Q5_0 or GgmlType.Q8_0 => QK,
        GgmlType.Q4_K or GgmlType.Q6_K => QKK,
        _ => 0,
    };

    /// <summary>Bytes per block of <paramref name="type"/> (0 when unsupported).</summary>
    public static int BlockBytes(GgmlType type) => type switch
    {
        GgmlType.F32 => 4,
        GgmlType.F16 => 2,
        GgmlType.Q4_0 => 2 + 16,
        GgmlType.Q5_0 => 2 + 4 + 16,
        GgmlType.Q8_0 => 2 + 32,
        GgmlType.Q4_K => 2 + 2 + 12 + 128,
        GgmlType.Q6_K => 128 + 64 + 16 + 2,
        _ => 0,
    };

    public static bool IsSupported(GgmlType type) => BlockLength(type) > 0;

    /// <summary>Bytes of a row of <paramref name="length"/> weights, or -1 when the type is unsupported or the length is not whole blocks.</summary>
    public static long RowBytes(GgmlType type, long length)
    {
        int block = BlockLength(type);
        if (block == 0 || length % block != 0) return -1;
        return length / block * BlockBytes(type);
    }

    /// <summary>The supported types, for error messages.</summary>
    public const string SupportedList = "F32, F16, Q4_0, Q5_0, Q8_0, Q4_K, Q6_K";

    /// <summary>Dequantize one row (<paramref name="destination"/>.Length weights) of <paramref name="type"/> from <paramref name="source"/>.</summary>
    public static void DequantizeRow(GgmlType type, ReadOnlySpan<byte> source, Span<float> destination)
    {
        if (Vector256.IsHardwareAccelerated) DequantizeRowVector(type, source, destination);
        else DequantizeRowScalar(type, source, destination);
    }

    // --- vector decoders ----------------------------------------------------------------------------------------------------

    internal static void DequantizeRowVector(GgmlType type, ReadOnlySpan<byte> source, Span<float> destination)
    {
        int n = destination.Length;
        CheckLengths(type, source, n);
        switch (type)
        {
            case GgmlType.F32:
                MemoryMarshal.Cast<byte, float>(source)[..n].CopyTo(destination);
                return;
            case GgmlType.F16:
                TensorPrimitives.ConvertToSingle(MemoryMarshal.Cast<byte, Half>(source)[..n], destination);
                return;
            case GgmlType.Q8_0:
                for (int b = 0, at = 0; b < n; b += QK, at += 34)
                {
                    Vector256<sbyte> q = Vector256.LoadUnsafe(ref Unsafe.As<byte, sbyte>(ref MemoryMarshal.GetReference(source)), (nuint)(at + 2));
                    Store32(q, F16(source, at), F16(source, at), 0f, destination.Slice(b, QK));
                }

                return;
            case GgmlType.Q4_0:
                for (int b = 0, at = 0; b < n; b += QK, at += 18)
                {
                    Vector128<byte> raw = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(source), (nuint)(at + 2));
                    Vector256<byte> nibbles = Vector256.Create(raw & Vector128.Create((byte)0x0F), Vector128.ShiftRightLogical(raw, 4));
                    float d = F16(source, at);
                    Store32(nibbles.AsSByte() - Vector256.Create((sbyte)8), d, d, 0f, destination.Slice(b, QK));
                }

                return;
            case GgmlType.Q5_0:
                for (int b = 0, at = 0; b < n; b += QK, at += 22)
                {
                    uint qh = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(at + 2, 4));
                    Vector128<byte> raw = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(source), (nuint)(at + 6));
                    Vector256<byte> nibbles = Vector256.Create(raw & Vector128.Create((byte)0x0F), Vector128.ShiftRightLogical(raw, 4));
                    Vector256<byte> high = HighBits(qh) & Vector256.Create((byte)0x10);
                    float d = F16(source, at);
                    Store32((nibbles | high).AsSByte() - Vector256.Create((sbyte)16), d, d, 0f, destination.Slice(b, QK));
                }

                return;
            case GgmlType.Q4_K:
                for (int b = 0, at = 0; b < n; b += QKK, at += 144)
                {
                    float d = F16(source, at), dmin = F16(source, at + 2);
                    ReadOnlySpan<byte> scales = source.Slice(at + 4, 12);
                    for (int j = 0; j < 4; j++)
                    {
                        Vector256<byte> raw = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(source), (nuint)(at + 16 + (32 * j)));
                        (int sc1, int m1) = ScaleMinK4(2 * j, scales);
                        (int sc2, int m2) = ScaleMinK4((2 * j) + 1, scales);
                        Store32((raw & Vector256.Create((byte)0x0F)).AsSByte(), d * sc1, d * sc1, dmin * m1, destination.Slice(b + (64 * j), 32));
                        Store32(Vector256.ShiftRightLogical(raw, 4).AsSByte(), d * sc2, d * sc2, dmin * m2, destination.Slice(b + (64 * j) + 32, 32));
                    }
                }

                return;
            case GgmlType.Q6_K:
                for (int b = 0, at = 0; b < n; b += QKK, at += 210)
                {
                    float d = F16(source, at + 208);
                    ReadOnlySpan<sbyte> sc = MemoryMarshal.Cast<byte, sbyte>(source.Slice(at + 192, 16));
                    for (int half = 0; half < 2; half++)
                    {
                        int ql = at + (64 * half), qh = at + 128 + (32 * half), s = 8 * half, y = b + (128 * half);
                        Vector256<byte> ql0 = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(source), (nuint)ql);
                        Vector256<byte> ql1 = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(source), (nuint)(ql + 32));
                        Vector256<byte> h = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(source), (nuint)qh);
                        Vector256<byte> low4 = Vector256.Create((byte)0x0F), two = Vector256.Create((byte)3);
                        Vector256<sbyte> bias = Vector256.Create((sbyte)32);
                        Vector256<byte> q1 = (ql0 & low4) | Vector256.ShiftLeft(h & two, 4);
                        Vector256<byte> q2 = (ql1 & low4) | Vector256.ShiftLeft(Vector256.ShiftRightLogical(h, 2) & two, 4);
                        Vector256<byte> q3 = Vector256.ShiftRightLogical(ql0, 4) | Vector256.ShiftLeft(Vector256.ShiftRightLogical(h, 4) & two, 4);
                        Vector256<byte> q4 = Vector256.ShiftRightLogical(ql1, 4) | Vector256.ShiftLeft(Vector256.ShiftRightLogical(h, 6) & two, 4);
                        Store32(q1.AsSByte() - bias, d * sc[s + 0], d * sc[s + 1], 0f, destination.Slice(y, 32));
                        Store32(q2.AsSByte() - bias, d * sc[s + 2], d * sc[s + 3], 0f, destination.Slice(y + 32, 32));
                        Store32(q3.AsSByte() - bias, d * sc[s + 4], d * sc[s + 5], 0f, destination.Slice(y + 64, 32));
                        Store32(q4.AsSByte() - bias, d * sc[s + 6], d * sc[s + 7], 0f, destination.Slice(y + 96, 32));
                    }
                }

                return;
            default:
                throw new NotSupportedException($"tensor type {type} is not supported ({SupportedList})");
        }
    }

    /// <summary>
    /// 32 signed weights to floats: lanes 0..15 times <paramref name="scaleLow"/>, lanes 16..31 times <paramref name="scaleHigh"/>
    /// (Q6_K's sub-blocks are 16 weights; every other format passes one scale twice), minus <paramref name="min"/> (Q4_K).
    /// </summary>
    private static void Store32(Vector256<sbyte> q, float scaleLow, float scaleHigh, float min, Span<float> destination)
    {
        if (destination.Length < 32) throw new ArgumentException("a block needs 32 floats", nameof(destination));
        (Vector256<short> low, Vector256<short> high) = Vector256.Widen(q);
        (Vector256<int> a, Vector256<int> b) = Vector256.Widen(low);
        (Vector256<int> c, Vector256<int> d) = Vector256.Widen(high);
        Vector256<float> s0 = Vector256.Create(scaleLow), s1 = Vector256.Create(scaleHigh), m = Vector256.Create(min);
        ref float target = ref MemoryMarshal.GetReference(destination);
        ((Vector256.ConvertToSingle(a) * s0) - m).StoreUnsafe(ref target, 0);
        ((Vector256.ConvertToSingle(b) * s0) - m).StoreUnsafe(ref target, 8);
        ((Vector256.ConvertToSingle(c) * s1) - m).StoreUnsafe(ref target, 16);
        ((Vector256.ConvertToSingle(d) * s1) - m).StoreUnsafe(ref target, 24);
    }

    /// <summary>Byte lane i is 0xFF when bit i of <paramref name="bits"/> is set (Q5_0's fifth bits).</summary>
    private static Vector256<byte> HighBits(uint bits)
    {
        const ulong spread = 0x0101010101010101UL;
        Vector256<byte> replicated = Vector256.Create(
            (bits & 0xFF) * spread, ((bits >> 8) & 0xFF) * spread, ((bits >> 16) & 0xFF) * spread, (bits >> 24) * spread).AsByte();
        Vector256<byte> mask = Vector256.Create(0x8040201008040201UL).AsByte();
        return Vector256.Equals(replicated & mask, mask);
    }

    // --- scalar reference ---------------------------------------------------------------------------------------------------

    /// <summary>The plain decoders (ggml-quants.c <c>dequantize_row_*</c>, line for line): the fallback and the tests' reference.</summary>
    internal static void DequantizeRowScalar(GgmlType type, ReadOnlySpan<byte> source, Span<float> destination)
    {
        int n = destination.Length;
        CheckLengths(type, source, n);
        switch (type)
        {
            case GgmlType.F32:
                MemoryMarshal.Cast<byte, float>(source)[..n].CopyTo(destination);
                return;
            case GgmlType.F16:
                for (int i = 0; i < n; i++) destination[i] = F16(source, 2 * i);
                return;
            case GgmlType.Q8_0:
                for (int b = 0, at = 0; b < n; b += QK, at += 34)
                {
                    float d = F16(source, at);
                    for (int j = 0; j < QK; j++) destination[b + j] = (sbyte)source[at + 2 + j] * d;
                }

                return;
            case GgmlType.Q4_0:
                for (int b = 0, at = 0; b < n; b += QK, at += 18)
                {
                    float d = F16(source, at);
                    for (int j = 0; j < 16; j++)
                    {
                        byte q = source[at + 2 + j];
                        destination[b + j] = ((q & 0x0F) - 8) * d;
                        destination[b + j + 16] = ((q >> 4) - 8) * d;
                    }
                }

                return;
            case GgmlType.Q5_0:
                for (int b = 0, at = 0; b < n; b += QK, at += 22)
                {
                    float d = F16(source, at);
                    uint qh = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(at + 2, 4));
                    for (int j = 0; j < 16; j++)
                    {
                        byte q = source[at + 6 + j];
                        int xh0 = (int)((qh >> j) << 4) & 0x10;
                        int xh1 = (int)(qh >> (j + 12)) & 0x10;
                        destination[b + j] = (((q & 0x0F) | xh0) - 16) * d;
                        destination[b + j + 16] = (((q >> 4) | xh1) - 16) * d;
                    }
                }

                return;
            case GgmlType.Q4_K:
                for (int b = 0, at = 0; b < n; b += QKK, at += 144)
                {
                    float d = F16(source, at), dmin = F16(source, at + 2);
                    ReadOnlySpan<byte> scales = source.Slice(at + 4, 12);
                    ReadOnlySpan<byte> q = source.Slice(at + 16, 128);
                    int y = b, qi = 0;
                    for (int j = 0, sub = 0; j < QKK; j += 64, sub += 2)
                    {
                        (int sc1, int m1) = ScaleMinK4(sub, scales);
                        (int sc2, int m2) = ScaleMinK4(sub + 1, scales);
                        float d1 = d * sc1, min1 = dmin * m1, d2 = d * sc2, min2 = dmin * m2;
                        for (int l = 0; l < 32; l++) destination[y + l] = (d1 * (q[qi + l] & 0x0F)) - min1;
                        for (int l = 0; l < 32; l++) destination[y + 32 + l] = (d2 * (q[qi + l] >> 4)) - min2;
                        qi += 32;
                        y += 64;
                    }
                }

                return;
            case GgmlType.Q6_K:
                for (int b = 0, at = 0; b < n; b += QKK, at += 210)
                {
                    float d = F16(source, at + 208);
                    int ql = at, qh = at + 128, sc = at + 192, y = b;
                    for (int half = 0; half < 2; half++)
                    {
                        for (int l = 0; l < 32; l++)
                        {
                            int s = l / 16;
                            int q1 = (sbyte)((source[ql + l] & 0x0F) | (((source[qh + l] >> 0) & 3) << 4)) - 32;
                            int q2 = (sbyte)((source[ql + l + 32] & 0x0F) | (((source[qh + l] >> 2) & 3) << 4)) - 32;
                            int q3 = (sbyte)((source[ql + l] >> 4) | (((source[qh + l] >> 4) & 3) << 4)) - 32;
                            int q4 = (sbyte)((source[ql + l + 32] >> 4) | (((source[qh + l] >> 6) & 3) << 4)) - 32;
                            destination[y + l] = d * (sbyte)source[sc + s + 0] * q1;
                            destination[y + l + 32] = d * (sbyte)source[sc + s + 2] * q2;
                            destination[y + l + 64] = d * (sbyte)source[sc + s + 4] * q3;
                            destination[y + l + 96] = d * (sbyte)source[sc + s + 6] * q4;
                        }

                        y += 128;
                        ql += 64;
                        qh += 32;
                        sc += 8;
                    }
                }

                return;
            default:
                throw new NotSupportedException($"tensor type {type} is not supported ({SupportedList})");
        }
    }

    /// <summary>Q4_K's packed 6-bit scale and min of sub-block <paramref name="j"/> (ggml-quants.c <c>get_scale_min_k4</c>).</summary>
    internal static (int Scale, int Min) ScaleMinK4(int j, ReadOnlySpan<byte> q)
        => j < 4
            ? (q[j] & 63, q[j + 4] & 63)
            : ((q[j + 4] & 0x0F) | ((q[j - 4] >> 6) << 4), (q[j + 4] >> 4) | ((q[j] >> 6) << 4));

    internal static float F16(ReadOnlySpan<byte> source, int at) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(at, 2)));

    private static void CheckLengths(GgmlType type, ReadOnlySpan<byte> source, int n)
    {
        long bytes = RowBytes(type, n);
        if (bytes < 0) throw new NotSupportedException($"a row of {n} {type} weights is not whole blocks of a supported type ({SupportedList})");
        if (source.Length < bytes) throw new ArgumentException($"a row of {n} {type} weights needs {bytes} bytes, not {source.Length}", nameof(source));
    }
}
