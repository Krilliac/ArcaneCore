using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>
/// Bot chat in a real world (real handlers and clock, socket clients, a managed bot): whispers, party and say lines that name the
/// bot, the replies as ordinary SMSG_MESSAGECHAT, the master's plain-language orders, the model call off the world thread, the
/// chat-off path, and the GM status command. The model transport is a fake: no network.
/// </summary>
public sealed class PlayerbotChatWorldTests
{
    private const string Secret = "sk-world-SECRET-42";

    private static WorldTestHost Start(FakeBotChatClient client, Action<PlayerbotOptions>? configure = null) => PartyTestHost.Start(options =>
    {
        options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone;
        options.Chat.Enabled = true;
        options.Chat.PerPlayerCooldownSeconds = 0;
        configure?.Invoke(options);
    }, services =>
    {
        services.AddSingleton<IBotChatClient>(client);
        services.AddSingleton<IBotChatEnvironment>(new FakeBotChatEnvironment(new() { ["TEST_CHAT_KEY"] = Secret }));
    });

    private static async Task<ChatMessage> ReadFromBotAsync(WorldTestClient client, ulong bot, ChatType type)
    {
        while (true)
        {
            ChatMessage line = ChatMessage.Parse(await client.ReadUntilAsync(WorldOpcode.SmsgMessagechat));
            if (line.Type == type && line.Sender == bot) return line;
        }
    }

