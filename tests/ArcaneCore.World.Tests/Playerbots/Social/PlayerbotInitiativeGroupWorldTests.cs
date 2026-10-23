using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Tests.Playerbots.Party;
using ArcaneCore.World.Tests.Playerbots.Chat;
using ArcaneCore.World.Tests.Playerbots.Groups;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// A player's yes to a bot's "want to team up?" (P2 section 5, review B2) through the real client path and the group coordinator: the client says "sure"
/// in /say without naming the bot, the lane takes it as the reply, the coordinator matches that very player, the leader bot sends the ordinary
/// invitation, the client accepts, and the bot and the player stand in one group.
/// </summary>
public sealed class PlayerbotInitiativeGroupWorldTests
{
    private const byte Human = 1, Warrior = 1;

    private static async Task GivePlayerQuestAsync(GroupTestWorld world, string playerName, uint quest)
        => await world.OnWorldAsync(() =>
        {
            Player player = world.World.Host.World.FindOnlinePlayer(playerName)!;
            PlayerNpcState state = world.World.Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(player)!;
            state.Quests.GetOrAdd(quest).Status = QuestStatus.Incomplete;
            return true;
        });

    // Bot chat on with a fake model client: the chat service only listens (and so only hears the player's say) while it is active.
    private static Task<GroupTestWorld> StartAsync()
        => GroupTestWorld.StartAsync([DuoQuest], options =>
        {
            options.Groups.InvitePlayers = true;
            options.MoveSpeed = 0.1f; // the bots stay where the test puts them, within say range
            options.Chat.Enabled = true;
            ChatSamples.NoScreeningDeadline(options.Chat);
            options.Chat.Providers = [new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.Anthropic, ApiKeyEnvironmentVariable = "TEST_TEAM_KEY" }];
        }, services: services =>
        {
            services.AddSingleton<IBotChatClient>(new FakeBotChatClient());
            services.AddSingleton<IBotChatEnvironment>(new FakeBotChatEnvironment(new() { ["TEST_TEAM_KEY"] = "sk-team" }));
        });

    private static bool PlayerInvited(GroupTestWorld world) => world.Coordinator.Groups.Any(g => g.Members.Any(m => m.Real && m.Invited));

    [Fact]
    public async Task APlayersYes_SaidInSayWithoutNamingTheBot_FormsARealGroupWithThem()
    {
        await using GroupTestWorld world = await StartAsync();
        await using WorldTestClient player = await world.World.EnterWorldAsync("TEAMYES", "Teamyes", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Teambotone", Human, Warrior, 1, []);
        await GivePlayerQuestAsync(world, "Teamyes", DuoQuest);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Any(w => w.BotId == bot.Id)), await world.TraceAsync());

        // Nobody is invited on their own account: a stranger with no affinity and no opt-in.
        await world.RunAsync(20_000);
        Assert.False(PlayerInvited(world), "a stranger is not invited unasked");

        // The waiting bot stands at its meeting point: the player walks up to it, within say range.
        await world.OnWorldAsync(() =>
        {
            Player waiting = world.Player(bot.Id);
            world.World.Host.World.FindOnlinePlayer("Teamyes")!.Relocate(waiting.X + 2f, waiting.Y, waiting.Z, 0, world.World.Host.World.NowMs);
            return true;
        });
        ObjectGuid botGuid = await world.OnWorldAsync(() => world.Player(bot.Id).Guid);
        ObjectGuid playerGuid = await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer("Teamyes")!.Guid);
        PlayerbotInitiativeLane lane = SocialTestKit.Feature(world.World.Host).Initiative;
        // The opening is staged (it needs a chat model to be spoken); everything after it is real.
        await world.OnWorldAsync(() =>
        {
            lane.Expect(world.Bots.Social, bot.Id, botGuid, playerGuid, PlayerbotOpeningKind.Objective, OgreEntry, DuoQuest, world.World.Host.World.NowMs);
            return true;
        });

        await player.SendChatAsync(ChatType.Say, Language.Common, "sure");
        // The packet crosses a socket in real time while the world's clock is manual: give the server real time to read it.
        for (int i = 0; i < 100 && lane.Accepted == 0; i++)
        {
            await Task.Delay(50);
            await world.RunAsync(200);
            await world.OnWorldAsync(() =>
            {
                lane.Run(SocialTestKit.BotPass(world.World.Host, world.World.Host.World.NowMs));
                return true;
            });
        }

        Assert.Equal(1, lane.Accepted);
        Assert.Equal(0, lane.Ignored);

        Assert.True(await world.RunUntilAsync(60_000, () => PlayerInvited(world)), await world.TraceAsync());
        byte[] invite = await player.ReadUntilAsync(WorldOpcode.SmsgGroupInvite);
        Assert.Equal(bot.Name, new PacketReader(invite).ReadCString());
        await player.SendAsync(WorldOpcode.CmsgGroupAccept, []);

        // The accept crosses a socket in real time while the world clock is manual: step the clock in small slices. The coordinator's own rule (a group with fewer than two bots is
        // given up as "too-few") disbands this bot-and-player pair a tick after it forms, so the proof is the coordinator's record that the player joined it as a member.
        string trace = string.Empty;
        for (int i = 0; i < 200 && !trace.Contains("Teamyes damage", StringComparison.Ordinal); i++)
        {
            await Task.Delay(25);
            await world.RunAsync(100);
            trace = await world.TraceAsync();
        }

        Assert.Contains("formed for", trace);
        Assert.Contains("Teambotone tank, Teamyes damage", trace);
        Assert.Contains("complete: 2 members", trace);
    }

    [Fact]
    public async Task APlayerWhoSaidNothing_CostsTheBotAnAffinity_AndBacksTheBotsOff_AndIsNeverInvited()
    {
        await using GroupTestWorld world = await StartAsync();
        await using WorldTestClient player = await world.World.EnterWorldAsync("TEAMNO", "Teamno", AccountSecurity.Player);
        var bot = await world.AddBotAsync("Teambottwo", Human, Warrior, 1, []);
        await GivePlayerQuestAsync(world, "Teamno", DuoQuest);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Any(w => w.BotId == bot.Id)), await world.TraceAsync());

        ObjectGuid botGuid = await world.OnWorldAsync(() => world.Player(bot.Id).Guid);
        ObjectGuid playerGuid = await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer("Teamno")!.Guid);
        PlayerbotInitiativeLane lane = SocialTestKit.Feature(world.World.Host).Initiative;
        long start = await world.OnWorldAsync(() => world.World.Host.World.NowMs);
        await world.OnWorldAsync(() =>
        {
            lane.Expect(world.Bots.Social, bot.Id, botGuid, playerGuid, PlayerbotOpeningKind.Objective, OgreEntry, DuoQuest, start);
            return true;
        });

        // The wait is over (120 seconds of world time) and the player said nothing.
        await world.OnWorldAsync(() =>
        {
            SocialTestKit.Feature(world.World.Host).Initiative.Run(SocialTestKit.BotPass(world.World.Host, start + PlayerbotInitiativeLane.ReplyWaitMs + 1_000));
            return true;
        });

        Assert.Equal(1, lane.Ignored);
        Assert.Equal(0, lane.Accepted);
        Assert.True(await world.OnWorldAsync(() => world.Bots.Social.Memory.TryGet(bot.Id, playerGuid.Value, out var relation) && relation.Affinity == -1));
        Assert.True(lane.NextOpeningFor(playerGuid) >= start + PlayerbotInitiativeLane.ReplyWaitMs + 60 * 60_000L);
        Assert.Equal(1, lane.BackoffOf(playerGuid));
        await world.RunAsync(20_000);
        Assert.False(PlayerInvited(world));
    }

    /// <summary>
    /// Review I10 and I5: another bot's yes to a bot's team-up line is followed through by the coordinator (the answering bot pledges to the group the
    /// asking bot waits in). The whole path is the lane's own, through <c>Open</c>: the asker's roll, the line (phrased by the fake model and said by the
    /// speech path), the answerer's roll, the group signal and the follow-through. Only the model's phrasing is staged.
    /// </summary>
    [Fact]
    public async Task AnotherBotsYes_ToATeamUpLine_RaisesTheAskersSignal_AndReachesTheAskersGroup()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options =>
        {
            options.Groups.InvitePlayers = true;
            options.MoveSpeed = 0.1f;
            options.Social.Guilds.Enabled = false;
            options.Groups.Cooperation.RecruitBaseChance = 0f; // the coordinator does not recruit the healer on its own: only the lane's follow-through can
            options.Groups.Cooperation.RecruitWaitSeconds = 3600; // no waiting-time recruiting either: the pledge below can only be the lane's
            options.Groups.Cooperation.LfgAnswerBonus = 1f; // and the healer's yes to the call is certain
            options.Chat.Enabled = true;
            ChatSamples.NoScreeningDeadline(options.Chat);
            options.Chat.Providers = [new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.Anthropic, ApiKeyEnvironmentVariable = "TEST_TEAM_KEY" }];
        }, services: services =>
        {
            services.AddSingleton<IBotChatClient>(new FakeBotChatClient());
            services.AddSingleton<IBotChatEnvironment>(new FakeBotChatEnvironment(new() { ["TEST_TEAM_KEY"] = "sk-team" }));
        });
        await using WorldTestClient listener = await world.World.EnterWorldAsync("TEAMLISTEN", "Teamlisten", AccountSecurity.Player);
        var tank = await world.AddBotAsync("Teamtank", Human, Warrior, 1, []);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Count() == 1), await world.TraceAsync()); // the asker waits for a partner for the shared quest
        var healer = await world.AddBotAsync("Teamheal", Human, Warrior, 1, [], new System.Numerics.Vector3(StartX, StartY - 150f, StartZ));
        // The healer and a real listener stand beside the waiting pair.
        await world.OnWorldAsync(() =>
        {
            Player anchor = world.Player(tank.Id);
            world.Player(healer.Id).Relocate(anchor.X + 3f, anchor.Y, anchor.Z, 0, world.World.Host.World.NowMs);
            world.World.Host.World.FindOnlinePlayer("Teamlisten")!.Relocate(anchor.X + 5f, anchor.Y, anchor.Z, 0, world.World.Host.World.NowMs);
            return true;
        });
        await world.RunAsync(600); // the characters see each other

        PlayerbotInitiativeLane lane = SocialTestKit.Feature(world.World.Host).Initiative;
        ObjectGuid tankGuid = await world.OnWorldAsync(() => world.Player(tank.Id).Guid);
        ObjectGuid healerGuid = await world.OnWorldAsync(() => world.Player(healer.Id).Guid);
        Assert.False(world.Coordinator.IsPledged(healer.Id, (PlayerbotGroupGoalKind.Quest, OgreEntry)));
        // A world time at which an asker's roll and the healer's answering roll both say yes.
        long Asks(ObjectGuid asker) => HelpTestKit.NowWhere(t =>
            PlayerbotInitiativeRules.Rolls(asker.Value, healerGuid.Value, t, PlayerbotInitiativeRules.SaltOf(PlayerbotOpeningKind.Objective),
                PlayerbotInitiativeRules.Chance(PlayerbotOpeningKind.Objective, PlayerbotTraits.For(asker.Value).Sociability, 0f))
            && PlayerbotInitiativeRules.Rolls(healerGuid.Value, asker.Value, t, PlayerbotInitiativeRules.SaltOf(PlayerbotOpeningKind.Objective) ^ 0xA25E12UL,
                PlayerbotInitiativeRules.Chance(PlayerbotOpeningKind.Objective, PlayerbotTraits.For(healerGuid.Value).Sociability, 0f)));
        long at = Asks(tankGuid);
        // The healer holds the shared quest only now, so it has not started to wait for a group of its own: the pledge is the lane's doing, not the coordinator's own matching.
        await GivePlayerQuestAsync(world, "Teamheal", DuoQuest);
        // The healer sets the shared quest aside (the coordinator's own need search skips a suspended quest), so the coordinator can never pair the two itself.
        await world.OnWorldAsync(() =>
        {
            PartyTestHost.Feature(world.World.Host).FindBrain(healer.Id)!.Suspensions.SuspendQuest(DuoQuest, world.World.Host.World.NowMs, countsAsSetAside: false);
            return true;
        });

        await world.OnWorldAsync(() =>
        {
            lane.Run(SocialTestKit.BotPass(world.World.Host, at));
            return true;
        });
        Assert.True(lane.Openings >= 1, "the asker opened");
        // The line is phrased by the model and said in real time (the wait for an answer, and the healer's own roll, start when it is said).
        PlayerbotBrain askerBrain = PartyTestHost.Feature(world.World.Host).FindBrain(tank.Id)!;
        for (int i = 0; i < 200 && await world.OnWorldAsync(() => askerBrain.Risk.GroupSignal) is null; i++)
        {
            await Task.Delay(10);
            await world.RunAsync(50);
        }

        // The answering bot's yes raised the asker's group signal, and the follow-through (one offer per pass until the asker waits) ran from the lane's passes.
        PlayerbotGroupSignal? signal = await world.OnWorldAsync(() => askerBrain.Risk.GroupSignal);
        Assert.True(signal.HasValue);
        Assert.Equal(PlayerbotGroupSignal.ContentionSource, signal!.Value.Source);
        for (int i = 0; i < 100 && lane.BotOffers == 0; i++)
        {
            await Task.Delay(10);
            await world.RunAsync(50);
            await world.OnWorldAsync(() =>
            {
                lane.Run(SocialTestKit.BotPass(world.World.Host, at + 1_000 + i * 50L));
                return true;
            });
        }

        // The coordinator never paired the two itself (the healer set the quest aside), so the lane's follow-through is the only way the healer can have joined.
        Assert.Equal(1, lane.BotOffers);
        Assert.True(world.Coordinator.IsPledged(healer.Id, (PlayerbotGroupGoalKind.Quest, OgreEntry)) || await world.OnWorldAsync(() => world.GroupManager.AreInSameGroup(tankGuid, healerGuid)), await world.TraceAsync());
    }
}
