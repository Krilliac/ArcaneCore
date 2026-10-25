using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Bot groups and real players (<see cref="PlayerbotGroupCoordinator"/>): a real player's party keeps its party bot (vmangos
/// PartyBotAI master behaviour, unchanged), a short group may invite a nearby player when allowed, the GM commands report the groups,
/// and a bot left inside an instance without a group walks out.
/// </summary>
public sealed class PlayerbotGroupPlayerTests
{
    private const byte Human = 1, Warrior = 1, Priest = 5, Mage = 8;
    private const uint LesserHeal = 2050, Fireball = 133, Smite = 585;

    /// <summary>The player holds <paramref name="quest"/> unfinished (it is on the bots' goal) and, when given, the bot remembers them with that affinity and those flags.</summary>
    private static async Task GivePlayerQuestAsync(GroupTestWorld world, string playerName, uint quest)
        => await world.OnWorldAsync(() =>
        {
            Player player = world.World.Host.World.FindOnlinePlayer(playerName)!;
            PlayerNpcState state = world.World.Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(player)!;
            state.Quests.GetOrAdd(quest).Status = QuestStatus.Incomplete;
            return true;
        });

    private static async Task RememberPlayerAsync(GroupTestWorld world, (Guid Id, string Name) bot, string playerName, short affinity, PlayerbotRelationFlags flags = PlayerbotRelationFlags.None)
        => await world.OnWorldAsync(() =>
        {
            Player self = world.Player(bot.Id), them = world.World.Host.World.FindOnlinePlayer(playerName)!;
            world.Bots.Social.Memory.Load([new PlayerbotRelation(bot.Id, (int)self.Guid.Low, them.Guid.Value, true, affinity, 6, 500, flags, world.Bots.Social.Memory.NowMs, "", "")]);
            return true;
        });

    /// <summary>Advances up to <paramref name="maxMs"/> in short slices, putting the bot back on the player before each, and returns whether the player was invited.</summary>
    private static async Task<bool> RunBesidePlayerUntilAsync(GroupTestWorld world, Guid botId, string playerName, uint maxMs)
    {
        const uint SliceMs = 1_000;
        for (uint elapsed = 0; elapsed < maxMs; elapsed += SliceMs)
        {
            await world.OnWorldAsync(() =>
            {
                Player them = world.World.Host.World.FindOnlinePlayer(playerName)!;
                world.Player(botId).Relocate(them.X + 2, them.Y, them.Z, 0, world.World.Host.World.NowMs);
                return true;
            });
            if (await world.RunUntilAsync(SliceMs, () => PlayerInvited(world))) return true;
        }

        return PlayerInvited(world);
    }

    private static bool PlayerInvited(GroupTestWorld world) => world.Coordinator.Groups.Any(g => g.Members.Any(m => m.Real && m.Invited));

