using System.Numerics;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A refusal for want of a navigation tile still being read in the background (<see cref="PlanOutcome.Pending"/>) is not a verdict on
/// the destination: the callers hold their goal and ask again, for <see cref="TileWait.MaxMs"/>, and only then does the refusal stand.
/// The world clock is manual, so no test here waits.
/// </summary>
public sealed class PlayerbotTileHoldTests
{
    private const float StartX = -8949.95f;

    // --- the wait itself (fake clock) -----------------------------------------------------------------------------------------

    [Fact]
    public void TileWait_HoldsForFifteenSeconds_ThenTheRefusalStands_UntilTheRunBreaksOffOrAPlanSucceeds()
    {
        PathSearchCounters.RecordTilePending(); // a pathfinder that only answers pending: bounded by the game clock alone
        var wait = new TileWait();
        for (uint t = 1_000; t <= 1_000 + TileWait.MaxMs; t += 3_000)
            Assert.True(wait.Hold(t), $"held at +{t - 1_000} ms");
        Assert.True(wait.Hold(1_000 + TileWait.MaxMs));
        Assert.False(wait.Hold(1_000 + TileWait.MaxMs + 1));
        Assert.False(wait.Hold(1_000 + TileWait.MaxMs + 2_000));
        Assert.False(wait.Active);

        Assert.True(wait.Hold(1_000 + TileWait.MaxMs + 2_000 + TileWait.GapMs + 1), "a pause longer than the gap starts a new wait");
        wait.Reset();
        Assert.False(wait.Active);
        Assert.True(wait.Hold(500_000));
    }

    [Fact]
    public void TileWait_OnARealBackgroundRead_IsSpentOnlyWhenBothTheGameClockAndTheWallClockPassedTheBound()
    {
        long realMs = 0;
        var wait = new TileWait { RealTicks = () => realMs * System.Diagnostics.Stopwatch.Frequency / 1000 };
        PathSearchCounters.RecordTilePendingOnRealRead();
        Assert.True(wait.Hold(1_000));

        // A world running faster than real time: 20 s of game time, 1 s of wall time. The read had no time: keep waiting.
        for (uint t = 4_000; t <= 21_000; t += 3_000) Assert.True(wait.Hold(t), $"held at game +{t - 1_000} ms");
        realMs = 1_000;
        Assert.True(wait.Hold(22_000));

        // The wall clock catches up to the bound, not past it: still held.
        realMs = TileWait.MaxMs;
        Assert.True(wait.Hold(23_000));

        // Both clocks past the bound: the refusal stands, and keeps standing.
        realMs = TileWait.MaxMs + 1;
        Assert.False(wait.Hold(24_000));
        Assert.False(wait.Hold(25_000));
        Assert.False(wait.Active);
    }

    [Fact]
    public void TileWait_OnARealBackgroundRead_WallClockAloneNeverSpendsIt()
    {
        long realMs = 0;
        var wait = new TileWait { RealTicks = () => realMs * System.Diagnostics.Stopwatch.Frequency / 1000 };
        PathSearchCounters.RecordTilePendingOnRealRead();
        Assert.True(wait.Hold(1_000));
        realMs = 60_000;
        Assert.True(wait.Hold(2_000)); // 1 s of game time since the start of the wait
        Assert.True(wait.Active);
    }

    [Fact]
    public void TileWait_OnAPathfinderThatOnlyAnswersPending_IsSpentOnTheGameClockAlone()
    {
        long realMs = 0;
        var wait = new TileWait { RealTicks = () => realMs * System.Diagnostics.Stopwatch.Frequency / 1000 };
        PathSearchCounters.RecordTilePending();
        for (uint t = 1_000; t <= 1_000 + TileWait.MaxMs; t += 3_000) Assert.True(wait.Hold(t));
        Assert.False(wait.Hold(1_000 + TileWait.MaxMs + 1), "no read in flight: nothing to give wall time to");
    }

    [Fact]
    public void TileWait_CountsAcrossTheClockWrapAndFromZero()
    {
        PathSearchCounters.RecordTilePending();
        var wrapped = new TileWait();
        uint start = uint.MaxValue - 4_000;
        Assert.True(wrapped.Hold(start));
        Assert.True(wrapped.Hold(unchecked(start + 5_000)));
        Assert.True(wrapped.Hold(unchecked(start + 10_000)));
        Assert.True(wrapped.Hold(unchecked(start + 15_000)));
        Assert.False(wrapped.Hold(unchecked(start + 16_000)));

        var zero = new TileWait();
        Assert.True(zero.Hold(0));
        Assert.True(zero.Active);
        Assert.True(zero.Hold(5_000));
        Assert.True(zero.Hold(10_000));
        Assert.True(zero.Hold(15_000));
        Assert.False(zero.Hold(15_002));
    }

