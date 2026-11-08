using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Social;
using ArcaneCore.World.Tests.Playerbots.Combat;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The group AI (bot-led groups) fights players under the one hostility rule and sticks to the leader's raid marks, as the party AI does.
/// A real client leads the group; the bot's <see cref="PlayerbotGroupAI"/> is driven by hand, one think at a time.
/// </summary>
public sealed class PlayerbotGroupAiPvpTests
{
    private sealed record Rig(WorldTestHost Host, WorldTestClient Leader, WorldSession Bot, WorldSession Enemy, PlayerbotGroupAI Ai,
        PlayerbotGroupCoordinator.BotGroup Group, PlayerbotGroupCoordinator.Member Me)
    {
        public Player LeaderPlayer => Host.World.FindOnlinePlayer("Gaileader")!;

        public Player BotPlayer => Bot.Player!;

        public Player EnemyPlayer => Enemy.Player!;

        public GroupManager Groups => Host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;

        public void Think()
        {
            Bot.ManagedBudget = new ManagedActionBudget(8);
            Ai.Update(BotPlayer, Group, Me, 500);
        }
    }

    private static async Task Run(Func<Rig, Task> body, bool botLeads = false)
    {
        await using WorldTestHost host = PartyTestHost.Start(options => options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);
        WorldSession bot = await PartyTestHost.EnterManagedAsync(host, "GAIBOT", "Gaibot");
        await using WorldTestClient leader = await host.EnterWorldAsync("GAILEADER", "Gaileader");
        WorldSession enemy = await PlayerbotPvpTestKit.EnterAsync(host, "GAIENEMY", "Gaienemy", PlayerbotPvpTestKit.Orc);
        try
        {
            var ai = new PlayerbotGroupAI(bot, PartyTestHost.PlayerbotOptionsOf(host));
            PlayerbotGroupCoordinator.BotGroup? group = null;
            PlayerbotGroupCoordinator.Member? me = null;
            await host.OnWorldAsync(() =>
            {
                Player b = bot.Player!, l = host.World.FindOnlinePlayer("Gaileader")!, e = enemy.Player!;
                Assert.Same(b.Map, e.Map);
                e.Relocate(b.X + 4, b.Y, b.Z, MathF.PI, host.World.NowMs);
                GroupManager groups = host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;
                groups.Invite(l, b.Name);
                groups.Accept(b);
                bot.Services.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System.Store = new SpellStore([], [], []);
                var goal = new PlayerbotGroupGoal(PlayerbotGroupGoalKind.Quest, 0, 0, b.MapId, b.MapId, Vector3.Zero, 0, Vector3.Zero, 2, 1, "test");
                group = new PlayerbotGroupCoordinator.BotGroup(1, goal, Guid.Empty, botLeads ? b.Guid : l.Guid, host.World.NowMs) { State = PlayerbotGroupState.Engaging };
                me = new PlayerbotGroupCoordinator.Member(Guid.NewGuid(), b.Guid, b.Name, PlayerbotGroupRole.Damage, PlayerbotRole.MeleeDps, 0, real: false)
                    { Joined = true, AI = ai };
                group.Members.Add(new PlayerbotGroupCoordinator.Member(Guid.Empty, l.Guid, l.Name, PlayerbotGroupRole.Tank, PlayerbotRole.Tank, 0, real: true) { Joined = true });
                group.Members.Add(me);
                return true;
            });
            await body(new Rig(host, leader, bot, enemy, ai, group!, me!));
        }
        finally
        {
            bot.Kick();
            enemy.Kick();
            await bot.ManagedClosed;
            await enemy.ManagedClosed;
        }
    }

    [Fact]
    public Task MemberAssistsLeaderAgainstAFlaggedEnemyPlayer() => Run(rig => rig.Host.OnWorldAsync(() =>
    {
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        rig.LeaderPlayer.Combat.Victim = rig.EnemyPlayer;
        rig.Think();
        Assert.Same(rig.EnemyPlayer, rig.Ai.TargetUnit);
        Assert.Null(rig.Ai.Target);
    }));

