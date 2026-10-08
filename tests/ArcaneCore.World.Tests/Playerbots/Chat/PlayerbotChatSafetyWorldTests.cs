using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>
/// Bot chat safety in a real world (real handlers, socket clients, a managed bot, a fake model transport): the disclosure as a system
/// message before the first reply, a flagged whisper kept from the provider, "ai off", the GM flags / pardon / status commands, and
/// the automatic mute through the account mute of <c>.mute</c>.
/// </summary>
public sealed class PlayerbotChatSafetyWorldTests
{
    private static WorldTestHost Start(FakeBotChatClient client, Action<PlayerbotOptions>? configure = null) => PartyTestHost.Start(options =>
    {
        options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone;
        options.Chat.Enabled = true;
        options.Chat.PerPlayerCooldownSeconds = 0;
        options.Chat.Providers = [new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.Anthropic, ApiKeyEnvironmentVariable = "TEST_CHAT_KEY" }];
        configure?.Invoke(options);
    }, services =>
    {
        services.AddSingleton<IBotChatClient>(client);
        services.AddSingleton<IBotChatEnvironment>(new FakeBotChatEnvironment(new() { ["TEST_CHAT_KEY"] = "sk-world-safety" }));
    });

    /// <summary>Every chat line up to and including the bot's next whisper to <paramref name="client"/>.</summary>
    private static async Task<List<ChatMessage>> ReadThroughWhisperAsync(WorldTestClient client, ulong bot)
    {
        var lines = new List<ChatMessage>();
        while (true)
        {
            ChatMessage line = ChatMessage.Parse(await client.ReadUntilAsync(WorldOpcode.SmsgMessagechat));
            lines.Add(line);
            if (line.Type == ChatType.Whisper && line.Sender == bot) return lines;
        }
    }

    private static async Task<List<string>> GmLinesAsync(WorldTestClient gm, string command, Func<string, bool> wanted, int count)
    {
        await gm.SendChatAsync(ChatType.Say, Language.Common, command);
        var lines = new List<string>();
        while (lines.Count < count)
        {
            ChatMessage line = ChatMessage.Parse(await gm.ReadUntilAsync(WorldOpcode.SmsgMessagechat));
            if (line.Type == ChatType.System && wanted(line.Text)) lines.Add(line.Text);
        }

        return lines;
    }

    [Fact]
    public async Task TheDisclosureComesOnce_AFlaggedWhisperNeverReachesTheModel_AndTheGmSeesAndPardonsIt()
    {
        var client = new FakeBotChatClient();
        await using WorldTestHost host = Start(client);
        await PartyTestHost.StartBotAsync(host, "Chatsafe");
        await using WorldTestClient player = await host.EnterWorldAsync("CHATSAFEP", "Chatsafep");
        await using WorldTestClient gm = await host.EnterWorldAsync("CHATSAFEGM", "Chatsafegm", AccountSecurity.GameMaster);
        ulong bot = (await host.PlayerStateAsync("Chatsafe", p => p.Guid)).Value;
        string disclosure = new PlayerbotChatSafetyOptions().DisclosureText;

        await player.SendChatAsync(ChatType.Whisper, Language.Common, "hello there", "Chatsafe");
        List<ChatMessage> first = await ReadThroughWhisperAsync(player, bot);
        ChatMessage notice = Assert.Single(first, line => line.Type == ChatType.System && line.Text.StartsWith(disclosure, StringComparison.Ordinal));
        Assert.Contains("\"ai off\"", notice.Text);
        Assert.Equal("fake reply", first[^1].Text);
        Assert.True(first.IndexOf(notice) < first.Count - 1); // before the reply

        await player.SendChatAsync(ChatType.Whisper, Language.Common, "kys", "Chatsafe");
        List<ChatMessage> second = await ReadThroughWhisperAsync(player, bot);
        Assert.DoesNotContain(second, line => line.Type == ChatType.System && line.Text.StartsWith(disclosure, StringComparison.Ordinal));
        Assert.Contains(second[^1].Text, PlayerbotChatTemplates.Lines["abuse"]);
        Assert.Single(client.Calls); // only "hello there" went to the model
        Assert.DoesNotContain(client.Calls, call => call.Prompt.Messages.Any(m => m.Text.Contains("kys", StringComparison.Ordinal)));

        List<string> flags = await GmLinesAsync(gm, ".playerbot chat flags Chatsafep", text => text.StartsWith("Chatsafep", StringComparison.Ordinal)
            || text.Contains("-> Chatsafe:", StringComparison.Ordinal), 2);
        Assert.StartsWith("Chatsafep: strikes=1 cut-off=no", flags[0]);
        Assert.Contains("Chatsafep (guid ", flags[1]);
        Assert.EndsWith("-> Chatsafe: Harassment (local filter) \"kys\"", flags[1]);

        List<string> pardon = await GmLinesAsync(gm, ".playerbot chat pardon Chatsafep", text => text.StartsWith("Chatsafep", StringComparison.Ordinal), 1);
        Assert.Equal("Chatsafep: bot chat strikes and cut-off cleared.", pardon[0]);

        List<string> status = await GmLinesAsync(gm, ".playerbot chat status", text => text.StartsWith("Bot chat safety:", StringComparison.Ordinal), 1);
        Assert.StartsWith("Bot chat safety: screening=on screened=2 flagged=1 ", status[0]);
        Assert.Contains(" disclosed=1 ", status[0]);

        // "ai off": confirmed by the bot, then built-in replies only.
        await player.SendChatAsync(ChatType.Whisper, Language.Common, "ai off", "Chatsafe");
        Assert.Contains("built-in replies only", (await ReadThroughWhisperAsync(player, bot))[^1].Text);
        await player.SendChatAsync(ChatType.Whisper, Language.Common, "what level are you?", "Chatsafe");
        Assert.Matches(@"\b1\b", (await ReadThroughWhisperAsync(player, bot))[^1].Text);
        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task AutoMute_MutesTheAccount_WhenAPlayerIsCutOff()
    {
        var client = new FakeBotChatClient();
        await using WorldTestHost host = Start(client, options =>
        {
            options.Chat.Safety.StrikesBeforeCutoff = 1;
            options.Chat.Safety.AutoMute = true;
            options.Chat.Safety.AutoMuteMinutes = 20;
        });
        await PartyTestHost.StartBotAsync(host, "Chatmute");
        await using WorldTestClient player = await host.EnterWorldAsync("CHATMUTEP", "Chatmutep");
        ulong bot = (await host.PlayerStateAsync("Chatmute", p => p.Guid)).Value;
        int account = await host.PlayerStateAsync("Chatmutep", p => p.AccountId);
        GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();
        Assert.Null(audit.MuteOf(account));

        await player.SendChatAsync(ChatType.Whisper, Language.Common, "go kill yourself", "Chatmute");
        List<ChatMessage> lines = await ReadThroughWhisperAsync(player, bot);
        await host.WaitForWorldAsync(() => audit.MuteOf(account) is not null, "the automatic mute");

        var mute = audit.MuteOf(account)!;
        Assert.Equal("Bot chat safety", mute.MutedBy);
        Assert.InRange(mute.MutedUntil - mute.MutedAt, 20 * 60 - 1, 20 * 60 + 1);
        static bool Told(ChatMessage line) => line.Type == ChatType.System && line.Text.StartsWith("Your chat has been disabled", StringComparison.Ordinal);
        while (!lines.Any(Told)) lines.Add(ChatMessage.Parse(await player.ReadUntilAsync(WorldOpcode.SmsgMessagechat)));

        Assert.Empty(client.Calls);
    }
}
