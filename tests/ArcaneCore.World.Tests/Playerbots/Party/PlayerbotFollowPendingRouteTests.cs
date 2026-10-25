using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// A follower behind a moving leader re-plans on nearly every think. When that re-plan is only pending (a tile still being read, or a path
/// job), the follower must keep walking the route it planned a moment ago instead of stopping and discarding it.
/// </summary>
public sealed class PlayerbotFollowPendingRouteTests
{
    private static bool Forward(Player player) => player.Movement.HasFlag(MovementFlags.Forward);

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

    /// <summary>The straight line, or, while <see cref="PendingNext"/> is set, the answer of a query whose tile is still being read.</summary>
    private sealed class PendingPathfinder : IPathfinder
    {
        public bool Enabled => true;

        public bool PendingNext { get; set; }

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            if (!PendingNext) return PathResult.StraightLine(start, end, PathType.Normal);
            PathSearchCounters.RecordTilePending();
            return new PathResult(PathType.TilesPending, [start]);
        }
    }

    [Fact]
    public async Task AFollowerWhoseReplanIsPending_KeepsWalkingTheRouteItPlannedBefore()
    {
        await using WorldTestHost host = PartyTestHost.Start(o =>
        {
            o.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone;
            o.Follow.CatchUpMode = PlayerbotCatchUpMode.FightThrough;
        });
        await using WorldTestClient master = await host.EnterWorldAsync("PENDFOLM", "Pendfolm");
        WorldSession session = await PartyTestHost.EnterManagedAsync(host, "PENDFOLB", "Pendfolb");
        try
        {
            PlayerbotOptions options = PartyTestHost.PlayerbotOptionsOf(host);
            var ai = new PlayerbotPartyAI(session, options);
            var nav = new PendingPathfinder();
            uint decided = (uint)options.ThinkIntervalMs + 400u;
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: nav);
                Player bot = session.Player!;
                Player owner = host.World.FindOnlinePlayer("Pendfolm")!;
                GroupManager groups = host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;
                groups.Invite(owner, bot.Name);
                groups.Accept(bot);
                Assert.True(ai.Drives(bot));

                owner.Relocate(bot.X + 60f, bot.Y, bot.Z, 0f, host.World.NowMs);
                session.ManagedBudget = new ManagedActionBudget(8);
                ai.Update(bot, decided);
                Assert.True(Forward(bot), "the follower did not start walking to a leader 60 yards away");
                Assert.True(ai.Follower.Walking);

                // The leader runs on 5 yards; the follower's re-plan is pending.
                owner.Relocate(owner.X + 5f, owner.Y, owner.Z, 0f, host.World.NowMs);
                nav.PendingNext = true;
                session.ManagedBudget = new ManagedActionBudget(8);
                ai.Update(bot, decided);
                Assert.True(Forward(bot), "a pending re-plan stopped the follower");
                Assert.True(ai.Follower.Walking);
                return true;
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }
}
