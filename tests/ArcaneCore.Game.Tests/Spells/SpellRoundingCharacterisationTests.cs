using ArcaneCore.Game.Spells.Procs.Talents;
using ArcaneCore.Game.Spells.Rules;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Pins the random-rounding seams (vmangos rand_dither / rand_ditheru): which Random method each draws, how many draws it takes, the sign and the
/// clamp behaviour, so a shared dither helper can replace the per-site copies without drifting the seeded stream.
/// </summary>
public sealed class SpellRoundingCharacterisationTests
{
    /// <summary>A Random that returns scripted values from NextDouble / NextSingle and records which was called.</summary>
    private sealed class ScriptedRandom(double value) : Random
    {
        public int DoubleDraws { get; private set; }
        public int SingleDraws { get; private set; }

        public override double NextDouble() { DoubleDraws++; return value; }
        public override float NextSingle() { SingleDraws++; return (float)value; }
    }

    [Theory]
    [InlineData(2.5f, 0.5, 3)]
    [InlineData(2.5f, 0.25, 2)]
    [InlineData(-2.5f, 0.5, -3)]
    [InlineData(-2.5f, 0.25, -2)]
    [InlineData(0f, 0.75, 0)]
    [InlineData(3f, 0.0, 3)]
    [InlineData(3f, 0.75, 3)]
    [InlineData(-3f, 0.75, -3)]
    [InlineData(0.5f, 0.5, 1)]
    [InlineData(0.5f, 0.25, 0)]
    public void SpellRounding_Dither_RoundsUpByFractionKeepingSign(float value, double draw, int expected)
    {
        var random = new ScriptedRandom(draw);
        Assert.Equal(expected, SpellRounding.Dither(value, random));
        Assert.Equal(1, random.DoubleDraws);
        Assert.Equal(0, random.SingleDraws);
    }

    [Theory]
    [InlineData(2.5f, 0.5, 3u)]
    [InlineData(2.5f, 0.25, 2u)]
    [InlineData(-4f, 0.75, 0u)]
    [InlineData(-0.5f, 0.75, 0u)]
    [InlineData(0f, 0.75, 0u)]
    public void SpellRounding_DitherUnsigned_ClampsNegativeToZeroAndStillDraws(float value, double draw, uint expected)
    {
        var random = new ScriptedRandom(draw);
        Assert.Equal(expected, SpellRounding.DitherUnsigned(value, random));
        Assert.Equal(1, random.DoubleDraws);
        Assert.Equal(0, random.SingleDraws);
    }

    [Fact]
    public void SpellRounding_Dither_NullRandomThrowsArgumentNull() =>
        Assert.Throws<ArgumentNullException>(() => SpellRounding.Dither(1f, null!));

    [Fact]
    public void SpellRounding_Dither_SeededStreamMatchesNextDoubleDraws()
    {
        var actual = new Random(20240607);
        var twin = new Random(20240607);
        float[] values = [1.25f, -7.5f, 0f, 12.75f, -0.125f, 100f, 3.5f];
        foreach (float v in values)
        {
            int expected = (int)MathF.Floor(MathF.Abs(v) + (float)twin.NextDouble());
            if (v < 0) expected = -expected;
            Assert.Equal(expected, SpellRounding.Dither(v, actual));
        }

        Assert.Equal(twin.NextDouble(), actual.NextDouble());
    }

    [Theory]
    [InlineData(2.5f, 0.5, 3)]
    [InlineData(2.5f, 0.25, 2)]
    [InlineData(-2.5f, 0.5, -3)]
    [InlineData(-2.5f, 0.25, -2)]
    [InlineData(0f, 0.75, 0)]
    [InlineData(3f, 0.75, 3)]
    [InlineData(-3f, 0.75, -3)]
    public void TalentProcSupport_Dither_RoundsUpByFractionKeepingSign(float value, double draw, int expected)
    {
        var random = new ScriptedRandom(draw);
        Assert.Equal(expected, TalentProcSupport.Dither(value, random));
        Assert.Equal(0, random.DoubleDraws);
        Assert.Equal(1, random.SingleDraws);
    }

    [Theory]
    [InlineData(2.5f, 0.5, 3)]
    [InlineData(-4f, 0.75, 0)]
    [InlineData(-0.5f, 0.75, 0)]
    [InlineData(0f, 0.75, 0)]
    public void TalentProcSupport_DitherUnsigned_ClampsNegativeToZeroAndReturnsInt(float value, double draw, int expected)
    {
        var random = new ScriptedRandom(draw);
        int result = TalentProcSupport.DitherUnsigned(value, random);
        Assert.Equal(expected, result);
        Assert.Equal(1, random.SingleDraws);
        Assert.Equal(0, random.DoubleDraws);
    }

    [Fact]
    public void TalentProcSupport_Dither_SeededStreamMatchesNextSingleDraws()
    {
        var actual = new Random(20240607);
        var twin = new Random(20240607);
        float[] values = [1.25f, -7.5f, 0f, 12.75f, -0.125f, 100f, 3.5f];
        foreach (float v in values)
        {
            int expected = (int)MathF.CopySign(MathF.Floor(MathF.Abs(v) + twin.NextSingle()), v);
            Assert.Equal(expected, TalentProcSupport.Dither(v, actual));
        }

        Assert.Equal(twin.NextSingle(), actual.NextSingle());
    }
}
