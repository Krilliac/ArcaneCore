using ArcaneCore.World.Playerbots;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Events;
using ArcaneCore.World.Tests.Playerbots.Chat;
using ArcaneCore.World.Tests.Playerbots.Social;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Events.EventsSocialKit;
using static ArcaneCore.World.Tests.Playerbots.Events.EventsTestWorld;

namespace ArcaneCore.World.Tests.Playerbots.Events;

/// <summary>
/// The arrival line of a bot held at a festival spot, with the chat safety in front of it (P7a H4a, design 4.3 and review item B1).
/// The social test world (a frozen social feature, hand-built passes at chosen world times, a fake model client, real clients as
/// listeners); the claims are put on the board by hand, so the tests are about what the events lane says once a bot holds a spot.
/// </summary>
public sealed class PlayerbotFestivalTalkWorldTests
{
    private const long Minute = 60_000;

    private static Task<SocialWorld> StartAsync(FakeBotChatClient client, Action<PlayerbotOptions>? configure = null)
        => EventsSocialKit.StartAsync(client, [new FestivalNpc(991_001, "Winter Reveler", ArcaneCore.Game.Npc.NpcFlags.Gossip, 20f, 0f)], configure);

    private static async Task<(ulong Bot, string Name)> BotAsync(SocialWorld world, string name)
    {
        await world.AddBotAsync(name);
        return ((await world.Host.PlayerStateAsync(name, p => p.Guid)).Value, name);
    }

    /// <summary>The lane says every arrival (the roll is made to always say yes), so a test counts decisions, not luck.</summary>
    private static void Eager(SocialWorld world) => Lane(world).RollOverride = (_, _) => 0.0;

    // --- the listener ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnArrivalWithNobodyToHearIt_SaysNothing_AndWithAListenerInRangeSaysOneLineInTheSay()
    {
        var model = new FakeBotChatClient();
        await using SocialWorld world = await StartAsync(model, options => options.Chat.Safety.RequireOptIn = true);
        await StartEventAsync(world, WinterVeil);
        (ulong botGuid, string bot) = await BotAsync(world, "Talkbot");
        Eager(world);

        // Nobody is in the world but the bot: a remark is for someone to hear.
        Assert.True(await ClaimAsync(world, bot, WinterVeil, 1_000));
        await RunLaneAsync(world, 1_000);
        Assert.Equal(0, Lane(world).Lines);
        Assert.True(Lane(world).Suppressed("no_listener") >= 1);

        await using WorldTestClient listener = await world.AddPlayerAsync("Talklisten");
        await HelpTestKit.StandAsync(world, bot, ("Talklisten", 10f));

        // The bot arrives again (its spot is given up and taken once more): now the line is said, to everyone in say range.
        Assert.True(await ClaimAsync(world, bot, WinterVeil, 100_000));
        await RunLaneAsync(world, 100_000);

        Assert.Equal(1, Lane(world).Lines);
        ChatMessage said = await SayFromAsync(listener, botGuid);
        Assert.False(string.IsNullOrWhiteSpace(said.Text));
        Assert.Empty(model.Calls); // RequireOptIn: built-in lines only
        Assert.Contains("event.winterveil.arrive", PlayerbotSocialTemplates.Lines.Keys);
    }

    [Fact]
    public async Task APlayerOfTheOtherFactionInRange_IsAListenerToo_AndOneBeyondTheRangeIsNot()
    {
        await using SocialWorld world = await StartAsync(new FakeBotChatClient(), options => options.Chat.Safety.RequireOptIn = true);
        await StartEventAsync(world, WinterVeil);
        (_, string bot) = await BotAsync(world, "Rangebot");
        Eager(world);
        await using WorldTestClient far = await world.AddPlayerAsync("Rangefar");
        await HelpTestKit.StandAsync(world, bot, ("Rangefar", 26f));

        Assert.True(await ClaimAsync(world, bot, WinterVeil, 1_000));
        await RunLaneAsync(world, 1_000);
        Assert.Equal(0, Lane(world).Lines); // 26 yards: out of /say range

        await using WorldTestClient orc = await world.AddPlayerAsync("Rangeorc", race: SocialTestKit.Orc);
        await HelpTestKit.StandAsync(world, bot, ("Rangeorc", 12f));
        Assert.True(await ClaimAsync(world, bot, WinterVeil, 200_000));
        await RunLaneAsync(world, 200_000);

        Assert.Equal(1, Lane(world).Lines); // an enemy of the bot's faction in range counts as a listener
    }

