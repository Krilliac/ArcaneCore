using ArcaneCore.Game.Social;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>The /who zone filter and search strings (vmangos WhoListClientQueryTask, MiscHandler.cpp:158-196).</summary>
public sealed class WhoRulesTests
{
    private const uint Elwynn = 12;
    private const uint WarsongGulch = 3277;
    private const uint WarsongMap = 489;

    [Fact]
    public void NoZones_ShowsEveryone_AndAListedZoneMustMatch()
    {
        Assert.True(WhoRules.ZoneFilterShows([], Elwynn, 0, 0, 40, 0, 0));
        Assert.True(WhoRules.ZoneFilterShows([Elwynn, 40], Elwynn, 0, 0, 40, 0, 0));
        Assert.False(WhoRules.ZoneFilterShows([Elwynn], Elwynn, 0, 0, 40, 0, 0));
    }

    [Fact]
    public void FilteringOnOnesOwnBattlegroundZone_ShowsOnlyOnesOwnInstance()
    {
        Assert.True(WhoRules.ZoneFilterShows([WarsongGulch], WarsongGulch, WarsongMap, 7, WarsongGulch, WarsongMap, 7));
        Assert.False(WhoRules.ZoneFilterShows([WarsongGulch], WarsongGulch, WarsongMap, 7, WarsongGulch, WarsongMap, 8));

        // Asked from outside the battleground, every instance of the zone is listed.
        Assert.True(WhoRules.ZoneFilterShows([WarsongGulch], Elwynn, 0, 0, WarsongGulch, WarsongMap, 8));
        // A plain zone has no instance rule.
        Assert.True(WhoRules.ZoneFilterShows([Elwynn], Elwynn, 0, 0, Elwynn, 0, 0));
        Assert.Equal([2597u, 3277u, 3358u], WhoRules.BattlegroundZones.Order());
    }

    [Fact]
    public void SearchStrings_MatchTheGuildTheNameOrTheArea_AndEmptyOnesAreSkipped()
    {
        Assert.True(WhoRules.MatchesSearchStrings([], "farmer", "", "westfall"));
        Assert.True(WhoRules.MatchesSearchStrings(["", ""], "farmer", "", "westfall"));
        Assert.True(WhoRules.MatchesSearchStrings(["west"], "farmer", "", "westfall"));
        Assert.True(WhoRules.MatchesSearchStrings(["xyz", "arm"], "farmer", "", "westfall"));
        Assert.True(WhoRules.MatchesSearchStrings(["guard"], "farmer", "the guard", ""));
        Assert.False(WhoRules.MatchesSearchStrings(["durotar"], "farmer", "the guard", "westfall"));
        Assert.False(WhoRules.MatchesSearchStrings(["west"], "farmer", "", "")); // a zone without a known name matches nothing
    }
}
