using ArcaneCore.Game;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Tests.Playerbots.Chat;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// Guild chat in a real world: a bot guild with a real player who listens, the topics the feature queues, the lines the bot chat
/// service writes, and the limits on them (messages per hour, the gap between one bot's lines, the listener requirement, the switches),
/// with the safety screening and RequireOptIn applied. The lines are asserted as the real client receives them (SMSG_MESSAGECHAT, guild
/// type) and as the bots dispatched them (CMSG_MESSAGECHAT). The feature's own passes are frozen and a test drives a
/// <see cref="PlayerbotGuildChat"/> with hand-built passes at a world time it picks.
/// </summary>
public sealed class PlayerbotGuildChatWorldTests
{
    private const string GuildName = "Quiet Owls";
    private const string Secret = "sk-guild-SECRET";

    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs) => pairs.ToDictionary(pair => pair.Key, pair => pair.Value);

    private static PlayerbotGuildTopic Welcome(string who, long queuedMs, string? sentence = null)
        => new("guild.welcome", sentence ?? $"{who} joined the guild {GuildName}.", $"Welcome {who}, who has just joined your guild {GuildName}.",
            Values(("guild", GuildName), ("who", who)), queuedMs);

    private static PlayerbotGuildTopic Chatter(long queuedMs)
        => new("guild.chatter", "Some small talk in the guild chat.", "Say one short, friendly line of small talk about what you are doing in the game.",
            Values(("guild", GuildName)), queuedMs);

    private sealed class Rig : IAsyncDisposable
    {
        private Rig(SocialWorld world, Guild guild, FakeBotChatClient client)
        {
            World = world;
            Guild = guild;
            Client = client;
        }

        public SocialWorld World { get; }

        public Guild Guild { get; }

        public FakeBotChatClient Client { get; }

        public WorldTestClient? Listener { get; private set; }

        public PlayerbotGuildChat Chat { get; } = new(NullLogger.Instance);

        public PlayerbotSocialTimings Timings { get; } = new() { SpeakerGapMs = 0, IdleChatter = false };

        /// <summary>CMSG_MESSAGECHAT dispatched by the bots: in this rig, all of it is guild chat.</summary>
        public int Lines => World.Log.Count(WorldOpcode.CmsgMessagechat);

        public PlayerbotChat BotChat => PartyTestHostChat(World);

        private static PlayerbotChat PartyTestHostChat(SocialWorld world) => PartyTestHost.Feature(world.Host).Chat!;

        /// <summary>Two bots in a guild (and, with <paramref name="listener"/>, a real player in it) on a frozen social feature.</summary>
        public static async Task<Rig> StartAsync(Action<PlayerbotOptions>? configure = null, bool listener = true)
        {
            var client = new FakeBotChatClient();
            SocialWorld world = await SocialWorld.StartAsync(options =>
            {
                options.Chat.Enabled = true;
                options.Social.Guilds.Enabled = false;
                // These tests are about the guild-chat feature's own limits (messages per hour, the speaker gap, the switches). The
                // speech limiter (5af206c8) has its own and would hide them: the model fake says the same line every time (dedupe), and
                // the bot gap, the per-minute caps and the guild's lines-per-minute budget are wall-clock while the passes here run in
                // simulated time. Its limits are covered by PlayerbotSpeechTests.
                options.Chat.Speech.DedupeSeconds = 0;
                options.Chat.Speech.BotGapSeconds = 0;
                options.Chat.Speech.TargetGapSeconds = 0;
                options.Chat.Speech.BotLinesPerMinute = 60;
                options.Chat.Speech.GuildLinesPerMinute = 120;
                configure?.Invoke(options);
            }, frozen: true, services: services =>
            {
                services.AddSingleton<IBotChatClient>(client);
                services.AddSingleton<IBotChatEnvironment>(new FakeBotChatEnvironment(new() { ["TEST_CHAT_KEY"] = Secret }));
            });
            try
            {
                await world.AddBotAsync("Quilla");
                await world.AddBotAsync("Quillb");
                Guild guild = await world.FoundGuildAsync("Quilla", GuildName);
                await world.BotJoinsAsync("Quilla", "Quillb", guild);
                var rig = new Rig(world, guild, client);
                if (listener) await rig.JoinListenerAsync();
                return rig;
            }
            catch
            {
                await world.DisposeAsync();
                throw;
            }
        }

        public async Task JoinListenerAsync()
        {
            Listener = await World.AddPlayerAsync("Listener");
            await World.PlayerJoinsAsync("Quilla", "Listener", Listener, Guild);
        }

        public Task QueueAsync(PlayerbotGuildTopic topic) => World.Host.OnWorldAsync(() => Chat.Queue(Guild.Id, topic));

        public Task SpeakAsync(long nowMs) => World.Host.OnWorldAsync(() => Chat.Speak(SocialTestSupport.BotPass(World.Host, nowMs, Timings)));

        public Task<int> WaitingAsync() => World.Host.OnWorldAsync(() => Chat.Waiting(Guild.Id));

        public Task WaitForLinesAsync(int count) => WorldTestHost.WaitForAsync(() => Lines >= count, $"{count} guild line(s) dispatched");

        /// <summary>The guild lines the listener receives: at least <paramref name="atLeast"/>, then whatever else arrives in the next moments.</summary>
        public async Task<List<ChatMessage>> GuildLinesAsync(int atLeast)
        {
            var lines = new List<ChatMessage>();
            while (lines.Count < atLeast)
            {
                ChatMessage line = await Listener!.ReadChatAsync();
                if (line.Type == ChatType.Guild) lines.Add(line);
            }

            foreach ((WorldOpcode opcode, byte[] payload) in await Listener!.CollectAsync(TimeSpan.FromMilliseconds(300)))
                if (opcode == WorldOpcode.SmsgMessagechat && ChatMessage.Parse(payload) is { Type: ChatType.Guild } line)
                    lines.Add(line);
            return lines;
        }

        public ValueTask DisposeAsync() => World.DisposeAsync();
    }

    // --- the event source and the whole path ---------------------------------------------------------------------------

    [Fact]
    public async Task ARealPlayerJoining_IsWelcomedInGuildChat_ByTheBotsOfTheGuild()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(options =>
        {
            options.Chat.Enabled = true;
            options.Social.Guilds.Enabled = false;
            // The listener stands beside the bots: with emotes on, a bot greets it with a /say half the time (a CMSG_MESSAGECHAT that is no guild line).
            options.Social.Emotes.Enabled = false;
            options.Social.GuildChat.Enabled = false; // the bots' own joining is not remarked on
        });
        await world.AddBotAsync("Quilla");
        await world.AddBotAsync("Quillb");
        await SocialTestSupport.WaitPassesAsync(world.Host, 10); // the registry of bots is read
        Guild guild = await world.FoundGuildAsync("Quilla", GuildName);
        await world.BotJoinsAsync("Quilla", "Quillb", guild);
        WorldTestClient listener = await world.AddPlayerAsync("Listener");
        ulong quilla = (await world.Host.PlayerStateAsync("Quilla", p => p.Guid)).Value;
        ulong quillb = (await world.Host.PlayerStateAsync("Quillb", p => p.Guid)).Value;
        await world.Host.OnWorldAsync(() => world.Options.Social.GuildChat.Enabled = true);

        await world.PlayerJoinsAsync("Quilla", "Listener", listener, guild);

        ChatMessage welcome;
        while ((welcome = await listener.ReadChatAsync()).Type != ChatType.Guild) { }
        Assert.Contains(welcome.Sender, new[] { quilla, quillb });
        Assert.Contains("Listener", welcome.Text);
        Assert.Contains(PlayerbotSocialTemplates.Lines["guild.welcome"], line => line.Replace("{who}", "Listener").Replace("{guild}", GuildName) == welcome.Text);

        // One line, from one bot, once: the topic is used up and the bot speaks no more for its gap.
        await SocialTestSupport.WaitPassesAsync(world.Host, 10);
        Assert.Equal(1, world.Log.Count(WorldOpcode.CmsgMessagechat));
        Assert.DoesNotContain(await listener.CollectAsync(TimeSpan.FromMilliseconds(200)), p => p.Opcode == WorldOpcode.SmsgMessagechat);
    }

    // --- limits -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MessagesPerHour_CapsAGuildsLines_AndTheWindowFreesSlotsAnHourLater()
    {
        await using Rig rig = await Rig.StartAsync(options => options.Social.GuildChat.MessagesPerHour = 2);
        for (int i = 0; i < 4; i++) await rig.QueueAsync(Chatter(0));

        for (int pass = 1; pass <= 10; pass++) await rig.SpeakAsync(pass * 1000L);
        await rig.WaitForLinesAsync(2);
        await Task.Delay(300);

        Assert.Equal(2, rig.Lines); // not 4: the third and fourth wait for a free slot
        Assert.Equal(2, await rig.World.Host.OnWorldAsync(() => rig.Chat.UsedThisHour(rig.Guild.Id, 10_000)));
        Assert.Equal(2, await rig.WaitingAsync());
        Assert.Equal(2, (await rig.GuildLinesAsync(2)).Count);

        // Fifty minutes on, the first two slots are still in the window: a fresh topic is not said.
        await rig.QueueAsync(Chatter(3_000_000));
        await rig.SpeakAsync(3_000_000);
        await Task.Delay(300);
        Assert.Equal(2, rig.Lines);

        // An hour after the first slot (1 s), one frees: the new topic is said, the four old ones were dropped as stale.
        await rig.QueueAsync(Chatter(3_700_000));
        await rig.SpeakAsync(3_700_000);
        await rig.WaitForLinesAsync(3);
        Assert.Single(await rig.GuildLinesAsync(1));
        Assert.Equal(3, rig.Lines);
    }

    [Fact]
    public async Task ABotSpeaksOnceInItsGap_SoTwoBotsSayTwoLines_AndTheGapEndsLater()
    {
        await using Rig rig = await Rig.StartAsync(options => options.Social.GuildChat.MessagesPerHour = 10);
        rig.Timings.SpeakerGapMs = 600_000;
        for (int i = 0; i < 4; i++) await rig.QueueAsync(Chatter(0));

        for (int pass = 1; pass <= 5; pass++) await rig.SpeakAsync(pass * 1000L);
        await rig.WaitForLinesAsync(2);
        await Task.Delay(300);

        Assert.Equal(2, rig.Lines);
        List<ChatMessage> first = await rig.GuildLinesAsync(2);
        Assert.Equal(2, first.Count);
        Assert.Equal(2, first.Select(line => line.Sender).Distinct().Count()); // one line from each bot
        Assert.Equal(2, await rig.WaitingAsync());

        // 8 min on both bots are still inside their gap; a fresh topic waits...
        await rig.QueueAsync(Chatter(500_000));
        await rig.SpeakAsync(500_000);
        await Task.Delay(300);
        Assert.Equal(2, rig.Lines);
        Assert.Equal(1, await rig.WaitingAsync());

        // ...until the bot that spoke first is out of its gap (600 s after 1 s); the other (2 s) still is.
        await rig.SpeakAsync(601_500);
        await rig.WaitForLinesAsync(3);
        await Task.Delay(300);
        Assert.Equal(3, rig.Lines);
        Assert.Equal(first[0].Sender, Assert.Single(await rig.GuildLinesAsync(1)).Sender);
    }

    [Fact]
    public async Task NothingIsSaid_WithoutARealPlayerWhoCanReadGuildChat_ThenItIsSaid()
    {
        await using Rig rig = await Rig.StartAsync(listener: false);
        await rig.QueueAsync(Chatter(0));
        for (int pass = 1; pass <= 5; pass++) await rig.SpeakAsync(pass * 1000L);
        await Task.Delay(300);

        Assert.Equal(0, rig.Lines); // two bots alone in a guild talk to nobody
        Assert.Equal(1, await rig.WaitingAsync());

        await rig.JoinListenerAsync();
        await rig.SpeakAsync(6000);
        await rig.WaitForLinesAsync(1);
        Assert.Single(await rig.GuildLinesAsync(1));
    }

    [Fact]
    public async Task TheSwitches_SilenceGuildChat_AndEachOneIsTheCause()
    {
        await using Rig rig = await Rig.StartAsync(options => options.Social.GuildChat.MessagesPerHour = 10);
        long now = 1000;
        int said = 0;

        async Task OffThenOnAsync(string switchName, Action off, Action on)
        {
            await rig.World.Host.OnWorldAsync(off);
            await rig.QueueAsync(Chatter(now));
            await rig.SpeakAsync(now);
            await Task.Delay(300);
            Assert.True(rig.Lines == said, $"{switchName} off: a line was still said");
            Assert.Equal(0, await rig.WaitingAsync()); // the topic is not kept for later

            await rig.World.Host.OnWorldAsync(on);
            now += 1000;
            await rig.QueueAsync(Chatter(now));
            await rig.SpeakAsync(now);
            await rig.WaitForLinesAsync(++said);
            now += 1000;
        }

        PlayerbotOptions options = rig.World.Options;
        await OffThenOnAsync("Chat:Enabled", () => options.Chat.Enabled = false, () => options.Chat.Enabled = true);
        await OffThenOnAsync("Social:GuildChat:Enabled", () => options.Social.GuildChat.Enabled = false, () => options.Social.GuildChat.Enabled = true);
        await OffThenOnAsync("Social:GuildChat:MessagesPerHour", () => options.Social.GuildChat.MessagesPerHour = 0, () => options.Social.GuildChat.MessagesPerHour = 10);
        Assert.Equal(3, rig.Lines);
    }

    // --- safety screening ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFlaggedSentenceOrName_IsNeverSaid_AndTheNextCleanTopicIs()
    {
        await using Rig rig = await Rig.StartAsync();
        PlayerbotChat chat = rig.BotChat;

        // The plain sentence of what happened is screened like a player's line: this one never reaches a provider or the guild.
        await rig.QueueAsync(Welcome("Listener", 0, sentence: "kys joined the guild."));
        await rig.SpeakAsync(1000);
        await WorldTestHost.WaitForAsync(() => chat.Safety.Flags().Count == 1, "the input flag");
        BotChatFlag input = Assert.Single(chat.Safety.Flags());
        Assert.Equal((BotChatFlagSource.Local, "Harassment"), (input.Source, input.Category));

        // A clean sentence with a flagged name in the line: every built-in phrasing is screened before it is said (the name is "kys now": the filter
        // does not catch a term glued to a "!", so a bare "kys" would pass in one of the four phrasings).
        await rig.QueueAsync(Welcome("kys now", 2000, sentence: "A new member joined the guild."));
        await rig.SpeakAsync(2000);
        await WorldTestHost.WaitForAsync(() => chat.Safety.Flags().Count == 2, "the output flag");
        Assert.Equal(["Local/Harassment", "Output/Harassment"], chat.Safety.Flags().Select(flag => $"{flag.Source}/{flag.Category}").Order());
        await WorldTestHost.WaitForAsync(() => chat.Status().Dropped >= 2, "both announcements to be dropped");
        await Task.Delay(300);
        Assert.Equal(0, rig.Lines);
        Assert.Empty(await rig.GuildLinesAsync(0));

        // The control: the same path with a clean name says its line.
        await rig.QueueAsync(Welcome("Listener", 3000));
        await rig.SpeakAsync(3000);
        await rig.WaitForLinesAsync(1);
        ChatMessage line = Assert.Single(await rig.GuildLinesAsync(1));
        Assert.Contains("Listener", line.Text);
        Assert.Equal(2, chat.Safety.Flags().Count);
    }

    [Fact]
    public async Task UnderRequireOptIn_TheGuildLineIsABuiltinOne_AndWithoutItTheModelWritesIt()
    {
        PlayerbotChatProviderOptions[] model = [new() { Kind = PlayerbotChatProviderKind.Anthropic, ApiKeyEnvironmentVariable = "TEST_CHAT_KEY" }];
        await using Rig rig = await Rig.StartAsync(options =>
        {
            options.Chat.Providers = model;
            options.Chat.Safety.RequireOptIn = true;
        });

        await rig.QueueAsync(Welcome("Listener", 0));
        await rig.SpeakAsync(1000);
        await rig.WaitForLinesAsync(1);
        ChatMessage builtin = Assert.Single(await rig.GuildLinesAsync(1));
        Assert.Empty(rig.Client.Calls); // nobody has opted in to an announcement: the provider is never asked
        Assert.Contains(PlayerbotSocialTemplates.Lines["guild.welcome"], text => text.Replace("{who}", "Listener").Replace("{guild}", GuildName) == builtin.Text);

        await rig.World.Host.OnWorldAsync(() => rig.World.Options.Chat.Safety.RequireOptIn = false);
        await rig.QueueAsync(Welcome("Listener", 2000));
        await rig.SpeakAsync(2000);
        await rig.WaitForLinesAsync(2);
        ChatMessage written = Assert.Single(await rig.GuildLinesAsync(1));
        Assert.Equal("fake reply", written.Text);
        var call = Assert.Single(rig.Client.Calls);
        Assert.Equal(Secret, call.Key);
        Assert.Contains(GuildName, call.Prompt.DynamicSystem);
        Assert.Equal($"Welcome Listener, who has just joined your guild {GuildName}.", call.Prompt.Messages[^1].Text);
        Assert.False(call.OnWorldThread);
    }

    // --- delivery -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADeliveredGuildLine_IsSaidOnlyInTheGuildItWasWrittenFor_AndOnlyWhileGuilded()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.World.AddBotAsync("Quillc"); // a bot in no guild
        Guid quilla = rig.World.Bots["Quilla"];
        Guid quillc = rig.World.Bots["Quillc"];

        await rig.World.Host.OnWorldAsync(() =>
        {
            BotChatReply reply(Guid bot, string name, string text, string? guild)
                => new(bot, rig.World.Host.World.FindOnlinePlayer(name)!.Guid, name, ChatType.Guild, text, null, false, "test", guild);
            ManagedPlayerbotFeature bots = PartyTestHost.Feature(rig.World.Host);

            // Addressed to another guild: said nowhere. A bot with no guild: said nowhere. Then the right guild: said, and it is the first line heard.
            bots.FindPartyAI(quilla)!.DeliverChat(rig.World.Host.World.FindOnlinePlayer("Quilla")!, reply(quilla, "Quilla", "wrong guild", "Other Guild"));
            bots.FindPartyAI(quillc)!.DeliverChat(rig.World.Host.World.FindOnlinePlayer("Quillc")!, reply(quillc, "Quillc", "no guild", GuildName));
            bots.FindPartyAI(quilla)!.DeliverChat(rig.World.Host.World.FindOnlinePlayer("Quilla")!, reply(quilla, "Quilla", "right guild", GuildName.ToUpperInvariant()));
        });

        Assert.Equal(1, rig.World.Log.Count("Quilla", WorldOpcode.CmsgMessagechat));
        Assert.Equal(0, rig.World.Log.Count("Quillc", WorldOpcode.CmsgMessagechat));
        ChatMessage heard = Assert.Single(await rig.GuildLinesAsync(1));
        Assert.Equal("right guild", heard.Text);
        Assert.Equal((await rig.World.Host.PlayerStateAsync("Quilla", p => p.Guid)).Value, heard.Sender);
    }

    // --- boss kills ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSameBossKilledInTwoInstances_GivesTwoTopics_ButTheSameInstanceOnlyOnceInTheWindow()
    {
        await using Rig rig = await Rig.StartAsync();
        const ulong bossGuid = 0xF130_0000_0042_0001UL; // the guid is the same in every instance of the dungeon
        (bool First, bool SameInstanceAgain, bool OtherInstance, bool AfterWindow, int Waiting) result = await rig.World.Host.OnWorldAsync(() =>
        {
            PlayerbotSocialPass pass = SocialTestSupport.BotPass(rig.World.Host, 1_000, rig.Timings);
            Guild guild = rig.Guild;
            bool first = rig.Chat.QueueBoss(pass, guild, 7, bossGuid, "Edwin", "The Deadmines", "Quilla");
            bool again = rig.Chat.QueueBoss(pass, guild, 7, bossGuid, "Edwin", "The Deadmines", "Quilla");
            bool other = rig.Chat.QueueBoss(pass, guild, 8, bossGuid, "Edwin", "The Deadmines", "Quilla");
            int waiting = rig.Chat.Waiting(guild.Id);
            PlayerbotSocialPass later = SocialTestSupport.BotPass(rig.World.Host, 1_000 + PlayerbotGuildChat.BossSeenLifeMs, rig.Timings);
            bool afterWindow = rig.Chat.QueueBoss(later, guild, 7, bossGuid, "Edwin", "The Deadmines", "Quilla");
            return (first, again, other, afterWindow, waiting);
        });

        Assert.True(result.First);
        Assert.False(result.SameInstanceAgain);
        Assert.True(result.OtherInstance);
        Assert.Equal(2, result.Waiting);
        Assert.True(result.AfterWindow);
    }
}
