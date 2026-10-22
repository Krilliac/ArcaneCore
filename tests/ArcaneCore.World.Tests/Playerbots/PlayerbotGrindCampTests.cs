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
    private const uint Kobold = 80, OtherEntry = 81;
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

        /// <summary>Every plan fails (no path), as to an unreachable spawn.</summary>
        public bool Refuse { get; set; }

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Calls++;
            if (Refuse) return PathResult.None(start);
            return PathResult.StraightLine(start, end, PathType.Normal);
        }
    }

    /// <summary>
    /// The bot's own position is the first spawn of the camp (four spawns within 10 yards, <c>respawnSeconds</c> each); a
    /// fifth lies <see cref="FarYards"/> west when <c>withFarSpawn</c>. No creature of the camp is left in sight.
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

        /// <param name="alive">How many creatures of the camp in sight are left alive (the rest are killed).</param>
        /// <param name="layout">Replaces the camp: the spawns from the bot position and the map id; no creature is killed or waited for.</param>
        public static async Task<Camp> StartAsync(bool withFarSpawn, uint respawnSeconds = 180, int alive = 0,
            Func<Vector3, uint, List<CreatureSpawn>>? layout = null)
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
                if (layout is not null) spawns = layout(origin, player.MapId);
                CreatureSpawn Spawn(uint guid, Vector3 at, float dx, float dy) => new()
                {
                    Guid = guid, Entry = Kobold, MapId = player.MapId, X = at.X + dx, Y = at.Y + dy, Z = at.Z,
                    SpawnTimeMinSeconds = respawnSeconds, SpawnTimeMaxSeconds = respawnSeconds,
                };
                host.WorldServices.GetRequiredService<CreatureWorldFeature>().Install(new CreatureContent(
                    [new CreatureTemplate { Entry = Kobold, Name = "Kobold Laborer", DisplayIds = [49], MinLevel = 1, MaxLevel = 1, MinLevelHealth = 5000, MaxLevelHealth = 5000 },
                     new CreatureTemplate { Entry = OtherEntry, Name = "Other", DisplayIds = [49] }], spawns, [], [], []));
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true });
                return true;
            });
            if (layout is not null) return new Camp(host, session, pathfinder, destinations, origin);
            // The map brings the spawns to life (the test map has a creature spawner); the camp is empty: kill the ones in sight (their
            // corpses stay until the respawn), except the first `alive`.
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Count >= 4, "camp creatures in sight");
            await host.World.InvokeAsync(() =>
            {
                var player = session.Player!;
                int kept = 0;
                foreach (var guid in player.VisibleObjects.ToArray())
                    if (player.Map!.FindObject(guid) is ArcaneCore.Game.Creatures.Creature { Entry: Kobold, IsAlive: true } creature && kept++ >= alive)
                        player.Map.Combat.Kill(null, creature);
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Count(guid =>
                session.Player.Map!.FindObject(guid) is ArcaneCore.Game.Creatures.Creature { Entry: Kobold, IsAlive: true }) == alive, "camp settled");
            return new Camp(host, session, pathfinder, destinations, origin);
        }

        /// <summary>One think: the destinations' answer and the bot's position after it.</summary>
        public Task<bool> ThinkAsync(uint elapsedMs = 500, uint entry = Kobold) => Host.World.InvokeAsync(() =>
        {
            Session.ManagedBudget = new ManagedActionBudget(1);
            bool moving = Destinations.Update(Session.Player!, entry, elapsedMs);
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

    [Fact]
    public async Task ALiveCreatureThatTheBotWouldTakeInSight_PreventsMarkingTheSpawnVisited()
    {
        await using Camp camp = await Camp.StartAsync(withFarSpawn: false, alive: 1);
        Assert.False(await camp.ThinkAsync()); // a kobold stands in sight: the brain targeting takes it, nothing is visited
        Assert.Equal(0, camp.Destinations.VisitedCount);
    }

    [Fact]
    public async Task ACreatureTappedByAnotherPlayer_DoesNotPreventMarkingTheSpawnVisited()
    {
        await using Camp camp = await Camp.StartAsync(withFarSpawn: false, alive: 1);
        WorldSession stranger = await ArcaneCore.World.Tests.Playerbots.Combat.CombatTestSessions.EnterAsync(camp.Host, "GRINDCAMPSTR", "Grindcampstr");
        var brain = new PlayerbotBrain(camp.Session, new PlayerbotOptions { Enabled = true });
        try
        {
            await camp.Host.World.InvokeAsync(() =>
            {
                var player = camp.Session.Player!;
                var mob = (ArcaneCore.Game.Creatures.Creature)player.VisibleObjects.Select(g => player.Map!.FindObject(g))
                    .Single(o => o is ArcaneCore.Game.Creatures.Creature { Entry: Kobold, IsAlive: true })!;
                camp.Destinations.Untakeable = brain.IsUntakeable;
                // The control: untagged, the same mob is takeable, so the tag is what changes the answer.
                bool before = brain.IsUntakeable(player, mob);
                player.Map!.Combat.DealDamage(stranger.Player!, mob, 1);
                Assert.True(mob.IsAlive, "the tag killed the mob");
                Assert.True(brain.IsUntakeable(player, mob));
                Assert.False(before, "the untagged mob was already untakeable for another reason");
                return true;
            });
            Assert.False(await camp.ThinkAsync()); // the only creature is another player: the spawn is empty for this bot
            Assert.Equal(4, camp.Destinations.VisitedCount);
        }
        finally
        {
            brain.Stop();
            stranger.Kick(); await stranger.ManagedClosed;
        }
    }

    [Fact]
    public async Task TheVisitedSetHoldsAsManySpawnsAsAreScanned_AndRefusesMoreUntilVisitsExpire()
    {
        const int Spots = PlayerbotWorldDestinations.MaxVisits;
        // 128 camps 12 yards apart (further than the 10-yard camp radius), one spawn of another entry beside them.
        await using Camp camp = await Camp.StartAsync(withFarSpawn: false, respawnSeconds: 180, layout: (origin, map) =>
        [
            .. Enumerable.Range(0, Spots).Select(i => new CreatureSpawn
            {
                Guid = (uint)(100 + i), Entry = Kobold, MapId = map, X = origin.X - 12f * (i % 16), Y = origin.Y - 12f * (i / 16), Z = origin.Z,
                SpawnTimeMinSeconds = 180, SpawnTimeMaxSeconds = 180,
            }),
            new CreatureSpawn { Guid = 900, Entry = OtherEntry, MapId = map, X = origin.X + 30f, Y = origin.Y, Z = origin.Z,
                SpawnTimeMinSeconds = 180, SpawnTimeMaxSeconds = 180 },
        ]);
        await camp.Host.World.InvokeAsync(() =>
        {
            camp.Destinations.Untakeable = (_, _) => true; // creatures the map brought to life are not to be hunted here
            return true;
        });
        for (int i = 0; i < Spots; i++)
        {
            await camp.Host.World.InvokeAsync(() =>
            {
                var player = camp.Session.Player!;
                PlayerbotMovementControl.Stop(camp.Session, player); // the walk the last think began would carry the bot off again
                // The bot is put on the next spot by hand, not walked there: drop the route the last think planned (towards whichever of the
                // equally near spots it chose), which the bot is no longer on.
                camp.Destinations.Stop();
                player.Relocate(camp.Origin.X - 12f * (i % 16), camp.Origin.Y - 12f * (i / 16), camp.Origin.Z, 0, camp.Host.World.NowMs);
                return true;
            });
            await camp.ThinkAsync();
        }

        Assert.Equal(Spots, camp.Destinations.VisitedCount);
        await camp.Host.World.InvokeAsync(() =>
        {
            PlayerbotMovementControl.Stop(camp.Session, camp.Session.Player!);
            camp.Session.Player!.Relocate(camp.Origin.X + 30f, camp.Origin.Y, camp.Origin.Z, 0, camp.Host.World.NowMs);
            return true;
        });
        await camp.ThinkAsync(entry: OtherEntry);
        Assert.Equal(Spots, camp.Destinations.VisitedCount); // full of live visits: no new mark, none dropped
        await camp.Host.World.InvokeAsync(() =>
        {
            uint now = camp.Host.World.NowMs;
            Assert.False(camp.Destinations.IsVisited(900, now));
            for (uint guid = 100; guid < 100 + Spots; guid++) Assert.True(camp.Destinations.IsVisited(guid, now), "visit " + guid + " was dropped");
            return true;
        });
        await camp.Host.World.AdvanceClockAsync(181_000);
        await camp.ThinkAsync(entry: OtherEntry);
        Assert.Equal(1, camp.Destinations.VisitedCount); // the expired ones made room
    }

    [Fact]
    public async Task ACampIsTenYardsAcross_ASpawnElevenYardsFromTheReachedOneIsNotVisited()
    {
        await using Camp camp = await Camp.StartAsync(withFarSpawn: false, layout: (origin, map) =>
        [
            new CreatureSpawn { Guid = 301, Entry = Kobold, MapId = map, X = origin.X, Y = origin.Y, Z = origin.Z },
            new CreatureSpawn { Guid = 302, Entry = Kobold, MapId = map, X = origin.X + 9.5f, Y = origin.Y, Z = origin.Z },
            new CreatureSpawn { Guid = 303, Entry = Kobold, MapId = map, X = origin.X, Y = origin.Y + 11f, Z = origin.Z },
        ]);
        await camp.Host.World.InvokeAsync(() => { camp.Destinations.Untakeable = (_, _) => true; return true; });
        await camp.ThinkAsync();
        Assert.Equal(2, camp.Destinations.VisitedCount);
    }

    [Fact]
    public async Task ASpawnThatWasOnlyBlocked_IsRetriedAfterTheBackoff_WhileTheRestOfTheCampStaysVisited()
    {
        await using Camp camp = await Camp.StartAsync(withFarSpawn: true);
        camp.Pathfinder.Refuse = true;
        Assert.False(await camp.ThinkAsync()); // the camp is visited, the far spawn plan is refused: blocked
        Assert.Equal(4, camp.Destinations.VisitedCount);
        Assert.False(await camp.ThinkAsync()); // every spawn visited or blocked: no destination, the blocked one is cleared for a retry
        camp.Pathfinder.Refuse = false;
        await camp.Host.World.AdvanceClockAsync(2_100); // the backoff
        Assert.True(await camp.ThinkAsync()); // the far spawn is tried again, not left blocked for good
        Assert.Equal(4, camp.Destinations.VisitedCount);
    }

    /// <summary>
    /// The nearest <see cref="PlayerbotWorldDestinations.MaxEntryScan"/> spawns are chosen where the bot stands. An entry with more
    /// spawns than that (Fleeting Plainstrider, 208) used to keep the first list forever: after the bot travelled, every cached
    /// spawn could lie in another region component and the grind target was reported unreachable, while the spawn beside it had
    /// been dropped. The list is chosen again once a dropped spawn can be the nearer one.
    /// </summary>
    [Fact]
    public async Task AfterTheBotTravels_ADroppedSpawnBesideIt_IsHunted_NotTheStaleNearestList()
    {
        const int Kept = PlayerbotWorldDestinations.MaxEntryScan;
        const float ClusterX = 40f, FarX = 4000f, Beside = 40f;
        await using Camp camp = await Camp.StartAsync(withFarSpawn: false, layout: (origin, map) =>
        [
            .. Enumerable.Range(0, Kept).Select(i => new CreatureSpawn
            {
                Guid = (uint)(1 + i), Entry = Kobold, MapId = map,
                X = origin.X + ClusterX, Y = origin.Y + (i * 0.05f), Z = origin.Z,
            }),
            new CreatureSpawn
            {
                Guid = 9000, Entry = Kobold, MapId = map, X = origin.X + FarX, Y = origin.Y, Z = origin.Z,
            },
        ]);
        await camp.Host.World.InvokeAsync(() =>
        {
            camp.Destinations.Untakeable = (_, _) => true;
            return true;
        });
        Assert.True(await camp.ThinkAsync(), "the first list, built at the origin, has a cluster spawn to walk to");

        float x = 0, y = 0;
        await camp.Host.World.InvokeAsync(() =>
        {
            var player = camp.Session.Player!;
            PlayerbotMovementControl.Stop(camp.Session, player);
            // Just north of the spawn the first list dropped. The stale list's cluster is thousands of yards west.
            player.Relocate(camp.Origin.X + FarX, camp.Origin.Y + Beside, camp.Origin.Z, 0, camp.Host.World.NowMs);
            x = player.X;
            y = player.Y;
            return true;
        });
        Assert.True(await camp.ThinkAsync());
        await camp.Host.World.AdvanceClockAsync(500);
        await camp.Host.World.InvokeAsync(() =>
        {
            var player = camp.Session.Player!;
            // The walk is a heartbeat, not the think itself: the managed feature pumps every running bot's motion each tick, and this
            // session is not one of its bots, so the test pumps it (the think only started the walk).
            PlayerbotMotion.Pump(camp.Session, player, camp.Host.World.NowMs);
            float dx = player.X - x, dy = player.Y - y;
            Assert.True(dy < -1f, $"the bot should step south toward the dropped spawn, dy={dy:F2}");
            Assert.True(MathF.Abs(dx) < 1f, $"a stale list sends the bot west toward the old cluster, dx={dx:F2}");
            return true;
        });
    }
}
