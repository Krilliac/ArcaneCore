using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
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

    private static async Task Run(Func<Rig, Task> body)
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
                group = new PlayerbotGroupCoordinator.BotGroup(1, goal, Guid.Empty, l.Guid, host.World.NowMs) { State = PlayerbotGroupState.Engaging };
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

    private static void Retreating(Rig rig)
    {
        var siege = new PlayerbotSiege(1, rig.BotPlayer.MapId, 990_000, [Vector3.Zero], Vector3.Zero, 0);
        rig.Group.Siege = new PlayerbotSiegeRaid(siege, 1) { Order = PlayerbotSiegeOrder.Retreat };
    }

    [Fact]
    public Task MemberFightsBackAnAttackerWhileTheSiegeRaidRetreats() => Run(rig => rig.Host.OnWorldAsync(() =>
    {
        Retreating(rig);
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        PlayerbotPvpTestKit.Flag(rig.BotPlayer);
        Assert.True(rig.BotPlayer.Map!.Combat.Attack(rig.EnemyPlayer, rig.BotPlayer));
        rig.BotPlayer.Map!.Combat.DealDamage(rig.EnemyPlayer, rig.BotPlayer, 1);
        rig.Think();
        Assert.Same(rig.EnemyPlayer, rig.Ai.TargetUnit);
    }));

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
