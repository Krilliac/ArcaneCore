using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The server-wide "invites off / invites on / invites" store (design lfg-guild S4): parse and normalisation as the chat safety's "ai off", an
/// opt-out that follows the account, honest texts (the choice lasts until the server restarts), one answer per player per ten seconds and
/// <c>AllowOptOut</c> deciding only whether the command is parsed.
/// <para>
/// Contract: <c>PlayerbotInviteOptOut</c> with <c>Handle(characterGuid, accountId, text, nowMs, PlayerbotSocialInviteOptions)</c> returning
/// <c>PlayerbotInviteCommandResult(Consumed, Answer)</c>, <c>ChoiceOf(characterGuid, accountId)</c> (false: opted out, true: said "invites on",
/// null: never chose), <c>IsOptedOut</c>, <c>Count</c>, <c>MaxPlayers</c> and <c>AnswerGapMs</c>.
/// </para>
/// </summary>
public sealed class PlayerbotInviteOptOutTests
{
    private const ulong Character = 0x1001, OtherCharacter = 0x1002;
    private const int Account = 7, OtherAccount = 8;

    private const string OffAnswer = "Okay: bots will not invite you to a group or guild unless you ask. This lasts until the server restarts; whisper \"invites off\" again after that. Whisper \"invites on\" to undo.";
    private const string StatusMayInvite = "Bots may invite you (whisper \"invites off\" to stop it)";
    private const string StatusOptedOut = "Bots will not invite you unless you ask, until the server restarts (whisper \"invites on\" to undo)";

    private static readonly long Later = PlayerbotInviteOptOut.AnswerGapMs + 1;

    private static PlayerbotSocialInviteOptions Options(bool allowOptOut = true) => new() { AllowOptOut = allowOptOut };

    private static PlayerbotInviteCommandResult Say(PlayerbotInviteOptOut store, string text, long nowMs = 0, PlayerbotSocialInviteOptions? options = null,
        ulong character = Character, int account = Account)
        => store.Handle(character, account, text, nowMs, options ?? Options());

    [Fact]
    public void InvitesOff_OptsThePlayerOut_AndAnswersWithTheRestartHonestText()
    {
        var store = new PlayerbotInviteOptOut();
        Assert.False(store.IsOptedOut(Character, Account));
        Assert.Null(store.ChoiceOf(Character, Account));

        PlayerbotInviteCommandResult result = Say(store, "invites off");

        Assert.True(result.Consumed);
        Assert.Equal(OffAnswer, result.Answer);
        Assert.True(store.IsOptedOut(Character, Account));
        Assert.False(store.ChoiceOf(Character, Account));
    }

    [Fact]
    public void InvitesOn_UndoesIt_AndIsAnswered()
    {
        var store = new PlayerbotInviteOptOut();
        Say(store, "invites off");

        PlayerbotInviteCommandResult result = Say(store, "invites on", Later);

        Assert.True(result.Consumed);
        Assert.False(string.IsNullOrWhiteSpace(result.Answer));
        Assert.NotEqual(OffAnswer, result.Answer);
        Assert.False(store.IsOptedOut(Character, Account));
        Assert.True(store.ChoiceOf(Character, Account));
    }

    [Fact]
    public void InvitesAlone_ReportsTheStatus_WithoutChangingIt()
    {
        var store = new PlayerbotInviteOptOut();
        Assert.Equal(StatusMayInvite, Say(store, "invites").Answer);
        Assert.Null(store.ChoiceOf(Character, Account));

        Say(store, "invites off", Later);
        PlayerbotInviteCommandResult status = Say(store, "Invites", 2 * Later);
        Assert.True(status.Consumed);
        Assert.Equal(StatusOptedOut, status.Answer);
        Assert.True(store.IsOptedOut(Character, Account));
    }

    [Theory]
    [InlineData("INVITES OFF")]
    [InlineData("  invites    off  ")]
    [InlineData("Invites Off!")]
    [InlineData("invites, off.")]
    [InlineData("invites off 123")]
    public void TheLine_IsNormalisedToLettersAndSpaces_AsTheAiCommandIs(string text)
    {
        var store = new PlayerbotInviteOptOut();
        Assert.True(Say(store, text).Consumed);
        Assert.True(store.IsOptedOut(Character, Account));
    }

