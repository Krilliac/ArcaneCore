using ArcaneCore.Game;
using ArcaneCore.World.Playerbots;
using ArcaneCore.Game.Npc;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Tests.Playerbots.Chat;
using ArcaneCore.World.Tests.Playerbots.Social;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Events.EventsSocialKit;
using static ArcaneCore.World.Tests.Playerbots.Events.EventsTestWorld;

namespace ArcaneCore.World.Tests.Playerbots.Events;

/// <summary>
/// The celebration emotes of bots held at a festival spot (P7a H4b, design 4.3 "Emotes", review item B3): untargeted, bounded per
/// listener and per anchor, never aimed at a bot, never at a player who ignores or is an enemy. Bots hold their spots by hand-made claims
/// that are renewed each pass; the events lane runs at chosen world times, and the client counts the SMSG_TEXT_EMOTE it receives.
/// </summary>
public sealed class PlayerbotFestivalEmoteWorldTests
{
    private const long Second = 1_000;
    private const long Minute = 60_000;

    private static Task<SocialWorld> StartAsync(Action<PlayerbotOptions>? configure = null)
        => EventsSocialKit.StartAsync(new FakeBotChatClient(), [new FestivalNpc(991_201, "Festival Host", NpcFlags.Gossip, 20f, 0f)], options =>
        {
            options.Chat.Safety.RequireOptIn = true;
            configure?.Invoke(options);
        });

    private static async Task<string[]> HoldBotsAsync(SocialWorld world, int count, string prefix)
    {
        string[] bots = [.. Enumerable.Range(0, count).Select(i => prefix + (char)('a' + i))];
        foreach (string bot in bots) await world.AddBotAsync(bot);
        return bots;
    }

    private static async Task ClaimAllAsync(SocialWorld world, string[] bots, long now)
    {
        for (int i = 0; i < bots.Length; i++) Assert.True(await ClaimAsync(world, bots[i], WinterVeil, now, anchorGuid: 5000 + (ulong)(i % 2)), bots[i]);
    }

    /// <summary>Run the lane every 10 game seconds for <paramref name="duration"/>, renewing every claim each time.</summary>
    private static async Task RunPacedAsync(SocialWorld world, long from, long duration)
    {
        for (long t = from; t <= from + duration; t += 10 * Second)
        {
            await RenewAllAsync(world, t);
            await RunLaneAsync(world, t);
        }
    }

    private static async Task<List<byte[]>> TextEmotesAsync(WorldTestClient client)
        => [.. (await client.CollectAsync(TimeSpan.FromMilliseconds(500))).Where(p => p.Opcode == WorldOpcode.SmsgTextEmote).Select(p => p.Payload)];

    [Fact]
    public async Task TwelveBotsAroundTwoAnchors_GiveOneListenerAtMostTenTextEmotesIn5Minutes_AndTheBotsDoDispatchSome()
    {
        await using SocialWorld world = await StartAsync(options => options.Social.Events.Holidays.TalkEnabled = false);
        await StartEventAsync(world, WinterVeil);
        string[] bots = await HoldBotsAsync(world, 12, "Emoteb");
        await using WorldTestClient listener = await world.AddPlayerAsync("Emotelisten");
        await HelpTestKit.StandAsync(world, bots[0], ("Emotelisten", 10f));
        Lane(world).RollOverride = (_, _) => 0.0;
        await ClaimAllAsync(world, bots, 0);

        await RunPacedAsync(world, 0, 5 * Minute);

        List<byte[]> received = await TextEmotesAsync(listener);
        Assert.InRange(received.Count, 1, 10);
        Assert.True(world.Log.Count(WorldOpcode.CmsgTextEmote) > 0, "the bots dispatched no CMSG_TEXT_EMOTE: a silent lane would pass the bound");
        Assert.Equal(Lane(world).EmotesSent, world.Log.Count(WorldOpcode.CmsgTextEmote));
        Assert.All(received, payload => Assert.False(TextEmoteHasTarget(payload)));
    }

    [Fact]
    public async Task WithTheListenerBudgetAtTwoAMinute_NoMoreThanTwoEmotesReachThePlayerInAnyMinute()
    {
        await using SocialWorld world = await StartAsync(options => options.Social.Events.Holidays.TalkEnabled = false);
        await StartEventAsync(world, WinterVeil);
        string[] bots = await HoldBotsAsync(world, 12, "Minuteb");
        await using WorldTestClient listener = await world.AddPlayerAsync("Minutelisten");
        await HelpTestKit.StandAsync(world, bots[0], ("Minutelisten", 10f));
        Lane(world).RollOverride = (_, _) => 0.0;
        await ClaimAllAsync(world, bots, 0);

        int previous = 0;
        for (int minute = 0; minute < 5; minute++)
        {
            await RunPacedAsync(world, minute * Minute, 50 * Second); // six passes inside the minute
            int sent = Lane(world).EmotesSent;
            Assert.True(sent - previous <= 2, $"minute {minute}: {sent - previous} festival emotes");
            previous = sent;
        }

        Assert.True(previous > 0);
    }

