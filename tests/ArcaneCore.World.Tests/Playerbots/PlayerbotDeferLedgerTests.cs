using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotDeferLedgerTests
{
    private static DeferKey Unreachable(ulong id) => new(DeferKind.UnreachableQuest, id);

    [Fact]
    public void Defer_UnreachableQuest_EscalatesTo24Hours_ThenHolds()
    {
        var ledger = new PlayerbotDeferLedger();
        DeferKey key = Unreachable(1);
        uint now = 0;
        uint[] expected = [60_000, 600_000, 3_600_000, 21_600_000, 86_400_000, 86_400_000];
        for (int i = 0; i < expected.Length; i++)
        {
            DeferResult result = ledger.Defer(key, now);
            Assert.Equal(expected[i], result.Ms);
            Assert.Equal(i + 1, result.Strikes);
            Assert.Equal(i == 0, result.NewEpisode); // only strikes 0 -> 1 begins an episode
            Assert.True(ledger.IsDeferred(key, now + result.Ms - 1));
            Assert.False(ledger.IsDeferred(key, now + result.Ms)); // the strikes stay
            now += result.Ms + 1_000;
        }

        Assert.Equal(6, ledger.Strikes(key));
    }

    [Fact]
    public void PinCell_EscalatesTo60Minutes_AndEntriesKeepTheirOldSteps()
    {
        Assert.Equal([120_000u, 600_000u, 1_800_000u, 3_600_000u, 3_600_000u],
            Enumerable.Range(1, 5).Select(strikes => PlayerbotDeferLedger.Backoff(DeferKind.PinCell, strikes)));

        var suspensions = new PlayerbotSuspensions();
        suspensions.SuspendEntry(5, 0);
        Assert.True(suspensions.IsEntrySuspended(5, 599_999));
        Assert.False(suspensions.IsEntrySuspended(5, 600_000));
        Assert.Equal(1, suspensions.EntryStrikeCount(5));
        Assert.Equal(1_200_000u, suspensions.SuspendEntryEscalating(5, 600_000));
        Assert.Equal(2_400_000u, suspensions.SuspendEntryEscalating(5, 700_000));
        Assert.Equal(4_800_000u, suspensions.SuspendEntryEscalating(5, 800_000));
        Assert.Equal(4_800_000u, suspensions.SuspendEntryEscalating(5, 900_000));
        suspensions.SuspendTraining(0);
        Assert.True(suspensions.IsTrainingSuspended(599_999));
        Assert.False(suspensions.IsTrainingSuspended(600_000));
    }

    [Fact]
    public void Succeeded_ForgetsStrikes_WakeKeepsThem()
    {
        var ledger = new PlayerbotDeferLedger();
        DeferKey key = Unreachable(7);
        ledger.Defer(key, 0);
        ledger.Defer(key, 1_000);
        Assert.Equal(2, ledger.Strikes(key));

        Assert.True(ledger.Wake(key, 2_000));
        Assert.False(ledger.IsDeferred(key, 2_000));
        Assert.Equal(2, ledger.Strikes(key));
        Assert.Equal(3_600_000u, ledger.Defer(key, 3_000).Ms); // the next deferral is the third step

        Assert.True(ledger.Succeeded(key));
        Assert.Equal(0, ledger.Strikes(key));
        Assert.False(ledger.IsDeferred(key, 3_000));
        DeferResult again = ledger.Defer(key, 4_000);
        Assert.Equal(60_000u, again.Ms);
        Assert.True(again.NewEpisode);
    }

    [Fact]
    public void Strikes_OfAnEntry_DecayAnHourAfterTheLastDeferralEnded_NotWhileItLasts()
    {
        var ledger = new PlayerbotDeferLedger();
        DeferKey key = new(DeferKind.Entry, 9);
        ledger.Defer(key, 0);
        ledger.Defer(key, 600_000); // strikes 2, ends at 1800 s
        DeferResult soon = ledger.Defer(key, 1_800_000 + PlayerbotDeferLedger.StrikeDecayMs - 1_000);
        Assert.Equal(3, soon.Strikes);
        uint endsAt = 1_800_000 + PlayerbotDeferLedger.StrikeDecayMs - 1_000 + soon.Ms;
        DeferResult late = ledger.Defer(key, endsAt + PlayerbotDeferLedger.StrikeDecayMs + 1_000);
        Assert.Equal(1, late.Strikes);
        Assert.True(late.NewEpisode);

        // Deferred again while it still lasts, nothing decays however long it has been going.
        var live = new PlayerbotDeferLedger();
        DeferKey held = new(DeferKind.Entry, 10);
        for (int i = 0; i < 4; i++) live.Defer(held, 0); // 80 minutes
        Assert.Equal(5, live.Defer(held, 70 * 60_000u).Strikes);
    }

    [Fact]
    public void UnreachableQuestStrikes_NeverDecay_WhateverTheCycleLength()
    {
        var ledger = new PlayerbotDeferLedger();
        DeferKey quest = Unreachable(5), entry = new(DeferKind.Entry, 5);
        ledger.Defer(quest, 0);
        ledger.Defer(entry, 0);
        const uint ThreeHours = 3 * 3_600_000;
        Assert.Equal(600_000u, ledger.Defer(quest, ThreeHours).Ms); // the second step, not a fresh minute
        Assert.Equal(2, ledger.Strikes(quest));
        Assert.Equal(1, ledger.Defer(entry, ThreeHours).Strikes); // an entry's quiet hour does decay
        Assert.Equal(3_600_000u, ledger.Defer(quest, 3 * ThreeHours).Ms);
    }

    [Fact]
    public void IsDeferred_IsPure_AndWaitingAtDropsFinishedDeferrals()
    {
        var ledger = new PlayerbotDeferLedger();
        DeferKey quest = Unreachable(6);
        ledger.Defer(quest, 0); // 60 s, waits for four causes
        Assert.Equal(DeferWake.Level | DeferWake.NavDataset | DeferWake.Definitions | DeferWake.QuestStore, ledger.WaitingAt(30_000));
        Assert.False(ledger.IsDeferred(quest, 10 * 3_600_000u)); // asked far ahead ...
        Assert.True(ledger.IsDeferred(quest, 1_000)); // ... it still holds at the real time
        Assert.Equal(DeferWake.Level | DeferWake.NavDataset | DeferWake.Definitions | DeferWake.QuestStore, ledger.WaitingAt(59_999));
        Assert.Equal(DeferWake.None, ledger.WaitingAt(60_000)); // finished: the probe has nothing to look up
        Assert.Equal(1, ledger.Strikes(quest));
    }

    [Fact]
    public void QuestSucceeded_ForgetsTheQuestsStrikes()
    {
        var suspensions = new PlayerbotSuspensions();
        suspensions.MarkUnreachableQuest(88, 0);
        suspensions.SuspendQuest(88, 0);
        suspensions.SuspendQuest(89, 0);
        Assert.Equal(1, suspensions.QuestSetAsideCount(88));
        suspensions.QuestSucceeded(88);
        Assert.Equal(0, suspensions.Ledger.Strikes(Unreachable(88)));
        Assert.Equal(0, suspensions.Ledger.Strikes(new DeferKey(DeferKind.Quest, 88)));
        Assert.Equal(0, suspensions.QuestSetAsideCount(88));
        Assert.False(suspensions.IsQuestSuspended(88, 1_000));
        Assert.True(suspensions.IsQuestSuspended(89, 1_000)); // another quest is not touched
        suspensions.MarkUnreachableQuest(88, 2_000);
        Assert.Equal(1, suspensions.Ledger.Strikes(Unreachable(88))); // starts clean
    }

    [Fact]
    public void WakeAll_Level_ReleasesOnlyLevelWaiters()
    {
        var ledger = new PlayerbotDeferLedger { Seed = 0xC0FFEE };
        DeferKey level = new(DeferKind.Entry, 1), store = new(DeferKind.Entry, 2), none = new(DeferKind.Entry, 3);
        ledger.Defer(level, 0, DeferWake.Level);
        ledger.Defer(store, 0, DeferWake.QuestStore);
        ledger.Defer(none, 0, DeferWake.None);
        Assert.Equal(DeferWake.Level | DeferWake.QuestStore, ledger.WaitingAt(500));

        Assert.Equal(1, ledger.WakeAll(DeferWake.Level, 1_000));
        Assert.False(ledger.IsDeferred(level, 1_000));
        Assert.True(ledger.IsDeferred(store, 1_000));
        Assert.True(ledger.IsDeferred(none, 1_000));
        Assert.Equal(1, ledger.Strikes(level)); // a wake keeps the strikes
        Assert.Equal(DeferWake.QuestStore, ledger.WaitingAt(1_500));
        Assert.Equal(0, ledger.WakeAll(DeferWake.Level, 2_000));
    }

    [Fact]
    public void FullKind_EvictsSoonestToWake_NeverStopsRecording()
    {
        var ledger = new PlayerbotDeferLedger();
        int cap = PlayerbotDeferLedger.PolicyOf(DeferKind.Quest).MaxEntries;
        Assert.Equal(64, cap);
        for (uint id = 1; id <= cap; id++)
            ledger.Defer(new DeferKey(DeferKind.Quest, id), 0, untilMs: 10_000 + id * 1_000, strike: false);

        DeferResult result = ledger.Defer(new DeferKey(DeferKind.Quest, 1000), 0, untilMs: 900_000, strike: false);
        Assert.Equal(900_000u, result.Ms);
        Assert.Equal(0, ledger.Strikes(new DeferKey(DeferKind.Quest, 1))); // the one soonest to wake went
        Assert.True(ledger.IsDeferred(new DeferKey(DeferKind.Quest, 2), 0));
        Assert.True(ledger.IsDeferred(new DeferKey(DeferKind.Quest, 1000), 0));

        // An expired entry goes before a live one.
        ledger.Defer(new DeferKey(DeferKind.Quest, 1001), 100_000, untilMs: 700_000, strike: false);
        Assert.True(ledger.IsDeferred(new DeferKey(DeferKind.Quest, 1000), 100_000));
        Assert.True(ledger.IsDeferred(new DeferKey(DeferKind.Quest, 1001), 100_000));
        Assert.Equal(62, Enumerable.Range(2, 63).Count(id => ledger.Strikes(new DeferKey(DeferKind.Quest, (ulong)id)) != 0));
    }

    [Fact]
    public void Restore_KeepsEscalatedEntry()
    {
        Guid bot = Guid.NewGuid();
        var before = new PlayerbotSuspensions();
        before.SuspendEntryEscalating(55, 0); // strike 2: 20 minutes, longer than the 10 minute step
        PlayerbotSetAside[] saved = before.Export(bot, 7, 100_000, 1_000_000);
        PlayerbotSetAside row = Assert.Single(saved);
        Assert.Equal(1_000_000 + 1_100_000, row.ExpiresUnixMs);

        var after = new PlayerbotSuspensions();
        after.Restore(saved, 0, 1_000_000);
        Assert.True(after.IsEntrySuspended(55, 1_000_000)); // past the 10 minutes, inside the 20
        Assert.False(after.IsEntrySuspended(55, 1_100_001));
    }

    [Fact]
    public void ReplaceBrain_KeepsEscalatedEntry()
    {
        // ManagedPlayerbotFeature.ReplaceBrain: Export from the old brain's suspensions, a new brain, Restore into it.
        Guid bot = Guid.NewGuid();
        var old = new PlayerbotSuspensions();
        old.SuspendEntry(55, 0);
        old.SuspendEntryEscalating(55, 10_000);
        old.SuspendEntryEscalating(55, 20_000); // strike 3: 40 minutes
        PlayerbotSetAside[] carried = old.Export(bot, 7, 30_000, 5_000_000);

        var replacement = new PlayerbotSuspensions();
        replacement.Restore(carried, 30_000, 5_000_000);
        Assert.True(replacement.IsEntrySuspended(55, 30_000 + 2_000_000));
        Assert.Equal(3, replacement.EntryStrikeCount(55));
        Assert.False(replacement.IsEntrySuspended(55, 20_000 + 2_400_000));
    }

    [Fact]
    public void Restore_EntryStrikesContinueEscalation()
    {
        Guid bot = Guid.NewGuid();
        var before = new PlayerbotSuspensions();
        before.SuspendEntryEscalating(5, 0);
        PlayerbotSetAside row = Assert.Single(before.Export(bot, 7, 0, 1_000_000));
        Assert.Equal(2, row.RepeatCount);

        var after = new PlayerbotSuspensions();
        after.Restore([row], 100, 1_000_100);
        Assert.Equal(2, after.EntryStrikeCount(5));
        Assert.Equal(2_400_000u, after.SuspendEntryEscalating(5, 1_300_000)); // the third step, after the second ended
    }

    [Fact]
    public void UnreachableQuest_ExpiresAndExportsFiniteRow_WithItsStrikesAcrossARestart()
    {
        Guid bot = Guid.NewGuid();
        var suspensions = new PlayerbotSuspensions();
        suspensions.MarkUnreachableQuest(77, 0);
        Assert.True(suspensions.IsQuestSuspended(77, 59_999));
        Assert.True(suspensions.IsQuestUnreachable(77, 59_999));
        suspensions.MarkUnreachableQuest(77, 30_000); // already set aside: nothing changes
        PlayerbotSetAside row = Assert.Single(suspensions.Export(bot, 7, 10_000, 1_000_000));
        Assert.Equal(PlayerbotSetAsideKind.UnreachableQuest, row.Kind);
        Assert.Equal(1_000_000 + 50_000, row.ExpiresUnixMs);
        Assert.Equal(1, row.RepeatCount);

        Assert.False(suspensions.IsQuestSuspended(77, 60_000));
        suspensions.MarkUnreachableQuest(77, 60_000); // the second diagnosis: ten minutes
        row = Assert.Single(suspensions.Export(bot, 7, 60_000, 1_000_000));
        Assert.Equal(1_000_000 + 600_000, row.ExpiresUnixMs);
        Assert.Equal(2, row.RepeatCount);

        // A restart keeps the strikes: the third diagnosis is an hour, the 24 hour row survives whole.
        var restarted = new PlayerbotSuspensions();
        restarted.Restore([row], 70_000, 1_010_000);
        Assert.False(restarted.IsQuestSuspended(77, 70_000 + 600_000));
        restarted.MarkUnreachableQuest(77, 70_000 + 600_000);
        Assert.True(restarted.IsQuestSuspended(77, 70_000 + 600_000 + 3_599_999));
        var day = new PlayerbotSuspensions();
        day.Restore([new(bot, 7, PlayerbotSetAsideKind.UnreachableQuest, 77, 1_010_000 + 86_400_000, 5)], 0, 1_010_000);
        Assert.True(day.IsQuestSuspended(77, 86_399_999));

        // A level-up is a reason to look again.
        suspensions.Ledger.WakeAll(DeferWake.Level, 61_000);
        Assert.False(suspensions.IsQuestSuspended(77, 61_000));
        PlayerbotSetAside kept = Assert.Single(suspensions.Export(bot, 7, 61_000, 1_000_000)); // over, but its strikes stay until a turn-in
        Assert.Equal(1_000_000, kept.ExpiresUnixMs);
        Assert.Equal(2, kept.RepeatCount);
    }

    [Fact]
    public void FleetWideWake_IsJitteredNotImmediate()
    {
        var ledger = new PlayerbotDeferLedger { Seed = 0xC0FFEE };
        uint jitter = PlayerbotDeferLedger.Jitter(ledger.Seed);
        Assert.InRange(jitter, 1u, PlayerbotDeferLedger.JitterSpanMs - 1);
        Assert.NotEqual(jitter, PlayerbotDeferLedger.Jitter(0xBEEF)); // bots differ

        DeferKey quest = Unreachable(9);
        DeferKey own = new(DeferKind.Entry, 1);
        ledger.Defer(quest, 0);
        ledger.Defer(own, 0, DeferWake.Level);
        Assert.Equal(2, ledger.WakeAll(DeferWake.Level, 500)); // a bot's own cause is immediate, the quest waits for a level too
        Assert.False(ledger.IsDeferred(own, 500));
        Assert.False(ledger.IsDeferred(quest, 500));

        ledger.Defer(quest, 1_000); // 10 minutes
        int version = ledger.Version;
        Assert.Equal(1, ledger.WakeAll(DeferWake.NavDataset, 2_000));
        Assert.NotEqual(version, ledger.Version); // the deadline moved
        Assert.True(ledger.IsDeferred(quest, 2_000));
        Assert.True(ledger.IsDeferred(quest, 2_000 + jitter - 1));
        Assert.False(ledger.IsDeferred(quest, 2_000 + jitter));
        Assert.Equal(2, ledger.Strikes(quest));

        // A deadline already inside the stagger is left alone, and saving it again is not asked for.
        ledger.Defer(quest, 100_000, untilMs: 100_000 + jitter / 2);
        version = ledger.Version;
        Assert.Equal(1, ledger.WakeAll(DeferWake.Definitions, 100_001));
        Assert.Equal(version, ledger.Version);
        Assert.True(ledger.IsDeferred(quest, 100_000 + jitter / 2 - 1));
    }

    [Fact]
    public void PinCell_ExpiresAfterBackoff_AndFromTheThirdStallANavDatasetFreesIt()
    {
        var watch = new PlayerbotStallWatch();
        watch.Ledger.Seed = 0xC0FFEE;
        Assert.Equal(1, watch.RecordPinStall(0, 35, 5, 1_000));
        Assert.False(watch.IsDeadEndPin(0, 35, 5, 1_000));
        Assert.Equal(2, watch.RecordPinStall(0, 36, 6, 1_000)); // the same 10 yard cell: ten minutes
        Assert.True(watch.IsDeadEndPin(0, 35, 5, 1_000));
        Assert.False(watch.IsDeadEndPin(0, 135, 5, 1_000));
        Assert.False(watch.IsDeadEndPin(1, 35, 5, 1_000));
        Assert.Equal(0, watch.Ledger.WakeAll(DeferWake.NavDataset, 2_000)); // not yet: the second stall does not wait for it

        Assert.True(watch.IsDeadEndPin(0, 35, 5, 1_000 + 599_999));
        Assert.False(watch.IsDeadEndPin(0, 35, 5, 1_000 + 600_000)); // the time asked is the world's, not the last stall's
        Assert.Equal(2, watch.PinStallCount(0, 35, 5)); // forgotten only an hour after the backoff ended
        Assert.Equal(3, watch.RecordPinStall(0, 35, 5, 601_000)); // thirty minutes
        Assert.True(watch.IsDeadEndPin(0, 35, 5, 601_000 + 1_799_999));
        Assert.False(watch.IsDeadEndPin(0, 35, 5, 601_000 + 1_800_000));
        Assert.Equal(4, watch.RecordPinStall(0, 35, 5, 2_500_000));
        Assert.True(watch.IsDeadEndPin(0, 35, 5, 2_500_000 + 3_599_999)); // an hour

        uint jitter = PlayerbotDeferLedger.Jitter(watch.Ledger.Seed);
        Assert.Equal(1, watch.Ledger.WakeAll(DeferWake.NavDataset, 3_000_000));
        Assert.True(watch.IsDeadEndPin(0, 35, 5, 3_000_000 + jitter - 1));
        Assert.False(watch.IsDeadEndPin(0, 35, 5, 3_000_000 + jitter)); // freed by the new mesh, after the stagger
        Assert.False(watch.IsDeadEndPin(0, float.NaN, 5, 0));
    }

    [Fact]
    public void ARepeatStallInADecayedPinCell_IsTheSecondStall_AndGetsNoNavDatasetWake()
    {
        var watch = new PlayerbotStallWatch();
        watch.RecordPinStall(0, 35, 5, 1_000);
        Assert.Equal(2, watch.RecordPinStall(0, 35, 5, 1_000)); // ten minutes
        Assert.Equal(1, watch.RecordPinStall(0, 35, 5, 10_000_000)); // hours of quiet: the cell starts again
        Assert.Equal(DeferWake.None, watch.Ledger.WaitingAt(10_000_001)); // the third-stall wake belongs to a third stall
    }

    [Fact]
    public void Version_BumpsOnlyForPersistedKinds()
    {
        var ledger = new PlayerbotDeferLedger();
        ledger.Defer(new DeferKey(DeferKind.PinCell, 5), 0);
        Assert.Equal(0, ledger.Version);
        DeferKey entry = new(DeferKind.Entry, 3);
        ledger.Defer(entry, 0);
        int afterDefer = ledger.Version;
        Assert.True(afterDefer > 0);
        ledger.Wake(entry, 1_000);
        Assert.True(ledger.Version > afterDefer);
    }

    [Fact]
    public void EndedDeferrals_NeverReadAsLiveAgain_AcrossTwoToThe31_AndTwoToThe32Milliseconds()
    {
        var ledger = new PlayerbotDeferLedger();
        DeferKey entry = new(DeferKind.Entry, 5), training = new(DeferKind.Training, 0), quest = new(DeferKind.Quest, 3);
        DeferKey pin = new(DeferKind.PinCell, 3), unreachable = Unreachable(3);
        ledger.Defer(training, 0, untilMs: 600_000, strike: false);
        ledger.Defer(entry, 0, untilMs: 600_000, strike: false);
        ledger.Defer(quest, 0);
        ledger.Defer(pin, 0);
        ledger.Defer(unreachable, 0, DeferWake.None); // 60 s, no wake: nothing but the deadline ends it
        const long Hour = 3_600_000, Wrap = 1L << 32;
        for (long t = Hour; t <= Wrap + 12 * Hour; t += Hour) // stepped like a long-running bot, every deferral asked at every step
        {
            foreach (DeferKey key in new[] { training, entry, quest, pin, unreachable })
                Assert.False(ledger.IsDeferred(key, t), $"{key} live again at {t}");
            Assert.Equal(DeferWake.None, ledger.WaitingAt(t));
        }

        // Past both marks the strikes behave as they would anywhere else: an entry's decayed, an unreachable quest's did not.
        long late = Wrap + 12 * Hour;
        Assert.Equal(0, ledger.EffectiveStrikes(entry, late));
        Assert.Equal(1, ledger.Strikes(entry)); // as stored, until the next deferral applies the decay
        Assert.Equal(1, ledger.Defer(entry, late).Strikes);
        Assert.Equal(1, ledger.EffectiveStrikes(unreachable, late));
        Assert.Equal(2, ledger.Defer(unreachable, late).Strikes);
        Assert.True(ledger.IsDeferred(unreachable, late + 599_999));
        Assert.False(ledger.IsDeferred(unreachable, late + 600_000));
    }

    [Fact]
    public void ALongIdleGap_WithNoLedgerCalls_ThenAQuery_ReadsEndedAndDecayed()
    {
        var ledger = new PlayerbotDeferLedger();
        DeferKey entry = new(DeferKind.Entry, 1), quest = Unreachable(1);
        ledger.Defer(entry, 1_000);
        ledger.Defer(quest, 1_000, DeferWake.None);
        foreach (long gap in new[] { 3_000_000_000L, 4_300_000_000L, 9_000_000_000L }) // beyond 2^31, past 2^32, far past it: one call each
        {
            Assert.False(ledger.IsDeferred(entry, gap), $"entry live after a gap to {gap}");
            Assert.False(ledger.IsDeferred(quest, gap), $"quest live after a gap to {gap}");
            Assert.Equal(0, ledger.EffectiveStrikes(entry, gap));
            Assert.Equal(1, ledger.EffectiveStrikes(quest, gap));
        }
    }

    [Fact]
    public void ADeferralThatEndsPastTwoToThe32_StaysLiveUntilItsEnd() // a regression guard; the wrap proof is the stepped test above
    {
        var ledger = new PlayerbotDeferLedger();
        DeferKey quest = Unreachable(4);
        long start = (1L << 32) - 1_000_000;
        ledger.Defer(quest, start, DeferWake.None); // 60 s
        long second = start + 60_000;
        ledger.Defer(quest, second, DeferWake.None); // ten minutes, ending past 2^32
        Assert.True(ledger.IsDeferred(quest, second + 500_000));
        Assert.True(ledger.IsDeferred(quest, second + 599_999));
        Assert.False(ledger.IsDeferred(quest, second + 600_000));
        Assert.Equal(DeferWake.None, ledger.WaitingAt(second + 600_000));
        Assert.Equal([new DeferRecord(quest, 0, 2)], ledger.Persistable(second + 700_000));
    }

    [Fact]
    public void Persistable_ListsEndedUnreachableStrikes_AndOnlyThose()
    {
        var suspensions = new PlayerbotSuspensions();
        suspensions.MarkUnreachableQuest(10, 0);
        suspensions.MarkUnreachableQuest(10, 60_000);
        suspensions.SuspendEntry(7, 0);
        suspensions.SuspendQuest(8, 0);
        const uint Later = 5 * 3_600_000;
        DeferRecord kept = Assert.Single(suspensions.Ledger.Persistable(Later)); // the ended entry and quest go, the unreachable quest keeps its strikes
        Assert.Equal(new DeferRecord(Unreachable(10), 0, 2), kept);

        Guid bot = Guid.NewGuid();
        PlayerbotSetAside row = Assert.Single(suspensions.Export(bot, 3, Later, 9_000_000));
        Assert.Equal(9_000_000, row.ExpiresUnixMs);
        Assert.Equal(2, row.RepeatCount);

        var restarted = new PlayerbotSuspensions();
        restarted.Restore([row], 1_000, 9_050_000);
        Assert.False(restarted.IsQuestSuspended(10, 1_000));
        Assert.Equal(2, restarted.Ledger.Strikes(Unreachable(10)));
        restarted.MarkUnreachableQuest(10, 1_000);
        Assert.Equal(3, restarted.Ledger.Strikes(Unreachable(10)));
        Assert.True(restarted.IsQuestSuspended(10, 1_000 + 3_599_999)); // the third step, an hour
        Assert.False(restarted.IsQuestSuspended(10, 1_000 + 3_600_000));

        // A turn-in clears them, and the save learns of it.
        var turnedIn = new PlayerbotSuspensions();
        turnedIn.Restore([row], 1_000, 9_050_000);
        int version = turnedIn.Version;
        turnedIn.QuestSucceeded(10);
        Assert.NotEqual(version, turnedIn.Version);
        Assert.Empty(turnedIn.Export(bot, 3, 1_000, 9_050_000));
    }

    [Fact]
    public void FullUnreachableKind_EvictsTheEndedEntryWithTheFewestStrikes_ThenTheOldest()
    {
        var ledger = new PlayerbotDeferLedger();
        int cap = PlayerbotDeferLedger.PolicyOf(DeferKind.UnreachableQuest).MaxEntries;
        for (uint id = 1; id <= cap; id++) ledger.Defer(Unreachable(id), id * 10); // all one strike, ended well before the last step below
        ledger.Defer(Unreachable(6), 1_000_000);
        ledger.Defer(Unreachable(6), 1_700_000);
        ledger.Defer(Unreachable(6), 2_400_000); // four strikes, ended
        const uint Now = 30_000_000;
        ledger.Defer(Unreachable(1000), Now); // evicts the ended entry with one strike that ended first: id 1
        Assert.Equal(0, ledger.Strikes(Unreachable(1)));
        Assert.Equal(1, ledger.Strikes(Unreachable(2)));
        for (uint id = 2; id <= cap; id++) // the other one-strike entries follow in order, the escalated one never goes
        {
            if (id == 6) continue;
            ledger.Defer(Unreachable(2000 + id), Now);
            Assert.Equal(0, ledger.Strikes(Unreachable(id)));
            Assert.Equal(4, ledger.Strikes(Unreachable(6)));
        }
    }

    [Fact]
    public void FullKind_ComparesEffectiveStrikes_ADecayedFiveStrikeEntryGoesBeforeAFreshOneStrikeOne()
    {
        var ledger = new PlayerbotDeferLedger();
        int cap = PlayerbotDeferLedger.PolicyOf(DeferKind.Entry).MaxEntries;
        const long Now = 14_400_000; // four hours
        for (uint id = 1; id <= cap; id++)
            ledger.Defer(new DeferKey(DeferKind.Entry, id), 0, untilMs: id == 7 ? 100 : 14_000_000 + id, strike: false, minStrikes: id == 7 ? 5 : 1);
        Assert.Equal(5, ledger.Strikes(new DeferKey(DeferKind.Entry, 7)));
        Assert.Equal(0, ledger.EffectiveStrikes(new DeferKey(DeferKind.Entry, 7), Now)); // quiet for hours
        Assert.Equal(1, ledger.EffectiveStrikes(new DeferKey(DeferKind.Entry, 1), Now)); // ended 400 s ago

        ledger.Defer(new DeferKey(DeferKind.Entry, 1000), Now);
        Assert.Equal(0, ledger.Strikes(new DeferKey(DeferKind.Entry, 7))); // its strikes had decayed: it costs least
        Assert.Equal(1, ledger.Strikes(new DeferKey(DeferKind.Entry, 1)));
    }

    [Fact]
    public void FullKindWithNothingEnded_EvictsTheEntrySoonestToEnd()
    {
        var ledger = new PlayerbotDeferLedger();
        int cap = PlayerbotDeferLedger.PolicyOf(DeferKind.UnreachableQuest).MaxEntries;
        for (uint id = 1; id <= cap; id++) ledger.Defer(Unreachable(id), 0, untilMs: 1_000_000 + id);
        ledger.Defer(Unreachable(500), 10);
        Assert.Equal(0, ledger.Strikes(Unreachable(1)));
        Assert.Equal(1, ledger.Strikes(Unreachable(2)));
    }

    [Fact]
    public void TheKindCaps_TogetherStayBelowTheRowLimit_SoNoKindNeedsAnotherKindsRoom()
    {
        Assert.Equal(257, PlayerbotDeferLedger.TotalCapacity);
        Assert.True(PlayerbotDeferLedger.TotalCapacity <= PlayerbotDeferLedger.MaxTotal);
        var ledger = new PlayerbotDeferLedger();
        foreach (DeferKind kind in Enum.GetValues<DeferKind>())
            for (uint id = 1; id <= 200; id++) ledger.Defer(new DeferKey(kind, id), 0);
        int total = Enum.GetValues<DeferKind>().Sum(kind => Enumerable.Range(1, 200)
            .Count(id => ledger.Strikes(new DeferKey(kind, (ulong)id)) != 0));
        Assert.Equal(PlayerbotDeferLedger.TotalCapacity, total);
        Assert.True(ledger.Persistable(1).Count <= PlayerbotDeferLedger.MaxTotal);
    }
}
