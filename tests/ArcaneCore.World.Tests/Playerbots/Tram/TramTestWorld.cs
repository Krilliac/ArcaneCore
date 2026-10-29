using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Tram;

/// <summary>
/// The Deeprun Tram rows of the world database (classic-db 5875, the live world's import): the four area triggers and their teleports
/// (<c>areatrigger_template</c> / <c>areatrigger_teleport</c> 2166, 2171, 2173, 2175) and the six "Subway" cars (<c>gameobject</c> guids
/// 18802-18807, entries 176080-176085, type 11, display 3831). Only the values the tests need, copied from the rows.
/// </summary>
internal static class TramTestData
{
    public static uint Tram => TramNetwork.TramMapId;

    /// <summary>Zone IDs for this fixture's flat continent: its western starts are Stormwind/Westfall, its east Ironforge.</summary>
    public static uint ZoneAt(Vector3 at) => at.X > -7000f ? 1537u : at.X < -10000f ? 40u : at.X < -9000f ? 12u : 1519u;

    public static readonly AreaTriggerTemplate[] Triggers =
    [
        new(2166, Tram, 76.0270f, 10.5043f, -4.29659f, 0f, 9.417f, 19f, 20.64f, 0f, ""),
        new(2171, Tram, 80.1633f, 2490.88f, -1.85693f, 10f, 0, 0, 0, 0, ""),
        new(2173, 0, -8346.46f, 514.031f, 96.5989f, 10f, 0, 0, 0, 0, ""),
        new(2175, 0, -4840.26f, -1330.46f, 508.172f, 10f, 0, 0, 0, 0, ""),
    ];

    public static readonly AreaTriggerTeleport[] Teleports =
    [
        new(2166, "Deeprun Tram - Ironforge Instance (Inside)", "", 0, 0, -4839.51f, -1317.74f, 501.868f, 1.48353f),
        new(2171, "Deeprun Tram - Stormwind Instance (Inside)", "", 0, 0, -8354.23f, 524.068f, 91.7971f, 2.35619f),
        new(2173, "Deeprun Tram - Stormwind Instance", "", 0, Tram, 67.7607f, 2490.98f, -4.29649f, 3.14159f),
        new(2175, "Deeprun Tram - Ironforge Instance", "", 0, Tram, 69.2277f, 10.3932f, -4.29665f, 3.14159f),
    ];

    /// <summary>The Stormwind side of the tram exit (where the bot starts) and where the Ironforge exit leads.</summary>
    public static readonly Vector3 StormwindExitLanding = new(-8354.23f, 524.068f, 91.7971f);
    public static readonly Vector3 IronforgeExitLanding = new(-4839.51f, -1317.74f, 501.868f);
    public static readonly Vector3 StormwindPlatform = new(67.7607f, 2490.98f, -4.29649f);
    public static readonly Vector3 IronforgePlatform = new(69.2277f, 10.3932f, -4.29665f);

    /// <summary>The platform floor (the landings' height) and the two city floors of the fixture's flat collision.</summary>
    public const float PlatformFloor = -4.2966f, StormwindFloor = 91.7971f, IronforgeFloor = 501.868f;

