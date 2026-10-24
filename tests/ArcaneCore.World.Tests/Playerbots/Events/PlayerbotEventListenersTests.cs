using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Events;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Events;

/// <summary>
/// Who hears a festival line or emote (P7a H4a and H4b, design 4.3 and review item B1): every real player of either faction within the
/// channel range; no line without a listener, or when one of them ignores the bot or is cut off by the chat safety; a listener who said
/// "ai off" makes the line built-in only. And the per-listener and per-anchor emote budgets (review item B3).
/// </summary>
public sealed class PlayerbotEventListenersTests
{
    private const float SayRange = 25f;

    private static readonly Func<ulong, BotChatPlayerSafety?> Nobody = _ => null;

    private static PlayerbotListenerCandidate Player(ulong guid, float yards, bool ignores = false) => new(guid, IsRealPlayer: true, yards, ignores);

    private static PlayerbotListenerCandidate Bot(ulong guid, float yards) => new(guid, IsRealPlayer: false, yards, IgnoresBot: false);

    private static BotChatPlayerSafety Safety(ulong guid, int cutoffSeconds = 0, bool? aiReplies = null)
        => new(guid, "Player" + guid, Strikes: 0, cutoffSeconds, aiReplies);

    // --- the listeners ---------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryRealPlayerWithinRange_IsAListener_OfEitherFaction()
    {
        // The candidate does not carry a faction: the other faction also receives a /say, and every text emote.
        PlayerbotListenerResult result = PlayerbotEventListeners.Evaluate([Player(1, 3f), Player(2, 24.9f), Player(3, SayRange), Player(4, 0f)], SayRange, Nobody);

        Assert.Equal(PlayerbotListenVerdict.Heard, result.Verdict);
        Assert.Equal([1UL, 2UL, 3UL, 4UL], result.Listeners.OrderBy(g => g));
        Assert.False(result.BuiltinOnly);
    }

    [Fact]
    public void APlayerPastTheRange_IsNotAListener()
    {
        PlayerbotListenerResult result = PlayerbotEventListeners.Evaluate([Player(1, 26f), Player(2, 25.01f), Player(3, 100f)], SayRange, Nobody);

        Assert.Equal(PlayerbotListenVerdict.NoListener, result.Verdict);
        Assert.Empty(result.Listeners);

        // The text-emote range is a separate channel range: the same players at 26 yards hear a 26+ yard emote range.
        Assert.Equal([1UL], PlayerbotEventListeners.Evaluate([Player(1, 26f), Player(2, 40f)], 30f, Nobody).Listeners);
    }

    [Fact]
    public void ABotIsNeverAListener()
    {
        PlayerbotListenerResult result = PlayerbotEventListeners.Evaluate([Bot(10, 2f), Bot(11, 5f)], SayRange, Nobody);

        Assert.Equal(PlayerbotListenVerdict.NoListener, result.Verdict);
        Assert.Empty(result.Listeners);

        result = PlayerbotEventListeners.Evaluate([Bot(10, 2f), Player(1, 5f)], SayRange, Nobody);
        Assert.Equal([1UL], result.Listeners);
    }

    [Fact]
    public void NoCandidates_IsNoListener()
    {
        PlayerbotListenerResult result = PlayerbotEventListeners.Evaluate([], SayRange, Nobody);

        Assert.Equal(PlayerbotListenVerdict.NoListener, result.Verdict);
        Assert.Empty(result.Listeners);
        Assert.False(result.BuiltinOnly);
    }

    // --- ignore, cut-off and "ai off" ------------------------------------------------------------------------------------

    [Fact]
    public void AListenerWhoIgnoresTheBot_SuppressesTheLine_EvenWithOthersAround()
    {
        PlayerbotListenerResult result = PlayerbotEventListeners.Evaluate([Player(1, 3f), Player(2, 8f, ignores: true), Player(3, 12f)], SayRange, Nobody);

        Assert.Equal(PlayerbotListenVerdict.Ignored, result.Verdict);
    }

    [Fact]
    public void AnIgnoringPlayerOutOfRange_SuppressesNothing()
    {
        PlayerbotListenerResult result = PlayerbotEventListeners.Evaluate([Player(1, 3f), Player(2, 60f, ignores: true)], SayRange, Nobody);

        Assert.Equal(PlayerbotListenVerdict.Heard, result.Verdict);
        Assert.Equal([1UL], result.Listeners);
    }