    [Fact]
    public async Task AStrangersWhisper_GetsABuiltinAnswerFromTheBotsState_ByDefault()
    {
        var client = new FakeBotChatClient();
        await using WorldTestHost host = Start(client);
        await PartyTestHost.StartBotAsync(host, "Chatwhis");
        await using WorldTestClient player = await host.EnterWorldAsync("CHATWHISP", "Chatwhisp");
        ulong bot = (await host.PlayerStateAsync("Chatwhis", p => p.Guid)).Value;

        await player.SendChatAsync(ChatType.Whisper, Language.Common, "what level are you?", "Chatwhis");
        ChatMessage reply = await ReadFromBotAsync(player, bot, ChatType.Whisper);

        Assert.Matches(@"\b1\b", reply.Text);
        Assert.NotEqual(PlayerbotChatCommands.PoliteReply, reply.Text);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task ASayLineNamingTheBot_IsAnsweredOutLoud_AndOneThatDoesNotIsIgnored()
    {
        var client = new FakeBotChatClient();
        await using WorldTestHost host = Start(client);
        await PartyTestHost.StartBotAsync(host, "Chatsay");
        await using WorldTestClient player = await host.EnterWorldAsync("CHATSAYP", "Chatsayp");
        await using WorldTestClient bystander = await host.EnterWorldAsync("CHATSAYB", "Chatsayb");
        ulong bot = (await host.PlayerStateAsync("Chatsay", p => p.Guid)).Value;

        await player.SendChatAsync(ChatType.Say, Language.Common, "nice weather today");
        await player.SendChatAsync(ChatType.Say, Language.Common, "Chatsay, are you a bot?");

        ChatMessage heard = await ReadFromBotAsync(bystander, bot, ChatType.Say);
        Assert.Contains(heard.Text, PlayerbotChatTemplates.Lines["bot"]);
        Assert.Equal(1, PartyTestHost.Feature(host).Chat!.Status().Answered);
    }

    [Fact]
    public async Task TheMastersPlainLanguageOrder_DrivesThePartyCommand_AndAStrangersDoesNot()
    {
        var client = new FakeBotChatClient();
        await using WorldTestHost host = Start(client);
        Guid botId = await PartyTestHost.StartBotAsync(host, "Chatparty");
        await using WorldTestClient master = await host.EnterWorldAsync("CHATPARTYM", "Chatpartym");
        await using WorldTestClient stranger = await host.EnterWorldAsync("CHATPARTYS", "Chatpartys");
        ulong bot = (await host.PlayerStateAsync("Chatparty", p => p.Guid)).Value;
        await master.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString("Chatparty"));
        await host.WaitForWorldAsync(() => PartyTestHost.Feature(host).IsPartyDriven(botId), "the party AI drives the bot");

        await master.SendChatAsync(ChatType.Party, Language.Common, "Chatparty, wait here please");
        Assert.Equal("Staying here.", (await PartyTestHost.ReadWhisperFromAsync(master, bot)).Text);
        await host.WaitForWorldAsync(() => PartyTestHost.Feature(host).FindParty(botId)?.Mode == PlayerbotPartyMode.Stay, "stay mode");

        await stranger.SendChatAsync(ChatType.Whisper, Language.Common, "follow me", "Chatparty");
        ChatMessage refused = await ReadFromBotAsync(stranger, bot, ChatType.Whisper);
        Assert.Contains("Chatpartym", refused.Text);
        Assert.Equal(PlayerbotPartyMode.Stay, await host.OnWorldAsync(() => PartyTestHost.Feature(host).FindParty(botId)!.Mode));

        await master.SendChatAsync(ChatType.Whisper, Language.Common, "ok, follow me", "Chatparty");
        Assert.Equal("Following.", (await PartyTestHost.ReadWhisperFromAsync(master, bot)).Text);
        await host.WaitForWorldAsync(() => PartyTestHost.Feature(host).FindParty(botId)?.Mode == PlayerbotPartyMode.Follow, "follow mode");

        await master.SendChatAsync(ChatType.Party, Language.Common, "Chatparty, where are you?");
        ChatMessage answer = await ReadFromBotAsync(master, bot, ChatType.Party);
        Assert.False(string.IsNullOrWhiteSpace(answer.Text));

        // The one-word commands work as before, chat on or off.
        await master.SendChatAsync(ChatType.Whisper, Language.Common, "stay", "Chatparty");
        Assert.Equal("Staying here.", (await PartyTestHost.ReadWhisperFromAsync(master, bot)).Text);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task AModelProvider_IsCalledOffTheWorldThread_AndItsReplyIsSaidThroughTheBot()
    {
        var client = new FakeBotChatClient();
        await using WorldTestHost host = Start(client, options => options.Chat.Providers =
            [new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.Anthropic, ApiKeyEnvironmentVariable = "TEST_CHAT_KEY" }]);
        client.IsWorldThread = () => host.World.IsWorldThread;
        client.Default = (_, _) => Task.FromResult(new BotChatResult(BotChatOutcome.Ok, "{\"reply\": \"Hello from Elwynn|r!\", \"intent\": \"none\"}", 50, 8));
        await PartyTestHost.StartBotAsync(host, "Chatmodel");
        await using WorldTestClient player = await host.EnterWorldAsync("CHATMODELP", "Chatmodelp");
        ulong bot = (await host.PlayerStateAsync("Chatmodel", p => p.Guid)).Value;

        await player.SendChatAsync(ChatType.Whisper, Language.Common, "hey, how is it going?", "Chatmodel");
        ChatMessage reply = await ReadFromBotAsync(player, bot, ChatType.Whisper);

        Assert.Equal("Hello from Elwynn/r!", reply.Text);
        var call = Assert.Single(client.Calls);
        Assert.False(call.OnWorldThread);
        Assert.Equal(Secret, call.Key);
        Assert.Contains("You are Chatmodel, a level 1 ", call.Prompt.DynamicSystem);
        Assert.Contains(" human warrior.", call.Prompt.DynamicSystem);
        Assert.Contains("speaking with Chatmodelp by whisper", call.Prompt.DynamicSystem);
    }

    [Fact]
    public async Task AFailingModel_NeverTouchesTheBot_TheBuiltinAnswerComesInstead()
    {
        var client = new FakeBotChatClient { Default = (_, _) => throw new InvalidOperationException("boom") };
        await using WorldTestHost host = Start(client, options => options.Chat.Providers =
            [new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.Anthropic, ApiKeyEnvironmentVariable = "TEST_CHAT_KEY" }]);
        Guid botId = await PartyTestHost.StartBotAsync(host, "Chatfail");
        await using WorldTestClient player = await host.EnterWorldAsync("CHATFAILP", "Chatfailp");
        ulong bot = (await host.PlayerStateAsync("Chatfail", p => p.Guid)).Value;