    // --- the per-player cap ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TenBotsArrivingAroundOnePlayer_GiveThatPlayerAtMostOneLineIn30Minutes_AcrossAllBots()
    {
        await using SocialWorld world = await StartAsync(new FakeBotChatClient(), options => options.Chat.Safety.RequireOptIn = true);
        await StartEventAsync(world, WinterVeil);
        string[] bots = [.. Enumerable.Range(0, 12).Select(i => "Capbot" + (char)('a' + i))];
        foreach (string bot in bots) await world.AddBotAsync(bot);
        Eager(world);
        await using WorldTestClient listener = await world.AddPlayerAsync("Caplisten");
        await HelpTestKit.StandAsync(world, bots[0], ("Caplisten", 8f));

        // Ten bots arrive in one pass.
        for (int i = 0; i < 10; i++) Assert.True(await ClaimAsync(world, bots[i], WinterVeil, 5_000, anchorGuid: 5000 + (ulong)(i % 2)));
        await RunLaneAsync(world, 5_000);
        Assert.Equal(1, Lane(world).Lines);
        Assert.True(Lane(world).Suppressed("listener_cap") >= 1);

        // 29 minutes later another bot arrives: the player was spoken to less than 30 minutes ago.
        Assert.True(await ClaimAsync(world, bots[10], WinterVeil, 5_000 + (29 * Minute)));
        await RunLaneAsync(world, 5_000 + (29 * Minute));
        Assert.Equal(1, Lane(world).Lines);

        // Past the 30 minutes the next arrival may speak.
        Assert.True(await ClaimAsync(world, bots[11], WinterVeil, 5_000 + (31 * Minute)));
        await RunLaneAsync(world, 5_000 + (31 * Minute));
        Assert.Equal(2, Lane(world).Lines);
    }

    [Fact]
    public async Task AnOpeningOfTheInitiative_ChargesThePlayerForTheFestivalLineToo_AndTheOtherWayRound()
    {
        // D2: festival lines and openings together reach a player at most once per 30 minutes (one ledger, not two).
        await using SocialWorld world = await StartAsync(new FakeBotChatClient(), options => options.Chat.Safety.RequireOptIn = true);
        await StartEventAsync(world, WinterVeil);
        (_, string bot) = await BotAsync(world, "Ledgerbot");
        Eager(world);
        await using WorldTestClient listener = await world.AddPlayerAsync("Ledgerlisten");
        await HelpTestKit.StandAsync(world, bot, ("Ledgerlisten", 8f));
        var player = await world.Host.PlayerStateAsync("Ledgerlisten", p => p.Guid);

        // The initiative has just spoken to them (its ledger says so): the festival line waits.
        await world.Host.OnWorldAsync(() => SocialTestKit.Feature(world.Host).Initiative.NoteAddressed(player, 1_000, openingCooldownMinutes: 30));
        Assert.True(await ClaimAsync(world, bot, WinterVeil, 10_000));
        await RunLaneAsync(world, 10_000);
        Assert.Equal(0, Lane(world).Lines);
        Assert.True(Lane(world).Suppressed("listener_cap") >= 1);

        // Past it, the line is said, and charges the same ledger.
        long later = 1_000 + (31 * Minute);
        Assert.True(await ClaimAsync(world, bot, WinterVeil, later));
        await RunLaneAsync(world, later);
        Assert.Equal(1, Lane(world).Lines);
        Assert.False(await world.Host.OnWorldAsync(() => SocialTestKit.Feature(world.Host).Initiative.MayAddress(player, later + Minute)));
        Assert.True(await world.Host.OnWorldAsync(() => SocialTestKit.Feature(world.Host).Initiative.NextOpeningFor(player)) >= later + (30 * Minute));
    }

    // --- ai off, cut-off and ignore --------------------------------------------------------------------------------------

