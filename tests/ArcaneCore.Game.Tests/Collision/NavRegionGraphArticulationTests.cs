using ArcaneCore.Game.Maps.Collision.MMaps;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The cut regions of a <see cref="NavRegionGraph"/> (<see cref="NavRegionGraph.IsArticulation"/>, <see cref="NavRegionGraph.JoinsLargeParts"/>):
/// the one region a tunnel is between two large pieces of mesh, on graphs made by hand (<see cref="NavRegionGraph.FromJoints"/>).
/// </summary>
public sealed class NavRegionGraphArticulationTests
{
    private static NavRegionGraph Graph(int[] polygons, params (int A, int B)[] joints) => NavRegionGraph.FromJoints(1, polygons, joints);

    [Fact]
    public void ATunnelRegionBetweenTwoLargeParts_IsACutRegion_AndJoinsLargeParts()
    {
        NavRegionGraph graph = Graph([1000, 1, 2000], (0, 1), (1, 2)); // A - B - C

        Assert.False(graph.IsArticulation(0));
        Assert.True(graph.IsArticulation(1));
        Assert.False(graph.IsArticulation(2));
        Assert.True(graph.JoinsLargeParts(1, 500));
        Assert.True(graph.JoinsLargeParts(1, 1000)); // the smaller side holds 1000
        Assert.False(graph.JoinsLargeParts(1, 1001));
        Assert.False(graph.JoinsLargeParts(0, 1));
    }

    [Fact]
    public void ARegionBetweenALargePartAndAPocket_IsACutRegion_ButDoesNotJoinLargeParts()
    {
        NavRegionGraph graph = Graph([5000, 1, 3], (0, 1), (1, 2));

        Assert.True(graph.IsArticulation(1));
        Assert.False(graph.JoinsLargeParts(1, 500));
        Assert.True(graph.JoinsLargeParts(1, 3));
    }

    [Fact]
    public void ARegionOnACycle_IsNoCutRegion()
    {
        NavRegionGraph graph = Graph([100, 100, 100, 100], (0, 1), (1, 2), (2, 3), (3, 0));

        for (int region = 0; region < 4; region++)
        {
            Assert.False(graph.IsArticulation(region));
            Assert.False(graph.JoinsLargeParts(region, 1));
        }
    }

    [Fact]
    public void TwoPortalsBetweenTheSameRegions_DoNotMakeTheMiddleRegionAnAlternative()
    {
        // A = B by two portals, B - C by one: B still stands between A and C.
        NavRegionGraph graph = Graph([600, 1, 600], (0, 1), (0, 1), (1, 2));

        Assert.True(graph.IsArticulation(1));
        Assert.True(graph.JoinsLargeParts(1, 600));
    }

    [Fact]
    public void TheDepthFirstRoot_IsACutRegion_WhenItHasTwoSeparatedChildren_AndTheRemainderPartIsCounted()
    {
        // Region 0 is the search's root and the middle of 1 - 0 - 2.
        NavRegionGraph star = Graph([1, 700, 800], (0, 1), (0, 2));
        Assert.True(star.IsArticulation(0));
        Assert.True(star.JoinsLargeParts(0, 700));
        Assert.False(star.JoinsLargeParts(0, 701));

        // Chain 0 - 1 - 2 - 3 - 4 rooted at 0: region 2 has the child side (3, 4: 14) and the root side (0, 1: 10) as parts.
        NavRegionGraph chain = Graph([5, 5, 1, 7, 7], (0, 1), (1, 2), (2, 3), (3, 4));
        Assert.True(chain.IsArticulation(2));
        Assert.True(chain.JoinsLargeParts(2, 10));
        Assert.False(chain.JoinsLargeParts(2, 11));
        Assert.False(chain.IsArticulation(0));
        Assert.False(chain.IsArticulation(4));
        Assert.True(chain.IsArticulation(1));
        Assert.False(chain.JoinsLargeParts(1, 6)); // the part behind region 0 holds 5
    }

    [Fact]
    public void EachComponentIsJudgedOnItsOwn_AndAnIsolatedRegionIsNoCutRegion()
    {
        NavRegionGraph graph = Graph([10, 10, 10, 50, 50, 50, 99], (0, 1), (1, 2), (3, 4), (4, 5));

        Assert.True(graph.IsArticulation(1));
        Assert.True(graph.IsArticulation(4));
        Assert.True(graph.JoinsLargeParts(4, 50));
        Assert.False(graph.JoinsLargeParts(1, 11));
        Assert.False(graph.IsArticulation(6));
        Assert.False(graph.IsArticulation(-1));
        Assert.False(graph.IsArticulation(7));
    }

    [Fact]
    public void ALongCorridorOfAHundredThousandRegions_IsLabelledWithoutRecursion()
    {
        const int Regions = 100_000;
        int[] polygons = new int[Regions];
        Array.Fill(polygons, 2);
        var joints = new (int A, int B)[Regions - 1];
        for (int i = 0; i < joints.Length; i++) joints[i] = (i, i + 1);
        NavRegionGraph graph = Graph(polygons, joints);

        graph.Prepare();

        Assert.False(graph.IsArticulation(0));
        Assert.False(graph.IsArticulation(Regions - 1));
        Assert.True(graph.IsArticulation(Regions / 2));
        Assert.True(graph.JoinsLargeParts(Regions / 2, 99_000));
        Assert.Equal(graph.ComponentOf(0), graph.ComponentOf(Regions - 1));
    }
}
