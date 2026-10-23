using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

public sealed class VisibilityAllocationTests
{
    [Fact]
    public void StablePlayerVisibility_ReusesCandidatesAfterWarmup()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        for (uint i = 1; i <= 1000; i++) map.AddObject(new TestUnit(i, i % 20, i / 20));
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        world.AddPlayer(player);
        world.RunTick(50);
        for (int i = 0; i < 20; i++) map.RefreshVisibility(player);
        // No packet changes: permit small fixed bookkeeping, never lists proportional to the visible population. Every GC-free window must pass.
        AllocationProbe.AssertEveryCleanWindowAtMost(() => { for (int i = 0; i < 100; i++) map.RefreshVisibility(player); }, 8191, "100 stable refreshes");
        Assert.Equal(1000, player.VisibleObjects.Count);
    }

    [Fact]
    public void NestedPlayerRefresh_AndThrowingRule_DoNotCorruptReusableCandidates()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        Player a = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
        Player b = TestWorld.CreatePlayer(2, 1000, 0, new FakeSession(2));
        world.AddPlayer(a); world.AddPlayer(b);
        var nearA = new TestUnit(1, 10, 0);
        var nearB = new TestUnit(2, 1010, 0);
        map.AddObject(nearA); map.AddObject(nearB);
        world.RunTick(50);
        var rule = new NestedRule(map, a, b);
        map.AddVisibilityRule(rule);
        map.RefreshVisibility(a);
        Assert.True(rule.Visited);
        Assert.Equal(new[] { nearA.Guid }, a.VisibleObjects);
        Assert.Equal(new[] { nearB.Guid }, b.VisibleObjects);
        rule.Throw = true;
        Assert.Throws<InvalidOperationException>(() => map.RefreshVisibility(a));
        rule.Throw = false;
        map.RefreshVisibility(a);
        Assert.Equal(new[] { nearA.Guid }, a.VisibleObjects);
        Assert.Contains(a, map.ObserversOf(nearA));
    }

    private sealed class NestedRule(Map map, Player outer, Player inner) : IVisibilityRule
    {
        public bool Visited;
        public bool Throw;
        public bool CanSee(Player viewer, WorldObject target, bool alreadyVisible, bool detect)
        {
            if (Throw) throw new InvalidOperationException("synthetic visibility failure");
            if (viewer == outer && !Visited)
            {
                Visited = true;
                map.RefreshVisibility(inner);
            }
            return true;
        }
    }
}