    [Fact]
    public async Task AListenerWhoSaidAiOff_GetsABuiltinLine_AndTheModelGetsNoCall_WhileAnotherListenerGetsTheModel()
    {
        var model = new FakeBotChatClient();
        // The same bot arrives twice 199 seconds apart: its own gap is shortened so it is not what holds the second line back.
        // (The speech limiter keeps its own bot gap on the real chat clock, which the two arrivals, seconds apart in real time, would also hit.)
        await using SocialWorld world = await StartAsync(model, options => { options.Social.Events.Holidays.TalkBotGapMinutes = 1; options.Chat.Speech.BotGapSeconds = 0; });
        await StartEventAsync(world, WinterVeil);
        (ulong botGuid, string bot) = await BotAsync(world, "Aioffbot");
        Eager(world);
        await using WorldTestClient optedOut = await world.AddPlayerAsync("Aioffplayer");
        await HelpTestKit.StandAsync(world, bot, ("Aioffplayer", 8f));
        await AiOffAsync(world, "Aioffplayer");

        Assert.True(await ClaimAsync(world, bot, WinterVeil, 1_000));
        await RunLaneAsync(world, 1_000);
        Assert.Equal(1, Lane(world).Lines);
        ChatMessage builtin = await SayFromAsync(optedOut, botGuid);
        Assert.NotEqual("fake reply", builtin.Text);
        Assert.Empty(model.Calls);

        // The control: the same arrival with the opted-out player gone and an ordinary listener in range asks the model.
        await optedOut.DisposeAsync();
        await world.Host.WaitForWorldAsync(() => world.Host.World.FindOnlinePlayer("Aioffplayer") is null, "the opted-out player to leave");
        await using WorldTestClient ordinary = await world.AddPlayerAsync("Aionplayer");
        await HelpTestKit.StandAsync(world, bot, ("Aionplayer", 8f));
        Assert.True(await ClaimAsync(world, bot, WinterVeil, 200_000));
        await RunLaneAsync(world, 200_000);

        Assert.Equal(2, Lane(world).Lines);
        Assert.Equal("fake reply", (await SayFromAsync(ordinary, botGuid)).Text);
        Assert.Single(model.Calls);
    }

    [Fact]
    public async Task AListenerWhoIsCutOff_HearsNoFestivalLine_AndNeitherDoTheOthersAroundThem()
    {
        await using SocialWorld world = await StartAsync(new FakeBotChatClient(), options => options.Chat.Safety.StrikesBeforeCutoff = 1);
        await StartEventAsync(world, WinterVeil);
        (_, string bot) = await BotAsync(world, "Cutbot");
        Eager(world);
        await using WorldTestClient cut = await world.AddPlayerAsync("Cutplayer");
        await using WorldTestClient other = await world.AddPlayerAsync("Cutother");
        await HelpTestKit.StandAsync(world, bot, ("Cutplayer", 6f), ("Cutother", 9f));
        await CutOffAsync(world, "Cutplayer");

        Assert.True(await ClaimAsync(world, bot, WinterVeil, 1_000));
        await RunLaneAsync(world, 1_000);

        Assert.Equal(0, Lane(world).Lines);
        Assert.True(Lane(world).Suppressed("cut_off") >= 1);
        Assert.Empty(await SaysAsync(other));
    }

    [Fact]
    public async Task AListenerWhoIgnoresTheBot_HearsNoFestivalLine()
    {
        await using SocialWorld world = await StartAsync(new FakeBotChatClient());
        await StartEventAsync(world, WinterVeil);
        (_, string bot) = await BotAsync(world, "Ignorebot");
        Eager(world);
        await using WorldTestClient ignorer = await world.AddPlayerAsync("Ignorer");
        await HelpTestKit.StandAsync(world, bot, ("Ignorer", 6f));
        await world.Host.OnWorldAsync(() => SocialTestKit.Social(world.Host).Context.Friends.AddIgnore(HelpTestKit.Online(world, "Ignorer"), bot));

        Assert.True(await ClaimAsync(world, bot, WinterVeil, 1_000));
        await RunLaneAsync(world, 1_000);

        Assert.Equal(0, Lane(world).Lines);
        Assert.True(Lane(world).Suppressed("ignored") >= 1);
    }

    // --- the bot and zone gaps, the switches -----------------------------------------------------------------------------

