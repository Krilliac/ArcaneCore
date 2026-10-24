using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The server-wide "rivals off / rivals on / rivals" store of P6a (design 4.3): parse and normalisation as the chat safety's "ai off", an opt-out
/// that follows the account, honest texts (the choice lasts until the server restarts), one answer per player per ten seconds, <c>AllowOptOut</c>
/// deciding only whether the command is parsed, and the once-per-login disclosure.
/// <para>
/// Contract: <c>PlayerbotRivalryOptOut</c> with <c>Handle(characterGuid, accountId, text, nowMs, PlayerbotSocialRivalryOptions)</c> returning
/// <c>PlayerbotRivalryCommandResult(Consumed, Answer)</c>, <c>ChoiceOf(characterGuid, accountId)</c> (false: opted out, true: said "rivals on",
/// null: never chose), <c>IsOptedOut</c>, <c>TakeDisclosure(characterGuid, accountId, login)</c>, <c>Count</c>, <c>MaxPlayers</c> and <c>AnswerGapMs</c>.
/// </para>
/// </summary>
public sealed class PlayerbotRivalryOptOutTests
{
    private const ulong Character = 0x1001, OtherCharacter = 0x1002;
    private const int Account = 7, OtherAccount = 8;

    // The texts of design 4.3 (the restart clause is dropped when the store is persisted, from P6c).
    private const string OffAnswer = "Okay: bots will not taunt you. This lasts until the server restarts; whisper \"rivals off\" again after that. Whisper \"rivals on\" to undo.";
    private const string StatusMayTaunt = "Bots may taunt you (whisper \"rivals off\" to stop it)";
    private const string StatusOptedOut = "Bots will not taunt you until the server restarts (whisper \"rivals on\" to undo)";
    private const string Disclosure = "A bot rival taunted you. Whisper \"rivals off\" to any bot of your faction to stop bot taunts.";

    private static readonly long Later = PlayerbotRivalryOptOut.AnswerGapMs + 1;

    private static PlayerbotSocialRivalryOptions Options(bool allowOptOut = true, bool requireOptIn = false)
    {
        var options = new PlayerbotSocialRivalryOptions { AllowOptOut = allowOptOut };
        options.Emotes.RudeRequiresOptIn = requireOptIn;
        return options;
    }

    private static PlayerbotRivalryCommandResult Say(PlayerbotRivalryOptOut store, string text, long nowMs = 0, PlayerbotSocialRivalryOptions? options = null,
        ulong character = Character, int account = Account)
        => store.Handle(character, account, text, nowMs, options ?? Options());

    [Fact]
    public void RivalsOff_OptsThePlayerOut_AndAnswersWithTheRestartHonestText()
    {
        var store = new PlayerbotRivalryOptOut();
        Assert.False(store.IsOptedOut(Character, Account));
        Assert.Null(store.ChoiceOf(Character, Account));

        PlayerbotRivalryCommandResult result = Say(store, "rivals off");

        Assert.True(result.Consumed);
        Assert.Equal(OffAnswer, result.Answer);
        Assert.True(store.IsOptedOut(Character, Account));
        Assert.False(store.ChoiceOf(Character, Account));
    }

    [Fact]
    public void RivalsOn_UndoesIt_AndIsAnswered()
    {
        var store = new PlayerbotRivalryOptOut();
        Say(store, "rivals off");

        PlayerbotRivalryCommandResult result = Say(store, "rivals on", Later);

        Assert.True(result.Consumed);
        Assert.False(string.IsNullOrWhiteSpace(result.Answer));
        Assert.NotEqual(OffAnswer, result.Answer);
        Assert.False(store.IsOptedOut(Character, Account));
        Assert.True(store.ChoiceOf(Character, Account));
    }

