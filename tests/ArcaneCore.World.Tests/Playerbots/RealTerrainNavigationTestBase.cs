using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Teleport;
using ArcaneCore.World.Features;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Playerbots.Dungeon;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Base of the real-terrain bot test classes: the world on the real terrain (<see cref="Terrain"/>) and the clocks they share. The
/// classes were once one partial class; xUnit runs the tests of a class one after another and the classes in parallel, so they are
/// separate classes now (the long walks would otherwise be the floor of the whole run). Nothing here is static state: every test starts
/// its own world.
/// </summary>
public abstract class RealTerrainNavigationTestBase
{
    protected readonly ITestOutputHelper output;

    protected RealTerrainNavigationTestBase(ITestOutputHelper output) => this.output = output;

    internal sealed class RevivalClock(long seconds) : DeathClock
    {
        public long Seconds { get; set; } = seconds;
        public override long UnixSeconds => Seconds;
    }

    /// <summary>A world on the real terrain with one managed bot session on the manual clock.</summary>
    internal sealed class Terrain : IAsyncDisposable
    {
        private Terrain(WorldTestHost host, WorldSession session)
        {
            Host = host;
            Session = session;
        }

        public WorldTestHost Host { get; }

        public WorldSession Session { get; }

        public Player Player => Session.Player!;

        private TeleportService Teleports => Host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;

        public static async Task<Terrain> StartAsync(bool withFactions = false, Action<IServiceCollection>? services = null)
        {
            string root = Environment.GetEnvironmentVariable(RealTerrainBotFactAttribute.Variable)!;
            Action<IServiceCollection>? extra = services;
            WorldTestHost host = WorldTestHost.Start(configure: options => options.Maps.DataDirectory = root,
                configureServices: services =>
                {
                    services.AddSingleton<IWorldFeature, ManualClock>();
                    extra?.Invoke(services);
                    if (!withFactions) return;
                    // The scenario world's factions (DungeonBotHost): faction 14 monsters are hostile to the human bot.
                    services.AddSingleton(new FactionTemplateCatalog(
                    [
                        new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),
                        new FactionTemplateRecord(14, 14, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 1),
                    ]));
                });
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            return new Terrain(host, session);
        }

        /// <summary>Teleport the bot (eastern kingdoms unless <paramref name="map"/> says otherwise) and acknowledge like a client until it stands there.</summary>
        public async Task PlaceAsync(Vector3 at, uint map = 0)
        {
            Assert.True(await Host.OnWorldAsync(() => Teleports.TeleportTo(Player, map, at.X, at.Y, at.Z, 0)), "teleport refused");
            for (int tick = 0; tick < 200; tick++)
            {
                bool there = await Host.OnWorldAsync(() =>
                {
                    PlayerbotMovementControl.Update(Session, Player);
                    return Player.IsInWorld && Teleports.StageOf(Player) is null && Player.MapId == map
                        && Vector2.Distance(new(Player.X, Player.Y), new(at.X, at.Y)) < 1f;
                });
                if (there) return;
                await Host.World.AdvanceClockAsync(50);
            }

            throw new TimeoutException($"the bot did not arrive at {at}");
        }

        /// <summary>Kill the bot where it stands and release its spirit; the body's position.</summary>
        public async Task<Vector3> KillAndReleaseAsync()
        {
            await Host.OnWorldAsync(() =>
            {
                Player.Health = 0;
                Player.Map!.Combat.KillPlayer(Player);
                if (!Player.Map!.Combat.RepopPlayer(Player)) throw new InvalidOperationException("release refused");
            });
            await WaitAsync(() => (Player.Flags & PlayerFlags.Ghost) != 0 && Teleports.StageOf(Player) is null, "the release");
            return await Host.OnWorldAsync(() => new Vector3(Player.Combat.Corpse!.X, Player.Combat.Corpse.Y, Player.Combat.Corpse.Z));
        }

        /// <summary>Tick the world and acknowledge the server orders until <paramref name="done"/> holds (bounded by game time).</summary>
        public async Task WaitAsync(Func<bool> done, string what)
        {
            for (int tick = 0; tick < 400; tick++)
            {
                bool reached = await Host.OnWorldAsync(() =>
                {
                    PlayerbotMovementControl.Update(Session, Player);
                    return Player.IsInWorld && done();
                });
                if (reached) return;
                await Host.World.AdvanceClockAsync(50);
            }

            throw new TimeoutException($"timed out waiting for: {what}");
        }

