using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Social;
using ArcaneCore.World.Tests.Playerbots.Party;
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
    public async Task AWarriorWhoSaysHeal_ForAHealerOnlyPlace_GetsTheRoleDecline_AndIsNotPlaced()
    {
        var (world, tank, _, player, chat, feature) = await StartAsync(healer: false, o => o.Groups.MinHealer = 1);
        await using (world)
        await using (player)
        {
            await SetLevelAsync(world, 9);
            // The coordinator itself: a warrior cannot heal, so the stated role is dropped and the healer-only place is refused as Role.
            PlayerbotVolunteerResult result = await world.OnWorldAsync(() =>
                world.Coordinator.Volunteer(tank.Id, world.World.Host.World.FindOnlinePlayer(PlayerName)!, PlayerbotGroupRole.Healer));
            Assert.Equal(PlayerbotVolunteerOutcome.Role, result.Outcome);
            Assert.Equal(PlayerbotGroupRole.Healer, result.Needed);

            long before = await world.OnWorldAsync(() => feature.Voice.Asked);
            await WhisperAsync(world, chat, tank.Id, "i can heal");
            Assert.Equal(1, await world.OnWorldAsync(() => feature.Voice.Asked) - before); // the Role decline is whispered
            Assert.False(await IsVolunteerAsync(world));
            await world.RunAsync(20_000);
            Assert.False(PlayerInvited(world));
        }
    }

    [Fact]
    public async Task ADualRoleBot_CountedAsTheTank_DoesNotRefuseAWarrior_WhoTanksWhileTheBotHeals()
    {
        // An unspecced paladin has the healer talent role and can also tank, so the role count (tanks first, from the classes that can tank) calls it the tank and a healer missing; only the dry run knows the warrior tanks while the paladin heals.
        GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Still(options);
            options.Groups.MinTank = 1;
            options.Groups.MinHealer = 1;
        });
        await using (world)
        {
            var paladin = await world.AddBotAsync("Volpally", Human, 2, 10, [LesserHeal]);
            await world.AddBotAsync("Volmage", Human, Mage, 10, [Fireball]);
            Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Count() == 2), await world.TraceAsync());
            await world.World.PrepareCharacterAsync("VOLUNTEERONE", PlayerName, AccountSecurity.Player);
            await using WorldTestClient player = await world.World.EnterWorldAsync("VOLUNTEERONE", PlayerName, AccountSecurity.Player);
            await SetLevelAsync(world, 9);

            PlayerbotVolunteerResult result = await world.OnWorldAsync(() =>
                world.Coordinator.Volunteer(paladin.Id, world.World.Host.World.FindOnlinePlayer(PlayerName)!, PlayerbotGroupRole.Tank));
            Assert.Equal(PlayerbotVolunteerOutcome.Ready, result.Outcome);
            Assert.True(await world.RunUntilAsync(60_000, () => PlayerInvited(world)), await world.TraceAsync());
            var real = world.Coordinator.Groups.SelectMany(g => g.Members).Single(m => m.Real);
            var bot = world.Coordinator.Groups.SelectMany(g => g.Members).Single(m => m.Name == "Volpally");
            Assert.Equal(PlayerbotGroupRole.Tank, real.Role);
            Assert.Equal(PlayerbotGroupRole.Healer, bot.Role);
        }
    }

    [Fact]
    public async Task AStatedRole_DecidesWhetherThePlayerFillsATankOnlyPlace()
    {
        // MinTank 2: the warrior bot is one tank and the priest cannot tank, so the group's last place is a tank's.
        var (world, tank, _, player, _, _) = await StartAsync(healer: true, o => o.Groups.MinTank = 2);
        await using (world)
        await using (player)
        {
            await SetLevelAsync(world, 9);
            PlayerbotVolunteerResult damage = await world.OnWorldAsync(() =>
                world.Coordinator.Volunteer(tank.Id, world.World.Host.World.FindOnlinePlayer(PlayerName)!, PlayerbotGroupRole.Damage));
            Assert.Equal(PlayerbotVolunteerOutcome.Role, damage.Outcome); // a warrior who says dps does not take the tank place
            Assert.Equal(PlayerbotGroupRole.Tank, damage.Needed);

            PlayerbotVolunteerResult stated = await world.OnWorldAsync(() =>
                world.Coordinator.Volunteer(tank.Id, world.World.Host.World.FindOnlinePlayer(PlayerName)!, PlayerbotGroupRole.Tank));
            Assert.Equal(PlayerbotVolunteerOutcome.Ready, stated.Outcome);
            Assert.True(await world.RunUntilAsync(60_000, () => PlayerInvited(world)), await world.TraceAsync());
        }
    }

    [Fact]
    public async Task AnInv_AfterTheGroupIsComplete_StaysQuiet_AndKeepsThePost()
    {
        var (world, tank, _, player, chat, feature) = await StartAsync(healer: true);
        await using (world)
        await using (player)
        {
            await SetLevelAsync(world, 9);
            long before = await world.OnWorldAsync(() => feature.Voice.Asked);
            await WhisperAsync(world, chat, tank.Id, "inv"); // Ready: the group forms, the invitation is pending
            Assert.Equal(1, await world.OnWorldAsync(() => feature.Voice.Asked) - before);

            Assert.True(await WhisperAsync(world, chat, tank.Id, "inv")); // the bot waits for nothing now: no "gave up" whisper
            Assert.Equal(1, await world.OnWorldAsync(() => feature.Voice.Asked) - before);
            Assert.True(await world.OnWorldAsync(() => chat.Voice.Find(tank.Id, chat.NowMs, 600_000L) is not null)); // the post is not forgotten
            Assert.True(await world.RunUntilAsync(60_000, () => PlayerInvited(world)), await world.TraceAsync());
        }
    }

    [Fact]
    public async Task ACannotPlaceAnswer_ThenANotedOne_AreBothSaid()
    {
        var (world, tank, _, player, chat, feature) = await StartAsync(healer: true, pair: false);
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
            await WhisperAsync(world, chat, tank.Id, "inv"); // too far: CannotPlace
            Assert.Equal(1, await world.OnWorldAsync(() => feature.Voice.Asked) - before);

            await world.OnWorldAsync(() =>
            {
                Player me = world.World.Host.World.FindOnlinePlayer(PlayerName)!;
                me.Relocate(StartX, StartY, StartZ, 0, world.World.Host.World.NowMs);
                return true;
            });
            await WhisperAsync(world, chat, tank.Id, "inv"); // near now: Noted, a different outcome, said again
            Assert.Equal(2, await world.OnWorldAsync(() => feature.Voice.Asked) - before);
            Assert.True(await IsVolunteerAsync(world));
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

    // --- reply timing, the stated role later, the rival branch and the reply texts -------------------------------------------------

    private static Task<PlayerbotVolunteerResult> VolunteerAsync(GroupTestWorld world, Guid botId, PlayerbotGroupRole? role = null)
        => world.OnWorldAsync(() => world.Coordinator.Volunteer(botId, world.World.Host.World.FindOnlinePlayer(PlayerName)!, role));

    /// <summary>The bot remembers the player as a rival (the memory the volunteer and match steps consult).</summary>
    private static async Task RememberRivalAsync(GroupTestWorld world, (Guid Id, string Name) bot)
        => Assert.True(await world.OnWorldAsync(() =>
        {
            Player self = world.Player(bot.Id), them = world.World.Host.World.FindOnlinePlayer(PlayerName)!;
            world.Bots.Social.Memory.Load([new PlayerbotRelation(bot.Id, (int)self.Guid.Low, them.Guid.Value, true, -80, 6, 500, PlayerbotRelationFlags.Rival, world.Bots.Social.Memory.NowMs, "", "")]);
            return world.Bots.Social.Memory.TryGet(bot.Id, them.Guid.Value, out PlayerbotRelation relation) && world.Bots.Social.Memory.IsRival(relation);
        }));

    /// <summary>The player whispers "inv" to the bot and its whisper back is returned (null when none came within 20 s of game time).</summary>
    private static async Task<string?> WhisperedReplyAsync(GroupTestWorld world, WorldTestClient player, (Guid Id, string Name) bot)
    {
        ulong botGuid = (await world.OnWorldAsync(() => world.Player(bot.Id).Guid)).Value;
        await player.SendChatAsync(ChatType.Whisper, Language.Common, "inv me pls", bot.Name);
        Task<ChatMessage> read = PartyTestHost.ReadWhisperFromAsync(player, botGuid);
        await world.RunUntilPacedAsync(() => read.IsCompleted, 20_000);
        return read.IsCompleted ? (await read).Text : null;
    }

    /// <summary>One bot waits for a group of three that needs two tanks; the player notes <paramref name="stated"/>; a priest then arrives, which completes the group when the player is placed as the second tank.</summary>
    private static async Task<bool> NotedThenCompletedByAPriestAsync(PlayerbotGroupRole stated)
    {
        var (world, tank, _, player, _, _) = await StartAsync(healer: true, o => o.Groups.MinTank = 2, pair: false);
        await using (world)
        await using (player)
        {
            await SetLevelAsync(world, 9);
            PlayerbotVolunteerResult noted = await VolunteerAsync(world, tank.Id, stated);
            Assert.Equal(PlayerbotVolunteerOutcome.Noted, noted.Outcome);
            Assert.Equal(1, noted.Missing);
            Assert.True(await IsVolunteerAsync(world));

            await world.AddBotAsync("Volheal", Human, Priest, 10, [LesserHeal, Smite]);
            return await world.RunUntilAsync(60_000, () => PlayerInvited(world));
        }
    }

    [Fact]
    public async Task ANotedVolunteer_WhoStatedTank_IsPlacedAsTheSecondTank_WhenTheGroupCompletes()
        => Assert.True(await NotedThenCompletedByAPriestAsync(PlayerbotGroupRole.Tank));

    [Fact]
    public async Task ANotedVolunteer_WhoStatedDamage_IsNeverPlacedInTheTankPlace_WhenTheGroupCompletes()
        => Assert.False(await NotedThenCompletedByAPriestAsync(PlayerbotGroupRole.Damage));

    [Fact]
    public async Task ARoleRefusal_IsGivenOnlyWhenTheRoleIsTheTrueReason()
    {
        // The healer place is open and a warrior cannot fill it, but the player is also two levels under a window of one level: a priest of that level
        // would not be placed either, so the role is not the reason and the general answer is given.
        var (world, tank, _, player, _, _) = await StartAsync(healer: false, o => { o.Groups.MinHealer = 1; o.Groups.LevelRange = 1; });
        await using (world)
        await using (player)
        {
            await SetLevelAsync(world, 8);
            PlayerbotVolunteerResult result = await VolunteerAsync(world, tank.Id, PlayerbotGroupRole.Healer);
            Assert.Equal(PlayerbotVolunteerOutcome.CannotPlace, result.Outcome);
            Assert.Null(result.Needed);
        }
    }

    [Fact]
    public async Task APlayerWhoseBotHoldsThemAsARival_GetsNoInvitationAndNoReply()
    {
        var (world, tank, _, player, chat, feature) = await StartAsync(healer: true);
        await using (world)
        await using (player)
        {
            await SetLevelAsync(world, 9);
            await RememberRivalAsync(world, tank);
            long before = await world.OnWorldAsync(() => feature.Voice.Asked);
            Assert.True(await WhisperAsync(world, chat, tank.Id, "inv")); // consumed: no other reply either
            Assert.False(await IsVolunteerAsync(world));
            Assert.Equal(before, await world.OnWorldAsync(() => feature.Voice.Asked));
            await world.RunAsync(20_000);
            Assert.False(PlayerInvited(world));
        }
    }

    [Fact]
    public async Task ANotedVolunteer_WhoBecomesARivalOfAPlannedBot_IsNotInvitedWhenTheGroupCompletes()
    {
        var (world, tank, _, player, _, _) = await StartAsync(healer: true, pair: false);
        await using (world)
        await using (player)
        {
            await SetLevelAsync(world, 9);
            PlayerbotVolunteerResult noted = await VolunteerAsync(world, tank.Id);
            Assert.Equal(PlayerbotVolunteerOutcome.Noted, noted.Outcome);

            await RememberRivalAsync(world, tank); // after the offer was checked; the best tank (Voltank, the leader) now holds the player as a rival
            await world.AddBotAsync("Volheal", Human, Priest, 10, [LesserHeal, Smite]);
            Assert.True(await world.RunUntilAsync(40_000, () => world.Coordinator.WaitingBots.Count() == 2), await world.TraceAsync()); // positive control: the priest began waiting, so the group could have formed
            await world.RunAsync(40_000);
            Assert.False(PlayerInvited(world));
            Assert.False(await IsVolunteerAsync(world)); // the entry was dropped silently, not kept to expire into a whisper from the rival
        }
    }

    [Fact]
    public async Task ADeclinedPlayer_IsToldTheyTurnedTheInvitationDown()
    {
        var (world, tank, _, player, _, _) = await StartAsync(healer: true);
        await using (world)
        await using (player)
        {
            await SetLevelAsync(world, 9);
            PlayerbotVolunteerResult result = await world.OnWorldAsync(() =>
            {
                Player me = world.World.Host.World.FindOnlinePlayer(PlayerName)!;
                world.Coordinator.NoteDeclined(me.Guid);
                return world.Coordinator.Volunteer(tank.Id, me);
            });
            Assert.Equal(PlayerbotVolunteerOutcome.Declined, result.Outcome);

            string? said = await WhisperedReplyAsync(world, player, tank);
            Assert.NotNull(said);
            Assert.Contains(said, PlayerbotVoiceTemplates.Lines["voice.reply.lfg.declined"]);
            Assert.False(PlayerInvited(world));
        }
    }

    [Fact]
    public async Task ALevelReply_NamesTheLevelTheGoalNeeds()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], options =>
        {
            Still(options);
            options.Groups.MinTank = 1;
            options.Social.Lists.Enabled = false;
            options.Groups.LevelRange = 50;
        }, dungeon: true);
        await world.World.PrepareCharacterAsync("VOLEVEL", PlayerName, AccountSecurity.Player);
        var bot = await world.AddBotAsync("Vollevelbot", Human, Mage, 20, [Fireball], EntranceArea);
        Assert.True(await world.RunUntilAsync(120_000, () => world.HasQuest(bot.Id, DungeonQuest) && world.Coordinator.WaitingBots.Any(w => w.BotId == bot.Id)), await world.TraceAsync());
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLEVEL", PlayerName, AccountSecurity.Player); // level 1, below the dungeon's entrance level
        PlayerbotChat chat = world.Bots.Chat!;
        Assert.True(await world.RunUntilAsync(10_000, () => chat.Responder is not null), await world.TraceAsync());
        await world.OnWorldAsync(() =>
        {
            chat.Voice.Post(bot.Id, new PlayerbotVoicePost(PlayerbotVoicePostKind.LookingForGroup, chat.NowMs, "the dungeon", MinLevel: 1));
            return true;
        });

        PlayerbotVolunteerResult result = await VolunteerAsync(world, bot.Id);
        Assert.Equal(PlayerbotVolunteerOutcome.Level, result.Outcome);
        Assert.True(result.Level > 1);

        string? said = await WhisperedReplyAsync(world, player, bot);
        Assert.NotNull(said);
        string level = result.Level.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(said, PlayerbotVoiceTemplates.Lines["voice.reply.lfg.level"].Select(line => line.Replace("{minlevel}", level, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ARepeatedInv_AfterTheRepeatWindow_IsAnsweredAgain()
    {
        var (world, tank, _, player, chat, feature) = await StartAsync(healer: true, o => o.Chat.Lfg.AnswerRepeatSeconds = 10, pair: false);
        await using (world)
        await using (player)
        {
            await SetLevelAsync(world, 9);
            long before = await world.OnWorldAsync(() => feature.Voice.Asked);
            await WhisperAsync(world, chat, tank.Id, "inv");
            await WhisperAsync(world, chat, tank.Id, "inv");
            Assert.Equal(1, await world.OnWorldAsync(() => feature.Voice.Asked) - before); // inside the window: said once

            await world.RunAsync(11_000);
            await world.OnWorldAsync(() =>
            {
                chat.Voice.Post(tank.Id, new PlayerbotVoicePost(PlayerbotVoicePostKind.LookingForGroup, chat.NowMs, "the ogre", MinLevel: 1)); // the call is still up
                return true;
            });
            await WhisperAsync(world, chat, tank.Id, "inv");
            Assert.Equal(2, await world.OnWorldAsync(() => feature.Voice.Asked) - before); // past it: said again
        }
    }

    [Fact]
    public async Task TheAnswersRemembered_AreBounded_ExpiredOnesGoFirst_AndAFullFreshMemoryIsCleared()
    {
        var (world, tank, _, player, chat, feature) = await StartAsync(healer: true, o => { o.Chat.Lfg.MaxTrackedVolunteers = 8; o.Chat.Lfg.AnswerRepeatSeconds = 10; });
        await using (world)
        await using (player)
        {
            (ObjectGuid, Guid, int, PlayerbotVolunteerOutcome) Key(ObjectGuid asker, int group) => (asker, tank.Id, group, PlayerbotVolunteerOutcome.Noted);
            ObjectGuid me = await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer(PlayerName)!.Guid);

            // Four answers, eleven seconds pass, four more: eight remembered, four of them expired.
            await world.OnWorldAsync(() => { for (int g = 1; g <= 4; g++) feature.Voice.RecordAnswer(chat, Key(me, g)); return true; });
            await world.RunAsync(11_000);
            await world.OnWorldAsync(() => { for (int g = 5; g <= 8; g++) feature.Voice.RecordAnswer(chat, Key(me, g)); return true; });
            Assert.Equal(8, await world.OnWorldAsync(() => feature.Voice.AnswersKept));
            Assert.False(await world.OnWorldAsync(() => feature.Voice.AnsweredLately(chat, Key(me, 1)))); // expired
            Assert.True(await world.OnWorldAsync(() => feature.Voice.AnsweredLately(chat, Key(me, 5))));

            // A ninth makes room by dropping the four expired ones only.
            await world.OnWorldAsync(() => { feature.Voice.RecordAnswer(chat, Key(me, 9)); return true; });
            Assert.Equal(5, await world.OnWorldAsync(() => feature.Voice.AnswersKept));
            Assert.True(await world.OnWorldAsync(() => feature.Voice.AnsweredLately(chat, Key(me, 8))));

            // Fill to eight fresh answers: the next one finds nothing expired and starts the memory over.
            await world.OnWorldAsync(() => { for (int g = 10; g <= 12; g++) feature.Voice.RecordAnswer(chat, Key(me, g)); return true; });
            Assert.Equal(8, await world.OnWorldAsync(() => feature.Voice.AnswersKept));
            await world.OnWorldAsync(() => { feature.Voice.RecordAnswer(chat, Key(me, 13)); return true; });
            Assert.Equal(1, await world.OnWorldAsync(() => feature.Voice.AnswersKept));
        }
    }

    // --- the answer is recorded only once sent; no reply is no record; one Ready for the whole formed group ---------------------------

    [Fact]
    public async Task AnAnswer_TheLimiterRefused_IsGivenOnTheNextInv_EvenInsideTheRepeatWindow()
    {
        var (world, tank, _, player, chat, feature) = await StartAsync(healer: true, o => { o.Chat.Speech.BotLinesPerMinute = 1; o.Chat.Lfg.AnswerRepeatSeconds = 600; }, pair: false);
        await using (world)
        await using (player)
        {
            await SetLevelAsync(world, 9);
            // Use up the bot's one line of the minute, so the answer to the "inv" is refused by the limiter.
            Assert.Equal(BotSpeechAdmission.Queued, await world.OnWorldAsync(() =>
            {
                Player bot = world.Player(tank.Id);
                return chat.Limiter.TryTake(tank.Id, bot.Guid, BotSpeech.WhisperTo("Nobody", ObjectGuid.Empty, string.Empty, "test-filler", BotSpeechPriority.Normal), null, chat.SpeechOptions, chat.NowMs);
            }));
            long before = await world.OnWorldAsync(() => feature.Voice.Asked);
            await WhisperAsync(world, chat, tank.Id, "inv");
            Assert.Equal(before, await world.OnWorldAsync(() => feature.Voice.Asked)); // refused: nothing said
            Assert.Equal(0, await world.OnWorldAsync(() => feature.Voice.AnswersKept));  // and nothing remembered as answered

            await world.RunAsync(61_000); // the minute passes; the 600 s repeat window has not
            await world.OnWorldAsync(() =>
            {
                chat.Voice.Post(tank.Id, new PlayerbotVoicePost(PlayerbotVoicePostKind.LookingForGroup, chat.NowMs, "the ogre", MinLevel: 1));
                return true;
            });
            await WhisperAsync(world, chat, tank.Id, "inv");
            Assert.Equal(before + 1, await world.OnWorldAsync(() => feature.Voice.Asked)); // answered now
            Assert.Equal(1, await world.OnWorldAsync(() => feature.Voice.AnswersKept));
        }
    }

    [Fact]
    public async Task APlayerOfTheOtherFaction_GetsNoReply_AndNothingIsRecorded()
    {
        var (world, tank, _, player, chat, feature) = await StartAsync(healer: true);
        await using (world)
        await using (player)
        {
            await SetLevelAsync(world, 9);
            await world.OnWorldAsync(() =>
            {
                world.World.Host.World.FindOnlinePlayer(PlayerName)!.SetByte(UpdateFields.UnitFieldBytes0, 0, (byte)Race.Orc); // Horde against the Alliance bots
                return true;
            });
            long before = await world.OnWorldAsync(() => feature.Voice.Asked);
            await WhisperAsync(world, chat, tank.Id, "inv");
            Assert.Equal(before, await world.OnWorldAsync(() => feature.Voice.Asked));
            Assert.Equal(0, await world.OnWorldAsync(() => feature.Voice.AnswersKept));
            Assert.False(await IsVolunteerAsync(world));
        }
    }

    [Fact]
    public async Task AReadyThroughOneMember_ThenAnInvToTheOtherBotWithAPost_IsQuiet_AndThePostStays()
    {
        var (world, tank, other, player, chat, feature) = await StartAsync(healer: true);
        await using (world)
        await using (player)
        {
            await SetLevelAsync(world, 9);
            long before = await world.OnWorldAsync(() => feature.Voice.Asked);
            await WhisperAsync(world, chat, other.Id, "inv pls"); // the member without a post: Ready, the group forms and the invitation is pending
            Assert.Equal(before + 1, await world.OnWorldAsync(() => feature.Voice.Asked));

            await WhisperAsync(world, chat, tank.Id, "inv"); // the poster, asked next
            Assert.Equal(before + 1, await world.OnWorldAsync(() => feature.Voice.Asked)); // quiet: the formed group already answered
            Assert.NotNull(await world.OnWorldAsync(() => chat.Voice.Find(tank.Id, chat.NowMs, 600_000L))); // the post was kept
        }
    }
}
