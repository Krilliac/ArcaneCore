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
    /// Review I10: another bot's yes to a bot's team-up line is followed through by the coordinator (the answering bot pledges to the group the asking
    /// bot waits in), instead of dying as a signal. The yes is staged (the opening needs a chat model to be spoken); the follow-through is the lane's own.
    /// </summary>
    [Fact]
    public async Task AnotherBotsYes_IsFollowedThrough_AsAPledgeToTheAskersGroup()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            options.MoveSpeed = 0.1f;
            options.Social.Guilds.Enabled = false;
        });
        var tank = await world.AddBotAsync("Teamtank", Human, Warrior, 10, [Taunt]);
        var mage = await world.AddBotAsync("Teammage", Human, 8, 10, [133]);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Count() == 2), await world.TraceAsync());
        var healer = await world.AddBotAsync("Teamheal", Human, 5, 10, [2050, 585], new System.Numerics.Vector3(StartX, StartY - 150f, StartZ));
        PlayerbotInitiativeLane lane = SocialTestKit.Feature(world.World.Host).Initiative;
        ObjectGuid healerGuid = await world.OnWorldAsync(() => world.Player(healer.Id).Guid);
        Assert.False(world.Coordinator.IsPledged(healer.Id, (PlayerbotGroupGoalKind.Quest, OgreEntry)));

        await world.OnWorldAsync(() =>
        {
            lane.FollowBot(tank.Id, healer.Id, healerGuid, world.World.Host.World.NowMs);
            lane.Run(SocialTestKit.BotPass(world.World.Host, world.World.Host.World.NowMs));
            return true;
        });

        Assert.Equal(1, lane.BotOffers);
        Assert.True(world.Coordinator.IsPledged(healer.Id, (PlayerbotGroupGoalKind.Quest, OgreEntry)));
    }
}