    [Fact]
    public async Task ABotSaysAtMostOneFestivalLineIn20Minutes_AndAZoneOneIn120Seconds()
    {
        await using SocialWorld world = await StartAsync(new FakeBotChatClient(), options =>
        {
            options.Chat.Safety.RequireOptIn = true;
            options.Social.Initiative.OpeningCooldownMinutes = 1; // so the per-player cap is not what holds the second line back
        });
        await StartEventAsync(world, WinterVeil);
        (_, string first) = await BotAsync(world, "Gapbota");
        (_, string second) = await BotAsync(world, "Gapbotb");
        Eager(world);
        await using WorldTestClient listener = await world.AddPlayerAsync("Gaplisten");
        await HelpTestKit.StandAsync(world, first, ("Gaplisten", 8f));

        Assert.True(await ClaimAsync(world, first, WinterVeil, 0));
        await RunLaneAsync(world, 0);
        Assert.Equal(1, Lane(world).Lines);

        // Another bot of the same zone 90 seconds later: the zone's 120 second gap.
        Assert.True(await ClaimAsync(world, second, WinterVeil, 90_000));
        await RunLaneAsync(world, 90_000);
        Assert.Equal(1, Lane(world).Lines);

        // 130 seconds: the zone is free again, and a different bot is not held back by the first one's gap.
        Assert.True(await ClaimAsync(world, second, WinterVeil, 130_000));
        await RunLaneAsync(world, 130_000);
        Assert.Equal(2, Lane(world).Lines);

        // The first bot arrives again 10 minutes in: its own 20 minute gap.
        Assert.True(await ClaimAsync(world, first, WinterVeil, 10 * Minute));
        await RunLaneAsync(world, 10 * Minute);
        Assert.Equal(2, Lane(world).Lines);

        // 21 minutes after its line it may speak again.
        Assert.True(await ClaimAsync(world, first, WinterVeil, 21 * Minute));
        await RunLaneAsync(world, 21 * Minute);
        Assert.Equal(3, Lane(world).Lines);
    }

    [Fact]
    public async Task WithTalkOffOrTheFeatureOff_NoLineIsSaid_AndWithItBackOnTheNextArrivalSpeaks()
    {
        await using SocialWorld world = await StartAsync(new FakeBotChatClient(), options => options.Chat.Safety.RequireOptIn = true);
        await StartEventAsync(world, WinterVeil);
        (_, string bot) = await BotAsync(world, "Switchbot");
        Eager(world);
        await using WorldTestClient listener = await world.AddPlayerAsync("Switchlisten");
        await HelpTestKit.StandAsync(world, bot, ("Switchlisten", 8f));
        PlayerbotOptions options = world.Options;

        long at = 0;
        foreach (Action<bool> flip in new Action<bool>[]
        {
            on => options.Social.Events.Holidays.TalkEnabled = on,
            on => options.Social.Events.Holidays.Enabled = on,
            on => options.Social.Events.Enabled = on,
        })
        {
            at += 100 * Minute;
            int before = Lane(world).Lines; // Lines counts for the whole test: each switch is judged by what it adds
            await world.Host.OnWorldAsync(() => flip(false));
            Assert.True(await ClaimAsync(world, bot, WinterVeil, at));
            await RunLaneAsync(world, at);
            Assert.Equal(before, Lane(world).Lines);

            at += 100 * Minute;
            await world.Host.OnWorldAsync(() => flip(true));
            Assert.True(await ClaimAsync(world, bot, WinterVeil, at));
            await RunLaneAsync(world, at);
            Assert.Equal(before + 1, Lane(world).Lines);
        }
    }

    [Fact]
    public async Task ABotWhoseFestivalHasEnded_SaysNothing()
    {
        await using SocialWorld world = await StartAsync(new FakeBotChatClient(), options => options.Chat.Safety.RequireOptIn = true);
        await StartEventAsync(world, WinterVeil);
        (_, string bot) = await BotAsync(world, "Overbot");
        Eager(world);
        await using WorldTestClient listener = await world.AddPlayerAsync("Overlisten");
        await HelpTestKit.StandAsync(world, bot, ("Overlisten", 8f));
        Assert.True(await ClaimAsync(world, bot, WinterVeil, 1_000));
        await StopEventAsync(world, WinterVeil);

        await RunLaneAsync(world, 1_000);

        Assert.Equal(0, Lane(world).Lines);
    }
}
