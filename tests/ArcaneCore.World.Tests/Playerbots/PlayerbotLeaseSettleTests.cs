using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Protocol;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Social;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The motion lease of a bot walked by a driver that does not call <see cref="PlayerbotMotion.Follow"/> every think (the bot-led group AI,
/// the party AI and its follower, the battleground AI): <see cref="PlayerbotMotion.SettleAfterDriver"/> renews the lease of a route its owner
/// still holds and stops a route its owner let go of (<see cref="PlayerbotRoute.Released"/>). These tests run the real drivers by hand, one
/// think at a time, on a flat world with an open pathfinder. Time inside a world-thread call moves through
/// <see cref="PlayerbotMotion.ElapseForTests"/>: the renewal proof is that the walk survives past the lease measured from its start but not
/// past it measured from the settle.
/// </summary>
public sealed class PlayerbotLeaseSettleTests
{
    // --- shared helpers ------------------------------------------------------------------------------------------------------

    private static void OpenWorld(WorldTestHost host)
        => WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());

    /// <summary>A think length under the driver's think interval: the driver's gate turns it away without deciding anything.</summary>
    private static uint Gate(PlayerbotOptions options) => Math.Max(1u, (uint)options.ThinkIntervalMs / 12u);

    private static uint DecidedMs(PlayerbotOptions options) => (uint)options.ThinkIntervalMs + 400u;

    private static bool Forward(Player player) => player.Movement.HasFlag(MovementFlags.Forward);

    /// <summary>
    /// Lets <paramref name="elapseMs"/> pass and pumps the motion: whether the bot still walks. The lease is the lease of the route the bot
    /// was walking, measured from the last renewal (<see cref="PlayerbotMotion.ElapseForTests"/> moves the renewal time back with it).
    /// </summary>
    private static bool WalksAfter(WorldSession session, uint elapseMs)
    {
        Player player = session.Player!;
        PlayerbotMotion.ElapseForTests(player, elapseMs);
        PlayerbotMotion.Pump(session, player, session.World.NowMs);
        return Forward(player);
    }

    // --- the bot-led group AI ------------------------------------------------------------------------------------------------

    private sealed record Rig(WorldTestHost Host, WorldTestClient Leader, WorldSession Bot, PlayerbotGroupAI Ai,
        PlayerbotGroupCoordinator.BotGroup Group, PlayerbotGroupCoordinator.Member Me, PlayerbotOptions Options)
    {
        public Player LeaderPlayer => Host.World.FindOnlinePlayer("Leaseleader")!;

        public Player BotPlayer => Bot.Player!;

        /// <summary>One think of the group AI of <paramref name="ms"/> ms (below the think interval: the gate only).</summary>
        public void Think(uint ms)
        {
            Bot.ManagedBudget = new ManagedActionBudget(8);
            Ai.Update(BotPlayer, Group, Me, ms);
        }

        /// <summary>A decided think (one that reaches the AI's decisions).</summary>
        public void Decide() => Think(DecidedMs(Options));

        /// <summary>The leader (a real player in the group) stands <paramref name="yards"/> east of the bot.</summary>
        public void LeaderAway(float yards)
            => LeaderPlayer.Relocate(BotPlayer.X + yards, BotPlayer.Y, BotPlayer.Z, 0f, Host.World.NowMs);
    }

    private static async Task RunGroup(Func<Rig, Task> body)
    {
        await using WorldTestHost host = PartyTestHost.Start(options => options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);
        WorldSession bot = await PartyTestHost.EnterManagedAsync(host, "LEASEBOT", "Leasebot");
        await using WorldTestClient leader = await host.EnterWorldAsync("LEASELEADER", "Leaseleader");
        try
        {
            PlayerbotOptions options = PartyTestHost.PlayerbotOptionsOf(host);
            var ai = new PlayerbotGroupAI(bot, options);
            PlayerbotGroupCoordinator.BotGroup? group = null;
            PlayerbotGroupCoordinator.Member? me = null;
            await host.OnWorldAsync(() =>
            {
                OpenWorld(host);
                Player b = bot.Player!, l = host.World.FindOnlinePlayer("Leaseleader")!;
                GroupManager groups = host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;
                groups.Invite(l, b.Name);
                groups.Accept(b);
                bot.Services.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System.Store = new SpellStore([], [], []);
                var goal = new PlayerbotGroupGoal(PlayerbotGroupGoalKind.Quest, 0, 0, b.MapId, b.MapId, Vector3.Zero, 0, Vector3.Zero, 2, 1, "test");
                group = new PlayerbotGroupCoordinator.BotGroup(1, goal, Guid.Empty, l.Guid, host.World.NowMs) { State = PlayerbotGroupState.Engaging };
                me = new PlayerbotGroupCoordinator.Member(Guid.NewGuid(), b.Guid, b.Name, PlayerbotGroupRole.Damage, PlayerbotRole.MeleeDps, 0, real: false)
                    { Joined = true, AI = ai };
                group.Members.Add(new PlayerbotGroupCoordinator.Member(Guid.Empty, l.Guid, l.Name, PlayerbotGroupRole.Tank, PlayerbotRole.Tank, 0, real: true) { Joined = true });
                group.Members.Add(me);
                return true;
            });
            await body(new Rig(host, leader, bot, ai, group!, me!, options));
        }
        finally
        {
            bot.Kick();
            await bot.ManagedClosed;
        }
    }

    /// <summary>Starts the group AI walking to a leader <paramref name="yards"/> away (a decided think) and returns the route it walks.</summary>
    private static PlayerbotRoute StartFollowWalk(Rig rig, float yards)
    {
        rig.LeaderAway(yards);
        rig.Decide();
        Player bot = rig.BotPlayer;
        Assert.True(PlayerbotMotion.IsActive(bot), $"the group AI did not start walking (step {rig.Ai.Step})");
        Assert.True(Forward(bot));
        return PlayerbotMotion.RouteOf(bot)!;
    }

    private static Creature SpawnAttacker(Player near)
    {
        var template = new CreatureTemplate { Entry = 990_801, Name = "leaseattacker", CreatureType = 1, MinLevel = 1, MaxLevel = 1, MinLevelHealth = 500, MaxLevelHealth = 500 };
        var system = new CreatureMapSystem(near.Map!, new CreatureContent([template], [], [], [], []), random: new Random(7));
        return system.SpawnTemporary(template, near.X + 4, near.Y, near.Z, 0f);
    }

    /// <summary>
    /// Test 1. The group AI's retreat route is owned by <see cref="PlayerbotRetreat"/>, a helper the AI itself never holds. A think that only
    /// ran the AI's gate (below the think interval) did not follow it again, yet the owner still holds it: the settle must renew the lease.
    /// Fails if the settle asks the AI to vouch for the route (a holder test that cannot see a helper's route stops or never renews it) or if it
    /// never renews: the walk then ends at its lease measured from the last Follow.
    /// </summary>
    [Fact]
    public Task GroupAiRetreatRoute_AGateOnlyThinkKeepsIt_AndTheSettleRenewsItsLease() => RunGroup(rig => rig.Host.OnWorldAsync(() =>
    {
        Player bot = rig.BotPlayer;
        Creature attacker = SpawnAttacker(bot);
        Assert.True(bot.Map!.Combat.Attack(attacker, bot), "the attacker could not engage the bot");
        rig.Ai.Risk.StartRetreat(bot, [attacker], "lease-test");
        rig.Decide();
        Assert.True(rig.Ai.Risk.Retreat.Active, "the retreat did not run: " + rig.Ai.Risk.Retreat.Trace);
        Assert.True(PlayerbotMotion.IsActive(bot), "the retreat did not walk: " + rig.Ai.Risk.Retreat.Trace);
        PlayerbotRoute route = PlayerbotMotion.RouteOf(bot)!;
        uint lease = PlayerbotMotion.LeaseMsOf(bot);
        Assert.InRange(lease, 4000u, 15_000u);

        // 3000 ms on, a think that only the gate turned away: nothing followed the route, nothing let go of it.
        PlayerbotMotion.ElapseForTests(bot, 3000);
        uint renewals = PlayerbotMotion.RenewalsOf(bot);
        uint gate = Gate(rig.Options);
        rig.Think(gate);
        Assert.Equal(renewals, PlayerbotMotion.RenewalsOf(bot));
        Assert.False(route.Released);
        Assert.False(PlayerbotMotion.SettleAfterDriver(rig.Bot, bot, renewals, gate));

        // Past the lease measured from the start (3000 + lease - 1000), inside it measured from the settle.
        Assert.True(WalksAfter(rig.Bot, lease - 1000), "the lease of a route its owner holds ran out after a gate-only think");
        Assert.Same(route, PlayerbotMotion.RouteOf(bot));
        Assert.True(Forward(bot));
        return true;
    }));

    /// <summary>
    /// Test 1b. The ordinary follow walk of the group AI, the same gate-only think: kept and renewed. Together with test 1 this pins that the
    /// renewal does not depend on which helper owns the route.
    /// </summary>
    [Fact]
    public Task GroupAiFollowRoute_AGateOnlyThinkKeepsIt_AndTheSettleRenewsItsLease() => RunGroup(rig => rig.Host.OnWorldAsync(() =>
    {
        Player bot = rig.BotPlayer;
        PlayerbotRoute route = StartFollowWalk(rig, 90f);
        uint lease = PlayerbotMotion.LeaseMsOf(bot);
        PlayerbotMotion.ElapseForTests(bot, 3000);
        uint renewals = PlayerbotMotion.RenewalsOf(bot);
        uint gate = Gate(rig.Options);
        rig.Think(gate);
        Assert.Equal(renewals, PlayerbotMotion.RenewalsOf(bot));
        Assert.False(PlayerbotMotion.SettleAfterDriver(rig.Bot, bot, renewals, gate));
        Assert.True(WalksAfter(rig.Bot, lease - 1000), "the lease of the follow walk ran out after a gate-only think");
        Assert.Same(route, PlayerbotMotion.RouteOf(bot));
        return true;
    }));

    /// <summary>
    /// Test 2. The group AI lets go of its route without stopping the bot (the map policy refuses the next step, so
    /// <see cref="PlayerbotNavigation.TryAdvance"/> says no and the AI drops the route and plans nothing else this think). The bot still
    /// walks the stale route; the settle must stop it now rather than let the lease run. Fails if a released route is renewed (the bot keeps
    /// walking), or if the owner's slot does not mark the route it drops.
    /// AllowedMaps is a plain settable array on the host's live options, so it is changed in place after the walk began.
    /// </summary>
    [Fact]
    public Task GroupAiDropWithoutStop_TheSettleStopsTheStaleRouteNow() => RunGroup(rig => rig.Host.OnWorldAsync(() =>
    {
        Player bot = rig.BotPlayer;
        PlayerbotRoute route = StartFollowWalk(rig, 50f);
        Assert.False(route.Released);

        rig.Options.AllowedMaps = [uint.MaxValue - 1]; // no map the bot is on: MayMoveOn is false from here
        uint renewals = PlayerbotMotion.RenewalsOf(bot);
        rig.Decide();
        Assert.Equal(renewals, PlayerbotMotion.RenewalsOf(bot));
        Assert.Same(route, PlayerbotMotion.RouteOf(bot)); // the AI let go of it, the bot still walks it
        Assert.True(Forward(bot), "the AI stopped the bot itself; this test needs the drop without a stop");
        Assert.True(route.Released, "the AI's route slot did not mark the route it let go of");

        Assert.True(PlayerbotMotion.SettleAfterDriver(rig.Bot, bot, renewals, DecidedMs(rig.Options)), "the bot kept walking a route its driver dropped");
        Assert.False(PlayerbotMotion.IsActive(bot));
        Assert.False(Forward(bot));
        return true;
    }));

    /// <summary>
    /// Test 2b. A think that drops a route but follows another leaves the bot on the new route: a settle after it neither stops nor
    /// renews anything (the renewal count moved on). The group AI's loop check (a pending loop) drops its route and the same think plans
    /// the next one.
    /// </summary>
    [Fact]
    public Task GroupAiDropThatFollowsAnotherRoute_TheSettleLeavesTheNewWalkAlone() => RunGroup(rig => rig.Host.OnWorldAsync(() =>
    {
        Player bot = rig.BotPlayer;
        PlayerbotRoute old = StartFollowWalk(rig, 50f);
        // A leader that moved 20 yards: the AI plans the new way and the motion switches to it.
        rig.LeaderAway(70f);
        uint renewals = PlayerbotMotion.RenewalsOf(bot);
        rig.Decide();
        PlayerbotRoute? now = PlayerbotMotion.RouteOf(bot);
        Assert.NotNull(now);
        Assert.NotSame(old, now);
        Assert.NotEqual(renewals, PlayerbotMotion.RenewalsOf(bot));
        Assert.True(old.Released);
        Assert.False(now!.Released);
        Assert.False(PlayerbotMotion.SettleAfterDriver(rig.Bot, bot, renewals, DecidedMs(rig.Options)));
        Assert.True(PlayerbotMotion.IsActive(bot));
        Assert.Same(now, PlayerbotMotion.RouteOf(bot));
        return true;
    }));

    // --- the party AI's follower ---------------------------------------------------------------------------------------------

    /// <summary>A party bot (hand-driven) grouped with a socket master, with a hand-made 12-yard drop 80 yards east of it, the master at its foot.</summary>
    private sealed class Drop : IAsyncDisposable
    {
        public required WorldTestHost Host { get; init; }

        public required WorldTestClient Master { get; init; }

        public required WorldSession Session { get; init; }

        public required PlayerbotPartyAI AI { get; init; }

        public required PlayerbotOptions Options { get; init; }

        public required string MasterName { get; init; }

        public PlayerbotDropPlan Plan { get; set; } = null!;

        public Player Bot => Session.Player!;

        public Player MasterPlayer => Host.World.FindOnlinePlayer(MasterName)!;

        public static async Task<Drop> StartAsync(string account, string name)
        {
            WorldTestHost host = PartyTestHost.Start(o => o.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);
            WorldTestClient master = await host.EnterWorldAsync(account + "M", name + "m");
            WorldSession session = await PartyTestHost.EnterManagedAsync(host, account + "B", name);
            PlayerbotOptions options = PartyTestHost.PlayerbotOptionsOf(host);
            var ai = new PlayerbotPartyAI(session, options);
            var drop = new Drop { Host = host, Master = master, Session = session, AI = ai, Options = options, MasterName = name + "m" };
            await host.OnWorldAsync(() =>
            {
                OpenWorld(host);
                Player bot = session.Player!;
                Player owner = drop.MasterPlayer;
                GroupManager groups = host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;
                groups.Invite(owner, bot.Name);
                groups.Accept(bot);
                Assert.True(ai.Drives(bot));
                var edge = new Vector3(bot.X + 80f, bot.Y, bot.Z);
                Assert.True(PlayerbotNavigation.TryPlan(bot, edge, options, out PlayerbotRoute? toEdge), "no walk to the edge");
                Vector3 stepOff = edge with { X = edge.X + 0.5f };
                drop.Plan = new PlayerbotDropPlan(toEdge, edge, stepOff, stepOff with { Z = edge.Z - 12f }, 12f, 0);
                // The master stands at the foot of the drop, 80 yards from the bot: the follower's decision is to move.
                owner.Relocate(drop.Plan.Landing.X, drop.Plan.Landing.Y, drop.Plan.Landing.Z, 0f, host.World.NowMs);
                session.ManagedBudget = new ManagedActionBudget(8);
                Assert.False(ai.Follower.StartDropForTests(bot, drop.Plan, drop.Plan.Landing), "the fall began before the walk to the edge");
                Assert.True(PlayerbotMotion.IsActive(bot), "the walk to the drop's edge did not start");
                Assert.Same(toEdge, PlayerbotMotion.RouteOf(bot));
                return true;
            });
            return drop;
        }

        public async ValueTask DisposeAsync()
        {
            Session.Kick();
            await Session.ManagedClosed;
            await Master.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    /// <summary>
    /// Test 3a. The follower walks to the edge of a drop on a route that only the drop plan holds (the follower's own route slot is empty).
    /// A gate-only think of the party AI keeps it: the settle must renew it. Fails if the settle only trusts the follower's route slot, or
    /// never renews.
    /// </summary>
    [Fact]
    public async Task PartyFollowerWalkToDropEdge_AGateOnlyThinkKeepsIt_AndTheSettleRenewsItsLease()
    {
        await using Drop drop = await Drop.StartAsync("LEASEDRA", "Leasedra");
        await drop.Host.OnWorldAsync(() =>
        {
            Player bot = drop.Bot;
            PlayerbotRoute toEdge = drop.Plan.ToEdge!;
            uint lease = PlayerbotMotion.LeaseMsOf(bot);
            PlayerbotMotion.ElapseForTests(bot, 3000);
            uint renewals = PlayerbotMotion.RenewalsOf(bot);
            uint gate = Gate(drop.Options);
            drop.Session.ManagedBudget = new ManagedActionBudget(8);
            drop.AI.Update(bot, gate);
            Assert.Equal(renewals, PlayerbotMotion.RenewalsOf(bot));
            Assert.False(toEdge.Released);
            Assert.False(PlayerbotMotion.SettleAfterDriver(drop.Session, bot, renewals, gate));
            Assert.True(WalksAfter(drop.Session, lease - 1000), "the lease of the walk to the drop's edge ran out after a gate-only think");
            Assert.Same(toEdge, PlayerbotMotion.RouteOf(bot));
        });
    }

    /// <summary>
    /// Test 3b. The party AI's fight suspends the follower (<see cref="PlayerbotFollower.Suspend"/>): the walk to the drop's edge is given up
    /// without stopping the bot. The plan that held the walk is dropped, so the settle stops the bot now. Fails if the drop's slot does not
    /// release the walk to the edge, or if the settle renews what nobody holds.
    /// </summary>
    [Fact]
    public async Task PartyFollowerDropWalkGivenUp_TheSettleStopsTheBotNow()
    {
        await using Drop drop = await Drop.StartAsync("LEASEDRB", "Leasedrb");
        await drop.Host.OnWorldAsync(() =>
        {
            Player bot = drop.Bot;
            PlayerbotRoute toEdge = drop.Plan.ToEdge!;
            uint renewals = PlayerbotMotion.RenewalsOf(bot);
            drop.AI.Follower.Suspend(bot);
            Assert.True(Forward(bot), "suspending the follower stopped the bot itself");
            Assert.True(toEdge.Released, "the follower's drop slot did not mark the walk to the edge it let go of");

            Assert.True(PlayerbotMotion.SettleAfterDriver(drop.Session, bot, renewals, Gate(drop.Options)), "the bot kept walking to the edge of a drop that was given up");
            Assert.False(PlayerbotMotion.IsActive(bot));
            Assert.False(Forward(bot));
        });
    }

    /// <summary>
    /// Test 3c. The master moved on from the foot of the drop: the follower gives the drop up (its plan is dropped, the walk to the edge is
    /// released) and, in the same think, follows a fresh route. The settle then sees that the think followed a route and leaves the new
    /// walk alone: it is neither stopped nor treated as released.
    /// </summary>
    [Fact]
    public async Task PartyFollowerDropGivenUpForAFreshRoute_TheSettleLeavesTheNewWalkAlone()
    {
        await using Drop drop = await Drop.StartAsync("LEASEDRC", "Leasedrc");
        await drop.Host.OnWorldAsync(() =>
        {
            Player bot = drop.Bot;
            PlayerbotRoute toEdge = drop.Plan.ToEdge!;
            // The master leaves the foot of the drop by more than the follower's tolerance, still 60 yards from the bot.
            Player master = drop.MasterPlayer;
            master.Relocate(master.X, master.Y + 30f, master.Z, 0f, drop.Host.World.NowMs);
            uint renewals = PlayerbotMotion.RenewalsOf(bot);
            drop.Session.ManagedBudget = new ManagedActionBudget(8);
            drop.AI.Update(bot, DecidedMs(drop.Options));
            Assert.True(toEdge.Released, "the drop was not given up");
            PlayerbotRoute? now = PlayerbotMotion.RouteOf(bot);
            Assert.NotNull(now);
            Assert.NotSame(toEdge, now);
            Assert.NotEqual(renewals, PlayerbotMotion.RenewalsOf(bot));
            Assert.False(PlayerbotMotion.SettleAfterDriver(drop.Session, bot, renewals, DecidedMs(drop.Options)));
            Assert.True(PlayerbotMotion.IsActive(bot));
            Assert.Same(now, PlayerbotMotion.RouteOf(bot));
        });
    }

    // --- the battleground AI -------------------------------------------------------------------------------------------------

    /// <summary>A managed bot in a plain world, with a battleground AI to walk it to a battlemaster 90 yards away (no match needed).</summary>
    private static async Task RunBattleground(Func<WorldTestHost, WorldSession, PlayerbotBattlegroundAI, PlayerbotOptions, Vector3, Task> body)
    {
        await using WorldTestHost host = PartyTestHost.Start();
        WorldSession bot = await PartyTestHost.EnterManagedAsync(host, "LEASEBG", "Leasebg");
        try
        {
            PlayerbotOptions options = PartyTestHost.PlayerbotOptionsOf(host);
            var ai = new PlayerbotBattlegroundAI(bot, options);
            Vector3 master = await host.OnWorldAsync(() =>
            {
                OpenWorld(host);
                Player p = bot.Player!;
                return new Vector3(p.X + 90f, p.Y, p.Z);
            });
            await body(host, bot, ai, options, master);
        }
        finally
        {
            bot.Kick();
            await bot.ManagedClosed;
        }
    }

    /// <summary>
    /// Test 4a. The battleground AI walking to a battlemaster, a gate-only think: its route is kept and renewed. The out-of-combat buff cast
    /// the plan names (an instant spell that ends a decided think without following) needs a spell the test host does not grant, so the
    /// gate-only think stands in: the same path, a think that follows nothing and releases nothing.
    /// </summary>
    [Fact]
    public Task BattlegroundAiWalk_AGateOnlyThinkKeepsIt_AndTheSettleRenewsItsLease() => RunBattleground((host, session, ai, options, master) => host.OnWorldAsync(() =>
    {
        Player bot = session.Player!;
        session.ManagedBudget = new ManagedActionBudget(8);
        Assert.True(ai.Travel(bot, master, DecidedMs(options)), "the battleground AI did not walk");
        Assert.True(PlayerbotMotion.IsActive(bot));
        PlayerbotRoute route = PlayerbotMotion.RouteOf(bot)!;
        uint lease = PlayerbotMotion.LeaseMsOf(bot);

        PlayerbotMotion.ElapseForTests(bot, 3000);
        uint renewals = PlayerbotMotion.RenewalsOf(bot);
        uint gate = Gate(options);
        Assert.True(ai.Travel(bot, master, gate)); // the gate turns it away
        Assert.Equal(renewals, PlayerbotMotion.RenewalsOf(bot));
        Assert.False(PlayerbotMotion.SettleAfterDriver(session, bot, renewals, gate));
        Assert.True(WalksAfter(session, lease - 1000), "the lease of the battleground AI's walk ran out after a gate-only think");
        Assert.Same(route, PlayerbotMotion.RouteOf(bot));
        return true;
    }));

    /// <summary>
    /// Test 4b. The battleground AI drops its route without stopping the bot (the map policy refuses the step and the replan): the settle
    /// stops the bot now.
    /// </summary>
    [Fact]
    public Task BattlegroundAiDropWithoutStop_TheSettleStopsTheStaleRouteNow() => RunBattleground((host, session, ai, options, master) => host.OnWorldAsync(() =>
    {
        Player bot = session.Player!;
        session.ManagedBudget = new ManagedActionBudget(8);
        Assert.True(ai.Travel(bot, master, DecidedMs(options)));
        PlayerbotRoute route = PlayerbotMotion.RouteOf(bot)!;

        options.AllowedMaps = [uint.MaxValue - 1];
        uint renewals = PlayerbotMotion.RenewalsOf(bot);
        Assert.False(ai.Travel(bot, master, DecidedMs(options)));
        Assert.Same(route, PlayerbotMotion.RouteOf(bot));
        Assert.True(Forward(bot), "the AI stopped the bot itself; this test needs the drop without a stop");
        Assert.True(route.Released, "the battleground AI's route slot did not mark the route it let go of");
        Assert.True(PlayerbotMotion.SettleAfterDriver(session, bot, renewals, DecidedMs(options)));
        Assert.False(PlayerbotMotion.IsActive(bot));
        Assert.False(Forward(bot));
        return true;
    }));

    // --- the cap -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Test 5. A route that nobody follows and nobody releases (an owner whose slot never lets go) is renewed by the settle at most
    /// <see cref="PlayerbotMotion.MaxUnfollowedRenewals"/> times, then left to die at its lease: the cap is the safety net for owners
    /// without a releasing slot. Nine gate-only thinks, each settled: the ninth is not renewed and the walk ends at its lease. A Follow
    /// (a decided think that re-asserts the route) clears the count: eight more settles renew again. Fails with the cap removed (the
    /// ninth settle renews and the bot walks on) and with a cap that Follow does not reset (the second run of settles is not renewed).
    /// </summary>
    [Fact]
    public Task TheSettleRenewsAnUnfollowedRouteAtMostMaxUnfollowedRenewalsTimes_AndAFollowResetsTheCount() => RunGroup(rig => rig.Host.OnWorldAsync(() =>
    {
        Player bot = rig.BotPlayer;
        PlayerbotRoute route = StartFollowWalk(rig, 90f);
        uint lease = PlayerbotMotion.LeaseMsOf(bot);
        uint max = PlayerbotMotion.MaxUnfollowedRenewals;
        uint gate = Math.Max(1u, (uint)rig.Options.ThinkIntervalMs / (max + 4u)); // max + 1 gate-only thinks stay under the think interval
        Assert.True(gate * (max + 1u) < rig.Options.ThinkIntervalMs);

        // Each settle is a gate-only think 1 ms after the last, except the ninth: it comes lease - 500 ms after the eighth renewal, still
        // inside that lease. Renewed, the walk lives 1000 ms past it; not renewed, it is past the eighth renewal's lease by then.
        for (uint i = 0; i < max + 1u; i++)
        {
            PlayerbotMotion.ElapseForTests(bot, i == max ? lease - 500 : 1);
            uint renewals = PlayerbotMotion.RenewalsOf(bot);
            rig.Think(gate);
            Assert.Equal(renewals, PlayerbotMotion.RenewalsOf(bot));
            Assert.False(PlayerbotMotion.SettleAfterDriver(rig.Bot, bot, renewals, gate)); // nothing is released: never stops the bot itself
            if (i < max) Assert.True(PlayerbotMotion.IsActive(bot));
        }

        Assert.False(route.Released);
        Assert.False(WalksAfter(rig.Bot, 1000), $"the ninth unfollowed settle renewed the route (cap {max}); it kept walking past the lease of the eighth renewal");
        Assert.False(PlayerbotMotion.IsActive(bot));
        return true;
    }));

    [Fact]
    public Task AFollowClearsTheCountOfUnfollowedRenewals_SoEightMoreSettlesRenew() => RunGroup(rig => rig.Host.OnWorldAsync(() =>
    {
        Player bot = rig.BotPlayer;
        PlayerbotRoute route = StartFollowWalk(rig, 90f);
        uint lease = PlayerbotMotion.LeaseMsOf(bot);
        uint max = PlayerbotMotion.MaxUnfollowedRenewals;
        uint gate = Math.Max(1u, (uint)rig.Options.ThinkIntervalMs / (max + 4u));

        // Use the whole allowance with gate-only thinks.
        for (uint i = 0; i < max; i++)
        {
            PlayerbotMotion.ElapseForTests(bot, 1);
            uint before = PlayerbotMotion.RenewalsOf(bot);
            rig.Think(gate);
            Assert.False(PlayerbotMotion.SettleAfterDriver(rig.Bot, bot, before, gate));
        }

        // One decided think re-asserts the route through Follow: it renews it and clears the count.
        uint followed = PlayerbotMotion.RenewalsOf(bot);
        rig.Decide();
        Assert.NotEqual(followed, PlayerbotMotion.RenewalsOf(bot));
        Assert.Same(route, PlayerbotMotion.RouteOf(bot));

        // Eight more settles each renew (500 ms apart: 4000 ms in all, the whole lease), so the walk is alive a lease-minus-1000 later.
        // With the count not cleared none of them would renew and the walk would have run out of lease by then.
        for (uint i = 0; i < max; i++)
        {
            PlayerbotMotion.ElapseForTests(bot, 500);
            uint before = PlayerbotMotion.RenewalsOf(bot);
            rig.Think(gate);
            Assert.False(PlayerbotMotion.SettleAfterDriver(rig.Bot, bot, before, gate));
        }

        Assert.True(WalksAfter(rig.Bot, lease - 1000), "the count of unfollowed renewals was not cleared by a Follow");
        Assert.Same(route, PlayerbotMotion.RouteOf(bot));
        return true;
    }));
}
