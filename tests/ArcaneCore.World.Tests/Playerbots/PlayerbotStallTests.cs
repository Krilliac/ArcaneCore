using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The watch that reports a bot making no progress (<see cref="PlayerbotStallWatch"/>) instead of letting it idle silently, as
/// Ironwander, Graveweaver and Dawnrover did on the live server for hours (2026-10-08). Time moves only on the manual world clock.
/// </summary>
public sealed class PlayerbotStallTests
{
    /// <summary>
    /// The host takes the stall diagnostic after the think, once it has marked the probe "stall log". The snapshot has to name the phase
    /// the think's work stopped in, not the host's bookkeeping phase (live: 2114 of 2163 Explore stall lines read phase=stall-log).
    /// </summary>
    [Fact]
    public async Task StallSnapshot_NamesTheThinkPhase_NotTheHostsStallLogPhase()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
        try
        {
            string? snapshot = null;
            for (int think = 0; think < 100 && snapshot is null; think++)
            {
                await host.World.AdvanceClockAsync(100);
                snapshot = await host.OnWorldAsync(() =>
                {
                    var probe = new PlayerbotThinkProbe();
                    probe.Begin();
                    try
                    {
                        session.ManagedBudget = new ManagedActionBudget(4);
                        brain.Update(100);
                        if (brain.LastThinkPhase != "explore wander plan") return null;
                        PlayerbotThinkProbe.Phase("settle");
                        PlayerbotThinkProbe.Phase("stall log");
                        return brain.FormatDiagnosticSnapshot(session.Player!);
                    }
                    finally { probe.End(); }
                });
            }

            Assert.NotNull(snapshot);
            Assert.Contains("phase=explore-wander-plan", snapshot);
            Assert.DoesNotContain("phase=stall-log", snapshot);
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// A bot that cannot go anywhere (no terrain: every route is refused) and changes nothing reports a stall once
    /// <see cref="PlayerbotOptions.StallSeconds"/> of world time passed, with its goal and position; the report clears when it moves.
    /// </summary>
    [Fact]
    public async Task ABotThatMakesNoProgress_ReportsAStall_AndTheReportClearsWhenItMoves()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, StallSeconds = 10 });
        try
        {
            for (int think = 0; think < 50; think++)
            {
                await host.OnWorldAsync(() => { session.ManagedBudget = new ManagedActionBudget(4); brain.Update(100); return true; });
                await host.World.AdvanceClockAsync(100);
            }

            Assert.Null(await host.OnWorldAsync(() => brain.StallReport)); // 5 seconds: not yet
            bool stalled = false;
            for (int think = 0; think < 100 && !stalled; think++)
            {
                await host.World.AdvanceClockAsync(100);
                stalled = await host.OnWorldAsync(() => { session.ManagedBudget = new ManagedActionBudget(4); brain.Update(100); return brain.StallReport is not null; });
            }

            Assert.True(stalled, "no stall reported after 15 seconds without progress");
            string report = (await host.OnWorldAsync(() => brain.StallReport))!;
            Assert.StartsWith("stalled 1", report);
            Assert.Contains("goal=Explore", report);
            Assert.Equal(1, await host.OnWorldAsync(() => brain.StallCount));

            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Relocate(player.X + 20, player.Y, player.Z, player.Orientation, host.World.NowMs);
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500); // one whole think
                Assert.Null(brain.StallReport);
                Assert.Equal(report, brain.LastStall);
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// A stall that outlasts the give-up gives the goal up again after each further stall bound, still reported and counted as one
    /// stall. Before, the goal was given up only once, when the stall began: Ironwander (live 2026-10-09) was reported
    /// <c>stalled 7960s</c> and gave nothing up after its first two minutes.
    /// </summary>
    [Fact]
    public async Task AStallThatOutlastsTheGiveUp_GivesTheGoalUpAgainEachStallBound()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, StallSeconds = 10 });
        try
        {
            // No terrain: every route is refused and nothing changes. 35 seconds: the stall at 10, again at 20 and 30.
            for (int think = 0; think < 350; think++)
            {
                await host.OnWorldAsync(() => { session.ManagedBudget = new ManagedActionBudget(4); brain.Update(100); return true; });
                await host.World.AdvanceClockAsync(100);
            }

            Assert.StartsWith("stalled 3", await host.OnWorldAsync(() => brain.StallReport));
            Assert.Equal(1, await host.OnWorldAsync(() => brain.StallCount));
            Assert.Equal(3, await host.OnWorldAsync(() => brain.StallGiveUps));
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// The stall of a running managed bot is visible where an operator looks: the error column of <c>.playerbot list</c> (and
    /// <c>status</c>). Before, a bot standing still for hours showed <c>state=Running error=none</c>.
    /// </summary>
    [Fact]
    public async Task AStalledManagedBot_ShowsTheStallInPlayerbotList()
    {
        // The daemon registers the command's service (AddWorldDaemon); the scenario world registers only the feature.
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(services =>
            services.AddSingleton<IPlayerbotService>(sp => sp.GetRequiredService<ManagedPlayerbotFeature>()));
        Guid id = Assert.IsType<Guid>((await world.Bots.CreateAsync("Stuckbot", 1, 1)).BotId);
        Assert.True((await world.Bots.StartAsync(id.ToString())).Success);

        // No floor in this world: the bot cannot plan any route, so after its first decisions nothing changes.
        bool stalled = await world.Host.World.AdvanceClockUntilAsync(600_000,
            () => world.Bots.Snapshot().Any(s => s.BotId == id && s.ErrorCode?.StartsWith("stalled", StringComparison.Ordinal) == true));
        Assert.True(stalled, "no stall in the bot's status after ten minutes of game time");
        PlayerbotStatus status = world.Bots.Snapshot().Single(s => s.BotId == id);
        Assert.Equal(ManagedPlayerbotState.Running, status.State);

        // The operator logs in now: ten minutes of game time without a packet would have timed an earlier session out.
        await using WorldTestClient admin = await world.EnterWorldAsync("STALLADMIN", "Stalladmin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot list");
        string line;
        do line = (await admin.ReadChatAsync()).Text;
        while (!line.Contains("Stuckbot", StringComparison.Ordinal));
        Assert.Contains("state=Running", line);
        Assert.Contains("error=stalled ", line);
        Assert.Contains("map 0", line);
    }

    /// <summary>
    /// A bot with a town home that has nothing to do stands at the inn of its town (<see cref="PlayerbotTownIdle"/>): that is the idle rule,
    /// not a stall. Before, the watch reported it every stall bound (live 2026-10-17, bot QA "stall|goal=Explore": 1 253 stalls of 186 bots at
    /// the Stormwind inn), gave its goal up (which freed nothing, the bot was not stuck) and sent it home to where it stood.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABotLingeringAtItsInn_IsNotStalled(bool capital)
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, StallSeconds = 10 });
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                brain.Home = new PlayerbotHomeArea(player.MapId, new Vector3(player.X, player.Y, player.Z), 300f, "town:stormwind") { Stay = capital ? TownStay.Capital : TownStay.Secondary };
                return true;
            });
            // 35 seconds at the inn, three stall bounds.
            for (int think = 0; think < 350; think++)
            {
                await host.OnWorldAsync(() => { session.ManagedBudget = new ManagedActionBudget(4); brain.Update(100); return true; });
                await host.World.AdvanceClockAsync(100);
            }

            Assert.Null(await host.OnWorldAsync(() => brain.StallReport));
            Assert.Equal(0, await host.OnWorldAsync(() => brain.StallCount));
            Assert.Equal(0, await host.OnWorldAsync(() => brain.StallGiveUps));
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// Only a bot at its inn (or within <see cref="PlayerbotTownIdle.HoldYards"/> of the spot it cannot get closer to) is held by the idle rule.
    /// A walk to the inn given up far from it (<see cref="TownIdleAction.Linger"/> for <see cref="PlayerbotTownIdle.StallDeferMs"/>) is not:
    /// the brain explores meanwhile. Before, 724 of the Explore stalls of 2026-10-17 stood over 150 yards from the town centre, in phase
    /// explore, doing nothing for the give-up.
    /// </summary>
    [Theory]
    [InlineData(0f, true)]
    [InlineData(4f, true)]
    [InlineData(PlayerbotTownIdle.HoldYards, true)]
    [InlineData(PlayerbotTownIdle.HoldYards + 1f, false)]
    [InlineData(450f, false)]
    [InlineData(float.NaN, false)]
    public void ALingerIsHeldOnlyNearTheInn(float distance, bool held)
        => Assert.Equal(held, PlayerbotTownIdle.IsHeld(distance));

    /// <summary>
    /// The same bot far from its town, its walk to the inn given up: it is not held, so the stall watch keeps watching it (it explores) and
    /// reports it when it gets nowhere, as any exploring bot. (No terrain here: every route is refused.)
    /// </summary>
    [Fact]
    public async Task ABotFarFromItsInn_WithTheWalkGivenUp_IsStillWatched()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, StallSeconds = 60 });
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                brain.Home = new PlayerbotHomeArea(player.MapId, new Vector3(player.X + 450f, player.Y, player.Z), 2_000f, "town:stormwind")
                { Stay = TownStay.Capital };
                return true;
            });
            // 20 s of a walk that gets no closer, then the give-up (Linger for 180 s): the stall bound (60 s) falls inside the give-up.
            for (int think = 0; think < 900; think++)
            {
                await host.OnWorldAsync(() => { session.ManagedBudget = new ManagedActionBudget(4); brain.Update(100); return true; });
                await host.World.AdvanceClockAsync(100);
            }

            Assert.StartsWith("stalled ", await host.OnWorldAsync(() => brain.StallReport));
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// A Linger past <see cref="PlayerbotTownIdle.HoldYards"/> (the walk to the inn given up) falls through to the explore: with no terrain every
    /// route is refused, so the explore's own wander plan is reached. Before, the bot stopped at the Linger and the explore was never
    /// reached (no wander plan, ever).
    /// </summary>
    [Fact]
    public async Task ABotFarFromItsInn_WithTheWalkGivenUp_ExploresInsteadOfStanding()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, StallSeconds = 600 });
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                brain.Home = new PlayerbotHomeArea(player.MapId, new Vector3(player.X + 450f, player.Y, player.Z), 2_000f, "town:stormwind")
                { Stay = TownStay.Capital };
                return true;
            });
            // 20 s of a walk that gets no closer, then the give-up: Linger far from the inn for the next 180 s.
            for (int think = 0; think < 600; think++)
            {
                await host.OnWorldAsync(() => { session.ManagedBudget = new ManagedActionBudget(4); brain.Update(100); return true; });
                await host.World.AdvanceClockAsync(100);
            }

            Assert.True(await host.OnWorldAsync(() => brain.ExploreWanderPlans) > 0, "the far Linger never reached the explore wander plan");
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}