    [Fact]
    public void RivalsAlone_ReportsTheStatus_WithoutChangingIt()
    {
        var store = new PlayerbotRivalryOptOut();
        Assert.Equal(StatusMayTaunt, Say(store, "rivals").Answer);
        Assert.Null(store.ChoiceOf(Character, Account));

        Say(store, "rivals off", Later);
        PlayerbotRivalryCommandResult status = Say(store, "Rivals", 2 * Later);
        Assert.True(status.Consumed);
        Assert.Equal(StatusOptedOut, status.Answer);
        Assert.True(store.IsOptedOut(Character, Account));
    }

    [Theory]
    [InlineData("RIVALS OFF")]
    [InlineData("  rivals    off  ")]
    [InlineData("Rivals Off!")]
    [InlineData("rivals, off.")]
    [InlineData("rivals off 123")]
    public void TheLine_IsNormalisedToLettersAndSpaces_AsTheAiCommandIs(string text)
    {
        var store = new PlayerbotRivalryOptOut();
        Assert.True(Say(store, text).Consumed);
        Assert.True(store.IsOptedOut(Character, Account));
    }

    [Theory]
    [InlineData("rivals off please")]
    [InlineData("rivalsoff")]
    [InlineData("rival off")]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData("ai off")]
    [InlineData("turn rivals off")]
    public void AnythingElse_IsNotACommand_AndIsNotConsumed(string text)
    {
        var store = new PlayerbotRivalryOptOut();
        PlayerbotRivalryCommandResult result = Say(store, text);
        Assert.False(result.Consumed);
        Assert.Null(result.Answer);
        Assert.False(store.IsOptedOut(Character, Account));
        Assert.Null(store.ChoiceOf(Character, Account));
    }

    [Fact]
    public void AnOptOut_FollowsTheAccount_ButNotAnotherAccount()
    {
        var store = new PlayerbotRivalryOptOut();
        Say(store, "rivals off", character: Character, account: Account);

        Assert.True(store.IsOptedOut(OtherCharacter, Account)); // another character of the same account
        Assert.True(store.IsOptedOut(Character, Account));
        Assert.False(store.IsOptedOut(OtherCharacter, OtherAccount));
    }

    [Fact]
    public void RivalsOn_FromAnotherCharacterOfTheAccount_LiftsTheAccountsOptOut()
    {
        var store = new PlayerbotRivalryOptOut();
        Say(store, "rivals off", 0, character: Character, account: Account);
        Say(store, "rivals on", Later, character: OtherCharacter, account: Account);

        Assert.False(store.IsOptedOut(Character, Account));
        Assert.False(store.IsOptedOut(OtherCharacter, Account));
    }

    [Fact]
    public void FurtherCommandLines_InsideTheTenSecondGap_AreConsumedWithoutAnAnswer()
    {
        var store = new PlayerbotRivalryOptOut();
        Assert.Equal(10_000, PlayerbotRivalryOptOut.AnswerGapMs);
        Assert.NotNull(Say(store, "rivals", 100_000).Answer);

        foreach (string line in new[] { "rivals", "rivals on", "rivals off" })
        {
            PlayerbotRivalryCommandResult inside = Say(store, line, 100_000 + 9_999);
            Assert.True(inside.Consumed, line);
            Assert.Null(inside.Answer);
        }

        // At exactly ten seconds the next one is answered again.
        Assert.NotNull(Say(store, "rivals", 100_000 + 10_000).Answer);
    }

    [Fact]
    public void TheAnswerGap_IsPerPlayer()
    {
        var store = new PlayerbotRivalryOptOut();
        Assert.NotNull(Say(store, "rivals", 5_000).Answer);
        Assert.NotNull(Say(store, "rivals", 5_001, character: OtherCharacter, account: OtherAccount).Answer);
    }

    [Fact]
    public void WithAllowOptOutOff_TheCommandIsNotParsed_AndALineIsLeftToTodaysHandling()
    {
        var store = new PlayerbotRivalryOptOut();
        PlayerbotRivalryCommandResult result = Say(store, "rivals off", options: Options(allowOptOut: false));
        Assert.False(result.Consumed);
        Assert.Null(result.Answer);
        Assert.False(store.IsOptedOut(Character, Account));
    }

    [Fact]
    public void ARecordedOptOut_IsStillHonoured_WhenAllowOptOutIsLaterSwitchedOff()
    {
        var store = new PlayerbotRivalryOptOut();
        Say(store, "rivals off");
        PlayerbotSocialRivalryOptions off = Options(allowOptOut: false);

        Assert.False(Say(store, "rivals on", Later, off).Consumed); // not parsed: cannot be undone either
        Assert.True(store.IsOptedOut(Character, Account));
    }

    [Fact]
    public void RudeRequiresOptIn_KeepsTheCommandParsed_EvenWithAllowOptOutOff()
    {
        var store = new PlayerbotRivalryOptOut();
        PlayerbotRivalryCommandResult result = Say(store, "rivals on", options: Options(allowOptOut: false, requireOptIn: true));
        Assert.True(result.Consumed);
        Assert.NotNull(result.Answer);
        Assert.True(store.ChoiceOf(Character, Account));
    }

    [Fact]
    public void TheDisclosure_ComesOncePerLogin_AndNeverToAnOptedOutPlayer()
    {
        var store = new PlayerbotRivalryOptOut();
        Assert.Equal(Disclosure, store.TakeDisclosure(Character, Account, login: 100));
        Assert.Null(store.TakeDisclosure(Character, Account, login: 100));
        Assert.Null(store.TakeDisclosure(Character, Account, login: 100));
        Assert.Equal(Disclosure, store.TakeDisclosure(Character, Account, login: 101)); // a new login
        Assert.Equal(Disclosure, store.TakeDisclosure(OtherCharacter, OtherAccount, login: 100)); // another player

        Say(store, "rivals off", Later);
        Assert.Null(store.TakeDisclosure(Character, Account, login: 102));
    }

    [Fact]
    public void TheStore_IsBounded_NeverDropsAnOptOut_AndRefusesANewChoiceWhenFullOfThem()
    {
        var store = new PlayerbotRivalryOptOut();
        int max = PlayerbotRivalryOptOut.MaxPlayers;
        for (int i = 1; i <= max; i++) Assert.NotNull(store.Handle((ulong)(0x10_0000 + i), 100_000 + i, "rivals off", 0, Options()).Answer);
        Assert.Equal(max, store.Count);

        // Full of opt-outs: a new choice is refused (and answered), nothing already chosen is lost.
        PlayerbotRivalryCommandResult refused = store.Handle(0x20_0000, 200_000, "rivals off", 0, Options());
        Assert.True(refused.Consumed);
        Assert.NotNull(refused.Answer);
        Assert.DoesNotContain("Okay", refused.Answer);
        Assert.Equal(max, store.Count);
        Assert.False(store.IsOptedOut(0x20_0000, 200_000));
        for (int i = 1; i <= max; i++) Assert.True(store.IsOptedOut((ulong)(0x10_0000 + i), 100_000 + i), $"opt-out {i} was dropped");
    }

    [Fact]
    public void WhenTheStoreIsFull_ARivalsOnIsDroppedToMakeRoomForAnOptOut()
    {
        var store = new PlayerbotRivalryOptOut();
        int max = PlayerbotRivalryOptOut.MaxPlayers;
        store.Handle(0x10_0001, 100_001, "rivals on", 0, Options());
        for (int i = 2; i <= max; i++) store.Handle((ulong)(0x10_0000 + i), 100_000 + i, "rivals off", 0, Options());
        Assert.Equal(max, store.Count);

        Assert.Contains("Okay", store.Handle(0x20_0000, 200_000, "rivals off", 0, Options()).Answer);
        Assert.Equal(max, store.Count);
        Assert.True(store.IsOptedOut(0x20_0000, 200_000));
        Assert.Null(store.ChoiceOf(0x10_0001, 100_001)); // the "rivals on" went
    }
}
