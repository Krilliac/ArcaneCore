using ArcaneCore.Game;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots.BotSocial;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social.Trading;

/// <summary>
/// P4 S3: the trade ledger (design 4.4): counts and times per (bot, partner), the scam and ninja windows read live, the War Mode read rule,
/// the once-a-day gift stamp, the snapshot and load round trip, versions and the row bound; and the recorder hooks that feed it.
/// </summary>
public sealed class PlayerbotTradeLedgerTests
{
    private const long Day = 86_400_000L;
    private static readonly Guid Bot = Guid.Parse("00000000-0000-0000-0000-0000000000f3");
    private static readonly Guid OtherBot = Guid.Parse("00000000-0000-0000-0000-0000000000f5");
    private const ulong Partner = 900;

    private sealed class Clock
    {
        // Midnight UTC of some day.
        public long Now = 20_000 * Day;
    }

    private static (PlayerbotTradeLedger Ledger, PlayerbotSocialTradingOptions Options, PlayerbotWarModeOptions War, Clock Clock) Make()
    {
        var options = new PlayerbotSocialTradingOptions();
        var war = new PlayerbotWarModeOptions();
        var clock = new Clock();
        return (new PlayerbotTradeLedger(options, war, () => clock.Now), options, war, clock);
    }

    private static PlayerbotTradeRecord Get(PlayerbotTradeLedger ledger, ulong other = Partner, Guid? bot = null)
    {
        Assert.True(ledger.TryGet(bot ?? Bot, other, out PlayerbotTradeRecord record));
        return record;
    }

    [Fact]
    public void AStranger_HasNoRow_AndIsNeitherScammerNorNinja()
    {
        (PlayerbotTradeLedger ledger, _, _, _) = Make();
        Assert.False(ledger.TryGet(Bot, Partner, out _));
        Assert.False(ledger.IsScammer(Bot, Partner));
        Assert.False(ledger.IsNinja(Bot, Partner));
        Assert.Empty(ledger.Snapshot(Bot));
        Assert.Equal(0, ledger.Version(Bot));
    }

    [Fact]
    public void FairDeals_AreCounted_PerPartner_AndPerBot()
    {
        (PlayerbotTradeLedger ledger, _, _, _) = Make();
        ledger.NoteFairDeal(Bot, 7, Partner);
        ledger.NoteFairDeal(Bot, 7, Partner);
        ledger.NoteFairDeal(Bot, 7, Partner + 1);
        ledger.NoteFairDeal(OtherBot, 8, Partner);

        PlayerbotTradeRecord row = Get(ledger);
        Assert.Equal((Bot, 7, Partner, 2, 0), (row.BotId, row.CharacterId, row.OtherGuid, row.FairDeals, row.Scams));
        Assert.Equal((0L, 0L, 0), (row.LastScamUnixMs, row.LastNinjaUnixMs, row.LastGiftDay));
        Assert.Equal(1, Get(ledger, Partner + 1).FairDeals);
        Assert.Equal(1, Get(ledger, bot: OtherBot).FairDeals);
        Assert.Equal(2, ledger.Snapshot(Bot).Count);
    }

    [Fact]
    public void AScam_IsCountedAndTimed_AndMakesAScammer_WithoutTouchingTheFairDeals()
    {
        (PlayerbotTradeLedger ledger, _, _, Clock clock) = Make();
        ledger.NoteFairDeal(Bot, 7, Partner);
        clock.Now += 5_000;
        ledger.NoteScam(Bot, 7, Partner);

        PlayerbotTradeRecord row = Get(ledger);
        Assert.Equal((1, 1, clock.Now), (row.FairDeals, row.Scams, row.LastScamUnixMs));
        Assert.True(ledger.IsScammer(Bot, Partner));
        Assert.False(ledger.IsNinja(Bot, Partner));
        Assert.False(ledger.IsScammer(Bot, Partner + 1));
        Assert.False(ledger.IsScammer(OtherBot, Partner)); // first-hand only: another bot has not been scammed
    }

