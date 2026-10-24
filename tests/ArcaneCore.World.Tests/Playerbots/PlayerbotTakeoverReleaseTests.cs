using System.Collections;
using System.Numerics;
using System.Reflection;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Tests.Playerbots.Groups;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The takeover of a bot that walks a route of its brain by a bot-led group (<c>ManagedPlayerbotFeature.ThinkCore</c>): the first group-driven
/// think marks the brain's route released (<c>ReleaseBrainRoute</c>) without stopping the bot, and the driver's settle
/// (<see cref="PlayerbotMotion.SettleAfterDriver"/>) stops the bot unless that think followed a route of its own. The battleground branch calls the
/// same two functions in the same order; it is not driven here because a battleground tracked in a driving phase needs a whole queue and match
/// fixture that the bot-led group branch does not.
/// The real <c>ThinkCore</c> is called by reflection on the world thread, with the coordinator's membership set directly (its private
/// <c>_memberOf</c>), so the feature's own call order is what is tested.
/// </summary>
public sealed class PlayerbotTakeoverReleaseTests
{
    private const byte Human = 1, Warrior = 1;

    private static async Task Run(bool decided, Action<Player, PlayerbotRoute> assert)
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest]);
        var bot = await world.AddBotAsync("Takeoverbot", Human, Warrior, 10, []);
        await using WorldTestClient leader = await world.World.EnterWorldAsync("TAKEL", "Takelead", AccountSecurity.Player);
        await world.OnWorldAsync(() =>
        {
            WorldSession session = world.Session(bot.Id);
            Player player = session.Player!;
            Player lead = world.World.Host.World.FindOnlinePlayer("Takelead")!;
            lead.Relocate(player.X + 90f, player.Y, player.Z, 0f, world.World.Host.World.NowMs);

            // The brain walks a route of its own (west), as the brain's goals do.
            var brainRoute = new PlayerbotRoute([new Vector3(player.X, player.Y, player.Z), new Vector3(player.X - 90f, player.Y, player.Z)], 90f);
            Assert.True(PlayerbotMotion.Follow(session, player, brainRoute, world.Options, world.World.Host.World.NowMs));
            Assert.True(player.Movement.HasFlag(MovementFlags.Forward));

            // A bot-led group takes the bot: it is a member the coordinator drives, its leader a real player east of it.
            var goal = new PlayerbotGroupGoal(PlayerbotGroupGoalKind.Quest, 0, 0, player.MapId, player.MapId, Vector3.Zero, 0, Vector3.Zero, 2, 1, "test");
            var group = new PlayerbotGroupCoordinator.BotGroup(1, goal, Guid.Empty, lead.Guid, world.World.Host.World.NowMs) { State = PlayerbotGroupState.Engaging };
            var me = new PlayerbotGroupCoordinator.Member(bot.Id, player.Guid, player.Name, PlayerbotGroupRole.Damage, PlayerbotRole.MeleeDps, 0, real: false)
                { Joined = true, AI = new PlayerbotGroupAI(session, world.Options) };
            group.Members.Add(new PlayerbotGroupCoordinator.Member(Guid.Empty, lead.Guid, lead.Name, PlayerbotGroupRole.Tank, PlayerbotRole.Tank, 0, real: true) { Joined = true });
            group.Members.Add(me);
            ((IDictionary)typeof(PlayerbotGroupCoordinator).GetField("_memberOf", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world.Coordinator)!)[bot.Id] = group;
            Assert.True(world.Coordinator.Drives(bot.Id));

            // The bot's first group-driven think, a gate-only one or a decided one: the think length is the time since its last update.
            object active = ((IDictionary)typeof(ManagedPlayerbotFeature).GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(world.Bots)!)[bot.Id]!;
            uint ms = decided ? (uint)world.Options.ThinkIntervalMs + 400u : Math.Max(1u, (uint)world.Options.ThinkIntervalMs / 12u);
            active.GetType().GetField("LastUpdateMs")!.SetValue(active, unchecked(world.World.Host.World.NowMs - ms));
            var thinkCore = typeof(ManagedPlayerbotFeature).GetMethod("ThinkCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Equal("group", (string)thinkCore.Invoke(world.Bots, [active, new ManagedActionBudget(8), 0])!);
            Assert.True((bool)active.GetType().GetField("GroupDriven")!.GetValue(active)!, "the think was not a group-driven takeover");
            assert(player, brainRoute);
            return true;
        });
    }

    /// <summary>Gate-only first think of the group AI: it follows nothing, the brain's route stays marked released and the settle stops the bot.</summary>
    [Fact]
    public Task AGateOnlyFirstGroupThink_AfterTheBrainsRoute_StopsTheBot() => Run(decided: false, (player, brainRoute) =>
    {
        Assert.True(brainRoute.Released, "the takeover did not mark the brain's route released");
        Assert.False(PlayerbotMotion.IsActive(player), "the bot kept walking a route its brain left behind");
        Assert.False(player.Movement.HasFlag(MovementFlags.Forward));
    });

    /// <summary>A decided first think follows a route of the group's own: the bot keeps walking, on that route and not the brain's.</summary>
    [Fact]
    public Task ADecidedFirstGroupThink_AfterTheBrainsRoute_KeepsTheBotWalkingTheGroupsRoute() => Run(decided: true, (player, brainRoute) =>
    {
        Assert.True(PlayerbotMotion.IsActive(player), "the settle stopped a bot whose group AI had followed a route");
        Assert.True(player.Movement.HasFlag(MovementFlags.Forward));
        PlayerbotRoute now = PlayerbotMotion.RouteOf(player)!;
        Assert.NotSame(brainRoute, now);
        Assert.False(now.Released);
    });
}
