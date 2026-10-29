using ArcaneCore.World.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>
/// Who posts the LFG line of a goal: the longest-waiting waiter that is eligible and near enough (a far first waiter must not silence a near
/// second one), verdicts asked lazily in waiting order, and a verdict the per-pass cap could not work out (null) defers instead of refusing.
/// </summary>
public sealed class PlayerbotNeedsVoicePosterTests
{
    private static PlayerbotLfgLocalityVerdict Near(string trace = "near") => new(true, PlayerbotLfgReach.SameZone, trace);

    private static PlayerbotLfgLocalityVerdict Far(string trace = "far") => new(false, PlayerbotLfgReach.TooFar, trace);

    /// <summary>One waiter whose verdict delegate counts its calls in <paramref name="calls"/>[<paramref name="index"/>].</summary>
    private static (bool Eligible, Func<PlayerbotLfgLocalityVerdict?> Verdict) Waiter(bool eligible, PlayerbotLfgLocalityVerdict? verdict, int[] calls, int index)
        => (eligible, () =>
        {
            calls[index]++;
            return verdict;
        });

    [Fact]
    public void TheFirstWaiterFar_TheSecondNear_TheSecondPosts_AndTheFirstIsTheHeldOne()
    {
        int[] calls = new int[2];
        var waiters = new[] { Waiter(true, Far("first is far"), calls, 0), Waiter(true, Near(), calls, 1) };

        PlayerbotNeedsVoice.PosterChoice choice = PlayerbotNeedsVoice.ChoosePosterIndex(waiters);

        Assert.Equal(1, choice.Index);
        Assert.False(choice.Deferred);
        Assert.Equal(0, choice.HeldIndex);
        Assert.Equal(PlayerbotLfgReach.TooFar, choice.HeldVerdict.Reach);
        Assert.Equal("first is far", choice.HeldVerdict.Trace);
        Assert.Equal(new[] { 1, 1 }, calls);
    }

    [Fact]
    public void AllWaitersFar_NobodyPosts_AndTheFirstRefusalIsReportedAsHeld()
    {
        int[] calls = new int[3];
        var waiters = new[] { Waiter(true, Far("far A"), calls, 0), Waiter(true, Far("far B"), calls, 1), Waiter(true, Far("far C"), calls, 2) };

        PlayerbotNeedsVoice.PosterChoice choice = PlayerbotNeedsVoice.ChoosePosterIndex(waiters);

        Assert.Null(choice.Index);
        Assert.False(choice.Deferred);
        Assert.Equal(0, choice.HeldIndex);
        Assert.Equal("far A", choice.HeldVerdict.Trace);
        Assert.False(choice.HeldVerdict.MayAdvertise);
        Assert.Equal(new[] { 1, 1, 1 }, calls);
    }

    [Fact]
    public void IneligibleWaiters_AreSkipped_AndTheirVerdictIsNeverAsked()
    {
        int[] calls = new int[4];
        var waiters = new[]
        {
            Waiter(false, Near("ineligible but near"), calls, 0),
            Waiter(true, Far("eligible far"), calls, 1),
            Waiter(false, Near("ineligible but near too"), calls, 2),
            Waiter(true, Near("eligible near"), calls, 3),
        };

        PlayerbotNeedsVoice.PosterChoice choice = PlayerbotNeedsVoice.ChoosePosterIndex(waiters);

        Assert.Equal(3, choice.Index);
        Assert.Equal(1, choice.HeldIndex);
        Assert.Equal("eligible far", choice.HeldVerdict.Trace);
        Assert.Equal(new[] { 0, 1, 0, 1 }, calls);
    }

    [Fact]
    public void NoEligibleWaiter_OrNoWaiter_PostsNothing_HoldsNothing_AndAsksNothing()
    {
        int[] calls = new int[2];
        var ineligible = new[] { Waiter(false, Near(), calls, 0), Waiter(false, Near(), calls, 1) };
        PlayerbotNeedsVoice.PosterChoice none = PlayerbotNeedsVoice.ChoosePosterIndex(ineligible);
        Assert.Null(none.Index);
        Assert.Null(none.HeldIndex);
        Assert.False(none.Deferred);
        Assert.Equal(new[] { 0, 0 }, calls);

        PlayerbotNeedsVoice.PosterChoice empty = PlayerbotNeedsVoice.ChoosePosterIndex(Array.Empty<(bool Eligible, Func<PlayerbotLfgLocalityVerdict?> Verdict)>());
        Assert.Null(empty.Index);
        Assert.Null(empty.HeldIndex);
        Assert.False(empty.Deferred);
    }

    [Fact]
    public void VerdictsAreAskedLazily_InWaitingOrder_AndStopAtTheFirstWaiterThatMayPost()
    {
        var order = new List<int>();
        (bool Eligible, Func<PlayerbotLfgLocalityVerdict?> Verdict) Ask(int index, PlayerbotLfgLocalityVerdict verdict) => (true, () =>
        {
            order.Add(index);
            return verdict;
        });

        var waiters = new[] { Ask(0, Far()), Ask(1, Far()), Ask(2, Near()), Ask(3, Near()), Ask(4, Far()) };
        PlayerbotNeedsVoice.PosterChoice choice = PlayerbotNeedsVoice.ChoosePosterIndex(waiters);

        Assert.Equal(2, choice.Index);
        Assert.Equal(new[] { 0, 1, 2 }, order);
    }

