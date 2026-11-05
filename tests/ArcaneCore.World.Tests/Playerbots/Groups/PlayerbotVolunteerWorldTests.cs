using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// A real player's whispered "inv" to a bot that waits for a group (docs/areas/playbots.md, Bot voice), end to end on the real world handlers
/// and the manual clock: the poster or any bot of its waiting group notes the volunteer, the leader invites through the ordinary
/// CMSG_GROUP_INVITE, and a player the group cannot take is told why (role, distance) or, for an ignore, not answered at all. The group
/// asked for is three: two bots and the player make it (Ready, the group forms at once), one bot and the player are one short (Noted).
/// </summary>
public sealed class PlayerbotVolunteerWorldTests
{
    private const byte Human = 1, Warrior = 1, Priest = 5, Mage = 8;
    private const uint LesserHeal = 2050, Fireball = 133, Smite = 585;
    private const string PlayerName = "Volunteerone";

    private static readonly (PlayerbotGroupGoalKind, uint) OgreKey = (PlayerbotGroupGoalKind.Quest, OgreEntry);

    /// <summary>Bots crawl and found no guilds; the chat service runs (the voice answers through it).</summary>
    private static void Still(PlayerbotOptions options)
    {
        options.MoveSpeed = 0.1f;
        options.Social.Guilds.Enabled = false;
        options.Groups.InvitePlayers = false;             // only a volunteer can be invited
        options.Chat.Enabled = true;
        options.Chat.PerPlayerCooldownSeconds = 0;
        options.Chat.Voice.LookingForGroup = false;       // the bots post no calls of their own over the one the test puts on the board
    }