        /// <summary>Plan one route to <paramref name="goal"/> and follow it as the brain does; the distance left when it stops.</summary>
        public async Task<float> WalkAsync(Vector3 goal, uint maxMs, ITestOutputHelper log)
        {
            var options = new PlayerbotOptions();
            TileWait[] wait = [new TileWait()];
            PlayerbotRoute? route = null;
            PlanOutcome outcome = PlanOutcome.Pending;
            while (outcome == PlanOutcome.Pending)
            {
                outcome = await Host.OnWorldAsync(() => PlayerbotNavigation.PlanExact(Player, goal, options, ref wait[0], Host.World.NowMs, out route));
                if (outcome != PlanOutcome.Pending) break;
                await Task.Delay(20);
                await Host.World.AdvanceClockAsync(100);
            }

            Assert.NotNull(route);
            log.WriteLine($"route: {string.Join(" ", route!.Points)}");
            return await FollowAsync(route, goal, maxMs);
        }

        /// <summary>
        /// Follow routes from <see cref="PlayerbotNavigation.PlanToward"/> until within <paramref name="reach"/> of the goal. A
        /// <see cref="PlanOutcome.Pending"/> answer (a navigation tile is being read in the background) is waited out as the brain does:
        /// the world clock moves on and the plan is asked again, until <see cref="TileWait.MaxMs"/> turns it into a refusal.
        /// </summary>
        public async Task<float> WalkTowardAsync(Vector3 goal, float reach, uint maxMs, ITestOutputHelper log, PlayerbotOptions? with = null)
        {
            PlayerbotOptions options = with ?? new PlayerbotOptions();
            float left = float.PositiveInfinity;
            TileWait[] wait = [new TileWait()]; // boxed: a ref local cannot be captured by the world-thread lambda
            const int MaxLegs = 2000;
            int legs = 0;
            for (uint spent = 0; spent < maxMs;)
            {
                Vector3 legStart = await Host.OnWorldAsync(() => new Vector3(Player.X, Player.Y, Player.Z));
                Assert.True(++legs <= MaxLegs,
                    $"walk towards {goal}: still {left:F1} yards away after {MaxLegs} legs, last at {legStart}");
                PlayerbotRoute? route = null;
                PlanOutcome outcome = PlanOutcome.Pending;
                while (outcome == PlanOutcome.Pending)
                {
                    outcome = await Host.OnWorldAsync(() => PlayerbotNavigation.PlanToward(Player, goal, options, ref wait[0], Host.World.NowMs, out route));
                    if (outcome != PlanOutcome.Pending) break;
                    await Task.Delay(20); // the tile is read on a background thread: real time, not game time
                    await Host.World.AdvanceClockAsync(100);
                }

                Assert.True(outcome == PlanOutcome.Route && route is not null,
                    $"no route towards {goal} from {await Host.OnWorldAsync(() => new Vector3(Player.X, Player.Y, Player.Z))} ({outcome})");
                log.WriteLine($"leg to {route!.Points[^1]}");
                uint started = Host.World.NowMs;
                left = await FollowAsync(route, goal, maxMs - spent);
                uint used = unchecked(Host.World.NowMs - started);
                spent += used;
                if (left < reach) return left;

                // A leg that used no game time and moved the bot nowhere would be planned again forever (the clock the cap counts
                // never advances): that is a stall, and fails here instead of hanging the run.
                Vector3 legEnd = await Host.OnWorldAsync(() => new Vector3(Player.X, Player.Y, Player.Z));
                Assert.False(used == 0 && Vector3.Distance(legStart, legEnd) < 0.5f,
                    $"walk towards {goal}: the leg to {route.Points[^1]} made no progress, stalled at {legEnd} ({left:F1} yards away)");
            }

            return left;
        }

