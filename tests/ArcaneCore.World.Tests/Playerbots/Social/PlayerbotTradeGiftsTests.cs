using ArcaneCore.Game;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The bot trade gifts lane (docs/areas/playbots-social.md, Trading): answering trade requests, the gift rules read from the options (instead of the old hard-coded 100 copper),
/// the hourly gift limit, reading a player's WTS line, stack splitting for the desk and the recipe reserve. Every expected value is worked out by hand from the rule's definition;
/// the stack and reserve rules are pure, so the tests need no world. The counters that survive a restart are covered at the book and desk level here and at the store level in the data tests (PlayerbotTradeCounterStoreTests).
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

    // --- (4) the counters survive a restart ------------------------------------------------------------------------------------------

    private static readonly Guid BotA = new("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid BotB = new("00000000-0000-0000-0000-00000000000b");

    private static PlayerbotTradeCounter[] Sorted(IEnumerable<PlayerbotTradeCounter> rows)
        => [.. rows.OrderBy(row => row.Kind).ThenBy(row => row.BotId).ThenBy(row => row.Key).ThenBy(row => row.Stamp)];

    private static PlayerbotTradeDesk NewDesk(PlayerbotOptions options, Func<long> clock)
    {
        var recorder = new PlayerbotSocialRecorder(new PlayerbotSocialMemory(new PlayerbotSocialMemoryOptions(), () => 1_760_000_000_000), NullLogger.Instance);
        return new PlayerbotTradeDesk(options, recorder, PlayerbotReferenceDealPrices.Instance, clock);
    }

    /// <summary>
    /// The received-gift counts are exported as rows and a new book (a restart) loaded from them judges the same: two gifts within the hour stay at the limit of 2, a different bot is
    /// counted on its own, the counts expire an hour after the gift, rows already expired are not loaded and a load replaces what the book held. Broken: a restart would forget the
    /// hour's gifts (the limit resets), count one bot's gifts for another, or revive expired stamps.
    /// </summary>
    [Fact]
    public void GiftCounts_SurviveARestart_PerBot_AndExpire()
    {
        var options = new PlayerbotOptions();
        options.Social.Trading.MaxGiftsReceivedPerHour = 2;
        long now = 10_000_000;
        var book = new PlayerbotGiftBook();
        var gifts = new PlayerbotTradeGifts(options, () => now, book, BotA);
        long before = book.Version;
        gifts.NoteReceived(Partner); // at 10_000_000
        Assert.NotEqual(before, book.Version);
        now += 1000;
        gifts.NoteReceived(Partner); // at 10_001_000
        gifts.NoteReceived(Partner + 1);
        Assert.Equal(PlayerbotGiftVerdict.Accept, gifts.Judge(Partner + 1));

        IReadOnlyList<PlayerbotTradeCounter> rows = book.Export(now);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal((PlayerbotTradeCounterKind.GiftsReceived, BotA), (row.Kind, row.BotId)));

        var restartedBook = new PlayerbotGiftBook();
        restartedBook.Import(rows, now);
        var restarted = new PlayerbotTradeGifts(options, () => now, restartedBook, BotA);
        Assert.Equal(2, restarted.ReceivedWithinHour(Partner));
        Assert.Equal(PlayerbotGiftVerdict.HourlyLimit, restarted.Judge(Partner));
        Assert.Equal(1, restarted.ReceivedWithinHour(Partner + 1));
        Assert.Equal(0, new PlayerbotTradeGifts(options, () => now, restartedBook, BotB).ReceivedWithinHour(Partner)); // another bot's count is its own

        now += 3_599_000; // the first gift is exactly an hour old (expired), the second one second younger
        Assert.Equal(1, restarted.ReceivedWithinHour(Partner));
        now += 1000;
        Assert.Equal(0, restarted.ReceivedWithinHour(Partner));

        var late = new PlayerbotGiftBook();
        late.Import(rows, now);
        Assert.Empty(late.Export(now)); // expired rows are not loaded

        restartedBook.Import([], now);
        Assert.Equal(0, restarted.ReceivedWithinHour(Partner + 1)); // a load replaces the book
    }

    /// <summary>
    /// The desk's hourly trade counts and daily outflow totals are exported as rows and a new desk (a restart) loaded from them exports the same rows; a stamp older than an hour and
    /// a total of another UTC day are not loaded, a load replaces the counters, and when the hour passes or the day turns the loaded rows no longer export.
    /// Broken: a restart would reset the limits (a bot could hand out its whole daily outflow again), or load yesterday's total as today's.
    /// </summary>
    [Fact]
    public void DeskCounters_SurviveARestart_AndOnlyWhileTheyStillCount()
    {
        const long DayMs = 86_400_000;
        long now = 1_760_000_000_000;
        long day = now / DayMs;
        var options = new PlayerbotOptions();
        PlayerbotTradeCounter[] saved =
        [
            new(PlayerbotTradeCounterKind.BotTrades, BotA, 0, now - 60_000, 2),
            new(PlayerbotTradeCounterKind.BotTrades, BotA, 0, now - 10_000, 1),
            new(PlayerbotTradeCounterKind.AccountTrades, Guid.Empty, 77, now - 5_000, 3),
            new(PlayerbotTradeCounterKind.BotOutflow, BotA, 0, day, 1500),
            new(PlayerbotTradeCounterKind.AccountOutflow, Guid.Empty, 77, day, 1500),
            new(PlayerbotTradeCounterKind.ServerOutflow, Guid.Empty, 0, day, 4000),
        ];

        PlayerbotTradeDesk desk = NewDesk(options, () => now);
        long version = desk.CounterVersion;
        desk.ImportCounters(saved);
        Assert.Equal(version, desk.CounterVersion); // a load is not a change to save
        Assert.Equal(Sorted(saved), Sorted(desk.ExportCounters()));

        // A restart: a new desk loaded from what the first one exported holds the same counters.
        PlayerbotTradeDesk restarted = NewDesk(options, () => now);
        restarted.ImportCounters(desk.ExportCounters());
        Assert.Equal(Sorted(saved), Sorted(restarted.ExportCounters()));

        // Counters that no longer count are not loaded, and a load replaces the held counters.
        restarted.ImportCounters(
        [
            new(PlayerbotTradeCounterKind.BotTrades, BotB, 0, now - 3_600_000, 1),
            new(PlayerbotTradeCounterKind.BotOutflow, BotB, 0, day - 1, 900),
            new(PlayerbotTradeCounterKind.GiftsReceived, BotB, 5, now, 1), // the gift book's kind is not the desk's
            new(PlayerbotTradeCounterKind.BotOutflow, BotB, 0, day, 700),
        ]);
        Assert.Equal([new PlayerbotTradeCounter(PlayerbotTradeCounterKind.BotOutflow, BotB, 0, day, 700)], restarted.ExportCounters());

        // The hour passes (the trade stamps stop counting), then the day turns (the outflow totals stop counting).
        long later = now;
        PlayerbotTradeDesk aging = NewDesk(options, () => later);
        aging.ImportCounters(saved);
        later = now + 3_595_000 - 1; // the newest stamp (5 s old at load) is one millisecond short of an hour old
        Assert.Contains(aging.ExportCounters(), row => row.Kind == PlayerbotTradeCounterKind.AccountTrades);
        later = now + 3_600_000;
        Assert.DoesNotContain(aging.ExportCounters(), row => row.Kind is PlayerbotTradeCounterKind.BotTrades or PlayerbotTradeCounterKind.AccountTrades);
        Assert.Equal(3, aging.ExportCounters().Count);
        later = (day + 1) * DayMs;
        Assert.Empty(aging.ExportCounters());
    }

    // --- (1) the Gifts section ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>MaxGiftValueCopper</c> is the cap at level 60 and scales down with the bot's level (5000 at 60, 2500 at 30, 83 at 1, never above it at 90); a worthless item is not offered;
    /// <c>Gifts:Enabled</c>, a cap of 0 and <c>Social:Trading:Enabled</c> each switch the flow off; <c>GiftClaimSeconds</c> is the listening window. Broken: a constant cap or an unread switch.
    /// </summary>
    [Fact]
    public void GroupGifts_ReadTheGiftsSection()
    {
        var options = new PlayerbotOptions();
        var gifts = new PlayerbotGroupGifts(options);
        Assert.True(gifts.Enabled);
        Assert.Equal((5000L, 2500L, 83L, 5000L), (gifts.MaxValue(60), gifts.MaxValue(30), gifts.MaxValue(1), gifts.MaxValue(90)));
        Assert.True(gifts.ValueAllowed(5000, 60));
        Assert.False(gifts.ValueAllowed(5001, 60));
        Assert.False(gifts.ValueAllowed(0, 60));
        Assert.False(gifts.ValueAllowed(2501, 30));
        Assert.Equal(15_000, gifts.ClaimWindowMs);

        options.Social.Trading.Gifts.MaxGiftValueCopper = 600;
        options.Social.Trading.Gifts.GiftClaimSeconds = 30;
        Assert.Equal((600L, 300L), (gifts.MaxValue(60), gifts.MaxValue(30)));
        Assert.Equal(30_000, gifts.ClaimWindowMs);
        options.Social.Trading.Gifts.MaxGiftValueCopper = 0;
        Assert.False(gifts.Enabled);
        options.Social.Trading.Gifts.MaxGiftValueCopper = 5000;
        options.Social.Trading.Gifts.Enabled = false;
        Assert.False(gifts.Enabled);
        options.Social.Trading.Gifts.Enabled = true;
        options.Social.Trading.Enabled = false;
        Assert.False(gifts.Enabled);
    }

    /// <summary>A claim is a short "me" or "need" answer (case and trailing punctuation do not matter); chatter, questions about the item and long lines are not claims.</summary>
    [Theory]
    [InlineData("me", true)]
    [InlineData("Me!", true)]
    [InlineData("need", true)]
    [InlineData("I need it.", true)]
    [InlineData("  mine ", true)]
    [InlineData("i'll take it", true)]
    [InlineData("what stats does it have", false)]
    [InlineData("i dont need it", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsClaim_ReadsShortAnswers(string? text, bool expected) => Assert.Equal(expected, PlayerbotGroupGifts.IsClaim(text));

    /// <summary>
    /// An offer is due only after a fight, once the bot has been out of combat for the arming delay, and not while the cooldown of the last offer runs; each offer needs its own fight.
    /// Broken: an offer without a fight, during a fight, straight after it, or again and again.
    /// </summary>
    [Fact]
    public void OfferDue_NeedsAFight_TheArmingDelay_AndTheCooldown()
    {
        var gifts = new PlayerbotGroupGifts(new PlayerbotOptions());
        long t = 1_000_000;
        Assert.False(gifts.OfferDue(false, t)); // never fought
        Assert.False(gifts.OfferDue(true, t += 100));
        Assert.False(gifts.OfferDue(false, t += 100)); // the fight just ended
        Assert.False(gifts.OfferDue(false, t + PlayerbotGroupGifts.ArmAfterCombatMs - 1));
        t += PlayerbotGroupGifts.ArmAfterCombatMs;
        Assert.True(gifts.OfferDue(false, t));

        gifts.Begin(new ObjectGuid(1), t);
        Assert.Equal(PlayerbotGiftPhase.Offered, gifts.Phase);
        Assert.False(gifts.OfferDue(false, t + 10_000_000)); // busy with the offer
        gifts.End(t);
        Assert.Equal(PlayerbotGiftPhase.Idle, gifts.Phase);
        Assert.False(gifts.OfferDue(false, t + 10_000_000)); // no new fight yet

        Assert.False(gifts.OfferDue(true, t += 1000));
        Assert.False(gifts.OfferDue(false, t += 100));
        Assert.False(gifts.OfferDue(false, t + PlayerbotGroupGifts.ArmAfterCombatMs)); // the cooldown still runs
        Assert.True(gifts.OfferDue(false, t + PlayerbotGroupGifts.CooldownMs));
    }

    /// <summary>
    /// The first claim inside <c>GiftClaimSeconds</c> wins and moves the flow to Claimed; a claim after the window or a second claim is refused; an unclaimed window can be awarded to a groupmate
    /// bot; a claimed gift times out. Broken: a late claim taking the item, or two claimants.
    /// </summary>
    [Fact]
    public void Claim_FirstWinsInsideTheWindow_AndTheTradeIsBounded()
    {
        var gifts = new PlayerbotGroupGifts(new PlayerbotOptions());
        ObjectGuid item = new(7), first = new(100), second = new(101), heir = new(102);
        Assert.False(gifts.Claim(first, 0)); // nothing on offer

        gifts.Begin(item, 0);
        Assert.False(gifts.ClaimWindowOver(14_999));
        Assert.True(gifts.Claim(first, 14_999));
        Assert.False(gifts.Claim(second, 15_000));
        Assert.Equal((PlayerbotGiftPhase.Claimed, first, item), (gifts.Phase, gifts.Recipient, gifts.Item));
        Assert.False(gifts.TradeTimedOut(14_999 + PlayerbotGroupGifts.TradeMaxMs - 1));
        Assert.True(gifts.TradeTimedOut(14_999 + PlayerbotGroupGifts.TradeMaxMs));
        gifts.End(50_000);

        gifts.Begin(item, 0);
        Assert.False(gifts.Claim(second, 15_000)); // the window is over
        Assert.True(gifts.ClaimWindowOver(15_000));
        Assert.True(gifts.Award(heir, 15_000));
        Assert.Equal((PlayerbotGiftPhase.Claimed, heir), (gifts.Phase, gifts.Recipient));
    }

    /// <summary>
    /// The item to offer is the most valuable one within the level's cap that a groupmate would wear and was not offered before; a stack over the cap or one nobody wants is skipped.
    /// Broken: a gift above the cap, an item offered twice, or an item nobody can use.
    /// </summary>
    [Fact]
    public void Choose_TakesTheMostValuableWithinTheCap_AndNeverOffersAnItemTwice()
    {
        var gifts = new PlayerbotGroupGifts(new PlayerbotOptions());
        PlayerbotGiftCandidate Candidate(ulong guid, long value) => new(new ObjectGuid(guid), 100 + (uint)guid, 0, (byte)(22 + guid), value);
        PlayerbotGiftCandidate[] candidates = [Candidate(1, 100), Candidate(2, 4000), Candidate(3, 9000), Candidate(4, 4500)];

        Assert.Equal(new ObjectGuid(4), gifts.Choose(candidates, 60, _ => true)!.Value.Item);
        Assert.Equal(new ObjectGuid(2), gifts.Choose(candidates, 60, candidate => candidate.Item.Value != 4)!.Value.Item); // nobody wants guid 4
        Assert.Equal(new ObjectGuid(1), gifts.Choose(candidates, 30, _ => true)!.Value.Item); // the cap at level 30 is 2500
        Assert.Null(gifts.Choose(candidates, 60, _ => false));

        gifts.Begin(new ObjectGuid(4), 0);
        gifts.End(0);
        Assert.Equal(new ObjectGuid(2), gifts.Choose(candidates, 60, _ => true)!.Value.Item); // guid 4 was offered
    }
}
