using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Quests;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Npc;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed partial class PlayerbotQuestReturnTests
{
    [Fact]
    public async Task CompletedQuestWithOneEnder_UnsafeSteepAscentIsSetAsideOnFirstPlan()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true);
        await fixture.AcceptAndCompleteAsync();
        await fixture.Host.World.InvokeAsync(() =>
        {
            var player = fixture.Session.Player!;
            var giver = (Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            giver.SetPosition(player.X + 150, player.Y, player.Z, 0);
            WorldCollision.Of(fixture.Host.World).Install(lineOfSight: new FlatFloor(player.Z), pathfinder: new UnsafeSteepRoute());
            return true;
        });
        await fixture.Host.WaitForWorldAsync(() =>
            !fixture.Session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "offscreen ender");

        var brain = new PlayerbotBrain(fixture.Session, new PlayerbotOptions { Enabled = true });
        try
        {
            await fixture.Host.World.InvokeAsync(() =>
            {
                var player = fixture.Session.Player!;
                Assert.Equal(QuestInteractionFixture.QuestId, new PlayerbotQuestGoals(fixture.Session,
                    new PlayerbotOptions { Enabled = true }).CompletedReturnQuest(player));
                fixture.Session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);
                Assert.Equal(QuestStatus.Complete, fixture.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)!.Status);
                Assert.True(brain.Suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId, fixture.Host.World.NowMs));
                Assert.True(brain.Suspensions.IsEntrySuspended(QuestInteractionFixture.Entry, fixture.Host.World.NowMs));
                Assert.Equal(0u, new PlayerbotQuestGoals(fixture.Session, new PlayerbotOptions { Enabled = true })
                    { Suspensions = brain.Suspensions }.CompletedReturnQuest(player));
                return true;
            });
        }
        finally { brain.Stop(); }
    }

    private sealed class UnsafeSteepRoute : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => ((options?.ExcludeFlags ?? NavTerrain.Empty) & NavTerrain.SteepSlopes) != 0
                ? new PathResult(PathType.Incomplete, [start, start])
                : new PathResult(PathType.Normal, [start, start + new Vector3(5, 0, 23.5f), end]);
    }
}
