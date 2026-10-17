// Ported from Krilliac/Sonder-Inference src/sampling/include/sonder/sampling/rng.hpp and src/sampling/src/rng.cpp @1c4c152 (Apache-2.0)

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;

namespace ArcaneCore.World.Playerbots.Chat.Inference.Sampling;

/// <summary>
/// Portable, seedable pseudo-random generator: xoshiro256** seeded through SplitMix64 (public-domain designs by Blackman and Vigna).
/// The output is defined bit for bit, so a fixed seed reproduces the same tokens on every runtime, and the same tokens as the source
/// implementation. A mutable struct: copy it to fork the stream.
/// </summary>
internal struct SamplingRng
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    public SamplingRng(ulong seed) => Reseed(seed);

    public void Reseed(ulong seed)
    {
        ulong x = seed;
        _s0 = SplitMix64(ref x);
        _s1 = SplitMix64(ref x);
        _s2 = SplitMix64(ref x);
        _s3 = SplitMix64(ref x);
    }

    /// <summary>Next raw 64-bit value.</summary>
    public ulong NextU64()
    {
        ulong result = BitOperations.RotateLeft(_s1 * 5UL, 7) * 9UL;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>Uniform double in [0, 1) from the top 53 bits. Exact and portable.</summary>
    public double Uniform() => (NextU64() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>A non-deterministic seed (OS CSPRNG mixed with the high-resolution clock).</summary>
    public static ulong EntropySeed()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes) ^ (ulong)Stopwatch.GetTimestamp();
    }

    private static ulong SplitMix64(ref ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        ulong z = x;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
