using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The bot trade gifts lane (docs/areas/playbots-social.md, Trading): answering trade requests, the gift rules read from the options (instead of the old hard-coded 100 copper),
/// the hourly gift limit, reading a player's WTS line, stack splitting for the desk and the recipe reserve. Every expected value is worked out by hand from the rule's definition;
/// the stack and reserve rules are pure, so the tests need no world. Persisting the hourly counters across restarts is not part of this type (it needs a schema step).
/// </summary>
public sealed class PlayerbotTradeGiftsTests
{
    private const ulong Partner = 0x1234;

    private static (PlayerbotTradeGifts Gifts, PlayerbotOptions Options, long[] Clock) Make()
    {
        var options = new PlayerbotOptions();
        long[] clock = [1_000_000];
        return (new PlayerbotTradeGifts(options, () => clock[0]), options, clock);
    }

    // --- (1) the options are wired ------------------------------------------------------------------------------------------------

    /// <summary>AnswerTrades off ignores every request; on, a trusted partner gets a window and a stranger is told busy. Broken: the key would still be unread.</summary>
    [Fact]
    public void Answer_FollowsAnswerTradesAndTrust()
    {
        (PlayerbotTradeGifts gifts, PlayerbotOptions options, _) = Make();
        Assert.Equal(PlayerbotTradeAnswer.Begin, gifts.Answer(trusted: true));
        Assert.Equal(PlayerbotTradeAnswer.Busy, gifts.Answer(trusted: false));
        options.Social.Trading.AnswerTrades = false;
        Assert.Equal(PlayerbotTradeAnswer.Ignore, gifts.Answer(trusted: true));
        Assert.Equal(PlayerbotTradeAnswer.Ignore, gifts.Answer(trusted: false));
    }

    /// <summary>AcceptGifts off refuses; the hourly limit counts only the last hour per partner and 0 accepts none. Broken: a limit that never expires, one shared by all partners, or one that ignores 0.</summary>
    [Fact]
    public void Judge_AppliesAcceptGiftsAndTheHourlyLimit()
    {
        (PlayerbotTradeGifts gifts, PlayerbotOptions options, long[] clock) = Make();
        options.Social.Trading.MaxGiftsReceivedPerHour = 2;
        Assert.Equal(PlayerbotGiftVerdict.Accept, gifts.Judge(Partner));
        gifts.NoteReceived(Partner);
        clock[0] += 60_000;
        gifts.NoteReceived(Partner);
        Assert.Equal(2, gifts.ReceivedWithinHour(Partner));
        Assert.Equal(PlayerbotGiftVerdict.HourlyLimit, gifts.Judge(Partner));
        Assert.Equal(PlayerbotGiftVerdict.Accept, gifts.Judge(Partner + 1)); // another partner is counted on its own

        clock[0] += 3_540_000; // the first gift is now exactly an hour old (expired), the second is 59 minutes old
        Assert.Equal(1, gifts.ReceivedWithinHour(Partner));
        Assert.Equal(PlayerbotGiftVerdict.Accept, gifts.Judge(Partner));
        clock[0] += 60_000;
        Assert.Equal(0, gifts.ReceivedWithinHour(Partner));

        options.Social.Trading.MaxGiftsReceivedPerHour = 0;
        Assert.Equal(PlayerbotGiftVerdict.HourlyLimit, gifts.Judge(Partner));
        options.Social.Trading.MaxGiftsReceivedPerHour = 10;
        options.Social.Trading.AcceptGifts = false;
        Assert.Equal(PlayerbotGiftVerdict.Disabled, gifts.Judge(Partner));
    }

    /// <summary>The value threshold is GiftMinValueCopper, not a constant: 99 copper counts at a minimum of 50 and not at the default 100; giving something always counts.</summary>
    [Fact]
    public void Counts_UsesGiftMinValueCopper()
    {
        (PlayerbotTradeGifts gifts, PlayerbotOptions options, _) = Make();
        Assert.False(gifts.Counts(gave: false, receivedValue: 99));
        Assert.True(gifts.Counts(gave: false, receivedValue: 100));
        options.Social.Trading.GiftMinValueCopper = 50;
        Assert.True(gifts.Counts(gave: false, receivedValue: 99));
        options.Social.Trading.GiftMinValueCopper = 1000;
        Assert.False(gifts.Counts(gave: false, receivedValue: 999));
        Assert.True(gifts.Counts(gave: true, receivedValue: 0));
    }

    /// <summary>The per-partner map is bounded: past MapCap partners the oldest is dropped, the newest stays counted.</summary>
    [Fact]
    public void NoteReceived_IsBounded()
    {
        (PlayerbotTradeGifts gifts, _, long[] clock) = Make();
        for (ulong p = 1; p <= PlayerbotTradeGifts.MapCap + 5; p++)
        {
            clock[0] += 10;
            gifts.NoteReceived(p);
        }

        Assert.Equal(1, gifts.ReceivedWithinHour(PlayerbotTradeGifts.MapCap + 5));
        Assert.Equal(0, gifts.ReceivedWithinHour(1));
    }