        await player.SendChatAsync(ChatType.Whisper, Language.Common, "thanks!", "Chatfail");
        ChatMessage reply = await ReadFromBotAsync(player, bot, ChatType.Whisper);

        Assert.Contains(reply.Text.Replace("Chatfailp", "{player}", StringComparison.Ordinal), PlayerbotChatTemplates.Lines["thanks"]);
        PlayerbotStatus status = PartyTestHost.Feature(host).Snapshot().Single(s => s.BotId == botId);
        Assert.Equal(global::ArcaneCore.Kernel.Characters.ManagedPlayerbotState.Running, status.State);
        Assert.Null(status.ErrorCode);
    }

    [Fact]
    public async Task ChatOff_KeepsTheFixedReplies_AndNeverCallsAProvider()
    {
        var client = new FakeBotChatClient();
        await using WorldTestHost host = Start(client, options =>
        {
            options.Chat.Enabled = false;
            options.Chat.Providers = [new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.Anthropic, ApiKeyEnvironmentVariable = "TEST_CHAT_KEY" }];
        });
        await PartyTestHost.StartBotAsync(host, "Chatoff");
        await using WorldTestClient player = await host.EnterWorldAsync("CHATOFFP", "Chatoffp");
        ulong bot = (await host.PlayerStateAsync("Chatoff", p => p.Guid)).Value;

        await player.SendChatAsync(ChatType.Whisper, Language.Common, "what level are you?", "Chatoff");
        Assert.Equal(PlayerbotChatCommands.PoliteReply, (await ReadFromBotAsync(player, bot, ChatType.Whisper)).Text);
        Assert.Empty(client.Calls);
        Assert.Equal(0, PartyTestHost.Feature(host).Chat!.Status().Answered);
    }

    [Fact]
    public async Task TheGmStatusCommand_ShowsEachProvider_ButNeverTheKey()
    {
        var client = new FakeBotChatClient();
        await using WorldTestHost host = Start(client, options => options.Chat.Providers =
        [
            new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.Anthropic, ApiKeyEnvironmentVariable = "TEST_CHAT_KEY" },
            new PlayerbotChatProviderOptions { Kind = PlayerbotChatProviderKind.OpenAICompatible, BaseUrl = "http://localhost:11434/v1", Model = "qwen3:0.6b", ApiKeyEnvironmentVariable = "" },
        ]);
        await PartyTestHost.Feature(host).StartupAsync(default);
        await using WorldTestClient gm = await host.EnterWorldAsync("CHATGM", "Chatgm", AccountSecurity.GameMaster);
        await using WorldTestClient player = await host.EnterWorldAsync("CHATPLAYER", "Chatplayer");

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".playerbot chat status");
        var lines = new List<string>();
        while (lines.Count < 4)
        {
            ChatMessage line = ChatMessage.Parse(await gm.ReadUntilAsync(WorldOpcode.SmsgMessagechat));
            if (line.Type == ChatType.System && (line.Text.StartsWith("Bot chat:", StringComparison.Ordinal) || line.Text.StartsWith('#'))) lines.Add(line.Text);
        }

        Assert.StartsWith("Bot chat: enabled=yes", lines[0]);
        Assert.StartsWith("#0 Anthropic model=claude-haiku-4-5 key=TEST_CHAT_KEY:present replies-hour=0/120", lines[1]);
        Assert.StartsWith("#1 OpenAICompatible model=qwen3:0.6b key=none", lines[2]);
        Assert.StartsWith("#2 Builtin model=templates key=none", lines[3]);
        Assert.All(lines, line => Assert.DoesNotContain(Secret, line));

        // A player has no access to it.
        await player.SendChatAsync(ChatType.Say, Language.Common, ".playerbot chat status");
        await player.SendChatAsync(ChatType.Say, Language.Common, "done");
        while (true)
        {
            ChatMessage line = ChatMessage.Parse(await player.ReadUntilAsync(WorldOpcode.SmsgMessagechat));
            Assert.False(line.Text.StartsWith("Bot chat:", StringComparison.Ordinal));
            if (line.Type == ChatType.Say && line.Text == "done") break;
        }
    }
}
