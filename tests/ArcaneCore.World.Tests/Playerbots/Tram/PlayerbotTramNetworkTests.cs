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
    [InlineData(-8354f, 524f, -4918.9f, -982.5f, 1519u, 1537u, true)]
    [InlineData(-9460f, 60f, -5600f, -500f, 12u, 1u, true)]
    [InlineData(-9400f, -1300f, -5600f, -500f, 12u, 1u, true)]
    [InlineData(-8354f, 524f, -5400f, -2900f, 1519u, 38u, true)]
    [InlineData(-3700f, -800f, -8800f, 600f, 11u, 1519u, true)]
    // Stormwind to Goldshire, Westfall to Elwynn, Ironforge to Loch Modan, Kharanos to Ironforge: one side.
    [InlineData(-8354f, 524f, -9460f, 60f, 1519u, 12u, false)]
    [InlineData(-10500f, 1000f, -9460f, 60f, 40u, 12u, false)]
    [InlineData(-4839f, -1317f, -5400f, -2900f, 1537u, 38u, false)]
    [InlineData(-5600f, -500f, -4918.9f, -982.5f, 1u, 1537u, false)]
    public void TheTram_IsTakenOnlyAcrossTheTwoCities(float botX, float botY, float goalX, float goalY,
        uint sourceZone, uint goalZone, bool expected)
    {
        (TramPlatform From, TramPlatform To)? leg = Network().Choose(0, new Vector3(botX, botY, 0),
            new Vector3(goalX, goalY, 0), sourceZone, goalZone);
        Assert.Equal(expected, leg is not null);
    }

    [Fact]
    public void ColdridgeUsesTheIronforgeEntranceForAStormwindDestination()
    {
        // Live Coldridge bot position and ClassicDB quest-579 giver position. Zone 1 is Dun Morogh.
        (TramPlatform From, TramPlatform To)? leg = Network().Choose(0,
            new Vector3(-6489.42f, 494.74f, 388.03f), new Vector3(-8344.69f, 418.89f, 122.36f),
            sourceZoneId: 1, goalZoneId: 1519);
        Assert.NotNull(leg);
        Assert.Equal(2175u, leg.Value.From.Entrance.Id);
        Assert.Equal(2173u, leg.Value.To.Entrance.Id);
    }

    [Theory]
    // All of these destinations remain on the Dun Morogh/Ironforge side despite their straight-line distance to Stormwind.
    [InlineData(-6489.42f, 494.74f, -6222.47f, 688.97f)] // Coldridge to Talin Keeneye
    [InlineData(-6489.42f, 494.74f, -6214.85f, 328.18f)] // Coldridge to Sten Stoutarm
    [InlineData(-5600f, -500f, -6214.85f, 328.18f)]       // Kharanos to Anvilmar
    public void DunMoroghGoals_DoNotStartAStormwindTrip(float botX, float botY, float goalX, float goalY)
        => Assert.Null(Network().Choose(0, new Vector3(botX, botY, 0), new Vector3(goalX, goalY, 0),
            sourceZoneId: 1, goalZoneId: 1));

    [Fact]
    public void GoldshireToColdridge_UsesTheStormwindEntrance()
    {
        (TramPlatform From, TramPlatform To)? leg = Network().Choose(0,
            new Vector3(-9460f, 60f, 56f), new Vector3(-6489.42f, 494.74f, 388.03f),
            sourceZoneId: 12, goalZoneId: 1);
        Assert.NotNull(leg);
        Assert.Equal(2173u, leg.Value.From.Entrance.Id);
        Assert.Equal(2175u, leg.Value.To.Entrance.Id);
    }

    [Fact]
    public void DuskwoodToThoriumPoint_UsesTheStormwindEntrance()
    {
        (TramPlatform From, TramPlatform To)? leg = Network().Choose(0,
            new Vector3(-10500f, -1160f, 40f), new Vector3(-6524.04f, -1188.72f, 309.34f),
            sourceZoneId: 10, goalZoneId: 51);
        Assert.NotNull(leg);
        Assert.Equal(2173u, leg.Value.From.Entrance.Id);
        Assert.Equal(2175u, leg.Value.To.Entrance.Id);
    }

    [Theory]
    [InlineData(293u, 1519u, 2175u)] // Thoradin's Wall -> Stormwind
    [InlineData(1477u, 1u, 2173u)] // Temple of Atal'Hakkar exterior -> Dun Morogh
    public void Additional5875Zones_UseTheirContinentSide(uint sourceZone, uint goalZone, uint expectedEntrance)
    {
        (TramPlatform From, TramPlatform To)? leg = Network().Choose(0,
            new Vector3(-6000f, -1000f, 100f), new Vector3(-8400f, 500f, 100f), sourceZone, goalZone);
        Assert.NotNull(leg);
        Assert.Equal(expectedEntrance, leg.Value.From.Entrance.Id);
    }

    [Fact]
    public void BlackrockMountainZone_RemainsUnknown()
        => Assert.Null(Network().Choose(0, new Vector3(-7500f, -1100f, 200f),
            new Vector3(-8400f, 500f, 100f), sourceZoneId: 25, goalZoneId: 1519));

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

    /// <summary>
    /// Deeprun car 176083 (TransportAnimation.dbc, abridged to its stops): the home dwell is 129,333 ms through the 143,333 ms cycle
    /// end and on to 867 ms. The offset is null at progress 0, so a sample that lands there used to end the stop. Live board-missed
    /// (#918) reported arriveMs=6250 departMs=20000, that cut. A walk that fits the whole dwell and not the cut must take this stop,
    /// not the same platform a cycle later.
    /// </summary>
    [Fact]
    public void ADeeprunStopThatSpansTheCycleWrap_IsOneStop_AndStillBoardable()
    {
        const uint cycle = 143_333;
        const uint now = 123_083; // 6,250 ms before the home keyframe at 129,333
        Vector3 here = new(0, 0, 0), other = new(-2481.768f, 0, -1.027f);
        Vector3? At(uint ahead) => DeeprunOffset((now + ahead) % cycle);

        TramDock? dock = PlayerbotTram.NextDock(cycle, At, here, other);
        Assert.NotNull(dock);
        Assert.Equal(6_250u, dock.Value.ArriveMs);
        Assert.Equal(21_000u, dock.Value.DepartMs);

        // 18.5 s to walk on: the cut dwell (depart 20,000) fails Depart > 20,500; the merged dwell (depart 21,000) passes.
        TramDock? usable = PlayerbotTram.NextDock(cycle, At, here, other,
            d => d.DepartMs > Math.Max(d.ArriveMs, 18_500u) + PlayerbotTram.BoardMarginMs);
        Assert.NotNull(usable);
        Assert.Equal(dock.Value.ArriveMs, usable.Value.ArriveMs);
        Assert.Equal(dock.Value.DepartMs, usable.Value.DepartMs);
    }

    /// <summary>Linear offset of the abridged Deeprun nodes; null at progress 0, as <c>TransportAnimationCatalog.Offset</c> is.</summary>
    private static Vector3? DeeprunOffset(uint progress)
    {
        (uint Time, float X, float Y, float Z)[] nodes =
        [
            (0, 0f, 0f, 0f),
            (867, 0f, 0f, 0f),
            (4951, -209.990f, 0f, -0.107f),
            (59500, -2481.768f, 0f, -1.027f),
            (70733, -2481.768f, 0f, -1.027f),
            (125308, -209.990f, 0f, -0.107f),
            (129333, 0f, 0f, 0f),
            (142465, 0f, 0f, 0f),
            (143333, 0f, 0f, 0f),
        ];
        int next = -1;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].Time >= progress)
            {
                next = i;
                break;
            }
        }

        if (next <= 0) return null;
        (uint time, float x, float y, float z) prev = nodes[next - 1];
        (uint time, float x, float y, float z) after = nodes[next];
        if (prev.x == after.x && prev.y == after.y && prev.z == after.z) return new Vector3(prev.x, prev.y, prev.z);
        float elapsed = progress - prev.time;
        float span = after.time - prev.time;
        return new Vector3(
            prev.x + (elapsed * (after.x - prev.x) / span),
            prev.y + (elapsed * (after.y - prev.y) / span),
            prev.z + (elapsed * (after.z - prev.z) / span));
    }
}