    [Fact]
    public Task MemberLeavesAnUnflaggedEnemyPlayerAlone() => Run(rig => rig.Host.OnWorldAsync(() =>
    {
        // The unflagged player is the leader's victim and attacks the leader: the same setup as the flagged assist, so the flag alone decides.
        rig.LeaderPlayer.Combat.Victim = rig.EnemyPlayer;
        rig.EnemyPlayer.Combat.Victim = rig.LeaderPlayer;
        Assert.False(rig.BotPlayer.Map!.Combat.Hooks.CanAttack(rig.BotPlayer, rig.EnemyPlayer));
        rig.Think();
        Assert.Null(rig.Ai.TargetUnit);

        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        rig.Think();
        Assert.Same(rig.EnemyPlayer, rig.Ai.TargetUnit);
    }));

    private static void Retreating(Rig rig, PlayerbotSiegeOrder order = PlayerbotSiegeOrder.Retreat)
    {
        var siege = new PlayerbotSiege(1, rig.BotPlayer.MapId, 990_000, [Vector3.Zero], Vector3.Zero, 0);
        rig.Group.Siege = new PlayerbotSiegeRaid(siege, 1) { Order = order };
    }

    [Fact]
    public Task MemberFightsBackAnAttackerWhileTheSiegeRaidRallies_ButNotWhileItDisengages() => Run(rig => rig.Host.OnWorldAsync(() =>
    {
        Retreating(rig);
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        PlayerbotPvpTestKit.Flag(rig.BotPlayer);
        Assert.True(rig.BotPlayer.Map!.Combat.Attack(rig.EnemyPlayer, rig.BotPlayer));
        rig.BotPlayer.Map!.Combat.DealDamage(rig.EnemyPlayer, rig.BotPlayer, 1);
        rig.Think();
        Assert.Null(rig.Ai.TargetUnit); // disengaging: nobody fights

        Retreating(rig, PlayerbotSiegeOrder.Rally);
        rig.Think();
        Assert.Same(rig.EnemyPlayer, rig.Ai.TargetUnit); // at the rally point the member answers its attacker
    }));

    /// <summary>
    /// The bot leads a rallying siege raid and its own attacker (a flagged player) stands 7 yd east of it. <paramref name="moveToDx"/> puts the retreat point
    /// that far east of the bot (negative: west), so the attacker is 7 - moveToDx yd from the point; the bot stays within RallyYards of the point.
    /// </summary>
    private static (float X, float Y) LeaderRallies(Rig rig, float moveToDx, bool noPath)
    {
        Player bot = rig.BotPlayer;
        rig.Bot.ManagedBudget = new ManagedActionBudget(8);
        rig.Enemy.Player!.Relocate(bot.X + 7, bot.Y, bot.Z, MathF.PI, rig.Host.World.NowMs);
        var siege = new PlayerbotSiege(1, bot.MapId, 990_000, [Vector3.Zero], Vector3.Zero, 0);
        rig.Group.Siege = new PlayerbotSiegeRaid(siege, 1)
            { Order = PlayerbotSiegeOrder.Rally, RetreatNoPath = noPath, MoveTo = new Vector3(bot.X + moveToDx, bot.Y, bot.Z) };
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        PlayerbotPvpTestKit.Flag(bot);
        Assert.True(bot.Map!.Combat.Attack(rig.EnemyPlayer, bot));
        bot.Map!.Combat.DealDamage(rig.EnemyPlayer, bot, 1);
        Assert.True(rig.Group.LeaderPlayer(rig.Host.World)!.Guid == bot.Guid, "the bot leads the group");
        return (bot.X, bot.Y);
    }

    [Fact]
    public Task RallyingLeaderKeepsItsAttackerBeyondRallyYardsAsItsTargetButDoesNotChaseIt() => Run(rig => rig.Host.OnWorldAsync(() =>
    {
        (float x, float y) = LeaderRallies(rig, -5, noPath: false);
        Assert.True(Vector3.Distance(rig.Group.Siege!.MoveTo, new Vector3(rig.EnemyPlayer.X, rig.EnemyPlayer.Y, rig.EnemyPlayer.Z)) > PlayerbotSiegeCommander.RallyYards);
        for (int i = 0; i < 4; i++)
        {
            rig.Think();
            Assert.Same(rig.EnemyPlayer, rig.Ai.TargetUnit); // still the target: no route failure dropped it
            Assert.StartsWith("fight:player:chase", rig.Ai.Step); // it wants to close in, but the leash holds it: no route is planned (that would end in no-path)
            Assert.Equal((x, y), (rig.BotPlayer.X, rig.BotPlayer.Y));
        }
    }), botLeads: true);

