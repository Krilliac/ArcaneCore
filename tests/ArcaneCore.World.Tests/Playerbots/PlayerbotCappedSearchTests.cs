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
    /// A real fragment (the bot's own search ran out of corridor, not of nodes) whose fallbacks then search towards the far goal and run
    /// out of nodes: the first such refusal ends each fallback, since the next corner or start beside the bot, a few yards on, searches the
    /// same ground. One plan: the bot's search, the steep escape's search, the walkable check of its corner, that corner's onward search
    /// and the first start beside the bot; before, every start beside it was searched.
    /// </summary>
    [Fact]
    public async Task OnAFragment_FallbacksThatRunOutOfNodes_StopAtTheFirst()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var origin = new Vector3(player.X, player.Y, player.Z);
            var fragment = new CappedEverywhere { CappedFrom = 2 }; // the first query (the bot's own) is not capped, every later one is
            WorldCollision.Of(world.Host.World).Install(pathfinder: fragment);
            var options = new PlayerbotOptions { MaxPathPoints = 128, MaxRouteYards = 2000 };

            Assert.False(PlayerbotNavigation.TryPlanToward(player, origin + new Vector3(1800, 0, 0), options, out _));
            Assert.Equal(5, fragment.Queries);
            return true;
        });
    }

    /// <summary>
    /// The steep escape's corner loop (TryLeaveSteepGround): the way off the slope passes several walkable corners, and the ordinary
    /// search onward from each of them runs out of nodes short of the far goal. The first such refusal ends the loop: the next corner,
    /// a few yards on, searches the same ground. Exactly one onward search; before, every corner searched (four here).
    /// </summary>
    [Fact]
    public async Task SteepEscape_OnwardSearchesThatRunOutOfNodes_StopAtTheFirstCorner()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var origin = new Vector3(player.X, player.Y, player.Z);
            var slope = new MultiCornerSlope(origin);
            WorldCollision.Of(world.Host.World).Install(pathfinder: slope);
            var options = new PlayerbotOptions { MaxPathPoints = 128, MaxRouteYards = 2000 };

            Assert.False(PlayerbotNavigation.TryPlanToward(player, origin + new Vector3(1800, 0, 0), options, out _));
            Assert.Equal(1, slope.SteepQueries); // the escape ran (the bot's own search was no capped one)
            Assert.Equal(1, slope.OnwardQueries);
            return true;
        });
    }

    /// <summary>
    /// The bot's own search (steep slopes excluded, from where it stands) ends three yards beside it, not capped: a patch, so the
    /// fallbacks run. With steep slopes allowed the way runs down past four walkable corners. Every other steep-excluding search is
    /// capped and ends three yards beside its start, no closer to its end.
    /// </summary>
    private sealed class MultiCornerSlope(Vector3 origin) : IPathfinder
    {
        private readonly Vector3[] _corners =
        [
            origin + new Vector3(1.2f, 0.3f, -0.5f), origin + new Vector3(3.1f, 0.7f, -1f),
            origin + new Vector3(3.8f, -1.1f, -1.5f), origin + new Vector3(6.3f, 0.4f, -2f),
        ];

        private int _ownDone;

        public int SteepQueries { get; private set; }

        public int OnwardQueries { get; private set; }

        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            options ??= PathOptions.Default;
            if (start == end)
            {
                PathSearchCounters.RecordSearch(1, 1, capped: false);
                return new PathResult(PathType.Normal, [start, end]); // every corner is walkable ground
            }

            if ((options.ExcludeFlags & NavTerrain.SteepSlopes) == 0)
            {
                SteepQueries++;
                PathSearchCounters.RecordSearch(64, 64, capped: false);
                return new PathResult(PathType.Incomplete, [start, .. _corners]); // down the slope, short of the far goal
            }

            bool own = _ownDone++ == 0; // the first steep-excluding search is the bot's own
            if (_corners.Contains(start)) OnwardQueries++;
            PathSearchCounters.RecordSearch(own ? 100 : options.MaxSearchNodes, own ? 100 : options.MaxSearchNodes, capped: !own);
            Vector3 flat = new(end.X - start.X, end.Y - start.Y, 0);
            Vector3 toward = flat.Length() < 0.01f ? Vector3.UnitX : Vector3.Normalize(flat);
            return new PathResult(PathType.Incomplete, [start, start + (new Vector3(-toward.Y, toward.X, 0) * 3f)]);
        }
    }

    /// <summary>
    /// A mesh on which every query ends three yards beside its start, short of its end and no closer to it (a partial answer: the
    /// visited polygon nearest the goal, beside the bot), each a search that used <see cref="NodesPerQuery"/> of its node bound and, when
    /// <see cref="Capped"/>, hit it (PathSearchCounters, as NavMeshQuery records it).
    /// </summary>
    internal sealed class CappedEverywhere : IPathfinder
    {
        public bool Capped { get; init; } = true;

        /// <summary>When set, only the queries from this one on (1-based) are capped, whatever <see cref="Capped"/> says.</summary>
        public int? CappedFrom { get; init; }

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
            PathSearchCounters.RecordSearch(nodes, nodes, CappedFrom is { } from ? Queries >= from : Capped);
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