    [Fact]
    public void TheScamWindow_IsScamMemoryDays_AndTheCountOutlivesIt()
    {
        (PlayerbotTradeLedger ledger, PlayerbotSocialTradingOptions options, _, Clock clock) = Make();
        Assert.Equal(30, options.ScamMemoryDays);
        ledger.NoteScam(Bot, 7, Partner);
        long scamAt = clock.Now;

        clock.Now = scamAt + 30 * Day - 1;
        Assert.True(ledger.IsScammer(Bot, Partner));
        clock.Now = scamAt + 30 * Day + 1;
        Assert.False(ledger.IsScammer(Bot, Partner));
        Assert.Equal((1, scamAt), (Get(ledger).Scams, Get(ledger).LastScamUnixMs)); // forgiven, not erased

        // A second scam restarts the window and the count goes up.
        ledger.NoteScam(Bot, 7, Partner);
        Assert.True(ledger.IsScammer(Bot, Partner));
        Assert.Equal((2, clock.Now), (Get(ledger).Scams, Get(ledger).LastScamUnixMs));
    }

    [Fact]
    public void TheWindowsAreReadLive_SoALiveOptionChangeApplies_AndZeroForgetsAtOnce()
    {
        (PlayerbotTradeLedger ledger, PlayerbotSocialTradingOptions options, _, Clock clock) = Make();
        ledger.NoteScam(Bot, 7, Partner);
        ledger.NoteNinja(Bot, 7, Partner);
        clock.Now += 10 * Day;
        Assert.True(ledger.IsScammer(Bot, Partner));
        Assert.True(ledger.IsNinja(Bot, Partner));

        options.ScamMemoryDays = 5;
        options.NinjaMemoryDays = 5;
        Assert.False(ledger.IsScammer(Bot, Partner));
        Assert.False(ledger.IsNinja(Bot, Partner));

        options.ScamMemoryDays = 365;
        options.NinjaMemoryDays = 365;
        Assert.True(ledger.IsScammer(Bot, Partner));
        Assert.True(ledger.IsNinja(Bot, Partner));

        // 0 forgets at once, yet the scam is still counted (design 5: "scams still cost affinity and trust").
        options.ScamMemoryDays = 0;
        options.NinjaMemoryDays = 0;
        Assert.False(ledger.IsScammer(Bot, Partner));
        Assert.False(ledger.IsNinja(Bot, Partner));
        ledger.NoteScam(Bot, 7, Partner);
        Assert.False(ledger.IsScammer(Bot, Partner));
        Assert.Equal(2, Get(ledger).Scams);
    }

    [Fact]
    public void TheNinjaWindow_IsNinjaMemoryDays_AndIsSeparateFromTheScamWindow()
    {
        (PlayerbotTradeLedger ledger, PlayerbotSocialTradingOptions options, _, Clock clock) = Make();
        Assert.Equal(14, options.NinjaMemoryDays);
        ledger.NoteNinja(Bot, 7, Partner);
        long ninjaAt = clock.Now;
        Assert.True(ledger.IsNinja(Bot, Partner));
        Assert.False(ledger.IsScammer(Bot, Partner));
        Assert.Equal((0, 0L, ninjaAt), (Get(ledger).Scams, Get(ledger).LastScamUnixMs, Get(ledger).LastNinjaUnixMs));

        clock.Now = ninjaAt + 14 * Day - 1;
        Assert.True(ledger.IsNinja(Bot, Partner));
        clock.Now = ninjaAt + 14 * Day + 1;
        Assert.False(ledger.IsNinja(Bot, Partner));
        Assert.Equal(ninjaAt, Get(ledger).LastNinjaUnixMs);
    }

