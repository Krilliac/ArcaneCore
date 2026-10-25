using ArcaneCore.Game;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Tests.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The chat-side seams of P7a H4a (design 4.3 and review item B1): an announcement whose ask says <c>BuiltinOnly</c> is answered by the
/// built-in provider and never reaches a model (the listener who said "ai off"), the safety state is readable by player guid, and the
/// initiative's per-player ledger answers whether a player may be addressed again (one unprompted line per 30 minutes across all bots).
/// </summary>
public sealed class PlayerbotAnnounceBuiltinOnlyTests
{
    private const string Key = "sk-builtin-only-test";

    private sealed class Rig : IAsyncDisposable
    {
        public PlayerbotOptions Options { get; } = new();
        public FakeBotChatClient Client { get; } = new();
        public ManualTime Time { get; } = new();
        public PlayerbotChat Chat { get; }

        public Rig(Action<PlayerbotChatOptions>? configure = null)
        {
            Options.Chat.Providers =
            [
                new PlayerbotChatProviderOptions
                {
                    Kind = PlayerbotChatProviderKind.OpenAICompatible, BaseUrl = "https://api.example.test/v1", Model = "test-model",
                    ApiKeyEnvironmentVariable = "TEST_BUILTIN_ONLY_KEY", InputUsdPerMillionTokens = 0, OutputUsdPerMillionTokens = 0,
                },
            ];
            configure?.Invoke(Options.Chat);
            Options.Validate();
            Chat = new PlayerbotChat(Options, Client, new FakeBotChatEnvironment(new() { ["TEST_BUILTIN_ONLY_KEY"] = Key }), Time, new ListLogger());
        }

        public async Task<BotChatReply?> AnnounceAsync(BotChatAsk ask)
        {
            BotChatStatus before = Chat.Status();
            Assert.Equal(BotChatAdmission.Queued, Chat.TryAnnounce(ask));
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (Chat.DrainReplies() is [var reply, ..]) return reply;
                if (Chat.Status().Dropped > before.Dropped) return null;
                await Task.Delay(10);
            }

            throw new TimeoutException("no reply and no drop");
        }

