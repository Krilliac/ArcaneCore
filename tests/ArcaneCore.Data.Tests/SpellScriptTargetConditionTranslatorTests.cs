using ArcaneCore.Data.Npc;
using ArcaneCore.Data.World.Creatures;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The vmangos <c>conditions</c> shapes that <c>spell_script_target.conditionId</c> uses (vmangos Conditions.h:101 HEALTH_PERCENT 41, :195
/// SOURCE_ENTRY 16, :217 OBJECT_IS_SPAWNED 48, :219 OBJECT_LOOT_STATE 49, :248 OBJECT_GO_STATE 55, CONDITION_AND -1), translated to the
/// term list the game reads. Written for the integration gate; not run in the lane.
/// </summary>
public sealed class SpellScriptTargetConditionTranslatorTests
{
    private static ConditionRow Row(uint entry, int type, uint v1 = 0, uint v2 = 0, uint v3 = 0, uint v4 = 0, byte flags = 0)
        => new() { ConditionEntry = entry, Type = type, Value1 = v1, Value2 = v2, Value3 = v3, Value4 = v4, Flags = flags };

    private static (bool Ok, string Expression, string Failure) Translate(uint id, params ConditionRow[] rows)
    {
        Dictionary<uint, ConditionRow> byId = rows.ToDictionary(r => r.ConditionEntry);
        bool ok = SpellScriptTargetConditionTranslator.TryTranslate(id, byId.GetValueOrDefault, out string expression, out string failure);
        return (ok, expression, failure);
    }

    [Fact]
    public void NoCondition_IsEmpty()
    {
        Assert.Equal((true, string.Empty, string.Empty), Translate(0));
    }

    [Fact]
    public void TheRealRowShapes_Translate()
    {
        ConditionRow[] rows =
        [
            Row(6492, 41, 5, 2), Row(122, 48), Row(123, 49, 1), Row(124, -1, 122, 123), Row(192, 55, 2), Row(15958, -1, 122, 192),
            Row(1111, 16, 15882, flags: 1),
        ];

        Assert.Equal("health:5:2", Translate(6492, rows).Expression);
        Assert.Equal("spawned", Translate(122, rows).Expression);
        Assert.Equal("spawned;loot:1", Translate(124, rows).Expression);
        Assert.Equal("spawned;gostate:2", Translate(15958, rows).Expression);
        Assert.Equal("!entry:15882", Translate(1111, rows).Expression);
    }

    [Fact]
    public void SourceEntry_KeepsOnlyTheEntriesThatAreSet()
    {
        Assert.Equal("entry:10:30", Translate(1, Row(1, 16, 10, 0, 30)).Expression);
    }

    [Fact]
    public void ACondition_NotInTheInput_IsRefused()
    {
        (bool ok, string expression, string failure) = Translate(7777, Row(122, 48));

        Assert.False(ok);
        Assert.Equal(string.Empty, expression);
        Assert.Contains("7777", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(6, 67u, 0u, 0)]      // CONDITION_TEAM has a cmangos meaning but is no target check here
    [InlineData(-2, 1u, 2u, 0)]      // OR
    [InlineData(-3, 1u, 0u, 0)]      // NOT
    [InlineData(41, 5u, 7u, 0)]      // a health comparison other than equal, at least, at most
    [InlineData(48, 0u, 0u, 2)]      // the swap-targets flag
    public void AnUntranslatableCondition_IsRefused(int type, uint v1, uint v2, int flags)
    {
        (bool ok, _, string failure) = Translate(1, Row(1, type, v1, v2, flags: (byte)flags), Row(2, 48));

        Assert.False(ok);
        Assert.NotEmpty(failure);
    }

    [Fact]
    public void AnAndThatLoopsOrIsReversed_IsRefused()
    {
        Assert.False(Translate(1, Row(1, -1, 2, 2), Row(2, -1, 1, 1)).Ok);
        Assert.False(Translate(1, Row(1, -1, 2, 2, flags: 1), Row(2, 48)).Ok);
    }
}
