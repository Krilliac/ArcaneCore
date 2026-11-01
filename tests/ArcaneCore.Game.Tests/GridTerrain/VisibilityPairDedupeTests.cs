using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// Worldthread audit lane 6 (finding 2.2): when two players both moved, A's visibility pass ran A-B and B-A and B's pass ran
/// both again. Inside one Output stage an ordered (viewer, target) pair is now evaluated once. The dedupe is per ordered pair,
/// so asymmetric rules still answer each direction, and it never applies outside the stage. Counters:
/// <see cref="Map.VisibilityPairEvaluations"/> and <see cref="Map.VisibilityPairDuplicatesSkipped"/>.
/// </summary>
public sealed class VisibilityPairDedupeTests
{
    private sealed class HidesFrom(Player blindViewer, Player hidden) : IVisibilityRule
    {
        public bool CanSee(Player viewer, WorldObject target, bool alreadyVisible, bool detect)
            => !(ReferenceEquals(viewer, blindViewer) && ReferenceEquals(target, hidden));
    }

    private static (WorldRuntime World, Map Map, Player A, Player B) Setup()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        Player a = TestWorld.CreatePlayer(1, 0, 0, new NullPlayerSession(1));
        world.AddPlayer(a);
        Player b = TestWorld.CreatePlayer(2, 10, 0, new NullPlayerSession(2));
        world.AddPlayer(b);
        return (world, map, a, b);
    }

    private static void MoveBoth(WorldRuntime world, Player a, Player b, float bx)
    {
        a.SetPosition(a.X + 1f, a.Y, a.Z, 0f);
        b.SetPosition(bx, b.Y, b.Z, 0f);
        a.NeedsVisibilityUpdate = true;
        b.NeedsVisibilityUpdate = true;
        world.RunTick(50);
    }

    [Fact]
    public void TwoMovingPlayersInRange_EachOrderedPairIsEvaluatedOnce()
    {
        (WorldRuntime world, Map map, Player a, Player b) = Setup();
        using (world)
        {
            long evaluations = map.VisibilityPairEvaluations;
            long skipped = map.VisibilityPairDuplicatesSkipped;
            MoveBoth(world, a, b, 11f);

            long performed = (map.VisibilityPairEvaluations - evaluations) - (map.VisibilityPairDuplicatesSkipped - skipped);
            Assert.Equal(2, performed);
            Assert.True(map.VisibilityPairDuplicatesSkipped - skipped >= 2);
            Assert.Contains(b.Guid, a.VisibleObjects);
            Assert.Contains(a.Guid, b.VisibleObjects);
        }
    }

    [Fact]
    public void ReEnteringRange_CreatesEachOtherExactlyOnce()
    {
        (WorldRuntime world, Map map, Player a, Player b) = Setup();
        using (world)
        {
            MoveBoth(world, a, b, 5000f);
            Assert.DoesNotContain(b.Guid, a.VisibleObjects);
            Assert.DoesNotContain(a.Guid, b.VisibleObjects);

            int aVersion = a.VisibilityVersion;
            int bVersion = b.VisibilityVersion;
            MoveBoth(world, a, b, 12f);

            Assert.Contains(b.Guid, a.VisibleObjects);
            Assert.Contains(a.Guid, b.VisibleObjects);
            Assert.Equal(aVersion + 1, a.VisibilityVersion);
            Assert.Equal(bVersion + 1, b.VisibilityVersion);
        }
    }

    [Fact]
    public void AnAsymmetricRule_StillAnswersEachDirectionSeparately()
    {
        (WorldRuntime world, Map map, Player a, Player b) = Setup();
        using (world)
        {
            map.AddVisibilityRule(new HidesFrom(a, b));
            MoveBoth(world, a, b, 5000f);
            MoveBoth(world, a, b, 12f);

            Assert.DoesNotContain(b.Guid, a.VisibleObjects); // A is blind to B
            Assert.Contains(a.Guid, b.VisibleObjects);       // B still sees A
        }
    }

    [Fact]
    public void EvaluationsOutsideTheOutputStage_AreNeverDeduplicated()
    {
        (WorldRuntime world, Map map, Player a, Player b) = Setup();
        using (world)
        {
            long skipped = map.VisibilityPairDuplicatesSkipped;
            map.RefreshVisibility(a);
            map.RefreshVisibility(a);
            map.RefreshVisibility(b);
            Assert.Equal(skipped, map.VisibilityPairDuplicatesSkipped);
        }
    }
}
