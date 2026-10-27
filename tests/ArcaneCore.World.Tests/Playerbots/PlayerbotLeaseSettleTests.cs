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
using ArcaneCore.World.Tests.Playerbots.Dungeon;
using ArcaneCore.World.Tests.Playerbots.Elevator;
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
    /// A bot at its battlemaster's spot with nobody there refuses the walk after 5 s; for a master that walks waypoints (it may be out of
    /// the map's reach for a moment) the empty spot is no refusal, only the progress watch and the timeout end the wait.
    /// </summary>
    [Fact]
    public Task BattlegroundAiAtAnEmptySpot_RefusesAfterFiveSeconds_ButNotForAMasterThatWalksWaypoints() => RunBattleground((host, session, ai, options, master) => host.OnWorldAsync(() =>
    {
        Player bot = session.Player!;
        session.ManagedBudget = new ManagedActionBudget(8);
        var here = new Vector3(bot.X, bot.Y, bot.Z);
        for (int i = 0; i < 60 && !ai.TravelRefused; i++) ai.Travel(bot, here, DecidedMs(options), refuseEmptySpot: false);
        Assert.False(ai.TravelRefused, "a master that walks waypoints was refused at an empty spot");
        for (int i = 0; i < 60 && !ai.TravelRefused; i++) ai.Travel(bot, here, DecidedMs(options));
        Assert.True(ai.TravelRefused, "control: a master that stands still is refused at an empty spot");
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

    // --- the time bound ------------------------------------------------------------------------------------------------------

    private static uint BoundOf(Player bot)
    {
        Assert.True(PlayerbotMotion.TryGetState(bot, out PlayerbotMotion.MotionState? state));
        return (uint)PlayerbotMotion.SettleBoundMs(state!);
    }

    /// <summary>
    /// <see cref="PlayerbotMotion.SettleAfterDriver"/> after a gate-only think, and whether that settle renewed the lease: whether it moved
    /// RenewedMs to a world-clock reading taken during the call. The world clock is the real one here, so a check against "now" read after the
    /// settle (a tolerance of some ms) fails whenever the test thread is descheduled past the tolerance between the two readings, as a loaded
    /// runner does. A renewal the settle did not make is at least one elapsed step (seconds) older than the reading before it.
    /// </summary>
    private static bool Settle(WorldSession session, Player bot, uint renewalsBefore, uint sinceLast, out bool renewed)
    {
        uint from = session.World.NowMs;
        bool stopped = PlayerbotMotion.SettleAfterDriver(session, bot, renewalsBefore, sinceLast);
        uint to = session.World.NowMs;
        Assert.True(PlayerbotMotion.TryGetState(bot, out PlayerbotMotion.MotionState? state));
        renewed = unchecked(state!.RenewedMs - from) <= unchecked(to - from);
        return stopped;
    }

    /// <summary>
    /// Test 5. A route that nobody follows and nobody releases (an owner whose slot never lets go) is renewed by the settle only while less
    /// than <see cref="PlayerbotMotion.SettleBoundMs"/> (the larger of its lease and three think gaps) has passed since its last Follow, then left to die. A
    /// gate-only settle half the bound after the Follow is renewed; one more than half the bound later is not, and the walk has run out of
    /// lease by the time it is checked. Fails with the bound removed (the second settle renews and the bot walks on).
    /// </summary>
    [Fact]
    public Task TheSettleRenewsAnUnfollowedRouteOnlyWhileLessThanItsBoundHasPassedSinceTheLastFollow() => RunGroup(rig => rig.Host.OnWorldAsync(() =>
    {
        Player bot = rig.BotPlayer;
        PlayerbotRoute route = StartFollowWalk(rig, 90f);
        uint lease = PlayerbotMotion.LeaseMsOf(bot);
        Assert.InRange(lease, 2000u, 8000u);
        uint bound = BoundOf(bot);
        Assert.True(bound >= lease);
        uint gate = Gate(rig.Options);

        // Inside the bound measured from the Follow: renewed.
        PlayerbotMotion.ElapseForTests(bot, bound / 2);
        uint renewals = PlayerbotMotion.RenewalsOf(bot);
        rig.Think(gate);
        Assert.Equal(renewals, PlayerbotMotion.RenewalsOf(bot));
        Assert.False(Settle(rig.Bot, bot, renewals, gate, out bool renewed)); // nothing is released: never stops the bot itself
        Assert.True(PlayerbotMotion.IsActive(bot));
        Assert.True(renewed);

        // Past it: not renewed, so the walk is past the lease of the first settle's renewal once that much more time has passed.
        PlayerbotMotion.ElapseForTests(bot, bound / 2 + 100);
        renewals = PlayerbotMotion.RenewalsOf(bot);
        rig.Think(gate);
        Assert.False(Settle(rig.Bot, bot, renewals, gate, out renewed));
        Assert.False(route.Released);
        Assert.False(renewed, "a settle renewed a route that had gone its whole bound without a Follow");
        uint lease2 = PlayerbotMotion.LeaseMsOf(bot);
        Assert.False(WalksAfter(rig.Bot, lease2 + 100), "the unrenewed route outlived its lease");
        Assert.False(PlayerbotMotion.IsActive(bot));
        return true;
    }));

    /// <summary>
    /// Test 5b. A Follow (a decided think that re-asserts the route) starts the bound over: after one, a settle half the bound later
    /// renews again. Fails when Follow does not record its time (the settle then measures from the first Follow and refuses).
    /// </summary>
    [Fact]
    public Task AFollowStartsTheTimeBoundOver_SoTheNextSettleRenewsAgain() => RunGroup(rig => rig.Host.OnWorldAsync(() =>
    {
        Player bot = rig.BotPlayer;
        PlayerbotRoute route = StartFollowWalk(rig, 90f);
        uint bound = BoundOf(bot);
        uint gate = Gate(rig.Options);

        // Let the bound pass: a settle past the bound since the Follow does not renew.
        PlayerbotMotion.ElapseForTests(bot, bound / 2);
        uint renewals = PlayerbotMotion.RenewalsOf(bot);
        rig.Think(gate);
        Assert.False(PlayerbotMotion.SettleAfterDriver(rig.Bot, bot, renewals, gate));
        PlayerbotMotion.ElapseForTests(bot, bound / 2 + 100);
        renewals = PlayerbotMotion.RenewalsOf(bot);
        rig.Think(gate);
        Assert.False(Settle(rig.Bot, bot, renewals, gate, out bool renewed));
        Assert.False(renewed, "the settle renewed past the bound");

        // One decided think re-asserts the route through Follow (the leader has not moved: the same route).
        uint followed = PlayerbotMotion.RenewalsOf(bot);
        rig.Decide();
        Assert.NotEqual(followed, PlayerbotMotion.RenewalsOf(bot));
        Assert.Same(route, PlayerbotMotion.RouteOf(bot));

        PlayerbotMotion.ElapseForTests(bot, BoundOf(bot) / 2);
        renewals = PlayerbotMotion.RenewalsOf(bot);
        rig.Think(gate);
        Assert.False(Settle(rig.Bot, bot, renewals, gate, out renewed));
        Assert.True(renewed, "the time bound was not started over by a Follow");
        Assert.Same(route, PlayerbotMotion.RouteOf(bot));
        return true;
    }));

    /// <summary>
    /// Test 5c. Under a heavy rotation (think gap 8 s, so the lease is held to the 15 s cap, under two gaps) the bound is three gaps (two
    /// plus a margin of one), 24 s: two unfollowed keep-thinks in a row (two instant buffs, one gap apart each) both renew, and a third
    /// past the bound does not. The expected values come from the gap, not from the bound under test. Fails with the bound at the lease
    /// alone (the second settle, 16 s after the Follow, is past the 15 s lease and is refused) and with no bound (the third renews).
    /// </summary>
    [Fact]
    public Task WithALargeThinkGap_TwoUnfollowedSettlesInARowRenew_AndAThirdPastTheBoundDoesNot() => RunGroup(rig => rig.Host.OnWorldAsync(() =>
    {
        Player bot = rig.BotPlayer;
        PlayerbotRoute route = StartFollowWalk(rig, 90f);
        PlayerbotMotion.NoteThinkGap(bot, 8000);
        const uint gap = 8000;
        uint lease = PlayerbotMotion.LeaseMsOf(bot);
        Assert.True(lease < 2 * gap, $"the lease ({lease} ms) is not held under two gaps: this test needs the cap to bite");
        uint bound = BoundOf(bot);
        uint gate = Gate(rig.Options);
        uint step = gap + 1; // 3 steps: age step and 2 x step (16 s) are inside the 3-gap bound (24 s), 3 x step is not

        for (int i = 1; i <= 3; i++)
        {
            PlayerbotMotion.ElapseForTests(bot, step);
            uint renewals = PlayerbotMotion.RenewalsOf(bot);
            rig.Think(gate);
            Assert.False(Settle(rig.Bot, bot, renewals, gate, out bool renewed));
            if (i < 3) Assert.True(renewed, $"unfollowed settle {i} was not renewed (bound {bound} ms)");
            else Assert.False(renewed, $"the settle past the bound ({bound} ms) renewed");
        }

        Assert.False(route.Released);
        return true;
    }));

    // --- the party AI's fight ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Test 6. The party AI's fight returns without a Follow or a Stop when the combat spells' helper says it acted while the bot is still
    /// moving: here the helper's first step, standing the bot up, is refused for want of action budget and it returns true at once.
    /// The fight suspended the follower first, which keeps its follow route (the way FightThrough looks along) but must release it:
    /// otherwise the settle renews a follow walk nobody holds. Drives the real <c>PlayerbotPartyAI.Update</c> (Fight, Suspend, the early
    /// return), then the settle. Fails with Suspend not releasing the kept route.
    /// </summary>
    [Fact]
    public async Task PartyFightThatReturnsWithoutFollowing_ReleasesTheKeptFollowRoute_SoTheSettleStopsTheBot()
    {
        await using WorldTestHost host = PartyTestHost.Start(o => o.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);
        await using WorldTestClient master = await host.EnterWorldAsync("LEASEFGM", "Leasefgm");
        WorldSession session = await PartyTestHost.EnterManagedAsync(host, "LEASEFGB", "Leasefgb");
        try
        {
            PlayerbotOptions options = PartyTestHost.PlayerbotOptionsOf(host);
            var ai = new PlayerbotPartyAI(session, options);
            await host.OnWorldAsync(() =>
            {
                OpenWorld(host);
                Player owner = host.World.FindOnlinePlayer("Leasefgm")!;
                Player bot = session.Player!;
                GroupManager groups = host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;
                groups.Invite(owner, bot.Name);
                groups.Accept(bot);
                Assert.True(ai.Drives(bot));
                owner.Relocate(bot.X, bot.Y + 90f, bot.Z, 0f, host.World.NowMs);

                // The follow walk, as any decided think starts it.
                session.ManagedBudget = new ManagedActionBudget(8);
                ai.Update(bot, DecidedMs(options));
                Assert.True(PlayerbotMotion.IsActive(bot), $"the party AI did not start following (goal {ai.Goal})");
                Assert.True(Forward(bot));
                PlayerbotRoute followRoute = PlayerbotMotion.RouteOf(bot)!;
                Assert.False(followRoute.Released);

                // A hostile on the bot and the bot sitting with no action budget left: the fight's spell helper asks the server to stand
                // the bot up, is refused, and says it acted without stopping the walk.
                Creature attacker = DungeonBotHost.AddCreature(bot.Map!, 990_811, bot.X + 3f, bot.Y, bot.Z, 0, host.World.NowMs, faction: 14);
                bot.Combat.AttackersInternal.Add(attacker);
                bot.SetStandState(StandState.Sit);
                session.ManagedBudget = new ManagedActionBudget(0);
                uint renewals = PlayerbotMotion.RenewalsOf(bot);
                ai.Update(bot, DecidedMs(options));
                Assert.Equal(ArcaneCore.Kernel.Characters.PlayerbotGoalKind.Assist, ai.Goal);
                Assert.Equal(renewals, PlayerbotMotion.RenewalsOf(bot)); // the fight followed nothing
                Assert.Same(followRoute, PlayerbotMotion.RouteOf(bot));  // and stopped nothing
                Assert.True(Forward(bot), "the fight stopped the bot itself; this test needs the early return without a stop");
                Assert.True(followRoute.Released, "Suspend kept the follow route without releasing it");

                Assert.True(PlayerbotMotion.SettleAfterDriver(session, bot, renewals, DecidedMs(options)), "the settle renewed a follow walk the fight had given up");
                Assert.False(PlayerbotMotion.IsActive(bot));
                Assert.False(Forward(bot));
                return true;
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    // --- the elevator trip ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Test 7. A party takes a bot whose lift trip has not boarded yet: <see cref="PlayerbotElevator.Cancel"/> removes the trip while the
    /// bot walks the trip's route to the stop. The trip's route is released with it (the releasing setter), so the settle stops the bot
    /// rather than renewing a walk no one holds. Fails when Cancel removes the trip without clearing its route.
    /// </summary>
    [Fact]
    public async Task ACancelledElevatorTripInToStop_ReleasesItsRoute_SoTheSettleStopsTheBot()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        ElevatorLift lift = await world.GreatLiftAsync();
        await world.OnWorldAsync(() =>
        {
            Player bot = world.Player;
            ElevatorTrip trip = PlayerbotElevator.Begin(world.Session, bot, lift, ElevatorLevel.Low, ElevatorTestWorld.Ratchet);
            Assert.Equal(ElevatorStage.ToStop, trip.Stage);

            // The bot walks the trip's route (what the trip's TryAdvance leaves behind), scripted without a path query.
            Vector3 here = new(bot.X, bot.Y, bot.Z);
            var route = new PlayerbotRoute([here, here + new Vector3(30f, 0f, 0f)], 30f) { NextPoint = 1 };
            trip.Route = route;
            PlayerbotMotion.MotionState state = PlayerbotMotion.Of(bot);
            state.Route = route;
            state.Map = bot.Map;
            state.Anchor = here;
            state.AnchorMs = world.Host.World.NowMs;
            state.RenewedMs = world.Host.World.NowMs;
            state.FollowedMs = world.Host.World.NowMs;
            state.LeaseMs = 4000;
            state.Heading = 0f;
            state.Speed = 0f;
            Assert.True(PlayerbotMotion.IsActive(bot));
            uint renewals = PlayerbotMotion.RenewalsOf(bot);

            PlayerbotElevator.Cancel(bot);
            Assert.Null(PlayerbotElevator.TripOf(bot));
            Assert.True(route.Released, "Cancel removed the trip without releasing the route it walked");

            Assert.True(PlayerbotMotion.SettleAfterDriver(world.Session, bot, renewals, 500), "the settle renewed a walk whose trip was cancelled");
            Assert.False(PlayerbotMotion.IsActive(bot));
        });
    }
}