    [Fact]
    public void AListenerWhoIsCutOffByTheChatSafety_SuppressesTheLine()
    {
        var safety = new Dictionary<ulong, BotChatPlayerSafety> { [2] = Safety(2, cutoffSeconds: 90) };

        PlayerbotListenerResult result = PlayerbotEventListeners.Evaluate([Player(1, 3f), Player(2, 8f)], SayRange, g => safety.GetValueOrDefault(g));

        Assert.Equal(PlayerbotListenVerdict.CutOff, result.Verdict);
    }

    [Fact]
    public void AStrikeWithoutACutOff_SuppressesNothing()
    {
        BotChatPlayerSafety struck = new(1, "Struck", Strikes: 2, CutoffSeconds: 0, AiReplies: null);

        PlayerbotListenerResult result = PlayerbotEventListeners.Evaluate([Player(1, 3f)], SayRange, _ => struck);

        Assert.Equal(PlayerbotListenVerdict.Heard, result.Verdict);
    }

    [Fact]
    public void AListenerWhoSaidAiOff_MakesTheLineBuiltinOnly_ForEveryoneInRange()
    {
        var safety = new Dictionary<ulong, BotChatPlayerSafety> { [2] = Safety(2, aiReplies: false) };

        PlayerbotListenerResult result = PlayerbotEventListeners.Evaluate([Player(1, 3f), Player(2, 20f), Player(3, 9f)], SayRange, g => safety.GetValueOrDefault(g));

        Assert.Equal(PlayerbotListenVerdict.Heard, result.Verdict);
        Assert.True(result.BuiltinOnly);
        Assert.Equal(3, result.Listeners.Count);
    }

    [Fact]
    public void AiOnOrNoChoice_LeavesTheLineFree_AndAnAiOffOutOfRangeIsNotHeard()
    {
        var safety = new Dictionary<ulong, BotChatPlayerSafety>
        {
            [1] = Safety(1, aiReplies: true), [2] = Safety(2, aiReplies: null), [3] = Safety(3, aiReplies: false),
        };

        PlayerbotListenerResult result = PlayerbotEventListeners.Evaluate([Player(1, 3f), Player(2, 4f), Player(3, 90f)], SayRange, g => safety.GetValueOrDefault(g));

        Assert.Equal(PlayerbotListenVerdict.Heard, result.Verdict);
        Assert.False(result.BuiltinOnly);
    }

    [Fact]
    public void ABotsAiOffIsNotAPlayersChoice()
    {
        // A bot in the candidate list is skipped before the safety lookup, so a stray entry for its guid changes nothing.
        var safety = new Dictionary<ulong, BotChatPlayerSafety> { [10] = Safety(10, cutoffSeconds: 600, aiReplies: false) };

        PlayerbotListenerResult result = PlayerbotEventListeners.Evaluate([Bot(10, 2f), Player(1, 3f)], SayRange, g => safety.GetValueOrDefault(g));

        Assert.Equal(PlayerbotListenVerdict.Heard, result.Verdict);
        Assert.False(result.BuiltinOnly);
    }

    // --- the emote budgets -----------------------------------------------------------------------------------------------

    private const int PerListener = 2, PerAnchor = 3;

    private static PlayerbotEmoteVerdict Take(PlayerbotEmoteBudget budget, ulong anchor, ulong[] listeners, long nowMs, int perListener = PerListener, int perAnchor = PerAnchor)
        => budget.TryTake(anchor, listeners, nowMs, perListener, perAnchor);