        public ValueTask DisposeAsync() => Chat.DisposeAsync();
    }

    private static BotChatAsk Festival(bool builtinOnly = false) => new(ChatSamples.BotId, new ObjectGuid(0x7), ChatSamples.Persona(), new ObjectGuid(0x7), "Chatbot",
        ChatType.Say, "Winter Veil is on.", FromMaster: false, InviteAllowed: false, SenderAccountId: 0,
        Topic: new BotChatTopic("guild.welcome", "Welcome Bob, who has just joined your guild Quiet Owls.",
            new Dictionary<string, string> { ["guild"] = "Quiet Owls", ["who"] = "Bob" }, "Quiet Owls"))
    {
        BuiltinOnly = builtinOnly,
    };

    // --- BuiltinOnly -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAskThatIsBuiltinOnly_IsAnsweredByTheBuiltinProvider_AndTheModelGetsNoCall()
    {
        await using var rig = new Rig();

        BotChatReply reply = (await rig.AnnounceAsync(Festival(builtinOnly: true)))!;

        Assert.Equal("Builtin", reply.Provider);
        Assert.Empty(rig.Client.Calls);
        Assert.Contains(PlayerbotSocialTemplates.Lines["guild.welcome"], line => line.Replace("{who}", "Bob").Replace("{guild}", "Quiet Owls") == reply.Text);
    }

    [Fact]
    public async Task TheSameAskWithoutIt_GoesToTheModel_SoTheFlagIsWhatKeepsItAway()
    {
        await using var rig = new Rig();

        BotChatReply reply = (await rig.AnnounceAsync(Festival(builtinOnly: false)))!;

        Assert.Equal("fake reply", reply.Text);
        Assert.Single(rig.Client.Calls);
    }

    [Fact]
    public void TheFlag_DefaultsToFalse_SoEveryExistingAskIsUnchanged()
    {
        Assert.False(ChatSamples.Ask("hello").BuiltinOnly);
        Assert.False(Festival().BuiltinOnly);
    }

    [Fact]
    public async Task BuiltinOnly_OnAnAskThatIsAlreadyBuiltin_ChangesNothing()
    {
        // RequireOptIn already takes the model away from an announcement; the flag must not make that path worse.
        await using var rig = new Rig(chat => chat.Safety.RequireOptIn = true);

        BotChatReply plain = (await rig.AnnounceAsync(Festival(builtinOnly: false)))!;
        BotChatReply flagged = (await rig.AnnounceAsync(Festival(builtinOnly: true)))!;

        Assert.Equal(("Builtin", "Builtin"), (plain.Provider, flagged.Provider));
        Assert.Empty(rig.Client.Calls);
    }

    [Fact]
    public async Task BuiltinOnly_OnOneAsk_DoesNotSpoilTheNextOne()
    {
        await using var rig = new Rig();

        Assert.Equal("Builtin", (await rig.AnnounceAsync(Festival(builtinOnly: true)))!.Provider);
        Assert.Equal("fake reply", (await rig.AnnounceAsync(Festival(builtinOnly: false)))!.Text);
        Assert.Single(rig.Client.Calls);
    }

    // --- the safety lookup by guid ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePlayersChoice_IsReadableByGuid_AsByName()
    {
        await using var rig = new Rig();
        Assert.Null(rig.Chat.Safety.Player(0x43UL)); // nothing is held about an unknown guid

        Assert.Equal(BotChatAdmission.Choice, rig.Chat.TryAsk(ChatSamples.Ask("ai off", sender: 0x43, name: "Optout")));
        rig.Chat.DrainReplies();

        BotChatPlayerSafety byGuid = rig.Chat.Safety.Player(0x43UL)!;
        BotChatPlayerSafety byName = rig.Chat.Safety.Player("Optout")!;
        Assert.Equal(byName, byGuid);
        Assert.Equal((0x43UL, "Optout", false), (byGuid.PlayerGuid, byGuid.PlayerName, byGuid.AiReplies));
        Assert.Null(rig.Chat.Safety.Player(0x44UL));
    }

    [Fact]
    public async Task ThePlayersChoice_ByGuid_FollowsAiOnAgain()
    {
        await using var rig = new Rig();
        rig.Chat.TryAsk(ChatSamples.Ask("ai off", sender: 0x43, name: "Optout"));
        rig.Chat.TryAsk(ChatSamples.Ask("ai on", sender: 0x43, name: "Optout"));
        rig.Chat.DrainReplies();

        Assert.NotEqual(false, rig.Chat.Safety.Player(0x43UL)?.AiReplies);
    }

    // --- the per-player ledger -------------------------------------------------------------------------------------------

    [Fact]
    public void ThePerPlayerLedger_ChargesAPlayerForTheOpeningCooldown_AndFreesThemAfterIt()
    {
        var lane = new PlayerbotInitiativeLane();
        var player = new ObjectGuid(0x1001);
        const long thirtyMinutes = 30 * 60_000L;

        Assert.True(lane.MayAddress(player, 5_000)); // nobody has spoken to them
        lane.NoteAddressed(player, 5_000, openingCooldownMinutes: 30);

        Assert.Equal(5_000 + thirtyMinutes, lane.NextOpeningFor(player));
        Assert.False(lane.MayAddress(player, 5_000));
        Assert.False(lane.MayAddress(player, 5_000 + thirtyMinutes - 1));
        Assert.True(lane.MayAddress(player, 5_000 + thirtyMinutes));
        Assert.True(lane.MayAddress(new ObjectGuid(0x1002), 5_000)); // another player is untouched
    }

    [Fact]
    public void ANoteNeverShortensAnEarlierCharge()
    {
        var lane = new PlayerbotInitiativeLane();
        var player = new ObjectGuid(0x1001);
        lane.NoteAddressed(player, 0, openingCooldownMinutes: 60);
        long until = lane.NextOpeningFor(player);

        lane.NoteAddressed(player, 1_000, openingCooldownMinutes: 1); // would end at 61 000, long before the hour is up

        Assert.Equal(until, lane.NextOpeningFor(player));
        Assert.False(lane.MayAddress(player, 30 * 60_000L));
    }

    [Fact]
    public void ANoteLaterThanTheCharge_MovesItOn()
    {
        var lane = new PlayerbotInitiativeLane();
        var player = new ObjectGuid(0x1001);
        lane.NoteAddressed(player, 0, openingCooldownMinutes: 30);

        lane.NoteAddressed(player, 40 * 60_000L, openingCooldownMinutes: 30);

        Assert.Equal(70 * 60_000L, lane.NextOpeningFor(player));
    }
}
