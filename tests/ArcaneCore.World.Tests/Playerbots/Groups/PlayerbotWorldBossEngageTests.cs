using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The engage decision of a world-boss raid (<see cref="PlayerbotWorldBossEngage.Decide"/>, p7b design 3.6) over plain facts records: the fixed order
/// of the kill and end tests (the corpse's tap, the ledger and the tap seen), the holds (another's tap or fight, real players near, the pull gate),
/// the release by <c>PresentFraction</c> or by <c>PullWaitSeconds</c>, <c>not-enough</c>, and that <c>HoldSeconds</c> counts only other people's claim.
/// </summary>
public sealed class PlayerbotWorldBossEngageTests
{
    private static readonly PlayerbotWorldBossOptions Defaults = new();

    /// <summary>An alive boss no one has pulled, a raid of 40 all at the rally, nobody else near: the pull gate is open.</summary>
    private static PlayerbotWorldBossEngageFacts Ready() => new() { BossFound = true, BossAlive = true, Members = 40, Present = 40 };

    private static PlayerbotWorldBossDecision Decide(PlayerbotWorldBossEngageFacts facts, PlayerbotWorldBossOptions? options = null)
        => PlayerbotWorldBossEngage.Decide(facts, options ?? Defaults);

    // --- steps 2 and 3: the boss is dead or gone -----------------------------------------------------------------------

    [Fact]
    public void ACorpseWithTheRaidsTap_IsAKill()
    {
        PlayerbotWorldBossDecision d = Decide(Ready() with { BossAlive = false, CorpseTapByRaid = true });
        Assert.Equal(PlayerbotWorldBossAction.Kill, d.Action);
        Assert.False(d.PullHeld);
        Assert.False(d.Waiting);
    }

    [Fact]
    public void ACorpseTappedBySomeoneElse_IsBossTaken_NoMatterWhatElseTheRaidSaw()
    {
        PlayerbotWorldBossDecision d = Decide(Ready() with { BossAlive = false, CorpseTapByRaid = false, TapSeen = true, LedgerPending = true });
        Assert.Equal(PlayerbotWorldBossAction.Finish, d.Action);
        Assert.Equal("boss-taken", d.Reason);
    }

    [Fact]
    public void AnUntappedCorpse_IsNotOurKill()
    {
        // Nobody tapped it (a scripted death, a despawn animation): the raid claims nothing it did not earn.
        Assert.Equal("boss-taken", Decide(Ready() with { BossAlive = false }).Reason);
    }

    [Fact]
    public void ABossThatVanished_AfterTheRaidHeldTheTap_WithARespawnPending_IsAKill()
    {
        PlayerbotWorldBossDecision d = Decide(Ready() with { BossFound = false, BossAlive = false, TapSeen = true, LedgerPending = true });
        Assert.Equal(PlayerbotWorldBossAction.Kill, d.Action);
    }

