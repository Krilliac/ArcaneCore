using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
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
/// A world with one managed bot on map 0 whose pathfinder is a repository-authored mesh (<see cref="NavMeshFixture"/>; every tile loaded,
/// the region graph built) and, optionally, a stand-in line-of-sight service (collision models: which places are indoors). Manual clock.
/// </summary>
internal sealed class SyntheticNavWorld : IAsyncDisposable
{
    private SyntheticNavWorld(WorldTestHost host, WorldSession session)
    {
        Host = host;
        Session = session;
    }

    public WorldTestHost Host { get; }

    public WorldSession Session { get; }

    public Player Player => Session.Player!;

    /// <summary>Write <paramref name="tiles"/> (file coordinates and tile) for map 0 and start the world on them.</summary>
    public static async Task<SyntheticNavWorld> StartAsync(NavMeshFixture fixture, (int X, int Y, CellTile Tile)[] tiles, ILineOfSight? lineOfSight = null)
    {
        fixture.WriteParams(0);
        foreach ((int x, int y, CellTile tile) in tiles)
            fixture.WriteTile(0, x, y, tile);
        var nav = new NavMeshPathfinder(fixture.Directory);
        foreach ((int x, int y, _) in tiles)
            Assert.True(nav.LoadTile(0, x, y));
        Assert.NotNull(await nav.RegionGraphTask(0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!);

        WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClock>());
        await host.OnWorldAsync(() => WorldCollision.Of(host.World).Install(lineOfSight: lineOfSight, pathfinder: nav));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        return new SyntheticNavWorld(host, session);
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
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}

/// <summary>
/// A line-of-sight stand-in with no models in the way, no model floors, and a WMO group (indoors, no exterior flag) wherever
/// <paramref name="indoors"/> says so: what <see cref="MapCollision.IsOutdoors"/> reads.
/// </summary>
internal sealed class IndoorsWhere(Func<Vector3, bool> indoors) : ILineOfSight
{
    public bool Enabled => true;

    public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

    public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
    {
        hit = to;
        return false;
    }

    public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => null;

    public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
    {
        info = new ModelAreaInfo(0, 0, 1, 1, z);
        return indoors(new Vector3(x, y, z));
    }
}
