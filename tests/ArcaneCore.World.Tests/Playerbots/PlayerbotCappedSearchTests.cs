using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Risk;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A bot plan whose search ran out of nodes is refused after that one search (200-bot terrain benchmark, 2026-10-11: Coldridge Valley
/// bots planning to a quest destination in Stormwind, 1 800 yards away, thought for up to 557 ms, 41 searches of up to 32 768 nodes).
/// </summary>
public sealed class PlayerbotCappedSearchTests
{
    /// <summary>
    /// A capped search (the node bound ran out, PathSearchCounters) towards a far goal ends on the visited polygon nearest it, here
    /// three yards beside the bot. It used to be taken for a fragment of mesh or a patch among steep slopes, and the steep escape and the
    /// sixteen starts beside the bot ran, each another capped search: one plan, 18+ searches. Now it is refused after the one search.
    /// </summary>
    [Fact]
    public async Task ACappedSearchEndingBesideTheBot_IsRefused_WithoutTheFragmentFallbacks()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var origin = new Vector3(player.X, player.Y, player.Z);
            var capped = new CappedEverywhere();
            WorldCollision.Of(world.Host.World).Install(pathfinder: capped);
            var options = new PlayerbotOptions { MaxPathPoints = 128, MaxRouteYards = 2000 };

            Assert.False(PlayerbotNavigation.TryPlanToward(player, origin + new Vector3(1800, 0, 0), options, out _));
            Assert.Equal(1, capped.Queries);
            return true;
        });
    }

    /// <summary>
    /// The same answer without the cap (the corridor itself ended beside the bot: a real fragment) still tries the ways off it: the
    /// fallbacks are kept for what they were made for.
    /// </summary>
    [Fact]
    public async Task AnUncappedSearchEndingBesideTheBot_StillTriesTheFragmentFallbacks()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var origin = new Vector3(player.X, player.Y, player.Z);
            var fragment = new CappedEverywhere { Capped = false };
            WorldCollision.Of(world.Host.World).Install(pathfinder: fragment);
            var options = new PlayerbotOptions { MaxPathPoints = 128, MaxRouteYards = 2000 };

            Assert.False(PlayerbotNavigation.TryPlanToward(player, origin + new Vector3(1800, 0, 0), options, out _));
            Assert.True(fragment.Queries > 10, $"{fragment.Queries} queries: the fragment fallbacks did not run");
            return true;
        });
    }

    /// <summary>
    /// A mesh on which every query ends three yards beside its start, short of its end and no closer to it (a partial answer: the
    /// visited polygon nearest the goal, beside the bot), each a search that used <see cref="NodesPerQuery"/> of its node bound and, when
    /// <see cref="Capped"/>, hit it (PathSearchCounters, as NavMeshQuery records it).
    /// </summary>
    internal sealed class CappedEverywhere : IPathfinder
    {
        public bool Capped { get; init; } = true;

        public int NodesPerQuery { get; init; } = 512;

        public int Queries { get; private set; }

        public List<int> Budgets { get; } = [];

        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            options ??= PathOptions.Default;
            Queries++;
            Budgets.Add(options.MaxSearchNodes);
            int nodes = Math.Min(NodesPerQuery, options.MaxSearchNodes);
            PathSearchCounters.RecordSearch(nodes, nodes, Capped);
            if (start == end) return new PathResult(PathType.Normal, [start, end]); // OnWalkableMesh: the bot stands on the mesh
            Vector3 flat = new(end.X - start.X, end.Y - start.Y, 0);
            Vector3 toward = flat.Length() < 0.01f ? Vector3.UnitX : Vector3.Normalize(flat);
            Vector3 aside = new(-toward.Y, toward.X, 0); // the nearest visited polygon lies beside the start: no progress
            return options.AllowPartial
                ? new PathResult(PathType.Incomplete, [start, start + (aside * 3f)])
                : PathResult.StraightLine(start, end, PathType.NoPath);
        }
    }
}
