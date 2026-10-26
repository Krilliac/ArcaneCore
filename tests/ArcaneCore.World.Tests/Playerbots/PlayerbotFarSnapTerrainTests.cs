using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Issue #1497 on the real terrain (<c>ARCANECORE_TEST_TERRAIN_DIR</c>): the five live goals of bot-nav/no-path-connected (map 1, the bot's
/// component 12764), each with the position the bot stood at, and what the walking lookup and IsUnreachableOnFoot say of the goal now.
/// </summary>
public sealed class PlayerbotFarSnapTerrainTests : RealTerrainNavigationTestBase
{
    public PlayerbotFarSnapTerrainTests(ITestOutputHelper output) : base(output) { }

    private static readonly (Vector3 Bot, Vector3 Goal)[] Samples =
    [
        (new(6866.1f, -2803.9f, 582.5f), new(6887.7f, -2824.8f, 582.5f)),
        (new(4044.9f, -1006.6f, 256.1f), new(4043.0f, -1010.2f, 263.3f)),
        (new(6786.3f, -4427.5f, 732.8f), new(6783.5f, -4429.1f, 737.3f)),
        (new(-2904.0f, 87.7f, 108.1f), new(-2908.1f, 83.6f, 119.6f)),
        (new(-3173.5f, -40.2f, 58.1f), new(-3175.5f, -38.1f, 64.5f)),
    ];

    /// <summary>
    /// All five goals are far, in the bot's own component, and give no verdict (goal component -1), so no refused plan toward them reads
    /// as no-path-connected. Four are a vertical miss; the third (6783.5,-4429.1,737.3) lies on a steep slope beside the walkable polygon the bot
    /// stands on, so its walking hit is 3.2 yd off horizontally while a lookup with nothing excluded finds the slope 0.17 yd away.
    /// </summary>
    [RealTerrainBotFact]
    public async Task TheLiveGoals_OfIssue1497_AreReportedFar_AndGiveNoVerdict()
    {
        var locate = new PathOptions { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, LoadTiles = true, NonBlockingTiles = true };
        for (int sample = 0; sample < Samples.Length; sample++)
        {
            (Vector3 bot, Vector3 goal) = Samples[sample];
            await using Terrain terrain = await Terrain.StartAsync();
            await terrain.PlaceAsync(bot, 1);
            Task<NavRegionGraph?> build = await terrain.Host.OnWorldAsync(() =>
                ((NavMeshPathfinder)terrain.Player.Map!.Collision.Pathfinder).RegionGraphTask(1, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!);
            Assert.NotNull(await build);
            var seen = await terrain.Host.OnWorldAsync(() =>
            {
                var nav = (NavMeshPathfinder)terrain.Player.Map!.Collision.Pathfinder;
                NavRegionGraph graph = nav.RegionGraph(1, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!;
                int from = nav.RegionAt(graph, bot, locate), to = nav.RegionAt(graph, goal, locate, out bool far);
                bool unreachable = PlayerbotNavigation.IsUnreachableOnFoot(terrain.Player, goal, out int fromComponent, out int toComponent);
                return (from, to, far, unreachable, fromComponent, toComponent, own: graph.ComponentOf(from), goalComponent: graph.ComponentOf(to));
            });
            output.WriteLine($"goal {goal} (bot {bot}): bot region {seen.from} comp {seen.own}, goal region {seen.to} comp {seen.goalComponent} far={seen.far}; "
                + $"IsUnreachableOnFoot={seen.unreachable} (components {seen.fromComponent}, {seen.toComponent})");
            Assert.True(seen.far, $"sample {sample} is a vertical miss");
            Assert.Equal(seen.own, seen.goalComponent);
            Assert.False(seen.unreachable);
            Assert.Equal(-1, seen.toComponent);
        }
    }
}
