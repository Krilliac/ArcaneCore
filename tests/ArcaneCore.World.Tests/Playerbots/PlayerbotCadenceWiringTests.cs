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
    private static async Task<(WorldTestHost Host, ManagedPlayerbotFeature Feature, Guid Id, ManagedPlayerbotLifecycleTests.CapturingLogger Log)> StartAsync(bool manualClock = true)
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var log = new ManagedPlayerbotLifecycleTests.CapturingLogger();
        WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners, logger: log, manualClock: manualClock);
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

    /// <summary>
    /// The call site of the party path: <c>UpdateAutonomous</c> snapshots the motion's renewals around the party AI's think and settles it
    /// with <see cref="PlayerbotMotion.SettleAfterDriver"/>. A gate-only think of the AI follows nothing, so the settle must renew the
    /// walk it keeps: the walk is alive past the lease measured from its start. Fails with the call site passing a zero renewals snapshot
    /// (<c>SettleAfterThink(..., renewalsBefore: 0, ...)</c> never renews, the lease runs out at 4 s).
    /// </summary>
    [Fact]
    public async Task UpdateAutonomous_APartyBotsGateOnlyThink_RenewsTheLeaseOfTheWalkItKeeps()
    {
        // The feature's stores are its own, so the real master player is made in them (WorldTestHost.EnterWorldAsync uses the host's).
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore(), manualClock: true);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await using (host)
        {
            await feature.StartupAsync(default);
            Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Wiringtwo", 1, 1)).BotId);
            Assert.True((await feature.StartScriptedAsync(id.ToString(), new Idle())).Success);
            WorldSession session = feature.FindSession(id)!;
            byte[] key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(ArcaneCore.Cryptography.WowSrp6.SessionKeyLength);
            await accounts.CreateAsync(new Account { Username = "WIREMASTER", Salt = new byte[32], Verifier = new byte[32], SessionKey = key });
            await using WorldTestClient master = await host.ConnectAsync();
            await master.AuthenticateAsync("WIREMASTER", key);
            await master.CreateCharacterAsync("Wiremaster", 1);
            Account stored = (await accounts.FindByUsernameAsync("WIREMASTER"))!;
            await master.LoginAsync((ulong)(await characters.GetByAccountAsync(stored.Id)).Single().Id);
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player bot = session.Player!;
                Player owner = host.World.FindOnlinePlayer("Wiremaster")!;
                ArcaneCore.Game.Groups.GroupManager groups = host.WorldServices.GetRequiredService<ArcaneCore.World.Social.SocialFeature>().Context.Groups;
                groups.Invite(owner, bot.Name);
                groups.Accept(bot);
                owner.Relocate(bot.X + 90f, bot.Y, bot.Z, 0f, host.World.NowMs);
                object active = ((IDictionary)typeof(ManagedPlayerbotFeature).GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(feature)!).Values.Cast<object>().Single();
                var update = typeof(ManagedPlayerbotFeature).GetMethod("UpdateAutonomous", BindingFlags.Instance | BindingFlags.NonPublic)!;

                // A decided think: the party AI follows its master.
                update.Invoke(feature, [active, bot, 5000u]);
                Assert.True(PlayerbotMotion.IsActive(bot), "the party AI did not start following");
                Assert.True(bot.Movement.HasFlag(MovementFlags.Forward));
                PlayerbotRoute route = PlayerbotMotion.RouteOf(bot)!;
                uint lease = PlayerbotMotion.LeaseMsOf(bot);

                // 3000 ms on, a think that only the AI's gate turned away: the walk is kept and the settle renews it.
                PlayerbotMotion.ElapseForTests(bot, 3000);
                uint renewals = PlayerbotMotion.RenewalsOf(bot);
                update.Invoke(feature, [active, bot, 10u]);
                Assert.Equal(renewals, PlayerbotMotion.RenewalsOf(bot));
                Assert.False(route.Released);

                PlayerbotMotion.ElapseForTests(bot, lease - 1000);
                PlayerbotMotion.Pump(session, bot, host.World.NowMs);
                Assert.True(bot.Movement.HasFlag(MovementFlags.Forward), "the lease ran out after a gate-only think of the party AI: the call site did not settle with the real renewals");
                Assert.Same(route, PlayerbotMotion.RouteOf(bot));
                return true;
            });
            await feature.ShutdownBeforeWorldStopAsync();
        }
    }

    /// <summary>
    /// The motion pump runs on game time, the clock the route's stamps (anchor, lease renewal) are taken on. A command that stands the world thread still for
    /// 1.5 s before the features of a tick must not make the walking bot cover the stall in one hop: the pump's age is one tick of game time, not the live clock's.
    /// Fails with the pump given the live clock (the bot walks speed x 1.5 s at once).
    /// </summary>
    [Fact]
    [Trait("Category", "Timing")] // a real 1.5 s stall of the world thread
    public async Task ThePump_UsesGameTime_ASlowTickDoesNotWalkTheBotTheStall()
    {
        (WorldTestHost host, ManagedPlayerbotFeature feature, Guid id, _) = await StartAsync(manualClock: false);
        await using (host)
        {
            WorldSession session = feature.FindSession(id)!;
            // The walk is anchored at this tick's start; the rest of the tick takes 1.5 s before the features (and the pump) run.
            (float x0, float y0) = await host.World.InvokeAsync(() =>
            {
                Walk(host, session);
                Thread.Sleep(1500);
                return (session.Player!.X, session.Player.Y);
            });
            // The next command runs in a following tick, after the stalled tick's pump and any tick since (50 ms of game time, 0.35 yards at run speed, each).
            (float x1, float y1) = await host.World.InvokeAsync(() => (session.Player!.X, session.Player.Y));
            double walked = Math.Sqrt(Math.Pow(x1 - x0, 2) + Math.Pow(y1 - y0, 2));
            Assert.True(walked < 2.0, $"the bot walked {walked:F1} yards in the tick that stalled 1.5 s before the pump");
            await feature.ShutdownBeforeWorldStopAsync();
        }
    }

    private sealed class Idle : IPlayerbotController
    {
        public void Tick(PlayerbotControllerContext context, uint elapsedMs) { }

        public void Detached(Guid botId) { }
    }
}
