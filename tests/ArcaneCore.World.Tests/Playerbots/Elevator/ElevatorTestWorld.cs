using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Playerbots.Tram;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Elevator;

/// <summary>
/// A world with the Great Lift (<see cref="ElevatorTestData"/>: its two cars, spawns 16874 and 16876, and their real paths), the manual
/// clock, straight-line paths that stop at the cliff (<see cref="CliffPathfinder"/>) and a fixture cliff: east of <see cref="Cliff"/> the plateau of The Barrens at <see cref="TopFloor"/>, west of
/// it the floor of Thousand Needles at <see cref="BottomFloor"/>; the zones follow the cliff (<see cref="PlayerbotElevator.UseSurvey"/>). One
/// managed bot. Time moves only through <see cref="AdvanceAsync"/>.
/// </summary>
internal sealed class ElevatorTestWorld : IAsyncDisposable
{
    /// <summary>The cliff's line (x): the top car (x -4665.4) has the plateau beside it, the bottom car (x -4670.8) the canyon all round.</summary>
    public const float Cliff = -4663f;

    /// <summary>The canyon floor (both cars' lower stops, -44.14 and -43.89, within the landing tolerance) and the plateau (85.41).</summary>
    public const float BottomFloor = -44f, TopFloor = 85.41f;

    public const uint ThousandNeedles = 400, TheBarrens = 17;

    /// <summary>A spot on the canyon floor 100 yards south-west of the lift.</summary>
    public static readonly Vector3 CanyonStart = new(-4750f, -1900f, BottomFloor);

    /// <summary>Ratchet's quest ender as the fixture has it: on the plateau, 3,700 yards away.</summary>
    public static readonly Vector3 Ratchet = ElevatorTestData.Dizzywig with { Z = TopFloor };

    private ElevatorTestWorld(WorldTestHost host, WorldSession session)
    {
        Host = host;
        Session = session;
    }

    public WorldTestHost Host { get; }

    public WorldSession Session { get; }

    public Player Player => Session.Player!;

    public TeleportService Teleports => Host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;

    /// <param name="shaftFloor">The floor inside the cars' footprints (null, the default: none, as on the real data, where a car is a
    /// dynamic object in no collision data and nothing lies under it but the bottom of the shaft).</param>
    public static async Task<ElevatorTestWorld> StartAsync(TransportAnimationCatalog? animation = null, CreatureContent? creatures = null,
        float? shaftFloor = null, Action<IServiceCollection>? configure = null)
    {
        WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IMapDataStore>(new MapStore());
            services.AddScoped<IGameObjectDataStore>(_ => new GameObjectStore());
            services.AddSingleton(animation ?? ElevatorTestData.Animation());
            services.AddSingleton<IWorldFeature, ManualClockFeature>();
            if (creatures is not null) services.AddSingleton<ICreatureDataStore>(new CreatureStore(creatures));
            configure?.Invoke(services);
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        await host.OnWorldAsync(() =>
        {
            WorldCollision.Of(host.World).Install(lineOfSight: new Floors(shaftFloor), pathfinder: new CliffPathfinder());
            PlayerbotElevator.UseSurvey(host.World, _ => new ElevatorSurvey(at => at.X < Cliff ? (ThousandNeedles, 0u) : (TheBarrens, 0u)));
        });
        return new ElevatorTestWorld(host, session);
    }