    [Fact]
    public void WarMode_WithNeverForget_IgnoresTheDayWindowsAtReadTime_AndSwitchingItOffRestoresThem()
    {
        (PlayerbotTradeLedger ledger, _, PlayerbotWarModeOptions war, Clock clock) = Make();
        ledger.NoteScam(Bot, 7, Partner);
        ledger.NoteNinja(Bot, 7, Partner);
        clock.Now += 100 * Day; // far past both windows
        Assert.False(ledger.IsScammer(Bot, Partner));
        Assert.False(ledger.IsNinja(Bot, Partner));

        war.Enabled = true;
        Assert.True(war.NeverForget);
        Assert.True(ledger.IsScammer(Bot, Partner));
        Assert.True(ledger.IsNinja(Bot, Partner));

        war.NeverForget = false; // War Mode on without NeverForget: the windows apply as ever
        Assert.False(ledger.IsScammer(Bot, Partner));
        Assert.False(ledger.IsNinja(Bot, Partner));

        war.NeverForget = true;
        war.Enabled = false; // switched off: the same as a world that never had War Mode
        Assert.False(ledger.IsScammer(Bot, Partner));
        Assert.False(ledger.IsNinja(Bot, Partner));
    }

    [Fact]
    public void WarMode_WritesNothingToTheLedgerItself()
    {
        // Design 7: "The ledger and trust are normal-only": the stored rows are the same with War Mode on and off for the same notes.
        (PlayerbotTradeLedger off, _, _, _) = Make();
        (PlayerbotTradeLedger on, _, PlayerbotWarModeOptions war, _) = Make();
        war.Enabled = true;
        foreach (PlayerbotTradeLedger ledger in new[] { off, on })
        {
            ledger.NoteFairDeal(Bot, 7, Partner);
            ledger.NoteScam(Bot, 7, Partner);
            ledger.NoteNinja(Bot, 7, Partner + 1);
            ledger.NoteGiftReceived(Bot, 7, Partner + 2);
        }

        Assert.Equal(off.Snapshot(Bot), on.Snapshot(Bot));
    }

    [Fact]
    public void AGift_CountsOncePerPairPerUtcDay()
    {
        (PlayerbotTradeLedger ledger, _, _, Clock clock) = Make();
        Assert.True(ledger.NoteGiftReceived(Bot, 7, Partner));
        Assert.Equal(20_000, Get(ledger).LastGiftDay);
        long version = ledger.Version(Bot);

        Assert.False(ledger.NoteGiftReceived(Bot, 7, Partner)); // same day
        clock.Now += Day - 1;                                    // the last millisecond of the day
        Assert.False(ledger.NoteGiftReceived(Bot, 7, Partner));
        Assert.Equal(version, ledger.Version(Bot)); // a refused gift changes nothing

        Assert.True(ledger.NoteGiftReceived(Bot, 7, Partner + 1)); // another partner the same day
        clock.Now += 1;                                            // midnight: the next UTC day
        Assert.True(ledger.NoteGiftReceived(Bot, 7, Partner));
        Assert.Equal(20_001, Get(ledger).LastGiftDay);
        Assert.False(ledger.NoteGiftReceived(Bot, 7, Partner));
        Assert.True(ledger.NoteGiftReceived(OtherBot, 8, Partner)); // another bot has its own stamp
    }

    [Fact]
    public void Snapshot_IsOrderedByPartner_AndLoadRestoresEveryFieldWithoutBumpingTheVersion()
    {
        (PlayerbotTradeLedger ledger, _, _, Clock clock) = Make();
        ledger.NoteFairDeal(Bot, 7, 30);
        ledger.NoteScam(Bot, 7, 10);
        ledger.NoteNinja(Bot, 7, 20);
        ledger.NoteGiftReceived(Bot, 7, 40);
        IReadOnlyList<PlayerbotTradeRecord> rows = ledger.Snapshot(Bot);
        Assert.Equal([10UL, 20, 30, 40], rows.Select(r => r.OtherGuid));
        Assert.All(rows, r => Assert.Equal((Bot, 7), (r.BotId, r.CharacterId)));

        (PlayerbotTradeLedger restarted, _, _, Clock again) = Make();
        again.Now = clock.Now;
        restarted.Load(rows);
        Assert.Equal(rows, restarted.Snapshot(Bot));
        Assert.True(restarted.IsScammer(Bot, 10));
        Assert.True(restarted.IsNinja(Bot, 20));
        Assert.Equal(1, Get(restarted, 30).FairDeals);
        Assert.Equal(0, restarted.Version(Bot)); // a loaded bot is not saved straight back
        Assert.False(restarted.NoteGiftReceived(Bot, 7, 40)); // the loaded gift day still holds today

        restarted.NoteFairDeal(Bot, 7, 30);
        Assert.True(restarted.Version(Bot) > 0);
        Assert.Equal(2, Get(restarted, 30).FairDeals);
    }