    [Fact]
    public Task RallyingLeaderChasesAnAttackerWithinRallyYardsOfThePoint() => Run(rig => rig.Host.OnWorldAsync(() =>
    {
        LeaderRallies(rig, 0, noPath: false);
        Assert.True(Vector3.Distance(rig.Group.Siege!.MoveTo, new Vector3(rig.EnemyPlayer.X, rig.EnemyPlayer.Y, rig.EnemyPlayer.Z)) <= PlayerbotSiegeCommander.RallyYards);
        rig.Think();
        // Past the leash check the chase asks for a route; the rig has no navmesh, so the plan fails and the step says so.
        // The chase got past the leash to the route planner; the rig has no navmesh, so the plan fails: that is the step, and the target is dropped.
        Assert.Equal("fight:player:no-path", rig.Ai.Step);
        Assert.Null(rig.Ai.TargetUnit);
    }), botLeads: true);

    [Fact]
    public Task RallyingLeaderWithNoPathChasesEvenAnAttackerBeyondRallyYards() => Run(rig => rig.Host.OnWorldAsync(() =>
    {
        LeaderRallies(rig, -5, noPath: true);
        Assert.True(Vector3.Distance(rig.Group.Siege!.MoveTo, new Vector3(rig.EnemyPlayer.X, rig.EnemyPlayer.Y, rig.EnemyPlayer.Z)) > PlayerbotSiegeCommander.RallyYards);
        rig.Think();
        // The chase got past the leash to the route planner; the rig has no navmesh, so the plan fails: that is the step, and the target is dropped.
        Assert.Equal("fight:player:no-path", rig.Ai.Step);
        Assert.Null(rig.Ai.TargetUnit);
    }), botLeads: true);

    /// <summary>
    /// The bot leads a siege raid that pushes through at its waypoint (waiting). Its attacker, a flagged player, stands <paramref name="attackerDx"/> yd east of the bot (default 7); the waypoint is <paramref name="waypointDx"/> east of the bot
    /// (negative: west), so the attacker is attackerDx - waypointDx yd from the waypoint, measured from the waypoint and not from the bot.
    /// </summary>
    private static void WaitingLeader(Rig rig, float waypointDx, bool answerSpent, float attackerDx = 7f)
    {
        Player bot = rig.BotPlayer;
        rig.Bot.ManagedBudget = new ManagedActionBudget(8);
        rig.Enemy.Player!.Relocate(bot.X + attackerDx, bot.Y, bot.Z, MathF.PI, rig.Host.World.NowMs);
        var siege = new PlayerbotSiege(1, bot.MapId, 990_000, [Vector3.Zero], Vector3.Zero, 0);
        rig.Group.Siege = new PlayerbotSiegeRaid(siege, 1)
            { Push = PlayerbotSiegePush.Quiet, AtWaypoint = true, AnswerSpent = answerSpent, MoveTo = new Vector3(bot.X + waypointDx, bot.Y, bot.Z) };
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        PlayerbotPvpTestKit.Flag(bot);
        Assert.True(bot.Map!.Combat.Attack(rig.EnemyPlayer, bot));
        bot.Map!.Combat.DealDamage(rig.EnemyPlayer, bot, 1);
        Assert.True(rig.Group.LeaderPlayer(rig.Host.World)!.Guid == bot.Guid, "the bot leads the group");
    }

    /// <summary>
    /// The fight-chase leash of a waiting siege leader (Fight's leash radius at the waiting-leader call): an attacker 20 yd from the waypoint, past WaypointYards and inside SiegeAnswerLeashYards, is chased
    /// while the step's answer is not spent. A leash narrowed to WaypointYards at that call would stand the leader at "chase" and never reach the route planner (which fails here, the rig has no navmesh).
    /// </summary>
    [Fact]
    public Task WaitingSiegeLeaderChasesAnAttackerBetweenWaypointYardsAndTheAnswerLeash_MeasuredFromTheWaypoint() => Run(rig => rig.Host.OnWorldAsync(() =>
    {
        WaitingLeader(rig, -13, answerSpent: false);
        float fromWaypoint = Vector3.Distance(rig.Group.Siege!.MoveTo, new Vector3(rig.EnemyPlayer.X, rig.EnemyPlayer.Y, rig.EnemyPlayer.Z));
        Assert.InRange(fromWaypoint, PlayerbotSiegeCommander.WaypointYards + 1, PlayerbotSiegeCommander.SiegeAnswerLeashYards - 1);
        rig.Think();
        Assert.Equal("fight:player:no-path", rig.Ai.Step);
        Assert.Null(rig.Ai.TargetUnit);
    }), botLeads: true);

