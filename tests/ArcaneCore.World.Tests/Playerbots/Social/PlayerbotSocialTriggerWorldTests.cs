using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Playerbots.Chat;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The social-memory triggers driven through the real paths of a running world (real handlers, socket clients, managed bots): a chat
/// strike, a trade, targeted emotes, a kick, a checkpoint and the GM command. Each asserts on the bot's <see cref="PlayerbotSocialMemory"/>
/// or on the relation store, so that a hook that is unplugged from its game path fails here and not only in the unit tests of the memory.
/// </summary>
public sealed class PlayerbotSocialTriggerWorldTests
{
    private static PlayerbotSocialMemory MemoryOf(WorldTestHost host) => PartyTestHost.Feature(host).Social.Memory;

    private static PlayerbotRelation? RelationOf(WorldTestHost host, Guid bot, ObjectGuid other)
        => MemoryOf(host).TryGet(bot, other.Value, out PlayerbotRelation relation) ? relation : null;

    /// <summary>Poll a condition the world thread keeps changing (memory has its own lock) until it holds.</summary>
    internal static async Task UntilAsync(Func<bool> condition, string what, int seconds = 15)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("timed out waiting for " + what);
            await Task.Delay(20);
        }
    }

    private static byte[] TextEmote(uint textEmote, ObjectGuid target)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt32(textEmote);
        writer.WriteUInt32(0); // the emote number
        writer.WriteUInt64(target.Value);
        return writer.ToArray();
    }

    /// <summary>A player stands beside the bot, sees it, and sends the targeted text emote through the real handler.</summary>
    private static async Task EmoteAtAsync(SocialWorld world, WorldTestClient player, string playerName, string bot, uint textEmote)
    {
        ObjectGuid botGuid = await world.Host.PlayerStateAsync(bot, p => p.Guid);
        await world.WhenNearAsync([bot], [playerName], () => { });
        await player.SendAsync(WorldOpcode.CmsgTextEmote, TextEmote(textEmote, botGuid));
    }

    // --- b) a chat safety strike ----------------------------------------------------------------------------------------------

    private static Task<SocialWorld> StartChatWorldAsync(FakeBotChatClient client) => SocialWorld.StartAsync(options =>
    {
        options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone;
        options.Chat.Enabled = true;
        options.Chat.PerPlayerCooldownSeconds = 0;
        ChatSamples.NoScreeningDeadline(options.Chat);
        options.Chat.Providers = [new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.Anthropic, ApiKeyEnvironmentVariable = "TEST_SOCIAL_KEY" }];
    }, frozen: true, services: services =>
    {
        services.AddSingleton<IBotChatClient>(client);
        services.AddSingleton<IBotChatEnvironment>(new FakeBotChatEnvironment(new() { ["TEST_SOCIAL_KEY"] = "sk-social-triggers" }));
    });

    private static async Task ReadWhisperAsync(WorldTestClient client, ulong bot)
    {
        while (true)
        {
            ChatMessage line = ChatMessage.Parse(await client.ReadUntilAsync(WorldOpcode.SmsgMessagechat));
            if (line.Type == ChatType.Whisper && line.Sender == bot) return;
        }
    }

    [Fact]
    public async Task AChatSafetyStrike_LowersTheBotsAffinityTowardThatPlayer_AndAFlaggedLineIsNotRememberedAsATopic()
    {
        var client = new FakeBotChatClient();
        await using SocialWorld world = await StartChatWorldAsync(client);
        Guid bot = await world.AddBotAsync("Strikebot");
        await using WorldTestClient player = await world.AddPlayerAsync("Strikeplayer");
        ObjectGuid playerGuid = await world.Host.PlayerStateAsync("Strikeplayer", p => p.Guid);
        ulong botGuid = (await world.Host.PlayerStateAsync("Strikebot", p => p.Guid)).Value;

        // A polite first whisper: one exchange, the chat gain, no strike.
        await player.SendChatAsync(ChatType.Whisper, Language.Common, "hello there", "Strikebot");
        await ReadWhisperAsync(player, botGuid);
        await UntilAsync(() => RelationOf(world.Host, bot, playerGuid) is not null, "the first exchange to be remembered");
        PlayerbotRelation polite = RelationOf(world.Host, bot, playerGuid)!;
        Assert.Equal((short)new PlayerbotSocialMemoryOptions().AffinityPerChat, polite.Affinity);

        // The local filter flags this one as harassment: a strike, applied through the chat service's OnStrike seam.
        await player.SendChatAsync(ChatType.Whisper, Language.Common, "kys", "Strikebot");
        await ReadWhisperAsync(player, botGuid);
        await UntilAsync(() => RelationOf(world.Host, bot, playerGuid)!.Affinity < polite.Affinity, "the strike to lower the affinity");

        PlayerbotRelation struck = RelationOf(world.Host, bot, playerGuid)!;
        int strike = new PlayerbotSocialMemoryOptions().AffinityPerStrike;
        // The flagged line is an exchange too, but the chat gain is held by the shared window: the strike is the only change.
        Assert.Equal((short)(polite.Affinity + strike), struck.Affinity);
        Assert.False(struck.OtherIsBot);
        Assert.DoesNotContain("kys", struck.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kys", struck.LastTopic, StringComparison.OrdinalIgnoreCase);
    }

    // --- c) a completed trade -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACompletedTrade_RecordsTradeCompleted_AndACancelledOneRecordsNothing()
    {
        // A trade counts for the bot's memory only when it gave something or was given at least 100 copper of vendor value (P4 S0).
        const uint giftEntry = 995_101;
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ArcaneCore.Kernel.Items.ItemTemplate { Entry = giftEntry, Class = 15, SubClass = 0, Name = "Trigger Gift", DisplayId = 1, Quality = 1, SellPrice = 500, Stackable = 1 });
        SocialWorld world;
        using (items.Use())
        {
            world = await SocialWorld.StartAsync(
                options => options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone, frozen: true,
                services: services => services.AddScoped<ArcaneCore.Kernel.Economy.IEconomyStore>(_ => new PlayerbotPartyTests.StubEconomyStore()));
        }

        await using SocialWorld _ = world;
        Guid bot = await world.AddBotAsync("Tradebot");
        await using WorldTestClient master = await world.AddPlayerAsync("Tradeplayer");
        ObjectGuid masterGuid = await world.Host.PlayerStateAsync("Tradeplayer", p => p.Guid);
        ObjectGuid botGuid = await world.Host.PlayerStateAsync("Tradebot", p => p.Guid);

        // The bot trades with its master and groupmates: the player invites it and the party AI takes over.
        await master.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString("Tradebot"));
        await world.Host.WaitForWorldAsync(() => PartyTestHost.Feature(world.Host).IsPartyDriven(bot), "the party AI drives the bot");
        await world.Host.OnWorldAsync(() =>
        {
            Player botPlayer = world.Host.World.FindOnlinePlayer("Tradebot")!;
            Player masterPlayer = world.Host.World.FindOnlinePlayer("Tradeplayer")!;
            PlayerbotMovementControl.Stop(PartyTestHost.Feature(world.Host).FindSession(bot)!, botPlayer);
            botPlayer.Relocate(masterPlayer.X + 2f, masterPlayer.Y, masterPlayer.Z, 0f, world.Host.World.NowMs);
            botPlayer.Inventory.Load([]);
            masterPlayer.Inventory.Load([]);
        });

        var initiate = new PacketWriter(8);
        initiate.WriteUInt64(botGuid.Value);

        // Begun, then cancelled by the player: nothing was traded, nothing is remembered.
        await master.SendAsync(WorldOpcode.CmsgInitiateTrade, initiate.ToArray());
        Assert.Equal((uint)TradeStatus.OpenWindow, BitConverter.ToUInt32(await master.ReadUntilAsync(WorldOpcode.SmsgTradeStatus), 0)); // the window opened: the bot saw BeginTrade
        await master.SendAsync(WorldOpcode.CmsgCancelTrade, []);
        uint seen;
        do seen = BitConverter.ToUInt32(await master.ReadUntilAsync(WorldOpcode.SmsgTradeStatus), 0);
        while (seen != (uint)TradeStatus.TradeCanceled);
        Assert.Null(RelationOf(world.Host, bot, masterGuid));

        // Begun and accepted by both: the bot sees BeginTrade (status 1) and then Complete (status 8).
        await master.SendAsync(WorldOpcode.CmsgInitiateTrade, initiate.ToArray());
        Assert.Equal((uint)TradeStatus.OpenWindow, BitConverter.ToUInt32(await master.ReadUntilAsync(WorldOpcode.SmsgTradeStatus), 0));
        byte[] offer = await world.Host.OnWorldAsync(() =>
        {
            Player owner = world.Host.World.FindOnlinePlayer("Tradeplayer")!;
            Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, owner.Inventory.AddItem(giftEntry, 1, out ArcaneCore.Game.Items.Item? gift));
            return new byte[] { 0, gift!.BagSlot, gift.Slot };
        });
        await master.SendAsync(WorldOpcode.CmsgSetTradeItem, offer);
        await Task.Delay(1100); // an accept right after a change is bounced by scam prevention
        await master.SendAsync(WorldOpcode.CmsgAcceptTrade, BitConverter.GetBytes(1u));
        uint last;
        do last = BitConverter.ToUInt32(await master.ReadUntilAsync(WorldOpcode.SmsgTradeStatus), 0);
        while (last != (uint)TradeStatus.TradeComplete);

        await UntilAsync(() => RelationOf(world.Host, bot, masterGuid) is not null, "the completed trade to be remembered");
        PlayerbotRelation relation = RelationOf(world.Host, bot, masterGuid)!;
        Assert.Equal((short)new PlayerbotSocialMemoryOptions().AffinityPerTrade, relation.Affinity);
        Assert.Equal(1, relation.Familiarity);
        Assert.False(relation.OtherIsBot);
        Assert.False(relation.Flags.HasFlag(PlayerbotRelationFlags.OtherFaction));
    }

    // --- g) targeted emotes ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATargetedFriendlyEmote_RaisesAffinity_AndATargetedRudeEmote_LowersIt()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        Guid friendlyBot = await world.AddBotAsync("Emotekind");
        Guid rudeBot = await world.AddBotAsync("Emoterude");
        await using WorldTestClient friend = await world.AddPlayerAsync("Emotefriend");
        await using WorldTestClient rude = await world.AddPlayerAsync("Emotefoe");
        ObjectGuid friendGuid = await world.Host.PlayerStateAsync("Emotefriend", p => p.Guid);
        ObjectGuid rudeGuid = await world.Host.PlayerStateAsync("Emotefoe", p => p.Guid);
        var options = new PlayerbotSocialMemoryOptions();

        await EmoteAtAsync(world, friend, "Emotefriend", "Emotekind", PlayerbotSocialEmotes.TextEmotes.Wave);
        await EmoteAtAsync(world, rude, "Emotefoe", "Emoterude", PlayerbotSocialEmotes.TextEmotes.Rude);

        await UntilAsync(() => RelationOf(world.Host, friendlyBot, friendGuid) is not null && RelationOf(world.Host, rudeBot, rudeGuid) is not null, "both emotes to be remembered");
        Assert.Equal((short)options.AffinityPerFriendlyEmote, RelationOf(world.Host, friendlyBot, friendGuid)!.Affinity);
        Assert.Equal((short)options.AffinityPerRudeEmote, RelationOf(world.Host, rudeBot, rudeGuid)!.Affinity);
        Assert.True(options.AffinityPerFriendlyEmote > 0 && options.AffinityPerRudeEmote < 0);
        // An emote aimed at one bot is not remembered by the other.
        Assert.Null(RelationOf(world.Host, rudeBot, friendGuid));
        Assert.Null(RelationOf(world.Host, friendlyBot, rudeGuid));
    }

    [Fact]
    public async Task ANeutralEmote_IsIgnored_AndAnEmoteAtSomeoneElse_IsNotTheBots()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        Guid bot = await world.AddBotAsync("Emoteneutral");
        await using WorldTestClient player = await world.AddPlayerAsync("Emotegreeter");
        await using WorldTestClient other = await world.AddPlayerAsync("Emotebystand");
        ObjectGuid playerGuid = await world.Host.PlayerStateAsync("Emotegreeter", p => p.Guid);
        ObjectGuid otherGuid = await world.Host.PlayerStateAsync("Emotebystand", p => p.Guid);

        // /agree is in no class; /wave at another player is not aimed at the bot; both are heard by it.
        await EmoteAtAsync(world, player, "Emotegreeter", "Emoteneutral", PlayerbotSocialEmotes.TextEmotes.Agree);
        await world.WhenNearAsync(["Emoteneutral"], ["Emotegreeter", "Emotebystand"], () => { });
        await player.SendAsync(WorldOpcode.CmsgTextEmote, TextEmote(PlayerbotSocialEmotes.TextEmotes.Wave, otherGuid));
        // The control: a real wave at the bot, sent after both, is the one that is remembered, so the two before it have been heard and dropped.
        await other.SendAsync(WorldOpcode.CmsgTextEmote, TextEmote(PlayerbotSocialEmotes.TextEmotes.Wave, await world.Host.PlayerStateAsync("Emoteneutral", p => p.Guid)));

        await UntilAsync(() => RelationOf(world.Host, bot, otherGuid) is not null, "the control wave to be remembered");
        Assert.Null(RelationOf(world.Host, bot, playerGuid));
    }

    [Fact]
    public async Task TheSorryEmoteTowardADislikedBot_RecordsApologized_OncePerDay_AndRepairsTheGrudge()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        Guid bot = await world.AddBotAsync("Sorrybot");
        await using WorldTestClient player = await world.AddPlayerAsync("Sorryplayer");
        ObjectGuid playerGuid = await world.Host.PlayerStateAsync("Sorryplayer", p => p.Guid);
        var options = new PlayerbotSocialMemoryOptions();

        // A rude emote first, so the bot holds something against the player.
        await EmoteAtAsync(world, player, "Sorryplayer", "Sorrybot", PlayerbotSocialEmotes.TextEmotes.Rude);
        await UntilAsync(() => RelationOf(world.Host, bot, playerGuid) is not null, "the rude emote to be remembered");
        Assert.True(RelationOf(world.Host, bot, playerGuid)!.Affinity < 0);
        short grudge = RelationOf(world.Host, bot, playerGuid)!.Affinity;

        await EmoteAtAsync(world, player, "Sorryplayer", "Sorrybot", PlayerbotSocialEmotes.TextEmotes.Apologize);
        await UntilAsync(() => RelationOf(world.Host, bot, playerGuid)!.Affinity > grudge, "the apology to be remembered");
        short repaired = RelationOf(world.Host, bot, playerGuid)!.Affinity;
        Assert.Equal((short)(grudge + options.AffinityPerApology), repaired);

        // The same day, a second apology (and a second one after it, as the control that the first was heard): nothing more.
        await EmoteAtAsync(world, player, "Sorryplayer", "Sorrybot", PlayerbotSocialEmotes.TextEmotes.Apologize);
        await EmoteAtAsync(world, player, "Sorryplayer", "Sorrybot", PlayerbotSocialEmotes.TextEmotes.Rude);
        await UntilAsync(() => RelationOf(world.Host, bot, playerGuid)!.Affinity < repaired, "the second rude emote to be remembered");
        Assert.Equal((short)(repaired + options.AffinityPerRudeEmote), RelationOf(world.Host, bot, playerGuid)!.Affinity);
    }

    // --- h) a kick ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APlayerKickingABotFromTheGroup_RecordsKicked_ButLeavingTheGroupDoesNot()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(options => options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone, frozen: true);
        Guid keeper = await world.AddBotAsync("Kickkeeper");
        Guid kicked = await world.AddBotAsync("Kickedbot");
        await using WorldTestClient leader = await world.AddPlayerAsync("Kickleader");
        ObjectGuid leaderGuid = await world.Host.PlayerStateAsync("Kickleader", p => p.Guid);
        ObjectGuid keeperGuid = await world.Host.PlayerStateAsync("Kickkeeper", p => p.Guid);

        await leader.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString("Kickkeeper"));
        await world.Host.WaitForWorldAsync(() => PartyTestHost.Feature(world.Host).IsPartyDriven(keeper), "the first bot joins");
        await leader.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString("Kickedbot"));
        await world.Host.WaitForWorldAsync(() => PartyTestHost.Feature(world.Host).IsPartyDriven(kicked), "the second bot joins");

        await leader.SendAsync(WorldOpcode.CmsgGroupUninvite, PartyTestHost.CString("Kickedbot"));
        await UntilAsync(() => RelationOf(world.Host, kicked, leaderGuid) is not null, "the kick to be remembered");

        PlayerbotRelation relation = RelationOf(world.Host, kicked, leaderGuid)!;
        Assert.Equal((short)new PlayerbotSocialMemoryOptions().AffinityPerKick, relation.Affinity);
        Assert.False(relation.OtherIsBot);
        // The other member saw nobody kicked from its own point of view: it remembers nothing of the leader.
        Assert.Null(RelationOf(world.Host, keeper, leaderGuid));
        Assert.Null(RelationOf(world.Host, kicked, keeperGuid));
    }

    // --- i) persistence, through a checkpoint ---------------------------------------------------------------------------------

    [Fact]
    public async Task ARelationFromARealEmote_IsWrittenByTheNextCheckpoint_WithoutStoppingTheBot()
    {
        var store = new PlayerbotSocialMemoryPersistenceTests.MemoryRelationStore();
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true,
            services: services => services.AddSingleton<IPlayerbotRelationStore>(store));
        Guid bot = await world.AddBotAsync("Checkbot");
        await using WorldTestClient player = await world.AddPlayerAsync("Checkplayer");
        ObjectGuid playerGuid = await world.Host.PlayerStateAsync("Checkplayer", p => p.Guid);
        Assert.Equal(0, store.Replaces);

        await EmoteAtAsync(world, player, "Checkplayer", "Checkbot", PlayerbotSocialEmotes.TextEmotes.Wave);

        // The feature checkpoints every five seconds; the bot keeps running.
        // Wait for the stored relation itself, not for Replaces: the test double used to bump Replaces before it stored the rows,
        // without a lock, so a poll could see Replaces > 0 with nothing stored yet.
        IReadOnlyList<PlayerbotRelation> stored = [];
        await UntilAsync(() => (stored = store.LoadAllAsync().GetAwaiter().GetResult()).Count > 0, "the checkpoint to write the relation", seconds: 30);
        PlayerbotRelation saved = Assert.Single(stored);
        Assert.Equal(bot, saved.BotId);
        Assert.Equal(playerGuid.Value, saved.OtherGuid);
        Assert.Equal((short)new PlayerbotSocialMemoryOptions().AffinityPerFriendlyEmote, saved.Affinity);
        Assert.Equal(ManagedPlayerbotState.Running, PartyTestHost.Feature(world.Host).Snapshot().Single(s => s.BotId == bot).State);
    }

    // --- j) the GM command ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheGmSocialCommand_RendersTheBotsTraitsAndItsTopRelations_ForAnAdministratorOnly()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        Guid bot = await world.AddBotAsync("Socialcmdbot");
        await using WorldTestClient player = await world.AddPlayerAsync("Quillfoot");
        ObjectGuid playerGuid = await world.Host.PlayerStateAsync("Quillfoot", p => p.Guid);
        ObjectGuid botGuid = await world.Host.PlayerStateAsync("Socialcmdbot", p => p.Guid);
        const ulong stranger = 0x1A2B3C;
        await EmoteAtAsync(world, player, "Quillfoot", "Socialcmdbot", PlayerbotSocialEmotes.TextEmotes.Wave);
        await UntilAsync(() => RelationOf(world.Host, bot, playerGuid) is not null, "the wave to be remembered");
        // A relation to someone who is not online shows as a guid; it is the better known of the two (two exchanges).
        var memory = MemoryOf(world.Host);
        memory.Note(bot, (int)botGuid.Low, stranger, false, PlayerbotSocialEvent.Grouped);
        memory.Note(bot, (int)botGuid.Low, stranger, false, PlayerbotSocialEvent.TradeCompleted);

        await using WorldTestClient admin = await world.Host.EnterWorldAsync("QUILLADMINA", "Quillroot", AccountSecurity.Administrator);
        List<string> reply = await FleetWorld.SayAsync(admin, ".playerbot social Socialcmdbot", lines: 3);

        PlayerbotTraits traits = PlayerbotTraits.For(botGuid.Value);
        CultureInfo c = CultureInfo.InvariantCulture;
        Assert.Equal(string.Create(c, $"Socialcmdbot: sociability {traits.Sociability:F2}, chattiness {traits.Chattiness:F2}, generosity {traits.Generosity:F2}, curiosity {traits.Curiosity:F2}, helpfulness {traits.Helpfulness:F2}."), reply[0]);
        // Best known first: the stranger (2 events, affinity 6, grouped) before the player (1 wave, affinity 2).
        Assert.Equal("guid 1A2B3C (player): affinity 6, familiarity 2, trust 500, flags Grouped, Interacted, topic -, seen 0m ago", reply[1]);
        Assert.Equal("Quillfoot (player): affinity 2, familiarity 1, trust 500, flags Interacted, topic -, seen 0m ago", reply[2]);
    }

    [Fact]
    public async Task TheGmSocialCommand_OnABotWithNoRelations_SaysSo_AndAnUnknownBotIsNotFound()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        await world.AddBotAsync("Socialempty");
        await using WorldTestClient admin = await world.Host.EnterWorldAsync("QUILLADMINB", "Quillbranch", AccountSecurity.Administrator);

        List<string> reply = await FleetWorld.SayAsync(admin, ".playerbot social Socialempty", lines: 3);
        Assert.StartsWith("Socialempty: sociability ", reply[0]);
        Assert.Equal("No relations yet.", reply[1]);

        Assert.Equal(["Playerbot not found."], await FleetWorld.SayAsync(admin, ".playerbot social Nosuchbot"));
    }
}