    /// <summary>
    /// A waiting pair (the warrior Voltank, and a priest Volheal or a mage Volmage when <paramref name="healer"/> is false; the warrior alone when
    /// <paramref name="pair"/> is false) that posted an LFG call for the elite quest, and a level 1 warrior player at the start.
    /// </summary>
    private static async Task<(GroupTestWorld World, (Guid Id, string Name) Tank, (Guid Id, string Name) Other, WorldTestClient Player, PlayerbotChat Chat, PlayerbotSocialFeature Feature)>
        StartAsync(bool healer, Action<PlayerbotOptions>? configure = null, bool pair = true)
    {
        GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Still(options);
            configure?.Invoke(options);
        });
        var tank = await world.AddBotAsync("Voltank", Human, Warrior, 10, [Taunt]);
        (Guid Id, string Name) other = default;
        if (pair)
            other = healer
                ? await world.AddBotAsync("Volheal", Human, Priest, 10, [LesserHeal, Smite])
                : await world.AddBotAsync("Volmage", Human, Mage, 10, [Fireball]);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Count() == (pair ? 2 : 1)), await world.TraceAsync());
        await world.World.PrepareCharacterAsync("VOLUNTEERONE", PlayerName, AccountSecurity.Player);
        WorldTestClient player = await world.World.EnterWorldAsync("VOLUNTEERONE", PlayerName, AccountSecurity.Player);
        PlayerbotChat chat = world.Bots.Chat!;
        Assert.True(await world.RunUntilAsync(10_000, () => chat.Responder is not null), await world.TraceAsync());
        await world.OnWorldAsync(() =>
        {
            chat.Voice.Post(tank.Id, new PlayerbotVoicePost(PlayerbotVoicePostKind.LookingForGroup, chat.NowMs, "the ogre", MinLevel: 1));
            return true;
        });
        return (world, tank, other, player, chat, world.World.Services.GetRequiredService<PlayerbotSocialFeature>());
    }

    private static Task SetLevelAsync(GroupTestWorld world, byte level) => world.OnWorldAsync(() =>
    {
        world.World.Host.World.FindOnlinePlayer(PlayerName)!.Level = level;
        return true;
    });

    private static Task<bool> WhisperAsync(GroupTestWorld world, PlayerbotChat chat, Guid botId, string text) => world.OnWorldAsync(() =>
    {
        Player bot = world.Player(botId), from = world.World.Host.World.FindOnlinePlayer(PlayerName)!;
        return chat.Hear(new BotHeardLine(botId, bot.Guid, bot.Name, ChatType.Whisper, null, from.Guid, from.Name,
            SenderIsBot: false, text, AddressesBot: true, FirstHearing: true));
    });

    private static Task<bool> IsVolunteerAsync(GroupTestWorld world) => world.OnWorldAsync(
        () => world.Coordinator.IsVolunteer(world.World.Host.World.FindOnlinePlayer(PlayerName)!.Guid, OgreKey));

    private static bool PlayerInvited(GroupTestWorld world) => world.Coordinator.Groups.Any(g => g.Members.Any(m => m.Real && m.Invited));

    [Fact]
    public async Task AnInv_WhisperedToThePoster_FormsTheGroup_AndTheLeaderInvitesThePlayer()
    {
        var (world, tank, _, player, chat, _) = await StartAsync(healer: true);
        await using (world)
        await using (player)
        {
            await SetLevelAsync(world, 9); // below the bot tank, so a bot leads
            await WhisperAsync(world, chat, tank.Id, "inv");

            Assert.True(await world.RunUntilAsync(60_000, () => PlayerInvited(world)), await world.TraceAsync());
            byte[] invite = await player.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
            Assert.Equal(tank.Name, new PacketReader(invite).ReadCString());
        }
    }

    [Fact]
    public async Task AnInv_WhisperedToANonPosterMemberOfTheWaitingGroup_IsTakenForTheGroup()
    {
        var (world, _, other, player, chat, _) = await StartAsync(healer: true);
        await using (world)
        await using (player)
        {
            await SetLevelAsync(world, 9);
            await WhisperAsync(world, chat, other.Id, "inv pls");
            Assert.True(await world.RunUntilAsync(60_000, () => PlayerInvited(world)), await world.TraceAsync());
        }
    }

    [Fact]
    public async Task AnInv_ToANonPosterMember_WithTheGroupTalkOff_IsNotTakenForTheGroup()
    {
        var (world, _, other, player, chat, _) = await StartAsync(healer: true, o => o.Chat.Lfg.Enabled = false);
        await using (world)
        await using (player)
        {
            await SetLevelAsync(world, 9);
            Assert.False(await WhisperAsync(world, chat, other.Id, "inv pls")); // no post of its own and the group talk off: not for this voice
            await world.RunAsync(20_000);
            Assert.False(PlayerInvited(world));
        }
    }

    [Fact]
    public async Task AWarriorWhoCanHeal_ForAHealerOnlyPlace_GetsTheRoleDecline_AndIsNotPlaced()
    {
        var (world, tank, _, player, chat, feature) = await StartAsync(healer: false, o => o.Groups.MinHealer = 1);
        await using (world)
        await using (player)
        {
            await SetLevelAsync(world, 9);
            long before = await world.OnWorldAsync(() => feature.Voice.Asked);
            await WhisperAsync(world, chat, tank.Id, "i can heal"); // a warrior cannot: the stated role is dropped and the class decides
            Assert.Equal(1, await world.OnWorldAsync(() => feature.Voice.Asked) - before);
            Assert.False(await IsVolunteerAsync(world));
            await world.RunAsync(20_000);
            Assert.False(PlayerInvited(world));
        }
    }

    [Fact]
    public async Task APlayerFarFromTheMeetingPoint_IsToldTheyCannotBePlaced_AndInvitedOnceNear()
    {
        var (world, tank, _, player, chat, feature) = await StartAsync(healer: true);
        await using (world)
        await using (player)
        {
            await SetLevelAsync(world, 9);
            await world.OnWorldAsync(() =>
            {
                Player me = world.World.Host.World.FindOnlinePlayer(PlayerName)!;
                me.Relocate(me.X, me.Y - 3_000f, me.Z, 0, world.World.Host.World.NowMs);
                return true;
            });
            long before = await world.OnWorldAsync(() => feature.Voice.Asked);
            await WhisperAsync(world, chat, tank.Id, "inv");
            Assert.Equal(1, await world.OnWorldAsync(() => feature.Voice.Asked) - before);
            Assert.False(await IsVolunteerAsync(world)); // nothing is kept for a player the coordinator cannot place
            await world.RunAsync(10_000);
            Assert.False(PlayerInvited(world));

            await world.OnWorldAsync(() =>
            {
                Player me = world.World.Host.World.FindOnlinePlayer(PlayerName)!;
                me.Relocate(StartX, StartY, StartZ, 0, world.World.Host.World.NowMs);
                return true;
            });
            await WhisperAsync(world, chat, tank.Id, "inv");
            Assert.True(await world.RunUntilAsync(60_000, () => PlayerInvited(world)), await world.TraceAsync());
        }
    }

    [Fact]
    public async Task APlayerWhoIgnoresThePoster_GetsNoInvitationAndNoReply()
    {
        var (world, tank, _, player, chat, feature) = await StartAsync(healer: true);
        await using (world)
        await using (player)
        {
            await SetLevelAsync(world, 9);
            await world.OnWorldAsync(() =>
            {
                Player me = world.World.Host.World.FindOnlinePlayer(PlayerName)!;
                world.World.Services.GetRequiredService<SocialFeature>().Context.Friends.AddIgnore(me, tank.Name);
                return true;
            });
            long before = await world.OnWorldAsync(() => feature.Voice.Asked);
            Assert.True(await WhisperAsync(world, chat, tank.Id, "inv")); // consumed: no other reply either
            Assert.False(await IsVolunteerAsync(world));
            Assert.Equal(before, await world.OnWorldAsync(() => feature.Voice.Asked));
            await world.RunAsync(20_000);
            Assert.False(PlayerInvited(world));
        }
    }

    [Fact]
    public async Task ARepeatedInvWithinTheRepeatWindow_IsAppliedAgain_ButAnsweredOnce()
    {
        var (world, tank, _, player, chat, feature) = await StartAsync(healer: true, pair: false); // one bot and the player are one short: Noted
        await using (world)
        await using (player)
        {
            await SetLevelAsync(world, 9);
            long before = await world.OnWorldAsync(() => feature.Voice.Asked);
            await WhisperAsync(world, chat, tank.Id, "inv");
            await WhisperAsync(world, chat, tank.Id, "inv");
            Assert.Equal(1, await world.OnWorldAsync(() => feature.Voice.Asked) - before);
            Assert.True(await IsVolunteerAsync(world));
        }
    }
}