    [Fact]
    public void Version_ChangesOnEveryWrite_AndOnlyForTheBotThatChanged()
    {
        (PlayerbotTradeLedger ledger, _, _, _) = Make();
        long previous = ledger.Version(Bot);
        foreach (Action write in new Action[]
        {
            () => ledger.NoteFairDeal(Bot, 7, Partner),
            () => ledger.NoteScam(Bot, 7, Partner),
            () => ledger.NoteNinja(Bot, 7, Partner),
            () => ledger.NoteGiftReceived(Bot, 7, Partner),
        })
        {
            write();
            long now = ledger.Version(Bot);
            Assert.NotEqual(previous, now);
            previous = now;
        }

        Assert.Equal(0, ledger.Version(OtherBot));
    }

    [Fact]
    public void Forget_DropsOneBotsRows_AndNoOtherBots()
    {
        (PlayerbotTradeLedger ledger, _, _, _) = Make();
        ledger.NoteScam(Bot, 7, Partner);
        ledger.NoteScam(OtherBot, 8, Partner);
        ledger.Forget(Bot);
        Assert.Empty(ledger.Snapshot(Bot));
        Assert.False(ledger.IsScammer(Bot, Partner));
        Assert.True(ledger.IsScammer(OtherBot, Partner));
        ledger.Forget(Guid.NewGuid()); // unknown bot: nothing
    }

    [Fact]
    public void ARowsPerBotBound_HoldsAtTheStoreLimit_AndAFreshScammerSurvivesTheEviction()
    {
        // Assumption (not in the design text): the ledger is bounded at PlayerbotTradeRecord.MaxRowsPerBot like the relation memory, and eviction
        // keeps a partner that is still inside a scam window, because dropping a scammer to make room would forget the scam.
        (PlayerbotTradeLedger ledger, _, _, _) = Make();
        ledger.NoteScam(Bot, 7, 1);
        for (ulong other = 2; other <= PlayerbotTradeRecord.MaxRowsPerBot + 50; other++) ledger.NoteFairDeal(Bot, 7, other);

        Assert.Equal(PlayerbotTradeRecord.MaxRowsPerBot, ledger.Snapshot(Bot).Count);
        Assert.True(ledger.IsScammer(Bot, 1));
    }

    // ---- the recorder hooks ----

    private static readonly Guid RecorderBot = Guid.Parse("00000000-0000-0000-0000-0000000000a9");
    private static readonly ObjectGuid BotGuid = ObjectGuid.Player(21);
    private static readonly ObjectGuid Human = ObjectGuid.Player(950);

    private static (PlayerbotSocialRecorder Recorder, PlayerbotTradeLedger Ledger) MakeRecorder()
    {
        var clock = new Clock();
        var ledger = new PlayerbotTradeLedger(new PlayerbotSocialTradingOptions(), new PlayerbotWarModeOptions(), () => clock.Now);
        var memory = new PlayerbotSocialMemory(new PlayerbotSocialMemoryOptions(), () => clock.Now);
        var recorder = new PlayerbotSocialRecorder(memory, NullLogger.Instance, ledger);
        recorder.Track(RecorderBot, 21);
        return (recorder, ledger);
    }

    [Fact]
    public void Recorder_FairDeal_FeedsTheMemoryAndTheLedger()
    {
        (PlayerbotSocialRecorder recorder, PlayerbotTradeLedger ledger) = MakeRecorder();
        recorder.NoteById(RecorderBot, BotGuid, Human, PlayerbotSocialEvent.FairDeal);
        Assert.True(recorder.Memory.TryGet(RecorderBot, Human.Value, out PlayerbotRelation relation));
        Assert.Equal((3, 520), (relation.Affinity, (int)relation.TrustPermille));
        PlayerbotTradeRecord row = Get(ledger, Human.Value, RecorderBot);
        Assert.Equal((1, 0, 21), (row.FairDeals, row.Scams, row.CharacterId));
        Assert.Same(ledger, recorder.Ledger);
    }

