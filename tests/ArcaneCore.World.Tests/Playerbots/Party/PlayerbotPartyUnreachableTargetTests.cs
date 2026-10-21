using System.Reflection;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Net;
using ArcaneCore.World.Tests.Playerbots.Dungeon;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// A party bot's own target choice (<c>PlayerbotPartyAI.SelectTarget</c>): a creature it found no path to is skipped for 30 seconds
/// instead of being picked again every think, while the leader's explicit choices (an order, a mark, its own victim) are still honoured.
/// </summary>
public sealed class PlayerbotPartyUnreachableTargetTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly MethodInfo SelectTarget = typeof(PlayerbotPartyAI).GetMethod("SelectTarget", Private)!;
    private static readonly FieldInfo Ordered = typeof(PlayerbotPartyAI).GetField("_ordered", Private)!;

    private static WorldTestHost Host()
        => PartyTestHost.Start(options => options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);

    private static (Player Master, Player Bot) Group(WorldTestHost host, string masterName, WorldSession session)
    {
        Player owner = host.World.FindOnlinePlayer(masterName)!;
        Player bot = session.Player!;
        GroupManagerOf(host).Invite(owner, bot.Name);
        GroupManagerOf(host).Accept(bot);
        Assert.True(GroupManagerOf(host).AreInSameGroup(owner.Guid, bot.Guid));
        return (owner, bot);
    }

    private static ArcaneCore.Game.Groups.GroupManager GroupManagerOf(WorldTestHost host)
        => host.WorldServices.GetRequiredService<ArcaneCore.World.Social.SocialFeature>().Context.Groups;

    /// <summary>A hostile creature attacking the bot, <paramref name="yards"/> east of it.</summary>
    private static Creature Attacker(WorldTestHost host, Player bot, uint low, float yards)
    {
        Creature creature = DungeonBotHost.AddCreature(bot.Map!, low, bot.X + yards, bot.Y, bot.Z, 0, host.World.NowMs, faction: 14);
        bot.Combat.AttackersInternal.Add(creature);
        return creature;
    }

    private static Unit? Select(PlayerbotPartyAI ai, Player bot, Player master) => (Unit?)SelectTarget.Invoke(ai, [bot, master]);

    private static readonly MethodInfo NoteUnreachableTarget = typeof(PlayerbotPartyAI).GetMethod("NoteUnreachableTarget", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

    /// <summary>What a failed chase records (<c>PlayerbotPartyAI.Fight</c> calls the same method when its route is refused).</summary>
    private static void NoPathTo(PlayerbotPartyAI ai, Creature target)
    {
        Assert.NotNull(NoteUnreachableTarget);
        NoteUnreachableTarget.Invoke(ai, [target]);
    }

    private static async Task Run(string account, string master, string bot, Action<PlayerbotPartyAI, Player, Player, WorldTestHost> body)
    {
        await using WorldTestHost host = Host();
        await using WorldTestClient masterClient = await host.EnterWorldAsync(account + "M", master);
        WorldSession session = await PartyTestHost.EnterManagedAsync(host, account + "B", bot);
        try
        {
            var ai = new PlayerbotPartyAI(session, PartyTestHost.PlayerbotOptionsOf(host));
            await host.OnWorldAsync(() =>
            {
                (Player owner, Player self) = Group(host, master, session);
                Assert.True(ai.Drives(self));
                body(ai, owner, self, host);
                return true;
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Fact]
    public async Task ABotThatFoundNoPathToATarget_DoesNotPickItAgain_AndPicksAReachableOneInstead()
        => await Run("PUTA", "Putma", "Putba", (ai, master, bot, host) =>
        {
            Creature near = Attacker(host, bot, 994501, 25f), far = Attacker(host, bot, 994502, 40f);
            Assert.Same(near, Select(ai, bot, master)); // the nearest attacker first
            NoPathTo(ai, near);
            Assert.Same(far, Select(ai, bot, master));
            Assert.Same(far, Select(ai, bot, master)); // and on every think after
        });

    [Fact]
    public async Task AnUnreachableTarget_MayBePickedAgain_OnceTheMemoryExpires()
        => await Run("PUTB", "Putmb", "Putbb", (ai, master, bot, host) =>
        {
            Creature near = Attacker(host, bot, 994511, 25f), far = Attacker(host, bot, 994512, 40f);
            NoPathTo(ai, near);
            ai.ClockOffsetMs = 30_000 - 1_000;
            Assert.Same(far, Select(ai, bot, master)); // still remembered
            ai.ClockOffsetMs = 30_000 + 1_000;
            Assert.Same(near, Select(ai, bot, master));
        });

    [Fact]
    public async Task ATargetThePlayerOrdered_IsStillAttacked_AfterNoPath()
        => await Run("PUTC", "Putmc", "Putbc", (ai, master, bot, host) =>
        {
            Creature ordered = Attacker(host, bot, 994521, 25f), other = Attacker(host, bot, 994522, 10f);
            Ordered.SetValue(ai, ordered.Guid);
            NoPathTo(ai, ordered);
            Assert.Same(ordered, Select(ai, bot, master));
            Assert.NotSame(other, Select(ai, bot, master)); // not displaced by the nearer attacker
        });

    [Fact]
    public async Task TheLeadersOwnVictim_IsStillAttacked_AfterNoPath()
        => await Run("PUTD", "Putmd", "Putbd", (ai, master, bot, host) =>
        {
            Creature victim = DungeonBotHost.AddCreature(bot.Map!, 994531, bot.X + 25f, bot.Y, bot.Z, 0, host.World.NowMs, faction: 14);
            master.Combat.Victim = victim;
            Assert.Same(victim, Select(ai, bot, master));
            NoPathTo(ai, victim);
            Assert.Same(victim, Select(ai, bot, master));
        });

    [Fact]
    public async Task Disengaging_ClearsRememberedUnreachableTargets()
        => await Run("PUTE", "Putme", "Putbe", (ai, master, bot, host) =>
        {
            Creature near = Attacker(host, bot, 994541, 25f), far = Attacker(host, bot, 994542, 40f);
            NoPathTo(ai, near);
            Assert.Same(far, Select(ai, bot, master));
            ai.Disengage(bot);
            Assert.True(ai.Drives(bot));
            Assert.Same(near, Select(ai, bot, master));
        });
}
