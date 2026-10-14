using System.Collections;
using System.Numerics;
using System.Reflection;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The destination planner's two ways of planning meet one invariant: a refusal for want of a navigation tile still being read is no verdict
/// on the spawn. These cases cover the answered async plan replayed on the world thread (the lookup that hits a pending tile), and the
/// early guards that must not drop a route.
/// </summary>
public sealed class PlayerbotAsyncPendingTileTests
{
    private const float StartX = -8949.95f;
    private const uint SpawnGuid = 20001;

    private static CreatureContent Content() => new(
        [new CreatureTemplate { Entry = 6, Name = "objective", DisplayIds = [49] }],
        [new CreatureSpawn { Guid = SpawnGuid, Entry = 6, MapId = 0, X = StartX + 300, Y = -132.49f, Z = 83.53f }], [], [], []);

    private static T Field<T>(object owner, string name)
        => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static int CountOf(IEnumerable collection) => collection.Cast<object>().Count();

    private static int Count(object owner, string name)
    {
        object value = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        return value is ICollection c ? c.Count : CountOf((IEnumerable)value);
    }

    [Fact]
    public async Task AnsweredAsyncPlan_ReplayHittingAPendingTile_HoldsTheGoal_AndRefusesOnlyPastTheBound()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-async-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var nav = new NavMeshPathfinder(directory);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new ContentStore(Content()));
            services.AddSingleton<IWorldFeature, PlayerbotTileHoldTests.ManualClock>();
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        await using var executor = new PlayerbotNavigationExecutor(() => new AwayPathfinder(), capacity: 1);
        PlayerbotWorldDestinations? destinations = null;
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(pathfinder: nav);
                destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true, MaxRouteYards = 20_000 }, executor)
                {
                    // The world-thread replay of the answered plan meets a tile still being read (what a region lookup records).
                    PreparedPlanProbe = PathSearchCounters.RecordTilePending,
                };
                return true;
            });

            // Submit, then wait for the worker's answer to be consumed: that think replays the answered plan and meets the pending tile.
            bool replayed = false;
            for (int i = 0; i < 500 && !replayed; i++)
            {
                replayed = await host.OnWorldAsync(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(1);
                    destinations!.Update(session.Player!, 6, 500);
                    return destinations.WaitingForTiles;
                });
                if (!replayed) await Task.Delay(10);
            }

            Assert.True(replayed, "the answered plan was never replayed");

            // Held for the whole bound: nothing set aside, backed off or blacklisted, and the goal is still the bot's.
            for (int second = 3; second <= 15; second += 3)
            {
                await host.World.AdvanceClockAsync(3_000);
                await host.OnWorldAsync(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.False(destinations!.Update(session.Player!, 6, 500));
                    Assert.True(destinations.WaitingForTiles, $"waiting at +{second} s");
                    Assert.Equal(0, Count(destinations, "_blockedSpawns"));
                    Assert.Equal(0, Count(destinations, "_farBlockedUntil"));
                    Assert.Equal(0u, Field<uint>(destinations, "_backoffUntil"));
                    return true;
                });
            }

            // Past TileWait.MaxMs the refusal stands like any other: the spawn is set aside.
            await host.World.AdvanceClockAsync(3_000);
            await host.OnWorldAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(destinations!.Update(session.Player!, 6, 500));
                Assert.False(destinations.WaitingForTiles);
                Assert.Equal(1, Count(destinations, "_blockedSpawns"));
                return true;
            });
        }
        finally
        {
            if (destinations is not null) await host.OnWorldAsync(() => destinations!.Stop());
            await executor.DisposeAsync();
            session.Kick();
            await session.ManagedClosed;
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task TransientGuards_KeepTheRoute_WhileDeathDropsIt()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new ContentStore(Content()));
            services.AddSingleton<IWorldFeature, PlayerbotTileHoldTests.ManualClock>();
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new StraightPathfinder());
                var destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true });
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(destinations.Update(session.Player!, 6, 500));
                Assert.NotNull(Field<object?>(destinations, "_route"));

                // A zero step is a transient guard: it answers false and leaves the route and the trip alone (before the cluster lanes,
                // Update returned false here without Stop()).
                Assert.False(destinations.Update(session.Player!, 6, 0));
                Assert.NotNull(Field<object?>(destinations, "_route"));
                Assert.Equal(6u, destinations.TargetEntry);

                // Death ends the trip.
                Player player = session.Player!;
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.False(player.IsAlive);
                Assert.False(destinations.Update(player, 6, 500));
                Assert.Null(Field<object?>(destinations, "_route"));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>A partial answer that leads away from the goal: the replay refuses it without any fallback query, so only a pending tile can make it wait.</summary>
    private sealed class AwayPathfinder : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(PathType.Normal | PathType.Incomplete, [start, start + new Vector3(-20, 0, 0)]);
    }

    private sealed class StraightPathfinder : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => PathResult.StraightLine(start, end, PathType.Normal);
    }

    private sealed class ContentStore(CreatureContent content) : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
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
}