    [Fact]
    public void Recorder_TradeScam_FeedsTheMemoryAndTheLedger_AndMakesAScammer()
    {
        (PlayerbotSocialRecorder recorder, PlayerbotTradeLedger ledger) = MakeRecorder();
        recorder.Note(BotGuid, Human, PlayerbotSocialEvent.TradeScam);
        Assert.True(recorder.Memory.TryGet(RecorderBot, Human.Value, out PlayerbotRelation relation));
        Assert.Equal((-15, 300), (relation.Affinity, (int)relation.TrustPermille));
        Assert.Equal(1, Get(ledger, Human.Value, RecorderBot).Scams);
        Assert.True(ledger.IsScammer(RecorderBot, Human.Value));
    }

    [Fact]
    public void Recorder_NinjaLoot_StampsTheLedgerNinjaTime_AsWellAsTheExistingAffinityHit()
    {
        // The existing hook (PlayerbotPartyAI.OnLootRollWon) already calls NoteById(NinjaLoot): the ledger stamp must ride on it.
        (PlayerbotSocialRecorder recorder, PlayerbotTradeLedger ledger) = MakeRecorder();
        recorder.NoteById(RecorderBot, BotGuid, Human, PlayerbotSocialEvent.NinjaLoot);
        Assert.True(recorder.Memory.TryGet(RecorderBot, Human.Value, out PlayerbotRelation relation));
        Assert.Equal(-8, relation.Affinity);
        Assert.True(ledger.IsNinja(RecorderBot, Human.Value));
        Assert.False(ledger.IsScammer(RecorderBot, Human.Value));
    }

    [Fact]
    public void Recorder_GiftReceived_StampsTheGiftDay_AndOtherEventsLeaveTheLedgerAlone()
    {
        (PlayerbotSocialRecorder recorder, PlayerbotTradeLedger ledger) = MakeRecorder();
        recorder.Note(BotGuid, Human, PlayerbotSocialEvent.GiftReceived);
        Assert.Equal(20_000, Get(ledger, Human.Value, RecorderBot).LastGiftDay);

        var other = ObjectGuid.Player(951);
        foreach (PlayerbotSocialEvent e in new[]
        {
            PlayerbotSocialEvent.ChatExchange, PlayerbotSocialEvent.Grouped, PlayerbotSocialEvent.TradeCompleted,
            PlayerbotSocialEvent.Kicked, PlayerbotSocialEvent.DealFlaked,
        })
            recorder.Note(BotGuid, other, e);
        Assert.False(ledger.TryGet(RecorderBot, other.Value, out _)); // a flake is a memory event, not a ledger row
    }

    [Fact]
    public void Recorder_IgnoresUnmanagedBots_AndNonPlayerPartners_ForTheLedgerToo()
    {
        (PlayerbotSocialRecorder recorder, PlayerbotTradeLedger ledger) = MakeRecorder();
        recorder.Note(ObjectGuid.Player(77), Human, PlayerbotSocialEvent.TradeScam); // not a managed bot
        recorder.Note(BotGuid, ObjectGuid.WithEntry(HighGuid.Unit, 100, 1), PlayerbotSocialEvent.TradeScam);
        Assert.Empty(ledger.Snapshot(RecorderBot));
        recorder.Untrack(21);
        recorder.Note(BotGuid, Human, PlayerbotSocialEvent.TradeScam);
        Assert.Empty(ledger.Snapshot(RecorderBot));
    }

