using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The life of an LFG post's answer count, and guild calls. Split from the former <c>PlayerbotRecruitWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotRecruitWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotRecruitGuildCallWorldTests : PlayerbotRecruitWorldTestBase
{
    /// <summary>A post's answer count lives as long as the post (PostLifeSeconds), not 30 s: the poster speaking again later does not buy it a fresh set of answers.</summary>
    [Fact]
    public async Task AnLfgPostsAnswerCount_LivesAsLongAsThePost()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Still(options);
            options.MaxBots = 30;
            options.MaxRegisteredBots = 40;
            options.Chat.Enabled = true;
            options.Chat.Voice.PostLifeSeconds = 60;
            options.Groups.Cooperation.RecruitBaseChance = 1f;
            options.Groups.Cooperation.RecruitRadiusYards = 1500;
            options.Groups.Cooperation.MaxLfgAnswersPerLine = 2;
        });
        var (tank, _) = await WaitingPairAsync(world);
        var healers = new List<(Guid Id, string Name)>();
        for (int i = 0; i < 8; i++)
            healers.Add(await world.AddBotAsync("Lfglife" + (char)('a' + i), Human, Priest, 10, [LesserHeal, Smite], South + new Vector3(i, 0, 0)));
        PlayerbotChat chat = world.Bots.Chat!;
        Assert.True(await world.RunUntilAsync(10_000, () => chat.Responder is not null), await world.TraceAsync());

        (long postedMs, int answers, int pledged) = await world.OnWorldAsync(() =>
        {
            long at = chat.NowMs;
            chat.Voice.Post(tank.Id, new PlayerbotVoicePost(PlayerbotVoicePostKind.LookingForGroup, at, "the ogre", MinLevel: 1));
            foreach (var hearer in healers)
            {
                Player hearing = world.Player(hearer.Id), speaking = world.Player(tank.Id);
                chat.Hear(new BotHeardLine(hearer.Id, hearing.Guid, hearing.Name, ChatType.Channel, "LookingForGroup", speaking.Guid, speaking.Name,
                    SenderIsBot: true, "LFM the ogre need heals", AddressesBot: false, FirstHearing: true));
            }

            var goalKey = world.Coordinator.WaitingBots.Single(w => w.BotId == tank.Id).Goal.Key;
            return (at, world.Coordinator.LfgAnswersOf(tank.Id, at), healers.Count(h => world.Coordinator.IsPledged(h.Id, goalKey)));
        });
        Assert.InRange(answers, 1, 2);
        Assert.Equal(answers, pledged); // pledges, not replies

        await world.RunAsync(31_000);
        Assert.Equal(answers, await world.OnWorldAsync(() => world.Coordinator.LfgAnswersOf(tank.Id, postedMs)));
        await world.RunAsync(35_000);
        Assert.Equal(-1, await world.OnWorldAsync(() => world.Coordinator.LfgAnswersOf(tank.Id, postedMs)));
    }

    [Fact]
    public async Task AGuildCall_ForADungeonGroup_BringsAGuildmateWithoutTheWait_AndOnlyThen()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], options =>
        {
            Still(options);
            options.Chat.Enabled = true;
            options.Groups.Cooperation.RecruitBaseChance = 1f;
        }, dungeon: true);
        var tank = await world.AddBotAsync("Gtank", Human, Warrior, 12, [Taunt], EntranceArea);
        var mage = await world.AddBotAsync("Gmage", Human, Mage, 12, [Fireball], EntranceArea);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Count() == 2), await world.TraceAsync());
        Vector3 away = EntranceArea + new Vector3(0, 120f, 0);
        var mate = await world.AddBotAsync("Gmate", Human, Priest, 12, [LesserHeal, Smite], away);
        var stranger = await world.AddBotAsync("Gstranger", Human, Priest, 12, [LesserHeal, Smite], away + new Vector3(5f, 0, 0));
        Guild guild = await world.OnWorldAsync(() =>
        {
            GuildManager guilds = world.World.Services.GetRequiredService<SocialFeature>().Context.Guilds;
            Assert.Equal(GuildAdminResult.Ok, guilds.Create(world.Player(tank.Id).Guid.Low, "Callers", out Guild? created));
            Assert.Equal(GuildAdminResult.Ok, guilds.AdminInvite(world.Player(mage.Id).Guid.Low, "Callers"));
            Assert.Equal(GuildAdminResult.Ok, guilds.AdminInvite(world.Player(mate.Id).Guid.Low, "Callers"));
            return created!;
        });

        // The coordinator has a forming dungeon group: the caller is the bot that waited longest, and its guild may be asked.
        var calls = await world.OnWorldAsync(() => world.Coordinator.FormingCalls());
        PlayerbotFormingCall call = Assert.Single(calls);
        Assert.Contains(call.CallerBotId, new[] { tank.Id, mage.Id });
        Assert.Equal(2, call.Have);
        Assert.Equal(PlayerbotGroupGoalKind.Dungeon, call.Goal.Kind);

        // The caller's guild has the "anyone for the dungeon?" topic queued (it is said once a real player can read guild chat).
        PlayerbotSocialFeature feature = world.World.Services.GetRequiredService<PlayerbotSocialFeature>();
        await world.RunAsync(5_000);
        Assert.Equal(1, await world.OnWorldAsync(() => feature.GuildChat.Waiting(guild.Id, "guild.lfm")));
        Assert.Empty(await world.OnWorldAsync(() => world.Coordinator.Groups));

        // Nobody comes before the call (the 20 second wait has not run out); once the guild is called, the guildmate does and the stranger does not.
        await world.OnWorldAsync(() => { world.Coordinator.NoteGuildCall(guild.Id, call.Goal.Key); return true; });
        Assert.True(await world.OnWorldAsync(() => world.Coordinator.HasGuildCall(guild.Id, call.Goal.Key)));
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Count > 0), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.NotNull(group.Find(mate.Id));
        Assert.Null(group.Find(stranger.Id));
    }

    [Fact]
    public async Task AnOpenWorldQuestGroup_IsNoGuildCall()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], Still);
        await WaitingPairAsync(world);
        Assert.Empty(await world.OnWorldAsync(() => world.Coordinator.FormingCalls()));
    }
}