    public static readonly MapTemplate[] Maps =
    [
        new(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
        new(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
        new(Tram, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Deeprun Tram", ""),
    ];

    /// <summary>(guid, entry, x, y, z, orientation, rotation2, rotation3) of the six cars.</summary>
    public static readonly (uint Guid, uint Entry, float X, float Y, float Z, float O, float R2, float R3)[] Cars =
    [
        (18802, 176080, 4.58064f, 28.2097f, 7.01107f, 1.5708f, 0.707107f, 0.707107f),
        (18803, 176081, 4.52807f, 8.43529f, 7.01107f, 1.5708f, 0.707107f, 0.707107f),
        (18804, 176082, -45.4005f, 2492.79f, 6.98860f, 1.5708f, 0.707107f, 0.707107f),
        (18805, 176083, -45.4007f, 2512.15f, 6.98860f, 1.5708f, 0.707107f, 0.707107f),
        (18806, 176084, -45.3934f, 2472.93f, 6.98860f, 1.5708f, 0.707107f, 0.707107f),
        (18807, 176085, 4.49883f, -11.3475f, 7.01107f, -1.5708f, -0.707107f, 0.707107f),
    ];

    public static GameObjectContent Content() => new(
        [.. Cars.Select(c => c.Entry).Distinct().Select(entry => new GameObjectTemplate
        {
            Entry = entry, Type = (uint)GameObjectType.Transport, DisplayId = 3831, Name = "Subway", Flags = 40,
            Data = new uint[GameObjectTemplate.DataCount],
        })],
        [.. Cars.Select(c => new GameObjectSpawn
        {
            Guid = c.Guid, Entry = c.Entry, MapId = Tram, X = c.X, Y = c.Y, Z = c.Z, Orientation = c.O,
            Rotation2 = c.R2, Rotation3 = c.R3, SpawnTimeSeconds = 25, AnimProgress = 0, State = 1,
        })],
        [], [], []);

    /// <summary>
    /// A synthetic animation shaped like the real one (TransportAnimation.dbc 5875: every car leaves its home platform at 0 and is back,
    /// stopped, for the last ~13 s of the cycle): out along the car's x axis for 47 s, 13 s stopped at the far platform, back in 47 s,
    /// 13 s stopped at home; a 120 s cycle. The far offset has the sign of the real rows (176080/176081 +x, the others -x).
    /// </summary>
    public static TransportAnimationCatalog SyntheticAnimation(params uint[] only)
    {
        var rows = new List<(uint, TransportAnimationNode)>();
        foreach ((_, uint entry, _, _, _, _, _, _) in Cars)
        {
            if (only.Length > 0 && !only.Contains(entry)) continue;
            float far = entry is 176080 or 176081 ? 2482.3f : -2482.5f;
            rows.Add((entry, new TransportAnimationNode(0, 0, 0, 0)));
            rows.Add((entry, new TransportAnimationNode(47_000, far, 0, 0)));
            rows.Add((entry, new TransportAnimationNode(60_000, far, 0, 0)));
            rows.Add((entry, new TransportAnimationNode(107_000, 0, 0, 0)));
            rows.Add((entry, new TransportAnimationNode(120_000, 0, 0, 0)));
        }

        return new TransportAnimationCatalog(rows);
    }

    public sealed class MapStore : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MapContent(Maps, [], Triggers, Teleports, []));
    }

    public sealed class GameObjectStore : IGameObjectDataStore
    {
        public Task<GameObjectContent> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Content());
    }

    /// <summary>Flat floors: the tram platforms on map 369, and on the continent the floor of the city nearer the probe.</summary>
    public sealed class Floors : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
            => mapId == Tram ? PlatformFloor : x < -6000f ? StormwindFloor : IronforgeFloor;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info) { info = default; return false; }
    }
}

/// <summary>
/// A world with the tram fixture (<see cref="TramTestData"/>), the manual clock, flat floors and straight-line paths, and one managed
/// bot (a human warrior). Time moves only through <see cref="AdvanceAsync"/>.
/// </summary>
internal sealed class TramTestWorld : IAsyncDisposable
{
    private TramTestWorld(WorldTestHost host, WorldSession session)
    {
        Host = host;
        Session = session;
    }

    public WorldTestHost Host { get; }

    public WorldSession Session { get; }

    public Player Player => Session.Player!;

    public TeleportService Teleports => Host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;

    public static async Task<TramTestWorld> StartAsync(TransportAnimationCatalog? animation = null, Action<IServiceCollection>? configure = null,
        IPathfinder? pathfinder = null, byte race = 1)
    {
        WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IMapDataStore>(new TramTestData.MapStore());
            services.AddScoped<IGameObjectDataStore>(_ => new TramTestData.GameObjectStore());
            services.AddSingleton(animation ?? TramTestData.SyntheticAnimation());
            services.AddSingleton<IWorldFeature, ManualClockFeature>();
            configure?.Invoke(services);
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host, race);
        await host.OnWorldAsync(() => WorldCollision.Of(host.World).Install(lineOfSight: new TramTestData.Floors(), pathfinder: pathfinder ?? new OpenPathfinder()));
        return new TramTestWorld(host, session);
    }

    public Task AdvanceAsync(uint milliseconds) => Host.World.AdvanceClockAsync(milliseconds);

    /// <summary>
    /// 100 ms of the bot's life as the managed-bot feature runs it: <paramref name="think"/> (a brain update), then the motion pumped every
    /// 50 ms world tick (heartbeats, area triggers, the worldport acknowledgement of a teleport).
    /// </summary>
    public async Task<T> TickAsync<T>(Func<T> think)
    {
        T result = await OnWorldAsync(think);
        for (int i = 0; i < 2; i++)
        {
            await AdvanceAsync(50);
            await OnWorldAsync(() => PlayerbotMotion.Pump(Session, Player, Host.World.NowMs));
        }

        return result;
    }

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

    /// <summary>The tram map's object system (the map is created by the first player on it).</summary>
    public GameObjectMapSystem? TramObjects()
        => Host.World.GetMap(TramTestData.Tram).FindUpdater<GameObjectMapSystem>();

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
