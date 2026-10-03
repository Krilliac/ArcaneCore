using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Social;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Social;

/// <summary>
/// The chat mute and anti-flood gate over the real world socket (vmangos ChatHandler.cpp:221-247, :417-430;
/// MasterPlayerChat.cpp:10-37). The clock is frozen, so every message of a test falls in the same second.
/// </summary>
public sealed class ChatRestrictionEndToEndTests
{
    private static WorldTestHost Start(ManualQuestClock clock)
        => WorldTestHost.Start(configureServices: services => services.AddSingleton<TimeProvider>(clock));

    private static async Task<string> NotificationAsync(WorldTestClient client)
    {
        var reader = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgNotification));
        return reader.ReadCString();
    }

    [Fact]
    public async Task ElevenSaysInASecond_ArmTheFloodMute_AndTheTwelfthIsRefusedWithTheWaitNotice()
    {
        var clock = new ManualQuestClock();
        await using WorldTestHost host = Start(clock);
        await using WorldTestClient speaker = await host.EnterWorldAsync("SPEAKER", "Speaker");
        await using WorldTestClient listener = await host.EnterWorldAsync("LISTENER", "Listener");
        await speaker.CollectAsync();
        await listener.CollectAsync();

        for (int i = 1; i <= 12; i++)
        {
            await speaker.SendChatAsync(ChatType.Say, Language.Common, "spam " + i);
        }

        Assert.Equal("You must wait 10 Seconds. before speaking again.", await NotificationAsync(speaker));
        List<string> heard = [];
        for (int i = 0; i < 11; i++)
        {
            heard.Add((await listener.ReadChatAsync()).Text);
        }

        Assert.Equal(Enumerable.Range(1, 11).Select(i => "spam " + i), heard); // the twelfth was consumed by the gate
        await listener.AssertSilentAsync(TimeSpan.FromMilliseconds(200));

        // Ten seconds later the speaker may talk again.
        clock.Advance(TimeSpan.FromSeconds(11));
        await speaker.SendChatAsync(ChatType.Say, Language.Common, "back");
        Assert.Equal("back", (await listener.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task AMutedSpeaker_CannotTalkInGuildChat_ButMayWhisperStaff_NotAPlainPlayer()
    {
        var clock = new ManualQuestClock();
        await using WorldTestHost host = Start(clock);
        await using WorldTestClient gm = await host.EnterWorldAsync("STAFF", "Keeper", AccountSecurity.GameMaster);
        await using WorldTestClient muted = await host.EnterWorldAsync("MUTED", "Muted");
        await using WorldTestClient friend = await host.EnterWorldAsync("FRIEND", "Friend");
        SocialFeature social = host.WorldServices.GetRequiredService<SocialFeature>();
        await social.GuildsLoaded.WaitAsync(TimeSpan.FromSeconds(10));
        await host.OnWorldAsync(() =>
        {
            social.Context.Guilds.Create(2, "Arcane", out _);
            social.Context.Guilds.AdminInvite(3, "Arcane");
            host.WorldServices.GetRequiredService<ChatRestrictionFeature>().Service.Mute(2, clock.GetUtcNow().ToUnixTimeSeconds() + 300);
            // Retail: a plain player can only whisper a staff member who accepts whispers (`.whispers on`, vmangos MasterPlayer::AcceptsWhispersFrom).
            host.WorldServices.GetRequiredService<ChatFeature>().SetAcceptWhispers(host.World.FindOnlinePlayer("Keeper")!, true);
        });
        foreach (WorldTestClient client in new[] { gm, muted, friend })
        {
            await client.CollectAsync();
        }

        await muted.SendChatAsync(ChatType.Guild, Language.Common, "hello guild");
        Assert.Equal("You must wait 5 Minutes  before speaking again.", await NotificationAsync(muted)); // secsToTimeString(300): "5 Minutes " then the format's own space
        await friend.AssertSilentAsync(TimeSpan.FromMilliseconds(200));

        await muted.SendChatAsync(ChatType.Whisper, Language.Common, "help me", "Friend");
        Assert.Equal("You must wait 5 Minutes  before speaking again.", await NotificationAsync(muted));
        await friend.AssertSilentAsync(TimeSpan.FromMilliseconds(200));

        await muted.SendChatAsync(ChatType.Whisper, Language.Common, "help me", "Keeper");
        ChatMessage whisper = await gm.ReadChatAsync();
        Assert.Equal((ChatType.Whisper, "help me"), (whisper.Type, whisper.Text));
    }
}