    [Fact]
    public void ABossThatVanished_WithARespawnPendingButNoTapSeen_IsBossTaken()
    {
        PlayerbotWorldBossDecision d = Decide(Ready() with { BossFound = false, BossAlive = false, TapSeen = false, LedgerPending = true });
        Assert.Equal(PlayerbotWorldBossAction.Finish, d.Action);
        Assert.Equal("boss-taken", d.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABossThatVanished_WithNothingInTheLedger_IsBossGone_EvenIfTheTapWasSeen(bool tapSeen)
    {
        PlayerbotWorldBossDecision d = Decide(Ready() with { BossFound = false, BossAlive = false, TapSeen = tapSeen, LedgerPending = false });
        Assert.Equal(PlayerbotWorldBossAction.Finish, d.Action);
        Assert.Equal("boss-gone", d.Reason);
    }

    // --- step 4: the holds ----------------------------------------------------------------------------------------------

    [Fact]
    public void AnotherPlayersTapOrFight_HoldsThePull_AndKeepsTheRaidWaiting()
    {
        PlayerbotWorldBossDecision d = Decide(Ready() with { OthersClaim = true, OthersClaimMs = 1_000 });
        Assert.Equal(PlayerbotWorldBossAction.Hold, d.Action);
        Assert.Equal(PlayerbotWorldBossHold.OthersClaim, d.Hold);
        Assert.True(d.PullHeld);
        Assert.True(d.Waiting);
    }

    [Fact]
    public void TheHoldOnAnothersClaim_EndsAsBossTaken_ExactlyAfterHoldSeconds()
    {
        long limit = Defaults.HoldSeconds * 1000L; // 180 s
        Assert.Equal(PlayerbotWorldBossAction.Hold, Decide(Ready() with { OthersClaim = true, OthersClaimMs = limit - 1 }).Action);

        PlayerbotWorldBossDecision d = Decide(Ready() with { OthersClaim = true, OthersClaimMs = limit });
        Assert.Equal(PlayerbotWorldBossAction.Finish, d.Action);
        Assert.Equal("boss-taken", d.Reason);
    }

    [Fact]
    public void RealPlayersNear_AtThePlayersNearCount_HoldThePull_AndFewerDoNot()
    {
        PlayerbotWorldBossDecision held = Decide(Ready() with { RealPlayersNear = 3 });
        Assert.Equal(PlayerbotWorldBossAction.Hold, held.Action);
        Assert.Equal(PlayerbotWorldBossHold.PlayersNear, held.Hold);
        Assert.True(held.PullHeld);
        Assert.True(held.Waiting);

        Assert.Equal(PlayerbotWorldBossAction.Release, Decide(Ready() with { RealPlayersNear = 2 }).Action);
        Assert.Equal(PlayerbotWorldBossAction.Hold, Decide(Ready() with { RealPlayersNear = 40 }).Action);
    }

    [Fact]
    public void ThePlayersNearCount_IsLive()
    {
        var one = new PlayerbotWorldBossOptions { PlayersNearCount = 1 };
        Assert.Equal(PlayerbotWorldBossAction.Hold, Decide(Ready() with { RealPlayersNear = 1 }, one).Action);
        Assert.Equal(PlayerbotWorldBossAction.Release, Decide(Ready() with { RealPlayersNear = 0 }, one).Action);
    }

    [Fact]
    public void PlayersNearYardsOfZero_TurnsThePlayerHoldOff()
    {
        var off = new PlayerbotWorldBossOptions { PlayersNearYards = 0f };
        Assert.Equal(PlayerbotWorldBossAction.Release, Decide(Ready() with { RealPlayersNear = 50 }, off).Action);
    }

    [Fact]
    public void WhenThePlayersLeave_ThePullGateRunsAgain()
    {
        Assert.Equal(PlayerbotWorldBossAction.Hold, Decide(Ready() with { RealPlayersNear = 3 }).Action);
        Assert.Equal(PlayerbotWorldBossAction.Release, Decide(Ready() with { RealPlayersNear = 0 }).Action);
    }

    [Fact]
    public void PlayersStandingNearForHoldSeconds_EndTheAttemptAsBossTaken()
    {
        // A player raid gathering at the boss is a claim like a tap is: the raid does not wait for ever.
        long limit = Defaults.HoldSeconds * 1000L;
        Assert.Equal(PlayerbotWorldBossAction.Hold, Decide(Ready() with { RealPlayersNear = 5, OthersClaimMs = limit - 1 }).Action);
        PlayerbotWorldBossDecision d = Decide(Ready() with { RealPlayersNear = 5, OthersClaimMs = limit });
        Assert.Equal("boss-taken", d.Reason);
    }

    [Fact]
    public void AnotherTap_IsTestedBeforePlayersNear_AndBeforeThePullGate()
    {
        PlayerbotWorldBossDecision d = Decide(Ready() with { OthersClaim = true, RealPlayersNear = 9, Present = 0, OthersClaimMs = 0 });
        Assert.Equal(PlayerbotWorldBossHold.OthersClaim, d.Hold);
    }

    // --- the pull gate ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TheGate_ReleasesWhenPresentFractionOfTheMembersAreAtTheRally_AndNoneIsResting()
    {
        var half = new PlayerbotWorldBossOptions { PresentFraction = 0.5f };
        PlayerbotWorldBossEngageFacts ten = Ready() with { Members = 10 };
        Assert.Equal(PlayerbotWorldBossAction.Release, Decide(ten with { Present = 5 }, half).Action);
        Assert.Equal(PlayerbotWorldBossAction.Hold, Decide(ten with { Present = 4 }, half).Action);

        // The default 0.8 of 40: 36 present pulls, 20 do not yet.
        Assert.Equal(PlayerbotWorldBossAction.Release, Decide(Ready() with { Present = 36 }).Action);
        PlayerbotWorldBossDecision waiting = Decide(Ready() with { Present = 20 });
        Assert.Equal(PlayerbotWorldBossAction.Hold, waiting.Action);
        Assert.Equal(PlayerbotWorldBossHold.Gate, waiting.Hold);
        Assert.True(waiting.PullHeld);
        Assert.True(waiting.Waiting);
    }

    [Fact]
    public void ARestingMember_HoldsTheGateEvenWhenEveryoneIsPresent()
    {
        PlayerbotWorldBossDecision d = Decide(Ready() with { AnyResting = true });
        Assert.Equal(PlayerbotWorldBossAction.Hold, d.Action);
        Assert.Equal(PlayerbotWorldBossHold.Gate, d.Hold);
    }

    [Fact]
    public void TheRelease_ClearsTheHoldAndTheWait()
    {
        PlayerbotWorldBossDecision d = Decide(Ready());
        Assert.Equal(PlayerbotWorldBossAction.Release, d.Action);
        Assert.Equal(PlayerbotWorldBossHold.None, d.Hold);
        Assert.False(d.PullHeld);
        Assert.False(d.Waiting);
    }

    [Fact]
    public void AfterPullWaitSeconds_TheRaidPullsWithWhoeverIsThere_IfAtLeastMinRaidSize()
    {
        long wait = Defaults.PullWaitSeconds * 1000L; // 120 s
        PlayerbotWorldBossEngageFacts partial = Ready() with { Present = 30 }; // under the 0.8 fraction, at MinRaidSize 30
        Assert.Equal(PlayerbotWorldBossAction.Hold, Decide(partial with { WaitMs = wait - 1 }).Action);
        Assert.Equal(PlayerbotWorldBossAction.Release, Decide(partial with { WaitMs = wait }).Action);
    }

    [Fact]
    public void AfterPullWaitSeconds_WithFewerThanMinRaidSizePresent_TheAttemptEndsAsNotEnough()
    {
        long wait = Defaults.PullWaitSeconds * 1000L;
        PlayerbotWorldBossEngageFacts few = Ready() with { Present = 29 };
        Assert.Equal(PlayerbotWorldBossAction.Hold, Decide(few with { WaitMs = wait - 1 }).Action);
        PlayerbotWorldBossDecision d = Decide(few with { WaitMs = wait });
        Assert.Equal(PlayerbotWorldBossAction.Finish, d.Action);
        Assert.Equal("not-enough", d.Reason);
    }

    [Fact]
    public void TheGateWait_IsNotOtherPeoplesClaim_SoHoldSecondsNeverEndsIt()
    {
        // Far past HoldSeconds of waiting for members, but nobody else claims the boss: the gate keeps waiting (PullWaitSeconds decides).
        PlayerbotWorldBossDecision d = Decide(Ready() with { Present = 20, OthersClaim = false, OthersClaimMs = 10 * Defaults.HoldSeconds * 1000L, WaitMs = 1_000 });
        Assert.Equal(PlayerbotWorldBossAction.Hold, d.Action);
        Assert.Equal(PlayerbotWorldBossHold.Gate, d.Hold);
    }

    [Fact]
    public void PullWait_DoesNotOverrideAnotherTap()
    {
        // The wait for the gate is over, but someone else has the boss: still a hold, not a pull.
        PlayerbotWorldBossDecision d = Decide(Ready() with { OthersClaim = true, Present = 40, WaitMs = 10 * Defaults.PullWaitSeconds * 1000L });
        Assert.Equal(PlayerbotWorldBossAction.Hold, d.Action);
        Assert.Equal(PlayerbotWorldBossHold.OthersClaim, d.Hold);
    }

    // --- steps 5 and the approach after a release ------------------------------------------------------------------------

    [Fact]
    public void AFightWeAreIn_IsFought_WithNoHold_AndNoWaiting()
    {
        PlayerbotWorldBossDecision d = Decide(Ready() with { FightingUs = true, RealPlayersNear = 9, Present = 3 });
        Assert.Equal(PlayerbotWorldBossAction.Fight, d.Action);
        Assert.False(d.PullHeld);
        Assert.False(d.Waiting);
    }

    [Fact]
    public void WhileFighting_TheTapIsNoted_OnlyWhenTheRaidHoldsIt()
    {
        Assert.True(Decide(Ready() with { FightingUs = true, TappedByRaid = true }).SetTapSeen);
        Assert.False(Decide(Ready() with { FightingUs = true, TappedByRaid = false }).SetTapSeen);
    }

    [Fact]
    public void WhenSomeoneElseTapsTheBossMidFight_TheRaidFinishesTheFightItIsIn()
    {
        // No "leave mid-fight" in the slice: the end is decided by whose tap the corpse carries.
        PlayerbotWorldBossDecision d = Decide(Ready() with { FightingUs = true, OthersClaim = true, OthersClaimMs = 10_000_000 });
        Assert.Equal(PlayerbotWorldBossAction.Fight, d.Action);
    }

    [Fact]
    public void AfterTheRelease_UntilTheFightStarts_TheRaidApproachesWithNoHold()
    {
        PlayerbotWorldBossDecision d = Decide(Ready() with { Released = true, Present = 10 });
        Assert.Equal(PlayerbotWorldBossAction.Approach, d.Action);
        Assert.False(d.PullHeld);
        Assert.False(d.Waiting);
    }

    [Fact]
    public void AfterTheRelease_AnotherTapAppearing_WithdrawsThePull()
    {
        PlayerbotWorldBossDecision d = Decide(Ready() with { Released = true, OthersClaim = true });
        Assert.Equal(PlayerbotWorldBossAction.Hold, d.Action);
        Assert.True(d.PullHeld);
    }

    // --- the raid warnings ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PlayerbotWorldBossWarning.Form)]
    [InlineData(PlayerbotWorldBossWarning.Pull)]
    [InlineData(PlayerbotWorldBossWarning.Kill)]
    [InlineData(PlayerbotWorldBossWarning.GiveUp)]
    public void EveryWarning_HasText_NamingTheBoss_ForEveryGroupId(PlayerbotWorldBossWarning kind)
    {
        for (int group = 0; group < 50; group++)
        {
            string text = PlayerbotWorldBoss.WarningText(kind, group, "Azuregos", "Azshara");
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.Contains("Azuregos", text, StringComparison.Ordinal);
            Assert.True(text.Length <= 255, "a chat message holds 255 characters");
        }
    }

    [Fact]
    public void TheFormingWarning_NamesTheRallyZone_AndTheWordingForAGroupIsFixed()
    {
        Assert.Contains("Azshara", PlayerbotWorldBoss.WarningText(PlayerbotWorldBossWarning.Form, 1, "Azuregos", "Azshara"), StringComparison.Ordinal);
        Assert.Equal(PlayerbotWorldBoss.WarningText(PlayerbotWorldBossWarning.Pull, 7, "Azuregos", "Azshara"), PlayerbotWorldBoss.WarningText(PlayerbotWorldBossWarning.Pull, 7, "Azuregos", "Azshara"));
    }
}