    /// <summary>
    /// The leash centre is the waypoint, not the bot's own position, when the bot stands off the waypoint on the far side: the bot is 10 yd west of the waypoint and the attacker 25 yd east of it, so it is 25 yd from the
    /// waypoint (inside SiegeAnswerLeashYards, chased) but 35 yd from the bot (a leash centred on the bot would hold the leader at "chase" without a route).
    /// </summary>
    [Fact]
    public Task WaitingSiegeLeaderChasesAnAttackerOnTheFarSideOfTheWaypoint_TheLeashIsCentredOnTheWaypointNotOnTheBot() => Run(rig => rig.Host.OnWorldAsync(() =>
    {
        WaitingLeader(rig, 10, answerSpent: false, attackerDx: 35);
        var attacker = new Vector3(rig.EnemyPlayer.X, rig.EnemyPlayer.Y, rig.EnemyPlayer.Z);
        Assert.InRange(Vector3.Distance(rig.Group.Siege!.MoveTo, attacker), PlayerbotSiegeCommander.WaypointYards + 1, PlayerbotSiegeCommander.SiegeAnswerLeashYards - 1);
        Assert.True(Vector3.Distance(new Vector3(rig.BotPlayer.X, rig.BotPlayer.Y, rig.BotPlayer.Z), attacker) > PlayerbotSiegeCommander.SiegeAnswerLeashYards);
        rig.Think();
        Assert.Equal("fight:player:no-path", rig.Ai.Step); // past the leash check the chase asks for a route; the rig has no navmesh, so it fails
        Assert.Null(rig.Ai.TargetUnit);
    }), botLeads: true);

    /// <summary>
    /// The leash is centred on the waypoint, not on the bot: the attacker stands 7 yd from the bot but 32 yd from the waypoint (past the answer leash), so it is not chased.
    /// A leash measured from the bot would take it (7 yd).
    /// </summary>
    [Fact]
    public Task WaitingSiegeLeaderIgnoresAnAttackerBeyondTheAnswerLeashOfTheWaypoint_EvenWhenNearTheBot() => Run(rig => rig.Host.OnWorldAsync(() =>
    {
        WaitingLeader(rig, -25, answerSpent: false);
        float fromWaypoint = Vector3.Distance(rig.Group.Siege!.MoveTo, new Vector3(rig.EnemyPlayer.X, rig.EnemyPlayer.Y, rig.EnemyPlayer.Z));
        Assert.True(fromWaypoint > PlayerbotSiegeCommander.SiegeAnswerLeashYards);
        rig.Think();
        Assert.Null(rig.Ai.TargetUnit);
        Assert.Equal("lead:siege-hold", rig.Ai.Step); // not chased: a chase that failed ends at fight:player:no-path with the same null target
    }), botLeads: true);

    /// <summary>With the step's answer spent the waiting leader only defends within WaypointYards: an attacker 20 yd from the waypoint is not targeted.</summary>
    [Fact]
    public Task WaitingSiegeLeaderWithASpentAnswerIgnoresAnAttackerPastWaypointYards() => Run(rig => rig.Host.OnWorldAsync(() =>
    {
        WaitingLeader(rig, -13, answerSpent: true);
        float fromWaypoint = Vector3.Distance(rig.Group.Siege!.MoveTo, new Vector3(rig.EnemyPlayer.X, rig.EnemyPlayer.Y, rig.EnemyPlayer.Z));
        Assert.True(fromWaypoint > PlayerbotSiegeCommander.WaypointYards);
        rig.Think();
        Assert.Null(rig.Ai.TargetUnit);
        Assert.Equal("lead:siege-hold", rig.Ai.Step);
    }), botLeads: true);

    private static CreatureTemplate SlowCreature(uint entry) => new()
        { Entry = entry, Name = "dummy" + entry, CreatureType = 1, MinLevel = 1, MaxLevel = 1, MinLevelHealth = 500, MaxLevelHealth = 500, SpeedRun = 0.5f };

