using System.Numerics;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// The off-thread reachability check (<see cref="PlayerbotTravel.CanReachStatic(ReadOnlySpan{ShipRoute}, bool, uint, uint, PlayerbotTravelOptions)"/>):
/// the same map always, other maps only through ships the faction may ride with the switches on, a chain of them included, and nothing
/// across maps without published routes. Pure and allocation-free, on any thread.
/// </summary>
public sealed class TravelCanReachStaticTests
{
    private static TravelPlace Port(uint map, float x) => new(map, new Vector3(x, 0f, 0f));

    private static readonly ShipRoute AllianceBoat = new(1, TravelMeans.Boat, TravelAccess.Alliance, Port(0, 10f), Port(1, 20f), 300f);
    private static readonly ShipRoute HordeZeppelin = new(2, TravelMeans.Zeppelin, TravelAccess.Horde, Port(0, 30f), Port(1, 40f), 300f);
    private static readonly ShipRoute SharedBoat = new(3, TravelMeans.Boat, TravelAccess.Both, Port(1, 50f), Port(30, 60f), 300f);
    private static readonly ShipRoute[] All = [AllianceBoat, HordeZeppelin, SharedBoat];

    private static bool Reach(ShipRoute[] routes, bool alliance, uint from, uint to, PlayerbotTravelOptions? travel = null)
        => PlayerbotTravel.CanReachStatic(routes, alliance, from, to, travel ?? new PlayerbotTravelOptions());

    [Fact]
    public void TheSameMap_IsAlwaysReachable_EvenWithTravelOffAndNoRoutes()
    {
        Assert.True(Reach([], alliance: true, 0, 0));
        Assert.True(Reach([], alliance: false, 1, 1, new PlayerbotTravelOptions { Enabled = false }));
        Assert.True(Reach(All, alliance: true, 30, 30, new PlayerbotTravelOptions { Enabled = false }));
    }

    [Fact]
    public void WithoutPublishedRoutes_OnlyTheSameMapAnswers()
    {
        Assert.False(Reach([], alliance: true, 0, 1));
        Assert.False(Reach([], alliance: false, 1, 0));
    }

    [Fact]
    public void AnAllianceBot_CrossesByItsBoat_BothWays_AndAHordeBotByItsZeppelin_NeverTheOthers()
    {
        Assert.True(Reach([AllianceBoat], alliance: true, 0, 1));
        Assert.True(Reach([AllianceBoat], alliance: true, 1, 0));
        Assert.False(Reach([AllianceBoat], alliance: false, 0, 1));
        Assert.True(Reach([HordeZeppelin], alliance: false, 0, 1));
        Assert.True(Reach([HordeZeppelin], alliance: false, 1, 0));
        Assert.False(Reach([HordeZeppelin], alliance: true, 0, 1));
        Assert.False(Reach(All, alliance: true, 0, 530));
    }

    [Fact]
    public void AChainOfShips_ReachesTheFarMap()
    {
        Assert.True(Reach(All, alliance: true, 0, 30));
        Assert.True(Reach(All, alliance: false, 30, 0));
        Assert.False(Reach([SharedBoat], alliance: true, 0, 30));
    }

    [Fact]
    public void ASwitchTurnedOff_ExcludesItsShips_AndTravelOff_ExcludesEveryOtherMap()
    {
        Assert.False(Reach([AllianceBoat], alliance: true, 0, 1, new PlayerbotTravelOptions { Boats = false }));
        Assert.True(Reach([AllianceBoat], alliance: true, 0, 1, new PlayerbotTravelOptions { Zeppelins = false }));
        Assert.False(Reach([HordeZeppelin], alliance: false, 0, 1, new PlayerbotTravelOptions { Zeppelins = false }));
        Assert.False(Reach(All, alliance: false, 0, 30, new PlayerbotTravelOptions { Boats = false }));
        Assert.False(Reach(All, alliance: true, 0, 1, new PlayerbotTravelOptions { Enabled = false }));
    }

    [Fact]
    public void TheCheck_AllocatesNothing()
    {
        var travel = new PlayerbotTravelOptions();
        bool any = false;
        for (int i = 0; i < 16; i++) any ^= PlayerbotTravel.CanReachStatic(All, i % 2 == 0, 0, 30, travel); // warm up (JIT)
        void Window()
        {
            for (int i = 0; i < 10_000; i++)
            {
                any ^= PlayerbotTravel.CanReachStatic(All, i % 2 == 0, 0, (uint)(i % 3 == 0 ? 30 : 1), travel);
                any ^= PlayerbotTravel.CanReachStatic(All, true, 0, 530, travel);
            }
        }

        ArcaneCore.TestSupport.AllocationProbe.AssertEveryCleanWindowAtMost(Window, 0, "20000 CanReachStatic checks");
        Assert.True(any || !any);
    }

    [Fact]
    public async Task TheCheck_AnswersTheSameOnOtherThreads()
    {
        var travel = new PlayerbotTravelOptions();
        bool[] answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            PlayerbotTravel.CanReachStatic(All, i % 2 == 0, 0, 30, travel) && !PlayerbotTravel.CanReachStatic(All, i % 2 == 0, 0, 530, travel))));
        Assert.All(answers, Assert.True);
    }
}
