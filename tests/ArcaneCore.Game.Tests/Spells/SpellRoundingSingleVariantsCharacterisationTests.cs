using ArcaneCore.Game.Spells.Rules;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Pins the NextSingle-drawing dither variants (floor without sign handling, clamped unsigned, sign-preserving) so each call site keeps its
/// current rounding of negatives, its clamp and its single draw when the per-site copies are replaced by the shared helpers.
/// </summary>
public sealed class SpellRoundingSingleVariantsCharacterisationTests
{
    private sealed class ScriptedRandom(float value) : Random
    {
        public int DoubleDraws { get; private set; }
        public int SingleDraws { get; private set; }

        public override double NextDouble() { DoubleDraws++; return value; }
        public override float NextSingle() { SingleDraws++; return value; }
    }

    [Theory]
    [InlineData(2.5f, 0.5f, 3)]
    [InlineData(2.5f, 0.25f, 2)]
    [InlineData(-2.5f, 0.25f, -3)]   // floor(-2.25): toward negative infinity, unlike DitherSingle
    [InlineData(-2.5f, 0.75f, -2)]   // floor(-1.75)
    [InlineData(-0.5f, 0.25f, -1)]
    [InlineData(0f, 0.75f, 0)]
    [InlineData(3f, 0.75f, 3)]
    [InlineData(-3f, 0.75f, -3)]
    public void SpellRounding_DitherFloor_FloorsWithoutSignHandlingAndDrawsOneSingle(float value, float draw, int expected)
    {
        var random = new ScriptedRandom(draw);
        Assert.Equal(expected, SpellRounding.DitherFloor(value, random));
        Assert.Equal(1, random.SingleDraws);
        Assert.Equal(0, random.DoubleDraws);
    }

    [Theory]
    [InlineData(2.5f, 0.5f, 3u)]
    [InlineData(2.5f, 0.25f, 2u)]
    [InlineData(-4f, 0.75f, 0u)]
    [InlineData(-0.5f, 0.75f, 0u)]
    [InlineData(0f, 0.75f, 0u)]
    [InlineData(0.5f, 0.25f, 0u)]
    [InlineData(3f, 0.75f, 3u)]
    public void SpellRounding_DitherUnsignedSingle_ClampsNegativeToZeroAndDrawsOneSingle(float value, float draw, uint expected)
    {
        var random = new ScriptedRandom(draw);
        Assert.Equal(expected, SpellRounding.DitherUnsignedSingle(value, random));
        Assert.Equal(1, random.SingleDraws);
        Assert.Equal(0, random.DoubleDraws);
    }

    [Theory]
    [InlineData(2.5f, 0.5f, 3)]
    [InlineData(-2.5f, 0.5f, -3)]
    [InlineData(-2.5f, 0.25f, -2)]   // rounds by magnitude, sign restored
    [InlineData(0f, 0.75f, 0)]
    public void SpellRounding_DitherSingle_RoundsMagnitudeKeepingSignAndDrawsOneSingle(float value, float draw, int expected)
    {
        var random = new ScriptedRandom(draw);
        Assert.Equal(expected, SpellRounding.DitherSingle(value, random));
        Assert.Equal(1, random.SingleDraws);
        Assert.Equal(0, random.DoubleDraws);
    }

    [Fact]
    public void SpellRounding_SingleVariants_SeededStreamsMatchNextSingleDraws()
    {
        float[] values = [1.25f, -7.5f, 0f, 12.75f, -0.125f, 100f, 3.5f];

        var floorActual = new Random(20240607);
        var floorTwin = new Random(20240607);
        foreach (float v in values)
            Assert.Equal((int)MathF.Floor(v + floorTwin.NextSingle()), SpellRounding.DitherFloor(v, floorActual));
        Assert.Equal(floorTwin.NextSingle(), floorActual.NextSingle());

        var unsignedActual = new Random(20240607);
        var unsignedTwin = new Random(20240607);
        foreach (float v in values)
            Assert.Equal((uint)MathF.Floor(Math.Max(v, 0f) + unsignedTwin.NextSingle()), SpellRounding.DitherUnsignedSingle(v, unsignedActual));
        Assert.Equal(unsignedTwin.NextSingle(), unsignedActual.NextSingle());
    }
}
