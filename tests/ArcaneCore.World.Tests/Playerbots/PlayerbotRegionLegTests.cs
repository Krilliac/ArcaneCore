using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Tests.Collision;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The leg <see cref="PlayerbotNavigation.TryPlanToward"/> chooses towards a far goal on a repository-authored mesh
/// (<see cref="NavMeshFixture"/>, the layout of <c>NavRegionGraphTests</c>): four 40-yard tiles, the bot in <c>A</c> (world X 0–40,
/// Y 0–40), the goal in <c>B</c> east of it, whose western 30 yards are a hole; the way goes north through <c>C</c>, east through <c>D</c>
/// and south into <c>B</c>. Routes are bounded to 30 yards (<see cref="PlayerbotOptions.MaxRouteYards"/>) and 22 corners, so the goal is
/// beyond one route from everywhere in <c>A</c>, as Grish Longrunner and Kargath are beyond 2,000 yards from Stonetalon and the swamp.
/// </summary>
public sealed class PlayerbotRegionLegTests : IDisposable
{
    private readonly NavMeshFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static readonly Vector3 Start = new(5, 5, 0), Goal = new(75, 5, 0);

    private static readonly PlayerbotOptions Options = new() { MaxRouteYards = 30, MaxPathPoints = 22 };

    /// <summary>
    /// Each leg goes to a portal of the region graph's way (north first, never into the hole) and the bot reaches the goal. Before, the
    /// next straight-line point 20 yards east of the hole's edge lay off the mesh: no route, and the bot stood at the edge for good.
    /// </summary>
    [Fact]
    public async Task AFarGoalBehindADeadEnd_IsReachedLegByLeg_RoundIt()
    {
        await using Synthetic world = await Synthetic.StartAsync(_fixture);
        await world.PlaceAsync(Start);
        var ends = new List<Vector3>();
        for (int leg = 0; leg < 12 && (ends.Count == 0 || Vector3.Distance(ends[^1], Goal) > 0.5f); leg++)
        {
            PlayerbotRoute? route = await world.Host.OnWorldAsync(() =>
                PlayerbotNavigation.TryPlanToward(world.Player, Goal, Options, out PlayerbotRoute? planned) ? planned : null);
            Assert.True(route is not null, $"no route towards the goal after legs ending {string.Join(" ", ends)}");
            Assert.True(route!.Distance <= Options.MaxRouteYards + 0.01f, $"a {route.Distance:F1}-yard route");
            ends.Add(route.Points[^1]);
            Assert.False(ends[^1].X is > 40 and < 70 && ends[^1].Y < 40, $"a leg ends in the hole at {ends[^1]}");
            await world.PlaceAsync(ends[^1]);
        }

        Assert.True(Vector3.Distance(ends[^1], Goal) <= 0.5f, $"the legs end {string.Join(" ", ends)}");
        Assert.True(ends[0].Y - Start.Y > ends[0].X - Start.X, $"the first leg ends at {ends[0]}, not north towards C");
        Assert.Contains(ends, end => end.Y > 40f && end.X > 40f); // through D
    }

    /// <summary>
    /// The leg ends at the farthest point of the way within half a route (<see cref="PlayerbotNavigation.RegionLegYards"/> at most),
    /// measured along it: from <c>C</c> the portal into <c>D</c> is 28.3 yards on, the one into <c>B</c> 68.3 and the goal 102.3.
    /// </summary>
    [Theory]
    [InlineData(120f, 41f, 60f)] // half a route 60: the portal into D
    [InlineData(140f, 75f, 39f)] // 70: the portal into B
    [InlineData(220f, 75f, 5f)] // 110: the goal itself
    public async Task ALeg_EndsAtTheFarthestPointOfTheWay_WithinHalfARoute(float maxRouteYards, float x, float y)
    {
        await using Synthetic world = await Synthetic.StartAsync(_fixture);
        await world.PlaceAsync(new Vector3(20, 41, 0));
        var options = new PlayerbotOptions { MaxRouteYards = maxRouteYards, MaxPathPoints = 22 };
        PlayerbotRoute? route = await world.Host.OnWorldAsync(() =>
            PlayerbotNavigation.TryPlanToward(world.Player, Goal, options, out PlayerbotRoute? planned) ? planned : null);

        Assert.NotNull(route);
        Vector3 end = route!.Points[^1];
        Assert.True(Vector2.Distance(new(end.X, end.Y), new(x, y)) < 0.5f, $"the leg ends at {end}");
    }

    /// <summary>A world with one managed bot on map 0, the fixture's mesh as its pathfinder and the region graph built.</summary>
    private sealed class Synthetic : IAsyncDisposable
    {
        private Synthetic(WorldTestHost host, WorldSession session)
        {
            Host = host;
            Session = session;
        }

        public WorldTestHost Host { get; }

        public WorldSession Session { get; }

        public Game.Entities.Player Player => Session.Player!;

        public static async Task<Synthetic> StartAsync(NavMeshFixture fixture)
        {
            // The layout of NavRegionGraphTests: Detour tile (x, y) covers world Y from 40x and world X from 40y.
            fixture.WriteParams(0);
            fixture.WriteTile(0, 10, 10, new CellTile(0, 0, 0, 0, 8, 8, Size: 5) { LinkedSides = [0, 2] });
            fixture.WriteTile(0, 10, 11, new CellTile(0, 1, 40, 0, 8, 8, Size: 5) { LinkedSides = [0, 6], Walkable = (i, _) => i >= 6 });
            fixture.WriteTile(0, 11, 10, new CellTile(1, 0, 0, 40, 8, 8, Size: 5) { LinkedSides = [2, 4] });
            fixture.WriteTile(0, 11, 11, new CellTile(1, 1, 40, 40, 8, 8, Size: 5) { LinkedSides = [4, 6] });
            var nav = new NavMeshPathfinder(fixture.Directory);
            foreach ((int x, int y) in (ValueTuple<int, int>[])[(10, 10), (10, 11), (11, 10), (11, 11)])
                Assert.True(nav.LoadTile(0, x, y));
            Assert.NotNull(await nav.RegionGraphTask(0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!);

            WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClock>());
            await host.OnWorldAsync(() => WorldCollision.Of(host.World).Install(pathfinder: nav));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            return new Synthetic(host, session);
        }

        public async Task PlaceAsync(Vector3 at)
        {
            var teleports = Host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
            Assert.True(await Host.OnWorldAsync(() => teleports.TeleportTo(Player, 0, at.X, at.Y, at.Z, 0)), "teleport refused");
            for (int tick = 0; tick < 200; tick++)
            {
                bool there = await Host.OnWorldAsync(() =>
                {
                    PlayerbotMovementControl.Update(Session, Player);
                    return Player.IsInWorld && teleports.StageOf(Player) is null && Player.MapId == 0
                        && Vector2.Distance(new(Player.X, Player.Y), new(at.X, at.Y)) < 0.1f;
                });
                if (there) return;
                await Host.World.AdvanceClockAsync(50);
            }

            throw new TimeoutException($"the bot did not arrive at {at}");
        }

        public async ValueTask DisposeAsync()
        {
            Session.Kick();
            await Session.ManagedClosed;
            await Host.DisposeAsync();
        }

        private sealed class ManualClock : IWorldFeature
        {
            public void Attach(Game.Maps.WorldRuntime world) => world.UseManualClock();
        }
    }
}