    [Theory]
    [InlineData("invites off please")]
    [InlineData("invitesoff")]
    [InlineData("invite off")]
    [InlineData("inv")]
    [InlineData("ginv")]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("rivals off")]
    [InlineData("turn invites off")]
    public void AnythingElse_IsNotACommand_AndIsNotConsumed(string text)
    {
        var store = new PlayerbotInviteOptOut();
        PlayerbotInviteCommandResult result = Say(store, text);
        Assert.False(result.Consumed);
        Assert.Null(result.Answer);
        Assert.False(store.IsOptedOut(Character, Account));
        Assert.Null(store.ChoiceOf(Character, Account));
    }

    [Fact]
    public void AnOptOut_FollowsTheAccount_ButNotAnotherAccount()
    {
        var store = new PlayerbotInviteOptOut();
        Say(store, "invites off", character: Character, account: Account);

        Assert.True(store.IsOptedOut(OtherCharacter, Account));
        Assert.True(store.IsOptedOut(Character, Account));
        Assert.False(store.IsOptedOut(OtherCharacter, OtherAccount));
    }

    [Fact]
    public void WithoutAnAccountId_TheCharacterStandsAlone()
    {
        var store = new PlayerbotInviteOptOut();
        Say(store, "invites off", character: Character, account: 0);

        Assert.True(store.IsOptedOut(Character, 0));
        Assert.False(store.IsOptedOut(OtherCharacter, 0));
    }

    [Fact]
    public void InvitesOn_FromAnotherCharacterOfTheAccount_LiftsTheAccountsOptOut()
    {
        var store = new PlayerbotInviteOptOut();
        Say(store, "invites off", 0, character: Character, account: Account);
        Say(store, "invites on", Later, character: OtherCharacter, account: Account);

        Assert.False(store.IsOptedOut(Character, Account));
        Assert.False(store.IsOptedOut(OtherCharacter, Account));
    }

    [Fact]
    public void FurtherCommandLines_InsideTheTenSecondGap_AreConsumedWithoutAnAnswer()
    {
        var store = new PlayerbotInviteOptOut();
        Assert.Equal(10_000, PlayerbotInviteOptOut.AnswerGapMs);
        Assert.NotNull(Say(store, "invites", 100_000).Answer);

        foreach (string line in new[] { "invites", "invites on", "invites off" })
        {
            PlayerbotInviteCommandResult inside = Say(store, line, 100_000 + 9_999);
            Assert.True(inside.Consumed, line);
            Assert.Null(inside.Answer);
        }

        Assert.True(store.IsOptedOut(Character, Account)); // the last line inside the gap was applied
        Assert.NotNull(Say(store, "invites", 100_000 + 10_000).Answer);
    }

    [Fact]
    public void TheAnswerGap_IsPerPlayer()
    {
        var store = new PlayerbotInviteOptOut();
        Assert.NotNull(Say(store, "invites", 5_000).Answer);
        Assert.NotNull(Say(store, "invites", 5_001, character: OtherCharacter, account: OtherAccount).Answer);
    }

    [Fact]
    public void WithAllowOptOutOff_TheCommandIsNotParsed_AndALineIsLeftToOrdinaryHandling()
    {
        var store = new PlayerbotInviteOptOut();
        PlayerbotInviteCommandResult result = Say(store, "invites off", options: Options(allowOptOut: false));
        Assert.False(result.Consumed);
        Assert.Null(result.Answer);
        Assert.False(store.IsOptedOut(Character, Account));
    }

    [Fact]
    public void ARecordedOptOut_IsStillHonoured_WhenAllowOptOutIsLaterSwitchedOff()
    {
        var store = new PlayerbotInviteOptOut();
        Say(store, "invites off");

        Assert.False(Say(store, "invites on", Later, Options(allowOptOut: false)).Consumed); // not parsed: cannot be undone either
        Assert.True(store.IsOptedOut(Character, Account));
    }

    [Fact]
    public void TheStore_IsBounded_NeverDropsAnOptOut_AndRefusesANewChoiceWhenFullOfThem()
    {
        var store = new PlayerbotInviteOptOut();
        int max = PlayerbotInviteOptOut.MaxPlayers;
        for (int i = 1; i <= max; i++) Assert.NotNull(store.Handle((ulong)(0x10_0000 + i), 100_000 + i, "invites off", 0, Options()).Answer);
        Assert.Equal(max, store.Count);

        PlayerbotInviteCommandResult refused = store.Handle(0x20_0000, 200_000, "invites off", 0, Options());
        Assert.True(refused.Consumed);
        Assert.NotNull(refused.Answer);
        Assert.DoesNotContain("Okay", refused.Answer);
        Assert.Equal(max, store.Count);
        Assert.False(store.IsOptedOut(0x20_0000, 200_000));
        for (int i = 1; i <= max; i++) Assert.True(store.IsOptedOut((ulong)(0x10_0000 + i), 100_000 + i), $"opt-out {i} was dropped");
    }

    [Fact]
    public void WhenTheStoreIsFull_AnInvitesOnIsDroppedToMakeRoomForAnOptOut()
    {
        var store = new PlayerbotInviteOptOut();
        int max = PlayerbotInviteOptOut.MaxPlayers;
        store.Handle(0x10_0001, 100_001, "invites on", 0, Options());
        for (int i = 2; i <= max; i++) store.Handle((ulong)(0x10_0000 + i), 100_000 + i, "invites off", 0, Options());
        Assert.Equal(max, store.Count);

        Assert.Contains("Okay", store.Handle(0x20_0000, 200_000, "invites off", 0, Options()).Answer);
        Assert.Equal(max, store.Count);
        Assert.True(store.IsOptedOut(0x20_0000, 200_000));
        Assert.Null(store.ChoiceOf(0x10_0001, 100_001));
    }
}
