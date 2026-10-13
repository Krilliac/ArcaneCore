using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>The cadence work as the feature wires it: what a think gap is, and who may leave a walking bot behind.</summary>
public sealed class PlayerbotCadenceWiringTests
{
    private static async Task<(WorldTestHost Host, ManagedPlayerbotFeature Feature, Guid Id, ManagedPlayerbotLifecycleTests.CapturingLogger Log)> StartAsync()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var log = new ManagedPlayerbotLifecycleTests.CapturingLogger();
        WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners, logger: log, manualClock: true);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Wiringone", 1, 1)).BotId);
        Assert.True((await feature.StartScriptedAsync(id.ToString(), new Idle())).Success);
        return (host, feature, id, log);
    }

    private static void Walk(WorldTestHost host, WorldSession session)
    {
        WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
        Player player = session.Player!;
        var options = new PlayerbotOptions { MaxPathPoints = 8, MaxRouteYards = 400 };
        Assert.True(PlayerbotNavigation.TryPlan(player, new(player.X + 200, player.Y, player.Z), options, out PlayerbotRoute? route));
        session.ManagedBudget = new ManagedActionBudget(4);
        Assert.True(PlayerbotNavigation.TryAdvance(session, route!, options, 500, host.World.NowMs));
        Assert.True(player.Movement.HasFlag(MovementFlags.Forward));
    }

    /// <summary>
    /// A bot's first think after login measures the time since its record was made, not a gap between thinks: it must not feed the
    /// smoothed gap (the lease would jump to its cap) nor the report's p95 and max.
    /// </summary>
    [Fact]
    public async Task TheFirstThinkAfterLogin_IsNotAThinkGap()
    {
        (WorldTestHost host, ManagedPlayerbotFeature feature, Guid id, var log) = await StartAsync();
        await using (host)
        {
            WorldSession session = feature.FindSession(id)!;
            await host.World.InvokeAsync(() =>
            {
                // Forty seconds between the record and its first think, as a login that took that long.
                var active = ((IDictionary)typeof(ManagedPlayerbotFeature).GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(feature)!).Values.Cast<object>().Single();
                Type type = active.GetType();
                var last = type.GetField("LastUpdateMs")!;
                last.SetValue(active, unchecked((uint)last.GetValue(active)! - 40_000u));
                type.GetField("GapPrimed")!.SetValue(active, false);
                return true;
            });

            await host.World.AdvanceClockAsync(61_000);
            string line = Assert.Single(log.Entries, e => e.Message.Contains("Playerbot cadence over the last minute")).Message;
            Assert.InRange(int.Parse(Regex.Match(line, @"max (\d+) ms").Groups[1].Value), 0, 100);
            Assert.InRange(await host.World.InvokeAsync(() => PlayerbotMotion.ThinkGapMs(session.Player!)), 1u, 100u);
            await feature.ShutdownBeforeWorldStopAsync();
        }
    }

    /// <summary>A controller that attaches to or detaches from a walking bot stops it: its route is not the next driver's.</summary>
    [Fact]
    public async Task AControllerAttachingOrDetaching_StopsAWalkingBot()
    {
        (WorldTestHost host, ManagedPlayerbotFeature feature, Guid id, _) = await StartAsync();
        await using (host)
        {
            WorldSession session = feature.FindSession(id)!;
            await host.World.InvokeAsync(() => { Walk(host, session); return true; });
            Assert.True(await feature.SetControllerAsync(id, new Idle()));
            Assert.False(await host.World.InvokeAsync(() => session.Player!.Movement.HasFlag(MovementFlags.Forward)), "walking on after a new controller");

            await host.World.InvokeAsync(() => { Walk(host, session); return true; });
            Assert.True(await feature.SetControllerAsync(id, null));
            Assert.False(await host.World.InvokeAsync(() => session.Player!.Movement.HasFlag(MovementFlags.Forward)), "walking on after the controller detached");
            await feature.ShutdownBeforeWorldStopAsync();
        }
    }

    /// <summary>
    /// The call site: <c>UpdateAutonomous</c> reads what the brain's think gave up and stops the walking bot. The brain is stopped so its
    /// think does nothing of its own; the route it "abandoned" is the one the bot walks.
    /// </summary>
    [Fact]
    public async Task UpdateAutonomous_StopsABotWhoseBrainAbandonedItsRoute_AndOnlyThen()
    {
        (WorldTestHost host, ManagedPlayerbotFeature feature, Guid id, _) = await StartAsync();
        await using (host)
        {
            WorldSession session = feature.FindSession(id)!;
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                object active = ((IDictionary)typeof(ManagedPlayerbotFeature).GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(feature)!).Values.Cast<object>().Single();
                var brain = (PlayerbotBrain)active.GetType().GetField("Brain")!.GetValue(active)!;
                brain.Stop();
                var abandoned = typeof(PlayerbotBrain).GetField("_routeAbandoned", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var update = typeof(ManagedPlayerbotFeature).GetMethod("UpdateAutonomous", BindingFlags.Instance | BindingFlags.NonPublic)!;

                Walk(host, session);
                update.Invoke(feature, [active, player, 5000u]);
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward), "stopped though the brain gave nothing up");

                abandoned.SetValue(brain, true);
                update.Invoke(feature, [active, player, 5000u]);
                Assert.False(player.Movement.HasFlag(MovementFlags.Forward), "walking on after the brain abandoned its route");
                return true;
            });
            await feature.ShutdownBeforeWorldStopAsync();
        }
    }

    private sealed class Idle : IPlayerbotController
    {
        public void Tick(PlayerbotControllerContext context, uint elapsedMs) { }

        public void Detached(Guid botId) { }
    }
}
