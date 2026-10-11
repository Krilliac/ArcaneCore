using System.Numerics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Tram;

/// <summary>
/// The Deeprun Tram as the trigger rows describe it (<see cref="TramNetwork"/>), when a destination is reached through it
/// (<see cref="TramNetwork.Choose"/>), and when a car stands at a platform (<see cref="PlayerbotTram.NextDock"/>). Pure: no world.
/// </summary>
public sealed class PlayerbotTramNetworkTests
{
    private static TramNetwork Network(IEnumerable<AreaTriggerTemplate>? triggers = null, IEnumerable<AreaTriggerTeleport>? teleports = null)
    {
        Dictionary<uint, AreaTriggerTeleport> byId = (teleports ?? TramTestData.Teleports).ToDictionary(t => t.Id);
        return TramNetwork.Build(triggers ?? TramTestData.Triggers, id => byId.GetValueOrDefault(id))!;
    }

    [Fact]
    public void TheClassicDbRows_DescribeTwoPlatforms_EachEntranceWithItsOwnExit()
    {
        TramNetwork network = Network();
        Assert.NotNull(network);
        Assert.Equal(0u, network.ContinentMap);
        Assert.Equal([(2173u, 2171u), (2175u, 2166u)], network.Platforms.Select(p => (p.Entrance.Id, p.Exit.Id)));
        Assert.Equal("Deeprun Tram - Stormwind Instance", network.Platforms[0].Name);
        Assert.Same(network.Platforms[0], network.NearestOnTramMap(TramTestData.StormwindPlatform));
        Assert.Same(network.Platforms[1], network.NearestOnTramMap(new Vector3(-45f, 30f, -4f)));
    }

    [Fact]
    public void AMissingExit_ASingleEntrance_OrAnExitLandingElsewhere_DescribeNoTram()
    {
        Assert.Null(TramNetwork.Build(TramTestData.Triggers.Where(t => t.Id != 2166), id => TramTestData.Teleports.FirstOrDefault(t => t.Id == id)));
        Assert.Null(TramNetwork.Build(TramTestData.Triggers.Where(t => t.Id != 2175), id => TramTestData.Teleports.FirstOrDefault(t => t.Id == id)));
        AreaTriggerTeleport[] moved = [.. TramTestData.Teleports.Select(t => t.Id == 2166 ? t with { TargetX = t.TargetX + 500f } : t)];
        Assert.Null(TramNetwork.Build(TramTestData.Triggers, id => moved.FirstOrDefault(t => t.Id == id)));
        Assert.Null(TramNetwork.Build(TramTestData.Triggers, _ => null));
    }

    [Theory]
    // Stormwind to the Commons, Goldshire to Kharanos, Eastvale to Kharanos, Stormwind to Thelsamar, Menethil Harbor to Stormwind.
    [InlineData(-8354f, 524f, -4918.9f, -982.5f, true)]
    [InlineData(-9460f, 60f, -5600f, -500f, true)]
    [InlineData(-9400f, -1300f, -5600f, -500f, true)]
    [InlineData(-8354f, 524f, -5400f, -2900f, true)]
    [InlineData(-3700f, -800f, -8800f, 600f, true)]
    // Stormwind to Goldshire, Westfall to Elwynn, Ironforge to Loch Modan, Kharanos to Ironforge: one side.
    [InlineData(-8354f, 524f, -9460f, 60f, false)]
    [InlineData(-10500f, 1000f, -9460f, 60f, false)]
    [InlineData(-4839f, -1317f, -5400f, -2900f, false)]
    [InlineData(-5600f, -500f, -4918.9f, -982.5f, false)]
    public void TheTram_IsTakenOnlyAcrossTheTwoCities(float botX, float botY, float goalX, float goalY, bool expected)
    {
        (TramPlatform From, TramPlatform To)? leg = Network().Choose(0, new Vector3(botX, botY, 0), new Vector3(goalX, goalY, 0));
        Assert.Equal(expected, leg is not null);
    }

    [Fact]
    public void ColdridgeUsesTheIronforgeEntranceForAStormwindDestination()
    {
        // Live Coldridge bot position and ClassicDB quest-579 giver position. Zone 1 is Dun Morogh.
        (TramPlatform From, TramPlatform To)? leg = Network().Choose(0,
            new Vector3(-6489.42f, 494.74f, 388.03f), new Vector3(-8344.69f, 418.89f, 122.36f), sourceZoneId: 1);
        Assert.NotNull(leg);
        Assert.Equal(2175u, leg.Value.From.Entrance.Id);
        Assert.Equal(2173u, leg.Value.To.Entrance.Id);
    }

    [Fact]
    public void TheTram_IsLeftAloneOnAnotherMap()
        => Assert.Null(Network().Choose(1, new Vector3(-8354f, 524f, 0), new Vector3(-4918.9f, -982.5f, 0)));

    [Fact]
    public void ACarsNextStop_IsFoundWithItsLength_AndATooShortOneIsPassedForTheNext()
    {
        // A car at "here" (y 0) from 107 s to 120 s of a 120 s cycle, at "other" (y 1000) from 47 s to 60 s; now is 100 s into the cycle.
        Vector3 here = new(0, 0, 0), other = new(0, 1000, 0);
        Vector3? At(uint ahead)
        {
            uint t = (100_000 + ahead) % 120_000;
            float y = t < 47_000 ? 1000f * t / 47_000 : t < 60_000 ? 1000f : t < 107_000 ? 1000f * (107_000 - t) / 47_000 : 0f;
            return new Vector3(0, y, 0);
        }

        TramDock? first = PlayerbotTram.NextDock(120_000, At, here, other);
        Assert.NotNull(first);
        Assert.Equal(7_000u, first.Value.ArriveMs);
        Assert.Equal(20_000u, first.Value.DepartMs);
        Assert.Equal(0f, first.Value.Position.Y, 0.01f);

        // Too short to board after a walk of 19 s: the next stop, one cycle later.
        TramDock? next = PlayerbotTram.NextDock(120_000, At, here, other, d => d.DepartMs > Math.Max(d.ArriveMs, 19_000u) + 2_000);
        Assert.NotNull(next);
        Assert.Equal(127_000u, next.Value.ArriveMs);

        // It never stops nearer a third place than the other end.
        Assert.Null(PlayerbotTram.NextDock(120_000, At, new Vector3(0, -5000, 0), new Vector3(0, 5000, 0)));
    }
}