    /// <summary>Flat ground at <c>z</c>: what the kite's floor probes, leg clip and line-of-sight checks read.</summary>
    private sealed class FlatGround(float z) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z2, float maxSearchDistance) => z;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z2, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }

    /// <summary>
    /// The kite is the only observable effect of the leash the waiting leader's fight hands to <c>PlayerbotKite.Update</c>: the bot is a hurt mage (45% health) that leads a siege raid waiting at its waypoint, with a creature
    /// of 0.5 run speed (out-walked, so no slow is needed) 2 yd from it, and the waypoint under its feet. Every kite step is 10 yd long (the clip keeps at least 7.5), so it ends past WaypointYards of the waypoint in every direction.
    /// Unspent, the leash is SiegeAnswerLeashYards and the first think steps (the step ends ":kite"); spent, it is WaypointYards and no direction has a spot, so no think kites and the bot stays within the disk. The unspent
    /// run is the control: the same setup does step. Replacing the spent leash passed to the kite by the answer leash (at the waiting-leader Fight call, or in the kite call inside Fight) makes the spent run step.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AHurtRangedWaitingSiegeLeaderKitesWithinTheAnswerLeash_AndNotPastWaypointYardsOnceTheAnswerIsSpent(bool answerSpent) => Run(rig => rig.Host.OnWorldAsync(() =>
    {
        Player bot = rig.BotPlayer;
        WorldCollision.Of(rig.Host.World).Install(lineOfSight: new FlatGround(bot.Z));
        rig.Enemy.Player!.Relocate(bot.X + 150, bot.Y, bot.Z, MathF.PI, rig.Host.World.NowMs); // nobody else near
        bot.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        var fireball = new SpellInfo
        {
            Id = 992_501, Name = "Fireball", Rank = "Rank 1", CastTime = new SpellCastTime(0, 0, 0), RangeIndex = 5, Range = new SpellRange(0f, 30f),
            StartRecoveryCategory = 133, StartRecoveryTime = 1500, DamageClass = SpellDamageClass.Magic,
            Effects = [new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage, TargetA = SpellImplicitTarget.UnitEnemy, BasePoints = 0, BaseDice = 1, DieSides = 1 }, new SpellEffectInfo(), new SpellEffectInfo()],
        };
        var spellFeature = rig.Host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>();
        spellFeature.System.Store = new SpellStore([.. spellFeature.System.Store.All, fireball], [], []);
        Assert.True(spellFeature.Spellbook.LearnSpell(bot, fireball.Id));

        var siege = new PlayerbotSiege(1, bot.MapId, 990_000, [Vector3.Zero], Vector3.Zero, 0);
        rig.Group.Siege = new PlayerbotSiegeRaid(siege, 1)
            { Push = PlayerbotSiegePush.Quiet, AtWaypoint = true, AnswerSpent = answerSpent, MoveTo = new Vector3(bot.X, bot.Y, bot.Z) };
        Assert.True(rig.Group.LeaderPlayer(rig.Host.World)!.Guid == bot.Guid, "the bot leads the group");

        var system = new CreatureMapSystem(bot.Map!, new CreatureContent([SlowCreature(990_441)], [], [], [], []), random: new Random(5));
        Creature mob = system.SpawnTemporary(SlowCreature(990_441), bot.X + 2, bot.Y, bot.Z, 0f);
        Assert.True(bot.Map!.Combat.Attack(mob, bot));
        bot.Map!.Combat.DealDamage(mob, bot, 1);
        bot.Health = bot.MaxHealth * 45 / 100;
        Assert.InRange(Vector3.Distance(rig.Group.Siege!.MoveTo, new Vector3(mob.X, mob.Y, mob.Z)), 1f, PlayerbotSiegeCommander.WaypointYards - 1);
        Assert.True(mob.RunSpeed <= bot.RunSpeed * PlayerbotKiteModel.SlowSpeedRatio, "the creature is out-walked, so the kite needs no slow");

        if (!answerSpent)
        {
            rig.Think();
            Assert.Same(mob, rig.Ai.TargetUnit);
            Assert.EndsWith(":kite", rig.Ai.Step);
            return;
        }

        for (int i = 0; i < 3; i++)
        {
            rig.Think();
            Assert.Same(mob, rig.Ai.TargetUnit);
            Assert.False(rig.Ai.Step.EndsWith(":kite", StringComparison.Ordinal), "a kite step toward a spot past the waypoint disk: " + rig.Ai.Step);
            Assert.True(Vector3.Distance(rig.Group.Siege!.MoveTo, new Vector3(bot.X, bot.Y, bot.Z)) <= PlayerbotSiegeCommander.WaypointYards);
        }
    }), botLeads: true);

    [Fact]
    public Task MemberPicksNoNewTargetWhileTheSiegeRaidRetreats_NeitherTheLeadersVictimNorAMarkedOne() => Run(rig => rig.Host.OnWorldAsync(() =>
    {
        Retreating(rig);
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        rig.LeaderPlayer.Combat.Victim = rig.EnemyPlayer;
        rig.Think();
        Assert.Null(rig.Ai.TargetUnit);
        Assert.Null(rig.Ai.Target);

        // The same setup without the retreat assists the leader (the control: the retreat alone made the member hold back).
        rig.Group.Siege = null;
        rig.Think();
        Assert.Same(rig.EnemyPlayer, rig.Ai.TargetUnit);
    }));

    [Fact]
    public Task MemberDefendsTheLeaderAttackedByAFlaggedPlayer() => Run(rig => rig.Host.OnWorldAsync(() =>
    {
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        PlayerbotPvpTestKit.Flag(rig.LeaderPlayer);
        Assert.True(rig.LeaderPlayer.Map!.Combat.Attack(rig.EnemyPlayer, rig.LeaderPlayer));
        rig.LeaderPlayer.Map!.Combat.DealDamage(rig.EnemyPlayer, rig.LeaderPlayer, 1);
        rig.Think();
        Assert.Same(rig.EnemyPlayer, rig.Ai.TargetUnit);
    }));

    private static Creature Spawn(Player near, uint entry, float dx, float dy, int seed)
    {
        var template = new CreatureTemplate { Entry = entry, Name = "dummy" + entry, CreatureType = 1, MinLevel = 1, MaxLevel = 1, MinLevelHealth = 500, MaxLevelHealth = 500 };
        var system = new CreatureMapSystem(near.Map!, new CreatureContent([template], [], [], [], []), random: new Random(seed));
        return system.SpawnTemporary(template, near.X + dx, near.Y + dy, near.Z, 0f);
    }

    private static async Task Mark(Rig rig, byte icon, Creature target)
    {
        var packet = new PacketWriter();
        packet.WriteByte(icon);
        packet.WriteUInt64(target.Guid.Value);
        await rig.Leader.SendAsync(WorldOpcode.MsgRaidTargetUpdate, packet.ToArray());
        await rig.Host.WaitForWorldAsync(() => rig.Groups.GetGroup(rig.LeaderPlayer.Guid)?.TargetIcons[icon] == target.Guid, "the mark is set");
    }

    [Fact]
    public Task SkullMarkBeatsTheLeadersVictim() => Run(async rig =>
    {
        (Creature victim, Creature skull) = await rig.Host.OnWorldAsync(() =>
        {
            Creature v = Spawn(rig.BotPlayer, 990_431, -3, 0, 1), s = Spawn(rig.BotPlayer, 990_432, 0, 3, 2);
            rig.LeaderPlayer.Combat.Victim = v;
            return (v, s);
        });
        await Mark(rig, 7, skull);
        await rig.Host.OnWorldAsync(() =>
        {
            rig.Think();
            Assert.Same(skull, rig.Ai.Target);
            Assert.NotSame(victim, rig.Ai.Target);
        });
    });

    [Fact]
    public Task AMembersFocusIconComesBeforeTheSkull() => Run(async rig =>
    {
        (Creature skull, Creature cross) = await rig.Host.OnWorldAsync(() =>
        {
            Creature v = Spawn(rig.BotPlayer, 990_431, -3, 0, 1), s = Spawn(rig.BotPlayer, 990_432, 0, 3, 2), c = Spawn(rig.BotPlayer, 990_433, 3, 0, 3);
            rig.LeaderPlayer.Combat.Victim = v;
            return (s, c);
        });
        await Mark(rig, 7, skull);
        await Mark(rig, 6, cross);
        await rig.Host.OnWorldAsync(() =>
        {
            rig.Me.FocusIcon = 6;
            rig.Think();
            Assert.Same(cross, rig.Ai.Target);
        });
    });
}