    [Fact]
    public async Task ARealPlayersPartyBot_StaysItsMastersBot_AndTheCoordinatorLeavesItAlone()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options => options.Party.Allowlist = ["Realmaster"]);
        var bot = await world.AddBotAsync("Partyone", Human, Mage, 10, [Fireball]);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Any(w => w.BotId == bot.Id)), await world.TraceAsync());
        await using WorldTestClient master = await world.World.EnterWorldAsync("REALMASTER", "Realmaster", AccountSecurity.Player);

        PlayerbotOperationResult invited = await world.OnWorldAsync(() => world.Bots.InviteToGroup(world.World.Host.World.FindOnlinePlayer("Realmaster")!, bot.Name));
        Assert.True(invited.Success, invited.Code);

        // The party AI drives it (follows its master); the coordinator stops waiting for it and never takes it into a bot group.
        Assert.True(await world.RunUntilAsync(5_000, () => world.Bots.IsPartyDriven(bot.Id)), await world.TraceAsync());
        await world.RunAsync(10_000);
        Assert.True(world.Bots.IsPartyDriven(bot.Id));
        Assert.False(world.Coordinator.Drives(bot.Id));
        Assert.DoesNotContain(world.Coordinator.WaitingBots, w => w.BotId == bot.Id);
        Assert.Empty(world.Coordinator.Groups);
        Assert.Empty(await world.OnWorldAsync(() => world.Bots.FindBrain(bot.Id)?.GroupHeld.ToArray() ?? []));
        Assert.Null((await world.OnWorldAsync(() => world.Bots.Snapshot().Single(s => s.BotId == bot.Id))).Group);
    }

    [Fact]
    public async Task WithInvitePlayers_AGroupOneShort_InvitesANearbyRealPlayer_ThroughTheOrdinaryInvitation()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => options.Groups.InvitePlayers = true);
        await using WorldTestClient player = await world.World.EnterWorldAsync("GROUPHELPER", "Grouphelper", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Askerone", Human, Warrior, 1, []);
        await GivePlayerQuestAsync(world, "Grouphelper", DuoQuest);
        await RememberPlayerAsync(world, bot, "Grouphelper", 30);

        Assert.True(await world.RunUntilAsync(60_000, () => PlayerInvited(world)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(bot.Id, group.LeaderBotId); // a real player never leads a bot group
        byte[] invite = await player.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        Assert.Equal(bot.Name, new PacketReader(invite).ReadCString());

        // The player declines: the group is given up and that player is not asked again for a while.
        await player.SendAsync(WorldOpcode.CmsgGroupDecline, []);
        Assert.True(await world.RunUntilPacedAsync(() => world.Coordinator.Groups.Count == 0, 10_000), await world.TraceAsync()); // the packet is read from the socket in real time
        Assert.Contains(world.Coordinator.Events, e => e.Contains("Grouphelper did not join", StringComparison.Ordinal));
        await world.RunAsync(10_000);
        Assert.Empty(world.Coordinator.Groups);
    }

    /// <summary>One bot and one real player are a group: the player counts toward the two members, so the coordinator keeps it and goes on to work the objective.</summary>
    [Fact]
    public async Task OneBotAndOneRealPlayer_AreAGroup_TheCoordinatorKeeps_AndWorksTheObjectiveWith()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => options.Groups.InvitePlayers = true);
        await using WorldTestClient player = await world.World.EnterWorldAsync("GROUPPAIR", "Grouppair", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Askerpair", Human, Warrior, 1, []);
        await GivePlayerQuestAsync(world, "Grouppair", DuoQuest);
        await RememberPlayerAsync(world, bot, "Grouppair", 30);
        Assert.True(await world.RunUntilAsync(60_000, () => PlayerInvited(world)), await world.TraceAsync());
        await player.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);

        await AcceptAsync(player);
        Assert.True(await world.RunUntilAsync(10_000, () => world.Coordinator.Groups.Any(g => g.Members.Any(m => m.Real && m.Joined))), await world.TraceAsync());

        // Several coordinator passes later (each 2 s) the pair is still one group, and it has moved on from forming.
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.Groups.Any(g => g.State is PlayerbotGroupState.Travelling or PlayerbotGroupState.Engaging)), await world.TraceAsync());
        await world.RunAsync(20_000);
        PlayerbotGroupCoordinator.BotGroup group = Assert.Single(world.Coordinator.Groups);
        Assert.Equal(2, group.Members.Count);
        Assert.Contains(group.Members, m => m.Real && m.Joined);
        Assert.DoesNotContain(world.Coordinator.Events, e => e.Contains("too-few", StringComparison.Ordinal));
    }

    /// <summary>
    /// Accepts the invitation and waits for the server's answer (the player's group list), which is read from the socket in real time: the
    /// manual clock does not wait for a packet still on its way, so advancing it right after the send can use up the budget before the
    /// server has even read the accept.
    /// </summary>
    private static async Task AcceptAsync(WorldTestClient player)
    {
        await player.SendAsync(WorldOpcode.CmsgGroupAccept, []);
        await player.ReadUntilAsync(WorldOpcode.SmsgGroupList);
    }

    /// <summary>A one-bot, one-player group that has moved on from forming (the player accepted the bot's invitation).</summary>
    private static async Task<WorldTestClient> FormPairAsync(GroupTestWorld world, string accountName, string playerName, string botName)
    {
        WorldTestClient player = await world.World.EnterWorldAsync(accountName, playerName, AccountSecurity.Player);
        var bot = await world.AddBotAsync(botName, Human, Warrior, 1, []);
        await GivePlayerQuestAsync(world, playerName, DuoQuest);
        await RememberPlayerAsync(world, bot, playerName, 30);
        Assert.True(await world.RunUntilAsync(60_000, () => PlayerInvited(world)), await world.TraceAsync());
        await player.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        await AcceptAsync(player);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.Groups.Any(g => g.State is PlayerbotGroupState.Travelling or PlayerbotGroupState.Engaging)), await world.TraceAsync());
        return player;
    }

    /// <summary>
    /// Logs the player out on the world thread, as a completed logout does (the same <c>LogoutPlayer</c> an instant logout calls). A logout
    /// request is not used: it is refused in combat (the pair is fighting), and otherwise runs a 20 s countdown that starts only when the
    /// packet is read from the socket, which the virtual clock does not wait for.
    /// </summary>
    private static async Task LogOutAsync(GroupTestWorld world, string name)
    {
        await world.OnWorldAsync(() =>
        {
            world.World.Host.World.LogoutPlayer(world.World.Host.World.FindOnlinePlayer(name)!);
            return true;
        });
        Assert.Null(await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer(name)));
    }

    /// <summary>The server keeps a logged-out player in its group, so the pair is still "two members"; a player who is not online does not count toward the two.</summary>
    [Fact]
    public async Task APairWhosePlayerLogsOut_Ends_BecauseAnOfflinePlayerDoesNotCountTowardTheTwo()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => options.Groups.InvitePlayers = true);
        await using WorldTestClient player = await FormPairAsync(world, "GROUPGONE", "Groupgone", "Askergone");

        await LogOutAsync(world, "Groupgone");

        Assert.True(await world.RunUntilAsync(15_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.Contains(world.Coordinator.Events, e => e.Contains("player-gone", StringComparison.Ordinal));
    }

    /// <summary>A run that already succeeded is not failed because the player logs out while the bots walk out: it ends as completed.</summary>
    [Fact]
    public async Task APairWhosePlayerLogsOutWhileLeaving_AfterASuccess_EndsAsCompleted_NotPlayerGone()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => options.Groups.InvitePlayers = true);
        await using WorldTestClient player = await FormPairAsync(world, "GROUPLEAVING", "Groupleaving", "Askerleaving");
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();

        int held = 0;
        // The group has succeeded and is walking out (held in Leaving, as a bot still inside an instance would hold it) until the logout completes.
        Assert.True(await world.RunUntilAsync(40_000, () =>
        {
            bool online = world.World.Host.World.FindOnlinePlayer("Groupleaving") is not null;
            if (online && world.Coordinator.Groups.Contains(group))
            {
                group.Succeeded = true;
                group.Reason = null;
                group.State = PlayerbotGroupState.Leaving;
                group.StateSinceMs = world.World.Host.World.NowMs;
                if (++held == 3) world.World.Host.World.LogoutPlayer(world.World.Host.World.FindOnlinePlayer("Groupleaving")!);
            }

            return !online;
        }), await world.TraceAsync());

        Assert.True(await world.RunUntilAsync(15_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.True(group.Succeeded);
        Assert.Equal("completed", group.Reason);
        Assert.Equal(0, world.Coordinator.Totals.Failed);
        Assert.DoesNotContain(world.Coordinator.Events, e => e.Contains("player-gone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APairWhosePlayerLeavesTheGroup_Ends()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => options.Groups.InvitePlayers = true);
        await using WorldTestClient player = await FormPairAsync(world, "GROUPLEAVE", "Groupleave", "Askerleave");

        ObjectGuid guid = await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer("Groupleave")!.Guid);

        await player.SendAsync(WorldOpcode.CmsgGroupDisband, []);

        // The server reads the packet from the socket in real time, which the manual clock does not wait for: poll for its handling in real
        // time (the world thread keeps ticking without game time moving), and only then give the coordinator its original game budget.
        System.Diagnostics.Stopwatch wall = System.Diagnostics.Stopwatch.StartNew();
        bool handled = false;
        while (!handled && wall.Elapsed < TimeSpan.FromSeconds(30))
        {
            handled = await world.OnWorldAsync(() => world.GroupManager.GetGroup(guid) is null);
            if (!handled) await Task.Delay(20);
        }

        Assert.True(handled, "the server handled the disband" + Environment.NewLine + await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(15_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.Contains(world.Coordinator.Events, e => e.Contains("Groupleave left", StringComparison.Ordinal));
        Assert.Contains(world.Coordinator.Events, e => e.Contains("too-few", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStranger_OnTheSameGoal_IsInvitedOnlyOnceTheBotsLikeThem()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest]);
        await using WorldTestClient player = await world.World.EnterWorldAsync("GROUPSTRANGE", "Groupstrange", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Askertwo", Human, Warrior, 1, []);
        await GivePlayerQuestAsync(world, "Groupstrange", DuoQuest);

        // The waiting bot grinds and wanders; an invite only reaches a player within InvitePlayerYards, so each phase keeps the bot beside the player
        // (otherwise a "not invited" phase proves nothing and the final phase depends on where the bot happened to roam).
        Assert.False(await RunBesidePlayerUntilAsync(world, bot.Id, "Groupstrange", 30_000), "a stranger with no affinity and no AI chat opt-in is not invited");
        await RememberPlayerAsync(world, bot, "Groupstrange", 19);
        Assert.False(await RunBesidePlayerUntilAsync(world, bot.Id, "Groupstrange", 10_000), "affinity 19 is below InvitePlayerAffinity (20)");
        await RememberPlayerAsync(world, bot, "Groupstrange", 20);
        Assert.True(await RunBesidePlayerUntilAsync(world, bot.Id, "Groupstrange", 30_000), await world.TraceAsync());
    }

    /// <summary>
    /// "invites off" (S4): a liked player on the goal, who would be invited unasked, is not once they opted out; asking ("inv" to the bot's LFG
    /// call) is still consent and gets the ordinary invitation.
    /// </summary>
    [Fact]
    public async Task AnOptedOutStranger_OnTheGoal_IsNeverInvitedUnasked_ButIsInvitedAfterWhisperingInv()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options =>
        {
            options.Chat.PerPlayerCooldownSeconds = 0;
            options.Groups.InvitePlayers = true;
        });
        await using WorldTestClient player = await world.World.EnterWorldAsync("GROUPOPTOUT", "Groupoptout", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Askeroptout", Human, Warrior, 1, []);
        await GivePlayerQuestAsync(world, "Groupoptout", DuoQuest);
        await RememberPlayerAsync(world, bot, "Groupoptout", 30); // liked and on the goal: invited unasked but for the opt-out
        ArcaneCore.World.Playerbots.BotSocial.PlayerbotInviteOptOut store = world.World.Services.GetRequiredService<ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialFeature>().InviteOptOut;
        Player real = await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer("Groupoptout")!);
        (ulong guid, int account) = (real.Guid.Value, real.AccountId);

        await player.SendChatAsync(ChatType.Whisper, Language.Common, "invites off", bot.Name);
        Assert.True(await world.RunUntilPacedAsync(() => store.IsOptedOut(guid, account), 10_000), $"the player opted out{Environment.NewLine}{await world.TraceAsync()}");
        Assert.False(await RunBesidePlayerUntilAsync(world, bot.Id, "Groupoptout", 60_000), "an opted-out player is never invited unasked");

        // Asking is consent: the bot has an LFG call out, the player whispers "inv", and the leader's ordinary invitation follows.
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Any(w => w.BotId == bot.Id)), await world.TraceAsync());
        await world.OnWorldAsync(() =>
        {
            ArcaneCore.World.Playerbots.Chat.PlayerbotChat chat = world.Bots.Chat!;
            chat.Voice.Post(bot.Id, new ArcaneCore.World.Playerbots.Chat.PlayerbotVoicePost(ArcaneCore.World.Playerbots.Chat.PlayerbotVoicePostKind.LookingForGroup, chat.NowMs, "the duo quest", MinLevel: 1));
            return true;
        });
        await player.SendChatAsync(ChatType.Whisper, Language.Common, "inv", bot.Name);
        Assert.True(await world.RunUntilPacedAsync(() =>
        {
            Player them = world.World.Host.World.FindOnlinePlayer("Groupoptout")!;
            world.Player(bot.Id).Relocate(them.X + 2, them.Y, them.Z, 0, world.World.Host.World.NowMs);
            return PlayerInvited(world);
        }, 60_000), $"the volunteer is invited{Environment.NewLine}{world.Bots.Chat!.Status()}{Environment.NewLine}{await world.TraceAsync()}");
        byte[] invite = await player.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        Assert.Equal(bot.Name, new PacketReader(invite).ReadCString());
        Assert.True(store.IsOptedOut(guid, account)); // asking did not undo the choice
    }

    [Fact]
    public async Task APlayerOnAnotherGoal_AndANoWhispersPlayer_AreNeverInvited()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest]);
        await using WorldTestClient elsewhere = await world.World.EnterWorldAsync("GROUPELSE", "Groupelse", AccountSecurity.Player);
        await using WorldTestClient quiet = await world.World.EnterWorldAsync("GROUPQUIET", "Groupquiet", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Askerthree", Human, Warrior, 1, []);
        await GivePlayerQuestAsync(world, "Groupquiet", DuoQuest);
        await RememberPlayerAsync(world, bot, "Groupelse", 90);                                         // liked, but not on the goal
        await RememberPlayerAsync(world, bot, "Groupquiet", 90, PlayerbotRelationFlags.NoWhispers);     // on the goal, liked, but asked for quiet

        await world.RunAsync(60_000);
        Assert.False(PlayerInvited(world), await world.TraceAsync());
    }

    /// <summary>The rival is on the goal and opted in to AI chat, so only the bot's rival flag keeps it out; forgiven, the same player is invited.</summary>
    [Fact]
    public async Task AnOptedInPlayerOnTheGoal_WhoIsARival_IsNeverInvited_UntilTheBotForgives()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest]);
        await using WorldTestClient rival = await world.World.EnterWorldAsync("GROUPRIVAL", "Grouprival", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Askerrival", Human, Warrior, 1, []);
        await GivePlayerQuestAsync(world, "Grouprival", DuoQuest);
        await rival.SendChatAsync(ChatType.Whisper, Language.Common, "ai on", bot.Name);
        Assert.True(await world.RunUntilPacedAsync(() => world.Bots.Chat!.Safety.Player("Grouprival")?.AiReplies == true, 10_000), $"the player opted in to AI chat{Environment.NewLine}{world.Bots.Chat!.Status()}{Environment.NewLine}{await world.TraceAsync()}");

        await RememberPlayerAsync(world, bot, "Grouprival", -80);
        Assert.True(await world.OnWorldAsync(() =>
        {
            Player them = world.World.Host.World.FindOnlinePlayer("Grouprival")!;
            return world.Bots.Social.Memory.TryGet(bot.Id, them.Guid.Value, out PlayerbotRelation relation) && world.Bots.Social.Memory.IsRival(relation);
        }), "the bot holds the player as a rival");
        await world.RunAsync(60_000);
        Assert.False(PlayerInvited(world), await world.TraceAsync());

        await RememberPlayerAsync(world, bot, "Grouprival", 0); // forgiven: the same opted-in player on the goal now qualifies
        Assert.True(await world.RunUntilAsync(60_000, () => PlayerInvited(world)), await world.TraceAsync());
    }

    /// <summary>"On the same quest" is the help-quest invite's reason, not a reason to open a trade: only a friend, a guildmate or a liked player gets one.</summary>
    [Theory]
    [InlineData(0, false)]  // a stranger on the same quest
    [InlineData(15, true)]  // liked: at least AcceptInviteAffinity (10)
    public async Task ATrade_IsOpenedForALikedPlayer_NotForAStrangerOnTheSameQuest(short affinity, bool opened)
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options =>
        {
            options.Chat.PerPlayerCooldownSeconds = 0;
            options.Groups.InvitePlayers = false;
            options.MoveSpeed = 0.1f; // the bot stays within trade reach of the player
        });
        await using WorldTestClient player = await world.World.EnterWorldAsync("TRADEPLAYER", "Tradeplayer", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Traderbot", Human, Warrior, 1, []);
        await GivePlayerQuestAsync(world, "Tradeplayer", DuoQuest);
        if (affinity != 0) await RememberPlayerAsync(world, bot, "Tradeplayer", affinity);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Any(w => w.BotId == bot.Id)), await world.TraceAsync());

        // Away from the creatures (a bot in a fight opens no trade), the player a few yards from the bot.
        await world.OnWorldAsync(() =>
        {
            var far = new System.Numerics.Vector3(StartX - 1000f, StartY, StartZ);
            uint now = world.World.Host.World.NowMs;
            world.Player(bot.Id).Relocate(far.X, far.Y, far.Z, 0, now);
            world.World.Host.World.FindOnlinePlayer("Tradeplayer")!.Relocate(far.X + 3f, far.Y, far.Z, 0, now);
            return true;
        });
        await world.RunAsync(1_000);
        await player.CollectAsync();

        var dispatched = new System.Collections.Concurrent.ConcurrentQueue<WorldOpcode>();
        world.Session(bot.Id).ManagedDispatchObserver = opcode => dispatched.Enqueue(opcode);
        await player.SendChatAsync(ChatType.Whisper, Language.Common, "trade me", bot.Name);
        // The player keeps standing beside the bot, which drifts while it waits (the answer comes after a typing delay).
        void Stick()
        {
            Player self = world.Player(bot.Id), other = world.World.Host.World.FindOnlinePlayer("Tradeplayer")!;
            other.Relocate(self.X + 2f, self.Y, self.Z, 0, world.World.Host.World.NowMs);
        }

        Assert.True(await world.RunUntilPacedAsync(() => { Stick(); return dispatched.Contains(WorldOpcode.CmsgMessagechat); }, 60_000), $"the bot answers the whisper{Environment.NewLine}{world.Bots.Chat!.Status()}{Environment.NewLine}{await world.TraceAsync()}");
        await world.RunUntilAsync(3_000, () => { Stick(); return dispatched.Contains(WorldOpcode.CmsgInitiateTrade); }); // the trade follows the answer
        Assert.Equal(opened, dispatched.Contains(WorldOpcode.CmsgInitiateTrade));
        Assert.Equal(opened, (await player.CollectAsync()).Any(packet => packet.Opcode == WorldOpcode.SmsgTradeStatus));
    }

    /// <summary>A player on the bot's ignore list is not heard: no answer to a whisper (the same whisper from a player not ignored is answered).</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task AWhisperFromAnIgnoredPlayer_GetsNoAnswer(bool ignored, bool answered)
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options =>
        {
            options.Chat.PerPlayerCooldownSeconds = 0;
            options.Groups.InvitePlayers = false;
        });
        await using WorldTestClient player = await world.World.EnterWorldAsync("IGNOREDONE", "Ignoredone", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Ignorerbot", Human, Warrior, 1, []);
        if (ignored)
            await world.OnWorldAsync(() =>
            {
                Social.SocialTestKit.Social(world.World.Host).Context.Friends.Get(world.Player(bot.Id)).Set(world.World.Host.World.FindOnlinePlayer("Ignoredone")!.Guid.Low, ArcaneCore.Kernel.Social.SocialFlags.Ignored, true);
                return true;
            });
        await player.CollectAsync();
        await player.SendChatAsync(ChatType.Whisper, Language.Common, "trade me", bot.Name);
        await world.RunAsync(30_000);
        // The bot's answer reaches the player as a whisper (the player's own whisper comes back as WhisperInform; other bot talk is not a whisper).
        bool replied = (await player.CollectAsync()).Any(packet => packet.Opcode == WorldOpcode.SmsgMessagechat && packet.Payload.Length > 0 && packet.Payload[0] == (byte)ChatType.Whisper);
        Assert.True(answered == replied, "ignoreHas=" + await world.OnWorldAsync(() => Social.SocialTestKit.Social(world.World.Host).Context.Friends.Get(world.Player(bot.Id)).Has(world.World.Host.World.FindOnlinePlayer("Ignoredone")!.Guid.Low, ArcaneCore.Kernel.Social.SocialFlags.Ignored)));
    }

    /// <summary>A friendly emote at a bot moves its memory of the emoter and is answered; from a player on its ignore list it does neither.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true, AccountSecurity.GameMaster)] // a game master is never dropped, as in OnChat
    public async Task AnEmoteFromAnIgnoredPlayer_ChangesNothingAndGetsNoReply(bool ignored, bool noticed, AccountSecurity security = AccountSecurity.Player)
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options =>
        {
            options.Groups.InvitePlayers = false;
            options.Chat.Speech.EmoteReplies = true;
            options.Chat.Speech.EmoteReplyGapSeconds = 1;
        });
        await using WorldTestClient player = await world.World.EnterWorldAsync("EMOTEONE", "Emoteone", security);
        var bot = await world.AddBotAsync("Emotebot", Human, Warrior, 1, []);
        await world.OnWorldAsync(() =>
        {
            Player them = world.World.Host.World.FindOnlinePlayer("Emoteone")!;
            them.Relocate(world.Player(bot.Id).X + 2f, world.Player(bot.Id).Y, world.Player(bot.Id).Z, 0, world.World.Host.World.NowMs);
            return true;
        });
        await world.RunAsync(3_000);
        if (ignored)
            await world.OnWorldAsync(() =>
            {
                Social.SocialTestKit.Social(world.World.Host).Context.Friends.Get(world.Player(bot.Id)).Set(world.World.Host.World.FindOnlinePlayer("Emoteone")!.Guid.Low, ArcaneCore.Kernel.Social.SocialFlags.Ignored, true);
                return true;
            });
        var dispatched = new System.Collections.Concurrent.ConcurrentQueue<WorldOpcode>();
        world.Session(bot.Id).ManagedDispatchObserver = opcode => dispatched.Enqueue(opcode);
        // The server's SMSG_TEXT_EMOTE is handed to the bot's session as it would be by the broadcast: the bot walks off on its errands, and a
        // broadcast from a player left behind would not reach it.
        await world.OnWorldAsync(() =>
        {
            Player them = world.World.Host.World.FindOnlinePlayer("Emoteone")!;
            world.Session(bot.Id).Send(WorldOpcode.SmsgTextEmote, ArcaneCore.World.Packets.ChatPackets.BuildTextEmote(them.Guid, 101 /* WAVE */, 0, bot.Name));
            return true;
        });
        await world.RunAsync(20_000);

        bool remembered = await world.OnWorldAsync(() =>
        {
            Player them = world.World.Host.World.FindOnlinePlayer("Emoteone")!;
            return world.Bots.Social.Memory.TryGet(bot.Id, them.Guid.Value, out PlayerbotRelation relation) && world.Bots.Social.Memory.EffectiveAffinity(relation) != 0;
        });
        Assert.Equal(noticed, remembered);
        Assert.Equal(noticed, dispatched.Contains(WorldOpcode.CmsgTextEmote));
    }

    [Fact]
    public async Task APlayerInvitedOnce_IsNotInvitedAgainWithinTheCooldown()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest]);
        await using WorldTestClient player = await world.World.EnterWorldAsync("GROUPCOOL", "Groupcool", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Askerfour", Human, Warrior, 1, []);
        await GivePlayerQuestAsync(world, "Groupcool", DuoQuest);
        await RememberPlayerAsync(world, bot, "Groupcool", 40);

        Assert.True(await world.RunUntilAsync(60_000, () => PlayerInvited(world)), await world.TraceAsync());
        ObjectGuid guid = await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer("Groupcool")!.Guid);
        Assert.True(await world.OnWorldAsync(() => world.Coordinator.InvitedRecently(guid)));
        await player.SendAsync(WorldOpcode.CmsgGroupDecline, []);
        Assert.True(await world.RunUntilPacedAsync(() => world.Coordinator.Groups.Count == 0, 10_000), await world.TraceAsync());

        // The decline's own hold (ten minutes) ends first; the thirty minute cooldown still keeps the bots from asking again.
        await world.RunAsync(11 * 60_000);
        Assert.True(await world.OnWorldAsync(() => world.Coordinator.InvitedRecently(guid)));
        Assert.False(PlayerInvited(world), await world.TraceAsync());
    }

    [Fact]
    public async Task TheGmCommands_ReportTheGroups_TheirMembers_RolesAndState()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest]);
        await world.AddBotAsync("Cmdtank", Human, Warrior, 10, [Taunt]);
        await world.AddBotAsync("Cmdprie", Human, Priest, 10, [LesserHeal, Smite]);
        await world.AddBotAsync("Cmdmage", Human, Mage, 10, [Fireball]);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State != PlayerbotGroupState.Forming)), await world.TraceAsync());
        await using WorldTestClient gm = await world.World.EnterWorldAsync("GROUPGM", "Groupgm", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".playerbot groups");
        Assert.StartsWith("Bot groups: enabled, 1 running", (await gm.ReadSystemChatAsync()).Text);
        string line = (await gm.ReadSystemChatAsync()).Text;
        Assert.StartsWith("group 1 quest:" + OgreEntry, line);
        Assert.Contains("Cmdtank(tank,leader)", line);
        Assert.Contains("Cmdprie(healer)", line);
        Assert.Contains("Cmdmage(damage)", line);

        // The status line's group part and the inspection's BOTINSPECT group line.
        Assert.StartsWith("group=1:healer:", (await world.OnWorldAsync(() => world.Bots.Snapshot().Single(s => s.Name == "Cmdprie"))).Group);
        Assert.StartsWith("group=1:tank:", (await world.Bots.InspectAsync("Cmdtank"))!.Group);
        Assert.EndsWith(":leader", (await world.Bots.InspectAsync("Cmdtank"))!.Group);
    }

    [Fact]
    public async Task ABotInABotLedGroup_StillAnswersAPlayersWhisper_AndTheGroupFormedThroughItsIntakeStays()
    {
        // Bot chat (on by default) and the coordinator share the party intake: the members' acceptances of the leader's invitations
        // must not be swallowed by the chat, and a grouped bot must still answer a real player (wave 8 merge of bot-chat and bot-groups).
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options => options.Chat.PerPlayerCooldownSeconds = 0);
        var tank = await world.AddBotAsync("Chattank", Human, Warrior, 10, [Taunt]);
        var healer = await world.AddBotAsync("Chatprie", Human, Priest, 10, [LesserHeal, Smite]);
        var mage = await world.AddBotAsync("Chatmage", Human, Mage, 10, [Fireball]);
        Assert.True(world.Options.Chat.Enabled);
        // Forming ends either with every member joined or with the group giving up (an invitation refused or unanswered, or a member not online,
        // for InviteTimeoutMs of game time; it then disbands). The bots' acceptances are game-time driven (PlayerbotPartyAI.Intake runs once per
        // think interval of game time for an ungrouped invitee, and the answer goes through a synchronous managed action), so the manual clock
        // cannot outrun them. A group that gave up would have passed the old wait ("state is not Forming") and failed later on the member count
        // without a reason (hypothesis: the failing run was not reproduced). Wait for the group that completed instead, so a group that gave up
        // fails with the trace and the coordinator's events. Why a 3-bot group would give up is still unknown.
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Any(g => g.State is not (PlayerbotGroupState.Forming or PlayerbotGroupState.Disbanding) && g.Members.All(m => m.Joined))), await world.TraceAsync());
        Assert.Equal(3, await world.OnWorldAsync(() => world.GroupManager.GetGroup(world.Player(tank.Id).Guid)?.MemberCount ?? 0));
        Assert.DoesNotContain(await world.OnWorldAsync(() => world.Coordinator.Events.ToArray()), e => e.Contains("gave up") || e.Contains("did not join"));

        await using WorldTestClient player = await world.World.EnterWorldAsync("CHATGROUPP", "Chatgroupp", AccountSecurity.Player);
        await player.CollectAsync();
        ulong bot = await world.OnWorldAsync(() => world.Player(healer.Id).Guid.Value);
        Assert.True(await world.OnWorldAsync(() => world.Coordinator.Drives(healer.Id)));
        await player.SendChatAsync(ChatType.Whisper, Language.Common, "what level are you?", healer.Name);

        ArcaneCore.World.Playerbots.Chat.PlayerbotChat chat = world.Bots.Chat!;
        // The answer comes from a chat worker thread in real time, so the wait is bounded by real time, not by game ticks.
        Assert.True(await world.RunUntilPacedAsync(() => chat.Status().Answered >= 1, 10_000), $"{chat.Status()}{Environment.NewLine}{await world.TraceAsync()}");
        await world.RunAsync(500); // the reply is said on the world thread at the next tick
        ChatMessage reply;
        do reply = await player.ReadChatAsync(); while (reply.Type != ChatType.Whisper || reply.Sender != bot);
        Assert.Matches(@"\b10\b", reply.Text);
        Assert.NotEqual(ArcaneCore.World.Playerbots.Party.PlayerbotChatCommands.PoliteReply, reply.Text);

        // The bot is still in its group and still driven by the coordinator; the mage was never turned away either.
        Assert.True(await world.OnWorldAsync(() => world.Coordinator.Drives(healer.Id) && world.Coordinator.Drives(mage.Id)));
        Assert.True(await world.OnWorldAsync(() => world.GroupManager.AreInSameGroup(world.Player(tank.Id).Guid, world.Player(healer.Id).Guid)));
    }

    [Fact]
    public async Task AServerGroupOfBotsNobodyLeads_IsLeft()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([]);
        var first = await world.AddBotAsync("Strayone", Human, Warrior, 5, []);
        var second = await world.AddBotAsync("Straytwo", Human, Mage, 5, []);
        // A group of bots the coordinator does not own (as the server restores one after a restart).
        await world.OnWorldAsync(() =>
        {
            world.GroupManager.Invite(world.Player(first.Id), second.Name);
            world.GroupManager.Accept(world.Player(second.Id));
            Assert.True(world.GroupManager.AreInSameGroup(world.Player(first.Id).Guid, world.Player(second.Id).Guid));
            return true;
        });

        Assert.True(await world.RunUntilAsync(10_000, () => world.GroupManager.GetGroup(world.Player(first.Id).Guid) is null
            && world.GroupManager.GetGroup(world.Player(second.Id).Guid) is null), await world.TraceAsync());
        Assert.Contains(world.Coordinator.Events, e => e.Contains("left a group of bots nobody leads", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABotLeftInsideAnInstanceWithoutAGroup_WalksOutThroughTheExit()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([], dungeon: true);
        var bot = await world.AddBotAsync("Strandone", Human, Warrior, 12, [], EntranceArea);
        Assert.True(await world.OnWorldAsync(() => world.World.Services.GetRequiredService<TeleportFeature>().Teleports
            .TeleportTo(world.Player(bot.Id), 36, -16.4f, -383.07f, 61.78f, 0f)));
        Assert.True(await world.RunUntilAsync(30_000, () => world.Player(bot.Id) is { IsInWorld: true, MapId: 36 }), await world.TraceAsync());

        // Saved inside and logged in again: the login gate lets it into the dungeon it is in (PlayerbotMapPolicy.MayStayOnMap; the
        // default AllowedMaps [0, 1] refused it before, and three such faults disabled the bot).
        Assert.True((await world.Bots.StopAsync(bot.Name)).Success);
        PlayerbotOperationResult started = await world.Bots.StartAsync(bot.Name);
        Assert.True(started.Success, started.Code);
        Assert.True(await world.RunUntilAsync(30_000, () => world.Player(bot.Id) is { IsInWorld: true, MapId: 36 }), await world.TraceAsync());

        Assert.True(await world.RunUntilAsync(120_000, () => world.Player(bot.Id) is { IsInWorld: true, MapId: 0 }), await world.TraceAsync());
        Assert.Contains(world.Coordinator.Events, e => e.Contains("Strandone is inside map 36 without a group", StringComparison.Ordinal));
        Assert.True(await world.RunUntilAsync(10_000, () => !world.Coordinator.Drives(bot.Id)), await world.TraceAsync());
    }
}
