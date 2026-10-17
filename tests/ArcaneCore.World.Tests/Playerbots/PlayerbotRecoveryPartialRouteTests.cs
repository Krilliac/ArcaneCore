using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The corpse run refuses the mesh's partial answer to a query for the body itself when that answer ends out of vertical reach of it
/// (<see cref="PlayerbotRecovery.LeavesGoalOutOfVerticalReach"/>), and only that: a leg cut on purpose short of a far body (region leg,
/// straight-line chunk, hop) is walked. No terrain data.
/// </summary>
public sealed class PlayerbotRecoveryPartialRouteTests
{
    private static PlayerbotRoute Route(Vector3 start, Vector3 end, bool partialToGoal)
        => new([start, end], Vector3.Distance(start, end), navigated: true) { MeshEndedShort = partialToGoal, PartialToGoal = partialToGoal };

    [Fact]
    public void APartialRouteToTheBody_EndingFarBelowIt_IsRefused()
    {
        var body = new Vector3(1000f, 0f, 438f);
        Assert.True(PlayerbotRecovery.LeavesGoalOutOfVerticalReach(Route(new(0, 0, 12f), new(230f, 0f, 0f), partialToGoal: true), body));
    }

    [Fact]
    public void ThatSameRoute_IsKept_WhenItIsNotThePartialAnswerToTheBody()
    {
        var body = new Vector3(1000f, 0f, 438f);
        Assert.False(PlayerbotRecovery.LeavesGoalOutOfVerticalReach(Route(new(0, 0, 12f), new(230f, 0f, 0f), partialToGoal: false), body));
    }

    [Fact]
    public void APartialRoute_ThatNarrowsTheVerticalGap_OrEndsWithinReach_IsKept()
    {
        var body = new Vector3(1000f, 0f, 438f);
        Assert.False(PlayerbotRecovery.LeavesGoalOutOfVerticalReach(Route(new(0, 0, 12f), new(230f, 0f, 200f), partialToGoal: true), body));
        Assert.False(PlayerbotRecovery.LeavesGoalOutOfVerticalReach(Route(new(0, 0, 12f), new(980f, 0f, 430f), partialToGoal: true), body));
        Assert.False(PlayerbotRecovery.LeavesGoalOutOfVerticalReach(Route(new(0, 0, 12f), new(230f, 0f, 420f), partialToGoal: true), body));
    }

    /// <summary>The mesh answers each query by <paramref name="answer"/>; the world's line of sight and floor are flat.</summary>
    private sealed class Scripted(Func<Vector3, Vector3, PathResult> answer) : IPathfinder
    {
        public int Queries { get; private set; }
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Queries++;
            return answer(start, end);
        }
    }

    private sealed class FlatFloor : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => 83.53f;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }

    private static async Task<(PlayerbotRecoveryStep Step, Vector3? RouteEnd, float Walked)> RunAsync(float bodyYards, float bodyUp, int maxRouteYards,
        Func<Vector3, Vector3, PathResult> answer)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            return await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new Scripted(answer));
                Player player = session.Player!;
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.True(player.Map!.Combat.RepopPlayer(player));
                Corpse corpse = player.Combat.Corpse!;
                corpse.SetPosition(player.X + bodyYards, player.Y, player.Z + bodyUp, 0);
                float startX = player.X;
                var recovery = new PlayerbotRecovery(session, new PlayerbotOptions { Enabled = true, MaxRouteYards = maxRouteYards, AllowedMaps = [0, 1] });
                for (int think = 0; think < 8; think++)
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    recovery.Update(player, 1000);
                    PlayerbotMotion.ElapseForTests(player, 1000);
                }

                return (recovery.LastStep, recovery.RouteEnd, player.X - startX);
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// A body 1,500 yards away and 80 up, past the route bound: the plan is a straight-line chunk the mesh answers whole and level. That
    /// leg ends short of the body on purpose and leaves it more than the reclaim radius above, yet it is a route, not a refusal; before
    /// the plan was told apart, three such legs gave the body up for the spirit healer.
    /// </summary>
    [Fact]
    public async Task ALevelChunkTowardAFarBodyAbove_IsWalked_NotRefused()
    {
        var run = await RunAsync(1500f, 80f, 1000, (start, end) => new(PathType.Normal, [start, new Vector3(end.X, end.Y, start.Z)]));
        Assert.Equal(PlayerbotRecoveryStep.WalkToCorpse, run.Step);
        Assert.True(run.Walked > 1f, $"walked {run.Walked}");
    }

    /// <summary>The mesh answers the query for the body itself with a level part of the way, 230 yards out, 438 below it: refused until the body is given up.</summary>
    [Fact]
    public async Task APartialAnswerForTheBodyItself_EndingFarBelowIt_IsRefused_AndTheBodyGivenUp()
    {
        var body = default(Vector3);
        var run = await RunAsync(1000f, 438f, 3000, (start, end) =>
        {
            body = end;
            Vector3 to = Vector3.Normalize(new Vector3(end.X - start.X, end.Y - start.Y, 0f));
            return new(PathType.Incomplete, [start, start + (to * 230f)]);
        });
        Assert.NotEqual(PlayerbotRecoveryStep.WalkToCorpse, run.Step);
        Assert.Null(run.RouteEnd);
        Assert.True(run.Walked < 20f, $"walked {run.Walked} toward {body}");
    }
}
