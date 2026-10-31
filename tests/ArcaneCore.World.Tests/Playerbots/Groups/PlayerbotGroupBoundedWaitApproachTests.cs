using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Approach points the leader makes no progress toward, and snapping approach points to the mesh. Split from the former <c>PlayerbotGroupBoundedWaitTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotGroupBoundedWaitTestBase"/>.
/// </summary>
public sealed class PlayerbotGroupBoundedWaitApproachTests : PlayerbotGroupBoundedWaitTestBase
{
    // --- B3: the approach point ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AnApproachPointTheLeaderMakesNoProgressTo_CountsAsReached()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
        });
        await world.AddBotAsync("Approachwar", Human, Warrior, 1, []);
        await world.AddBotAsync("Approachmage", Human, Mage, 1, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State == PlayerbotGroupState.Travelling)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        uint start = await world.OnWorldAsync(() =>
        {
            // The leader plans its way, but something holds it where it stands (a root, a snare, a wall the mesh does not show).
            world.Player(group.LeaderBotId).AddMovementFlags(ArcaneCore.Protocol.MovementFlags.Root);
            return NowMs(world);
        });

        await world.RunAsync(35_000);
        Assert.Equal(PlayerbotGroupState.Travelling, group.State);

        Assert.True(await world.RunUntilAsync(60_000, () => group.State != PlayerbotGroupState.Travelling), await world.TraceAsync());
        Assert.Equal(PlayerbotGroupState.Engaging, group.State);
        await world.OnWorldAsync(() =>
        {
            Player leader = world.Player(group.LeaderBotId);
            Assert.True(Vector3.Distance(new Vector3(leader.X, leader.Y, leader.Z), group.Approach) > 20f, "the leader never got near the approach point");
            Assert.True(unchecked(NowMs(world) - start) >= 40_000u);
            return true;
        });
    }

    [Fact]
    public void AnApproachPointOffTheMesh_IsPulledToTheLastPointOfTheMeshOnTheWayToIt()
    {
        var from = new Vector3(0, 0, 10);
        var offMesh = new Vector3(100, 0, 10);
        var stop = new Vector3(60, 0, 12.5f);

        Assert.Equal(stop, PlayerbotGroupCoordinator.SnapToMesh(new MeshEndsAt(stop), 0, from, offMesh));
        // No mesh query, or a mesh with no answer for this place: the point stays.
        Assert.Equal(offMesh, PlayerbotGroupCoordinator.SnapToMesh(new NoMeshQuery(), 0, from, offMesh));
        Assert.Equal(offMesh, PlayerbotGroupCoordinator.SnapToMesh(new MeshUnavailable(), 0, from, offMesh));
        Assert.Equal(offMesh, PlayerbotGroupCoordinator.SnapToMesh(null, 0, from, offMesh));
    }

    [Fact]
    public async Task TheApproachPointOfANewGroup_IsSnappedToTheMesh()
    {
        var stop = new Vector3(StartX + 5f, StartY + 20f, StartZ);
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options => options.Risk.Tolerance = 0.25f);
        await world.OnWorldAsync(() =>
        {
            WorldCollision.Of(world.World.Host.World).Install(pathfinder: new MeshEndsAt(stop));
            return true;
        });
        await world.AddBotAsync("Snapwar", Human, Warrior, 1, []);
        await world.AddBotAsync("Snapmage", Human, Mage, 1, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Count == 1), await world.TraceAsync());
        Assert.Equal(stop, world.Coordinator.Groups.Single().Approach);
    }

    // --- B4: a quest group that can plan no way gives up as a run does -----------------------------------------------

    [Fact]
    public async Task AQuestGroupThatCanPlanNoWayToItsApproachPoint_GivesUpAfterThirtySeconds()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([BruteQuest], options =>
        {
            options.Risk.Tolerance = 0.25f;
            options.StallSeconds = 600;
        });
        await world.OnWorldAsync(() =>
        {
            Block(world);
            return true;
        });
        await world.AddBotAsync("Noapwar", Human, Warrior, 1, []);
        await world.AddBotAsync("Noapmage", Human, Mage, 1, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Count == 1), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();

        Assert.True(await world.RunUntilAsync(120_000, () => group.Reason is not null), await world.TraceAsync());
        Assert.Equal("no-path-to-approach", group.Reason);
        Assert.False(group.Succeeded);
    }
}