    /// <summary>
    /// The same world on the real data: Kalimdor's terrain, models and navigation mesh from <paramref name="terrain"/> (the live server's
    /// extraction) and the client's paths of the Great Lift's two cars (<paramref name="animation"/>, TransportAnimation.dbc); no fixture.
    /// </summary>
    public static async Task<ElevatorTestWorld> StartRealAsync(string terrain, TransportAnimationCatalog animation)
    {
        WorldTestHost host = WorldTestHost.Start(configure: options => options.Maps.DataDirectory = terrain, configureServices: services =>
        {
            services.AddSingleton<IMapDataStore>(new MapStore());
            services.AddScoped<IGameObjectDataStore>(_ => new GameObjectStore());
            services.AddSingleton(animation);
            services.AddSingleton<IWorldFeature, ManualClockFeature>();
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        return new ElevatorTestWorld(host, session);
    }

    public Task AdvanceAsync(uint milliseconds) => Host.World.AdvanceClockAsync(milliseconds);

    /// <summary>100 ms of the bot's life: <paramref name="think"/>, then the motion pumped every 50 ms world tick.</summary>
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
    public async Task PlaceAsync(Vector3 at)
    {
        Assert.True(await OnWorldAsync(() => Teleports.TeleportTo(Player, ElevatorTestData.Kalimdor, at.X, at.Y, at.Z, 0)), "teleport refused");
        for (int i = 0; i < 200; i++)
        {
            bool there = await OnWorldAsync(() =>
            {
                PlayerbotMovementControl.Update(Session, Player);
                return Player.IsInWorld && Teleports.StageOf(Player) is null && Player.MapId == ElevatorTestData.Kalimdor
                    && Vector2.Distance(new(Player.X, Player.Y), new(at.X, at.Y)) < 1f;
            });
            if (there) return;
            await AdvanceAsync(50);
        }

        throw new TimeoutException($"the bot did not arrive at {at}");
    }

    /// <summary>
    /// A real player's client (a socket, as <see cref="WorldTestHost.EnterWorldAsync"/> makes one) brought to <paramref name="at"/> on
    /// Kalimdor, so it observes the bot there: the far teleport from its start, its worldport acknowledgement, the clock moved meanwhile.
    /// </summary>
    public async Task<WorldTestClient> ObserverAsync(string account, string name, Vector3 at)
    {
        WorldTestClient client = await Host.EnterWorldAsync(account, name);
        Assert.True(await OnWorldAsync(() => Teleports.TeleportTo(Host.World.FindOnlinePlayer(name)!, ElevatorTestData.Kalimdor, at.X, at.Y, at.Z, 0)),
            "the observer's teleport was refused");
        for (int i = 0; i < 10; i++) await AdvanceAsync(50);
        await client.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
        await client.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        for (int i = 0; i < 200; i++)
        {
            bool there = await OnWorldAsync(() => Host.World.FindOnlinePlayer(name) is { IsInWorld: true, MapId: ElevatorTestData.Kalimdor } p
                && Teleports.StageOf(p) is null);
            if (there) return client;
            await AdvanceAsync(50);
        }

        throw new TimeoutException("the observer never arrived on Kalimdor");
    }

    public GameObjectMapSystem? Objects() => Host.World.GetMap(ElevatorTestData.Kalimdor).FindUpdater<GameObjectMapSystem>();

    /// <summary>The lift of the world's data, as the bots see it.</summary>
    public Task<ElevatorLift> GreatLiftAsync()
        => OnWorldAsync(() => Assert.Single(PlayerbotElevator.NetworkOf(Session)!.Lifts));

    public async ValueTask DisposeAsync()
    {
        Session.Kick();
        await Session.ManagedClosed;
        await Host.DisposeAsync();
    }

    private sealed class MapStore : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MapContent([.. TramTestData.Maps.Where(m => m.Entry != TramTestData.Tram)], [], [], [], []));
    }

    private sealed class GameObjectStore : IGameObjectDataStore
    {
        public Task<GameObjectContent> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(ElevatorTestData.Content(16874, 16876));
    }

    private sealed class CreatureStore(CreatureContent content) : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }

    /// <summary>The two cars' shafts (their spawns, flat): no floor inside them unless the fixture gives one.</summary>
    internal static readonly Vector2[] Shafts = [new(-4665.43f, -1827.67f), new(-4670.77f, -1849.61f)];

    /// <summary>
    /// The fixture cliff: the plateau east of <see cref="Cliff"/> (found only from above it), the canyon floor west of it; inside a car's
    /// footprint (<see cref="PlayerbotElevator.CarHalfYards"/> of a shaft) <paramref name="shaftFloor"/> or nothing.
    /// </summary>
    internal sealed class Floors(float? shaftFloor) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
        {
            if (Shafts.Any(s => Vector2.Distance(s, new Vector2(x, y)) <= PlayerbotElevator.CarHalfYards))
                return shaftFloor is { } floor && z >= floor - 0.1f ? floor : null;
            return x >= Cliff ? (z >= TopFloor - 0.1f ? TopFloor : null) : (z >= BottomFloor - 0.1f ? BottomFloor : null);
        }
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info) { info = default; return false; }
    }

    /// <summary>
    /// The navigation mesh of the fixture: a straight line on one side of the cliff; across it, the incomplete way to the cliff's edge on
    /// the start's side (as a mesh answers a goal it does not join: the nearest point it reaches), none when the start is at the edge.
    /// </summary>
    internal sealed class CliffPathfinder : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            bool startTop = start.X >= Cliff, endTop = end.X >= Cliff;
            if (startTop == endTop) return PathResult.StraightLine(start, end with { Z = startTop ? TopFloor : BottomFloor }, PathType.Normal);
            float edgeX = startTop ? Cliff + 0.5f : Cliff - 0.5f;
            float t = (edgeX - start.X) / (end.X - start.X);
            Vector3 edge = Vector3.Lerp(start, end, Math.Clamp(t, 0f, 1f)) with { Z = startTop ? TopFloor : BottomFloor };
            return Vector3.Distance(start, edge) < 0.5f ? PathResult.None(start) : new PathResult(PathType.Incomplete, [start, edge]);
        }
    }

    private sealed class ManualClockFeature : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}