    // --- the planning call, on the real pathfinder ------------------------------------------------------------------------------

    [Fact]
    public async Task PlanToward_IsPendingWhileTheTileIsRead_RefusedPastTheBound_AndRouteOnceTheTileIsIn()
    {
        using var fixture = new NavMeshFixture();
        using var gate = new ManualResetEventSlim();
        bool armed = false;
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(fixture, [],
            unloadedTiles: [(31, 31, new CellTile(0, 0, 0, 0, 8, 8, Size: 5))],
            configure: nav => nav.FileReader = path =>
            {
                if (Volatile.Read(ref armed) && path.EndsWith(".mmtile", StringComparison.Ordinal)) gate.Wait();
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            });
        try
        {
            await world.PlaceAsync(new Vector3(5, 5, 0));
            await world.Host.OnWorldAsync(() => world.Nav.UnloadTile(0, 31, 31));
            Volatile.Write(ref armed, true);
            Vector3 goal = new(30, 5, 0);
            var options = new PlayerbotOptions { MaxRouteYards = 30, MaxPathPoints = 22 };
            long realMs = 0;
            var wait = new TileWait { RealTicks = () => realMs * System.Diagnostics.Stopwatch.Frequency / 1000 };

            PlanOutcome Plan(uint now) => PlayerbotNavigation.PlanToward(world.Player, goal, options, ref wait, now, out _);

            Assert.Equal(PlanOutcome.Pending, await world.Host.OnWorldAsync(() => Plan(10_000)));
            foreach (uint at in new uint[] { 15_000, 20_000, 25_000 })
                Assert.Equal(PlanOutcome.Pending, await world.Host.OnWorldAsync(() => Plan(at)));
            // The tile is really being read on a background thread: game time past the bound is not enough while the wall clock has not.
            Assert.Equal(PlanOutcome.Pending, await world.Host.OnWorldAsync(() => Plan(10_000 + TileWait.MaxMs + 1)));
            Interlocked.Exchange(ref realMs, TileWait.MaxMs + 1);
            Assert.Equal(PlanOutcome.Refused, await world.Host.OnWorldAsync(() => Plan(10_000 + TileWait.MaxMs + 2)));

            gate.Set();
            await world.Nav.PendingTileRead(0, 31, 31)!;
            PlayerbotRoute? route = null;
            PlanOutcome done = await world.Host.OnWorldAsync(() =>
                PlayerbotNavigation.PlanToward(world.Player, goal, options, ref wait, 10_000 + TileWait.MaxMs + 3, out route));
            Assert.Equal(PlanOutcome.Route, done);
            Assert.NotNull(route);
            Assert.False(wait.Active);
        }
        finally { gate.Set(); }
    }

    // --- the destination planner (the wait of PlayerbotWorldDestinations) ---------------------------------------------------------

