using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Transports;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Transports;
using ArcaneCore.World.Transports;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Ship;

/// <summary>
/// A world with the synthetic ships of <see cref="TransportWorldContent"/> (with the Booty Bay boat, entry 20808, on the crossing path: a port
/// on map 0 50 yards north of the human start, a port on map 1 at (1100, 0)), the manual clock, a flat floor at the start's height on every
/// map (<see cref="FlatFloor"/>), straight-line paths (<see cref="OpenPathfinder"/>) and one real managed bot session (a warrior of
/// <paramref name="race"/>: 1 human, 2 orc). Time moves only through <see cref="AdvanceAsync"/>; the motion and the worldport
/// acknowledgement run as the managed-bot feature runs them (<see cref="PlayerbotMotion.Pump"/> every 50 ms tick).
/// </summary>
internal sealed class ShipTestWorld : IAsyncDisposable
{
    /// <summary>The crossing's port on map 0 and on map 1 (the TaxiPathNode stop rows of <see cref="TransportWorldContent.Paths"/>).</summary>
    public static readonly TravelPlace PortOnMap0 = new(0, new Vector3(TransportWorldContent.StartX, TransportWorldContent.CrossingY, TransportWorldContent.StartZ));

    public static readonly TravelPlace PortOnMap1 = new(1, new Vector3(TransportWorldContent.FarX, 0f, TransportWorldContent.StartZ));

    /// <summary>Where the bot is put on map 0 before a leg: 67 yards from the port, so it walks to the dock.</summary>
    public static readonly Vector3 Start = new(TransportWorldContent.StartX - 60f, TransportWorldContent.CrossingY - 30f, TransportWorldContent.StartZ);

    private ShipTestWorld(WorldTestHost host, WorldSession session, TransportSystem system)
    {
        Host = host;
        Session = session;
        System = system;
    }

    public WorldTestHost Host { get; }

    public WorldSession Session { get; }

    public TransportSystem System { get; }

    public Player Player => Session.Player!;

    public TeleportService Teleports => Host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;

    public static async Task<ShipTestWorld> StartAsync(byte race = 1)
    {
        WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            TransportWorldContent.Register(services, withBootyBayBoat: true);
            services.AddSingleton<IWorldFeature, ManualClockFeature>();
        });
        TransportFeature feature = host.WorldServices.GetRequiredService<TransportFeature>();
        await host.WaitForWorldAsync(() => feature.System is { Ships.Count: 3 }, "the ferry, the crossing and the Booty Bay boat sail");
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host, race);
        await host.OnWorldAsync(() => WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder()));
        return new ShipTestWorld(host, session, await host.OnWorldAsync(() => feature.System!));
    }

    public Task AdvanceAsync(uint milliseconds) => Host.World.AdvanceClockAsync(milliseconds);

    public Task<T> OnWorldAsync<T>(Func<T> action) => Host.OnWorldAsync(action);

    public Task OnWorldAsync(Action action) => Host.OnWorldAsync(action);

    /// <summary>Teleport the bot and acknowledge like a client until it stands there.</summary>
    public async Task PlaceAsync(uint mapId, Vector3 at)
    {
        Assert.True(await OnWorldAsync(() => Teleports.TeleportTo(Player, mapId, at.X, at.Y, at.Z, 0)), "teleport refused");
        for (int i = 0; i < 200; i++)
        {
            bool there = await OnWorldAsync(() =>
            {
                PlayerbotMovementControl.Update(Session, Player);
                return Player.IsInWorld && Teleports.StageOf(Player) is null && Player.MapId == mapId
                    && Vector2.Distance(new(Player.X, Player.Y), new(at.X, at.Y)) < 1f;
            });
            if (there) return;
            await AdvanceAsync(50);
        }

        throw new TimeoutException($"the bot did not arrive at {at} on map {mapId}");
    }

    /// <summary>
    /// Run the leg as the brain would: <see cref="PlayerbotShip.Drive"/> every <paramref name="thinkMs"/> (a multiple of 50), the motion pumped
    /// every 50 ms tick (and <see cref="PlayerbotShip.Pump"/> with <paramref name="pumpShip"/>), until the leg ends or
    /// <paramref name="maxMs"/> of game time passed (then <see cref="TravelLegStep.Traveling"/>). <paramref name="observe"/> sees the bot after
    /// every tick (world thread).
    /// </summary>
    public async Task<(TravelLegStep Step, string? Failure)> DriveAsync(PlayerbotOptions options, uint maxMs, uint thinkMs = 100, bool pumpShip = false,
        Action<Player>? observe = null)
    {
        for (uint elapsed = 0; elapsed < maxMs; elapsed += 50)
        {
            if (elapsed % thinkMs == 0)
            {
                (TravelLegStep step, string? failure) = await OnWorldAsync(() =>
                {
                    TravelLegStep s = PlayerbotShip.Drive(Session, Player, options, thinkMs, out string? why);
                    return (s, why);
                });
                if (step is TravelLegStep.Arrived or TravelLegStep.Failed or TravelLegStep.Idle) return (step, failure);
            }

            await AdvanceAsync(50);
            await OnWorldAsync(() =>
            {
                PlayerbotMotion.Pump(Session, Player, Host.World.NowMs);
                if (pumpShip) PlayerbotShip.Pump(Session, Player, Host.World.NowMs);
                observe?.Invoke(Player);
            });
        }

        return (TravelLegStep.Traveling, null);
    }

    public async ValueTask DisposeAsync()
    {
        Session.Kick();
        await Session.ManagedClosed;
        await Host.DisposeAsync();
    }

    private sealed class ManualClockFeature : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}