    [Fact]
    public void WhenTheFirstWaiterMayPost_NoOtherVerdictIsAsked()
    {
        int[] calls = new int[3];
        var waiters = new[] { Waiter(true, Near(), calls, 0), Waiter(true, Near(), calls, 1), Waiter(true, Far(), calls, 2) };

        PlayerbotNeedsVoice.PosterChoice choice = PlayerbotNeedsVoice.ChoosePosterIndex(waiters);

        Assert.Equal(0, choice.Index);
        Assert.Null(choice.HeldIndex);
        Assert.False(choice.Deferred);
        Assert.Equal(new[] { 1, 0, 0 }, calls);
    }

    [Fact]
    public void AVerdictThePerPassCapCouldNotWorkOut_DefersTheChoice_AndIsNotAHolding()
    {
        // All verdicts unknown (null): nothing posts, nothing is held, and the choice is not final.
        int[] calls = new int[2];
        var waiters = new[] { Waiter(true, null, calls, 0), Waiter(true, null, calls, 1) };

        PlayerbotNeedsVoice.PosterChoice choice = PlayerbotNeedsVoice.ChoosePosterIndex(waiters);

        Assert.Null(choice.Index);
        Assert.True(choice.Deferred);
        Assert.Null(choice.HeldIndex);
        Assert.Equal(new[] { 1, 1 }, calls);
    }

    [Fact]
    public void ADeferredWaiter_DoesNotBlockALaterNearOne_ButTheChoiceIsMarkedDeferred()
    {
        int[] calls = new int[3];
        var waiters = new[] { Waiter(true, Far("far first"), calls, 0), Waiter(true, null, calls, 1), Waiter(true, Near(), calls, 2) };

        PlayerbotNeedsVoice.PosterChoice choice = PlayerbotNeedsVoice.ChoosePosterIndex(waiters);

        Assert.Equal(2, choice.Index);
        Assert.True(choice.Deferred);
        Assert.Equal(0, choice.HeldIndex);
        Assert.Equal("far first", choice.HeldVerdict.Trace);
    }

    [Fact]
    public void WithTheCapReached_TheExpensiveWorkIsBounded_AndTheChoiceIsDeferredNotRefused()
    {
        // The delegates behave as PlayerbotNeedsVoice.LocalityOf does: past MaxLocalityVerdictsPerPass computed verdicts they return null
        // without doing the work. Twelve far waiters: eight verdicts are worked out, the other four are deferred.
        int worked = 0;
        int asked = 0;
        var waiters = new (bool Eligible, Func<PlayerbotLfgLocalityVerdict?> Verdict)[12];
        for (int i = 0; i < waiters.Length; i++)
        {
            int index = i;
            waiters[i] = (true, () =>
            {
                asked++;
                if (worked >= PlayerbotNeedsVoice.MaxLocalityVerdictsPerPass) return null;
                worked++;
                return Far($"far {index}");
            });
        }

        PlayerbotNeedsVoice.PosterChoice choice = PlayerbotNeedsVoice.ChoosePosterIndex(waiters);

        Assert.Null(choice.Index);
        Assert.True(choice.Deferred);
        Assert.Equal(0, choice.HeldIndex);
        Assert.Equal("far 0", choice.HeldVerdict.Trace);
        Assert.Equal(PlayerbotNeedsVoice.MaxLocalityVerdictsPerPass, worked);
        Assert.Equal(12, asked);
    }

    [Fact]
    public void ANearWaiterReachedWithinTheCap_Posts_AndTheWaitersAfterItAreNeverAsked()
    {
        // Twelve waiters, only index 7 is near: it is the eighth verdict, still within the cap of eight, so it posts and the other four are not asked.
        int worked = 0;
        int asked = 0;
        var waiters = new (bool Eligible, Func<PlayerbotLfgLocalityVerdict?> Verdict)[12];
        for (int i = 0; i < waiters.Length; i++)
        {
            bool near = i == 7;
            waiters[i] = (true, () =>
            {
                asked++;
                if (worked >= PlayerbotNeedsVoice.MaxLocalityVerdictsPerPass) return null;
                worked++;
                return near ? Near() : Far();
            });
        }

        PlayerbotNeedsVoice.PosterChoice choice = PlayerbotNeedsVoice.ChoosePosterIndex(waiters);

        Assert.Equal(7, choice.Index);
        Assert.False(choice.Deferred);
        Assert.Equal(0, choice.HeldIndex);
        Assert.Equal(8, asked);
        Assert.Equal(8, worked);
    }

    [Fact]
    public void ThePerPassCap_IsEight()
        => Assert.Equal(8, PlayerbotNeedsVoice.MaxLocalityVerdictsPerPass);

    [Fact]
    public void ANullWaiterList_Throws()
        => Assert.Throws<ArgumentNullException>(() => PlayerbotNeedsVoice.ChoosePosterIndex(null!));

    // The verdict cache (LocalityOf) is private and runs inside a social pass against a live world, so no seam drives its time-to-live
    // directly: this pins the constant that sets it (an unknown-zone verdict is asked again after 10 s, not after the 180 s default repeat).
    [Fact]
    public void AnUnknownZoneVerdict_IsKeptTenSeconds_FarShorterThanTheDefaultRepeatInterval()
    {
        Assert.Equal(10_000L, PlayerbotNeedsVoice.UnknownZoneVerdictMs);
        Assert.True(PlayerbotNeedsVoice.UnknownZoneVerdictMs < new PlayerbotVoiceOptions().LookingForGroupRepeatSeconds * 1000L);
    }
}
