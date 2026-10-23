using System.Numerics;
using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Progression;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// "On the same goal" for a dungeon goal (docs/areas/playbots-groups.md, Cooperation): a player inside the dungeon, or at its entrance on the
/// bot's continent (<see cref="PlayerbotCooperationOptions.SameGoalMeetingRadiusYards"/>) and in level range, is on the goal; a stranger
/// anywhere else on the continent is not. Through the real path: a player's CMSG_GROUP_INVITE to a waiting bot, answered by the bot's party intake
/// (<c>InviteStandingOf</c> and the affinity policy).
/// </summary>
public sealed class PlayerbotSameGoalWorldTests
{
    private const byte Human = 1, Warrior = 1;

    private static byte[] CString(string text) => [.. Encoding.UTF8.GetBytes(text), 0];

    private static void Options(PlayerbotOptions options)
    {
        options.MoveSpeed = 0.1f;
        options.Social.Guilds.Enabled = false;
        options.Groups.Cooperation.Recruit = false;
    }

    /// <summary>A level 12 warrior with the Deadmines quest at the entrance, waiting for a group of three.</summary>
    private static async Task<(Guid Id, string Name)> WaitingBotAsync(GroupTestWorld world)
    {
        var bot = await world.AddBotAsync("Gateguard", Human, Warrior, 12, [Taunt], EntranceArea);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Any(w => w.BotId == bot.Id && w.Goal.InInstance)), await world.TraceAsync());
        return bot;
    }

    /// <summary>A real player of level 12 at <paramref name="at"/> (null: where it entered, the human start, a continent away from the entrance).</summary>
    private static async Task<WorldTestClient> PlayerAsync(GroupTestWorld world, string name, Vector3? at)
    {
        WorldTestClient client = await world.World.EnterWorldAsync(name.ToUpperInvariant(), name, AccountSecurity.Player);
        await world.OnWorldAsync(() =>
        {
            Player player = world.World.Host.World.FindOnlinePlayer(name)!;
            world.World.Services.GetRequiredService<ProgressionFeature>().Progression.GiveLevel(player, 12);
            if (at is { } spot) player.Relocate(spot.X, spot.Y, spot.Z, 0, world.World.Host.World.NowMs);
            return true;
        });
        return client;
    }

    private static Task RememberAsync(GroupTestWorld world, (Guid Id, string Name) bot, string playerName, short affinity, PlayerbotRelationFlags flags = PlayerbotRelationFlags.None)
        => world.OnWorldAsync(() =>
        {
            Player self = world.Player(bot.Id), them = world.World.Host.World.FindOnlinePlayer(playerName)!;
            world.Bots.Social.Memory.Load([new PlayerbotRelation(bot.Id, (int)self.Guid.Low, them.Guid.Value, true, affinity, 6, 500, flags, world.Bots.Social.Memory.NowMs, "", "")]);
            return true;
        });

    /// <summary>
    /// The player invites the bot; true when the bot accepted (they stand in one group), false when it declined. The outcome is the bot's own
    /// answer (the packet its party intake sends), awaited from before the invite is sent: a poll of the group state alone could read "no
    /// invite, no group" before the server had even processed the invitation.
    /// </summary>
    private static async Task<bool> InviteAcceptedAsync(GroupTestWorld world, WorldTestClient player, string playerName, (Guid Id, string Name) bot)
    {
        var answers = new System.Collections.Concurrent.ConcurrentQueue<WorldOpcode>();
        world.Session(bot.Id).ManagedDispatchObserver = opcode =>
        {
            if (opcode is WorldOpcode.CmsgGroupAccept or WorldOpcode.CmsgGroupDecline) answers.Enqueue(opcode);
        };
        await player.SendAsync(WorldOpcode.CmsgGroupInvite, CString(bot.Name));
        bool answered = await world.RunUntilPacedAsync(() => !answers.IsEmpty, 15_000); // the invite is read from the socket in real time
        Assert.True(answered, "the bot never answered the invitation" + Environment.NewLine + await world.TraceAsync());
        await world.RunAsync(500);
        ObjectGuid botGuid = await world.OnWorldAsync(() => world.Player(bot.Id).Guid);
        ObjectGuid playerGuid = await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer(playerName)!.Guid);
        bool accepted = answers.Contains(WorldOpcode.CmsgGroupAccept);
        bool grouped = await world.OnWorldAsync(() => world.GroupManager.GetGroup(botGuid) is { } group && group.IsMember(playerGuid));
        Assert.Equal(accepted, grouped);
        return accepted;
    }

    [Fact]
    public async Task AStrangerFarFromTheEntrance_OnTheSameContinent_IsDeclined()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], Options, dungeon: true);
        var bot = await WaitingBotAsync(world);
        await using WorldTestClient far = await PlayerAsync(world, "Farstranger", EntranceArea + new Vector3(0, 600f, 0));

        Assert.False(await InviteAcceptedAsync(world, far, "Farstranger", bot), "a stranger 600 yards from the entrance is not on the dungeon goal");
    }

    [Fact]
    public async Task AStrangerAtTheEntrance_InLevelRange_IsAccepted()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], Options, dungeon: true);
        var bot = await WaitingBotAsync(world);
        await using WorldTestClient near = await PlayerAsync(world, "Nearstranger", EntranceArea + new Vector3(0, 40f, 0));

        Assert.True(await InviteAcceptedAsync(world, near, "Nearstranger", bot), await world.TraceAsync());
    }

    [Fact]
    public async Task AStrangerAtTheEntrance_ButOutOfLevelRange_IsDeclined()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], Options, dungeon: true);
        var bot = await WaitingBotAsync(world);
        await using WorldTestClient low = await PlayerAsync(world, "Lowstranger", EntranceArea + new Vector3(0, 40f, 0));
        await world.OnWorldAsync(() =>
        {
            Player player = world.World.Host.World.FindOnlinePlayer("Lowstranger")!;
            player.Level = 1;
            return true;
        });

        Assert.False(await InviteAcceptedAsync(world, low, "Lowstranger", bot));
    }

    [Fact]
    public async Task AFarPlayerTheBotLikes_IsAccepted_ByAffinity()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], Options, dungeon: true);
        var bot = await WaitingBotAsync(world);
        await using WorldTestClient friend = await PlayerAsync(world, "Farfriend", EntranceArea + new Vector3(0, 600f, 0));
        await RememberAsync(world, bot, "Farfriend", 10);

        Assert.True(await InviteAcceptedAsync(world, friend, "Farfriend", bot), await world.TraceAsync());
    }

    [Fact]
    public async Task ARivalFlag_IsDeclined_EvenAtTheEntranceOnTheSameGoal()
    {
        // The Rival flag follows the memory's RivalAffinity line, so the line is raised to -20: affinity -30 is a rival to IsRival
        // and still above the recruiting refusal line (-40), and a stranger would be accepted at the entrance.
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], options =>
        {
            Options(options);
            options.Social.Memory.RivalAffinity = -20;
        }, dungeon: true);
        var bot = await WaitingBotAsync(world);
        await using WorldTestClient rival = await PlayerAsync(world, "Gaterival", EntranceArea + new Vector3(0, 40f, 0));
        await RememberAsync(world, bot, "Gaterival", -30);
        Assert.True(await world.OnWorldAsync(() =>
        {
            Player them = world.World.Host.World.FindOnlinePlayer("Gaterival")!;
            return world.Bots.Social.Memory.TryGet(bot.Id, them.Guid.Value, out PlayerbotRelation relation) && world.Bots.Social.Memory.IsRival(relation);
        }), "the relation reads as a rival");

        Assert.False(await InviteAcceptedAsync(world, rival, "Gaterival", bot));
    }
}
