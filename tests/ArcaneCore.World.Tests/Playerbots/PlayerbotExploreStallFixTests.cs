using System.Numerics;
using System.Reflection;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Risk;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Live bot-QA stalls of 2026-10-24: the stall diagnostic taken after the give-up (it read <c>moving:yes</c> for a bot that had stood
/// still), #1465 (Winterspring: level-capped bots killing respawns of a no-coin beast; Explore holding silently for a pending path) and
/// #1464 (Feralas Twin Colossals: wander rays refused on broken steep ground, one component of the walking graph to another).
/// </summary>
public sealed class PlayerbotExploreStallFixTests
{
    private const float StartX = -8949.95f, StartY = -132.49f, StartZ = 83.53f;

    private static string Snapshot(PlayerbotBrain brain, Player player) => brain.FormatDiagnosticSnapshot(player);

    // --- the stall diagnostic is taken before the give-up -------------------------------------------------------------------------

    /// <summary>
    /// A bot stands still for the stall bound; on the give-up think the same think plans a new explore leg. The logged diagnostic
    /// (<see cref="PlayerbotBrain.StallSnapshot"/>, taken at the give-up) has to describe the bot that stood still (<c>moving:no</c>), not the
    /// leg planned after it (<c>moving:yes,wp:1/N</c>, which misled triage of #1464 and #1465).
    /// </summary>
    [Fact]
    public async Task StallDiagnostic_DescribesTheStandingBot_NotTheLegPlannedAfterTheGiveUp()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, PlayerbotTileHoldTests.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, StallSeconds = 10, Risk = { Enabled = false } });
        // Every path is refused until the stall is reported (the think that gives the goal up), then the ground opens.
        var pathfinder = new GatePathfinder(() => brain.StallCount > 0);
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                return true;
            });
            string? snapshot = null, after = null;
            for (int think = 0; think < 200 && snapshot is null; think++)
            {
                (snapshot, after) = await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    session.ManagedBudget = new ManagedActionBudget(4);
                    brain.Update(100);
                    PlayerbotMotion.ElapseForTests(player, 100);
                    return brain.StallSnapshot is { } taken ? (taken, Snapshot(brain, player)) : (null, null);
                });
                await host.World.AdvanceClockAsync(100);
            }

            Assert.NotNull(snapshot);
            Assert.Contains("moving:no", snapshot);
            Assert.Contains("wp:0/0", snapshot);
            Assert.Contains("moving:yes", after); // the same think did plan a new leg: the test is not vacuous
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    // --- #1465 fix A: kills with loot rights are progress -------------------------------------------------------------------------

    /// <summary>
    /// Bot QA #1465, Winterspring: level-60 bots with full bags fought respawns of a no-coin beast at the same spawn points and stalled every
    /// 120 s (no experience at the cap, no item, no coin: no field of the fingerprint changed). A kill the bot had loot rights to is progress now.
    /// </summary>
    [Fact]
    public async Task LevelCappedBot_KillingNoCoinRespawnsAtOneSpot_IsNotStalled()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync(o => { o.StallSeconds = 120; o.Risk.Enabled = false; });
        CreatureTemplate template = RiskTestWorld.Template(993301, level: 60, health: 20, minDamage: 0, maxDamage: 0);
        await world.OnWorldAsync(() =>
        {
            world.Player.Level = 60;
            // The ground refuses every walk, as at a boxed-in spawn: only the kills can be the bot's progress.
            WorldCollision.Of(world.Host.World).Install(pathfinder: new GatePathfinder(() => false));
            return true;
        });
        uint startMs = world.Host.World.NowMs;
        int kills = 0;
        while (world.Host.World.NowMs - startMs < 150_000)
        {
            uint roundStart = world.Host.World.NowMs;
            Creature mob = await world.OnWorldAsync(() => world.Spawn(template, 3, 0));
            await world.SeeAsync(mob);
            bool engaged = await world.ThinkUntilAsync(10_000, () => world.Brain.TargetEntry == template.Entry || !mob.IsAlive);
            Assert.True(engaged, "the bot never took the beast as its target");
            await world.OnWorldAsync(() =>
            {
                if (mob.IsAlive) world.Player.Map!.Combat.DealDamage(world.Player, mob, mob.Health, direct: true, meleeDamage: true);
                return true;
            });
            kills++;
            uint spent = world.Host.World.NowMs - roundStart;
            await world.ThinkUntilAsync(spent >= 30_000 ? 100u : 30_000u - spent, () => false);
            Assert.Equal(0, await world.OnWorldAsync(() => world.Brain.StallCount));
        }

        Assert.True(kills >= 5, $"only {kills} kills in the window");
        Assert.True(await world.OnWorldAsync(() => world.Brain.LootedKills) >= 5u, "the brain did not count the kills it had loot rights to");
        Assert.Equal(0, await world.OnWorldAsync(() => world.Brain.StallCount));
    }

    // --- #1465 fix B: the explore pending hold is bounded and traced -------------------------------------------------------------

    /// <summary>
    /// A path seam that answers Pending for the explore candidate forever (a path job that is never answered, or a stale answer that restarts
    /// the wait each time): Explore held silently until the stall watch. It holds for <see cref="PlayerbotBrain.ExplorePendingMaxMs"/> and
    /// records <c>explore-pending</c>; then the short wander leg runs.
    /// </summary>
    [Fact]
    public async Task ExplorePending_ForeverForTheCandidate_IsTraced_AndTheWanderLegRunsAfterTheBound()
    {
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 99101, Name = "right", MinLevel = 8, MaxLevel = 8, Faction = 14 }],
            [new CreatureSpawn { Guid = 99101, Entry = 99101, MapId = 0, X = StartX + 140, Y = StartY, Z = StartZ }], [], [], []);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new ContentStore(content));
            services.AddSingleton<IWorldFeature, PlayerbotTileHoldTests.ManualClock>();
            services.AddSingleton(new FactionTemplateCatalog([
                new FactionTemplateRecord(1, 1, 0, 1, 0, 0),
                new FactionTemplateRecord(14, 0, 0, 8, 0, 1)]));
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } });
        var pathfinder = new PhasePendingPathfinder("explore plan");
        try
        {
            Vector3 start = await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Level = 8;
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                return new Vector3(player.X, player.Y, player.Z);
            });
            uint startMs = host.World.NowMs;
            uint movedAtMs = 0;
            string snapshot = "";
            for (int think = 0; think < 200 && movedAtMs == 0; think++)
            {
                (bool moved, snapshot) = await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    var probe = new PlayerbotThinkProbe();
                    probe.Begin();
                    try
                    {
                        session.ManagedBudget = new ManagedActionBudget(8);
                        brain.Update(100);
                    }
                    finally { probe.End(); }
                    PlayerbotMotion.ElapseForTests(player, 100);
                    return (Vector3.Distance(start, new Vector3(player.X, player.Y, player.Z)) > 2f, Snapshot(brain, player));
                });
                await host.World.AdvanceClockAsync(100);
                if (moved) movedAtMs = host.World.NowMs - startMs;
            }

            Assert.True(pathfinder.PendingAnswers > 0, "the candidate was never planned: the test proves nothing");
            Assert.True(movedAtMs != 0 && movedAtMs <= 13_000, $"the bot first moved at +{movedAtMs} ms (held for the pending candidate)");
            Assert.True(movedAtMs >= PlayerbotBrain.ExplorePendingMaxMs - 1_000, $"the bot left the hold at +{movedAtMs} ms, before the bound");
            Assert.Contains("explore-pending", snapshot);
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    // --- #1464: the wander component pre-check and refusal reasons ---------------------------------------------------------------

    private static async Task<(PlayerbotBrain Brain, PlayerbotRoute? Route, string Trace, int Rotations)> WanderOnceAsync(
        IPathfinder pathfinder, Func<Player, Vector3, int>? components, int thinks = 1)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, Risk = { Enabled = false } })
        {
            WanderComponentProbe = components,
        };
        try
        {
            return await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.SetPosition(100f, 100f, StartZ, 0f);
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                MethodInfo explore = typeof(PlayerbotBrain).GetMethod("Explore", BindingFlags.Instance | BindingFlags.NonPublic)!;
                FieldInfo routeField = typeof(PlayerbotBrain).GetField("_route", BindingFlags.Instance | BindingFlags.NonPublic)!;
                for (int i = 0; i < thinks; i++)
                {
                    session.ManagedBudget = new ManagedActionBudget(2);
                    explore.Invoke(brain, [player, 500u]);
                }

                return (brain, routeField.GetValue(brain) as PlayerbotRoute, Snapshot(brain, player), brain.WanderComponentRotations);
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>The bot stands in component 1 at x = 100; the ground east of it (x &gt; 100.5) is component 2, a plateau cut off by slopes.</summary>
    private static int EastIsAnotherComponent(Player player, Vector3 point) => point.X > 100.5f ? 2 : 1;

    [Fact]
    public async Task WanderRayIntoAnotherComponent_IsRotatedAway()
    {
        var pathfinder = new CountingPathfinder();
        // Facing east, the first rays point east (into the other component): they are turned until one stays in component 1.
        (_, PlayerbotRoute? route, _, int rotations) = await WanderOnceAsync(pathfinder, EastIsAnotherComponent);
        Assert.NotNull(route);
        Assert.True(rotations >= 1, "no ray was turned away from the other component: the test proves nothing");
        Assert.True(route!.Points[^1].X <= 100.5f, $"the leg ends in the other component at x={route.Points[^1].X:F1}");
        Assert.All(pathfinder.Ends, end => Assert.True(end.X <= 100.5f, $"a path was asked toward the other component, x={end.X:F1}"));
    }

    [Fact]
    public async Task WanderRayIntoTheSameComponent_IsKept()
    {
        var pathfinder = new CountingPathfinder();
        (_, PlayerbotRoute? route, _, int rotations) = await WanderOnceAsync(pathfinder, (_, _) => 1);
        Assert.NotNull(route);
        Assert.Equal(0, rotations);
        Assert.True(pathfinder.Ends.Count > 0);
    }

    [Fact]
    public async Task WanderRaysAllIntoOtherComponents_AreRefusedWithoutAPathQuery_AsComponent()
    {
        var pathfinder = new CountingPathfinder();
        Vector3 bot = new(100f, 100f, StartZ);
        (PlayerbotBrain brain, PlayerbotRoute? route, string trace, int rotations) = await WanderOnceAsync(pathfinder,
            (_, point) => Vector2.Distance(new(point.X, point.Y), new(bot.X, bot.Y)) < 0.5f ? 1 : 2);
        Assert.Null(route);
        Assert.Equal(PlayerbotBrain.WanderComponentTries, rotations);
        Assert.Empty(pathfinder.Ends);
        Assert.Contains("wander-refused:component", trace);
    }

    [Fact]
    public async Task WanderRefusalReason_IsTraced_ForAShortfall()
    {
        (_, PlayerbotRoute? route, string trace, _) = await WanderOnceAsync(new ShortOpenPathfinder(12f), null);
        Assert.Null(route);
        Assert.Contains("wander-refused:shortfall", trace);
    }

    [Fact]
    public async Task WanderRefusalReason_IsTraced_ForNoPath()
    {
        (_, PlayerbotRoute? route, string trace, _) = await WanderOnceAsync(new GatePathfinder(() => false), null);
        Assert.Null(route);
        Assert.Contains("wander-refused:no-path", trace);
    }

    // --- fixtures -------------------------------------------------------------------------------------------------------------------

    /// <summary>Straight paths once <c>open</c> says so, a refusal (no path) until then.</summary>
    private sealed class GatePathfinder(Func<bool> open) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => open() ? PathResult.StraightLine(start, end, PathType.Normal) : PathResult.None(start);
    }

    /// <summary>Straight paths that records each requested end.</summary>
    private sealed class CountingPathfinder : IPathfinder
    {
        public bool Enabled => true;
        public List<Vector3> Ends { get; } = [];

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Ends.Add(end);
            return PathResult.StraightLine(start, end, PathType.Normal);
        }
    }

    /// <summary>Completes a ray of at most <paramref name="openYards"/>; a longer ray ends there, short of its point.</summary>
    private sealed class ShortOpenPathfinder(float openYards) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            float dx = end.X - start.X, dy = end.Y - start.Y;
            float distance = MathF.Sqrt((dx * dx) + (dy * dy));
            if (!float.IsFinite(distance) || distance <= openYards + 0.05f) return PathResult.StraightLine(start, end, PathType.Normal);
            float scale = openYards / distance;
            return PathResult.StraightLine(start, new Vector3(start.X + (dx * scale), start.Y + (dy * scale), end.Z), PathType.Normal | PathType.Incomplete);
        }
    }

    /// <summary>Answers "tiles pending" for every path asked in think phase <c>phase</c> (never an answer), straight paths otherwise.</summary>
    private sealed class PhasePendingPathfinder(string phase) : IPathfinder
    {
        public bool Enabled => true;
        public int PendingAnswers { get; private set; }

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            if (PlayerbotThinkProbe.CurrentPhase == phase)
            {
                PendingAnswers++;
                PathSearchCounters.RecordTilePending();
                return new PathResult(PathType.TilesPending, [start]);
            }

            return PathResult.StraightLine(start, end, PathType.Normal);
        }
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
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => StartZ;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }
}
