using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// The visible-set invariant pass (<see cref="Map.VerifyVisibleSetInvariants"/>) runs inside the gated Output stage of every
/// visibility update. It is only needed after something left the map or a visible set, so it is epoch-gated, with a backstop
/// pass on every 64th update of a player. <see cref="Map.VisibilityInvariantScans"/> is the counter seam.
/// </summary>
public sealed class VisibilityInvariantGatingTests
{
    private static (WorldRuntime World, Map Map, Player Player, TestUnit Near) Setup()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var near = new TestUnit(1, 10, 0);
        map.AddObject(near);
        Player player = TestWorld.CreatePlayer(1, 0, 0, new NullPlayerSession(1));
        world.AddPlayer(player);
        return (world, map, player, near);
    }

    [Fact]
    public void UpdatesWithoutAnyRemoval_SkipTheInvariantPass()
    {
        (WorldRuntime world, Map map, Player player, _) = Setup();
        using (world)
        {
            long before = map.VisibilityInvariantScans;
            for (int i = 0; i < 20; i++) map.RefreshVisibility(player);
            Assert.Equal(before, map.VisibilityInvariantScans);
        }
    }

    [Fact]
    public void ARemoval_TriggersTheInvariantPassOnTheNextUpdate()
    {
        (WorldRuntime world, Map map, Player player, TestUnit near) = Setup();
        using (world)
        {
            Assert.Contains(near.Guid, player.VisibleObjects);
            long before = map.VisibilityInvariantScans;
            map.RefreshVisibility(player);
            Assert.Equal(before, map.VisibilityInvariantScans);

            map.RemoveObject(near);
            map.RefreshVisibility(player);
            Assert.Equal(before + 1, map.VisibilityInvariantScans);

            // Handled: the next update is skipped again.
            map.RefreshVisibility(player);
            Assert.Equal(before + 1, map.VisibilityInvariantScans);
        }
    }

    [Fact]
    public void TheBackstop_RunsThePassOnEverySixtyFourthUpdateOfAPlayer()
    {
        (WorldRuntime world, Map map, Player player, _) = Setup();
        using (world)
        {
            // Joining ran update number 1, which scanned (nothing verified yet).
            Assert.Equal(1, map.VisibilityInvariantScans);
            for (int update = 2; update <= 63; update++)
            {
                map.RefreshVisibility(player);
                Assert.Equal(1, map.VisibilityInvariantScans);
            }

            map.RefreshVisibility(player); // the 64th
            Assert.Equal(2, map.VisibilityInvariantScans);
            for (int update = 65; update <= 127; update++) map.RefreshVisibility(player);
            Assert.Equal(2, map.VisibilityInvariantScans);
            map.RefreshVisibility(player); // the 128th
            Assert.Equal(3, map.VisibilityInvariantScans);
        }
    }

    [Fact]
    public void AnInjectedOrphan_IsHealedAfterARemovalAndByTheBackstop()
    {
        (WorldRuntime world, Map map, Player player, TestUnit near) = Setup();
        using (world)
        {
            var ghost = new ObjectGuid(0x00FF_FFFF_FFFFUL);

            // Path 1: a removal notice makes the next update verify and heal it.
            player.VisibleObjects.Add(ghost);
            map.NoteRemoval();
            map.RefreshVisibility(player);
            Assert.DoesNotContain(ghost, player.VisibleObjects);
            Assert.Contains(near.Guid, player.VisibleObjects);

            // Path 2: with no notice, the 64th update heals it.
            player.VisibleObjects.Add(ghost);
            bool healed = false;
            for (int update = 1; update <= Map.VisibleSetBackstopInterval && !healed; update++)
            {
                map.RefreshVisibility(player);
                healed = !player.VisibleObjects.Contains(ghost);
            }

            Assert.True(healed);
        }
    }

    [Fact]
    public void TheDirectVerifier_AlwaysScansAndReportsAnOrphan()
    {
        (WorldRuntime world, Map map, Player player, _) = Setup();
        using (world)
        {
            long before = map.VisibilityInvariantScans;
            Assert.False(map.VerifyVisibleSetInvariants(player));
            Assert.Equal(before + 1, map.VisibilityInvariantScans);

            var ghost = new ObjectGuid(0x00FF_FFFF_FFFEUL);
            player.VisibleObjects.Add(ghost);
            Assert.True(map.VerifyVisibleSetInvariants(player));
            Assert.DoesNotContain(ghost, player.VisibleObjects);
        }
    }
}
