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
}