        private async Task<float> FollowAsync(PlayerbotRoute route, Vector3 goal, uint maxMs)
        {
            var options = new PlayerbotOptions();
            for (uint elapsed = 0; elapsed <= maxMs; elapsed += 100)
            {
                bool moving = await Host.OnWorldAsync(() =>
                {
                    Session.ManagedBudget = new ManagedActionBudget(4);
                    return PlayerbotNavigation.TryAdvance(Session, route, options, 100, Host.World.NowMs);
                });
                if (!moving) break;
                await Host.World.AdvanceClockAsync(50);
                await Host.OnWorldAsync(() => PlayerbotMotion.Pump(Session, Player, Host.World.NowMs));
                await Host.World.AdvanceClockAsync(50);
                await Host.OnWorldAsync(() => PlayerbotMotion.Pump(Session, Player, Host.World.NowMs));
            }

            return await Host.OnWorldAsync(() => Vector2.Distance(new(Player.X, Player.Y), new(goal.X, goal.Y)));
        }

        public async ValueTask DisposeAsync()
        {
            Session.Kick();
            await Session.ManagedClosed;
            await Host.DisposeAsync();
        }
    }

    internal sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    protected static readonly PlayerbotOptions LiveOptions = new() { MaxPathPoints = 128, MaxRouteYards = 2000 };

    // Innkeeper Kauth (creature 6747), Bloodhoof Village; Innkeeper Keldamyr (6736), Dolanaar.
    protected static readonly Vector3 InnkeeperKauth = new(-2365.4f, -347.3f, -8.9f);
    protected static readonly Vector3 InnkeeperKeldamyr = new(9802.2f, 982.6f, 1314.0f);

    protected async Task WalksAsync(Vector3 from, Vector3 to, uint map = 1)
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(from, map);

        // The region leg of a plan answers nothing until the map's graph is built (in the background): wait for it, so every walk
        // takes the same planning path whatever the timing.
        Task<NavRegionGraph?> build = await terrain.Host.OnWorldAsync(() =>
            ((NavMeshPathfinder)terrain.Player.Map!.Collision.Pathfinder).RegionGraphTask(map, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!);
        Assert.NotNull(await build);

        float left = await terrain.WalkTowardAsync(to, 5f, 300_000, output, LiveOptions); // a refused route fails inside
        Assert.True(left <= 5f, $"stopped {left:F1} yards from the destination");
    }

    protected async Task WalkProbeAsync(uint map, Vector3 pin, Vector3 goal, uint maxMs = 120_000)
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(pin, map);
        Task<NavRegionGraph?> build = await terrain.Host.OnWorldAsync(() =>
            ((NavMeshPathfinder)terrain.Player.Map!.Collision.Pathfinder).RegionGraphTask(map, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!);
        Assert.NotNull(await build);
        output.WriteLine($"=== walk map {map} from {pin} to {goal}");
        string result;
        try
        {
            float left = await terrain.WalkTowardAsync(goal, 5f, maxMs, output, new PlayerbotOptions { MaxPathPoints = 128, MaxRouteYards = 2000 });
            result = $"walk ended {left:F1} yards from the goal";
        }
        catch (Xunit.Sdk.XunitException problem)
        {
            result = "walk failed: " + problem.Message.Split('\n')[0];
        }

        Vector3 end = await terrain.Host.OnWorldAsync(() => new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z));
        output.WriteLine($"(walk) {result}; the bot ends at {end}, {Vector3.Distance(end, pin):F0} yards from the pin, {Vector3.Distance(end, goal):F0} from the goal");
    }

    // Places shared by several of the real-terrain classes (the crypt start, the revive spot at Coldridge, the Goldshire inn).
    protected static readonly Vector3 CryptStart = new(1676.35f, 1677.45f, 121.67f);
    protected static readonly Vector3 ColdridgeAfterRevive = new(-6222.2f, 236.17f, 388.48f);
    protected static readonly Vector3 InnBesidePestle = new(-9465.6f, 35.2f, 57.0f);


    /// <summary>The map's region graph for the bots' filter, waiting for its build.</summary>
    internal static async Task<NavRegionGraph> RegionGraphAsync(Terrain terrain, uint map)
    {
        Task<NavRegionGraph?> build = await terrain.Host.OnWorldAsync(() =>
            ((NavMeshPathfinder)terrain.Player.Map!.Collision.Pathfinder).RegionGraphTask(map, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!);
        return await build ?? throw new InvalidOperationException($"no region graph for map {map}");
    }
}
