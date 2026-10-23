namespace ArcaneCore.Game.Spells.Rules;

/// <summary>Random rounding of fractional amounts (vmangos rand_dither, shared/Utilities/Random.cpp:80-84).</summary>
public static class SpellRounding
{
    /// <summary>
    /// A float as an integer rounded up with the probability of its fractional part and down otherwise, sign
    /// preserved: <c>copysign(floor(|v| + rand[0,1)), v)</c>.
    /// </summary>
    public static int Dither(float value, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        int rounded = (int)MathF.Floor(MathF.Abs(value) + (float)random.NextDouble());
        return value < 0 ? -rounded : rounded;
    }

    /// <summary>Dithered, negative values clamped to 0 (vmangos rand_ditheru).</summary>
    public static uint DitherUnsigned(float value, Random random) => (uint)Dither(Math.Max(value, 0f), random);

    /// <summary>
    /// <see cref="Dither"/> drawing <see cref="Random.NextSingle"/> (the stream the proc and script sites consume), sign preserved:
    /// <c>copysign(floor(|v| + rand[0,1)), v)</c>. Does not null-check <paramref name="random"/>.
    /// </summary>
    public static int DitherSingle(float value, Random random) => (int)MathF.CopySign(MathF.Floor(MathF.Abs(value) + random.NextSingle()), value);

    /// <summary>
    /// <c>floor(v + rand[0,1))</c> with one <see cref="Random.NextSingle"/> draw and no sign handling: a negative value rounds toward
    /// negative infinity, unlike <see cref="DitherSingle"/>. Does not null-check <paramref name="random"/>.
    /// </summary>
    public static int DitherFloor(float value, Random random) => (int)MathF.Floor(value + random.NextSingle());

    /// <summary>
    /// <c>floor(max(v, 0) + rand[0,1))</c> as a <see cref="uint"/> with one <see cref="Random.NextSingle"/> draw (vmangos rand_ditheru).
    /// Does not null-check <paramref name="random"/>.
    /// </summary>
    public static uint DitherUnsignedSingle(float value, Random random) => (uint)MathF.Floor(Math.Max(value, 0f) + random.NextSingle());
}
