using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A bot's plan on the world thread never waits for a navigation tile file: a tile that is not resident is read on the pool, the plan
/// answers "pending" (not a refusal: <see cref="PlayerbotNavigation.PendingSince"/>) and succeeds once the tile is installed.
/// </summary>
public sealed class PlayerbotTilePendingTests : IDisposable
{
    private readonly NavMeshFixture _fixture = new();
    private readonly ManualResetEventSlim _gate = new();
    private bool _armed;

    public void Dispose()
    {
        _gate.Set();
        _gate.Dispose();
        _fixture.Dispose();
    }

    private static readonly PlayerbotOptions Options = new() { MaxRouteYards = 30, MaxPathPoints = 22 };

    [Fact]
    public async Task APlanNeedingAnUnreadTile_IsPendingNotRefused_AndSucceedsOnceTheTileIsIn()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, [],
            unloadedTiles: [(31, 31, new CellTile(0, 0, 0, 0, 8, 8, Size: 5))],
            configure: nav => nav.FileReader = path =>
            {
                if (Volatile.Read(ref _armed) && path.EndsWith(".mmtile", StringComparison.Ordinal)) _gate.Wait();
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            });
        await world.PlaceAsync(new Vector3(5, 5, 0)); // the grid it lands in loads the tile (not gated yet)
        await world.Host.OnWorldAsync(() => world.Nav.UnloadTile(0, 31, 31)); // so that the plan has to bring it in
        Volatile.Write(ref _armed, true);
        Vector3 goal = new(30, 5, 0);

        Task<(bool Planned, bool Pending)> plan = world.Host.OnWorldAsync(() =>
        {
            long mark = PlayerbotNavigation.PendingMark;
            bool planned = PlayerbotNavigation.TryPlanToward(world.Player, goal, Options, out _);
            return (planned, PlayerbotNavigation.PendingSince(mark));
        });
        Assert.Same(plan, await Task.WhenAny(plan, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.Equal((false, true), await plan);

        _gate.Set();
        await world.Nav.PendingTileRead(0, 31, 31)!;
        (bool Planned, bool Pending) done = await world.Host.OnWorldAsync(() =>
        {
            long mark = PlayerbotNavigation.PendingMark;
            bool planned = PlayerbotNavigation.TryPlanToward(world.Player, goal, Options, out PlayerbotRoute? route);
            return (planned && route is { Navigated: true }, PlayerbotNavigation.PendingSince(mark));
        });
        Assert.Equal((true, false), done);
    }

    [Fact]
    public void TheThinkLine_ReportsPendingTiles()
    {
        var probe = new PlayerbotThinkProbe();
        probe.Begin();
        ArcaneCore.Game.Maps.Collision.PathSearchCounters.RecordTilePending();
        probe.End();
        Assert.Contains("tiles pending 1", probe.Describe());
    }
}
