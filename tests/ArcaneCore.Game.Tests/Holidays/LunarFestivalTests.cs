using ArcaneCore.Game.Holidays;
using Xunit;

namespace ArcaneCore.Game.Tests.Holidays;

/// <summary>boss_omen, the fireworks and the city show (vmangos boss_omen.cpp, npcs_special.cpp, fireworks_show.cpp).</summary>
public sealed class LunarFestivalTests
{
    [Fact]
    public void Omen_rises_on_the_twentieth_firework_and_not_again_for_fifteen_minutes()
    {
        long now = 1_000_000;
        var omen = new OmenController { NowUnix = () => now };
        for (int i = 1; i < OmenController.FireworksToSummon; i++) Assert.False(omen.CountLaunch());
        Assert.True(omen.CountLaunch());

        // he died: the next twenty launches inside fifteen minutes keep counting but do not summon
        omen.FireworksCount = 0;
        omen.NextRespawnUnix = now + OmenController.RespawnSeconds;
        for (int i = 0; i < 25; i++) Assert.False(omen.CountLaunch());
        now += OmenController.RespawnSeconds + 1;
        Assert.True(omen.CountLaunch()); // the count kept growing past twenty
    }

    [Fact]
    public void The_firework_table_matches_vmangos()
    {
        Assert.Equal(25, FireworkCatalog.Fireworks.Count);
        Assert.All(FireworkCatalog.Fireworks.Where(f => f.IsCluster), f => Assert.Equal(5, f.Spells.Length));
        Assert.All(FireworkCatalog.Fireworks.Where(f => !f.IsCluster), f => Assert.Single(f.Spells));
        Assert.Equal([26487u, 26509, 26508, 26484, 26483], FireworkCatalog.Find(FireworkCatalog.NpcLuckyCluster)!.Spells);
    }

    [Theory]
    [InlineData(33u, 25)]
    [InlineData(1519u, 117)]
    [InlineData(1637u, 93)]
    [InlineData(14u, 93)]
    [InlineData(1537u, 57)]
    [InlineData(1u, 57)]
    [InlineData(1638u, 58)]
    [InlineData(1497u, 56)]
    [InlineData(85u, 56)]
    [InlineData(141u, 59)]
    public void Every_city_has_its_launch_points(uint zone, int count)
        => Assert.Equal(count, CheerSpeakerAi.PositionsFor(zone)!.Length);

    [Fact]
    public void Zones_without_a_show_have_no_points()
    {
        Assert.Null(CheerSpeakerAi.PositionsFor(12));
        Assert.Equal(23, CheerSpeakerAi.FireworkIds.Length);
        Assert.Equal(12, CheerSpeakerAi.FireworkBigIds.Length);
        Assert.All(CheerSpeakerAi.FireworkBigIds, id => Assert.Contains(id, CheerSpeakerAi.FireworkIds));
    }
}
