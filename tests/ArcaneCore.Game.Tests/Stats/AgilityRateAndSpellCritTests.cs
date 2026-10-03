using ArcaneCore.Game.Stats;
using Xunit;

namespace ArcaneCore.Game.Tests.Stats;

/// <summary>
/// vmangos ObjectMgr.cpp:5072-5148 (rate table loader, linear gap interpolation), :5332-5358 (lookup)
/// and Unit.cpp:2597-2641 (spell crit from intellect).
/// </summary>
public sealed class AgilityRateAndSpellCritTests
{
    private const float Tol = 0.001f;

    private static AgilityRateTable Table(params (int Level, float Rate)[] entries)
        => AgilityRateTable.FromEntries(entries.Select(e => new KeyValuePair<int, float>(e.Level, e.Rate)));

    [Fact]
    public void Table_InterpolatesGapsLinearlyBetweenDefinedLevels()
    {
        AgilityRateTable t = Table((1, 5f), (20, 8.1367f), (49, 16.0514f), (60, 25f));
        Assert.Equal(5f, t.Get(1), Tol);
        Assert.Equal(8.1367f, t.Get(20), Tol);
        Assert.Equal(10.8659f, t.Get(30), Tol);        // 8.1367 + 10/29 * (16.0514 - 8.1367)
        Assert.Equal(16.0514f, t.Get(49), Tol);
        Assert.Equal(25f, t.Get(60), Tol);
        Assert.Equal(5f + (9f / 19f * (8.1367f - 5f)), t.Get(10), Tol);
    }

    [Fact]
    public void Table_LevelsAboveTheEndUseTheLastRateAndEntriesPastSixtyExtendTheTable()
    {
        AgilityRateTable sixty = Table((1, 4f), (60, 20f));
        Assert.Equal(60, sixty.Length);
        Assert.Equal(20f, sixty.Get(61), Tol);
        Assert.Equal(20f, sixty.Get(255), Tol);

        AgilityRateTable seventy = Table((1, 4f), (60, 20f), (70, 30f));
        Assert.Equal(70, seventy.Length);
        Assert.Equal(25f, seventy.Get(65), Tol);
        Assert.Equal(30f, seventy.Get(80), Tol);
    }

    [Fact]
    public void Table_LevelZeroUsesLevelOne()
        => Assert.Equal(4f, Table((1, 4f), (60, 20f)).Get(0), Tol);

    [Fact]
    public void Table_MissingFirstOrLastLevelIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Table((2, 4f), (60, 20f)));
        Assert.Throws<ArgumentException>(() => Table((1, 4f), (50, 20f)));
    }

    [Fact]
    public void Table_RejectsNonPositiveRatesAndLevelZeroEntries()
    {
        Assert.Throws<ArgumentException>(() => Table((1, 4f), (30, 0f), (60, 20f)));
        Assert.Throws<ArgumentException>(() => Table((1, 4f), (30, -2f), (60, 20f)));
        Assert.Throws<ArgumentException>(() => Table((0, 3f), (1, 4f), (60, 20f)));
    }

    [Fact]
    public void Table_FromSparseTreatsZeroAsUndefinedLikeTheLoader()
    {
        float[] sparse = new float[60];
        sparse[0] = 2f;
        sparse[59] = 61f;
        AgilityRateTable t = AgilityRateTable.FromSparse(sparse);
        Assert.Equal(32f, t.Get(31), Tol);                                 // 2 + 30/59 * 59
        Assert.Throws<ArgumentException>(() => AgilityRateTable.FromSparse(new float[60]));
        Assert.Throws<ArgumentException>(() => AgilityRateTable.FromSparse(new float[10]));
    }

    [Fact]
    public void Rates_DivideAgilityByTheClassLevelRate()
    {
        var crit = new Dictionary<Class, AgilityRateTable> { [Class.Rogue] = Table((1, 4f), (60, 40f)) };
        var dodge = new Dictionary<Class, AgilityRateTable> { [Class.Rogue] = Table((1, 2f), (60, 20f)) };
        var rates = new AgilityRates(crit, dodge);
        Assert.Equal(4f, rates.CritPerAgility(Class.Rogue, 1), Tol);
        Assert.Equal(40f, rates.CritPerAgility(Class.Rogue, 60), Tol);
        Assert.Equal(5f, rates.MeleeCritFromAgility(Class.Rogue, 60, 200f), Tol);
        Assert.Equal(10f, rates.DodgeFromAgility(Class.Rogue, 60, 200f), Tol);
        Assert.Equal(15f, rates.MeleeCritFromAgility(Class.Rogue, 1, 60f), Tol);
    }

    [Fact]
    public void Rates_AClassWithoutATableUsesRateOneLikeVmangosUndefinedClasses()
    {
        var rates = new AgilityRates(new Dictionary<Class, AgilityRateTable>(), new Dictionary<Class, AgilityRateTable>());
        Assert.Equal(1f, rates.CritPerAgility(Class.Mage, 30), Tol);
        Assert.Equal(1f, rates.DodgePerAgility(Class.Mage, 30), Tol);
        Assert.Equal(40f, rates.MeleeCritFromAgility(Class.Mage, 30, 40f), Tol);
    }

    [Theory]
    [InlineData(Class.Mage, 60u, 100f, 5.5597f)]        // 3.70 + 100 / (14.77 + 0.65 * 60)
    [InlineData(Class.Paladin, 1u, 20f, 4.9970f)]       // 3.70 + 20 / (14.77 + 0.65)
    [InlineData(Class.Priest, 60u, 0f, 2.97f)]
    [InlineData(Class.Shaman, 40u, 80f, 5.3786f)]       // 3.54 + 80 / (11.51 + 0.80 * 40)
    [InlineData(Class.Warlock, 20u, 60f, 5.3461f)]      // 3.18 + 60 / (11.30 + 0.82 * 20)
    [InlineData(Class.Druid, 30u, 50f, 4.7146f)]        // 3.33 + 50 / (12.41 + 0.79 * 30)
    [InlineData(Class.Warrior, 60u, 100f, 0.1667f)]     // 0 + 100 / (0 + 10 * 60)
    [InlineData(Class.Hunter, 30u, 30f, 0.1f)]
    [InlineData(Class.Rogue, 30u, 60f, 0.2f)]
    public void SpellCrit_IsBasePlusIntellectOverTheLevelRatio(Class cls, uint level, float intellect, float expected)
        => Assert.Equal(expected, SpellCritTable.CritFromIntellect(cls, level, intellect), 0.001f);

    [Fact]
    public void SpellCrit_UnknownClassesGetNone()
        => Assert.Equal(0f, SpellCritTable.CritFromIntellect((Class)6, 60, 100f), Tol);

    [Fact]
    public void SpellCrit_IsFlaggedUnverifiedBecauseVmangosAndCmangosBothSayItMustBeChecked()
    {
        Assert.False(SpellCritTable.VerifiedRetail);
        Assert.Equal(5f, SpellCritTable.NonPlayerSpellCrit, Tol);
    }
}
