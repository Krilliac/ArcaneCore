using ArcaneCore.Game.Stats;
using Xunit;

namespace ArcaneCore.Game.Tests.Stats;

/// <summary>
/// Pins the whole-point uint entry points of <see cref="StatFormulas"/> over every 16-bit stat value against the
/// closed form (first 20 points give 1 each, the rest 10 for health or 15 for mana), the same rule
/// <see cref="StatBonusRangeCharacterisationTests"/> pins for the float forms.
/// </summary>
public sealed class StatBonusPointsWrapperTests
{
    [Fact]
    public void HealthBonusFromStaminaPoints_MatchesClosedFormOverEveryUInt16()
    {
        for (uint stamina = 0; stamina <= ushort.MaxValue; stamina++)
        {
            uint baseStamina = Math.Min(stamina, 20u);
            uint expected = baseStamina + ((stamina - baseStamina) * 10u);
            uint actual = StatFormulas.HealthBonusFromStaminaPoints(stamina);
            Assert.True(expected == actual, $"stamina {stamina}: expected {expected}, got {actual}");
        }
    }

    [Fact]
    public void ManaBonusFromIntellectPoints_MatchesClosedFormOverEveryUInt16()
    {
        for (uint intellect = 0; intellect <= ushort.MaxValue; intellect++)
        {
            uint baseIntellect = Math.Min(intellect, 20u);
            uint expected = baseIntellect + ((intellect - baseIntellect) * 15u);
            uint actual = StatFormulas.ManaBonusFromIntellectPoints(intellect);
            Assert.True(expected == actual, $"intellect {intellect}: expected {expected}, got {actual}");
        }
    }
}
