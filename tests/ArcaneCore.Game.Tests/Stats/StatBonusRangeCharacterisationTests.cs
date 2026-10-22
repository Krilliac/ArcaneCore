using ArcaneCore.Game.Stats;
using Xunit;

namespace ArcaneCore.Game.Tests.Stats;

/// <summary>
/// Pins Player::GetHealthBonusFromStamina / GetManaBonusFromIntellect (StatSystem.cpp:149-163) over the whole
/// 16-bit stat range as exact integers (the largest result, 982745, is below 2^24 so the float is exact).
/// The values are the closed form of the rule: the first 20 points give 1 each, the rest 10 (health) or 15 (mana).
/// A uint wrapper over <see cref="StatFormulas"/> must reproduce these exactly.
/// </summary>
public sealed class StatBonusRangeCharacterisationTests
{
    [Fact]
    public void HealthBonusFromStamina_MatchesClosedFormOverEveryUInt16()
    {
        for (uint stamina = 0; stamina <= ushort.MaxValue; stamina++)
        {
            uint baseStamina = Math.Min(stamina, 20u);
            uint expected = baseStamina + ((stamina - baseStamina) * 10u);
            float actual = StatFormulas.HealthBonusFromStamina(stamina);
            Assert.True(expected == (uint)actual && expected == actual, $"stamina {stamina}: expected {expected}, got {actual}");
        }
    }

    [Fact]
    public void ManaBonusFromIntellect_MatchesClosedFormOverEveryUInt16()
    {
        for (uint intellect = 0; intellect <= ushort.MaxValue; intellect++)
        {
            uint baseIntellect = Math.Min(intellect, 20u);
            uint expected = baseIntellect + ((intellect - baseIntellect) * 15u);
            float actual = StatFormulas.ManaBonusFromIntellect(intellect);
            Assert.True(expected == (uint)actual && expected == actual, $"intellect {intellect}: expected {expected}, got {actual}");
        }
    }

    [Theory]
    [InlineData(19u, 19u, 19u)]
    [InlineData(20u, 20u, 20u)]
    [InlineData(21u, 30u, 35u)]
    [InlineData(65535u, 655_170u, 982_745u)]
    public void Boundaries_AroundTheTwentyPointKnee(uint stat, uint health, uint mana)
    {
        Assert.Equal(health, (uint)StatFormulas.HealthBonusFromStamina(stat));
        Assert.Equal(mana, (uint)StatFormulas.ManaBonusFromIntellect(stat));
    }
}