    [Fact]
    public async Task WorldDestinations_HoldTheGoalWhilePending_AndSetTheSpawnAsideOnlyPastTheBound()
    {
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 6, Name = "objective", DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 20001, Entry = 6, MapId = 0, X = StartX + 300, Y = -132.49f, Z = 83.53f },
             new CreatureSpawn { Guid = 20002, Entry = 6, MapId = 0, X = StartX - 400, Y = -132.49f, Z = 83.53f }], [], [], []);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new Content(content));
            services.AddSingleton<IWorldFeature, ManualClock>();
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            var pathfinder = new PendingPathfinder { Pending = true };
            PlayerbotWorldDestinations? destinations = null;
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true });
                return true;
            });

            for (int second = 0; second <= 15; second += 3)
            {
                await host.OnWorldAsync(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.False(destinations!.Update(session.Player!, 6, 500));
                    Assert.True(destinations.WaitingForTiles, $"waiting at {second} s");
                    return true;
                });
                await host.World.AdvanceClockAsync(3_000);
            }

            await host.OnWorldAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(destinations!.Update(session.Player!, 6, 500));
                Assert.False(destinations.WaitingForTiles);
                return true;
            });

            pathfinder.Pending = false;
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                float before = player.X;
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(destinations!.Update(player, 6, 500));
                PlayerbotMotion.ElapseForTests(player, 500);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(destinations.Update(player, 6, 500));
                Assert.True(player.X < before, "the bot goes to the other spawn, west");
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task WorldDestinations_ClearWaitingForTilesOnEveryEntry_EvenWhenTheEarlyGuardsRefuse()
    {
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 6, Name = "objective", DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 20001, Entry = 6, MapId = 0, X = StartX + 300, Y = -132.49f, Z = 83.53f }], [], [], []);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new Content(content));
            services.AddSingleton<IWorldFeature, ManualClock>();
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new PendingPathfinder { Pending = true });
                var destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true });
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(destinations.Update(session.Player!, 6, 500));
                Assert.True(destinations.WaitingForTiles);
                Assert.False(destinations.Update(session.Player!, 6, 0));
                Assert.False(destinations.WaitingForTiles);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    // --- the trainer walk -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TrainerWalk_HoldsAPendingTrainer_NeverBlacklistsIt()
    {
        var fixture = new FarTrainerFixture { TwoSpawns = true };
        FarTrainerServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton<ISpellContentStore>(fixture);
                services.AddSingleton<IWorldFeature, ManualClock>();
            });
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                var pathfinder = new PendingPathfinder { Pending = true };
                PlayerbotTrainerDestinations? route = null;
                await host.OnWorldAsync(() =>
                {
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                    session.Player!.Money = 100;
                    route = new PlayerbotTrainerDestinations(session, new PlayerbotOptions { Enabled = true });
                    return true;
                });

                for (int second = 0; second <= 15; second += 3)
                {
                    await host.OnWorldAsync(() =>
                    {
                        session.ManagedBudget = new ManagedActionBudget(1);
                        Assert.True(route!.Update(session.Player!, 500), $"held at {second} s");
                        Assert.Equal(StartX, session.Player!.X, 2);
                        return true;
                    });
                    await host.World.AdvanceClockAsync(3_000);
                }

                pathfinder.Pending = false;
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(route!.Update(player, 500));
                    PlayerbotMotion.ElapseForTests(player, 500);
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(route.Update(player, 500));
                    Assert.True(player.X > StartX, "towards the nearest trainer, east");
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { FarTrainerServices.Current.Value = null; }
    }

    [Fact]
    public async Task TrainerWalk_AcceptsThePendingRefusal_PastTheBound_AndRotatesToTheOtherTrainer()
    {
        var fixture = new FarTrainerFixture { TwoSpawns = true };
        FarTrainerServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton<ISpellContentStore>(fixture);
                services.AddSingleton<IWorldFeature, ManualClock>();
            });
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                var pathfinder = new PendingPathfinder { Pending = true };
                PlayerbotTrainerDestinations? route = null;
                await host.OnWorldAsync(() =>
                {
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                    session.Player!.Money = 100;
                    route = new PlayerbotTrainerDestinations(session, new PlayerbotOptions { Enabled = true });
                    return true;
                });
                for (int second = 0; second <= 18; second += 3)
                {
                    bool held = await host.OnWorldAsync(() =>
                    {
                        session.ManagedBudget = new ManagedActionBudget(1);
                        return route!.Update(session.Player!, 500);
                    });
                    Assert.Equal(second <= 15, held);
                    await host.World.AdvanceClockAsync(3_000);
                }

                pathfinder.Pending = false;
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(route!.Update(player, 500));
                    PlayerbotMotion.ElapseForTests(player, 500);
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(route.Update(player, 500));
                    Assert.True(player.X < StartX, "the east trainer was set aside after the bound: the west one is walked to");
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { FarTrainerServices.Current.Value = null; }
    }

    // --- the spirit healer's long way round ---------------------------------------------------------------------------------------

    /// <summary>
    /// The once-per-death detour to the spirit healer is not spent by a plan that waits for a tile: the walk stalls (near searches
    /// refuse), the long search answers "pending" first and then finds the way; the detour is taken then.
    /// </summary>
    [Fact]
    public async Task HealerDetour_IsNotSpentByAPendingPlan()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            var pathfinder = new PendingPathfinder
            {
                Pending = true,
                PendingFor = o => o is { MaxSearchNodes: >= PlayerbotNavigation.MaxSearchNodes },
                RefuseFor = o => o is not { MaxSearchNodes: >= PlayerbotNavigation.MaxSearchNodes },
            };
            Player player = session.Player!;
            Creature? healer = null;
            PlayerbotRecovery? recovery = null;
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.True(player.Map!.Combat.RepopPlayer(player));
                player.Combat.Corpse!.SetPosition(player.X + 60, player.Y, player.Z, 0);
                healer = ArcaneCore.World.Tests.Playerbots.Dungeon.DungeonBotHost.AddCreature(player.Map!, 994701u, player.X - 30, player.Y,
                    player.Z, (uint)ArcaneCore.Game.Npc.NpcFlags.SpiritHealer, host.World.NowMs, faction: 1);
                recovery = new PlayerbotRecovery(session, new PlayerbotOptions { Enabled = true, MaxRouteYards = 200, AllowedMaps = [0, 1] });
                recovery.TakeSpiritHealer();
                return true;
            });
            await host.WaitForWorldAsync(() => player.VisibleObjects.Contains(healer!.Guid), "the ghost sees the healer");

            var steps = new List<PlayerbotSpiritHealerStep?>();
            int pendingDetourAt = -1, openedAt = -1;
            bool detourTaken = false;
            for (int think = 0; think < 120 && !detourTaken; think++)
            {
                await host.OnWorldAsync(() =>
                {
                    PlayerbotMovementControl.Update(session, player);
                    session.ManagedBudget = new ManagedActionBudget(4);
                    recovery!.Update(player, 500);
                    steps.Add(recovery.LastSpiritHealerStep);
                    if (openedAt < 0 && pendingDetourAt < 0 && recovery.LastSpiritHealerStep == PlayerbotSpiritHealerStep.DetourToHealer)
                    {
                        pendingDetourAt = think; // the detour was tried while its tiles were being read
                        pathfinder.Pending = false;
                        openedAt = think;
                    }
                    else if (openedAt >= 0 && think > openedAt && recovery.LastSpiritHealerStep == PlayerbotSpiritHealerStep.DetourToHealer)
                    {
                        detourTaken = true;
                    }

                    return true;
                });
                await host.World.AdvanceClockAsync(500);
            }

            string trail = string.Join(",", steps.Select(s => s?.ToString() ?? "-").Distinct());
            Assert.True(pendingDetourAt >= 0, "the pending long search never showed as the detour step: " + trail);
            Assert.True(detourTaken, "the detour was spent by the pending plan: " + trail);
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    // --- nobody forgets pending ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The bool planning calls cannot tell "tile still being read" from "unreachable". Production code outside this allowlist plans
    /// through the tri-state calls (<see cref="PlayerbotNavigation.PlanToward"/>, PlanExact, PlanFar); the allowlisted callers either plan
    /// to weigh something (trial, unguarded), flee (never wait), or already retry a refusal on a timer without setting anything aside.
    /// </summary>
    [Fact]
    public void ProductionCallers_PlanThroughTheTriStateCalls()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "ArcaneCore.Playerbots"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        string[] allowed = ["PlayerbotNavigation.cs", "PlayerbotWater.cs", "PlayerbotRisk.cs", "PlayerbotRetreat.cs", "PlayerbotRecovery.cs",
            "PlayerbotElevator.cs", "PlayerbotTram.cs"];
        var bare = new System.Text.RegularExpressions.Regex(@"PlayerbotNavigation\.(TryPlan|TryPlanToward|TryPlanFar|TryPlanUnguarded)\(");
        var offenders = new List<string>();
        int scanned = 0;
        foreach (string file in Directory.EnumerateFiles(Path.Combine(dir!, "src", "ArcaneCore.Playerbots"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || allowed.Contains(Path.GetFileName(file))) continue;
            scanned++;
            foreach (string line in File.ReadLines(file))
                if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal) && bare.IsMatch(line)) offenders.Add(Path.GetFileName(file) + ": " + line.Trim());
        }

        Assert.True(scanned > 50, "the scan found the sources");
        Assert.Empty(offenders);
    }

    // --- stand-ins ------------------------------------------------------------------------------------------------------------------

    /// <summary>Answers every search "tile pending" (recording it as the real pathfinder does) while <see cref="Pending"/>, else a straight line.</summary>
    internal sealed class PendingPathfinder : IPathfinder
    {
        public bool Enabled => true;
        public bool Pending { get; set; }
        public Func<PathOptions?, bool>? PendingFor { get; set; }
        public Func<PathOptions?, bool>? RefuseFor { get; set; }

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            if (Pending && (PendingFor?.Invoke(options) ?? true))
            {
                PathSearchCounters.RecordTilePending();
                return new PathResult(PathType.TilesPending, [start]);
            }

            return RefuseFor?.Invoke(options) == true ? PathResult.None(start) : PathResult.StraightLine(start, end, PathType.Normal);
        }
    }

    internal sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    private sealed class Content(CreatureContent content) : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }
}