    // --- (2) reading a player's WTS line --------------------------------------------------------------------------------------------

    /// <summary>A WTS or "selling" line is read as WantToSell; a WTB line stays WantToBuy even when it says selling; the bot answers only for a reagent it lacks.</summary>
    [Fact]
    public void WtsLine_IsReadAndMatchedToTheNeededReagent()
    {
        Assert.Equal(WorldLineKind.WantToSell, PlayerbotWorldChatter.Read("WTS Linen Cloth x20, pst"));
        Assert.Equal(WorldLineKind.WantToSell, PlayerbotWorldChatter.Read("selling copper ore"));
        Assert.Equal(WorldLineKind.WantToBuy, PlayerbotWorldChatter.Read("anyone selling linen? buying"));
        Assert.Equal(WorldLineKind.None, PlayerbotWorldChatter.Read("hello everyone"));

        BotChatItem linen = new(2589, "Linen Cloth", 5, 1, 13);
        BotChatItem thread = new(2320, "Coarse Thread", 2, 1, 10);
        string line = PlayerbotWorldChatter.Normalize("WTS linen cloth x20, pst");
        Assert.Equal(linen, PlayerbotTradeGifts.NeededFromLine(line, [thread, linen]));
        Assert.Null(PlayerbotTradeGifts.NeededFromLine(PlayerbotWorldChatter.Normalize("WTS wool cloth"), [thread, linen]));
        Assert.Null(PlayerbotTradeGifts.NeededFromLine(line, []));
    }

    // --- (3) stack splitting --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Stacks 20 and 5 for a count of 8: the greedy plan takes the 5 (the 20 is larger than what remains), 3 remain, and 3 are split off the 20, whichever order the stacks
    /// are listed in. Broken: no split (the deal would fail), a split of the whole count (8) or a split off the stack the plan already uses.
    /// </summary>
    [Fact]
    public void ChooseSplit_SplitsTheShortfallOffALargerStack()
    {
        Assert.Equal((0, 3u), PlayerbotTradeGifts.ChooseSplit([20, 5], 8));
        Assert.Equal((1, 3u), PlayerbotTradeGifts.ChooseSplit([5, 20], 8));
    }

    /// <summary>No split is wanted when the count is already exact, when units are short, when no slot is left or the amount exceeds a byte.</summary>
    [Fact]
    public void ChooseSplit_ReturnsNullWhenNoSplitHelps()
    {
        Assert.Null(PlayerbotTradeGifts.ChooseSplit([8], 8)); // exact single stack
        Assert.Null(PlayerbotTradeGifts.ChooseSplit([5, 3], 8)); // exact sum of two stacks
        Assert.Null(PlayerbotTradeGifts.ChooseSplit([5, 2], 9)); // only 7 units
        Assert.Null(PlayerbotTradeGifts.ChooseSplit([], 4));
        Assert.Null(PlayerbotTradeGifts.ChooseSplit([20], 0));
        Assert.Null(PlayerbotTradeGifts.ChooseSplit([1, 1, 1, 1, 1, 1, 50], 56)); // the 50 and five 1s fill the six traded slots: the split's new stack has no slot
        Assert.Null(PlayerbotTradeGifts.ChooseSplit([1000], 300)); // 300 is not a byte
    }

    /// <summary>The split takes the smallest stack that is large enough: [20, 12] for 10 splits the 12, not the 20.</summary>
    [Fact]
    public void ChooseSplit_PrefersTheSmallestSufficientStack()
    {
        Assert.Equal((1, 10u), PlayerbotTradeGifts.ChooseSplit([20, 12], 10));
    }

    // --- (5) recipe reserve ---------------------------------------------------------------------------------------------------------

    /// <summary>Linen 12 with a recipe needing 4: 8 are offered; 4 with a need of 4: none; an item no recipe wants is untouched. The input list is not changed.</summary>
    [Fact]
    public void WithoutReserved_KeepsWhatWantedRecipesNeed()
    {
        BotChatItem linen = new(2589, "Linen Cloth", 12, 1, 13);
        BotChatItem ore = new(2770, "Copper Ore", 4, 1, 20);
        BotChatItem bar = new(2840, "Copper Bar", 6, 1, 40);
        BotChatItem[] surplus = [linen, ore, bar];
        var reserve = new Dictionary<uint, uint> { [2589] = 4, [2770] = 4 };

        List<BotChatItem> spare = PlayerbotTradeGifts.WithoutReserved(surplus, reserve);
        Assert.Equal([linen with { Count = 8 }, bar], spare);
        Assert.Equal(12, surplus[0].Count);
        Assert.Equal(surplus, PlayerbotTradeGifts.WithoutReserved(surplus, new Dictionary<uint, uint>()));
    }
}
