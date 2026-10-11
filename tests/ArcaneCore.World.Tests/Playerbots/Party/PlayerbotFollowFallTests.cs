using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Social;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// A follower's drop always ends on the ground or is forgotten, never in the air (no terrain data: flat ground and a hand-made drop
/// plan, the bot at its edge). An order that stops the follow mid-air lands the fall first; a bot that dies or is teleported
/// away mid-air does not land at the old drop afterwards.
/// </summary>
public sealed class PlayerbotFollowFallTests
{
    private const float FallYards = 12f;

    [Fact]
    public async Task AStayOrderMidFall_LandsTheFallFirst_AndTheBotHoldsOnTheGround()
    {
        await using Fall fall = await Fall.StartAsync("FALLSTAY", "Fallstay");
        await fall.Master.SendChatAsync(ChatType.Whisper, Language.Common, "stay", fall.BotName);
        await fall.Host.WaitForWorldAsync(() =>
        {
            fall.Session.ManagedBudget = null;
            fall.AI.Intake(fall.Bot);
            return fall.AI.Mode == PlayerbotPartyMode.Stay;
        }, "the bot takes the stay order");

        await fall.Host.OnWorldAsync(() =>
        {
            Assert.False(fall.AI.Follower.Airborne);
            Assert.Equal(1, fall.AI.Follower.Drops);
            Assert.InRange(fall.Bot.Z, fall.Plan.Landing.Z - 0.2f, fall.Plan.Landing.Z + 0.2f);
            Assert.Equal(MovementFlags.None, fall.Bot.Movement.Flags & MovementFlags.MaskMoving);
            fall.AI.Update(fall.Bot, 1_000); // the place to hold is taken where it stands: on the floor below
            Assert.InRange(fall.Bot.Z, fall.Plan.Landing.Z - 0.2f, fall.Plan.Landing.Z + 0.2f);
        });
    }

    [Fact]
    public async Task ABotThatDiesMidFall_IsNotLandedAtTheOldDrop_AfterItsRevival()
    {
        await using Fall fall = await Fall.StartAsync("FALLDEAD", "Falldead");
        await fall.Host.OnWorldAsync(() =>
        {
            Player bot = fall.Bot;
            bot.Health = 0;
            bot.Map!.Combat.KillPlayer(bot);
            fall.AI.Update(bot, 1_000);
            Assert.False(fall.AI.Follower.Airborne);
            bot.Map!.Combat.ResurrectPlayer(bot, 1f, applySickness: false);
            Assert.True(bot.IsAlive);
        });
        await Task.Delay(1_500); // past the fall's time
        await fall.Host.OnWorldAsync(() =>
        {
            fall.AI.Follower.ContinueFall(fall.Bot);
            Assert.Equal(0, fall.AI.Follower.Drops);
            Assert.True(MathF.Abs(fall.Bot.Z - fall.Plan.Landing.Z) > 1f, $"landed at the old drop: {fall.Bot.Z}");
        });
    }

    [Fact]
    public async Task ABotTeleportedAwayMidFall_IsNotLandedAtTheOldDrop()
    {
        await using Fall fall = await Fall.StartAsync("FALLPORT", "Fallport");
        Vector3 away = await fall.Host.OnWorldAsync(() => new Vector3(fall.Bot.X + 200f, fall.Bot.Y, fall.Plan.Edge.Z));
        TeleportService teleports = fall.Host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
        await fall.Host.OnWorldAsync(() => Assert.True(teleports.TeleportTo(fall.Bot, fall.Bot.MapId, away.X, away.Y, away.Z, 0)));
        await fall.Host.OnWorldAsync(() =>
        {
            fall.AI.Follower.ContinueFall(fall.Bot); // a teleport pending: the fall is forgotten at once
            Assert.False(fall.AI.Follower.Airborne);
        });
        await fall.Host.WaitForWorldAsync(() =>
        {
            PlayerbotMovementControl.Update(fall.Session, fall.Bot);
            return teleports.StageOf(fall.Bot) is null;
        }, "the teleport completes");
        await Task.Delay(1_500);
        await fall.Host.OnWorldAsync(() =>
        {
            fall.AI.Follower.ContinueFall(fall.Bot);
            Assert.Equal(0, fall.AI.Follower.Drops);
            Assert.Equal(away.X, fall.Bot.X, 1);
            Assert.True(MathF.Abs(fall.Bot.Z - fall.Plan.Landing.Z) > 1f, $"landed at the old drop: {fall.Bot.Z}");
        });
    }

    /// <summary>A party bot (hand-driven) grouped with a socket master, standing at the edge of a hand-made 12-yard drop, falling.</summary>
    private sealed class Fall : IAsyncDisposable
    {
        public required WorldTestHost Host { get; init; }

        public required WorldTestClient Master { get; init; }

        public required WorldSession Session { get; init; }

        public required PlayerbotPartyAI AI { get; init; }

        public required PlayerbotDropPlan Plan { get; init; }

        public required string BotName { get; init; }

        public Player Bot => Session.Player!;

        public static async Task<Fall> StartAsync(string account, string name)
        {
            WorldTestHost host = PartyTestHost.Start(o => o.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);
            WorldTestClient master = await host.EnterWorldAsync(account + "M", name + "m");
            WorldSession session = await PartyTestHost.EnterManagedAsync(host, account + "B", name);
            float ground = await host.PlayerStateAsync(name, p => p.Z);
            await PartyTestHost.InstallFlatGroundAsync(host, ground);
            var ai = new PlayerbotPartyAI(session, PartyTestHost.PlayerbotOptionsOf(host));
            PlayerbotDropPlan plan = await host.OnWorldAsync(() =>
            {
                Player bot = session.Player!;
                Player owner = host.World.FindOnlinePlayer(name + "m")!;
                GroupManager groups = host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;
                groups.Invite(owner, bot.Name);
                groups.Accept(bot);
                Assert.True(ai.Drives(bot));
                var edge = new Vector3(bot.X, bot.Y, bot.Z);
                var stepOff = edge with { X = edge.X + 0.5f };
                var drop = new PlayerbotDropPlan(null, edge, stepOff, stepOff with { Z = edge.Z - FallYards }, FallYards, 0);
                Assert.True(ai.Follower.StartDropForTests(bot, drop, drop.Landing), "the fall did not start");
                Assert.True((bot.Movement.Flags & MovementFlags.FallingFar) != 0);
                return drop;
            });
            return new Fall { Host = host, Master = master, Session = session, AI = ai, Plan = plan, BotName = name };
        }

        public async ValueTask DisposeAsync()
        {
            Session.Kick();
            await Session.ManagedClosed;
            await Master.DisposeAsync();
            await Host.DisposeAsync();
        }
    }
}
