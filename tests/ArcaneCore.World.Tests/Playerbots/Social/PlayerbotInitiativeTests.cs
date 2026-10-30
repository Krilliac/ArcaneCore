using ArcaneCore.Protocol;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Progression;
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
        Assert.Equal(BotSpeechChannel.Whisper, PlayerbotInitiativeRules.ChannelFor(true, -5, false));
        Assert.Equal(BotSpeechChannel.Whisper, PlayerbotInitiativeRules.ChannelFor(false, 10, true));
        Assert.Equal(BotSpeechChannel.Say, PlayerbotInitiativeRules.ChannelFor(false, 9, true));
        Assert.Equal(BotSpeechChannel.Say, PlayerbotInitiativeRules.ChannelFor(false, 40, false)); // affinity the bot earned by helping is no invitation
        Assert.Equal(30 * 60_000L, PlayerbotInitiativeRules.CooldownMs(30, 0));
        Assert.Equal(60 * 60_000L, PlayerbotInitiativeRules.CooldownMs(30, 1));
        Assert.Equal(30 * 60_000L * 16, PlayerbotInitiativeRules.CooldownMs(30, 9));
        foreach (string yes in new[] { "yes", "Sure, why not", "ok!", "yeah invite me" }) Assert.True(PlayerbotInitiativeRules.IsPositiveReply(yes), yes);
        foreach (string no in new[] { "no", "no thanks", "sure but I am busy", "hello", "", "i am busy", "not sure", "not now", "sure, I can't", "ok but I won't" })
            Assert.False(PlayerbotInitiativeRules.IsPositiveReply(no), no);
        Assert.Equal(PlayerbotInitiativeRules.Reply.No, PlayerbotInitiativeRules.Classify("not sure"));
        Assert.Equal(PlayerbotInitiativeRules.Reply.Other, PlayerbotInitiativeRules.Classify("hello again"));
        Assert.Equal(PlayerbotInitiativeRules.Reply.Yes, PlayerbotInitiativeRules.Classify("sure"));
        foreach (string word in new[] { "Sure", "no", "cant", "wont", "why", "now", "later", "maybe", "thx", "ty", "np", "thanks", "Hello" }) Assert.True(PlayerbotInitiativeRules.IsReplyWord(word), word);
        Assert.False(PlayerbotInitiativeRules.IsReplyWord("Bobby"));
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
        await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() => Lane(world).PendingCount).Result == 1, "the greeting to be said"); // the cooldown starts when the line is said
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
        await using SocialWorld world = await StartAsync(options =>
        {
            options.Social.Initiative.MaxOpeningsPerPass = 1;
            options.Chat.Speech.BotGapSeconds = 0; // the speech limiter works in real time: two lines said within a moment of each other would be refused
            options.Chat.Speech.TargetGapSeconds = 0;
            options.Chat.Speech.DedupeSeconds = 0; // the fake model phrases every opening alike: the speech dedupe would drop the second line
        });
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
        await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() => Lane(world).PendingCount).Result == 1, "the first opening to be said");

        await PassAsync(world, now + 25_000); // the next pass (the first bot rests for 20 seconds): the other player, the other bot
        Assert.Equal(2, Lane(world).Openings);
        await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() => Lane(world).PendingCount).Result == 2, "the second opening to be said");
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
        // The wait for the answer starts when the line is really said (the model's phrasing arrives in real time), not when it is queued.
        await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() => Lane(world).PendingFor(pair.Bot, pair.PlayerGuid)).Result, "the opening to be said");

        await PassAsync(world, now + PlayerbotInitiativeLane.ReplyWaitMs + 1_000);

        Assert.Equal(1, Lane(world).Ignored);
        Assert.True(memory.TryGet(pair.Bot, pair.PlayerGuid.Value, out PlayerbotRelation after));
        Assert.Equal(before.Affinity - 1, after.Affinity);
        Assert.Equal(before.Familiarity, after.Familiarity);
        Assert.True(Lane(world).NextOpeningFor(pair.PlayerGuid) >= now + PlayerbotInitiativeLane.ReplyWaitMs + 60 * 60_000L);

        // An answered opening is not counted against the player.
        Lane(world).Expect(HelpTestKit.RecorderOf(world), pair.Bot, pair.BotGuid, pair.PlayerGuid, PlayerbotOpeningKind.Greeting, 0, 0, now + 200_000);
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

        Lane(world).Expect(HelpTestKit.RecorderOf(world), pair.Bot, pair.BotGuid, pair.PlayerGuid, PlayerbotOpeningKind.Objective, 1234, 77, 1_000);
        HelpTestKit.RecorderOf(world).NoteChatLine(pair.Bot, pair.BotGuid, pair.PlayerGuid, "no thanks, I am busy");
        await PassAsync(world, 2_000);
        Assert.Equal(0, Lane(world).Accepted);
        Assert.Null(await world.Host.OnWorldAsync(() => brain.Risk.GroupSignal));
        Assert.True(Lane(world).NextOpeningFor(pair.PlayerGuid) >= 2_000 + 60 * 60_000L); // a no backs the bots off: the cooldown doubles

        Lane(world).Expect(HelpTestKit.RecorderOf(world), pair.Bot, pair.BotGuid, pair.PlayerGuid, PlayerbotOpeningKind.Objective, 1234, 77, 3_000);
        HelpTestKit.RecorderOf(world).NoteChatLine(pair.Bot, pair.BotGuid, pair.PlayerGuid, "yes sure, let's team up");
        await PassAsync(world, 4_000);
        Assert.Equal(1, Lane(world).Accepted);
        PlayerbotGroupSignal signal = (await world.Host.OnWorldAsync(() => brain.Risk.GroupSignal))!.Value;
        Assert.Equal(1234u, signal.Entry);
        Assert.Equal(77u, signal.QuestId);
        Assert.Equal(PlayerbotGroupSignal.ContentionSource, signal.Source);
    }

    // Run passes until the lane has taken the answer (the player's say reaches the bot on its own thread of the world).
    private static Task AnswerTakenAsync(SocialWorld world, Pair pair, long nowMs)
        => HelpTestKit.UntilAsync(() =>
        {
            PassAsync(world, nowMs).GetAwaiter().GetResult();
            return Lane(world).PendingFor(pair.Bot, pair.PlayerGuid) == false;
        }, "the lane to take the player's answer");

    [Fact]
    public async Task ThePlayersNextSay_IsTheReply_NamedOrNot_ANoBacksOffWithoutAPenalty_APositiveOneDoesNot()
    {
        await using SocialWorld world = await StartAsync();
        await using WorldTestClient player = await world.AddPlayerAsync("Initreply");
        Pair pair = await FamiliarAsync(world, "Initheard", "Initreply");
        PlayerbotSocialMemory memory = HelpTestKit.RecorderOf(world).Memory;
        Assert.True(memory.TryGet(pair.Bot, pair.PlayerGuid.Value, out PlayerbotRelation before));
        long now = 10_000;

        // A "no" the bot is not even named in, said within earshot.
        await world.Host.OnWorldAsync(() => Lane(world).Expect(HelpTestKit.RecorderOf(world), pair.Bot, pair.BotGuid, pair.PlayerGuid, PlayerbotOpeningKind.Greeting, 0, 0, now));
        await player.SendChatAsync(ChatType.Say, Language.Common, "no thanks");
        await AnswerTakenAsync(world, pair, now + 1_000);
        Assert.Equal(0, Lane(world).Ignored); // it answered: never marked unanswered
        Assert.True(Lane(world).NextOpeningFor(pair.PlayerGuid) >= now + 1_000 + 60 * 60_000L); // but the cooldown doubled
        Assert.True(memory.TryGet(pair.Bot, pair.PlayerGuid.Value, out PlayerbotRelation after));
        Assert.Equal(before.Affinity, after.Affinity); // and no penalty in affinity

        // A friendly reply: the back-off is cleared, so the next cooldown is the plain one.
        await world.Host.OnWorldAsync(() => Lane(world).Expect(HelpTestKit.RecorderOf(world), pair.Bot, pair.BotGuid, pair.PlayerGuid, PlayerbotOpeningKind.Greeting, 0, 0, now + 2_000));
        await player.SendChatAsync(ChatType.Say, Language.Common, "hello there");
        await AnswerTakenAsync(world, pair, now + 3_000);
        Assert.Equal(0, Lane(world).Ignored);
        Assert.True(memory.TryGet(pair.Bot, pair.PlayerGuid.Value, out PlayerbotRelation last));
        Assert.Equal(before.Affinity, last.Affinity);
        Assert.Equal(0, Lane(world).BackoffOf(pair.PlayerGuid));
    }

    [Fact]
    public async Task HelpTheBotGaveAlone_IsNoInvitationToGreetOrWhisper_ButAnInteractionIs()
    {
        await using SocialWorld world = await StartAsync();
        await using WorldTestClient player = await world.AddPlayerAsync("Initquiet");
        Guid id = await world.AddBotAsync("Initbuffer");
        await HelpTestKit.StandAsync(world, "Initbuffer", ("Initquiet", 5f));
        ObjectGuid botGuid = await world.Host.PlayerStateAsync("Initbuffer", p => p.Guid);
        ObjectGuid playerGuid = await world.Host.PlayerStateAsync("Initquiet", p => p.Guid);
        var pair = new Pair(id, botGuid, playerGuid);
        PlayerbotSocialMemory memory = HelpTestKit.RecorderOf(world).Memory;

        // Two buffs the bot gave a stranger who never said a word: familiar enough, liked enough, but nothing came from their side.
        await world.Host.OnWorldAsync(() =>
        {
            for (int i = 0; i < 2; i++) HelpTestKit.RecorderOf(world).Note(botGuid, playerGuid, PlayerbotSocialEvent.HelpedThem);
        });
        Assert.True(memory.TryGet(id, playerGuid.Value, out PlayerbotRelation relation));
        Assert.True(relation.Familiarity >= 2 && memory.EffectiveAffinity(relation) >= PlayerbotInitiativeRules.WhisperAffinity);
        Assert.Equal(PlayerbotRelationFlags.None, relation.Flags & PlayerbotRelationFlags.Interacted);
        long now = GreetingTime(pair);
        await PassAsync(world, now);
        Assert.Equal(0, Lane(world).Openings); // no whisper and no greeting
        Assert.Equal(0, Lane(world).Whispers);

        // One word back from the player (an exchange) and the same relation may be greeted, by a whisper.
        await world.Host.OnWorldAsync(() => HelpTestKit.RecorderOf(world).NoteChatLine(id, botGuid, playerGuid, "hi"));
        Assert.True(memory.TryGet(id, playerGuid.Value, out PlayerbotRelation talked));
        Assert.NotEqual(PlayerbotRelationFlags.None, talked.Flags & PlayerbotRelationFlags.Interacted);
        await PassAsync(world, GreetingTime(pair, now + PlayerbotInitiativeLane.ArrivalGapMs + 60_000)); // a fresh arrival
        Assert.Equal(1, Lane(world).Openings);
        Assert.Equal(1, Lane(world).Whispers);
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
        ObjectGuid rareGuid = await world.Host.OnWorldAsync(() => HelpTestKit.SpawnRare(HelpTestKit.Online(world, "Initrare"), 10f).Guid);
        // The spawn reaches the bot's view on a later map update; a pass before that finds nothing to remark on.
        await world.Host.WaitForWorldAsync(() => HelpTestKit.Online(world, "Initrare").VisibleObjects.Contains(rareGuid), "the rare creature to be in sight");
        // The roll below is made with mood 0; a real mood (a level, an item) would shift the chance the pass uses, so the test has none.
        await world.Host.OnWorldAsync(() => world.Options.Social.Mood.Enabled = false);
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

    [Fact]
    public async Task AnEnemyRaid_IsRemarkedOn_OnlyWhenFourRealPlayersOfTheOtherFactionAreAround_ManagedBotsDoNotCount()
    {
        await using SocialWorld world = await StartAsync();
        await using WorldTestClient listener = await world.AddPlayerAsync("Raidlisten");
        Guid bot = await world.AddBotAsync("Raidwatch");
        await using WorldTestClient orcOne = await world.AddPlayerAsync("Raidorca", race: 2);
        await using WorldTestClient orcTwo = await world.AddPlayerAsync("Raidorcb", race: 2);
        await using WorldTestClient orcThree = await world.AddPlayerAsync("Raidorcc", race: 2);
        await world.AddBotAsync("Raidorcbot", race: 2); // the fourth enemy is a managed bot: not a raid of players
        await HelpTestKit.StandAsync(world, "Raidwatch", ("Raidlisten", 5f), ("Raidorca", 10f), ("Raidorcb", 15f), ("Raidorcc", 20f), ("Raidorcbot", 25f));
        ObjectGuid botGuid = await world.Host.PlayerStateAsync("Raidwatch", p => p.Guid);
        uint zone = await world.Host.PlayerStateAsync("Raidwatch", p => p.ZoneId);
        PlayerbotTraits traits = PlayerbotTraits.For(botGuid.Value);
        long Roll(long after) => HelpTestKit.NowWhere(t => t > after && PlayerbotInitiativeRules.Rolls(botGuid.Value, zone, t, PlayerbotInitiativeRules.CommentSalt, PlayerbotInitiativeRules.CommentChance(traits.Chattiness, 0f)));

        long first = Roll(0);
        await PassAsync(world, first);
        Assert.Equal(0, Lane(world).Comments); // three players and a bot

        await using WorldTestClient orcFour = await world.AddPlayerAsync("Raidorcd", race: 2);
        await HelpTestKit.StandAsync(world, "Raidwatch", ("Raidorcd", 30f));
        await PassAsync(world, Roll(first + 30_000));
        Assert.Equal(1, Lane(world).Comments);
        _ = bot;
    }

    [Fact]
    public async Task AFirstMount_IsRemarkedOn_OnlyForAPlayerSeenOnFootFirst()
    {
        await using SocialWorld world = await StartAsync();
        await using WorldTestClient rider = await world.AddPlayerAsync("Mountrider");
        Guid bot = await world.AddBotAsync("Mountwatch");
        await HelpTestKit.StandAsync(world, "Mountwatch", ("Mountrider", 5f));
        ObjectGuid botGuid = await world.Host.PlayerStateAsync("Mountwatch", p => p.Guid);
        uint zone = await world.Host.PlayerStateAsync("Mountwatch", p => p.ZoneId);
        PlayerbotTraits traits = PlayerbotTraits.For(botGuid.Value);
        long Roll(long after) => HelpTestKit.NowWhere(t => t > after && PlayerbotInitiativeRules.Rolls(botGuid.Value, zone, t, PlayerbotInitiativeRules.CommentSalt, PlayerbotInitiativeRules.CommentChance(traits.Chattiness, 0f)));
        Task SetMountAsync(uint display) => world.Host.OnWorldAsync(() => HelpTestKit.Online(world, "Mountrider").SetUInt32(UpdateFields.UnitFieldMountdisplayid, display));
        await world.Host.OnWorldAsync(() => world.Host.WorldServices.GetRequiredService<ProgressionFeature>().Progression.GiveLevel(HelpTestKit.Online(world, "Mountrider"), 40));

        // Mounted from the first sighting: the bot never saw them on foot, so there is no first mount to congratulate.
        await SetMountAsync(1);
        long first = Roll(0);
        await PassAsync(world, first);
        Assert.Equal(0, Lane(world).Comments);

        // On foot, seen; then mounted at a time whose comment roll says yes.
        await SetMountAsync(0);
        await PassAsync(world, first + 30_000);
        await SetMountAsync(1);
        await PassAsync(world, Roll(first + 60_000));
        Assert.Equal(1, Lane(world).Comments);
        _ = bot;
    }

    /// <summary>A world whose chat client is the one handed in (so a test can hold the model's answer back), and whose model may phrase an announcement (the opt-in is not required).</summary>
    private static Task<SocialWorld> StartWithClientAsync(FakeBotChatClient client, Action<PlayerbotOptions>? configure = null) => SocialWorld.StartAsync(options =>
    {
        options.Chat.Enabled = true;
        options.Chat.PerPlayerCooldownSeconds = 0;
        options.Chat.Safety.RequireOptIn = false;
        ChatSamples.NoScreeningDeadline(options.Chat);
        options.Chat.Providers = [new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.Anthropic, ApiKeyEnvironmentVariable = "TEST_INITIATIVE_KEY" }];
        configure?.Invoke(options);
    }, frozen: true, services: services =>
    {
        services.AddSingleton<IBotChatClient>(client);
        services.AddSingleton<IBotChatEnvironment>(new FakeBotChatEnvironment(new() { ["TEST_INITIATIVE_KEY"] = "sk-initiative" }));
    });

    [Fact]
    public async Task ASlowModel_KeepsTheSecondBotFromOpeningToThePlayer_UntilTheFirstLineIsSaid()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeBotChatClient();
        client.Default = async (_, _) =>
        {
            await gate.Task;
            return new BotChatResult(BotChatOutcome.Ok, "{\"reply\": \"hello there\", \"intent\": \"none\"}", 100, 10);
        };
        await using SocialWorld world = await StartWithClientAsync(client);
        await using WorldTestClient player = await world.AddPlayerAsync("Initslowp");
        Pair first = await FamiliarAsync(world, "Initslowa", "Initslowp");
        long now = GreetingTime(first);
        await PassAsync(world, now);
        Assert.Equal(1, Lane(world).Openings);
        await HelpTestKit.UntilAsync(() => !client.Calls.IsEmpty, "the model to be asked for the first line");

        // The model has not answered: a second bot that has just met the player rolls yes far past the short bot gap, and must still not open to them.
        Pair second = await FamiliarAsync(world, "Initslowb", "Initslowp");
        long later = GreetingTime(second, now + PlayerbotInitiativeLane.BotGapMs + 5_000);
        await PassAsync(world, later);
        Assert.Equal(1, Lane(world).Openings); // the second bot opened while the first line was still being phrased

        gate.SetResult();
        await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() => Lane(world).PendingCount).Result == 1, "the first greeting to be said");
        await PassAsync(world, later + 60_000);
        Assert.Equal(1, Lane(world).Openings); // said: the player's cooldown holds
    }

    [Fact]
    public async Task ALineTheSpeechPathRefusesAfterTheModelAnswered_ReleasesThePlayer_SoAnotherBotMayOpen()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeBotChatClient();
        client.Default = async (_, _) =>
        {
            await gate.Task;
            return new BotChatResult(BotChatOutcome.Ok, "{\"reply\": \"hello there\", \"intent\": \"none\"}", 100, 10);
        };
        await using SocialWorld world = await StartWithClientAsync(client, options => options.Chat.Speech.LocalLinesPerMinute = 0);
        await using WorldTestClient player = await world.AddPlayerAsync("Initfailp");
        Pair first = await FamiliarAsync(world, "Initfaila", "Initfailp");
        long now = GreetingTime(first);
        await PassAsync(world, now);
        Assert.Equal(1, Lane(world).Openings);
        await HelpTestKit.UntilAsync(() => !client.Calls.IsEmpty, "the model to be asked for the first line");
        long held = await world.Host.OnWorldAsync(() => Lane(world).NextOpeningFor(first.PlayerGuid));
        Assert.True(held > now + PlayerbotInitiativeLane.BotGapMs, "held while the line is phrased");

        // The model answers and the limiter refuses the line: nothing will be said, so the hold is given back at once.
        gate.SetResult();
        await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() => Lane(world).NextOpeningFor(first.PlayerGuid)).Result <= now, "the refused line to release the player");
        Pair second = await FamiliarAsync(world, "Initfailb", "Initfailp");
        long later = GreetingTime(second, now + PlayerbotInitiativeLane.BotGapMs + 5_000);
        await PassAsync(world, later);
        Assert.Equal(2, Lane(world).Openings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AModelThatGivesNothingOrThrows_ReleasesThePlayer_SoTheWorkerFailureDoesNotHoldItForTenMinutes(bool throws)
    {
        // The chain ends in the built-in template, so a model that merely fails still gets its opening said. The worker gives up on the line only when a
        // model's moderation step flags it (the null path) or the client faults with an exception the request path does not translate (the catch path).
        var client = new FakeBotChatClient();
        if (throws) client.Default = (_, _) => throw new FormatException("the provider blew up");
        else client.Moderation = _ => new BotChatModeration(BotChatModerationVerdict.Flagged);
        await using SocialWorld world = await StartWithClientAsync(client, options =>
        {
            if (throws) return;
            options.Chat.Providers[0].Moderation = PlayerbotChatModerationKind.Endpoint;
            options.Chat.Providers[0].ModerationBaseUrl = "https://moderation.invalid/v1";
        });
        string suffix = throws ? "t" : "n";
        await using WorldTestClient player = await world.AddPlayerAsync("Initnone" + suffix);
        Pair first = await FamiliarAsync(world, "Initnonea" + suffix, "Initnone" + suffix);
        long now = GreetingTime(first);
        await PassAsync(world, now);
        Assert.Equal(1, Lane(world).Openings);

        // The worker gave up on the line: the speech's Dropped callback must reach the lane on a world-thread drain, which gives the hold back.
        await HelpTestKit.UntilAsync(() => throws ? !client.Calls.IsEmpty : !client.Moderations.IsEmpty, "the worker to reach the provider");
        await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() => Lane(world).NextOpeningFor(first.PlayerGuid)).Result <= now, "the failed line to release the player");
    }

    [Fact]
    public async Task AnOpeningThatWasNeverSaid_IsNotWaitedFor_AndNeverPenalisesOrBacksOffThePlayer()
    {
        // The speech limiter refuses every local line: the opening is queued and the model phrases it, but it is never said.
        await using SocialWorld world = await StartAsync(options => options.Chat.Speech.LocalLinesPerMinute = 0);
        await using WorldTestClient player = await world.AddPlayerAsync("Initmute");
        Pair pair = await FamiliarAsync(world, "Initmuted", "Initmute");
        PlayerbotSocialMemory memory = HelpTestKit.RecorderOf(world).Memory;
        Assert.True(memory.TryGet(pair.Bot, pair.PlayerGuid.Value, out PlayerbotRelation before));
        long now = GreetingTime(pair);
        await PassAsync(world, now);
        Assert.Equal(1, Lane(world).Openings);
        await Task.Delay(1500); // nothing to observe: a refused line leaves no trace; this is only long enough for the queued line to reach the speech path and be refused there
        // Nothing was said, so the player's cooldown is only the short hold that keeps a second bot from piling on, not the 30 minutes of a real opening.
        Assert.True(Lane(world).NextOpeningFor(pair.PlayerGuid) <= now + PlayerbotInitiativeLane.BotGapMs, "the cooldown of an unsaid opening");

        // The player says something: it is not the answer to a line nobody heard.
        await player.SendChatAsync(ChatType.Say, Language.Common, "no thanks");
        await Task.Delay(500); // nothing to observe: a say nobody waits for is not queued (so no counter moves); the delay only gives the server time to read it
        await PassAsync(world, now + 1_000);
        Assert.False(Lane(world).PendingFor(pair.Bot, pair.PlayerGuid));
        Assert.Equal(0, Lane(world).BackoffOf(pair.PlayerGuid));

        await PassAsync(world, now + PlayerbotInitiativeLane.ReplyWaitMs + 1_000);
        Assert.Equal(0, Lane(world).Ignored);
        Assert.True(memory.TryGet(pair.Bot, pair.PlayerGuid.Value, out PlayerbotRelation after));
        Assert.Equal(before.Affinity, after.Affinity);
        Assert.Equal(0, Lane(world).BackoffOf(pair.PlayerGuid));
    }

    [Fact]
    public async Task ALineThatNamesAnotherCharacter_IsNotTheAnswer_TheNextPlainOneIs()
    {
        await using SocialWorld world = await StartAsync();
        await using WorldTestClient player = await world.AddPlayerAsync("Initnamer");
        await using WorldTestClient other = await world.AddPlayerAsync("Initfriendb");
        Pair pair = await FamiliarAsync(world, "Initnamed", "Initnamer");
        await HelpTestKit.StandAsync(world, "Initnamed", ("Initfriendb", 8f)); // the named character stands within earshot, in the bot's sight
        long now = 10_000;
        await world.Host.OnWorldAsync(() => Lane(world).Expect(HelpTestKit.RecorderOf(world), pair.Bot, pair.BotGuid, pair.PlayerGuid, PlayerbotOpeningKind.Greeting, 0, 0, now));

        int heard = HelpTestKit.RecorderOf(world).RepliesHeard;
        await player.SendChatAsync(ChatType.Say, Language.Common, "hey Initfriendb no thanks");
        await HelpTestKit.UntilAsync(() => HelpTestKit.RecorderOf(world).RepliesHeard > heard, "the say to be heard");
        await PassAsync(world, now + 1_000);
        Assert.True(Lane(world).PendingFor(pair.Bot, pair.PlayerGuid)); // still waiting
        Assert.Equal(0, Lane(world).BackoffOf(pair.PlayerGuid));

        await player.SendChatAsync(ChatType.Say, Language.Common, "hello there");
        await AnswerTakenAsync(world, pair, now + 2_000);
        Assert.Equal(0, Lane(world).BackoffOf(pair.PlayerGuid)); // a friendly answer
    }

    [Fact]
    public async Task APendingOpeningsReply_IsNeverDropped_WhateverTheSharedQueueHolds()
    {
        await using SocialWorld world = await StartAsync();
        await using WorldTestClient player = await world.AddPlayerAsync("Initbusy");
        Pair pair = await FamiliarAsync(world, "Initbusybot", "Initbusy");
        PlayerbotSocialRecorder recorder = HelpTestKit.RecorderOf(world);
        long now = 10_000;
        await world.Host.OnWorldAsync(() =>
        {
            // The shared reply queue is full of other characters' lines (its cap is 256).
            for (int i = 0; i < 400; i++) recorder.NoteChatLine(pair.Bot, pair.BotGuid, ObjectGuid.Player((uint)(5_000 + i)), "hello");
            Lane(world).Expect(recorder, pair.Bot, pair.BotGuid, pair.PlayerGuid, PlayerbotOpeningKind.Greeting, 0, 0, now);
        });

        int heard = HelpTestKit.RecorderOf(world).RepliesHeard;
        await player.SendChatAsync(ChatType.Say, Language.Common, "no thanks");
        await HelpTestKit.UntilAsync(() => HelpTestKit.RecorderOf(world).RepliesHeard > heard, "the reply to be heard");
        // The lane drains 32 lines a pass: a few passes later it has reached the reply, which was kept although the queue was full.
        for (int i = 0; i < 20 && Lane(world).PendingFor(pair.Bot, pair.PlayerGuid); i++) await PassAsync(world, now + 1_000 + i * 100);

        Assert.False(Lane(world).PendingFor(pair.Bot, pair.PlayerGuid));
        Assert.Equal(1, Lane(world).BackoffOf(pair.PlayerGuid)); // answered ("no thanks"), so a back-off but not unanswered
        await PassAsync(world, now + PlayerbotInitiativeLane.ReplyWaitMs + 1_000);
        Assert.Equal(0, Lane(world).Ignored);
    }

    [Fact]
    public async Task AnOnlineCharacterNamedLikeAReply_FarAway_OrBeside_NeverSwallowsTheAnswer()
    {
        await using SocialWorld world = await StartAsync();
        await using WorldTestClient player = await world.AddPlayerAsync("Initsayer");
        await using WorldTestClient namesake = await world.AddPlayerAsync("Sure"); // a character called like a yes
        Pair pair = await FamiliarAsync(world, "Initnamesake", "Initsayer");
        long now = 10_000;

        // Case 1: the namesake is 400 yards away, nowhere near the conversation.
        await world.Host.OnWorldAsync(() =>
        {
            Player bot = HelpTestKit.Online(world, "Initnamesake");
            HelpTestKit.Online(world, "Sure").Relocate(bot.X + 400f, bot.Y, bot.Z, 0f, world.Host.World.NowMs);
            Lane(world).Expect(HelpTestKit.RecorderOf(world), pair.Bot, pair.BotGuid, pair.PlayerGuid, PlayerbotOpeningKind.Objective, 1234, 77, now);
        });
        await player.SendChatAsync(ChatType.Say, Language.Common, "sure");
        await AnswerTakenAsync(world, pair, now + 1_000);
        Assert.Equal(1, Lane(world).Accepted); // fails today: NamesAnother finds the online character "Sure" anywhere in the realm
        Assert.False(Lane(world).PendingFor(pair.Bot, pair.PlayerGuid));

        // Case 2: the namesake stands beside the bot: "sure" is still a reply word, never a name.
        await world.Host.OnWorldAsync(() =>
        {
            Player bot = HelpTestKit.Online(world, "Initnamesake");
            HelpTestKit.Online(world, "Sure").Relocate(bot.X + 3f, bot.Y, bot.Z, 0f, world.Host.World.NowMs);
        });
        await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() => HelpTestKit.Online(world, "Initnamesake").VisibleObjects.Contains(HelpTestKit.Online(world, "Sure").Guid)).Result, "the namesake to be in sight");
        await world.Host.OnWorldAsync(() => Lane(world).Expect(HelpTestKit.RecorderOf(world), pair.Bot, pair.BotGuid, pair.PlayerGuid, PlayerbotOpeningKind.Objective, 1234, 77, now + 2_000));
        await player.SendChatAsync(ChatType.Say, Language.Common, "sure");
        await AnswerTakenAsync(world, pair, now + 3_000);
        Assert.Equal(2, Lane(world).Accepted);
        Assert.False(Lane(world).PendingFor(pair.Bot, pair.PlayerGuid));
    }

    [Fact]
    public async Task ASecondSay_WhileTheFirstIsStillQueued_IsNotLost()
    {
        await using SocialWorld world = await StartAsync();
        await using WorldTestClient player = await world.AddPlayerAsync("Initdouble");
        await using WorldTestClient other = await world.AddPlayerAsync("Initfriendc");
        Pair pair = await FamiliarAsync(world, "Initdblbot", "Initdouble");
        await HelpTestKit.StandAsync(world, "Initdblbot", ("Initfriendc", 4f));
        PlayerbotSocialRecorder recorder = HelpTestKit.RecorderOf(world);
        long now = 10_000;
        await world.Host.OnWorldAsync(() => Lane(world).Expect(recorder, pair.Bot, pair.BotGuid, pair.PlayerGuid, PlayerbotOpeningKind.Objective, 1234, 77, now));

        // Two lines before the lane reads the first: one for somebody else, then the answer.
        int before = recorder.RepliesHeard;
        await player.SendChatAsync(ChatType.Say, Language.Common, "hey Initfriendc");
        await player.SendChatAsync(ChatType.Say, Language.Common, "sure");
        await HelpTestKit.UntilAsync(() => recorder.RepliesHeard >= before + 2, "both lines to be heard");

        await AnswerTakenAsync(world, pair, now + 1_000);
        Assert.Equal(1, Lane(world).Accepted); // the second line was dropped before: the lane then waited for an answer that never came
    }
}
