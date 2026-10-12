using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The empty camp (live 2026-10-12, bot QA #368, "stall: goal Grind target 80"): bots hunting a quest creature with none in sight walked
/// to the nearest spawn point, found it empty, and planned the same point again until the stall watch gave up and they picked it again.
/// The destinations now mark a reached, empty spawn (and its camp) visited for its respawn delay, and a Grind stall with no target sets
/// the entry aside.
/// </summary>
public sealed class PlayerbotGrindCampTests
{
    private const uint Kobold = 80;
    private const float FarYards = 200f;

    private sealed class ManualClockFeature : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    private sealed class FlatFloor : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => 83.53f;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }

    private sealed class CountingPathfinder : IPathfinder
    {
        public bool Enabled => true;
        public int Calls { get; private set; }

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Calls++;
            return PathResult.StraightLine(start, end, PathType.Normal);
        }
    }

    /// <summary>
    /// The bot's own position is the first spawn of the camp (four spawns within 10 yards, <paramref name="respawnSeconds"/> each); a
    /// fifth lies <see cref="FarYards"/> west when <paramref name="withFarSpawn"/>. No creature of the camp is left in sight.
    /// </summary>
    private sealed class Camp : IAsyncDisposable
    {
        private Camp(WorldTestHost host, WorldSession session, CountingPathfinder pathfinder, PlayerbotWorldDestinations destinations, Vector3 origin)
        {
            Host = host;
            Session = session;
            Pathfinder = pathfinder;
            Destinations = destinations;
            Origin = origin;
        }

        public WorldTestHost Host { get; }
        public WorldSession Session { get; }
        public CountingPathfinder Pathfinder { get; }
        public PlayerbotWorldDestinations Destinations { get; }
        public Vector3 Origin { get; }

        public static async Task<Camp> StartAsync(bool withFarSpawn, uint respawnSeconds = 180)
        {
            // The player logs in at its race's start; the spawns are installed from that position before the destinations first read them.
            WorldTestHost host = WorldTestHost.Start(configureServices: services =>
                services.AddSingleton<IWorldFeature, ManualClockFeature>());
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            var pathfinder = new CountingPathfinder();
            Vector3 origin = default;
            PlayerbotWorldDestinations destinations = null!;
            await host.World.InvokeAsync(() =>
            {
                var player = session.Player!;
                origin = new Vector3(player.X, player.Y, player.Z);
                List<CreatureSpawn> spawns =
                [
                    Spawn(1, origin, 0, 0),
                    Spawn(2, origin, 4, 0),
                    Spawn(3, origin, 0, 4),
                    Spawn(4, origin, -4, 0),
                ];
                if (withFarSpawn) spawns.Add(Spawn(5, origin, -FarYards, 0));
                CreatureSpawn Spawn(uint guid, Vector3 at, float dx, float dy) => new()
                {
                    Guid = guid, Entry = Kobold, MapId = player.MapId, X = at.X + dx, Y = at.Y + dy, Z = at.Z,
                    SpawnTimeMinSeconds = respawnSeconds, SpawnTimeMaxSeconds = respawnSeconds,
                };
                host.WorldServices.GetRequiredService<CreatureWorldFeature>().Install(new CreatureContent(
                    [new CreatureTemplate { Entry = Kobold, Name = "Kobold Laborer", DisplayIds = [49] }], spawns, [], [], []));
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true });
                return true;
            });
            // The map brings the spawns to life (the test map has a creature spawner); the camp is empty: kill the ones in sight (their corpses stay until the respawn).
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Count >= 4, "camp creatures in sight");
            await host.World.InvokeAsync(() =>
            {
                var player = session.Player!;
                foreach (var guid in player.VisibleObjects.ToArray())
                    if (player.Map!.FindObject(guid) is ArcaneCore.Game.Creatures.Creature { Entry: Kobold, IsAlive: true } creature)
                        player.Map.Combat.Kill(null, creature);
                return true;
            });
            await host.WaitForWorldAsync(() => !session.Player!.VisibleObjects.Any(guid =>
                session.Player.Map!.FindObject(guid) is ArcaneCore.Game.Creatures.Creature { Entry: Kobold, IsAlive: true }), "camp empty");
            return new Camp(host, session, pathfinder, destinations, origin);
        }

        /// <summary>One think: the destinations' answer and the bot's position after it.</summary>
        public Task<bool> ThinkAsync(uint elapsedMs = 500) => Host.World.InvokeAsync(() =>
        {
            Session.ManagedBudget = new ManagedActionBudget(1);
            bool moving = Destinations.Update(Session.Player!, Kobold, elapsedMs);
            PlayerbotMotion.ElapseForTests(Session.Player!, elapsedMs);
            return moving;
        });

        public Task<float> DistanceFromOriginAsync() => Host.World.InvokeAsync(() =>
            Vector3.Distance(Origin, new Vector3(Session.Player!.X, Session.Player.Y, Session.Player.Z)));

        public async ValueTask DisposeAsync()
        {
            Session.Kick();
            await Session.ManagedClosed;
            await Host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ArrivingAtAnEmptyCamp_MovesTheBotOnToTheFartherSpawn_NotBackToTheNearOne()
    {
        await using Camp camp = await Camp.StartAsync(withFarSpawn: true);
        bool left = false;
        // 120 s of thinks, 500 ms apart: the bot stands on the camp's first spawn and must walk off to the far one.
        for (int think = 0; think < 240 && !left; think++)
        {
            await camp.ThinkAsync();
            await camp.Host.World.AdvanceClockAsync(500);
            left = await camp.DistanceFromOriginAsync() > 60f;
        }

        Assert.True(left, $"the bot never left the empty camp: visited={camp.Destinations.VisitedCount} calls={camp.Pathfinder.Calls} target={camp.Destinations.TargetEntry} dist={await camp.DistanceFromOriginAsync()}");
        Assert.True(camp.Destinations.VisitedCount >= 4);
    }

    [Fact]
    public async Task WhenEverySpawnIsVisited_TheBotDropsTheDestination_AndPlansNothing()
    {
        await using Camp camp = await Camp.StartAsync(withFarSpawn: false);
        Assert.False(await camp.ThinkAsync()); // at the camp, nothing alive: all four spawns are visited
        int calls = camp.Pathfinder.Calls;
        for (int think = 0; think < 60; think++)
        {
            Assert.False(await camp.ThinkAsync());
            await camp.Host.World.AdvanceClockAsync(500);
        }

        Assert.Equal(calls, camp.Pathfinder.Calls); // 30 s: no path query, no backoff cycle
        Assert.Equal(0u, camp.Destinations.TargetEntry);
        Assert.True(await camp.DistanceFromOriginAsync() < 1f);
    }

    [Fact]
    public async Task AVisitExpiresAtTheRespawnTime_SoTheCampIsRevisited()
    {
        await using Camp camp = await Camp.StartAsync(withFarSpawn: false, respawnSeconds: 180);
        Assert.False(await camp.ThinkAsync());
        Assert.Equal(4, camp.Destinations.VisitedCount);
        // Step 150 yards off the camp (out of arrival range and out of sight of its respawns), so the next think can choose a destination again.
        await camp.Host.World.InvokeAsync(() =>
        {
            var player = camp.Session.Player!;
            player.Relocate(player.X + 150f, player.Y, player.Z, player.Orientation, camp.Host.World.NowMs);
            return true;
        });
        await camp.Host.World.AdvanceClockAsync(170_000);
        Assert.False(await camp.ThinkAsync()); // 170 s of 180: still skipped
        Assert.Equal(0, camp.Pathfinder.Calls);
        await camp.Host.World.AdvanceClockAsync(11_000);
        Assert.True(await camp.ThinkAsync()); // 181 s: the camp is due back, the bot walks to it
        Assert.Equal(Kobold, camp.Destinations.TargetEntry);
        Assert.True(camp.Pathfinder.Calls > 0);
    }

    [Fact]
    public async Task AGrindStallWithNoTarget_SetsTheEntryAside_SoTheNextDecisionDoesNotRouteBackToTheCamp()
    {
        await using Camp camp = await Camp.StartAsync(withFarSpawn: true);
        var brain = new PlayerbotBrain(camp.Session, new PlayerbotOptions { Enabled = true });
        try
        {
            await camp.Host.World.InvokeAsync(() =>
            {
                camp.Destinations.Suspensions = brain.Suspensions;
                brain.HoldGoalForTests(PlayerbotGoalKind.Grind, Kobold, 21);
                brain.GiveUpStalledGoal(camp.Session.Player!, shareable: false);
                Assert.True(brain.Suspensions.IsEntrySuspended(Kobold, camp.Host.World.NowMs));
                int calls = camp.Pathfinder.Calls;
                camp.Session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(camp.Destinations.Update(camp.Session.Player!, Kobold, 500));
                Assert.Equal(calls, camp.Pathfinder.Calls);
                return true;
            });
        }
        finally { brain.Stop(); }
    }
}