    [Fact]
    public void ThirdEmoteInAMinute_IsRefusedForAListener_AndEveryListenerInRangeWasChargedByTheFirstTwo()
    {
        var budget = new PlayerbotEmoteBudget();
        ulong[] both = [1, 2];

        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 100, both, 0));
        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 101, both, 10_000)); // another anchor: the listener budget is the player's, not the anchor's
        Assert.Equal(PlayerbotEmoteVerdict.ListenerBudget, Take(budget, 102, both, 20_000));

        // Listener 2 alone is just as spent: sending charged both of them.
        Assert.Equal(PlayerbotEmoteVerdict.ListenerBudget, Take(budget, 103, [2], 21_000));
        Assert.Equal(PlayerbotEmoteVerdict.ListenerBudget, Take(budget, 103, [1], 21_000));
    }

    [Fact]
    public void ARefusedEmote_ChargesNobody()
    {
        var budget = new PlayerbotEmoteBudget();
        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 100, [1], 0));
        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 101, [1], 1_000));

        // Listener 1 is spent, so an emote heard by 1 and 3 is refused, and 3 must not pay for it.
        Assert.Equal(PlayerbotEmoteVerdict.ListenerBudget, Take(budget, 102, [1, 3], 2_000));
        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 103, [3], 3_000));
        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 104, [3], 4_000));
        Assert.Equal(PlayerbotEmoteVerdict.ListenerBudget, Take(budget, 105, [3], 5_000));
    }

    [Fact]
    public void TheListenerBudget_IsAMinuteWindow_ThatSlides()
    {
        var budget = new PlayerbotEmoteBudget();
        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 100, [1], 0));
        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 101, [1], 30_000));
        Assert.Equal(PlayerbotEmoteVerdict.ListenerBudget, Take(budget, 102, [1], 59_999));

        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 103, [1], 60_001)); // the emote at 0 left the window
        Assert.Equal(PlayerbotEmoteVerdict.ListenerBudget, Take(budget, 104, [1], 61_000)); // 30 000 and 60 001 are in it
        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 105, [1], 90_001));
    }

    [Fact]
    public void TheAnchorBudget_LimitsAnAnchorToThreeAMinute_ListenersOrNot()
    {
        var budget = new PlayerbotEmoteBudget();

        for (int i = 0; i < 3; i++) Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 100, [], i * 1_000));
        Assert.Equal(PlayerbotEmoteVerdict.AnchorBudget, Take(budget, 100, [], 4_000));
        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 200, [], 4_000)); // another anchor has its own
        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 100, [], 60_001)); // the first left the window
    }

    [Fact]
    public void AnAnchorRefusal_ChargesNoListener()
    {
        var budget = new PlayerbotEmoteBudget();
        for (int i = 0; i < 3; i++) Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 100, [], i * 1_000));

        Assert.Equal(PlayerbotEmoteVerdict.AnchorBudget, Take(budget, 100, [7], 5_000));

        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 200, [7], 6_000));
        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 201, [7], 7_000));
        Assert.Equal(PlayerbotEmoteVerdict.ListenerBudget, Take(budget, 202, [7], 8_000));
    }

    [Fact]
    public void AListenerRefusal_ChargesNoAnchor()
    {
        var budget = new PlayerbotEmoteBudget();
        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 100, [1], 0));
        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 101, [1], 1_000));
        for (int i = 0; i < 5; i++) Assert.Equal(PlayerbotEmoteVerdict.ListenerBudget, Take(budget, 300, [1], 2_000 + i));

        // Anchor 300 has had nothing sent, so a player-less emote there has all three places.
        for (int i = 0; i < 3; i++) Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 300, [], 3_000 + i));
    }

    [Fact]
    public void ABudgetOfZero_SendsNothing_WhileAPlayerIsInRange()
    {
        var budget = new PlayerbotEmoteBudget();

        Assert.Equal(PlayerbotEmoteVerdict.ListenerBudget, Take(budget, 100, [1], 0, perListener: 0));
        // With nobody in range the listener budget has nobody to charge; the anchor budget alone limits it.
        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 100, [], 1_000, perListener: 0));
        Assert.Equal(PlayerbotEmoteVerdict.AnchorBudget, Take(budget, 101, [], 2_000, perAnchor: 0));
    }

    [Fact]
    public void ABudgetRaisedLive_AppliesToTheEmotesAlreadySent()
    {
        var budget = new PlayerbotEmoteBudget();
        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 100, [1], 0));
        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 101, [1], 1_000));
        Assert.Equal(PlayerbotEmoteVerdict.ListenerBudget, Take(budget, 102, [1], 2_000));

        Assert.Equal(PlayerbotEmoteVerdict.Sent, Take(budget, 102, [1], 3_000, perListener: 3));
        Assert.Equal(PlayerbotEmoteVerdict.ListenerBudget, Take(budget, 103, [1], 4_000, perListener: 3));
    }
}