    [Fact]
    public async Task WithNoListenerInRange_TheAnchorBudgetStillBoundsTheEmotes_ToThreeAMinute()
    {
        await using SocialWorld world = await StartAsync(options =>
        {
            options.Social.Events.Holidays.TalkEnabled = false;
            options.Social.Events.Holidays.EmotesPerAnchorMinute = 3;
        });
        await StartEventAsync(world, WinterVeil);
        string[] bots = await HoldBotsAsync(world, 12, "Anchorb");
        Lane(world).RollOverride = (_, _) => 0.0;
        for (int i = 0; i < bots.Length; i++) Assert.True(await ClaimAsync(world, bots[i], WinterVeil, 0, anchorGuid: 5000)); // one anchor

        int previous = 0;
        for (int minute = 0; minute < 3; minute++)
        {
            await RunPacedAsync(world, minute * Minute, 50 * Second);
            int sent = Lane(world).EmotesSent;
            Assert.True(sent - previous <= 3, $"minute {minute}: {sent - previous} emotes at one anchor");
            previous = sent;
        }

        Assert.True(previous > 0);
    }

    [Fact]
    public async Task AListenerBudgetOfZero_SendsNoEmoteWhileAPlayerIsInRange()
    {
        await using SocialWorld world = await StartAsync(options => options.Social.Events.Holidays.EmotesPerListenerMinute = 0);
        await StartEventAsync(world, WinterVeil);
        string[] bots = await HoldBotsAsync(world, 4, "Zerob");
        await using WorldTestClient listener = await world.AddPlayerAsync("Zerolisten");
        await HelpTestKit.StandAsync(world, bots[0], ("Zerolisten", 10f));
        Lane(world).RollOverride = (_, _) => 0.0;
        await ClaimAllAsync(world, bots, 0);

        await RunPacedAsync(world, 0, 5 * Minute);

        Assert.Empty(await TextEmotesAsync(listener));
        Assert.Equal(0, Lane(world).EmotesSent);
    }

    [Fact]
    public async Task TwoBotsHeldSideBySideForSixGameHours_GainNoRelationToEachOther_AndEmoteNeitherAtTheOther()
    {
        await using SocialWorld world = await StartAsync(options => options.Social.Events.Holidays.TalkEnabled = false);
        await StartEventAsync(world, WinterVeil);
        string[] bots = await HoldBotsAsync(world, 2, "Pair");
        Lane(world).RollOverride = (_, _) => 0.0;
        await ClaimAllAsync(world, bots, 0);
        ObjectGuid first = await world.Host.PlayerStateAsync(bots[0], p => p.Guid);
        ObjectGuid second = await world.Host.PlayerStateAsync(bots[1], p => p.Guid);

        for (long hour = 0; hour < 6; hour++) await RunPacedAsync(world, hour * 60 * Minute, 59 * Minute);

        Assert.True(Lane(world).EmotesSent > 0, "the lane was silent for six hours, so the relation check proves nothing");
        PlayerbotSocialMemory memory = HelpTestKit.RecorderOf(world).Memory;
        Guid firstId = world.Bots[bots[0]];
        Guid secondId = world.Bots[bots[1]];
        Assert.False(memory.TryGet(firstId, second.Value, out _));
        Assert.False(memory.TryGet(secondId, first.Value, out _));
        Assert.Equal(0, world.Log.Count(WorldOpcode.CmsgTextEmote) - Lane(world).EmotesSent); // every dispatched emote is one the lane counted
    }

    [Fact]
    public async Task AnEnemyFactionClientAndAnIgnoringClient_ReceiveNoTargetedEmote()
    {
        await using SocialWorld world = await StartAsync(options => options.Social.Events.Holidays.TalkEnabled = false);
        await StartEventAsync(world, WinterVeil);
        string[] bots = await HoldBotsAsync(world, 3, "Foeb");
        await using WorldTestClient orc = await world.AddPlayerAsync("Foeorc", race: SocialTestKit.Orc);
        await using WorldTestClient ignorer = await world.AddPlayerAsync("Foeignorer");
        await HelpTestKit.StandAsync(world, bots[0], ("Foeorc", 5f), ("Foeignorer", 6f));
        foreach (string bot in bots)
            await world.Host.OnWorldAsync(() => SocialTestKit.Social(world.Host).Context.Friends.AddIgnore(HelpTestKit.Online(world, "Foeignorer"), bot));
        Lane(world).RollOverride = (_, _) => 0.0;
        await ClaimAllAsync(world, bots, 0);

        await RunPacedAsync(world, 0, 5 * Minute);

        Assert.True(Lane(world).EmotesSent > 0);
        Assert.All(await TextEmotesAsync(orc), payload => Assert.False(TextEmoteHasTarget(payload)));
        Assert.All(await TextEmotesAsync(ignorer), payload => Assert.False(TextEmoteHasTarget(payload)));
    }
}
