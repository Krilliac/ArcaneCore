using System.Collections.Concurrent;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Tests.Playerbots.Party;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The "rivals off / on" whisper through the real chat path (design 4.3, review item 3): it works with bot chat off, with the bot ignoring the
/// player, through any bot of the player's own faction (the cross-faction whisper is dropped by the server unless <c>World:AllowTwoSideChat</c>),
/// it is one server-wide store, and a recognised line is consumed: the chat service's listeners never hear it.
/// <para>
/// Contract: <c>PlayerbotSocialFeature.RivalryOptOut</c> (the one store, owned by the feature) and <c>PlayerbotSocialFeature.Rivalry</c> (the lane).
/// </para>
/// </summary>
public sealed class PlayerbotRivalryOptOutWorldTests
{
    private const string OffAnswer = "Okay: bots will not taunt you. This lasts until the server restarts; whisper \"rivals off\" again after that. Whisper \"rivals on\" to undo.";
    private const string StatusMayTaunt = "Bots may taunt you (whisper \"rivals off\" to stop it)";

    private static async Task<(WorldTestClient Player, ulong PlayerGuid, int Account)> HumanPlayerAsync(SocialWorld world, string name, string nearBot)
    {
        WorldTestClient player = await world.AddPlayerAsync(name);
        await HelpTestKit.StandAsync(world, nearBot, (name, 3f));
        return (player, (await RivalryKit.GuidAsync(world, name)).Value, await world.Host.PlayerStateAsync(name, p => p.AccountId));
    }

    /// <summary>Everything the chat service's listeners are handed (the texts), from now on.</summary>
    private static async Task<ConcurrentQueue<string>> ListenToChatAsync(SocialWorld world)
    {
        var heard = new ConcurrentQueue<string>();
        await world.Host.OnWorldAsync(() =>
        {
            PartyTestHost.Feature(world.Host).Chat!.LineHeard += line => heard.Enqueue(line.Text);
            return true;
        });
        return heard;
    }

    [Fact]
    public async Task AWhisperedRivalsOff_WithChatOff_AndTheBotIgnoringThePlayer_IsAnswered_AndStopsAnotherFactionsBotFromTaunting()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        Assert.False(world.Options.Chat.Enabled); // the test host's default: the line cannot have been taken by the chat service
        await world.AddBotAsync("Rvofhuman");
        await world.AddBotAsync("Rvofundead", race: RivalryKit.Undead);
        (WorldTestClient client, ulong playerGuid, int account) = await HumanPlayerAsync(world, "Rvofplayer", "Rvofhuman");
        await using WorldTestClient player = client;
        await HelpTestKit.StandAsync(world, "Rvofhuman", ("Rvofundead", 2f));
        await RivalryKit.SeedAsync(world, "Rvofundead", "Rvofplayer", killerIsBot: false);
        uint playerLow = (uint)(playerGuid & 0xFFFFFFFF);
        await world.Host.OnWorldAsync(() =>
            SocialTestKit.Social(world.Host).Context.Friends.Get(RivalryKit.Online(world, "Rvofhuman")).Set(playerLow, SocialFlags.Ignored, true));

        // Control: before the choice the Undead bot's rival taunts the Human player.
        long t0 = 3_600_000;
        await RivalryKit.RunAsync(world, t0);
        Assert.Single(RivalryKit.Lane(world).Sends, s => s.Rude && s.Target == playerGuid);
        await RivalryKit.ListenAsync(player); // the taunt and its disclosure

        await player.SendChatAsync(ChatType.Whisper, Language.Common, "rivals off", "Rvofhuman");
        ChatMessage answer = await player.ReadSystemChatAsync();

        Assert.Equal(OffAnswer, answer.Text);
        Assert.True(RivalryKit.OptOut(world).IsOptedOut(playerGuid, account));

