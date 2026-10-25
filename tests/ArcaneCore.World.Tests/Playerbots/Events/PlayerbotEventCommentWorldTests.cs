using ArcaneCore.World.Playerbots;
using ArcaneCore.Game.Npc;
using ArcaneCore.Protocol;
using ArcaneCore.World.Tests.Playerbots.Chat;
using ArcaneCore.World.Tests.Playerbots.Social;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Events.EventsSocialKit;
using static ArcaneCore.World.Tests.Playerbots.Events.EventsTestWorld;

namespace ArcaneCore.World.Tests.Playerbots.Events;

/// <summary>
/// The initiative's "an event has started" remark (P7a H4a, design 4.3 "Event comment fix"): only a festival root names itself, in a zone
/// that has an anchor of it, through the listener filter; the Hourly Bells, a member event and a root without an anchor say nothing.
/// The remark's chance is a hash roll the tests do not control, so each test starts the event again and again (a stop pass, then a start
/// pass, 70 game seconds apart) until it speaks or the attempts run out; a test that expects silence runs the same number of attempts as
/// the control that speaks.
/// </summary>
public sealed class PlayerbotEventCommentWorldTests
{
    private const int Attempts = 120;
    private const long Step = 70_000;
    private const long Minute = 60_000;

    private static FestivalNpc Gossip(ushort eventId, uint entry = 991_101) => new(entry, "Festival Host", NpcFlags.Gossip, 20f, 0f, Event: eventId);

    private static Task<SocialWorld> StartAsync(FakeBotChatClient model, IReadOnlyList<FestivalNpc> npcs, Action<PlayerbotOptions>? configure = null)
        => EventsSocialKit.StartAsync(model, npcs, options =>
        {
            options.Social.Initiative.CommentBotGapMinutes = 1;
            options.Social.Initiative.CommentZoneGapSeconds = 1;
            configure?.Invoke(options);
        });

    /// <summary>Start <paramref name="id"/> afresh up to <paramref name="attempts"/> times from <paramref name="from"/>; stops as soon as the comment count reaches <paramref name="until"/>. Returns the time of the last pass.</summary>
    private static async Task<long> ScanAsync(SocialWorld world, ushort id, long from, int attempts, int until)
    {
        long now = from;
        for (int i = 0; i < attempts && SocialTestKit.Feature(world.Host).Initiative.Comments < until; i++)
        {
            now += Step;
            await StopEventAsync(world, id);
            await RunInitiativeAsync(world, now);
            await StartEventAsync(world, id);
            await RunInitiativeAsync(world, now + 1_000);
        }

        return now;
    }

    private static int Comments(SocialWorld world) => SocialTestKit.Feature(world.Host).Initiative.Comments;

    private static async Task<(string Bot, WorldTestClient Listener)> SetUpAsync(SocialWorld world, string tag)
    {
        await world.AddBotAsync(tag + "bot");
        WorldTestClient listener = await world.AddPlayerAsync(tag + "listen");
        await HelpTestKit.StandAsync(world, tag + "bot", (tag + "listen", 8f));
        return (tag + "bot", listener);
    }

    [Fact]
    public async Task AFestivalRootThatStarts_IsRemarkedOnByName_InAZoneWithAnAnchorOfIt()
    {
        await using SocialWorld world = await StartAsync(new FakeBotChatClient(), [Gossip(WinterVeil)], options => options.Chat.Safety.RequireOptIn = true);
        (string bot, WorldTestClient listener) = await SetUpAsync(world, "Named");
        await using WorldTestClient disposeListener = listener;

        await ScanAsync(world, WinterVeil, 0, Attempts, until: 1);

        Assert.Equal(1, Comments(world));
        ChatMessage line = (await SaysAsync(listener)).Single();
        Assert.Contains("Winter Veil", line.Text);
        Assert.DoesNotContain("{event}", line.Text);
    }

    [Fact]
    public async Task AFestivalRootWithNoAnchorInTheZone_IsNotRemarkedOn()
    {
        // The control above speaks within the attempts; here the same festival has no anchor (its only NPC belongs to another event).
        await using SocialWorld world = await StartAsync(new FakeBotChatClient(), [Gossip(Hallows)], options => options.Chat.Safety.RequireOptIn = true);
        (_, WorldTestClient listener) = await SetUpAsync(world, "Noanc");
        await using WorldTestClient disposeListener = listener;

        await ScanAsync(world, WinterVeil, 0, Attempts, until: 1);

        Assert.Equal(0, Comments(world));
        Assert.Empty(await SaysAsync(listener));
    }

    [Fact]
    public async Task TheHourlyBells_AreNotRemarkedOn()
    {
        await using SocialWorld world = await StartAsync(new FakeBotChatClient(), [Gossip(WinterVeil)], options => options.Chat.Safety.RequireOptIn = true);
        (_, WorldTestClient listener) = await SetUpAsync(world, "Bells");
        await using WorldTestClient disposeListener = listener;

        await ScanAsync(world, HourlyBells, 0, Attempts, until: 1);

        Assert.Equal(0, Comments(world));
        Assert.Empty(await SaysAsync(listener));
    }