    [Fact]
    public void Recorder_WithoutALedger_StillRecordsTheMemory_AndNeverThrows()
    {
        var memory = new PlayerbotSocialMemory(new PlayerbotSocialMemoryOptions(), () => 20_000 * Day);
        var recorder = new PlayerbotSocialRecorder(memory, NullLogger.Instance);
        recorder.Track(RecorderBot, 21);
        recorder.Note(BotGuid, Human, PlayerbotSocialEvent.TradeScam);
        Assert.Null(recorder.Ledger);
        Assert.True(memory.TryGet(RecorderBot, Human.Value, out PlayerbotRelation relation));
        Assert.Equal(-15, relation.Affinity);
    }

    [Fact]
    public void Recorder_WithTheMemoryOff_StillFeedsTheLedger_AndTheMemoryGetsNoRelation()
    {
        var clock = new Clock();
        var ledger = new PlayerbotTradeLedger(new PlayerbotSocialTradingOptions(), new PlayerbotWarModeOptions(), () => clock.Now);
        var memory = new PlayerbotSocialMemory(new PlayerbotSocialMemoryOptions { Enabled = false }, () => clock.Now);
        var recorder = new PlayerbotSocialRecorder(memory, NullLogger.Instance, ledger);
        recorder.Track(RecorderBot, 21);
        Assert.False(recorder.Enabled);

        recorder.Note(BotGuid, Human, PlayerbotSocialEvent.TradeScam);
        recorder.NoteById(RecorderBot, BotGuid, Human, PlayerbotSocialEvent.NinjaLoot);
        recorder.NoteById(RecorderBot, BotGuid, Human, PlayerbotSocialEvent.GiftReceived);

        PlayerbotTradeRecord row = Get(ledger, Human.Value, RecorderBot);
        Assert.Equal(1, row.Scams);
        Assert.True(ledger.IsScammer(RecorderBot, Human.Value));
        Assert.True(ledger.IsNinja(RecorderBot, Human.Value));
        Assert.True(row.LastGiftDay > 0);
        Assert.False(memory.TryGet(RecorderBot, Human.Value, out _));
    }

    [Fact]
    public void ForgetOthers_RemovesTheRowFromEveryBot_BumpsOnlyTheirVersions_AndReturnsThem()
    {
        (PlayerbotTradeLedger ledger, _, _, _) = Make();
        Guid third = Guid.Parse("00000000-0000-0000-0000-0000000000f7");
        ledger.NoteScam(Bot, 7, Partner);
        ledger.NoteFairDeal(Bot, 7, Partner + 1);
        ledger.NoteScam(OtherBot, 8, Partner);
        ledger.NoteFairDeal(third, 9, Partner + 1);
        long thirdVersion = ledger.Version(third), botVersion = ledger.Version(Bot);

        IReadOnlyList<Guid> changed = ledger.ForgetOthers(Partner);

        Assert.Equal(new[] { Bot, OtherBot }.Order(), changed.Order());
        Assert.False(ledger.TryGet(Bot, Partner, out _));
        Assert.False(ledger.TryGet(OtherBot, Partner, out _));
        Assert.True(ledger.TryGet(Bot, Partner + 1, out _));
        Assert.True(ledger.Version(Bot) > botVersion);
        Assert.Equal(thirdVersion, ledger.Version(third));
        Assert.Empty(ledger.ForgetOthers(Partner));
    }

    [Fact]
    public void Recorder_AfterAForgottenBotIsUntracked_ANoteByIdDoesNotBringItsLedgerBack()
    {
        (PlayerbotSocialRecorder recorder, PlayerbotTradeLedger ledger) = MakeRecorder();
        recorder.NoteById(RecorderBot, BotGuid, Human, PlayerbotSocialEvent.TradeScam);
        Assert.NotEmpty(ledger.Snapshot(RecorderBot));

        ledger.Forget(RecorderBot);
        recorder.Untrack(21);
        recorder.NoteById(RecorderBot, BotGuid, Human, PlayerbotSocialEvent.TradeScam);
        recorder.NoteById(RecorderBot, BotGuid, Human, PlayerbotSocialEvent.NinjaLoot);

        Assert.Empty(ledger.Snapshot(RecorderBot));
        Assert.Equal(0, ledger.Version(RecorderBot));
    }
}
