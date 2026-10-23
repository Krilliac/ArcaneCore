using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Tests.Playerbots.Chat;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>Initiative (P2 section 5): the pure rules, and the openings, caps, back-off, replies and comments on a running world with a frozen social feature and a fake model client.</summary>
public sealed class PlayerbotInitiativeTests
{
    private static Task<SocialWorld> StartAsync(Action<PlayerbotOptions>? configure = null) => SocialWorld.StartAsync(options =>
    {
        options.Chat.Enabled = true;
        options.Chat.PerPlayerCooldownSeconds = 0;
        ChatSamples.NoScreeningDeadline(options.Chat);
        options.Chat.Providers = [new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.Anthropic, ApiKeyEnvironmentVariable = "TEST_INITIATIVE_KEY" }];
        configure?.Invoke(options);
    }, frozen: true, services: services =>
    {
        services.AddSingleton<IBotChatClient>(new FakeBotChatClient());
        services.AddSingleton<IBotChatEnvironment>(new FakeBotChatEnvironment(new() { ["TEST_INITIATIVE_KEY"] = "sk-initiative" }));
    });

    private static Task PassAsync(SocialWorld world, long nowMs)
        => world.Host.OnWorldAsync(() => SocialTestKit.Feature(world.Host).Initiative.Run(SocialTestKit.BotPass(world.Host, nowMs)));

    private static PlayerbotInitiativeLane Lane(SocialWorld world) => SocialTestKit.Feature(world.Host).Initiative;

    // --- the rules ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRules_ScaleByMood_ChooseTheChannel_BackOff_AndReadAYes()
    {
        Assert.True(PlayerbotInitiativeRules.Chance(PlayerbotOpeningKind.Greeting, 0.5f, 1f) > PlayerbotInitiativeRules.Chance(PlayerbotOpeningKind.Greeting, 0.5f, 0f));
        Assert.True(PlayerbotInitiativeRules.Chance(PlayerbotOpeningKind.Greeting, 0.5f, -1f) < PlayerbotInitiativeRules.Chance(PlayerbotOpeningKind.Greeting, 0.5f, 0f));
        Assert.Equal(3.0, PlayerbotInitiativeRules.Chance(PlayerbotOpeningKind.Greeting, 0.5f, 1f) / PlayerbotInitiativeRules.Chance(PlayerbotOpeningKind.Greeting, 0.5f, -1f), 6);
        Assert.Equal(1.0, PlayerbotInitiativeRules.Chance(PlayerbotOpeningKind.Objective, 1f, 1f));
        Assert.Equal(BotSpeechChannel.Whisper, PlayerbotInitiativeRules.ChannelFor(true, -5));
        Assert.Equal(BotSpeechChannel.Whisper, PlayerbotInitiativeRules.ChannelFor(false, 10));
        Assert.Equal(BotSpeechChannel.Say, PlayerbotInitiativeRules.ChannelFor(false, 9));
        Assert.Equal(30 * 60_000L, PlayerbotInitiativeRules.CooldownMs(30, 0));
        Assert.Equal(60 * 60_000L, PlayerbotInitiativeRules.CooldownMs(30, 1));
        Assert.Equal(30 * 60_000L * 16, PlayerbotInitiativeRules.CooldownMs(30, 9));
        foreach (string yes in new[] { "yes", "Sure, why not", "ok!", "yeah invite me" }) Assert.True(PlayerbotInitiativeRules.IsPositiveReply(yes), yes);
        foreach (string no in new[] { "no", "no thanks", "sure but I am busy", "hello", "", "i am busy" }) Assert.False(PlayerbotInitiativeRules.IsPositiveReply(no), no);
        for (long t = 0; t < 200_000; t += 60_000)
            Assert.Equal(PlayerbotInitiativeRules.Rolls(1, 2, t, 3, 0.5), PlayerbotInitiativeRules.Rolls(1, 2, t, 3, 0.5));
    }

    // --- openings -----------------------------------------------------------------------------------------------------------------

    private sealed record Pair(Guid Bot, ObjectGuid BotGuid, ObjectGuid PlayerGuid);

    /// <summary>A free bot that knows a real player beside it: two groupings (familiarity 2), and as many more as <paramref name="extraGroupings"/> asks for (more affinity).</summary>
    private static async Task<Pair> FamiliarAsync(SocialWorld world, string bot, string player, int extraGroupings = 0)
    {
        Guid id = await world.AddBotAsync(bot);
        await HelpTestKit.StandAsync(world, bot, (player, 5f));
        ObjectGuid botGuid = await world.Host.PlayerStateAsync(bot, p => p.Guid);
        ObjectGuid playerGuid = await world.Host.PlayerStateAsync(player, p => p.Guid);
        await world.Host.OnWorldAsync(() =>
        {
            for (int i = 0; i < 2 + extraGroupings; i++) HelpTestKit.RecorderOf(world).Note(botGuid, playerGuid, PlayerbotSocialEvent.Grouped);
        });
        return new Pair(id, botGuid, playerGuid);
    }

    private static long GreetingTime(Pair pair, long after = 0)
    {
        PlayerbotTraits traits = PlayerbotTraits.For(pair.BotGuid.Value);
        double chance = PlayerbotInitiativeRules.Chance(PlayerbotOpeningKind.Greeting, traits.Sociability, 0f);
        return HelpTestKit.NowWhere(t => t > after && PlayerbotInitiativeRules.Rolls(pair.BotGuid.Value, pair.PlayerGuid.Value, t, PlayerbotInitiativeRules.SaltOf(PlayerbotOpeningKind.Greeting), chance));
    }

    [Fact]
    public async Task AFamiliarArrival_IsGreeted_ByASay_UntilTheBotLikesThemEnoughToWhisper_AndOnlyOncePerCooldown()
    {
        await using SocialWorld world = await StartAsync();
        await using WorldTestClient player = await world.AddPlayerAsync("Initfriend");
        Pair pair = await FamiliarAsync(world, "Initbot", "Initfriend");
        Assert.True(HelpTestKit.RecorderOf(world).Memory.TryGet(pair.Bot, pair.PlayerGuid.Value, out PlayerbotRelation relation));
        Assert.True(relation.Familiarity >= 2 && HelpTestKit.RecorderOf(world).Memory.EffectiveAffinity(relation) is >= 0 and < PlayerbotInitiativeRules.WhisperAffinity);
        long now = GreetingTime(pair);

        await PassAsync(world, now);

        Assert.Equal(1, Lane(world).Openings);
        Assert.Equal(1, Lane(world).ByKind[(int)PlayerbotOpeningKind.Greeting]);
        Assert.Equal(0, Lane(world).Whispers); // not opted in, not liked enough: a public say
        long until = Lane(world).NextOpeningFor(pair.PlayerGuid);
        Assert.True(until >= now + 30 * 60_000L);

        // Inside the cooldown no bot opens with this player again, whatever the roll says.
        long again = GreetingTime(pair, now + 60_000);
        Assert.True(again < until);
        await PassAsync(world, again);
        Assert.Equal(1, Lane(world).Openings);
    }

    [Fact]
    public async Task ALikedPlayer_IsWhisperedTo()
    {
        await using SocialWorld world = await StartAsync();
        await using WorldTestClient player = await world.AddPlayerAsync("Initliked");
        Pair pair = await FamiliarAsync(world, "Initwhisper", "Initliked", extraGroupings: 4);
        Assert.True(HelpTestKit.RecorderOf(world).Memory.TryGet(pair.Bot, pair.PlayerGuid.Value, out PlayerbotRelation relation));
        Assert.True(HelpTestKit.RecorderOf(world).Memory.EffectiveAffinity(relation) >= PlayerbotInitiativeRules.WhisperAffinity);

        await PassAsync(world, GreetingTime(pair));

        Assert.Equal(1, Lane(world).Openings);
        Assert.Equal(1, Lane(world).Whispers);
    }

    [Fact]
    public async Task ARivalOrADislikedPlayer_IsNeverApproached_AndSwitchedOff_NobodyIs()
    {
        await using SocialWorld world = await StartAsync();
        await using WorldTestClient player = await world.AddPlayerAsync("Initfoe");
        Pair pair = await FamiliarAsync(world, "Initwary", "Initfoe");
        long now = GreetingTime(pair);
        await world.Host.OnWorldAsync(() =>
        {
            for (int i = 0; i < 3; i++) HelpTestKit.RecorderOf(world).Note(pair.BotGuid, pair.PlayerGuid, PlayerbotSocialEvent.KilledBy);
        });
        await PassAsync(world, now);
        Assert.Equal(0, Lane(world).Openings);

        await world.Host.OnWorldAsync(() => HelpTestKit.RecorderOf(world).Memory.ForgetOthers(pair.PlayerGuid.Value));
        await world.Host.OnWorldAsync(() =>
        {
            for (int i = 0; i < 2; i++) HelpTestKit.RecorderOf(world).Note(pair.BotGuid, pair.PlayerGuid, PlayerbotSocialEvent.Grouped);
            world.Options.Social.Initiative.Enabled = false;
        });
        await PassAsync(world, now + 60_000);
        Assert.Equal(0, Lane(world).Openings);
    }

    [Fact]
    public async Task OneOpeningPerPlayerPerCooldown_AndTheGlobalCapPerPass()
    {
        await using SocialWorld world = await StartAsync(options => options.Social.Initiative.MaxOpeningsPerPass = 1);
        await using WorldTestClient p1 = await world.AddPlayerAsync("Capone");
        await using WorldTestClient p2 = await world.AddPlayerAsync("Captwo");
        Pair a1 = await FamiliarAsync(world, "Capbota", "Capone");
        Pair b1 = await FamiliarAsync(world, "Capbotb", "Capone");
        await HelpTestKit.StandAsync(world, "Capbota", ("Captwo", 6f), ("Capbotb", 4f));
        ObjectGuid p2Guid = await world.Host.PlayerStateAsync("Captwo", p => p.Guid);
        await world.Host.OnWorldAsync(() =>
        {
            foreach (ObjectGuid bot in new[] { a1.BotGuid, b1.BotGuid })
                for (int i = 0; i < 2; i++) HelpTestKit.RecorderOf(world).Note(bot, p2Guid, PlayerbotSocialEvent.Grouped);
        });
        // A time at which every bot-and-player pair would open.
        long now = HelpTestKit.NowWhere(t => new[] { (a1.BotGuid, a1.PlayerGuid), (a1.BotGuid, p2Guid), (b1.BotGuid, b1.PlayerGuid), (b1.BotGuid, p2Guid) }.All(pair =>
            PlayerbotInitiativeRules.Rolls(pair.Item1.Value, pair.Item2.Value, t, PlayerbotInitiativeRules.SaltOf(PlayerbotOpeningKind.Greeting),
                PlayerbotInitiativeRules.Chance(PlayerbotOpeningKind.Greeting, PlayerbotTraits.For(pair.Item1.Value).Sociability, 0f))));

        await PassAsync(world, now);
        Assert.Equal(1, Lane(world).Openings); // two bots could open, the cap is one per pass

        await PassAsync(world, now + 25_000); // the next pass (the first bot rests for 20 seconds): the other player, the other bot
        Assert.Equal(2, Lane(world).Openings);
        await PassAsync(world, now + 50_000);
        Assert.Equal(2, Lane(world).Openings); // both players have been spoken to: one opening per player per cooldown
    }

    [Fact]
    public async Task AnOpeningNobodyAnswers_CostsAffinityAndDoublesTheCooldown_AnAnsweredOneDoesNot()
    {
        await using SocialWorld world = await StartAsync();
        await using WorldTestClient player = await world.AddPlayerAsync("Initsilent");
        Pair pair = await FamiliarAsync(world, "Initwait", "Initsilent");
        PlayerbotSocialMemory memory = HelpTestKit.RecorderOf(world).Memory;
        Assert.True(memory.TryGet(pair.Bot, pair.PlayerGuid.Value, out PlayerbotRelation before));
        long now = GreetingTime(pair);
        await PassAsync(world, now);
        Assert.Equal(1, Lane(world).Openings);

        await PassAsync(world, now + PlayerbotInitiativeLane.ReplyWaitMs + 1_000);

        Assert.Equal(1, Lane(world).Ignored);
        Assert.True(memory.TryGet(pair.Bot, pair.PlayerGuid.Value, out PlayerbotRelation after));
        Assert.Equal(before.Affinity - 1, after.Affinity);
        Assert.Equal(before.Familiarity, after.Familiarity);
        Assert.True(Lane(world).NextOpeningFor(pair.PlayerGuid) >= now + PlayerbotInitiativeLane.ReplyWaitMs + 60 * 60_000L);

        // An answered opening is not counted against the player.
        Lane(world).Expect(pair.Bot, pair.BotGuid, pair.PlayerGuid, PlayerbotOpeningKind.Greeting, 0, 0, now + 200_000);
        HelpTestKit.RecorderOf(world).NoteChatLine(pair.Bot, pair.BotGuid, pair.PlayerGuid, "hello again");
        await PassAsync(world, now + 200_000 + PlayerbotInitiativeLane.ReplyWaitMs + 1_000);
        Assert.Equal(1, Lane(world).Ignored);
    }

    [Fact]
    public async Task AYesToTeamingUp_RaisesAGroupSignal_ANoDoesNot()
    {
        await using SocialWorld world = await StartAsync();
        await using WorldTestClient player = await world.AddPlayerAsync("Initteam");
        Pair pair = await FamiliarAsync(world, "Initquest", "Initteam");
        PlayerbotBrain brain = HelpTestKit.BrainOf(world, pair.Bot);

        Lane(world).Expect(pair.Bot, pair.BotGuid, pair.PlayerGuid, PlayerbotOpeningKind.Objective, 1234, 77, 1_000);
        HelpTestKit.RecorderOf(world).NoteChatLine(pair.Bot, pair.BotGuid, pair.PlayerGuid, "no thanks, I am busy");
        await PassAsync(world, 2_000);
        Assert.Equal(0, Lane(world).Accepted);
        Assert.Null(await world.Host.OnWorldAsync(() => brain.Risk.GroupSignal));

        Lane(world).Expect(pair.Bot, pair.BotGuid, pair.PlayerGuid, PlayerbotOpeningKind.Objective, 1234, 77, 3_000);
        HelpTestKit.RecorderOf(world).NoteChatLine(pair.Bot, pair.BotGuid, pair.PlayerGuid, "yes sure, let's team up");
        await PassAsync(world, 4_000);
        Assert.Equal(1, Lane(world).Accepted);
        PlayerbotGroupSignal signal = (await world.Host.OnWorldAsync(() => brain.Risk.GroupSignal))!.Value;
        Assert.Equal(1234u, signal.Entry);
        Assert.Equal(77u, signal.QuestId);
        Assert.Equal(PlayerbotGroupSignal.ContentionSource, signal.Source);
    }

    // --- comments -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARareCreatureInSight_IsRemarkedOn_OncePerBotGap_AndNotWhenSwitchedOff()
    {
        await using SocialWorld world = await StartAsync();
        await using WorldTestClient player = await world.AddPlayerAsync("Initlisten");
        Guid bot = await world.AddBotAsync("Initrare");
        await HelpTestKit.StandAsync(world, "Initrare", ("Initlisten", 5f));
        ObjectGuid botGuid = await world.Host.PlayerStateAsync("Initrare", p => p.Guid);
        uint zone = await world.Host.PlayerStateAsync("Initrare", p => p.ZoneId);
        await world.Host.OnWorldAsync(() => HelpTestKit.SpawnRare(HelpTestKit.Online(world, "Initrare"), 10f));
        PlayerbotTraits traits = PlayerbotTraits.For(botGuid.Value);
        long Roll(long after) => HelpTestKit.NowWhere(t => t > after && PlayerbotInitiativeRules.Rolls(botGuid.Value, zone, t, PlayerbotInitiativeRules.CommentSalt, PlayerbotInitiativeRules.CommentChance(traits.Chattiness, 0f)));

        await world.Host.OnWorldAsync(() => world.Options.Social.Initiative.CommentsEnabled = false);
        long first = Roll(0);
        await PassAsync(world, first);
        Assert.Equal(0, Lane(world).Comments);

        await world.Host.OnWorldAsync(() => world.Options.Social.Initiative.CommentsEnabled = true);
        await PassAsync(world, first + 30_000);
        long second = Roll(first + 30_000);
        await PassAsync(world, second);
        Assert.Equal(1, Lane(world).Comments);

        // Another minute-roll that says yes, but the bot spoke less than ten minutes ago.
        long third = Roll(second + 61_000);
        Assert.True(third < second + 10 * 60_000L);
        await PassAsync(world, third);
        Assert.Equal(1, Lane(world).Comments);
        _ = bot;
    }
}