        // Server-wide: the choice was made through the Human bot and holds for the Undead one. Two hours on every gap is long over.
        int before = RivalryKit.Lane(world).Sends.Count;
        await RivalryKit.RunAsync(world, t0 + 120 * 60_000);
        Assert.Equal(before, RivalryKit.Lane(world).Sends.Count);
    }

    [Fact]
    public async Task AnOptOut_SurvivesTheSocialFeatureBeingDetachedAndAttachedAgain_AndTheBotsPartyAisStillShareTheStore()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        await world.AddBotAsync("Rvswaphuman");
        await world.AddBotAsync("Rvswapundead", race: RivalryKit.Undead);
        (WorldTestClient client, ulong playerGuid, int account) = await HumanPlayerAsync(world, "Rvswapplayer", "Rvswaphuman");
        await using WorldTestClient player = client;
        await HelpTestKit.StandAsync(world, "Rvswaphuman", ("Rvswapundead", 2f));
        await RivalryKit.SeedAsync(world, "Rvswapundead", "Rvswapplayer", killerIsBot: false);
        PlayerbotRivalryOptOut before = RivalryKit.OptOut(world);
        before.Handle(playerGuid, account, "rivals off", -100_000, world.Options.Social.Rivalry);

        // The host's detach and re-attach of a detachable feature (a hot reload's rollback, or a swap of the same feature): same instance, cold attach.
        await world.Host.OnWorldAsync(() =>
        {
            PlayerbotSocialFeature feature = SocialTestKit.Feature(world.Host);
            feature.Detach(new ArcaneCore.World.Features.FeatureHandover());
            feature.AttachFrom(world.Host.World, null, new ArcaneCore.World.Features.FeatureSubscriptions("rivalry-swap"));
            return true;
        });

        Assert.Same(before, RivalryKit.OptOut(world));
        Assert.Same(before, RivalryKit.Lane(world).OptOut);
        Assert.Same(before, PartyTestHost.Feature(world.Host).Social.RivalryOptOut); // what every bot's party AI writes whispers into and reads from
        Assert.True(RivalryKit.OptOut(world).IsOptedOut(playerGuid, account));
        await RivalryKit.RunAsync(world, 3_600_000);
        Assert.DoesNotContain(RivalryKit.Lane(world).Sends, s => s.Target == playerGuid);
    }

    [Fact]
    public async Task RivalsOn_ThroughTheWhisperPath_BringsTheTauntsBack()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        await world.AddBotAsync("Rvonhuman");
        await world.AddBotAsync("Rvonundead", race: RivalryKit.Undead);
        (WorldTestClient client, ulong playerGuid, int account) = await HumanPlayerAsync(world, "Rvonplayer", "Rvonhuman");
        await using WorldTestClient player = client;
        await HelpTestKit.StandAsync(world, "Rvonhuman", ("Rvonundead", 2f));
        await RivalryKit.SeedAsync(world, "Rvonundead", "Rvonplayer", killerIsBot: false);
        // The choice is made directly, long before the party AI's clock starts, so the whispered answer is not inside the ten second gap.
        RivalryKit.OptOut(world).Handle(playerGuid, account, "rivals off", -100_000, world.Options.Social.Rivalry);
        await RivalryKit.RunAsync(world, 3_600_000);
        Assert.DoesNotContain(RivalryKit.Lane(world).Sends, s => s.Target == playerGuid); // the two bots may greet each other in front of the player; nothing is aimed at the player

        await player.SendChatAsync(ChatType.Whisper, Language.Common, "rivals on", "Rvonhuman");
        ChatMessage answer = await player.ReadSystemChatAsync();

        Assert.False(string.IsNullOrWhiteSpace(answer.Text));
        Assert.NotEqual(OffAnswer, answer.Text);
        Assert.False(RivalryKit.OptOut(world).IsOptedOut(playerGuid, account));
        await RivalryKit.RunAsync(world, 3_700_000);
        Assert.Single(RivalryKit.Lane(world).Sends, s => s.Rude && s.Target == playerGuid);
    }

    [Fact]
    public async Task ABareRivals_ReportsTheStatus_AndChangesNothing()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        await world.AddBotAsync("Rvstatbot");
        (WorldTestClient client, ulong playerGuid, int account) = await HumanPlayerAsync(world, "Rvstatplayer", "Rvstatbot");
        await using WorldTestClient player = client;

        await player.SendChatAsync(ChatType.Whisper, Language.Common, "rivals", "Rvstatbot");

        Assert.Equal(StatusMayTaunt, (await player.ReadSystemChatAsync()).Text);
        Assert.False(RivalryKit.OptOut(world).IsOptedOut(playerGuid, account));
        Assert.Null(RivalryKit.OptOut(world).ChoiceOf(playerGuid, account));
    }

    [Fact]
    public async Task AWhisperToABotOfTheOtherFaction_IsDroppedByTheServer_WhileAllowTwoSideChatIsOff_AndAcceptedWhenItIsOn()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        await world.AddBotAsync("Rvwrongbot", race: RivalryKit.Undead);
        (WorldTestClient client, ulong playerGuid, int account) = await HumanPlayerAsync(world, "Rvwrongplay", "Rvwrongbot");
        await using WorldTestClient player = client;
        Assert.False(world.Host.World.Options.AllowTwoSideChat);

        await player.SendChatAsync(ChatType.Whisper, Language.Common, "rivals off", "Rvwrongbot");
        await player.ReadUntilAsync(WorldOpcode.SmsgChatWrongFaction);
        Assert.False(RivalryKit.OptOut(world).IsOptedOut(playerGuid, account)); // the documented path is a bot of the player's own faction

        world.Host.World.Options.AllowTwoSideChat = true;
        await player.SendChatAsync(ChatType.Whisper, Language.Common, "rivals off", "Rvwrongbot");
        Assert.Equal(OffAnswer, (await player.ReadSystemChatAsync()).Text);
        Assert.True(RivalryKit.OptOut(world).IsOptedOut(playerGuid, account));
    }

    [Fact]
    public async Task ARecognisedLine_IsConsumed_SoTheChatServiceNeverHearsIt_WhileAnOrdinaryWhisperIs()
    {
        await using SocialWorld world = await RivalryKit.StartAsync(chat: true);
        await world.AddBotAsync("Rvheardbot");
        (WorldTestClient client, ulong playerGuid, int account) = await HumanPlayerAsync(world, "Rvheardplay", "Rvheardbot");
        await using WorldTestClient player = client;
        ConcurrentQueue<string> heard = await ListenToChatAsync(world);

        // Control: an ordinary whisper does reach the listeners (so the silence below is the consumption, not a listener that hears nothing).
        await player.SendChatAsync(ChatType.Whisper, Language.Common, "good evening to you", "Rvheardbot");
        await HelpTestKit.UntilAsync(() => heard.Contains("good evening to you"), "an ordinary whisper to be heard");

        await player.SendChatAsync(ChatType.Whisper, Language.Common, "rivals off", "Rvheardbot");
        await HelpTestKit.UntilAsync(() => RivalryKit.OptOut(world).IsOptedOut(playerGuid, account), "the opt-out to be recorded");
        await player.SendChatAsync(ChatType.Whisper, Language.Common, "another ordinary line", "Rvheardbot"); // everything before it has been handled by now
        await HelpTestKit.UntilAsync(() => heard.Contains("another ordinary line"), "a later whisper to be heard");

        Assert.DoesNotContain("rivals off", heard);
    }

    [Fact]
    public async Task WithAllowOptOutOff_TheWhisperIsNotParsed_ItGoesOnToTheChatService_AndNothingIsRecorded()
    {
        await using SocialWorld world = await RivalryKit.StartAsync(o => o.Social.Rivalry.AllowOptOut = false, chat: true);
        await world.AddBotAsync("Rvnoparsebot");
        (WorldTestClient client, ulong playerGuid, int account) = await HumanPlayerAsync(world, "Rvnoparsepl", "Rvnoparsebot");
        await using WorldTestClient player = client;
        ConcurrentQueue<string> heard = await ListenToChatAsync(world);

        await player.SendChatAsync(ChatType.Whisper, Language.Common, "rivals off", "Rvnoparsebot");

        await HelpTestKit.UntilAsync(() => heard.Contains("rivals off"), "the unparsed line to be heard as any whisper is");
        Assert.False(RivalryKit.OptOut(world).IsOptedOut(playerGuid, account));
    }

    [Fact]
    public async Task ABotsWhisper_IsNeverAnOptOut()
    {
        await using SocialWorld world = await RivalryKit.StartAsync(chat: true);
        await world.AddBotAsync("Rvwhisperer");
        await world.AddBotAsync("Rvwhispered");
        await HelpTestKit.StandAsync(world, "Rvwhisperer", ("Rvwhispered", 2f));
        ConcurrentQueue<string> heard = await ListenToChatAsync(world);
        ulong whispererGuid = (await RivalryKit.GuidAsync(world, "Rvwhisperer")).Value;
        int account = await world.Host.PlayerStateAsync("Rvwhisperer", p => p.AccountId);
        var line = new PacketWriter();
        line.WriteUInt32((uint)ChatType.Whisper);
        line.WriteUInt32((uint)Language.Common);
        line.WriteCString("Rvwhispered");
        line.WriteCString("rivals off");

        Assert.True(await world.SendAsync("Rvwhisperer", WorldOpcode.CmsgMessagechat, line.ToArray()));

        // The bot's line is ordinary talk to the listeners (and so it was not taken as a command), and no choice is recorded for it.
        await HelpTestKit.UntilAsync(() => heard.Contains("rivals off"), "the bot's whisper to be heard");
        Assert.False(RivalryKit.OptOut(world).IsOptedOut(whispererGuid, account));
    }
}
