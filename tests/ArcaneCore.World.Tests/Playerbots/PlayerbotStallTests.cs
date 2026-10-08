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
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The watch that reports a bot making no progress (<see cref="PlayerbotStallWatch"/>) instead of letting it idle silently, as
/// Ironwander, Graveweaver and Dawnrover did on the live server for hours (2026-10-08). Time moves only on the manual world clock.
/// </summary>
public sealed class PlayerbotStallTests
{
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

    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}
