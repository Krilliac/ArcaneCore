// Ported from Krilliac/Sonder-Inference src/sampling/tests/test_rng.cpp @1c4c152 (Apache-2.0)

using ArcaneCore.World.Playerbots.Chat.Inference.Sampling;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference.Sampling;

/// <summary>The portable xoshiro256** / SplitMix64 generator: determinism, reseeding, range, and the source's known-answer vector.</summary>
public sealed class SamplingRngTests
{
    [Fact]
    public void SameSeedYieldsIdenticalSequence()
    {
        var a = new SamplingRng(42);
        var b = new SamplingRng(42);
        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(a.NextU64(), b.NextU64());
        }
    }

    [Fact]
    public void DifferentSeedsDiverge()
    {
        var a = new SamplingRng(1);
        var b = new SamplingRng(2);
        int equal = 0;
        for (int i = 0; i < 100; i++)
        {
            equal += a.NextU64() == b.NextU64() ? 1 : 0;
        }

        Assert.Equal(0, equal);
    }

    [Fact]
    public void ReseedRestartsTheSequence()
    {
        var a = new SamplingRng(7);
        var first = new ulong[10];
        for (int i = 0; i < first.Length; i++)
        {
            first[i] = a.NextU64();
        }

        a.Reseed(7);
        for (int i = 0; i < first.Length; i++)
        {
            Assert.Equal(first[i], a.NextU64());
        }
    }

    [Fact]
    public void UniformIsInTheUnitIntervalWithSaneMean()
    {
        var r = new SamplingRng(123);
        double sum = 0.0;
        const int n = 100000;
        for (int i = 0; i < n; i++)
        {
            double u = r.Uniform();
            Assert.True(u >= 0.0 && u < 1.0);
            sum += u;
        }

        Assert.True(Math.Abs((sum / n) - 0.5) <= 0.01);
    }

    [Fact]
    public void KnownAnswerVectorPinsCrossPlatformOutput()
    {
        // The source's reference values (computed independently in Python) for SplitMix64-seeded xoshiro256** with seed 0.
        var r = new SamplingRng(0);
        Assert.Equal(0x99EC5F36CB75F2B4UL, r.NextU64());
        Assert.Equal(0xBF6E1F784956452AUL, r.NextU64());
        Assert.Equal(0x1A5F849D4933E6E0UL, r.NextU64());
    }

    [Fact]
    public void CopyingTheStructForksTheStream()
    {
        var a = new SamplingRng(9);
        a.NextU64();
        SamplingRng b = a;
        Assert.Equal(a.NextU64(), b.NextU64());
    }

    [Fact]
    public void EntropySeedProducesVaryingSeeds()
    {
        ulong a = SamplingRng.EntropySeed();
        ulong b = SamplingRng.EntropySeed();
        ulong c = SamplingRng.EntropySeed();
        Assert.False(a == b && b == c);
    }
}