    [Fact]
    public async Task AMemberEvent_DoesNotReannounceItsFestival()
    {
        // Wickerman Ember's (24) is linked to Hallow's End (12) and starts every day.
        await using SocialWorld world = await StartAsync(new FakeBotChatClient(), [Gossip(Hallows)], options => options.Chat.Safety.RequireOptIn = true);
        (_, WorldTestClient listener) = await SetUpAsync(world, "Member");
        await using WorldTestClient disposeListener = listener;

        await ScanAsync(world, WickermanEmbers, 0, Attempts, until: 1);

        Assert.Equal(0, Comments(world));
        Assert.Empty(await SaysAsync(listener));
    }

    [Fact]
    public async Task HallowsEndAsARoot_IsRemarkedOnByItsName()
    {
        await using SocialWorld world = await StartAsync(new FakeBotChatClient(), [Gossip(Hallows)], options => options.Chat.Safety.RequireOptIn = true);
        (_, WorldTestClient listener) = await SetUpAsync(world, "Hallow");
        await using WorldTestClient disposeListener = listener;

        await ScanAsync(world, Hallows, 0, Attempts, until: 1);

        Assert.Equal(1, Comments(world));
        Assert.Contains("Hallow's End", (await SaysAsync(listener)).Single().Text);
    }

    [Fact]
    public async Task AListenerWhoSaidAiOff_GetsABuiltinRemark_AndTheModelIsNotCalled_WhileAnOrdinaryListenerGetsTheModel()
    {
        var model = new FakeBotChatClient();
        await using SocialWorld world = await StartAsync(model, [Gossip(WinterVeil)]);
        (string bot, WorldTestClient optedOut) = await SetUpAsync(world, "Aioffc");
        await using WorldTestClient disposeOptedOut = optedOut;
        await AiOffAsync(world, "Aioffclisten");

        long now = await ScanAsync(world, WinterVeil, 0, Attempts, until: 1);

        Assert.Equal(1, Comments(world));
        Assert.Empty(model.Calls);
        ChatMessage builtin = (await SaysAsync(optedOut)).Single();
        Assert.Contains("Winter Veil", builtin.Text);

        // Control: the same remark for an ordinary listener (the opted-out one gone) goes to the model.
        await optedOut.DisposeAsync();
        await world.Host.WaitForWorldAsync(() => world.Host.World.FindOnlinePlayer("Aioffclisten") is null, "the opted-out player to leave");
        await using WorldTestClient ordinary = await world.AddPlayerAsync("Aiocontrol");
        await HelpTestKit.StandAsync(world, bot, ("Aiocontrol", 8f));
        await ScanAsync(world, WinterVeil, now + (40 * Minute), Attempts, until: 2);

        Assert.Equal(2, Comments(world));
        await HelpTestKit.UntilAsync(() => !model.Calls.IsEmpty, "the model call of the ordinary listener's remark"); // a chat worker makes it, after the scan
        Assert.NotEmpty(model.Calls);
    }

    [Fact]
    public async Task ACutOffListener_HearsNoRemark()
    {
        await using SocialWorld world = await StartAsync(new FakeBotChatClient(), [Gossip(WinterVeil)], options =>
        {
            options.Chat.Safety.RequireOptIn = true;
            options.Chat.Safety.StrikesBeforeCutoff = 1;
        });
        (_, WorldTestClient listener) = await SetUpAsync(world, "Cutc");
        await using WorldTestClient disposeListener = listener;
        await CutOffAsync(world, "Cutclisten");

        await ScanAsync(world, WinterVeil, 0, Attempts, until: 1);

        Assert.Equal(0, Comments(world));
        Assert.Empty(await SaysAsync(listener));
    }

    [Fact]
    public async Task AnIgnoringListener_HearsNoRemark()
    {
        await using SocialWorld world = await StartAsync(new FakeBotChatClient(), [Gossip(WinterVeil)], options => options.Chat.Safety.RequireOptIn = true);
        (string bot, WorldTestClient listener) = await SetUpAsync(world, "Ignc");
        await using WorldTestClient disposeListener = listener;
        await world.Host.OnWorldAsync(() => SocialTestKit.Social(world.Host).Context.Friends.AddIgnore(HelpTestKit.Online(world, "Ignclisten"), bot));

        await ScanAsync(world, WinterVeil, 0, Attempts, until: 1);

        Assert.Equal(0, Comments(world));
        Assert.Empty(await SaysAsync(listener));
    }

    [Fact]
    public async Task APlayerWhoWasRemarkedTo_IsNotAddressedAgainWithin30Minutes_ButIsAfterIt()
    {
        await using SocialWorld world = await StartAsync(new FakeBotChatClient(), [Gossip(WinterVeil)], options => options.Chat.Safety.RequireOptIn = true);
        (_, WorldTestClient listener) = await SetUpAsync(world, "Ledgc");
        await using WorldTestClient disposeListener = listener;

        long first = await ScanAsync(world, WinterVeil, 0, Attempts, until: 1);
        Assert.Equal(1, Comments(world));

        // Up to 29 minutes after it, every restart of the festival stays silent for this player.
        long now = first;
        while (now + Step < first + (29 * Minute)) now = await ScanAsync(world, WinterVeil, now, 1, until: 2);
        Assert.Equal(1, Comments(world));

        // Past the 30 minutes the next remark may be said.
        await ScanAsync(world, WinterVeil, first + (31 * Minute), Attempts, until: 2);
        Assert.Equal(2, Comments(world));
    }
}
